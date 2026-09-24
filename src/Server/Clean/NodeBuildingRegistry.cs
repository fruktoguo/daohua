namespace Server.Clean;

/// <summary>建筑行写事务的结局类别；错误码与文案由注册表统一映射，仓储不回传展示文案。</summary>
public enum NodeBuildingWriteStatus : byte
{
    /// <summary>写入已提交。</summary>
    Applied,
    /// <summary>目标行不存在。</summary>
    NotFound,
    /// <summary>建筑当前没有进行中的工程。</summary>
    Idle,
    /// <summary>按活行剩余需求封顶后没有可接受的材料数量。</summary>
    NothingToContribute,
    /// <summary>背包扣减回调拒绝（视为材料不足）。</summary>
    DebitDenied,
    /// <summary>建筑尚未建成，不能发起升级。</summary>
    Incomplete,
    /// <summary>建筑已有工程进行中。</summary>
    Busy
}

/// <summary>
/// 一次建筑行写事务的结果：结局类别 + 数据库活行（写入成功时为写后行）。
/// 注册表据此校正进程内缓存并映射既有错误码；AppliedMaterials 为实际入库的材料明细。
/// </summary>
public sealed record NodeBuildingWriteOutcome(
    NodeBuildingWriteStatus Status,
    NodeBuildingState? State = null,
    IReadOnlyDictionary<string, double>? AppliedMaterials = null);

/// <summary>
/// 节点公共建筑持久化适配：game.node_buildings 按 (node_id, building_id) 主键读写，
/// 材料投入明细以 jsonb 字典落库；读路径在服务启动后首次访问时全量加载。
/// 写路径统一为「SELECT ... FOR UPDATE 锁行 → 按数据库活行裁决 → 写回」的短事务：
/// 跨实例写者在行锁上串行，封顶/可升级性等裁决只认活行而非调用方缓存，
/// 杜绝贡献材料被旧投影整行覆盖的丢更新问题（见 docs/distributed-readiness.md §F）。
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
            result.Add((
                reader.GetString(0),
                new NodeBuildingState(
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetDouble(4),
                    ReadMaterials(reader.GetString(5)))));
        }
        return result;
    }

    /// <summary>按主键读取单行建筑状态（不加锁）；行不存在返回 null，供缓存采纳活行使用。</summary>
    public async ValueTask<NodeBuildingState?> TryLoadAsync(string nodeId, string buildingId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT level, target_level, started_at_unix_seconds, contributed_materials
            FROM game.node_buildings
            WHERE node_id = $1 AND building_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = buildingId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new NodeBuildingState(
            buildingId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetDouble(2),
            ReadMaterials(reader.GetString(3)));
    }

    /// <summary>写入一座新建筑；同节点同种已存在时按唯一约束裁决返回 false。</summary>
    public async ValueTask<bool> InsertIfAbsentAsync(string nodeId, NodeBuildingState state, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.node_buildings (node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, now())
            ON CONFLICT (node_id, building_id) DO NOTHING
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
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 贡献材料事务：行锁内读活行 → 按活行剩余需求封顶 → 锁内回调扣减背包 → 合并写回。
    /// 封顶基数只认活行，跨实例并发贡献在行锁上串行合并，既不丢也不重复接纳；
    /// 扣减失败即整体回滚，材料扣减与贡献登记同生同灭。
    /// </summary>
    public async ValueTask<NodeBuildingWriteOutcome> ContributeAsync(
        string nodeId,
        string buildingId,
        BuildingDefinition definition,
        IReadOnlyDictionary<string, double> requested,
        Func<IReadOnlyDictionary<string, double>, ValueTask<bool>> commitDebitAsync,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await SelectForUpdateAsync(connection, transaction, nodeId, buildingId, cancellationToken);
        if (row is null)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.NotFound);
        if (row.TargetLevel <= 0)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.Idle, row);
        var required = NodeBuildingRules.RequiredMaterials(definition, row.TargetLevel);
        var applied = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (itemId, quantity) in requested)
        {
            if (!required.TryGetValue(itemId, out var need) || !double.IsFinite(quantity) || quantity <= 0) continue;
            var remaining = need - row.ContributedMaterials.GetValueOrDefault(itemId);
            var accepted = Math.Min(remaining, quantity);
            if (accepted > 0) applied[itemId] = accepted;
        }
        if (applied.Count == 0)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.NothingToContribute, row);
        if (!await commitDebitAsync(applied))
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.DebitDenied, row);
        var merged = new Dictionary<string, double>(row.ContributedMaterials, StringComparer.Ordinal);
        foreach (var (itemId, quantity) in applied)
            merged[itemId] = merged.GetValueOrDefault(itemId) + quantity;
        var updated = row with { ContributedMaterials = merged };
        await UpdateLockedRowAsync(connection, transaction, nodeId, updated, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.Applied, updated, applied);
    }

    /// <summary>
    /// 升级发起事务：行锁内先对活行做惰性完工推算（到期工程先落成再评估，与读路径同口径），
    /// 再按活行校验可升级性并写回目标等级；跨实例并发升级在行锁上串行，后到者按活行报 Busy。
    /// </summary>
    public async ValueTask<NodeBuildingWriteOutcome> StartUpgradeAsync(
        string nodeId,
        string buildingId,
        BuildingDefinition definition,
        double nowUnixSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await SelectForUpdateAsync(connection, transaction, nodeId, buildingId, cancellationToken);
        if (row is null)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.NotFound);
        // 退化行（Level == TargetLevel）完工不改写等级，按原行继续评估（与 FinalizeCompletionsAsync 跳过口径一致）。
        var finalized = NodeBuildingRules.FinalizeIfComplete(row, definition, nowUnixSeconds);
        var live = finalized.Level == row.Level ? row : finalized;
        if (live.Level <= 0)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.Incomplete, row);
        if (live.TargetLevel > 0)
            return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.Busy, row);
        var upgraded = live with
        {
            TargetLevel = live.Level + 1,
            StartedAtUnixSeconds = nowUnixSeconds,
            ContributedMaterials = new Dictionary<string, double>(StringComparer.Ordinal)
        };
        await UpdateLockedRowAsync(connection, transaction, nodeId, upgraded, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NodeBuildingWriteOutcome(NodeBuildingWriteStatus.Applied, upgraded);
    }

    /// <summary>
    /// 惰性完工事务：行锁内读活行，到期则落成目标等级写回，未到期或被外部改过则原样返回活行；
    /// 行不存在返回 null。完工结算幂等，双实例重复执行落到同一结果。
    /// </summary>
    public async ValueTask<NodeBuildingState?> FinalizeIfDueAsync(
        string nodeId,
        string buildingId,
        BuildingDefinition definition,
        double nowUnixSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await SelectForUpdateAsync(connection, transaction, nodeId, buildingId, cancellationToken);
        if (row is null) return null;
        var finalized = NodeBuildingRules.FinalizeIfComplete(row, definition, nowUnixSeconds);
        if (finalized.Level == row.Level) return row;
        await UpdateLockedRowAsync(connection, transaction, nodeId, finalized, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return finalized;
    }

    /// <summary>锁住并读取单行建筑状态；FOR UPDATE 让跨实例写者在行锁上串行。行不存在返回 null。</summary>
    private static async ValueTask<NodeBuildingState?> SelectForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string nodeId,
        string buildingId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT level, target_level, started_at_unix_seconds, contributed_materials
            FROM game.node_buildings
            WHERE node_id = $1 AND building_id = $2
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = buildingId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new NodeBuildingState(
            buildingId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetDouble(2),
            ReadMaterials(reader.GetString(3)));
    }

    /// <summary>把已锁定行写回为指定状态；行锁保证同一时刻只有本事务是写者。</summary>
    private static async ValueTask UpdateLockedRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string nodeId,
        NodeBuildingState state,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE game.node_buildings
            SET level = $3, target_level = $4, started_at_unix_seconds = $5,
                contributed_materials = $6::jsonb, updated_at = now()
            WHERE node_id = $1 AND building_id = $2
            """, connection, transaction);
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

    /// <summary>解析 contributed_materials jsonb 字典：仅采纳有限正数项，与历史落库口径一致。</summary>
    private static Dictionary<string, double> ReadMaterials(string json)
    {
        var materials = new Dictionary<string, double>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
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
        return materials;
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
/// 写路径（建造/升级/贡献/惰性完工落库）经仓储行锁事务按数据库活行裁决——进程内
/// _gate 只负责本实例的缓存一致与快照发布，跨实例正确性由行锁保证；裁决后把活行
/// 采纳进缓存并重发该节点快照。读路径为 Volatile 发布的不可变快照，玩家每拍推进
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
            // 缓存未收录不代表行不存在：并发实例可能已抢先建行，以唯一约束裁决为准；
            // 撞行时把活行采纳进快照，让「已存在」的节点立刻看得见那座建筑。
            if (!await repository.InsertIfAbsentAsync(nodeId, state, cancellationToken))
            {
                if (await repository.TryLoadAsync(nodeId, definition.Id, cancellationToken) is { } existing)
                    AdoptState(nodeId, existing);
                return NodeBuildingMutationResult.Fail("BUILDING_EXISTS", "此地已有同类建筑或同类工程进行中。");
            }
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
        if (!gameData.Buildings.TryGetValue(buildingId, out var definition))
            return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_nodes.TryGetValue(nodeId, out var list)
                && await FinalizeCompletionsAsync(nodeId, list, now, cancellationToken))
                PublishNode(nodeId, list);
            // 可升级性以数据库活行为准（行锁内先做惰性完工再校验），缓存仅作快照展示。
            var outcome = await repository.StartUpgradeAsync(
                nodeId, buildingId, definition, now.ToUnixTimeMilliseconds() / 1000d, cancellationToken);
            if (outcome.State is { } live) AdoptState(nodeId, live);
            return outcome.Status switch
            {
                NodeBuildingWriteStatus.Applied => NodeBuildingMutationResult.Ok(),
                NodeBuildingWriteStatus.Incomplete => NodeBuildingMutationResult.Fail("BUILDING_INCOMPLETE", "建筑尚未建成，无法升级。"),
                NodeBuildingWriteStatus.Busy => NodeBuildingMutationResult.Fail("BUILDING_BUSY", "该建筑已有工程进行中。"),
                _ => NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。")
            };
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 提交建造材料：行锁事务内按活行剩余需求封顶后，先经 commitDebitAsync 扣减玩家背包，
    /// 扣减成功才合并写回；全部已满或无有效数量时不做任何变更。
    /// </summary>
    /// <param name="commitDebitAsync">在建筑行锁内执行的背包扣减回调；返回 false 视为材料不足放弃。</param>
    public async ValueTask<NodeBuildingMutationResult> ContributeAsync(
        string nodeId,
        string buildingId,
        IReadOnlyDictionary<string, double> requested,
        Func<IReadOnlyDictionary<string, double>, ValueTask<bool>> commitDebitAsync,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!gameData.Buildings.TryGetValue(buildingId, out var definition))
            return NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var outcome = await repository.ContributeAsync(nodeId, buildingId, definition, requested, commitDebitAsync, cancellationToken);
            if (outcome.State is { } live) AdoptState(nodeId, live);
            return outcome.Status switch
            {
                NodeBuildingWriteStatus.Applied => new NodeBuildingMutationResult(true, AppliedMaterials: outcome.AppliedMaterials),
                NodeBuildingWriteStatus.Idle => NodeBuildingMutationResult.Fail("BUILDING_IDLE", "该建筑当前没有进行中的工程。"),
                NodeBuildingWriteStatus.NothingToContribute => NodeBuildingMutationResult.Fail("NOTHING_TO_CONTRIBUTE", "没有可提交的材料数量。"),
                NodeBuildingWriteStatus.DebitDenied => NodeBuildingMutationResult.Fail("INSUFFICIENT_MATERIALS", "背包材料不足。"),
                _ => NodeBuildingMutationResult.Fail("BUILDING_NOT_FOUND", "此地没有该建筑。")
            };
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

    /// <summary>
    /// 把数据库活行/写后行采纳进缓存：未收录条目（外部实例创建）追加，已有条目按内容
    /// 差异替换；有实质变化时重发该节点快照，缓存由此对跨实例写入自愈。
    /// </summary>
    private void AdoptState(string nodeId, NodeBuildingState state)
    {
        if (!_nodes.TryGetValue(nodeId, out var list)) _nodes[nodeId] = list = [];
        var index = list.FindIndex(entry => string.Equals(entry.BuildingId, state.BuildingId, StringComparison.Ordinal));
        if (index >= 0 && StatesEqual(list[index], state)) return;
        if (index < 0) list.Add(state);
        else list[index] = state;
        PublishNode(nodeId, list);
    }

    /// <summary>两条建筑状态的内容等价比较（材料明细按键值逐项比对）。</summary>
    private static bool StatesEqual(NodeBuildingState a, NodeBuildingState b)
    {
        if (a.Level != b.Level || a.TargetLevel != b.TargetLevel
            || !a.StartedAtUnixSeconds.Equals(b.StartedAtUnixSeconds)
            || a.ContributedMaterials.Count != b.ContributedMaterials.Count)
            return false;
        foreach (var (itemId, quantity) in a.ContributedMaterials)
        {
            if (!b.ContributedMaterials.TryGetValue(itemId, out var other) || !other.Equals(quantity))
                return false;
        }
        return true;
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

    /// <summary>
    /// 惰性完工结算：对快照内已到双进度满格的工程，经仓储行锁事务按活行落成有效等级；
    /// 活行被外部改过时采纳活行。返回缓存是否有实质变化。
    /// </summary>
    private async ValueTask<bool> FinalizeCompletionsAsync(string nodeId, List<NodeBuildingState> list, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        for (var index = 0; index < list.Count; index++)
        {
            var state = list[index];
            if (state.TargetLevel <= 0 || !gameData.Buildings.TryGetValue(state.BuildingId, out var definition)) continue;
            if (!NodeBuildingRules.IsComplete(state, definition, nowUnixSeconds)) continue;
            var live = await repository.FinalizeIfDueAsync(nodeId, state.BuildingId, definition, nowUnixSeconds, cancellationToken);
            if (live is null || StatesEqual(live, state)) continue;
            if (live.Level != state.Level)
                logger.LogInformation("节点建筑完工 node={NodeId} building={BuildingId} level={Level}", nodeId, live.BuildingId, live.Level);
            list[index] = live;
            changed = true;
        }
        return changed;
    }
}
