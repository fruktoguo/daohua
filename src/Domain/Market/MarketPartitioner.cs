using System.Buffers;
using System.Text;

namespace Domain.Market;

/// <summary>
/// 坊市交易标的分片计算器。
/// 采用 FNV-1a 64位哈希算法与零堆分配 UTF-8 Span 编码，将标的物名称一致性映射到指定的分布式/并发处理分片（Partition）。
/// </summary>
public static class MarketPartitioner
{
    /// <summary>分片算法标识</summary>
    public const string Algorithm = "fnv1a64-v1";

    /// <summary>
    /// 计算指定交易标的所归属的分片索引（0 到 partitionCount - 1）。
    /// 基于栈内存与 Span 编码计算 FNV-1a 64 位哈希，消除 string 和 byte[] 堆分配。
    /// </summary>
    /// <param name="instrument">交易标的</param>
    /// <param name="partitionCount">分片总数（1 到 4096）</param>
    public static int GetPartition(MarketInstrument instrument, int partitionCount)
    {
        if (partitionCount is < 1 or > 4096) throw new DomainException(DomainErrorCode.InvalidArgument, "市场分片数量无效。");

        var itemIdSpan = (instrument.ItemId.Value ?? string.Empty).AsSpan();
        var variantSpan = (instrument.Variant ?? string.Empty).AsSpan();

        var maxBytes = Encoding.UTF8.GetMaxByteCount(itemIdSpan.Length + (variantSpan.IsEmpty ? 0 : 1 + variantSpan.Length));
        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));

        try
        {
            var written = Encoding.UTF8.GetBytes(itemIdSpan, buffer);
            if (!variantSpan.IsEmpty)
            {
                buffer[written++] = (byte)':';
                written += Encoding.UTF8.GetBytes(variantSpan, buffer[written..]);
            }

            // FNV-1a 64-bit offset_basis 与 FNV_prime 常量
            ulong hash = 14695981039346656037UL;
            var bytes = buffer[..written];
            for (var i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return (int)(hash % (ulong)partitionCount);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}

