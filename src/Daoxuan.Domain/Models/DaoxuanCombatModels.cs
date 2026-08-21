namespace Daoxuan.Domain.Models;

public sealed class DaoxuanCharacterVisualDefinition
{
    public string Renderer { get; init; } = "text";
    public string Source { get; init; } = "";
    public string? Poster { get; init; }
}

/// <summary>
/// 触发条件的观察对象。友方/敌方极值依赖在当前单人战斗拓扑下分别解析为玩家自身与当前目标；
/// 组队战斗接入后，极值依赖应改为在对应队伍的存活成员集合上求值。
/// </summary>
public enum DaoxuanCombatTriggerDependency
{
    Self = 0,
    TargetEnemy = 1,
    AllyLowestHitpoints = 2,
    AllyHighestHitpoints = 3,
    EnemyLowestHitpoints = 4,
    EnemyHighestHitpoints = 5
}

public enum DaoxuanCombatTriggerCondition
{
    CurrentHitpoints = 0,
    CurrentManapoints = 1,
    MissingHitpoints = 2,
    MissingManapoints = 3,
    BuffStacks = 4,
    StunStatus = 5,
    BlindStatus = 6,
    SilenceStatus = 7,
    CurrentHitpointsPercentage = 8,
    MissingHitpointsPercentage = 9,
    CurrentManapointsPercentage = 10,
    MissingManapointsPercentage = 11
}

public enum DaoxuanCombatTriggerComparator
{
    LessThanOrEqual = 0,
    GreaterThanOrEqual = 1,
    IsActive = 2,
    IsInactive = 3,
    LessThan = 4,
    GreaterThan = 5
}

/// <summary>
/// 功法和战斗消耗品共用的触发条件。一个槽位内的多条条件按 AND 计算。
/// </summary>
public sealed class DaoxuanCombatTrigger
{
    public DaoxuanCombatTriggerDependency Dependency { get; set; }
    public DaoxuanCombatTriggerCondition Condition { get; set; }
    public DaoxuanCombatTriggerComparator Comparator { get; set; }
    public double Value { get; set; }
    public string? BuffId { get; set; }

    public DaoxuanCombatTrigger Clone() => new()
    {
        Dependency = Dependency,
        Condition = Condition,
        Comparator = Comparator,
        Value = Value,
        BuffId = BuffId
    };
}

public enum DaoxuanCombatAbilityEffectType
{
    Damage = 0,
    Heal = 1
}

/// <summary>
/// 功法的权威目标范围。当前战斗只有一名玩家，但仍保留完整范围，便于后续扩展组队战斗。
/// </summary>
public enum DaoxuanCombatAbilityTargetType
{
    Enemy = 0,
    AllEnemies = 1,
    Self = 2,
    AllAllies = 3,
    LowestHitpointsAlly = 4
}

public sealed class DaoxuanCombatDropDefinition
{
    public required string ItemId { get; init; }
    public double DropChance { get; init; }
    public long MinQuantity { get; init; } = 1;
    public long MaxQuantity { get; init; } = 1;
}

/// <summary>
/// 战斗能力的静态数据。数值放在目录而不是模拟器分支中，方便以后做版本化配置。
/// </summary>
public sealed class DaoxuanCombatAbilityDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DaoxuanCombatStyle Style { get; init; }
    public DaoxuanDamageType DamageType { get; init; }
    public int CooldownMilliseconds { get; init; }
    public int CastMilliseconds { get; init; }
    public long ManaCost { get; init; }
    public DaoxuanCombatAbilityEffectType EffectType { get; init; } = DaoxuanCombatAbilityEffectType.Damage;
    public DaoxuanCombatAbilityTargetType TargetType { get; init; } = DaoxuanCombatAbilityTargetType.Enemy;
    public double DamageRatio { get; init; } = 1;
    public long FlatDamage { get; init; }
    public double DamageRatioLevelBonus { get; init; }
    public double FlatDamageLevelBonus { get; init; }
    public double HealingRatio { get; init; }
    public long FlatHealing { get; init; }
    public double HealingRatioLevelBonus { get; init; }
    public double FlatHealingLevelBonus { get; init; }
    public double DamageOverTimeRatio { get; init; }
    public int DamageOverTimeMilliseconds { get; init; }
    public int DamageOverTimeTickMilliseconds { get; init; } = 3_000;
    public double StunChance { get; init; }
    public int StunMilliseconds { get; init; }
    public double BlindChance { get; init; }
    public int BlindMilliseconds { get; init; }
    public double SilenceChance { get; init; }
    public int SilenceMilliseconds { get; init; }
    public double PenetrationRatio { get; init; }
    // 与公开战斗模拟器的 ability effect 字段保持同一语义：护甲增伤按攻击方总护甲比例计算，吸血只在命中后生效。
    public double ArmorDamageRatio { get; init; }
    public double HpDrainRatio { get; init; }
    public double PierceChance { get; init; }
    public double TriggerHpRatio { get; init; } = 1;
    public IReadOnlyList<DaoxuanCombatTrigger> DefaultTriggers { get; init; } = [];
}

public sealed class DaoxuanCombatantDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DaoxuanCharacterVisualDefinition Visual { get; init; } = new();
    public DaoxuanCombatStyle Style { get; init; } = DaoxuanCombatStyle.Body;
    public DaoxuanDamageType DamageType { get; init; } = DaoxuanDamageType.Nature;
    public long MaxHitpoints { get; init; }
    public long MaxManapoints { get; init; }
    public int AttackIntervalMilliseconds { get; init; }
    public double AccuracyRating { get; init; }
    public double EvasionRating { get; init; }
    public long MaxDamage { get; init; }
    public long DefensiveMaxDamage { get; init; }
    public double Armor { get; init; }
    public double WaterResistance { get; init; }
    public double NatureResistance { get; init; }
    public double FireResistance { get; init; }
    public double Tenacity { get; init; }
    public double PierceChance { get; init; }
    public double CurseRatio { get; init; }
    public double FuryRatio { get; init; }
    public double WeakenRatio { get; init; }
    public double LifeStealRatio { get; init; }
    public double ArmorDamageRatio { get; init; }
    public double HpDrainRatio { get; init; }
    public double CriticalChance { get; init; }
    public double CriticalDamageRatio { get; init; }
    public double CriticalAvoidance { get; init; }
    public double DamageAbsorptionRatio { get; init; }
    public double BreakRating { get; init; }
    public double ParryRating { get; init; }
    public double SkillIntensity { get; init; } = 1.0;
    public double DynamicResistanceFactorK { get; init; } = 1.0;
    public double ActionSpeedReductionRatio { get; init; }
    public double CooldownReductionRatio { get; init; }
    public double HpRegenPerTenSeconds { get; init; } = 0.0;
    public double MpRegenPerTenSeconds { get; init; } = 0.0;
    public int EnrageTimeMilliseconds { get; init; }
    public int AttackLevel { get; init; } = 1;
    public long Experience { get; init; }
    public long SpiritMin { get; init; }
    public long SpiritMax { get; init; }
    public IReadOnlyList<DaoxuanCombatDropDefinition> DropTable { get; init; } = [];
    public IReadOnlyList<DaoxuanCombatDropDefinition> RareDropTable { get; init; } = [];
    public IReadOnlyList<DaoxuanCombatAbilityDefinition> Abilities { get; init; } = [];
}

/// <summary>
/// 普通战区的一项随机生成数据。每次抽取相互独立，允许同一种敌人重复出现。
/// </summary>
public sealed class DaoxuanCombatSpawnDefinition
{
    public required DaoxuanCombatantDefinition Enemy { get; init; }
    public double Weight { get; init; } = 1;
    public int Strength { get; init; } = 1;
}

/// <summary>
/// 已装备的修仙物品。强化等级只引用服务端目录中的静态定义，不允许客户端提交任意战斗属性。
/// </summary>
public sealed class DaoxuanEquippedItem
{
    public required string ItemId { get; set; }
    public int EnhancementLevel { get; set; }

    public DaoxuanEquippedItem Clone() => new()
    {
        ItemId = ItemId,
        EnhancementLevel = EnhancementLevel
    };
}

/// <summary>
/// 装备槽位聚合后的战斗属性。数值使用公开数据的百分比增量语义。
/// </summary>
public sealed record DaoxuanEquipmentCombatStats(
    double AccuracyRatio = 0,
    double DamageRatio = 0,
    double AttackSpeedRatio = 0,
    double EvasionRatio = 0,
    double Armor = 0,
    double WaterResistance = 0,
    double NatureResistance = 0,
    double FireResistance = 0,
    double Tenacity = 0,
    double PenetrationRatio = 0,
    double PierceChance = 0,
    double CurseRatio = 0,
    double FuryRatio = 0,
    double WeakenRatio = 0,
    double LifeStealRatio = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0,
    double CriticalChance = 0,
    double CriticalDamageRatio = 0,
    double CriticalAvoidance = 0,
    double DamageAbsorptionRatio = 0,
    double BreakRating = 0,
    double ParryRating = 0,
    double SkillIntensity = 0,
    double ActionSpeedRatio = 0,
    double CooldownReductionRatio = 0,
    int AttackIntervalMilliseconds = 0,
    DaoxuanCombatStyle? Style = null,
    DaoxuanDamageType? DamageType = null);

/// <summary>
/// 可被装备的静态目录项。强化奖励按公开的总倍率表应用到 EnhancementBonus 字段。
/// </summary>
public sealed class DaoxuanCombatEquipmentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slot { get; init; }
    public int Tier { get; init; } = 1;
    public DaoxuanEquipmentCombatStats Stats { get; init; } = new();
    public DaoxuanEquipmentCombatStats EnhancementBonus { get; init; } = new();
}

public sealed class DaoxuanCombatEncounterDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    // 旧版单敌人入口继续保留；没有随机生成表时仍按该字段生成一名敌人。
    public required DaoxuanCombatantDefinition Enemy { get; init; }
    public IReadOnlyList<DaoxuanCombatSpawnDefinition> RandomSpawns { get; init; } = [];
    public int MaxSpawnCount { get; init; } = 1;
    public int MaxTotalStrength { get; init; } = 1;
    public IReadOnlyList<DaoxuanCombatantDefinition> BossEnemies { get; init; } = [];
    public int BattlesPerBoss { get; init; }
    public int SearchMilliseconds { get; init; } = 5_000;
    public int RespawnMilliseconds { get; init; } = 3_000;
    public int PlayerRespawnMilliseconds { get; init; } = 180_000;
    public long ExperiencePerWin { get; init; }
    public long SpiritMinPerWin { get; init; }
    public long SpiritMaxPerWin { get; init; }
    public int EnrageTimeMilliseconds { get; init; } = 180_000;
    public int EnrageTickMilliseconds { get; init; } = 60_000;
    public int MaxEnrageStacks { get; init; } = 10;
    public double EnrageRatioPerStack { get; init; } = 0.10;
    public IReadOnlyList<DaoxuanCombatDropDefinition> DropTable { get; init; } = [];
    public IReadOnlyList<DaoxuanCombatDropDefinition> RareDropTable { get; init; } = [];

    // 旧版单掉落字段保留，便于读取早期快照和第三方调用方。
    public string? DropItemId { get; init; }
    public double DropChance { get; init; }
    public long DropQuantity { get; init; } = 1;
}

public sealed record DaoxuanCombatBuffApplication(
    string BuffId,
    string Name,
    int Stacks,
    int MaxStacks,
    double QiGainMultiplier,
    double AttackLevelRatio,
    double DamageRatio,
    double AttackSpeedRatio,
    string SourceItemId,
    long ExpiresAfterMilliseconds);

public sealed record DaoxuanCombatEnemyState(
    int Slot,
    string EnemyId,
    string Name,
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    bool IsDefeated,
    int EnrageStacks);

public sealed record DaoxuanCombatPlayerState(
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    bool IsDefeated);

public sealed record DaoxuanCombatAttributeState(
    long MaxHitpoints,
    long MaxManapoints,
    int AttackIntervalMilliseconds,
    double AccuracyRating,
    double MaxDamage,
    double EvasionRating,
    double Armor,
    double WaterResistance,
    double NatureResistance,
    double FireResistance,
    DaoxuanCombatStyle Style,
    DaoxuanDamageType DamageType,
    double Tenacity,
    double HpRegenPerTenSeconds,
    double MpRegenPerTenSeconds,
    double PenetrationRatio,
    double PierceChance,
    double CurseRatio,
    double FuryRatio,
    double WeakenRatio,
    double LifeStealRatio,
    double ArmorDamageRatio,
    double HpDrainRatio,
    double DamageTakenRatio,
    double CriticalChance,
    double CriticalDamageRatio,
    double CriticalAvoidance,
    double DamageAbsorptionRatio,
    double BreakRating = 0,
    double ParryRating = 0,
    double SkillIntensity = 1.0,
    double DynamicResistanceFactorK = 1.0,
    double ActionSpeedReductionRatio = 0,
    double CooldownReductionRatio = 0,
    double CriticalRating = 0,
    double CriticalAvoidanceRating = 0);

public sealed record DaoxuanCombatActionState(
    string Name,
    string Kind,
    long ElapsedMilliseconds,
    long DurationMilliseconds);

public sealed record DaoxuanCombatRunResult(
    string EncounterId,
    long SimulatedMilliseconds,
    int Encounters,
    int Wins,
    int Losses,
    long DamageDealt,
    long DamageTaken,
    double Experience,
    long Spirit,
    long DroppedQuantity,
    IReadOnlyDictionary<string, long> ConsumedItems,
    IReadOnlyList<DaoxuanCombatBuffApplication> BuffApplications,
    IReadOnlyList<string> Events,
    int MaxEnrageStacks = 0,
    long HitpointsRegenerated = 0,
    long ManapointsRegenerated = 0,
    int ProcessedEvents = 0,
    long HitpointsRecoveredFromConsumables = 0,
    long ManapointsRecoveredFromConsumables = 0,
    long HitpointsRecoveredFromAbilities = 0,
    IReadOnlyDictionary<string, long>? DroppedItems = null,
    IReadOnlyDictionary<string, long>? RareDroppedItems = null,
    int EnemiesDefeated = 0,
    IReadOnlyList<DaoxuanCombatEnemyState>? FinalEnemies = null,
    DaoxuanCombatPlayerState? FinalPlayer = null,
    DaoxuanCombatActionState? PlayerAction = null,
    DaoxuanCombatActionState? EnemyAction = null,
    long? OutcomeMilliseconds = null,
    IReadOnlyDictionary<string, long>? PlayerAbilityCooldowns = null,
    long? NextEventMilliseconds = null,
    IReadOnlyList<long>? PlayerConsumableCooldowns = null);
