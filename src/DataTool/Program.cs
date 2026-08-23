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
await WriteAsync("actions.json", ActionCatalog.AllIncludingHidden);
await WriteAsync("items.json", ItemCatalog.All.Select(ToItemConfig).ToArray());
await WriteAsync("abilities.json", CombatCatalog.AllAbilities);
await WriteAsync("equipment.json", CombatCatalog.AllEquipment);
var encounters = CombatCatalog.AllEncounters.ToArray();
var monsters = encounters
    .SelectMany(encounter => new[] { encounter.Enemy }
        .Concat(encounter.RandomSpawns.Select(spawn => spawn.Enemy))
        .Concat(encounter.BossEnemies))
    .GroupBy(monster => monster.Id, StringComparer.Ordinal)
    .Select(group => ToCombatantConfig(group.First()))
    .OrderBy(monster => monster.Id, StringComparer.Ordinal)
    .ToArray();
await WriteAsync("monsters.json", monsters);
await WriteAsync("encounters.json", encounters.Select(ToEncounterConfig).ToArray());
await WriteAsync("maps.json", WorldCatalog.All);

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
        : monster.Visual,
    Style: monster.Style,
    Category: monster.Category,
    Element: monster.Element,
    Level: monster.AttackLevel,
    Tier: monster.AttackLevel > 0 ? (monster.AttackLevel - 1) / 12 + 1 : 1,
    Rank: UnitRank.Minion,
    Weights: null,
    MaxHitpoints: monster.MaxHitpoints,
    MaxManapoints: monster.MaxManapoints,
    AttackIntervalMilliseconds: monster.AttackIntervalMilliseconds,
    AccuracyRating: monster.AccuracyRating,
    EvasionRating: monster.EvasionRating,
    MaxDamage: monster.MaxDamage,
    DefensiveMaxDamage: monster.DefensiveMaxDamage,
    Armor: monster.Armor,
    MagicResistance: monster.MagicResistance,
    MetalResistance: monster.MetalResistance,
    WoodResistance: monster.WoodResistance,
    WaterResistance: monster.WaterResistance,
    FireResistance: monster.FireResistance,
    EarthResistance: monster.EarthResistance,
    Tenacity: monster.Tenacity,
    PierceChance: monster.PierceChance,
    CurseRatio: monster.CurseRatio,
    FuryRatio: monster.FuryRatio,
    WeakenRatio: monster.WeakenRatio,
    LifeStealRatio: monster.LifeStealRatio,
    ArmorDamageRatio: monster.ArmorDamageRatio,
    HpDrainRatio: monster.HpDrainRatio,
    CriticalRating: monster.CriticalRating,
    CriticalChance: monster.CriticalChance,
    CriticalDamageRatio: monster.CriticalDamageRatio,
    CriticalAvoidance: monster.CriticalAvoidance,
    DamageAbsorptionRatio: monster.DamageAbsorptionRatio,
    HpRegen: monster.HpRegen,
    MpRegen: monster.MpRegen,
    HpRegenPerTenSeconds: monster.HpRegenPerTenSeconds,
    MpRegenPerTenSeconds: monster.MpRegenPerTenSeconds,
    EnrageTimeMilliseconds: monster.EnrageTimeMilliseconds,
    ExperienceMultiplier: monster.ExperienceMultiplier,
    Experience: monster.Experience,
    SpiritMin: monster.SpiritMin,
    SpiritMax: monster.SpiritMax,
    DropTable: monster.DropTable,
    RareDropTable: monster.RareDropTable,
    Skills: monster.Abilities.Select(ability => new MonsterSkillConfig(ability.Id, 1, ability.TriggerHpRatio)).ToArray(),
    BreakRating: monster.BreakRating,
    ParryRating: monster.ParryRating,
    SkillIntensity: monster.SkillIntensity,
    DynamicResistanceFactorK: monster.DynamicResistanceFactorK,
    ActionSpeedReductionRatio: monster.ActionSpeedReductionRatio,
    CooldownReductionRatio: monster.CooldownReductionRatio);

/// <summary>
/// 将副本领域定义转换为可导出的副本配置模型 (CombatEncounterConfig)。
/// </summary>
static CombatEncounterConfig ToEncounterConfig(CombatEncounterDefinition encounter) => new(
    Id: encounter.Id,
    Name: encounter.Name,
    RandomSpawns: encounter.RandomSpawns.Select(spawn => new CombatSpawnConfig(
        spawn.Enemy.Id,
        spawn.Weight,
        spawn.Strength)).ToArray(),
    MaxSpawnCount: encounter.MaxSpawnCount,
    MaxTotalStrength: encounter.MaxTotalStrength,
    BossEnemyIds: encounter.BossEnemies.Select(enemy => enemy.Id).ToArray(),
    BattlesPerBoss: encounter.BattlesPerBoss,
    SearchMilliseconds: encounter.SearchMilliseconds,
    RespawnMilliseconds: encounter.RespawnMilliseconds,
    PlayerRespawnMilliseconds: encounter.PlayerRespawnMilliseconds,
    ExperiencePerWin: encounter.ExperiencePerWin,
    SpiritMinPerWin: encounter.SpiritMinPerWin,
    SpiritMaxPerWin: encounter.SpiritMaxPerWin,
    EnrageTimeMilliseconds: encounter.EnrageTimeMilliseconds,
    EnrageTickMilliseconds: encounter.EnrageTickMilliseconds,
    MaxEnrageStacks: encounter.MaxEnrageStacks,
    EnrageRatioPerStack: encounter.EnrageRatioPerStack,
    DropTable: encounter.DropTable,
    RareDropTable: encounter.RareDropTable);

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
