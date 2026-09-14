using System.Text.Json;
using Domain.GameData;

namespace Server.Clean;

/// <summary>把内嵌游戏数据映射为前端稳定读取的静态目录 DTO。</summary>
public static class StaticGameCatalogMapper
{
    /// <summary>
    /// 执行Items。
    /// </summary>
    /// <param name="catalog">参数 catalog。</param>
    /// <returns>返回 IReadOnlyList&lt;object&gt; 类型的结果。</returns>
    public static IReadOnlyList<object> Items(GameDataCatalog catalog) => catalog.RawItems.Values
        .OrderBy(item => ReadString(item, "id"), StringComparer.Ordinal)
        .Select(item =>
        {
            var id = ReadString(item, "id");
            var name = ReadString(item, "name", id);
            var desc = ReadString(item, "description");
            var kind = ReadString(item, "kind", "material");
            var category = ReadString(item, "category", "material");
            var tier = ReadInt(item, "tier", 1);
            var sellPrice = ReadDouble(item, "sellPrice", 0);
            var isTradable = ReadBool(item, "isTradable", true);

            var consumable = item.TryGetProperty("consumable", out var cons) && cons.ValueKind == JsonValueKind.Object ? cons : default;
            var hasConsumable = consumable.ValueKind == JsonValueKind.Object;

            var usableActionTypes = hasConsumable ? ReadStringArray(consumable, "usableInActionTypes") : [];
            var durationSeconds = hasConsumable ? ReadDouble(consumable, "durationSeconds", 0) : 0;
            var isContinuous = hasConsumable && ReadBool(consumable, "isContinuous", false);
            var cultivationMultiplier = hasConsumable ? ReadDouble(consumable, "cultivationMultiplier", 1) : 1;
            var gatheringMultiplier = hasConsumable ? ReadDouble(consumable, "gatheringMultiplier", 1) : 1;
            var alchemyMultiplier = hasConsumable ? ReadDouble(consumable, "alchemyMultiplier", 1) : 1;
            var cooldownSeconds = hasConsumable ? ReadDouble(consumable, "cooldownSeconds", 0) : 0;
            var recoverySeconds = hasConsumable ? ReadDouble(consumable, "recoverySeconds", 0) : 0;
            var hitpointRestore = hasConsumable ? ReadDouble(consumable, "hitpointRestore", 0) : 0;
            var manapointRestore = hasConsumable ? ReadDouble(consumable, "manapointRestore", 0) : 0;
            var triggerHpRatio = hasConsumable ? ReadDouble(consumable, "triggerHpRatio", 0) : 0;
            var triggerAtCombatStart = hasConsumable && ReadBool(consumable, "triggerAtCombatStart", false);

            var buffEffects = hasConsumable && consumable.TryGetProperty("buffs", out var buffsArray) && buffsArray.ValueKind == JsonValueKind.Array
                ? buffsArray.EnumerateArray().Select(b => new
                {
                    buffId = ReadString(b, "buffId"),
                    name = ReadString(b, "name", ReadString(b, "buffId", "增益")),
                    durationSeconds = ReadDouble(b, "durationSeconds", 0),
                    maxStacks = ReadInt(b, "maxStacks", 1),
                    description = ReadString(b, "description"),
                    qiGainMultiplier = ReadDouble(b, "qiGainMultiplier", 1),
                    attackLevelRatio = ReadDouble(b, "attackLevelRatio", 0),
                    damageRatio = ReadDouble(b, "damageRatio", 0),
                    attackSpeedRatio = ReadDouble(b, "attackSpeedRatio", 0),
                    hpRegen = ReadDouble(b, "hpRegen", 0),
                    mpRegen = ReadDouble(b, "mpRegen", 0),
                    damageAbsorptionRatio = ReadDouble(b, "damageAbsorptionRatio", 0),
                    armor = ReadDouble(b, "armor", 0),
                    magicResistance = ReadDouble(b, "magicResistance", 0),
                    criticalChance = ReadDouble(b, "criticalChance", 0),
                    criticalDamageRatio = ReadDouble(b, "criticalDamageRatio", 0),
                    tenacity = ReadDouble(b, "tenacity", 0),
                    gatheringOutputMultiplier = ReadDouble(b, "gatheringOutputMultiplier", 1),
                    alchemyOutputMultiplier = ReadDouble(b, "alchemyOutputMultiplier", 1)
                }).ToArray()
                : [];

            return (object)new
            {
                itemId = id,
                name,
                description = desc,
                kind,
                category,
                tier,
                sellPrice,
                isTradable,
                usableActionTypes,
                durationSeconds,
                isContinuous,
                cultivationMultiplier,
                gatheringMultiplier,
                alchemyMultiplier,
                cooldownSeconds,
                recoverySeconds,
                hitpointRestore,
                manapointRestore,
                triggerHpRatio,
                triggerAtCombatStart,
                buffEffects
            };
        }).ToArray();

    /// <summary>
    /// 执行Equipments。
    /// </summary>
    /// <param name="catalog">参数 catalog。</param>
    /// <returns>返回 IReadOnlyList&lt;object&gt; 类型的结果。</returns>
    public static IReadOnlyList<object> Equipments(GameDataCatalog catalog) => catalog.RawEquipments.Values
        .OrderBy(eq => ReadString(eq, "id"), StringComparer.Ordinal)
        .Select(eq =>
        {
            var id = ReadString(eq, "id");
            var name = ReadString(eq, "name", id);
            var desc = ReadString(eq, "description");
            var slot = ReadString(eq, "slot", "weapon");
            var tier = ReadInt(eq, "tier", 1);

            var baseStatRatios = eq.TryGetProperty("baseStatRatios", out var bsr) && bsr.ValueKind == JsonValueKind.Object
                ? bsr.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>();

            var flatStats = eq.TryGetProperty("flatStats", out var fs) && fs.ValueKind == JsonValueKind.Object
                ? fs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>();

            var specialMechanics = eq.TryGetProperty("specialMechanics", out var sm) && sm.ValueKind == JsonValueKind.Object
                ? sm.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>();

            var enhancementBonus = eq.TryGetProperty("enhancementBonus", out var eb) && eb.ValueKind == JsonValueKind.Object
                ? eb.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>();

            return (object)new
            {
                equipmentId = id,
                name,
                description = desc,
                slot,
                tier,
                baseStatRatios,
                flatStats,
                specialMechanics,
                enhancementBonus,
                stats = new
                {
                    damageRatio = baseStatRatios.GetValueOrDefault("damage", baseStatRatios.GetValueOrDefault("physicalAttack", 0)),
                    accuracyRatio = baseStatRatios.GetValueOrDefault("accuracy", 0),
                    attackSpeedRatio = baseStatRatios.GetValueOrDefault("actionSpeed", 0),
                    evasionRatio = baseStatRatios.GetValueOrDefault("evasion", 0),
                    armor = baseStatRatios.GetValueOrDefault("physicalArmor", baseStatRatios.GetValueOrDefault("armor", 0)),
                    magicResistance = baseStatRatios.GetValueOrDefault("spellResistance", baseStatRatios.GetValueOrDefault("mr", 0)),
                    tenacity = flatStats.GetValueOrDefault("tenacity", 0),
                    penetrationRatio = 0d,
                    lifeStealRatio = 0d,
                    armorDamageRatio = 0d,
                    hpDrainRatio = 0d,
                    criticalChance = baseStatRatios.GetValueOrDefault("criticalChance", flatStats.GetValueOrDefault("criticalChance", 0)),
                    criticalDamageRatio = baseStatRatios.GetValueOrDefault("criticalDamage", 1.5),
                    criticalAvoidance = 0d,
                    damageAbsorptionRatio = baseStatRatios.GetValueOrDefault("damageAbsorption", 0)
                }
            };
        }).ToArray();

    /// <summary>
    /// 映射s。
    /// </summary>
    /// <param name="catalog">参数 catalog。</param>
    /// <returns>返回 IReadOnlyList&lt;object&gt; 类型的结果。</returns>
    public static IReadOnlyList<object> Maps(GameDataCatalog catalog) => catalog.Maps
        .OrderBy(map => ReadString(map, "id"), StringComparer.Ordinal)
        .Select(map => new
        {
            mapId = ReadString(map, "id"),
            name = ReadString(map, "name"),
            description = ReadString(map, "description"),
            backgroundSeed = ReadDouble(map, "backgroundSeed"),
            nodes = ReadArray(map, "nodes").Select(node => ToNode(node, catalog)).ToArray(),
            edges = ReadArray(map, "edges").Select(edge => new
            {
                edgeId = ReadString(edge, "id"),
                fromNodeId = ReadString(edge, "fromNodeId"),
                toNodeId = ReadString(edge, "toNodeId"),
                durationSeconds = ReadDouble(edge, "durationSeconds")
            }).ToArray()
        }).Cast<object>().ToArray();

    /// <summary>
    /// 执行Encounters。
    /// </summary>
    /// <param name="catalog">参数 catalog。</param>
    /// <returns>返回 IReadOnlyList&lt;object&gt; 类型的结果。</returns>
    public static IReadOnlyList<object> Encounters(GameDataCatalog catalog) => catalog.Encounters
        .OrderBy(encounter => ReadString(encounter, "id"), StringComparer.Ordinal)
        .Select(encounter =>
        {
            var random = ReadArray(encounter, "randomSpawns")
                .Select(spawn => new
                {
                    enemy = ResolveMonster(catalog, ReadString(spawn, "enemyId")),
                    weight = ReadDouble(spawn, "weight", 1),
                    strength = ReadDouble(spawn, "strength", 1)
                }).ToArray();
            var primary = random.FirstOrDefault()?.enemy ?? EmptyMonster("enemy.unknown", "未知生灵");
            return (object)new
            {
                encounterId = ReadString(encounter, "id"),
                name = ReadString(encounter, "name"),
                enemy = primary,
                randomSpawns = random,
                maxSpawnCount = ReadInt(encounter, "maxSpawnCount", Math.Max(1, random.Length)),
                maxTotalStrength = ReadDouble(encounter, "maxTotalStrength", ReadDouble(encounter, "maxBudget", 0)),
                bossEnemies = ReadArray(encounter, "bossEnemyIds").Select(id => ResolveMonster(catalog, id.GetString() ?? string.Empty)).ToArray(),
                battlesPerBoss = ReadInt(encounter, "battlesPerBoss", 0),
                searchMilliseconds = ReadInt(encounter, "searchMilliseconds", 5_000),
                respawnMilliseconds = ReadInt(encounter, "respawnMilliseconds", 5_000),
                playerRespawnMilliseconds = ReadInt(encounter, "playerRespawnMilliseconds", 5_000)
            };
        }).ToArray();

    private static object ToNode(JsonElement node, GameDataCatalog catalog)
    {
        var encounterIds = ReadStringArray(node, "encounterIds");
        var monsterIds = encounterIds
            .SelectMany(id => catalog.Encounters.FirstOrDefault(encounter => ReadString(encounter, "id") == id) is { } encounter
                ? ReadArray(encounter, "randomSpawns").Select(spawn => ReadString(spawn, "enemyId"))
                    .Concat(ReadStringArray(encounter, "eliteEnemyIds"))
                    .Concat(ReadStringArray(encounter, "bossEnemyIds"))
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new
        {
            nodeId = ReadString(node, "id"),
            name = ReadString(node, "name"),
            description = ReadString(node, "description"),
            x = ReadDouble(node, "x"),
            y = ReadDouble(node, "y"),
            cultivationMultiplier = ReadDouble(node, "cultivationMultiplier", 1),
            availableActionIds = ReadStringArray(node, "availableActionIds"),
            encounterIds,
            npcs = ReadEntities(node, "npcs"),
            buildings = ReadEntities(node, "buildings"),
            minBudget = ReadDoubleNullable(node, "minBudget"),
            maxBudget = ReadDoubleNullable(node, "maxBudget"),
            eliteInterval = ReadDoubleNullable(node, "eliteInterval"),
            bossInterval = ReadDoubleNullable(node, "bossInterval"),
            monsters = monsterIds.Where(id => IsMonsterInRank(catalog, id, "normal")).Select(id => ResolveMonster(catalog, id)).ToArray(),
            eliteEnemies = monsterIds.Where(id => IsMonsterInRank(catalog, id, "elite")).Select(id => ResolveMonster(catalog, id)).ToArray(),
            bossEnemies = monsterIds.Where(id => IsMonsterInRank(catalog, id, "boss")).Select(id => ResolveMonster(catalog, id)).ToArray()
        };
    }

    private static object[] ReadEntities(JsonElement value, string property) => ReadArray(value, property).Select(entity => new
    {
        entityId = ReadString(entity, "id"),
        name = ReadString(entity, "name"),
        kind = ReadString(entity, "kind", "building"),
        description = ReadString(entity, "description"),
        effect = ReadString(entity, "effect")
    }).Cast<object>().ToArray();

    private static object ResolveMonster(GameDataCatalog catalog, string id) => catalog.Monsters.TryGetValue(id, out var monster)
        ? new
        {
            enemyId = id,
            name = ReadString(monster, "name", id),
            visual = monster.TryGetProperty("visual", out var visual) ? visual.Clone() : JsonSerializer.SerializeToElement(new { renderer = "text", source = "?" }),
            style = NormalizeEnum(ReadString(monster, "style", "body")),
            category = NormalizeEnum(ReadString(monster, "category", "physical")),
            element = NormalizeEnum(ReadString(monster, "element", "none")),
            rank = NormalizeEnum(ReadString(monster, "rank", "normal")),
            maxHitpoints = catalog.CombatMonsters[id].Stats.MaxHitpoints,
            maxManapoints = Math.Max(0, ReadDouble(monster, "maxManapoints", 0)),
            attackIntervalMilliseconds = catalog.CombatMonsters[id].Stats.AttackIntervalSeconds * 1000,
            accuracyRating = catalog.CombatMonsters[id].Stats.Accuracy,
            evasionRating = catalog.CombatMonsters[id].Stats.Evasion,
            maxDamage = catalog.CombatMonsters[id].Stats.AttackPower,
            armor = catalog.CombatMonsters[id].Stats.Defense,
            magicResistance = Math.Max(0, ReadDouble(monster, "magicResistance", 0)),
            metalResistance = Math.Max(0, ReadDouble(monster, "metalResistance", 0)),
            woodResistance = Math.Max(0, ReadDouble(monster, "woodResistance", 0)),
            waterResistance = Math.Max(0, ReadDouble(monster, "waterResistance", 0)),
            fireResistance = Math.Max(0, ReadDouble(monster, "fireResistance", 0)),
            earthResistance = Math.Max(0, ReadDouble(monster, "earthResistance", 0)),
            tenacity = ReadDouble(monster, "tenacity", 0),
            lifeStealRatio = ReadDouble(monster, "lifeStealRatio", 0),
            armorDamageRatio = ReadDouble(monster, "armorDamageRatio", 0),
            hpDrainRatio = ReadDouble(monster, "hpDrainRatio", 0),
            criticalChance = ReadDouble(monster, "criticalChance", 0),
            criticalDamageRatio = ReadDouble(monster, "criticalDamageRatio", 1.5),
            criticalAvoidance = ReadDouble(monster, "criticalAvoidance", 0),
            damageAbsorptionRatio = ReadDouble(monster, "damageAbsorptionRatio", 0),
            hpRegenPerFiveSeconds = ReadDouble(monster, "hpRegen", 0),
            mpRegenPerFiveSeconds = ReadDouble(monster, "mpRegen", 0),
            experience = ReadDouble(monster, "experience", ReadDouble(monster, "budgetCost", 1)),
            abilityIds = ReadArray(monster, "skills").Select(skill => ReadString(skill, "abilityId")).Where(id => id.Length > 0).ToArray()
        }
        : EmptyMonster(id, id);

    private static object EmptyMonster(string id, string name) => new
    {
        enemyId = id, name, visual = new { renderer = "text", source = "?" }, style = "body", category = "physical", element = "none", rank = "normal",
        maxHitpoints = 1d, maxManapoints = 0d, attackIntervalMilliseconds = 2_000d, accuracyRating = 1d, evasionRating = 0d, maxDamage = 0d,
        armor = 0d, magicResistance = 0d, metalResistance = 0d, woodResistance = 0d, waterResistance = 0d, fireResistance = 0d, earthResistance = 0d,
        tenacity = 0d, lifeStealRatio = 0d, armorDamageRatio = 0d, hpDrainRatio = 0d, criticalChance = 0d, criticalDamageRatio = 1.5d,
        criticalAvoidance = 0d, damageAbsorptionRatio = 0d, hpRegenPerFiveSeconds = 0d, mpRegenPerFiveSeconds = 0d, experience = 0d, abilityIds = Array.Empty<string>()
    };

    private static bool IsMonsterInRank(GameDataCatalog catalog, string id, string rank) => catalog.Monsters.TryGetValue(id, out var monster)
        && string.Equals(ReadString(monster, "rank", "normal"), rank, StringComparison.OrdinalIgnoreCase);

    private static JsonElement[] ReadArray(JsonElement value, string property) => value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Array
        ? child.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
    private static string[] ReadStringArray(JsonElement value, string property) => ReadArray(value, property).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
    private static string ReadString(JsonElement value, string property, string fallback = "") => value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? fallback : fallback;
    private static double ReadDouble(JsonElement value, string property, double fallback = 0) => value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : fallback;
    private static double? ReadDoubleNullable(JsonElement value, string property) => value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : null;
    private static int ReadInt(JsonElement value, string property, int fallback) => value.TryGetProperty(property, out var child) && child.TryGetInt32(out var parsed) ? parsed : fallback;
    private static bool ReadBool(JsonElement value, string property, bool fallback = false) => value.TryGetProperty(property, out var child) && (child.ValueKind == JsonValueKind.True || child.ValueKind == JsonValueKind.False) ? child.GetBoolean() : fallback;
    private static string NormalizeEnum(string value) => value.Replace('_', '-').Replace("fiveElements", "five-elements", StringComparison.Ordinal).Replace("playerMirror", "player-mirror", StringComparison.Ordinal);
}
