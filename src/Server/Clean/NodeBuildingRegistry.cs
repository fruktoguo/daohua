namespace Server.Clean;

/// <summary>
/// 节点公共建筑持久化适配：game.node_buildings 按 (node_id, building_id) 主键整行 upsert，
/// 材料投入明细以 jsonb 字典落库；读路径在服务启动后首次访问时全量加载。
/// </summary>
public sealed class PostgresNodeBuildingRepository(NpgsqlDataSource dataSource)
{
    /// <summary>加载全部节点建筑行；表为空返回空列表。</summary>
    public async ValueTask<IReadOnlyList<(string NodeId, NodeBuildingState State)>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<(string, NodeBuildingState)>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials
            FROM game.node_buildings
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var materials = new Dictionary<string, double>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(reader.GetString(5)))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.TryGetDouble(out var quantity)
                            && double.IsFinite(quantity) && quantity > 0)
                            materials[property.Name] = quantity;
                    }
                }
            }
            result.Add((
                reader.GetString(0),
                new NodeBuildingState(
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetDouble(4),
                    materials)));
        }
        return result;
    }

    /// <summary>整行 upsert 一座节点建筑的最新状态。</summary>
    public async ValueTask UpsertAsync(string nodeId, NodeBuildingState state, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.node_buildings (node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, now())
            ON CONFLICT (node_id, building_id) DO UPDATE SET
                level = EXCLUDED.level,
                target_level = EXCLUDED.target_level,
                started_at_unix_seconds = EXCLUDED.started_at_unix_seconds,
                contributed_materials = EXCLUDED.contributed_materials,
                updated_at = now()
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.BuildingId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.Level });
        command.Parameters.Add(new NpgsqlParameter { Value = state.TargetLevel });
        command.Parameters.Add(new NpgsqlParameter { Value = state.StartedAtUnixSeconds });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(state.ContributedMaterials ?? new Dictionary<string, double>()),
            NpgsqlDbType = NpgsqlDbType.Jsonb
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>建筑命令结局；贡献命令在 AppliedMaterials 中回传实际入库的材料量（按剩余需求封顶）。</summary>
public sealed record NodeBuildingMutationResult(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyDictionary<string, double>? AppliedMaterials = null)
{
    public static NodeBuildingMutationResult Fail(string code, string message) => new(false, code, message);
    public static NodeBuildingMutationResult Ok() => new(true);
}

/// <summary>
/// 单节点建筑只读快照：不可变数组 + 单调递增版本戳。
/// 同节点内容任何变化（建造/升级/材料/完工）都会递增版本；版本不变即可跳过下游重投影。
/// </summary>
public sealed record NodeBuildingSnapshot(long Version, IReadOnlyList<NodeBuildingState> Buildings);

/// <summary>
/// 节点公共建筑共享注册表：跨玩家的权威建筑状态，进程内缓存 + PostgreSQL 持久化。
/// 写路径（建造/升级/贡献/惰性完工落库）仍经单一信号量串行化——contributed_materials
/// 叠加与完工结算都要求单写者；读路径改为 Volatile 发布的不可变快照，玩家每拍推进
/// 零锁直读，仅在快照内检测到到期完工工程时才进锁结算并重发该节点快照。
/// </summary>
public sealed class NodeBuildingRegistry(
    PostgresNodeBuildingRepository repository,
    GameDataCatalog gameData,
    ILogger<NodeBuildingRegistry> logger)
{
    /// <summary>「节点无建筑」的空快照：版本恒 0。</summary>
    private static readonly NodeBuildingSnapshot EmptySnapshot = new(0, []);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<NodeBuildingState>> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    /// <summary>已发布只读快照：锁内整体替换引用；锁外 Volatile 读取，数组内容不可变。</summary>
    private IReadOnlyDictionary<string, NodeBuildingSnapshot> _snapshot =
        new Dictionary<string, NodeBuildingSnapshot>(StringComparer.Ordinal);
    private volatile bool _loaded;

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;
            foreach (var (nodeId, state) in await repository.LoadAllAsync(cancellationToken))
            {
                if (!_nodes.TryGetValue(nodeId, out var list)) _nodes[nodeId] = list = [];
                list.Add(state);
            }
            PublishAll();
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 返回指定节点当前建筑快照（含惰性完工结算；完工跳变会落库并递增版本）。
    /// 无到期工程时零锁直读已发布快照；节点无建筑返回版本 0 的空快照。
    /// </summary>
    public async ValueTask<NodeBuildingSnapshot> BuildingsForAsync(string nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var published = Volatile.Read(ref _snapshot);
        if (!published.TryGetValue(nodeId, out var entry) || !HasDueCompletion(entry.Buildings, now))
            return entry ?? EmptySnapshot;
        // 读路径快进快出：只有快照内确有到期完工工程时才进写锁做惰性结算并重发。
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_nodes.TryGetValue(nodeId, out var list) || list.Count == 0) return EmptySnapshot;
            if (await FinalizeCompletionsAsync(nodeId, list, now, cancellationToken))
                PublishNode(nodeId, list);
            return Volatile.Read(ref _snapshot).GetValueOrDefault(nodeId, EmptySnapshot);
        }
        finally { _gate.Release(); }
    }

    /// <summary>汇总指定节点已建成建筑效果；无建筑返回空效果集。</summary>
    public async ValueTask<BuildingEffectSet> EffectsForAsync(string nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await BuildingsForAsync(nodeId, now, cancellationToken);
        if (snapshot.Buildings.Count == 0) return BuildingEffectSet.Empty;
        return NodeBuildingRules.AggregateEffects(snapshot.Buildings, gameData.Buildings, now.ToUnixTimeMilliseconds() / 1000d);
    }

    /// <summary>发起新建筑建造（目标等级 1）；同节点同种仅允许一座。</summary>
    public async ValueTask<NodeBuildingMutationResult> StartConstructionAsync(string nodeId, BuildingDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_nodes.TryGetValue(nodeId, out var list)) _nodes[nodeId] = list = [];
            if (list.Any(entry => string.Equals(entry.BuildingId, definition.Id, StringComparison.Ordinal)))
                return NodeBuildingMutationResult.Fail("BUILDING_EXISTS", "此地已有同类建筑或同类工程进行中。");
            var state = new NodeBuildingState(
                definition.Id,
                0,
                1,
                now.ToUnixTimeMilliseconds() / 1000d,
                new Dictionary<string, double>(StringComparer.Ordinal));
            await repository.UpsertAsync(nodeId, state, cancellationToken);
            list.Add(state);
            PublishNode(nodeId, list);
            return NodeBuildingMutationResult.Ok();
        }
        finally { _gate.Release(); }
    }

    /// <summary>发起升级工程（目标等级 = 当前有效等级 + 1）；仅已建成且空闲的建筑可升级。</summary>
    public async ValueTask<NodeBuildingMutationResult> StartUpgradeAsync(string nodeId, string buildingId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_nodes.TryGetValue(nodeId, out var list))
                return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
            var index = list.FindIndex(entry => string.Equals(entry.BuildingId, buildingId, StringComparison.Ordinal));
            if (index < 0 || !gameData.Buildings.TryGetValue(buildingId, out var definition))
                return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
            if (await FinalizeCompletionsAsync(nodeId, list, now, cancellationToken))
                PublishNode(nodeId, list);
            var state = list[index];
            if (state.Level <= 0)
                return NodeBuildingMutationResult.Fail("BUILDING_INCOMPLETE", "建筑尚未建成，无法升级。");
            if (state.TargetLevel > 0)
                return NodeBuildingMutationResult.Fail("BUILDING_BUSY", "该建筑已有工程进行中。");
            var upgraded = state with
            {
                TargetLevel = state.Level + 1,
                StartedAtUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d,
                ContributedMaterials = new Dictionary<string, double>(StringComparer.Ordinal)
            };
            await repository.UpsertAsync(nodeId, upgraded, cancellationToken);
            list[index] = upgraded;
            PublishNode(nodeId, list);
            return NodeBuildingMutationResult.Ok();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 提交建造材料：逐项按剩余需求封顶后，先经 commitDebitAsync 扣减玩家背包，
    /// 扣减成功才写入注册表并落库；全部已满或无有效数量时不做任何变更。
    /// </summary>
    /// <param name="commitDebitAsync">在注册表锁内执行的背包扣减回调；返回 false 视为材料不足放弃。</param>
    public async ValueTask<NodeBuildingMutationResult> ContributeAsync(
        string nodeId,
        string buildingId,
        IReadOnlyDictionary<string, double> requested,
        Func<IReadOnlyDictionary<string, double>, ValueTask<bool>> commitDebitAsync,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_nodes.TryGetValue(nodeId, out var list))
                return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
            var index = list.FindIndex(entry => string.Equals(entry.BuildingId, buildingId, StringComparison.Ordinal));
            if (index < 0 || !gameData.Buildings.TryGetValue(buildingId, out var definition))
                return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
            var state = list[index];
            if (state.TargetLevel <= 0)
                return NodeBuildingMutationResult.Fail("BUILDING_IDLE", "该建筑当前没有进行中的工程。");
            var required = NodeBuildingRules.RequiredMaterials(definition, state.TargetLevel);
            var applied = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (itemId, quantity) in requested)
            {
                if (!required.TryGetValue(itemId, out var need) || !double.IsFinite(quantity) || quantity <= 0) continue;
                var remaining = need - state.ContributedMaterials.GetValueOrDefault(itemId);
                var accepted = Math.Min(remaining, quantity);
                if (accepted > 0) applied[itemId] = accepted;
            }
            if (applied.Count == 0)
                return NodeBuildingMutationResult.Fail("NOTHING_TO_CONTRIBUTE", "没有可提交的材料数量。");
            if (!await commitDebitAsync(applied))
                return NodeBuildingMutationResult.Fail("INSUFFICIENT_MATERIALS", "背包材料不足。");
            var merged = new Dictionary<string, double>(state.ContributedMaterials, StringComparer.Ordinal);
            foreach (var (itemId, quantity) in applied)
                merged[itemId] = merged.GetValueOrDefault(itemId) + quantity;
            var updated = state with { ContributedMaterials = merged };
            await repository.UpsertAsync(nodeId, updated, cancellationToken);
            list[index] = updated;
            PublishNode(nodeId, list);
            return new NodeBuildingMutationResult(true, AppliedMaterials: applied);
        }
        finally { _gate.Release(); }
    }

    /// <summary>快照内是否存在已到期完工的工程（材料与时间双进度满格）；纯只读推算，不进锁。</summary>
    private bool HasDueCompletion(IReadOnlyList<NodeBuildingState> buildings, DateTimeOffset now)
    {
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        foreach (var state in buildings)
        {
            // TargetLevel == Level 的退化行永远不会改写等级，不算到期（与 FinalizeCompletionsAsync 跳过条件对齐）。
            if (state.TargetLevel <= 0 || state.TargetLevel == state.Level) continue;
            if (gameData.Buildings.TryGetValue(state.BuildingId, out var definition)
                && NodeBuildingRules.IsComplete(state, definition, nowUnixSeconds))
                return true;
        }
        return false;
    }

    /// <summary>锁内重发单节点快照：复制为不可变数组并递增版本戳，读路径随后零锁可见。</summary>
    private void PublishNode(string nodeId, List<NodeBuildingState> list)
    {
        var version = _versions.GetValueOrDefault(nodeId) + 1;
        _versions[nodeId] = version;
        var next = new Dictionary<string, NodeBuildingSnapshot>(_snapshot, StringComparer.Ordinal);
        next[nodeId] = new NodeBuildingSnapshot(version, list.ToArray());
        Volatile.Write(ref _snapshot, next);
    }

    /// <summary>锁内重建全量快照：仅在初次加载后调用一次，各节点版本从 1 起（0 保留给空节点）。</summary>
    private void PublishAll()
    {
        var next = new Dictionary<string, NodeBuildingSnapshot>(_nodes.Count, StringComparer.Ordinal);
        foreach (var (nodeId, list) in _nodes)
        {
            var version = _versions.GetValueOrDefault(nodeId) + 1;
            _versions[nodeId] = version;
            next[nodeId] = new NodeBuildingSnapshot(version, list.ToArray());
        }
        Volatile.Write(ref _snapshot, next);
    }

    /// <summary>惰性完工结算：把已到双进度满格的工程落为有效等级并持久化跳变；返回是否有落成。</summary>
    private async ValueTask<bool> FinalizeCompletionsAsync(string nodeId, List<NodeBuildingState> list, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        for (var index = 0; index < list.Count; index++)
        {
            var state = list[index];
            if (state.TargetLevel <= 0 || !gameData.Buildings.TryGetValue(state.BuildingId, out var definition)) continue;
            var finalized = NodeBuildingRules.FinalizeIfComplete(state, definition, nowUnixSeconds);
            if (ReferenceEquals(finalized, state) || finalized.Level == state.Level) continue;
            await repository.UpsertAsync(nodeId, finalized, cancellationToken);
            list[index] = finalized;
            changed = true;
            logger.LogInformation("节点建筑完工 node={NodeId} building={BuildingId} level={Level}", nodeId, finalized.BuildingId, finalized.Level);
        }
        return changed;
    }
}
