using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Domain.GameData;

public enum ActionKind : byte
{
    Cultivation = 0,
    Exploration = 1,
    Alchemy = 2,
    Combat = 3,
    Travel = 4
}

public sealed record ActionDefinition(
    ActionId Id,
    string Name,
    ActionKind Kind,
    double DurationSeconds,
    IReadOnlyDictionary<ItemId, double> Inputs,
    IReadOnlyDictionary<ItemId, double> Outputs,
    double QiPerSecond = 0,
    double ExperiencePerSecond = 0,
    IReadOnlySet<string>? AllowedNodeIds = null)
{
}

public sealed record ItemDefinition(
    ItemId Id,
    string Name,
    string Description,
    bool IsTradable = true,
    double SellPrice = 0)
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

    public IReadOnlyCollection<ActionDefinition> All => _definitions.Values.ToArray();
    public bool Contains(ActionId id) => _definitions.ContainsKey(id);
    public bool TryGet(ActionId id, out ActionDefinition definition) => _definitions.TryGetValue(id, out definition!);
    public ActionDefinition Get(ActionId id) => _definitions.TryGetValue(id, out var definition)
        ? definition
        : throw new DomainException(DomainErrorCode.NotFound, $"行动 {id} 不存在。");

    public static ActionCatalog Default { get; } = new(
    [
        new(new ActionId("cultivation.draw-qi"), "引气入体", ActionKind.Cultivation, 10, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1.0, 0.25),
        new(new ActionId("explore.gather-ningxue-grass"), "采摘凝血草", ActionKind.Exploration, 8, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 }),
        new(new ActionId("alchemy.refine-qi-pill"), "炼制引气丹", ActionKind.Alchemy, 12, new Dictionary<ItemId, double> { [new ItemId("item.ningxue-grass")] = 3 }, new Dictionary<ItemId, double> { [new ItemId("item.qi-gathering-pill")] = 1 })
    ]);

    private static void Validate(ActionDefinition definition)
    {
        if (!definition.Id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动定义无效。");
        if (!double.IsFinite(definition.DurationSeconds) || definition.DurationSeconds < 0 ||
            !double.IsFinite(definition.QiPerSecond) || definition.QiPerSecond < 0 ||
            !double.IsFinite(definition.ExperiencePerSecond) || definition.ExperiencePerSecond < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动 {definition.Id} 的数值无效。");
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

public sealed class GameDataCatalog
{
    public const int CurrentSchemaVersion = 1;

    public GameDataCatalog(
        string contentVersion,
        string fingerprint,
        ActionCatalog actions,
        IReadOnlyDictionary<ItemId, ItemDefinition>? items = null)
    {
        if (string.IsNullOrWhiteSpace(contentVersion)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容版本不能为空。");
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new DomainException(DomainErrorCode.InvalidArgument, "内容指纹不能为空。");
        ContentVersion = contentVersion;
        Fingerprint = fingerprint;
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        Items = NormalizeItems(items);
    }

    public string ContentVersion { get; }
    public string Fingerprint { get; }
    public ActionCatalog Actions { get; }
    public IReadOnlyDictionary<ItemId, ItemDefinition> Items { get; }

    public static GameDataCatalog BuiltIn { get; } = new("builtin-1", "builtin", ActionCatalog.Default);

    /// <summary>从纯字节资源解析最小稳定目录；文件系统、数据库均由宿主负责读取。</summary>
    public static GameDataCatalog Parse(IEnumerable<ReadOnlyMemory<byte>> jsonDocuments)
    {
        ArgumentNullException.ThrowIfNull(jsonDocuments);
        var actions = new List<ActionDefinition>();
        var items = new Dictionary<ItemId, ItemDefinition>();
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
                }
                else if (entry.TryGetProperty("durationSeconds", out _))
                {
                    actions.Add(ParseAction(entry, id));
                }
            }
        }
        var fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()[..16];
        return new GameDataCatalog(contentVersion, fingerprint, actions.Count == 0 ? ActionCatalog.Default : new ActionCatalog(actions), items);
    }

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
        return new ActionDefinition(new ActionId(id), entry.TryGetProperty("name", out var name) ? name.GetString() ?? id : id, kind, duration, inputs, outputs, kind == ActionKind.Cultivation ? 1 : 0, kind == ActionKind.Cultivation ? 0.25 : 0, nodes);
    }

    private static ItemDefinition ParseItem(JsonElement entry, string id)
    {
        var name = entry.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? id : id;
        var description = entry.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() ?? string.Empty : string.Empty;
        var price = entry.TryGetProperty("sellPrice", out var priceProperty) && priceProperty.TryGetDouble(out var parsed) ? parsed : 0;
        var tradable = !entry.TryGetProperty("isTradable", out var tradableProperty) || tradableProperty.GetBoolean();
        return new ItemDefinition(new ItemId(id), name, description, tradable, price);
    }

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

    private static ReadOnlyDictionary<ItemId, ItemDefinition> NormalizeItems(IReadOnlyDictionary<ItemId, ItemDefinition>? items)
    {
        var normalized = new Dictionary<ItemId, ItemDefinition>();
        if (items is null) return new ReadOnlyDictionary<ItemId, ItemDefinition>(normalized);
        foreach (var (id, definition) in items)
        {
            if (id != definition.Id || !id.IsValid || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128 ||
                !double.IsFinite(definition.SellPrice) || definition.SellPrice < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "物品定义无效。");
            if (!normalized.TryAdd(id, definition)) throw new DomainException(DomainErrorCode.Duplicate, $"重复物品定义：{id}。");
        }
        return new ReadOnlyDictionary<ItemId, ItemDefinition>(normalized);
    }
}
