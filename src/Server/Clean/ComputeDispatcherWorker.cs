namespace Server.Clean;

/// <summary>
/// 主服务 → 计算节点组冷挂机派发 worker（compute-link 反向拨入模型），按节点并发容量并行派发。
/// 主循环：Acquire 原子占一个节点批次槽位 → ClaimDue 按节点协商上限领取 → 立即派生独立派发任务，
/// 不等响应即继续下一轮；在飞批次数 = Σ 各节点 Capacity，节点声明的 MaxConcurrentBatches 全部用满。
/// 仅当节点组无剩余容量或无到期任务时 sleep（容量归还/新节点注册经 <see cref="ComputeLinkRegistry.CapacitySignal"/>
/// 即时唤醒，最长不超过轮询周期）。
/// 批次装配按「解码后字节预算」截断：装不下的 claim 立即退回 pending 下轮再派；
/// 超过节点组承接能力的单任务直接判 dead，不再靠反复领取烧 attempts。
/// 节点断线/批次超时仍按租约令牌整批退回 pending，并按各 job 自身 attempts 分组指数退避；
/// 领取次数超过上限自动进入 dead 终态保留诊断窗口。
/// 未配置令牌或派发停用时 worker 安全退出（任务保留 pending）。
/// </summary>
public sealed class ComputeDispatcherWorker(
    PostgresComputeJobRepository jobs,
    PostgresSectionRepository sections,
    NpgsqlDataSource dataSource,
    ComputeLinkRegistry registry,
    ServerOptions options,
    GameDataCatalog gameData,
    ILogger<ComputeDispatcherWorker> logger) : BackgroundService
{
    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    /// <summary>在飞派发任务簿记：停机时统一等待排空；各任务槽位已在 Acquire 时预留。</summary>
    private readonly ConcurrentDictionary<Task, byte> _dispatches = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ComputeNodeConfigured)
        {
            logger.LogInformation(
                "冷挂机派发器未启用：需要 ComputeNode:Enabled=true 且配置 ComputeNode:Token；任务保持 pending。");
            return;
        }

        logger.LogInformation(
            "ComputeDispatcherWorker started batch={Batch} batchBytes={BatchBytes} interval={Interval}（compute-link 按节点容量并行派发）。",
            options.ComputeBatchSize, options.ComputeBatchMaxBytes, options.ComputePollInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 先取信号再取节点：Acquire→sleep 之间发生的容量归还必然完成这份信号，
                // 不会漏过唤醒睡满整个轮询周期。
                var capacitySignal = registry.CapacitySignal;
                try
                {
                    // 先取节点再领任务：节点组为空或全忙时任务留在 pending 不动，不消耗领取次数。
                    var node = registry.Acquire();
                    if (node is null)
                    {
                        await WaitForNextRoundAsync(capacitySignal, stoppingToken);
                        continue;
                    }

                    IReadOnlyList<ComputeJobClaim> claims;
                    try
                    {
                        // 领取上限按 hello 协商的节点单批任务数收紧，避免领回来装不下再退。
                        var maxJobs = node.MaxJobsPerBatch > 0
                            ? Math.Min(options.ComputeBatchSize, node.MaxJobsPerBatch)
                            : options.ComputeBatchSize;
                        claims = await jobs.ClaimDueAsync(maxJobs, options.ComputeLease, stoppingToken);
                    }
                    catch
                    {
                        node.ReleaseSlot();
                        throw;
                    }
                    if (claims.Count == 0)
                    {
                        node.ReleaseSlot();
                        await WaitForNextRoundAsync(capacitySignal, stoppingToken);
                        continue;
                    }
                    // 槽位已占即派生独立批次任务，主循环不等响应直接继续取下一个节点。
                    Track(DispatchBatchAsync(node, claims, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (_failureLog.TryAcquire())
                        logger.LogError(exception, "ComputeDispatcherWorker 循环失败，将在下个周期重试。");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
        }
        finally
        {
            // 停机收敛：在飞派发共享 stoppingToken 已全部取消，等簿记排空；
            // 未及时退回的租约按既有孤儿语义到期重取，收敛等待本身有界。
            try { await Task.WhenAll(_dispatches.Keys).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* 关闭路径不阻断退出 */ }
        }
    }

    /// <summary>无可用节点或无到期任务时等待：已捕获的容量信号（节点接入/槽位归还）与轮询周期先到先醒。</summary>
    private async Task WaitForNextRoundAsync(Task capacitySignal, CancellationToken stoppingToken)
    {
        var delay = Task.Delay(options.ComputePollInterval, stoppingToken);
        await Task.WhenAny(capacitySignal, delay);
    }

    /// <summary>登记在飞派发任务：停机排空与异常兜底都依赖这份簿记。</summary>
    private void Track(Task dispatch)
    {
        _dispatches.TryAdd(dispatch, 0);
        _ = dispatch.ContinueWith(
            (task, state) => ((ComputeDispatcherWorker)state!)._dispatches.TryRemove(task, out _),
            this,
            CancellationToken.None);
    }

    /// <summary>本节点 hello 声明与主服配置协商出的单批解码后字节预算；未声明按主服配置。</summary>
    private long EffectiveBatchBytes(ComputeLinkNode node) =>
        node.MaxBatchBytes > 0
            ? Math.Min(options.ComputeBatchMaxBytes, node.MaxBatchBytes)
            : options.ComputeBatchMaxBytes;

    /// <summary>
    /// 单个批次任务的完整生命周期：校验载荷 → 字节预算装批 → 登记在飞 → 写帧 → 等响应 → 结果落库。
    /// 槽位在 Acquire 时已预留，finally 统一归还；任何未捕获异常兜底为整批退回，
    /// claim 不会悬挂到租约超时。
    /// </summary>
    private async Task DispatchBatchAsync(
        ComputeLinkNode node,
        IReadOnlyList<ComputeJobClaim> claims,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunBatchAsync(node, claims, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机取消：未完结租约按孤儿语义到期重取。
        }
        catch (Exception exception)
        {
            // 派发任务级兜底：环节崩溃把本批剩余 claim 退回 pending 退避（已完结行不受影响——
            // ReleaseLease 只命中 status='leased' 的行），不悬挂到租约超时。
            if (_failureLog.TryAcquire())
                logger.LogError(exception, "ComputeNode 批次任务异常 node={Node} name={Name}", node.NodeId, node.Name);
            try
            {
                await ReleaseClaimsAsync(claims, exception.Message, CancellationToken.None);
            }
            catch { /* 退回失败不升级：租约到期后由 SKIP LOCKED 重取 */ }
        }
        finally
        {
            node.ReleaseSlot();
        }
    }

    private async Task RunBatchAsync(
        ComputeLinkNode node,
        IReadOnlyList<ComputeJobClaim> claims,
        CancellationToken cancellationToken)
    {
        var ready = new List<ReadyJob>(claims.Count);
        foreach (var claim in claims)
        {
            ComputeAdvanceJobRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<ComputeAdvanceJobRequest>(claim.InputPayload, ServerJson.Options);
            }
            catch (JsonException)
            {
                // 坏载荷不会自愈：直接进 dead 保留诊断窗口。
            }
            if (request is null || request.Sections is not { Count: > 0 }
                || request.PlayerId != claim.PlayerId
                || request.Sections.Any(section => section is null || string.IsNullOrEmpty(section.PayloadBase64)))
            {
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, "input_payload 无效", retryable: false,
                    TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
                continue;
            }
            ready.Add(new ReadyJob(claim, request, ComputeBatchBudget.EstimateDecodedBytes(request)));
        }
        if (ready.Count == 0) return;

        // 节点组最大可承接预算：单任务比任一在线节点能装的都大时直接判 dead，
        // 避免在节点间反复退回、烧完领取次数才进 dead（生产-判死死循环治理）。
        var batchBudget = EffectiveBatchBytes(node);
        var groupBudget = Math.Max(batchBudget,
            registry.Nodes.Select(EffectiveBatchBytes).DefaultIfEmpty(0L).Max());
        var dispatchable = new List<ReadyJob>(ready.Count);
        foreach (var entry in ready)
        {
            if (entry.DecodedBytes > groupBudget)
            {
                await jobs.FailAsync(entry.Claim.JobId, entry.Claim.LeaseToken,
                    $"JOB_TOO_LARGE: 单任务解码后 {entry.DecodedBytes}B 超过节点组批次预算 {groupBudget}B",
                    retryable: false, TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
                continue;
            }
            dispatchable.Add(entry);
        }
        if (dispatchable.Count == 0) return;

        // 本节点有效预算内贪心装批：装不下的立即零退避退回 pending，下轮派给其他批次/节点。
        var (fits, overflow) = ComputeBatchBudget.Pack(dispatchable, entry => entry.DecodedBytes, batchBudget);
        if (overflow.Count > 0)
        {
            await ReleaseClaimsAsync(
                overflow.Select(entry => entry.Claim).ToArray(),
                "批次字节预算截断，退回下轮再派", cancellationToken, retryAfter: TimeSpan.Zero);
        }
        if (fits.Count == 0) return;

        var batchId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<ComputeAdvanceBatchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!node.Pending.TryAdd(batchId, waiter))
        {
            await ReleaseClaimsAsync(ClaimsOf(fits), "批次登记冲突", cancellationToken);
            return;
        }

        try
        {
            // 出站队列 bounded 64：并行派发下可能顶满，补写入超时防止派发任务被挂死。
            await node.Outbound.Writer.WriteAsync(
                    ComputeLinkCodec.Encode(new ComputeLinkBatch(batchId, new ComputeAdvanceBatchRequest(
                        ComputeDispatchContract.Version,
                        gameData.Fingerprint,
                        fits.Select(entry => entry.Request).ToArray()))),
                    cancellationToken)
                .AsTask().WaitAsync(options.ComputeOutboundWriteTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            node.Pending.TryRemove(batchId, out _);
            await ReleaseClaimsAsync(ClaimsOf(fits), $"批次下发失败：{exception.Message}", cancellationToken);
            return;
        }

        ComputeAdvanceBatchResponse response;
        try
        {
            response = await waiter.Task.WaitAsync(options.ComputeRequestTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 节点断线（FailAllPending 注入异常）或批次超时：整批退回 pending 退避重试。
            node.Pending.TryRemove(batchId, out _);
            if (_failureLog.TryAcquire())
                logger.LogError("ComputeNode 批次派发失败 node={Node} jobs={Jobs} error={Error}",
                    node.Name, fits.Count, exception.Message);
            await ReleaseClaimsAsync(ClaimsOf(fits), exception.Message, cancellationToken);
            return;
        }

        if (response is null || response.Jobs is null
            || !string.Equals(response.ContractVersion, ComputeDispatchContract.Version, StringComparison.Ordinal))
        {
            node.Pending.TryRemove(batchId, out _);
            await ReleaseClaimsAsync(ClaimsOf(fits), "节点响应契约无效", cancellationToken);
            return;
        }

        var results = new Dictionary<Guid, ComputeAdvanceJobResult>(response.Jobs.Count);
        foreach (var result in response.Jobs) results[result.JobId] = result;

        // 结果回写小并发（≤4）：不同玩家的提交头行锁天然不冲突；
        // 同一 job 的 section 落库与 compute_jobs 完结并入同一事务。
        await Parallel.ForEachAsync(fits,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (entry, ct) => ApplyJobResultAsync(entry.Claim, results.GetValueOrDefault(entry.Claim.JobId), ct));
    }

    /// <summary>单个任务结果回写：版本 CAS 落库与任务完结同事务提交；失败按该 job 自身 attempts 退避。</summary>
    private async ValueTask ApplyJobResultAsync(
        ComputeJobClaim claim, ComputeAdvanceJobResult? result, CancellationToken cancellationToken)
    {
        if (result is null || result.PlayerId != claim.PlayerId)
        {
            await jobs.FailAsync(claim.JobId, claim.LeaseToken, "节点响应缺少对应任务结果",
                retryable: true, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
            return;
        }
        if (!result.Success)
        {
            await jobs.FailAsync(claim.JobId, claim.LeaseToken,
                $"{result.ErrorCode}:{result.ErrorMessage}",
                result.Retryable, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var writes = result.Sections is { Count: > 0 }
                ? result.Sections.Select(section => ComputeSectionCodec.ToSectionWrite(
                    claim.PlayerId, result.TargetStateVersion, section, now)).ToArray()
                : throw new InvalidDataException("成功结果缺少 section 输出。");
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var applied = await sections.ApplyComputeResultInTransactionAsync(
                connection, transaction,
                claim.PlayerId, result.SourceStateVersion, result.TargetStateVersion,
                writes, claim.TargetTime, gameData.Fingerprint, cancellationToken);
            // output_payload 只存摘要：result JSON 内含全量 base64 section，整存会把单任务存储放大 ~2.3 倍。
            var output = JsonSerializer.SerializeToUtf8Bytes(new ComputeJobOutputSummary(
                claim.JobId, claim.PlayerId, result.SourceStateVersion, result.TargetStateVersion,
                applied.ToString(), result.Metrics, result.ErrorCode, result.ErrorMessage), ServerJson.Options);
            if (applied != ComputeApplyOutcome.Applied)
            {
                // 玩家已上线或版本漂移：结果丢弃但任务仍按完成归档，证据留在 output_payload。
                logger.LogInformation("冷挂机结果因版本漂移被丢弃 job={JobId} player={PlayerId} outcome={Outcome}",
                    claim.JobId, claim.PlayerId, applied);
            }
            await PostgresComputeJobRepository.CompleteInTransactionAsync(
                connection, transaction, claim.JobId, claim.LeaseToken, output, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            // 节点产出污染不能重试出正确结果：进 dead 保留诊断窗口。
            await jobs.FailAsync(claim.JobId, claim.LeaseToken, $"INVALID_OUTPUT:{exception.Message}",
                retryable: false, TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_failureLog.TryAcquire())
                logger.LogError(exception, "冷挂机结果落库失败 job={JobId} player={PlayerId}", claim.JobId, claim.PlayerId);
            await jobs.FailAsync(claim.JobId, claim.LeaseToken, exception.Message,
                retryable: true, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
        }
    }

    /// <summary>
    /// 按租约令牌整批退回 pending：同一批领取共享一个令牌，但各 job 的 attempts 可能不同
    /// （孤儿行重取会累加）。retryAfter 未指定时按 attempts 分组施加各自指数退避；
    /// 显式指定时统一退避——批次字节预算截断传 <see cref="TimeSpan.Zero"/> 立即回 pending 下轮再派。
    /// </summary>
    private async Task ReleaseClaimsAsync(
        IReadOnlyList<ComputeJobClaim> claims,
        string error,
        CancellationToken cancellationToken,
        TimeSpan? retryAfter = null)
    {
        if (claims.Count == 0) return;
        if (retryAfter is { } uniform)
        {
            await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                claims.Select(claim => claim.JobId).ToArray(), error,
                uniform, options.ComputeMaxAttempts, cancellationToken);
            return;
        }
        foreach (var group in claims.GroupBy(claim => claim.Attempts))
        {
            await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                group.Select(claim => claim.JobId).ToArray(), error,
                Backoff(group.Key), options.ComputeMaxAttempts, cancellationToken);
        }
    }

    private static IReadOnlyList<ComputeJobClaim> ClaimsOf(IReadOnlyList<ReadyJob> entries) =>
        entries.Select(entry => entry.Claim).ToArray();

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 8))));

    /// <summary>一个通过载荷校验、可参与装批的任务及其解码后字节估算。</summary>
    private sealed record ReadyJob(ComputeJobClaim Claim, ComputeAdvanceJobRequest Request, long DecodedBytes);
}

/// <summary>
/// 冷挂机派发链的服务装配扩展：仅 Api 角色装配。
/// /internal/compute-link 端点只在 Api map，PersistenceWorker 装配注册表只会空转，
/// 且 ColdCatchUpWorker 会与 Api 重复生产任务——生产者与派发器统一收拢到 Api。
/// </summary>
public static class ComputeDispatchServiceCollectionExtensions
{
    /// <summary>
    /// 注册冷挂机派发链：任务仓库、在线节点注册表、派发 worker 与冷挂机扫描 worker。
    /// </summary>
    public static IServiceCollection AddComputeDispatch(this IServiceCollection services)
    {
        services.AddSingleton<PostgresComputeJobRepository>();
        services.AddSingleton<ComputeLinkRegistry>();
        services.AddHostedService<ComputeDispatcherWorker>();
        services.AddHostedService<ColdCatchUpWorker>();
        return services;
    }
}
