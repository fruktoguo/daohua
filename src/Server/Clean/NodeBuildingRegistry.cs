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
/// 节点公共建筑共享注册表：跨玩家的权威建筑状态，进程内缓存 + PostgreSQL 持久化。
/// 所有读写经单一信号量串行化（低频操作）；完工按 NodeBuildingRules 惰性推算，
/// 首次被任何玩家观察到时把 Level 落库，保证在线/离线/重算结果一致。
/// </summary>
public sealed class NodeBuildingRegistry(
    PostgresNodeBuildingRepository repository,
    GameDataCatalog gameData,
    ILogger<NodeBuildingRegistry> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<NodeBuildingState>> _nodes = new(StringComparer.Ordinal);
    private bool _loaded;

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
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 返回指定节点当前全部建筑投影（含惰性完工结算；完工跳变会落库）。
    /// 无建筑返回空数组。
    /// </summary>
    public async ValueTask<IReadOnlyList<NodeBuildingState>> BuildingsForAsync(string nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_nodes.TryGetValue(nodeId, out var list) || list.Count == 0) return [];
            await FinalizeCompletionsAsync(nodeId, list, now, cancellationToken);
            return list.ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>汇总指定节点已建成建筑效果；无建筑返回空效果集。</summary>
    public async ValueTask<BuildingEffectSet> EffectsForAsync(string nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var buildings = await BuildingsForAsync(nodeId, now, cancellationToken);
        if (buildings.Count == 0) return BuildingEffectSet.Empty;
        return NodeBuildingRules.AggregateEffects(buildings, gameData.Buildings, now.ToUnixTimeMilliseconds() / 1000d);
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
            await FinalizeCompletionsAsync(nodeId, list, now, cancellationToken);
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
            return new NodeBuildingMutationResult(true, AppliedMaterials: applied);
        }
        finally { _gate.Release(); }
    }

    /// <summary>惰性完工结算：把已到双进度满格的工程落为有效等级并持久化跳变。</summary>
    private async ValueTask FinalizeCompletionsAsync(string nodeId, List<NodeBuildingState> list, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        for (var index = 0; index < list.Count; index++)
        {
            var state = list[index];
            if (state.TargetLevel <= 0 || !gameData.Buildings.TryGetValue(state.BuildingId, out var definition)) continue;
            var finalized = NodeBuildingRules.FinalizeIfComplete(state, definition, nowUnixSeconds);
            if (ReferenceEquals(finalized, state) || finalized.Level == state.Level) continue;
            await repository.UpsertAsync(nodeId, finalized, cancellationToken);
            list[index] = finalized;
            logger.LogInformation("节点建筑完工 node={NodeId} building={BuildingId} level={Level}", nodeId, finalized.BuildingId, finalized.Level);
        }
    }
}
