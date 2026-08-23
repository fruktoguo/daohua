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
    DamageCategory Category = DamageCategory.Physical,
    Element Element = Element.None,
    int? CooldownMilliseconds = null,
    int? CastMilliseconds = null,
    double? ManaCostRatio = null,
    CombatAbilityEffectType? EffectType = null,
    CombatAbilityTargetType? TargetType = null,
    double? DamageRatio = null,
    long? FlatDamage = null,
    double? DamageRatioLevelBonus = null,
    double? FlatDamageLevelBonus = null,
    double? HealingRatio = null,
    long? FlatHealing = null,
    double? HealingRatioLevelBonus = null,
    double? FlatHealingLevelBonus = null,
    double? DamageOverTimeRatio = null,
    int? DamageOverTimeMilliseconds = null,
    int? DamageOverTimeTickMilliseconds = null,
    double? StunChance = null,
    int? StunMilliseconds = null,
    double? BlindChance = null,
    int? BlindMilliseconds = null,
    double? SilenceChance = null,
    int? SilenceMilliseconds = null,
    double? PenetrationRatio = null,
    double? ArmorDamageRatio = null,
    double? HpDrainRatio = null,
    double? TriggerHpRatio = null,
    IReadOnlyList<CombatTrigger>? DefaultTriggers = null,
    int? Tier = null);

/// <summary>
/// 装备的配置数据传输对象，支持三段区间结构（BaseStatRatios 14属性百分比、FlatStats 固定值、SpecialMechanics 特殊机制）或显式 Stats/权重。
/// </summary>
public sealed record EquipmentConfig(
    string Id,
    string Name,
    EquipmentSlot Slot = EquipmentSlot.Weapon,
    int? Tier = null,
    int? Quality = null,
    CombatStyle? Style = null,
    DamageCategory? Category = null,
    Element? Element = null,
    int? AttackIntervalMilliseconds = null,
    IReadOnlyDictionary<string, double>? BaseStatRatios = null,
    IReadOnlyDictionary<string, double>? FlatStats = null,
    IReadOnlyDictionary<string, double>? SpecialMechanics = null,
    EquipmentCombatStats? Stats = null,
    EquipmentCombatStats? EnhancementBonus = null,
    double? Intensity = null,
    double? Budget = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    string? Description = null);
/// <summary>
/// 消耗品增益效果配置，支持显式属性或由强度权重推导。
/// </summary>
public sealed record BuffEffectConfig(
    string BuffId,
    string Name,
    int DurationSeconds,
    bool? Stackable = null,
    int? MaxStacks = null,
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
    double? DurationSeconds = null,
    bool? IsContinuous = null,
    double? CultivationMultiplier = null,
    double? GatheringMultiplier = null,
    double? AlchemyMultiplier = null,
    int? CooldownSeconds = null,
    int? RecoverySeconds = null,
    double? TriggerHpRatio = null,
    bool? TriggerAtCombatStart = null,
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
    ItemCategory? Category = null,
    int? Tier = null,
    long? SellPrice = null,
    bool? IsTradable = null,
    ConsumableConfig? Consumable = null);

/// <summary>
/// 敌怪技能装配配置。
/// </summary>
public sealed record MonsterSkillConfig(
    string AbilityId,
    int? Level = null,
    double? TriggerHpRatio = null);

/// <summary>
/// 战斗单位（怪物/Boss）原始数据配置传输对象。
/// </summary>
public sealed record CombatantConfig(
    string Id,
    string Name,
    CharacterVisualDefinition? Visual = null,
    CombatStyle Style = CombatStyle.Body,
    DamageCategory Category = DamageCategory.Physical,
    Element Element = Element.None,
    int? Level = null,
    int? Tier = null,
    UnitRank? Rank = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    long? MaxHitpoints = null,
    long? MaxManapoints = null,
    int? AttackIntervalMilliseconds = null,
    double? AccuracyRating = null,
    double? EvasionRating = null,
    long? MaxDamage = null,
    long? DefensiveMaxDamage = null,
    double? Armor = null,
    double? MagicResistance = null,
    double? MetalResistance = null,
    double? WoodResistance = null,
    double? WaterResistance = null,
    double? FireResistance = null,
    double? EarthResistance = null,
    double? Tenacity = null,
    double? LifeStealRatio = null,
    double? ArmorDamageRatio = null,
    double? HpDrainRatio = null,
    double? CriticalRating = null,
    double? CriticalChance = null,
    double? CriticalDamageRatio = null,
    double? CriticalAvoidance = null,
    double? DamageAbsorptionRatio = null,
    double? HpRegen = null,
    double? MpRegen = null,
    double? HpRegenPerTenSeconds = null,
    double? MpRegenPerTenSeconds = null,
    double? ExperienceMultiplier = null,
    long? Experience = null,
    long? SpiritMin = null,
    long? SpiritMax = null,
    IReadOnlyList<CombatDropDefinition>? DropTable = null,
    IReadOnlyList<CombatDropDefinition>? RareDropTable = null,
    IReadOnlyList<MonsterSkillConfig>? Skills = null,
    double? BreakRating = null,
    double? ParryRating = null,
    double? SkillIntensity = null,
    double? DynamicResistanceFactorK = null,
    double? ActionSpeedReductionRatio = null,
    double? CooldownReductionRatio = null,
    double? BudgetCost = null);

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
    IReadOnlyList<CombatSpawnConfig>? RandomSpawns = null,
    int? MaxSpawnCount = null,
    int? MaxTotalStrength = null,
    IReadOnlyList<string>? BossEnemyIds = null,
    int? BattlesPerBoss = null,
    int? SearchMilliseconds = null,
    int? RespawnMilliseconds = null,
    int? PlayerRespawnMilliseconds = null,
    double? MinBudget = null,
    double? MaxBudget = null,
    int? EliteInterval = null,
    IReadOnlyList<string>? EliteEnemyIds = null,
    long? ExperiencePerWin = null,
    long? SpiritMinPerWin = null,
    long? SpiritMaxPerWin = null,
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
    public UnitRank Rank { get; init; } = UnitRank.Minion;
    public CombatStyle Style { get; init; } = CombatStyle.Body;
    public DamageCategory Category { get; init; } = DamageCategory.Physical;
    public Element Element { get; init; } = Element.None;
    public IReadOnlyDictionary<string, double>? Weights { get; init; }
    public bool Floor { get; init; } = true;
    public double? StatMultiplier { get; init; }
    public double? HpMultiplier { get; init; }

    public long? MaxHitpoints { get; init; }
    public long? MaxManapoints { get; init; }
    public long? MaxDamage { get; init; }
    public long? DefensiveMaxDamage { get; init; }
    public double? Armor { get; init; }
    public double? MagicResistance { get; init; }
    public double? MetalResistance { get; init; }
    public double? WoodResistance { get; init; }
    public double? WaterResistance { get; init; }
    public double? FireResistance { get; init; }
    public double? EarthResistance { get; init; }
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
    double MagicResistance,
    double MetalResistance,
    double WoodResistance,
    double WaterResistance,
    double FireResistance,
    double EarthResistance,
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

