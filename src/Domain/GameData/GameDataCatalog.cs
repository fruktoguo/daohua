using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Domain.Player;
using Domain.World;

namespace Domain.GameData;

/// <summary>行动行为类型分类</summary>
public enum ActionKind : byte
{
    /// <summary>打坐修行/纳气纳灵</summary>
    Cultivation = 0,
    /// <summary>大千世界探索与采集草药矿石</summary>
    Exploration = 1,
    /// <summary>开炉炼丹/炼器精炼</summary>
    Alchemy = 2,
    /// <summary>野外或秘境战斗交锋</summary>
    Combat = 3,
    /// <summary>御剑漫游/跨地图长途迁跃</summary>
    Travel = 4,
    /// <summary>装备强化升阶</summary>
    Enhancement = 5
}

/// <summary>
/// 自动使用物品栏域（总纲 §6.2.3）：独立于行动大类的槽位归属键。
/// 槽位 actionType、物品 usableInActionTypes 值域与 queue.auto_use.configure 命令统一承载本词表。
/// </summary>
public static class AutoUseBar
{
    /// <summary>历练栏：修炼与历练两大类行动共用（吐纳修炼/采集/挖掘/探索等非制造行动），6 槽。</summary>
    public const string Exploration = "exploration";
    /// <summary>炼丹栏。</summary>
    public const string Alchemy = "alchemy";
    /// <summary>炼器栏。</summary>
    public const string Forging = "forging";
    /// <summary>符箓栏。</summary>
    public const string Talisman = "talisman";
    /// <summary>阵法栏。</summary>
    public const string Formation = "formation";
    /// <summary>强化栏。</summary>
    public const string Enhancement = "enhancement";
    /// <summary>战斗栏：药品栏（potion）+ 通用栏（general）各 3 槽。</summary>
    public const string Combat = "combat";

    /// <summary>历练栏槽位数。</summary>
    public const int ExplorationSlotCount = 6;
    /// <summary>其余栏域单组槽位数（战斗栏药品/通用各一组）。</summary>
    public const int DefaultSlotCount = 3;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        { Exploration, Alchemy, Forging, Talisman, Formation, Enhancement, Combat };
    /// <summary>制造技艺栏域：Alchemy 大类行动按配表 technique 归属对应栏。</summary>
    private static readonly HashSet<string> Manufacturing = new(StringComparer.Ordinal)
        { Alchemy, Forging, Talisman, Formation };

    /// <summary>判断标识是否为合法物品栏域（词表内小写标识）。</summary>
    public static bool IsKnown(string? barId) => barId is not null && Known.Contains(barId);

    /// <summary>判断技艺标识是否属于制造技艺（Alchemy 大类行动的栏域归属词表）。</summary>
    public static bool IsManufacturingTechnique(string? technique) => technique is not null && Manufacturing.Contains(technique);

    /// <summary>物品栏域单组槽位数：历练栏 6，其余每组 3。</summary>
    public static int SlotCountFor(string barId)
        => string.Equals(barId, Exploration, StringComparison.OrdinalIgnoreCase) ? ExplorationSlotCount : DefaultSlotCount;

    /// <summary>行动定义 → 所属物品栏域；赶路等无栏域行动返回 null。</summary>
    public static string? For(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            ActionKind.Cultivation or ActionKind.Exploration => Exploration,
            ActionKind.Combat => Combat,
            ActionKind.Enhancement => Enhancement,
            // 炼丹制造大类按配表 technique 归属对应技艺栏；装载校验保证为制造技艺标识。
            ActionKind.Alchemy => action.Technique is { } technique && Manufacturing.Contains(technique) ? technique : null,
            _ => null
        };
    }
}

/// <summary>行动在模拟器中的执行方式。</summary>
public enum ActionExecutionKind : byte
{
    /// <summary>按单轮时长结算的普通行动</summary>
    Timed = 0,
    /// <summary>包含搜寻与战斗状态机的遭遇行动</summary>
    CombatEncounter = 1,
    /// <summary>跨世界节点移动行动</summary>
    Travel = 2
}

/// <summary>
/// 单个行动的静态配表定义。
/// 包含单轮时长、材料消耗与声明式收益列表；收益类型与行动类别无关，由配表逐项声明。
/// </summary>
/// <param name="Id">行动唯一标识。</param>
/// <param name="Name">行动展示名。</param>
/// <param name="Kind">行动类别（修炼/历练/炼丹/战斗/赶路等）。</param>
/// <param name="DurationSeconds">单轮基准时长（秒）；强化行动按目标等级动态覆盖。</param>
/// <param name="Inputs">单轮材料消耗表（ItemId → 数量）。</param>
/// <param name="Yields">声明式收益列表（按 Kind 决定入账通道）。</param>
/// <param name="AllowedNodeIds">允许执行该行动的地图节点集合（白名单）；null 表示全图可行。</param>
/// <param name="HiddenFromCatalog">是否在行动目录中隐藏（仅经入口/剧情开放）。</param>
/// <param name="ExecutionKind">执行方式（计时/战斗遭遇/旅行）。</param>
/// <param name="CombatEncounterId">战斗遭遇行动绑定的遭遇定义 ID；非战斗行动为 null。</param>
/// <param name="Technique">关联的技艺 ID（对应技艺等级提供行动速度加成）；无关联为 null。</param>
/// <param name="ExcludedNodeIds">禁止执行该行动的地图节点集合（黑名单）：AllowedNodeIds 缺省时行动默认全图可行，
/// 仅此处列出的节点被排除（如引气入体之类常驻行动按地点裁剪）；与 AllowedNodeIds 互斥。</param>
/// <param name="Level">行动等级（技艺/配方等级）；null 表示由配方消耗推导。</param>
/// <param name="FoundationCostRatio">单轮额外底蕴成本比例：按当前境界升级所需修为折算（逆命丹 0.25）；
/// 0 表示该行动无底蕴成本，准入与结算完全沿用材料通道。</param>
public sealed record ActionDefinition(
    ActionId Id,
    string Name,
    ActionKind Kind,
    double DurationSeconds,
    IReadOnlyDictionary<ItemId, double> Inputs,
    IReadOnlyList<ActionYield> Yields,
    IReadOnlySet<string>? AllowedNodeIds = null,
    bool HiddenFromCatalog = false,
    ActionExecutionKind ExecutionKind = ActionExecutionKind.Timed,
    string? CombatEncounterId = null,
    string? Technique = null,
    IReadOnlySet<string>? ExcludedNodeIds = null,
    int? Level = null,
    double FoundationCostRatio = 0)
{
}

/// <summary>
/// 行动收益类别。收益入账通道由类别唯一决定，与行动所属 ActionKind 无耦合。
/// </summary>
public enum ActionYieldKind : byte
{
    /// <summary>灵力：战斗资源，累加 Resources.Manapoints 并按上限截断</summary>
    Mana = 0,
    /// <summary>气血：累加 Resources.Hitpoints 并按上限截断</summary>
    Hitpoints = 1,
    /// <summary>修为：经 CreditExperience 入账 Progression.Experience/Foundation（非战斗行动不增加战斗经验）</summary>
    Experience = 2,
    /// <summary>物品：按 Item 进入资产流水</summary>
    Item = 3,
    /// <summary>Buff：按 Buff 授权规格写入 Buffs.Active</summary>
    Buff = 4,
    /// <summary>六维经验：按 Aspect 定向定额入账 Loadout.CoreAttributes，不吃收益倍率</summary>
    RootExp = 5
}

/// <summary>行动收益结算频率。</summary>
public enum ActionYieldPeriod : byte
{
    /// <summary>按推进的整秒逐秒结算（持续速率）</summary>
    PerSecond = 0,
    /// <summary>按单轮行动完成时一次性结算</summary>
    PerRepeat = 1
}

/// <summary>
/// Buff 收益的授权规格：数值效果以通道载荷承载（总纲 §7.9.5.3），与 ActiveBuff 共享形态。
/// </summary>
public sealed record ActionBuffGrant(
    string BuffId,
    string Name,
    double DurationSeconds,
    IReadOnlyList<BuffEffect>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null);

/// <summary>
/// 单条行动收益声明：Kind 决定入账通道，Period 决定按秒或按轮结算。
/// Item 类别必须携带合法 ItemId；Buff 类别必须携带完整授权规格，Amount 表示叠加层数；
/// RootExp 类别必须携带 Aspect 归属维度，Amount 为定向定额六维经验（总纲 §20.1.4）。
/// </summary>
public sealed record ActionYield(
    ActionYieldKind Kind,
    ActionYieldPeriod Period,
    double Amount,
    ItemId? Item = null,
    ActionBuffGrant? Buff = null,
    CoreAspectKind? Aspect = null,
    double? Ratio = null,
    double? AmountMin = null,
    double? AmountMax = null);

/// <summary>
/// 即时打击类消耗品的打击规格（雷击符）：扣除自身灵力后对当前主要敌人造成一次无命中/暴击掷点的技能伤害。
/// </summary>
/// <param name="ManaCostRatio">灵力消耗占上限比率。</param>
/// <param name="ManaDamageRatio">伤害中按灵力上限折算的部分。</param>
/// <param name="SpellAttackRatio">伤害中按法攻折算的部分。</param>
/// <param name="Element">打击元素（决定攻方增伤与守方减伤的元素通道）。</param>
public sealed record ConsumableStrike(
    double ManaCostRatio,
    double ManaDamageRatio,
    double SpellAttackRatio,
    Domain.Combat.CombatElement Element);

/// <summary>
/// 持续/自动消耗品对各行动类型提供的权威效果定义。
/// </summary>
/// <param name="UsableInBars">物品可装配的物品栏域集合（<see cref="AutoUseBar"/> 词表）；物品栏准入唯一权威键。</param>
public sealed record ConsumableDefinition(
    IReadOnlySet<string> UsableInBars,
    double DurationSeconds = 0,
    bool IsContinuous = false,
    double CultivationMultiplier = 1,
    double GatheringMultiplier = 1,
    double AlchemyMultiplier = 1,
    IReadOnlyList<JsonElement>? Triggers = null,
    bool Durable = false,
    ConsumableStrike? Strike = null,
    double CooldownSeconds = 0)
{
    /// <summary>药品类消耗品分类键：战斗域药品栏仅接受本分类的即时消耗品。</summary>
    public const string CategoryPill = "pill";

    /// <summary>获取该消耗品对指定行动类型的收益倍率；不适用类型恒为 1。</summary>
    /// <param name="kind">行动类型。</param>
    /// <returns>对应的收益倍率。</returns>
    public double GetMultiplier(ActionKind kind) => kind switch
    {
        ActionKind.Cultivation => CultivationMultiplier,
        ActionKind.Exploration => GatheringMultiplier,
        ActionKind.Alchemy => AlchemyMultiplier,
        _ => 1d
    };
}

/// <summary>
/// 玩家可建造建筑的静态配表定义。
/// RequiredRealmLevel 为建造解锁所需境界等级；Group 为分组（production/combat/other）；
/// Modifiers 为每级基准效果（Lv N 生效 = 基准 × N）；Materials 为一级基础材料清单（每级 ×1.1^(N-1)）。
/// </summary>
public sealed record BuildingDefinition(
    string Id,
    string Name,
    string Description,
    string Effect,
    int RequiredRealmLevel = 1,
    IReadOnlySet<string>? AvailableNodeIds = null,
    string Group = World.NodeBuildingRules.GroupOther,
    IReadOnlyList<Domain.World.BuildingMaterial>? Materials = null)
{
    /// <summary>1 级初始基准效果。</summary>
    public Domain.World.BuildingEffectSet BaseModifiers { get; init; } = Domain.World.BuildingEffectSet.Empty;

    /// <summary>每升 1 级的线性增幅效果。</summary>
    public Domain.World.BuildingEffectSet GrowthModifiers { get; init; } = Domain.World.BuildingEffectSet.Empty;

    /// <summary>基准修饰项兼容访问器（等于 1 级效果）。</summary>
    public Domain.World.BuildingEffectSet Modifiers
    {
        get => BaseModifiers;
        init
        {
            BaseModifiers = value;
            if (GrowthModifiers == Domain.World.BuildingEffectSet.Empty)
                GrowthModifiers = value;
        }
    }

    /// <summary>计算指定已建成等级（level >= 1）下的实际未折减效果；level <= 0 返回 Empty。</summary>
    public Domain.World.BuildingEffectSet EffectAtLevel(int level)
    {
        if (level <= 0) return Domain.World.BuildingEffectSet.Empty;
        if (level == 1) return BaseModifiers;
        return BaseModifiers.Add(GrowthModifiers.Scale(level - 1));
    }
}

/// <summary>
/// 道具/物品静态配表定义。
/// </summary>
public sealed record ItemDefinition(
    ItemId Id,
    string Name,
    string Description,
    bool IsTradable = true,
    double SellPrice = 0,
    ConsumableDefinition? Consumable = null,
    string Category = "general",
    string? SubCategory = null)
{
    /// <summary>自动消耗品主分类键：自动使用槽位仅接受本主分类的物品；手动消耗品（manual/chest 等）不入槽。</summary>
    public const string CategoryConsumable = "consumable";

    /// <summary>展示用子类型键：缺省时回落主类型（左上角品类丝带与分类筛选的统一口径）。</summary>
    public string DisplaySubCategory => SubCategory ?? Category;
}

/// <summary>纯内存行动目录，运行时可由配置载入；不依赖数据库或宿主。</summary>
public sealed class ActionCatalog
{
    /// <summary>行动目录内容。</summary>

    private readonly IReadOnlyDictionary<ActionId, ActionDefinition> _definitions;

    /// <summary>以行动定义集合创建目录；逐项校验、规范化收益/输入并拒绝重复 ID。</summary>
    /// <param name="definitions">行动定义集合。</param>
    public ActionCatalog(IEnumerable<ActionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<ActionId, ActionDefinition>();
        foreach (var definition in definitions)
        {
            Validate(definition);
            var normalized = definition with
            {
                Inputs = NormalizeQuantities(definition.Inputs, "输入"),
                Yields = NormalizeYields(definition.Yields, definition.Id),
                AllowedNodeIds = definition.AllowedNodeIds is null
                    ? null
                    : new HashSet<string>(definition.AllowedNodeIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal),
                ExcludedNodeIds = definition.ExcludedNodeIds is null
                    ? null
                    : new HashSet<string>(definition.ExcludedNodeIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal)
            };
            if (!map.TryAdd(normalized.Id, normalized))
                throw new DomainException(DomainErrorCode.Duplicate, $"重复行动定义：{definition.Id}。");
        }
        _definitions = new ReadOnlyDictionary<ActionId, ActionDefinition>(map);
    }

    /// <summary>全部已加载行动列表</summary>
    public IReadOnlyCollection<ActionDefinition> All => _definitions.Values.ToArray();
    /// <summary>检查是否包含指定行动</summary>
    public bool Contains(ActionId id) => _definitions.ContainsKey(id);
    /// <summary>尝试根据 ID 获取行动定义</summary>
    public bool TryGet(ActionId id, out ActionDefinition definition) => _definitions.TryGetValue(id, out definition!);
    /// <summary>获取行动定义；未找到时抛出 NotFound 领域异常</summary>
    public ActionDefinition Get(ActionId id) => _definitions.TryGetValue(id, out var definition)
        ? definition
        : throw new DomainException(DomainErrorCode.NotFound, $"行动 {id} 不存在。");

    /// <summary>内置默认基础行动集（引气入体、采药、炼丹、移动赶路）</summary>
    public static ActionCatalog Default { get; } = new(
    [
        new(new ActionId("cultivation.draw-qi"), "引气入体", ActionKind.Cultivation, 10, new Dictionary<ItemId, double>(),
        [
            new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10.0, Ratio: 1.0),
            new ActionYield(ActionYieldKind.Mana, ActionYieldPeriod.PerRepeat, 10.0),
            new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 5.0, Aspect: CoreAspectKind.Meridians, Ratio: 0.5),
            new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 5.0, Aspect: CoreAspectKind.Bone, Ratio: 0.5)
        ]),
        new(new ActionId("explore.gather-ningxue-grass"), "采摘凝气草", ActionKind.Exploration, 8, new Dictionary<ItemId, double>(),
        [
            new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, new ItemId("item.ningxue-grass")),
            new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 8.0, Aspect: CoreAspectKind.Agility, Ratio: 1.0)
        ]),
        new(new ActionId("alchemy.refine-qi-pill"), "炼制引气丹", ActionKind.Alchemy, 12, new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 },
        [
            new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, new ItemId("item.qi-gathering-pill")),
            new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 12.0, Aspect: CoreAspectKind.Consciousness, Ratio: 1.0)
        ], Technique: "alchemy"),
        new(new ActionId("world.travel"), "赶路", ActionKind.Travel, 1, new Dictionary<ItemId, double>(),
        [
            new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerSecond, 1.0, Aspect: CoreAspectKind.Agility, Ratio: 1.0)
        ], null, true, ActionExecutionKind.Travel)
    ]);

    private static void Validate(ActionDefinition definition)
    {
        if (!definition.Id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动定义无效。");
        if (!double.IsFinite(definition.DurationSeconds) || definition.DurationSeconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的数值无效。");
        // 底蕴成本比例是「升级所需修为」的折算系数：只接受 [0, 1] 内的有限数，0 表示不启用该通道。
        if (!double.IsFinite(definition.FoundationCostRatio)
            || definition.FoundationCostRatio < 0 || definition.FoundationCostRatio > 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的底蕴成本比例必须为 [0,1] 内的有限数。");
        if (!Enum.IsDefined(definition.ExecutionKind)
            || (definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && string.IsNullOrWhiteSpace(definition.CombatEncounterId)))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的执行元数据无效。");
        // 地点约束互斥：白名单（仅列处可执行）与黑名单（仅列处禁执行）不可同配，否则语义含糊。
        if (definition.AllowedNodeIds is { Count: > 0 } && definition.ExcludedNodeIds is { Count: > 0 })
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的节点白名单与黑名单互斥。");
    }

    /// <summary>
    /// 校验并规范化行动收益列表：数值必须为正有限数，物品/Buff 只能按轮结算，
    /// 资源类收益不得携带实体标识，同通道同目标重复声明直接拒绝。
    /// </summary>
    private static List<ActionYield> NormalizeYields(IReadOnlyList<ActionYield>? yields, ActionId actionId)
    {
        if (yields is null || yields.Count == 0) return [];
        var normalized = new List<ActionYield>(yields.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var yield in yields)
        {
            if (!Enum.IsDefined(yield.Kind) || !Enum.IsDefined(yield.Period))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益类型或周期无效。");
            if (yield.Ratio is { } r)
            {
                if (!double.IsFinite(r) || r <= 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益百分比必须为正有限数。");
                if (!double.IsFinite(yield.Amount) || yield.Amount < 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益数值无效。");
            }
            else
            {
                if (!double.IsFinite(yield.Amount) || yield.Amount <= 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益数值无效。");
            }
            var key = yield.Kind switch
            {
                ActionYieldKind.Item => yield.Item is { IsValid: true } item && yield.Buff is null
                        && yield.Period == ActionYieldPeriod.PerRepeat
                    ? $"item:{item.Value}"
                    : throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的物品收益必须按轮结算并携带合法物品 ID。"),
                ActionYieldKind.Buff => yield.Buff is { } grant && yield.Item is null
                        && yield.Period == ActionYieldPeriod.PerRepeat
                        && grant.BuffId is { Length: > 0 and <= 128 } && grant.Name is { Length: > 0 and <= 128 }
                        && double.IsFinite(grant.DurationSeconds) && grant.DurationSeconds > 0
                        && (grant.Effects is null || grant.Effects.All(effect => effect is not null
                            && Enum.IsDefined(effect.Channel) && double.IsFinite(effect.Value)))
                    ? $"buff:{grant.BuffId}"
                    : throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的 Buff 收益必须按轮结算并携带完整授权规格。"),
                ActionYieldKind.RootExp => yield.Aspect is { } aspect && yield.Item is null && yield.Buff is null
                    ? $"rootexp:{aspect}:{yield.Period}"
                    : throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的六维收益必须携带归属维度且不得携带实体标识。"),
                _ => yield.Item is null && yield.Buff is null && yield.Aspect is null
                    ? $"resource:{yield.Kind}:{yield.Period}"
                    : throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的资源收益不得携带实体标识。")
            };
            if (!seen.Add(key))
                throw new DomainException(DomainErrorCode.Duplicate, $"行动 {actionId} 的收益声明重复。");
            normalized.Add(yield);
        }
        return normalized;
    }

    private static ReadOnlyDictionary<ItemId, double> NormalizeQuantities(IReadOnlyDictionary<ItemId, double>? values, string label)
    {
        var normalized = new Dictionary<ItemId, double>();
        if (values is null) return new ReadOnlyDictionary<ItemId, double>(normalized);
        foreach (var (item, quantity) in values)
        {
            if (!item.IsValid || !double.IsFinite(quantity) || quantity <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"行动资源{label}无效。");
            if (!normalized.TryAdd(item, quantity)) throw new DomainException(DomainErrorCode.Duplicate, $"行动资源{label}重复。");
        }
        return new ReadOnlyDictionary<ItemId, double>(normalized);
    }
}

/// <summary>
/// 全局游戏静态配表只读目录。
/// 包含版本号、SHA-256 数据指纹、行动/道具/地图/怪物/装备不可变数据，确保服务端与客户端数据同构。
/// </summary>
public sealed class GameDataCatalog
{
    /// <summary>配表结构版本号。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>组装全局配表目录；未提供的解析产物（怪物/遭遇/世界图）按既有 JSON 原料自动重建。</summary>
    /// <param name="contentVersion">配表版本标签。</param>
    /// <param name="fingerprint">配表内容指纹。</param>
    /// <param name="actions">行动目录。</param>
    /// <param name="items">道具定义表。</param>
    /// <param name="maps">地图原始 JSON 列表。</param>
    /// <param name="encounters">遭遇原始 JSON 列表。</param>
    /// <param name="monsters">怪物原始 JSON 表。</param>
    /// <param name="rawItems">物品原始 JSON 表。</param>
    /// <param name="rawEquipments">装备原始 JSON 表。</param>
    /// <param name="combatMonsters">预解析怪物战斗定义（可空，空时由 monsters + expectedAttributes 自动解析）。</param>
    /// <param name="combatEncounters">预解析遭遇定义（可空，空时由 encounters 自动解析）。</param>
    /// <param name="worldGraph">世界拓扑图（可空，空时由 maps 自动构建）。</param>
    /// <param name="baseAttributes">境界属性锚点目录（可空，玩家裸底座）。</param>
    /// <param name="expectedAttributes">期望玩家面板锚点目录（可空，怪物同构建模的基准底座，源自 tiers[].finalExpected）。</param>
    /// <param name="abilities">功法原始 JSON 表。</param>
    /// <param name="duel">斗法全局调参（可空，缺省用内建默认）。</param>
    /// <param name="realmProgression">境界修为上限与寿元配置（可空）。</param>
    /// <param name="buildings">公共建筑定义表（可空，空时无节点建筑可装配）。</param>
    /// <param name="shopOffers">系统商店商品目录（可空，空时商店无货可售）。</param>
    public GameDataCatalog(
        string contentVersion,
        string fingerprint,
        ActionCatalog actions,
        IReadOnlyDictionary<ItemId, ItemDefinition>? items = null,
        IReadOnlyList<JsonElement>? maps = null,
        IReadOnlyList<JsonElement>? encounters = null,
        IReadOnlyDictionary<string, JsonElement>? monsters = null,
        IReadOnlyDictionary<string, JsonElement>? rawItems = null,
        IReadOnlyDictionary<string, JsonElement>? rawEquipments = null,
        IReadOnlyDictionary<string, CombatMonsterDefinition>? combatMonsters = null,
        IReadOnlyDictionary<string, CombatEncounterDefinition>? combatEncounters = null,
        WorldGraph? worldGraph = null,
        BaseAttributeCatalog? baseAttributes = null,
        BaseAttributeCatalog? expectedAttributes = null,
        IReadOnlyDictionary<string, JsonElement>? abilities = null,
        DuelSettings? duel = null,
        RealmProgressionCatalog? realmProgression = null,
        IReadOnlyDictionary<string, BuildingDefinition>? buildings = null,
        ShopCatalog? shopOffers = null)
    {
        if (string.IsNullOrWhiteSpace(contentVersion)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容版本不能为空。");
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容指纹不能为空。");
        ContentVersion = contentVersion;
        Fingerprint = fingerprint;
        // Actions 延迟至 RawItems、RawEquipments 与 RealmProgression 准备就绪后解析推导
        Items = NormalizeItems(items);
        Maps = maps?.Select(value => value.Clone()).ToArray() ?? [];
        Encounters = encounters?.Select(value => value.Clone()).ToArray() ?? [];
        Monsters = monsters?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        RawItems = rawItems?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        RawEquipments = rawEquipments?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Abilities = abilities ?? new Dictionary<string, JsonElement>();
        // 功法配表在装载期整体过统一校验：配表笔误（无效触发词表/缺施放周期）加载即拒绝。
        foreach (var (abilityId, ability) in Abilities)
            try
            {
                Domain.Combat.SkillTriggerEvaluator.ValidateAbility(ability);
            }
            catch (DomainException error)
            {
                throw new DomainException(error.Code, $"功法 {abilityId} 配表无效：{error.Message}");
            }
        Duel = duel ?? DuelSettings.Default;
        // 怪物同构建模底座 = 期望玩家面板（expectedAttributes / tiers[].finalExpected），禁止回退裸锚点；
        // 期望目录与基础锚点同为 1/18/30/…/127 等阶边界，TierAt 口径一致可复用。
        CombatMonsters = combatMonsters ?? ParseMonsterDefinitions(Monsters, expectedAttributes, baseAttributes, Abilities, realmProgression);
        CombatEncounters = combatEncounters ?? ParseEncounterDefinitions(Encounters, CombatMonsters);
        WorldGraph = worldGraph ?? BuildWorldGraph(Maps);
        BaseAttributes = baseAttributes;
        ExpectedAttributes = expectedAttributes;
        RealmProgression = realmProgression;
        Buildings = NormalizeBuildings(buildings);
        ShopOffers = NormalizeShopOffers(shopOffers);
        Actions = ResolveActionCatalog(actions ?? throw new ArgumentNullException(nameof(actions)), RawItems, RawEquipments, realmProgression);
        foreach (var action in Actions.All)
        {
            if (action.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (string.IsNullOrWhiteSpace(action.CombatEncounterId)
                    || !CombatEncounters.ContainsKey(action.CombatEncounterId)))
                throw new DomainException(DomainErrorCode.NotFound, $"行动 {action.Id} 关联的遭遇不存在。");
        }
        TriggerBuffs = DeriveTriggerBuffs(RawItems, Abilities, Actions);
        TriggerBuffIds = new HashSet<string>(TriggerBuffs.Keys, StringComparer.Ordinal);
        ValidateTriggerVocabulary();
    }

    /// <summary>
    /// 词表一致性门禁：结构校验在各自解析点完成，Buff 词表依赖全部配表派生结果，
    /// 故统一在构造末尾复查——引用配表中不存在 Buff 的 buff-stacks 条件层数恒为 0，
    /// 属静默死配置，加载期即拒绝而不是留给运行时表现为「技能永不触发」。
    /// </summary>
    private void ValidateTriggerVocabulary()
    {
        void Check(IReadOnlyList<JsonElement>? triggers, string owner)
        {
            if (triggers is not { Count: > 0 }) return;
            try
            {
                foreach (var trigger in triggers) SkillTriggerEvaluator.ValidateTrigger(trigger, TriggerBuffIds);
            }
            catch (DomainException error)
            {
                throw new DomainException(error.Code, $"{owner} 触发条件无效：{error.Message}");
            }
        }
        foreach (var item in Items.Values) Check(item.Consumable?.Triggers, $"物品 {item.Id}");
        foreach (var (abilityId, ability) in Abilities)
            if (ability.TryGetProperty("defaultTriggers", out var defaults) && defaults.ValueKind == JsonValueKind.Array)
                Check(defaults.EnumerateArray().Select(trigger => trigger.Clone()).ToArray(), $"功法 {abilityId}");
        foreach (var (enemyId, monster) in CombatMonsters)
            foreach (var skill in monster.Skills)
                Check(skill.Triggers, $"怪物 {enemyId}");
    }

    /// <summary>配表版本标签</summary>
    public string ContentVersion { get; }
    /// <summary>配表内容 SHA-256 十六进制前16位指纹，用于网络握手对齐与热更检测</summary>
    public string Fingerprint { get; }
    /// <summary>行动目录</summary>
    public ActionCatalog Actions { get; }
    /// <summary>道具目录</summary>
    public IReadOnlyDictionary<ItemId, ItemDefinition> Items { get; }
    /// <summary>地图原始定义由宿主映射为协议 DTO；领域只负责持有经过资源解析的不可变 JSON 值。</summary>
    public IReadOnlyList<JsonElement> Maps { get; }
    /// <summary>由配表 Maps 解析构建的世界拓扑图</summary>
    public WorldGraph WorldGraph { get; }
    /// <summary>遭遇原始定义由宿主映射为协议 DTO。</summary>
    public IReadOnlyList<JsonElement> Encounters { get; }
    /// <summary>怪物原始定义，用于解析遭遇中的 enemyId 引用。</summary>
    public IReadOnlyDictionary<string, JsonElement> Monsters { get; }
    /// <summary>物品原始定义，用于宿主映射为全功能物品配置 DTO。</summary>
    public IReadOnlyDictionary<string, JsonElement> RawItems { get; }
    /// <summary>装备原始定义，用于宿主映射为全功能装备配置 DTO。</summary>
    public IReadOnlyDictionary<string, JsonElement> RawEquipments { get; }
    /// <summary>已解析的怪物战斗定义，键为 enemy ID。</summary>
    public IReadOnlyDictionary<string, CombatMonsterDefinition> CombatMonsters { get; }
    /// <summary>已解析的遭遇战定义，键为 encounter ID。</summary>
    public IReadOnlyDictionary<string, CombatEncounterDefinition> CombatEncounters { get; }
    /// <summary>境界基础属性锚点目录（玩家裸底座）；未加载 expected-attributes 配置时为 null。</summary>
    public BaseAttributeCatalog? BaseAttributes { get; }
    /// <summary>期望玩家面板锚点目录（tiers[].finalExpected 四乘区完全体）；怪物同构建模的基准底座，未加载时为 null。</summary>
    public BaseAttributeCatalog? ExpectedAttributes { get; }
    /// <summary>功法/神通原始 JSON 定义，键为 ability ID。</summary>
    public IReadOnlyDictionary<string, JsonElement> Abilities { get; }
    /// <summary>斗法玩法全局调参（duel.json）。</summary>
    public DuelSettings Duel { get; }
    /// <summary>境界修为上限与寿元配置；expected-attributes 未提供时为 null（内置兜底目录无境界进度）。</summary>
    public RealmProgressionCatalog? RealmProgression { get; }
    /// <summary>玩家可建造建筑定义，键为 building ID。</summary>
    public IReadOnlyDictionary<string, BuildingDefinition> Buildings { get; }
    /// <summary>系统商店商品目录（官价直售；未配置 shop 配表时为空目录）。</summary>
    public ShopCatalog ShopOffers { get; }

    /// <summary>
    /// 触发条件可引用的 Buff 标识 → 显示名派生表：从当前配表枚举实际可被授予的 Buff——
    /// 消耗品 <c>consumable.buffs[].buffId</c>、功法 <c>buff.buffId</c>（缺省时引擎合成
    /// <c>ability-buff:&lt;abilityId&gt;</c>）、行动产出 Buff <c>yields[].buff.buffId</c> 与特质注入 Buff。
    /// 条件编辑器只能从这里取项，杜绝引用配表中不存在、层数恒为 0 的静默死配置。
    /// </summary>
    public IReadOnlyDictionary<string, string> TriggerBuffs { get; }

    /// <summary>触发条件可引用 Buff 标识集合（<see cref="TriggerBuffs"/> 的键集）。</summary>
    public IReadOnlySet<string> TriggerBuffIds { get; }

    /// <summary>派生触发条件 Buff 词表；同名 Buff 以先出现的显示名为准。</summary>
    private static Dictionary<string, string> DeriveTriggerBuffs(
        IReadOnlyDictionary<string, JsonElement> rawItems,
        IReadOnlyDictionary<string, JsonElement> abilities,
        ActionCatalog actions)
    {
        var buffs = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string? buffId, string? name)
        {
            if (string.IsNullOrWhiteSpace(buffId)) return;
            // 已存在时不覆盖：首个来源的显示名即权威（配表已声明的 name 优先于 id 兜底）。
            buffs.TryAdd(buffId, string.IsNullOrWhiteSpace(name) ? buffId : name);
        }

        // 消耗品战内 Buff：与求值路径读取的同一来源（原始配表 consumable.buffs）。
        foreach (var raw in rawItems.Values)
        {
            if (!raw.TryGetProperty("consumable", out var consumable)
                || consumable.ValueKind != JsonValueKind.Object
                || !consumable.TryGetProperty("buffs", out var buffsArray)
                || buffsArray.ValueKind != JsonValueKind.Array) continue;
            foreach (var buff in buffsArray.EnumerateArray())
                Add(JsonRead.Text(buff, "buffId"), JsonRead.Text(buff, "name"));
        }
        // 功法战内 Buff：显式 buffId 优先，缺省走引擎合成的 ability-buff:<abilityId>。
        // buffs[] 多 Buff 形态逐条登记（如碎罡同时授予碎罡与罡锋）。
        foreach (var (abilityId, ability) in abilities)
        {
            if (ability.TryGetProperty("buffs", out var buffArray) && buffArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in buffArray.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object)
                        Add(JsonRead.Text(entry, "buffId") ?? $"ability-buff:{abilityId}",
                            JsonRead.Text(entry, "name") ?? JsonRead.Text(ability, "name"));
                continue;
            }
            if (!ability.TryGetProperty("buff", out var buff) || buff.ValueKind != JsonValueKind.Object) continue;
            Add(JsonRead.Text(buff, "buffId") ?? $"ability-buff:{abilityId}",
                JsonRead.Text(buff, "name") ?? JsonRead.Text(ability, "name"));
        }
        // 行动产出 Buff（打坐/历练授予的长效增益）。
        foreach (var action in actions.All)
            foreach (var yield in action.Yields)
                if (yield.Buff is { } grant) Add(grant.BuffId, grant.Name);
        // 特质注入的机制 Buff（如隐身）。
        Add(Combat.CombatTraitRules.UnseenBuffId, "隐身");
        return buffs;
    }

    /// <summary>内置单例兜底配表</summary>
    public static GameDataCatalog BuiltIn { get; } = new("builtin-1", "builtin", ActionCatalog.Default);

    /// <summary>
    /// 从原始 JSON 字节流集合解析构建不可变游戏配表目录，并实时计算 SHA-256 数据指纹。
    /// </summary>
    public static GameDataCatalog Parse(IEnumerable<ReadOnlyMemory<byte>> jsonDocuments)
    {
        ArgumentNullException.ThrowIfNull(jsonDocuments);
        var actions = new List<ActionDefinition>();
        var items = new Dictionary<ItemId, ItemDefinition>();
        var maps = new List<JsonElement>();
        var encounters = new List<JsonElement>();
        var monsters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rawItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rawEquipments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var abilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var buildings = new Dictionary<string, BuildingDefinition>(StringComparer.Ordinal);
        var shopOffers = new List<ShopOfferDefinition>();
        DuelSettings? duel = null;
        BaseAttributeCatalog? baseAttributes = null;
        BaseAttributeCatalog? expectedAttributes = null;
        RealmProgressionCatalog? realmProgression = null;
        var contentVersion = "unknown";
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in jsonDocuments)
        {
            digest.AppendData(bytes.Span);
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("baseAttributeAnchors", out var anchors))
            {
                baseAttributes = BaseAttributeCatalog.Parse(anchors);
                var rawBaseAttributes = BaseAttributeCatalog.ParseRaw(anchors);
                // 同一文件的 levelExpected[].finalExpected = 逐级期望玩家面板（四乘区完全体），怪物同构建模底座；
                // 按整级锚点建目录避免跨等阶线性插值把 T2 面板提前摊进 T1 中后段。
                if (document.RootElement.TryGetProperty("levelExpected", out var levelExpected) && levelExpected.ValueKind == JsonValueKind.Array)
                    expectedAttributes = BaseAttributeCatalog.ParseExpected(levelExpected, baseAttributes, rawBaseAttributes);
            }
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("realmProgression", out var progression))
                realmProgression = RealmProgressionCatalog.Parse(progression);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("contentVersion", out var version))
            {
                contentVersion = version.GetString() ?? contentVersion;
                continue;
            }
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idProperty)) continue;
                var id = idProperty.GetString() ?? string.Empty;
                if (id.StartsWith("item.", StringComparison.Ordinal) || id.StartsWith("currency.", StringComparison.Ordinal))
                {
                    var definition = ParseItem(entry, id);
                    items.Add(definition.Id, definition);
                    rawItems.TryAdd(id, entry.Clone());
                }
                else if (id.StartsWith("ability.", StringComparison.Ordinal))
                {
                    abilities.Add(id, entry.Clone());
                }
                else if (id.StartsWith("equipment.", StringComparison.Ordinal))
                {
                    rawEquipments.TryAdd(id, entry.Clone());
                }
                else if (id.StartsWith("map.", StringComparison.Ordinal))
                {
                    maps.Add(entry.Clone());
                }
                else if (id.StartsWith("encounter.", StringComparison.Ordinal))
                {
                    encounters.Add(entry.Clone());
                }
                else if (id.StartsWith("enemy.", StringComparison.Ordinal))
                {
                    monsters.Add(id, entry.Clone());
                }
                else if (id.StartsWith("duel.", StringComparison.Ordinal))
                {
                    duel = DuelSettings.Parse(entry);
                }
                else if (id.StartsWith("building.", StringComparison.Ordinal))
                {
                    buildings.Add(id, ParseBuilding(entry, id));
                }
                else if (id.StartsWith(ShopOfferDefinition.IdPrefix, StringComparison.Ordinal))
                {
                    // 商品对物品的引用在循环结束后统一校验：配表文件顺序不保证 items 先于 shop。
                    if (shopOffers.Any(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)))
                        throw new DomainException(DomainErrorCode.Duplicate, $"商店商品 ID 重复：{id}。");
                    shopOffers.Add(ParseShopOffer(entry, id));
                }
                else if (entry.TryGetProperty("durationSeconds", out _))
                {
                    actions.Add(ParseAction(entry, id));
                }
            }
        }
        var fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()[..16];
        return new GameDataCatalog(contentVersion, fingerprint, actions.Count == 0 ? ActionCatalog.Default : new ActionCatalog(actions), items, maps, encounters, monsters, rawItems, rawEquipments, baseAttributes: baseAttributes, expectedAttributes: expectedAttributes, abilities: abilities, duel: duel, realmProgression: realmProgression, buildings: buildings, shopOffers: new ShopCatalog(shopOffers));
    }

    /// <summary>
    /// 从当前程序集嵌入的 .json 资源中反射加载完整游戏数据。
    /// </summary>
    public static GameDataCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(GameDataCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"无法读取内嵌游戏数据：{name}。");
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return (ReadOnlyMemory<byte>)memory.ToArray();
            });
        return Parse(resources);
    }

    /// <summary>解析系统商店商品条目（shop.offers.json 单条）。</summary>
    private static ShopOfferDefinition ParseShopOffer(JsonElement entry, string id)
    {
        var itemId = JsonRead.Text(entry, "itemId", string.Empty);
        var unitPrice = JsonRead.Number(entry, "unitPrice");
        int? dailyLimit = null;
        if (entry.TryGetProperty("dailyLimit", out var limitProperty) && limitProperty.ValueKind != JsonValueKind.Null)
        {
            if (limitProperty.ValueKind != JsonValueKind.Number || !limitProperty.TryGetInt32(out var parsedLimit))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"商店商品 {id} 的每日限购必须为正整数。");
            dailyLimit = parsedLimit;
        }
        var sortOrder = entry.TryGetProperty("sortOrder", out var sortProperty) && sortProperty.TryGetInt32(out var parsedSort)
            ? parsedSort : 0;
        return new ShopOfferDefinition(id, itemId, unitPrice, dailyLimit, sortOrder);
    }

    /// <summary>商店目录归一：未配置时返回空目录；已配置时校验引用的物品是否存在。</summary>
    private ShopCatalog NormalizeShopOffers(ShopCatalog? shopOffers)
    {
        if (shopOffers is null || shopOffers.Count == 0) return ShopCatalog.Empty;
        foreach (var offer in shopOffers.All)
        {
            if (!Items.ContainsKey(new ItemId(offer.ItemId)))
                throw new DomainException(DomainErrorCode.NotFound, $"商店商品 {offer.Id} 引用的物品不存在：{offer.ItemId}。");
        }
        return shopOffers;
    }

    /// <summary>从 JSON 对象提取并解析玩家可建造建筑定义（BuildingDefinition）</summary>
    private static BuildingDefinition ParseBuilding(JsonElement entry, string id)
    {
        var name = entry.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? id : id;
        var description = entry.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() ?? string.Empty : string.Empty;
        var effect = entry.TryGetProperty("effect", out var effectProperty) ? effectProperty.GetString() ?? string.Empty : string.Empty;
        var requiredRealmLevel = entry.TryGetProperty("requiredRealmLevel", out var realmProperty) && realmProperty.TryGetInt32(out var realm) ? realm : 1;
        var group = entry.TryGetProperty("group", out var groupProperty) ? groupProperty.GetString() ?? World.NodeBuildingRules.GroupOther : World.NodeBuildingRules.GroupOther;
        var nodes = entry.TryGetProperty("availableNodeIds", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array
            ? new HashSet<string>(nodeArray.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0), StringComparer.Ordinal)
            : null;
        var baseModifiers = ParseBuildingModifiers(entry, "baseModifiers");
        var growthModifiers = ParseBuildingModifiers(entry, "growthModifiers");
        if (baseModifiers == World.BuildingEffectSet.Empty && entry.TryGetProperty("modifiers", out _))
        {
            baseModifiers = ParseBuildingModifiers(entry, "modifiers");
        }
        if (growthModifiers == World.BuildingEffectSet.Empty)
        {
            growthModifiers = baseModifiers;
        }
        var materials = ParseBuildingMaterials(entry);
        return new BuildingDefinition(id, name, description, effect, requiredRealmLevel, nodes, group, materials)
        {
            BaseModifiers = baseModifiers,
            GrowthModifiers = growthModifiers
        };
    }

    /// <summary>解析建筑效果块（缺省全零）。</summary>
    private static World.BuildingEffectSet ParseBuildingModifiers(JsonElement entry, string propertyName = "modifiers")
    {
        if (!entry.TryGetProperty(propertyName, out var block) || block.ValueKind != JsonValueKind.Object)
            return World.BuildingEffectSet.Empty;
        return new World.BuildingEffectSet(
            ExplorationSpeedRatio: JsonRead.Number(block, "explorationSpeedRatio"),
            ExplorationOutputRatio: JsonRead.Number(block, "explorationOutputRatio"),
            AlchemySpeedRatio: JsonRead.Number(block, "alchemySpeedRatio"),
            AlchemyOutputRatio: JsonRead.Number(block, "alchemyOutputRatio"),
            CultivationOutputRatio: JsonRead.Number(block, "cultivationOutputRatio"),
            CombatHitpointRegen: JsonRead.Number(block, "combatHitpointRegen"),
            CombatDamageReductionRatio: JsonRead.Number(block, "combatDamageReductionRatio"),
            MonsterWeakenRatio: JsonRead.Number(block, "monsterWeakenRatio"),
            GatheringSpeedRatio: JsonRead.Number(block, "gatheringSpeedRatio"),
            MiningSpeedRatio: JsonRead.Number(block, "miningSpeedRatio"),
            ForgingSpeedRatio: JsonRead.Number(block, "forgingSpeedRatio"),
            TalismanSpeedRatio: JsonRead.Number(block, "talismanSpeedRatio"),
            FormationSpeedRatio: JsonRead.Number(block, "formationSpeedRatio"),
            EnhancementSpeedRatio: JsonRead.Number(block, "enhancementSpeedRatio"),
            CombatManapointRegen: JsonRead.Number(block, "combatManapointRegen"));
    }

    /// <summary>解析建筑一级基础材料清单（materials 数组，缺省为空）。</summary>
    private static List<World.BuildingMaterial>? ParseBuildingMaterials(JsonElement entry)
    {
        if (!entry.TryGetProperty("materials", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var materials = new List<World.BuildingMaterial>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemId = item.TryGetProperty("itemId", out var idProperty) ? idProperty.GetString() : null;
            var quantity = JsonRead.Number(item, "quantity");
            if (string.IsNullOrWhiteSpace(itemId) || !double.IsFinite(quantity) || quantity <= 0) continue;
            materials.Add(new World.BuildingMaterial(itemId.Trim(), quantity));
        }
        return materials.Count == 0 ? null : materials;
    }

    /// <summary>校验并规范化建筑定义表：标识/名称非空、境界门槛为正、节点引用去空白。</summary>
    private static ReadOnlyDictionary<string, BuildingDefinition> NormalizeBuildings(IReadOnlyDictionary<string, BuildingDefinition>? buildings)
    {
        var normalized = new Dictionary<string, BuildingDefinition>(StringComparer.Ordinal);
        if (buildings is null) return new ReadOnlyDictionary<string, BuildingDefinition>(normalized);
        foreach (var (id, definition) in buildings)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || definition.Id.Length > 128 || definition.Id != id
                || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128
                || definition.RequiredRealmLevel < 1
                || definition.Group is not (World.NodeBuildingRules.GroupProduction or World.NodeBuildingRules.GroupCombat or World.NodeBuildingRules.GroupOther)
                || (definition.Description?.Length ?? 0) > 1024 || (definition.Effect?.Length ?? 0) > 512
                || (definition.Materials ?? []).Any(material => material is null
                    || string.IsNullOrWhiteSpace(material.ItemId) || material.ItemId.Length > 128
                    || !double.IsFinite(material.Quantity) || material.Quantity <= 0))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"建筑定义无效：{id}。");
            normalized.Add(id, definition with
            {
                Description = definition.Description ?? string.Empty,
                Effect = definition.Effect ?? string.Empty,
                AvailableNodeIds = definition.AvailableNodeIds is null
                    ? null
                    : new HashSet<string>(definition.AvailableNodeIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal)
            });
        }
        return new ReadOnlyDictionary<string, BuildingDefinition>(normalized);
    }

    /// <summary>从 JSON 对象提取并解析行动定义（ActionDefinition）</summary>
    private static ActionDefinition ParseAction(JsonElement entry, string id)
    {
        var kind = entry.TryGetProperty("type", out var type)
            ? ParseKind(type.GetString())
            : ActionKind.Exploration;
        var inputs = ParseQuantities(entry, "inputs");
        var yields = ParseYields(entry, id);
        var nodes = entry.TryGetProperty("availableNodeIds", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array
            ? new HashSet<string>(nodeArray.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0), StringComparer.Ordinal)
            : null;
        var excludedNodes = entry.TryGetProperty("excludedNodeIds", out var excludedArray) && excludedArray.ValueKind == JsonValueKind.Array
            ? new HashSet<string>(excludedArray.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0), StringComparer.Ordinal)
            : null;
        var duration = entry.TryGetProperty("durationSeconds", out var durationProperty) ? durationProperty.GetDouble() : 0;
        var hiddenFromCatalog = entry.TryGetProperty("hiddenFromCatalog", out var hiddenProperty)
            && hiddenProperty.ValueKind == JsonValueKind.True;
        var executionKind = ParseExecutionKind(entry, kind);
        var combatEncounterId = entry.TryGetProperty("combatEncounterId", out var encounter)
            && encounter.ValueKind == JsonValueKind.String
            ? encounter.GetString()
            : null;
        var technique = entry.TryGetProperty("technique", out var techniqueProperty)
            && techniqueProperty.ValueKind == JsonValueKind.String
            ? techniqueProperty.GetString()
            : null;
        var level = entry.TryGetProperty("level", out var levelProp) && levelProp.TryGetInt32(out var lvl) && lvl > 0
            ? lvl
            : (int?)null;
        // 底蕴成本比例缺省 0（无该通道）；非数值或非 [0,1] 的非法配置按 0 处理，
        // 与「配表笔误不得让行动永久不可执行」的自愈口径一致。
        var foundationCostRatio = entry.TryGetProperty("foundationCostRatio", out var foundationProperty)
            && foundationProperty.ValueKind == JsonValueKind.Number
            && foundationProperty.TryGetDouble(out var parsedFoundationRatio)
            && double.IsFinite(parsedFoundationRatio) && parsedFoundationRatio is >= 0 and <= 1
            ? parsedFoundationRatio
            : 0;
        return new ActionDefinition(new ActionId(id), entry.TryGetProperty("name", out var name) ? name.GetString() ?? id : id, kind, duration, inputs, yields, nodes, hiddenFromCatalog, executionKind, combatEncounterId, technique, excludedNodes, level, foundationCostRatio);
    }

    /// <summary>
    /// 解析行动声明式收益列表（yields）。未知收益类型或结算频率直接拒绝配置，
    /// 数值与结构约束由 ActionCatalog.NormalizeYields 统一校验。
    /// </summary>
    private static List<ActionYield> ParseYields(JsonElement entry, string actionId)
    {
        if (!entry.TryGetProperty("yields", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        var yields = new List<ActionYield>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益配置必须为对象。");
            var kindText = element.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() : null;
            var kind = kindText?.ToLowerInvariant() switch
            {
                "mana" => ActionYieldKind.Mana,
                "hitpoints" => ActionYieldKind.Hitpoints,
                "experience" => ActionYieldKind.Experience,
                "item" => ActionYieldKind.Item,
                "buff" => ActionYieldKind.Buff,
                "rootexp" => ActionYieldKind.RootExp,
                _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益类型无效：{kindText ?? "缺失"}。")
            };
            var perText = element.TryGetProperty("per", out var perProperty) ? perProperty.GetString() : null;
            var period = perText?.ToLowerInvariant() switch
            {
                "second" => ActionYieldPeriod.PerSecond,
                "repeat" => ActionYieldPeriod.PerRepeat,
                _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益结算频率无效：{perText ?? "缺失"}。")
            };
            double? ratio = null;
            if (element.TryGetProperty("percentage", out var percentageProperty) && percentageProperty.TryGetDouble(out var pct))
                ratio = pct / 100.0;
            else if (element.TryGetProperty("percent", out var percentProperty) && percentProperty.TryGetDouble(out var p))
                ratio = p / 100.0;
            else if (element.TryGetProperty("ratio", out var ratioProperty) && ratioProperty.TryGetDouble(out var r))
                ratio = r;

            var amount = element.TryGetProperty("amount", out _) ? JsonRead.Number(element, "amount") : 0.0;
            if (kind == ActionYieldKind.Item)
            {
                var itemId = element.TryGetProperty("id", out var itemIdProperty) ? itemIdProperty.GetString() : null;
                var amountMin = element.TryGetProperty("amountMin", out var minProp) && minProp.TryGetDouble(out var minVal) ? (double?)minVal : null;
                var amountMax = element.TryGetProperty("amountMax", out var maxProp) && maxProp.TryGetDouble(out var maxVal) ? (double?)maxVal : null;
                yields.Add(new(kind, period, amount, new ItemId(itemId ?? string.Empty), AmountMin: amountMin, AmountMax: amountMax));
                continue;
            }
            if (kind == ActionYieldKind.Buff)
            {
                var buffId = element.TryGetProperty("id", out var buffIdProperty) ? buffIdProperty.GetString() : null;
                var buffName = element.TryGetProperty("name", out var buffNameProperty) ? buffNameProperty.GetString() : null;
                var durationSeconds = JsonRead.Number(element, "durationSeconds");
                // 效果键按注册表从 modifiers 解析（行动产出无等级成长，乘区恒 1）；statRatios 键不在注册表、由下方独立读取。
                List<BuffEffect>? effects = null;
                if (element.TryGetProperty("modifiers", out var modifierObject)
                    && modifierObject.ValueKind == JsonValueKind.Object)
                    effects = BuffEffectParser.Parse(modifierObject, 1.0);
                IReadOnlyDictionary<string, double>? statRatios = null;
                if (element.TryGetProperty("modifiers", out var modifierRatioObject)
                    && modifierRatioObject.ValueKind == JsonValueKind.Object
                    && modifierRatioObject.TryGetProperty("statRatios", out var ratioObject)
                    && ratioObject.ValueKind == JsonValueKind.Object)
                    statRatios = ratioObject.EnumerateObject()
                        .Where(pair => pair.Value.TryGetDouble(out _))
                        .ToDictionary(pair => pair.Name, pair => pair.Value.GetDouble(), StringComparer.Ordinal);
                yields.Add(new(kind, period, amount, null, new ActionBuffGrant(
                    buffId ?? string.Empty, buffName ?? string.Empty, durationSeconds, effects, statRatios)));
                continue;
            }
            if (kind == ActionYieldKind.RootExp)
            {
                var aspectText = element.TryGetProperty("aspect", out var aspectProperty) ? aspectProperty.GetString() : null;
                var aspect = aspectText?.ToLowerInvariant() switch
                {
                    "stamina" => CoreAspectKind.Stamina,
                    "meridians" => CoreAspectKind.Meridians,
                    "attack" => CoreAspectKind.Attack,
                    "bone" => CoreAspectKind.Bone,
                    "agility" => CoreAspectKind.Agility,
                    "consciousness" => CoreAspectKind.Consciousness,
                    _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的六维收益归属维度无效：{aspectText ?? "缺失"}。")
                };
                yields.Add(new(kind, period, amount, null, null, aspect, ratio));
                continue;
            }
            yields.Add(new(kind, period, amount, null, null, null, ratio));
        }
        return yields;
    }

    private static ActionExecutionKind ParseExecutionKind(JsonElement entry, ActionKind kind)
    {
        if (entry.TryGetProperty("executionKind", out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.ToLowerInvariant() switch
            {
                "combatencounter" or "combat-encounter" => ActionExecutionKind.CombatEncounter,
                "travel" => ActionExecutionKind.Travel,
                "timed" => ActionExecutionKind.Timed,
                _ => throw new DomainException(DomainErrorCode.InvalidArgument, "行动执行类型无效。")
            };
        }
        return kind switch
        {
            ActionKind.Combat => ActionExecutionKind.CombatEncounter,
            ActionKind.Travel => ActionExecutionKind.Travel,
            _ => ActionExecutionKind.Timed
        };
    }

    /// <summary>从 JSON 对象提取并解析道具定义（ItemDefinition）</summary>
    private static ItemDefinition ParseItem(JsonElement entry, string id)
    {
        var name = entry.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? id : id;
        var description = entry.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() ?? string.Empty : string.Empty;
        var price = JsonRead.Number(entry, "sellPrice");
        var tradable = !entry.TryGetProperty("isTradable", out var tradableProperty) || tradableProperty.GetBoolean();
        ConsumableDefinition? consumable = null;
        if (entry.TryGetProperty("consumable", out var consumableProperty) && consumableProperty.ValueKind == JsonValueKind.Object)
        {
            var usableBars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in JsonRead.StringArray(consumableProperty, "usableInActionTypes"))
            {
                var bar = value.Trim().ToLowerInvariant();
                if (!AutoUseBar.IsKnown(bar))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"消耗品适用物品栏域无效：{value}。");
                usableBars.Add(bar);
            }
            List<JsonElement>? triggers = null;
            if (consumableProperty.TryGetProperty("triggers", out var triggersProperty)
                && triggersProperty.ValueKind == JsonValueKind.Array)
            {
                triggers = [];
                foreach (var trigger in triggersProperty.EnumerateArray()) triggers.Add(trigger.Clone());
            }
            ConsumableStrike? strike = null;
            if (consumableProperty.TryGetProperty("strike", out var strikeProperty)
                && strikeProperty.ValueKind == JsonValueKind.Object)
            {
                var elementText = strikeProperty.TryGetProperty("element", out var elementProperty)
                    ? elementProperty.GetString() : null;
                if (!Enum.TryParse<Domain.Combat.CombatElement>(elementText ?? "none", true, out var element))
                    element = Domain.Combat.CombatElement.None;
                strike = new ConsumableStrike(
                    JsonRead.Number(strikeProperty, "manaCostRatio"),
                    JsonRead.Number(strikeProperty, "manaDamageRatio"),
                    JsonRead.Number(strikeProperty, "spellAttackRatio"),
                    element);
            }
            consumable = new ConsumableDefinition(
                new ReadOnlySet<string>(usableBars),
                JsonRead.Number(consumableProperty, "durationSeconds"),
                JsonRead.Boolean(consumableProperty, "isContinuous"),
                JsonRead.Number(consumableProperty, "cultivationMultiplier", 1),
                JsonRead.Number(consumableProperty, "gatheringMultiplier", 1),
                JsonRead.Number(consumableProperty, "alchemyMultiplier", 1),
                triggers,
                JsonRead.Boolean(consumableProperty, "durable"),
                strike,
                JsonRead.Number(consumableProperty, "cooldownSeconds"));
        }
        var category = entry.TryGetProperty("category", out var categoryProperty)
            ? categoryProperty.GetString() ?? "general" : "general";
        var subCategory = entry.TryGetProperty("subCategory", out var subCategoryProperty)
            ? subCategoryProperty.GetString() : null;
        return new ItemDefinition(new ItemId(id), name, description, tradable, price, consumable, category, subCategory);
    }

    /// <summary>解析消耗/产出等数量字典</summary>
    private static Dictionary<ItemId, double> ParseQuantities(JsonElement entry, string property)
    {
        var values = new Dictionary<ItemId, double>();
        if (!entry.TryGetProperty(property, out var objectValue) || objectValue.ValueKind != JsonValueKind.Object) return values;
        foreach (var item in objectValue.EnumerateObject()) values[new ItemId(item.Name)] = item.Value.GetDouble();
        return values;
    }

    private static ActionKind ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "cultivation" => ActionKind.Cultivation,
        "alchemy" => ActionKind.Alchemy,
        "combat" => ActionKind.Combat,
        "travel" => ActionKind.Travel,
        "enhancement" => ActionKind.Enhancement,
        _ => ActionKind.Exploration
    };

    private static Dictionary<string, CombatMonsterDefinition> ParseMonsterDefinitions(
        IReadOnlyDictionary<string, JsonElement> raw, BaseAttributeCatalog? playerBaseline,
        BaseAttributeCatalog? tierBoundaries,
        IReadOnlyDictionary<string, JsonElement> abilities, RealmProgressionCatalog? realmProgression)
    {
        var result = new Dictionary<string, CombatMonsterDefinition>(StringComparer.Ordinal);
        foreach (var (id, value) in raw)
        {
            var budget = JsonRead.Number(value, "budgetCost", 1);
            var weights = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("weights", out var weightsValue) && weightsValue.ValueKind == JsonValueKind.Object
                ? weightsValue : default;
            double Weight(string key) => JsonRead.Number(weights, key, 1);
            // 怪我同构建模：以同等级期望玩家面板（tiers[].finalExpected 四乘区完全体）为底座，
            // 按生命层次倍率 + 倾向权重派生全部战斗属性——非裸 baseAttributeAnchors（总纲 §7.3/§10.1）。
            var level = JsonRead.Integer(value, "level", 1);
            if (playerBaseline is null || tierBoundaries is null)
                throw new DomainException(DomainErrorCode.InvalidArgument,
                    $"怪物 {id} 缺少期望玩家面板基准：expected-attributes 的 levelExpected 未加载。");
            var anchor = playerBaseline.At(level);
            var rank = JsonRead.Text(value, "rank", "minion").ToLowerInvariant() switch
            {
                "elite" => CombatMonsterRank.Elite,
                "boss" => CombatMonsterRank.Boss,
                _ => CombatMonsterRank.Minion
            };
            var attrMultiplier = rank switch { CombatMonsterRank.Elite => 1.2, CombatMonsterRank.Boss => 1.5, _ => 0.8 };
            var hpMultiplier = rank switch { CombatMonsterRank.Elite => 5.0, CombatMonsterRank.Boss => 20.0, _ => 3.0 };
            // 成长点数全单位同构：与玩家属性解析共用 CombatGrowthCurve 单点实现，
            // 折算为点数注入对应通道（下方与 modifiers 同池相加）。
            var interval = JsonRead.Number(value, "attackIntervalMilliseconds", 0) / 1_000d;
            // 普攻节奏全单位统一：基准 1.0s（或配表覆写基准）÷(1+攻速/100)÷(1+行动速度/100)，
            // 双除数由引擎在出手边界结算；技能吟唱另一条线：castMilliseconds ÷(1+行动速度/100)。
            interval = interval > 0 ? interval : 1;
            // 怪物双攻按等级阶梯削减（总纲 §7.3.1 / §10.1.1）：1级削40%至24级削20%，后续恒定削20%
            var attackMultiplier = level <= 1 ? 0.6 : (level >= 24 ? 0.8 : 0.6 + 0.2 * (level - 1.0) / 23.0);
            var ad = anchor.Ad * attrMultiplier * Weight("ad") * attackMultiplier;
            var ap = anchor.Ap * attrMultiplier * Weight("ap") * attackMultiplier;
            // 等阶边界由 12 阶锚点目录判定——levelExpected 是 127 个每级锚点，不能直接当等阶边界用。
            var tier = tierBoundaries.TierAt(level);
            var drops = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("dropTable", out var dropValue) && dropValue.ValueKind == JsonValueKind.Array
                ? dropValue.EnumerateArray().Select(drop => new CombatDropDefinition(
                    new ItemId(JsonRead.Text(drop, "itemId", "")),
                    JsonRead.Number(drop, "dropChance", 0),
                    JsonRead.Number(drop, "minQuantity", 0),
                    JsonRead.Number(drop, "maxQuantity", 0))).ToArray()
                : [];
            var skills = JsonRead.Array(value, "skills").Select(skill =>
            {
                if (skill.ValueKind != JsonValueKind.Object)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"怪物 {id} 的技能槽配置无效。");
                var abilityId = JsonRead.Text(skill, "abilityId", "");
                if (!abilities.TryGetValue(abilityId, out var ability))
                    throw new DomainException(DomainErrorCode.NotFound, $"怪物 {id} 引用了不存在的技能 {abilityId}。");
                // 触发门槛解析顺序：槽位 triggers → triggerHpRatio 简写折叠 → 配表 defaultTriggers → 恒可用。
                IReadOnlyList<JsonElement> triggers;
                if (skill.TryGetProperty("triggers", out var slotTriggers) && slotTriggers.ValueKind == JsonValueKind.Array)
                {
                    var parsed = slotTriggers.EnumerateArray().Select(trigger => trigger.Clone()).ToArray();
                    foreach (var trigger in parsed) Domain.Combat.SkillTriggerEvaluator.ValidateTrigger(trigger);
                    triggers = parsed;
                }
                else if (skill.TryGetProperty("triggerHpRatio", out var ratioElement) && ratioElement.TryGetDouble(out var ratio))
                {
                    if (!double.IsFinite(ratio) || ratio < 0)
                        throw new DomainException(DomainErrorCode.InvalidArgument, $"怪物 {id} 的技能气血门槛无效。");
                    triggers = [HpRatioTrigger(ratio)];
                }
                else if (ability.TryGetProperty("defaultTriggers", out var defaults) && defaults.ValueKind == JsonValueKind.Array)
                {
                    triggers = defaults.EnumerateArray().Select(trigger => trigger.Clone()).ToArray();
                }
                else
                {
                    triggers = [];
                }
                return new SkillSlot(ability.Clone(), JsonRead.Number(skill, "level", 1), triggers);
            }).ToArray();
            // modifiers 段直接声明百分比点数（100 点 = 100%）：增减伤、速度与特殊修饰全单位同构。
            var modifiers = value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("modifiers", out var modifiersValue)
                && modifiersValue.ValueKind == JsonValueKind.Object ? modifiersValue : default;
            double Modifier(string key) => modifiers.ValueKind == JsonValueKind.Object ? JsonRead.Number(modifiers, key) : 0;
            var stats = CombatantStats.Create(
                anchor.Hp * hpMultiplier * Weight("hp"),
                ad,
                anchor.Armor * attrMultiplier * Weight("armor"),
                interval,
                anchor.Acc * attrMultiplier * Weight("hit"),
                anchor.Eva * attrMultiplier * Weight("eva") * 0.5,
                CombatDamageKind.Physical) with
            {
                SpellAttack = ap,
                MaxManapoints = anchor.Mp * attrMultiplier * Weight("mp"),
                MagicResistance = anchor.Mr * attrMultiplier * Weight("mres"),
                ResistanceScale = tier,
                Critical = anchor.Crit * attrMultiplier * Weight("crit"),
                CriticalResistance = anchor.Cavoid * attrMultiplier * Weight("cavoid") * 0.5,
                // 暴击伤害 = 基础 50 点 + 境界成长 + 配表加点，与玩家同池（倍率 = 1+点数/100）。
                CriticalDamagePoints = CombatGrowthCurve.CriticalDamagePoints(level) + Modifier("criticalDamageBonusPoints"),
                BreakPower = anchor.BreakPt * attrMultiplier * Weight("break"),
                ParryResolve = anchor.Parry * attrMultiplier * Weight("parry"),
                Element = Enum.Parse<CombatElement>(JsonRead.Text(value, "element", "none"), true),
                PhysicalDamageBonusPoints = Modifier("physicalDamageBonusPoints"),
                SpellDamageBonusPoints = Modifier("spellDamageBonusPoints"),
                MetalDamageBonusPoints = Modifier("metalDamageBonusPoints"),
                WoodDamageBonusPoints = Modifier("woodDamageBonusPoints"),
                WaterDamageBonusPoints = Modifier("waterDamageBonusPoints"),
                FireDamageBonusPoints = Modifier("fireDamageBonusPoints"),
                EarthDamageBonusPoints = Modifier("earthDamageBonusPoints"),
                TotalDamageBonusPoints = Modifier("totalDamageBonusPoints"),
                BasicAttackBonusPoints = Modifier("basicAttackBonusPoints") + CombatGrowthCurve.BasicAttackBonusPoints(level),
                SkillBonusPoints = Modifier("skillBonusPoints") + CombatGrowthCurve.SkillBonusPoints(tier),
                PhysicalDamageReductionPoints = Modifier("physicalDamageReductionPoints"),
                SpellDamageReductionPoints = Modifier("spellDamageReductionPoints"),
                MetalDamageReductionPoints = Modifier("metalDamageReductionPoints"),
                WoodDamageReductionPoints = Modifier("woodDamageReductionPoints"),
                WaterDamageReductionPoints = Modifier("waterDamageReductionPoints"),
                FireDamageReductionPoints = Modifier("fireDamageReductionPoints"),
                EarthDamageReductionPoints = Modifier("earthDamageReductionPoints"),
                DamageAbsorptionPoints = Modifier("damageAbsorptionPoints"),
                FixedDamageBonus = Modifier("fixedDamageBonus"),
                FixedDamageReduction = Modifier("fixedDamageReduction"),
                FixedPhysicalDamageBonus = Modifier("fixedPhysicalDamageBonus"),
                FixedPhysicalDamageReduction = Modifier("fixedPhysicalDamageReduction"),
                FixedSpellDamageBonus = Modifier("fixedSpellDamageBonus"),
                FixedSpellDamageReduction = Modifier("fixedSpellDamageReduction"),
                AttackSpeedPoints = Modifier("attackSpeedPoints") + CombatGrowthCurve.AttackSpeedPoints(level),
                ActionSpeedPoints = Modifier("actionSpeedPoints") + CombatGrowthCurve.ActionSpeedPoints(level),
                CooldownReductionPoints = Modifier("cooldownReductionPoints") + CombatGrowthCurve.CooldownReductionPoints(level),
                ThreatBonusPoints = Modifier("threatBonusPoints"),
                // 反应效果参数包：配表直接声明汲取/反弹/治疗加成/受击涨威胁点数，由钩子管线在结算中读取。
                Effects = new CombatEffectProfile(
                    JsonRead.Number(value, "hpDrainPoints"),
                    JsonRead.Number(value, "mpDrainPoints"),
                    JsonRead.Number(value, "damageReflectPoints"),
                    JsonRead.Number(value, "healingBonusPoints"),
                    JsonRead.Number(value, "threatOnTakenPoints")),
                // 战斗经验不入配表：怪物按配表 level 取「升到该级的累计修为」派生（总纲 §7.7.6）。
                CombatExperience = realmProgression?.CumulativeExpFor(level) ?? 0
            };
            // 击杀奖励单参数：experience 同时喂修为结算（瓶颈/底蕴/战斗经验）与六维入账（总纲 §7.11）。
            // 配表显式声明时生效；缺省按境界曲线派生 expToNext[level]/1000×0.9^(level-1)×层次倍率（§7.10.3）。
            var experience = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("experience", out _)
                ? JsonRead.Number(value, "experience", 0)
                : realmProgression?.MonsterKillExperience(level, rank) ?? 0;
            if (experience < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"怪物 {id} 的击杀奖励不能为负。");
            result[id] = new CombatMonsterDefinition(id, JsonRead.Text(value, "name", id), budget, level, rank, stats, drops, skills,
                experience);
        }
        return result;
    }

    private static Dictionary<string, CombatEncounterDefinition> ParseEncounterDefinitions(
        IReadOnlyList<JsonElement> raw,
        IReadOnlyDictionary<string, CombatMonsterDefinition> monsters)
    {
        var result = new Dictionary<string, CombatEncounterDefinition>(StringComparer.Ordinal);
        foreach (var value in raw)
        {
            var id = JsonRead.Text(value, "id", "");
            var spawns = JsonRead.Array(value, "randomSpawns").Select(spawn => new CombatSpawnDefinition(
                JsonRead.Text(spawn, "enemyId", ""), JsonRead.Number(spawn, "weight", 1), JsonRead.Number(spawn, "strength", 1))).ToArray();
            foreach (var spawn in spawns)
                if (!monsters.ContainsKey(spawn.EnemyId)) throw new DomainException(DomainErrorCode.NotFound, $"遭遇 {id} 引用了不存在的怪物 {spawn.EnemyId}。");
            var searchSeconds = JsonRead.Number(value, "searchMilliseconds", 20_000) / 1_000d;
            var respawnSeconds = JsonRead.Number(value, "respawnMilliseconds", 5_000) / 1_000d;
            var playerRespawnSeconds = JsonRead.Number(value, "playerRespawnMilliseconds", 180_000) / 1_000d;
            var onPartyWipe = JsonRead.Text(value, "onPartyWipe", "respawn") switch
            {
                "respawn" => PartyWipeRule.Respawn,
                "defeat" => PartyWipeRule.Defeat,
                var other => throw new DomainException(DomainErrorCode.InvalidArgument, $"遭遇 {id} 的 onPartyWipe 取值无效：{other}。")
            };
            if (string.IsNullOrWhiteSpace(id) || spawns.Length == 0 || searchSeconds <= 0 || respawnSeconds <= 0 || playerRespawnSeconds <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"遭遇 {id} 配置无效。");
            result[id] = new CombatEncounterDefinition(
                id, JsonRead.Text(value, "name", id), JsonRead.Number(value, "minBudget", 0), JsonRead.Number(value, "maxBudget", 0),
                JsonRead.Number(value, "maxTotalStrength", JsonRead.Number(value, "maxBudget", 0)), searchSeconds, respawnSeconds,
                playerRespawnSeconds, spawns, JsonRead.StringArray(value, "eliteEnemyIds"), JsonRead.StringArray(value, "bossEnemyIds"),
                JsonRead.Integer(value, "battlesPerBoss", 0), JsonRead.Integer(value, "battlesPerElite", JsonRead.Integer(value, "eliteInterval", 5)),
                onPartyWipe);
        }
        return result;
    }

    /// <summary>把怪物技能槽的 triggerHpRatio 简写折叠为等价触发器（self 气血百分比 ≤ 阈值×100）。</summary>
    private static JsonElement HpRatioTrigger(double ratio)
        => JsonDocument.Parse(
            $"{{\"dependency\":\"self\",\"condition\":\"current-hp-percentage\",\"comparator\":\"less-than-equal\",\"value\":{(ratio * 100).ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}}}")
            .RootElement.Clone();


    private static ReadOnlyDictionary<ItemId, ItemDefinition> NormalizeItems(IReadOnlyDictionary<ItemId, ItemDefinition>? items)
    {
        var normalized = new Dictionary<ItemId, ItemDefinition>();
        if (items is null) return new ReadOnlyDictionary<ItemId, ItemDefinition>(normalized);
        foreach (var (id, definition) in items)
        {
            var consumable = definition.Consumable;
            if (id != definition.Id || !id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128 ||
                !double.IsFinite(definition.SellPrice) || definition.SellPrice < 0 ||
                consumable is not null && (
                    consumable.UsableInBars is null || consumable.UsableInBars.Count == 0
                    || consumable.UsableInBars.Any(bar => !AutoUseBar.IsKnown(bar)) ||
                    !double.IsFinite(consumable.DurationSeconds) || consumable.DurationSeconds < 0 ||
                    !double.IsFinite(consumable.CooldownSeconds) || consumable.CooldownSeconds < 0 ||
                    // durable 持续消耗品持有即生效、无时长耗竭，不要求 durationSeconds；非 durable 持续品仍需正时长。
                    consumable.IsContinuous && !consumable.Durable && consumable.DurationSeconds <= 0 ||
                    consumable.Durable && !consumable.IsContinuous ||
                    // 持续性消耗品不得配置冷却时间。
                    consumable.IsContinuous && consumable.CooldownSeconds > 0 ||
                    !double.IsFinite(consumable.CultivationMultiplier) || consumable.CultivationMultiplier <= 0 ||
                    !double.IsFinite(consumable.GatheringMultiplier) || consumable.GatheringMultiplier <= 0 ||
                    !double.IsFinite(consumable.AlchemyMultiplier) || consumable.AlchemyMultiplier <= 0 ||
                    consumable.Strike is { } strike && (
                        !double.IsFinite(strike.ManaCostRatio) || strike.ManaCostRatio <= 0 ||
                        !double.IsFinite(strike.ManaDamageRatio) || strike.ManaDamageRatio < 0 ||
                        !double.IsFinite(strike.SpellAttackRatio) || strike.SpellAttackRatio < 0 ||
                        !Enum.IsDefined(strike.Element) || consumable.IsContinuous)))
                throw new DomainException(DomainErrorCode.InvalidArgument, "物品定义无效。");
            // 消耗品使用条件与技能共用同一套触发判定词表，装载期即拒绝词表外输入。
            if (consumable?.Triggers is { Count: > 0 } triggers)
                foreach (var trigger in triggers)
                    Domain.Combat.SkillTriggerEvaluator.ValidateTrigger(trigger);
            if (!normalized.TryAdd(id, definition)) throw new DomainException(DomainErrorCode.Duplicate, $"重复物品定义：{id}。");
        }
        return new ReadOnlyDictionary<ItemId, ItemDefinition>(normalized);
    }

    private static WorldGraph BuildWorldGraph(IReadOnlyList<JsonElement> maps)
    {
        var graph = new WorldGraph();
        if (maps is null || maps.Count == 0) return graph;

        // 先全量登记节点，再解析边：允许跨图边引用另一张地图中后加载的节点。
        for (var m = 0; m < maps.Count; m++)
        {
            var map = maps[m];
            if (map.ValueKind != JsonValueKind.Object) continue;

            if (map.TryGetProperty("nodes", out var nodesElement) && nodesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in nodesElement.EnumerateArray())
                {
                    if (n.ValueKind != JsonValueKind.Object) continue;
                    var id = n.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var name = n.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : id;
                    var x = JsonRead.Number(n, "x");
                    var y = JsonRead.Number(n, "y");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    if (!graph.ContainsNode(id))
                    {
                        var encounterIds = n.TryGetProperty("encounterIds", out var encProp) && encProp.ValueKind == JsonValueKind.Array
                            ? encProp.EnumerateArray()
                                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                                .Where(e => !string.IsNullOrWhiteSpace(e))
                                .Select(e => e!.Trim())
                                .ToArray()
                            : [];
                        var hidden = n.TryGetProperty("hidden", out var hiddenProp) && hiddenProp.ValueKind == JsonValueKind.True;
                        var requiredConsciousness = 0.0;
                        if (n.TryGetProperty("requirements", out var reqProp) && reqProp.ValueKind == JsonValueKind.Object
                            && reqProp.TryGetProperty("consciousness", out var conProp) && conProp.TryGetDouble(out var conVal))
                        {
                            requiredConsciousness = conVal;
                        }
                        graph.AddNode(WorldNode.Create(id, name, x, y, encounterIds, hidden, requiredConsciousness));
                    }
                }
            }
        }

        for (var m = 0; m < maps.Count; m++)
        {
            var map = maps[m];
            if (map.ValueKind != JsonValueKind.Object) continue;

            if (map.TryGetProperty("edges", out var edgesElement) && edgesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in edgesElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var id = e.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var from = e.TryGetProperty("fromNodeId", out var fromProp) ? fromProp.GetString() : null;
                    var to = e.TryGetProperty("toNodeId", out var toProp) ? toProp.GetString() : null;
                    var duration = JsonRead.Number(e, "durationSeconds", 1);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;
                    if (duration <= 0) duration = 1;

                    var distance = duration;
                    if (graph.TryGetNode(from, out var fromNode) && graph.TryGetNode(to, out var toNode))
                    {
                        var dx = toNode.X - fromNode.X;
                        var dy = toNode.Y - fromNode.Y;
                        var euc = Math.Sqrt(dx * dx + dy * dy);
                        if (euc > 0 && double.IsFinite(euc)) distance = euc;
                    }

                    if (!graph.ContainsEdge(id))
                    {
                        graph.AddUndirectedEdge(WorldEdge.Create(id, from, to, distance, duration));
                    }
                }
            }
        }

        return graph;
    }

    /// <summary>
    /// 装配期解析行动目录：依据通用基准经验公式将声明了百分比（Ratio）的收益推导为确定性点数（Amount）。
    /// 单轮 100% 基准经验 = BaseExperienceForLevel(行动等级) × 基础时长秒 / 10。
    /// </summary>
    private static ActionCatalog ResolveActionCatalog(
        ActionCatalog actions,
        IReadOnlyDictionary<string, JsonElement> rawItems,
        IReadOnlyDictionary<string, JsonElement> rawEquipments,
        RealmProgressionCatalog? realmProgression)
    {
        var resolved = new List<ActionDefinition>(actions.All.Count);
        foreach (var action in actions.All)
        {
            var actionLevel = action.Level ?? CraftingRecipeRules.ResolveRequiredRealmLevel(action, rawItems, rawEquipments);
            var baseExp = realmProgression?.BaseExperienceForLevel(actionLevel) ?? 10.0;
            var repeatBaseExp = baseExp * (action.DurationSeconds / 10.0);
            var secondBaseExp = baseExp / 10.0;

            var needsResolution = action.Yields.Any(y => y.Ratio is not null);
            List<ActionYield>? updatedYields = null;
            if (needsResolution)
            {
                updatedYields = new List<ActionYield>(action.Yields.Count);
                foreach (var yield in action.Yields)
                {
                    if (yield.Ratio is { } ratio)
                    {
                        var baseAmount = yield.Period == ActionYieldPeriod.PerSecond ? secondBaseExp : repeatBaseExp;
                        var computedAmount = Math.Max(1.0, Math.Round(baseAmount * ratio));
                        updatedYields.Add(yield with { Amount = computedAmount });
                    }
                    else
                    {
                        updatedYields.Add(yield);
                    }
                }
            }
            resolved.Add(action with
            {
                Level = actionLevel,
                Yields = updatedYields ?? action.Yields
            });
        }
        return new ActionCatalog(resolved);
    }
}
