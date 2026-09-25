using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>
/// 行动系统对实体分段的直接视图。该对象只保存实体、配表与派生面板缓存，
/// 不保存 PlayerAggregate 或聚合快照，队列、资源及账本变更直接进入组件。
/// </summary>
public sealed class EcsPlayerActionContext(YuoEntity entity, GameDataCatalog catalog) : IPlayerResourceContext
{
    private PlayerAttributes? _attributes;
    private GameDataCatalog? _attributeCatalog;
    private long _attributeRevision = -1;
    private PlayerProgression? _attributeProgression;
    private PlayerWorldState? _attributeWorld;
    private PlayerBuffState? _attributeBuffs;
    private PlayerLoadoutState? _attributeLoadout;

    private PlayerIdentityComponent Identity => entity.GetComponent<PlayerIdentityComponent>()!;
    private PlayerActionQueueComponent Queue => entity.GetComponent<PlayerActionQueueComponent>()!;

    public PlayerId PlayerId => new(Identity.PlayerId);
    public PlayerProgression Progression => entity.GetComponent<PlayerProgressionComponent>()!.Progression;
    public PlayerWorldState World => entity.GetComponent<PlayerWorldComponent>()!.World;
    public PlayerCombatState Combat => entity.GetComponent<PlayerCombatComponent>()!.Combat;
    public PlayerBuffState Buffs => entity.GetComponent<PlayerBuffComponent>()!.Buffs;
    public PlayerSettingsState Settings => entity.GetComponent<PlayerSettingsComponent>()!.Settings;
    public PlayerLoadoutState Loadout => entity.GetComponent<PlayerLoadoutComponent>()!.Loadout;
    public PlayerDuelState Duel => entity.GetComponent<PlayerDuelComponent>()!.Duel;
    public PresenceState Presence => Identity.Presence;
    public DateTimeOffset LastSimulatedAt { get => Queue.LastAdvancedAt; set => Queue.LastAdvancedAt = value; }
    public long StateVersion { get => Identity.StateVersion; set => Identity.StateVersion = value; }
    public IReadOnlyList<IPlayerActionState> Actions => Queue.Actions;

    /// <summary>当前请求实际触及的分段；宿主在完成增量/刷盘交接后可显式清空。</summary>
    public IReadOnlySet<PlayerSectionKind> DirtySections => Identity.DirtySections;

    public void MarkDirty(PlayerSectionKind section) => Identity.DirtySections.Add(section);

    public void ClearDirtySections() => Identity.DirtySections.Clear();

    /// <summary>直接以组件引用计算属性；输入未改时复用派生结果，不导出完整玩家。</summary>
    public PlayerAttributes ResolveAttributes(GameDataCatalog gameData)
    {
        var progression = Progression;
        var world = World;
        var buffs = Buffs;
        var loadout = Loadout;
        var revision = PlayerAttributeResolver.InputRevision(progression, world, buffs, loadout);
        if (gameData.BaseAttributes is not null && _attributes is { } cached
            && ReferenceEquals(_attributeCatalog, gameData) && _attributeRevision == revision
            && ReferenceEquals(_attributeProgression, progression) && ReferenceEquals(_attributeWorld, world)
            && ReferenceEquals(_attributeBuffs, buffs) && ReferenceEquals(_attributeLoadout, loadout))
            return cached;
        var attributes = PlayerAttributeResolver.Resolve(progression, world, Combat, buffs, loadout, gameData);
        if (gameData.BaseAttributes is not null)
        {
            _attributes = attributes;
            _attributeCatalog = gameData;
            _attributeRevision = revision;
            _attributeProgression = progression;
            _attributeWorld = world;
            _attributeBuffs = buffs;
            _attributeLoadout = loadout;
        }
        return attributes;
    }

    public void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => SimulationEngine.EnsureActionReport(this, instanceId, actionId, actionName, now);

    public void RecordStats(Guid? actionInstanceId, double atUnixSeconds, Action<PlayerBattleReport> apply)
        => SimulationEngine.RecordActionStats(this, actionInstanceId, atUnixSeconds, apply);

    /// <summary>出队与聚合保持同一归档、死亡恢复、后继激活及版本时机。</summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1)
    {
        var actions = Queue.Actions;
        var index = actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0) return false;
        if (actions[index].Expedition is not null && Combat.Hitpoints <= 0)
        {
            Combat.Hitpoints = 1;
            MarkDirty(PlayerSectionKind.Combat);
        }
        SimulationEngine.ArchiveActionReport(this, actionInstanceId,
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

    /// <summary>仅在网络或落盘快照边界导出斗法分段，行动边界不调用此方法。</summary>
    public PlayerDuelStateDocument ExportDuelDocument()
        => PlayerDuelStateCodec.Export(Duel, Queue.Actions.Count == 0 ? null : Queue.Actions[0].Id);

    private void TouchActionQueue()
    {
        StateVersion = checked(StateVersion + 1);
        MarkDirty(PlayerSectionKind.ActionQueue);
    }
}
