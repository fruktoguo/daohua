namespace Domain.Combat;

/// <summary>
/// 秘境副本专用体力快照（计算派生结果，不直接落盘）。
/// </summary>
/// <param name="Current">当前有效体力整数值 [0, MaxEnergy]。</param>
/// <param name="Max">体力上限（1680）。</param>
/// <param name="NextRecoverySeconds">距下一点体力恢复剩余秒数；满体时为 0。</param>
/// <param name="FullRecoverySeconds">距完全回满至 1680 剩余秒数；满体时为 0。</param>
/// <param name="EffectiveAnchorValue">规范化后的有效锚点体力值。</param>
/// <param name="EffectiveAnchorUpdatedAtUnixSeconds">规范化后的有效锚点时刻（Unix 秒）。</param>
public readonly record struct DungeonEnergySnapshot(
    int Current,
    int Max,
    double NextRecoverySeconds,
    double FullRecoverySeconds,
    int EffectiveAnchorValue,
    double EffectiveAnchorUpdatedAtUnixSeconds);

/// <summary>
/// 秘境副本周清体力（Tili / DungeonEnergy）确定性懒恢复与扣费规则（总纲 §30.2）。
/// 严禁复用已被六维「体魄」占用的 Stamina 标识符。
/// 规则：上限 240 × 7 = 1680，每 360 秒（10 点/小时）恢复 1 点，整点推进守恒不吞余数秒；
/// 新角色及旧存档首次激活默认赋予首日额度 240 点；进入秘境开局实扣、团灭或撤退不返还。
/// </summary>
public static class DungeonEnergyRules
{
    /// <summary>周清体力上限（240/日 × 7 日 = 1680 点）。</summary>
    public const int MaxEnergy = 1680;

    /// <summary>新角色创角与旧存档首次初始化赋予的首日初始体力。</summary>
    public const int InitialEnergy = 240;

    /// <summary>恢复 1 点体力所需秒数（10 点/小时 = 360 秒/点）。</summary>
    public const double RecoveryIntervalSeconds = 360.0;

    /// <summary>
    /// 按给定墙钟时间戳确定性推导当前有效体力及恢复倒计时。
    /// 若锚点时刻 &lt;= 0（旧存档缺省解码未初始化），视为首次激活并赋予 <see cref="InitialEnergy"/>。
    /// </summary>
    public static DungeonEnergySnapshot Compute(int anchorValue, double anchorUpdatedAtUnixSeconds, double nowUnixSeconds)
    {
        var safeNow = double.IsFinite(nowUnixSeconds) && nowUnixSeconds > 0 ? nowUnixSeconds : 0;
        if (!double.IsFinite(anchorUpdatedAtUnixSeconds) || anchorUpdatedAtUnixSeconds <= 0)
        {
            var fullSec = (MaxEnergy - InitialEnergy) * RecoveryIntervalSeconds;
            return new DungeonEnergySnapshot(
                InitialEnergy,
                MaxEnergy,
                RecoveryIntervalSeconds,
                fullSec,
                InitialEnergy,
                safeNow);
        }

        var clampedAnchor = Math.Clamp(anchorValue, 0, MaxEnergy);
        if (clampedAnchor >= MaxEnergy)
        {
            return new DungeonEnergySnapshot(MaxEnergy, MaxEnergy, 0, 0, MaxEnergy, safeNow);
        }

        var elapsed = Math.Max(0, safeNow - anchorUpdatedAtUnixSeconds);
        var recoveredTicks = (long)Math.Floor(elapsed / RecoveryIntervalSeconds);
        if (recoveredTicks <= 0)
        {
            var nextSec = Math.Max(0, RecoveryIntervalSeconds - elapsed);
            var missing = MaxEnergy - clampedAnchor;
            var fullSec = nextSec + Math.Max(0, missing - 1) * RecoveryIntervalSeconds;
            return new DungeonEnergySnapshot(
                clampedAnchor,
                MaxEnergy,
                nextSec,
                fullSec,
                clampedAnchor,
                anchorUpdatedAtUnixSeconds);
        }

        var current = (int)Math.Min(MaxEnergy, clampedAnchor + recoveredTicks);
        if (current >= MaxEnergy)
        {
            return new DungeonEnergySnapshot(MaxEnergy, MaxEnergy, 0, 0, MaxEnergy, safeNow);
        }

        var advancedAnchor = anchorUpdatedAtUnixSeconds + recoveredTicks * RecoveryIntervalSeconds;
        var remainder = Math.Max(0, safeNow - advancedAnchor);
        var nextRecovery = Math.Max(0, RecoveryIntervalSeconds - remainder);
        var remainingPoints = MaxEnergy - current;
        var fullRecovery = nextRecovery + Math.Max(0, remainingPoints - 1) * RecoveryIntervalSeconds;
        return new DungeonEnergySnapshot(
            current,
            MaxEnergy,
            nextRecovery,
            fullRecovery,
            current,
            advancedAnchor);
    }

    /// <summary>
    /// 尝试扣除指定点数的秘境体力，采用整点推进守恒（不吞不足 360 秒的余数进度）：
    /// - 若消耗前未达上限，新锚点按整点步进推进，保留余数进度；
    /// - 若消耗前已达上限 1680，自跌破上限的本次消耗时刻重新计时。
    /// 体力不足时返回 false 且保持原锚点不变。
    /// </summary>
    public static bool TryConsume(
        int anchorValue,
        double anchorUpdatedAtUnixSeconds,
        int cost,
        double nowUnixSeconds,
        out int nextAnchorValue,
        out double nextAnchorUpdatedAtUnixSeconds)
    {
        if (cost < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "体力消耗不能为负数。");

        var snapshot = Compute(anchorValue, anchorUpdatedAtUnixSeconds, nowUnixSeconds);
        if (snapshot.Current < cost)
        {
            nextAnchorValue = snapshot.EffectiveAnchorValue;
            nextAnchorUpdatedAtUnixSeconds = snapshot.EffectiveAnchorUpdatedAtUnixSeconds;
            return false;
        }

        if (cost == 0)
        {
            nextAnchorValue = snapshot.EffectiveAnchorValue;
            nextAnchorUpdatedAtUnixSeconds = snapshot.EffectiveAnchorUpdatedAtUnixSeconds;
            return true;
        }

        var safeNow = double.IsFinite(nowUnixSeconds) && nowUnixSeconds > 0 ? nowUnixSeconds : 0;
        nextAnchorValue = snapshot.Current - cost;
        // 满体态首次跌破上限时从消耗瞬间起算；未满态保留整点推进后的余数进度。
        nextAnchorUpdatedAtUnixSeconds = snapshot.Current >= MaxEnergy
            ? safeNow
            : snapshot.EffectiveAnchorUpdatedAtUnixSeconds;
        return true;
    }
}
