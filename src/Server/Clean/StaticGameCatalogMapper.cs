using System.Text.Json;
using Domain.Combat;
using Domain.GameData;

namespace Server.Clean;

/// <summary>把内嵌游戏数据映射为前端稳定读取的静态目录 DTO。</summary>
public static class StaticGameCatalogMapper
{
    /// <summary>
    /// 物品静态目录投影：基础字段之外展开消耗品属性与 buff 效果，供前端物品图鉴消费；
    /// 按 id 排序保证输出稳定可缓存。
    /// </summary>
    /// <param name="catalog">当前激活配表。</param>
    public static IReadOnlyList<object> Items(GameDataCatalog catalog) => catalog.RawItems.Values
        .OrderBy(item => ReadString(item, "id"), StringComparer.Ordinal)
        .Select(item =>
        {
            var id = ReadString(item, "id");
            var name = ReadString(item, "name", id);
            var desc = ReadString(item, "description");
            var kind = ReadString(item, "kind", "material");
            var category = ReadString(item, "category", "material");
            var subCategory = ReadString(item, "subCategory");
            var tier = ReadInt(item, "tier", 1);
            var sellPrice = ReadDouble(item, "sellPrice", 0);
            var isTradable = ReadBool(item, "isTradable", true);

            var consumable = item.TryGetProperty("consumable", out var cons) && cons.ValueKind == JsonValueKind.Object ? cons : default;
            var hasConsumable = consumable.ValueKind == JsonValueKind.Object;

            // 配表 usableInActionTypes 承载物品栏域集合（§6.2.3），出站字段直接按栏域语义命名。
            var usableBars = hasConsumable ? ReadStringArray(consumable, "usableInActionTypes") : [];
            var durationSeconds = hasConsumable ? ReadDouble(consumable, "durationSeconds", 0) : 0;
            var isContinuous = hasConsumable && ReadBool(consumable, "isContinuous", false);
            var cultivationMultiplier = hasConsumable ? ReadDouble(consumable, "cultivationMultiplier", 1) : 1;
            var gatheringMultiplier = hasConsumable ? ReadDouble(consumable, "gatheringMultiplier", 1) : 1;
            var alchemyMultiplier = hasConsumable ? ReadDouble(consumable, "alchemyMultiplier", 1) : 1;
            var cooldownSeconds = hasConsumable ? ReadDouble(consumable, "cooldownSeconds", 0) : 0;
            var hitpointRestore = hasConsumable ? ReadDouble(consumable, "hitpointRestore", 0) : 0;
            var manapointRestore = hasConsumable ? ReadDouble(consumable, "manapointRestore", 0) : 0;
            // durable 持续消耗品：部署后不消耗不耗竭、槽位持有即生效。
            var durable = hasConsumable && ReadBool(consumable, "durable", false);
            // strike 即时打击（雷击符）：耗蓝代价 + 蓝量/法攻折算的木系法术一击。
            var strike = hasConsumable && consumable.TryGetProperty("strike", out var strikeElement) && strikeElement.ValueKind == JsonValueKind.Object
                ? new
                {
                    manaCostRatio = ReadDouble(strikeElement, "manaCostRatio", 0),
                    manaDamageRatio = ReadDouble(strikeElement, "manaDamageRatio", 0),
                    spellAttackRatio = ReadDouble(strikeElement, "spellAttackRatio", 0),
                    element = ReadString(strikeElement, "element")
                }
                : null;

            // 消耗品增益效果与领域结算读取的字段一一对应（SimulationEngine 只消费以下键），
            // 不下发领域未消费的占位字段，避免前端误以为存在自动触发/阈值语义。
            // hpRegenPostRatio/mpRegenPostRatio 为后段回复乘区（固定回复合并后再乘）。
            var buffEffects = hasConsumable && consumable.TryGetProperty("buffs", out var buffsArray) && buffsArray.ValueKind == JsonValueKind.Array
                ? buffsArray.EnumerateArray().Select(b => new
                {
                    buffId = ReadString(b, "buffId"),
                    name = ReadString(b, "name", ReadString(b, "buffId", "增益")),
                    durationSeconds = ReadDouble(b, "durationSeconds", 0),
                    hpRegen = ReadDouble(b, "hpRegen", 0),
                    mpRegen = ReadDouble(b, "mpRegen", 0),
                    hpRegenPostRatio = ReadDouble(b, "hpRegenPostRatio", 0),
                    mpRegenPostRatio = ReadDouble(b, "mpRegenPostRatio", 0),
                    attackSpeedPoints = ReadDouble(b, "attackSpeedPoints", 0),
                    damageAbsorptionPoints = ReadDouble(b, "damageAbsorptionPoints", 0)
                }).ToArray()
                : [];

            return (object)new
            {
                itemId = id,
                name,
                description = desc,
                kind,
                category,
                subCategory = subCategory.Length > 0 ? subCategory : null,
                tier,
                sellPrice,
                isTradable,
                usableBars,
                durationSeconds,
                isContinuous,
                cultivationMultiplier,
                gatheringMultiplier,
                alchemyMultiplier,
                cooldownSeconds,
                hitpointRestore,
                manapointRestore,
                durable,
                strike,
                buffEffects
            };
        }).ToArray();

    /// <summary>
    /// 装备静态目录投影：只透传配表原始数值块（baseStatRatios/flatStats/
    /// enhancementBonus）。战斗终值由服务端 PlayerAttributeResolver 统一结算并经 combat.attributes
    /// 下发，协议不再提供派生 stats 幻影块，前端也不得本地重建。
    /// </summary>
    /// <param name="catalog">当前激活配表。</param>
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

            var enhancementBonus = eq.TryGetProperty("enhancementBonus", out var eb) && eb.ValueKind == JsonValueKind.Object
                ? eb.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
                : new Dictionary<string, double>();

            // 强化保护材料白名单：配了列表就从列表内任选材料代护，未配时前端回退为同装备基础件提示。
            var enhanceProtectionItems = eq.TryGetProperty("enhanceProtectionItems", out var ep) && ep.ValueKind == JsonValueKind.Array
                ? ep.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String)
                    .Select(entry => entry.GetString()!).Where(text => text.Length > 0).ToArray()
                : null;

            // 技艺工具加成块：配表声明 techniqueBonus 时透传绑定技艺与速度比率，普通装备下发 null。
            var techniqueBonus = eq.TryGetProperty("techniqueBonus", out var tb) && tb.ValueKind == JsonValueKind.Object
                ? new
                {
                    technique = ReadString(tb, "technique"),
                    speedRatio = ReadDouble(tb, "speedRatio", 0)
                }
                : null;

            return (object)new
            {
                equipmentId = id,
                name,
                description = desc,
                slot,
                tier,
                baseStatRatios,
                flatStats,
                enhancementBonus,
                enhanceProtectionItems,
                techniqueBonus
            };
        }).ToArray();

    /// <summary>
    /// 地图静态目录投影：节点与边完整透传，节点附带的怪物列表经 encounter 引用解析归并。
    /// </summary>
    /// <param name="catalog">当前激活配表。</param>
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
    /// 遭遇配置静态目录投影：randomSpawns/bossEnemies 中的怪物 ID 解析为完整怪物摘要，
    /// 未知 ID 降级为占位条目而不是让整项目录失败。
    /// </summary>
    /// <param name="catalog">当前激活配表。</param>
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
                eliteEnemies = ReadStringArray(encounter, "eliteEnemyIds").Select(id => ResolveMonster(catalog, id)).ToArray(),
                battlesPerElite = ReadInt(encounter, "battlesPerElite", 0),
                bossEnemies = ReadStringArray(encounter, "bossEnemyIds").Select(id => ResolveMonster(catalog, id)).ToArray(),
                battlesPerBoss = ReadInt(encounter, "battlesPerBoss", 0),
                searchMilliseconds = ReadInt(encounter, "searchMilliseconds", 5_000),
                respawnMilliseconds = ReadInt(encounter, "respawnMilliseconds", 5_000),
                playerRespawnMilliseconds = ReadInt(encounter, "playerRespawnMilliseconds", 5_000)
            };
        }).ToArray();

    /// <summary>
    /// 玩家可建造建筑静态目录投影：按 id 排序输出稳定的建造候选定义，供前端加号占位弹窗消费。
    /// </summary>
    /// <param name="catalog">当前激活配表。</param>
    public static IReadOnlyList<object> Buildings(GameDataCatalog catalog) => catalog.Buildings.Values
        .OrderBy(definition => definition.Id, StringComparer.Ordinal)
        .Select(definition => (object)new
        {
            buildingId = definition.Id,
            name = definition.Name,
            description = definition.Description,
            effect = definition.Effect,
            requiredRealmLevel = definition.RequiredRealmLevel,
            availableNodeIds = definition.AvailableNodeIds?.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            group = definition.Group,
            baseModifiers = new
            {
                explorationSpeedRatio = definition.BaseModifiers.ExplorationSpeedRatio,
                explorationOutputRatio = definition.BaseModifiers.ExplorationOutputRatio,
                alchemySpeedRatio = definition.BaseModifiers.AlchemySpeedRatio,
                alchemyOutputRatio = definition.BaseModifiers.AlchemyOutputRatio,
                cultivationOutputRatio = definition.BaseModifiers.CultivationOutputRatio,
                combatHitpointRegen = definition.BaseModifiers.CombatHitpointRegen,
                combatManapointRegen = definition.BaseModifiers.CombatManapointRegen,
                combatDamageReductionRatio = definition.BaseModifiers.CombatDamageReductionRatio,
                monsterWeakenRatio = definition.BaseModifiers.MonsterWeakenRatio,
                gatheringSpeedRatio = definition.BaseModifiers.GatheringSpeedRatio,
                miningSpeedRatio = definition.BaseModifiers.MiningSpeedRatio,
                forgingSpeedRatio = definition.BaseModifiers.ForgingSpeedRatio,
                talismanSpeedRatio = definition.BaseModifiers.TalismanSpeedRatio,
                formationSpeedRatio = definition.BaseModifiers.FormationSpeedRatio,
                enhancementSpeedRatio = definition.BaseModifiers.EnhancementSpeedRatio,
            },
            growthModifiers = new
            {
                explorationSpeedRatio = definition.GrowthModifiers.ExplorationSpeedRatio,
                explorationOutputRatio = definition.GrowthModifiers.ExplorationOutputRatio,
                alchemySpeedRatio = definition.GrowthModifiers.AlchemySpeedRatio,
                alchemyOutputRatio = definition.GrowthModifiers.AlchemyOutputRatio,
                cultivationOutputRatio = definition.GrowthModifiers.CultivationOutputRatio,
                combatHitpointRegen = definition.GrowthModifiers.CombatHitpointRegen,
                combatManapointRegen = definition.GrowthModifiers.CombatManapointRegen,
                combatDamageReductionRatio = definition.GrowthModifiers.CombatDamageReductionRatio,
                monsterWeakenRatio = definition.GrowthModifiers.MonsterWeakenRatio,
                gatheringSpeedRatio = definition.GrowthModifiers.GatheringSpeedRatio,
                miningSpeedRatio = definition.GrowthModifiers.MiningSpeedRatio,
                forgingSpeedRatio = definition.GrowthModifiers.ForgingSpeedRatio,
                talismanSpeedRatio = definition.GrowthModifiers.TalismanSpeedRatio,
                formationSpeedRatio = definition.GrowthModifiers.FormationSpeedRatio,
                enhancementSpeedRatio = definition.GrowthModifiers.EnhancementSpeedRatio,
            },
            modifiers = new
            {
                explorationSpeedRatio = definition.Modifiers.ExplorationSpeedRatio,
                explorationOutputRatio = definition.Modifiers.ExplorationOutputRatio,
                alchemySpeedRatio = definition.Modifiers.AlchemySpeedRatio,
                alchemyOutputRatio = definition.Modifiers.AlchemyOutputRatio,
                cultivationOutputRatio = definition.Modifiers.CultivationOutputRatio,
                combatHitpointRegen = definition.Modifiers.CombatHitpointRegen,
                combatManapointRegen = definition.Modifiers.CombatManapointRegen,
                combatDamageReductionRatio = definition.Modifiers.CombatDamageReductionRatio,
                monsterWeakenRatio = definition.Modifiers.MonsterWeakenRatio,
                gatheringSpeedRatio = definition.Modifiers.GatheringSpeedRatio,
                miningSpeedRatio = definition.Modifiers.MiningSpeedRatio,
                forgingSpeedRatio = definition.Modifiers.ForgingSpeedRatio,
                talismanSpeedRatio = definition.Modifiers.TalismanSpeedRatio,
                formationSpeedRatio = definition.Modifiers.FormationSpeedRatio,
                enhancementSpeedRatio = definition.Modifiers.EnhancementSpeedRatio,
            },
            materials = (definition.Materials ?? [])
                .OrderBy(material => material.ItemId, StringComparer.Ordinal)
                .Select(material => (object)new { itemId = material.ItemId, quantity = material.Quantity })
                .ToArray()
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

    /// <summary>
    /// 怪物静态摘要投影：攻防/资源/抗性/击杀收益一律取领域解算结果（CombatMonsterDefinition.Stats 与奖励字段），
    /// 原始配表 JSON 只承担名称、立绘、流派标签与技能列表等声明式字段——属性由「等级锚点 × 生命层次倍率 × 倾向权重」
    /// 派生，配表顶层并不存在 maxManapoints/magicResistance/experience 等同名字段，直接读会恒为 0。
    /// </summary>
    private static object ResolveMonster(GameDataCatalog catalog, string id)
    {
        if (!catalog.Monsters.TryGetValue(id, out var monster)
            || !catalog.CombatMonsters.TryGetValue(id, out var definition))
            return EmptyMonster(id, id);
        var stats = definition.Stats;
        return new
        {
            enemyId = id,
            name = ReadString(monster, "name", id),
            visual = monster.TryGetProperty("visual", out var visual) ? visual.Clone() : JsonSerializer.SerializeToElement(new { renderer = "text", source = "?" }),
            style = NormalizeEnum(ReadString(monster, "style", "body")),
            category = NormalizeEnum(ReadString(monster, "category", "physical")),
            element = NormalizeEnum(ReadString(monster, "element", "none")),
            rank = NormalizeEnum(ReadString(monster, "rank", "normal")),
            level = definition.Level,
            tier = catalog.BaseAttributes?.TierAt(definition.Level) ?? 1,
            budgetCost = definition.BudgetCost,
            // 怪物属性与玩家/斗法镜像走同一份协议形状，字段语义完全一致。
            attributes = ProtocolSnapshotMapper.ToAttributesDto(stats),
            experience = definition.Experience,
            drops = definition.DropTable.Select(drop => new
            {
                itemId = drop.ItemId.Value,
                dropChance = drop.DropChance,
                minQuantity = drop.MinQuantity,
                maxQuantity = drop.MaxQuantity
            }).ToArray(),
            // 功法槽按 CombatAbilitySlot 形状投影（abilityId+level，等级取配表施放等级），
            // 详情弹窗「装配」Tab 与生态面板共用此来源。
            skills = definition.Skills.Select(skill => new
            {
                abilityId = skill.Ability.TryGetProperty("id", out var abilityIdElement)
                    ? abilityIdElement.GetString() ?? string.Empty
                    : string.Empty,
                level = skill.Level,
            }).Where(skill => skill.abilityId.Length > 0).ToArray()
        };
    }

    private static object EmptyMonster(string id, string name) => new
    {
        enemyId = id, name, visual = new { renderer = "text", source = "?" }, style = "body", category = "physical", element = "none", rank = "normal",
        level = 1, tier = 1, budgetCost = 0d,
        attributes = ProtocolSnapshotMapper.ToAttributesDto(CombatantStats.Create(1, 0, 0, 2)),
        experience = 0d,
        drops = Array.Empty<object>(), skills = Array.Empty<object>()
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
