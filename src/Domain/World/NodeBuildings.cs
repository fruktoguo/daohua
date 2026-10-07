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
/// 单座节点建筑的拥挤使用快照：随节点建筑投影一并发入玩家分段并持久化，供建筑 tab 展示。
/// ActiveUsers 为该建筑服务类别下的正在使用人数（玩家 Running 行动类别命中建筑服务类别），
/// Capacity 为当前有效等级承载上限，Efficiency 为拥挤衰减后有效系数（超载时 &lt; 1）。
/// </summary>
public sealed record NodeBuildingUsage(string BuildingId, double ActiveUsers, int Capacity, double Efficiency);

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

    /// <summary>一级建筑拥挤承载基数（人）。</summary>
    public const int CrowdBaseCapacity = 100;
    /// <summary>承载上限随等级线性成长（每级 +10% 基数：capacity = 100 + 10×(L-1)）。</summary>
    public const double CrowdCapacityGrowthPerLevel = 0.1;
    /// <summary>超载时每超出 1 人效果衰减 0.1%（即每 100 人 -10%）。</summary>
    public const double CrowdReductionPerExtraUser = 0.001;
    /// <summary>拥挤衰减幅度封顶 50%（超载 +500 人后效果恒为 50%，永不归零）。</summary>
    public const double CrowdMaxReduction = 0.5;

    /// <summary>建筑有效等级的拥挤承载上限（人）；未建成（L≤0）按一级口径给出展示基数。</summary>
    public static int CrowdCapacity(int level)
        => CrowdBaseCapacity + (int)Math.Round(CrowdBaseCapacity * CrowdCapacityGrowthPerLevel * Math.Max(0, level - 1));

    /// <summary>拥挤效率系数：人数不超承载为 1；超载线性衰减（每超 1 人 -0.1%），减幅封顶 50%。</summary>
    public static double CrowdEfficiency(int level, double activeUsers)
    {
        var capacity = CrowdCapacity(level);
        if (activeUsers <= capacity) return 1;
        return 1 - Math.Min(CrowdMaxReduction, (activeUsers - capacity) * CrowdReductionPerExtraUser);
    }

    /// <summary>
    /// 建筑修饰集服务的行动类别集合：按非零修饰项映射（exploration*→Exploration、alchemy*→Alchemy、
    /// cultivationOutputRatio→Cultivation、combat*→Combat）；空集表示该建筑不提供增益型服务。
    /// </summary>
    public static ActionKind[] ServedKinds(BuildingEffectSet modifiers)
    {
        var kinds = new List<ActionKind>(2);
        if (modifiers.ExplorationSpeedRatio != 0 || modifiers.ExplorationOutputRatio != 0) kinds.Add(ActionKind.Exploration);
        if (modifiers.AlchemySpeedRatio != 0 || modifiers.AlchemyOutputRatio != 0) kinds.Add(ActionKind.Alchemy);
        if (modifiers.CultivationOutputRatio != 0) kinds.Add(ActionKind.Cultivation);
        if (modifiers.CombatHitpointRegen != 0 || modifiers.CombatDamageReductionRatio != 0) kinds.Add(ActionKind.Combat);
        return kinds.ToArray();
    }

    /// <summary>建筑在服务类别下的正在使用人数：各类别计数求和（行动队列串行，同一玩家不会重复计）。</summary>
    public static double ActiveUsers(BuildingEffectSet modifiers, IReadOnlyDictionary<ActionKind, double> countsByKind)
    {
        double users = 0;
        foreach (var kind in ServedKinds(modifiers)) users += countsByKind.GetValueOrDefault(kind);
        return users;
    }

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
        => AggregateEffects(buildings, definitions, nowUnixSeconds, null);

    /// <summary>
    /// 汇总一组节点建筑的当前效果：只计入已建成等级，效果 = 每级基准 × 等级 × 拥挤效率。
    /// <paramref name="countsByKind"/> 为该节点各行动类别的正在使用人数（null 视为零拥挤，
    /// 等价旧口径）；单座建筑使用人数按其服务类别计数求和后统一缩放全部修饰项。
    /// </summary>
    public static BuildingEffectSet AggregateEffects(
        IEnumerable<NodeBuildingState> buildings,
        IReadOnlyDictionary<string, BuildingDefinition> definitions,
        double nowUnixSeconds,
        IReadOnlyDictionary<ActionKind, double>? countsByKind)
    {
        var total = BuildingEffectSet.Empty;
        foreach (var state in buildings)
        {
            if (!definitions.TryGetValue(state.BuildingId, out var definition)) continue;
            var level = EffectiveLevel(state, definition, nowUnixSeconds);
            if (level <= 0) continue;
            var efficiency = countsByKind is null ? 1
                : CrowdEfficiency(level, ActiveUsers(definition.Modifiers, countsByKind));
            total = total.Add(definition.Modifiers.Scale(level * efficiency));
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
