namespace DaoHua.ComputeNode;

/// <summary>
/// 计算节点运行时统计计数器。
/// 使用 <see cref="Interlocked"/> 与 <see cref="Volatile"/> 保证并发安全，
/// 用于 <c>/healthz</c>、<c>/readyz</c> 与批处理指标聚合。
/// </summary>
public sealed class ComputeMetrics
{
    /// <summary>当前正在执行的批次数。</summary>
    private long _activeBatches;
    /// <summary>已完成的批次数。</summary>
    private long _completedBatches;
    /// <summary>已失败的批次数。</summary>
    private long _failedBatches;
    /// <summary>已取消的批次数。</summary>
    private long _cancelledBatches;
    /// <summary>已完成的任务数。</summary>
    private long _completedJobs;
    /// <summary>已失败的任务数。</summary>
    private long _failedJobs;
    /// <summary>被拒绝的请求数。</summary>
    private long _rejectedRequests;
    /// <summary>未授权的请求数。</summary>
    private long _unauthorizedRequests;
    /// <summary>总累计耗时毫秒数。</summary>
    private long _totalElapsedMilliseconds;

    /// <summary>获取当前正在执行的批次数。</summary>
    public long ActiveBatches => Volatile.Read(ref _activeBatches);
    /// <summary>获取已完成的批次数。</summary>
    public long CompletedBatches => Volatile.Read(ref _completedBatches);
    /// <summary>获取已失败的批次数。</summary>
    public long FailedBatches => Volatile.Read(ref _failedBatches);
    /// <summary>获取已取消的批次数。</summary>
    public long CancelledBatches => Volatile.Read(ref _cancelledBatches);
    /// <summary>获取已完成的任务数。</summary>
    public long CompletedJobs => Volatile.Read(ref _completedJobs);
    /// <summary>获取已失败的任务数。</summary>
    public long FailedJobs => Volatile.Read(ref _failedJobs);
    /// <summary>获取被拒绝的请求数。</summary>
    public long RejectedRequests => Volatile.Read(ref _rejectedRequests);
    /// <summary>获取未授权的请求数。</summary>
    public long UnauthorizedRequests => Volatile.Read(ref _unauthorizedRequests);
    /// <summary>获取总累计耗时毫秒数。</summary>
    public long TotalElapsedMilliseconds => Volatile.Read(ref _totalElapsedMilliseconds);

    /// <summary>开始一个批次。</summary>
    public void BeginBatch() => Interlocked.Increment(ref _activeBatches);
    /// <summary>结束一个批次。</summary>
    public void EndBatch() => Interlocked.Decrement(ref _activeBatches);
    /// <summary>记录一个已完成任务。</summary>
    public void RecordCompletedJob() => Interlocked.Increment(ref _completedJobs);
    /// <summary>记录一个失败任务。</summary>
    public void RecordFailedJob() => Interlocked.Increment(ref _failedJobs);
    /// <summary>记录一次被拒绝请求。</summary>
    public void RecordRejected() => Interlocked.Increment(ref _rejectedRequests);
    /// <summary>记录一次未授权请求。</summary>
    public void RecordUnauthorized() => Interlocked.Increment(ref _unauthorizedRequests);
    /// <summary>记录一个失败批次。</summary>
    public void RecordFailedBatch() => Interlocked.Increment(ref _failedBatches);
    /// <summary>记录一个取消批次。</summary>
    public void RecordCancelledBatch() => Interlocked.Increment(ref _cancelledBatches);

    /// <summary>
    /// 记录一个完整批次的执行时间。
    /// </summary>
    /// <param name="elapsed">批次的执行耗时。</param>
    public void RecordCompletedBatch(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _completedBatches);
        Interlocked.Add(ref _totalElapsedMilliseconds, (long)elapsed.TotalMilliseconds);
    }
}
