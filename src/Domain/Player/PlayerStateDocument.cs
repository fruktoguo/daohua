using Domain.World;

namespace Domain.Player;

/// <summary>
/// 跨进程、跨网络或持久化落库使用的纯数据快照文档。
/// 严格遵循纯数据传输（POCO），不包含 Entity、Fiber、网络连接或存储句柄。
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
    PlayerLoadoutDocument Loadout,
    PlayerDuelStateDocument Duel,
    IReadOnlyList<ItemId>? EquippedItemIds = null);

/// <summary>境界与修为进度的离线/持久化快照 DTO</summary>
public sealed record PlayerProgressionState(
    int RealmLevel,
    double Qi,
    double Experience,
    double LifetimeQi,
    double CompletedActions,
    double SimulationCarrySeconds = 0,
    double NormalKills = 0,
    double EliteKills = 0,
    double BossKills = 0,
    double TotalDefeats = 0,
    double CultivationSeconds = 0,
    double AgeSeconds = 0);

/// <summary>世界地理坐标与探索种子快照 DTO</summary>
public sealed record PlayerWorldStateDocument(
    string NodeId,
    double X,
    double Y,
    double ExplorationSeed,
    string? EdgeId = null,
    double EdgeProgress = 0);

/// <summary>战斗属性数值快照 DTO</summary>
public sealed record PlayerCombatStateDocument(
    double Hitpoints,
    double MaxHitpoints,
    double Manapoints,
    double MaxManapoints,
    double AttackPower,
    double Defense,
    double RegenCarrySeconds = 0)
{
    /// <summary>获取或设置 Skills 的值。</summary>
    public PlayerCombatSkillState Skills { get; init; } = PlayerCombatSkillState.Empty;
}

/// <summary>客户端设置与快捷自动使用配置快照 DTO</summary>
public sealed record PlayerSettingsStateDocument(
    string Locale,
    bool NotificationsEnabled,
    bool LoopEnabled = true,
    IReadOnlyList<AutoUseSlotState>? AutoUseSlots = null,
    double RenameCooldownUntilUnixSeconds = 0);

/// <summary>行动项状态快照 DTO，记录进度与剩余轮次</summary>
public sealed record QueuedActionState(
    Guid Id,
    ActionIntent Intent,
    DateTimeOffset EnqueuedAt,
    ActionQueueStatus Status,
    double ProgressSeconds,
    double RemainingRepeats,
    double CompletedRepeats,
    CombatExpeditionState? Expedition = null,
    TravelActionState? Travel = null);

/// <summary>玩家聚合 的领域定义。</summary>
public sealed partial class PlayerAggregate
{
    private ActiveBuff[]? _cachedBuffs;
    private InstanceId[]? _cachedEquippedInstances;
    private AbilityId[]? _cachedLearnedAbilities;
    private ItemId[]? _cachedEquippedItemIds;
    private AutoUseSlotState[]? _cachedAutoUseSlots;
    private PlayerSettingsStateDocument? _cachedSettingsDoc;

    /// <summary>
    /// 将当前可变领域聚合导出为不可变的纯数据快照文档。
    /// 经过零闭包与空数组/未脏分段单例复用优化，降低高频快照导出的 GC 压力。
    /// </summary>
    public PlayerAggregateState ExportState()
    {
        QueuedActionState[] actions;
        if (_actions.Count == 0)
        {
            actions = [];
        }
        else
        {
            actions = new QueuedActionState[_actions.Count];
            for (var i = 0; i < _actions.Count; i++)
            {
                var action = _actions[i];
                actions[i] = new QueuedActionState(action.Id, action.Intent, action.EnqueuedAt, action.Status, action.ProgressSeconds, action.RemainingRepeats, action.CompletedRepeats, action.Expedition, action.Travel);
            }
        }

        return new(
            PlayerId,
            AccountId,
            DisplayName,
            CreatedAt,
            LastSimulatedAt,
            Presence,
            StateVersion,
            GameDataFingerprint,
            new PlayerProgressionState(Progression.RealmLevel, Progression.Qi, Progression.Experience, Progression.LifetimeQi, Progression.CompletedActions, Progression.SimulationCarrySeconds,
                Progression.NormalKills, Progression.EliteKills, Progression.BossKills, Progression.TotalDefeats, Progression.CultivationSeconds, Progression.AgeSeconds),
            new PlayerWorldStateDocument(World.NodeId, World.X, World.Y, World.ExplorationSeed, World.EdgeId, World.EdgeProgress),
            new PlayerCombatStateDocument(Combat.Hitpoints, Combat.MaxHitpoints, Combat.Manapoints, Combat.MaxManapoints, Combat.AttackPower, Combat.Defense, Combat.RegenCarrySeconds) { Skills = Combat.Skills },
            GetCachedBuffs(),
            GetCachedEquippedInstances(),
            GetCachedLearnedAbilities(),
            GetCachedSettings(),
            actions,
            Loadout.Export(),
            ExportDuel(),
            GetCachedEquippedItemIds());
    }

    private static PlayerBattleReportDocument ExportReport(PlayerBattleReport report) => new(
        report.ReportId, report.ActionId, report.ActionName,
        report.StartedAtUnixSeconds, report.EndedAtUnixSeconds,
        report.Qi, report.Experience,
        new PlayerRootExpDocument(report.RootExp.Stamina, report.RootExp.Intelligence, report.RootExp.Attack,
            report.RootExp.Defense, report.RootExp.Swordsmanship, report.RootExp.Talisman),
        report.Items.ToArray(), report.DuelWins, report.DuelLosses);

    private PlayerDuelStateDocument ExportDuel() => new(
        Duel.EncounterCharges,
        Duel.CooldownUntilUnixSeconds,
        Duel.ActiveDuelId,
        Duel.StagedItems.ToArray(),
        Duel.CurrentReport is null ? null : ExportReport(Duel.CurrentReport),
        Duel.Reports.Select(ExportReport).ToArray());

    private ActiveBuff[] GetCachedBuffs()
    {
        if (Buffs.Active.Count == 0) return [];
        if (_cachedBuffs is not null && _cachedBuffs.Length == Buffs.Active.Count)
        {
            var same = true;
            for (var i = 0; i < _cachedBuffs.Length; i++)
            {
                if (_cachedBuffs[i] != Buffs.Active[i]) { same = false; break; }
            }
            if (same) return _cachedBuffs;
        }
        return _cachedBuffs = Buffs.Active.ToArray();
    }

    private InstanceId[] GetCachedEquippedInstances()
    {
        if (Loadout.EquippedInstances.Count == 0) return [];
        if (_cachedEquippedInstances is not null && _cachedEquippedInstances.Length == Loadout.EquippedInstances.Count)
        {
            var same = true;
            for (var i = 0; i < _cachedEquippedInstances.Length; i++)
            {
                if (_cachedEquippedInstances[i] != Loadout.EquippedInstances[i]) { same = false; break; }
            }
            if (same) return _cachedEquippedInstances;
        }
        return _cachedEquippedInstances = Loadout.EquippedInstances.ToArray();
    }

    private AbilityId[] GetCachedLearnedAbilities()
    {
        if (Loadout.LearnedAbilities.Count == 0) return [];
        if (_cachedLearnedAbilities is not null && _cachedLearnedAbilities.Length == Loadout.LearnedAbilities.Count)
        {
            var same = true;
            for (var i = 0; i < _cachedLearnedAbilities.Length; i++)
            {
                if (_cachedLearnedAbilities[i] != Loadout.LearnedAbilities[i]) { same = false; break; }
            }
            if (same) return _cachedLearnedAbilities;
        }
        return _cachedLearnedAbilities = Loadout.LearnedAbilities.ToArray();
    }

    private ItemId[] GetCachedEquippedItemIds()
    {
        if (Loadout.EquippedItemIds.Count == 0) return [];
        if (_cachedEquippedItemIds is not null && _cachedEquippedItemIds.Length == Loadout.EquippedItemIds.Count)
        {
            var same = true;
            for (var i = 0; i < _cachedEquippedItemIds.Length; i++)
            {
                if (_cachedEquippedItemIds[i] != Loadout.EquippedItemIds[i]) { same = false; break; }
            }
            if (same) return _cachedEquippedItemIds;
        }
        return _cachedEquippedItemIds = Loadout.EquippedItemIds.ToArray();
    }

    private PlayerSettingsStateDocument GetCachedSettings()
    {
        var slots = GetCachedAutoUseSlots();
        if (_cachedSettingsDoc is not null
            && _cachedSettingsDoc.Locale == Settings.Locale
            && _cachedSettingsDoc.NotificationsEnabled == Settings.NotificationsEnabled
            && _cachedSettingsDoc.LoopEnabled == Settings.LoopEnabled
            && _cachedSettingsDoc.RenameCooldownUntilUnixSeconds == Settings.RenameCooldownUntilUnixSeconds
            && ReferenceEquals(_cachedSettingsDoc.AutoUseSlots, slots))
        {
            return _cachedSettingsDoc;
        }
        return _cachedSettingsDoc = new PlayerSettingsStateDocument(
            Settings.Locale,
            Settings.NotificationsEnabled,
            Settings.LoopEnabled,
            slots,
            Settings.RenameCooldownUntilUnixSeconds);
    }

    private AutoUseSlotState[] GetCachedAutoUseSlots()
    {
        if (Settings.AutoUseSlots.Count == 0) return [];
        if (_cachedAutoUseSlots is not null && _cachedAutoUseSlots.Length == Settings.AutoUseSlots.Count)
        {
            var same = true;
            for (var i = 0; i < _cachedAutoUseSlots.Length; i++)
            {
                if (_cachedAutoUseSlots[i] != Settings.AutoUseSlots[i]) { same = false; break; }
            }
            if (same) return _cachedAutoUseSlots;
        }
        return _cachedAutoUseSlots = Settings.AutoUseSlots.ToArray();
    }

    /// <summary>
    /// 从持久化纯数据快照还原出全新的可变角色聚合实例。
    /// 执行严格的业务不变量核验（数值有限性、非负约束、界限范围），并对历史浮点重复次数执行容错自愈。
    /// </summary>
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
            !double.IsFinite(state.Progression.SimulationCarrySeconds) || state.Progression.SimulationCarrySeconds < 0 ||
            !double.IsFinite(state.Progression.NormalKills) || state.Progression.NormalKills < 0 ||
            !double.IsFinite(state.Progression.EliteKills) || state.Progression.EliteKills < 0 ||
            !double.IsFinite(state.Progression.BossKills) || state.Progression.BossKills < 0 ||
            !double.IsFinite(state.Progression.TotalDefeats) || state.Progression.TotalDefeats < 0 ||
            !double.IsFinite(state.Progression.CultivationSeconds) || state.Progression.CultivationSeconds < 0 ||
            !double.IsFinite(state.Progression.AgeSeconds) || state.Progression.AgeSeconds < 0 ||
            !double.IsFinite(state.World.X) || !double.IsFinite(state.World.Y) || !double.IsFinite(state.World.ExplorationSeed) ||
            !double.IsFinite(state.World.EdgeProgress) || state.World.EdgeProgress < 0 || state.World.EdgeProgress > 1 ||
            !double.IsFinite(state.Combat.Hitpoints) || state.Combat.Hitpoints < 0 ||
            !double.IsFinite(state.Combat.MaxHitpoints) || state.Combat.MaxHitpoints <= 0 || state.Combat.Hitpoints > state.Combat.MaxHitpoints ||
            !double.IsFinite(state.Combat.Manapoints) || state.Combat.Manapoints < 0 ||
            !double.IsFinite(state.Combat.MaxManapoints) || state.Combat.MaxManapoints <= 0 || state.Combat.Manapoints > state.Combat.MaxManapoints ||
            !double.IsFinite(state.Combat.AttackPower) || state.Combat.AttackPower < 0 ||
            !double.IsFinite(state.Combat.Defense) || state.Combat.Defense < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家数值状态无效。");
        if (state.Progression.RealmLevel < 1 || string.IsNullOrWhiteSpace(state.World.NodeId) || state.World.NodeId.Length > 128
            || (state.World.EdgeId is not null && (state.World.EdgeId.Length == 0 || state.World.EdgeId.Length > 128))
            || string.IsNullOrWhiteSpace(state.Settings.Locale) || state.Settings.Locale.Length > 32)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家基础 section 标识无效。");
        if (state.Duel is null || !IsValidDuelDocument(state.Duel))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家斗法分段无效。");
        if (state.EquippedInstances.Any(item => item.IsEmpty)
            || (state.Settings.AutoUseSlots ?? []).Any(slot => slot is null || string.IsNullOrWhiteSpace(slot.ActionType)
                || slot.ActionType.Length > 32 || slot.SlotIndex is < 0 || slot.SlotIndex > AutoUseSlotState.MaxSlotIndex
                || !double.IsFinite(slot.Quantity) || slot.Quantity <= 0
                || !double.IsFinite(slot.RemainingDurationSeconds) || slot.RemainingDurationSeconds < 0
                || (slot.ItemId is not null && (slot.ItemId.Length == 0 || slot.ItemId.Length > 128))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家装配或设置 section 无效。");
        aggregate.Progression.RealmLevel = state.Progression.RealmLevel;
        aggregate.Progression.Qi = state.Progression.Qi;
        aggregate.Progression.Experience = state.Progression.Experience;
        aggregate.Progression.LifetimeQi = state.Progression.LifetimeQi;
        aggregate.Progression.CompletedActions = state.Progression.CompletedActions;
        aggregate.Progression.SimulationCarrySeconds = state.Progression.SimulationCarrySeconds;
        aggregate.Progression.NormalKills = state.Progression.NormalKills;
        aggregate.Progression.EliteKills = state.Progression.EliteKills;
        aggregate.Progression.BossKills = state.Progression.BossKills;
        aggregate.Progression.TotalDefeats = state.Progression.TotalDefeats;
        aggregate.Progression.CultivationSeconds = state.Progression.CultivationSeconds;
        aggregate.Progression.AgeSeconds = state.Progression.AgeSeconds;
        aggregate.World.NodeId = state.World.NodeId;
        aggregate.World.EdgeId = state.World.EdgeId;
        aggregate.World.EdgeProgress = state.World.EdgeProgress;
        aggregate.World.X = state.World.X;
        aggregate.World.Y = state.World.Y;
        aggregate.World.ExplorationSeed = state.World.ExplorationSeed;
        aggregate.Combat.Hitpoints = state.Combat.Hitpoints;
        aggregate.Combat.MaxHitpoints = state.Combat.MaxHitpoints;
        aggregate.Combat.Manapoints = state.Combat.Manapoints;
        aggregate.Combat.MaxManapoints = state.Combat.MaxManapoints;
        aggregate.Combat.AttackPower = state.Combat.AttackPower;
        aggregate.Combat.Defense = state.Combat.Defense;
        if (!double.IsFinite(state.Combat.RegenCarrySeconds) || state.Combat.RegenCarrySeconds < 0 || state.Combat.RegenCarrySeconds >= 5)
            throw new DomainException(DomainErrorCode.InvalidArgument, "回复时钟余量无效。");
        aggregate.Combat.RegenCarrySeconds = state.Combat.RegenCarrySeconds;
        if (state.Combat.Skills is null || !state.Combat.Skills.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能时钟状态无效。");
        aggregate.Combat.Skills = state.Combat.Skills;
        aggregate.Buffs.Active.AddRange(state.Buffs);
        aggregate.Loadout.EquippedInstances.AddRange(state.EquippedInstances);
        aggregate.Loadout.EquippedItemIds.AddRange(state.EquippedItemIds ?? []);
        aggregate.Loadout.LearnedAbilities.AddRange(state.LearnedAbilities);
        aggregate.Loadout.Restore(state.Loadout);
        aggregate.RestoreDuel(state.Duel);
        aggregate.Settings.Locale = state.Settings.Locale;
        aggregate.Settings.NotificationsEnabled = state.Settings.NotificationsEnabled;
        aggregate.Settings.LoopEnabled = state.Settings.LoopEnabled;
        aggregate.Settings.RenameCooldownUntilUnixSeconds = state.Settings.RenameCooldownUntilUnixSeconds;
        aggregate.Settings.AutoUseSlots.AddRange(state.Settings.AutoUseSlots ?? []);
        if (state.Actions.Count > MaxActionQueueLength)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"行动队列最多只能包含 {MaxActionQueueLength} 项。");
        var actionIds = new HashSet<Guid>();
        foreach (var actionState in state.Actions)
        {
            var intent = actionState?.Intent;
            if (actionState is null || intent is null || actionState.Id == Guid.Empty || !actionIds.Add(actionState.Id) || !intent.ActionId.IsValid ||
                !Enum.IsDefined(actionState.Status) ||
                (intent.TargetNodeId is not null && (intent.TargetNodeId.Length == 0 || intent.TargetNodeId.Length > 128)) ||
                HasInvalidParameters(intent.Parameters) ||
                !double.IsFinite(actionState.ProgressSeconds) || actionState.ProgressSeconds < 0 ||
                !double.IsFinite(actionState.CompletedRepeats) || actionState.CompletedRepeats < 0 ||
                actionState.Expedition is { IsValid: false } ||
                actionState.Travel is { IsValid: false })
                throw new DomainException(DomainErrorCode.InvalidArgument, "行动状态无效。");
            // 开发期容错自愈：旧版本曾允许非整数 RepeatCount 入队，递减后 RemainingRepeats 会落入 (0,1) 或负数区间，
            // 造成 "== 0" 永不成立的事实无限行动。此处对非整数值向下取整修正；负值不参与取整，避免翻转为 -1 无限语义；
            // 修正后仍无效（含已耗尽为 0）的行动按自愈规约安全出队，不阻断整个存档恢复。
            var repeatCount = intent.RepeatCount == -1 || intent.RepeatCount <= 0 ? intent.RepeatCount : Math.Floor(intent.RepeatCount);
            var remainingRepeats = actionState.RemainingRepeats == -1 || actionState.RemainingRepeats <= 0
                ? actionState.RemainingRepeats
                : Math.Floor(actionState.RemainingRepeats);
            var repeatCountValid = double.IsFinite(repeatCount) && (repeatCount == -1 || repeatCount is >= 1 and <= 1_000_000);
            var remainingRepeatsValid = double.IsFinite(remainingRepeats) && (remainingRepeats == -1 || remainingRepeats >= 1);
            if (!repeatCountValid || !remainingRepeatsValid) continue;
            if (actionState.Expedition is { } expedition
                && ((expedition.Phase == CombatExpeditionPhase.Combat
                        && Math.Abs(actionState.ProgressSeconds) > 1e-9)
                    || (expedition.Phase is CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search
                        && Math.Abs(actionState.ProgressSeconds - expedition.PhaseElapsedSeconds) > 1e-9)))
                throw new DomainException(DomainErrorCode.InvalidArgument, "战斗行动进度与远征阶段不一致。");
            var normalizedIntent = new ActionIntent(
                intent.ActionId,
                repeatCount,
                intent.TargetNodeId,
                intent.Parameters is null
                    ? null
                    : new ReadOnlyDictionary<string, double>(intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                intent.PvpEnabled);
            var action = new QueuedAction(actionState.Id, normalizedIntent, actionState.EnqueuedAt)
            {
                Status = actionState.Status,
                ProgressSeconds = actionState.ProgressSeconds,
                RemainingRepeats = remainingRepeats,
                CompletedRepeats = actionState.CompletedRepeats,
                Expedition = actionState.Expedition,
                Travel = actionState.Travel
            };
            aggregate._actions.Add(action);
        }
        aggregate._dirtySections.Clear();
        return aggregate;
    }

    /// <summary>
    /// 将已验证的标准（canonical）快照全量刷入当前可变聚合实例。
    /// 适用于单 Fiber Actor 内事务失败回滚或收到外部权威状态重同步。
    /// 玩家/账号创建信息（PlayerId、AccountId、CreatedAt）严格不可变，其余可变分段与行动队列全部覆盖。
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
        Progression.SimulationCarrySeconds = restored.Progression.SimulationCarrySeconds;
        Progression.NormalKills = restored.Progression.NormalKills;
        Progression.EliteKills = restored.Progression.EliteKills;
        Progression.BossKills = restored.Progression.BossKills;
        Progression.TotalDefeats = restored.Progression.TotalDefeats;
        Progression.CultivationSeconds = restored.Progression.CultivationSeconds;
        Progression.AgeSeconds = restored.Progression.AgeSeconds;
        World.NodeId = restored.World.NodeId;
        World.EdgeId = restored.World.EdgeId;
        World.EdgeProgress = restored.World.EdgeProgress;
        World.X = restored.World.X;
        World.Y = restored.World.Y;
        World.ExplorationSeed = restored.World.ExplorationSeed;
        Combat.Hitpoints = restored.Combat.Hitpoints;
        Combat.MaxHitpoints = restored.Combat.MaxHitpoints;
        Combat.Manapoints = restored.Combat.Manapoints;
        Combat.MaxManapoints = restored.Combat.MaxManapoints;
        Combat.AttackPower = restored.Combat.AttackPower;
        Combat.Defense = restored.Combat.Defense;
        Combat.RegenCarrySeconds = restored.Combat.RegenCarrySeconds;
        Combat.Skills = restored.Combat.Skills;
        Buffs.Active.Clear();
        Buffs.Active.AddRange(restored.Buffs.Active);
        Loadout.EquippedInstances.Clear();
        Loadout.EquippedInstances.AddRange(restored.Loadout.EquippedInstances);
        Loadout.EquippedItemIds.Clear();
        Loadout.EquippedItemIds.AddRange(restored.Loadout.EquippedItemIds);
        Loadout.LearnedAbilities.Clear();
        Loadout.LearnedAbilities.AddRange(restored.Loadout.LearnedAbilities);
        Loadout.Restore(restored.Loadout.Export());
        RestoreDuel(restored.ExportDuel());
        Settings.Locale = restored.Settings.Locale;
        Settings.NotificationsEnabled = restored.Settings.NotificationsEnabled;
        Settings.LoopEnabled = restored.Settings.LoopEnabled;
        Settings.RenameCooldownUntilUnixSeconds = restored.Settings.RenameCooldownUntilUnixSeconds;
        Settings.AutoUseSlots.Clear();
        Settings.AutoUseSlots.AddRange(restored.Settings.AutoUseSlots);
        _actions.Clear();
        for (var i = 0; i < restored._actions.Count; i++)
            _actions.Add(restored._actions[i].Clone());
        _dirtySections.Clear();
        _sectionVersion = restored._sectionVersion;
    }

    private void RestoreDuel(PlayerDuelStateDocument document)
    {
        Duel.EncounterCharges = document.EncounterCharges;
        Duel.CooldownUntilUnixSeconds = document.CooldownUntilUnixSeconds;
        Duel.ActiveDuelId = document.ActiveDuelId;
        Duel.StagedItems.Clear();
        Duel.StagedItems.AddRange(document.StagedItems);
        Duel.CurrentReport = document.CurrentReport is null ? null : ImportReport(document.CurrentReport);
        Duel.Reports.Clear();
        Duel.Reports.AddRange(document.Reports.Select(ImportReport));
    }

    private static PlayerBattleReport ImportReport(PlayerBattleReportDocument document)
    {
        var report = new PlayerBattleReport
        {
            ReportId = document.ReportId,
            ActionInstanceId = Guid.TryParse(document.ReportId, out var guid) ? guid : Guid.Empty,
            ActionId = document.ActionId,
            ActionName = document.ActionName,
            StartedAtUnixSeconds = document.StartedAtUnixSeconds,
            EndedAtUnixSeconds = document.EndedAtUnixSeconds,
            Qi = document.Qi,
            Experience = document.Experience,
            DuelWins = document.DuelWins,
            DuelLosses = document.DuelLosses,
            RootExp =
            {
                Stamina = document.RootExp.Stamina,
                Intelligence = document.RootExp.Intelligence,
                Attack = document.RootExp.Attack,
                Defense = document.RootExp.Defense,
                Swordsmanship = document.RootExp.Swordsmanship,
                Talisman = document.RootExp.Talisman
            }
        };
        report.Items.AddRange(document.Items);
        return report;
    }

    private static bool IsValidReportDocument(PlayerBattleReportDocument? report)
        => report is null || (!string.IsNullOrWhiteSpace(report.ReportId) && report.ReportId.Length <= 64
            && !string.IsNullOrWhiteSpace(report.ActionId) && report.ActionId.Length <= 128
            && (report.ActionName?.Length ?? 0) <= 128
            && double.IsFinite(report.StartedAtUnixSeconds) && report.StartedAtUnixSeconds >= 0
            && double.IsFinite(report.EndedAtUnixSeconds) && report.EndedAtUnixSeconds >= 0
            && double.IsFinite(report.Qi) && report.Qi >= 0
            && double.IsFinite(report.Experience) && report.Experience >= 0
            && report.RootExp is not null
            && new[] { report.RootExp.Stamina, report.RootExp.Intelligence, report.RootExp.Attack,
                report.RootExp.Defense, report.RootExp.Swordsmanship, report.RootExp.Talisman }
                .All(value => double.IsFinite(value) && value >= 0)
            && report.Items is not null && report.Items.Count <= 512
            && report.Items.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ItemId)
                && item.ItemId.Length <= 128 && double.IsFinite(item.Quantity) && item.Quantity > 0)
            && double.IsFinite(report.DuelWins) && report.DuelWins >= 0
            && double.IsFinite(report.DuelLosses) && report.DuelLosses >= 0);

    private static bool IsValidDuelDocument(PlayerDuelStateDocument document)
        => double.IsFinite(document.EncounterCharges) && document.EncounterCharges >= 0
            && double.IsFinite(document.CooldownUntilUnixSeconds) && document.CooldownUntilUnixSeconds >= 0
            && (document.ActiveDuelId is null || document.ActiveDuelId.Length is > 0 and <= 64)
            && document.StagedItems is not null && document.StagedItems.Count <= 512
            && document.StagedItems.All(item => item is not null && item.IsValid)
            && IsValidReportDocument(document.CurrentReport)
            && document.Reports is not null && document.Reports.Count <= 100
            && document.Reports.All(report => report is not null && IsValidReportDocument(report));

    /// <summary>
    /// 检验动态参数字典的合法性（键非空且不超过64字符，值必须是有效有限浮点数）。
    /// 采用普通循环代替 LINQ Any，消除高频参数校验中的闭包分配。
    /// </summary>
    private static bool HasInvalidParameters(IReadOnlyDictionary<string, double>? parameters)
    {
        if (parameters is null) return false;
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || !double.IsFinite(value))
                return true;
        }
        return false;
    }
}
