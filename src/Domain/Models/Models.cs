// ==============================================================================
// 文件名称: Models.cs
// 主要职责: 定义玩家核心实体、个人档案、动作队列、境界属性以及 ECS 组件与快照等基础领域模型。
// 核心逻辑 / 定义内容:
//   1. 动作原型定义与玩家动作队列实例管理（ActionDefinition, QueuedAction）
//   2. 玩家角色档案、基础属性与在线/挂机运行时状态（PlayerProfile, PlayerState）
//   3. 实体组件与完整玩家状态持久化快照（PlayerComponent, PlayerSnapshot）
// ==============================================================================
using System.Text.Json.Serialization;
using YuoCore.Ecs;

namespace Domain.Models;

/// <summary>
/// 动作静态原型定义，描述打坐、探索、炼丹、战斗及移动等行为的基础消耗、产出与执行规则。
/// </summary>
public sealed class ActionDefinition
{
    /// <summary>动作唯一标识符。</summary>
    public required string Id { get; init; }
    /// <summary>动作显示名称。</summary>
    public required string Name { get; init; }
    /// <summary>动作背景与功能描述。</summary>
    public required string Description { get; init; }
    /// <summary>动作所属基本类型（修炼/探索/炼丹/战斗）。</summary>
    public required ActionType Type { get; init; }
    /// <summary>单次执行标准耗时（秒）。</summary>
    public required int DurationSeconds { get; init; }
    /// <summary>推进执行模式（计时模式/战斗遭遇/大地图旅行）。</summary>
    public ActionExecutionKind ExecutionKind { get; init; } = ActionExecutionKind.Timed;
    /// <summary>关联的战斗遭遇 ID（仅当为战斗动作时有效）。</summary>
    public string? CombatEncounterId { get; init; }
    /// <summary>允许执行该动作的世界地图节点 ID 集合（为空表示全域可用）。</summary>
    public IReadOnlySet<string> AvailableNodeIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>是否在常规动作目录中隐藏（如特殊位移动作）。</summary>
    public bool HiddenFromCatalog { get; init; }
    /// <summary>单次执行所需消耗的物品及数量字典。</summary>
    public Dictionary<string, long> Inputs { get; init; } = new(StringComparer.Ordinal);
    /// <summary>单次执行成功产出的物品及数量字典。</summary>
    public Dictionary<string, long> Outputs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 玩家当前动作队列中的具体执行实例，维护单项进度、剩余循环次数与关联状态。
/// </summary>
public sealed class QueuedAction
{
    /// <summary>队列项唯一实例 GUID。</summary>
    public Guid EntryId { get; set; } = Guid.NewGuid();
    /// <summary>动作原型模板 ID。</summary>
    public required string ActionId { get; set; }
    /// <summary>初始配置的总重复执行次数。</summary>
    public int TotalRepeats { get; set; }
    /// <summary>当前剩余执行次数（小于 0 表示无限循环）。</summary>
    public int RemainingRepeats { get; set; } = 1;
    /// <summary>当前单次动作已推进的整秒数。</summary>
    public int ProgressSeconds { get; set; }
    /// <summary>当前单次动作累计推进的毫秒数。</summary>
    public long ProgressMilliseconds { get; set; }
    /// <summary>单次动作总耗时（秒）。</summary>
    public int DurationSeconds { get; set; }
    /// <summary>当前队列项状态（排队/执行中/受阻）。</summary>
    public ActionStatus Status { get; set; } = ActionStatus.Queued;
    /// <summary>旅行移动方案与路径（若为位移动作）。</summary>
    public TravelPlan? TravelPlan { get; set; }
    /// <summary>历练/战斗运行态（若为战斗动作）。</summary>
    public CombatExpeditionState? CombatExpedition { get; set; }

    /// <summary>是否为无限次重复执行动作。</summary>
    [JsonIgnore]
    public bool IsInfinite => RemainingRepeats < 0;

    /// <summary>
    /// 当队列循环开启时，为下一次循环克隆重置新的队列动作项。
    /// </summary>
    /// <returns>重置了进度与全新 EntryId 的动作副本。</returns>
    public QueuedAction CloneForLoop()
    {
        return new QueuedAction
        {
            EntryId = Guid.NewGuid(),
            ActionId = ActionId,
            TotalRepeats = TotalRepeats == 0 ? RemainingRepeats : TotalRepeats,
            RemainingRepeats = RemainingRepeats,
            ProgressSeconds = 0,
            ProgressMilliseconds = 0,
            DurationSeconds = DurationSeconds,
            Status = ActionStatus.Queued,
            TravelPlan = TravelPlan?.Clone(),
            CombatExpedition = null
        };
    }
}

/// <summary>
/// 战斗历练阶段枚举。
/// </summary>
public enum CombatExpeditionPhase
{
    /// <summary>初始寻敌/搜寻敌人阶段。</summary>
    InitialSearch = 0,
    /// <summary>战斗交锋回合阶段。</summary>
    Combat = 1,
    /// <summary>战后搜寻下一波敌人阶段。</summary>
    Search = 2
}

/// <summary>
/// 玩家跨连续遭遇及脱战后完全持久化的生命和灵力。
/// 设定上不进行任何自然自动回复，生命和灵力恢复完全依赖消耗品（丹药等）或主动技能；脱战或取消战斗时保持当前生命与灵力，若处于死亡状态则脱战复活（生命值恢复至 1 点，灵力不回复）。
/// </summary>
public sealed class CombatResourceState
{
    /// <summary>是否已完成初次战斗资源初始化。</summary>
    public bool Initialized { get; set; }
    /// <summary>当前持久化生命值。</summary>
    public long Hitpoints { get; set; }
    /// <summary>当前持久化法力/灵力值。</summary>
    public long Manapoints { get; set; }
    /// <summary>生命回复小数累加余量（用于保证高精度点数结算）。</summary>
    public double HitpointRemainder { get; set; }
    /// <summary>法力回复小数累加余量。</summary>
    public double ManapointRemainder { get; set; }
    /// <summary>各技能最后一次释放完成的绝对冷却到期时间字典。</summary>
    public Dictionary<string, DateTimeOffset> AbilityCooldowns { get; set; } = new(StringComparer.Ordinal);
    /// <summary>各消耗品快捷槽位的绝对冷却到期时间列表。</summary>
    public List<DateTimeOffset> ConsumableSlotCooldowns { get; set; } = [];

    /// <summary>深克隆当前战斗资源状态。</summary>
    public CombatResourceState Clone() => new()
    {
        Initialized = Initialized,
        Hitpoints = Hitpoints,
        Manapoints = Manapoints,
        HitpointRemainder = HitpointRemainder,
        ManapointRemainder = ManapointRemainder,
        AbilityCooldowns = new Dictionary<string, DateTimeOffset>(AbilityCooldowns, StringComparer.Ordinal),
        ConsumableSlotCooldowns = new List<DateTimeOffset>(ConsumableSlotCooldowns)
    };
}

/// <summary>
/// 战斗入场时的固定快照。包含遭遇 ID、战斗序号、入场资源/冷却/装备/Buff/Inventory 及当前配置指纹。
/// 进入 Combat 阶段只创建一次该快照，后续微步与分段推进均基于此快照重放。
/// </summary>
public sealed class BattleSnapshot
{
    /// <summary>当前副本遭遇 ID。</summary>
    public string EncounterId { get; set; } = string.Empty;

    /// <summary>战斗轮次递增序号。</summary>
    public long BattleSequence { get; set; }

    /// <summary>本次战斗前已通关的遭遇波次数。</summary>
    public long CompletedEncountersBeforeBattle { get; set; }

    /// <summary>本场战斗开始的绝对时间戳。</summary>
    public DateTimeOffset BattleStartedAt { get; set; }

    /// <summary>战斗入场时的初始生命值。</summary>
    public long InitialHitpoints { get; set; }

    /// <summary>战斗入场时的初始法力值。</summary>
    public long InitialManapoints { get; set; }

    /// <summary>入场时各技能的剩余冷却毫秒数字典。</summary>
    public Dictionary<string, long> InitialAbilityCooldowns { get; set; } = new(StringComparer.Ordinal);

    /// <summary>入场时各消耗品槽位的剩余冷却毫秒数列表。</summary>
    public List<long> InitialConsumableCooldowns { get; set; } = [];

    /// <summary>战斗入场时的玩家战斗装配快照。</summary>
    public CombatLoadout BattleLoadout { get; set; } = new();

    /// <summary>战斗入场时的 Buff 列表快照。</summary>
    public List<BuffState> BattleBuffs { get; set; } = [];

    /// <summary>战斗入场时的背包物品快照。</summary>
    public Dictionary<string, long> BattleInventory { get; set; } = new(StringComparer.Ordinal);

    /// <summary>快照创建时的游戏静态配置数据指纹。</summary>
    public string ConfigFingerprint { get; set; } = string.Empty;

    /// <summary>深克隆战斗入场快照。</summary>
    public BattleSnapshot Clone() => new()
    {
        EncounterId = EncounterId,
        BattleSequence = BattleSequence,
        CompletedEncountersBeforeBattle = CompletedEncountersBeforeBattle,
        BattleStartedAt = BattleStartedAt,
        InitialHitpoints = InitialHitpoints,
        InitialManapoints = InitialManapoints,
        InitialAbilityCooldowns = new Dictionary<string, long>(InitialAbilityCooldowns, StringComparer.Ordinal),
        InitialConsumableCooldowns = new List<long>(InitialConsumableCooldowns),
        BattleLoadout = BattleLoadout.Clone(),
        BattleBuffs = BattleBuffs.Select(CloneBuff).ToList(),
        BattleInventory = new Dictionary<string, long>(BattleInventory, StringComparer.Ordinal),
        ConfigFingerprint = ConfigFingerprint
    };

    private static BuffState CloneBuff(BuffState buff) => buff.Clone();
}

/// <summary>
/// 单个历练行动的持久运行态。当前战斗以固定快照重放，保证在线推进、离线快进和服务重启结果一致。
/// </summary>
public sealed class CombatExpeditionState
{
    /// <summary>当前历练所处阶段（寻敌/战斗）。</summary>
    public CombatExpeditionPhase Phase { get; set; } = CombatExpeditionPhase.InitialSearch;

    /// <summary>当前阶段已推进的毫秒数。</summary>
    public long PhaseElapsedMilliseconds { get; set; }

    /// <summary>当前阶段预期总耗时（毫秒）。</summary>
    public long PhaseDurationMilliseconds { get; set; } = 20_000;

    /// <summary>当前历练已进入/执行的战斗序号。</summary>
    public long BattleSequence { get; set; }

    /// <summary>战斗入场固定快照。进入 Combat 阶段后只创建一次。</summary>
    public BattleSnapshot? BattleSnapshot { get; set; }

    /// <summary>已向客户端发射的战斗事件数量。</summary>
    public int EmittedEventCount { get; set; }

    /// <summary>战斗中已应用消耗的物品及数量统计。</summary>
    public Dictionary<string, long> AppliedConsumedItems { get; set; } = new(StringComparer.Ordinal);

    /// <summary>战斗中已结算常规掉落的物品及数量统计。</summary>
    public Dictionary<string, long> AppliedDroppedItems { get; set; } = new(StringComparer.Ordinal);

    /// <summary>战斗中已结算稀有掉落的物品及数量统计。</summary>
    public Dictionary<string, long> AppliedRareDroppedItems { get; set; } = new(StringComparer.Ordinal);

    /// <summary>已累加的胜场数。</summary>
    public int AppliedWins { get; set; }

    /// <summary>已累加的败场数。</summary>
    public int AppliedLosses { get; set; }

    /// <summary>已累加击败的敌人数。</summary>
    public int AppliedEnemiesDefeated { get; set; }

    /// <summary>已处理的战斗时间线事件数。</summary>
    public int AppliedProcessedEvents { get; set; }

    /// <summary>已造成的累计伤害量。</summary>
    public long AppliedDamageDealt { get; set; }

    /// <summary>已承受的累计伤害量。</summary>
    public long AppliedDamageTaken { get; set; }

    /// <summary>已获得的累计战斗经验值。</summary>
    public double AppliedExperience { get; set; }

    /// <summary>已结算获得的灵石数量。</summary>
    public long AppliedSpirit { get; set; }

    /// <summary>玩家阵亡复活后是否直接标记队列项完成。</summary>
    public bool CompleteAfterRespawn { get; set; }

    /// <summary>深克隆历练运行态对象。</summary>
    public CombatExpeditionState Clone() => new()
    {
        Phase = Phase,
        PhaseElapsedMilliseconds = PhaseElapsedMilliseconds,
        PhaseDurationMilliseconds = PhaseDurationMilliseconds,
        BattleSequence = BattleSequence,
        BattleSnapshot = BattleSnapshot?.Clone(),
        EmittedEventCount = EmittedEventCount,
        AppliedConsumedItems = new Dictionary<string, long>(AppliedConsumedItems, StringComparer.Ordinal),
        AppliedDroppedItems = new Dictionary<string, long>(AppliedDroppedItems, StringComparer.Ordinal),
        AppliedRareDroppedItems = new Dictionary<string, long>(AppliedRareDroppedItems, StringComparer.Ordinal),
        AppliedWins = AppliedWins,
        AppliedLosses = AppliedLosses,
        AppliedEnemiesDefeated = AppliedEnemiesDefeated,
        AppliedProcessedEvents = AppliedProcessedEvents,
        AppliedDamageDealt = AppliedDamageDealt,
        AppliedDamageTaken = AppliedDamageTaken,
        AppliedExperience = AppliedExperience,
        AppliedSpirit = AppliedSpirit,
        CompleteAfterRespawn = CompleteAfterRespawn
    };
}

/// <summary>
/// 自动使用消耗品槽位配置模型。
/// </summary>
public sealed class AutoUseSlot
{
    /// <summary>绑定的物品 ID。</summary>
    public string? ItemId { get; set; }
    /// <summary>单次触发消耗数量。</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>是否开启自动触发。</summary>
    public bool Enabled { get; set; }
    /// <summary>当前开封的 1 件持续消耗品剩余有效动作持续时间（秒，银河奶牛式）。</summary>
    public double RemainingDurationSeconds { get; set; }
    /// <summary>触发条件规则列表。</summary>
    public List<CombatTrigger>? Triggers { get; set; }

    /// <summary>克隆自动使用槽位配置。</summary>
    public AutoUseSlot Clone()
    {
        return new AutoUseSlot
        {
            ItemId = ItemId,
            Quantity = Quantity,
            Enabled = Enabled,
            RemainingDurationSeconds = RemainingDurationSeconds,
            Triggers = Triggers?.Select(trigger => trigger.Clone()).ToList()
        };
    }
}

/// <summary>
/// 玩家动作队列整体聚合状态，包含待办列表、循环开关与四大类动作的自动消耗品槽位。
/// </summary>
public sealed class ActionQueueState
{
    /// <summary>排队中的动作项列表。</summary>
    public List<QueuedAction> Entries { get; set; } = [];
    /// <summary>是否开启队列循环模式（完成末尾项后自动从头重新入队）。</summary>
    public bool LoopEnabled { get; set; } = true;
    /// <summary>不同动作分类（修炼/探索/炼丹/战斗）对应的自动使用槽位配置字典。</summary>
    public Dictionary<ActionType, List<AutoUseSlot>> AutoUseSlots { get; set; } =
        new()
        {
            [ActionType.Cultivation] = [new(), new(), new()],
            [ActionType.Exploration] = [new(), new(), new()],
            [ActionType.Alchemy] = [new(), new(), new()],
            [ActionType.Combat] = [new(), new(), new()]
        };
}

/// <summary>
/// 玩家背包存储状态，维护物品 ID 到持有数量的映射。
/// </summary>
public sealed class InventoryState
{
    /// <summary>物品持有字典（键为物品 ID，值为持有数量）。</summary>
    public Dictionary<string, long> Items { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取指定物品的当前持有数量。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <returns>持有数量，若不存在则返回 0。</returns>
    public long Get(string itemId) => Items.TryGetValue(itemId, out var amount) ? amount : 0;

    /// <summary>
    /// 增加或扣减背包物品数量（溢出检查与非负校验）。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <param name="amount">变更数量（正数为增加，负数为扣减）。</param>
    /// <exception cref="InvalidOperationException">当最终数量小于 0 时抛出异常。</exception>
    public void Add(string itemId, long amount)
    {
        if (amount == 0) return;
        var nextAmount = checked(Get(itemId) + amount);
        if (nextAmount < 0)
        {
            throw new InvalidOperationException($"物品 {itemId} 数量不能为负数。");
        }

        if (nextAmount == 0)
        {
            Items.Remove(itemId);
            return;
        }

        Items[itemId] = nextAmount;
    }
}

/// <summary>
/// 玩家当前生效中的 Buff 状态实例。
/// </summary>
public sealed class BuffState
{
    /// <summary>Buff 原型 ID。</summary>
    public required string BuffId { get; set; }

    /// <summary>Buff 显示名称。</summary>
    public required string Name { get; set; }

    /// <summary>当前叠加层数。</summary>
    public int Stacks { get; set; } = 1;

    /// <summary>通用属性修饰器字典（属性键 -> 加成数值/倍率）。</summary>
    public Dictionary<string, double> Modifiers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>施加该 Buff 的来源物品 ID（若有）。</summary>
    public string? SourceItemId { get; set; }

    /// <summary>Buff 到期时间戳。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>获取指定属性的修饰数值（若未显式配置则返回默认值）。</summary>
    public double GetModifier(string statKey, double defaultValue = 0.0) =>
        Modifiers.TryGetValue(statKey, out var value) ? value : defaultValue;

    /// <summary>设置指定属性的修饰数值。</summary>
    public void SetModifier(string statKey, double value) => Modifiers[statKey] = value;

    /// <summary>灵气吸纳倍率修正。</summary>
    public double QiGainMultiplier
    {
        get => GetModifier(Domain.Constants.StatKeys.QiGain, 1.0);
        set => SetModifier(Domain.Constants.StatKeys.QiGain, value);
    }

    /// <summary>攻击等级加成比例。</summary>
    public double AttackLevelRatio
    {
        get => GetModifier(Domain.Constants.StatKeys.AttackLevelRatio, 0.0);
        set => SetModifier(Domain.Constants.StatKeys.AttackLevelRatio, value);
    }

    /// <summary>伤害加成比例。</summary>
    public double DamageRatio
    {
        get => GetModifier(Domain.Constants.StatKeys.DamageRatio, 0.0);
        set => SetModifier(Domain.Constants.StatKeys.DamageRatio, value);
    }

    /// <summary>攻速加成比例。</summary>
    public double AttackSpeedRatio
    {
        get => GetModifier(Domain.Constants.StatKeys.AttackSpeedRatio, 0.0);
        set => SetModifier(Domain.Constants.StatKeys.AttackSpeedRatio, value);
    }

    /// <summary>采集产出倍率修正。</summary>
    public double GatheringOutputMultiplier
    {
        get => GetModifier(Domain.Constants.StatKeys.GatheringOutput, 1.0);
        set => SetModifier(Domain.Constants.StatKeys.GatheringOutput, value);
    }

    /// <summary>炼丹产出倍率修正。</summary>
    public double AlchemyOutputMultiplier
    {
        get => GetModifier(Domain.Constants.StatKeys.AlchemyOutput, 1.0);
        set => SetModifier(Domain.Constants.StatKeys.AlchemyOutput, value);
    }

    /// <summary>深克隆 Buff 状态对象。</summary>
    public BuffState Clone() => new()
    {
        BuffId = BuffId,
        Name = Name,
        Stacks = Stacks,
        Modifiers = new Dictionary<string, double>(Modifiers, StringComparer.Ordinal),
        SourceItemId = SourceItemId,
        ExpiresAt = ExpiresAt
    };
}

/// <summary>
/// 玩家各项生涯成就、行为次数与战斗统计数据。
/// </summary>
public sealed class PlayerStats
{
    /// <summary>当前灵气修为储备。</summary>
    public long Qi { get; set; }
    /// <summary>灵石持有数量。</summary>
    public long Spirit { get; set; }
    /// <summary>筑基/功法修炼经验值。</summary>
    public double FoundationExp { get; set; }
    /// <summary>累计采集灵草总数。</summary>
    public long HerbsCollected { get; set; }
    /// <summary>累计炼制丹药总数。</summary>
    public long PillsCrafted { get; set; }
    /// <summary>累计打坐修炼大周天次数。</summary>
    public long CultivationCycles { get; set; }
    /// <summary>累计完成动作总次数。</summary>
    public long CompletedActions { get; set; }
    /// <summary>累计受阻动作次数。</summary>
    public long BlockedActions { get; set; }
    /// <summary>战斗胜利总场次。</summary>
    public long CombatWins { get; set; }
    /// <summary>战斗失败总场次。</summary>
    public long CombatLosses { get; set; }
    /// <summary>累计战斗经验值。</summary>
    public double CombatExperience { get; set; }
    /// <summary>战斗中累计造成的总伤害。</summary>
    public long CombatDamageDealt { get; set; }
    /// <summary>战斗中累计承受的总伤害。</summary>
    public long CombatDamageTaken { get; set; }
    /// <summary>击败敌怪总数。</summary>
    public long CombatEnemiesDefeated { get; set; }
    /// <summary>切磋击败玩家总数。</summary>
    public long CombatPlayersDefeated { get; set; }
}

/// <summary>
/// 结构化事件日志条目，用于记录玩家修行生涯中的重要事件。
/// </summary>
public sealed class LogEvent
{
    /// <summary>日志条目单调递增序列号。</summary>
    public long Sequence { get; set; }
    /// <summary>事件发生时间。</summary>
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>日志文本模板与占位符内容。</summary>
    public string Template { get; set; } = string.Empty;
}

/// <summary>
/// 玩家聚合根状态模型，集中持久化玩家的全部领域属性。
/// </summary>
public sealed class PlayerState
{
    /// <summary>玩家唯一全局 GUID。</summary>
    public Guid PlayerId { get; set; }
    /// <summary>玩家道号/角色名。</summary>
    public string Name { get; set; } = "无名修士";
    /// <summary>绑定的账号用户名。</summary>
    public string Account { get; set; } = string.Empty;
    /// <summary>账号内角色序号。</summary>
    public long SequenceId { get; set; }
    /// <summary>角色创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>上次修改道号的时间戳（用于改名冷却计算）。</summary>
    public DateTimeOffset? LastRenamedAt { get; set; }
    /// <summary>角色视觉形象定义。</summary>
    public CharacterVisualDefinition Visual { get; set; } = new() { Renderer = VisualRendererKind.Text, Source = "修" };
    /// <summary>状态快照版本号，每次持久化写回时单调自增。</summary>
    public long Version { get; set; }
    /// <summary>上次完成离线/在线模拟结算的时间戳。</summary>
    public DateTimeOffset LastSimulatedAt { get; set; }
    /// <summary>玩家当前的在线与挂机模式。</summary>
    public PresenceMode PresenceMode { get; set; } = PresenceMode.IdleOffline;
    /// <summary>是否处于等待突破境界状态。</summary>
    public bool IsPendingBreakthrough { get; set; }
    /// <summary>玩家动作队列状态。</summary>
    public ActionQueueState Queue { get; set; } = new();
    /// <summary>玩家背包物品状态。</summary>
    public InventoryState Inventory { get; set; } = new();
    /// <summary>生效中的 Buff 列表。</summary>
    public List<BuffState> Buffs { get; set; } = [];
    /// <summary>战斗配置方案（功法等级、装备与技能槽位）。</summary>
    public CombatLoadout Combat { get; set; } = new();
    /// <summary>持久化战斗资源（生命、法力与冷却）。</summary>
    public CombatResourceState CombatResources { get; set; } = new();
    /// <summary>世界大地图所处位置与节点状态。</summary>
    public WorldState World { get; set; } = new();
    /// <summary>各副本遭遇的历史通关计数字典。</summary>
    public Dictionary<string, long> CombatEncounterCompletions { get; set; } = new(StringComparer.Ordinal);
    /// <summary>玩家统计数据。</summary>
    public PlayerStats Stats { get; set; } = new();
    /// <summary>事件序列号计数器。</summary>
    public long EventSequence { get; set; }
    /// <summary>已持久化的结构化事件日志列表。</summary>
    public List<LogEvent> StructuredEventLog { get; set; } = [];

    /// <summary>本轮模拟产生且尚未同步给网络或落库的暂存日志列表。</summary>
    [JsonIgnore]
    public List<LogEvent> PendingEvents { get; } = [];

    /// <summary>当前状态的脏标记掩码。</summary>
    [JsonIgnore]
    public DirtySections DirtySections { get; set; } = DirtySections.All;

    /// <summary>
    /// 完整深克隆当前玩家状态，用于隔离只读计算或生成持久化快照。
    /// </summary>
    /// <returns>玩家全量状态副本。</returns>
    public PlayerState Clone()
    {
        return new PlayerState
        {
            PlayerId = PlayerId,
            Name = Name,
            Account = Account,
            SequenceId = SequenceId,
            CreatedAt = CreatedAt,
            LastRenamedAt = LastRenamedAt,
            Visual = new CharacterVisualDefinition
            {
                Renderer = Visual.Renderer,
                Source = Visual.Source,
                Poster = Visual.Poster
            },
            Version = Version,
            LastSimulatedAt = LastSimulatedAt,
            PresenceMode = PresenceMode,
            IsPendingBreakthrough = IsPendingBreakthrough,
            Queue = new ActionQueueState
            {
                LoopEnabled = Queue.LoopEnabled,
                Entries = Queue.Entries.Select(CloneAction).ToList(),
                AutoUseSlots = Queue.AutoUseSlots.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(slot => slot.Clone()).ToList())
            },
            Inventory = new InventoryState { Items = new Dictionary<string, long>(Inventory.Items, StringComparer.Ordinal) },
            Buffs = Buffs.Select(buff => buff.Clone()).ToList(),
            Combat = Combat.Clone(),
            CombatResources = CombatResources?.Clone() ?? new CombatResourceState(),
            World = World.Clone(),
            CombatEncounterCompletions = new Dictionary<string, long>(CombatEncounterCompletions, StringComparer.Ordinal),
            Stats = new PlayerStats
            {
                Qi = Stats.Qi,
                Spirit = Stats.Spirit,
                FoundationExp = Stats.FoundationExp,
                HerbsCollected = Stats.HerbsCollected,
                PillsCrafted = Stats.PillsCrafted,
                CultivationCycles = Stats.CultivationCycles,
                CompletedActions = Stats.CompletedActions,
                BlockedActions = Stats.BlockedActions,
                CombatWins = Stats.CombatWins,
                CombatLosses = Stats.CombatLosses,
                CombatExperience = Stats.CombatExperience,
                CombatDamageDealt = Stats.CombatDamageDealt,
                CombatDamageTaken = Stats.CombatDamageTaken,
                CombatEnemiesDefeated = Stats.CombatEnemiesDefeated,
                CombatPlayersDefeated = Stats.CombatPlayersDefeated
            },
            EventSequence = EventSequence,
            StructuredEventLog = StructuredEventLog.Select(CloneLogEvent).ToList()
        };
    }

    private static LogEvent CloneLogEvent(LogEvent entry)
    {
        return new LogEvent
        {
            Sequence = entry.Sequence,
            OccurredAt = entry.OccurredAt,
            Template = entry.Template
        };
    }

    private static QueuedAction CloneAction(QueuedAction action)
    {
        return new QueuedAction
        {
            EntryId = action.EntryId,
            ActionId = action.ActionId,
            TotalRepeats = action.TotalRepeats,
            RemainingRepeats = action.RemainingRepeats,
            ProgressSeconds = action.ProgressSeconds,
            ProgressMilliseconds = action.ProgressMilliseconds,
            DurationSeconds = action.DurationSeconds,
            Status = action.Status,
            TravelPlan = action.TravelPlan?.Clone(),
            CombatExpedition = action.CombatExpedition?.Clone()
        };
    }
}

/// <summary>
/// ECS 中的玩家组件只保存由所属 Fiber 修改的聚合状态；网络、持久化和 UI 不直接持有 Entity。
/// </summary>
public sealed class PlayerComponent : YuoComponent
{
    /// <summary>当前 Entity 挂载的玩家聚合状态。</summary>
    public PlayerState State { get; set; } = new();
}

/// <summary>
/// 单次模拟推演周期的结算结果记录。
/// </summary>
/// <param name="DirtySections">发生变动的状态节掩码。</param>
/// <param name="CompletedActions">本次结算完成的动作总数。</param>
/// <param name="TransitionCount">动作流转/切换次数。</param>
/// <param name="Events">产生的事件日志文本列表。</param>
/// <param name="CombatEvents">产生的战斗事件数。</param>
/// <param name="ReachedTarget">本次推进是否已完整到达目标时间。</param>
/// <param name="Capped">本次推进是否触及单次转换上限导致提前暂停。</param>
public sealed record SimulationResult(
    DirtySections DirtySections,
    int CompletedActions,
    int TransitionCount,
    IReadOnlyList<string> Events,
    int CombatEvents = 0,
    bool ReachedTarget = true,
    bool Capped = false);

/// <summary>
/// 玩家持久化快照信封包装（不可变纯数据结构）。
/// 包含快照 Schema 版本、游戏配置指纹、玩家状态单调版本号、账号角色序号、上次结算时间和玩家状态载荷。
/// </summary>
public sealed record SnapshotEnvelope(
    int SnapshotSchemaVersion,
    string GameDataFingerprint,
    long PlayerStateVersion,
    long SequenceId,
    DateTimeOffset LastSimulatedAt,
    PlayerState Payload)
{
    /// <summary>当前系统支持的快照 Envelope 结构版本。</summary>
    public const int CurrentSchemaVersion = 1;
}

