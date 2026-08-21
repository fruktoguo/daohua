using System.Text.Json.Serialization;
using YuoCore.Ecs;

namespace Xiuxian.Domain.Models;

public sealed class XiuxianActionDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required XiuxianActionType Type { get; init; }
    public required int DurationSeconds { get; init; }
    public XiuxianActionExecutionKind ExecutionKind { get; init; } = XiuxianActionExecutionKind.Timed;
    public string? CombatEncounterId { get; init; }
    public IReadOnlySet<string> AvailableNodeIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool HiddenFromCatalog { get; init; }
    public Dictionary<string, long> Inputs { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Outputs { get; init; } = new(StringComparer.Ordinal);
}

public sealed class XiuxianQueuedAction
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public required string ActionId { get; set; }
    public int TotalRepeats { get; set; }
    public int RemainingRepeats { get; set; } = 1;
    public int ProgressSeconds { get; set; }
    public long ProgressMilliseconds { get; set; }
    public int DurationSeconds { get; set; }
    public XiuxianActionStatus Status { get; set; } = XiuxianActionStatus.Queued;
    public XiuxianTravelPlan? TravelPlan { get; set; }
    public XiuxianCombatExpeditionState? CombatExpedition { get; set; }

    [JsonIgnore]
    public bool IsInfinite => RemainingRepeats < 0;

    public XiuxianQueuedAction CloneForLoop()
    {
        return new XiuxianQueuedAction
        {
            EntryId = Guid.NewGuid(),
            ActionId = ActionId,
            TotalRepeats = TotalRepeats == 0 ? RemainingRepeats : TotalRepeats,
            RemainingRepeats = RemainingRepeats,
            ProgressSeconds = 0,
            ProgressMilliseconds = 0,
            DurationSeconds = DurationSeconds,
            Status = XiuxianActionStatus.Queued,
            TravelPlan = TravelPlan?.Clone(),
            CombatExpedition = null
        };
    }
}

public enum XiuxianCombatExpeditionPhase
{
    InitialSearch = 0,
    Combat = 1,
    Search = 2,
    // 仅用于兼容旧快照；推进时会迁移为 Search，且不会恢复生命或灵力。
    Recovery = 3,
    // 仅用于兼容旧快照；新战斗中的死亡由 Combat 内的角色状态表达。
    Death = 4
}

/// <summary>
/// 玩家跨连续遭遇及脱战后完全持久化的生命和灵力。
/// 设定上不进行任何自然自动回复，生命和灵力恢复完全依赖消耗品（丹药等）或主动技能；脱战或取消战斗时保持当前生命与灵力，若处于死亡状态则脱战复活（生命值恢复至 1 点，灵力不回复）。
/// </summary>
public sealed class XiuxianCombatResourceState
{
    public bool Initialized { get; set; }
    public long Hitpoints { get; set; }
    public long Manapoints { get; set; }
    public double HitpointRemainder { get; set; }
    public double ManapointRemainder { get; set; }
    public Dictionary<string, DateTimeOffset> AbilityCooldowns { get; set; } = new(StringComparer.Ordinal);
    public List<DateTimeOffset> ConsumableSlotCooldowns { get; set; } = [];

    public XiuxianCombatResourceState Clone() => new()
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
/// 单个历练行动的持久运行态。当前战斗以固定快照重放，保证在线推进、离线快进和服务重启结果一致。
/// </summary>
public sealed class XiuxianCombatExpeditionState
{
    public XiuxianCombatExpeditionPhase Phase { get; set; } = XiuxianCombatExpeditionPhase.InitialSearch;
    public long PhaseElapsedMilliseconds { get; set; }
    public long PhaseDurationMilliseconds { get; set; } = 20_000;
    public string? EncounterId { get; set; }
    public long BattleSequence { get; set; }
    public long CompletedEncountersBeforeBattle { get; set; }
    public DateTimeOffset BattleStartedAt { get; set; }
    public long InitialHitpoints { get; set; }
    public long InitialManapoints { get; set; }
    public Dictionary<string, long> InitialAbilityCooldowns { get; set; } = new(StringComparer.Ordinal);
    public List<long> InitialConsumableCooldowns { get; set; } = [];
    public int EmittedEventCount { get; set; }
    public XiuxianCombatLoadout? BattleLoadout { get; set; }
    public List<XiuxianBuffState> BattleBuffs { get; set; } = [];
    public Dictionary<string, long> BattleInventory { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AppliedConsumedItems { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AppliedDroppedItems { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AppliedRareDroppedItems { get; set; } = new(StringComparer.Ordinal);
    public int AppliedWins { get; set; }
    public int AppliedLosses { get; set; }
    public int AppliedEnemiesDefeated { get; set; }
    public int AppliedProcessedEvents { get; set; }
    public long AppliedDamageDealt { get; set; }
    public long AppliedDamageTaken { get; set; }
    public double AppliedExperience { get; set; }
    public long AppliedSpirit { get; set; }
    public bool CompleteAfterRespawn { get; set; }

    public XiuxianCombatExpeditionState Clone() => new()
    {
        Phase = Phase,
        PhaseElapsedMilliseconds = PhaseElapsedMilliseconds,
        PhaseDurationMilliseconds = PhaseDurationMilliseconds,
        EncounterId = EncounterId,
        BattleSequence = BattleSequence,
        CompletedEncountersBeforeBattle = CompletedEncountersBeforeBattle,
        BattleStartedAt = BattleStartedAt,
        InitialHitpoints = InitialHitpoints,
        InitialManapoints = InitialManapoints,
        InitialAbilityCooldowns = new Dictionary<string, long>(InitialAbilityCooldowns, StringComparer.Ordinal),
        InitialConsumableCooldowns = new List<long>(InitialConsumableCooldowns),
        EmittedEventCount = EmittedEventCount,
        BattleLoadout = BattleLoadout?.Clone(),
        BattleBuffs = BattleBuffs.Select(CloneBuff).ToList(),
        BattleInventory = new Dictionary<string, long>(BattleInventory, StringComparer.Ordinal),
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

    private static XiuxianBuffState CloneBuff(XiuxianBuffState buff) => new()
    {
        BuffId = buff.BuffId,
        Name = buff.Name,
        Stacks = buff.Stacks,
        QiGainMultiplier = buff.QiGainMultiplier,
        AttackLevelRatio = buff.AttackLevelRatio,
        DamageRatio = buff.DamageRatio,
        AttackSpeedRatio = buff.AttackSpeedRatio,
        GatheringOutputMultiplier = buff.GatheringOutputMultiplier,
        AlchemyOutputMultiplier = buff.AlchemyOutputMultiplier,
        SourceItemId = buff.SourceItemId,
        ExpiresAt = buff.ExpiresAt
    };
}

public sealed class XiuxianAutoUseSlot
{
    public string? ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Enabled { get; set; }
    public List<XiuxianCombatTrigger>? Triggers { get; set; }

    public XiuxianAutoUseSlot Clone()
    {
        return new XiuxianAutoUseSlot
        {
            ItemId = ItemId,
            Quantity = Quantity,
            Enabled = Enabled,
            Triggers = Triggers?.Select(trigger => trigger.Clone()).ToList()
        };
    }
}

public sealed class XiuxianActionQueueState
{
    public List<XiuxianQueuedAction> Entries { get; set; } = [];
    // 仅用于读取旧版快照；归一化后会清空，新的循环语义直接轮转 Entries。
    public List<XiuxianQueuedAction> LoopTemplate { get; set; } = [];
    public bool LoopEnabled { get; set; } = true;
    // 每种行动固定 3 格自动使用槽；旧版快照的单槽形态由转换器兼容读取。
    [JsonConverter(typeof(XiuxianAutoUseSlotsJsonConverter))]
    public Dictionary<XiuxianActionType, List<XiuxianAutoUseSlot>> AutoUseSlots { get; set; } =
        new()
        {
            [XiuxianActionType.Cultivation] = [new(), new(), new()],
            [XiuxianActionType.Exploration] = [new(), new(), new()],
            [XiuxianActionType.Alchemy] = [new(), new(), new()],
            [XiuxianActionType.Combat] = [new(), new(), new()]
        };
}

public sealed class XiuxianInventoryState
{
    public Dictionary<string, long> Items { get; set; } = new(StringComparer.Ordinal);

    public long Get(string itemId) => Items.TryGetValue(itemId, out var amount) ? amount : 0;

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

public sealed class XiuxianBuffState
{
    public required string BuffId { get; set; }
    public required string Name { get; set; }
    public int Stacks { get; set; } = 1;
    public double QiGainMultiplier { get; set; } = 1;
    public double AttackLevelRatio { get; set; }
    public double DamageRatio { get; set; }
    public double AttackSpeedRatio { get; set; }
    public double GatheringOutputMultiplier { get; set; } = 1;
    public double AlchemyOutputMultiplier { get; set; } = 1;
    public string? SourceItemId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class XiuxianPlayerStats
{
    public long Qi { get; set; }
    public long Spirit { get; set; }
    public double FoundationExp { get; set; }
    public long HerbsCollected { get; set; }
    public long PillsCrafted { get; set; }
    public long CultivationCycles { get; set; }
    public long CompletedActions { get; set; }
    public long BlockedActions { get; set; }
    public long CombatWins { get; set; }
    public long CombatLosses { get; set; }
    public double CombatExperience { get; set; }
    public long CombatDamageDealt { get; set; }
    public long CombatDamageTaken { get; set; }
    public long CombatEnemiesDefeated { get; set; }
    public long CombatPlayersDefeated { get; set; }
}

public sealed class XiuxianLogEvent
{
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Template { get; set; } = string.Empty;
}

public sealed class XiuxianPlayerState
{
    public Guid PlayerId { get; set; }
    public string Name { get; set; } = "无名修士";
    public XiuxianCharacterVisualDefinition Visual { get; set; } = new() { Renderer = "text", Source = "修" };
    public long Version { get; set; }
    public DateTimeOffset LastSimulatedAt { get; set; }
    public XiuxianPresenceMode PresenceMode { get; set; } = XiuxianPresenceMode.IdleOffline;
    public bool IsPendingBreakthrough { get; set; }
    public XiuxianActionQueueState Queue { get; set; } = new();
    public XiuxianInventoryState Inventory { get; set; } = new();
    public List<XiuxianBuffState> Buffs { get; set; } = [];
    public XiuxianCombatLoadout Combat { get; set; } = new();
    public XiuxianCombatResourceState CombatResources { get; set; } = new();
    public XiuxianWorldState World { get; set; } = new();
    public Dictionary<string, long> CombatEncounterCompletions { get; set; } = new(StringComparer.Ordinal);
    public XiuxianPlayerStats Stats { get; set; } = new();
    public List<string> EventLog { get; set; } = [];
    public long EventSequence { get; set; }
    public List<XiuxianLogEvent> StructuredEventLog { get; set; } = [];

    [JsonIgnore]
    public List<XiuxianLogEvent> PendingEvents { get; } = [];

    [JsonIgnore]
    public XiuxianDirtySections DirtySections { get; set; } = XiuxianDirtySections.All;

    public XiuxianPlayerState Clone()
    {
        return new XiuxianPlayerState
        {
            PlayerId = PlayerId,
            Name = Name,
            Visual = new XiuxianCharacterVisualDefinition
            {
                Renderer = Visual.Renderer,
                Source = Visual.Source,
                Poster = Visual.Poster
            },
            Version = Version,
            LastSimulatedAt = LastSimulatedAt,
            PresenceMode = PresenceMode,
            IsPendingBreakthrough = IsPendingBreakthrough,
            Queue = new XiuxianActionQueueState
            {
                LoopEnabled = Queue.LoopEnabled,
                Entries = Queue.Entries.Select(CloneAction).ToList(),
                LoopTemplate = Queue.LoopTemplate.Select(CloneAction).ToList(),
                AutoUseSlots = Queue.AutoUseSlots.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(slot => slot.Clone()).ToList())
            },
            Inventory = new XiuxianInventoryState { Items = new Dictionary<string, long>(Inventory.Items, StringComparer.Ordinal) },
            Buffs = Buffs.Select(buff => new XiuxianBuffState
            {
                BuffId = buff.BuffId,
                Name = buff.Name,
                Stacks = buff.Stacks,
                QiGainMultiplier = buff.QiGainMultiplier,
                AttackLevelRatio = buff.AttackLevelRatio,
                DamageRatio = buff.DamageRatio,
                AttackSpeedRatio = buff.AttackSpeedRatio,
                GatheringOutputMultiplier = buff.GatheringOutputMultiplier,
                AlchemyOutputMultiplier = buff.AlchemyOutputMultiplier,
                SourceItemId = buff.SourceItemId,
                ExpiresAt = buff.ExpiresAt
            }).ToList(),
            Combat = Combat.Clone(),
            CombatResources = CombatResources?.Clone() ?? new XiuxianCombatResourceState(),
            World = World.Clone(),
            CombatEncounterCompletions = new Dictionary<string, long>(CombatEncounterCompletions, StringComparer.Ordinal),
            Stats = new XiuxianPlayerStats
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
            EventLog = [.. EventLog],
            EventSequence = EventSequence,
            StructuredEventLog = StructuredEventLog.Select(CloneLogEvent).ToList()
        };
    }

    private static XiuxianLogEvent CloneLogEvent(XiuxianLogEvent entry)
    {
        return new XiuxianLogEvent
        {
            Sequence = entry.Sequence,
            OccurredAt = entry.OccurredAt,
            Template = entry.Template
        };
    }

    private static XiuxianQueuedAction CloneAction(XiuxianQueuedAction action)
    {
        return new XiuxianQueuedAction
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
public sealed class XiuxianPlayerComponent : YuoComponent
{
    public XiuxianPlayerState State { get; set; } = new();
}

public sealed record XiuxianSimulationResult(
    XiuxianDirtySections DirtySections,
    int CompletedActions,
    int TransitionCount,
    IReadOnlyList<string> Events,
    int CombatEvents = 0);
