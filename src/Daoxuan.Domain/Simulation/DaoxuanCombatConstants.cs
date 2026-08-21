namespace Daoxuan.Domain.Simulation;

/// <summary>
/// 修仙战斗核心常量配置与公式说明规范。
/// 包含抗性减伤系数、功法法力消耗倍率、攻防对抗系数、六维根基增益系数及暴击倍率等全局常量。
/// </summary>
public static class DaoxuanCombatConstants
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
    /// 对抗公式基础平滑常数 (默认 100.0)。
    /// 
    /// 【中文公式说明】：
    /// 1. 通用对抗成功概率 = 自身属性 / (自身属性 + 对方对抗属性 * 对抗放大系数 + 基础平滑常数 100.0)
    /// 2. 闪避概率 = 守方闪避 / (守方闪避 + 攻方命中 * 对抗放大系数 + 100.0)
    /// 3. 命中概率 = 1.0 - 闪避概率 = (攻方命中 * 对抗放大系数 + 100.0) / (守方闪避 + 攻方命中 * 对抗放大系数 + 100.0)
    /// 4. 暴击概率 = 攻方暴击 / (攻方暴击 + 守方免暴 * 对抗放大系数 + 100.0)
    /// </summary>
    public const double DefaultContestBaseConst = 100.0;


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

    // =========================================================================
    // 9. 境界升级经验表与换算规范 (127 级，乘以 1000 后的经验值)
    // =========================================================================

    public const int MaxRealmLevel = 127;

    /// <summary>
    /// 各境界等级升到下一级所需的经验 (ExpToNext)，索引 0 对应 Lv.1，索引 126 对应 Lv.127 (终境为 0)。
    /// </summary>
    public static readonly double[] RealmLevelExpToNext = [
        10000.0, // Lv.1 凡胎
        12000.0, // Lv.2 炼皮
        15000.0, // Lv.3 锻骨
        20000.0, // Lv.4 易筋
        25000.0, // Lv.5 洗髓
        30000.0, // Lv.6 养气
        50000.0, // Lv.7 通脉
        60000.0, // Lv.8 瑶光
        70000.0, // Lv.9 开阳
        90000.0, // Lv.10 玉衡
        110000.0, // Lv.11 天权
        130000.0, // Lv.12 天玑
        160000.0, // Lv.13 天璇
        250000.0, // Lv.14 天枢
        300000.0, // Lv.15 宗师
        400000.0, // Lv.16 大宗师
        500000.0, // Lv.17 临渊
        800000.0, // Lv.18 叩仙门
        1263000.0, // Lv.19 练气一层
        1516000.0, // Lv.20 练气二层
        1895000.0, // Lv.21 练气三层
        2274000.0, // Lv.22 练气四层
        2779000.0, // Lv.23 练气五层
        3789000.0, // Lv.24 练气六层
        4547000.0, // Lv.25 练气七层
        5432000.0, // Lv.26 练气八层
        6568000.0, // Lv.27 练气九层
        7832000.0, // Lv.28 练气巅峰
        9474000.0, // Lv.29 练气大圆满
        12631000.0, // Lv.30 半步筑基
        15158000.0, // Lv.31 筑基一层
        18316000.0, // Lv.32 筑基二层
        21979000.0, // Lv.33 筑基三层
        26526000.0, // Lv.34 筑基四层
        31579000.0, // Lv.35 筑基五层
        44211000.0, // Lv.36 筑基六层
        53053000.0, // Lv.37 筑基七层
        63158000.0, // Lv.38 筑基八层
        75789000.0, // Lv.39 筑基九层
        90947000.0, // Lv.40 筑基巅峰
        108632000.0, // Lv.41 筑基大圆满
        151578000.0, // Lv.42 半步金丹
        176842000.0, // Lv.43 金丹一层
        214737000.0, // Lv.44 金丹二层
        252632000.0, // Lv.45 金丹三层
        303158000.0, // Lv.46 金丹四层
        366316000.0, // Lv.47 金丹五层
        505263000.0, // Lv.48 金丹六层
        606316000.0, // Lv.49 金丹七层
        732632000.0, // Lv.50 金丹八层
        884211000.0, // Lv.51 金丹九层
        1061053000.0, // Lv.52 金丹巅峰
        1263158000.0, // Lv.53 金丹大圆满
        1768419000.0, // Lv.54 半步元婴
        2147368000.0, // Lv.55 元婴一层
        2526316000.0, // Lv.56 元婴二层
        3031579000.0, // Lv.57 元婴三层
        3663158000.0, // Lv.58 元婴四层
        4421053000.0, // Lv.59 元婴五层
        6315789000.0, // Lv.60 元婴六层
        7578947000.0, // Lv.61 元婴七层
        9094737000.0, // Lv.62 元婴八层
        10863158000.0, // Lv.63 元婴九层
        13010526000.0, // Lv.64 元婴巅峰
        15663158000.0, // Lv.65 元婴大圆满
        22736843000.0, // Lv.66 半步化神
        27789474000.0, // Lv.67 化神一层
        32842105000.0, // Lv.68 化神二层
        39157895000.0, // Lv.69 化神三层
        46736842000.0, // Lv.70 化神四层
        56842105000.0, // Lv.71 化神五层
        82105263000.0, // Lv.72 化神六层
        101052632000.0, // Lv.73 化神七层
        109136842000.0, // Lv.74 化神八层
        117852632000.0, // Lv.75 化神九层
        127326316000.0, // Lv.76 化神巅峰
        137557895000.0, // Lv.77 化神大圆满
        148547367000.0, // Lv.78 半步炼虚
        160421053000.0, // Lv.79 炼虚一层
        173305263000.0, // Lv.80 炼虚二层
        187200000000.0, // Lv.81 炼虚三层
        202231579000.0, // Lv.82 炼虚四层
        218400000000.0, // Lv.83 炼虚五层
        235831579000.0, // Lv.84 炼虚六层
        254652632000.0, // Lv.85 炼虚七层
        274989474000.0, // Lv.86 炼虚八层
        296968421000.0, // Lv.87 炼虚九层
        320715789000.0, // Lv.88 炼虚巅峰
        346357895000.0, // Lv.89 炼虚大圆满
        374021052000.0, // Lv.90 半步合体
        403957895000.0, // Lv.91 合体一层
        436294737000.0, // Lv.92 合体二层
        471157895000.0, // Lv.93 合体三层
        508800000000.0, // Lv.94 合体四层
        549473684000.0, // Lv.95 合体五层
        593431579000.0, // Lv.96 合体六层
        640926316000.0, // Lv.97 合体七层
        692210526000.0, // Lv.98 合体八层
        747536842000.0, // Lv.99 合体九层
        807284211000.0, // Lv.100 合体巅峰
        871831579000.0, // Lv.101 合体大圆满
        941557894000.0, // Lv.102 半步大乘
        1016842105000.0, // Lv.103 大乘一层
        1098189474000.0, // Lv.104 大乘二层
        1186105263000.0, // Lv.105 大乘三层
        1280968421000.0, // Lv.106 大乘四层
        1383410526000.0, // Lv.107 大乘五层
        1494063158000.0, // Lv.108 大乘六层
        1613557895000.0, // Lv.109 大乘七层
        1742652632000.0, // Lv.110 大乘八层
        1882105263000.0, // Lv.111 大乘九层
        2032673684000.0, // Lv.112 大乘巅峰
        2195242105000.0, // Lv.113 大乘大圆满
        2370821053000.0, // Lv.114 半步渡劫
        2560547368000.0, // Lv.115 渡劫一层
        2765431579000.0, // Lv.116 渡劫二层
        2986610526000.0, // Lv.117 渡劫三层
        3225600000000.0, // Lv.118 渡劫四层
        3483663158000.0, // Lv.119 渡劫五层
        3762315789000.0, // Lv.120 渡劫六层
        4063326316000.0, // Lv.121 渡劫七层
        4388336842000.0, // Lv.122 渡劫八层
        4739368421000.0, // Lv.123 渡劫九层
        5118568421000.0, // Lv.124 渡劫巅峰
        5528084211000.0, // Lv.125 渡劫大圆满
        5970315790000.0, // Lv.126 半步飞升
        0.0, // Lv.127 飞升
    ];

    /// <summary>
    /// 各境界等级起始所需的累计总经验 (CumulativeExp)，大小为 128 (索引 1 对应 Lv.1 起始为 0)。
    /// </summary>
    public static readonly double[] RealmLevelCumulativeExp = InitializeCumulativeExp();

    private static double[] InitializeCumulativeExp()
    {
        var cum = new double[MaxRealmLevel + 1];
        cum[0] = 0.0;
        cum[1] = 0.0;
        for (var i = 1; i < MaxRealmLevel; i++)
        {
            cum[i + 1] = cum[i] + RealmLevelExpToNext[i - 1];
        }
        return cum;
    }

    public static double GetRealmExpToNext(int level)
    {
        if (level < 1) return RealmLevelExpToNext[0];
        if (level >= MaxRealmLevel) return 0.0;
        return RealmLevelExpToNext[level - 1];
    }

    public static double GetRealmCumulativeExp(int level)
    {
        if (level <= 1) return 0.0;
        if (level > MaxRealmLevel) return RealmLevelCumulativeExp[MaxRealmLevel];
        return RealmLevelCumulativeExp[level];
    }

    public readonly record struct RealmProgressResult(
        int Level,
        double CurrentLayerExp,
        double ExpToNext,
        double ProgressPercentage,
        double TotalLevel);

    public static RealmProgressResult CalculateRealmProgress(double totalSpirit)
    {
        var spirit = Math.Max(0.0, totalSpirit);
        var level = 1;
        while (level < MaxRealmLevel && spirit >= RealmLevelCumulativeExp[level + 1])
        {
            level++;
        }

        var baseExp = RealmLevelCumulativeExp[level];
        var expToNext = GetRealmExpToNext(level);
        var currentLayerExp = spirit - baseExp;

        if (expToNext <= 0.0 || level >= MaxRealmLevel)
        {
            return new RealmProgressResult(MaxRealmLevel, 0.0, 0.0, 100.0, MaxRealmLevel);
        }

        var progressPercentage = Math.Clamp((currentLayerExp / expToNext) * 100.0, 0.0, 100.0);
        var totalLevel = level + Math.Clamp(currentLayerExp / expToNext, 0.0, 1.0);

        return new RealmProgressResult(level, currentLayerExp, expToNext, progressPercentage, totalLevel);
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
