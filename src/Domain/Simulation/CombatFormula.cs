// ==============================================================================
// 文件名称: CombatFormula.cs
// 主要职责: 纯确定性、无状态的战斗数值计算公式集合。
// 核心逻辑 / 定义内容:
//   1. 抗性穿透、伤害吸收与减免等防御端公式计算。
//   2. 控制命中率、对抗判定概率（命中/闪避、暴击/免暴、破招/拆招）计算。
//   3. 伤害期望、治疗量、生命回复与技能等级数值加成计算。
// ==============================================================================

namespace Domain.Simulation;


/// <summary>
/// 战斗公式的无状态实现。公式与事件调度分离，便于数值版本化和边界回归测试。
/// </summary>
public static class CombatFormula
{
    /// <summary>
    /// 公开战斗模拟器的控制命中修正：韧性为百分比点数，而不是 0 到 1 的比例。
    /// 命中几率 = 基础概率 * 100 / (100 + 目标韧性)。
    /// </summary>
    /// <param name="baseChance">技能基础控制概率（0.0 ~ 1.0）。</param>
    /// <param name="tenacity">受击目标韧性点数。</param>
    /// <returns>修正后的实际控制触发概率。</returns>
    public static double CalculateControlChance(double baseChance, double tenacity)
    {
        var chance = Math.Clamp(double.IsFinite(baseChance) ? baseChance : 0, 0, 1);
        var normalizedTenacity = double.IsFinite(tenacity) ? Math.Max(0, tenacity) : 0;
        return chance * 100 / (100 + normalizedTenacity);
    }

    /// <summary>
    /// 计算数值在最大上限中的百分比比例（0.0 ~ 100.0）。
    /// </summary>
    /// <param name="value">当前数值。</param>
    /// <param name="maximum">最大上限。</param>
    /// <returns>百分比数值。</returns>
    public static double CalculatePercentage(long value, long maximum)
    {
        if (maximum <= 0) return 0;
        return Math.Clamp(value * 100d / maximum, 0, 100);
    }

    /// <summary>
    /// 计算经过防御穿透削减后的有效抗性/护甲。
    /// 有效抗性 = 原始抗性 / (1 + 穿透比例)。
    /// </summary>
    /// <param name="resistance">受击方原始抗性值。</param>
    /// <param name="penetrationRatio">攻击方穿透比例。</param>
    /// <returns>削减后的有效抗性。</returns>
    public static double CalculateEffectiveResistance(double resistance, double penetrationRatio)
    {
        return resistance >= 0
            ? resistance / (1 + Math.Max(0, penetrationRatio))
            : resistance;
    }

    /// <summary>
    /// 应用伤害吸收/减免比例，计算最终穿透吸收层后的实际伤害。
    /// </summary>
    /// <param name="damage">承受伤害。</param>
    /// <param name="damageAbsorptionRatio">伤害吸收比例（0.0 ~ 1.0）。</param>
    /// <returns>吸收后的剩余伤害。</returns>
    public static double ApplyDamageAbsorption(double damage, double damageAbsorptionRatio)
    {
        var absorption = double.IsFinite(damageAbsorptionRatio)
            ? Math.Clamp(damageAbsorptionRatio, 0, 1)
            : 0;
        return Math.Max(0, damage) * (1 - absorption);
    }

    /// <summary>
    /// 根据攻速加成比例计算普攻实际攻击间隔（毫秒）。
    /// 实际间隔 = 基础间隔 / (1 + 攻速比例)，保底 100 毫秒。
    /// </summary>
    /// <param name="baseIntervalMilliseconds">基础攻击间隔毫秒数。</param>
    /// <param name="attackSpeedRatio">攻击速度加成比例。</param>
    /// <returns>修正后的攻击间隔毫秒数。</returns>
    public static int CalculateAttackIntervalMilliseconds(
        int baseIntervalMilliseconds,
        double attackSpeedRatio)
    {
        return Math.Max(
            100,
            (int)Math.Round(
                Math.Max(100, baseIntervalMilliseconds)
                / (1 + Math.Max(-0.99, attackSpeedRatio))));
    }


    /// <summary>
    /// 计算固定比例回复的资源点数（如按最大上限百分比回复）。
    /// </summary>
    /// <param name="maxResource">资源最大上限。</param>
    /// <param name="ratio">回复比例。</param>
    /// <returns>回复点数。</returns>
    public static long CalculateRegeneration(long maxResource, double ratio)
    {
        if (maxResource < 0) throw new ArgumentOutOfRangeException(nameof(maxResource));
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio));
        }

        return Math.Max(0, (long)Math.Floor(maxResource * ratio));
    }

    /// <summary>
    /// 减伤计算公式：伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 抗性 * 抗性减伤系数 + 系数))。
    /// </summary>
    /// <param name="rawDamage">原始未减免伤害。</param>
    /// <param name="resistance">受击方对应抗性/护甲值。</param>
    /// <param name="penetrationRatio">攻击方穿透比例。</param>
    /// <param name="resistanceFactor">抗性放大系数。</param>
    /// <param name="baseFactor">防零平滑常数。</param>
    /// <returns>减免后造成的实际伤害（保底 1 点）。</returns>
    public static long CalculateMitigatedDamageWithResistanceFactor(
        long rawDamage,
        double resistance,
        double penetrationRatio = 0,
        double resistanceFactor = CombatConstants.DefaultResistanceFactor,
        double baseFactor = CombatConstants.DefaultDamageBaseFactor)
    {
        if (rawDamage <= 0) return 0;
        var effectiveResistance = CalculateEffectiveResistance(resistance, penetrationRatio);
        if (effectiveResistance <= 0) return rawDamage;

        var denominator = rawDamage + effectiveResistance * resistanceFactor + baseFactor;
        if (denominator <= 0) return rawDamage;
        var multiplier = rawDamage / denominator;
        return Math.Max(1, (long)Math.Ceiling(rawDamage * multiplier));
    }

    /// <summary>
    /// 对抗概率计算公式：自身属性 / (自身属性 + 对方对抗属性 * 系数 + 基础平滑常数)。
    /// 适用于：闪避 vs 命中、暴击 vs 免爆、破招 vs 拆招。
    /// </summary>
    /// <param name="selfAttr">己方对抗属性值。</param>
    /// <param name="opponentAttr">对方对抗属性值。</param>
    /// <param name="factor">对方属性权重系数。</param>
    /// <param name="baseConst">基础平滑常数。</param>
    /// <returns>己方对抗胜出概率（0.0 ~ 1.0）。</returns>
    public static double CalculateContestRate(
        double selfAttr,
        double opponentAttr,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        var selfVal = Math.Max(0, selfAttr);
        var opponentVal = Math.Max(0, opponentAttr);
        var denom = selfVal + opponentVal * factor + baseConst;
        if (denom <= 0) return 0;
        return Math.Clamp(selfVal / denom, 0, 1);
    }

    /// <summary>
    /// 闪避率计算 (闪避 vs 命中)。
    /// 闪避率 = 守方闪避 / (守方闪避 + 攻方命中 * 系数 + 基础平滑常数)。
    /// 闪避成功则免除本次全部伤害。0闪避0命中时闪避率为0% (即100%命中)。
    /// </summary>
    /// <param name="evasion">守方身法闪避值。</param>
    /// <param name="hit">攻方神识精准命中值。</param>
    /// <param name="factor">对抗放大系数。</param>
    /// <param name="baseConst">基础平滑常数。</param>
    /// <returns>本次攻击被闪避的概率。</returns>
    public static double CalculateDodgeContestRate(
        double evasion,
        double hit,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        return CalculateContestRate(evasion, hit, factor, baseConst);
    }

    /// <summary>
    /// 命中率计算 (命中 vs 闪避)。
    /// 命中率 = 1.0 - 闪避率 = (攻方命中 * 系数 + 基础平滑常数) / (守方闪避 + 攻方命中 * 系数 + 基础平滑常数)。
    /// </summary>
    /// <param name="hit">攻方命中值。</param>
    /// <param name="evasion">守方闪避值。</param>
    /// <param name="factor">对抗放大系数。</param>
    /// <param name="baseConst">基础平滑常数。</param>
    /// <returns>攻击命中的概率。</returns>
    public static double CalculateHitContestRate(
        double hit,
        double evasion,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        return 1.0 - CalculateDodgeContestRate(evasion, hit, factor, baseConst);
    }

    /// <summary>
    /// 暴击率计算 (暴击 vs 免爆)。
    /// 暴击率 = 攻方暴击 / (攻方暴击 + 守方免爆 * 系数 + 基础平滑常数)。
    /// </summary>
    /// <param name="crit">攻方暴击值。</param>
    /// <param name="critAvoid">守方抗暴免爆值。</param>
    /// <param name="factor">对抗放大系数。</param>
    /// <param name="baseConst">基础平滑常数。</param>
    /// <returns>触发暴击的概率。</returns>
    public static double CalculateCritContestRate(
        double crit,
        double critAvoid,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        return CalculateContestRate(crit, critAvoid, factor, baseConst);
    }

    /// <summary>
    /// 破招判定：当攻击者破招 > 被攻击者拆招时计算破招概率。
    /// 破招成功：命中与暴击效果翻倍 (× BreakSuccessEffectMultiplier)。
    /// </summary>
    /// <param name="attackerBreak">攻击方破招值。</param>
    /// <param name="defenderParry">防御方拆招值。</param>
    /// <param name="rollRatio">随机掷骰比率（0.0 ~ 1.0）。</param>
    /// <param name="factor">对抗系数。</param>
    /// <param name="baseConst">平滑常数。</param>
    /// <returns>是否判定破招成功。</returns>
    public static bool CheckBreakSuccess(
        double attackerBreak,
        double defenderParry,
        double rollRatio,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        if (attackerBreak <= defenderParry) return false;
        var rate = CalculateContestRate(attackerBreak, defenderParry, factor, baseConst);
        return rollRatio < rate;
    }

    /// <summary>
    /// 拆招判定：当被攻击者拆招 > 攻击者破招时计算拆招概率。
    /// 拆招成功：双抗与闪避计算效果翻倍 (× ParrySuccessEffectMultiplier)。
    /// </summary>
    /// <param name="defenderParry">防御方拆招值。</param>
    /// <param name="attackerBreak">攻击方破招值。</param>
    /// <param name="rollRatio">随机掷骰比率（0.0 ~ 1.0）。</param>
    /// <param name="factor">对抗系数。</param>
    /// <param name="baseConst">平滑常数。</param>
    /// <returns>是否判定拆招成功。</returns>
    public static bool CheckParrySuccess(
        double defenderParry,
        double attackerBreak,
        double rollRatio,
        double factor = CombatConstants.DefaultContestOpponentFactor,
        double baseConst = CombatConstants.DefaultContestBaseConst)
    {
        if (defenderParry <= attackerBreak) return false;
        var rate = CalculateContestRate(defenderParry, attackerBreak, factor, baseConst);
        return rollRatio < rate;
    }

    /// <summary>
    /// 计算击败单个真实战斗单位所获得的战斗经验。
    /// 基础经验：1.0 点
    /// 对方每超过自身 1 级：指数额外获得 10% (1.0 * 1.10^diff)
    /// 对方每低于自身 1 级：指数减少 20% (1.0 * 0.80^diff)
    /// </summary>
    /// <param name="playerLevel">玩家当前境界等级。</param>
    /// <param name="targetLevel">目标敌怪境界等级。</param>
    /// <returns>本次结算获得的战斗经验值。</returns>
    public static double CalculateCombatExperience(double playerLevel, double targetLevel)
    {
        var levelDiff = targetLevel - playerLevel;
        if (levelDiff > 0)
        {
            return Math.Max(0.01, 1.0 * Math.Pow(1.10, levelDiff));
        }
        if (levelDiff < 0)
        {
            return Math.Max(0.01, 1.0 * Math.Pow(0.80, -levelDiff));
        }
        return 1.0;
    }

    /// <summary>
    /// 角色在当前境界等级下的全量基础战斗属性池快照。
    /// </summary>
    public readonly record struct BaseAttributePool(
        double Level,
        int Tier,
        double BaseHitpoints,
        double BaseManapoints,
        double BasePhysicalAttack,
        double BaseSpellAttack,
        double BaseArmor,
        double BaseMagicResistance,
        double BaseAccuracy,
        double BaseEvasion,
        double BaseCritical,
        double BaseCriticalAvoidance,
        double BaseBreak,
        double BaseParry,
        double BaseHpRegen,
        double BaseMpRegen);

    /// <summary>
    /// 根据玩家当前持有的灵气修为总点数，通过境界基准锚点线性插值计算出基础属性池。
    /// </summary>
    /// <param name="spirit">累计修为点数。</param>
    /// <returns>基础战斗属性池快照。</returns>
    public static BaseAttributePool CalculateBaseAttributes(long spirit)
    {
        var progress = CombatConstants.CalculateRealmProgress(spirit);
        var level = Math.Max(1.0, progress.TotalLevel);
        var tier = CalculateTier(level);

        var anchors = CombatConstants.BaseFlatAnchors;

        // 在 12 阶基准锚点中进行分段线性平滑插值
        var lowIndex = 0;
        for (var i = 0; i < anchors.Length - 1; i++)
        {
            if (level >= anchors[i].Level && level <= anchors[i + 1].Level)
            {
                lowIndex = i;
                break;
            }
            if (level > anchors[i + 1].Level)
            {
                lowIndex = i + 1;
            }
        }

        var low = anchors[lowIndex];
        var high = lowIndex < anchors.Length - 1 ? anchors[lowIndex + 1] : low;
        var span = Math.Max(1e-6, high.Level - low.Level);
        var t = lowIndex < anchors.Length - 1 ? Math.Clamp((level - low.Level) / span, 0.0, 1.0) : 0.0;

        var baseHp = low.Hp + (high.Hp - low.Hp) * t;
        var baseMp = low.Mp + (high.Mp - low.Mp) * t;
        var baseAd = low.Ad + (high.Ad - low.Ad) * t;
        var baseAp = low.Ap + (high.Ap - low.Ap) * t;
        var baseArmor = low.Armor + (high.Armor - low.Armor) * t;
        var baseMagicRes = low.Mr + (high.Mr - low.Mr) * t;
        var baseAccuracy = low.Acc + (high.Acc - low.Acc) * t;
        var baseEvasion = low.Eva + (high.Eva - low.Eva) * t;
        var baseCrit = low.Crit + (high.Crit - low.Crit) * t;
        var baseCritAvoid = low.CAvoid + (high.CAvoid - low.CAvoid) * t;
        var baseBreak = low.Break + (high.Break - low.Break) * t;
        var baseParry = low.Parry + (high.Parry - low.Parry) * t;

        // 基础生命回复与灵力回复固定为每 5 秒 2% 基础上限，四舍五入取整（保底 1.0）
        var baseHpRegen = Math.Max(1.0, Math.Round(baseHp * 0.02));
        var baseMpRegen = Math.Max(1.0, Math.Round(baseMp * 0.02));

        return new BaseAttributePool(
            level,
            tier,
            baseHp,
            baseMp,
            baseAd,
            baseAp,
            baseArmor,
            baseMagicRes,
            baseAccuracy,
            baseEvasion,
            baseCrit,
            baseCritAvoid,
            baseBreak,
            baseParry,
            baseHpRegen,
            baseMpRegen);
    }

    /// <summary>
    /// 根据境界等级计算对应的大境界/品阶层次（1 ~ 12 阶）。
    /// </summary>
    /// <param name="level">境界等级。</param>
    /// <returns>品阶编号。</returns>
    public static int CalculateTier(double level)
    {
        if (level <= 18.0) return level <= 1.0 ? 1 : 2;
        if (level <= 30.0) return 3;
        if (level <= 42.0) return 4;
        if (level <= 54.0) return 5;
        if (level <= 66.0) return 6;
        if (level <= 78.0) return 7;
        if (level <= 90.0) return 8;
        if (level <= 102.0) return 9;
        if (level <= 114.0) return 10;
        if (level <= 126.0) return 11;
        return 12;
    }
}

