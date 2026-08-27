using System.Text.Json;
using Domain.GameData;

namespace Server.Clean;

/// <summary>把内嵌游戏数据映射为前端稳定读取的静态目录 DTO。</summary>
public static class StaticGameCatalogMapper
{
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
            maxHitpoints = Math.Max(1, ReadDouble(monster, "maxHitpoints", ReadDouble(monster, "budgetCost", 1) * 10)),
            maxManapoints = Math.Max(0, ReadDouble(monster, "maxManapoints", 0)),
            attackIntervalMilliseconds = Math.Max(100, ReadDouble(monster, "attackIntervalMilliseconds", 2_000)),
            accuracyRating = ReadDouble(monster, "accuracyRating", 1),
            evasionRating = ReadDouble(monster, "evasionRating", 0),
            maxDamage = Math.Max(0, ReadDouble(monster, "maxDamage", ReadDouble(monster, "budgetCost", 1))),
            armor = Math.Max(0, ReadDouble(monster, "armor", 0)),
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
            hpRegenPerTenSeconds = ReadDouble(monster, "hpRegenPerTenSeconds", 0),
            mpRegenPerTenSeconds = ReadDouble(monster, "mpRegenPerTenSeconds", 0),
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
        criticalAvoidance = 0d, damageAbsorptionRatio = 0d, hpRegenPerTenSeconds = 0d, mpRegenPerTenSeconds = 0d, experience = 0d, abilityIds = Array.Empty<string>()
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
    private static string NormalizeEnum(string value) => value.Replace('_', '-').Replace("fiveElements", "five-elements", StringComparison.Ordinal).Replace("playerMirror", "player-mirror", StringComparison.Ordinal);
}
