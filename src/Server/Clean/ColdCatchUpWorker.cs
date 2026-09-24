namespace Server.Clean;

/// <summary>
/// 冷挂机任务生产者：周期性从 game.player_projection JOIN game.player_state_heads 预筛
/// 离线且提交头模拟欠债落在窗口内的玩家（last_simulated_at 过滤已前移进扫描 SQL），
/// 只对入选候选做全量状态加载（含 durable 队列叠加）+ 正余额快照，编码为 compute.v2 任务请求入队。
/// 任务携带完整身份与提交头快照：节点凭此重建 PlayerStateBundle 并逐字节还原聚合。
/// 玩家上线或所有权租约生效期间自动跳过；同一玩家最多一个未完结任务由数据库唯一约束保证。
/// 多副本部署时由会话级咨询锁选出唯一生产者：未持锁实例每轮空转跳过，
/// 锁随连接断开/进程退出自动释放，无残留状态（见 docs/distributed-readiness.md §H）。
/// </summary>
public sealed class ColdCatchUpWorker(
    PostgresComputeJobRepository jobs,
    IPlayerStateRepository playerStates,
    IAssetBalanceRepository assetBalances,
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<ColdCatchUpWorker> logger) : BackgroundService
{
    /// <summary>冷挂机生产 leader 会话级咨询锁键：全集群固定，持有者才执行扫描与入队。</summary>
    private const long ProducerLockKey = 772_311;

    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    private readonly LogThrottle _playerErrorLog = new(TimeSpan.FromMinutes(5));
    /// <summary>持有咨询锁的专用连接：锁是会话级状态，必须让这条连接活到进程退出才自动释放。</summary>
    private NpgsqlConnection? _lockConnection;
    /// <summary>本实例当前是否为生产 leader；仅用于持锁状态切换时打一条日志。</summary>
    private bool _isProducer;

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

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await TryHoldProducerLockAsync(stoppingToken))
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
            var bundle = await playerStates.LoadAsync(candidate.PlayerId, cancellationToken);
            // 无提交头或 section 不全的玩家没有可推进状态；LoadAsync 对不完整存档直接抛错。
            if (bundle is null || bundle.Head.StateVersion <= 0) return false;

            var now = DateTimeOffset.UtcNow;
            var from = bundle.Head.LastSimulatedAt;
            // 轻量复核：扫描 SQL 已按提交头 last_simulated_at 预筛，但叠加 durable 队列后的
            // 有效头可能更新；距上次模拟不足最小步长则跳过（既给刚断线玩家宽限，也限制补算频率）。
            if (from >= now || now - from < options.ColdCatchUpMinIdle) return false;
            // 收益封顶 48 小时后只推进模拟时钟，无必要再派发任务。
            if (now - from > options.ColdCatchUpWindow) return false;

            var sections = bundle.Sections.Values
                .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
                .Select(ComputeSectionCodec.ToWire)
                .ToArray();
            if (sections.Length == 0 || sections.Length > ComputeDispatchContract.MaxSectionsPerJob)
                return false;

            // 余额快照随任务冻结：节点钱包还原仅作可用性判定，实际入账仍以回传增量经经济事务为准。
            // Ordinal 排序保证同一输入产出同一载荷字节（入队幂等键绑定目标时刻，载荷参与存档）。
            var balances = (await assetBalances.LoadBalancesAsync(candidate.PlayerId, cancellationToken))
                .Select(row => new ComputeAssetBalance(row.AssetId, row.Variant, row.Quantity))
                .OrderBy(row => row.AssetId, StringComparer.Ordinal)
                .ThenBy(row => row.Variant, StringComparer.Ordinal)
                .ToArray();

            var target = now;
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
}
