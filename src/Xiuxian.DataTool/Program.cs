using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: dotnet run --project src/Xiuxian.DataTool -- <validate|export-defaults> <目录>");
    return 2;
}

var outputDirectory = Path.GetFullPath(args[1]);
if (string.Equals(args[0], "validate", StringComparison.Ordinal))
{
    var snapshot = XiuxianGameData.LoadFromDirectory(outputDirectory);
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

await WriteAsync("manifest.json", new XiuxianGameDataManifest(1, "2026.08.10.2"));
await WriteAsync("actions.json", XiuxianActionCatalog.AllIncludingHidden);
await WriteAsync("items.json", XiuxianItemCatalog.All);
await WriteAsync("abilities.json", XiuxianCombatCatalog.AllAbilities);
await WriteAsync("equipment.json", XiuxianCombatCatalog.AllEquipment);
var encounters = XiuxianCombatCatalog.AllEncounters.ToArray();
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
await WriteAsync("maps.json", XiuxianWorldCatalog.All);

Console.WriteLine($"游戏数据已导出到 {outputDirectory}");
return 0;

async Task WriteAsync<T>(string fileName, T value)
{
    var path = Path.Combine(outputDirectory, fileName);
    await using var stream = File.Create(path);
    await JsonSerializer.SerializeAsync(stream, value, options);
    await stream.WriteAsync("\n"u8.ToArray());
}

static XiuxianCombatantConfig ToCombatantConfig(XiuxianCombatantDefinition monster) => new(
    monster.Id,
    monster.Name,
    string.IsNullOrWhiteSpace(monster.Visual.Source)
        ? new XiuxianCharacterVisualDefinition { Renderer = "text", Source = monster.Name[..1] }
        : monster.Visual,
    monster.Style,
    monster.DamageType,
    monster.MaxHitpoints,
    monster.MaxManapoints,
    monster.AttackIntervalMilliseconds,
    monster.AccuracyRating,
    monster.EvasionRating,
    monster.MaxDamage,
    monster.DefensiveMaxDamage,
    monster.Armor,
    monster.WaterResistance,
    monster.NatureResistance,
    monster.FireResistance,
    monster.Tenacity,
    monster.PierceChance,
    monster.CurseRatio,
    monster.FuryRatio,
    monster.WeakenRatio,
    monster.LifeStealRatio,
    monster.ArmorDamageRatio,
    monster.HpDrainRatio,
    monster.CriticalChance,
    monster.CriticalDamageRatio,
    monster.CriticalAvoidance,
    monster.DamageAbsorptionRatio,
    monster.HpRegenPerTenSeconds,
    monster.MpRegenPerTenSeconds,
    monster.EnrageTimeMilliseconds,
    monster.Experience,
    monster.SpiritMin,
    monster.SpiritMax,
    monster.DropTable,
    monster.RareDropTable,
    monster.Abilities.Select(ability => ability.Id).ToArray());

static XiuxianCombatEncounterConfig ToEncounterConfig(XiuxianCombatEncounterDefinition encounter) => new(
    encounter.Id,
    encounter.Name,
    encounter.Enemy.Id,
    encounter.RandomSpawns.Select(spawn => new XiuxianCombatSpawnConfig(
        spawn.Enemy.Id,
        spawn.Weight,
        spawn.Strength)).ToArray(),
    encounter.MaxSpawnCount,
    encounter.MaxTotalStrength,
    encounter.BossEnemies.Select(enemy => enemy.Id).ToArray(),
    encounter.BattlesPerBoss,
    encounter.SearchMilliseconds,
    encounter.RespawnMilliseconds,
    encounter.PlayerRespawnMilliseconds,
    encounter.ExperiencePerWin,
    encounter.SpiritMinPerWin,
    encounter.SpiritMaxPerWin,
    encounter.EnrageTimeMilliseconds,
    encounter.EnrageTickMilliseconds,
    encounter.MaxEnrageStacks,
    encounter.EnrageRatioPerStack,
    encounter.DropTable,
    encounter.RareDropTable,
    encounter.DropItemId,
    encounter.DropChance,
    encounter.DropQuantity);
