namespace Domain.Combat;

/// <summary>
/// 战斗确定性随机函数：SplitMix64 混合固定计数器，各判定使用独立通道。
/// 切片与恢复不会改变随机流——同一 (seed, side, count, channel) 必得同一值。
/// </summary>
public static class DeterministicRoll
{
    public static double Next(long seed, ulong side, double attackCount, ulong channel)
    {
        unchecked
        {
            var value = (ulong)seed ^ ((ulong)BitConverter.DoubleToInt64Bits(attackCount) * 0x9E3779B97F4A7C15UL)
                ^ ((side + 1) * 0xD1B54A32D192ED03UL) ^ ((channel + 1) * 0x94D049BB133111EBUL);
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 11) * (1d / 9007199254740992d);
        }
    }
}
