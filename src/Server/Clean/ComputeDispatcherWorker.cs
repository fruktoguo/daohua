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
    NpgsqlDataSource dataSource,
    ComputeLinkRegistry registry,
    ComputeLinkMetrics metrics,
    ComputeResultCommitter committer,
    ServerOptions options,
    GameDataCatalog gameData,
    IComputeResultVerifier resultVerifier,
    ILogger<ComputeDispatcherWorker> logger) : BackgroundService
{
    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    /// <summary>在飞派发任务簿记：停机时统一等待排空；各任务槽位已在 Acquire 时预留。</summary>
    private readonly ConcurrentDictionary<Task, byte> _dispatches = new();
    /// <summary>
    /// 在飞批次的独立生命周期：主循环随 stoppingToken 停派后，已下发批次仍要在排空窗口内
    /// 照常收结果落库；窗口耗尽才取消，未完结租约按孤儿语义到期重取。
    /// </summary>
    private readonly CancellationTokenSource _dispatchCts = new();
    /// <summary>停机排空窗口：主服下线前给在飞批次的收尾时间，覆盖绝大多数正常批次往返。</summary>
    private static readonly TimeSpan ShutdownDrainWindow = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 派发主循环：未配置计算节点令牌/停用时直接退出（任务保留 pending）；
    /// 否则按类注释所述「Acquire 节点槽位 → ClaimDue 领取 → 派生独立派发任务」运转，
    /// 无空闲节点或无到期任务时等待容量信号唤醒或轮询周期到期；停机时先停派发再排空在飞批次。
    /// </summary>
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
                        // 节点派发只承接 pending 超过溢出阈值的积压：本地执行器实时消费新任务。
                        claims = await jobs.ClaimDueAsync(maxJobs, options.ComputeLease,
                            options.ComputeOverflowAfter, stoppingToken);
                        metrics.RecordJobsClaimed(claims.Count);
                    }
                    catch
                    {
                        node.ReleaseSlot();
                        throw;
                    }
                    if (claims.Count == 0)
                    {
                        node.ReleaseSlot();
                        // 归还自身槽位会完成轮首捕获的信号：必须释放后重取一份新信号再等，
                        // 否则自唤醒让退避轮询退化为空转，把 ClaimDue 轮询打成满速自旋。
                        var idleSignal = registry.CapacitySignal;
                        await Task.WhenAny(idleSignal, jobs.JobAvailableSignal, Task.Delay(options.ComputePollInterval, stoppingToken));
                        continue;
                    }
                    // 槽位已占即派生独立批次任务，主循环不等响应直接继续取下一个节点；
                    // 批次挂 _dispatchCts 而非 stoppingToken：停机时给在飞批次排空窗口照常收结果。
                    Track(DispatchBatchAsync(node, claims, _dispatchCts.Token));
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
            // 停机收敛：先向全部在线节点下发 drain 提示（compute-link v1.3），节点跑完在飞后
            // 主动断链重连；在飞批次不受 stoppingToken 影响，在排空窗口内照常收结果落库，
            // 窗口耗尽才取消——未完结租约按既有孤儿语义到期重取，收敛等待本身有界。
            try { registry.DrainAll("master draining"); }
            catch { /* 排空提示下发失败不阻断退出 */ }
            try { await Task.WhenAll(_dispatches.Keys).WaitAsync(ShutdownDrainWindow); }
            catch { /* 关闭路径不阻断退出 */ }
            _dispatchCts.Cancel();
            try { await Task.WhenAll(_dispatches.Keys).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* 残余派发随租约孤儿语义重取 */ }
            // _dispatchCts 不 Dispose：进程已在退出，残余任务可能仍持有其 token。
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
            metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Error);
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
            // v3 任务必须携带完整身份、提交头快照与全集 8 段载荷——缺一则节点无法还原聚合，
            // 这里先行判死，不把必然失败的任务送上链路烧节点配额。
            if (request is null || request.PlayerId != claim.PlayerId
                || request.AccountId == Guid.Empty
                || string.IsNullOrWhiteSpace(request.DisplayName)
                || string.IsNullOrWhiteSpace(request.HeadChecksum)
                || string.IsNullOrWhiteSpace(request.HeadGameDataFingerprint)
                || request.Sections is null || request.Sections.Count != PlayerSectionKinds.All.Count
                || request.Sections.Any(section => section is null || string.IsNullOrEmpty(section.PayloadBase64)))
            {
                metrics.RecordJobDead(node);
                await committer.FailWithDiagnosticsAsync(claim, "INVALID_INPUT", "input_payload 无效", retryable: false,
                    TimeSpan.Zero, cancellationToken);
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
                metrics.RecordJobDead(node);
                await committer.FailWithDiagnosticsAsync(entry.Claim, "JOB_TOO_LARGE",
                    $"JOB_TOO_LARGE: 单任务解码后 {entry.DecodedBytes}B 超过节点组批次预算 {groupBudget}B",
                    retryable: false, TimeSpan.Zero, cancellationToken);
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

        // 观测口径：解码后字节供批次大小分布；积压等待取批内最大「目标时刻→派发」年龄，近似租约等待时长。
        var decodedBytes = fits.Sum(entry => entry.DecodedBytes);
        var maxWaitAgeMs = fits.Max(entry => Math.Max(0, (DateTimeOffset.UtcNow - entry.Claim.TargetTime).TotalMilliseconds));
        var batchStarted = DateTimeOffset.UtcNow;
        var frame = ComputeLinkCodec.Encode(new ComputeLinkBatch(batchId, new ComputeAdvanceBatchRequest(
            ComputeDispatchContract.Version,
            gameData.Fingerprint,
            fits.Select(entry => entry.Request).ToArray())));
        try
        {
            // 出站队列 bounded 64：并行派发下可能顶满，补写入超时防止派发任务被挂死。
            await node.Outbound.Writer.WriteAsync(frame, cancellationToken)
                .AsTask().WaitAsync(options.ComputeOutboundWriteTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            node.Pending.TryRemove(batchId, out _);
            metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Write);
            if (_failureLog.TryAcquire())
                logger.LogWarning("ComputeNode 批次下发失败 node={Node} name={Name} jobs={Jobs} error={Error}",
                    node.NodeId, node.Name, fits.Count, exception.Message);
            await ReleaseClaimsAsync(ClaimsOf(fits), $"批次下发失败：{exception.Message}", cancellationToken);
            return;
        }
        metrics.RecordBatchWritten(node, fits.Count, frame.LongLength);

        ComputeAdvanceBatchResponse response;
        try
        {
            response = await waiter.Task.WaitAsync(options.ComputeRequestTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 节点断线（FailAllPending 注入异常）或批次超时：整批退回 pending 退避重试。
            node.Pending.TryRemove(batchId, out _);
            metrics.RecordBatchFailed(node, exception switch
            {
                ComputeNodeDisconnectedException => ComputeLinkMetrics.BatchFailureKind.Disconnect,
                TimeoutException => ComputeLinkMetrics.BatchFailureKind.Timeout,
                _ => ComputeLinkMetrics.BatchFailureKind.Error
            });
            if (_failureLog.TryAcquire())
                logger.LogError("ComputeNode 批次派发失败 node={Node} jobs={Jobs} error={Error}",
                    node.Name, fits.Count, exception.Message);
            await ReleaseClaimsAsync(ClaimsOf(fits), exception.Message, cancellationToken);
            return;
        }

        if (response is null || response.Jobs is null
            || !string.Equals(response.ContractVersion, ComputeDispatchContract.Version, StringComparison.Ordinal))
        {
            // 契约不符此前静默退回：节点可能跑旧版本，必须留可计数证据。
            node.Pending.TryRemove(batchId, out _);
            metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Contract);
            if (_failureLog.TryAcquire())
                logger.LogWarning(
                    "ComputeNode 批次响应契约无效 node={Node} name={Name} jobs={Jobs} expected={Expected} actual={Actual}",
                    node.NodeId, node.Name, fits.Count, ComputeDispatchContract.Version,
                    response?.ContractVersion ?? "null");
            await ReleaseClaimsAsync(ClaimsOf(fits), "节点响应契约无效", cancellationToken);
            return;
        }
        metrics.RecordBatchCompleted(node, fits.Count, decodedBytes, DateTimeOffset.UtcNow - batchStarted, maxWaitAgeMs);

        var results = new Dictionary<Guid, ComputeAdvanceJobResult>(response.Jobs.Count);
        var expectedJobIds = fits.Select(entry => entry.Claim.JobId).ToHashSet();
        if (response.Jobs.Any(result => result is null || !expectedJobIds.Contains(result.JobId)
                || !results.TryAdd(result.JobId, result)))
        {
            // 整批拒绝，不能让重复结果覆盖第一份或静默忽略未派发任务。
            metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Contract);
            foreach (var entry in fits)
            {
                metrics.RecordJobDead(node);
                await committer.FailWithDiagnosticsAsync(entry.Claim, "INVALID_OUTPUT",
                    "INVALID_OUTPUT:批次含重复或额外任务结果", retryable: false,
                    TimeSpan.Zero, cancellationToken);
            }
            return;
        }

        // 结果回写按批并发（≤8，与单批任务数对齐）：不同玩家的提交头行锁天然不冲突；
        // 同一 job 的 section 落库与 compute_jobs 完结并入同一事务。
        await Parallel.ForEachAsync(fits,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            (entry, ct) => ApplyJobResultAsync(node, entry, results.GetValueOrDefault(entry.Claim.JobId), ct));
    }

    /// <summary>
    /// 单个任务结果回写（compute.v3）：RepeatableRead 固定快照内，section CAS、资产增量入账与任务完结并入同一事务，
    /// 三者同生同灭——结果因版本漂移被丢弃时其资产增量一并丢弃（增量按旧状态结算，不可入账新状态）。
    /// 空推进（目标版本等于源版本）是合法结果：只前移提交头模拟时钟，阻断无变化玩家的无限重派。
    /// 失败按该 job 自身 attempts 退避；节点产出污染不可重试，直接判 dead 保留证据。
    /// </summary>
    private async ValueTask ApplyJobResultAsync(
        ComputeLinkNode node, ReadyJob entry, ComputeAdvanceJobResult? result, CancellationToken cancellationToken)
    {
        var claim = entry.Claim;
        if (result is null || result.PlayerId != claim.PlayerId)
        {
            metrics.RecordJobNodeFailed(node);
            await committer.FailWithDiagnosticsAsync(claim, "MISSING_RESULT", "节点响应缺少对应任务结果",
                retryable: true, ComputeResultCommitter.Backoff(claim.Attempts), cancellationToken);
            return;
        }
        if (!result.Success)
        {
            metrics.RecordJobNodeFailed(node);
            await committer.FailWithDiagnosticsAsync(claim, "NODE_FAILURE",
                $"{result.ErrorCode}:{result.ErrorMessage}",
                result.Retryable, ComputeResultCommitter.Backoff(claim.Attempts), cancellationToken);
            return;
        }

        try
        {
            // 节点输出只作候选：从冻结任务独立重演，写回使用主服自己的结果，不信任节点自证。
            var verification = await resultVerifier.VerifyAsync(entry.Request, result, cancellationToken);
            if (!verification.Accepted)
                throw new InvalidDataException(verification.Error ?? "主服独立重演拒绝节点结果。");

            // 可信产物经统一提交器落库：租约/基线/钱包复核、section CAS、资产入账与任务完结同事务。
            var applied = await committer.CommitAsync(claim, entry.Request, verification,
                result.Metrics, result.ErrorCode, result.ErrorMessage, cancellationToken);
            if (applied == ComputeApplyOutcome.Applied)
            {
                metrics.RecordJobApplied(node);
            }
            else
            {
                // 玩家已上线或版本漂移：结果丢弃但任务仍按完成归档，证据留在 output_payload。
                metrics.RecordJobDriftDiscarded();
                logger.LogInformation("冷挂机结果因版本漂移被丢弃 job={JobId} player={PlayerId} outcome={Outcome}",
                    claim.JobId, claim.PlayerId, applied);
            }
        }
        catch (InvalidDataException exception)
        {
            // 节点产出污染不能重试出正确结果：进 dead 保留诊断窗口，实际终态日志不受重试节流影响。
            metrics.RecordJobDead(node);
            await committer.FailWithDiagnosticsAsync(claim, "INVALID_OUTPUT", $"INVALID_OUTPUT:{exception.Message}",
                retryable: false, TimeSpan.Zero, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.RecordJobPersistFailed();
            if (_failureLog.TryAcquire())
                logger.LogError(exception, "冷挂机结果落库失败 job={JobId} player={PlayerId}", claim.JobId, claim.PlayerId);
            await committer.FailWithDiagnosticsAsync(claim, "PERSISTENCE_FAILURE", exception.Message,
                retryable: true, ComputeResultCommitter.Backoff(claim.Attempts), cancellationToken);
        }
    }

    /// <summary>
    /// 校验并按资产键合并节点回传的资产增量。该方法只负责边界格式与配表键校验；
    /// 结果是否可信必须由主服独立重演验证器确认，调用方不得仅凭本方法直接入账。
    /// </summary>
    public static Server.Clean.AssetDelta[] ValidateAndMergeAssetDeltas(
        Guid playerId, ComputeAdvanceJobRequest request, IReadOnlyList<ComputeAssetDelta>? rows, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (rows is not { Count: > 0 }) return [];
        if (rows.Count > ComputeDispatchContract.MaxAssetDeltasPerResult)
            throw new InvalidDataException("资产增量行数超过上限。");

        var merged = new Dictionary<(string AssetId, string Variant), double>();
        foreach (var row in rows)
        {
            if (row is null
                || string.IsNullOrWhiteSpace(row.AssetId) || row.AssetId.Length > 128
                || row.Variant is null || row.Variant.Length > 128
                || !double.IsFinite(row.QuantityDelta) || row.QuantityDelta == 0)
                throw new InvalidDataException("资产增量行字段无效。");
            if (!IsKnownAssetVariant(row.AssetId, row.Variant, catalog))
                throw new InvalidDataException($"资产标识或变体不属于当前配表：{row.AssetId}:{row.Variant}。");

            var key = (row.AssetId, row.Variant);
            if (!merged.TryAdd(key, row.QuantityDelta))
                throw new InvalidDataException("资产增量包含重复键。");
            if (!double.IsFinite(merged[key]))
                throw new InvalidDataException("资产增量合并结果不是有限数值。");
        }

        return merged
            .Where(pair => pair.Value != 0)
            .OrderBy(pair => pair.Key.AssetId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Variant, StringComparer.Ordinal)
            .Select(pair => new Server.Clean.AssetDelta(playerId, pair.Key.AssetId, pair.Key.Variant, pair.Value))
            .ToArray();
    }


    private static bool IsKnownAssetVariant(string assetId, string variant, GameDataCatalog catalog)
    {
        if (assetId == Domain.Player.EnhancementRules.SpiritStoneItemId)
            return variant.Length == 0;
        var id = new Domain.Common.ItemId(assetId);
        if (!id.IsValid) return false;
        if (variant.Length == 0)
            return catalog.Items.ContainsKey(id) || catalog.RawEquipments.ContainsKey(assetId);
        if (!catalog.RawEquipments.ContainsKey(assetId)) return false;
        var level = Domain.Player.EnhancementRules.ParseLevel(variant);
        return level is > 0 and <= Domain.Player.EnhancementRules.MaxLevel
            && variant == "+" + level.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        var released = 0;
        if (retryAfter is { } uniform)
        {
            released = await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                claims.Select(claim => claim.JobId).ToArray(), error,
                uniform, options.ComputeMaxAttempts, cancellationToken);
        }
        else
        {
            foreach (var group in claims.GroupBy(claim => claim.Attempts))
            {
                released += await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                    group.Select(claim => claim.JobId).ToArray(), error,
                    ComputeResultCommitter.Backoff(group.Key), options.ComputeMaxAttempts, cancellationToken);
            }
        }
        // ReleaseLease 批量返回总更新行数；只查询达到终态次数的候选，避免把已完结或租约丢失任务误记判死。
        var finalAttempts = claims.Where(claim => claim.Attempts >= options.ComputeMaxAttempts).ToArray();
        if (released > 0 && finalAttempts.Length > 0)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT job_id FROM platform.compute_jobs
                WHERE job_id = ANY($1) AND status = 'dead' AND attempts >= $2
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = finalAttempts.Select(claim => claim.JobId).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = options.ComputeMaxAttempts });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var finalById = finalAttempts.ToDictionary(claim => claim.JobId);
            while (await reader.ReadAsync(cancellationToken))
                committer.LogTerminalFailure(finalById[reader.GetGuid(0)], "BATCH_FAILURE");
        }
        // 保持现有释放计数口径，本次只补终态日志。
        metrics.RecordJobsReleased(released);
    }

    private static IReadOnlyList<ComputeJobClaim> ClaimsOf(IReadOnlyList<ReadyJob> entries) =>
        entries.Select(entry => entry.Claim).ToArray();

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
    /// 注册离线挂机批算链：任务仓库、结果提交器、在线节点注册表、本地执行器、
    /// 派发 worker 与离线批算扫描 worker。扫描与本地执行不受计算节点配置门控。
    /// </summary>
    public static IServiceCollection AddComputeDispatch(this IServiceCollection services)
    {
        services.AddSingleton<PostgresComputeJobRepository>();
        services.AddSingleton<ComputeLinkMetrics>();
        services.AddSingleton<ComputeLinkRegistry>();
        services.AddSingleton<ComputeResultCommitter>();
        services.AddHostedService<LocalComputeExecutorWorker>();
        services.AddHostedService<ComputeDispatcherWorker>();
        services.AddHostedService<ColdCatchUpWorker>();
        return services;
    }
}
