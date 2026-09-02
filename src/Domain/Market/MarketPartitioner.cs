namespace Domain.Market;

/// <summary>
/// 坊市交易标的分片计算器。
/// 采用 FNV-1a 64位哈希算法，将标的物名称一致性映射到指定的分布式/并发处理分片（Partition）。
/// </summary>
public static class MarketPartitioner
{
    /// <summary>分片算法标识</summary>
    public const string Algorithm = "fnv1a64-v1";

    /// <summary>
    /// 计算指定交易标的所归属的分片索引（0 到 partitionCount - 1）。
    /// </summary>
    /// <param name="instrument">交易标的</param>
    /// <param name="partitionCount">分片总数（1 到 4096）</param>
    public static int GetPartition(MarketInstrument instrument, int partitionCount)
    {
        if (partitionCount is < 1 or > 4096) throw new DomainException(DomainErrorCode.InvalidArgument, "市场分片数量无效。");
        var bytes = System.Text.Encoding.UTF8.GetBytes(instrument.ToString());
        // FNV-1a 64-bit offset_basis 与 FNV_prime 常量
        ulong hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return (int)(hash % (ulong)partitionCount);
    }
}

