namespace Domain.Combat;

/// <summary>
/// 战斗反应效果参数包：单位随战携带的「攻击后/受击后」衍生效果强度集合。
/// 打包为单一记录挂载在 <see cref="CombatantStats.Effects"/> 上，后续新增效果
/// 只需增加字段，无需改动结算签名、快照结构与钩子接口。
/// 强度来源：装备 specialMechanics 与怪物配表，经属性解析折算写入。
/// </summary>
/// <param name="HpDrainPoints">气血汲取点数：命中后按「实伤 × 点数/100」回血（Linear 非负钳制，普攻与技能同构）。</param>
/// <param name="MpDrainPoints">灵力汲取点数：命中后按「实伤 × 点数/100」从目标灵力池偷取灵力。</param>
/// <param name="DamageReflectPoints">伤害反弹点数：受击落账后把实收伤害按「点数/100」弹回攻击方。</param>
/// <param name="HealingBonusPoints">治疗增幅点数（Linear）：施放治疗技能时按 1+点数/100 放大治疗量。</param>
public sealed record CombatEffectProfile(
    double HpDrainPoints = 0,
    double MpDrainPoints = 0,
    double DamageReflectPoints = 0,
    double HealingBonusPoints = 0)
{
    /// <summary>全部效果为零的空参数包。</summary>
    public static CombatEffectProfile Empty { get; } = new();

    /// <summary>校验全部效果强度为有限非负数；非法时抛出 InvalidArgument。</summary>
    internal void Validate()
    {
        if (!double.IsFinite(HpDrainPoints) || HpDrainPoints < 0
            || !double.IsFinite(MpDrainPoints) || MpDrainPoints < 0
            || !double.IsFinite(DamageReflectPoints) || DamageReflectPoints < 0
            || !double.IsFinite(HealingBonusPoints) || HealingBonusPoints < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗反应效果参数无效。");
    }
}
