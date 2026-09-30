using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>
/// 行动系统对实体分段的直接视图。该对象只保存实体、配表与派生面板缓存，
/// 不保存 PlayerAggregate 或聚合快照，队列、资源及账本变更直接进入组件。
/// </summary>
public sealed class EcsPlayerActionContext(YuoEntity entity, GameDataCatalog catalog) : IPlayerResourceContext
{
    // 一个请求期间这些粗粒度组件不会被替换，只会修改其内部领域状态。
    // 缓存引用避免 8,000 万级自然边界循环反复走组件存储查询。
    private readonly PlayerIdentityComponent _identity = entity.GetComponent<PlayerIdentityComponent>()!;
    private readonly PlayerActionQueueComponent _queue = entity.GetComponent<PlayerActionQueueComponent>()!;
    private readonly PlayerProgressionComponent _progression = entity.GetComponent<PlayerProgressionComponent>()!;
    private readonly PlayerWorldComponent _world = entity.GetComponent<PlayerWorldComponent>()!;
    private readonly PlayerResourcesComponent _resources = entity.GetComponent<PlayerResourcesComponent>()!;
    private readonly PlayerCombatComponent _combat = entity.GetComponent<PlayerCombatComponent>()!;
    private readonly PlayerBuffComponent _buffs = entity.GetComponent<PlayerBuffComponent>()!;
    private readonly PlayerLoadoutComponent _loadout = entity.GetComponent<PlayerLoadoutComponent>()!;
    private readonly PlayerSettingsComponent _settings = entity.GetComponent<PlayerSettingsComponent>()!;
    private readonly PlayerDuelComponent _duel = entity.GetComponent<PlayerDuelComponent>()!;
    private readonly PlayerStatsComponent _stats = entity.GetComponent<PlayerStatsComponent>()!;

    private PlayerIdentityComponent Identity => _identity;
    private PlayerActionQueueComponent Queue => _queue;

    public PlayerId PlayerId => new(Identity.PlayerId);
    public PlayerProgression Progression => _progression.Progression;
    public PlayerWorldState World => _world.World;
    public PlayerResourceState Resources => _resources.Resources;
    public PlayerCombatState Combat => _combat.Combat;
    public PlayerBuffState Buffs => _buffs.Buffs;
    public PlayerSettingsState Settings => _settings.Settings;
    public PlayerLoadoutState Loadout => _loadout.Loadout;
    public PlayerDuelState Duel => _duel.Duel;
    public PresenceState Presence => Identity.Presence;
    public DateTimeOffset LastSimulatedAt { get => Queue.LastAdvancedAt; set => Queue.LastAdvancedAt = value; }
    public long StateVersion { get => Identity.StateVersion; set => Identity.StateVersion = value; }
    public IReadOnlyList<IPlayerActionState> Actions => Queue.Actions;

    /// <summary>当前请求实际触及的分段；宿主在完成增量/刷盘交接后可显式清空。</summary>
    public IReadOnlySet<PlayerSectionKind> DirtySections => Identity.DirtySections;

    public void MarkDirty(PlayerSectionKind section) => Identity.MarkDirty(section);

    public void ClearDirtySections() => Identity.DirtySections.Clear();

    /// <summary>直接以组件引用计算属性；输入未改时复用 PlayerStatsComponent 上的派生结果。</summary>
    public PlayerAttributes ResolveAttributes(GameDataCatalog gameData)
    {
        var progression = Progression;
        var world = World;
        var buffs = Buffs;
        var loadout = Loadout;
        var revision = PlayerAttributeResolver.InputRevision(progression, world, buffs, loadout);
        if (gameData.BaseAttributes is not null && _stats.Attributes is { } cached
            && ReferenceEquals(_stats.Catalog, gameData) && _stats.InputRevision == revision
            && ReferenceEquals(_stats.ProgressionInput, progression) && ReferenceEquals(_stats.WorldInput, world)
            && ReferenceEquals(_stats.BuffsInput, buffs) && ReferenceEquals(_stats.LoadoutInput, loadout))
            return cached;
        var attributes = PlayerAttributeResolver.Resolve(progression, world, Resources, Combat, buffs, loadout, gameData);
        if (gameData.BaseAttributes is not null)
        {
            _stats.Attributes = attributes;
            _stats.Catalog = gameData;
            _stats.InputRevision = revision;
            _stats.ProgressionInput = progression;
            _stats.WorldInput = world;
            _stats.BuffsInput = buffs;
            _stats.LoadoutInput = loadout;
        }
        return attributes;
    }

    public void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => ActionReportBook.EnsureActionReport(this, instanceId, actionId, actionName, now);

    public void RecordStats(Guid? actionInstanceId, double atUnixSeconds, Action<PlayerBattleReport> apply)
        => ActionReportBook.RecordActionStats(this, actionInstanceId, atUnixSeconds, apply);

    /// <summary>出队与聚合保持同一归档、死亡恢复、后继激活及版本时机。</summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1)
    {
        var actions = Queue.Actions;
        var index = actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0) return false;
        if (actions[index].Expedition is not null && Resources.Hitpoints <= 0)
        {
            Resources.Hitpoints = 1;
            MarkDirty(PlayerSectionKind.Combat);
        }
        ActionReportBook.ArchiveActionReport(this, actionInstanceId,
            endedAtUnixSeconds >= 0 ? endedAtUnixSeconds : LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d,
            catalog.Duel.MaxBattleReports);
        actions.RemoveAt(index);
        if (index == 0 && actions.Count > 0 && actions[0].Status == ActionQueueStatus.Pending)
            actions[0].Status = ActionQueueStatus.Running;
        if (incrementVersion) TouchActionQueue();
        return true;
    }

    public bool RotateActionToTail(Guid actionInstanceId)
    {
        var actions = Queue.Actions;
        var index = actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0 || actions.Count <= 1) return false;
        var action = actions[index];
        actions.RemoveAt(index);
        if (action.Status == ActionQueueStatus.Running) action.Status = ActionQueueStatus.Pending;
        actions.Add(action);
        if (actions[0].Status == ActionQueueStatus.Pending) actions[0].Status = ActionQueueStatus.Running;
        TouchActionQueue();
        return true;
    }

    /// <summary>
    /// 与 <see cref="PlayerAggregate.EnqueuePriority"/> 同一语义：同类行动去重归档、挂起运行中队首、
    /// 新行动插入队首立即 Running；自动移动与 world.travel 命令共用此通道。
    /// </summary>
    public IPlayerActionState EnqueuePriority(ActionIntent intent, DateTimeOffset at, ActionCatalog actionCatalog,
        TravelActionState? travel = null, bool incrementVersion = true)
    {
        ArgumentNullException.ThrowIfNull(actionCatalog);
        if (intent is null || !intent.ActionId.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动意图无效。");
        if (!actionCatalog.Contains(intent.ActionId))
            throw new DomainException(DomainErrorCode.NotFound, $"行动 {intent.ActionId} 不存在。");
        var actions = Queue.Actions;
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            if (actions[i].Intent.ActionId == intent.ActionId)
            {
                ActionReportBook.ArchiveActionReport(this, actions[i].Id,
                    at.ToUnixTimeMilliseconds() / 1000d, catalog.Duel.MaxBattleReports);
                actions.RemoveAt(i);
            }
        }
        if (actions.Count >= PlayerAggregate.MaxActionQueueLength)
            throw new DomainException(DomainErrorCode.Conflict, $"行动队列最多只能包含 {PlayerAggregate.MaxActionQueueLength} 项。");
        if (actions.Count > 0 && actions[0].Status == ActionQueueStatus.Running)
            actions[0].Status = ActionQueueStatus.Pending;
        var action = new EcsActionState(Guid.NewGuid(), intent)
        {
            EnqueuedAt = at,
            Status = ActionQueueStatus.Running,
            Travel = travel
        };
        actions.Insert(0, action);
        if (incrementVersion) TouchActionQueue();
        else MarkDirty(PlayerSectionKind.ActionQueue);
        return action;
    }

    /// <summary>仅在网络或落盘快照边界导出斗法分段，行动边界不调用此方法。</summary>
    public PlayerDuelStateDocument ExportDuelDocument()
        => PlayerDuelStateCodec.Export(Duel, Queue.Actions.Count == 0 ? null : Queue.Actions[0].Id);

    private void TouchActionQueue()
    {
        StateVersion = checked(StateVersion + 1);
        MarkDirty(PlayerSectionKind.ActionQueue);
    }
}
