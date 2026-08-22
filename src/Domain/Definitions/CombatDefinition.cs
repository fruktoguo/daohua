// ==============================================================================
// 文件名称: CombatDefinition.cs
// 主要职责: 定义修仙战斗系统相关的技能、战斗实体、掉落物、刷怪生成与遭遇关卡静态只读原型。
// 核心逻辑 / 定义内容:
//   1. 描述战斗技能、功法效果、冷却与触发器。
//   2. 描述怪物与战斗单位六维属性、抗性与掉落表。
//   3. 描述战斗遭遇战、首领波次与阶段时间边界。
//   4. 采用 sealed record 与只读不可变集合，不混入任何运行时状态。
// ==============================================================================

namespace Domain.Definitions;

using System.Collections.Immutable;
using Domain.Common;
using Domain.Models;

/// <summary>
/// 角色或怪物的视觉形象渲染配置静态只读定义。
/// </summary>
public sealed record CharacterVisualDefinition
{
    /// <summary>渲染器类型（如 text 文字图标、glyph 字符、image 贴图等）。</summary>
    public VisualRendererKind Renderer { get; init; } = VisualRendererKind.Text;

    /// <summary>资源地址或图标字符。</summary>
    public string Source { get; init; } = "";

    /// <summary>封面海报缩略图地址。</summary>
    public string? Poster { get; init; }
}

/// <summary>
/// 功法技能与消耗品触发条件规则静态只读配置定义。
/// </summary>
public sealed record CombatTriggerDefinition
{
    /// <summary>触发条件观察的目标对象。</summary>
    public CombatTriggerDependency Dependency { get; init; } = CombatTriggerDependency.Self;

    /// <summary>判定的条件类型。</summary>
    public CombatTriggerCondition Condition { get; init; } = CombatTriggerCondition.CurrentHitpointsPercentage;

    /// <summary>比较运算符。</summary>
    public CombatTriggerComparator Comparator { get; init; } = CombatTriggerComparator.LessThanOrEqual;

    /// <summary>比较阈值数值。</summary>
    public double Value { get; init; }

    /// <summary>指定检查的 Buff ID（若适用）。</summary>
    public string? BuffId { get; init; }
}

/// <summary>
/// 战斗击败敌人的掉落物概率与数量静态只读配置定义。
/// </summary>
public sealed record CombatDropDefinition
{
    /// <summary>掉落物品强类型 ID。</summary>
    public required ItemId ItemId { get; init; }

    /// <summary>掉落概率（0.0 ~ 1.0）。</summary>
    public double DropChance { get; init; }

    /// <summary>单次掉落最小数量。</summary>
    public double MinQuantity { get; init; } = 1.0;

    /// <summary>单次掉落最大数量。</summary>
    public double MaxQuantity { get; init; } = 1.0;
}

/// <summary>
/// 战斗技能与功法的静态只读配置数据。
/// </summary>
public sealed record CombatAbilityDefinition
{
    /// <summary>技能唯一强类型标识 ID。</summary>
    public required AbilityId Id { get; init; }

    /// <summary>技能名称。</summary>
    public required string Name { get; init; }

    /// <summary>技能所属武道流派。</summary>
    public CombatStyle Style { get; init; }

    /// <summary>伤害属性类型。</summary>
    public DamageType DamageType { get; init; }

    /// <summary>技能基础冷却时间（毫秒）。</summary>
    public double CooldownMilliseconds { get; init; }

    /// <summary>技能吟唱/施法读条时间（毫秒）。</summary>
    public double CastMilliseconds { get; init; }

    /// <summary>施法消耗法力/灵力值。</summary>
    public double ManaCost { get; init; }

    /// <summary>主要效果类型（伤害/治疗）。</summary>
    public CombatAbilityEffectType EffectType { get; init; } = CombatAbilityEffectType.Damage;

    /// <summary>目标选择范围。</summary>
    public CombatAbilityTargetType TargetType { get; init; } = CombatAbilityTargetType.Enemy;

    /// <summary>基础伤害倍率。</summary>
    public double DamageRatio { get; init; } = 1.0;

    /// <summary>基础固定附加伤害。</summary>
    public double FlatDamage { get; init; }

    /// <summary>每级技能附加伤害倍率成长。</summary>
    public double DamageRatioLevelBonus { get; init; }

    /// <summary>每级技能附加固定伤害成长。</summary>
    public double FlatDamageLevelBonus { get; init; }

    /// <summary>基础治疗倍率。</summary>
    public double HealingRatio { get; init; }

    /// <summary>基础固定治疗量。</summary>
    public double FlatHealing { get; init; }

    /// <summary>每级治疗倍率成长。</summary>
    public double HealingRatioLevelBonus { get; init; }

    /// <summary>每级固定治疗量成长。</summary>
    public double FlatHealingLevelBonus { get; init; }

    /// <summary>持续伤害 (DoT) 基础伤害倍率。</summary>
    public double DamageOverTimeRatio { get; init; }

    /// <summary>持续伤害总持续时间（毫秒）。</summary>
    public double DamageOverTimeMilliseconds { get; init; }

    /// <summary>持续伤害跳字结算间隔（毫秒，默认 3000ms）。</summary>
    public double DamageOverTimeTickMilliseconds { get; init; } = 3000.0;

    /// <summary>眩晕附加概率（0.0 ~ 1.0）。</summary>
    public double StunChance { get; init; }

    /// <summary>眩晕持续时间（毫秒）。</summary>
    public double StunMilliseconds { get; init; }

    /// <summary>致盲附加概率（0.0 ~ 1.0）。</summary>
    public double BlindChance { get; init; }

    /// <summary>致盲持续时间（毫秒）。</summary>
    public double BlindMilliseconds { get; init; }

    /// <summary>沉默附加概率（0.0 ~ 1.0）。</summary>
    public double SilenceChance { get; init; }

    /// <summary>沉默持续时间（毫秒）。</summary>
    public double SilenceMilliseconds { get; init; }

    /// <summary>防御穿透比例。</summary>
    public double PenetrationRatio { get; init; }

    /// <summary>根据施法者总护甲附加伤害比例（攻击方护甲联动增伤）。</summary>
    public double ArmorDamageRatio { get; init; }

    /// <summary>技能命中吸血比例。</summary>
    public double HpDrainRatio { get; init; }

    /// <summary>无视防御的穿透一击概率。</summary>
    public double PierceChance { get; init; }

    /// <summary>默认血量触发阈值比例。</summary>
    public double TriggerHpRatio { get; init; } = 1.0;

    /// <summary>技能默认配置的触发规则列表。</summary>
    public ImmutableArray<CombatTriggerDefinition> DefaultTriggers { get; init; } = [];
}

/// <summary>
/// 战斗实体（怪物、Boss、NPC 或玩家镜像）的基础数值与能力配置静态只读原型。
/// </summary>
public sealed record CombatantDefinition
{
    /// <summary>实体唯一原型 ID。</summary>
    public required string Id { get; init; }

    /// <summary>实体名称。</summary>
    public required string Name { get; init; }

    /// <summary>视觉外观配置。</summary>
    public CharacterVisualDefinition Visual { get; init; } = new();

    /// <summary>战斗流派。</summary>
    public CombatStyle Style { get; init; } = CombatStyle.Body;

    /// <summary>主要伤害类型。</summary>
    public DamageType DamageType { get; init; } = DamageType.Nature;

    /// <summary>最大生命值上限。</summary>
    public double MaxHitpoints { get; init; }

    /// <summary>最大法力/灵力值上限。</summary>
    public double MaxManapoints { get; init; }

    /// <summary>普通攻击间隔（毫秒）。</summary>
    public double AttackIntervalMilliseconds { get; init; } = 1000.0;

    /// <summary>命中等级/精准值。</summary>
    public double AccuracyRating { get; init; }

    /// <summary>闪避等级/身法值。</summary>
    public double EvasionRating { get; init; }

    /// <summary>基础普攻最大伤害上限。</summary>
    public double MaxDamage { get; init; }

    /// <summary>防守反击最大伤害。</summary>
    public double DefensiveMaxDamage { get; init; }

    /// <summary>护甲/物理减伤值。</summary>
    public double Armor { get; init; }

    /// <summary>水系抗性点数。</summary>
    public double WaterResistance { get; init; }

    /// <summary>木系抗性点数。</summary>
    public double NatureResistance { get; init; }

    /// <summary>火系抗性点数。</summary>
    public double FireResistance { get; init; }

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

    /// <summary>暴击等级分。</summary>
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
    public double HpRegen { get; init; }

    /// <summary>每秒法力自然回复量。</summary>
    public double MpRegen { get; init; }

    /// <summary>每 10 秒生命回复量。</summary>
    public double HpRegenPerTenSeconds { get; init; }

    /// <summary>每 10 秒法力回复量。</summary>
    public double MpRegenPerTenSeconds { get; init; }

    /// <summary>狂暴狂热倒计时（毫秒，超时后进入狂暴增伤）。</summary>
    public double EnrageTimeMilliseconds { get; init; }

    /// <summary>攻击修炼等级。</summary>
    public double AttackLevel { get; init; } = 1.0;

    /// <summary>击败该实体产出的经验倍率。</summary>
    public double ExperienceMultiplier { get; init; } = 1.0;

    /// <summary>击败基础经验值。</summary>
    public double Experience { get; init; }

    /// <summary>掉落灵石最小数量。</summary>
    public double SpiritMin { get; init; }

    /// <summary>掉落灵石最大数量。</summary>
    public double SpiritMax { get; init; }

    /// <summary>常规掉落物品表。</summary>
    public ImmutableArray<CombatDropDefinition> DropTable { get; init; } = [];

    /// <summary>稀有掉落物品表。</summary>
    public ImmutableArray<CombatDropDefinition> RareDropTable { get; init; } = [];

    /// <summary>携带并自动释放的主动技能列表。</summary>
    public ImmutableArray<CombatAbilityDefinition> Abilities { get; init; } = [];
}

/// <summary>
/// 普通战区的一项随机刷怪静态只读配置数据。
/// </summary>
public sealed record CombatSpawnDefinition
{
    /// <summary>生成的敌人模板。</summary>
    public required CombatantDefinition Enemy { get; init; }

    /// <summary>生成权重概率。</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>怪物战力强度系数。</summary>
    public double Strength { get; init; } = 1.0;
}

/// <summary>
/// 战斗遭遇/副本关卡完整静态只读配置定义。
/// </summary>
public sealed record CombatEncounterDefinition
{
    /// <summary>遭遇关卡强类型 ID。</summary>
    public required EncounterId Id { get; init; }

    /// <summary>关卡名称。</summary>
    public required string Name { get; init; }

    /// <summary>默认/单人保底敌人配置。</summary>
    public required CombatantDefinition Enemy { get; init; }

    /// <summary>随机小怪生成列表。</summary>
    public ImmutableArray<CombatSpawnDefinition> RandomSpawns { get; init; } = [];

    /// <summary>单场战斗最大同屏刷怪数量。</summary>
    public int MaxSpawnCount { get; init; } = 1;

    /// <summary>单场战斗最大敌人总战力上限。</summary>
    public double MaxTotalStrength { get; init; } = 1.0;

    /// <summary>关底 Boss 敌人列表。</summary>
    public ImmutableArray<CombatantDefinition> BossEnemies { get; init; } = [];

    /// <summary>每通关多少次小怪战斗遭遇一次 Boss。</summary>
    public int BattlesPerBoss { get; init; }

    /// <summary>寻敌阶段基础耗时（毫秒）。</summary>
    public double SearchMilliseconds { get; init; } = 5000.0;

    /// <summary>敌怪击败后刷新等待耗时（毫秒）。</summary>
    public double RespawnMilliseconds { get; init; } = 3000.0;

    /// <summary>玩家阵亡后复活倒计时（毫秒）。</summary>
    public double PlayerRespawnMilliseconds { get; init; } = 180000.0;

    /// <summary>通关单场胜利基础经验奖励。</summary>
    public double ExperiencePerWin { get; init; }

    /// <summary>通关单场胜利掉落灵石下限。</summary>
    public double SpiritMinPerWin { get; init; }

    /// <summary>通关单场胜利掉落灵石上限。</summary>
    public double SpiritMaxPerWin { get; init; }

    /// <summary>战斗超时狂暴时间阈值（毫秒）。</summary>
    public double EnrageTimeMilliseconds { get; init; } = 180000.0;

    /// <summary>狂暴层数增加间隔（毫秒）。</summary>
    public double EnrageTickMilliseconds { get; init; } = 60000.0;

    /// <summary>最大狂暴叠加层数。</summary>
    public int MaxEnrageStacks { get; init; } = 10;

    /// <summary>每层狂暴攻击提升比例。</summary>
    public double EnrageRatioPerStack { get; init; } = 0.10;

    /// <summary>副本全局常规掉落表。</summary>
    public ImmutableArray<CombatDropDefinition> DropTable { get; init; } = [];

    /// <summary>副本全局稀有掉落表。</summary>
    public ImmutableArray<CombatDropDefinition> RareDropTable { get; init; } = [];
}
