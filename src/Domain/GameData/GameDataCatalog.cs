// ==============================================================================
// 文件名称: GameData.cs
// 主要职责: 游戏静态配置数据的只读单例仓储与热加载器，负责内嵌资源加载、外部 JSON 目录解析、校验以及构建原子替换的数据快照。
// 核心逻辑 / 定义内容:
//   1. 支持从程序集内嵌资源或外部 JSON 目录加载游戏全量静态配置
//   2. 校验配置数据的完整性与版本号（SchemaVersion），并使用解析器预处理动态数值
//   3. 提供线程安全的原子快照访问（GameDataCatalogSnapshot），维护动作、物品、技能、装备、怪物、遭遇及地图等全局索引字典
// ==============================================================================
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Constants;
using Domain.Models;

namespace Domain.GameData;


/// <summary>
/// 游戏数据只读单例仓储与加载器。支持内嵌默认资源与外部目录热加载。
/// </summary>
public static class GameDataCatalog
{
    /// <summary>支持的数据结构 Schema 版本。</summary>
    public const int SupportedSchemaVersion = GameDataCatalogConstants.SupportedSchemaVersion;

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
    private static GameDataCatalogSnapshot _current = LoadEmbedded();

    /// <summary>获取当前生效的游戏静态数据单例快照。</summary>
    public static GameDataCatalogSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// 从外部指定目录读取 JSON 数据文件并原子替换当前单例快照。
    /// </summary>
    /// <param name="directory">包含全量数据 JSON 文件的目录路径。</param>
    /// <returns>构建后的新游戏数据快照。</returns>
    public static GameDataCatalogSnapshot LoadFromDirectory(string directory)
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


    private static GameDataCatalogSnapshot LoadEmbedded()
    {
        var assembly = typeof(GameDataCatalog).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var fileName in FileNames)
        {
            var resourceName = $"Domain.GameData.Defaults.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"缺少内置游戏数据资源: {resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[fileName] = memory.ToArray();
        }

        return BuildSnapshot(files);
    }

    private static GameDataCatalogSnapshot BuildSnapshot(IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = Read<GameDataManifest>(files, "manifest.json");
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"游戏数据结构版本不兼容: {manifest.SchemaVersion}，当前仅支持 {SupportedSchemaVersion}。");
        }

        var actions = Index(Read<ActionDefinition[]>(files, "actions.json"), entry => entry.Id, "行动");
        var itemConfigs = Index(Read<ItemConfig[]>(files, "items.json"), entry => entry.Id, "物品");
        var items = itemConfigs.ToDictionary(
            pair => pair.Key,
            pair => DataBudgetResolver.ResolveItem(pair.Value),
            StringComparer.Ordinal);
        var abilityConfigs = Index(Read<AbilityConfig[]>(files, "abilities.json"), entry => entry.Id, "技能");
        var abilities = abilityConfigs.ToDictionary(
            pair => pair.Key,
            pair => DataBudgetResolver.ResolveAbility(pair.Value),
            StringComparer.Ordinal);
        var equipmentConfigs = Index(Read<EquipmentConfig[]>(files, "equipment.json"), entry => entry.Id, "装备");
        var equipment = equipmentConfigs.ToDictionary(
            pair => pair.Key,
            pair => DataBudgetResolver.ResolveEquipment(pair.Value),
            StringComparer.Ordinal);
        foreach (var eq in equipment.Values)
        {
            if (!items.ContainsKey(eq.Id))
            {
                var desc = equipmentConfigs.TryGetValue(eq.Id, out var eqCfg) && !string.IsNullOrWhiteSpace(eqCfg.Description)
                    ? eqCfg.Description
                    : ResolveEquipmentDescription(eq.Id, eq.Name);
                items[eq.Id] = new ItemDefinition
                {
                    Id = eq.Id,
                    Name = eq.Name,
                    Description = desc,
                    Kind = ItemKind.Equipment,
                    Category = ItemCategory.Equipment,
                    Tier = eq.Tier,
                    SellPrice = eq.Tier == 1 ? 50 : 150,
                    IsTradable = true,
                    Consumable = null
                };
            }
        }
        var monsterConfigs = Index(Read<CombatantConfig[]>(files, "monsters.json"), entry => entry.Id, "怪物");
        var monsters = monsterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToCombatant(pair.Value, abilities),
            StringComparer.Ordinal);
        var encounterConfigs = Index(Read<CombatEncounterConfig[]>(files, "encounters.json"), entry => entry.Id, "遭遇");
        var encounters = encounterConfigs.ToDictionary(
            pair => pair.Key,
            pair => ToEncounter(pair.Value, monsters),
            StringComparer.Ordinal);
        var maps = Index(Read<WorldMapDefinition[]>(files, "maps.json"), entry => entry.Id, "地图");

        Validate(actions, items, abilities, equipment, monsters, encounters, maps);
        return new GameDataCatalogSnapshot(
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

    private static CombatantDefinition ToCombatant(
        CombatantConfig config,
        IReadOnlyDictionary<string, CombatAbilityDefinition> abilities)
    {
        return DataBudgetResolver.ResolveCombatant(config, abilities);
    }

    private static CombatEncounterDefinition ToEncounter(
        CombatEncounterConfig config,
        IReadOnlyDictionary<string, CombatantDefinition> monsters)
    {
        var randomSpawns = config.RandomSpawns ?? [];
        var bossEnemyIds = config.BossEnemyIds ?? [];
        var firstEnemy = randomSpawns.Count > 0
            ? monsters[randomSpawns[0].EnemyId]
            : (bossEnemyIds.Count > 0 ? monsters[bossEnemyIds[0]] : null!);

        return new CombatEncounterDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Enemy = firstEnemy,
            RandomSpawns = randomSpawns.Select(spawn => new CombatSpawnDefinition
            {
                Enemy = monsters[spawn.EnemyId],
                Weight = spawn.Weight,
                Strength = spawn.Strength
            }).ToArray(),
            MaxSpawnCount = config.MaxSpawnCount ?? 3,
            MaxTotalStrength = config.MaxTotalStrength ?? 50,
            MinBudget = config.MinBudget is > 0 ? config.MinBudget.Value : 10.0,
            MaxBudget = config.MaxBudget is > 0 ? config.MaxBudget.Value : 50.0,
            EliteInterval = config.EliteInterval is > 0 ? config.EliteInterval.Value : 5,
            EliteEnemies = (config.EliteEnemyIds ?? []).Where(monsters.ContainsKey).Select(enemyId => monsters[enemyId]).ToArray(),
            BossEnemies = bossEnemyIds.Where(monsters.ContainsKey).Select(enemyId => monsters[enemyId]).ToArray(),
            BattlesPerBoss = config.BattlesPerBoss ?? 0,
            SearchMilliseconds = config.SearchMilliseconds ?? 20000,
            RespawnMilliseconds = config.RespawnMilliseconds ?? 5000,
            PlayerRespawnMilliseconds = config.PlayerRespawnMilliseconds ?? 180000,
            ExperiencePerWin = config.ExperiencePerWin ?? 0L,
            SpiritMinPerWin = config.SpiritMinPerWin ?? 0L,
            SpiritMaxPerWin = config.SpiritMaxPerWin ?? 0L,
            EnrageTimeMilliseconds = config.EnrageTimeMilliseconds ?? 180000,
            EnrageTickMilliseconds = config.EnrageTickMilliseconds ?? 60000,
            MaxEnrageStacks = config.MaxEnrageStacks ?? 10,
            EnrageRatioPerStack = config.EnrageRatioPerStack ?? 0.1,
            DropTable = config.DropTable ?? [],
            RareDropTable = config.RareDropTable ?? []
        };
    }

    private static void Validate(
        IReadOnlyDictionary<string, ActionDefinition> actions,
        IReadOnlyDictionary<string, ItemDefinition> items,
        IReadOnlyDictionary<string, CombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, CombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, CombatantDefinition> monsters,
        IReadOnlyDictionary<string, CombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, WorldMapDefinition> maps)
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

    private static void ValidateEquipmentStats(string id, EquipmentCombatStats stats)
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
        IEnumerable<CombatDropDefinition> drops,
        IReadOnlyDictionary<string, ItemDefinition> items)
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

    private static string ResolveEquipmentDescription(string id, string name) => id switch
    {
        "equipment.qingfeng-sword" => "青钢淬火锻造的三尺长剑，剑身青光如秋水澄澈。",
        "equipment.pishan-blade" => "沉重坚实的山林朴刀，刀背厚重，刀锋刃口雪亮。",
        "equipment.shufa-scarf" => "以细密麻布缝制的行脚束发头巾，简朴干练。",
        "equipment.baina-armor" => "多层坚韧粗布与兽皮拼接缝制的护身短甲，结实耐磨。",
        "equipment.zhakou-pants" => "下摆收口绑扎的武行布裤，便于翻山越岭与近身搏击。",
        "equipment.xingshan-boots" => "鹿皮底厚麻短靴，靴帮结实，行走山林落足无声。",
        "equipment.shouya-pendant" => "以凶兽獠牙穿系而成的粗犷项坠，野性十足。",
        "equipment.jiujie-staff" => "老山竹根削成的九节法杖，通体油亮光滑。",
        "equipment.suxin-zither" => "以山中老桐木制成的七弦古琴，音质清越素雅。",
        "equipment.hunyuan-scarf" => "道家修士常戴的素色道巾，轻柔透气。",
        "equipment.suma-robe" => "以粗麻纺织的宽松道袍，宽袖飘逸，不染俗尘。",
        "equipment.xingqi-pants" => "宽大舒适的练功长裤，透气吸汗，利于周天行气。",
        "equipment.shifang-shoes" => "云游道人常穿的十方布鞋，鞋底轻软舒适。",
        "equipment.taomu-talisman" => "百年阳坡桃木雕刻而成的辟邪木符，散发着草木幽香。",
        "equipment.pogang-sword" => "融入赤铜精淬炼的锋锐灵剑，剑芒吞吐，能破护体罡气。",
        "equipment.zhansha-blade" => "百炼铁融合赤铜打造的厚重大刀，刀身隐泛暗红血槽。",
        "equipment.chijin-helmet" => "赤金铜精打磨而成的全护铁盔，金光内敛，威风凛凛。",
        "equipment.yulin-armor" => "片片赤铜精锁扣编缀而成的战甲，宛如赤色龙鳞覆盖。",
        "equipment.tazhen-greaves" => "厚重的金属护腿胫甲，线条硬朗，坚不可摧。",
        "equipment.tashan-boots" => "外衬赤铜甲片的战将重靴，踏地沉稳如山。",
        "equipment.chiwen-ring" => "赤红铜精打磨而成的指环，戒面铭刻着古朴赤龙纹样。",
        "equipment.chaoyuan-staff" => "引灵松树芯雕琢的长杖，杖头盘旋如祥云聚顶。",
        "equipment.songfeng-zither" => "千年引灵松精雕而成的古琴，抚弦宛如松涛阵阵。",
        "equipment.ziwu-crown" => "灵松晶雕刻打磨的青玉道冠，玉色温润清透。",
        "equipment.pichen-shirt" => "混纺火羽与灵蚕丝织就的轻薄法衣，光洁如新，尘垢不沾。",
        "equipment.liuyun-skirt" => "如流云般轻盈飘逸的羽丝下裳，随风而动。",
        "equipment.bichen-shoes" => "灵羽为底、云丝为面的法靴，步履轻盈如踏云霄。",
        "equipment.juling-pendant" => "青翠灵松晶雕琢而成的通灵玉佩，常年萦绕着淡淡清气。",
        _ => $"修仙界流传的{name}。"
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new ItemCategoryJsonConverter());
        options.Converters.Add(new EquipmentSlotJsonConverter());
        options.Converters.Add(new UnitRankJsonConverter());
        options.Converters.Add(new VisualRendererKindJsonConverter());
        options.Converters.Add(new CombatActionKindJsonConverter());
        options.Converters.Add(new ExpeditionPhaseJsonConverter());
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
