using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    Travel = 4
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
    string? CombatEncounterId = null)
{
}

/// <summary>
/// 行动收益类别。收益入账通道由类别唯一决定，与行动所属 ActionKind 无耦合。
/// </summary>
public enum ActionYieldKind : byte
{
    /// <summary>修为：境界推进资源，累加 Progression.Qi 与 LifetimeQi</summary>
    Qi = 0,
    /// <summary>灵力：战斗资源，累加 Combat.Manapoints 并按上限截断</summary>
    Mana = 1,
    /// <summary>气血：累加 Combat.Hitpoints 并按上限截断</summary>
    Hitpoints = 2,
    /// <summary>历练经验：累加 Progression.Experience</summary>
    Experience = 3,
    /// <summary>物品：按 Item 进入资产流水</summary>
    Item = 4,
    /// <summary>Buff：按 Buff 授权规格写入 Buffs.Active</summary>
    Buff = 5
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
/// Buff 收益的授权规格，字段含义与 ActiveBuff 修饰器一致。
/// </summary>
public sealed record ActionBuffGrant(
    string BuffId,
    string Name,
    double DurationSeconds,
    double HpRegen = 0,
    double MpRegen = 0,
    double AttackSpeedRatio = 0,
    double DamageAbsorptionRatio = 0,
    IReadOnlyDictionary<string, double>? StatRatios = null);

/// <summary>
/// 单条行动收益声明：Kind 决定入账通道，Period 决定按秒或按轮结算。
/// Item 类别必须携带合法 ItemId；Buff 类别必须携带完整授权规格，Amount 表示叠加层数。
/// </summary>
public sealed record ActionYield(
    ActionYieldKind Kind,
    ActionYieldPeriod Period,
    double Amount,
    ItemId? Item = null,
    ActionBuffGrant? Buff = null);

/// <summary>持续/自动消耗品对各行动类型提供的权威效果定义。</summary>
public sealed record ConsumableDefinition(
    IReadOnlySet<ActionKind> UsableInActionKinds,
    double DurationSeconds = 0,
    bool IsContinuous = false,
    double CultivationMultiplier = 1,
    double GatheringMultiplier = 1,
    double AlchemyMultiplier = 1)
{
    /// <summary>
    /// 执行 获取倍率 操作。
    /// </summary>
    /// <param name="kind">kind 参数。</param>
    /// <returns>返回处理结果。</returns>
    public double GetMultiplier(ActionKind kind) => kind switch
    {
        ActionKind.Cultivation => CultivationMultiplier,
        ActionKind.Exploration => GatheringMultiplier,
        ActionKind.Alchemy => AlchemyMultiplier,
        _ => 1d
    };
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
    ConsumableDefinition? Consumable = null)
{
}

/// <summary>纯内存行动目录，运行时可由配置载入；不依赖数据库或宿主。</summary>
public sealed class ActionCatalog
{
    private readonly IReadOnlyDictionary<ActionId, ActionDefinition> _definitions;

    /// <summary>
    /// 初始化 行动目录。
    /// </summary>
    /// <param name="definitions">definitions 参数。</param>
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
                    : new HashSet<string>(definition.AllowedNodeIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal)
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
            new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1.0),
            new ActionYield(ActionYieldKind.Mana, ActionYieldPeriod.PerRepeat, 1.0),
            new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10.0)
        ]),
        new(new ActionId("explore.gather-ningxue-grass"), "采摘凝血草", ActionKind.Exploration, 8, new Dictionary<ItemId, double>(),
        [
            new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 3, new ItemId("item.ningxue-grass"))
        ]),
        new(new ActionId("alchemy.refine-qi-pill"), "炼制引气丹", ActionKind.Alchemy, 12, new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 },
        [
            new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, new ItemId("item.qi-gathering-pill"))
        ]),
        new(new ActionId("world.travel"), "赶路", ActionKind.Travel, 1, new Dictionary<ItemId, double>(), [], null, true, ActionExecutionKind.Travel)
    ]);

    private static void Validate(ActionDefinition definition)
    {
        if (!definition.Id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动定义无效。");
        if (!double.IsFinite(definition.DurationSeconds) || definition.DurationSeconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的数值无效。");
        if (!Enum.IsDefined(definition.ExecutionKind)
            || (definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && string.IsNullOrWhiteSpace(definition.CombatEncounterId)))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的执行元数据无效。");
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
            if (!Enum.IsDefined(yield.Kind) || !Enum.IsDefined(yield.Period)
                || !double.IsFinite(yield.Amount) || yield.Amount <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益数值无效。");
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
                        && double.IsFinite(grant.HpRegen) && double.IsFinite(grant.MpRegen)
                        && double.IsFinite(grant.AttackSpeedRatio) && double.IsFinite(grant.DamageAbsorptionRatio)
                    ? $"buff:{grant.BuffId}"
                    : throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的 Buff 收益必须按轮结算并携带完整授权规格。"),
                _ => yield.Item is null && yield.Buff is null
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
    /// <summary>当前Schema版本 常量。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 初始化 GameData目录。
    /// </summary>
    /// <param name="contentVersion">contentVersion 参数。</param>
    /// <param name="fingerprint">fingerprint 参数。</param>
    /// <param name="actions">actions 参数。</param>
    /// <param name="items">items 参数。</param>
    /// <param name="maps">maps 参数。</param>
    /// <param name="encounters">encounters 参数。</param>
    /// <param name="monsters">monsters 参数。</param>
    /// <param name="rawItems">rawItems 参数。</param>
    /// <param name="rawEquipments">rawEquipments 参数。</param>
    /// <param name="combatMonsters">combatMonsters 参数。</param>
    /// <param name="combatEncounters">combatEncounters 参数。</param>
    /// <param name="worldGraph">worldGraph 参数。</param>
    /// <param name="baseAttributes">baseAttributes 参数。</param>
    /// <param name="abilities">abilities 参数。</param>
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
        IReadOnlyDictionary<string, JsonElement>? abilities = null)
    {
        if (string.IsNullOrWhiteSpace(contentVersion)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容版本不能为空。");
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容指纹不能为空。");
        ContentVersion = contentVersion;
        Fingerprint = fingerprint;
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
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
        CombatMonsters = combatMonsters ?? ParseMonsterDefinitions(Monsters, baseAttributes, Abilities);
        CombatEncounters = combatEncounters ?? ParseEncounterDefinitions(Encounters, CombatMonsters);
        WorldGraph = worldGraph ?? BuildWorldGraph(Maps);
        BaseAttributes = baseAttributes;
        foreach (var action in Actions.All)
        {
            if (action.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (string.IsNullOrWhiteSpace(action.CombatEncounterId)
                    || !CombatEncounters.ContainsKey(action.CombatEncounterId)))
                throw new DomainException(DomainErrorCode.NotFound, $"行动 {action.Id} 关联的遭遇不存在。");
        }
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
    /// <summary>获取或设置 基础属性 的值。</summary>
    public BaseAttributeCatalog? BaseAttributes { get; }
    public IReadOnlyDictionary<string, JsonElement> Abilities { get; }

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
        BaseAttributeCatalog? baseAttributes = null;
        var contentVersion = "unknown";
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in jsonDocuments)
        {
            digest.AppendData(bytes.Span);
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("baseAttributeAnchors", out var anchors))
                baseAttributes = BaseAttributeCatalog.Parse(anchors);
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
                if (id.StartsWith("item.", StringComparison.Ordinal))
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
                else if (entry.TryGetProperty("durationSeconds", out _))
                {
                    actions.Add(ParseAction(entry, id));
                }
            }
        }
        var fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()[..16];
        return new GameDataCatalog(contentVersion, fingerprint, actions.Count == 0 ? ActionCatalog.Default : new ActionCatalog(actions), items, maps, encounters, monsters, rawItems, rawEquipments, baseAttributes: baseAttributes, abilities: abilities);
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
        var duration = entry.TryGetProperty("durationSeconds", out var durationProperty) ? durationProperty.GetDouble() : 0;
        var hiddenFromCatalog = entry.TryGetProperty("hiddenFromCatalog", out var hiddenProperty)
            && hiddenProperty.ValueKind == JsonValueKind.True;
        var executionKind = ParseExecutionKind(entry, kind);
        var combatEncounterId = entry.TryGetProperty("combatEncounterId", out var encounter)
            && encounter.ValueKind == JsonValueKind.String
            ? encounter.GetString()
            : null;
        return new ActionDefinition(new ActionId(id), entry.TryGetProperty("name", out var name) ? name.GetString() ?? id : id, kind, duration, inputs, yields, nodes, hiddenFromCatalog, executionKind, combatEncounterId);
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
                "qi" => ActionYieldKind.Qi,
                "mana" => ActionYieldKind.Mana,
                "hitpoints" => ActionYieldKind.Hitpoints,
                "experience" => ActionYieldKind.Experience,
                "item" => ActionYieldKind.Item,
                "buff" => ActionYieldKind.Buff,
                _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益类型无效：{kindText ?? "缺失"}。")
            };
            var perText = element.TryGetProperty("per", out var perProperty) ? perProperty.GetString() : null;
            var period = perText?.ToLowerInvariant() switch
            {
                "second" => ActionYieldPeriod.PerSecond,
                "repeat" => ActionYieldPeriod.PerRepeat,
                _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {actionId} 的收益结算频率无效：{perText ?? "缺失"}。")
            };
            var amount = element.TryGetProperty("amount", out var amountProperty) && amountProperty.TryGetDouble(out var parsed) ? parsed : 0;
            if (kind == ActionYieldKind.Item)
            {
                var itemId = element.TryGetProperty("id", out var itemIdProperty) ? itemIdProperty.GetString() : null;
                yields.Add(new(kind, period, amount, new ItemId(itemId ?? string.Empty)));
                continue;
            }
            if (kind == ActionYieldKind.Buff)
            {
                var buffId = element.TryGetProperty("id", out var buffIdProperty) ? buffIdProperty.GetString() : null;
                var buffName = element.TryGetProperty("name", out var buffNameProperty) ? buffNameProperty.GetString() : null;
                var durationSeconds = ReadDouble(element, "durationSeconds");
                double ReadModifier(string key) => element.TryGetProperty("modifiers", out var modifiers)
                    && modifiers.ValueKind == JsonValueKind.Object
                    && modifiers.TryGetProperty(key, out var value) && value.TryGetDouble(out var number)
                    ? number : 0;
                IReadOnlyDictionary<string, double>? statRatios = null;
                if (element.TryGetProperty("modifiers", out var modifierObject)
                    && modifierObject.ValueKind == JsonValueKind.Object
                    && modifierObject.TryGetProperty("statRatios", out var ratioObject)
                    && ratioObject.ValueKind == JsonValueKind.Object)
                    statRatios = ratioObject.EnumerateObject()
                        .Where(pair => pair.Value.TryGetDouble(out _))
                        .ToDictionary(pair => pair.Name, pair => pair.Value.GetDouble(), StringComparer.Ordinal);
                yields.Add(new(kind, period, amount, null, new ActionBuffGrant(
                    buffId ?? string.Empty, buffName ?? string.Empty, durationSeconds,
                    ReadModifier("hpRegen"), ReadModifier("mpRegen"),
                    ReadModifier("attackSpeedRatio"), ReadModifier("damageAbsorptionRatio"), statRatios)));
                continue;
            }
            yields.Add(new(kind, period, amount));
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
        var price = entry.TryGetProperty("sellPrice", out var priceProperty) && priceProperty.TryGetDouble(out var parsed) ? parsed : 0;
        var tradable = !entry.TryGetProperty("isTradable", out var tradableProperty) || tradableProperty.GetBoolean();
        ConsumableDefinition? consumable = null;
        if (entry.TryGetProperty("consumable", out var consumableProperty) && consumableProperty.ValueKind == JsonValueKind.Object)
        {
            var usableKinds = new HashSet<ActionKind>();
            foreach (var value in ReadStringArray(consumableProperty, "usableInActionTypes"))
                usableKinds.Add(ParseExplicitKind(value));
            consumable = new ConsumableDefinition(
                new ReadOnlySet<ActionKind>(usableKinds),
                ReadDouble(consumableProperty, "durationSeconds"),
                ReadBool(consumableProperty, "isContinuous"),
                ReadDouble(consumableProperty, "cultivationMultiplier", 1),
                ReadDouble(consumableProperty, "gatheringMultiplier", 1),
                ReadDouble(consumableProperty, "alchemyMultiplier", 1));
        }
        return new ItemDefinition(new ItemId(id), name, description, tradable, price, consumable);
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
        _ => ActionKind.Exploration
    };

    private static ActionKind ParseExplicitKind(string value) => value.ToLowerInvariant() switch
    {
        "cultivation" => ActionKind.Cultivation,
        "exploration" => ActionKind.Exploration,
        "alchemy" => ActionKind.Alchemy,
        "combat" => ActionKind.Combat,
        "travel" => ActionKind.Travel,
        _ => throw new DomainException(DomainErrorCode.InvalidArgument, $"消耗品适用行动类型无效：{value}。")
    };

    private static Dictionary<string, CombatMonsterDefinition> ParseMonsterDefinitions(
        IReadOnlyDictionary<string, JsonElement> raw, BaseAttributeCatalog? baseAttributes,
        IReadOnlyDictionary<string, JsonElement> abilities)
    {
        var result = new Dictionary<string, CombatMonsterDefinition>(StringComparer.Ordinal);
        foreach (var (id, value) in raw)
        {
            var budget = ReadDouble(value, "budgetCost", 1);
            var weights = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("weights", out var weightsValue) && weightsValue.ValueKind == JsonValueKind.Object
                ? weightsValue : default;
            double Weight(string key) => ReadDouble(weights, key, 1);
            // 怪我同构建模：以同等级玩家基准属性锚点为底座，按品阶倍率 + 倾向权重派生全部战斗属性。
            var level = ReadInt(value, "level", 1);
            var anchor = baseAttributes?.At(level)
                ?? new BaseAttributeAnchor(level, 100, 50, 10, 5, 10, 0, 1, 1, 1, 1, 1, 1, 2, 1);
            var rank = ReadString(value, "rank", "minion").ToLowerInvariant() switch
            {
                "elite" => CombatMonsterRank.Elite,
                "boss" => CombatMonsterRank.Boss,
                _ => CombatMonsterRank.Minion
            };
            var attrMultiplier = rank switch { CombatMonsterRank.Elite => 1.2, CombatMonsterRank.Boss => 1.5, _ => 0.8 };
            var hpMultiplier = rank switch { CombatMonsterRank.Elite => 5.0, CombatMonsterRank.Boss => 20.0, _ => 3.0 };
            var progress = Math.Pow((Math.Clamp((double)level, 1, 127) - 1) / 126d, 2);
            var actionFactor = 1 - 0.5 * progress;
            var spell = string.Equals(ReadString(value, "category", "physical"), "spell", StringComparison.OrdinalIgnoreCase);
            var interval = ReadDouble(value, "attackIntervalMilliseconds", 0) / 1_000d;
            // 同构普攻节奏：物理系 1.0s 基准 ×(1-ATR)×(1-专属攻速加成)，术法系灵爆 2.0s 基准 ×(1-ATR)。
            interval = interval > 0
                ? interval * actionFactor * (spell ? 1 : actionFactor)
                : spell ? 2 * actionFactor : actionFactor * actionFactor;
            var ad = anchor.Ad * attrMultiplier * Weight("ad");
            var ap = anchor.Ap * attrMultiplier * Weight("ap");
            var tier = baseAttributes?.TierAt(level) ?? 1;
            var drops = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("dropTable", out var dropValue) && dropValue.ValueKind == JsonValueKind.Array
                ? dropValue.EnumerateArray().Select(drop => new CombatDropDefinition(
                    new ItemId(ReadString(drop, "itemId")),
                    ReadDouble(drop, "dropChance", 0),
                    ReadDouble(drop, "minQuantity", 0),
                    ReadDouble(drop, "maxQuantity", 0))).ToArray()
                : [];
            var skills = ReadArray(value, "skills").Select(skill =>
            {
                var abilityId = ReadString(skill, "abilityId");
                if (!abilities.TryGetValue(abilityId, out var ability))
                    throw new DomainException(DomainErrorCode.NotFound, $"怪物 {id} 引用了不存在的技能 {abilityId}。");
                return new MonsterSkillSlot(ability.Clone(), ReadDouble(skill, "level", 1), ReadDouble(skill, "triggerHpRatio", 1));
            }).ToArray();
            var skillIntensity = 1 + 0.3 * (tier - 1);
            // 五行抗性权重按点数解释：weight × 10 写入对应抗性点数。
            var stats = CombatantStats.Create(
                anchor.Hp * hpMultiplier * Weight("hp"),
                spell ? ap : ad,
                anchor.Armor * attrMultiplier * Weight("armor"),
                interval,
                anchor.Acc * attrMultiplier * Weight("hit"),
                anchor.Eva * attrMultiplier * Weight("eva"),
                spell ? CombatDamageKind.Spell : CombatDamageKind.Physical) with
            {
                SpellAttack = ap,
                MaxManapoints = anchor.Mp * attrMultiplier * Weight("mp"),
                // 普攻倍率与玩家同构：物理系 1.0→2.0 AD，术法系灵爆 60%→258% AP。
                BasicAttackRatio = spell ? 0.6 * skillIntensity : 1 + progress,
                SkillIntensity = skillIntensity,
                MagicResistance = anchor.Mr * attrMultiplier * Weight("mres"),
                ResistanceScale = tier,
                Critical = anchor.Crit * attrMultiplier * Weight("crit"),
                CriticalResistance = anchor.Cavoid * attrMultiplier * Weight("cavoid"),
                CriticalMultiplier = 1.5 + 3.5 * progress,
                BreakPower = anchor.BreakPt * attrMultiplier * Weight("break"),
                ParryResolve = anchor.Parry * attrMultiplier * Weight("parry"),
                Element = Enum.Parse<CombatElement>(ReadString(value, "element", "none"), true),
                MetalResistance = Weight("metalResistance") is var metal && metal != 1 ? metal * 10 : 0,
                WoodResistance = Weight("woodResistance") is var wood && wood != 1 ? wood * 10 : 0,
                WaterResistance = Weight("waterResistance") is var water && water != 1 ? water * 10 : 0,
                FireResistance = Weight("fireResistance") is var fire && fire != 1 ? fire * 10 : 0,
                EarthResistance = Weight("earthResistance") is var earth && earth != 1 ? earth * 10 : 0
            };
            result[id] = new CombatMonsterDefinition(id, ReadString(value, "name", id), budget, level, rank, stats, drops, skills);
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
            var id = ReadString(value, "id");
            var spawns = ReadArray(value, "randomSpawns").Select(spawn => new CombatSpawnDefinition(
                ReadString(spawn, "enemyId"), ReadDouble(spawn, "weight", 1), ReadDouble(spawn, "strength", 1))).ToArray();
            foreach (var spawn in spawns)
                if (!monsters.ContainsKey(spawn.EnemyId)) throw new DomainException(DomainErrorCode.NotFound, $"遭遇 {id} 引用了不存在的怪物 {spawn.EnemyId}。");
            var searchSeconds = ReadDouble(value, "searchMilliseconds", 5_000) / 1_000d;
            var respawnSeconds = ReadDouble(value, "respawnMilliseconds", 5_000) / 1_000d;
            var playerRespawnSeconds = ReadDouble(value, "playerRespawnMilliseconds", 180_000) / 1_000d;
            if (string.IsNullOrWhiteSpace(id) || spawns.Length == 0 || searchSeconds <= 0 || respawnSeconds <= 0 || playerRespawnSeconds <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"遭遇 {id} 配置无效。");
            result[id] = new CombatEncounterDefinition(
                id, ReadString(value, "name", id), ReadDouble(value, "minBudget", 0), ReadDouble(value, "maxBudget", 0),
                ReadDouble(value, "maxTotalStrength", ReadDouble(value, "maxBudget", 0)), searchSeconds, respawnSeconds,
                playerRespawnSeconds, spawns, ReadStringArray(value, "eliteEnemyIds"), ReadStringArray(value, "bossEnemyIds"),
                ReadInt(value, "battlesPerBoss", 0), ReadInt(value, "battlesPerElite", ReadInt(value, "eliteInterval", 5)));
        }
        return result;
    }

    private static string ReadString(JsonElement value, string property, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback : fallback;

    private static double ReadDouble(JsonElement value, string property, double fallback = 0) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element) && element.TryGetDouble(out var parsed)
            ? parsed : fallback;

    private static bool ReadBool(JsonElement value, string property, bool fallback = false) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element)
            ? element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            }
            : fallback;

    private static int ReadInt(JsonElement value, string property, int fallback = 0) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element) && element.TryGetInt32(out var parsed)
            ? parsed : fallback;

    private static JsonElement[] ReadArray(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToArray() : [];

    private static string[] ReadStringArray(JsonElement value, string property) =>
        ReadArray(value, property).Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();

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
                    consumable.UsableInActionKinds is null || consumable.UsableInActionKinds.Any(kind => !Enum.IsDefined(kind)) ||
                    !double.IsFinite(consumable.DurationSeconds) || consumable.DurationSeconds < 0 ||
                    consumable.IsContinuous && consumable.DurationSeconds <= 0 ||
                    !double.IsFinite(consumable.CultivationMultiplier) || consumable.CultivationMultiplier <= 0 ||
                    !double.IsFinite(consumable.GatheringMultiplier) || consumable.GatheringMultiplier <= 0 ||
                    !double.IsFinite(consumable.AlchemyMultiplier) || consumable.AlchemyMultiplier <= 0))
                throw new DomainException(DomainErrorCode.InvalidArgument, "物品定义无效。");
            if (!normalized.TryAdd(id, definition)) throw new DomainException(DomainErrorCode.Duplicate, $"重复物品定义：{id}。");
        }
        return new ReadOnlyDictionary<ItemId, ItemDefinition>(normalized);
    }

    private static WorldGraph BuildWorldGraph(IReadOnlyList<JsonElement> maps)
    {
        var graph = new WorldGraph();
        if (maps is null || maps.Count == 0) return graph;

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
                    var x = n.TryGetProperty("x", out var xProp) && xProp.TryGetDouble(out var px) ? px : 0;
                    var y = n.TryGetProperty("y", out var yProp) && yProp.TryGetDouble(out var py) ? py : 0;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    if (!graph.ContainsNode(id))
                    {
                        graph.AddNode(WorldNode.Create(id, name, x, y));
                    }
                }
            }

            if (map.TryGetProperty("edges", out var edgesElement) && edgesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in edgesElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var id = e.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var from = e.TryGetProperty("fromNodeId", out var fromProp) ? fromProp.GetString() : null;
                    var to = e.TryGetProperty("toNodeId", out var toProp) ? toProp.GetString() : null;
                    var duration = e.TryGetProperty("durationSeconds", out var dProp) && dProp.TryGetDouble(out var pd) ? pd : 1;
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
}
