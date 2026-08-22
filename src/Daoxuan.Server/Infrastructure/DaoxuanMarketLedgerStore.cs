using Daoxuan.Domain.Market;
using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

public sealed class DaoxuanMarketTopology
{
    public DaoxuanMarketTopology(int partitionId, int partitionCount)
    {
        if (partitionCount is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount),
                "市场主分片数量必须在 1 到 4096 之间。");
        }

        if (partitionId < 0 || partitionId >= partitionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionId),
                "市场主分片编号必须落在分片数量范围内。");
        }

        PartitionId = partitionId;
        PartitionCount = partitionCount;
    }

    public int PartitionId { get; }
    public int PartitionCount { get; }
    public string Algorithm => DaoxuanMarketPartitioner.Algorithm;

    public int GetOwner(DaoxuanMarketInstrument instrument) =>
        DaoxuanMarketPartitioner.GetPartition(instrument, PartitionCount);

    public bool Owns(DaoxuanMarketInstrument instrument) =>
        GetOwner(instrument) == PartitionId;
}

/// <summary>
/// PostgreSQL 市场账本持久化边界。账本 CAS、玩家快照和 Outbox 必须进入同一事务。
/// </summary>
public interface IDaoxuanMarketLedgerStore
{
    string ProviderName { get; }
    bool SupportsDistributedPartitions { get; }
    bool PersistsPlayerSnapshotAtomically { get; }

    ValueTask AcquireWriterAsync(
        DaoxuanMarketTopology topology,
        string instanceId,
        CancellationToken cancellationToken = default);

    ValueTask<DaoxuanMarketLedgerState?> LoadAsync(
        DaoxuanMarketTopology topology,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        DaoxuanMarketTopology topology,
        long expectedRevision,
        DaoxuanMarketLedgerState ledger,
        YuoSnapshotRecord? playerSnapshot,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseWriterAsync(CancellationToken cancellationToken = default);
}

public sealed class DaoxuanMarketLedgerConcurrencyException : InvalidOperationException
{
    public DaoxuanMarketLedgerConcurrencyException(long expectedRevision, long actualRevision)
        : base($"市场账本版本冲突：期望 {expectedRevision}，实际 {actualRevision}。")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}
