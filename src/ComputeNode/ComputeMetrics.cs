namespace DaoHua.ComputeNode;

public sealed class ComputeMetrics
{
    private long _activeBatches;
    private long _completedBatches;
    private long _failedBatches;
    private long _cancelledBatches;
    private long _completedJobs;
    private long _failedJobs;
    private long _rejectedRequests;
    private long _unauthorizedRequests;
    private long _totalElapsedMilliseconds;

    public long ActiveBatches => Volatile.Read(ref _activeBatches);
    public long CompletedBatches => Volatile.Read(ref _completedBatches);
    public long FailedBatches => Volatile.Read(ref _failedBatches);
    public long CancelledBatches => Volatile.Read(ref _cancelledBatches);
    public long CompletedJobs => Volatile.Read(ref _completedJobs);
    public long FailedJobs => Volatile.Read(ref _failedJobs);
    public long RejectedRequests => Volatile.Read(ref _rejectedRequests);
    public long UnauthorizedRequests => Volatile.Read(ref _unauthorizedRequests);
    public long TotalElapsedMilliseconds => Volatile.Read(ref _totalElapsedMilliseconds);

    public void BeginBatch() => Interlocked.Increment(ref _activeBatches);
    public void EndBatch() => Interlocked.Decrement(ref _activeBatches);
    public void RecordCompletedJob() => Interlocked.Increment(ref _completedJobs);
    public void RecordFailedJob() => Interlocked.Increment(ref _failedJobs);
    public void RecordRejected() => Interlocked.Increment(ref _rejectedRequests);
    public void RecordUnauthorized() => Interlocked.Increment(ref _unauthorizedRequests);
    public void RecordFailedBatch() => Interlocked.Increment(ref _failedBatches);
    public void RecordCancelledBatch() => Interlocked.Increment(ref _cancelledBatches);

    public void RecordCompletedBatch(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _completedBatches);
        Interlocked.Add(ref _totalElapsedMilliseconds, (long)elapsed.TotalMilliseconds);
    }
}
