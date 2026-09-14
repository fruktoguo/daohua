using System.Text.Json;

namespace Domain.GameData;

/// <summary>基础属性Anchor 的领域定义。</summary>
public sealed record BaseAttributeAnchor(
    double Level, double Hp, double Mp, double Ad, double Ap, double Armor, double Mr,
    double Acc, double Eva, double Crit, double Cavoid, double BreakPt, double Parry,
    double HpRegen, double MpRegen);

/// <summary>境界属性按配表锚点插值；回复是独立列，不从生命或灵力上限反推。</summary>
public sealed class BaseAttributeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public IReadOnlyList<BaseAttributeAnchor> Anchors { get; }

    /// <summary>
    /// 初始化 基础属性目录。
    /// </summary>
    /// <param name="anchors">anchors 参数。</param>
    public BaseAttributeCatalog(IReadOnlyList<BaseAttributeAnchor> anchors)
    {
        if (anchors.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性锚点不能为空。");
        var previous = 0d;
        foreach (var anchor in anchors)
        {
            var values = new[] { anchor.Level, anchor.Hp, anchor.Mp, anchor.Ad, anchor.Ap, anchor.Armor,
                anchor.Mr, anchor.Acc, anchor.Eva, anchor.Crit, anchor.Cavoid, anchor.BreakPt, anchor.Parry,
                anchor.HpRegen, anchor.MpRegen };
            if (values.Any(x => !double.IsFinite(x) || x < 0) || anchor.Level <= previous || anchor.Hp <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性锚点必须递增且数值有效。");
            previous = anchor.Level;
        }
        Anchors = anchors.ToArray();
    }

    /// <summary>
    /// 执行 Parse 操作。
    /// </summary>
    /// <param name="value">value 参数。</param>
    /// <returns>返回处理结果。</returns>
    public static BaseAttributeCatalog Parse(JsonElement value)
    {
        return new BaseAttributeCatalog(value.Deserialize<BaseAttributeAnchor[]>(JsonOptions)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性配置无效。"));
    }

    /// <summary>
    /// 执行 在 操作。
    /// </summary>
    /// <param name="level">level 参数。</param>
    /// <returns>返回处理结果。</returns>
    public BaseAttributeAnchor At(double level)
    {
        var low = Anchors[0];
        var high = low;
        foreach (var anchor in Anchors)
        {
            high = anchor;
            if (anchor.Level >= level) break;
            low = anchor;
        }
        var ratio = high.Level == low.Level ? 0 : Math.Clamp((level - low.Level) / (high.Level - low.Level), 0, 1);
        double Mix(double a, double b) => a + (b - a) * ratio;
        return new(level, Mix(low.Hp, high.Hp), Mix(low.Mp, high.Mp), Mix(low.Ad, high.Ad), Mix(low.Ap, high.Ap),
            Mix(low.Armor, high.Armor), Mix(low.Mr, high.Mr), Mix(low.Acc, high.Acc), Mix(low.Eva, high.Eva),
            Mix(low.Crit, high.Crit), Mix(low.Cavoid, high.Cavoid), Mix(low.BreakPt, high.BreakPt),
            Mix(low.Parry, high.Parry), Mix(low.HpRegen, high.HpRegen), Mix(low.MpRegen, high.MpRegen));
    }
}
