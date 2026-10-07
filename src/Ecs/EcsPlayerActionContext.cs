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
public sealed class EcsPlayerActionContext(YuoEntity entity) : IPlayerResourceContext
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
    private readonly PlayerBuffExpiryInboxComponent _buffExpiryInbox = entity.GetComponent<PlayerBuffExpiryInboxComponent>()!;
    private readonly PlayerLoadoutComponent _loadout = entity.GetComponent<PlayerLoadoutComponent>()!;
    private readonly PlayerSettingsComponent _settings = entity.GetComponent<PlayerSettingsComponent>()!;
    private readonly PlayerDuelComponent _duel = entity.GetComponent<PlayerDuelComponent>()!;
    private readonly PlayerStatsComponent _stats = entity.GetComponent<PlayerStatsComponent>()!;
    private readonly EcsPlayerStatsSink _statsSink = new(entity);
    private readonly IPlayerActionQueueMutationSink _actionQueueSink
        = entity.GetComponent<PlayerActionQueueMutationComponent>()!.Sink;

    private PlayerIdentityComponent Identity => _identity;
    private PlayerActionQueueComponent Queue => _queue;

    /// <summary>玩家标识。</summary>
    public PlayerId PlayerId => new(Identity.PlayerId);
    /// <summary>成长分段：修为/底蕴/战斗经验/境界与道龄（组件本体直接暴露）。</summary>
    public PlayerProgression Progression => _progression.Progression;
    /// <summary>世界漫游分段：驻留节点、旅途进度与探索种子。</summary>
    public PlayerWorldState World => _world.World;
    /// <summary>战斗资源分段（气血/灵力池与回复游标）；战斗内外同一份可变状态。</summary>
    public PlayerResourceState Resources => _resources.Resources;
    /// <summary>基础战斗属性分段：装备/功法/境界加成前的基础值。</summary>
    public PlayerCombatState Combat => _combat.Combat;
    /// <summary>临时增益/减益分段。</summary>
    public PlayerBuffState Buffs => _buffs.Buffs;
    /// <summary>客户端偏好与挂机自动化配置分段。</summary>
    public PlayerSettingsState Settings => _settings.Settings;
    /// <summary>装备配置与已习得功法分段。</summary>
    public PlayerLoadoutState Loadout => _loadout.Loadout;
    /// <summary>斗法分段：撮合状态、回放载荷与暂存区。</summary>
    public PlayerDuelState Duel => _duel.Duel;
    /// <summary>在线状态（Offline/Online/Suspended）。</summary>
    public PresenceState Presence => Identity.Presence;
    /// <summary>最近一次模拟推进到的墙钟时刻；离线补算与在线推进共用同一推进游标。</summary>
    public DateTimeOffset LastSimulatedAt { get => Queue.LastAdvancedAt; set => Queue.LastAdvancedAt = value; }
    /// <summary>聚合状态版本号；每次对外可见变更递增，用于并发冲突检测。</summary>
    public long StateVersion { get => Identity.StateVersion; set => Identity.StateVersion = value; }
    /// <summary>行动队列只读视图；结构变更须经队列派发原语，不在此处直接增删。</summary>
    public IReadOnlyList<IPlayerActionState> Actions => Queue.Actions;

    /// <summary>当前请求实际触及的分段；宿主在完成增量/刷盘交接后可显式清空。</summary>
    public IReadOnlySet<PlayerSectionKind> DirtySections => Identity.DirtySections;

    /// <summary>把指定分段加入待持久化集合。</summary>
    public void MarkDirty(PlayerSectionKind section) => Identity.MarkDirty(section);

    /// <summary>宿主完成增量/刷盘交接后清空待持久化集合。</summary>
    public void ClearDirtySections() => Identity.DirtySections.Clear();

    /// <summary>资源边界同步裁决 Buff 到期；返回后信箱不保留本次时刻与结果。</summary>
    public bool ExpireBuffs(double atUnixSeconds)
    {
        _buffExpiryInbox.AtUnixSeconds = atUnixSeconds;
        _buffExpiryInbox.Changed = false;
        try
        {
            YuoWorld.RunSystem<IExpirePlayerBuffs>(_buffExpiryInbox.Entity);
            return _buffExpiryInbox.Changed;
        }
        finally
        {
            _buffExpiryInbox.AtUnixSeconds = 0;
            _buffExpiryInbox.Changed = false;
        }
    }

    /// <summary>
    /// 经驻留节点实体读取遭遇池；旅途中（无驻留节点实体）或成员关系尚未同步到
    /// World.NodeId 的新驻留节点时返回 null，由调用方回退配表 WorldGraph 查询——
    /// 节点实体成员关系在会话提交时才迁移，会话内以权威 World.NodeId 为准。
    /// </summary>
    public IReadOnlyList<string>? NodeEncounterIds
        => _world.CurrentNodeEntityId != 0
            && entity.World.GetEntity(_world.CurrentNodeEntityId) is { } nodeEntity
            && nodeEntity.GetComponent<MapNodeComponent>()?.NodeId == _world.World.NodeId
            && nodeEntity.GetComponent<EncounterProfileComponent>() is { } profile
            ? profile.EncounterIds
            : null;

    /// <summary>直接以组件引用计算属性；输入未改时复用 PlayerStatsComponent 上的派生结果。</summary>
    public PlayerAttributes ResolveAttributes(GameDataCatalog gameData)
    {
        var progression = Progression;
        var world = World;
        var buffs = Buffs;
        var loadout = Loadout;
        var revision = PlayerAttributeResolver.InputRevision(progression, world, buffs, loadout);
        // 修订号与四个分段输入引用同时命中才复用缓存，防止不同聚合实例的修订号巧合相等。
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

    /// <summary>确保指定行动实例的战报账本存在；缺失时按参数补建并标脏 Duel 分段。</summary>
    public void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => ActionReportBook.EnsureActionReport(this, instanceId, actionId, actionName, now);

    /// <summary>把一批已结算增量经统计信箱同步派发到行动/离线/每日三账本。</summary>
    public void RecordStats(Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta)
        => _statsSink.Record(this, actionInstanceId, atUnixSeconds, delta);

    /// <summary>出队与聚合保持同一归档、死亡恢复、后继激活及版本时机。</summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1)
        => _actionQueueSink.Dispatch(this, Queue.Actions, new(PlayerActionQueueMutationKind.Remove, actionInstanceId,
            IncrementVersion: incrementVersion, EndedAtUnixSeconds: endedAtUnixSeconds)).Changed;

    /// <summary>把指定行动实例轮转到队尾（手动轮换用）；实例不存在时返回 false。</summary>
    public bool RotateActionToTail(Guid actionInstanceId)
        => _actionQueueSink.Dispatch(this, Queue.Actions,
            new(PlayerActionQueueMutationKind.Rotate, actionInstanceId)).Changed;

    /// <summary>
    /// 与 <see cref="PlayerAggregate.EnqueuePriority"/> 同一语义：同类行动去重归档、挂起运行中队首、
    /// 新行动插入队首立即 Running；自动移动与 world.travel 命令共用此通道。
    /// </summary>
    public IPlayerActionState EnqueuePriority(ActionIntent intent, DateTimeOffset at, ActionCatalog actionCatalog,
        TravelActionState? travel = null, bool incrementVersion = true, ActionParticipationBinding? participation = null)
        => _actionQueueSink.Dispatch(this, Queue.Actions, new(PlayerActionQueueMutationKind.EnqueuePriority,
            Intent: intent, At: at, Catalog: actionCatalog, Travel: travel, Participation: participation,
            IncrementVersion: incrementVersion)).Action!;

    /// <summary>仅在网络或落盘快照边界导出斗法分段，行动边界不调用此方法。</summary>
    public PlayerDuelStateDocument ExportDuelDocument()
        => PlayerDuelStateCodec.Export(Duel, Queue.Actions.Count == 0 ? null : Queue.Actions[0].Id);

    /// <summary>收益落账后的轮完成裁决；只有重复次数为 0 才循环或离队（-1 无限循环恒驻留队列）。</summary>
    public PlayerActionRoundDisposition FinishActionRound(Guid actionInstanceId, bool loopable, double endedAtUnixSeconds)
        => _actionQueueSink.Dispatch(this, Queue.Actions, new(PlayerActionQueueMutationKind.FinishRound, actionInstanceId,
            IncrementVersion: false, EndedAtUnixSeconds: endedAtUnixSeconds, Loopable: loopable)).RoundDisposition;
}
