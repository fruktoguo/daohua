namespace Domain.World;

/// <summary>
/// 大地图移动速度纯领域规则（总纲 §3.1 / §8.3.1b）：
/// 每次中境界阶段晋升提升 +5%、每次大境界突破提升 +30%，采用线性加算，
/// 大境界入口仅计大境界突破、不重复叠加中境界（与根基上限及装备固定回血档位同口径）。
/// </summary>
public static class WorldTravelRules
{
    /// <summary>每次中境界阶段晋升的大地图移速增幅点数（5%）。</summary>
    public const int MidRealmSpeedBonusPoints = 5;

    /// <summary>每次大境界突破的大地图移速增幅点数（30%）。</summary>
    public const int MajorRealmSpeedBonusPoints = 30;

    /// <summary>大地图移速成长封顶境界等级（Lv.127 飞升）。</summary>
    public const int MaxRealmLevel = 127;

    /// <summary>
    /// 统计指定境界等级累计获得的中境界晋升次数与大境界突破次数：
    /// 凡俗段（Lv.1~18）：武(1~6) 0 次、玄(7~12) 1 次、天(13~18) 2 次，大境界 0 次；
    /// 修真段（Lv.19~127）：每 12 层一大境界，入口第 1 层（及 Lv.127 飞升）计 1 次大境界突破、不重复计中境界，
    /// 档内第 4/7/10 层（中/后/极）各计 1 次中境界晋升。
    /// </summary>
    /// <param name="realmLevel">角色当前境界等级（钳制至 [1, 127]）。</param>
    /// <returns>累计中境界晋升次数与大境界突破次数。</returns>
    public static (int MidBreaks, int MajorBreaks) CountRealmBreaks(int realmLevel)
    {
        var level = Math.Clamp(realmLevel, 1, MaxRealmLevel);
        if (level <= 18)
            return ((level - 1) / 6, 0);

        var layerIndex = level - 19;
        var midBreaks = 2 + (layerIndex / 12) * 3 + (layerIndex % 12) / 3;
        var majorBreaks = layerIndex / 12 + 1;
        return (midBreaks, majorBreaks);
    }

    /// <summary>计算指定境界等级的大地图移动速度增幅点数（整数百分比，如 40 表示 +40%）。</summary>
    /// <param name="realmLevel">角色当前境界等级。</param>
    /// <returns>整数百分比点数（0 ~ 445）。</returns>
    public static int SpeedBonusPoints(int realmLevel)
    {
        var (midBreaks, majorBreaks) = CountRealmBreaks(realmLevel);
        return midBreaks * MidRealmSpeedBonusPoints + majorBreaks * MajorRealmSpeedBonusPoints;
    }

    /// <summary>计算指定境界等级的大地图移动速度倍率（如 1.00 / 1.40 / 5.45）。</summary>
    /// <param name="realmLevel">角色当前境界等级。</param>
    /// <returns>大于等于 1.0 的精确移速倍率。</returns>
    public static double SpeedMultiplier(int realmLevel)
        => (100 + SpeedBonusPoints(realmLevel)) / 100d;
}
