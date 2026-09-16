namespace Server.Clean;

/// <summary>
/// 冷挂机任务生产者：周期性从 game.player_projection 选出离线且仍有未结算进度的玩家，
/// 从 section 存储读取最新有效状态（含 durable 队列叠加），编码为 compute.v1 任务请求入队。
/// 玩家上线或所有权租约生效期间自动跳过；同一玩家最多一个未完结任务由数据库唯一约束保证。
/// </summary>
public sealed class ColdCatchUpWorker(
    PostgresComputeJobRepository jobs,
    IPlayerStateRepository playerStates,
    ServerOptions options,
    ILogger<ColdCatchUpWorker> logger) : BackgroundService
{
    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    private readonly LogThrottle _playerErrorLog = new(TimeSpan.FromMinutes(5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ComputeNodeConfigured)
        {
            // 派发链未配置时不生产任务，避免 pending 行无意义堆积。
            logger.LogInformation("冷挂机扫描未启用：ComputeNode 未配置或未开启。");
            return;
        }

        logger.LogInformation("ColdCatchUpWorker started interval={Interval} window={Window} batch={Batch}.",
            options.ColdCatchUpInterval, options.ColdCatchUpWindow, options.ColdCatchUpBatchSize);
        // 启动后先让一轮：避开建库与在线恢复热路径，也给 projection 留出首笔提交时间。
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await jobs.ScanColdPlayersAsync(
                    options.ColdCatchUpMinIdle, options.ColdCatchUpWindow,
                    options.ColdCatchUpBatchSize, stoppingToken);
                var enqueued = 0;
                foreach (var candidate in candidates)
                {
                    if (await TryEnqueueAsync(candidate, stoppingToken)) enqueued++;
                }
                if (enqueued > 0)
                    logger.LogInformation("冷挂机入队 {Enqueued}/{Candidates} 个玩家。", enqueued, candidates.Count);
                await Task.Delay(options.ColdCatchUpInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_failureLog.TryAcquire())
                    logger.LogError(exception, "ColdCatchUpWorker 循环失败，将在下个周期重试。");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async ValueTask<bool> TryEnqueueAsync(ColdPlayerCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await playerStates.LoadAsync(candidate.PlayerId, cancellationToken);
            // 无提交头或 section 不全的玩家没有可推进状态；LoadAsync 对不完整存档直接抛错。
            if (bundle is null || bundle.Head.StateVersion <= 0) return false;

            var now = DateTimeOffset.UtcNow;
            var from = bundle.Head.LastSimulatedAt;
            // 距上次模拟不足最小步长则跳过：既给刚断线玩家宽限，也限制单玩家补算频率。
            if (from >= now || now - from < options.ColdCatchUpMinIdle) return false;
            // 收益封顶 48 小时后只推进模拟时钟，无必要再派发任务。
            if (now - from > options.ColdCatchUpWindow) return false;

            var sections = bundle.Sections.Values
                .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
                .Select(ComputeSectionCodec.ToWire)
                .ToArray();
            if (sections.Length == 0 || sections.Length > ComputeDispatchContract.MaxSectionsPerJob)
                return false;

            var target = now;
            var request = new ComputeAdvanceJobRequest(
                PostgresComputeJobRepository.DeriveJobId(candidate.PlayerId, target.ToUnixTimeMilliseconds()),
                candidate.PlayerId,
                bundle.Head.StateVersion,
                from.ToUnixTimeMilliseconds(),
                target.ToUnixTimeMilliseconds(),
                sections,
                Actions: null,
                IdempotencyKey: $"cold:{candidate.PlayerId:D}:{target.ToUnixTimeMilliseconds()}");
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, ServerJson.Options);
            var result = await jobs.EnqueueAsync(candidate.PlayerId, target, payload, cancellationToken);
            return result is ComputeEnqueueResult.Inserted or ComputeEnqueueResult.Refreshed;
        }
        catch (InvalidDataException exception)
        {
            // 存档损坏的玩家长期跳过，节流保留证据，等待人工核查。
            if (_playerErrorLog.TryAcquire())
                logger.LogWarning("冷挂机候选玩家存档无效，已跳过 player={PlayerId} error={Error}", candidate.PlayerId, exception.Message);
            return false;
        }
    }
}
