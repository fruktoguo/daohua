using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Server.Clean;

/// <summary>
/// 主服务 → 计算节点组冷挂机派发 worker（compute-link.v1 反向拨入模型）。
/// 轮询领取 platform.compute_jobs 到期任务 → 从 <see cref="ComputeLinkRegistry"/>
/// 取剩余容量最大的在线节点 → 经节点链路下发批次 → 结果按 SourceStateVersion CAS 落库。
/// 无在线节点时不领取任务（attempts 不消耗）；节点断线/批次超时按租约令牌整批退回
/// pending，并按各 job 自身 attempts 分组施加指数退避；领取次数超过上限自动进入 dead 终态保留诊断窗口。
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ComputeNodeConfigured)
        {
            logger.LogInformation(
                "冷挂机派发器未启用：需要 ComputeNode:Enabled=true 且配置 ComputeNode:Token；任务保持 pending。");
            return;
        }

        logger.LogInformation("ComputeDispatcherWorker started batch={Batch} interval={Interval}（compute-link.v1 动态节点组）。",
            options.ComputeBatchSize, options.ComputePollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 先取节点再领任务：节点组为空时任务留在 pending 不动，不消耗领取次数。
                var node = registry.Acquire();
                if (node is null)
                {
                    await Task.Delay(options.ComputePollInterval, stoppingToken);
                    continue;
                }
                var claims = await jobs.ClaimDueAsync(options.ComputeBatchSize, options.ComputeLease, stoppingToken);
                if (claims.Count == 0)
                {
                    await Task.Delay(options.ComputePollInterval, stoppingToken);
                    continue;
                }
                await DispatchBatchAsync(node, claims, stoppingToken);
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

    private async Task DispatchBatchAsync(
        ComputeLinkNode node,
        IReadOnlyList<ComputeJobClaim> claims,
        CancellationToken cancellationToken)
    {
        var ready = new List<(ComputeJobClaim Claim, ComputeAdvanceJobRequest Request)>(claims.Count);
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
                || request.PlayerId != claim.PlayerId)
            {
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, "input_payload 无效", retryable: false,
                    TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
                continue;
            }
            ready.Add((claim, request));
        }
        if (ready.Count == 0) return;

        var batchId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<ComputeAdvanceBatchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!node.Pending.TryAdd(batchId, waiter))
        {
            await ReleaseClaimsAsync(claims, "批次登记冲突", cancellationToken);
            return;
        }

        try
        {
            await node.Outbound.Writer.WriteAsync(
                ComputeLinkCodec.Encode(new ComputeLinkBatch(batchId, new ComputeAdvanceBatchRequest(
                    ComputeDispatchContract.Version,
                    gameData.Fingerprint,
                    ready.Select(entry => entry.Request).ToArray()))),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            node.Pending.TryRemove(batchId, out _);
            await ReleaseClaimsAsync(claims, $"批次下发失败：{exception.Message}", cancellationToken);
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
                    node.Name, claims.Count, exception.Message);
            await ReleaseClaimsAsync(claims, exception.Message, cancellationToken);
            return;
        }

        if (response is null || response.Jobs is null
            || !string.Equals(response.ContractVersion, ComputeDispatchContract.Version, StringComparison.Ordinal))
        {
            node.Pending.TryRemove(batchId, out _);
            await ReleaseClaimsAsync(claims, "节点响应契约无效", cancellationToken);
            return;
        }

        var results = new Dictionary<Guid, ComputeAdvanceJobResult>(response.Jobs.Count);
        foreach (var result in response.Jobs) results[result.JobId] = result;

        // 结果回写小并发（≤4）：不同玩家的提交头行锁天然不冲突；
        // 同一 job 的 section 落库与 compute_jobs 完结并入同一事务。
        await Parallel.ForEachAsync(ready,
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
    /// （孤儿行重取会累加），按 attempts 分组后逐组应用各自的指数退避。
    /// </summary>
    private async Task ReleaseClaimsAsync(
        IReadOnlyList<ComputeJobClaim> claims, string error, CancellationToken cancellationToken)
    {
        foreach (var group in claims.GroupBy(claim => claim.Attempts))
        {
            await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                group.Select(claim => claim.JobId).ToArray(), error,
                Backoff(group.Key), options.ComputeMaxAttempts, cancellationToken);
        }
    }

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 8))));
}

/// <summary>
/// 冷挂机派发链的服务装配扩展。
/// Api 与 PersistenceWorker 两种进程角色共用；未配置 ComputeNode:* 时 worker 安全停用。
/// </summary>
public static class ComputeDispatchServiceCollectionExtensions
{
    /// <summary>
    /// 注册冷挂机派发链：任务仓库、在线节点注册表、派发 worker 与冷挂机扫描 worker。
    /// </summary>
    public static IServiceCollection AddComputeDispatch(this IServiceCollection services)
    {
        // PersistenceWorker 角色不注册 GameDataCatalog；派发器只需要指纹，按惰性工厂兜底。
        services.TryAddSingleton(_ => GameDataCatalog.LoadEmbedded());
        services.AddSingleton<PostgresComputeJobRepository>();
        services.AddSingleton<ComputeLinkRegistry>();
        services.AddHostedService<ComputeDispatcherWorker>();
        services.AddHostedService<ColdCatchUpWorker>();
        return services;
    }
}
