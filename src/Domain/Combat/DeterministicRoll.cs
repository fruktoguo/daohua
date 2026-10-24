namespace Domain.Combat;

/// <summary>
/// 战斗确定性随机函数：SplitMix64 混合固定计数器，各判定使用独立通道。
/// 切片与恢复不会改变随机流——同一 (seed, side, count, channel) 必得同一值。
/// </summary>
public static class DeterministicRoll
{
    /// <summary>
    /// 掷出 [0,1) 均匀随机数：以攻击计数与通道号混入种子，映射到 53 位尾数精度。
    /// 各判定（命中/暴击/掉落等）必须使用独立 channel，避免同帧判定间相互关联。
    /// </summary>
    /// <param name="seed">战斗种子（远征/遭遇创建时确定）。</param>
    /// <param name="side">阵营序号（攻守双方各占一个值），隔离双方随机流。</param>
    /// <param name="attackCount">攻方已完成的攻击计数，作为随机流游标。</param>
    /// <param name="channel">判定通道号；同一游标上不同判定取不同通道。</param>
    /// <returns>[0,1) 内的双精度均匀值；同参数重复调用恒返回同值。</returns>
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
