// ==============================================================================
// 文件名称: DaoHuaMarketPartitioner.cs
// 主要职责: 提供基于 FNV-1a 哈希的跨进程确定性市场交易品种分片与分区计算策略。
// 核心逻辑 / 定义内容:
//   1. 实现 64 位 FNV-1a 哈希算法，对物品 ID 及强化等级进行确定性哈希计算。
//   2. 计算交易品种（DaoHuaMarketInstrument）对应的分片分区序号（GetPartition）。
//   3. 校验交易品种是否归属于指定分片分区（BelongsToPartition）。
// ==============================================================================

using DaoHua.Domain.Models;


namespace DaoHua.Domain.Market;

/// <summary>
/// 为市场品种计算跨进程稳定的分片编号（采用 FNV-1a 64位哈希算法）。
/// 不能使用 string.GetHashCode；其随机种子会使同一品种在重启后落入不同分片。
/// </summary>
public static class DaoHuaMarketPartitioner
{
    /// <summary>分片哈希算法标识版本。</summary>
    public const string Algorithm = "fnv1a64-utf16-v1";

    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>
    /// 根据交易品种计算其对应的确定性分片分区序号（0 ~ partitionCount - 1）。
    /// </summary>
    /// <param name="instrument">交易品种。</param>
    /// <param name="partitionCount">分片分区总数。</param>
    /// <returns>分片索引。</returns>
    public static int GetPartition(
        DaoHuaMarketInstrument instrument,
        int partitionCount)
    {
        if (!instrument.IsValid)
        {
            throw new ArgumentException("市场品种无效。", nameof(instrument));
        }

        if (partitionCount is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount),
                "市场分片数量必须在 1 到 4096 之间。");
        }

        var hash = OffsetBasis;
        foreach (var character in instrument.ItemId)
        {
            hash = Mix(hash, (byte)character);
            hash = Mix(hash, (byte)(character >> 8));
        }

        // 分隔物品 ID 和强化等级，避免未来扩展数字 ID 时产生拼接歧义。
        hash = Mix(hash, 0xff);
        var enhancementLevel = (uint)instrument.EnhancementLevel;
        hash = Mix(hash, (byte)enhancementLevel);
        hash = Mix(hash, (byte)(enhancementLevel >> 8));
        hash = Mix(hash, (byte)(enhancementLevel >> 16));
        hash = Mix(hash, (byte)(enhancementLevel >> 24));

        return (int)(hash % (uint)partitionCount);
    }

    /// <summary>
    /// 校验指定交易品种是否归属于特定的分区分片。
    /// </summary>
    /// <param name="instrument">交易品种。</param>
    /// <param name="partitionId">目标分片 ID。</param>
    /// <param name="partitionCount">分片总数。</param>
    /// <returns>属于返回 true，否则返回 false。</returns>
    public static bool IsOwnedBy(
        DaoHuaMarketInstrument instrument,
        int partitionId,
        int partitionCount)
    {
        if (partitionId < 0 || partitionId >= partitionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionId));
        }

        return GetPartition(instrument, partitionCount) == partitionId;
    }

    private static ulong Mix(ulong hash, byte value) => (hash ^ value) * Prime;
}

