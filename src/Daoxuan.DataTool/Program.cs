using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: dotnet run --project src/Daoxuan.DataTool -- <validate|export-defaults> <目录>");
    return 2;
}

var outputDirectory = Path.GetFullPath(args[1]);
if (string.Equals(args[0], "validate", StringComparison.Ordinal))
{
    var snapshot = DaoxuanGameData.LoadFromDirectory(outputDirectory);
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

Directory.CreateDirectory(outputDirectory);

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true
};
options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

await WriteAsync("manifest.json", new DaoxuanGameDataManifest(1, "2026.08.10.2"));
await WriteAsync("actions.json", DaoxuanActionCatalog.AllIncludingHidden);
await WriteAsync("items.json", DaoxuanItemCatalog.All);
await WriteAsync("abilities.json", DaoxuanCombatCatalog.AllAbilities);
await WriteAsync("equipment.json", DaoxuanCombatCatalog.AllEquipment);
var encounters = DaoxuanCombatCatalog.AllEncounters.ToArray();
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
await WriteAsync("maps.json", DaoxuanWorldCatalog.All);

Console.WriteLine($"游戏数据已导出到 {outputDirectory}");
return 0;

async Task WriteAsync<T>(string fileName, T value)
{
    var path = Path.Combine(outputDirectory, fileName);
    await using var stream = File.Create(path);
    await JsonSerializer.SerializeAsync(stream, value, options);
    await stream.WriteAsync("\n"u8.ToArray());
}

static DaoxuanCombatantConfig ToCombatantConfig(DaoxuanCombatantDefinition monster) => new(
    Id: monster.Id,
    Name: monster.Name,
    Visual: string.IsNullOrWhiteSpace(monster.Visual.Source)
        ? new DaoxuanCharacterVisualDefinition { Renderer = "text", Source = monster.Name[..1] }
        : monster.Visual,
    Style: monster.Style,
    DamageType: monster.DamageType,
    Level: monster.AttackLevel,
    Tier: monster.AttackLevel > 0 ? (monster.AttackLevel - 1) / 12 + 1 : 1,
    Rank: "minion",
    Weights: null,
    MaxHitpoints: monster.MaxHitpoints,
    MaxManapoints: monster.MaxManapoints,
    AttackIntervalMilliseconds: monster.AttackIntervalMilliseconds,
    AccuracyRating: monster.AccuracyRating,
    EvasionRating: monster.EvasionRating,
    MaxDamage: monster.MaxDamage,
    DefensiveMaxDamage: monster.DefensiveMaxDamage,
    Armor: monster.Armor,
    WaterResistance: monster.WaterResistance,
    NatureResistance: monster.NatureResistance,
    FireResistance: monster.FireResistance,
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
    Skills: monster.Abilities.Select(ability => new DaoxuanMonsterSkillConfig(ability.Id, 1, ability.TriggerHpRatio)).ToArray(),
    BreakRating: monster.BreakRating,
    ParryRating: monster.ParryRating,
    SkillIntensity: monster.SkillIntensity,
    DynamicResistanceFactorK: monster.DynamicResistanceFactorK,
    ActionSpeedReductionRatio: monster.ActionSpeedReductionRatio,
    CooldownReductionRatio: monster.CooldownReductionRatio);

static DaoxuanCombatEncounterConfig ToEncounterConfig(DaoxuanCombatEncounterDefinition encounter) => new(
    Id: encounter.Id,
    Name: encounter.Name,
    RandomSpawns: encounter.RandomSpawns.Select(spawn => new DaoxuanCombatSpawnConfig(
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
