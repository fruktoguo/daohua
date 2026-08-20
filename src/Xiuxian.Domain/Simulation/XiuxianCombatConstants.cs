namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 修仙战斗核心常量配置与公式说明规范。
/// 包含抗性减伤系数、功法法力消耗倍率、攻防对抗系数、六维根基增益系数及暴击倍率等全局常量。
/// </summary>
public static class XiuxianCombatConstants
{
    // =========================================================================
    // 1. 伤害与抗性减伤公式相关常量
    // =========================================================================

    /// <summary>
    /// 抗性减伤系数 (常量配置，暂定为 5.0)。
    /// 
    /// 【中文公式说明】：
    /// 实际伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 有效抗性 * 抗性减伤系数 + 基础常数))
    /// 
    /// 说明：
    /// 1. 当有效抗性为 0 时，减伤比例为 0，造成 100% 原始伤害；
    /// 2. 当有效抗性增加时，分母增大，伤害呈平滑双曲线衰减，抗性收益平滑且递减；
    /// 3. 有效抗性 = 基础抗性 / (1 + 穿透比例)。
    /// </summary>
    public const double DefaultResistanceFactor = 5.0;

    /// <summary>
    /// 减伤公式底数常数 (默认 1.0)，防止分母为 0 并平滑初始极小数值。
    /// </summary>
    public const double DefaultDamageBaseFactor = 1.0;


    // =========================================================================
    // 2. 技能灵力消耗计算常量
    // =========================================================================

    /// <summary>
    /// 功法技能灵力消耗换算倍率 (暂定为 10)。
    /// 
    /// 【中文公式说明】：
    /// 实际灵力消耗 = 基础消耗 * 消耗换算倍率
    /// 
    /// 示例：
    /// 基础消耗 2 -> 实际消耗 20 灵力
    /// 基础消耗 5 -> 实际消耗 50 灵力
    /// </summary>
    public const int DefaultManaCostMultiplier = 10;


    // =========================================================================
    // 3. 对抗概率计算公式相关常量 (命中vs闪避, 暴击vs免爆, 破招vs拆招)
    // =========================================================================

    /// <summary>
    /// 对抗公式对方属性放大系数 (默认 1.0)。
    /// </summary>
    public const double DefaultContestOpponentFactor = 1.0;

    /// <summary>
    /// 对抗公式基础平滑常数 (默认 1.0)。
    /// 
    /// 【中文公式说明】：
    /// 对抗成功概率 = 自身属性 / (自身属性 + 对方对抗属性 * 对抗放大系数 + 基础平滑常数)
    /// </summary>
    public const double DefaultContestBaseConst = 1.0;


    // =========================================================================
    // 4. 破招与拆招 (化解) 翻倍判定规则
    // =========================================================================

    /// <summary>
    /// 破招成功后的属性放大倍率 (翻倍 = 2.0)。
    /// 
    /// 【中文规则说明】：
    /// 当 攻击者破招 > 被攻击者拆招 时，按对抗概率计算破招是否成功。
    /// 若破招成功：本次攻击的【命中值】与【暴击值】实际计算效果翻倍 (× 2.0)。
    /// </summary>
    public const double BreakSuccessEffectMultiplier = 2.0;

    /// <summary>
    /// 拆招成功后的属性放大倍率 (翻倍 = 2.0)。
    /// 
    /// 【中文规则说明】：
    /// 当 被攻击者拆招 > 攻击者破招 时，按对抗概率计算拆招是否成功。
    /// 若拆招成功：本次承伤的【双抗(护甲/法抗)】与【闪避值】实际计算效果翻倍 (× 2.0)。
    /// </summary>
    public const double ParrySuccessEffectMultiplier = 2.0;


    // =========================================================================
    // 5. 暴击伤害基础倍率
    // =========================================================================

    /// <summary>
    /// 基础暴击伤害倍率 (150% = 1.5)。
    /// 
    /// 【中文公式说明】：
    /// 最终暴击伤害 = 未暴击伤害 * (基础暴击倍率 + 额外暴击伤害加成比例)
    /// </summary>
    public const double BaseCriticalDamageMultiplier = 1.5;


    // =========================================================================
    // 6. 六维根基换算系数 (百分比增益)
    // =========================================================================

    /// <summary>
    /// 体魄：每点提供 1% (0.01) 生命值、1% (0.01) 护甲、1% (0.01) 生命值回复。
    /// </summary>
    public const double StaminaHpRatioPerLevel = 0.01;
    public const double StaminaArmorRatioPerLevel = 0.01;
    public const double StaminaHpRegenRatioPerLevel = 0.01;

    /// <summary>
    /// 经脉：每点提供 1% (0.01) 灵力值、1% (0.01) 法抗、1% (0.01) 灵力值回复。
    /// </summary>
    public const double MeridiansMpRatioPerLevel = 0.01;
    public const double MeridiansMagicResRatioPerLevel = 0.01;
    public const double MeridiansMpRegenRatioPerLevel = 0.01;

    /// <summary>
    /// 根骨：每点提供 1% (0.01) 生命值、1% (0.01) 灵力值、1% (0.01) 化解(拆招)。
    /// </summary>
    public const double BoneHpRatioPerLevel = 0.01;
    public const double BoneMpRatioPerLevel = 0.01;
    public const double BoneParryRatioPerLevel = 0.01;

    /// <summary>
    /// 力道：每点提供 1% (0.01) 物理攻击、1% (0.01) 暴击、1% (0.01) 破招。
    /// </summary>
    public const double StrengthPhysAtkRatioPerLevel = 0.01;
    public const double StrengthCritRatioPerLevel = 0.01;
    public const double StrengthBreakRatioPerLevel = 0.01;

    /// <summary>
    /// 神识：每点提供 2% (0.02) 术法攻击 (术法攻击×2)、1% (0.01) 命中。
    /// </summary>
    public const double ConsciousnessSpellAtkRatioPerLevel = 0.02;
    public const double ConsciousnessHitRatioPerLevel = 0.01;

    /// <summary>
    /// 身法：每点提供 1% (0.01) 闪避、1% (0.01) 免爆、1% (0.01) 化解(拆招)。
    /// </summary>
    public const double AgilityEvasionRatioPerLevel = 0.01;
    public const double AgilityCritAvoidRatioPerLevel = 0.01;
    public const double AgilityParryRatioPerLevel = 0.01;

    /// <summary>
    /// 默认初始六维点数（全 10 点/重）。
    /// </summary>
    public const double DefaultRootAttributePoints = 10.0;

    // =========================================================================
    // 7. 普通攻击 (Basic Attack) 常量规则
    // =========================================================================

    /// <summary>
    /// 普通攻击物理伤害加成比例 (100% 物理攻击 = 1.0)。
    /// </summary>
    public const double BasicAttackDamageRatio = 1.0;

    /// <summary>
    /// 普通攻击冷却时间毫秒数 (0s = 0.0ms)。
    /// </summary>
    public const double BasicAttackCooldownMilliseconds = 0.0;

    /// <summary>
    /// 普通攻击引导施法时间毫秒数 (1.0s = 1000.0ms)。
    /// </summary>
    public const double BasicAttackCastMilliseconds = 1000.0;

    /// <summary>
    /// 普通攻击基础灵力消耗 (0 消耗)。
    /// </summary>
    public const double BasicAttackManaCost = 0.0;

    /// <summary>
    /// 默认行动速度点数 (0 点，增减 0%)。
    /// </summary>
    public const double DefaultActionSpeedPoints = 0.0;

    // =========================================================================
    // 8. 统一基准数值规范 (Baseline Attributes, 全部采用 double)
    // =========================================================================

    /// <summary>
    /// 默认角色与全量单位的统一基准战斗数值。
    /// 所有非玩家单位均以该基准数值为底座，进行境界缩放与百分比调整。
    /// </summary>
    public const double BaselineHitpoints = 100.0;
    public const double BaselineManapoints = 50.0;
    public const double BaselinePhysicalAttack = 10.0;
    public const double BaselineSpellAttack = 5.0;
    public const double BaselinePhysicalArmor = 10.0;
    public const double BaselineMagicResistance = 0.0;

    /// <summary>
    /// 生命值回复基准值 (固定值，每 5 秒回复 2.0 生命值)。
    /// </summary>
    public const double BaselineHpRegenPerFiveSeconds = 2.0;

    /// <summary>
    /// 灵力回复基准值 (固定值，每 5 秒回复 1.0 灵力值)。
    /// </summary>
    public const double BaselineMpRegenPerFiveSeconds = 1.0;

    /// <summary>
    /// 其他基础战斗属性基准值 (命中, 闪避, 暴击, 免爆, 破招, 拆招 均为 1.0)。
    /// </summary>
    public const double BaselineAccuracy = 1.0;
    public const double BaselineEvasion = 1.0;
    public const double BaselineCriticalRating = 1.0;
    public const double BaselineCritAvoidance = 1.0;
    public const double BaselineBreakPower = 1.0;
    public const double BaselineParryResolve = 1.0;

    /// <summary>
    /// 所有百分比属性基准点数 (均为 0.0 点)。
    /// </summary>
    public const double BaselinePercentPoints = 0.0;

    /// <summary>
    /// 六维累计经验到等级换算方法（经验累加不清空）。
    /// 每级累计所需经验 = 100 * (level ^ 3)。
    /// </summary>
    public static (double Level, double CurrentProgress, double LevelRequirement) CalculateRootLevelAndProgress(double totalCumulativeExp)
    {
        var exp = Math.Max(0.0, totalCumulativeExp);
        // Level 1 基础
        var level = 1.0;
        while (GetCumulativeExpForLevel(level + 1.0) <= exp)
        {
            level += 1.0;
        }

        var currentLevelBaseExp = GetCumulativeExpForLevel(level);
        var nextLevelBaseExp = GetCumulativeExpForLevel(level + 1.0);
        var currentProgress = exp - currentLevelBaseExp;
        var levelRequirement = nextLevelBaseExp - currentLevelBaseExp;

        return (level, currentProgress, levelRequirement);
    }

    public static double GetCumulativeExpForLevel(double level)
    {
        if (level <= 1.0) return 0.0;
        var l = level - 1.0;
        return 100.0 * Math.Pow(l, 3.0);
    }
}

/// <summary>
/// 非玩家单位品阶层级量化定义
/// </summary>
public enum NonPlayerUnitTier
{
    /// <summary>
    /// 普通怪：全属性 100% 基准，生命 100% 基准。
    /// </summary>
    Normal = 0,

    /// <summary>
    /// 精英怪：全属性 120% 基准，生命 300% 基准。
    /// </summary>
    Elite = 1,

    /// <summary>
    /// 妖王首领：全属性 150% 基准，生命 1000% 基准。
    /// </summary>
    Boss = 2
}

/// <summary>
/// 百分比曲线换算模式
/// </summary>
public enum PercentCurveMode
{
    /// <summary>
    /// 线性模式（用于：增伤、免伤、仇恨增加、各类属性伤害增免）
    /// 正向：+x% (倍率 1 + x/100)
    /// 负向：除法软衰减 -|x|/(100+|x|)% (倍率 100/(100+|x|))
    /// </summary>
    Linear = 0,

    /// <summary>
    /// 非线性/速率倒数模式（用于：冷却速度、行动速度、灵力消耗减免）
    /// 正向：双曲缩减 x/(100+x)% (倍率 100/(100+x))
    /// 负向：时间/消耗线性延长 -|x|% (倍率 1 + |x|/100)
    /// </summary>
    NonLinear = 1
}

/// <summary>
/// 统一百分比数值类型。
/// 内部储存与计算均使用 double 类型的固定点数 (Points，基准 100 点 = 100%)。
/// 支持在 Linear (线性) 和 NonLinear (非线性) 模式间换算。
/// </summary>
public readonly record struct PercentValue(double Points, PercentCurveMode Mode = PercentCurveMode.Linear)
{
    public static PercentValue Linear(double points) => new(points, PercentCurveMode.Linear);
    public static PercentValue NonLinear(double points) => new(points, PercentCurveMode.NonLinear);

    /// <summary>
    /// 参与战斗公式乘法计算的实际倍率 (Multiplier)
    /// </summary>
    public double Multiplier => Mode switch
    {
        PercentCurveMode.Linear => Points >= 0
            ? 1.0 + Points / 100.0
            : 100.0 / (100.0 + Math.Abs(Points)),

        PercentCurveMode.NonLinear => Points >= 0
            ? 100.0 / (100.0 + Points)
            : 1.0 + Math.Abs(Points) / 100.0,

        _ => 1.0
    };

    /// <summary>
    /// 面板或 UI 展示的实际百分比值 (DisplayPercentage)
    /// </summary>
    public double DisplayPercentage => Mode switch
    {
        PercentCurveMode.Linear => Points >= 0
            ? Points
            : -(Math.Abs(Points) / (100.0 + Math.Abs(Points))) * 100.0,

        PercentCurveMode.NonLinear => Points >= 0
            ? (Points / (100.0 + Points)) * 100.0
            : -Math.Abs(Points),

        _ => 0.0
    };

    /// <summary>
    /// 格式化为百分比文本字符串
    /// </summary>
    public string Format(int decimals = 1)
    {
        var val = DisplayPercentage;
        var prefix = (Mode == PercentCurveMode.Linear && val > 0) ? "+" : "";
        return $"{prefix}{val.ToString($"F{decimals}")}%";
    }
}
