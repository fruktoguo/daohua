namespace Xiuxian.Domain.Market;

/// <summary>
/// 为市场品种计算跨进程稳定的分片编号。
/// 不能使用 string.GetHashCode；其随机种子会使同一品种在重启后落入不同分片。
/// </summary>
public static class XiuxianMarketPartitioner
{
    public const string Algorithm = "fnv1a64-utf16-v1";

    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static int GetPartition(
        XiuxianMarketInstrument instrument,
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

    public static bool IsOwnedBy(
        XiuxianMarketInstrument instrument,
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
