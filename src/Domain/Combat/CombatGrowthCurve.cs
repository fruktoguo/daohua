namespace Domain.Combat;

/// <summary>
/// 怪物同构基准成长曲线单点实现：配表等级换算的成长点数注入 §7.7.5 既有点数通道，
/// 与配表 modifiers 同池相加后统一过 <see cref="PercentValue"/> 曲线。
/// 玩家侧特殊属性为获取型（装备 flatStats 点数键/内功/特质/Buff 等），境界不保送、不调用本组注入。
/// NonLinear 类由旧乘区 f 反解点数 p = 100·(1/f−1)，Linear 类直接 ×100，等效生效值与原曲线逐位一致。
/// 所有输入输出均为 double。
/// </summary>
public static class CombatGrowthCurve
{
    /// <summary>成长等级上限（境界与怪物等级共用同一上限）。</summary>
    public const double MaxLevel = 127;

    /// <summary>暴击伤害基础点数：50 点即 150% 暴伤倍率（1+点数/100）。</summary>
    public const double CriticalDamageBasePoints = 50;

    /// <summary>等级成长进度：x = (clamp(level,1,127)-1)/(127-1)，平方曲线（前期缓后期陡）。</summary>
    public static double Progress(double level)
        => Math.Pow((Math.Clamp(level, 1, MaxLevel) - 1) / (MaxLevel - 1), 2);

    /// <summary>行动速度基准点数：注入行动速度通道，满级 +100（全部读条等效 ×0.5，含波间寻敌）。</summary>
    public static double ActionSpeedPoints(double level) => SpeedPoints(level);

    /// <summary>攻击速度基准点数：注入攻击速度通道，与行动速度同曲线；满级 +100（普攻间隔第二除数）。</summary>
    public static double AttackSpeedPoints(double level) => SpeedPoints(level);

    /// <summary>冷却缩减基准点数：注入冷却缩减通道，满级 +300（冷却等效 ×0.25）。</summary>
    public static double CooldownReductionPoints(double level)
        => 100 * (1 / (1 - 0.75 * Progress(level)) - 1);

    /// <summary>技能增幅基准点数：30·(Tier-1)，满阶 +330（等效技能威力 ×4.3）。</summary>
    public static double SkillBonusPoints(double tier) => 30 * (tier - 1);

    /// <summary>暴击伤害点数（含基础 50 点）：50 + 350·x²，满级 400（倍率 ×5）。</summary>
    public static double CriticalDamagePoints(double level)
        => CriticalDamageBasePoints + 350 * Progress(level);

    /// <summary>普攻增幅基准点数：100·x²，满级 +100（普攻伤害 ×2，Linear 精确等价）。</summary>
    public static double BasicAttackBonusPoints(double level) => 100 * Progress(level);

    /// <summary>技能等级成长：全部数值项每级 +2.0% 线性成长（伤害/治疗/支援数值同式）。</summary>
    public static double AbilityLevelGrowth(double level) => 1 + (level - 1) * 0.02;

    /// <summary>速度类基准点数：100·(1/(1−0.5·x²)−1)，满级 +100（NonLinear 除数下时间 ×0.5）。</summary>
    private static double SpeedPoints(double level) => 100 * (1 / (1 - 0.5 * Progress(level)) - 1);
}
