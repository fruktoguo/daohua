using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using YuoCore.Persistence;

namespace DaoHua.Server.Infrastructure;

/// <summary>
/// 市场主分片拓扑元数据 (DaoHuaMarketTopology)。
/// <para>
/// 负责记录当前服务实例所负责的主分片编号 (<see cref="PartitionId"/>) 与全局分片总数 (<see cref="PartitionCount"/>)，
/// 并基于确定性哈希算法判定交易品种 (<see cref="DaoHuaMarketInstrument"/>) 的归属。
/// </para>
/// </summary>
public sealed class DaoHuaMarketTopology
{
    public DaoHuaMarketTopology(int partitionId, int partitionCount)
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

    /// <summary>当前实例负责的分片索引（从 0 开始）。</summary>
    public int PartitionId { get; }

    /// <summary>全局市场主分片总数。</summary>
    public int PartitionCount { get; }

    /// <summary>分片路由算法名称。</summary>
    public string Algorithm => DaoHuaMarketPartitioner.Algorithm;

    /// <summary>
    /// 获取负责指定交易品种的分片编号。
    /// </summary>
    public int GetOwner(DaoHuaMarketInstrument instrument) =>
        DaoHuaMarketPartitioner.GetPartition(instrument, PartitionCount);

    /// <summary>
    /// 判断指定交易品种是否归属于当前主分片。
    /// </summary>
    public bool Owns(DaoHuaMarketInstrument instrument) =>
        GetOwner(instrument) == PartitionId;
}

/// <summary>
/// 市场账本底层存储契约 (IDaoHuaMarketLedgerStore)。
/// <para>
/// 职责：
/// 1. 提供分布式独占写者租约抢占 (<see cref="AcquireWriterAsync"/>) 与释放 (<see cref="ReleaseWriterAsync"/>)。
/// 2. 账本快照加载 (<see cref="LoadAsync"/>) 与基于 CAS 修订号 (<see cref="CommitAsync"/>) 的原子提交。
/// 3. 支持将市场账本更新与触发该变更的玩家快照放在同一数据库事务中原子写入。
/// </para>
/// </summary>
public interface IDaoHuaMarketLedgerStore
{
    /// <summary>底层存储适配器名称。</summary>
    string ProviderName { get; }

    /// <summary>是否支持多实例多主分片分布式部署。</summary>
    bool SupportsDistributedPartitions { get; }

    /// <summary>是否支持玩家快照与市场账本同事务原子持久化。</summary>
    bool PersistsPlayerSnapshotAtomically { get; }

    /// <summary>
    /// 获取指定主分片的独占写者租约/分布式咨询锁。
    /// </summary>
    ValueTask AcquireWriterAsync(
        DaoHuaMarketTopology topology,
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 加载指定分片的最新市场账本快照状态。
    /// </summary>
    ValueTask<DaoHuaMarketLedgerState?> LoadAsync(
        DaoHuaMarketTopology topology,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于版本号 CAS 乐观锁提交最新账本快照，支持附带玩家快照同事务原子写入。
    /// </summary>
    ValueTask CommitAsync(
        DaoHuaMarketTopology topology,
        long expectedRevision,
        DaoHuaMarketLedgerState ledger,
        YuoSnapshotRecord? playerSnapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放写者租约与锁。
    /// </summary>
    ValueTask ReleaseWriterAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 市场账本 CAS 乐观并发冲突异常（当期望提交的版本号与数据库当前版本不一致时抛出）。
/// </summary>
public sealed class DaoHuaMarketLedgerConcurrencyException : InvalidOperationException
{
    public DaoHuaMarketLedgerConcurrencyException(long expectedRevision, long actualRevision)
        : base($"市场账本版本冲突：期望 {expectedRevision}，实际 {actualRevision}。")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>提交时期望的旧版本号。</summary>
    public long ExpectedRevision { get; }

    /// <summary>数据库中已存在的实际版本号。</summary>
    public long ActualRevision { get; }
}
