// ==============================================================================
// 文件名称: GameDataModels.cs
// 主要职责: 定义游戏静态配置（JSON/清单资源）的数据传输对象（DTO）及属性解算上下文数据结构。
// 核心逻辑 / 定义内容:
//   1. 游戏数据清单与版本元数据定义（GameDataManifest）
//   2. 技能、装备、怪物、遭遇及地图等静态配置传输对象（AbilityConfig, EquipmentConfig, MonsterConfig, EncounterConfig, MapConfig）
//   3. 角色与怪物数值解析请求与解算结果模型（CharacterStatRequest, ResolvedCharacterStats 等）
// ==============================================================================
using Domain.Models;

namespace Domain.Models;

/// <summary>
/// 游戏数据清单清单元数据。
/// </summary>
public sealed record GameDataManifest(
    int SchemaVersion,
    string ContentVersion);

/// <summary>
/// 技能配置数据传输对象。所有数值直接指定，仅灵力消耗通过 ManaCostRatio 系数解算。
/// ManaCostRatio = 1.0 时消耗 = ceil(该技能设计阶位基准玩家最大灵力 × 1%)。
/// </summary>
public sealed record AbilityConfig(
    string Id,
    string Name,
    CombatStyle Style = CombatStyle.FiveElements,
    DamageType DamageType = DamageType.Nature,
    int CooldownMilliseconds = 0,
    int CastMilliseconds = 0,
    double ManaCostRatio = 0,
    CombatAbilityEffectType EffectType = CombatAbilityEffectType.Damage,
    CombatAbilityTargetType TargetType = CombatAbilityTargetType.Enemy,
    double DamageRatio = 1.0,
    long FlatDamage = 0,
    double DamageRatioLevelBonus = 0,
    double FlatDamageLevelBonus = 0,
    double HealingRatio = 0,
    long FlatHealing = 0,
    double HealingRatioLevelBonus = 0,
    double FlatHealingLevelBonus = 0,
    double DamageOverTimeRatio = 0,
    int DamageOverTimeMilliseconds = 0,
    int DamageOverTimeTickMilliseconds = 3000,
    double StunChance = 0,
    int StunMilliseconds = 0,
    double BlindChance = 0,
    int BlindMilliseconds = 0,
    double SilenceChance = 0,
    int SilenceMilliseconds = 0,
    double PenetrationRatio = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0,
    double PierceChance = 0,
    double TriggerHpRatio = 1.0,
    IReadOnlyList<CombatTrigger>? DefaultTriggers = null,
    int Tier = 1);

/// <summary>
/// 装备的配置数据传输对象，支持直接填写显式 Stats 或通过 intensity + weights 自动解算。
/// </summary>
public sealed record EquipmentConfig(
    string Id,
    string Name,
    string Slot,
    int Tier = 1,
    int Quality = 1,
    double? Intensity = null,
    double? Budget = null,
    EquipmentCombatStats? Stats = null,
    EquipmentCombatStats? EnhancementBonus = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    int AttackIntervalMilliseconds = 0,
    CombatStyle? Style = null,
    DamageType? DamageType = null);

/// <summary>
/// 消耗品增益效果配置，支持显式属性或由强度权重推导。
/// </summary>
public sealed record BuffEffectConfig(
    string BuffId,
    string Name,
    int DurationSeconds,
    bool Stackable = false,
    int MaxStacks = 1,
    double? QiGainMultiplier = null,
    double? AttackLevelRatio = null,
    double? DamageRatio = null,
    double? AttackSpeedRatio = null,
    double? GatheringOutputMultiplier = null,
    double? AlchemyOutputMultiplier = null,
    double? Intensity = null,
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// 消耗品配置。
/// </summary>
public sealed record ConsumableConfig(
    IReadOnlySet<ActionType> UsableInActionTypes,
    int CooldownSeconds = 0,
    int RecoverySeconds = 0,
    double TriggerHpRatio = 0.6,
    bool TriggerAtCombatStart = false,
    long? HitpointRestore = null,
    long? ManapointRestore = null,
    IReadOnlyList<BuffEffectConfig>? Buffs = null,
    IReadOnlyList<CombatTrigger>? DefaultCombatTriggers = null,
    double? Intensity = null,
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// 物品总配置数据传输对象。
/// </summary>
public sealed record ItemConfig(
    string Id,
    string Name,
    string Description,
    ItemKind Kind,
    string Category,
    int Tier = 1,
    long SellPrice = 0,
    bool IsTradable = true,
    ConsumableConfig? Consumable = null);

/// <summary>
/// 敌怪技能装配配置。
/// </summary>
public sealed record MonsterSkillConfig(
    string AbilityId,
    int Level = 1,
    double TriggerHpRatio = 1.0);

/// <summary>
/// 战斗单位（怪物/Boss）原始数据配置传输对象。
/// </summary>
public sealed record CombatantConfig(
    string Id,
    string Name,
    CharacterVisualDefinition? Visual = null,
    CombatStyle Style = CombatStyle.Body,
    DamageType DamageType = DamageType.Physical,
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
    IReadOnlyList<CombatDropDefinition>? DropTable = null,
    IReadOnlyList<CombatDropDefinition>? RareDropTable = null,
    IReadOnlyList<MonsterSkillConfig>? Skills = null,
    double BreakRating = 0,
    double ParryRating = 0,
    double SkillIntensity = 1.0,
    double DynamicResistanceFactorK = 1.0,
    double ActionSpeedReductionRatio = 0,
    double CooldownReductionRatio = 0);

/// <summary>
/// 遭遇战随机刷怪权重配置。
/// </summary>
public sealed record CombatSpawnConfig(
    string EnemyId,
    double Weight,
    int Strength);

/// <summary>
/// 战斗遭遇（副本/关卡）静态配置传输对象。
/// </summary>
public sealed record CombatEncounterConfig(
    string Id,
    string Name,
    IReadOnlyList<CombatSpawnConfig> RandomSpawns,
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
    IReadOnlyList<CombatDropDefinition>? DropTable = null,
    IReadOnlyList<CombatDropDefinition>? RareDropTable = null);

/// <summary>
/// 通用角色属性解析请求参数。
/// 适用于怪物 (Monster)、NPC、召唤物 (Summons/Pets/Minions)、傀儡与器灵等非玩家战斗实体的数值推导。
/// </summary>
public sealed record CharacterStatRequest
{
    public int Level { get; init; } = 1;
    public int Tier { get; init; } = 0;
    public string? Rank { get; init; } = "minion";
    public CombatStyle Style { get; init; } = CombatStyle.Body;
    public DamageType DamageType { get; init; } = DamageType.Physical;
    public IReadOnlyDictionary<string, double>? Weights { get; init; }
    public bool Floor { get; init; } = true;
    public double? StatMultiplier { get; init; }
    public double? HpMultiplier { get; init; }

    public long? MaxHitpoints { get; init; }
    public long? MaxManapoints { get; init; }
    public long? MaxDamage { get; init; }
    public long? DefensiveMaxDamage { get; init; }
    public double? Armor { get; init; }
    public double? WaterResistance { get; init; }
    public double? NatureResistance { get; init; }
    public double? FireResistance { get; init; }
    public double? AccuracyRating { get; init; }
    public double? EvasionRating { get; init; }
    public double? CriticalRating { get; init; }
    public double? CriticalAvoidance { get; init; }
    public double? BreakRating { get; init; }
    public double? ParryRating { get; init; }
    public double? HpRegen { get; init; }
    public double? MpRegen { get; init; }
}

/// <summary>
/// 通用角色属性解析结果（包含 14 项可量化基础属性与解析元数据）。
/// </summary>
public sealed record ResolvedCharacterStats(
    long MaxHitpoints,
    long MaxManapoints,
    long MaxDamage,
    long DefensiveMaxDamage,
    double Armor,
    double WaterResistance,
    double NatureResistance,
    double FireResistance,
    double AccuracyRating,
    double EvasionRating,
    double CriticalRating,
    double CriticalAvoidance,
    double BreakRating,
    double ParryRating,
    double HpRegen,
    double MpRegen,
    int Tier,
    int Level,
    double StatMultiplier,
    double HpMultiplier);

/// <summary>
/// 标准玩家期望属性数据模型（源自 expected-attributes.json）。
/// </summary>
public sealed record FinalExpectedPlayerAttributes(
    double MaxHitpoints,
    double MaxManapoints,
    double PhysicalAttack,
    double MagicAttack,
    double Armor,
    double MagicResistance,
    double AccuracyRating,
    double EvasionRating,
    double CriticalRating,
    double CriticalAvoidance,
    double BreakRating,
    double ParryRating);

/// <summary>
/// 游戏静态只读配置全量快照聚合根，包含所有动作、物品、技能、装备、怪物、遭遇和地图定义。
/// </summary>
public sealed class GameDataCatalogSnapshot
{
    public GameDataCatalogSnapshot(
        string contentVersion,
        string fingerprint,
        IReadOnlyDictionary<string, ActionDefinition> actions,
        IReadOnlyDictionary<string, ItemDefinition> items,
        IReadOnlyDictionary<string, CombatAbilityDefinition> abilities,
        IReadOnlyDictionary<string, CombatEquipmentDefinition> equipment,
        IReadOnlyDictionary<string, CombatantDefinition> monsters,
        IReadOnlyDictionary<string, CombatEncounterDefinition> encounters,
        IReadOnlyDictionary<string, WorldMapDefinition> maps)
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
    public IReadOnlyDictionary<string, ActionDefinition> Actions { get; }
    /// <summary>物品原型字典。</summary>
    public IReadOnlyDictionary<string, ItemDefinition> Items { get; }
    /// <summary>技能原型字典。</summary>
    public IReadOnlyDictionary<string, CombatAbilityDefinition> Abilities { get; }
    /// <summary>装备原型字典。</summary>
    public IReadOnlyDictionary<string, CombatEquipmentDefinition> Equipment { get; }
    /// <summary>怪物/Boss 战斗原型字典。</summary>
    public IReadOnlyDictionary<string, CombatantDefinition> Monsters { get; }
    /// <summary>遭遇关卡原型字典。</summary>
    public IReadOnlyDictionary<string, CombatEncounterDefinition> Encounters { get; }
    /// <summary>大世界地图原型字典。</summary>
    public IReadOnlyDictionary<string, WorldMapDefinition> Maps { get; }
}

