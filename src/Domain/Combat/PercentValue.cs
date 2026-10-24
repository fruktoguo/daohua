namespace Domain.Combat;

/// <summary>百分比属性的曲线模式；点数基准 100 点 = 100%。</summary>
public enum PercentCurveMode : byte
{
    /// <summary>线性模式：适用于增伤、免伤、仇恨增加与各类属性伤害增减。</summary>
    Linear = 0,
    /// <summary>非线性/速率倒数模式：适用于冷却速度 (CDR)、行动速度与灵力消耗减少。</summary>
    NonLinear = 1
}

/// <summary>
/// 统一百分比数值类型换算：内部统一以 double 点数存储。
/// Linear：+x 点 → 1+x/100；负向按 100/(100+|x|) 除法软着陆。
/// NonLinear：+x 点 → 100/(100+x)（时间/消耗乘数）；负向 1+|x|/100。
/// </summary>
public static class PercentValue
{
    /// <summary>把百分比点数换算为乘区倍率；负值按对应曲线软着陆（收益递减而非线性扣减）。</summary>
    /// <param name="points">百分比点数（100 点 = 100%，允许负值）。</param>
    /// <param name="mode">曲线模式。</param>
    /// <returns>乘区倍率（恒为正有限数）。</returns>
    public static double Multiplier(double points, PercentCurveMode mode)
    {
        if (!double.IsFinite(points)) throw new DomainException(DomainErrorCode.InvalidArgument, "百分比点数必须有限。");
        return mode switch
        {
            PercentCurveMode.Linear => points >= 0 ? 1 + points / 100 : 100 / (100 - points),
            PercentCurveMode.NonLinear => points >= 0 ? 100 / (100 + points) : 1 - points / 100,
            _ => throw new DomainException(DomainErrorCode.InvalidArgument, "百分比曲线模式无效。")
        };
    }
}
