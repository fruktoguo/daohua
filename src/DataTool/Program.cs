// DataTool：游戏数据配表（game-data JSON）的离线校验与指纹工具，独立命令行程序，无项目引用。
// 用法：validate <目录> [--strict] 输出校验报告 JSON；fingerprint <目录> 复用同一管线输出指纹。
// 退出码：0 校验通过，1 校验失败/目录不存在，2 用法错误。指纹口径为 gamedata.v1，
// 必须与 ComputeNode 的 GameDataFingerprint 逐字节一致，否则双方会误判配表不一致。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("用法: dotnet run --project src/DataTool -- validate <游戏数据目录> [--strict]");
    return 2;
}

var command = args[0].Trim().ToLowerInvariant();
if (command is not "validate" and not "fingerprint")
{
    Console.Error.WriteLine($"未知命令: {args[0]}。新基线只提供 validate/fingerprint，不生成旧默认配置。");
    return 2;
}

var directory = Path.GetFullPath(args[1]);
var strict = args.Length == 3 && string.Equals(args[2], "--strict", StringComparison.Ordinal);
if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"游戏数据目录不存在: {directory}");
    return 1;
}

try
{
    // fingerprint 命令故意复用完整校验管线：指纹必须永远对应"通过校验的这份内容"，
    // 避免出现"校验失败但指纹可用"的分叉口径。
    var result = Validate(directory, strict);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}
catch (DataValidationException exception)
{
    Console.Error.WriteLine($"游戏数据校验失败: {exception.Message}");
    return 1;
}

static ValidationResult Validate(string directory, bool strict)
{
    var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
        .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) throw new DataValidationException("目录中没有 JSON 配置文件。");

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var objectCount = 0L;
    var arrayCount = 0L;
    var idCount = 0L;
    using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
        var bytes = File.ReadAllBytes(file);
        try
        {
            // 配表只允许严格 JSON（禁注释/尾逗号、限制深度）：既保证指纹对应字节在任何
            // 解析器下口径一致，也把格式问题挡在入库之前。
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object) objectCount++;
            if (document.RootElement.ValueKind == JsonValueKind.Array) arrayCount++;
            // 只对顶层定义条目做全局唯一性检查：嵌套对象里的 id 是引用键而非定义。
            var topLevel = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Cast<JsonElement?>()
                : document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().Select(p => (JsonElement?)p.Value)
                    : [];
            foreach (var element in topLevel)
            {
                if (element is not { ValueKind: JsonValueKind.Object } entry) continue;
                if (!entry.TryGetProperty("id", out var idElement)) continue;
                if (idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString()))
                    throw new DataValidationException($"{relative} 中 id 必须是非空字符串。");
                var id = idElement.GetString()!;
                if (!ids.Add(id)) throw new DataValidationException($"重复配置 ID: {id}（文件 {relative}）。");
                idCount++;
            }
        }
        catch (JsonException exception)
        {
            throw new DataValidationException($"{relative} JSON 无效: {exception.Message}");
        }

        fingerprint.AppendData(bytes);
    }

    // manifest.json 只在 --strict 下强制要求：默认模式兼容尚未引入清单的开发中配表目录。
    var manifestPath = Path.Combine(directory, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        if (strict) throw new DataValidationException("缺少 manifest.json。");
    }
    else
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (manifest.RootElement.ValueKind != JsonValueKind.Object)
            throw new DataValidationException("manifest.json 根节点必须是对象。");
    }

    ValidateReferences(directory);

    return new ValidationResult(
        "gamedata.v1",
        Path.GetFileName(directory),
        files.Length,
        objectCount,
        arrayCount,
        idCount,
        Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant()[..16],
        strict,
        DateTimeOffset.UtcNow);
}

/// <summary>
/// 语义校验：跨域引用完整性、数值范围与世界图连通性。
/// 这些是真实错误而不是风格建议，因此无论是否 --strict 都强制执行。
/// </summary>
static void ValidateReferences(string directory)
{
    var monsters = CollectIds(directory, "monsters");
    var encounters = CollectIds(directory, "encounters");
    var actions = CollectIds(directory, "actions");
    var abilities = CollectIds(directory, "abilities");
    var items = CollectIds(directory, "items");
    var buildingIds = CollectIds(directory, "buildings");
    var percentPointKeys = PercentPointKeys();
    var abilityBuffKeys = AbilityBuffKeys();
    var consumableBuffKeys = ConsumableBuffKeys();
    var nodeIds = new HashSet<string>(StringComparer.Ordinal);
    var mapEdges = new List<(string From, string To, string File)>();

    foreach (var file in JsonFiles(directory, "maps"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var map in Elements(doc.RootElement))
        {
            foreach (var node in Elements(Property(map, "nodes")))
            {
                var nodeId = Text(node, "id");
                if (nodeId.Length > 0) nodeIds.Add(nodeId);
                foreach (var id in StringArray(node, "encounterIds"))
                    Require(encounters.Contains(id), $"{Rel(directory, file)} 节点 {nodeId} 引用了不存在的遭遇 {id}。");
                foreach (var id in StringArray(node, "availableActionIds"))
                    Require(actions.Contains(id), $"{Rel(directory, file)} 节点 {nodeId} 引用了不存在的行动 {id}。");
                // 节点可建列表：buildableList 自定义 ID 或 buildableTemplate 分组模板二选一。
                foreach (var id in StringArray(node, "buildableList"))
                    Require(buildingIds.Contains(id), $"{Rel(directory, file)} 节点 {nodeId} 可建列表引用了不存在的建筑 {id}。");
                var buildableTemplate = Text(node, "buildableTemplate");
                if (buildableTemplate.Length > 0)
                    Require(buildableTemplate is "all" or "production" or "combat" or "other",
                        $"{Rel(directory, file)} 节点 {nodeId} 的 buildableTemplate={buildableTemplate} 不是合法模板。");
            }
            foreach (var edge in Elements(Property(map, "edges")))
                mapEdges.Add((Text(edge, "fromNodeId"), Text(edge, "toNodeId"), Rel(directory, file)));
        }
    }

    foreach (var (from, to, file) in mapEdges)
    {
        Require(nodeIds.Contains(from), $"{file} 边起点 {from} 不是已登记节点。");
        Require(nodeIds.Contains(to), $"{file} 边终点 {to} 不是已登记节点。");
    }

    // 世界图连通性：所有节点必须能从起始村到达（无向边）。
    if (nodeIds.Count > 0)
    {
        var adjacency = nodeIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (from, to, _) in mapEdges)
        {
            if (!adjacency.ContainsKey(from) || !adjacency.ContainsKey(to)) continue;
            adjacency[from].Add(to);
            adjacency[to].Add(from);
        }
        var start = nodeIds.Contains("node.yunxi-village") ? "node.yunxi-village" : nodeIds.Order(StringComparer.Ordinal).First();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            foreach (var next in adjacency[queue.Dequeue()])
                if (visited.Add(next)) queue.Enqueue(next);
        }
        var unreachable = nodeIds.Where(id => !visited.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        Require(unreachable.Length == 0, $"存在不可达的世界节点: {string.Join(", ", unreachable)}。");
    }

    foreach (var file in JsonFiles(directory, "encounters"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var encounter in Elements(doc.RootElement))
        {
            var id = Text(encounter, "id");
            var minBudget = Number(encounter, "minBudget", 0);
            var maxBudget = Number(encounter, "maxBudget", 0);
            Require(minBudget >= 0 && maxBudget >= minBudget, $"{Rel(directory, file)} 遭遇 {id} 的 minBudget/maxBudget 无效。");
            var onPartyWipe = Text(encounter, "onPartyWipe");
            Require(onPartyWipe.Length == 0 || onPartyWipe is "respawn" or "defeat",
                $"{Rel(directory, file)} 遭遇 {id} 的 onPartyWipe={onPartyWipe} 不是合法团灭规则（respawn|defeat）。");
            foreach (var spawn in Elements(Property(encounter, "randomSpawns")))
            {
                Require(monsters.Contains(Text(spawn, "enemyId")), $"{Rel(directory, file)} 遭遇 {id} 引用了不存在的怪物 {Text(spawn, "enemyId")}。");
                Require(Number(spawn, "weight", 0) > 0 && Number(spawn, "strength", 0) > 0, $"{Rel(directory, file)} 遭遇 {id} 的刷怪权重/强度必须为正数。");
            }
            foreach (var eid in StringArray(encounter, "eliteEnemyIds").Concat(StringArray(encounter, "bossEnemyIds")))
                Require(monsters.Contains(eid), $"{Rel(directory, file)} 遭遇 {id} 引用了不存在的怪物 {eid}。");
        }
    }

    foreach (var file in JsonFiles(directory, "monsters"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var monster in Elements(doc.RootElement))
        {
            var id = Text(monster, "id");
            foreach (var skill in Elements(Property(monster, "skills")))
            {
                var abilityId = Text(skill, "abilityId");
                Require(abilities.Contains(abilityId), $"{Rel(directory, file)} 怪物 {id} 引用了不存在的技能 {abilityId}。");
            }
            foreach (var drop in Elements(Property(monster, "dropTable")))
            {
                Require(items.Contains(Text(drop, "itemId")), $"{Rel(directory, file)} 怪物 {id} 掉落了不存在的物品 {Text(drop, "itemId")}。");
                var chance = Number(drop, "dropChance", 1);
                Require(chance > 0 && chance <= 1, $"{Rel(directory, file)} 怪物 {id} 掉落概率 {chance} 超出 (0,1]。");
            }
            // modifiers 段为百分比点数词汇表；五行抗性等历史误植键直接拒绝。
            foreach (var key in ObjectKeys(Property(monster, "modifiers")))
                Require(percentPointKeys.Contains(key), $"{Rel(directory, file)} 怪物 {id} 的 modifiers.{key} 不在授权词汇表内。");
        }
    }

    // 装备 flatStats 中 Points 结尾的键为百分比点数通道，与怪物 modifiers 共用同一授权词汇表。
    foreach (var file in JsonFiles(directory, "equipment"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var equipment in Elements(doc.RootElement))
            foreach (var key in ObjectKeys(Property(equipment, "flatStats")).Where(name => name.EndsWith("Points", StringComparison.Ordinal)))
                Require(percentPointKeys.Contains(key),
                    $"{Rel(directory, file)} 装备 {Text(equipment, "id")} 的 flatStats.{key} 不在授权词汇表内。");
    }

    // 技能 Buff 与消耗品 Buff 修饰键同样是授权词汇表；ratio 后缀旧键已废除。
    foreach (var file in JsonFiles(directory, "abilities"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var ability in Elements(doc.RootElement))
            foreach (var key in ObjectKeys(Property(ability, "buff")))
                Require(abilityBuffKeys.Contains(key),
                    $"{Rel(directory, file)} 技能 {Text(ability, "id")} 的 buff.{key} 不在授权词汇表内。");
    }

    foreach (var file in JsonFiles(directory, "items"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var item in Elements(doc.RootElement))
        {
            var consumable = Property(item, "consumable");
            foreach (var key in ObjectKeys(consumable))
                Require(ConsumableKeys().Contains(key),
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 consumable.{key} 不在授权词汇表内。");
            foreach (var bar in StringArray(consumable, "usableInActionTypes"))
                Require(AutoUseBarVocabulary().Contains(bar),
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 usableInActionTypes.{bar} 不是合法物品栏域。");
            // consumable 块为自动消耗品专属规格：必须声明可入槽行动域，否则该物品永远无法装配成静默死物品；
            // 手动消耗品（秘籍/宝箱/礼包等）不配 consumable 块，不受此约束。
            if (consumable.ValueKind == JsonValueKind.Object)
                Require(StringArray(consumable, "usableInActionTypes").Any(),
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 声明了 consumable 块但 usableInActionTypes 为空。");
            foreach (var buff in Elements(Property(consumable, "buffs")))
                foreach (var key in ObjectKeys(buff))
                    Require(consumableBuffKeys.Contains(key),
                        $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 buffs.{key} 不在授权词汇表内。");
            var strike = Property(consumable, "strike");
            foreach (var key in ObjectKeys(strike))
                Require(StrikeKeys().Contains(key),
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 strike.{key} 不在授权词汇表内。");
            if (strike.ValueKind == JsonValueKind.Object)
            {
                Require(Number(strike, "manaCostRatio", 0) > 0
                        && Number(strike, "manaDamageRatio", 0) >= 0
                        && Number(strike, "spellAttackRatio", 0) >= 0,
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 strike 比率必须为非负且 manaCostRatio>0。");
                var element = Text(strike, "element");
                Require(element.Length == 0 || CombatElementVocabulary().Contains(element),
                    $"{Rel(directory, file)} 物品 {Text(item, "id")} 的 strike.element={element} 不是合法元素。");
            }
        }
    }

    foreach (var file in JsonFiles(directory, "actions"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var action in Elements(doc.RootElement))
        {
            var id = Text(action, "id");
            var type = Text(action, "type");
            Require(type.Length == 0 || ActionKindVocabulary().Contains(type),
                $"{Rel(directory, file)} 行动 {id} 的 type={type} 不是合法行动类型。");
            var technique = Text(action, "technique");
            Require(technique.Length == 0 || TechniqueVocabulary().Contains(technique),
                $"{Rel(directory, file)} 行动 {id} 的 technique={technique} 不是合法技艺。");
            // 制造大类行动按 technique 归属对应技艺物品栏：必须声明制造技艺（总纲 §6.2.3）。
            Require(type != "alchemy" || ManufacturingTechniqueVocabulary().Contains(technique),
                $"{Rel(directory, file)} 行动 {id} 为制造大类，technique={technique} 必须是制造技艺之一。");
            var encounterId = Text(action, "combatEncounterId");
            if (encounterId.Length > 0)
                Require(encounters.Contains(encounterId), $"{Rel(directory, file)} 行动 {id} 引用了不存在的遭遇 {encounterId}。");
            foreach (var nodeId in StringArray(action, "availableNodeIds"))
                Require(nodeIds.Contains(nodeId), $"{Rel(directory, file)} 行动 {id} 引用了不存在的地图节点 {nodeId}。");
            foreach (var nodeId in StringArray(action, "excludedNodeIds"))
                Require(nodeIds.Contains(nodeId), $"{Rel(directory, file)} 行动 {id} 引用了不存在的地图节点 {nodeId}。");
            // 地点约束互斥：白名单与黑名单同配语义含糊，直接拒绝。
            Require(!(StringArray(action, "availableNodeIds").Any() && StringArray(action, "excludedNodeIds").Any()),
                $"{Rel(directory, file)} 行动 {id} 的 availableNodeIds 与 excludedNodeIds 互斥。");
        }
    }

    foreach (var file in JsonFiles(directory, "buildings"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var building in Elements(doc.RootElement))
        {
            var id = Text(building, "id");
            foreach (var nodeId in StringArray(building, "availableNodeIds"))
                Require(nodeIds.Contains(nodeId), $"{Rel(directory, file)} 建筑 {id} 引用了不存在的地图节点 {nodeId}。");
            var group = Text(building, "group");
            Require(group is "production" or "combat" or "other",
                $"{Rel(directory, file)} 建筑 {id} 的 group={group} 不是合法分组。");
            foreach (var material in Elements(Property(building, "materials")))
            {
                Require(items.Contains(Text(material, "itemId")),
                    $"{Rel(directory, file)} 建筑 {id} 材料引用了不存在的物品 {Text(material, "itemId")}。");
                Require(Number(material, "quantity", 0) > 0,
                    $"{Rel(directory, file)} 建筑 {id} 材料 {Text(material, "itemId")} 数量必须为正数。");
            }
        }
    }
}

static HashSet<string> CollectIds(string directory, string subfolder)
{
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in JsonFiles(directory, subfolder))
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        // 只收集顶层定义条目的 id；嵌套 id 是引用键。
        foreach (var element in Elements(doc.RootElement))
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String)
            {
                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }
    }
    return ids;
}

static IEnumerable<string> JsonFiles(string directory, string subfolder)
{
    var path = Path.Combine(directory, subfolder);
    return Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
        : [];
}

static string Rel(string directory, string file) => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
static IEnumerable<JsonElement> Elements(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;
static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? string.Empty : string.Empty;
static double Number(JsonElement element, string name, double fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.TryGetDouble(out var value) ? value : fallback;
static IEnumerable<string> StringArray(JsonElement element, string name)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var child) || child.ValueKind != JsonValueKind.Array) yield break;
    foreach (var item in child.EnumerateArray())
        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) yield return text;
}

static IEnumerable<string> ObjectKeys(JsonElement element)
{
    if (element.ValueKind != JsonValueKind.Object) yield break;
    foreach (var property in element.EnumerateObject()) yield return property.Name;
}

/// <summary>百分比点数属性授权词汇表（总纲第七篇 §7.7.5）：装备 flatStats 点数键与怪物 modifiers 共用。</summary>
static HashSet<string> PercentPointKeys() => new(StringComparer.Ordinal)
{
    "physicalDamageBonusPoints", "spellDamageBonusPoints",
    "metalDamageBonusPoints", "woodDamageBonusPoints", "waterDamageBonusPoints",
    "fireDamageBonusPoints", "earthDamageBonusPoints",
    "totalDamageBonusPoints", "basicAttackBonusPoints", "skillBonusPoints",
    "physicalDamageReductionPoints", "spellDamageReductionPoints",
    "metalDamageReductionPoints", "woodDamageReductionPoints", "waterDamageReductionPoints",
    "fireDamageReductionPoints", "earthDamageReductionPoints",
    "damageAbsorptionPoints",
    "attackSpeedPoints", "actionSpeedPoints", "cooldownReductionPoints", "threatBonusPoints",
    "criticalDamageBonusPoints",
    "hpDrainPoints", "mpDrainPoints", "damageReflectPoints", "healingBonusPoints", "threatOnTakenPoints"
};

/// <summary>技能战内 Buff 授权键：读条/免伤/增伤/威胁点数与回复、时长、叠层上限。</summary>
static HashSet<string> AbilityBuffKeys() => new(StringComparer.Ordinal)
{
    "buffId", "name", "durationSeconds", "hpRegen", "mpRegen",
    "attackSpeedPoints", "damageAbsorptionPoints", "damageBonusPoints", "threatPoints", "maxStacks",
    "hpRegenPostRatio", "mpRegenPostRatio", "threatPerSecondMaxHpPoints", "resetOnWave"
};

/// <summary>消耗品战外 Buff 授权键（含 statRatios 通用比率字典与回复后段乘区）。</summary>
static HashSet<string> ConsumableBuffKeys() => new(StringComparer.Ordinal)
{
    "buffId", "name", "durationSeconds", "hpRegen", "mpRegen",
    "attackSpeedPoints", "damageAbsorptionPoints", "statRatios",
    "hpRegenPostRatio", "mpRegenPostRatio"
};

/// <summary>消耗品顶层授权键（durable 阵盘与 strike 雷击符规格）。</summary>
static HashSet<string> ConsumableKeys() => new(StringComparer.Ordinal)
{
    "usableInActionTypes", "durationSeconds", "isContinuous", "durable",
    "cultivationMultiplier", "gatheringMultiplier", "alchemyMultiplier",
    "cooldownSeconds", "hitpointRestore", "manapointRestore", "buffs", "triggers", "strike"
};

/// <summary>雷击符打击规格授权键。</summary>
static HashSet<string> StrikeKeys() => new(StringComparer.Ordinal)
{
    "manaCostRatio", "manaDamageRatio", "spellAttackRatio", "element"
};

/// <summary>行动类型词汇表：与 GameDataCatalog.ParseKind 同口径。</summary>
static HashSet<string> ActionKindVocabulary() => new(StringComparer.Ordinal)
{
    "cultivation", "exploration", "alchemy", "combat", "travel", "enhancement"
};

/// <summary>物品栏域词汇表：与 AutoUseBar 栏域常量同口径（历练涵盖修炼/历练两大类行动）。</summary>
static HashSet<string> AutoUseBarVocabulary() => new(StringComparer.Ordinal)
{
    "exploration", "alchemy", "forging", "talisman", "formation", "enhancement", "combat"
};

/// <summary>制造技艺词汇表：Alchemy 大类行动的栏域归属词表。</summary>
static HashSet<string> ManufacturingTechniqueVocabulary() => new(StringComparer.Ordinal)
{
    "alchemy", "forging", "talisman", "formation"
};

/// <summary>七技艺词汇表：与 TechniqueCatalog 固定标识同口径。</summary>
static HashSet<string> TechniqueVocabulary() => new(StringComparer.Ordinal)
{
    "alchemy", "forging", "talisman", "enhancement", "formation", "gathering", "mining"
};

/// <summary>五行元素词汇表（none 为无元素）。</summary>
static HashSet<string> CombatElementVocabulary() => new(StringComparer.Ordinal)
{
    "none", "metal", "wood", "water", "fire", "earth"
};
static void Require(bool condition, string message)
{
    if (!condition) throw new DataValidationException(message);
}

static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        yield return element;
        foreach (var property in element.EnumerateObject())
        {
            foreach (var child in EnumerateObjects(property.Value)) yield return child;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var child in element.EnumerateArray())
        {
            foreach (var nested in EnumerateObjects(child)) yield return nested;
        }
    }
}

/// <summary>游戏数据校验结果 DTO</summary>
/// <param name="ContractVersion">结果契约版本</param>
/// <param name="Directory">被校验目录名称</param>
/// <param name="Files">扫描到的 JSON 文件数量</param>
/// <param name="ObjectCount">顶层 JSON 对象数量</param>
/// <param name="ArrayCount">顶层 JSON 数组数量</param>
/// <param name="IdCount">全局唯一 id 数量</param>
/// <param name="Fingerprint">目录内容 SHA256 前 16 位指纹</param>
/// <param name="Strict">是否启用严格模式</param>
/// <param name="ValidatedAt">校验完成时间</param>
file sealed record ValidationResult(
    string ContractVersion,
    string Directory,
    int Files,
    long ObjectCount,
    long ArrayCount,
    long IdCount,
    string Fingerprint,
    bool Strict,
    DateTimeOffset ValidatedAt);

/// <summary>游戏数据校验失败异常</summary>
/// <param name="message">异常消息</param>
file sealed class DataValidationException(string message) : Exception(message);
