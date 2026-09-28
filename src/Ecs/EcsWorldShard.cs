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
    private bool _disposed;

    public EcsWorldShard(GameDataCatalog? gameData = null, DateTimeOffset? startAt = null, string? fiberName = null, bool setWorldAsDefault = true, bool ownCloseSystemTry = true)
    {
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

    /// <summary>
    /// 把宿主从公共节点注册表读取的不可变投影写入玩家世界分段。
    /// 与 OOP 的公共建筑刷新一致：投影刷新只标记 World 分段，不递增玩家业务版本。
    /// </summary>
    public bool ApplyNodeBuildingProjection(Guid playerId, EcsNodeBuildingProjection projection, DateTimeOffset now)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            var world = entity.GetOrAddComponent<PlayerWorldComponent>();
            var current = world.World;
            var same = world.NodeBuildingProjectionVersion == projection.Version
                && string.Equals(current.NodeBuildingNodeId, projection.NodeId, StringComparison.Ordinal)
                && current.NodeBuildingEffects == projection.Effects
                && current.NodeBuildings.SequenceEqual(projection.Buildings);
            if (same) return false;
            current.NodeBuildings = projection.Buildings.ToList();
            current.NodeBuildingEffects = projection.Effects;
            current.NodeBuildingNodeId = projection.NodeId;
            world.NodeBuildingProjectionVersion = projection.Version;
            var identity = entity.GetOrAddComponent<PlayerIdentityComponent>();
            identity.MarkDirty(PlayerSectionKind.World);
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

    /// <summary>在拥有 Fiber 的线程内创建玩家实体；状态按完整领域快照映射到粗粒度组件。</summary>
    public Guid CreatePlayer(PlayerAggregateState state, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExecuteOwned(() =>
        {
            if (_players.ContainsKey(state.PlayerId.Value)) throw new InvalidOperationException($"ECS player already exists: {state.PlayerId}");
            using var creationMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create", "player");
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
            var combat = entity.AddComponent<PlayerCombatComponent>();
            combat.Combat = aggregate.Combat;
            var buffs = entity.AddComponent<PlayerBuffComponent>();
            buffs.Buffs = aggregate.Buffs;
            var loadout = entity.AddComponent<PlayerLoadoutComponent>();
            loadout.Loadout = aggregate.Loadout;
            var settings = entity.AddComponent<PlayerSettingsComponent>();
            settings.Settings = aggregate.Settings;
            entity.AddComponent<PlayerDuelComponent>().Document = state.Duel;
            var queue = entity.AddComponent<PlayerActionQueueComponent>();
            queue.LastAdvancedAt = state.LastSimulatedAt;
            foreach (var action in state.Actions)
            {
                queue.Actions.Add(new EcsActionState(action.Id, action.Intent)
                {
                    EnqueuedAt = action.EnqueuedAt,
                    Status = action.Status,
                    ProgressSeconds = action.ProgressSeconds,
                    RemainingRepeats = action.RemainingRepeats,
                    CompletedRepeats = action.CompletedRepeats,
                    Expedition = action.Expedition,
                    Travel = action.Travel
                });
            }
            var inventory = entity.AddComponent<PlayerInventoryComponent>();
            if (balances is not null)
                foreach (var balance in balances)
                    inventory.Items[balance.Asset] = balance.Quantity;
            _players[state.PlayerId.Value] = entity.ID;
            return state.PlayerId.Value;
        });
    }

    public Guid CreatePlayer(PlayerAggregate aggregate, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return CreatePlayer(aggregate.ExportState(), balances);
    }

    public long CreateMapNode(WorldNode node) => ExecuteOwned(() => CreateMapNodeCore(node));

    public long CreateMonster(string monsterDefinitionId, long? parentNodeEntityId = null)
        => ExecuteOwned(() => CreateMonsterCore(monsterDefinitionId, parentNodeEntityId));

    /// <summary>创建真实战斗实体：玩家 Combatant、怪物 Combatant 和长期 Encounter 实体均归属同一 World。</summary>
    public long CreateEncounter(Guid playerId, string encounterId, long seed)
        => ExecuteOwned(() => CreateEncounterCore(playerId, encounterId, seed));

    /// <summary>把动作实例 ID 作为回放输入保留；未提供时仅在线命令路径生成新 ID。</summary>
    public void EnqueueAction(Guid playerId, ActionId actionId, double repeatCount = 1, Guid? actionInstanceId = null)
        => ExecuteOwned(() =>
        {
            var player = GetPlayer(playerId);
            var queue = player.GetComponent<PlayerActionQueueComponent>()!;
            if (!GameData.Actions.Contains(actionId)) throw new DomainException(DomainErrorCode.NotFound, $"行动 {actionId} 不存在。");
            if (!double.IsFinite(repeatCount) || (repeatCount != -1 && (repeatCount < 1 || repeatCount != Math.Floor(repeatCount))))
                throw new DomainException(DomainErrorCode.InvalidArgument, "行动次数无效。");
            var instanceId = actionInstanceId ?? Guid.NewGuid();
            if (instanceId == Guid.Empty) throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识不能为空。");
            queue.Actions.Add(new EcsActionState(instanceId, new ActionIntent(actionId, repeatCount))
            {
                EnqueuedAt = _clock.Current,
                Status = queue.Actions.Count == 0 ? ActionQueueStatus.Running : ActionQueueStatus.Pending
            });
            var identity = player.GetOrAddComponent<PlayerIdentityComponent>();
            identity.StateVersion = checked(identity.StateVersion + 1);
            identity.MarkDirty(PlayerSectionKind.ActionQueue);
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
        => SimulationSchedule.NextVisibleBoundaryUtc(new EcsPlayerActionContext(GetPlayer(playerId), GameData), GameData);

    /// <summary>卸载玩家及其运行中战斗实体；必须在 Fiber 所有权门内完成。</summary>
    public bool RemovePlayer(Guid playerId)
        => ExecuteOwned(() =>
        {
            if (!_players.TryRemove(playerId, out var entityId)) return false;
            _simulationEvents.Remove(playerId);
            _simulationResults.Remove(playerId);
            using var removalMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_destroy", "player");
            RuntimeInstrumentation.Count("lifecycle.entity_destroyed.player");
            DestroyPlayerEncounters(entityId);
            if (World.GetEntity(entityId) is { } playerEntity)
                YuoWorld.DestroyEntityForce(playerEntity);
            return true;
        });

    /// <summary>在 Fiber 所有权内更新玩家在线状态并记录可见版本变更。</summary>
    public bool SetPresence(Guid playerId, PresenceState presence)
        => ExecuteOwned(() =>
        {
            var player = GetPlayer(playerId);
            var identity = player.GetOrAddComponent<PlayerIdentityComponent>();
            if (!Enum.IsDefined(presence)) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
            if (identity.Presence == presence) return false;
            identity.Presence = presence;
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
            var prevRevisions = CaptureAttributeRevisions(entity);
            var aggregate = PlayerAggregate.Restore(ExportSnapshotCore(playerId).State);
            var baselineRevisions = CaptureAttributeRevisions(aggregate);
            var removed = aggregate.RemoveAction(actionInstanceId);
            if (!removed) return false;
            ContinueAttributeRevisions(aggregate, prevRevisions, baselineRevisions);
            if (index == 0) DestroyPlayerEncounters(entity.ID);
            var wallet = new AssetWallet(aggregate.PlayerId);
            if (entity.GetComponent<PlayerInventoryComponent>() is { } inventory)
                wallet.Restore(inventory.Items.Select(pair => new AssetBalance(aggregate.PlayerId, pair.Key, pair.Value, 0)));
            ApplyAggregateState(entity, aggregate, wallet.Balances);
            var removedIdentity = entity.GetComponent<PlayerIdentityComponent>()!;
            foreach (var section in aggregate.DirtySections) removedIdentity.MarkDirty(section);
            return true;
        });

    /// <summary>
    /// 命令级玩家变更通道：在 Fiber 所有权内把实体状态还原为聚合、执行变更并整体回写组件，
    /// 与原聚合运行时命令语义逐位对齐。返回本次变更新增的脏标记差集（供命令回执窄化编码）；
    /// destroyEncounters 仅用于队首战斗行动被移除时的等价实体清理。
    /// </summary>
    public IReadOnlySet<PlayerSectionKind> MutatePlayer(Guid playerId, Action<PlayerAggregate> mutate,
        bool destroyEncounters = false)
        => ExecuteOwned(() =>
        {
            var entity = GetPlayer(playerId);
            var identity = entity.GetComponent<PlayerIdentityComponent>()!;
            var prevRevisions = CaptureAttributeRevisions(entity);
            var aggregate = PlayerAggregate.Restore(ExportSnapshotCore(playerId).State);
            var baselineRevisions = CaptureAttributeRevisions(aggregate);
            var marksBefore = new Dictionary<PlayerSectionKind, long>(aggregate.DirtySectionMarks);
            mutate(aggregate);
            // 文档往返会把各分段内部修订计数重置为恢复基线：按 prev+Δ 续写，
            // 属性输入修订号才能像 OOP 聚合一样单调增长，WireAttributesChanged 判定才成立。
            ContinueAttributeRevisions(aggregate, prevRevisions, baselineRevisions);
            if (destroyEncounters) DestroyPlayerEncounters(entity.ID);
            var wallet = new AssetWallet(aggregate.PlayerId);
            if (entity.GetComponent<PlayerInventoryComponent>() is { } inventory)
                wallet.Restore(inventory.Items.Select(pair => new AssetBalance(aggregate.PlayerId, pair.Key, pair.Value, 0)));
            ApplyAggregateState(entity, aggregate, wallet.Balances);
            foreach (var section in aggregate.DirtySections) identity.MarkDirty(section);
            var touched = new HashSet<PlayerSectionKind>();
            foreach (var (section, mark) in aggregate.DirtySectionMarks)
                if (!marksBefore.TryGetValue(section, out var previous) || previous != mark) touched.Add(section);
            return touched;
        });

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
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_destroy", "encounter");
        foreach (var encounterId in _encounters
                     .Where(pair => pair.Value == playerEntityId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _encounters.TryRemove(encounterId, out _);
            if (World.GetEntity(encounterId) is not { } encounterEntity) continue;
            var enemyIds = encounterEntity.GetComponent<CombatEncounterComponent>()?.EnemyEntityIds ?? [];
            foreach (var enemyId in enemyIds)
                if (World.GetEntity(enemyId) is { } enemyEntity)
                {
                    YuoWorld.DestroyEntityForce(enemyEntity);
                    RuntimeInstrumentation.Count("lifecycle.entity_destroyed.monster");
                }
            YuoWorld.DestroyEntityForce(encounterEntity);
            RuntimeInstrumentation.Count("lifecycle.entity_destroyed.encounter");
        }
        if (World.GetEntity(playerEntityId)?.GetComponent<PlayerCombatComponent>() is { } combat)
        {
            combat.Encounter = null;
            combat.EncounterEntityId = 0;
        }
    }

    private async Task RunOnceCoreAsync(DateTimeOffset target, Guid? playerId, CancellationToken cancellationToken)
    {
        using (RuntimeInstrumentation.Measure("queue.owner_wait"))
            await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ownerHoldMeasurement = RuntimeInstrumentation.Measure("ecs.owner_hold");
        var clockBefore = _clock.Current;
        var backups = new Dictionary<Guid, (PlayerAggregateState State, AssetBalance[] Assets, PlayerSectionKind[] Dirty)>();
        var pipelines = new Dictionary<Guid, EcsActionPipeline>();
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
                EcsActionPipeline.BeginRequest(entity, GameData, target);
                pipelines[id] = EcsActionPipeline.Find(entity)!;
            }
            var cursor = pipelines.Count > 0 ? pipelines.Values.Min(pipeline => pipeline.Current) : target;
            var steps = 0;
            // 离线采集/制造的自然边界固定由资源回复窗口切分（默认 5 秒）。
            // 这些玩家没有战斗实体时，直接执行同一批管线原语可消除数百万次
            // Fiber/World/System 往返；一旦队列切换到战斗或旅行，立即回到完整 ECS 路径。
            var bulkCompleted = false;
            if (playerId is not null && pipelines.Count == 1 && pipelines.Values.All(pipeline => pipeline.CanBulkAdvance))
            {
                using var bulkMeasurement = RuntimeInstrumentation.Measure("ecs.bulk_advance", scenario);
                var pipeline = pipelines.Values.Single();
                bulkCompleted = pipeline.AdvanceBulkTimedUntilUnsupported(cancellationToken);
                cursor = pipeline.Current;
                steps = pipeline.BulkStepCount;
            }
            if (!bulkCompleted)
            {
            while (pipelines.Values.Any(pipeline => !pipeline.IsComplete))
            {
                if (++steps > SimulationEngine.MaxTransitionsPerAdvance)
                {
                    var diag = string.Join(";", pipelines.Where(pair => !pair.Value.IsComplete)
                        .OrderBy(pair => pair.Value.NextBoundaryAt).Take(8)
                        .Select(pair => $"{pair.Key:N}:cursor={pair.Value.Current:o},next={pair.Value.NextBoundaryAt:o},[{pair.Value.DiagnosticState}]"));
                    throw new DomainException(DomainErrorCode.InvariantViolation,
                        $"ECS 自然边界推进超过最大转换数。incomplete=[{diag}] " +
                        $"clock=[skip={_clock.SkipWorldUpdate},skipN={_clock.SkipWorldUpdateEntities},active={_clock.ActivePlayerId},prep={_clock.PreparationOnly}]");
                }
                cancellationToken.ThrowIfCancellationRequested();
                while (pipelines.Values.Any(pipeline => pipeline.NeedsPreparation))
                    await Tick(cursor, true).ConfigureAwait(false);
                if (pipelines.Values.All(pipeline => pipeline.IsComplete)) break;
                // 真实系统分别推进自己的自然边界；UTC 只决定执行顺序，不再回减成战斗 double 预算。
                var next = pipelines.Values.Where(pipeline => !pipeline.IsComplete).Min(pipeline => pipeline.NextBoundaryAt);
                if (next < cursor) throw new DomainException(DomainErrorCode.InvariantViolation, "ECS 自然边界时钟发生倒退。");
                await Tick(next, false).ConfigureAwait(false);
                cursor = next;
            }
            }
            using (RuntimeInstrumentation.Measure("ecs.mailbox.post", scenario))
            await _fiber.PostAsync((_, _) =>
            {
                using var callbackMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.mailbox.callback");
                _clock.Current = _clock.Target = target;
                _clock.SkipWorldUpdate = pipelines.Count > 0;
                _clock.SkipWorldUpdateEntities = pipelines.Count;
                foreach (var (id, pipeline) in pipelines)
                {
                    var result = pipeline.Commit();
                    _simulationResults[id] = result;
                    AppendSimulationEvents(id, result.Events);
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
            foreach (var pipeline in pipelines.Values) pipeline.Detach();
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
                foreach (var pipeline in pipelines.Values) pipeline.Detach();
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
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create", "map_node");
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

    private long CreateMonsterCore(string monsterDefinitionId, long? parentNodeEntityId)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create", "monster");
        if (!GameData.CombatMonsters.TryGetValue(monsterDefinitionId, out var definition)) throw new DomainException(DomainErrorCode.NotFound, $"怪物 {monsterDefinitionId} 不存在。");
        var entity = new YuoEntity(World);
        RuntimeInstrumentation.Count("lifecycle.entity_created.monster");
        entity.EntityName = $"monster:{monsterDefinitionId}:{entity.ID}";
        entity.AddComponent<MonsterIdentityComponent>();
        var identity = entity.GetComponent<MonsterIdentityComponent>()!;
        identity.MonsterDefinitionId = definition.Id; identity.DisplayName = definition.Name;
        entity.AddComponent<CombatantComponent>();
        var combatant = entity.GetComponent<CombatantComponent>()!;
        combatant.CombatantId = $"enemy:{entity.ID}"; combatant.Stats = definition.Stats; combatant.State = new CombatantState(combatant.CombatantId, definition.Stats, definition.Skills); combatant.IsPlayer = false;
        entity.AddComponent<SkillStateComponent>().Skills.AddRange(definition.Skills);
        entity.AddComponent<BuffComponent>();
        entity.AddComponent<DropProfileComponent>().Drops = definition.DropTable;
        if (parentNodeEntityId is { } parent && World.GetEntity(parent) is { } parentEntity) World.SetParent(entity, parentEntity);
        return entity.ID;
    }

    private long CreateEncounterCore(Guid playerId, string encounterId, long seed)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create", "encounter");
        var player = GetPlayer(playerId);
        if (!GameData.CombatEncounters.TryGetValue(encounterId, out var definition)) throw new DomainException(DomainErrorCode.NotFound, $"遭遇 {encounterId} 不存在。");
        var playerCombat = player.GetOrAddComponent<PlayerCombatComponent>();
        // 与 OOP 远征入口共用属性解析和技能槽映射；不能用 Combat 分段里的旧面板或空技能覆盖装备结果。
        var playerState = ExportSnapshotCore(playerId).State;
        // 战外 Buff 以 CombatEncounterState.Buffs 注入并由战斗边界计时；属性面板解析不能
        // 再把同一组 AttackSpeed/减伤点数折叠一次，否则会产生双倍增益与随机序列漂移。
        var resolved = PlayerAttributeResolver.Resolve(playerState with { Buffs = [] }, GameData);
        var playerStats = resolved.ToCombatant();
        var loadout = player.GetComponent<PlayerLoadoutComponent>()?.Loadout;
        var playerSkills = loadout is null
            ? []
            : loadout.AbilitySlots
                .Where(slot => slot.AbilityId is not null && GameData.Abilities.TryGetValue(slot.AbilityId, out _))
                .Select(slot => new SkillSlot(
                    GameData.Abilities[slot.AbilityId!].Clone(),
                    slot.Level,
                    slot.Triggers.Count == 0 ? null : slot.Triggers.Select(trigger => trigger.Clone()).ToArray()))
                .ToArray();
        var enemyDefinitions = EncounterWaveSelector.Select(definition, GameData.CombatMonsters, seed, 1).ToArray();
        if (enemyDefinitions.Length == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇没有可用怪物。");
        var enemyTemplates = enemyDefinitions.Select(m => new CombatantTemplate(m.Stats, m.Skills)).ToArray();
        var state = CombatEncounterState.Start(encounterId, seed, new CombatantTemplate(playerStats, playerSkills), enemyTemplates, definition.PlayerRespawnSeconds);
        var battleStartUnix = player.GetComponent<PlayerActionQueueComponent>()?.LastAdvancedAt.ToUnixTimeSeconds() ?? StartAt.ToUnixTimeSeconds();
        foreach (var active in player.GetComponent<PlayerBuffComponent>()?.Active ?? [])
        {
            var expiresAt = Math.Max(0, active.ExpiresAtUnixSeconds - battleStartUnix);
            state.Player.Buffs.Add(new CombatBuffState(active.BuffId, expiresAt, active.Stacks,
                active.HpRegen, active.MpRegen, active.AttackSpeedPoints, active.DamageAbsorptionPoints));
        }
        var encounterEntity = new YuoEntity(World); encounterEntity.EntityName = $"encounter:{encounterId}:{seed}";
        RuntimeInstrumentation.Count("lifecycle.entity_created.encounter");
        var encounter = encounterEntity.AddComponent<CombatEncounterComponent>(); encounter.EncounterId = encounterId; encounter.PlayerEntityId = player.ID; encounter.State = state; encounter.Seed = seed;
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
            var combatant = enemy.AddComponent<CombatantComponent>(); combatant.CombatantId = state.Enemies[i].Id; combatant.Stats = state.Enemies[i].Stats; combatant.State = state.Enemies[i];
            enemy.AddComponent<SkillStateComponent>().Skills.AddRange(enemyDefinitions[i].Skills); enemy.AddComponent<BuffComponent>(); enemy.AddComponent<DropProfileComponent>().Drops = enemyDefinitions[i].DropTable;
            enemyIds.Add(enemy.ID);
        }
        encounter.EnemyEntityIds = enemyIds;
        var playerCombatant = player.GetOrAddComponent<CombatantComponent>(); playerCombatant.CombatantId = state.Player.Id; playerCombatant.Stats = state.Player.Stats; playerCombatant.State = state.Player; playerCombatant.IsPlayer = true;
        playerCombat.Encounter = state; playerCombat.EncounterSeed = seed;
        playerCombat.EncounterEntityId = encounterEntity.ID;
        encounter.AttributeRevision = PlayerAttributeResolver.InputRevision(player.GetComponent<PlayerProgressionComponent>()!.Progression,
            player.GetComponent<PlayerWorldComponent>()!.World, player.GetComponent<PlayerBuffComponent>()!.Buffs,
            player.GetComponent<PlayerLoadoutComponent>()!.Loadout);
        var queue = player.GetComponent<PlayerActionQueueComponent>();
        if (queue is { Actions.Count: > 0 })
        {
            // CreateEncounter 是显式进入战斗的调试/服务入口，已经完成搜寻，
            // 因此直接记录 Combat 阶段，不能被行动准备系统重新覆盖为 InitialSearch。
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
                PlayerState = CombatEncounterState.Snapshot(state.Player)
            };
        }
        _encounters[encounterEntity.ID] = player.ID;
        return encounterEntity.ID;
    }

    /// <summary>把共享领域入场原语生成的战斗单位状态挂载到真实 ECS 实体。</summary>
    private long CreateEncounterFromStateCore(Guid playerId, CombatEncounterState state, DateTimeOffset startedAt)
    {
        using var lifecycleMeasurement = RuntimeInstrumentation.Measure("lifecycle.entity_create", "encounter");
        var player = GetPlayer(playerId);
        var encounterEntity = new YuoEntity(World);
        RuntimeInstrumentation.Count("lifecycle.entity_created.encounter");
        encounterEntity.EntityName = $"encounter:{state.EncounterId}:{state.Seed}";
        var encounter = encounterEntity.AddComponent<CombatEncounterComponent>();
        encounter.EncounterId = state.EncounterId;
        encounter.PlayerEntityId = player.ID;
        encounter.State = state;
        encounter.Seed = state.Seed;
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
            combatant.CombatantId = enemyState.Id;
            combatant.Stats = enemyState.Stats;
            combatant.State = enemyState;
            enemy.AddComponent<SkillStateComponent>().Skills.AddRange(enemyState.Skills);
            enemy.AddComponent<BuffComponent>();
            enemy.AddComponent<DropProfileComponent>().Drops = definition?.DropTable ?? [];
            enemyIds.Add(enemy.ID);
        }
        encounter.EnemyEntityIds = enemyIds;
        var playerCombatant = player.GetOrAddComponent<CombatantComponent>();
        playerCombatant.CombatantId = state.Player.Id;
        playerCombatant.Stats = state.Player.Stats;
        playerCombatant.State = state.Player;
        playerCombatant.IsPlayer = true;
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
        identity.GameDataFingerprint = restored.GameDataFingerprint;
        var progression = entity.GetOrAddComponent<PlayerProgressionComponent>();
        progression.Progression = restored.Progression;
        entity.GetOrAddComponent<PlayerWorldComponent>().World = restored.World;
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
            queue.Actions.Add(new EcsActionState(action.Id, action.Intent)
            {
                EnqueuedAt = action.EnqueuedAt,
                Status = action.Status,
                ProgressSeconds = action.ProgressSeconds,
                RemainingRepeats = action.RemainingRepeats,
                CompletedRepeats = action.CompletedRepeats,
                Expedition = action.Expedition,
                Travel = action.Travel
            });
        }
        var inventory = entity.GetOrAddComponent<PlayerInventoryComponent>();
        inventory.Items.Clear();
        foreach (var balance in balances)
            inventory.Items[balance.Asset] = balance.Quantity;
    }

    /// <summary>属性输入修订计数快照：对应 PlayerAttributeResolver.InputRevision 的全部输入源。</summary>
    private readonly record struct AttributeRevisionCounters(
        long Realm, long World, long Buffs, long Items, long Equipment, long Core);

    private AttributeRevisionCounters CaptureAttributeRevisions(YuoEntity entity)
        => CaptureAttributeRevisions(
            entity.GetComponent<PlayerProgressionComponent>()!.Progression,
            entity.GetComponent<PlayerWorldComponent>()!.World,
            entity.GetComponent<PlayerBuffComponent>()!.Buffs,
            entity.GetComponent<PlayerLoadoutComponent>()!.Loadout);

    private static AttributeRevisionCounters CaptureAttributeRevisions(PlayerAggregate player)
        => CaptureAttributeRevisions(player.Progression, player.World, player.Buffs, player.Loadout);

    private static AttributeRevisionCounters CaptureAttributeRevisions(
        PlayerProgression progression, PlayerWorldState world, PlayerBuffState buffs, PlayerLoadoutState loadout)
        => new(progression.RealmLevelVersion, world.AttributeInputVersion, buffs.Active.Version,
            loadout.EquippedItemIds.Version, loadout.Equipment.Version, loadout.CoreAttributesVersion);

    /// <summary>把聚合内相对恢复基线的修订增量续写到既有计数之上；写操作未触及的源保持原值。</summary>
    private static void ContinueAttributeRevisions(PlayerAggregate aggregate,
        in AttributeRevisionCounters prev, in AttributeRevisionCounters baseline)
    {
        var post = CaptureAttributeRevisions(aggregate);
        var progression = aggregate.Progression;
        progression.RestoreSnapshot(new PlayerProgressionState(progression.RealmLevel, progression.Qi,
            progression.Experience, progression.LifetimeQi, progression.CompletedActions,
            progression.SimulationCarrySeconds, progression.NormalKills, progression.EliteKills,
            progression.BossKills, progression.TotalDefeats, progression.CultivationSeconds,
            progression.AgeSeconds), prev.Realm + post.Realm - baseline.Realm);
        var world = aggregate.World;
        world.RestoreSnapshot(new PlayerWorldStateDocument(world.NodeId, world.X, world.Y,
            world.ExplorationSeed, world.EdgeId, world.EdgeProgress, world.NodeBuildings,
            world.NodeBuildingEffects, world.NodeBuildingNodeId),
            prev.World + post.World - baseline.World);
        aggregate.Buffs.Active.RestoreVersion(prev.Buffs + post.Buffs - baseline.Buffs);
        var loadout = aggregate.Loadout;
        loadout.RestoreRevisionCounters(prev.Core + post.Core - baseline.Core,
            loadout.AbilitySlots.Version, prev.Equipment + post.Equipment - baseline.Equipment,
            loadout.AbilityExperiences.Version);
        loadout.EquippedItemIds.RestoreVersion(prev.Items + post.Items - baseline.Items);
    }

    private EcsPlayerSnapshot ExportSnapshotCore(Guid id)
    {
        using var snapshotMeasurement = RuntimeInstrumentation.Measure("snapshot.export");
        var entity = GetPlayer(id);
        var identity = entity.GetComponent<PlayerIdentityComponent>()!;
        var progression = entity.GetComponent<PlayerProgressionComponent>()!;
        var world = entity.GetComponent<PlayerWorldComponent>()!;
        var combat = entity.GetComponent<PlayerCombatComponent>()!;
        var buffs = entity.GetComponent<PlayerBuffComponent>()!;
        var loadout = entity.GetComponent<PlayerLoadoutComponent>()!;
        var settings = entity.GetComponent<PlayerSettingsComponent>()!;
        var duel = entity.GetComponent<PlayerDuelComponent>()!;
        var queue = entity.GetComponent<PlayerActionQueueComponent>()!;
        var actions = queue.Actions.Select(a => new QueuedActionState(a.Id, a.Intent, a.EnqueuedAt, a.Status, a.ProgressSeconds, a.RemainingRepeats, a.CompletedRepeats, a.Expedition, a.Travel)).ToArray();
        var state = new PlayerAggregateState(new PlayerId(identity.PlayerId), new AccountId(Guid.Parse(identity.AccountId)), identity.DisplayName, identity.CreatedAt, queue.LastAdvancedAt, identity.Presence, identity.StateVersion, identity.GameDataFingerprint,
            new PlayerProgressionState(progression.RealmLevel, progression.Qi, progression.Experience, progression.LifetimeQi, progression.CompletedActions, progression.Progression.SimulationCarrySeconds, progression.Progression.NormalKills, progression.Progression.EliteKills, progression.Progression.BossKills, progression.Progression.TotalDefeats, progression.Progression.CultivationSeconds, progression.AgeSeconds),
            new PlayerWorldStateDocument(world.NodeId, world.X, world.Y, world.ExplorationSeed, world.World.EdgeId, world.World.EdgeProgress, world.World.NodeBuildings, world.World.NodeBuildingEffects, world.World.NodeBuildingNodeId),
            new PlayerCombatStateDocument(combat.Hitpoints, combat.MaxHitpoints, combat.Manapoints, combat.MaxManapoints, combat.AttackPower, combat.Defense, combat.Combat.RegenCarrySeconds) { Skills = combat.Combat.Skills },
            buffs.Active.ToArray(), loadout.Loadout.EquippedInstances.ToArray(), loadout.Loadout.LearnedAbilities.ToArray(), new PlayerSettingsStateDocument(settings.Settings.Locale, settings.Settings.NotificationsEnabled, settings.Settings.LoopEnabled, settings.Settings.AutoUseSlots.ToArray(), settings.Settings.RenameCooldownUntilUnixSeconds), actions, loadout.Loadout.Export(), duel.Document,
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

public sealed record EcsPlayerSnapshot(Guid PlayerId, long EntityId, PlayerAggregateState State, IReadOnlyDictionary<AssetKey, double> Assets);

/// <summary>Server.Clean 与 ECS 之间的纯数据节点建筑投影，避免 Ecs 项目反向引用宿主程序集。</summary>
public sealed record EcsNodeBuildingProjection(
    long Version,
    IReadOnlyList<NodeBuildingState> Buildings,
    BuildingEffectSet Effects,
    string? NodeId);

internal static class CombatantStatsExtensions
{
    public static CombatantStats WithMana(this CombatantStats stats, double maxMana) => stats with { MaxManapoints = maxMana };
}
