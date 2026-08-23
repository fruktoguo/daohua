// ==============================================================================
// 文件名称: CombatModels.cs
// 主要职责: 定义修仙世界战斗系统相关的核心领域模型、触发器、技能效果类型、战斗行动、战斗日志与遭遇/结算运行时状态。
// 核心逻辑 / 定义内容:
//   1. 战斗视觉、目标与自动化触发器系统（CharacterVisualDefinition, CombatTriggerDependency, CombatTrigger 等）
//   2. 技能释放与战斗属性定义（CombatAbilitySlot, CombatStats 等）
//   3. 战斗日志、回合结算与历练状态模型（CombatTurnLog, CombatResult, CombatExpeditionState 等）
// ==============================================================================
namespace Domain.Models;

/// <summary>
/// 角色或怪物的视觉形象渲染配置定义。
/// </summary>
public sealed class CharacterVisualDefinition
{
    /// <summary>渲染器类型（如 text 文字图标、glyph 字符、image 贴图等）。</summary>
    public VisualRendererKind Renderer { get; init; } = VisualRendererKind.Text;
    /// <summary>资源地址或图标字符。</summary>
    public string Source { get; init; } = "";
    /// <summary>封面海报缩略图地址。</summary>
    public string? Poster { get; init; }
}

/// <summary>
/// 触发条件的观察对象。友方/敌方极值依赖在当前单人战斗拓扑下分别解析为玩家自身与当前目标；
/// 组队战斗接入后，极值依赖应改为在对应队伍的存活成员集合上求值。
/// </summary>
public enum CombatTriggerDependency
{
    /// <summary>自身角色。</summary>
    Self = 0,
    /// <summary>当前选中的目标敌怪。</summary>
    TargetEnemy = 1,
    /// <summary>友方阵营中当前生命最低者。</summary>
    AllyLowestHitpoints = 2,
    /// <summary>友方阵营中当前生命最高者。</summary>
    AllyHighestHitpoints = 3,
    /// <summary>敌方阵营中当前生命最低者。</summary>
    EnemyLowestHitpoints = 4,
    /// <summary>敌方阵营中当前生命最高者。</summary>
    EnemyHighestHitpoints = 5
}

/// <summary>
/// 战斗触发器检查的具体数值或状态条件枚举。
/// </summary>
public enum CombatTriggerCondition
{
    /// <summary>当前生命绝对值。</summary>
    CurrentHitpoints = 0,
    /// <summary>当前法力绝对值。</summary>
    CurrentManapoints = 1,
    /// <summary>已损失生命绝对值。</summary>
    MissingHitpoints = 2,
    /// <summary>已损失法力绝对值。</summary>
    MissingManapoints = 3,
    /// <summary>指定 Buff 的叠加层数。</summary>
    BuffStacks = 4,
    /// <summary>是否处于眩晕状态。</summary>
    StunStatus = 5,
    /// <summary>是否处于致盲状态。</summary>
    BlindStatus = 6,
    /// <summary>是否处于沉默状态。</summary>
    SilenceStatus = 7,
    /// <summary>当前生命百分比（0.0 ~ 1.0）。</summary>
    CurrentHitpointsPercentage = 8,
    /// <summary>已损失生命百分比（0.0 ~ 1.0）。</summary>
    MissingHitpointsPercentage = 9,
    /// <summary>当前法力百分比（0.0 ~ 1.0）。</summary>
    CurrentManapointsPercentage = 10,
    /// <summary>已损失法力百分比（0.0 ~ 1.0）。</summary>
    MissingManapointsPercentage = 11
}

/// <summary>
/// 战斗触发器比较判定操作符枚举。
/// </summary>
public enum CombatTriggerComparator
{
    /// <summary>小于或等于 (≤)。</summary>
    LessThanOrEqual = 0,
    /// <summary>大于或等于 (≥)。</summary>
    GreaterThanOrEqual = 1,
    /// <summary>处于激活状态。</summary>
    IsActive = 2,
    /// <summary>处于未激活状态。</summary>
    IsInactive = 3,
    /// <summary>严格小于 (<)。</summary>
    LessThan = 4,
    /// <summary>严格大于 (>)。</summary>
    GreaterThan = 5
}

/// <summary>
/// 功法技能和战斗消耗品共用的触发条件规则。一个槽位内的多条条件按 AND 逻辑同时满足才触发。
/// </summary>
public sealed class CombatTrigger
{
    /// <summary>触发条件观察的目标对象。</summary>
    public CombatTriggerDependency Dependency { get; set; }
    /// <summary>判定的条件类型。</summary>
    public CombatTriggerCondition Condition { get; set; }
    /// <summary>比较运算符。</summary>
    public CombatTriggerComparator Comparator { get; set; }
    /// <summary>比较阈值数值。</summary>
    public double Value { get; set; }
    /// <summary>指定检查的 Buff ID（若适用）。</summary>
    public string? BuffId { get; set; }

    /// <summary>克隆触发器规则对象。</summary>
    public CombatTrigger Clone() => new()
    {
        Dependency = Dependency,
        Condition = Condition,
        Comparator = Comparator,
        Value = Value,
        BuffId = BuffId
    };
}

/// <summary>
/// 战斗技能释放产生的主要效果类型枚举。
/// </summary>
public enum CombatAbilityEffectType
{
    /// <summary>对目标造成伤害。</summary>
    Damage = 0,
    /// <summary>为目标回复生命。</summary>
    Heal = 1
}

/// <summary>
/// 功法的权威目标范围。当前战斗只有一名玩家，但仍保留完整范围，便于后续扩展组队战斗。
/// </summary>
public enum CombatAbilityTargetType
{
    /// <summary>单个目标敌人。</summary>
    Enemy = 0,
    /// <summary>全体敌方单位（群攻）。</summary>
    AllEnemies = 1,
    /// <summary>施法者自身。</summary>
    Self = 2,
    /// <summary>全体友方单位。</summary>
    AllAllies = 3,
    /// <summary>生命值最低的友方单位。</summary>
    LowestHitpointsAlly = 4
}

/// <summary>
/// 战斗击败敌人的掉落物概率与数量配置定义。
/// </summary>
public sealed class CombatDropDefinition
{
    /// <summary>掉落物品 ID。</summary>
    public required string ItemId { get; init; }
    /// <summary>掉落概率（0.0 ~ 1.0）。</summary>
    public double DropChance { get; init; }
    /// <summary>单次掉落最小数量。</summary>
    public long MinQuantity { get; init; } = 1;
    /// <summary>单次掉落最大数量。</summary>
    public long MaxQuantity { get; init; } = 1;
}

/// <summary>
/// 战斗技能与功法的静态配置数据。数值存放在目录中，保证确定性结算。
/// </summary>
public sealed class CombatAbilityDefinition
{
    /// <summary>技能唯一标识 ID。</summary>
    public required string Id { get; init; }
    /// <summary>技能名称。</summary>
    public required string Name { get; init; }
    /// <summary>技能所属武道流派。</summary>
    public CombatStyle Style { get; init; }
    /// <summary>伤害大类（物理/术法）。</summary>
    public DamageCategory Category { get; init; } = DamageCategory.Physical;
    /// <summary>五行属性类型。</summary>
    public Element Element { get; init; } = Element.None;
    /// <summary>技能基础冷却时间（毫秒）。</summary>
    public int CooldownMilliseconds { get; init; }
    /// <summary>技能吟唱/施法读条时间（毫秒）。</summary>
    public int CastMilliseconds { get; init; }
    /// <summary>施法消耗法力值。</summary>
    public long ManaCost { get; init; }
    /// <summary>主要效果类型（伤害/治疗）。</summary>
    public CombatAbilityEffectType EffectType { get; init; } = CombatAbilityEffectType.Damage;
    /// <summary>目标选择范围。</summary>
    public CombatAbilityTargetType TargetType { get; init; } = CombatAbilityTargetType.Enemy;
    /// <summary>基础伤害倍率。</summary>
    public double DamageRatio { get; init; } = 1;
    /// <summary>基础固定附加伤害。</summary>
    public long FlatDamage { get; init; }
    /// <summary>每级技能附加伤害倍率成长。</summary>
    public double DamageRatioLevelBonus { get; init; }
    /// <summary>每级技能附加固定伤害成长。</summary>
    public double FlatDamageLevelBonus { get; init; }
    /// <summary>基础治疗倍率。</summary>
    public double HealingRatio { get; init; }
    /// <summary>基础固定治疗量。</summary>
    public long FlatHealing { get; init; }
    /// <summary>每级治疗倍率成长。</summary>
    public double HealingRatioLevelBonus { get; init; }
    /// <summary>每级固定治疗量成长。</summary>
    public double FlatHealingLevelBonus { get; init; }
    /// <summary>持续伤害 (DoT) 基础伤害倍率。</summary>
    public double DamageOverTimeRatio { get; init; }
    /// <summary>持续伤害总持续时间（毫秒）。</summary>
    public int DamageOverTimeMilliseconds { get; init; }
    /// <summary>持续伤害跳字结算间隔（毫秒，默认 3000ms）。</summary>
    public int DamageOverTimeTickMilliseconds { get; init; } = 3_000;
    /// <summary>眩晕附加概率（0.0 ~ 1.0）。</summary>
    public double StunChance { get; init; }
    /// <summary>眩晕持续时间（毫秒）。</summary>
    public int StunMilliseconds { get; init; }
    /// <summary>致盲附加概率（0.0 ~ 1.0）。</summary>
    public double BlindChance { get; init; }
    /// <summary>致盲持续时间（毫秒）。</summary>
    public int BlindMilliseconds { get; init; }
    /// <summary>沉默附加概率（0.0 ~ 1.0）。</summary>
    public double SilenceChance { get; init; }
    /// <summary>沉默持续时间（毫秒）。</summary>
    public int SilenceMilliseconds { get; init; }
    /// <summary>防御穿透比例。</summary>
    public double PenetrationRatio { get; init; }
    /// <summary>根据施法者总护甲附加伤害比例（攻击方护甲联动增伤）。</summary>
    public double ArmorDamageRatio { get; init; }
    /// <summary>技能命中吸血比例。</summary>
    public double HpDrainRatio { get; init; }
    /// <summary>无视防御的穿透一击概率。</summary>
    public double PierceChance { get; init; }
    /// <summary>默认血量触发阈值比例。</summary>
    public double TriggerHpRatio { get; init; } = 1;
    /// <summary>技能默认配置的触发规则列表。</summary>
    public IReadOnlyList<CombatTrigger> DefaultTriggers { get; init; } = [];
}

/// <summary>
/// 战斗实体（怪物、Boss、NPC 或玩家镜像）的基础数值与能力配置原型。
/// </summary>
public sealed class CombatantDefinition
{
    /// <summary>实体唯一原型 ID。</summary>
    public required string Id { get; init; }
    /// <summary>实体名称。</summary>
    public required string Name { get; init; }
    /// <summary>视觉外观配置。</summary>
    public CharacterVisualDefinition Visual { get; init; } = new();
    /// <summary>战斗流派。</summary>
    public CombatStyle Style { get; init; } = CombatStyle.Body;
    /// <summary>主要伤害大类。</summary>
    public DamageCategory Category { get; init; } = DamageCategory.Physical;
    /// <summary>主要五行属性。</summary>
    public Element Element { get; init; } = Element.None;
    /// <summary>最大生命值上限。</summary>
    public long MaxHitpoints { get; init; }
    /// <summary>最大法力值上限。</summary>
    public long MaxManapoints { get; init; }
    /// <summary>普通攻击间隔（毫秒）。</summary>
    public int AttackIntervalMilliseconds { get; init; }
    /// <summary>命中等级/精准值。</summary>
    public double AccuracyRating { get; init; }
    /// <summary>闪避等级/身法值。</summary>
    public double EvasionRating { get; init; }
    /// <summary>基础普攻最大伤害上限。</summary>
    public long MaxDamage { get; init; }
    /// <summary>防守反击最大伤害。</summary>
    public long DefensiveMaxDamage { get; init; }
    /// <summary>护甲/物理减伤值。</summary>
    public double Armor { get; init; }
    /// <summary>法术抗性点数。</summary>
    public double MagicResistance { get; init; }
    /// <summary>金系抗性点数。</summary>
    public double MetalResistance { get; init; }
    /// <summary>木系抗性点数。</summary>
    public double WoodResistance { get; init; }
    /// <summary>水系抗性点数。</summary>
    public double WaterResistance { get; init; }
    /// <summary>火系抗性点数。</summary>
    public double FireResistance { get; init; }
    /// <summary>土系抗性点数。</summary>
    public double EarthResistance { get; init; }
    /// <summary>韧性/异常状态抗性。</summary>
    public double Tenacity { get; init; }
    /// <summary>贯穿/破甲一击概率。</summary>
    public double PierceChance { get; init; }
    /// <summary>诅咒增伤倍率。</summary>
    public double CurseRatio { get; init; }
    /// <summary>狂暴增伤倍率。</summary>
    public double FuryRatio { get; init; }
    /// <summary>虚弱减伤倍率。</summary>
    public double WeakenRatio { get; init; }
    /// <summary>普攻生命偷取比例。</summary>
    public double LifeStealRatio { get; init; }
    /// <summary>护甲加成伤害比例。</summary>
    public double ArmorDamageRatio { get; init; }
    /// <summary>伤害吸血比例。</summary>
    public double HpDrainRatio { get; init; }
    /// <summary>暴击等级等级分。</summary>
    public double CriticalRating { get; init; }
    /// <summary>暴击概率（0.0 ~ 1.0）。</summary>
    public double CriticalChance { get; init; }
    /// <summary>暴击伤害加成倍率。</summary>
    public double CriticalDamageRatio { get; init; }
    /// <summary>暴击抵抗/抗暴概率。</summary>
    public double CriticalAvoidance { get; init; }
    /// <summary>伤害吸收/减伤比例。</summary>
    public double DamageAbsorptionRatio { get; init; }
    /// <summary>破招等级分。</summary>
    public double BreakRating { get; init; }
    /// <summary>格挡/招架等级分。</summary>
    public double ParryRating { get; init; }
    /// <summary>技能强度修正系数（默认 1.0）。</summary>
    public double SkillIntensity { get; init; } = 1.0;
    /// <summary>动态抗性衰减常数 K。</summary>
    public double DynamicResistanceFactorK { get; init; } = 1.0;
    /// <summary>行动减速比例。</summary>
    public double ActionSpeedReductionRatio { get; init; }
    /// <summary>冷却缩减比例。</summary>
    public double CooldownReductionRatio { get; init; }
    /// <summary>每秒生命自然回复量。</summary>
    public double HpRegen { get; init; } = 0.0;
    /// <summary>每秒法力自然回复量。</summary>
    public double MpRegen { get; init; } = 0.0;
    /// <summary>每 10 秒生命回复量。</summary>
    public double HpRegenPerTenSeconds { get; init; } = 0.0;
    /// <summary>每 10 秒法力回复量。</summary>
    public double MpRegenPerTenSeconds { get; init; } = 0.0;
    /// <summary>狂暴狂热倒计时（毫秒，超时后进入狂暴增伤）。</summary>
    public int EnrageTimeMilliseconds { get; init; }
    /// <summary>阶位品级。</summary>
    public int Tier { get; init; } = 1;
    /// <summary>实体位阶（普通、精英、Boss等）。</summary>
    public UnitRank Rank { get; init; } = UnitRank.Minion;
    /// <summary>攻击修炼等级。</summary>
    public int AttackLevel { get; init; } = 1;
    /// <summary>击败该实体产出的经验倍率。</summary>
    public double ExperienceMultiplier { get; init; } = 1.0;
    /// <summary>击败基础经验值。</summary>
    public long Experience { get; init; }
    /// <summary>掉落灵石最小数量。</summary>
    public long SpiritMin { get; init; }
    /// <summary>掉落灵石最大数量。</summary>
    public long SpiritMax { get; init; }
    /// <summary>单体消耗预算/质量点数（对应刷怪预算扣减）。</summary>
    public double BudgetCost { get; init; } = 10.0;
    /// <summary>常规掉落物品表。</summary>
    public IReadOnlyList<CombatDropDefinition> DropTable { get; init; } = [];
    /// <summary>稀有掉落物品表。</summary>
    public IReadOnlyList<CombatDropDefinition> RareDropTable { get; init; } = [];
    /// <summary>携带并自动释放的主动技能列表。</summary>
    public IReadOnlyList<CombatAbilityDefinition> Abilities { get; init; } = [];
}

/// <summary>
/// 普通战区的一项随机生成数据。每次抽取相互独立，允许同一种敌人重复出现。
/// </summary>
public sealed class CombatSpawnDefinition
{
    /// <summary>生成的敌人模板。</summary>
    public required CombatantDefinition Enemy { get; init; }
    /// <summary>生成权重概率。</summary>
    public double Weight { get; init; } = 1;
    /// <summary>怪物战力强度系数。</summary>
    public int Strength { get; init; } = 1;
}

/// <summary>
/// 已装备的修仙物品。强化等级只引用服务端目录中的静态定义，不允许客户端提交任意战斗属性。
/// </summary>
public sealed class EquippedItem
{
    /// <summary>装备物品 ID。</summary>
    public required string ItemId { get; set; }
    /// <summary>当前强化等级（0 ~ 20）。</summary>
    public int EnhancementLevel { get; set; }

    /// <summary>克隆已装备实例。</summary>
    public EquippedItem Clone() => new()
    {
        ItemId = ItemId,
        EnhancementLevel = EnhancementLevel
    };
}

/// <summary>
/// 装备槽位聚合后的战斗属性。数值使用公开数据的百分比增量语义。
/// </summary>
public sealed record EquipmentCombatStats(
    double AccuracyRatio = 0,
    double DamageRatio = 0,
    double AttackSpeedRatio = 0,
    double EvasionRatio = 0,
    double Armor = 0,
    double MagicResistance = 0,
    double MetalResistance = 0,
    double WoodResistance = 0,
    double WaterResistance = 0,
    double FireResistance = 0,
    double EarthResistance = 0,
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
    CombatStyle? Style = null,
    DamageCategory? Category = null,
    Element? Element = null);

/// <summary>
/// 可被装备的静态目录项。强化奖励按公开的总倍率表应用到 EnhancementBonus 字段。
/// </summary>
public sealed class CombatEquipmentDefinition
{
    /// <summary>装备原型唯一 ID。</summary>
    public required string Id { get; init; }
    /// <summary>装备名称。</summary>
    public required string Name { get; init; }
    /// <summary>装备穿戴槽位（如 weapon 武器、armor 衣服、ring 戒指等）。</summary>
    public required EquipmentSlot Slot { get; init; }
    /// <summary>装备品阶层次。</summary>
    public int Tier { get; init; } = 1;
    /// <summary>装备基础属性修饰。</summary>
    public EquipmentCombatStats Stats { get; init; } = new();
    /// <summary>每级强化的属性增量加成。</summary>
    public EquipmentCombatStats EnhancementBonus { get; init; } = new();
}

/// <summary>
/// 战斗遭遇/副本关卡完整静态配置定义。
/// </summary>
public sealed class CombatEncounterDefinition
{
    /// <summary>遭遇关卡 ID。</summary>
    public required string Id { get; init; }
    /// <summary>关卡名称。</summary>
    public required string Name { get; init; }
    /// <summary>默认/单人保底敌人配置。</summary>
    public required CombatantDefinition Enemy { get; init; }
    /// <summary>随机小怪生成列表。</summary>
    public IReadOnlyList<CombatSpawnDefinition> RandomSpawns { get; init; } = [];
    /// <summary>单场战斗最大同屏刷怪数量。</summary>
    public int MaxSpawnCount { get; init; } = 1;
    /// <summary>单场战斗最大敌人总战力上限。</summary>
    public int MaxTotalStrength { get; init; } = 1;
    /// <summary>关卡危险度刷怪总预算下限。</summary>
    public double MinBudget { get; init; } = 10.0;
    /// <summary>关卡危险度刷怪总预算上限。</summary>
    public double MaxBudget { get; init; } = 50.0;
    /// <summary>每通关多少次遭遇一次精英敌人（默认 5）。</summary>
    public int EliteInterval { get; init; } = 5;
    /// <summary>关卡精英敌人列表。</summary>
    public IReadOnlyList<CombatantDefinition> EliteEnemies { get; init; } = [];
    /// <summary>关底 Boss 敌人列表。</summary>
    public IReadOnlyList<CombatantDefinition> BossEnemies { get; init; } = [];
    /// <summary>每通关多少次小怪战斗遭遇一次 Boss。</summary>
    public int BattlesPerBoss { get; init; }
    /// <summary>寻敌阶段基础耗时（毫秒）。</summary>
    public int SearchMilliseconds { get; init; } = 5_000;
    /// <summary>敌怪击败后刷新等待耗时（毫秒）。</summary>
    public int RespawnMilliseconds { get; init; } = 3_000;
    /// <summary>玩家阵亡后复活倒计时（毫秒）。</summary>
    public int PlayerRespawnMilliseconds { get; init; } = 180_000;
    /// <summary>通关单场胜利基础经验奖励。</summary>
    public long ExperiencePerWin { get; init; }
    /// <summary>通关单场胜利掉落灵石下限。</summary>
    public long SpiritMinPerWin { get; init; }
    /// <summary>通关单场胜利掉落灵石上限。</summary>
    public long SpiritMaxPerWin { get; init; }
    /// <summary>战斗超时狂暴时间阈值（毫秒）。</summary>
    public int EnrageTimeMilliseconds { get; init; } = 180_000;
    /// <summary>狂暴层数增加间隔（毫秒）。</summary>
    public int EnrageTickMilliseconds { get; init; } = 60_000;
    /// <summary>最大狂暴叠加层数。</summary>
    public int MaxEnrageStacks { get; init; } = 10;
    /// <summary>每层狂暴攻击提升比例。</summary>
    public double EnrageRatioPerStack { get; init; } = 0.10;
    /// <summary>副本全局常规掉落表。</summary>
    public IReadOnlyList<CombatDropDefinition> DropTable { get; init; } = [];
    /// <summary>副本全局稀有掉落表。</summary>
    public IReadOnlyList<CombatDropDefinition> RareDropTable { get; init; } = [];
}

/// <summary>
/// 战斗中即时生效 Buff 记录。
/// </summary>
public sealed record CombatBuffApplication(
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

/// <summary>
/// 战斗中单个敌怪的即时生命与狂暴状态。
/// </summary>
public sealed record CombatEnemyState(
    int Slot,
    string EnemyId,
    string Name,
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    bool IsDefeated,
    int EnrageStacks);

/// <summary>
/// 战斗中玩家角色的即时生命与存活状态。
/// </summary>
public sealed record CombatPlayerState(
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    bool IsDefeated);

/// <summary>
/// 经过加点、装备及 Buff 汇总后，参与战斗公式运算的最终属性状态。
/// </summary>
public sealed record CombatAttributeState(
    long MaxHitpoints,
    long MaxManapoints,
    int AttackIntervalMilliseconds,
    double AccuracyRating,
    double MaxDamage,
    double EvasionRating,
    double Armor,
    double MagicResistance,
    double MetalResistance,
    double WoodResistance,
    double WaterResistance,
    double FireResistance,
    double EarthResistance,
    CombatStyle Style,
    DamageCategory Category,
    Element Element,
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

/// <summary>
/// 战斗单位当前执行的动作状态（普攻或技能读条）。
/// </summary>
public sealed record CombatActionState(
    string Name,
    CombatActionKind Kind,
    long ElapsedMilliseconds,
    long DurationMilliseconds);

/// <summary>
/// 战斗推进结算输出结果，包含伤害统计、掉落、消耗品使用明细及最终战斗实体状态。
/// </summary>
public sealed record CombatRunResult(
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
    IReadOnlyList<CombatBuffApplication> BuffApplications,
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
    IReadOnlyList<CombatEnemyState>? FinalEnemies = null,
    CombatPlayerState? FinalPlayer = null,
    CombatActionState? PlayerAction = null,
    CombatActionState? EnemyAction = null,
    long? OutcomeMilliseconds = null,
    IReadOnlyDictionary<string, long>? PlayerAbilityCooldowns = null,
    long? NextEventMilliseconds = null,
    IReadOnlyList<long>? PlayerConsumableCooldowns = null);

/// <summary>
/// 非玩家单位品阶层级量化定义
/// </summary>
public enum NonPlayerUnitTier
{
    /// <summary>
    /// 普通怪：全属性 100% 基准，生命 100% 基准。
    /// </summary>
    Normal = 0,

    /// <summary>
    /// 精英怪：全属性 120% 基准，生命 300% 基准。
    /// </summary>
    Elite = 1,

    /// <summary>
    /// 妖王首领：全属性 150% 基准，生命 1000% 基准。
    /// </summary>
    Boss = 2
}

/// <summary>
/// 百分比曲线换算模式
/// </summary>
public enum PercentCurveMode
{
    /// <summary>
    /// 线性模式（用于：增伤、免伤、仇恨增加、各类属性伤害增免）
    /// 正向：+x% (倍率 1 + x/100)
    /// 负向：除法软衰减 -|x|/(100+|x|)% (倍率 100/(100+|x|))
    /// </summary>
    Linear = 0,

    /// <summary>
    /// 非线性/速率倒数模式（用于：冷却速度、行动速度、灵力消耗减免）
    /// 正向：双曲缩减 x/(100+x)% (倍率 100/(100+x))
    /// 负向：时间/消耗线性延长 -|x|% (倍率 1 + |x|/100)
    /// </summary>
    NonLinear = 1
}

/// <summary>
/// 统一百分比数值类型。
/// 内部储存与计算均使用 double 类型的固定点数 (Points，基准 100 点 = 100%)。
/// 支持在 Linear (线性) 和 NonLinear (非线性) 模式间换算。
/// </summary>
public readonly record struct PercentValue(double Points, PercentCurveMode Mode = PercentCurveMode.Linear)
{
    public static PercentValue Linear(double points) => new(points, PercentCurveMode.Linear);
    public static PercentValue NonLinear(double points) => new(points, PercentCurveMode.NonLinear);

    /// <summary>
    /// 参与战斗公式乘法计算的实际倍率 (Multiplier)
    /// </summary>
    public double Multiplier => Mode switch
    {
        PercentCurveMode.Linear => Points >= 0
            ? 1.0 + Points / 100.0
            : 100.0 / (100.0 + Math.Abs(Points)),

        PercentCurveMode.NonLinear => Points >= 0
            ? 100.0 / (100.0 + Points)
            : 1.0 + Math.Abs(Points) / 100.0,

        _ => 1.0
    };

    /// <summary>
    /// 面板或 UI 展示的实际百分比值 (DisplayPercentage)
    /// </summary>
    public double DisplayPercentage => Mode switch
    {
        PercentCurveMode.Linear => Points >= 0
            ? Points
            : -(Math.Abs(Points) / (100.0 + Math.Abs(Points))) * 100.0,

        PercentCurveMode.NonLinear => Points >= 0
            ? (Points / (100.0 + Points)) * 100.0
            : -Math.Abs(Points),

        _ => 0.0
    };

    /// <summary>
    /// 格式化为百分比文本字符串
    /// </summary>
    public string Format(int decimals = 1)
    {
        var val = DisplayPercentage;
        var prefix = (Mode == PercentCurveMode.Linear && val > 0) ? "+" : "";
        return $"{prefix}{val.ToString($"F{decimals}")}%";
    }
}


