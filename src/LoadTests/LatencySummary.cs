namespace DaoHua.LoadTests;

/// <summary>
/// 延迟分位数统计辅助。
/// 压测场景共用：对采样列表计算 p50/p95/p99/max，采用最近秩（nearest-rank）口径。
/// </summary>
internal static class LatencySummary
{
    /// <summary>
    /// 计算指定分位数（0-100）；样本为空时返回 0。
    /// </summary>
    /// <param name="sortedSamples">已升序排序的采样列表。</param>
    /// <param name="percentile">分位数（0-100）。</param>
    /// <returns>分位数对应的采样值。</returns>
    public static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedSamples.Count);
        return sortedSamples[Math.Clamp(rank - 1, 0, sortedSamples.Count - 1)];
    }

    /// <summary>
    /// 对原始采样排序并输出结构化分位数摘要（用于报告 metrics）。
    /// </summary>
    /// <param name="samples">未排序的采样集合。</param>
    /// <returns>包含 count/p50/p95/p99/max 的匿名摘要对象。</returns>
    public static object Describe(IEnumerable<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        return new
        {
            count = sorted.Length,
            p50 = Math.Round(Percentile(sorted, 50), 2),
            p95 = Math.Round(Percentile(sorted, 95), 2),
            p99 = Math.Round(Percentile(sorted, 99), 2),
            max = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 2)
        };
    }

    /// <summary>
    /// 对原始采样排序并返回 p99 值；样本为空时返回 0。
    /// </summary>
    /// <param name="samples">未排序的采样集合。</param>
    /// <returns>p99 采样值。</returns>
    public static double P99(IEnumerable<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        return Percentile(sorted, 99);
    }
}
