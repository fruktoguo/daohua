// ==============================================================================
// 文件名称: Contracts.cs
// 主要职责: 定义道化太初客户端、服务端与压测共享的数据传输契约与网络通信协议。
// 核心逻辑 / 定义内容:
//   1. 网络信封与包类型标识 (Envelope, IncomingPacket, PacketKinds)。
//   2. 游戏配置 DTO (ActionDefinitionDto, ItemDefinitionDto, AbilityDefinitionDto 等)。
//   3. 玩家快照与增量更新契约 (SnapshotPayload, DeltaPayload)。
//   4. 客户端上行指令与回执 (CommandPayload, AckPayload, ErrorPayload)。
//   5. 挂机补算与市场交易协议契约 (OfflineCatchUpContract, MarketContracts 等)。
// ==============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protocol;

/// <summary>
/// 定义客户端与服务端 WebSocket 通信的数据包类型标识常量。
/// </summary>
public static class PacketKinds
{
    /// <summary>握手与初次连接请求/响应。</summary>
    public const string Hello = "hello";
    /// <summary>玩家全量状态快照同步包。</summary>
    public const string Snapshot = "snapshot";
    /// <summary>玩家增量状态更新包。</summary>
    public const string Delta = "delta";
    /// <summary>客户端上行操作指令包。</summary>
    public const string Command = "command";
    /// <summary>指令执行确认与回执包。</summary>
    public const string Ack = "ack";
    /// <summary>异常或错误信息包。</summary>
    public const string Error = "error";
}

/// <summary>
/// 统一网络传输信封，包装所有通过 WebSocket 发送的协议数据载荷。
/// </summary>
/// <typeparam name="T">载荷数据类型。</typeparam>
/// <param name="Kind">数据包类型标识（见 <see cref="PacketKinds"/>）。</param>
/// <param name="RequestId">请求追踪 ID，用于指令响应关联。</param>
/// <param name="Sequence">消息序列号，保证包序和幂等。</param>
/// <param name="Payload">具体业务数据载荷。</param>
public sealed record Envelope<T>(
    string Kind,
    string RequestId,
    long Sequence,
    T Payload);

/// <summary>
/// 入站未定类型数据包，用于初次反序列化解析信封头部，载荷保持为 <see cref="JsonElement"/> 供后续分发。
/// </summary>
/// <param name="Kind">数据包类型标识。</param>
/// <param name="RequestId">请求追踪 ID。</param>
/// <param name="Sequence">消息序列号。</param>
/// <param name="Payload">未反序列化的 JSON 载荷元素。</param>
public sealed record IncomingPacket(
    string Kind,
    string RequestId,
    long Sequence,
    JsonElement Payload);

/// <summary>
/// 动作定义数据传输对象，描述游戏中可排队执行的基础行为配置。
/// </summary>
/// <param name="Id">动作唯一标识符。</param>
/// <param name="Name">动作显示名称。</param>
/// <param name="Description">动作文本描述。</param>
/// <param name="Type">动作类型分类（如 cultivation、alchemy、gathering 等）。</param>
/// <param name="DurationSeconds">单次执行所需的基础耗时（秒）。</param>
/// <param name="ExecutionKind">执行模式（如 timed 计时模式、combat 战斗模式、travel 移动模式）。</param>
/// <param name="CombatEncounterId">关联的战斗遭遇 ID（若为战斗动作）。</param>
/// <param name="Inputs">执行消耗的物品与数量字典。</param>
/// <param name="Outputs">完成产出的物品与数量字典。</param>
/// <param name="AvailableNodeIds">该动作允许执行的世界节点 ID 列表。</param>
public sealed record ActionDto(
    string Id,
    string Name,
    string Description,
    string Type,
    int DurationSeconds,
    string ExecutionKind = "timed",
    string? CombatEncounterId = null,
    IReadOnlyDictionary<string, long>? Inputs = null,
    IReadOnlyDictionary<string, long>? Outputs = null,
    IReadOnlyList<string>? AvailableNodeIds = null);

/// <summary>
/// 玩家当前动作队列中的单个条目状态传输对象。
/// </summary>
/// <param name="EntryId">队列项唯一标识符。</param>
/// <param name="ActionId">对应的动作模板 ID。</param>
/// <param name="ActionName">动作名称。</param>
/// <param name="Type">动作类型。</param>
/// <param name="Status">执行状态（pending、running、completed、blocked 等）。</param>
/// <param name="ProgressSeconds">当前动作已推进的秒数。</param>
/// <param name="DurationSeconds">单次执行总耗时秒数。</param>
/// <param name="RemainingRepeats">剩余重复执行次数。</param>
/// <param name="ExecutionKind">执行模式。</param>
/// <param name="TravelDestinationNodeId">旅行目的地节点 ID（若为移动动作）。</param>
/// <param name="TravelPathEdgeIds">移动路径上的边 ID 列表。</param>
/// <param name="Expedition">历练/战斗推进状态（若包含实时历练）。</param>
public sealed record QueueEntryDto(
    string EntryId,
    string ActionId,
    string ActionName,
    string Type,
    string Status,
    int ProgressSeconds,
    int DurationSeconds,
    int RemainingRepeats,
    string ExecutionKind = "timed",
    string? TravelDestinationNodeId = null,
    IReadOnlyList<string>? TravelPathEdgeIds = null,
    ExpeditionProgressDto? Expedition = null);

/// <summary>
/// 自动使用物品/消耗品槽位配置传输对象。
/// </summary>
/// <param name="ItemId">绑定的物品 ID。</param>
/// <param name="Quantity">单次触发使用的数量。</param>
/// <param name="Enabled">是否启用该槽位自动触发。</param>
/// <param name="RemainingDurationSeconds">已开启的持续消耗品剩余有效秒数（银河奶牛式）。</param>
/// <param name="Triggers">触发条件规则列表。</param>
public sealed record AutoUseSlotDto(
    string? ItemId,
    int Quantity,
    bool Enabled,
    double RemainingDurationSeconds = 0.0,
    IReadOnlyList<CombatTriggerDto>? Triggers = null);

/// <summary>
/// 玩家动作队列整体状态传输对象。
/// </summary>
/// <param name="Entries">当前队列中的所有动作项列表。</param>
/// <param name="LoopEnabled">是否开启队列循环执行模式。</param>
/// <param name="AutoUseSlots">各动作类型下的自动使用物品槽位配置字典。</param>
public sealed record QueueDto(
    IReadOnlyList<QueueEntryDto> Entries,
    bool LoopEnabled,
    IReadOnlyDictionary<string, IReadOnlyList<AutoUseSlotDto>> AutoUseSlots);

/// <summary>
/// 动作队列当前执行进度的轻量增量更新传输对象。
/// </summary>
/// <param name="EntryId">当前活跃的队列项 ID。</param>
/// <param name="Status">当前状态。</param>
/// <param name="ProgressSeconds">当前已执行进度秒数。</param>
/// <param name="DurationSeconds">总耗时秒数。</param>
/// <param name="Expedition">历练/战斗进度详情。</param>
public sealed record QueueProgressDto(
    string EntryId,
    string Status,
    int ProgressSeconds,
    int DurationSeconds,
    ExpeditionProgressDto? Expedition = null);

/// <summary>
/// 历练/战斗/游历副本的实时推进详情传输对象。
/// </summary>
/// <param name="Phase">当前历练阶段（searching 搜敌、fighting 战斗、recovering 恢复等）。</param>
/// <param name="Progress">当前阶段进度比例（0.0 ~ 1.0）。</param>
/// <param name="ElapsedSeconds">当前阶段已过去的时间（秒）。</param>
/// <param name="DurationSeconds">当前阶段总持续时间（秒）。</param>
/// <param name="TravelledDistance">已行进距离。</param>
/// <param name="TotalDistance">总行进距离。</param>
/// <param name="PlayerHitpoints">玩家当前生命值。</param>
/// <param name="PlayerMaxHitpoints">玩家最大生命值。</param>
/// <param name="PlayerManapoints">玩家当前法力值。</param>
/// <param name="PlayerMaxManapoints">玩家最大法力值。</param>
/// <param name="EnemyHitpoints">敌人当前生命值。</param>
/// <param name="EnemyMaxHitpoints">敌人最大生命值。</param>
/// <param name="EnemyManapoints">敌人当前法力值。</param>
/// <param name="EnemyMaxManapoints">敌人最大法力值。</param>
/// <param name="EnemyName">当前遭遇敌人名称。</param>
/// <param name="Activity">当前实时活动状态描述。</param>
/// <param name="PlayerAction">玩家当前正在执行的战斗动作状态。</param>
/// <param name="EnemyAction">敌人当前正在执行的战斗动作状态。</param>
/// <param name="Activities">历史活动日志列表。</param>
/// <param name="BattleSequence">当前战斗序列号。</param>
/// <param name="AbilityCooldownMilliseconds">玩家各技能剩余冷却时间字典（毫秒）。</param>
/// <param name="ConsumableCooldownMilliseconds">消耗品槽位冷却时间列表（毫秒）。</param>
/// <param name="EncounterId">当前遭遇副本 ID。</param>
public sealed record ExpeditionProgressDto(
    string Phase,
    double Progress,
    int ElapsedSeconds,
    int DurationSeconds,
    double? TravelledDistance = null,
    double? TotalDistance = null,
    long? PlayerHitpoints = null,
    long? PlayerMaxHitpoints = null,
    long? PlayerManapoints = null,
    long? PlayerMaxManapoints = null,
    long? EnemyHitpoints = null,
    long? EnemyMaxHitpoints = null,
    long? EnemyManapoints = null,
    long? EnemyMaxManapoints = null,
    string? EnemyName = null,
    string? EnemyId = null,
    string? Activity = null,
    CombatActionStateDto? PlayerAction = null,
    CombatActionStateDto? EnemyAction = null,
    IReadOnlyList<string>? Activities = null,
    long BattleSequence = 0,
    IReadOnlyDictionary<string, long>? AbilityCooldownMilliseconds = null,
    IReadOnlyList<long>? ConsumableCooldownMilliseconds = null,
    string? EncounterId = null);

/// <summary>
/// 战斗中单个动作（普攻、施法读条等）的实时状态传输对象。
/// </summary>
/// <param name="Name">动作或技能名称。</param>
/// <param name="Kind">动作类型（attack、cast 等）。</param>
/// <param name="ElapsedMilliseconds">动作已执行的毫秒数。</param>
/// <param name="DurationMilliseconds">动作总耗时毫秒数。</param>
/// <param name="CompletesAtUnixMilliseconds">预计完成时的 Unix 毫秒时间戳。</param>
public sealed record CombatActionStateDto(
    string Name,
    string Kind,
    long ElapsedMilliseconds,
    long DurationMilliseconds,
    long? CompletesAtUnixMilliseconds = null);

/// <summary>
/// 战斗动态资源数据传输对象（生命、法力及各项冷却）。
/// </summary>
/// <param name="Hitpoints">当前生命值。</param>
/// <param name="MaxHitpoints">最大生命值。</param>
/// <param name="Manapoints">当前法力值。</param>
/// <param name="MaxManapoints">最大法力值。</param>
/// <param name="AbilityCooldownMilliseconds">技能冷却剩余毫秒字典。</param>
/// <param name="ConsumableCooldownMilliseconds">消耗品槽位冷却剩余毫秒列表。</param>
public sealed record CombatResourceDto(
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    IReadOnlyDictionary<string, long>? AbilityCooldownMilliseconds = null,
    IReadOnlyList<long>? ConsumableCooldownMilliseconds = null);

/// <summary>
/// 角色综合战斗属性面板数据传输对象。
/// </summary>
public sealed record CombatAttributesDto(
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
    string Style,
    string Category,
    string Element,
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
    double CriticalRating = 0,
    double CriticalAvoidanceRating = 0);

/// <summary>
/// 玩家背包中的单个物品实例传输对象。
/// </summary>
/// <param name="ItemId">物品 ID。</param>
/// <param name="Name">物品显示名称。</param>
/// <param name="Category">物品分类（material、pill、equipment 等）。</param>
/// <param name="Amount">持有数量。</param>
/// <param name="Tone">品质/色调标识。</param>
/// <param name="Glyph">UI 渲染图标字符。</param>
/// <param name="Tier">物品品阶/境界层次。</param>
/// <param name="Description">物品描述文本。</param>
/// <param name="IsTradable">是否允许在市场挂单交易。</param>
public sealed record InventoryItemDto(
    string ItemId,
    string Name,
    string Category,
    long Amount,
    string Tone,
    string Glyph,
    int Tier = 1,
    string Description = "",
    bool IsTradable = true);

/// <summary>
/// 物品静态原型定义传输对象，包含消耗品效果、恢复数值及触发规则。
/// </summary>
public sealed record ItemDefinitionDto(
    string ItemId,
    string Name,
    string Description,
    string Kind,
    string Category,
    int Tier,
    long SellPrice,
    bool IsTradable,
    IReadOnlyList<string> UsableActionTypes,
    int CooldownSeconds = 0,
    int RecoverySeconds = 0,
    long HitpointRestore = 0,
    long ManapointRestore = 0,
    double TriggerHpRatio = 0.6,
    bool TriggerAtCombatStart = false,
    IReadOnlyList<CombatTriggerDto>? DefaultCombatTriggers = null,
    IReadOnlyList<ItemBuffEffectDto>? BuffEffects = null,
    double DurationSeconds = 0,
    bool IsContinuous = false,
    double CultivationMultiplier = 1.0,
    double GatheringMultiplier = 1.0,
    double AlchemyMultiplier = 1.0);

/// <summary>
/// 物品附加的 Buff 增益效果定义传输对象。
/// </summary>
public sealed record ItemBuffEffectDto(
    string Name,
    int DurationSeconds,
    int MaxStacks,
    double QiGainMultiplier,
    double AttackLevelRatio,
    double DamageRatio,
    double AttackSpeedRatio,
    double GatheringOutputMultiplier,
    double AlchemyOutputMultiplier);

/// <summary>
/// 战斗/自动使用条件的触发器规则配置传输对象。
/// </summary>
/// <param name="Dependency">依赖的目标类型（self 自身、enemy 敌人、time 时间等）。</param>
/// <param name="Condition">判定条件（hp 百分比、mp 百分比、buff 状态等）。</param>
/// <param name="Comparator">比较符号（lt 小于、lte 小于等于、gt 大于、gte 大于等于、eq 等于）。</param>
/// <param name="Value">比较基准数值。</param>
/// <param name="BuffId">指定检查的 Buff ID（若适用）。</param>
public sealed record CombatTriggerDto(
    string Dependency,
    string Condition,
    string Comparator,
    double Value = 0,
    string? BuffId = null);

/// <summary>
/// 玩家当前生效中的 Buff 状态传输对象。
/// </summary>
public sealed record BuffDto(
    string BuffId,
    string Name,
    int Stacks,
    double QiGainMultiplier,
    long ExpiresAtUnixSeconds,
    double AttackLevelRatio = 0,
    double DamageRatio = 0,
    double AttackSpeedRatio = 0,
    double GatheringOutputMultiplier = 1,
    double AlchemyOutputMultiplier = 1,
    string? SourceItemId = null);

/// <summary>
/// 玩家生涯累计与统计数据传输对象。
/// </summary>
public sealed record StatsDto(
    long Qi,
    long Spirit,
    long HerbsCollected,
    long PillsCrafted,
    long CultivationCycles,
    long CompletedActions,
    long BlockedActions,
    long CombatWins = 0,
    long CombatLosses = 0,
    double CombatExperience = 0,
    long CombatDamageDealt = 0,
    long CombatDamageTaken = 0,
    long CombatEnemiesDefeated = 0,
    long CombatPlayersDefeated = 0,
    double FoundationExp = 0);

/// <summary>
/// 玩家已配置的战斗技能槽位传输对象。
/// </summary>
/// <param name="AbilityId">装配的技能 ID。</param>
/// <param name="Level">技能等级。</param>
/// <param name="Triggers">技能释放条件触发器列表。</param>
public sealed record CombatAbilitySlotDto(
    string? AbilityId,
    int Level,
    IReadOnlyList<CombatTriggerDto>? Triggers = null);

/// <summary>
/// 战斗技能静态配置定义传输对象。
/// </summary>
public sealed record CombatAbilityDto(
    string AbilityId,
    string Name,
    string Style,
    string Category,
    string Element,
    int CooldownMilliseconds,
    int CastMilliseconds,
    long ManaCost,
    double DamageRatio,
    long FlatDamage,
    double PenetrationRatio,
    double TriggerHpRatio,
    string EffectType = "damage",
    double HealingRatio = 0,
    long FlatHealing = 0,
    IReadOnlyList<CombatTriggerDto>? DefaultTriggers = null,
    double DamageOverTimeRatio = 0,
    int DamageOverTimeMilliseconds = 0,
    int DamageOverTimeTickMilliseconds = 3000,
    double StunChance = 0,
    int StunMilliseconds = 0,
    double BlindChance = 0,
    int BlindMilliseconds = 0,
    double SilenceChance = 0,
    int SilenceMilliseconds = 0,
    double DamageRatioLevelBonus = 0,
    double FlatDamageLevelBonus = 0,
    double HealingRatioLevelBonus = 0,
    double FlatHealingLevelBonus = 0,
    string TargetType = "enemy",
    double PierceChance = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0);

/// <summary>
/// 装备附加的基础战斗属性与词条数值传输对象。
/// </summary>
public sealed record EquipmentCombatStatsDto(
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
    int AttackIntervalMilliseconds = 0,
    string? Style = null,
    string? Category = null,
    string? Element = null);

/// <summary>
/// 战斗装备定义及其强化属性数据传输对象。
/// </summary>
public sealed record CombatEquipmentDto(
    string EquipmentId,
    string Name,
    string Slot,
    int Tier,
    EquipmentCombatStatsDto Stats,
    EquipmentCombatStatsDto EnhancementBonus,
    IReadOnlyDictionary<string, double>? BaseStatRatios = null,
    IReadOnlyDictionary<string, double>? FlatStats = null,
    IReadOnlyDictionary<string, double>? SpecialMechanics = null);

/// <summary>
/// 角色或怪物形象视觉展示配置传输对象。
/// </summary>
/// <param name="Renderer">渲染器类型（glyph 图标、image 图片等）。</param>
/// <param name="Source">图像资源地址或字符代号。</param>
/// <param name="Poster">缩略预览图地址。</param>
public sealed record CharacterVisualDto(
    string Renderer,
    string Source,
    string? Poster = null);

/// <summary>
/// 战斗敌人/怪物原型与数值传输对象。
/// </summary>
public sealed record CombatantDto(
    string EnemyId,
    string Name,
    CharacterVisualDto Visual,
    string Style,
    string Category,
    string Element,
    long MaxHitpoints,
    long MaxManapoints,
    int AttackIntervalMilliseconds,
    double AccuracyRating,
    double EvasionRating,
    long MaxDamage,
    double Armor,
    double MagicResistance,
    double MetalResistance,
    double WoodResistance,
    double WaterResistance,
    double FireResistance,
    double EarthResistance,
    double Tenacity,
    double PierceChance,
    double CurseRatio,
    double FuryRatio,
    double WeakenRatio,
    double LifeStealRatio,
    double ArmorDamageRatio,
    double HpDrainRatio,
    double CriticalChance,
    double CriticalDamageRatio,
    double CriticalAvoidance,
    double DamageAbsorptionRatio,
    double HpRegenPerTenSeconds,
    double MpRegenPerTenSeconds,
    int EnrageTimeMilliseconds,
    long Experience,
    IReadOnlyList<string> AbilityIds);

/// <summary>
/// 战斗副本中随机怪物生成的权重条目传输对象。
/// </summary>
public sealed record CombatSpawnDto(
    CombatantDto Enemy,
    double Weight,
    int Strength);

/// <summary>
/// 战斗遭遇/副本关卡完整配置传输对象。
/// </summary>
public sealed record CombatEncounterDto(
    string EncounterId,
    string Name,
    CombatantDto Enemy,
    IReadOnlyList<CombatSpawnDto> RandomSpawns,
    int MaxSpawnCount,
    int MaxTotalStrength,
    IReadOnlyList<CombatantDto> BossEnemies,
    int BattlesPerBoss,
    int SearchMilliseconds,
    int RespawnMilliseconds,
    int PlayerRespawnMilliseconds);

/// <summary>
/// 角色已穿戴的装备项及其强化等级传输对象。
/// </summary>
/// <param name="ItemId">装备对应的物品 ID。</param>
/// <param name="EnhancementLevel">当前强化等级。</param>
public sealed record EquippedItemDto(
    string ItemId,
    int EnhancementLevel = 0);

/// <summary>
/// 战斗快捷消耗品槽位配置传输对象。
/// </summary>
public sealed record ConsumableSlotDto(
    string? ItemId,
    int Quantity,
    bool Enabled,
    IReadOnlyList<CombatTriggerDto>? Triggers = null);

/// <summary>
/// 玩家战斗配置方案（功法偏好、修炼境界加点、技能装备配置）传输对象。
/// </summary>
public sealed record CombatLoadoutDto(
    int StaminaLevel,
    int IntelligenceLevel,
    int AttackLevel,
    int DefenseLevel,
    int SwordsmanshipLevel,
    int TalismanLevel,
    IReadOnlyList<CombatAbilitySlotDto> AbilitySlots,
    IReadOnlyList<ConsumableSlotDto> ConsumableSlots,
    double CombatDropRate = 0,
    double CombatDropQuantity = 0,
    double CombatRareFind = 0,
    double Tenacity = 0,
    double SearchSpeedRatio = 0,
    IReadOnlyList<EquippedItemDto>? Equipment = null,
    IReadOnlyList<string>? LearnedAbilityIds = null,
    IReadOnlyDictionary<string, double>? AbilityExperiences = null);

/// <summary>
/// 地点栖息的怪物实体信息传输对象。
/// </summary>
public sealed record NearbyMonsterDto(
    string MonsterId,
    string Name,
    int Level,
    int Tier,
    string Rank,
    double BudgetCost,
    double MaxHitpoints,
    double MaxDamage,
    double Armor,
    IReadOnlyList<string> Drops,
    string? VisualSource = null,
    string? VisualRenderer = null);

/// <summary>
/// 世界地图节点传输对象，代表大地图上的地点/宗门/秘境。
/// </summary>
public sealed record WorldNodeDto(
    string NodeId,
    string Name,
    string Description,
    double X,
    double Y,
    double CultivationMultiplier,
    IReadOnlyList<string> AvailableActionIds,
    IReadOnlyList<string> EncounterIds,
    IReadOnlyList<NearbyEntityDto> Npcs,
    IReadOnlyList<NearbyEntityDto> Buildings,
    double MinBudget = 0,
    double MaxBudget = 0,
    int EliteInterval = 5,
    int BossInterval = 20,
    IReadOnlyList<NearbyMonsterDto>? Monsters = null,
    IReadOnlyList<NearbyMonsterDto>? EliteEnemies = null,
    IReadOnlyList<NearbyMonsterDto>? BossEnemies = null);

/// <summary>
/// 世界地图路径边传输对象，连接两个节点。
/// </summary>
public sealed record WorldEdgeDto(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    int DurationSeconds);

/// <summary>
/// 世界大地图完整拓扑配置传输对象。
/// </summary>
public sealed record WorldMapDto(
    string MapId,
    string Name,
    string Description,
    int BackgroundSeed,
    IReadOnlyList<WorldNodeDto> Nodes,
    IReadOnlyList<WorldEdgeDto> Edges);

/// <summary>
/// 地点附近可见实体（NPC、建筑物、交互设施）传输对象。
/// </summary>
public sealed record NearbyEntityDto(
    string EntityId,
    string Name,
    string Kind,
    string Description,
    string Effect);

/// <summary>
/// 玩家在世界大地图中的实时位置与状态传输对象。
/// </summary>
public sealed record WorldStateDto(
    string MapId,
    string? NodeId,
    string? EdgeId,
    double EdgeProgress,
    double X,
    double Y,
    string LocationName,
    double CultivationMultiplier,
    IReadOnlyList<string> AvailableActionIds,
    IReadOnlyList<string> EncounterIds,
    IReadOnlyList<NearbyEntityDto> Npcs,
    IReadOnlyList<NearbyEntityDto> Buildings,
    double MinBudget = 0,
    double MaxBudget = 0,
    int EliteInterval = 5,
    int BossInterval = 20,
    IReadOnlyList<NearbyMonsterDto>? Monsters = null,
    IReadOnlyList<NearbyMonsterDto>? EliteEnemies = null,
    IReadOnlyList<NearbyMonsterDto>? BossEnemies = null);

/// <summary>
/// 历史事件日志条目传输对象。
/// </summary>
/// <param name="Sequence">事件全局序列号。</param>
/// <param name="TimestampUnixMilliseconds">事件发生的 Unix 毫秒时间戳。</param>
/// <param name="Template">日志内容模板或描述文本。</param>
public sealed record LogEventDto(
    long Sequence,
    long TimestampUnixMilliseconds,
    string Template);

/// <summary>
/// 玩家全量状态快照传输对象，初次连接或全量同步时下发。
/// </summary>
public sealed record PlayerSnapshotDto(
    string PlayerId,
    string Name,
    long Version,
    long ServerTimeUnixSeconds,
    string PresenceMode,
    QueueDto Queue,
    IReadOnlyList<InventoryItemDto> Inventory,
    IReadOnlyList<BuffDto> Buffs,
    StatsDto Stats,
    IReadOnlyList<LogEventDto>? LogEvents = null,
    CombatLoadoutDto? Combat = null,
    WorldStateDto? World = null,
    CombatResourceDto? CombatResources = null,
    CombatAttributesDto? CombatAttributes = null,
    CharacterVisualDto? Visual = null,
    long? ServerTimeUnixMilliseconds = null,
    bool IsPendingBreakthrough = false,
    string? Account = null,
    long SequenceId = 0,
    long CreatedAtUnixSeconds = 0,
    long RenameCooldownSeconds = 0);

/// <summary>
/// 玩家增量状态更新 Patch 传输对象，仅包含发生变动的字段。
/// </summary>
public sealed record PlayerPatchDto(
    string PlayerId,
    long Version,
    long ServerTimeUnixSeconds,
    string? PresenceMode = null,
    QueueDto? Queue = null,
    IReadOnlyList<InventoryItemDto>? Inventory = null,
    IReadOnlyList<BuffDto>? Buffs = null,
    StatsDto? Stats = null,
    QueueProgressDto? QueueProgress = null,
    IReadOnlyList<LogEventDto>? EventBatch = null,
    CombatLoadoutDto? Combat = null,
    WorldStateDto? World = null,
    CombatResourceDto? CombatResources = null,
    CombatAttributesDto? CombatAttributes = null,
    CharacterVisualDto? Visual = null,
    long? ServerTimeUnixMilliseconds = null,
    bool? IsPendingBreakthrough = null,
    string? Name = null,
    string? Account = null,
    long? SequenceId = null,
    long? CreatedAtUnixSeconds = null,
    long? RenameCooldownSeconds = null);

/// <summary>
/// 指令执行确认与回执传输对象。
/// </summary>
/// <param name="Success">是否执行成功。</param>
/// <param name="Code">状态码（如 ok、failed 等）。</param>
/// <param name="Message">附带提示或错误信息。</param>
/// <param name="ServerTimeUnixSeconds">服务端当前 Unix 秒时间戳。</param>
public sealed record AckDto(
    bool Success,
    string Code,
    string Message,
    long ServerTimeUnixSeconds);

/// <summary>
/// 异常错误通知传输对象。
/// </summary>
/// <param name="Code">错误代码。</param>
/// <param name="Message">错误详细提示。</param>
public sealed record ErrorDto(string Code, string Message);

/// <summary>客户端发送的握手请求载荷。</summary>
public sealed record HelloRequest();

/// <summary>客户端发起的修改角色名请求载荷。</summary>
/// <param name="Name">新角色名称。</param>
public sealed record RenameRequest(string Name);

/// <summary>客户端发起的将动作压入队列请求载荷。</summary>
/// <param name="ActionId">动作模板 ID。</param>
/// <param name="Repeats">重复执行次数。</param>
public sealed record EnqueueActionRequest(string ActionId, int Repeats = 1);

/// <summary>客户端发起的开始战斗/历练请求载荷。</summary>
/// <param name="ActionId">战斗动作模板 ID。</param>
/// <param name="Repeats">重复执行次数。</param>
public sealed record StartCombatRequest(string ActionId, int Repeats = 1);

/// <summary>客户端发起的移除队列项请求载荷。</summary>
/// <param name="EntryId">要移除的队列项唯一 ID。</param>
public sealed record RemoveActionRequest(string EntryId);

/// <summary>客户端发起的移动调整队列项顺序请求载荷。</summary>
/// <param name="EntryId">目标队列项 ID。</param>
/// <param name="Direction">移动方向（-1 上移，1 下移）。</param>
public sealed record MoveActionRequest(string EntryId, int Direction);

/// <summary>客户端发起的前往指定世界地图节点旅行请求载荷。</summary>
/// <param name="DestinationNodeId">目标地点节点 ID。</param>
public sealed record TravelToRequest(string DestinationNodeId);

/// <summary>客户端发起的配置自动使用槽位请求载荷。</summary>
public sealed record SetAutoUseRequest(
    string ActionType,
    string? ItemId,
    int Quantity = 1,
    bool Enabled = true,
    int SlotIndex = 0,
    IReadOnlyList<CombatTriggerDto>? Triggers = null);

/// <summary>客户端发起的即时使用物品请求载荷。</summary>
public sealed record UseItemRequest(string ItemId, string ActionType, int Quantity = 1);

/// <summary>客户端发起的设置队列循环模式请求载荷。</summary>
/// <param name="Enabled">是否开启循环。</param>
public sealed record SetLoopRequest(bool Enabled);

/// <summary>客户端发起的保存战斗配置方案请求载荷。</summary>
public sealed record SetCombatLoadoutRequest(
    IReadOnlyList<CombatAbilitySlotDto>? AbilitySlots = null,
    IReadOnlyList<ConsumableSlotDto>? ConsumableSlots = null,
    double? CombatDropRate = null,
    double? CombatDropQuantity = null,
    double? CombatRareFind = null,
    double? Tenacity = null,
    double? SearchSpeedRatio = null,
    IReadOnlyList<EquippedItemDto>? Equipment = null);

/// <summary>
/// 交易行挂单深度档位传输对象。
/// </summary>
/// <param name="Price">单价。</param>
/// <param name="Quantity">该价位累计挂单数量。</param>
/// <param name="OrderCount">该价位挂单笔数。</param>
public sealed record MarketLevelDto(long Price, long Quantity, int OrderCount);

/// <summary>
/// 交易行指定物品的订单簿买卖盘深度传输对象。
/// </summary>
/// <param name="ItemId">物品 ID。</param>
/// <param name="EnhancementLevel">装备强化等级。</param>
/// <param name="Asks">卖单深度列表（按价格升序）。</param>
/// <param name="Bids">买单深度列表（按价格降序）。</param>
/// <param name="ActiveOrderCount">当前有效订单总数。</param>
/// <param name="Sequence">订单簿序列号。</param>
public sealed record MarketBookDto(
    string ItemId,
    int EnhancementLevel,
    IReadOnlyList<MarketLevelDto> Asks,
    IReadOnlyList<MarketLevelDto> Bids,
    long ActiveOrderCount,
    long Sequence);

/// <summary>
/// 市场全局统计度量指标传输对象。
/// </summary>
public sealed record MarketMetricsDto(
    int ShardCount,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

/// <summary>
/// 市场订单详情数据传输对象。
/// </summary>
public sealed record MarketOrderDto(
    string OrderId,
    string ItemId,
    int EnhancementLevel,
    string Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    long FilledQuantity,
    string Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

/// <summary>
/// 市场订单撮合成交明细传输对象。
/// </summary>
public sealed record MarketFillDto(
    string TakerOrderId,
    string MakerOrderId,
    string BuyerId,
    string SellerId,
    string ItemId,
    int EnhancementLevel,
    long Price,
    long Quantity,
    long GrossAmount,
    long SellerProceeds,
    long SellerTax);

/// <summary>
/// 市场挂单/撤单操作响应传输对象。
/// </summary>
public sealed record MarketCommandResponseDto(
    bool Success,
    string Code,
    string Message,
    MarketOrderDto? Order,
    IReadOnlyList<MarketFillDto> Fills,
    PlayerSnapshotDto? Snapshot);

/// <summary>
/// 客户端发起的市场挂单请求载荷。
/// </summary>
public sealed record MarketPlaceRequest(
    string OrderId,
    string ItemId,
    string Side,
    long Price,
    long Quantity,
    int EnhancementLevel = 0);

/// <summary>
/// 客户端发起的市场撤单请求载荷。
/// </summary>
/// <param name="OrderId">要撤销的订单 ID。</param>
public sealed record MarketCancelRequest(string OrderId);

/// <summary>
/// 道化协议数据包 JSON 序列化与反序列化编解码辅助工具类。
/// </summary>
public static class PacketCodec
{
    /// <summary>
    /// 全局标准驼峰式 JSON 序列化配置。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 将指定载荷编码为 UTF-8 字节数组信封包。
    /// </summary>
    /// <typeparam name="T">载荷数据类型。</typeparam>
    /// <param name="kind">包类型标识。</param>
    /// <param name="requestId">请求追踪 ID。</param>
    /// <param name="sequence">消息序列号。</param>
    /// <param name="payload">数据载荷。</param>
    /// <returns>编码后的 JSON UTF-8 字节数组。</returns>
    public static byte[] Encode<T>(string kind, string requestId, long sequence, T payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new Envelope<T>(kind, requestId, sequence, payload),
            JsonOptions);
    }

    /// <summary>
    /// 尝试从字节流解析入站数据包信封。
    /// </summary>
    /// <param name="bytes">待解析的 UTF-8 字节数据。</param>
    /// <param name="packet">解析成功后输出的数据包对象。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryDecode(ReadOnlyMemory<byte> bytes, out IncomingPacket packet)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement)
                || !root.TryGetProperty("payload", out var payloadElement))
            {
                packet = default!;
                return false;
            }

            packet = new IncomingPacket(
                kindElement.GetString() ?? string.Empty,
                root.TryGetProperty("requestId", out var requestIdElement)
                    ? requestIdElement.GetString() ?? string.Empty
                    : string.Empty,
                root.TryGetProperty("sequence", out var sequenceElement) && sequenceElement.TryGetInt64(out var sequence)
                    ? sequence
                    : 0,
                payloadElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            packet = default!;
            return false;
        }
    }

    /// <summary>
    /// 编码客户端指令请求包。
    /// </summary>
    /// <typeparam name="T">指令参数类型。</typeparam>
    /// <param name="requestId">请求追踪 ID。</param>
    /// <param name="command">指令名称。</param>
    /// <param name="payload">指令参数载荷。</param>
    /// <returns>编码后的字节数组。</returns>
    public static byte[] EncodeCommand<T>(string requestId, string command, T payload)
    {
        return Encode(PacketKinds.Command, requestId, 0, new { command, payload });
    }

    /// <summary>
    /// 编码客户端握手请求包。
    /// </summary>
    /// <param name="requestId">请求追踪 ID。</param>
    /// <param name="payload">握手载荷。</param>
    /// <returns>编码后的字节数组。</returns>
    public static byte[] EncodeHello(string requestId, HelloRequest payload)
    {
        return Encode(PacketKinds.Hello, requestId, 0, payload);
    }
}

/// <summary>
/// 注册账号 HTTP 请求载荷。
/// </summary>
/// <param name="Account">账号用户名。</param>
/// <param name="Password">账号密码。</param>
/// <param name="CharacterName">初始道号/角色名（可选）。</param>
public sealed record RegisterAccountRequest(
    string Account,
    string Password,
    string? CharacterName = null);

/// <summary>
/// 登录账号 HTTP 请求载荷。
/// </summary>
/// <param name="Account">账号用户名。</param>
/// <param name="Password">账号密码。</param>
public sealed record LoginAccountRequest(
    string Account,
    string Password);

/// <summary>
/// 账号认证响应结果传输对象。
/// </summary>
/// <param name="Success">是否认证成功。</param>
/// <param name="Account">账号名称。</param>
/// <param name="CharacterName">角色名称。</param>
/// <param name="PlayerId">玩家唯一标识 ID。</param>
/// <param name="SequenceId">账号递增序列序号。</param>
/// <param name="Message">附带提示或失败原因。</param>
public sealed record AuthResponse(
    bool Success,
    string? Account,
    string? CharacterName,
    string? PlayerId,
    long SequenceId,
    string? Message = null);

