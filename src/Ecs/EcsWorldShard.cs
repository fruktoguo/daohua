using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using DaoHua.Ecs.Components;
using YuoCore.Ecs;
using YuoCore.Runtime;

namespace DaoHua.Ecs;

/// <summary>不依赖 HTTP/数据库的 ECS 游戏运行时；服务宿主只负责把命令投递到此单写者。</summary>
internal sealed class EcsWorldShard : IAsyncDisposable, IDisposable
{
    private readonly YuoFiberManager _fiberManager = new();
    private readonly SemaphoreSlim _ownerGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, long> _players = new();
    private readonly ConcurrentDictionary<string, long> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, long> _encounters = new();
    private readonly Dictionary<Guid, List<SimulationEvent>> _simulationEvents = [];
    private readonly Dictionary<Guid, SimulationResult> _simulationResults = [];
    private readonly List<YuoEntity> _activeEntities = new(4);
    private readonly EcsClockComponent _clock;
    private readonly YuoFiber _fiber;
    private readonly bool _previousCloseSystemTry;
    private readonly bool _ownsCloseSystemTry;
    private readonly DuelAdvanceOptions? _duelAdvance;
    private EcsCombatTriggerSink? _triggerSink;
    private bool _disposed;

    public EcsWorldShard(GameDataCatalog? gameData = null, DateTimeOffset? startAt = null, string? fiberName = null, bool setWorldAsDefault = true, bool ownCloseSystemTry = true,
        DuelAdvanceOptions? duelAdvance = null)
    {
        _duelAdvance = duelAdvance;
        // 业务运行时不允许 YuoCore 把 System 异常隔离成成功请求；宿主通过异常
        // 感知本次命令失败并执行数据库回滚。
        _ownsCloseSystemTry = ownCloseSystemTry;
        _previousCloseSystemTry = YuoWorld.CloseSystemTry;
        EcsCoreTraceBridge.Install();
        try
        {
            if (ownCloseSystemTry) YuoWorld.CloseSystemTry = true;
            GameData = gameData ?? GameDataCatalog.BuiltIn;
            StartAt = startAt ?? DateTimeOffset.UnixEpoch;
            _fiber = _fiberManager.CreateFiber(fiberName ?? "daohua-ecs", setWorldAsDefault: setWorldAsDefault);
            _clock = _fiber.World.WorldMain!.GetOrAddComponent<EcsClockComponent>();
            _clock.Catalog = GameData;
            _clock.AttachCombatBattle = (entity, battle, startedAt) =>
            {
                var id = CreateEncounterFromStateCore(entity.GetComponent<PlayerIdentityComponent>()!.PlayerId, battle, startedAt);
                return World.GetEntity(id)!.GetComponent<CombatEncounterComponent>()!;
            };
            _clock.ReleaseCombatBattle = entity => DestroyPlayerEncounters(entity.ID);
            _clock.Current = StartAt;
            _clock.Target = StartAt;
            _clock.RequestTarget = StartAt;
            foreach (var node in GameData.WorldGraph.Nodes)
                CreateMapNodeCore(node);
        }
        catch
        {
            try { _fiberManager.Dispose(); }
            finally
            {
                _ownerGate.Dispose();
                EcsCoreTraceBridge.Uninstall();
                if (_ownsCloseSystemTry) YuoWorld.CloseSystemTry = _previousCloseSystemTry;
            }
            throw;
        }
    }

    public GameDataCatalog GameData { get; }
    public DateTimeOffset StartAt { get; }
    public YuoWorld World => _fiber.World;
    public YuoFiber Fiber => _fiber;
    public int PlayerCount => _players.Count;
    public int MapNodeCount => _nodes.Count;

    /// 把宿主从公共节点注册表读取的不可变投影写入玩家世界分段。
    /// 实际投影变化推进状态版本并标记 World；相同投影保持幂等。
    public bool ApplyNodeBuildingProjection(Guid playerId, EcsNodeBuildingProjection projection, DateTimeOffset now)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            var world = entity.GetOrAddComponent<PlayerWorldComponent>();
            var current = world.World;
            var same = world.NodeBuildingProjectionVersion == projection.Version
                && string.Equals(current.NodeBuildingNodeId, projection.NodeId, StringComparison.Ordinal)
                && current.NodeBuildingEffects == projection.Effects
                && current.NodeBuildings.SequenceEqual(projection.Buildings)
                && current.NodeBuildingUsages.SequenceEqual(projection.Usages);
            if (same) return false;
            current.NodeBuildings = projection.Buildings.ToList();
            current.NodeBuildingEffects = projection.Effects;
            current.NodeBuildingNodeId = projection.NodeId;
            current.NodeBuildingUsages = projection.Usages.ToList();
            world.NodeBuildingProjectionVersion = projection.Version;
            // 节点实体侧镜像：MapNodeEntity.NodeBuildingComponent 承载公共建筑投影，
            // 供节点实体查询/驻留统计使用；权威状态仍在宿主侧 NodeBuildingRegistry。
            if (projection.NodeId is not null
                && _nodes.TryGetValue(projection.NodeId, out var nodeEntityId)
                && World.GetEntity(nodeEntityId)?.GetComponent<NodeBuildingComponent>() is { } nodeBuilding)
            {
                nodeBuilding.Effects = projection.Effects;
                nodeBuilding.Buildings = projection.Buildings;
            }
            entity.GetOrAddComponent<PlayerIdentityComponent>().MarkDirty(PlayerSectionKind.World);
            return true;
        });

    /// <summary>取出并清空玩家模拟事件；顺序与领域原语和战斗边界产生顺序一致。</summary>
    public IReadOnlyList<SimulationEvent> DrainSimulationEvents(Guid playerId)
        => ExecuteOwned(() =>
        {
            if (!_simulationEvents.Remove(playerId, out var events) || events.Count == 0)
                return Array.Empty<SimulationEvent>();
            return events.ToArray();
        });

    private void AppendSimulationEvents(Guid playerId, IReadOnlyList<SimulationEvent> events)
    {
        if (events.Count == 0) return;
        if (!_simulationEvents.TryGetValue(playerId, out var buffer))
            _simulationEvents[playerId] = buffer = [];
        buffer.AddRange(events);
    }

    /// <summary>宿主消费完整推进结果，以领域原始增量原序提交经济事务。</summary>
    public SimulationResult? DrainSimulationResult(Guid playerId)
        => ExecuteOwned(() => _simulationResults.Remove(playerId, out var result) ? result : null);

    public PlayerSectionKind[] GetDirtySections(Guid playerId)
        => ExecuteOwned(() => GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!.DirtySections.ToArray());

    public void MarkDirty(Guid playerId, PlayerSectionKind section)
        => ExecuteOwned(() => GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!.DirtySections.Add(section));

    public void AcknowledgeDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => ExecuteOwned(() => GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!.DirtySections.ExceptWith(sections));

    /// <summary>事务失败时恢复请求前的脏边界，避免旧脏段在恢复组件后丢失。</summary>
    public void RestoreDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => ExecuteOwned(() =>
        {
            var dirty = GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!.DirtySections;
            dirty.Clear();
            dirty.UnionWith(sections);
        });

    /// <summary>PG 提交回执的余额是宿主权威；只更新资产组件，不重建其它玩家组件。</summary>
    public void SyncAssetBalances(Guid playerId, IReadOnlyCollection<AssetBalance> balances)
        => ExecuteOwned(() =>
        {
            var inventory = GetPlayer(playerId).GetComponent<PlayerInventoryComponent>()!.Items;
            inventory.Clear();
            foreach (var balance in balances) inventory[balance.Asset] = balance.Quantity;
        });

    /// <summary>
    /// 宿主外部来源（市场结算入账等）改动了玩家客户端可见状态但不属任何分段：
    /// 仅推进状态版本不伪造分段脏——版本推进让增量推送的资产差量通过客户端版本门禁。
    /// </summary>
    public void MarkExternalStateChanged(Guid playerId)
        => ExecuteOwned(() =>
        {
            var identity = GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!;
            identity.StateVersion = checked(identity.StateVersion + 1);
        });

    /// <summary>在拥有 Fiber 的线程内创建玩家实体；状态按完整领域快照映射到粗粒度组件。</summary>
    public Guid CreatePlayer(PlayerAggregateState state, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExecuteOwned(() =>
        {
            if (_players.ContainsKey(state.PlayerId.Value)) throw new InvalidOperationException($"ECS player already exists: {state.PlayerId}");
            using var creationMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create.player");
            var entity = new YuoEntity(World, IDGenerate.GetID($"player:{state.PlayerId.Value:N}"));
            RuntimeInstrumentation.Count("lifecycle.entity_created.player");
            entity.EntityName = $"player:{state.PlayerId}";
            var aggregate = PlayerAggregate.Restore(state);
            var identity = entity.AddComponent<PlayerIdentityComponent>();
            identity.PlayerId = state.PlayerId.Value;
            identity.AccountId = state.AccountId.Value.ToString("N");
            identity.DisplayName = state.DisplayName;
            identity.StateVersion = state.StateVersion;
            identity.GameDataFingerprint = state.GameDataFingerprint;
            identity.CreatedAt = state.CreatedAt;
            identity.Presence = state.Presence;
            var progression = entity.AddComponent<PlayerProgressionComponent>();
            progression.Progression = aggregate.Progression;
            var world = entity.AddComponent<PlayerWorldComponent>();
            world.World = aggregate.World;
            var resources = entity.AddComponent<PlayerResourcesComponent>();
            resources.Resources = aggregate.Resources;
            entity.AddComponent<PlayerStatsComponent>();
            var combat = entity.AddComponent<PlayerCombatComponent>();
            combat.Combat = aggregate.Combat;
            var buffs = entity.AddComponent<PlayerBuffComponent>();
            buffs.Buffs = aggregate.Buffs;
            entity.AddComponent<PlayerBuffExpiryInboxComponent>();
            var loadout = entity.AddComponent<PlayerLoadoutComponent>();
            loadout.Loadout = aggregate.Loadout;
            var settings = entity.AddComponent<PlayerSettingsComponent>();
            settings.Settings = aggregate.Settings;
            entity.AddComponent<PlayerDuelComponent>().Document = state.Duel;
            entity.AddComponent<PlayerStatsInboxComponent>();
            var queue = entity.AddComponent<PlayerActionQueueComponent>();
            queue.LastAdvancedAt = state.LastSimulatedAt;
            foreach (var action in state.Actions)
            {
                queue.Actions.Add(new QueuedAction(action.Id, action.Intent, action.EnqueuedAt)
                {
                    Status = action.Status,
                    ProgressSeconds = action.ProgressSeconds,
                    RemainingRepeats = action.RemainingRepeats,
                    CompletedRepeats = action.CompletedRepeats,
                    Expedition = action.Expedition,
                    Travel = action.Travel,
                    Participation = action.Participation,
                    EnhancementCurrentLevel = action.EnhancementCurrentLevel
                });
            }
            entity.AddComponent<PlayerActionQueueMutationComponent>().Sink
                = new EcsPlayerActionQueueMutationSink(entity, GameData.Duel.MaxBattleReports);
            var inventory = entity.AddComponent<PlayerInventoryComponent>();
            if (balances is not null)
                foreach (var balance in balances)
                    inventory.Items[balance.Asset] = balance.Quantity;
            // 生命周期规范化（§14.1）：持久化 Online 视为崩溃残留，按断线时刻=LastSimulatedAt
            // 转离线（与 SetPresence 同效果：重置离线账本、标脏 Duel/Settings）；
            // 旧档缺失离线锚点时以 LastSimulatedAt 补锚。两种情形都推进版本并标脏待回写。
            identity.OfflineSinceAt = state.OfflineSinceAt;
            if (identity.Presence == PresenceState.Online)
            {
                identity.Presence = PresenceState.Offline;
                identity.OfflineSinceAt = queue.LastAdvancedAt;
                entity.GetComponent<PlayerDuelComponent>()!.Duel.ResetOfflineReport(
                    queue.LastAdvancedAt.ToUnixTimeMilliseconds() / 1000d);
                identity.MarkDirty(PlayerSectionKind.Duel);
                identity.StateVersion = checked(identity.StateVersion + 1);
                identity.MarkDirty(PlayerSectionKind.Settings);
            }
            else if (identity.OfflineSinceAt is null)
            {
                identity.OfflineSinceAt = queue.LastAdvancedAt;
                identity.StateVersion = checked(identity.StateVersion + 1);
                identity.MarkDirty(PlayerSectionKind.Settings);
            }
            _players[state.PlayerId.Value] = entity.ID;
            SyncNodeMembership(entity);
            return state.PlayerId.Value;
        });
    }

    public Guid CreatePlayer(PlayerAggregate aggregate, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return CreatePlayer(aggregate.ExportState(), balances);
    }

    public long CreateMapNode(WorldNode node) => ExecuteOwned(() => CreateMapNodeCore(node));

    /// <summary>按节点 ID 解析长期存在的地图节点实体；配表未覆盖的节点返回 null。</summary>
    public long? GetMapNodeEntityId(string nodeId)
        => ExecuteOwned(() => _nodes.TryGetValue(nodeId, out var id) ? id : (long?)null);

    public long CreateMonster(string monsterDefinitionId, long? parentNodeEntityId = null)
        => ExecuteOwned(() => CreateMonsterCore(monsterDefinitionId, parentNodeEntityId));

    /// <summary>创建真实战斗实体：玩家 Combatant、怪物 Combatant 和长期 Encounter 实体均归属同一 World。</summary>
    public long CreateEncounter(Guid playerId, string encounterId, long seed)
        => ExecuteOwned(() => CreateEncounterCore(playerId, encounterId, seed));

    /// <summary>把动作实例 ID 作为回放输入保留；未提供时仅在线命令路径生成新 ID。</summary>
    public void EnqueueAction(Guid playerId, ActionId actionId, double repeatCount = 1, Guid? actionInstanceId = null)
        => ExecuteOwned(() =>
        {
            if (actionInstanceId == Guid.Empty)
                throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识不能为空。");
            var entity = GetPlayer(playerId);
            var queue = entity.GetComponent<PlayerActionQueueComponent>()!;
            entity.GetComponent<PlayerActionQueueMutationComponent>()!.Sink.Dispatch(
                new EcsPlayerActionContext(entity), queue.Actions,
                new(PlayerActionQueueMutationKind.Enqueue, actionInstanceId ?? Guid.Empty,
                    new ActionIntent(actionId, repeatCount), _clock.Current, GameData.Actions));
        });

    public ValueTask RunOnceAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
        => new(RunOnceCoreAsync(target, null, cancellationToken));

    /// <summary>只推进一个玩家的专属 ECS 行动时钟，避免基准把一次全世界 Tick 重复计入每名玩家。</summary>
    public ValueTask RunPlayerOnceAsync(Guid playerId, DateTimeOffset target, CancellationToken cancellationToken = default)
        => new(RunOnceCoreAsync(target, playerId, cancellationToken));

    public EcsPlayerSnapshot ExportSnapshot(Guid playerId)
        => ExecuteOwned(() => ExportSnapshotCore(playerId));

    public EcsPlayerSnapshot[] ExportSnapshots()
        => ExecuteOwned(() => _players.Keys.OrderBy(id => id).Select(ExportSnapshotCore).ToArray());

    /// <summary>查询指定玩家的下一个可见行动边界；只读，不创建临时运行时对象。</summary>
    public DateTimeOffset? NextAdvanceBoundary(Guid playerId)
        => ExecuteOwned(() => NextAdvanceBoundaryCore(playerId));

    /// <summary>所有已加载玩家的最早边界，供宿主后台时钟动态休眠。</summary>
    public DateTimeOffset? NextAdvanceBoundaryUtc
        => ExecuteOwned(() => _players.Keys
            .Select(id => NextAdvanceBoundaryCore(id))
            .Where(value => value is not null)
            .Min());

    private DateTimeOffset? NextAdvanceBoundaryCore(Guid playerId)
        => SimulationSchedule.NextVisibleBoundaryUtc(new EcsPlayerActionContext(GetPlayer(playerId)), GameData);

    /// <summary>卸载玩家及其运行中战斗实体；必须在 Fiber 所有权门内完成。</summary>
    public bool RemovePlayer(Guid playerId)
        => ExecuteOwned(() =>
        {
            if (!_players.TryRemove(playerId, out var entityId)) return false;
            _simulationEvents.Remove(playerId);
            _simulationResults.Remove(playerId);
            using var removalMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_destroy.player");
            RuntimeInstrumentation.Count("lifecycle.entity_destroyed.player");
            DestroyPlayerEncounters(entityId);
            if (World.GetEntity(entityId) is { } playerEntity)
            {
                if (playerEntity.GetComponent<PlayerWorldComponent>() is { } leavingWorld
                    && leavingWorld.CurrentNodeEntityId != 0
                    && World.GetEntity(leavingWorld.CurrentNodeEntityId)?.GetComponent<NodePopulationComponent>() is { } population)
                    population.PresentPlayerEntityIds.Remove(entityId);
                YuoWorld.DestroyEntityForce(playerEntity);
            }
            return true;
        });

    /// <summary>在 Fiber 所有权内更新玩家在线状态并维护离线锚点、记录可见版本变更。</summary>
    public bool SetPresence(Guid playerId, PresenceState presence, DateTimeOffset at)
        => ExecuteOwned(() =>
        {
            var player = GetPlayer(playerId);
            var identity = player.GetOrAddComponent<PlayerIdentityComponent>();
            if (!Enum.IsDefined(presence)) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
            if (identity.Presence == presence) return false;
            var wasOnline = identity.Presence == PresenceState.Online;
            identity.Presence = presence;
            // 离线锚点：转在线清空；由在线转非在线以 at 记锚；非在线之间切换保留原锚点。
            if (presence == PresenceState.Online) identity.OfflineSinceAt = null;
            else if (wasOnline) identity.OfflineSinceAt = at;
            if (presence == PresenceState.Offline)
            {
                player.GetComponent<PlayerDuelComponent>()!.Duel.ResetOfflineReport(
                    player.GetComponent<PlayerActionQueueComponent>()!.LastAdvancedAt.ToUnixTimeMilliseconds() / 1000d);
                identity.MarkDirty(PlayerSectionKind.Duel);
            }
            identity.StateVersion = checked(identity.StateVersion + 1);
            identity.MarkDirty(PlayerSectionKind.Settings);
            return true;
        });

    /// <summary>资产事务失败或宿主回滚时，在原实体上恢复完整快照与钱包。</summary>
    public void RestorePlayer(Guid playerId, PlayerAggregateState state, IReadOnlyCollection<AssetBalance> balances)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            if (state.PlayerId.Value != playerId) throw new DomainException(DomainErrorCode.InvalidArgument, "恢复玩家标识不一致。");
            DestroyPlayerEncounters(entity.ID);
            ApplyAggregateState(entity, state, balances);
            _simulationResults.Remove(playerId);
            _simulationEvents.Remove(playerId);
            SyncNodeMembership(entity);
        });

    /// <summary>显式取消行动；队首战斗实体与战报一并按领域移除语义清理。</summary>
    public bool RemoveAction(Guid playerId, Guid actionInstanceId)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            var queue = entity.GetComponent<PlayerActionQueueComponent>();
            if (queue is null) return false;
            var index = queue.Actions.FindIndex(action => action.Id == actionInstanceId);
            if (index < 0) return false;
            var identity = entity.GetComponent<PlayerIdentityComponent>()!;
            var aggregate = WrapViewAggregate(entity, identity, queue);
            var removed = aggregate.RemoveAction(actionInstanceId);
            if (!removed) return false;
            identity.StateVersion = aggregate.StateVersion;
            queue.LastAdvancedAt = aggregate.LastSimulatedAt;
            SyncNodeMembership(entity);
            foreach (var section in aggregate.DirtySections) identity.MarkDirty(section);
            return true;
        });

    /// <summary>
    /// 命令级玩家变更通道：在 Fiber 所有权内以「视图聚合」包装组件分段执行聚合命令方法，
    /// 分段与行动队列就地写组件本体，不再经快照导出/重建/回灌往返。
    /// 返回本次变更触及的脏标记差集（供命令回执窄化编码）；
    /// destroyEncounters 仅用于队首战斗行动被移除时的等价实体清理。
    /// </summary>
    public IReadOnlySet<PlayerSectionKind> MutatePlayer(Guid playerId, Action<PlayerAggregate> mutate,
        bool destroyEncounters = false)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            var identity = entity.GetComponent<PlayerIdentityComponent>()!;
            var queue = entity.GetComponent<PlayerActionQueueComponent>()!;
            var aggregate = WrapViewAggregate(entity, identity, queue);
            mutate(aggregate);
            // 聚合标量头字段回写组件：改名/在线态/离线锚点/指纹/版本号/推进游标由命令方法就地修改。
            identity.DisplayName = aggregate.DisplayName;
            identity.Presence = aggregate.Presence;
            identity.OfflineSinceAt = aggregate.OfflineSinceAt;
            identity.StateVersion = aggregate.StateVersion;
            identity.GameDataFingerprint = aggregate.GameDataFingerprint;
            queue.LastAdvancedAt = aggregate.LastSimulatedAt;
            if (destroyEncounters) DestroyPlayerEncounters(entity.ID);
            SyncNodeMembership(entity);
            foreach (var section in aggregate.DirtySections) identity.MarkDirty(section);
            return new HashSet<PlayerSectionKind>(aggregate.DirtySectionMarks.Keys);
        });

    /// <summary>
    /// 视图聚合装配：分段 POCO 与行动队列直接引用组件持有实例；
    /// 聚合仅承担命令门面（校验、不变量、脏标记、修订号自增），属性输入修订计数
    /// 随分段本体单调存活，无需文档往返的基线续写。
    /// </summary>
    private static PlayerAggregate WrapViewAggregate(
        YuoEntity entity, PlayerIdentityComponent identity, PlayerActionQueueComponent queue)
        => PlayerAggregate.Wrap(
            new PlayerId(identity.PlayerId),
            new AccountId(Guid.Parse(identity.AccountId)),
            identity.DisplayName,
            identity.CreatedAt,
            identity.GameDataFingerprint,
            identity.StateVersion,
            identity.Presence,
            identity.OfflineSinceAt,
            queue.LastAdvancedAt,
            entity.GetComponent<PlayerProgressionComponent>()!.Progression,
            entity.GetComponent<PlayerWorldComponent>()!.World,
            entity.GetComponent<PlayerResourcesComponent>()!.Resources,
            entity.GetComponent<PlayerCombatComponent>()!.Combat,
            entity.GetComponent<PlayerBuffComponent>()!.Buffs,
            entity.GetComponent<PlayerLoadoutComponent>()!.Loadout,
            entity.GetComponent<PlayerSettingsComponent>()!.Settings,
            entity.GetComponent<PlayerDuelComponent>()!.Duel,
            queue.Actions,
            new EcsPlayerStatsSink(entity),
            entity.GetComponent<PlayerActionQueueMutationComponent>()!.Sink);

    /// <summary>分段脏流水号快照；差集判定语义与 PlayerAggregateState.DirtySectionMarks 一致。</summary>
    public IReadOnlyDictionary<PlayerSectionKind, long> GetDirtyMarks(Guid playerId)
        => ExecuteOwned(() => new Dictionary<PlayerSectionKind, long>(
            GetPlayer(playerId).GetComponent<PlayerIdentityComponent>()!.DirtyMarks));

    /// <summary>属性输入修订号（与 OOP 锁内 WireAttributesChanged 判定的输入等价）：面板解析输入四元组的版本和。</summary>
    public long AttributeRevision(Guid playerId)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            return PlayerAttributeResolver.InputRevision(
                entity.GetComponent<PlayerProgressionComponent>()!.Progression,
                entity.GetComponent<PlayerWorldComponent>()!.World,
                entity.GetComponent<PlayerBuffComponent>()!.Buffs,
                entity.GetComponent<PlayerLoadoutComponent>()!.Loadout);
        });

    private void DestroyPlayerEncounters(long playerEntityId)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_destroy.encounter");
        foreach (var encounterId in _encounters
                     .Where(pair => pair.Value == playerEntityId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _encounters.TryRemove(encounterId, out _);
            if (World.GetEntity(encounterId) is not { } encounterEntity) continue;
            var encounterComponent = encounterEntity.GetComponent<CombatEncounterComponent>();
            var enemyIds = encounterComponent?.EnemyEntityIds ?? [];
            foreach (var enemyId in enemyIds)
                if (World.GetEntity(enemyId) is { } enemyEntity)
                {
                    YuoWorld.DestroyEntityForce(enemyEntity);
                    RuntimeInstrumentation.Count("lifecycle.entity_destroyed.monster");
                }
            // 组队盟友实体随遭遇一并销毁（纯战斗容器，非真实玩家实体）。
            var allyIds = encounterComponent?.AllyEntityIds ?? [];
            foreach (var allyId in allyIds)
                if (World.GetEntity(allyId) is { } allyEntity)
                {
                    YuoWorld.DestroyEntityForce(allyEntity);
                    RuntimeInstrumentation.Count("lifecycle.entity_destroyed.ally");
                }
            YuoWorld.DestroyEntityForce(encounterEntity);
            RuntimeInstrumentation.Count("lifecycle.entity_destroyed.encounter");
        }
        if (World.GetEntity(playerEntityId) is { } player)
        {
            if (player.GetComponent<PlayerCombatComponent>() is { } combat)
            {
                combat.Encounter = null;
                combat.EncounterEntityId = 0;
            }
            // 战内能力面只随遭遇存活；新遭遇重新装配，不能继续暴露已结束单位的状态视图。
            player.RemoveComponent<OnHitLeechComponent>();
            player.RemoveComponent<ManaLeechComponent>();
            player.RemoveComponent<ThornsComponent>();
            player.RemoveComponent<ThreatOnTakenComponent>();
            player.RemoveComponent<CombatTriggerInboxComponent>();
            player.RemoveComponent<SkillStateComponent>();
            player.RemoveComponent<BuffComponent>();
            player.RemoveComponent<CombatantComponent>();
        }
    }

    private async Task RunOnceCoreAsync(DateTimeOffset target, Guid? playerId, CancellationToken cancellationToken)
    {
        using (RuntimeInstrumentation.Measure("queue.owner_wait"))
            await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ownerHoldMeasurement = RuntimeInstrumentation.Measure("ecs.owner_hold");
        var clockBefore = _clock.Current;
        var backups = new Dictionary<Guid, (PlayerAggregateState State, AssetBalance[] Assets, PlayerSectionKind[] Dirty)>();
        var executions = new Dictionary<Guid, EcsActionExecutionComponent>();
        try
        {
            var scenario = "batch";
            if (RuntimeInstrumentation.Enabled && playerId is { } measuredPlayer)
            {
                var actions = GetPlayer(measuredPlayer).GetComponent<PlayerActionQueueComponent>()!.Actions;
                scenario = actions.Count > 0 && GameData.Actions.TryGet(actions[0].Intent.ActionId, out var definition)
                    ? RuntimeInstrumentation.Scenario(definition.Kind) : "idle";
            }
            using var simulationMeasurement = RuntimeInstrumentation.Measure("core.simulation", scenario);

            foreach (var id in playerId is { } one ? new[] { one } : _players.Keys.ToArray())
            {
                var entity = GetPlayer(id);
                var state = ExportSnapshotCore(id).State;
                var assets = entity.GetComponent<PlayerInventoryComponent>()!.Items
                    .Select(pair => new AssetBalance(new PlayerId(id), pair.Key, pair.Value, 0)).ToArray();
                backups[id] = (state, assets, entity.GetComponent<PlayerIdentityComponent>()!.DirtySections.ToArray());
                executions[id] = EcsActionExecutionComponent.Attach(entity, GameData, target, _duelAdvance);
            }
            var cursor = executions.Count > 0 ? executions.Values.Min(EcsActionSession.Current) : target;
            var steps = 0;
            // 离线采集/制造的自然边界固定由资源回复窗口切分（默认 5 秒）。
            // 这些玩家没有战斗实体时，直接执行同一批管线原语可消除数百万次
            // Fiber/World/System 往返；一旦队列切换到战斗或旅行，立即回到完整 ECS 路径。
            var bulkCompleted = false;
            if (playerId is not null && executions.Count == 1 && executions.Values.All(EcsActionSession.CanBulkAdvance))
            {
                using var bulkMeasurement = RuntimeInstrumentation.Measure("ecs.bulk_advance", scenario);
                var execution = executions.Values.Single();
                bulkCompleted = EcsActionSession.AdvanceBulkTimedUntilUnsupported(execution, cancellationToken);
                cursor = EcsActionSession.Current(execution);
                steps = execution.BulkStepCount;
            }
            if (!bulkCompleted)
            {
            while (executions.Values.Any(e => !EcsActionSession.IsComplete(e)))
            {
                if (++steps > SimulationEngine.MaxTransitionsPerAdvance)
                {
                    var diag = string.Join(";", executions.Where(pair => !EcsActionSession.IsComplete(pair.Value))
                        .OrderBy(pair => EcsActionSession.NextBoundaryAt(pair.Value)).Take(8)
                        .Select(pair => $"{pair.Key:N}:cursor={EcsActionSession.Current(pair.Value):o},next={EcsActionSession.NextBoundaryAt(pair.Value):o},[{EcsActionSession.DiagnosticState(pair.Value)}]"));
                    throw new DomainException(DomainErrorCode.InvariantViolation,
                        $"ECS 自然边界推进超过最大转换数。incomplete=[{diag}] " +
                        $"clock=[skip={_clock.SkipWorldUpdate},skipN={_clock.SkipWorldUpdateEntities},active={_clock.ActivePlayerId},prep={_clock.PreparationOnly}]");
                }
                cancellationToken.ThrowIfCancellationRequested();
                while (executions.Values.Any(EcsActionSession.NeedsPreparation))
                    await Tick(cursor, true).ConfigureAwait(false);
                if (executions.Values.All(EcsActionSession.IsComplete)) break;
                // 真实系统分别推进自己的自然边界；UTC 只决定执行顺序，不再回减成战斗预算。
                var next = executions.Values.Where(e => !EcsActionSession.IsComplete(e)).Min(EcsActionSession.NextBoundaryAt);
                if (next < cursor)
                {
                    // 不变量违例必须携带现场证据：外层游标与各会话的窗口/预算/战斗段状态，
                    // 否则离线补算的倒退无法离线归因。
                    var diag = string.Join(";", executions.Select(pair =>
                    {
                        var execution = pair.Value;
                        string boundary;
                        try { boundary = EcsActionSession.NextBoundaryAt(execution).ToString("o"); }
                        catch (Exception error) { boundary = $"error:{error.Message}"; }
                        return $"{pair.Key:N}:cursor={EcsActionSession.Current(execution):o},next={boundary}," +
                            $"window=[{execution.WindowStart:o}..{execution.WindowEnd:o}]," +
                            $"elapsed={execution.WindowElapsedSeconds:0.########},remaining={execution.RemainingSeconds:0.########}," +
                            $"open={execution.WindowOpen},waiting={execution.Waiting},prepared={execution.PreparedAction is not null}," +
                            $"kind={execution.Definition?.ExecutionKind.ToString() ?? "-"},battle={execution.Battle is not null}," +
                            $"battleFrom={execution.BattleFrom:0.###},battleTarget={execution.BattleTarget:0.###}," +
                            $"frame={execution.FrameTarget:o},target={execution.Target:o},compute={execution.ComputeTarget:o}";
                    }));
                    throw new DomainException(DomainErrorCode.InvariantViolation,
                        $"ECS 自然边界时钟发生倒退。outer={cursor:o} [{diag}]");
                }
                await Tick(next, false).ConfigureAwait(false);
                // 战斗段边界是预算承诺（窗口末），战斗提前终局时会话实际只推进到胜利时刻；
                // 外层游标必须跟随会话真实游标，否则下一轮合法边界会被误判为时钟倒退。
                cursor = executions.Values.Min(EcsActionSession.Current);
            }
            }
            using (RuntimeInstrumentation.Measure("ecs.mailbox.post", scenario))
            await _fiber.PostAsync((_, _) =>
            {
                using var callbackMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.mailbox.callback");
                _clock.Current = _clock.Target = target;
                _clock.SkipWorldUpdate = executions.Count > 0;
                _clock.SkipWorldUpdateEntities = executions.Count;
                foreach (var (id, execution) in executions)
                {
                    var result = EcsActionSession.Commit(execution);
                    _simulationResults[id] = result;
                    AppendSimulationEvents(id, result.Events);
                    if (execution.Entity is not null)
                        SyncNodeMembership(execution.Entity);
                }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            using var scenarioScope = RuntimeInstrumentation.BeginScenario(scenario);
            using (RuntimeInstrumentation.Measure("ecs.system_tick", scenario))
            {
                SetActiveEntities(playerId);
                try
                {
                    await _fiber.TickAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ClearActiveEntities();
                }
            }

            async Task Tick(DateTimeOffset at, bool preparation)
            {
                using (RuntimeInstrumentation.Measure("ecs.mailbox.post", scenario))
                await _fiber.PostAsync((_, _) =>
                {
                    using var callbackMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.mailbox.callback");
                    _clock.Target = _clock.Current = at;
                    _clock.RequestTarget = target;
                    _clock.DeltaSeconds = preparation ? 0 : (at - cursor).TotalSeconds;
                    _clock.ActivePlayerId = playerId;
                    _clock.PreparationOnly = preparation;
                    return ValueTask.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
                using var scenarioScope = RuntimeInstrumentation.BeginScenario(scenario);
                using (RuntimeInstrumentation.Measure("ecs.system_tick", scenario))
                {
                    SetActiveEntities(playerId);
                    try
                    {
                        await _fiber.TickAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ClearActiveEntities();
                    }
                }
            }
        }
        catch
        {
            foreach (var execution in executions.Values) execution.Detach();
            foreach (var (id, backup) in backups)
            {
                if (!_players.ContainsKey(id)) continue;
                var entity = GetPlayer(id);
                DestroyPlayerEncounters(entity.ID);
                ApplyAggregateState(entity, backup.State, backup.Assets);
                var dirty = entity.GetComponent<PlayerIdentityComponent>()!.DirtySections;
                dirty.Clear();
                dirty.UnionWith(backup.Dirty);
                _simulationEvents.Remove(id);
                _simulationResults.Remove(id);
            }
            _clock.Current = _clock.Target = clockBefore;
            throw;
        }
        finally
        {
            try
            {
                foreach (var execution in executions.Values) execution.Detach();
                if (!_fiber.IsDisposed)
                    using (RuntimeInstrumentation.Measure("ecs.mailbox.post"))
                    await _fiber.PostAsync((_, _) =>
                    {
                        _clock.ActivePlayerId = null;
                        _clock.PreparationOnly = false;
                        return ValueTask.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ownerHoldMeasurement.Dispose();
                _ownerGate.Release();
            }
        }
    }

    private void SetActiveEntities(Guid? playerId)
    {
        // 系统 tick 前把扫描集收敛为当前玩家与遭遇实体，避免全量遍历。
        if (playerId is not { } id) return;
        var player = GetPlayer(id);
        _activeEntities.Clear();
        _activeEntities.Add(player);
        var encounterId = player.GetComponent<PlayerCombatComponent>()!.EncounterEntityId;
        if (encounterId != 0 && World.GetEntity(encounterId) is { } encounter)
            _activeEntities.Add(encounter);
        World.SetActiveEntities(_activeEntities);
    }

    private void ClearActiveEntities()
    {
        World.ClearActiveEntities();
        _activeEntities.Clear();
    }

    private T ExecuteOwned<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (RuntimeInstrumentation.Measure("queue.owner_wait")) _ownerGate.Wait();
        var ownerHoldMeasurement = RuntimeInstrumentation.Measure("ecs.owner_hold");
        try { return action(); }
        finally
        {
            ownerHoldMeasurement.Dispose();
            _ownerGate.Release();
        }
    }
    private void ExecuteOwned(Action action) => ExecuteOwned(() => { action(); return true; });

    private YuoEntity GetPlayer(Guid id)
        => _players.TryGetValue(id, out var entityId) ? World.GetEntity(entityId) ?? throw new KeyNotFoundException($"ECS player missing: {id}") : throw new KeyNotFoundException($"ECS player missing: {id}");

    private long CreateMapNodeCore(WorldNode node)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create.map_node");
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.TryGetValue(node.Id, out var existing)) return existing;
        var entity = new YuoEntity(World, IDGenerate.GetID(node.Id));
        RuntimeInstrumentation.Count("lifecycle.entity_created.map_node");
        entity.EntityName = $"map:{node.Id}";
        entity.AddComponent<MapNodeComponent>();
        var map = entity.GetComponent<MapNodeComponent>()!;
        map.NodeId = node.Id; map.Name = node.Name; map.X = node.X; map.Y = node.Y; map.Neighbors = GameData.WorldGraph.Edges.Where(e => e.FromNodeId == node.Id || e.ToNodeId == node.Id).Select(e => e.FromNodeId == node.Id ? e.ToNodeId : e.FromNodeId).Distinct(StringComparer.Ordinal).Order().ToArray();
        entity.AddComponent<NodeBuildingComponent>();
        entity.AddComponent<EncounterProfileComponent>().EncounterIds = node.EncounterIds;
        entity.AddComponent<NodePopulationComponent>();
        _nodes[node.Id] = entity.ID;
        return entity.ID;
    }

    /// <summary>
    /// 把玩家驻留节点实体引用与节点人口同步到当前 World.NodeId。
    /// 在创建/恢复、命令 MutatePlayer、行动推进与旅行落地后统一调用；
    /// 处于边中段（EdgeId 非空）时视为离站，不占任何节点人口。
    /// </summary>
    internal void SyncNodeMembership(YuoEntity playerEntity)
    {
        var world = playerEntity.GetComponent<PlayerWorldComponent>();
        if (world is null) return;
        var targetNodeEntityId = 0L;
        if (world.World.EdgeId is null
            && !string.IsNullOrEmpty(world.World.NodeId)
            && _nodes.TryGetValue(world.World.NodeId, out var nodeEntityId))
            targetNodeEntityId = nodeEntityId;
        if (world.CurrentNodeEntityId == targetNodeEntityId) return;
        if (world.CurrentNodeEntityId != 0
            && World.GetEntity(world.CurrentNodeEntityId)?.GetComponent<NodePopulationComponent>() is { } previousPopulation)
            previousPopulation.PresentPlayerEntityIds.Remove(playerEntity.ID);
        if (targetNodeEntityId != 0
            && World.GetEntity(targetNodeEntityId)?.GetComponent<NodePopulationComponent>() is { } nextPopulation)
            nextPopulation.PresentPlayerEntityIds.Add(playerEntity.ID);
        world.CurrentNodeEntityId = targetNodeEntityId;
    }

    private long CreateMonsterCore(string monsterDefinitionId, long? parentNodeEntityId)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create.monster");
        if (!GameData.CombatMonsters.TryGetValue(monsterDefinitionId, out var definition)) throw new DomainException(DomainErrorCode.NotFound, $"怪物 {monsterDefinitionId} 不存在。");
        var entity = new YuoEntity(World);
        RuntimeInstrumentation.Count("lifecycle.entity_created.monster");
        entity.EntityName = $"monster:{monsterDefinitionId}:{entity.ID}";
        entity.AddComponent<MonsterIdentityComponent>();
        var identity = entity.GetComponent<MonsterIdentityComponent>()!;
        identity.MonsterDefinitionId = definition.Id; identity.DisplayName = definition.Name;
        entity.AddComponent<CombatantComponent>();
        var combatant = entity.GetComponent<CombatantComponent>()!;
        // 生态刷怪按节点实体侧建筑投影削弱（封魔阵类效果对同节点全体生效，不依赖单个玩家投影）。
        var stats = parentNodeEntityId is { } nodeParent
            && World.GetEntity(nodeParent)?.GetComponent<NodeBuildingComponent>() is { } nodeBuilding
            ? NodeBuildingRules.WeakenMonsterStats(definition.Stats, nodeBuilding.Effects.MonsterWeakenRatio)
            : definition.Stats;
        combatant.State = new CombatantState($"enemy:{entity.ID}", stats, definition.Skills); combatant.IsPlayer = false;
        entity.AddComponent<SkillStateComponent>();
        entity.AddComponent<BuffComponent>();
        entity.AddComponent<DropProfileComponent>().Drops = definition.DropTable;
        if (parentNodeEntityId is { } parent && World.GetEntity(parent) is { } parentEntity)
        {
            World.SetParent(entity, parentEntity);
            if (parentEntity.GetComponent<NodePopulationComponent>() is { } population)
                population.ActiveMonsterCount++;
        }
        return entity.ID;
    }

    private long CreateEncounterCore(Guid playerId, string encounterId, long seed)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create.encounter");
        var player = GetPlayer(playerId);
        if (!GameData.CombatEncounters.TryGetValue(encounterId, out var definition)) throw new DomainException(DomainErrorCode.NotFound, $"遭遇 {encounterId} 不存在。");
        var playerCombat = player.GetOrAddComponent<PlayerCombatComponent>();
        // 与 OOP 远征入口共用属性解析和技能槽映射；不能用 Combat 分段里的旧面板或空技能覆盖装备结果。
        var playerState = ExportSnapshotCore(playerId).State;
        // 战外 Buff 以 CombatEncounterState.Buffs 注入并由战斗边界计时；属性面板解析不能
        // 再把同一组 AttackSpeed/减伤点数折叠一次，否则会产生双倍增益与随机序列漂移。
        var resolved = PlayerAttributeResolver.Resolve(playerState with { Buffs = [] }, GameData);
        var playerStats = resolved.ToCombatant(playerState.Progression.CombatExperience);
        var loadout = player.GetComponent<PlayerLoadoutComponent>()?.Loadout;
        var playerSkills = loadout is null
            ? []
            : SkillSlot.FromAbilitySlots(loadout.AbilitySlots, GameData.Abilities, loadout.AbilityTriggers)
                .ToArray();
        var enemyDefinitions = EncounterWaveSelector.Select(definition, GameData.CombatMonsters, seed, 1).ToArray();
        if (enemyDefinitions.Length == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇没有可用怪物。");
        // 钉死入战同样吃节点封魔阵类削弱：取玩家当前驻留节点的建筑投影。
        var weaken = player.GetComponent<PlayerWorldComponent>()?.World.EffectiveNodeBuildingEffects.MonsterWeakenRatio ?? 0;
        var enemyTemplates = enemyDefinitions.Select(m =>
            new CombatantTemplate(NodeBuildingRules.WeakenMonsterStats(m.Stats, weaken), m.Skills)).ToArray();
        var state = Systems.CombatEventAdvanceSystem.Start(encounterId, seed, new CombatantTemplate(playerStats, playerSkills), enemyTemplates, definition.PlayerRespawnSeconds);
        var battleStartUnix = player.GetComponent<PlayerActionQueueComponent>()?.LastAdvancedAt.ToUnixTimeSeconds() ?? StartAt.ToUnixTimeSeconds();
        foreach (var active in player.GetComponent<PlayerBuffComponent>()?.Active ?? [])
        {
            // durable 阵盘的 ActiveBuff 过期时刻为 +∞：战内以 double.MaxValue 表达永驻，
            // 既满足 CombatBuffState 有限性不变量，也永不触达过期剔除边界。
            var expiresAt = double.IsFinite(active.ExpiresAtUnixSeconds)
                ? Math.Max(0, active.ExpiresAtUnixSeconds - battleStartUnix)
                : double.MaxValue;
            state.Player.Buffs.Add(new CombatBuffState(active.BuffId, expiresAt, active.Stacks,
                active.HpRegen, active.MpRegen, active.AttackSpeedPoints, active.DamageAbsorptionPoints,
                0, active.HpRegenPostRatio, active.MpRegenPostRatio, 0, 0, active.Name, ResetOnWave: true));
        }
        var encounterEntity = new YuoEntity(World); encounterEntity.EntityName = $"encounter:{encounterId}:{seed}";
        RuntimeInstrumentation.Count("lifecycle.entity_created.encounter");
        var encounter = encounterEntity.AddComponent<CombatEncounterComponent>(); encounter.EncounterId = encounterId; encounter.PlayerEntityId = player.ID; encounter.State = state; encounter.Seed = seed;
        state.TriggerSink = _triggerSink ??= new EcsCombatTriggerSink(World);
        // 搜寻完成边界已写入 World 时钟，但队列提交系统尚未运行；必须以本帧
        // 的 ECS 时钟作为战斗绝对起点，不能读取仍停在上一边界的 LastAdvancedAt。
        encounter.StartedAt = _clock.Current;
        var enemyIds = new List<long>();
        for (var i = 0; i < enemyDefinitions.Length; i++)
        {
            state.Enemies[i].DefinitionId = enemyDefinitions[i].Id;
            var enemy = new YuoEntity(World); enemy.EntityName = $"monster:{enemyDefinitions[i].Id}:{seed}:{i}";
            RuntimeInstrumentation.Count("lifecycle.entity_created.monster");
            enemy.AddComponent<MonsterIdentityComponent>().MonsterDefinitionId = enemyDefinitions[i].Id;
            enemy.GetComponent<MonsterIdentityComponent>()!.DisplayName = enemyDefinitions[i].Name;
            var combatant = enemy.AddComponent<CombatantComponent>(); combatant.State = state.Enemies[i];
            enemy.AddComponent<SkillStateComponent>(); enemy.AddComponent<BuffComponent>(); enemy.AddComponent<DropProfileComponent>().Drops = enemyDefinitions[i].DropTable;
            CombatTriggerSurface.Attach(enemy, state.Enemies[i]);
            enemyIds.Add(enemy.ID);
        }
        encounter.EnemyEntityIds = enemyIds;
        var playerCombatant = player.GetOrAddComponent<CombatantComponent>(); playerCombatant.State = state.Player; playerCombatant.IsPlayer = true;
        player.GetOrAddComponent<SkillStateComponent>();
        player.GetOrAddComponent<BuffComponent>();
        CombatTriggerSurface.Attach(player, state.Player);
        playerCombat.Encounter = state; playerCombat.EncounterSeed = seed;
        playerCombat.EncounterEntityId = encounterEntity.ID;
        encounter.AttributeRevision = PlayerAttributeResolver.InputRevision(player.GetComponent<PlayerProgressionComponent>()!.Progression,
            player.GetComponent<PlayerWorldComponent>()!.World, player.GetComponent<PlayerBuffComponent>()!.Buffs,
            player.GetComponent<PlayerLoadoutComponent>()!.Loadout);
        var queue = player.GetComponent<PlayerActionQueueComponent>();
        if (queue is { Actions.Count: > 0 })
        {
            // CreateEncounter 是显式进入战斗的调试/服务入口，已经完成搜寻，
            // 因此直接记录 Combat 阶段并钉死遭遇（IsPinned），不能被行动准备系统
            // 重新覆盖为 InitialSearch，也不参与节点池迁地重锚判定。
            queue.Actions[0].Expedition = new CombatExpeditionState(
                encounterId,
                CombatExpeditionPhase.Combat,
                0,
                0,
                seed,
                0,
                state.Player.Hitpoints,
                state.Player.Manapoints,
                state.Player.NextAttackAtSeconds,
                state.Player.AttackCount,
                state.Player.Threat,
                state.Outcome,
                1,
                state.Enemies.Select(enemy => new ExpeditionEnemyState(
                    enemy.DefinitionId!,
                    CombatEncounterState.Snapshot(enemy),
                    enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                        ? enemy.Skills[enemy.PendingSkillIndex].Ability.TryGetProperty("id", out var abilityId)
                            && abilityId.ValueKind == JsonValueKind.String
                            ? abilityId.GetString()
                            : null
                        : null,
                    0)).ToArray())
            {
                PlayerState = CombatEncounterState.Snapshot(state.Player),
                IsPinned = true
            };
            // 钉死入战同样挂接行动统计簿：注册本机与本波怪物类型桶。
            if (player.GetComponent<PlayerDuelComponent>()?.Duel.ReportFor(queue.Actions[0].Id) is { } pinReport)
            {
                var statsBook = pinReport.CombatStats;
                state.StatsBook = statsBook;
                statsBook.Register("player",
                    player.GetComponent<PlayerIdentityComponent>()?.DisplayName ?? "");
                foreach (var enemy in state.Enemies)
                    if (enemy.DefinitionId is { } pinDefId)
                        statsBook.Register($"monster:{pinDefId}",
                            GameData.CombatMonsters.TryGetValue(pinDefId, out var pinMonster)
                                ? pinMonster.Name : pinDefId);
            }
        }
        _encounters[encounterEntity.ID] = player.ID;
        return encounterEntity.ID;
    }

    /// <summary>把共享领域入场原语生成的战斗单位状态挂载到真实 ECS 实体。</summary>
    private long CreateEncounterFromStateCore(Guid playerId, CombatEncounterState state, DateTimeOffset startedAt)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create.encounter");
        var player = GetPlayer(playerId);
        var encounterEntity = new YuoEntity(World);
        RuntimeInstrumentation.Count("lifecycle.entity_created.encounter");
        encounterEntity.EntityName = $"encounter:{state.EncounterId}:{state.Seed}";
        var encounter = encounterEntity.AddComponent<CombatEncounterComponent>();
        encounter.EncounterId = state.EncounterId;
        encounter.PlayerEntityId = player.ID;
        encounter.State = state;
        encounter.Seed = state.Seed;
        state.TriggerSink = _triggerSink ??= new EcsCombatTriggerSink(World);
        // 搜寻完成边界已写入 World 时钟，但队列提交系统尚未运行；必须以本帧
        // 的 ECS 时钟作为战斗绝对起点，不能读取仍停在上一边界的 LastAdvancedAt。
        encounter.StartedAt = startedAt;
        var enemyIds = new List<long>(state.Enemies.Count);
        foreach (var enemyState in state.Enemies)
        {
            var enemy = new YuoEntity(World);
            RuntimeInstrumentation.Count("lifecycle.entity_created.monster");
            enemy.EntityName = $"monster:{enemyState.DefinitionId ?? enemyState.Id}:{state.Seed}:{enemyIds.Count}";
            var identity = enemy.AddComponent<MonsterIdentityComponent>();
            identity.MonsterDefinitionId = enemyState.DefinitionId ?? enemyState.Id;
            identity.DisplayName = GameData.CombatMonsters.TryGetValue(identity.MonsterDefinitionId, out var definition)
                ? definition.Name : identity.MonsterDefinitionId;
            var combatant = enemy.AddComponent<CombatantComponent>();
            combatant.State = enemyState;
            enemy.AddComponent<SkillStateComponent>();
            enemy.AddComponent<BuffComponent>();
            enemy.AddComponent<DropProfileComponent>().Drops = definition?.DropTable ?? [];
            CombatTriggerSurface.Attach(enemy, enemyState);
            enemyIds.Add(enemy.ID);
        }
        encounter.EnemyEntityIds = enemyIds;
        // 参与会话的非本机盟友挂接为纯战斗实体：触发器派发需要实体句柄（EntityId=-1 会不变量失败），
        // 盟友实体不属于任何真实玩家，只是本机模拟里的友方单位容器。
        var allyIds = new List<long>(Math.Max(0, state.PlayerAllies.Count - 1));
        for (var i = 0; i < state.PlayerAllies.Count; i++)
        {
            if (i == state.SelfIndex) continue;
            var allyState = state.PlayerAllies[i];
            var ally = new YuoEntity(World);
            RuntimeInstrumentation.Count("lifecycle.entity_created.ally");
            ally.EntityName = $"ally:{i}:{state.Seed}";
            var allyCombatant = ally.AddComponent<CombatantComponent>();
            allyCombatant.State = allyState;
            allyCombatant.IsPlayer = false;
            ally.AddComponent<SkillStateComponent>();
            ally.AddComponent<BuffComponent>();
            CombatTriggerSurface.Attach(ally, allyState);
            allyIds.Add(ally.ID);
        }
        encounter.AllyEntityIds = allyIds;
        var playerCombatant = player.GetOrAddComponent<CombatantComponent>();
        playerCombatant.State = state.Player;
        playerCombatant.IsPlayer = true;
        player.GetOrAddComponent<SkillStateComponent>();
        player.GetOrAddComponent<BuffComponent>();
        CombatTriggerSurface.Attach(player, state.Player);
        var playerCombat = player.GetOrAddComponent<PlayerCombatComponent>();
        playerCombat.Encounter = state;
        playerCombat.EncounterSeed = state.Seed;
        playerCombat.EncounterEntityId = encounterEntity.ID;
        encounter.AttributeRevision = PlayerAttributeResolver.InputRevision(player.GetComponent<PlayerProgressionComponent>()!.Progression,
            player.GetComponent<PlayerWorldComponent>()!.World, player.GetComponent<PlayerBuffComponent>()!.Buffs,
            player.GetComponent<PlayerLoadoutComponent>()!.Loadout);
        _encounters[encounterEntity.ID] = player.ID;
        return encounterEntity.ID;
    }

    private static void ApplyAggregateState(YuoEntity entity, PlayerAggregateState state, AssetWallet wallet)
        => ApplyAggregateState(entity, state, wallet.Balances);

    private static void ApplyAggregateState(YuoEntity entity, PlayerAggregateState state, IReadOnlyCollection<AssetBalance> balances)
        => ApplyAggregateState(entity, PlayerAggregate.Restore(state), balances);

    private static void ApplyAggregateState(YuoEntity entity, PlayerAggregate restored, IReadOnlyCollection<AssetBalance> balances)
    {
        var identity = entity.GetOrAddComponent<PlayerIdentityComponent>();
        identity.StateVersion = restored.StateVersion;
        identity.Presence = restored.Presence;
        identity.OfflineSinceAt = restored.OfflineSinceAt;
        identity.GameDataFingerprint = restored.GameDataFingerprint;
        var progression = entity.GetOrAddComponent<PlayerProgressionComponent>();
        progression.Progression = restored.Progression;
        entity.GetOrAddComponent<PlayerWorldComponent>().World = restored.World;
        entity.GetOrAddComponent<PlayerResourcesComponent>().Resources = restored.Resources;
        entity.GetOrAddComponent<PlayerStatsComponent>();
        var combat = entity.GetOrAddComponent<PlayerCombatComponent>();
        combat.Combat = restored.Combat;
        entity.GetOrAddComponent<PlayerBuffComponent>().Buffs = restored.Buffs;
        var loadout = entity.GetOrAddComponent<PlayerLoadoutComponent>();
        loadout.Loadout = restored.Loadout;
        entity.GetOrAddComponent<PlayerSettingsComponent>().Settings = restored.Settings;
        entity.GetOrAddComponent<PlayerDuelComponent>().Document =
            PlayerDuelStateCodec.Export(restored.Duel, restored.Actions.Count > 0 ? restored.Actions[0].Id : null);
        var queue = entity.GetOrAddComponent<PlayerActionQueueComponent>();
        queue.LastAdvancedAt = restored.LastSimulatedAt;
        queue.Actions.Clear();
        foreach (var action in restored.Actions)
        {
            queue.Actions.Add(new QueuedAction(action.Id, action.Intent, action.EnqueuedAt)
            {
                Status = action.Status,
                ProgressSeconds = action.ProgressSeconds,
                RemainingRepeats = action.RemainingRepeats,
                CompletedRepeats = action.CompletedRepeats,
                Expedition = action.Expedition,
                Travel = action.Travel,
                Participation = action.Participation,
                EnhancementCurrentLevel = action.EnhancementCurrentLevel
            });
        }
        var inventory = entity.GetOrAddComponent<PlayerInventoryComponent>();
        inventory.Items.Clear();
        foreach (var balance in balances)
            inventory.Items[balance.Asset] = balance.Quantity;
    }

    private EcsPlayerSnapshot ExportSnapshotCore(Guid id)
    {
        using var snapshotMeasurement = RuntimeInstrumentation.Measure("snapshot.export");
        var entity = GetPlayer(id);
        var identity = entity.GetComponent<PlayerIdentityComponent>()!;
        var progression = entity.GetComponent<PlayerProgressionComponent>()!;
        var world = entity.GetComponent<PlayerWorldComponent>()!;
        var resources = entity.GetComponent<PlayerResourcesComponent>()!;
        var combat = entity.GetComponent<PlayerCombatComponent>()!;
        var buffs = entity.GetComponent<PlayerBuffComponent>()!;
        var loadout = entity.GetComponent<PlayerLoadoutComponent>()!;
        var settings = entity.GetComponent<PlayerSettingsComponent>()!;
        var duel = entity.GetComponent<PlayerDuelComponent>()!;
        var queue = entity.GetComponent<PlayerActionQueueComponent>()!;
        var actions = queue.Actions.Select(a => new QueuedActionState(a.Id, a.Intent, a.EnqueuedAt, a.Status, a.ProgressSeconds, a.RemainingRepeats, a.CompletedRepeats, a.Expedition, a.Travel, a.Participation, a.EnhancementCurrentLevel)).ToArray();
        var state = new PlayerAggregateState(new PlayerId(identity.PlayerId), new AccountId(Guid.Parse(identity.AccountId)), identity.DisplayName, identity.CreatedAt, queue.LastAdvancedAt, identity.Presence, identity.OfflineSinceAt, identity.StateVersion, identity.GameDataFingerprint,
            new PlayerProgressionState(progression.RealmLevel, progression.Experience, progression.CompletedActions, progression.Progression.SimulationCarrySeconds, progression.Progression.NormalKills, progression.Progression.EliteKills, progression.Progression.BossKills, progression.Progression.TotalDefeats, progression.Progression.CultivationSeconds, progression.AgeSeconds, progression.Progression.Foundation, progression.Progression.CombatExperience, progression.Progression.LifetimeExperience, progression.Progression.TraitIds.Count > 0 ? progression.Progression.TraitIds.ToArray() : null, progression.Progression.DefeatedMonsterIds.Count > 0 ? progression.Progression.DefeatedMonsterIds.ToArray() : null),
            new PlayerWorldStateDocument(world.NodeId, world.X, world.Y, world.ExplorationSeed, world.World.EdgeId, world.World.EdgeProgress, world.World.NodeBuildings, world.World.NodeBuildingEffects, world.World.NodeBuildingNodeId, world.World.NodeBuildingUsages),
            new PlayerCombatStateDocument(resources.Hitpoints, resources.MaxHitpoints, resources.Manapoints, resources.MaxManapoints, combat.AttackPower, combat.Defense, resources.Resources.RegenCarrySeconds) { Skills = combat.Combat.Skills, Buffs = combat.Combat.Buffs },
            buffs.Active.ToArray(), loadout.Loadout.EquippedInstances.ToArray(), loadout.Loadout.LearnedAbilities.ToArray(), new PlayerSettingsStateDocument(settings.Settings.Locale, settings.Settings.NotificationsEnabled, settings.Settings.LoopEnabled, settings.Settings.AutoTravelEnabled, settings.Settings.AutoUseSlots.ToArray(), settings.Settings.RenameCooldownUntilUnixSeconds, settings.Settings.QueuePaused, settings.Settings.TutorialStep, settings.Settings.TutorialRewardsStep, settings.Settings.Avatar, settings.Settings.UnlockedAvatars.ToArray()), actions, loadout.Loadout.Export(), duel.Document,
            loadout.Loadout.EquippedItemIds.ToArray());
        var inventory = entity.GetComponent<PlayerInventoryComponent>()?.Items.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<AssetKey, double>();
        return new EcsPlayerSnapshot(identity.PlayerId, entity.ID, state, inventory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fiberManager.Dispose(); }
        finally
        {
            _ownerGate.Dispose();
            EcsCoreTraceBridge.Uninstall();
            if (_ownsCloseSystemTry) YuoWorld.CloseSystemTry = _previousCloseSystemTry;
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>
/// 玩家实体的完整导出快照：领域聚合状态、资产余额与实体标识。
/// 宿主持久化、增量推送与 ComputeNode 补算都以它为唯一交接载体。
/// </summary>
/// <param name="PlayerId">玩家标识。</param>
/// <param name="EntityId">玩家实体 ID（分片 World 内唯一，仅运行期有效）。</param>
/// <param name="State">完整领域聚合状态（八分段 + 行动队列）。</param>
/// <param name="Assets">资产余额快照（AssetKey → 数量）。</param>
public sealed record EcsPlayerSnapshot(Guid PlayerId, long EntityId, PlayerAggregateState State, IReadOnlyDictionary<AssetKey, double> Assets);

/// <summary>Server.Clean 与 ECS 之间的纯数据节点建筑投影，避免 Ecs 项目反向引用宿主程序集。</summary>
/// <param name="Version">宿主侧投影版本号；相同版本且内容一致时跳过写入。</param>
/// <param name="Buildings">节点公共建筑列表。</param>
/// <param name="Effects">建筑效果集合（属性乘区输入，已按拥挤人数衰减）。</param>
/// <param name="NodeId">投影所属节点 ID；null 表示无驻留节点。</param>
/// <param name="Usages">各建筑拥挤使用快照（使用中人数/承载上限/效率系数），供建筑 tab 展示。</param>
public sealed record EcsNodeBuildingProjection(
    long Version,
    IReadOnlyList<NodeBuildingState> Buildings,
    BuildingEffectSet Effects,
    string? NodeId,
    IReadOnlyList<NodeBuildingUsage> Usages);

internal static class CombatantStatsExtensions
{
    public static CombatantStats WithMana(this CombatantStats stats, double maxMana) => stats with { MaxManapoints = maxMana };
}
