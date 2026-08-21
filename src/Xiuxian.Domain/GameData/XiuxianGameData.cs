using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.GameData;

public sealed record XiuxianGameDataManifest(
    int SchemaVersion,
    string ContentVersion);

public sealed record XiuxianMonsterSkillConfig(
    string AbilityId,
    int Level = 1,
    double TriggerHpRatio = 1.0);

public sealed record XiuxianCombatantConfig(
    string Id,
    string Name,
    XiuxianCharacterVisualDefinition? Visual = null,
    XiuxianCombatStyle Style = XiuxianCombatStyle.Body,
    XiuxianDamageType DamageType = XiuxianDamageType.Physical,
    int Level = 1,
    int Tier = 1,
    string? Rank = "minion",
    IReadOnlyDictionary<string, double>? Weights = null,
    long? MaxHitpoints = null,
    long? MaxManapoints = null,
    int AttackIntervalMilliseconds = 1000,
    double? AccuracyRating = null,
    double? EvasionRating = null,
    long? MaxDamage = null,
    long? DefensiveMaxDamage = null,
    double? Armor = null,
    double? WaterResistance = null,
    double? NatureResistance = null,
    double? FireResistance = null,
    double Tenacity = 0,
    double PierceChance = 0,
    double CurseRatio = 0,
    double FuryRatio = 0,
    double WeakenRatio = 0,
    double LifeStealRatio = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0,
    double CriticalChance = 0,
    double CriticalDamageRatio = 0,
    double CriticalAvoidance = 0,
    double DamageAbsorptionRatio = 0,
    double HpRegenPerTenSeconds = 0.0,
    double MpRegenPerTenSeconds = 0.0,
    int EnrageTimeMilliseconds = 0,
    long Experience = 0,
    long SpiritMin = 0,
    long SpiritMax = 0,
    IReadOnlyList<XiuxianCombatDropDefinition>? DropTable = null,
    IReadOnlyList<XiuxianCombatDropDefinition>? RareDropTable = null,
    IReadOnlyList<XiuxianMonsterSkillConfig>? Skills = null,
    IReadOnlyList<string>? AbilityIds = null,
    double BreakRating = 0,
    double ParryRating = 0,
    double SkillIntensity = 1.0,
    double DynamicResistanceFactorK = 1.0,
    double ActionSpeedReductionRatio = 0,
    double CooldownReductionRatio = 0);

public sealed record XiuxianCombatSpawnConfig(
    string EnemyId,
    double Weight,
    int Strength);

public sealed record XiuxianCombatEncounterConfig(
    string Id,
    string Name,
    string EnemyId,
    IReadOnlyList<XiuxianCombatSpawnConfig> RandomSpawns,
    int MaxSpawnCount,
    int MaxTotalStrength,
    IReadOnlyList<string> BossEnemyIds,
    int BattlesPerBoss,
    int SearchMilliseconds,
    int RespawnMilliseconds,
    int PlayerRespawnMilliseconds,
    long ExperiencePerWin,
    long SpiritMinPerWin,
    long SpiritMaxPerWin,
    int EnrageTimeMilliseconds,
    int EnrageTickMilliseconds,
    int MaxEnrageStacks,
    double EnrageRatioPerStack,
    IReadOnlyList<XiuxianCombatDropDefinition> DropTable,
    IReadOnlyList<XiuxianCombatDropDefinition> RareDropTable,
    string? DropItemId,
    double DropChance,
    long DropQuantity);

public sealed class XiuxianGameDataSnapshot
{
    internal XiuxianGameDataSnapshot(
        string contentVersion,
        string fingerprint,
        IReadOnlyDictionary<string, XiuxianActionDefinition> actions,
        IReadOnlyDictionary<string, XiuxianItemDefinition> items,
        IReadOnlyDictionary<string, XiuxianCombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, XiuxianCombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, XiuxianCombatantDefinition> monsters,
        IReadOnlyDictionary<string, XiuxianCombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, XiuxianWorldMapDefinition> maps)
    {
        ContentVersion = contentVersion;
        Fingerprint = fingerprint;
        Actions = actions;
        Items = items;
        Abilities = abilities;
        Equipment = equipment;
        Monsters = monsters;
        Encounters = encounters;
        Maps = maps;
    }

    public string ContentVersion { get; }
    public string Fingerprint { get; }
    public IReadOnlyDictionary<string, XiuxianActionDefinition> Actions { get; }
    public IReadOnlyDictionary<string, XiuxianItemDefinition> Items { get; }
    public IReadOnlyDictionary<string, XiuxianCombatAbilityDefinition> Abilities { get; }
    public IReadOnlyDictionary<string, XiuxianCombatEquipmentDefinition> Equipment { get; }
    public IReadOnlyDictionary<string, XiuxianCombatantDefinition> Monsters { get; }
    public IReadOnlyDictionary<string, XiuxianCombatEncounterDefinition> Encounters { get; }
    public IReadOnlyDictionary<string, XiuxianWorldMapDefinition> Maps { get; }
}

public static class XiuxianGameData
{
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] FileNames =
    [
        "manifest.json",
        "actions.json",
        "items.json",
        "abilities.json",
        "equipment.json",
        "monsters.json",
        "encounters.json",
        "maps.json"
    ];

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static XiuxianGameDataSnapshot _current = LoadEmbedded();

    public static XiuxianGameDataSnapshot Current => Volatile.Read(ref _current);

    public static XiuxianGameDataSnapshot LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        var files = FileNames.ToDictionary(
            fileName => fileName,
            fileName => File.ReadAllBytes(Path.Combine(fullPath, fileName)),
            StringComparer.Ordinal);
        var snapshot = BuildSnapshot(files);
        Interlocked.Exchange(ref _current, snapshot);
        return snapshot;
    }

    private static XiuxianGameDataSnapshot LoadEmbedded()
    {
        var assembly = typeof(XiuxianGameData).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var fileName in FileNames)
        {
            var resourceName = $"Xiuxian.Domain.GameData.Defaults.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"缺少内置游戏数据资源: {resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[fileName] = memory.ToArray();
        }

        return BuildSnapshot(files);
    }

    private static XiuxianGameDataSnapshot BuildSnapshot(IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = Read<XiuxianGameDataManifest>(files, "manifest.json");
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"游戏数据结构版本不兼容: {manifest.SchemaVersion}，当前仅支持 {SupportedSchemaVersion}。");
        }

        var actions = Index(Read<XiuxianActionDefinition[]>(files, "actions.json"), entry => entry.Id, "行动");
        var itemConfigs = Index(Read<XiuxianItemConfig[]>(files, "items.json"), entry => entry.Id, "物品");
        var items = itemConfigs.ToDictionary(
            pair => pair.Key,
            pair => XiuxianDataBudgetResolver.ResolveItem(pair.Value),
            StringComparer.Ordinal);
        var abilityConfigs = Index(Read<XiuxianAbilityConfig[]>(files, "abilities.json"), entry => entry.Id, "技能");
        var abilities = abilityConfigs.ToDictionary(
            pair => pair.Key,
            pair => XiuxianDataBudgetResolver.ResolveAbility(pair.Value),
            StringComparer.Ordinal);
        var equipmentConfigs = Index(Read<XiuxianEquipmentConfig[]>(files, "equipment.json"), entry => entry.Id, "装备");
        var equipment = equipmentConfigs.ToDictionary(
            pair => pair.Key,
            pair => XiuxianDataBudgetResolver.ResolveEquipment(pair.Value),
            StringComparer.Ordinal);
        var monsterConfigs = Index(Read<XiuxianCombatantConfig[]>(files, "monsters.json"), entry => entry.Id, "怪物");
        var monsters = monsterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToCombatant(pair.Value, abilities),
            StringComparer.Ordinal);
        var encounterConfigs = Index(Read<XiuxianCombatEncounterConfig[]>(files, "encounters.json"), entry => entry.Id, "遭遇");
        var encounters = encounterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToEncounter(pair.Value, monsters),
            StringComparer.Ordinal);
        var maps = Index(Read<XiuxianWorldMapDefinition[]>(files, "maps.json"), entry => entry.Id, "地图");

        Validate(actions, items, abilities, equipment, monsters, encounters, maps);
        return new XiuxianGameDataSnapshot(
            manifest.ContentVersion,
            CalculateFingerprint(files),
            actions,
            items,
            abilities,
            equipment,
            monsters,
            encounters,
            maps);
    }

    private static XiuxianCombatantDefinition ToCombatant(
        XiuxianCombatantConfig config,
        IReadOnlyDictionary<string, XiuxianCombatAbilityDefinition> abilities)
    {
        return XiuxianDataBudgetResolver.ResolveCombatant(config, abilities);
    }

    private static XiuxianCombatEncounterDefinition ToEncounter(
        XiuxianCombatEncounterConfig config,
        IReadOnlyDictionary<string, XiuxianCombatantDefinition> monsters)
    {
        return new XiuxianCombatEncounterDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Enemy = monsters[config.EnemyId],
            RandomSpawns = config.RandomSpawns.Select(spawn => new XiuxianCombatSpawnDefinition
            {
                Enemy = monsters[spawn.EnemyId],
                Weight = spawn.Weight,
                Strength = spawn.Strength
            }).ToArray(),
            MaxSpawnCount = config.MaxSpawnCount,
            MaxTotalStrength = config.MaxTotalStrength,
            BossEnemies = config.BossEnemyIds.Select(enemyId => monsters[enemyId]).ToArray(),
            BattlesPerBoss = config.BattlesPerBoss,
            SearchMilliseconds = config.SearchMilliseconds,
            RespawnMilliseconds = config.RespawnMilliseconds,
            PlayerRespawnMilliseconds = config.PlayerRespawnMilliseconds,
            ExperiencePerWin = config.ExperiencePerWin,
            SpiritMinPerWin = config.SpiritMinPerWin,
            SpiritMaxPerWin = config.SpiritMaxPerWin,
            EnrageTimeMilliseconds = config.EnrageTimeMilliseconds,
            EnrageTickMilliseconds = config.EnrageTickMilliseconds,
            MaxEnrageStacks = config.MaxEnrageStacks,
            EnrageRatioPerStack = config.EnrageRatioPerStack,
            DropTable = config.DropTable,
            RareDropTable = config.RareDropTable,
            DropItemId = config.DropItemId,
            DropChance = config.DropChance,
            DropQuantity = config.DropQuantity
        };
    }

    private static void Validate(
        IReadOnlyDictionary<string, XiuxianActionDefinition> actions,
        IReadOnlyDictionary<string, XiuxianItemDefinition> items,
        IReadOnlyDictionary<string, XiuxianCombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, XiuxianCombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, XiuxianCombatantDefinition> monsters,
        IReadOnlyDictionary<string, XiuxianCombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, XiuxianWorldMapDefinition> maps)
    {
        RequireAny(actions, "行动");
        RequireAny(items, "物品");
        RequireAny(abilities, "技能");
        RequireAny(monsters, "怪物");
        RequireAny(encounters, "遭遇");
        RequireAny(maps, "地图");

        foreach (var action in actions.Values)
        {
            if (action.DurationSeconds < 0) throw Invalid("行动", action.Id, "durationSeconds 不能为负数");
            if (action.CombatEncounterId is { Length: > 0 } encounterId && !encounters.ContainsKey(encounterId))
            {
                throw Invalid("行动", action.Id, $"引用了未知遭遇 {encounterId}");
            }
            foreach (var itemId in action.Inputs.Keys.Concat(action.Outputs.Keys))
            {
                if (!items.ContainsKey(itemId)) throw Invalid("行动", action.Id, $"引用了未知物品 {itemId}");
            }
        }

        foreach (var monster in monsters.Values)
        {
            if (monster.MaxHitpoints <= 0) throw Invalid("怪物", monster.Id, "maxHitpoints 必须大于 0");
            if (monster.AttackIntervalMilliseconds <= 0) throw Invalid("怪物", monster.Id, "attackIntervalMilliseconds 必须大于 0");
            ValidateProbability("怪物", monster.Id, "criticalChance", monster.CriticalChance);
            ValidateProbability("怪物", monster.Id, "criticalAvoidance", monster.CriticalAvoidance);
            ValidateProbability("怪物", monster.Id, "damageAbsorptionRatio", monster.DamageAbsorptionRatio);
            ValidateNonNegativeRatio("怪物", monster.Id, "criticalDamageRatio", monster.CriticalDamageRatio);
            ValidateDrops("怪物", monster.Id, monster.DropTable.Concat(monster.RareDropTable), items);
        }

        foreach (var encounter in encounters.Values)
        {
            if (encounter.SearchMilliseconds <= 0) throw Invalid("遭遇", encounter.Id, "searchMilliseconds 必须大于 0");
            if (encounter.PlayerRespawnMilliseconds <= 0) throw Invalid("遭遇", encounter.Id, "playerRespawnMilliseconds 必须大于 0");
            if (encounter.RandomSpawns.Any(spawn => spawn.Weight <= 0 || spawn.Strength <= 0))
            {
                throw Invalid("遭遇", encounter.Id, "随机生成的 weight 和 strength 必须大于 0");
            }
            ValidateDrops("遭遇", encounter.Id, encounter.DropTable.Concat(encounter.RareDropTable), items);
            if (encounter.DropItemId is { Length: > 0 } dropItemId && !items.ContainsKey(dropItemId))
            {
                throw Invalid("遭遇", encounter.Id, $"引用了未知物品 {dropItemId}");
            }
        }

        var knownNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in maps.Values)
        {
            var nodes = Index(map.Nodes, node => node.Id, $"地图 {map.Id} 节点");
            _ = Index(map.Edges, edge => edge.Id, $"地图 {map.Id} 连线");
            foreach (var node in nodes.Values)
            {
                knownNodes.Add(node.Id);
                foreach (var actionId in node.AvailableActionIds)
                {
                    if (!actions.ContainsKey(actionId)) throw Invalid("地图节点", node.Id, $"引用了未知行动 {actionId}");
                }
                foreach (var encounterId in node.EncounterIds)
                {
                    if (!encounters.ContainsKey(encounterId)) throw Invalid("地图节点", node.Id, $"引用了未知遭遇 {encounterId}");
                }
            }
            foreach (var edge in map.Edges)
            {
                if (!nodes.ContainsKey(edge.FromNodeId) || !nodes.ContainsKey(edge.ToNodeId))
                {
                    throw Invalid("地图连线", edge.Id, "端点引用不存在");
                }
                if (edge.DurationSeconds <= 0) throw Invalid("地图连线", edge.Id, "durationSeconds 必须大于 0");
            }
        }

        foreach (var action in actions.Values)
        {
            foreach (var nodeId in action.AvailableNodeIds)
            {
                if (!knownNodes.Contains(nodeId)) throw Invalid("行动", action.Id, $"引用了未知地图节点 {nodeId}");
            }
        }

        foreach (var equipmentId in equipment.Keys)
        {
            if (!items.ContainsKey(equipmentId)) throw Invalid("装备", equipmentId, "缺少同 ID 物品定义");
            var definition = equipment[equipmentId];
            ValidateEquipmentStats(equipmentId, definition.Stats);
            ValidateEquipmentStats(equipmentId, definition.EnhancementBonus);
        }
    }

    private static void ValidateEquipmentStats(string id, XiuxianEquipmentCombatStats stats)
    {
        ValidateProbability("装备", id, "criticalChance", stats.CriticalChance);
        ValidateProbability("装备", id, "criticalAvoidance", stats.CriticalAvoidance);
        ValidateProbability("装备", id, "damageAbsorptionRatio", stats.DamageAbsorptionRatio);
        ValidateNonNegativeRatio("装备", id, "criticalDamageRatio", stats.CriticalDamageRatio);
    }

    private static void ValidateProbability(string kind, string id, string field, double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw Invalid(kind, id, $"{field} 必须在 0 到 1 之间");
        }
    }

    private static void ValidateNonNegativeRatio(string kind, string id, string field, double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 10)
        {
            throw Invalid(kind, id, $"{field} 必须在 0 到 10 之间");
        }
    }

    private static void ValidateDrops(
        string kind,
        string id,
        IEnumerable<XiuxianCombatDropDefinition> drops,
        IReadOnlyDictionary<string, XiuxianItemDefinition> items)
    {
        foreach (var drop in drops)
        {
            if (!items.ContainsKey(drop.ItemId)) throw Invalid(kind, id, $"掉落引用了未知物品 {drop.ItemId}");
            if (drop.DropChance is < 0 or > 1) throw Invalid(kind, id, "dropChance 必须在 0 到 1 之间");
            if (drop.MinQuantity <= 0 || drop.MaxQuantity < drop.MinQuantity)
            {
                throw Invalid(kind, id, "掉落数量范围无效");
            }
        }
    }

    private static IReadOnlyDictionary<string, T> Index<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException($"{kind}存在空 ID。");
            if (!result.TryAdd(key, value)) throw new InvalidDataException($"{kind}存在重复 ID: {key}");
        }
        return result;
    }

    private static void RequireAny<T>(IReadOnlyDictionary<string, T> values, string kind)
    {
        if (values.Count == 0) throw new InvalidDataException($"{kind}配置不能为空。");
    }

    private static InvalidDataException Invalid(string kind, string id, string message) =>
        new($"{kind} {id} 配置无效: {message}");

    private static T Read<T>(IReadOnlyDictionary<string, byte[]> files, string fileName)
    {
        if (!files.TryGetValue(fileName, out var bytes)) throw new InvalidDataException($"缺少游戏数据文件: {fileName}");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        ValidateNoDuplicateProperties(document.RootElement, fileName);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"游戏数据文件为空: {fileName}");
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"游戏数据存在重复字段: {path}.{property.Name}");
                }
                ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            ValidateNoDuplicateProperties(item, $"{path}[{index}]");
            index++;
        }
    }

    private static string CalculateFingerprint(IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var fileName in FileNames.OrderBy(value => value, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(fileName));
            hash.AppendData(files[fileName]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    where T : notnull
{
    public override IReadOnlySet<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<HashSet<T>>(ref reader, options)
            ?? new HashSet<T>();
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlySet<T> value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
