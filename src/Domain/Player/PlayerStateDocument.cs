using System.Text.Json.Serialization;
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
    double Experience,
    double CompletedActions,
    double SimulationCarrySeconds = 0,
    double NormalKills = 0,
    double EliteKills = 0,
    double BossKills = 0,
    double TotalDefeats = 0,
    double CultivationSeconds = 0,
    double AgeSeconds = 0,
    double Foundation = 0,
    double CombatExperience = 0,
    double LifetimeExperience = 0);

/// <summary>世界地理坐标与探索种子快照 DTO</summary>
public sealed record PlayerWorldStateDocument(
    string NodeId,
    double X,
    double Y,
    double ExplorationSeed,
    string? EdgeId = null,
    double EdgeProgress = 0,
    IReadOnlyList<Domain.World.NodeBuildingState>? NodeBuildings = null,
    Domain.World.BuildingEffectSet? NodeBuildingEffects = null,
    string? NodeBuildingNodeId = null)
{
    /// <summary>当前位置生效的节点建筑效果；投影属于其他节点或缺失时返回空集（与聚合侧同名语义一致）。计算属性不参与持久化。</summary>
    [JsonIgnore]
    public Domain.World.BuildingEffectSet EffectiveNodeBuildingEffects =>
        string.Equals(NodeBuildingNodeId, NodeId, StringComparison.Ordinal)
            ? NodeBuildingEffects ?? Domain.World.BuildingEffectSet.Empty
            : Domain.World.BuildingEffectSet.Empty;
}

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
    /// <summary>技能调度状态快照。</summary>
    public PlayerCombatSkillState Skills { get; init; } = PlayerCombatSkillState.Empty;
}

/// <summary>客户端设置与快捷自动使用配置快照 DTO</summary>
public sealed record PlayerSettingsStateDocument(
    string Locale,
    bool NotificationsEnabled,
    bool LoopEnabled = true,
    bool AutoTravelEnabled = false,
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

/// <summary>玩家聚合的快照导出与恢复分部。</summary>
public sealed partial class PlayerAggregate
{
    private ActiveBuff[]? _cachedBuffs;
    private long _cachedBuffsVersion = -1;
    private InstanceId[]? _cachedEquippedInstances;
    private AbilityId[]? _cachedLearnedAbilities;
    private ItemId[]? _cachedEquippedItemIds;
    private long _cachedEquippedItemIdsVersion = -1;
    private AutoUseSlotState[]? _cachedAutoUseSlots;
    private PlayerSettingsStateDocument? _cachedSettingsDoc;
    private PlayerDuelStateDocument? _cachedDuelDocument;
    private long _cachedDuelRevision = -1;
    private PlayerBattleReport? _cachedDuelCurrentReport;

    /// <summary>
    /// 将当前可变领域聚合导出为不可变的纯数据快照文档。
    /// 经过零闭包与空数组/未脏分段单例复用优化，降低高频快照导出的 GC 压力。
    /// </summary>
    public PlayerAggregateState ExportState()
    {
        var actions = ExportActions();

        return new(
            PlayerId,
            AccountId,
            DisplayName,
            CreatedAt,
            LastSimulatedAt,
            Presence,
            StateVersion,
            GameDataFingerprint,
            new PlayerProgressionState(Progression.RealmLevel, Progression.Experience, Progression.CompletedActions, Progression.SimulationCarrySeconds,
                Progression.NormalKills, Progression.EliteKills, Progression.BossKills, Progression.TotalDefeats, Progression.CultivationSeconds, Progression.AgeSeconds,
                Progression.Foundation, Progression.CombatExperience, Progression.LifetimeExperience),
            new PlayerWorldStateDocument(World.NodeId, World.X, World.Y, World.ExplorationSeed, World.EdgeId, World.EdgeProgress, World.NodeBuildings.ToArray(), World.NodeBuildingEffects, World.NodeBuildingNodeId),
            new PlayerCombatStateDocument(Resources.Hitpoints, Resources.MaxHitpoints, Resources.Manapoints, Resources.MaxManapoints, Combat.AttackPower, Combat.Defense, Resources.RegenCarrySeconds) { Skills = Combat.Skills },
            GetCachedBuffs(),
            GetCachedEquippedInstances(),
            GetCachedLearnedAbilities(),
            GetCachedSettings(),
            actions,
            Loadout.Export(),
            ExportDuel(),
            GetCachedEquippedItemIds());
    }

    /// <summary>按队列顺序导出行动分段快照数组；供 ExportState 与分段检查点共用，保证两侧产出逐位一致。</summary>
    private QueuedActionState[] ExportActions()
    {
        if (_actions.Count == 0) return [];
        var actions = new QueuedActionState[_actions.Count];
        for (var i = 0; i < _actions.Count; i++)
        {
            var action = _actions[i];
            actions[i] = new QueuedActionState(action.Id, action.Intent, action.EnqueuedAt, action.Status, action.ProgressSeconds, action.RemainingRepeats, action.CompletedRepeats, action.Expedition, action.Travel);
        }
        return actions;
    }

    /// <summary>
    /// 斗法分段导出：三级短路——①结构修订号（计数/冷却/进行中斗法/集合增删/账本换引用）
    /// ②队首行动账本对象引用 ③逐账本 ExportDirty 扫描；全部命中时直接复用上轮文档。
    /// 重建时未脏账本按位复用其已登记文档，保证导出字节与全量重建逐位一致。
    /// </summary>
    private PlayerDuelStateDocument ExportDuel()
    {
        var current = Duel.CurrentReport;
        if (_cachedDuelDocument is { } cached
            && _cachedDuelRevision == Duel.ExportRevision
            && ReferenceEquals(_cachedDuelCurrentReport, current)
            && !AnyDuelReportDirty())
            return cached;
        var document = PlayerDuelStateCodec.Export(Duel, _actions.Count > 0 ? _actions[0].Id : null);
        _cachedDuelDocument = document;
        _cachedDuelRevision = Duel.ExportRevision;
        _cachedDuelCurrentReport = current;
        return document;
    }

    /// <summary>扫描全部账本脏标记；数量上界 ~3+Reports+ActionReports，远低于重建开销。</summary>
    private bool AnyDuelReportDirty()
    {
        if (Duel.OfflineReport.ExportDirty || Duel.DailyReport.ExportDirty) return true;
        for (var i = 0; i < Duel.Reports.Count; i++)
            if (Duel.Reports[i].ExportDirty) return true;
        foreach (var pair in Duel.ActionReports)
            if (pair.Value.ExportDirty) return true;
        return false;
    }

    private ActiveBuff[] GetCachedBuffs()
    {
        if (Buffs.Active.Count == 0) return [];
        if (_cachedBuffs is not null && _cachedBuffsVersion == Buffs.Active.Version) return _cachedBuffs;
        _cachedBuffs = Buffs.Active.ToArray();
        _cachedBuffsVersion = Buffs.Active.Version;
        return _cachedBuffs;
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
        if (_cachedEquippedItemIds is not null && _cachedEquippedItemIdsVersion == Loadout.EquippedItemIds.Version)
            return _cachedEquippedItemIds;
        _cachedEquippedItemIds = Loadout.EquippedItemIds.ToArray();
        _cachedEquippedItemIdsVersion = Loadout.EquippedItemIds.Version;
        return _cachedEquippedItemIds;
    }

    private PlayerSettingsStateDocument GetCachedSettings()
    {
        var slots = GetCachedAutoUseSlots();
        if (_cachedSettingsDoc is not null
            && _cachedSettingsDoc.Locale == Settings.Locale
            && _cachedSettingsDoc.NotificationsEnabled == Settings.NotificationsEnabled
            && _cachedSettingsDoc.LoopEnabled == Settings.LoopEnabled
            && _cachedSettingsDoc.AutoTravelEnabled == Settings.AutoTravelEnabled
            && _cachedSettingsDoc.RenameCooldownUntilUnixSeconds == Settings.RenameCooldownUntilUnixSeconds
            && ReferenceEquals(_cachedSettingsDoc.AutoUseSlots, slots))
        {
            return _cachedSettingsDoc;
        }
        return _cachedSettingsDoc = new PlayerSettingsStateDocument(
            Settings.Locale,
            Settings.NotificationsEnabled,
            Settings.LoopEnabled,
            Settings.AutoTravelEnabled,
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
        if (!double.IsFinite(state.Progression.Experience) || state.Progression.Experience < 0 ||
            !double.IsFinite(state.Progression.CompletedActions) || state.Progression.CompletedActions < 0 ||
            !double.IsFinite(state.Progression.SimulationCarrySeconds) || state.Progression.SimulationCarrySeconds < 0 ||
            !double.IsFinite(state.Progression.NormalKills) || state.Progression.NormalKills < 0 ||
            !double.IsFinite(state.Progression.EliteKills) || state.Progression.EliteKills < 0 ||
            !double.IsFinite(state.Progression.BossKills) || state.Progression.BossKills < 0 ||
            !double.IsFinite(state.Progression.TotalDefeats) || state.Progression.TotalDefeats < 0 ||
            !double.IsFinite(state.Progression.CultivationSeconds) || state.Progression.CultivationSeconds < 0 ||
            !double.IsFinite(state.Progression.AgeSeconds) || state.Progression.AgeSeconds < 0 ||
            !double.IsFinite(state.Progression.Foundation) || state.Progression.Foundation < 0 ||
            !double.IsFinite(state.Progression.CombatExperience) || state.Progression.CombatExperience < 0 ||
            !double.IsFinite(state.Progression.LifetimeExperience) || state.Progression.LifetimeExperience < 0 ||
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
        aggregate.Progression.Experience = state.Progression.Experience;
        aggregate.Progression.CompletedActions = state.Progression.CompletedActions;
        aggregate.Progression.SimulationCarrySeconds = state.Progression.SimulationCarrySeconds;
        aggregate.Progression.NormalKills = state.Progression.NormalKills;
        aggregate.Progression.EliteKills = state.Progression.EliteKills;
        aggregate.Progression.BossKills = state.Progression.BossKills;
        aggregate.Progression.TotalDefeats = state.Progression.TotalDefeats;
        aggregate.Progression.CultivationSeconds = state.Progression.CultivationSeconds;
        aggregate.Progression.AgeSeconds = state.Progression.AgeSeconds;
        aggregate.Progression.Foundation = state.Progression.Foundation;
        aggregate.Progression.CombatExperience = state.Progression.CombatExperience;
        aggregate.Progression.LifetimeExperience = state.Progression.LifetimeExperience;
        aggregate.World.NodeId = state.World.NodeId;
        aggregate.World.EdgeId = state.World.EdgeId;
        aggregate.World.EdgeProgress = state.World.EdgeProgress;
        aggregate.World.X = state.World.X;
        aggregate.World.Y = state.World.Y;
        aggregate.World.ExplorationSeed = state.World.ExplorationSeed;
        var buildingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var building in state.World.NodeBuildings ?? [])
        {
            if (building is null || string.IsNullOrWhiteSpace(building.BuildingId) || building.BuildingId.Length > 128
                || building.Level < 0 || building.TargetLevel < 0
                || (building.TargetLevel > 0 && building.TargetLevel <= building.Level)
                || !double.IsFinite(building.StartedAtUnixSeconds)
                || !buildingKeys.Add(building.BuildingId)
                || building.ContributedMaterials.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                    || !double.IsFinite(pair.Value) || pair.Value < 0))
                throw new DomainException(DomainErrorCode.InvalidArgument, "节点建筑记录无效。");
            aggregate.World.NodeBuildings.Add(building);
        }
        aggregate.World.NodeBuildingEffects = state.World.NodeBuildingEffects ?? Domain.World.BuildingEffectSet.Empty;
        aggregate.World.NodeBuildingNodeId = state.World.NodeBuildingNodeId;
        aggregate.Resources.Hitpoints = state.Combat.Hitpoints;
        aggregate.Resources.MaxHitpoints = state.Combat.MaxHitpoints;
        aggregate.Resources.Manapoints = state.Combat.Manapoints;
        aggregate.Resources.MaxManapoints = state.Combat.MaxManapoints;
        aggregate.Combat.AttackPower = state.Combat.AttackPower;
        aggregate.Combat.Defense = state.Combat.Defense;
        if (!double.IsFinite(state.Combat.RegenCarrySeconds) || state.Combat.RegenCarrySeconds < 0 || state.Combat.RegenCarrySeconds >= 5)
            throw new DomainException(DomainErrorCode.InvalidArgument, "回复时钟余量无效。");
        aggregate.Resources.RegenCarrySeconds = state.Combat.RegenCarrySeconds;
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
        aggregate.Settings.AutoTravelEnabled = state.Settings.AutoTravelEnabled;
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
                (intent.AnchorNodeId is not null && (intent.AnchorNodeId.Length == 0 || intent.AnchorNodeId.Length > 128)) ||
                (intent.TargetItemId is not null && (intent.TargetItemId.Length == 0 || intent.TargetItemId.Length > 128)) ||
                (intent.AuxItemId is not null && (intent.AuxItemId.Length == 0 || intent.AuxItemId.Length > 128)) ||
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
                intent.PvpEnabled,
                intent.AnchorNodeId,
                intent.TargetItemId,
                intent.AuxItemId);
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
        // 恢复出的聚合视为持久化基线：待刷盘脏集合与触及流水号一并清零，不携带恢复过程的标脏痕迹。
        aggregate._dirtySections.Clear();
        aggregate._dirtyMarks.Clear();
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
        Progression.Experience = restored.Progression.Experience;
        Progression.CompletedActions = restored.Progression.CompletedActions;
        Progression.SimulationCarrySeconds = restored.Progression.SimulationCarrySeconds;
        Progression.NormalKills = restored.Progression.NormalKills;
        Progression.EliteKills = restored.Progression.EliteKills;
        Progression.BossKills = restored.Progression.BossKills;
        Progression.TotalDefeats = restored.Progression.TotalDefeats;
        Progression.CultivationSeconds = restored.Progression.CultivationSeconds;
        Progression.AgeSeconds = restored.Progression.AgeSeconds;
        Progression.Foundation = restored.Progression.Foundation;
        Progression.CombatExperience = restored.Progression.CombatExperience;
        Progression.LifetimeExperience = restored.Progression.LifetimeExperience;
        World.NodeId = restored.World.NodeId;
        World.EdgeId = restored.World.EdgeId;
        World.EdgeProgress = restored.World.EdgeProgress;
        World.X = restored.World.X;
        World.Y = restored.World.Y;
        World.ExplorationSeed = restored.World.ExplorationSeed;
        Resources.Hitpoints = restored.Resources.Hitpoints;
        Resources.MaxHitpoints = restored.Resources.MaxHitpoints;
        Resources.Manapoints = restored.Resources.Manapoints;
        Resources.MaxManapoints = restored.Resources.MaxManapoints;
        Combat.AttackPower = restored.Combat.AttackPower;
        Combat.Defense = restored.Combat.Defense;
        Resources.RegenCarrySeconds = restored.Resources.RegenCarrySeconds;
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
        // 事务回滚/权威覆盖重置待刷盘脏集合与触及流水号：回滚点之后的标脏视为未发生。
        _dirtySections.Clear();
        _dirtyMarks.Clear();
        _sectionVersion = restored._sectionVersion;
    }

    private void RestoreDuel(PlayerDuelStateDocument document)
        => PlayerDuelStateCodec.Restore(Duel, document);



    private static bool IsValidReportDocument(PlayerBattleReportDocument? report)
        => report is null || (!string.IsNullOrWhiteSpace(report.ReportId) && report.ReportId.Length <= 64
            // ActionId 允许空串：offline/daily 单例账本无行动绑定。
            && report.ActionId is not null && report.ActionId.Length <= 128
            && (report.ActionName?.Length ?? 0) <= 128
            && double.IsFinite(report.StartedAtUnixSeconds) && report.StartedAtUnixSeconds >= 0
            && double.IsFinite(report.EndedAtUnixSeconds) && report.EndedAtUnixSeconds >= 0
            && double.IsFinite(report.Experience) && report.Experience >= 0
            // 底蕴记净变动：兑换消耗为负、满层沉淀为正，只校验有限性；战斗经验恒非负。
            && double.IsFinite(report.Foundation)
            && double.IsFinite(report.CombatExperience) && report.CombatExperience >= 0
            && report.RootExp is not null
            && new[] { report.RootExp.Stamina, report.RootExp.Meridians, report.RootExp.Attack,
                report.RootExp.Bone, report.RootExp.Agility, report.RootExp.Consciousness }
                .All(value => double.IsFinite(value) && value >= 0)
            && report.Items is not null && report.Items.Count <= 512
            && report.Items.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ItemId)
                && item.ItemId.Length <= 128 && double.IsFinite(item.Quantity) && item.Quantity > 0)
            && (report.ItemsLost is null || (report.ItemsLost.Count <= 512
                && report.ItemsLost.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ItemId)
                    && item.ItemId.Length <= 128 && double.IsFinite(item.Quantity) && item.Quantity > 0)))
            && (report.AbilityExp is null || (report.AbilityExp.Count <= 512
                && report.AbilityExp.All(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.AbilityId)
                    && entry.AbilityId.Length <= 128 && double.IsFinite(entry.Experience) && entry.Experience >= 0)))
            && (report.TechniqueExp is null || (report.TechniqueExp.Count <= 512
                && report.TechniqueExp.All(entry => entry is not null
                    && Domain.GameData.TechniqueCatalog.IsKnown(entry.TechniqueId)
                    && double.IsFinite(entry.Experience) && entry.Experience >= 0)))
            && (report.LevelUps is null || (report.LevelUps.Count <= 512
                && report.LevelUps.All(entry => entry is not null
                    && entry.Kind is "realm" or "root" or "ability" or "technique"
                    && (entry.TargetId?.Length ?? 0) <= 128
                    && double.IsFinite(entry.FromLevel) && double.IsFinite(entry.ToLevel)
                    && entry.FromLevel < entry.ToLevel)))
            && new[] { report.Cycles, report.Encounters, report.NormalKills,
                report.EliteKills, report.BossKills, report.Deaths }
                .All(value => double.IsFinite(value) && value >= 0)
            && report.DayIndex >= -1
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
            && document.Reports.All(report => report is not null && IsValidReportDocument(report))
            && (document.ActiveReports is null || (document.ActiveReports.Count <= 64
                && document.ActiveReports.All(report => report is not null && IsValidReportDocument(report))))
            && IsValidReportDocument(document.OfflineReport)
            && IsValidReportDocument(document.DailyReport);

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

    // ---------- 分段级回滚检查点（internal） ----------
    // 供单写者运行时在「受保护写操作」起点按需捕获分段基线：副本均为不可变文档或独立数组，
    // 连同该分段内部修订计数一起写回，保证失败后聚合状态与备份时点逐位一致。
    // 检查点不构成第二套可变状态通道——只能经 Capture/RestoreSectionCheckpoint 成对读写。

    /// <summary>聚合头标量与分段版本游标的回滚副本（不可变）。</summary>
    internal readonly record struct PlayerHeadCheckpoint(
        string DisplayName,
        DateTimeOffset LastSimulatedAt,
        PresenceState Presence,
        long StateVersion,
        string GameDataFingerprint,
        long SectionVersion,
        Domain.Combat.DuelSettings DuelSettingsTuning);

    private sealed record ProgressionCheckpoint(PlayerProgressionState State, long RealmLevelVersion);
    private sealed record WorldCheckpoint(PlayerWorldStateDocument State, long AttributeInputVersion);
    private sealed record CombatCheckpoint(PlayerCombatStateDocument State);
    private sealed record BuffsCheckpoint(ActiveBuff[] Items, long Version);
    private sealed record LoadoutCheckpoint(
        PlayerLoadoutDocument State,
        InstanceId[] EquippedInstances,
        ItemId[] EquippedItemIds,
        AbilityId[] LearnedAbilities,
        long CoreAttributesVersion,
        long AbilitySlotsVersion,
        long EquipmentVersion,
        long AbilityExperiencesVersion,
        long TechniqueExperiencesVersion,
        long EquippedItemIdsVersion);
    private sealed record SettingsCheckpoint(PlayerSettingsStateDocument State);
    private sealed record ActionQueueCheckpoint(QueuedActionState[] Actions);
    private sealed record DuelCheckpoint(
        PlayerDuelStateDocument State,
        long StructureVersion,
        long StagedItemsVersion,
        long ReportsVersion,
        long ActionReportsVersion);

    /// <summary>捕获聚合头标量与分段版本游标的回滚副本；每次受保护写操作起点随基线一并刷新。</summary>
    internal PlayerHeadCheckpoint CaptureHeadCheckpoint() =>
        new(DisplayName, LastSimulatedAt, Presence, StateVersion, GameDataFingerprint, _sectionVersion, DuelSettingsTuning);

    /// <summary>把头标量与分段版本游标写回备份值；不回退脏流水戳号（已消耗戳号不复用，差集判定不会漏报）。</summary>
    internal void RestoreHeadCheckpoint(PlayerHeadCheckpoint head)
    {
        DisplayName = head.DisplayName;
        LastSimulatedAt = head.LastSimulatedAt;
        Presence = head.Presence;
        StateVersion = head.StateVersion;
        GameDataFingerprint = head.GameDataFingerprint;
        _sectionVersion = head.SectionVersion;
        DuelSettingsTuning = head.DuelSettingsTuning;
    }

    /// <summary>
    /// 捕获单个分段的不可变回滚副本：含分段全部持久化字段与该分段内部修订计数。
    /// 副本不共享分段内部可变状态，之后的写入不会反向污染基线。
    /// </summary>
    internal object CaptureSectionCheckpoint(PlayerSectionKind section) => section switch
    {
        PlayerSectionKind.Progression => new ProgressionCheckpoint(
            new PlayerProgressionState(Progression.RealmLevel, Progression.Experience,
                Progression.CompletedActions, Progression.SimulationCarrySeconds, Progression.NormalKills, Progression.EliteKills,
                Progression.BossKills, Progression.TotalDefeats, Progression.CultivationSeconds, Progression.AgeSeconds,
                Progression.Foundation, Progression.CombatExperience, Progression.LifetimeExperience),
            Progression.RealmLevelVersion),
        PlayerSectionKind.World => new WorldCheckpoint(
            new PlayerWorldStateDocument(World.NodeId, World.X, World.Y, World.ExplorationSeed, World.EdgeId, World.EdgeProgress,
                World.NodeBuildings.ToArray(), World.NodeBuildingEffects, World.NodeBuildingNodeId),
            World.AttributeInputVersion),
        PlayerSectionKind.Combat => new CombatCheckpoint(
            new PlayerCombatStateDocument(Resources.Hitpoints, Resources.MaxHitpoints, Resources.Manapoints, Resources.MaxManapoints,
                Combat.AttackPower, Combat.Defense, Resources.RegenCarrySeconds) { Skills = Combat.Skills }),
        PlayerSectionKind.Buffs => new BuffsCheckpoint(Buffs.Active.ToArray(), Buffs.Active.Version),
        PlayerSectionKind.Loadout => new LoadoutCheckpoint(
            Loadout.Export(),
            Loadout.EquippedInstances.ToArray(),
            Loadout.EquippedItemIds.ToArray(),
            Loadout.LearnedAbilities.ToArray(),
            Loadout.CoreAttributesVersion,
            Loadout.AbilitySlots.Version,
            Loadout.Equipment.Version,
            Loadout.AbilityExperiences.Version,
            Loadout.TechniqueExperiences.Version,
            Loadout.EquippedItemIds.Version),
        PlayerSectionKind.Settings => new SettingsCheckpoint(
            new PlayerSettingsStateDocument(Settings.Locale, Settings.NotificationsEnabled, Settings.LoopEnabled,
                Settings.AutoTravelEnabled, Settings.AutoUseSlots.ToArray(), Settings.RenameCooldownUntilUnixSeconds)),
        PlayerSectionKind.ActionQueue => new ActionQueueCheckpoint(ExportActions()),
        PlayerSectionKind.Duel => new DuelCheckpoint(
            ExportDuel(), Duel.StructureVersion, Duel.StagedItems.Version, Duel.Reports.Version, Duel.ActionReports.Version),
        _ => throw new DomainException(DomainErrorCode.InvalidArgument, "未知玩家分段。")
    };

    /// <summary>
    /// 用回滚副本就地恢复单个分段：只写回该分段字段与内部修订计数，
    /// 不触碰脏集合、脏流水号与头标量（由调用方按操作边界另行恢复）。
    /// 恢复中校验失败或副本形态不匹配视为内部不变量破坏，直接失败快速抛出。
    /// </summary>
    internal void RestoreSectionCheckpoint(PlayerSectionKind section, object checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        switch (section)
        {
            case PlayerSectionKind.Progression:
            {
                var data = (ProgressionCheckpoint)checkpoint;
                Progression.RestoreSnapshot(data.State, data.RealmLevelVersion);
                break;
            }
            case PlayerSectionKind.World:
            {
                var data = (WorldCheckpoint)checkpoint;
                World.RestoreSnapshot(data.State, data.AttributeInputVersion);
                break;
            }
            case PlayerSectionKind.Combat:
            {
                var data = (CombatCheckpoint)checkpoint;
                Resources.Hitpoints = data.State.Hitpoints;
                Resources.MaxHitpoints = data.State.MaxHitpoints;
                Resources.Manapoints = data.State.Manapoints;
                Resources.MaxManapoints = data.State.MaxManapoints;
                Combat.AttackPower = data.State.AttackPower;
                Combat.Defense = data.State.Defense;
                Resources.RegenCarrySeconds = data.State.RegenCarrySeconds;
                Combat.Skills = data.State.Skills;
                break;
            }
            case PlayerSectionKind.Buffs:
            {
                var data = (BuffsCheckpoint)checkpoint;
                Buffs.Active.Clear();
                Buffs.Active.AddRange(data.Items);
                Buffs.Active.RestoreVersion(data.Version);
                break;
            }
            case PlayerSectionKind.Loadout:
            {
                var data = (LoadoutCheckpoint)checkpoint;
                Loadout.Restore(data.State);
                Loadout.EquippedInstances.Clear();
                Loadout.EquippedInstances.AddRange(data.EquippedInstances);
                Loadout.EquippedItemIds.Clear();
                Loadout.EquippedItemIds.AddRange(data.EquippedItemIds);
                Loadout.LearnedAbilities.Clear();
                Loadout.LearnedAbilities.AddRange(data.LearnedAbilities);
                Loadout.RestoreRevisionCounters(
                    data.CoreAttributesVersion, data.AbilitySlotsVersion, data.EquipmentVersion,
                    data.AbilityExperiencesVersion, data.TechniqueExperiencesVersion);
                Loadout.EquippedItemIds.RestoreVersion(data.EquippedItemIdsVersion);
                break;
            }
            case PlayerSectionKind.Settings:
            {
                var data = (SettingsCheckpoint)checkpoint;
                Settings.Locale = data.State.Locale;
                Settings.NotificationsEnabled = data.State.NotificationsEnabled;
                Settings.LoopEnabled = data.State.LoopEnabled;
                Settings.AutoTravelEnabled = data.State.AutoTravelEnabled;
                Settings.RenameCooldownUntilUnixSeconds = data.State.RenameCooldownUntilUnixSeconds;
                Settings.AutoUseSlots.Clear();
                Settings.AutoUseSlots.AddRange(data.State.AutoUseSlots ?? []);
                break;
            }
            case PlayerSectionKind.ActionQueue:
            {
                var data = (ActionQueueCheckpoint)checkpoint;
                _actions.Clear();
                foreach (var actionState in data.Actions)
                {
                    _actions.Add(new QueuedAction(actionState.Id, actionState.Intent, actionState.EnqueuedAt)
                    {
                        Status = actionState.Status,
                        ProgressSeconds = actionState.ProgressSeconds,
                        RemainingRepeats = actionState.RemainingRepeats,
                        CompletedRepeats = actionState.CompletedRepeats,
                        Expedition = actionState.Expedition,
                        Travel = actionState.Travel
                    });
                }
                break;
            }
            case PlayerSectionKind.Duel:
            {
                var data = (DuelCheckpoint)checkpoint;
                RestoreDuel(data.State);
                Duel.RestoreRevisionCounters(data.StructureVersion, data.StagedItemsVersion, data.ReportsVersion, data.ActionReportsVersion);
                break;
            }
            default:
                throw new DomainException(DomainErrorCode.InvalidArgument, "未知玩家分段。");
        }
    }

    /// <summary>
    /// 把待刷盘脏集合与触及流水号精确恢复为给定快照：用于失败回滚后保证
    /// 「标脏集合 + 触及差集」与操作前逐位一致。流水游标不回退——已消耗的戳号不复用，
    /// 后续标脏仍取严格更大的戳，差集与重捕判定不会因戳号复用而失真。
    /// </summary>
    internal void RestoreDirtyTracking(IReadOnlyCollection<PlayerSectionKind> dirtySections, IReadOnlyDictionary<PlayerSectionKind, long> marks)
    {
        ArgumentNullException.ThrowIfNull(dirtySections);
        ArgumentNullException.ThrowIfNull(marks);
        _dirtySections.Clear();
        foreach (var section in dirtySections) _dirtySections.Add(section);
        _dirtyMarks.Clear();
        foreach (var (section, mark) in marks) _dirtyMarks[section] = mark;
    }
}
