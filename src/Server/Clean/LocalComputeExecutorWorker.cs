namespace Server.Clean;

/// <summary>
/// 主服本地执行器（总纲18 §14.4）：常驻消费 compute_jobs 队列，与节点派发器共用同一队列
/// 但走不同领取口径——本地执行器以 minPendingAge=0 立即领取到期任务，节点派发只取 pending
/// 超过 ComputeNode:OverflowAfterSeconds 的溢出积压。
/// 单路串行逐任务执行：载荷反序列化/校验失败或独立重演拒绝 → 判 dead 保留诊断窗口；
/// 可信产物经 <see cref="ComputeResultCommitter"/> 与节点路径同一事务边界写回；
/// 持久化异常按任务 attempts 指数退避重试，超上限进 dead 终态。
/// 执行器不受计算节点配置门控：无节点接入时主服自行承接全部离线批算。
/// </summary>
public sealed class LocalComputeExecutorWorker(
    PostgresComputeJobRepository jobs,
    ComputeResultCommitter committer,
    ComputeLinkMetrics metrics,
    ServerOptions options,
    IComputeResultVerifier verifier,
    ILogger<LocalComputeExecutorWorker> logger) : BackgroundService
{
    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// 执行主循环：无到期任务时等待轮询周期；有任务则连续串行消费直到队列排空。
    /// 循环异常经节流记录后 5 秒退避重试。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "LocalComputeExecutorWorker started interval={Interval} lease={Lease}.",
            options.ComputePollInterval, options.ComputeLease);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken))
                    await Task.WhenAny(jobs.JobAvailableSignal, Task.Delay(options.ComputePollInterval, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_failureLog.TryAcquire())
                    logger.LogError(exception, "LocalComputeExecutorWorker 循环失败，将在下个周期重试。");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// 单步执行入口：领取一个到期任务并完整走完「校验 → 本地重演 → 事务写回」；
    /// 返回是否处理了一个任务（false 表示队列空，主循环转入轮询等待）。供测试驱动。
    /// </summary>
    internal async ValueTask<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claims = await jobs.ClaimDueAsync(1, options.ComputeLease, TimeSpan.Zero, cancellationToken);
        if (claims.Count == 0) return false;
        var claim = claims[0];
        metrics.RecordLocalJobClaimed();

        ComputeAdvanceJobRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ComputeAdvanceJobRequest>(claim.InputPayload, ServerJson.Options);
        }
        catch (JsonException)
        {
            // 坏载荷不会自愈：直接进 dead 保留诊断窗口。
        }
        // 与派发器同口径的载荷校验：缺身份/提交头/全集 section 的任务无法重演，先行判死。
        if (request is null || request.PlayerId != claim.PlayerId
            || request.AccountId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.HeadChecksum)
            || string.IsNullOrWhiteSpace(request.HeadGameDataFingerprint)
            || request.Sections is null || request.Sections.Count != PlayerSectionKinds.All.Count
            || request.Sections.Any(section => section is null || string.IsNullOrEmpty(section.PayloadBase64)))
        {
            metrics.RecordLocalJobDead();
            await committer.FailWithDiagnosticsAsync(claim, "INVALID_INPUT", "input_payload 无效",
                retryable: false, TimeSpan.Zero, cancellationToken);
            return true;
        }

        try
        {
            var verification = await verifier.ExecuteAsync(request, cancellationToken);
            if (!verification.Accepted)
            {
                // 本地重演拒绝即任务输入不可执行：判 dead 保留诊断窗口，不无限重试。
                metrics.RecordLocalJobDead();
                await committer.FailWithDiagnosticsAsync(claim, "LOCAL_REPLAY_REJECTED",
                    $"LOCAL_REPLAY_REJECTED:{verification.Error}", retryable: false, TimeSpan.Zero, cancellationToken);
                return true;
            }
            var applied = await committer.CommitAsync(claim, request, verification,
                nodeMetrics: null, nodeErrorCode: null, nodeErrorMessage: null, cancellationToken);
            if (applied == ComputeApplyOutcome.Applied)
            {
                metrics.RecordLocalJobApplied();
            }
            else
            {
                // 玩家已上线或版本漂移：结果丢弃但任务仍按完成归档，证据留在 output_payload。
                metrics.RecordLocalJobDriftDiscarded();
                logger.LogInformation("本地执行结果因版本漂移被丢弃 job={JobId} player={PlayerId} outcome={Outcome}",
                    claim.JobId, claim.PlayerId, applied);
            }
        }
        catch (InvalidDataException exception)
        {
            // 事务内载荷类校验失败不可重试出正确结果：进 dead 保留诊断窗口。
            metrics.RecordLocalJobDead();
            await committer.FailWithDiagnosticsAsync(claim, "INVALID_OUTPUT",
                $"INVALID_OUTPUT:{exception.Message}", retryable: false, TimeSpan.Zero, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.RecordLocalJobPersistFailed();
            if (_failureLog.TryAcquire())
                logger.LogError(exception, "本地执行结果落库失败 job={JobId} player={PlayerId}", claim.JobId, claim.PlayerId);
            await committer.FailWithDiagnosticsAsync(claim, "PERSISTENCE_FAILURE", exception.Message,
                retryable: true, ComputeResultCommitter.Backoff(claim.Attempts), cancellationToken);
        }
        return true;
    }
}
