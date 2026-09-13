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
/// 包含单轮时长、材料消耗、产物掉落、持续灵力收益与单轮完成经验。
/// </summary>
public sealed record ActionDefinition(
    ActionId Id,
    string Name,
    ActionKind Kind,
    double DurationSeconds,
    IReadOnlyDictionary<ItemId, double> Inputs,
    IReadOnlyDictionary<ItemId, double> Outputs,
    double QiPerSecond = 0,
    double ExperiencePerRepeat = 0,
    IReadOnlySet<string>? AllowedNodeIds = null,
    bool HiddenFromCatalog = false,
    ActionExecutionKind ExecutionKind = ActionExecutionKind.Timed,
    string? CombatEncounterId = null)
{
}

/// <summary>持续/自动消耗品对各行动类型提供的权威效果定义。</summary>
public sealed record ConsumableDefinition(
    IReadOnlySet<ActionKind> UsableInActionKinds,
    double DurationSeconds = 0,
    bool IsContinuous = false,
    double CultivationMultiplier = 1,
    double GatheringMultiplier = 1,
    double AlchemyMultiplier = 1)
{
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
                Outputs = NormalizeQuantities(definition.Outputs, "输出"),
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
        new(new ActionId("cultivation.draw-qi"), "引气入体", ActionKind.Cultivation, 10, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1.0, 10.0),
        new(new ActionId("explore.gather-ningxue-grass"), "采摘凝血草", ActionKind.Exploration, 8, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 }),
        new(new ActionId("alchemy.refine-qi-pill"), "炼制引气丹", ActionKind.Alchemy, 12, new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 }, new Dictionary<ItemId, double> { [new ItemId("item.qi-gathering-pill")] = 1 }),
        new(new ActionId("world.travel"), "赶路", ActionKind.Travel, 1, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 0, 0, null, true, ActionExecutionKind.Travel)
    ]);

    private static void Validate(ActionDefinition definition)
    {
        if (!definition.Id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动定义无效。");
        if (!double.IsFinite(definition.DurationSeconds) || definition.DurationSeconds < 0 ||
            !double.IsFinite(definition.QiPerSecond) || definition.QiPerSecond < 0 ||
            !double.IsFinite(definition.ExperiencePerRepeat) || definition.ExperiencePerRepeat < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的数值无效。");
        if (!Enum.IsDefined(definition.ExecutionKind)
            || (definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && string.IsNullOrWhiteSpace(definition.CombatEncounterId)))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的执行元数据无效。");
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
    public const int CurrentSchemaVersion = 1;

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
        WorldGraph? worldGraph = null)
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
        CombatMonsters = combatMonsters ?? ParseMonsterDefinitions(Monsters);
        CombatEncounters = combatEncounters ?? ParseEncounterDefinitions(Encounters, CombatMonsters);
        WorldGraph = worldGraph ?? BuildWorldGraph(Maps);
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
        var contentVersion = "unknown";
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in jsonDocuments)
        {
            digest.AppendData(bytes.Span);
            using var document = JsonDocument.Parse(bytes.ToArray());
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
        return new GameDataCatalog(contentVersion, fingerprint, actions.Count == 0 ? ActionCatalog.Default : new ActionCatalog(actions), items, maps, encounters, monsters, rawItems, rawEquipments);
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
        var outputs = ParseQuantities(entry, "outputs");
        var nodes = entry.TryGetProperty("availableNodeIds", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array
            ? new HashSet<string>(nodeArray.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0), StringComparer.Ordinal)
            : null;
        var duration = entry.TryGetProperty("durationSeconds", out var durationProperty) ? durationProperty.GetDouble() : 0;
        var qiPerSecond = entry.TryGetProperty("qiPerSecond", out var qiProperty)
            ? qiProperty.GetDouble()
            : kind == ActionKind.Cultivation ? 1 : 0;
        var experiencePerRepeat = entry.TryGetProperty("experiencePerRepeat", out var experienceProperty)
            ? experienceProperty.GetDouble()
            : kind == ActionKind.Cultivation ? 10 : 0;
        var hiddenFromCatalog = entry.TryGetProperty("hiddenFromCatalog", out var hiddenProperty)
            && hiddenProperty.ValueKind == JsonValueKind.True;
        var executionKind = ParseExecutionKind(entry, kind);
        var combatEncounterId = entry.TryGetProperty("combatEncounterId", out var encounter)
            && encounter.ValueKind == JsonValueKind.String
            ? encounter.GetString()
            : null;
        return new ActionDefinition(new ActionId(id), entry.TryGetProperty("name", out var name) ? name.GetString() ?? id : id, kind, duration, inputs, outputs, qiPerSecond, experiencePerRepeat, nodes, hiddenFromCatalog, executionKind, combatEncounterId);
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

    private static Dictionary<string, CombatMonsterDefinition> ParseMonsterDefinitions(IReadOnlyDictionary<string, JsonElement> raw)
    {
        var result = new Dictionary<string, CombatMonsterDefinition>(StringComparer.Ordinal);
        foreach (var (id, value) in raw)
        {
            var budget = ReadDouble(value, "budgetCost", 1);
            var weights = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("weights", out var weightsValue) && weightsValue.ValueKind == JsonValueKind.Object
                ? weightsValue : default;
            var hpWeight = ReadDouble(weights, "hp", 1);
            var attackWeight = ReadDouble(weights, "ad", 1);
            var armorWeight = ReadDouble(weights, "armor", 1);
            var maxHitpoints = ReadDouble(value, "maxHitpoints", budget * 10 * hpWeight);
            var attackPower = ReadDouble(value, "maxDamage", budget * attackWeight);
            var defense = ReadDouble(value, "armor", budget * 0.1 * armorWeight);
            var interval = ReadDouble(value, "attackIntervalMilliseconds", 2_000) / 1_000d;
            var accuracy = ReadDouble(value, "accuracyRating", ReadDouble(weights, "hit", 1));
            var evasion = ReadDouble(value, "evasionRating", ReadDouble(weights, "eva", 0));
            var damageKind = string.Equals(ReadString(value, "category", "physical"), "spell", StringComparison.OrdinalIgnoreCase)
                ? CombatDamageKind.Spell : CombatDamageKind.Physical;
            var drops = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("dropTable", out var dropValue) && dropValue.ValueKind == JsonValueKind.Array
                ? dropValue.EnumerateArray().Select(drop => new CombatDropDefinition(
                    new ItemId(ReadString(drop, "itemId")),
                    ReadDouble(drop, "dropChance", 0),
                    ReadDouble(drop, "minQuantity", 0),
                    ReadDouble(drop, "maxQuantity", 0))).ToArray()
                : [];
            var stats = CombatantStats.Create(maxHitpoints, attackPower, defense, interval, accuracy, evasion, damageKind);
            result[id] = new CombatMonsterDefinition(id, ReadString(value, "name", id), budget, stats, drops);
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
            var playerRespawnSeconds = ReadDouble(value, "playerRespawnMilliseconds", 5_000) / 1_000d;
            if (string.IsNullOrWhiteSpace(id) || spawns.Length == 0 || searchSeconds <= 0 || respawnSeconds <= 0 || playerRespawnSeconds <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"遭遇 {id} 配置无效。");
            result[id] = new CombatEncounterDefinition(
                id, ReadString(value, "name", id), ReadDouble(value, "minBudget", 0), ReadDouble(value, "maxBudget", 0),
                ReadDouble(value, "maxTotalStrength", ReadDouble(value, "maxBudget", 0)), searchSeconds, respawnSeconds,
                playerRespawnSeconds, spawns, ReadStringArray(value, "eliteEnemyIds"), ReadStringArray(value, "bossEnemyIds"),
                ReadInt(value, "battlesPerBoss", 0));
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
