namespace Server.Clean;

/// <summary>
/// compute-link 派发链的进程内观测指标（不引入外部依赖）：
/// 节点接入/拒绝计数、批次吞吐与失败分类、任务结局计数，
/// 以及最近完成批次样本（批量/解码字节/往返时延/积压等待）供分位数与分布统计。
/// 打点方：<see cref="ComputeLinkRegistry"/>（节点接入/注销）、
/// <see cref="ComputeLinkEndpoint"/>（握手拒绝/迟到结果）、
/// <see cref="ComputeDispatcherWorker"/>（批次与任务结局）。
/// 运维面板与健康检查只读快照；指标对象不接触令牌、连接串与玩家载荷。
/// </summary>
public sealed class ComputeLinkMetrics
{
    /// <summary>批次样本环形缓冲容量：足够覆盖近期吞吐波动，快照时复制后离线统计。</summary>
    private const int SampleCapacity = 256;

    private long _nodesConnected;
    private long _nodesDisconnected;
    private long _tokenRejected;
    private long _nodeLimitRejected;
    private long _fingerprintRejected;
    private long _handshakeRejected;
    private long _lateResultsDropped;

    private long _batchesWritten;
    private long _batchesCompleted;
    private long _batchesFailedWrite;
    private long _batchesFailedTimeout;
    private long _batchesFailedDisconnect;
    private long _batchesFailedContract;
    private long _batchesFailedError;

    private long _jobsClaimed;
    private long _jobsDispatched;
    private long _jobsReleased;
    private long _jobsApplied;
    private long _jobsDriftDiscarded;
    private long _jobsNodeFailed;
    private long _jobsPersistFailed;
    private long _jobsDead;

    private readonly object _sampleGate = new();
    private readonly BatchSample[] _samples = new BatchSample[SampleCapacity];
    private int _sampleHead;
    private int _sampleCount;

    /// <summary>批次失败分类：与派发路径上各失败出口一一对应。</summary>
    public enum BatchFailureKind : byte
    {
        /// <summary>出站队列写入失败（含写超时）。</summary>
        Write,
        /// <summary>批次等待响应超时。</summary>
        Timeout,
        /// <summary>节点链路断开，在飞批次被统一了结。</summary>
        Disconnect,
        /// <summary>节点响应契约版本不符或载荷缺失。</summary>
        Contract,
        /// <summary>派发任务未捕获异常兜底。</summary>
        Error
    }

    /// <summary>单个已完成批次的关键样本。</summary>
    /// <param name="Jobs">批内任务数。</param>
    /// <param name="DecodedBytes">批内任务解码后字节合计。</param>
    /// <param name="RoundTripMs">出站写入到收到合法响应的往返毫秒。</param>
    /// <param name="MaxWaitAgeMs">批内最大的「目标时刻 → 派发」积压等待毫秒（近似租约/队列等待时长）。</param>
    internal readonly record struct BatchSample(int Jobs, long DecodedBytes, double RoundTripMs, double MaxWaitAgeMs);

    /// <summary>节点注册成功（拨入握手通过）。</summary>
    public void RecordNodeConnected() => Interlocked.Increment(ref _nodesConnected);
    /// <summary>节点实际注销。</summary>
    public void RecordNodeDisconnected() => Interlocked.Increment(ref _nodesDisconnected);
    /// <summary>令牌校验失败拒绝拨入。</summary>
    public void RecordTokenRejected() => Interlocked.Increment(ref _tokenRejected);
    /// <summary>在线节点数达上限拒绝拨入（含注册期拒绝）。</summary>
    public void RecordNodeLimitRejected() => Interlocked.Increment(ref _nodeLimitRejected);
    /// <summary>配表指纹不匹配拒绝接入。</summary>
    public void RecordFingerprintRejected() => Interlocked.Increment(ref _fingerprintRejected);
    /// <summary>hello 契约/能力非法等握手期协议违规拒绝。</summary>
    public void RecordHandshakeRejected() => Interlocked.Increment(ref _handshakeRejected);
    /// <summary>丢弃迟到或未知 batchId 的结果帧。</summary>
    public void RecordLateResultDropped() => Interlocked.Increment(ref _lateResultsDropped);

    /// <summary>领取到待派任务（含后续可能退回的行）。</summary>
    public void RecordJobsClaimed(int count) => Interlocked.Add(ref _jobsClaimed, count);
    /// <summary>批次帧成功写入节点出站队列：累计批次/任务/节点发送侧计数。</summary>
    public void RecordBatchWritten(ComputeLinkNode node, int jobs, long frameBytes)
    {
        ArgumentNullException.ThrowIfNull(node);
        Interlocked.Increment(ref _batchesWritten);
        Interlocked.Add(ref _jobsDispatched, jobs);
        node.RecordBatchSent(jobs, frameBytes);
    }
    /// <summary>批次拿到契约合法的响应：记入样本环与节点往返时延。</summary>
    public void RecordBatchCompleted(
        ComputeLinkNode node, int jobs, long decodedBytes, TimeSpan roundTrip, double maxWaitAgeMs)
    {
        ArgumentNullException.ThrowIfNull(node);
        Interlocked.Increment(ref _batchesCompleted);
        node.RecordBatchCompleted(roundTrip);
        lock (_sampleGate)
        {
            _samples[_sampleHead] = new BatchSample(jobs, decodedBytes, roundTrip.TotalMilliseconds, maxWaitAgeMs);
            _sampleHead = (_sampleHead + 1) % SampleCapacity;
            if (_sampleCount < SampleCapacity) _sampleCount++;
        }
    }
    /// <summary>批次失败（未拿到合法响应即退回租约）。</summary>
    public void RecordBatchFailed(ComputeLinkNode node, BatchFailureKind kind)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.RecordBatchFailed();
        switch (kind)
        {
            case BatchFailureKind.Write: Interlocked.Increment(ref _batchesFailedWrite); break;
            case BatchFailureKind.Timeout: Interlocked.Increment(ref _batchesFailedTimeout); break;
            case BatchFailureKind.Disconnect: Interlocked.Increment(ref _batchesFailedDisconnect); break;
            case BatchFailureKind.Contract: Interlocked.Increment(ref _batchesFailedContract); break;
            default: Interlocked.Increment(ref _batchesFailedError); break;
        }
    }

    /// <summary>链路层失败整批退回 pending（租约释放）。</summary>
    public void RecordJobsReleased(int count) => Interlocked.Add(ref _jobsReleased, count);
    /// <summary>任务结果落库 Applied（含空推进与资产入账）。</summary>
    public void RecordJobApplied(ComputeLinkNode node)
    {
        Interlocked.Increment(ref _jobsApplied);
        node.RecordJobApplied();
    }
    /// <summary>结果因版本漂移被丢弃（任务仍按完成归档）。</summary>
    public void RecordJobDriftDiscarded() => Interlocked.Increment(ref _jobsDriftDiscarded);
    /// <summary>节点回报失败或响应缺该任务结果（可重试退回）。</summary>
    public void RecordJobNodeFailed(ComputeLinkNode node)
    {
        Interlocked.Increment(ref _jobsNodeFailed);
        node.RecordJobFailed();
    }
    /// <summary>主服侧结果落库异常退回（非节点责任）。</summary>
    public void RecordJobPersistFailed() => Interlocked.Increment(ref _jobsPersistFailed);
    /// <summary>任务被判 dead 终态（坏载荷/超批次预算/节点产出污染）。</summary>
    public void RecordJobDead(ComputeLinkNode node)
    {
        Interlocked.Increment(ref _jobsDead);
        node.RecordJobFailed();
    }

    /// <summary>汇总当前计数与样本统计为只读快照；快照本身不引用实时状态。</summary>
    public ComputeLinkMetricsSnapshot Snapshot()
    {
        BatchSample[] samples;
        lock (_sampleGate)
        {
            samples = _samples.AsSpan(0, _sampleCount).ToArray();
        }
        return new ComputeLinkMetricsSnapshot(
            Interlocked.Read(ref _nodesConnected),
            Interlocked.Read(ref _nodesDisconnected),
            Interlocked.Read(ref _tokenRejected),
            Interlocked.Read(ref _nodeLimitRejected),
            Interlocked.Read(ref _fingerprintRejected),
            Interlocked.Read(ref _handshakeRejected),
            Interlocked.Read(ref _lateResultsDropped),
            Interlocked.Read(ref _batchesWritten),
            Interlocked.Read(ref _batchesCompleted),
            Interlocked.Read(ref _batchesFailedWrite),
            Interlocked.Read(ref _batchesFailedTimeout),
            Interlocked.Read(ref _batchesFailedDisconnect),
            Interlocked.Read(ref _batchesFailedContract),
            Interlocked.Read(ref _batchesFailedError),
            Interlocked.Read(ref _jobsClaimed),
            Interlocked.Read(ref _jobsDispatched),
            Interlocked.Read(ref _jobsReleased),
            Interlocked.Read(ref _jobsApplied),
            Interlocked.Read(ref _jobsDriftDiscarded),
            Interlocked.Read(ref _jobsNodeFailed),
            Interlocked.Read(ref _jobsPersistFailed),
            Interlocked.Read(ref _jobsDead),
            ComputeBatchSampleSummary.From(samples));
    }
}

/// <summary>compute-link 派发链计数快照（ camelCase 序列化后直接上运维面板）。</summary>
public sealed record ComputeLinkMetricsSnapshot(
    long NodesConnected,
    long NodesDisconnected,
    long TokenRejected,
    long NodeLimitRejected,
    long FingerprintRejected,
    long HandshakeRejected,
    long LateResultsDropped,
    long BatchesWritten,
    long BatchesCompleted,
    long BatchesFailedWrite,
    long BatchesFailedTimeout,
    long BatchesFailedDisconnect,
    long BatchesFailedContract,
    long BatchesFailedError,
    long JobsClaimed,
    long JobsDispatched,
    long JobsReleased,
    long JobsApplied,
    long JobsDriftDiscarded,
    long JobsNodeFailed,
    long JobsPersistFailed,
    long JobsDead,
    ComputeBatchSampleSummary RecentBatches);

/// <summary>最近完成批次样本的分位数与规模分布统计。</summary>
/// <param name="SampleCount">样本数（环形缓冲最多保留 256）。</param>
/// <param name="RoundTripAvgMs">批次往返时延均值（毫秒）。</param>
/// <param name="RoundTripP50Ms">批次往返时延 p50（毫秒）。</param>
/// <param name="RoundTripP95Ms">批次往返时延 p95（毫秒）。</param>
/// <param name="RoundTripMaxMs">批次往返时延最大值（毫秒）。</param>
/// <param name="WaitAgeP50Ms">任务积压等待（目标时刻→派发）p50（毫秒）。</param>
/// <param name="WaitAgeP95Ms">任务积压等待 p95（毫秒）。</param>
/// <param name="WaitAgeMaxMs">任务积压等待最大值（毫秒）。</param>
/// <param name="JobsP50">批内任务数 p50。</param>
/// <param name="JobsMax">批内任务数最大值。</param>
/// <param name="BytesP50">批次解码后字节 p50。</param>
/// <param name="BytesMax">批次解码后字节最大值。</param>
/// <param name="JobsHistogram">批内任务数分布桶（1 / 2-4 / 5-16 / 17-64 / 65+）。</param>
public sealed record ComputeBatchSampleSummary(
    int SampleCount,
    double RoundTripAvgMs,
    double RoundTripP50Ms,
    double RoundTripP95Ms,
    double RoundTripMaxMs,
    double WaitAgeP50Ms,
    double WaitAgeP95Ms,
    double WaitAgeMaxMs,
    double JobsP50,
    int JobsMax,
    long BytesP50,
    long BytesMax,
    IReadOnlyDictionary<string, long> JobsHistogram)
{
    /// <summary>从样本数组离线统计分位数与分布；样本为空时各值取 0。</summary>
    internal static ComputeBatchSampleSummary From(IReadOnlyList<ComputeLinkMetrics.BatchSample> samples)
    {
        var roundTrips = samples.Select(sample => (double)sample.RoundTripMs).OrderBy(value => value).ToArray();
        var waits = samples.Select(sample => sample.MaxWaitAgeMs).OrderBy(value => value).ToArray();
        var jobs = samples.Select(sample => (double)sample.Jobs).OrderBy(value => value).ToArray();
        var bytes = samples.Select(sample => (double)sample.DecodedBytes).OrderBy(value => value).ToArray();
        var histogram = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["1"] = 0, ["2-4"] = 0, ["5-16"] = 0, ["17-64"] = 0, ["65+"] = 0
        };
        foreach (var sample in samples)
        {
            var bucket = sample.Jobs switch
            {
                <= 1 => "1",
                <= 4 => "2-4",
                <= 16 => "5-16",
                <= 64 => "17-64",
                _ => "65+"
            };
            histogram[bucket]++;
        }
        return new ComputeBatchSampleSummary(
            samples.Count,
            Math.Round(roundTrips.Length == 0 ? 0 : roundTrips.Average(), 2),
            Math.Round(Percentile(roundTrips, 50), 2),
            Math.Round(Percentile(roundTrips, 95), 2),
            Math.Round(roundTrips.Length == 0 ? 0 : roundTrips[^1], 2),
            Math.Round(Percentile(waits, 50), 2),
            Math.Round(Percentile(waits, 95), 2),
            Math.Round(waits.Length == 0 ? 0 : waits[^1], 2),
            Math.Round(Percentile(jobs, 50), 2),
            (int)(jobs.Length == 0 ? 0 : jobs[^1]),
            (long)Percentile(bytes, 50),
            (long)(bytes.Length == 0 ? 0 : bytes[^1]),
            histogram);
    }

    /// <summary>最近秩分位数；样本为空返回 0。</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}

/// <summary>
/// 在线计算节点的运维视图：名称、容量、在飞/等待/出站积压、拨入时长、
/// 运行时指纹与节点侧吞吐/时延/失败计数。序列化后经运维面板展示，不含令牌与连接串。
/// </summary>
public sealed record ComputeLinkNodeView(
    Guid NodeId,
    string Name,
    int Capacity,
    int InFlight,
    int Available,
    bool Draining,
    int PendingResults,
    int OutboundDepth,
    DateTimeOffset ConnectedAt,
    double UptimeSeconds,
    string? RuntimeFingerprint,
    long BatchesSent,
    long BatchesCompleted,
    long BatchesFailed,
    long JobsApplied,
    long JobsFailed,
    double LastRoundTripMs,
    double AvgRoundTripMs,
    double MaxRoundTripMs,
    long BytesSent)
{
    /// <summary>从实时节点对象截取一致性近似的只读视图。</summary>
    public static ComputeLinkNodeView From(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new ComputeLinkNodeView(
            node.NodeId,
            node.Name,
            node.Capacity,
            node.InFlight,
            node.Available,
            node.Draining,
            node.Pending.Count,
            node.OutboundDepth,
            node.ConnectedAt,
            (DateTimeOffset.UtcNow - node.ConnectedAt).TotalSeconds,
            node.RuntimeFingerprint,
            node.BatchesSent,
            node.BatchesCompleted,
            node.BatchesFailed,
            node.JobsApplied,
            node.JobsFailed,
            node.LastRoundTripMs,
            node.AvgRoundTripMs,
            node.MaxRoundTripMs,
            node.BytesSent);
    }
}

/// <summary>
/// platform.compute_jobs 积压快照：供运维面板与诊断端点展示队列健康。
/// </summary>
/// <param name="Pending">待派任务数（含未到重试闸门的）。</param>
/// <param name="PendingDue">已到点且过重试闸门、立即可领取的积压数。</param>
/// <param name="Leased">已领取在飞（含孤儿租约）。</param>
/// <param name="Succeeded">累计成功终态行数（保留窗口内）。</param>
/// <param name="Dead">dead 终态行数（诊断窗口）。</param>
/// <param name="OldestPendingAgeSeconds">最老 pending 行按 target_time 的积压年龄（秒）；无积压为 null。</param>
public sealed record ComputeJobBacklog(
    long Pending,
    long PendingDue,
    long Leased,
    long Succeeded,
    long Dead,
    double? OldestPendingAgeSeconds);

/// <summary>
/// 冷挂机派发链健康检查：仅以节点在线状态反映链路可用性，不携带令牌/连接串/内部异常。
/// 不挂 ready 标签——节点未接入属容量事实而非服务不就绪，避免影响 /readyz 探针口径；
/// 面板 /api/health 全量执行所有检查，运维侧仍可见。
/// </summary>
public sealed class ComputeLinkHealthCheck(ServerOptions options, ComputeLinkRegistry registry) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.ComputeNodeConfigured)
            return Task.FromResult(HealthCheckResult.Healthy("冷挂机派发链未启用。"));
        var nodes = registry.Nodes;
        var data = new Dictionary<string, object>
        {
            ["nodes"] = nodes.Count,
            ["inFlight"] = nodes.Sum(node => node.InFlight),
            ["capacity"] = nodes.Sum(node => node.Capacity)
        };
        return Task.FromResult(nodes.Count > 0
            ? HealthCheckResult.Healthy($"在线计算节点 {nodes.Count} 个。", data: data)
            : HealthCheckResult.Degraded("已启用但暂无在线计算节点。", data: data));
    }
}
