// ==============================================================================
// 文件名称: Program.cs
// 主要职责: 游戏数值配置校验与默认数据导出的独立 CLI 工具入口。
// 核心逻辑 / 定义内容:
//   1. 校验模式 (validate): 读取并校验指定目录下的游戏配置 JSON 与 Manifest，统计数据要素。
//   2. 导出模式 (export-defaults): 将代码内置的游戏数值与规则定义导出为标准格式的 JSON 配置文件。
//   3. 统一规范化 JSON 序列化格式与命名策略。
// ==============================================================================

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.GameData;
using Domain.Models;
using Domain.Simulation;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: dotnet run --project src/DataTool -- <validate|export-defaults> <目录>");
    return 2;
}

var outputDirectory = Path.GetFullPath(args[1]);

// --- 1. 校验模式 ---
if (string.Equals(args[0], "validate", StringComparison.Ordinal))
{
    var snapshot = GameDataCatalog.LoadFromDirectory(outputDirectory);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        snapshot.ContentVersion,
        snapshot.Fingerprint,
        ActionCount = snapshot.Actions.Count,
        ItemCount = snapshot.Items.Count,
        AbilityCount = snapshot.Abilities.Count,
        EquipmentCount = snapshot.Equipment.Count,
        MonsterCount = snapshot.Monsters.Count,
        EncounterCount = snapshot.Encounters.Count,
        MapCount = snapshot.Maps.Count
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}

if (!string.Equals(args[0], "export-defaults", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"未知命令: {args[0]}");
    return 2;
}

// --- 2. 导出默认数据模式 ---
Directory.CreateDirectory(outputDirectory);

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

await WriteAsync("manifest.json", new GameDataManifest(1, "2026.08.10.2"));
await WriteAsync("actions.json", ActionCatalog.AllIncludingHidden.Select(ToActionConfig).ToArray());
await WriteAsync("items.json", ItemCatalog.All.Select(ToItemConfig).ToArray());
await WriteAsync("abilities.json", CombatCatalog.AllAbilities.Select(ToAbilityConfig).ToArray());
await WriteAsync("equipment.json", CombatCatalog.AllEquipment.Select(ToEquipmentConfig).ToArray());
var encounters = CombatCatalog.AllEncounters.ToArray();
var monsters = encounters
    .SelectMany(encounter => new[] { encounter.Enemy }
        .Concat(encounter.RandomSpawns.Select(spawn => spawn.Enemy))
        .Concat(encounter.BossEnemies))
    .GroupBy(monster => monster.Id, StringComparer.Ordinal)
    .Select(group => group.First())
    .OrderBy(monster => monster.Id, StringComparer.Ordinal)
    .Select(ToCombatantConfig)
    .ToArray();
await WriteAsync("monsters.json", monsters);
await WriteAsync("encounters.json", encounters.Select(ToEncounterConfig).ToArray());
await WriteAsync("maps.json", WorldCatalog.All.Select(ToMapConfig).ToArray());

Console.WriteLine($"游戏数据已导出到 {outputDirectory}");
return 0;

/// <summary>
/// 将配置对象序列化为格式化 JSON 文件并写入磁盘。
/// </summary>
async Task WriteAsync<T>(string fileName, T value)
{
    var path = Path.Combine(outputDirectory, fileName);
    await using var stream = File.Create(path);
    await JsonSerializer.SerializeAsync(stream, value, options);
    await stream.WriteAsync("\n"u8.ToArray());
}

/// <summary>
/// 将内存战斗实体定义转换为可导出的怪物配置模型 (CombatantConfig)。
/// </summary>
static CombatantConfig ToCombatantConfig(CombatantDefinition monster) => new(
    Id: monster.Id,
    Name: monster.Name,
    Visual: string.IsNullOrWhiteSpace(monster.Visual.Source)
        ? new CharacterVisualDefinition { Renderer = VisualRendererKind.Text, Source = monster.Name[..1] }
        : (string.IsNullOrEmpty(monster.Visual.Poster) ? new CharacterVisualDefinition { Renderer = monster.Visual.Renderer, Source = monster.Visual.Source } : monster.Visual),
    Style: monster.Style != CombatStyle.Body ? monster.Style : CombatStyle.Body,
    Category: monster.Category != DamageCategory.Physical ? monster.Category : DamageCategory.Physical,
    Element: monster.Element != Element.None ? monster.Element : Element.None,
    Level: monster.AttackLevel > 1 ? monster.AttackLevel : null,
    Tier: monster.AttackLevel > 0 && ((monster.AttackLevel - 1) / 12 + 1) > 1 ? (monster.AttackLevel - 1) / 12 + 1 : null,
    Rank: monster.Rank != UnitRank.Minion ? monster.Rank : null,
    BudgetCost: monster.BudgetCost > 0 && Math.Abs(monster.BudgetCost - 10.0) > 0.001 ? monster.BudgetCost : null,
    AttackIntervalMilliseconds: monster.AttackIntervalMilliseconds != 1000 ? monster.AttackIntervalMilliseconds : null,
    DropTable: monster.DropTable.Count > 0 ? monster.DropTable : null,
    RareDropTable: monster.RareDropTable.Count > 0 ? monster.RareDropTable : null,
    Skills: monster.Abilities.Count > 0
        ? monster.Abilities.Select(ability => new MonsterSkillConfig(ability.Id, 1, Math.Abs(ability.TriggerHpRatio - 1.0) > 0.001 ? ability.TriggerHpRatio : null)).ToArray()
        : null);

/// <summary>
/// 将副本领域定义转换为精简的副本配置模型 (CombatEncounterConfig)。
/// </summary>
static CombatEncounterConfig ToEncounterConfig(CombatEncounterDefinition encounter) => new(
    Id: encounter.Id,
    Name: encounter.Name,
    RandomSpawns: encounter.RandomSpawns.Count > 0 ? encounter.RandomSpawns.Select(spawn => new CombatSpawnConfig(
        spawn.Enemy.Id,
        spawn.Weight,
        spawn.Strength)).ToArray() : null,
    MaxSpawnCount: encounter.MaxSpawnCount != 3 ? encounter.MaxSpawnCount : null,
    MaxTotalStrength: encounter.MaxTotalStrength != 50 ? encounter.MaxTotalStrength : null,
    MinBudget: Math.Abs(encounter.MinBudget - 10.0) > 0.001 ? encounter.MinBudget : null,
    MaxBudget: Math.Abs(encounter.MaxBudget - 50.0) > 0.001 ? encounter.MaxBudget : null,
    EliteInterval: encounter.EliteInterval != 5 ? encounter.EliteInterval : null,
    EliteEnemyIds: encounter.EliteEnemies.Count > 0 ? encounter.EliteEnemies.Select(enemy => enemy.Id).ToArray() : null,
    BossEnemyIds: encounter.BossEnemies.Count > 0 ? encounter.BossEnemies.Select(enemy => enemy.Id).ToArray() : null,
    BattlesPerBoss: encounter.BattlesPerBoss > 0 ? encounter.BattlesPerBoss : null,
    SearchMilliseconds: encounter.SearchMilliseconds != 20000 ? encounter.SearchMilliseconds : null,
    RespawnMilliseconds: encounter.RespawnMilliseconds != 5000 ? encounter.RespawnMilliseconds : null,
    PlayerRespawnMilliseconds: encounter.PlayerRespawnMilliseconds != 180000 ? encounter.PlayerRespawnMilliseconds : null,
    ExperiencePerWin: encounter.ExperiencePerWin > 0 ? encounter.ExperiencePerWin : null,
    SpiritMinPerWin: encounter.SpiritMinPerWin > 0 ? encounter.SpiritMinPerWin : null,
    SpiritMaxPerWin: encounter.SpiritMaxPerWin > 0 ? encounter.SpiritMaxPerWin : null,
    EnrageTimeMilliseconds: encounter.EnrageTimeMilliseconds != 180000 ? encounter.EnrageTimeMilliseconds : null,
    EnrageTickMilliseconds: encounter.EnrageTickMilliseconds != 60000 ? encounter.EnrageTickMilliseconds : null,
    MaxEnrageStacks: encounter.MaxEnrageStacks != 10 ? encounter.MaxEnrageStacks : null,
    EnrageRatioPerStack: Math.Abs(encounter.EnrageRatioPerStack - 0.1) > 0.001 ? encounter.EnrageRatioPerStack : null,
    DropTable: encounter.DropTable.Count > 0 ? encounter.DropTable : null,
    RareDropTable: encounter.RareDropTable.Count > 0 ? encounter.RareDropTable : null);

/// <summary>
/// 将行动定义转换为精简的字典对象以剔除默认配置。
/// </summary>
static object ToActionConfig(ActionDefinition action)
{
    var dict = new Dictionary<string, object?>
    {
        ["id"] = action.Id,
        ["name"] = action.Name,
        ["description"] = action.Description,
        ["type"] = action.Type,
        ["durationSeconds"] = action.DurationSeconds
    };

    if (action.ExecutionKind != ActionExecutionKind.Timed)
    {
        dict["executionKind"] = action.ExecutionKind;
    }

    if (!string.IsNullOrEmpty(action.CombatEncounterId))
    {
        dict["combatEncounterId"] = action.CombatEncounterId;
    }

    if (action.AvailableNodeIds.Count > 0)
    {
        dict["availableNodeIds"] = action.AvailableNodeIds;
    }

    if (action.HiddenFromCatalog)
    {
        dict["hiddenFromCatalog"] = true;
    }

    if (action.Inputs.Count > 0)
    {
        dict["inputs"] = action.Inputs;
    }

    if (action.Outputs.Count > 0)
    {
        dict["outputs"] = action.Outputs;
    }

    return dict;
}

/// <summary>
/// 将技能定义转换为精简的技能配置传输对象 (AbilityConfig)。
/// </summary>
static AbilityConfig ToAbilityConfig(CombatAbilityDefinition ability) => new(
    Id: ability.Id,
    Name: ability.Name,
    Style: ability.Style,
    Category: ability.Category,
    Element: ability.Element,
    CooldownMilliseconds: ability.CooldownMilliseconds > 0 ? ability.CooldownMilliseconds : null,
    CastMilliseconds: ability.CastMilliseconds > 0 ? ability.CastMilliseconds : null,
    ManaCostRatio: ability.ManaCost > 0 ? 10.0 : null,
    EffectType: ability.EffectType != CombatAbilityEffectType.Damage ? ability.EffectType : null,
    TargetType: ability.TargetType != CombatAbilityTargetType.Enemy ? ability.TargetType : null,
    DamageRatio: Math.Abs(ability.DamageRatio - 1.0) > 0.001 || ability.FlatDamage > 0 ? ability.DamageRatio : null,
    FlatDamage: ability.FlatDamage > 0 ? ability.FlatDamage : null,
    DamageRatioLevelBonus: ability.DamageRatioLevelBonus > 0 ? ability.DamageRatioLevelBonus : null,
    FlatDamageLevelBonus: ability.FlatDamageLevelBonus > 0 ? ability.FlatDamageLevelBonus : null,
    HealingRatio: ability.HealingRatio > 0 ? ability.HealingRatio : null,
    FlatHealing: ability.FlatHealing > 0 ? ability.FlatHealing : null,
    HealingRatioLevelBonus: ability.HealingRatioLevelBonus > 0 ? ability.HealingRatioLevelBonus : null,
    FlatHealingLevelBonus: ability.FlatHealingLevelBonus > 0 ? ability.FlatHealingLevelBonus : null,
    DamageOverTimeRatio: ability.DamageOverTimeRatio > 0 ? ability.DamageOverTimeRatio : null,
    DamageOverTimeMilliseconds: ability.DamageOverTimeMilliseconds > 0 ? ability.DamageOverTimeMilliseconds : null,
    DamageOverTimeTickMilliseconds: ability.DamageOverTimeTickMilliseconds != 3000 ? ability.DamageOverTimeTickMilliseconds : null,
    StunChance: ability.StunChance > 0 ? ability.StunChance : null,
    StunMilliseconds: ability.StunMilliseconds > 0 ? ability.StunMilliseconds : null,
    BlindChance: ability.BlindChance > 0 ? ability.BlindChance : null,
    BlindMilliseconds: ability.BlindMilliseconds > 0 ? ability.BlindMilliseconds : null,
    SilenceChance: ability.SilenceChance > 0 ? ability.SilenceChance : null,
    SilenceMilliseconds: ability.SilenceMilliseconds > 0 ? ability.SilenceMilliseconds : null,
    PenetrationRatio: ability.PenetrationRatio > 0 ? ability.PenetrationRatio : null,
    ArmorDamageRatio: ability.ArmorDamageRatio > 0 ? ability.ArmorDamageRatio : null,
    HpDrainRatio: ability.HpDrainRatio > 0 ? ability.HpDrainRatio : null,
    PierceChance: ability.PierceChance > 0 ? ability.PierceChance : null,
    TriggerHpRatio: Math.Abs(ability.TriggerHpRatio - 1.0) > 0.001 ? ability.TriggerHpRatio : null,
    DefaultTriggers: ability.DefaultTriggers.Count > 0 ? ability.DefaultTriggers : null,
    Tier: null);

/// <summary>
/// 将装备定义转换为精简的装备配置传输对象 (EquipmentConfig)。
/// </summary>
static EquipmentConfig ToEquipmentConfig(CombatEquipmentDefinition equipment)
{
    var stats = equipment.Stats;
    var baseRatios = new Dictionary<string, double>(StringComparer.Ordinal);
    if (stats.DamageRatio > 0) baseRatios["physicalAttack"] = stats.DamageRatio;
    if (stats.Armor > 0) baseRatios["armor"] = stats.Armor;
    if (stats.MagicResistance > 0) baseRatios["spellResistance"] = stats.MagicResistance;

    var flats = new Dictionary<string, double>(StringComparer.Ordinal);
    if (stats.AccuracyRatio > 0) flats["accuracy"] = stats.AccuracyRatio;
    if (stats.EvasionRatio > 0) flats["evasion"] = stats.EvasionRatio;
    return new EquipmentConfig(
        Id: equipment.Id,
        Name: equipment.Name,
        Slot: equipment.Slot,
        Tier: equipment.Tier > 1 ? equipment.Tier : null,
        Quality: null,
        Style: stats.Style,
        Category: stats.Category,
        Element: stats.Element != Element.None ? stats.Element : null,
        AttackIntervalMilliseconds: stats.AttackIntervalMilliseconds > 0 ? stats.AttackIntervalMilliseconds : null,
        BaseStatRatios: baseRatios.Count > 0 ? baseRatios : null,
        FlatStats: flats.Count > 0 ? flats : null);
}

/// <summary>
/// 将大地图定义转换为精简的对象以剔除默认空集合与默认倍率。
/// </summary>
static object ToMapConfig(WorldMapDefinition map) => new
{
    id = map.Id,
    name = map.Name,
    description = map.Description,
    backgroundSeed = map.BackgroundSeed,
    nodes = map.Nodes.Select(node =>
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["description"] = node.Description,
            ["x"] = node.X,
            ["y"] = node.Y
        };
        if (Math.Abs(node.CultivationMultiplier - 1.0) > 0.001)
        {
            dict["cultivationMultiplier"] = node.CultivationMultiplier;
        }
        if (node.AvailableActionIds.Count > 0)
        {
            dict["availableActionIds"] = node.AvailableActionIds;
        }
        if (node.EncounterIds.Count > 0)
        {
            dict["encounterIds"] = node.EncounterIds;
        }
        if (node.Npcs.Count > 0)
        {
            dict["npcs"] = node.Npcs;
        }
        if (node.Buildings.Count > 0)
        {
            dict["buildings"] = node.Buildings;
        }
        return dict;
    }),
    edges = map.Edges
};

/// <summary>
/// 将内存物品实体定义转换为精简的物品配置传输对象 (ItemConfig)。
/// </summary>
static ItemConfig ToItemConfig(ItemDefinition item) => new(
    Id: item.Id,
    Name: item.Name,
    Description: item.Description,
    Kind: item.Kind,
    Category: item.Category,
    Tier: item.Tier,
    SellPrice: item.SellPrice,
    IsTradable: item.IsTradable,
    Consumable: item.Consumable == null ? null : ToConsumableConfig(item.Consumable));

/// <summary>
/// 将内存消耗品定义转换为精简的消耗品配置模型 (ConsumableConfig)，自动裁剪无意义的默认参数。
/// </summary>
static ConsumableConfig ToConsumableConfig(ConsumableDefinition consumable)
{
    var isManual = consumable.UsableInActionTypes.Count == 4
        && consumable.DurationSeconds == 0
        && consumable.Buffs.Count == 0
        && consumable.HitpointRestore == 0
        && consumable.ManapointRestore == 0;

    if (isManual)
    {
        return new ConsumableConfig(
            UsableInActionTypes: consumable.UsableInActionTypes);
    }

    return new ConsumableConfig(
        UsableInActionTypes: consumable.UsableInActionTypes,
        DurationSeconds: consumable.DurationSeconds > 0 ? consumable.DurationSeconds : null,
        IsContinuous: consumable.IsContinuous ? true : null,
        CultivationMultiplier: Math.Abs(consumable.CultivationMultiplier - 1.0) > 0.001 ? consumable.CultivationMultiplier : null,
        GatheringMultiplier: Math.Abs(consumable.GatheringMultiplier - 1.0) > 0.001 ? consumable.GatheringMultiplier : null,
        AlchemyMultiplier: Math.Abs(consumable.AlchemyMultiplier - 1.0) > 0.001 ? consumable.AlchemyMultiplier : null,
        CooldownSeconds: consumable.CooldownSeconds > 0 ? consumable.CooldownSeconds : null,
        RecoverySeconds: consumable.RecoverySeconds > 0 ? consumable.RecoverySeconds : null,
        TriggerHpRatio: Math.Abs(consumable.TriggerHpRatio - 0.6) > 0.001 ? consumable.TriggerHpRatio : null,
        TriggerAtCombatStart: consumable.TriggerAtCombatStart ? true : null,
        HitpointRestore: consumable.HitpointRestore > 0 ? consumable.HitpointRestore : null,
        ManapointRestore: consumable.ManapointRestore > 0 ? consumable.ManapointRestore : null,
        Buffs: consumable.Buffs.Count > 0 ? consumable.Buffs.Select(ToBuffEffectConfig).ToArray() : null,
        DefaultCombatTriggers: consumable.DefaultCombatTriggers.Count > 0 ? consumable.DefaultCombatTriggers.ToArray() : null);
}

/// <summary>
/// 将内存增益效果定义转换为精简的增益配置模型 (BuffEffectConfig)，裁剪为 0 或 1.0 的默认加成。
/// </summary>
static BuffEffectConfig ToBuffEffectConfig(BuffEffectDefinition buff) => new(
    BuffId: buff.BuffId,
    Name: buff.Name,
    DurationSeconds: buff.DurationSeconds,
    Stackable: buff.Stackable ? true : null,
    MaxStacks: buff.MaxStacks > 1 ? buff.MaxStacks : null,
    QiGainMultiplier: Math.Abs(buff.QiGainMultiplier - 1.0) > 0.001 ? buff.QiGainMultiplier : null,
    AttackLevelRatio: Math.Abs(buff.AttackLevelRatio) > 0.0001 ? buff.AttackLevelRatio : null,
    DamageRatio: Math.Abs(buff.DamageRatio) > 0.0001 ? buff.DamageRatio : null,
    AttackSpeedRatio: Math.Abs(buff.AttackSpeedRatio) > 0.0001 ? buff.AttackSpeedRatio : null,
    GatheringOutputMultiplier: Math.Abs(buff.GatheringOutputMultiplier - 1.0) > 0.001 ? buff.GatheringOutputMultiplier : null,
    AlchemyOutputMultiplier: Math.Abs(buff.AlchemyOutputMultiplier - 1.0) > 0.001 ? buff.AlchemyOutputMultiplier : null);
