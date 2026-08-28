namespace Domain.Player;

/// <summary>
/// 跨进程/持久化使用的纯数据文档。它不包含 Entity、Fiber、网络连接或存储句柄。
/// </summary>
public sealed record PlayerAggregateState(
    PlayerId PlayerId,
    AccountId AccountId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSimulatedAt,
    PresenceState Presence,
    long StateVersion,
    string GameDataFingerprint,
    PlayerProgressionState Progression,
    PlayerWorldStateDocument World,
    PlayerCombatStateDocument Combat,
    IReadOnlyList<ActiveBuff> Buffs,
    IReadOnlyList<InstanceId> EquippedInstances,
    IReadOnlyList<AbilityId> LearnedAbilities,
    PlayerSettingsStateDocument Settings,
    IReadOnlyList<QueuedActionState> Actions,
    IReadOnlyList<ItemId>? EquippedItemIds = null);

public sealed record PlayerProgressionState(
    int RealmLevel,
    double Qi,
    double Experience,
    double LifetimeQi,
    double CompletedActions);

public sealed record PlayerWorldStateDocument(
    string NodeId,
    double X,
    double Y,
    double ExplorationSeed);

public sealed record PlayerCombatStateDocument(
    double Hitpoints,
    double MaxHitpoints,
    double Manapoints,
    double MaxManapoints,
    double AttackPower,
    double Defense);

public sealed record PlayerSettingsStateDocument(
    string Locale,
    bool NotificationsEnabled,
    bool LoopEnabled = true,
    IReadOnlyList<AutoUseSlotState>? AutoUseSlots = null);

public sealed record QueuedActionState(
    Guid Id,
    ActionIntent Intent,
    DateTimeOffset EnqueuedAt,
    ActionQueueStatus Status,
    double ProgressSeconds,
    double RemainingRepeats,
    double CompletedRepeats);

public sealed partial class PlayerAggregate
{
    public PlayerAggregateState ExportState() => new(
        PlayerId,
        AccountId,
        DisplayName,
        CreatedAt,
        LastSimulatedAt,
        Presence,
        StateVersion,
        GameDataFingerprint,
        new PlayerProgressionState(Progression.RealmLevel, Progression.Qi, Progression.Experience, Progression.LifetimeQi, Progression.CompletedActions),
        new PlayerWorldStateDocument(World.NodeId, World.X, World.Y, World.ExplorationSeed),
        new PlayerCombatStateDocument(Combat.Hitpoints, Combat.MaxHitpoints, Combat.Manapoints, Combat.MaxManapoints, Combat.AttackPower, Combat.Defense),
        Buffs.Active.ToArray(),
        Loadout.EquippedInstances.ToArray(),
        Loadout.LearnedAbilities.ToArray(),
        new PlayerSettingsStateDocument(Settings.Locale, Settings.NotificationsEnabled, Settings.LoopEnabled, Settings.AutoUseSlots.ToArray()),
        _actions.Select(action => new QueuedActionState(action.Id, action.Intent, action.EnqueuedAt, action.Status, action.ProgressSeconds, action.RemainingRepeats, action.CompletedRepeats)).ToArray(),
        Loadout.EquippedItemIds.ToArray());

    public static PlayerAggregate Restore(PlayerAggregateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var aggregate = new PlayerAggregate(state.PlayerId, state.AccountId, state.DisplayName, state.CreatedAt, state.GameDataFingerprint)
        {
            LastSimulatedAt = state.LastSimulatedAt.ToUniversalTime(),
            Presence = state.Presence,
            StateVersion = state.StateVersion
        };
        if (state.StateVersion < 0 || aggregate.LastSimulatedAt < aggregate.CreatedAt)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家状态版本或时间无效。");
        if (state.Progression is null || state.World is null || state.Combat is null || state.Settings is null || state.Buffs is null ||
            state.EquippedInstances is null || state.LearnedAbilities is null || state.Actions is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家 section 不能为空。");
        if (!double.IsFinite(state.Progression.Qi) || state.Progression.Qi < 0 ||
            !double.IsFinite(state.Progression.Experience) || state.Progression.Experience < 0 ||
            !double.IsFinite(state.Progression.LifetimeQi) || state.Progression.LifetimeQi < 0 ||
            !double.IsFinite(state.Progression.CompletedActions) || state.Progression.CompletedActions < 0 ||
            !double.IsFinite(state.World.X) || !double.IsFinite(state.World.Y) || !double.IsFinite(state.World.ExplorationSeed) ||
            !double.IsFinite(state.Combat.Hitpoints) || state.Combat.Hitpoints < 0 ||
            !double.IsFinite(state.Combat.MaxHitpoints) || state.Combat.MaxHitpoints <= 0 || state.Combat.Hitpoints > state.Combat.MaxHitpoints ||
            !double.IsFinite(state.Combat.Manapoints) || state.Combat.Manapoints < 0 ||
            !double.IsFinite(state.Combat.MaxManapoints) || state.Combat.MaxManapoints <= 0 || state.Combat.Manapoints > state.Combat.MaxManapoints ||
            !double.IsFinite(state.Combat.AttackPower) || state.Combat.AttackPower < 0 ||
            !double.IsFinite(state.Combat.Defense) || state.Combat.Defense < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家数值状态无效。");
        if (state.Progression.RealmLevel < 1 || string.IsNullOrWhiteSpace(state.World.NodeId) || state.World.NodeId.Length > 128
            || string.IsNullOrWhiteSpace(state.Settings.Locale) || state.Settings.Locale.Length > 32)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家基础 section 标识无效。");
        if (state.EquippedInstances.Any(item => item.IsEmpty)
            || (state.EquippedItemIds ?? []).Any(item => !item.IsValid)
            || state.LearnedAbilities.Any(ability => !ability.IsValid)
            || (state.Settings.AutoUseSlots ?? []).Any(slot => slot is null || string.IsNullOrWhiteSpace(slot.ActionType)
                || slot.ActionType.Length > 32 || slot.SlotIndex is < 0 or > 15 || !double.IsFinite(slot.Quantity) || slot.Quantity <= 0
                || (slot.ItemId is not null && (slot.ItemId.Length == 0 || slot.ItemId.Length > 128))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家装配或设置 section 无效。");
        aggregate.Progression.RealmLevel = state.Progression.RealmLevel;
        aggregate.Progression.Qi = state.Progression.Qi;
        aggregate.Progression.Experience = state.Progression.Experience;
        aggregate.Progression.LifetimeQi = state.Progression.LifetimeQi;
        aggregate.Progression.CompletedActions = state.Progression.CompletedActions;
        aggregate.World.NodeId = state.World.NodeId;
        aggregate.World.X = state.World.X;
        aggregate.World.Y = state.World.Y;
        aggregate.World.ExplorationSeed = state.World.ExplorationSeed;
        aggregate.Combat.Hitpoints = state.Combat.Hitpoints;
        aggregate.Combat.MaxHitpoints = state.Combat.MaxHitpoints;
        aggregate.Combat.Manapoints = state.Combat.Manapoints;
        aggregate.Combat.MaxManapoints = state.Combat.MaxManapoints;
        aggregate.Combat.AttackPower = state.Combat.AttackPower;
        aggregate.Combat.Defense = state.Combat.Defense;
        aggregate.Buffs.Active.AddRange(state.Buffs);
        aggregate.Loadout.EquippedInstances.AddRange(state.EquippedInstances);
        aggregate.Loadout.EquippedItemIds.AddRange(state.EquippedItemIds ?? []);
        aggregate.Loadout.LearnedAbilities.AddRange(state.LearnedAbilities);
        aggregate.Settings.Locale = state.Settings.Locale;
        aggregate.Settings.NotificationsEnabled = state.Settings.NotificationsEnabled;
        aggregate.Settings.LoopEnabled = state.Settings.LoopEnabled;
        aggregate.Settings.AutoUseSlots.AddRange(state.Settings.AutoUseSlots ?? []);
        if (state.Actions.Count > MaxActionQueueLength)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动队列最多只能包含 {MaxActionQueueLength} 项。");
        var actionIds = new HashSet<Guid>();
        foreach (var actionState in state.Actions)
        {
            var intent = actionState?.Intent;
            var repeatCountValid = intent is not null && double.IsFinite(intent.RepeatCount)
                && (intent.RepeatCount == -1 || intent.RepeatCount is >= 1 and <= 1_000_000);
            var remainingRepeatsValid = actionState is not null && double.IsFinite(actionState.RemainingRepeats)
                && (actionState.RemainingRepeats == -1 || actionState.RemainingRepeats > 0);
            if (actionState is null || intent is null || actionState.Id == Guid.Empty || !actionIds.Add(actionState.Id) || !intent.ActionId.IsValid ||
                !repeatCountValid ||
                !Enum.IsDefined(actionState.Status) ||
                (intent.TargetNodeId is not null && (intent.TargetNodeId.Length == 0 || intent.TargetNodeId.Length > 128)) ||
                (intent.Parameters is not null && intent.Parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || !double.IsFinite(pair.Value))) ||
                !double.IsFinite(actionState.ProgressSeconds) || actionState.ProgressSeconds < 0 ||
                !remainingRepeatsValid ||
                !double.IsFinite(actionState.CompletedRepeats) || actionState.CompletedRepeats < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "行动状态无效。");
            var normalizedIntent = new ActionIntent(
                intent.ActionId,
                intent.RepeatCount,
                intent.TargetNodeId,
                intent.Parameters is null
                    ? null
                    : new ReadOnlyDictionary<string, double>(intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            var action = new QueuedAction(actionState.Id, normalizedIntent, actionState.EnqueuedAt)
            {
                Status = actionState.Status,
                ProgressSeconds = actionState.ProgressSeconds,
                RemainingRepeats = actionState.RemainingRepeats,
                CompletedRepeats = actionState.CompletedRepeats
            };
            aggregate._actions.Add(action);
        }
        aggregate._dirtySections.Clear();
        return aggregate;
    }

    /// <summary>
    /// 将已验证的 canonical 状态写回当前聚合，用于同一 actor 内的失败回滚或一致性恢复。
    /// 玩家/账号创建信息不可被替换，所有可变 section、行动队列和版本游标会被完整覆盖。
    /// </summary>
    public void RestoreFrom(PlayerAggregateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PlayerId != PlayerId || state.AccountId != AccountId || state.CreatedAt.ToUniversalTime() != CreatedAt)
            throw new DomainException(DomainErrorCode.Conflict, "恢复状态与当前玩家身份不一致。");
        var restored = Restore(state);
        DisplayName = restored.DisplayName;
        LastSimulatedAt = restored.LastSimulatedAt;
        Presence = restored.Presence;
        StateVersion = restored.StateVersion;
        GameDataFingerprint = restored.GameDataFingerprint;
        Progression.RealmLevel = restored.Progression.RealmLevel;
        Progression.Qi = restored.Progression.Qi;
        Progression.Experience = restored.Progression.Experience;
        Progression.LifetimeQi = restored.Progression.LifetimeQi;
        Progression.CompletedActions = restored.Progression.CompletedActions;
        World.NodeId = restored.World.NodeId;
        World.X = restored.World.X;
        World.Y = restored.World.Y;
        World.ExplorationSeed = restored.World.ExplorationSeed;
        Combat.Hitpoints = restored.Combat.Hitpoints;
        Combat.MaxHitpoints = restored.Combat.MaxHitpoints;
        Combat.Manapoints = restored.Combat.Manapoints;
        Combat.MaxManapoints = restored.Combat.MaxManapoints;
        Combat.AttackPower = restored.Combat.AttackPower;
        Combat.Defense = restored.Combat.Defense;
        Buffs.Active.Clear();
        Buffs.Active.AddRange(restored.Buffs.Active);
        Loadout.EquippedInstances.Clear();
        Loadout.EquippedInstances.AddRange(restored.Loadout.EquippedInstances);
        Loadout.EquippedItemIds.Clear();
        Loadout.EquippedItemIds.AddRange(restored.Loadout.EquippedItemIds);
        Loadout.LearnedAbilities.Clear();
        Loadout.LearnedAbilities.AddRange(restored.Loadout.LearnedAbilities);
        Settings.Locale = restored.Settings.Locale;
        Settings.NotificationsEnabled = restored.Settings.NotificationsEnabled;
        Settings.LoopEnabled = restored.Settings.LoopEnabled;
        Settings.AutoUseSlots.Clear();
        Settings.AutoUseSlots.AddRange(restored.Settings.AutoUseSlots);
        _actions.Clear();
        _actions.AddRange(restored._actions.Select(action => action.Clone()));
        _dirtySections.Clear();
        _sectionVersion = restored._sectionVersion;
    }
}
