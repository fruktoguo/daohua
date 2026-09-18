using System.Text.Json;
using Domain.GameData;

namespace Domain.World;

/// <summary>
/// 建筑效果集合：建筑配表中的「每级基准效果」，以及节点上全部已建成建筑按等级线性叠加后的汇总。
/// 产出/收益类为比率（0.05 = +5%），速度类为加速比率（除以 1+ratio 缩短行动耗时），
/// 战斗类为每 5 秒气血回复固定值与减伤比率。
/// </summary>
public sealed record BuildingEffectSet(
    double ExplorationSpeedRatio = 0,
    double ExplorationOutputRatio = 0,
    double AlchemySpeedRatio = 0,
    double AlchemyOutputRatio = 0,
    double CultivationOutputRatio = 0,
    double CombatHitpointRegen = 0,
    double CombatDamageReductionRatio = 0)
{
    /// <summary>全零效果单例。</summary>
    public static readonly BuildingEffectSet Empty = new();

    /// <summary>逐项按倍率缩放（建筑 Lv N 效果 = 每级基准 × N）。</summary>
    public BuildingEffectSet Scale(double factor) => new(
        ExplorationSpeedRatio * factor,
        ExplorationOutputRatio * factor,
        AlchemySpeedRatio * factor,
        AlchemyOutputRatio * factor,
        CultivationOutputRatio * factor,
        CombatHitpointRegen * factor,
        CombatDamageReductionRatio * factor);

    /// <summary>逐项相加（多建筑汇总）。</summary>
    public BuildingEffectSet Add(BuildingEffectSet other) => new(
        ExplorationSpeedRatio + other.ExplorationSpeedRatio,
        ExplorationOutputRatio + other.ExplorationOutputRatio,
        AlchemySpeedRatio + other.AlchemySpeedRatio,
        AlchemyOutputRatio + other.AlchemyOutputRatio,
        CultivationOutputRatio + other.CultivationOutputRatio,
        CombatHitpointRegen + other.CombatHitpointRegen,
        CombatDamageReductionRatio + other.CombatDamageReductionRatio);

    /// <summary>行动类别对应的建造加速比率；未覆盖类别返回 0。</summary>
    public double SpeedRatioFor(ActionKind kind) => kind switch
    {
        ActionKind.Exploration => ExplorationSpeedRatio,
        ActionKind.Alchemy => AlchemySpeedRatio,
        _ => 0
    };

    /// <summary>行动类别对应的收益增幅比率；未覆盖类别返回 0。</summary>
    public double OutputRatioFor(ActionKind kind) => kind switch
    {
        ActionKind.Exploration => ExplorationOutputRatio,
        ActionKind.Alchemy => AlchemyOutputRatio,
        ActionKind.Cultivation => CultivationOutputRatio,
        _ => 0
    };
}

/// <summary>建筑单份材料需求（itemId + 基础数量；第 N 级需求 = 基础数量 × 1.1^(N-1)）。</summary>
public sealed record BuildingMaterial(string ItemId, double Quantity);

/// <summary>
/// 某世界节点上一座公共建筑的持久化状态。
/// Level 为已建成等级（0 = 尚未建成）；TargetLevel 为进行中的工程目标等级（0 = 无工程）。
/// 建造中建筑的卡片已存在于节点，但效果按 Level 结算——首建期间 Level=0 不生效，
/// 升级期间维持旧等级，工程完工瞬间跳变。
/// </summary>
public sealed record NodeBuildingState(
    string BuildingId,
    int Level,
    int TargetLevel,
    double StartedAtUnixSeconds,
    IReadOnlyDictionary<string, double> ContributedMaterials);

/// <summary>
/// 节点公共建筑的确定性规则：需求公式、双进度推算、惰性完工与效果汇总。
/// 所有推算只依赖持久化字段与当前时刻，无后台 tick，离线/在线结果一致。
/// </summary>
public static class NodeBuildingRules
{
    /// <summary>一级建造基础耗时（秒）。</summary>
    public const double BaseBuildSeconds = 600;
    /// <summary>每级建造耗时线性增长（目标等级 N 需 Base × (1 + 0.2×(N-1))，51 级 = 110 分钟）。</summary>
    public const double TimeGrowthPerLevel = 0.2;
    /// <summary>每级材料需求指数增长（目标等级 N 需基础 × 1.1^(N-1)）。</summary>
    public const double MaterialGrowthPerLevel = 0.1;

    /// <summary>建筑分组标识（配表 group 字段与节点可建模板共用）。</summary>
    public const string GroupProduction = "production";
    /// <summary>战斗类建筑分组。</summary>
    public const string GroupCombat = "combat";
    /// <summary>其他类建筑分组。</summary>
    public const string GroupOther = "other";
    /// <summary>全部建筑的可建模板标识。</summary>
    public const string TemplateAll = "all";

    /// <summary>目标等级建造所需秒数；N≥1，线性 +20%/级。</summary>
    public static double RequiredBuildSeconds(int targetLevel)
        => BaseBuildSeconds * (1 + TimeGrowthPerLevel * Math.Max(0, targetLevel - 1));

    /// <summary>目标等级所需材料清单：基础数量 × 1.1^(N-1)。</summary>
    public static IReadOnlyDictionary<string, double> RequiredMaterials(BuildingDefinition definition, int targetLevel)
    {
        var scale = Math.Pow(1 + MaterialGrowthPerLevel, Math.Max(0, targetLevel - 1));
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (definition.Materials is null) return result;
        foreach (var material in definition.Materials)
            result[material.ItemId] = material.Quantity * scale;
        return result;
    }

    /// <summary>材料填充比例：逐项按需求封顶后求和 / 需求总量；无材料需求视为已满。</summary>
    public static double MaterialFraction(NodeBuildingState state, BuildingDefinition definition)
    {
        if (state.TargetLevel <= 0) return 1;
        var required = RequiredMaterials(definition, state.TargetLevel);
        if (required.Count == 0) return 1;
        double got = 0, need = 0;
        foreach (var (itemId, quantity) in required)
        {
            need += quantity;
            got += Math.Min(quantity, state.ContributedMaterials.GetValueOrDefault(itemId));
        }
        return need <= 0 ? 1 : Math.Clamp(got / need, 0, 1);
    }

    /// <summary>时间推进比例：已耗时 / 本级所需秒数。</summary>
    public static double TimeFraction(NodeBuildingState state, double nowUnixSeconds)
    {
        if (state.TargetLevel <= 0) return 1;
        return Math.Clamp((nowUnixSeconds - state.StartedAtUnixSeconds) / RequiredBuildSeconds(state.TargetLevel), 0, 1);
    }

    /// <summary>建造总进度 = min(材料%, 时间%)；材料不足时进度停滞，补足后继续。</summary>
    public static double BuildProgress(NodeBuildingState state, BuildingDefinition definition, double nowUnixSeconds)
    {
        if (state.TargetLevel <= 0) return 1;
        return Math.Min(MaterialFraction(state, definition), TimeFraction(state, nowUnixSeconds));
    }

    /// <summary>当前工程是否达到完工条件（材料与时间双进度均满）。</summary>
    public static bool IsComplete(NodeBuildingState state, BuildingDefinition definition, double nowUnixSeconds)
        => state.TargetLevel > 0 && BuildProgress(state, definition, nowUnixSeconds) >= 1;

    /// <summary>惰性完工结算：满足完成条件则落成目标等级并清空材料投入，否则原样返回。</summary>
    public static NodeBuildingState FinalizeIfComplete(NodeBuildingState state, BuildingDefinition definition, double nowUnixSeconds)
        => IsComplete(state, definition, nowUnixSeconds)
            ? state with { Level = state.TargetLevel, TargetLevel = 0, ContributedMaterials = new Dictionary<string, double>(StringComparer.Ordinal) }
            : state;

    /// <summary>完工判定后的有效等级（未完工维持旧等级）。</summary>
    public static int EffectiveLevel(NodeBuildingState state, BuildingDefinition definition, double nowUnixSeconds)
        => FinalizeIfComplete(state, definition, nowUnixSeconds).Level;

    /// <summary>汇总一组节点建筑的当前效果：只计入已建成等级，效果 = 每级基准 × 等级。</summary>
    public static BuildingEffectSet AggregateEffects(
        IEnumerable<NodeBuildingState> buildings,
        IReadOnlyDictionary<string, BuildingDefinition> definitions,
        double nowUnixSeconds)
    {
        var total = BuildingEffectSet.Empty;
        foreach (var state in buildings)
        {
            if (!definitions.TryGetValue(state.BuildingId, out var definition)) continue;
            var level = EffectiveLevel(state, definition, nowUnixSeconds);
            if (level <= 0) continue;
            total = total.Add(definition.Modifiers.Scale(level));
        }
        return total;
    }

    /// <summary>
    /// 解析节点可建造建筑集合：buildableList 自定义列表优先，否则按 buildableTemplate 模板映射分组；
    /// 两者均未配置返回 null（该节点不可建造）。
    /// </summary>
    public static IReadOnlySet<string>? ResolveBuildableIds(JsonElement node, IReadOnlyDictionary<string, BuildingDefinition> definitions)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("buildableList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                var id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
            return ids;
        }
        if (node.TryGetProperty("buildableTemplate", out var template) && template.ValueKind == JsonValueKind.String)
        {
            var name = template.GetString() ?? string.Empty;
            if (string.Equals(name, TemplateAll, StringComparison.Ordinal))
                return new HashSet<string>(definitions.Keys, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, definition) in definitions)
                if (string.Equals(definition.Group, name, StringComparison.Ordinal)) ids.Add(id);
            return ids;
        }
        return null;
    }
}
