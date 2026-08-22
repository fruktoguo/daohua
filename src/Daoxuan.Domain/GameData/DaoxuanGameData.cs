using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.GameData;

/// <summary>
/// 游戏数据清单清单元数据。
/// </summary>
public sealed record DaoxuanGameDataManifest(
    int SchemaVersion,
    string ContentVersion);

/// <summary>
/// 敌怪技能装配配置。
/// </summary>
public sealed record DaoxuanMonsterSkillConfig(
    string AbilityId,
    int Level = 1,
    double TriggerHpRatio = 1.0);

/// <summary>
/// 战斗单位（怪物/Boss）原始数据配置传输对象。
/// </summary>
public sealed record DaoxuanCombatantConfig(
    string Id,
    string Name,
    DaoxuanCharacterVisualDefinition? Visual = null,
    DaoxuanCombatStyle Style = DaoxuanCombatStyle.Body,
    DaoxuanDamageType DamageType = DaoxuanDamageType.Physical,
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
    double? CriticalRating = null,
    double CriticalChance = 0,
    double CriticalDamageRatio = 0,
    double CriticalAvoidance = 0,
    double DamageAbsorptionRatio = 0,
    double? HpRegen = null,
    double? MpRegen = null,
    double HpRegenPerTenSeconds = 0.0,
    double MpRegenPerTenSeconds = 0.0,
    int EnrageTimeMilliseconds = 0,
    double? ExperienceMultiplier = 1.0,
    long Experience = 0,
    long SpiritMin = 0,
    long SpiritMax = 0,
    IReadOnlyList<DaoxuanCombatDropDefinition>? DropTable = null,
    IReadOnlyList<DaoxuanCombatDropDefinition>? RareDropTable = null,
    IReadOnlyList<DaoxuanMonsterSkillConfig>? Skills = null,
    double BreakRating = 0,
    double ParryRating = 0,
    double SkillIntensity = 1.0,
    double DynamicResistanceFactorK = 1.0,
    double ActionSpeedReductionRatio = 0,
    double CooldownReductionRatio = 0);

/// <summary>
/// 遭遇战随机刷怪权重配置。
/// </summary>
public sealed record DaoxuanCombatSpawnConfig(
    string EnemyId,
    double Weight,
    int Strength);

/// <summary>
/// 战斗遭遇（副本/关卡）静态配置传输对象。
/// </summary>
public sealed record DaoxuanCombatEncounterConfig(
    string Id,
    string Name,
    IReadOnlyList<DaoxuanCombatSpawnConfig> RandomSpawns,
    int MaxSpawnCount,
    int MaxTotalStrength,
    IReadOnlyList<string> BossEnemyIds,
    int BattlesPerBoss,
    int SearchMilliseconds,
    int RespawnMilliseconds,
    int PlayerRespawnMilliseconds,
    long ExperiencePerWin = 0,
    long SpiritMinPerWin = 0,
    long SpiritMaxPerWin = 0,
    int EnrageTimeMilliseconds = 180000,
    int EnrageTickMilliseconds = 60000,
    int MaxEnrageStacks = 10,
    double EnrageRatioPerStack = 0.1,
    IReadOnlyList<DaoxuanCombatDropDefinition>? DropTable = null,
    IReadOnlyList<DaoxuanCombatDropDefinition>? RareDropTable = null);

/// <summary>
/// 游戏静态只读配置全量快照聚合根，包含所有动作、物品、技能、装备、怪物、遭遇和地图定义。
/// </summary>
public sealed class DaoxuanGameDataSnapshot
{
    internal DaoxuanGameDataSnapshot(
        string contentVersion,
        string fingerprint,
        IReadOnlyDictionary<string, DaoxuanActionDefinition> actions,
        IReadOnlyDictionary<string, DaoxuanItemDefinition> items,
        IReadOnlyDictionary<string, DaoxuanCombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, DaoxuanCombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, DaoxuanCombatantDefinition> monsters,
        IReadOnlyDictionary<string, DaoxuanCombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, DaoxuanWorldMapDefinition> maps)
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

    /// <summary>内容数据版本号。</summary>
    public string ContentVersion { get; }
    /// <summary>全量数据哈希指纹（前 16 位）。</summary>
    public string Fingerprint { get; }
    /// <summary>行动原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanActionDefinition> Actions { get; }
    /// <summary>物品原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanItemDefinition> Items { get; }
    /// <summary>技能原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanCombatAbilityDefinition> Abilities { get; }
    /// <summary>装备原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanCombatEquipmentDefinition> Equipment { get; }
    /// <summary>怪物/Boss 战斗原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanCombatantDefinition> Monsters { get; }
    /// <summary>遭遇关卡原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanCombatEncounterDefinition> Encounters { get; }
    /// <summary>大世界地图原型字典。</summary>
    public IReadOnlyDictionary<string, DaoxuanWorldMapDefinition> Maps { get; }
}

/// <summary>
/// 游戏数据只读单例仓储与加载器。支持内嵌默认资源与外部目录热加载。
/// </summary>
public static class DaoxuanGameData
{
    /// <summary>支持的数据结构 Schema 版本。</summary>
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
    private static DaoxuanGameDataSnapshot _current = LoadEmbedded();

    /// <summary>获取当前生效的游戏静态数据单例快照。</summary>
    public static DaoxuanGameDataSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// 从外部指定目录读取 JSON 数据文件并原子替换当前单例快照。
    /// </summary>
    /// <param name="directory">包含全量数据 JSON 文件的目录路径。</param>
    /// <returns>构建后的新游戏数据快照。</returns>
    public static DaoxuanGameDataSnapshot LoadFromDirectory(string directory)
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


    private static DaoxuanGameDataSnapshot LoadEmbedded()
    {
        var assembly = typeof(DaoxuanGameData).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var fileName in FileNames)
        {
            var resourceName = $"Daoxuan.Domain.GameData.Defaults.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"缺少内置游戏数据资源: {resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[fileName] = memory.ToArray();
        }

        return BuildSnapshot(files);
    }

    private static DaoxuanGameDataSnapshot BuildSnapshot(IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = Read<DaoxuanGameDataManifest>(files, "manifest.json");
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"游戏数据结构版本不兼容: {manifest.SchemaVersion}，当前仅支持 {SupportedSchemaVersion}。");
        }

        var actions = Index(Read<DaoxuanActionDefinition[]>(files, "actions.json"), entry => entry.Id, "行动");
        var itemConfigs = Index(Read<DaoxuanItemConfig[]>(files, "items.json"), entry => entry.Id, "物品");
        var items = itemConfigs.ToDictionary(
            pair => pair.Key,
            pair => DaoxuanDataBudgetResolver.ResolveItem(pair.Value),
            StringComparer.Ordinal);
        var abilityConfigs = Index(Read<DaoxuanAbilityConfig[]>(files, "abilities.json"), entry => entry.Id, "技能");
        var abilities = abilityConfigs.ToDictionary(
            pair => pair.Key,
            pair => DaoxuanDataBudgetResolver.ResolveAbility(pair.Value),
            StringComparer.Ordinal);
        var equipmentConfigs = Index(Read<DaoxuanEquipmentConfig[]>(files, "equipment.json"), entry => entry.Id, "装备");
        var equipment = equipmentConfigs.ToDictionary(
            pair => pair.Key,
            pair => DaoxuanDataBudgetResolver.ResolveEquipment(pair.Value),
            StringComparer.Ordinal);
        var monsterConfigs = Index(Read<DaoxuanCombatantConfig[]>(files, "monsters.json"), entry => entry.Id, "怪物");
        var monsters = monsterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToCombatant(pair.Value, abilities),
            StringComparer.Ordinal);
        var encounterConfigs = Index(Read<DaoxuanCombatEncounterConfig[]>(files, "encounters.json"), entry => entry.Id, "遭遇");
        var encounters = encounterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToEncounter(pair.Value, monsters),
            StringComparer.Ordinal);
        var maps = Index(Read<DaoxuanWorldMapDefinition[]>(files, "maps.json"), entry => entry.Id, "地图");

        Validate(actions, items, abilities, equipment, monsters, encounters, maps);
        return new DaoxuanGameDataSnapshot(
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

    private static DaoxuanCombatantDefinition ToCombatant(
        DaoxuanCombatantConfig config,
        IReadOnlyDictionary<string, DaoxuanCombatAbilityDefinition> abilities)
    {
        return DaoxuanDataBudgetResolver.ResolveCombatant(config, abilities);
    }

    private static DaoxuanCombatEncounterDefinition ToEncounter(
        DaoxuanCombatEncounterConfig config,
        IReadOnlyDictionary<string, DaoxuanCombatantDefinition> monsters)
    {
        return new DaoxuanCombatEncounterDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Enemy = config.RandomSpawns.Count > 0
                ? monsters[config.RandomSpawns[0].EnemyId]
                : monsters[config.BossEnemyIds[0]],
            RandomSpawns = config.RandomSpawns.Select(spawn => new DaoxuanCombatSpawnDefinition
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
            DropTable = config.DropTable ?? [],
            RareDropTable = config.RareDropTable ?? []
        };
    }

    private static void Validate(
        IReadOnlyDictionary<string, DaoxuanActionDefinition> actions,
        IReadOnlyDictionary<string, DaoxuanItemDefinition> items,
        IReadOnlyDictionary<string, DaoxuanCombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, DaoxuanCombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, DaoxuanCombatantDefinition> monsters,
        IReadOnlyDictionary<string, DaoxuanCombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, DaoxuanWorldMapDefinition> maps)
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
            if (monster.CriticalAvoidance < 0 || !double.IsFinite(monster.CriticalAvoidance))
            {
                throw Invalid("怪物", monster.Id, "criticalAvoidance 不能为负数");
            }
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

    private static void ValidateEquipmentStats(string id, DaoxuanEquipmentCombatStats stats)
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
        IEnumerable<DaoxuanCombatDropDefinition> drops,
        IReadOnlyDictionary<string, DaoxuanItemDefinition> items)
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
