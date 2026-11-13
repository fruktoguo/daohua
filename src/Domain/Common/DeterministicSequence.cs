namespace Domain.Common;

/// <summary>
/// 确定性伪随机序列推进的唯一实现（xorshift64）：远征波次、遭遇抽怪与掉落判定
/// 共用同一条种子流递推规则，保证切片推进与快照恢复后的抽取序列位级一致。
/// </summary>
internal static class DeterministicSequence
{
    /// <summary>
    /// 64 位 FNV-1a 确定性文本哈希：替代进程级随机化的 string.GetHashCode / StringComparer.Ordinal，
    /// 保证主服与冷挂机计算节点等跨进程确定性模拟得到位级一致的伪随机种子。
    /// </summary>
    public static ulong HashText(string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    /// <summary>递推到序列的下一个种子状态。</summary>
    public static long NextSeed(long seed)
    {
        unchecked
        {
            var value = (ulong)seed;
            value ^= value << 13;
            value ^= value >> 7;
            value ^= value << 17;
            return (long)value;
        }
    }
}
