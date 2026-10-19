namespace Server.Clean;

/// <summary>
/// 离线挂机批算任务生产者：周期性从 game.player_projection JOIN game.player_state_heads
/// 预筛投影离线且带离线锚点、提交头欠债仍在收益边界内、距上次结算已满批算间隔（或已到
/// 边界仍有欠债的终末批）的玩家，只对入选候选做全量状态加载（含 durable 队列叠加）+ 正
/// 余额快照，编码为 compute.v2 任务请求入队；批目标 = min(now, offline_since + 48h)。
/// 任务携带完整身份与提交头快照：节点凭此重建 PlayerStateBundle 并逐字节还原聚合。
/// 玩家上线或所有权租约生效期间自动跳过；同一玩家最多一个未完结任务由数据库唯一约束保证。
/// 扫描与入队不依赖计算节点配置：本地执行器常驻消费同一队列，节点只承接溢出积压。
/// 多副本部署时由会话级咨询锁选出唯一生产者：未持锁实例每轮空转跳过，
/// 锁随连接断开/进程退出自动释放，无残留状态（见 docs/distributed-readiness.md §H）。
/// </summary>
public sealed class ColdCatchUpWorker(
    PostgresComputeJobRepository jobs,
    PostgresSectionRepository playerStates,
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<ColdCatchUpWorker> logger) : BackgroundService
{
    /// <summary>冷挂机生产 leader 会话级咨询锁键：全集群固定，持有者才执行扫描与入队。</summary>
    private const long ProducerLockKey = 772_311;
    /// <summary>候选入队并行度：每候选一次 RepeatableRead 快照读加一次 upsert 往返；
    /// 12 路并行充分利用 32 连接池，使 2000 候选入队在 2 秒内完成，压窄生产窗口。</summary>
    private const int EnqueueParallelism = 12;

    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    private readonly LogThrottle _playerErrorLog = new(TimeSpan.FromMinutes(5));
    /// <summary>持有咨询锁的专用连接：锁是会话级状态，必须让这条连接活到进程退出才自动释放。</summary>
    private NpgsqlConnection? _lockConnection;
    /// <summary>本实例当前是否为生产 leader；仅用于持锁状态切换时打一条日志。</summary>
    private bool _isProducer;

    /// <summary>
    /// 扫描主循环：不受计算节点配置门控（本地执行器始终消费该队列）；启动延迟 30 秒避开建库
    /// 热路径后，每 ColdCatchUpInterval 一轮——仅持有生产咨询锁的实例扫描离线欠债玩家并逐个入队，
    /// 循环异常经节流记录后 5 秒退避重试，停机时释放持锁连接（会话结束即让出生产权）。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "ColdCatchUpWorker started interval={Interval} batchInterval={BatchInterval} batch={Batch}.",
            options.ColdCatchUpInterval, options.OfflineBatchInterval, options.ColdCatchUpBatchSize);
        // 启动后先让一轮：避开建库与在线恢复热路径，也给 projection 留出首笔提交时间。
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await TryHoldProducerLockAsync(stoppingToken))
                    {
                        var candidates = await jobs.ScanColdPlayersAsync(
                            options.OfflineBatchInterval,
                            options.ColdCatchUpBatchSize, stoppingToken);
                        var enqueued = 0;
                        // 有界并行入队：仓储无共享状态（每次调用自开连接）、日志节流器为 CAS 实现，
                        // 并行仅放大数据库往返并发度，语义与串行逐个入队一致。
                        await Parallel.ForEachAsync(candidates,
                            new ParallelOptions { MaxDegreeOfParallelism = EnqueueParallelism, CancellationToken = stoppingToken },
                            async (candidate, token) =>
                            {
                                if (await TryEnqueueAsync(candidate, token)) Interlocked.Increment(ref enqueued);
                            });
                        if (enqueued > 0)
                            logger.LogInformation("冷挂机入队 {Enqueued}/{Candidates} 个玩家。", enqueued, candidates.Count);
                    }
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
        finally
        {
            // 关闭持锁连接：会话结束即释放咨询锁，优雅停机时让出生产权而不是等连接超时。
            if (_lockConnection is not null)
                await _lockConnection.DisposeAsync();
        }
    }

    /// <summary>
    /// 确保本实例持有冷扫描生产咨询锁：已持有返回 true；被其他实例持有则本轮空转跳过。
    /// pg_try_advisory_lock 可重入，每轮调用同时充当持锁连接的活性探针——连接断开即锁
    /// 已随会话释放，抛错后丢弃连接，下一轮重建并重抢（holder 宕机由活副本自动接管）。
    /// </summary>
    private async ValueTask<bool> TryHoldProducerLockAsync(CancellationToken cancellationToken)
    {
        _lockConnection ??= await dataSource.OpenConnectionAsync(cancellationToken);
        bool held;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", _lockConnection);
            command.Parameters.Add(new NpgsqlParameter { Value = ProducerLockKey });
            held = await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch
        {
            await _lockConnection.DisposeAsync();
            _lockConnection = null;
            _isProducer = false;
            throw;
        }
        if (!held)
        {
            // 未抢到：该连接不携带任何锁状态，退回池复用即可。
            if (_isProducer)
            {
                _isProducer = false;
                logger.LogInformation("冷挂机扫描生产锁由其他实例持有，本实例转入空转待命。");
            }
            await _lockConnection.DisposeAsync();
            _lockConnection = null;
            return false;
        }
        if (!_isProducer)
        {
            _isProducer = true;
            logger.LogInformation("冷挂机扫描已获得生产咨询锁，本实例承担任务生产。");
        }
        return true;
    }

    private async ValueTask<bool> TryEnqueueAsync(ColdPlayerCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var frozen = await playerStates.LoadComputeInputAsync(candidate.PlayerId, cancellationToken);
            // 存档与余额必须来自同一 RepeatableRead 快照；不完整状态直接跳过。
            if (frozen is null || frozen.Bundle.Head.StateVersion <= 0) return false;
            var bundle = frozen.Bundle;

            var now = DateTimeOffset.UtcNow;
            var from = bundle.Head.LastSimulatedAt;
            // 批目标锚点取扫描候选的投影 offline_since（总纲18 §14.1 离线时长判定列），
            // 不做逐玩家全量解码：执行器内部仍按存档内锚点封顶，目标取投影锚点不影响收益正确性。
            // 轻量复核：扫描 SQL 已预筛，但叠加 durable 队列后的有效头可能更新；
            // 距上次结算不足批算间隔（且未到封顶边界）则本轮跳过。
            if (ComputeBatchTarget(from, candidate.OfflineSince, now, options.OfflineBatchInterval) is not { } target)
                return false;

            var sections = bundle.Sections.Values
                .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
                .Select(ComputeSectionCodec.ToWire)
                .ToArray();
            if (sections.Length == 0 || sections.Length > ComputeDispatchContract.MaxSectionsPerJob)
                return false;

            // 余额快照随任务冻结：节点钱包还原仅作可用性判定，实际入账仍以回传增量经经济事务为准。
            // Ordinal 排序保证同一输入产出同一载荷字节（入队幂等键绑定目标时刻，载荷参与存档）。
            var balances = frozen.Balances
                .Select(row => new ComputeAssetBalance(row.AssetId, row.Variant, row.Quantity, row.Version))
                .OrderBy(row => row.AssetId, StringComparer.Ordinal)
                .ThenBy(row => row.Variant, StringComparer.Ordinal)
                .ToArray();

            var request = new ComputeAdvanceJobRequest(
                PostgresComputeJobRepository.DeriveJobId(candidate.PlayerId, target.ToUnixTimeMilliseconds()),
                candidate.PlayerId,
                bundle.Identity.AccountId,
                bundle.Identity.DisplayName,
                bundle.Identity.CreatedAt.ToUnixTimeMilliseconds(),
                bundle.Head.StateVersion,
                from.ToUnixTimeMilliseconds(),
                target.ToUnixTimeMilliseconds(),
                bundle.Head.Checksum,
                bundle.Head.GameDataFingerprint,
                sections,
                balances,
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

    /// <summary>
    /// 批算目标判定：from 为提交头已结算时刻，anchor 为离线锚点，边界 = anchor + OfflineIdleLimit。
    /// from 已到/越过边界 → null（无收益欠债可补）；目标 = min(now, boundary)，距上次结算不足
    /// 批算间隔且未触边界 → null（未到批算点）；触边界的终末批不受间隔限制，直接补到边界。
    /// </summary>
    internal static DateTimeOffset? ComputeBatchTarget(
        DateTimeOffset from, DateTimeOffset anchor, DateTimeOffset now, TimeSpan batchInterval)
    {
        var boundary = anchor.Add(PlayerLifecycleRules.OfflineIdleLimit);
        if (from >= boundary) return null;
        var target = now < boundary ? now : boundary;
        if (target < boundary && target - from < batchInterval) return null;
        return target;
    }
}
