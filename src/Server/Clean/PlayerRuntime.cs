using Domain.Player;
using Domain.Combat;
using Domain.Simulation;
using Domain.GameData;
using Domain.Common;
using Domain.Economy;
using Domain.World;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 角色运行时只读快照。
/// 包含内存聚合不可变状态、数据库持久化排队动作与当前资产钱包余额。
/// </summary>
public sealed record PlayerRuntimeSnapshot(
    Guid PlayerId,
    string AccountId,
    string DisplayName,
    long StateVersion,
    DateTimeOffset LastSimulatedAt,
    PlayerAggregateState State,
    IReadOnlyList<StoredAction> Actions,
    IReadOnlyList<AssetBalanceRecord> Assets);

/// <summary>
/// 玩家指令执行回执契约包。
/// 包含执行成功标志、领域/协议错误代码、执行后最新快照以及产生的战报日志。
/// </summary>
public sealed record PlayerCommandResult(
    bool Success,
    string Code,
    string Message,
    PlayerRuntimeSnapshot Snapshot,
    SimulationResult? Simulation = null,
    IReadOnlyList<LogEventDto>? LogEvents = null);

/// <summary>
/// 在线玩家单写者运行时：内存持有完整领域聚合，跨边界只交换消息和 section 载荷。
/// 每个活跃角色对应一个单线程串行的 PlayerActor，由本地信号量保证指令与模拟推进的严格有序性。
/// </summary>
public sealed class PlayerRuntime(
    IPlayerStateRepository repository,
    IAssetBalanceRepository assetRepository,
    IEconomyTransactionStore economy,
    GameDataCatalog gameData,
    ILogger<PlayerRuntime> logger,
    MarketRuntime? marketRuntime = null,
    ICommandReceiptStore? commandReceipts = null,
    IPlayerOwnershipStore? ownership = null,
    IAccountRepository? accounts = null,
    NodeBuildingRegistry? nodeBuildings = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<PlayerActor>>> _actors = new();
    private readonly ActionCatalog _actions = gameData.Actions;
    private readonly Guid _ownerInstanceId = Guid.NewGuid();
    private readonly TimeSpan _ownershipLease = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<Guid, PlayerLogBuffer> _logBuffers = new();

    /// <summary>每玩家战报日志环形缓冲容量：保留最近 200 条用于重连回放。</summary>
    private const int LogBufferCapacity = 200;

    /// <summary>当前已加载并在内存中活跃的玩家 Actor 实例数</summary>
    public int LoadedCount => _actors.Count;
    /// <summary>快照更新事件广播（供 WebSocket 推送订阅）</summary>
    public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;

    /// <summary>玩家战报日志批次推送；参数为玩家 ID 与本批日志，订阅者必须按玩家过滤。</summary>
    public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;

    /// <summary>加载并获取指定玩家的最新运行时快照</summary>
    public async ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));
        var actor = await GetOrAddActorAsync(playerId, cancellationToken);
        return await actor.SnapshotAsync(cancellationToken);
    }

    /// <summary>
    /// 在玩家专属 Actor 内串行执行一条领域命令；requestId 从载荷的 requestId 字段透传（缺失则不参与幂等去重）。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="command">命令名称（如 action.enqueue、world.travel）。</param>
    /// <param name="payload">命令 JSON 载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>命令回执：成功标志、错误码、执行后快照与战报日志。</returns>
    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(playerId, command, payload, 0, TryReadRequestId(payload), cancellationToken);

    /// <summary>在玩家单写者锁内校验期望版本，避免 HTTP/WS 先读后写的竞态。</summary>
    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(playerId, command, payload, expectedStateVersion, TryReadRequestId(payload), cancellationToken);

    /// <summary>
    /// 显式指定期望版本与请求标识的命令入口；requestId 为空或载荷内 requestId 不一致时拒绝。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="command">命令名称（如 action.enqueue、world.travel）。</param>
    /// <param name="payload">命令 JSON 载荷。</param>
    /// <param name="expectedStateVersion">期望的状态版本；大于 0 时在单写者锁内校验，不等即返回版本冲突。</param>
    /// <param name="requestId">请求唯一标识，作为命令幂等去重键。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>命令回执：成功标志、错误码、执行后快照与战报日志。</returns>
    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("RequestId 不能为空。", nameof(requestId));
        return ExecuteCoreAsync(playerId, command, payload, expectedStateVersion, requestId, cancellationToken);
    }

    private async ValueTask<PlayerCommandResult> ExecuteCoreAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid? requestId, CancellationToken cancellationToken)
    {
        var actor = await GetOrAddActorAsync(playerId, cancellationToken);
        var result = await actor.ExecuteAsync(command, payload, expectedStateVersion, requestId, cancellationToken);
        // 所有权失效的清理必须在命令返回后、锁外执行：在锁内触发释放会让同链路的
        // 下一条命令撞上已销毁的信号量。失效后后续命令走重新加载或拿到明确失败。
        if (result.Code == "PLAYER_OWNERSHIP_LOST") EvictOwnershipLostActor(playerId, actor);
        else
        {
            try
            {
                await RouteDuelForfeitsAsync(actor, cancellationToken);
                await TryMatchDuelAsync(actor, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // actor 已在锁外释放：弃战通知与撮合随下次加载重试，不阻断本次命令回执。
            }
        }
        // 日志推送在单写者锁外进行，慢客户端不能反向阻塞命令与模拟推进。
        await EmitLogBatchAsync(playerId, result.LogEvents);
        return result;
    }

    /// <summary>
    /// 斗法撮合：对处于匹配等待相位的寻求者，在已加载玩家中寻找实力 ±30% 带内的候选，
    /// 双方先预约后挂载同一份确定性剧本；任一步失败全部回滚，无候选时退出等待转正常波次。
    /// </summary>
    private async ValueTask TryMatchDuelAsync(PlayerActor seeker, CancellationToken cancellationToken)
    {
        var seek = await seeker.ReadDuelPendingAsync(cancellationToken);
        if (seek is null) return;
        var band = gameData.Duel.PowerBandRatio;
        var candidates = new List<(PlayerActor Actor, Guid PlayerId)>();
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            PlayerActor candidate;
            try { candidate = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken); }
            catch { continue; }
            if (ReferenceEquals(candidate, seeker)) continue;
            var info = await candidate.ReadDuelCandidateAsync(cancellationToken);
            if (info is null) continue;
            if (info.Value.Power < seek.Value.Power * (1 - band) || info.Value.Power > seek.Value.Power * (1 + band)) continue;
            candidates.Add((candidate, info.Value.PlayerId));
        }
        if (candidates.Count == 0)
        {
            // 无候选对手：静默回退为正常波次，不产生任何玩家可见提示。
            await seeker.AbortDuelSeekAsync(cancellationToken);
            return;
        }
        var (picked, pickedId) = candidates[Random.Shared.Next(candidates.Count)];
        var duelId = Guid.NewGuid();
        var initiatorMirror = await seeker.ReserveDuelAsync(duelId, cancellationToken);
        if (initiatorMirror is null)
        {
            await seeker.AbortDuelSeekAsync(cancellationToken);
            return;
        }
        var defenderMirror = await picked.ReserveDuelAsync(duelId, cancellationToken);
        if (defenderMirror is null)
        {
            await seeker.ReleaseDuelReservationAsync(duelId, cancellationToken);
            await seeker.AbortDuelSeekAsync(cancellationToken);
            return;
        }
        var seed = BitConverter.ToInt64(SHA256.HashData(duelId.ToByteArray()));
        var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, seed, gameData.Duel);
        var duelIdText = duelId.ToString("N");
        if (!await seeker.AttachDuelAsync(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, viewerIsInitiator: true), cancellationToken))
        {
            await seeker.ReleaseDuelReservationAsync(duelId, cancellationToken);
            await picked.ReleaseDuelReservationAsync(duelId, cancellationToken);
            await seeker.AbortDuelSeekAsync(cancellationToken);
            return;
        }
        if (!await picked.AttachDuelAsync(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, viewerIsInitiator: false), cancellationToken))
        {
            await picked.ReleaseDuelReservationAsync(duelId, cancellationToken);
            await seeker.DetachDuelAsync(duelId, cancellationToken);
            return;
        }
        // 双方挂载成功立刻广播快照，应战方客户端即时看到斗法开打。
        var handler = SnapshotUpdated;
        if (handler is not null)
        {
            await handler(await seeker.SnapshotAsync(cancellationToken));
            await handler(await picked.SnapshotAsync(cancellationToken));
        }
        // 撮合成立才补「斗法遭遇」日志：匹配等待阶段不下发提示，无对手时全程静默。
        await EmitDuelMatchedLogAsync(seek.Value.PlayerId, defenderMirror.DisplayName);
        await EmitDuelMatchedLogAsync(pickedId, initiatorMirror.DisplayName);
    }

    /// <summary>撮合成功通知：以玩家战报日志通道写一条「斗法遭遇」并即时推送；不入缓冲时仅跳过。</summary>
    private async ValueTask EmitDuelMatchedLogAsync(Guid playerId, string opponentName)
    {
        if (!_logBuffers.TryGetValue(playerId, out var buffer)) return;
        var entry = new LogEventDto(
            buffer.NextSequence(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            $"斗法遭遇：{opponentName} 与你狭路相逢。",
            Channel: LogChannels.System);
        buffer.Append(entry);
        await EmitLogBatchAsync(playerId, [entry]);
    }

    /// <summary>把本侧弃战产生的对手判胜通知路由到对方 Actor，并即时广播双方快照。</summary>
    private async ValueTask RouteDuelForfeitsAsync(PlayerActor actor, CancellationToken cancellationToken)
    {
        var forfeits = await actor.DrainDuelForfeitsAsync(cancellationToken);
        if (forfeits.Count == 0) return;
        var handler = SnapshotUpdated;
        foreach (var (duelId, peerId) in forfeits)
        {
            if (peerId == Guid.Empty || !_actors.TryGetValue(peerId, out var peerLazy) || !peerLazy.IsValueCreated) continue;
            PlayerActor peer;
            try { peer = await AwaitActorAsync(peerId, peerLazy, cancellationToken); }
            catch { continue; }
            if (ReferenceEquals(peer, actor)) continue;
            if (await peer.ApplyPeerForfeitAsync(duelId, cancellationToken) && handler is not null)
                await handler(await peer.SnapshotAsync(cancellationToken));
        }
        if (handler is not null) await handler(await actor.SnapshotAsync(cancellationToken));
    }

    private async ValueTask<PlayerActor> GetOrAddActorAsync(Guid playerId, CancellationToken cancellationToken)
    {
        // Lazy 工厂固定使用 CancellationToken.None：actor 创建与初始模拟推进不能被首个调用者的
        // 请求取消连带中断；调用方 token 只用于等待已缓存的任务。
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
        return await AwaitActorAsync(playerId, lazy, cancellationToken);
    }

    private async ValueTask<PlayerActor> AwaitActorAsync(Guid playerId, Lazy<Task<PlayerActor>> lazy, CancellationToken cancellationToken)
    {
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 等待被取消不代表创建失败：任务可能仍在执行，保留条目避免重复创建。
            throw;
        }
        catch
        {
            // 创建失败必须移除缓存的失败条目，否则该玩家后续所有命令会永久重放同一异常直到重启。
            _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy));
            throw;
        }
    }

    /// <summary>
    /// 对全部已加载玩家执行一次脏 section 落盘；force 为 true 时忽略每段 15 秒最小脏龄门槛。
    /// </summary>
    /// <param name="force">是否强制写全部脏段；周期刷新传 false，进程退出传 true。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            var actor = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken);
            await actor.FlushAsync(force, cancellationToken);
        }
    }

    /// <summary>推进当前已加载玩家的确定性模拟；只在状态实际变化时向在线会话广播增量。</summary>
    public async ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            var actor = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken);
            var result = await actor.AdvanceSimulationAsync(target, cancellationToken);
            if (result is null) continue;
            // 模拟推进同样会撞上所有权失效：从缓存清除并继续推进其余玩家。
            if (result.Code == "PLAYER_OWNERSHIP_LOST")
            {
                EvictOwnershipLostActor(entry.Key, actor);
                continue;
            }
            // 搜寻完成进入匹配等待相位后由撮合器在本拍内完成挂接；弃战通知同样在锁外路由。
            try
            {
                await RouteDuelForfeitsAsync(actor, cancellationToken);
                await TryMatchDuelAsync(actor, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            await EmitLogBatchAsync(entry.Key, result.LogEvents);
            if (result.Simulation is not { Changed: true }) continue;
            var handler = SnapshotUpdated;
            if (handler is not null) await handler(result.Snapshot);
        }
    }

    /// <summary>全部已加载玩家中最早的可见事件边界；驱动宿主服务的动态唤醒节拍，无边界为 null。</summary>
    public DateTimeOffset? NextAdvanceBoundaryUtc
    {
        get
        {
            DateTimeOffset? best = null;
            foreach (var entry in _actors)
            {
                if (!entry.Value.IsValueCreated) continue;
                if (entry.Value.Value is not { IsCompletedSuccessfully: true } task) continue;
                var boundary = task.Result.NextVisibleBoundaryUtc;
                if (boundary is null) continue;
                if (best is null || boundary < best) best = boundary;
            }
            return best;
        }
    }

    /// <summary>
    /// 对单个已加载玩家执行一次脏 section 落盘；未加载时直接返回。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="force">是否强制写全部脏段（忽略 15 秒最小脏龄门槛）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        var actor = await AwaitActorAsync(playerId, lazy, cancellationToken);
        await actor.FlushAsync(force, cancellationToken);
    }

    /// <summary>
    /// 卸载玩家 Actor：可选先落离线状态并强刷，再移出缓存并释放信号量与所有权租约。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="flush">是否在卸载前把 presence=offline 与全部脏段强制落盘。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        var actor = await AwaitActorAsync(playerId, lazy, cancellationToken);
        // 卸载即离线：先落 presence=offline 再强刷，player_projection 才能筛出可冷挂机玩家。
        if (flush) await actor.MarkOfflineAndFlushAsync(cancellationToken);
        _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy));
        await actor.DisposeAsync();
    }

    /// <summary>宿主关闭路径：逐个卸载全部已加载玩家并强制落盘；单个玩家失败仅记录不中断整体关闭。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var playerId in _actors.Keys.ToArray())
        {
            try { await RemoveAsync(playerId, flush: true, CancellationToken.None); }
            catch (Exception exception) { logger.LogError(exception, "玩家运行时关闭时释放失败 player={PlayerId}", playerId); }
        }
    }

    private async Task<PlayerActor> CreateActorAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (ownership is not null && !await ownership.AcquireAsync(playerId, _ownerInstanceId, _ownershipLease, cancellationToken))
            throw new PlayerOwnershipLostException(playerId);
        try
        {
            var bundle = await repository.LoadAsync(playerId, cancellationToken)
                ?? throw new InvalidOperationException($"玩家 {playerId} 不存在。");
        PlayerAggregate aggregate;
        if (bundle.Head.StateVersion == 0 && bundle.Sections.Count == 0)
        {
            aggregate = PlayerAggregate.Create(
                new PlayerId(bundle.Identity.PlayerId),
                new AccountId(bundle.Identity.AccountId),
                bundle.Identity.DisplayName,
                bundle.Identity.CreatedAt,
                gameData.Fingerprint);
        }
        else
        {
            var state = SectionCodec.DecodeAggregate(bundle);
            aggregate = PlayerAggregate.Restore(state);
            var previousFingerprint = aggregate.GameDataFingerprint;
            if (aggregate.RebindGameDataFingerprint(gameData.Fingerprint))
            {
                logger.LogInformation(
                    "玩家存档已切换到当前游戏数据版本 player={PlayerId} previousFingerprint={PreviousFingerprint} currentFingerprint={CurrentFingerprint}",
                    playerId,
                    previousFingerprint,
                    gameData.Fingerprint);
            }
        }
            var balances = await assetRepository.LoadBalancesAsync(playerId, cancellationToken);
            var wallet = new AssetWallet(new PlayerId(playerId));
            wallet.Restore(balances.Select(balance => new AssetBalance(
                new PlayerId(balance.PlayerId),
                new AssetKey(new ItemId(balance.AssetId), balance.Variant),
                balance.Quantity,
                balance.Version)));
            var logBuffer = _logBuffers.GetOrAdd(playerId, _ => new PlayerLogBuffer(LogBufferCapacity));
            var actor = new PlayerActor(aggregate, bundle, wallet, assetRepository, repository, economy, _actions, gameData, logBuffer, logger, marketRuntime, commandReceipts, ownership, _ownerInstanceId, _ownershipLease, accounts, nodeBuildings);
            var advanced = await actor.AdvanceSimulationAsync(DateTimeOffset.UtcNow, cancellationToken);
            // 重连补算在 Presence=Offline 下完成，离线收益正确归因离线账本；补算结束后才转入在线。
            aggregate.SetPresence(PresenceState.Online);
            // 创建时的初始推进可能结算离线行动，产生的战报日志同样在锁外推送。
            await EmitLogBatchAsync(playerId, advanced?.LogEvents);
            return actor;
        }
        catch
        {
            if (ownership is not null) await ownership.ReleaseAsync(playerId, _ownerInstanceId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>所有权已转移到其他实例：把 actor 移出缓存并在锁外异步释放，
    /// 已解析到旧 actor 的并发命令会拿到明确的会话关闭错误，后续命令走重新加载。</summary>
    private void EvictOwnershipLostActor(Guid playerId, PlayerActor actor)
    {
        if (_actors.TryGetValue(playerId, out var lazy)
            && lazy.IsValueCreated
            && lazy.Value.Status == TaskStatus.RanToCompletion
            && ReferenceEquals(lazy.Value.Result, actor)
            && _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy)))
        {
            _ = ReleaseActorQuietlyAsync(playerId, actor);
        }
    }

    private async Task ReleaseActorQuietlyAsync(Guid playerId, PlayerActor actor)
    {
        try
        {
            await actor.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "所有权丢失后释放玩家 actor 失败 player={PlayerId}", playerId);
        }
    }

    private async ValueTask EmitLogBatchAsync(Guid playerId, IReadOnlyList<LogEventDto>? events)
    {
        if (events is not { Count: > 0 }) return;
        var handler = LogBatchEmitted;
        if (handler is not null) await handler(playerId, events);
    }

    /// <summary>读取玩家最近战报日志（用于重连回放）；缓冲在进程生命周期内保留。</summary>
    public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) =>
        _logBuffers.TryGetValue(playerId, out var buffer) ? buffer.SnapshotRecent() : [];

    /// <summary>
    /// 采集全部已加载玩家的只读快照，供本地运维面板展示。
    /// 单个玩家创建或读取失败仅跳过，不影响整体视图。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public async ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<PlayerRuntimeSnapshot>(_actors.Count);
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            PlayerActor actor;
            try { actor = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken); }
            catch { continue; }
            try { snapshots.Add(await actor.SnapshotAsync(cancellationToken)); }
            catch (Exception) { /* 只读诊断视图：单玩家读取失败跳过，不影响其余玩家展示。 */ }
        }
        return snapshots;
    }

    private static Guid? TryReadRequestId(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("requestId", out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var requestId)
            && requestId != Guid.Empty
            ? requestId
            : null;
    }

    /// <summary>
    /// 单玩家单写者执行单元：持有聚合、资产钱包与持久化 bundle，
    /// 命令执行、模拟推进与 section 落盘全部经 _gate 信号量串行；
    /// 对外只暴露异步方法，跨边界不泄露聚合引用。
    /// </summary>
    private sealed class PlayerActor : IAsyncDisposable
    {
        private readonly PlayerAggregate aggregate;
        private readonly PlayerStateBundle bundle;
        private readonly AssetWallet wallet;
        private readonly IAssetBalanceRepository assetRepository;
        private readonly IPlayerStateRepository repository;
        private readonly IEconomyTransactionStore economy;
        private readonly ActionCatalog actions;
        private readonly GameDataCatalog gameData;
        private readonly PlayerLogBuffer logBuffer;
        private readonly ILogger logger;
        private readonly MarketRuntime? marketRuntime;
        private readonly ICommandReceiptStore? commandReceipts;
        private readonly IPlayerOwnershipStore? ownership;
        private readonly IAccountRepository? accounts;
        private readonly NodeBuildingRegistry? nodeBuildings;
        private long _nodeBuildingSignature = long.MinValue;
        private readonly Guid ownerInstanceId;
        private readonly TimeSpan ownershipLease;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<(Guid DuelId, Guid PeerId)> _pendingDuelForfeits = [];
        private readonly Dictionary<string, DateTimeOffset> _dirtyAt = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, (string Command, string PayloadHash)> _recentRequests = new();
        private readonly Queue<Guid> _recentRequestOrder = new();
        private readonly List<CommandReceipt> _pendingReceipts = [];
        private readonly Dictionary<string, long> _sectionVersions;
        private readonly LogThrottle _walletErrorLog = new(TimeSpan.FromMinutes(1));
        private readonly LogThrottle _advanceErrorLog = new(TimeSpan.FromMinutes(1));
        private long _logicalVersion;
        // 0 表示无可见边界；由单写者锁内刷新、宿主调度线程无锁读取。
        private long _nextVisibleBoundaryUtcTicks;
        private bool _walletReloadRequired;
        private bool _disposed;
        private int _disposeStarted;

        /// <summary>下一个用户可见事件边界的墙钟时刻；无边界（空闲/斗法等待/受阻）为 null。</summary>
        public DateTimeOffset? NextVisibleBoundaryUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _nextVisibleBoundaryUtcTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        /// <summary>
        /// 初始化 PlayerActor 的新实例。
        /// </summary>
        /// <param name="aggregate">已恢复/新建的玩家聚合。</param>
        /// <param name="bundle">持久化层加载的状态包（提交头 + section 集合 + 身份）。</param>
        /// <param name="wallet">从资产余额表重建的玩家钱包。</param>
        /// <param name="assetRepository">资产余额仓储，用于钱包刷新。</param>
        /// <param name="repository">玩家状态仓储，用于 section 落盘队列。</param>
        /// <param name="economy">经济事务存储，资产增量随模拟/命令原子提交。</param>
        /// <param name="actions">行动定义目录（来自激活配表）。</param>
        /// <param name="gameData">当前激活的游戏数据目录。</param>
        /// <param name="logBuffer">战报日志环形缓冲（重连回放用）。</param>
        /// <param name="logger">日志记录器。</param>
        /// <param name="marketRuntime">市场运行时；未装配时市场命令返回 MARKET_UNAVAILABLE。</param>
        /// <param name="commandReceipts">命令回执持久化存储；为 null 时跨重启幂等去重不生效。</param>
        /// <param name="ownership">玩家所有权租约存储；为 null 时跳过单实例所有权校验。</param>
        /// <param name="ownerInstanceId">本进程实例标识，写入所有权租约。</param>
        /// <param name="ownershipLease">所有权租约时长，随每次命令/刷盘续期。</param>
        /// <param name="accounts">账号仓储，用于改名查重。</param>
        public PlayerActor(
            PlayerAggregate aggregate,
            PlayerStateBundle bundle,
            AssetWallet wallet,
            IAssetBalanceRepository assetRepository,
            IPlayerStateRepository repository,
            IEconomyTransactionStore economy,
            ActionCatalog actions,
            GameDataCatalog gameData,
            PlayerLogBuffer logBuffer,
            ILogger logger,
            MarketRuntime? marketRuntime,
            ICommandReceiptStore? commandReceipts,
            IPlayerOwnershipStore? ownership,
            Guid ownerInstanceId,
            TimeSpan ownershipLease,
            IAccountRepository? accounts = null,
            NodeBuildingRegistry? nodeBuildings = null)
        {
            this.aggregate = aggregate;
            this.bundle = bundle;
            this.wallet = wallet;
            this.assetRepository = assetRepository;
            this.repository = repository;
            this.economy = economy;
            this.actions = actions;
            this.gameData = gameData;
            this.logBuffer = logBuffer;
            this.logger = logger;
            this.marketRuntime = marketRuntime;
            this.commandReceipts = commandReceipts;
            this.ownership = ownership;
            this.accounts = accounts;
            this.nodeBuildings = nodeBuildings;
            this.ownerInstanceId = ownerInstanceId;
            this.ownershipLease = ownershipLease;
            _sectionVersions = bundle.Sections.ToDictionary(x => x.Key, x => x.Value.StateVersion, StringComparer.Ordinal);
            _logicalVersion = Math.Max(0, aggregate.StateVersion);
            foreach (var section in aggregate.DirtySections) _dirtyAt[SectionCodec.ToStorageKind(section)] = DateTimeOffset.UtcNow;
        }

        /// <summary>加锁读取快照；仅供未持有单写者锁的外部路径调用，锁内路径必须使用无锁 Snapshot()。</summary>
        public async ValueTask<PlayerRuntimeSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return Snapshot(); }
            finally { _gate.Release(); }
        }

        /// <summary>无锁快照投影；调用方必须已持有单写者锁。</summary>
        private PlayerRuntimeSnapshot Snapshot() => new(
            aggregate.PlayerId.Value,
            aggregate.AccountId.Value.ToString("D"),
            aggregate.DisplayName,
            Math.Max(_logicalVersion, aggregate.StateVersion),
            aggregate.LastSimulatedAt,
            aggregate.ExportState() with { StateVersion = Math.Max(_logicalVersion, aggregate.StateVersion) },
            BuildStoredActions(),
            wallet.Balances
                .OrderBy(balance => balance.Asset.ItemId.Value, StringComparer.Ordinal)
                .ThenBy(balance => balance.Asset.Variant, StringComparer.Ordinal)
                .Select(balance => new AssetBalanceRecord(
                    balance.PlayerId.Value,
                    balance.Asset.ItemId.Value,
                    balance.Asset.Variant,
                    balance.Quantity,
                    balance.Version))
                .ToArray());

        /// <summary>
        /// 在单写者锁内执行一条领域命令：先续租所有权 → requestId 幂等去重（内存窗口 +
        /// 持久化回执双层）→ 期望版本校验 → 命令分发 → 登记回执与待落盘 receipt。
        /// 业务与协议错误统一收敛为 PlayerCommandResult，不会抛出到调用方。
        /// </summary>
        /// <param name="command">命令名称（如 action.enqueue、market.place）。</param>
        /// <param name="payload">命令 JSON 载荷。</param>
        /// <param name="expectedStateVersion">期望的状态版本；大于 0 时校验，不等即返回 STATE_VERSION_CONFLICT。</param>
        /// <param name="requestId">可选幂等键；提供时必须与载荷内 requestId 一致。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>命令回执：成功标志、错误码、执行后快照与战报日志。</returns>
        public async ValueTask<PlayerCommandResult> ExecuteAsync(string command, JsonElement payload, long expectedStateVersion, Guid? requestId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PlayerActor));
                if (ownership is not null && !await ownership.RenewAsync(aggregate.PlayerId.Value, ownerInstanceId, ownershipLease, cancellationToken))
                    throw new PlayerOwnershipLostException(aggregate.PlayerId.Value);
                if (string.IsNullOrWhiteSpace(command) || command.Length > 96)
                    return Fail("INVALID_COMMAND", "命令名称无效。");
                if (requestId.HasValue)
                {
                    if (payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("requestId", out var requestElement)
                        || requestElement.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(requestElement.GetString(), out var payloadRequestId)
                        || payloadRequestId != requestId.Value)
                        return Fail("REQUEST_ID_MISMATCH", "命令载荷中的请求 ID 无效或不一致。");
                }
                var payloadHash = requestId.HasValue ? ComputePayloadHash(payload) : string.Empty;
                if (requestId.HasValue && _recentRequests.TryGetValue(requestId.Value, out var previous))
                {
                    if (!string.Equals(previous.Command, command, StringComparison.Ordinal) || !string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                        return Fail("REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。");
                    return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", Snapshot());
                }
                if (requestId.HasValue && commandReceipts is not null && ShouldPersistReceipt(command))
                {
                    var persisted = await commandReceipts.FindAsync(aggregate.PlayerId.Value, requestId.Value, cancellationToken);
                    if (persisted is not null)
                    {
                        if (!string.Equals(persisted.Command, command, StringComparison.Ordinal)
                            || !string.Equals(persisted.PayloadHash, payloadHash, StringComparison.Ordinal))
                            return Fail("REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。");
                        RememberRequest(requestId.Value, command, payloadHash);
                        return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", Snapshot());
                    }
                }
                if (expectedStateVersion > 0 && Math.Max(_logicalVersion, aggregate.StateVersion) != expectedStateVersion)
                    return Fail("STATE_VERSION_CONFLICT", "玩家状态版本已变化。");
                SimulationResult? simulation = null;
                switch (command)
                {
                    case "action.enqueue":
                    {
                        var request = JsonSerializer.Deserialize<ActionIntentRequest>(payload.GetRawText(), ProtocolJson.Options)
                            ?? throw new JsonException("行动请求为空。");
                        if (!Guid.TryParse(request.RequestId, out _)) return Fail("INVALID_REQUEST_ID", "请求 ID 无效。");
                        var intent = new ActionIntent(new ActionId(request.ActionId), request.RepeatCount, request.TargetNodeId, request.Parameters, request.PvpEnabled);
                        var actionDefinition = actions.Get(intent.ActionId);
                        if (actionDefinition.AllowedNodeIds is { Count: > 0 }
                            && (aggregate.World.EdgeId is not null || !actionDefinition.AllowedNodeIds.Contains(aggregate.World.NodeId)))
                            return Fail("WRONG_LOCATION", "当前位置不允许执行该行动。");
                        aggregate.Enqueue(intent, DateTimeOffset.UtcNow, actions);
                        Touch();
                        break;
                    }
                    case "action.remove":
                    {
                        if (!payload.TryGetProperty("actionInstanceId", out var id) || !Guid.TryParse(id.GetString(), out var actionId)) return Fail("INVALID_ACTION_ID", "行动实例 ID 无效。");
                        if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionId)
                        {
                            var now = DateTimeOffset.UtcNow;
                            if (now > aggregate.LastSimulatedAt)
                            {
                                var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                                if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                            }
                        }
                        // 移除队首斗法中的行动即弃战判负；通知在锁外路由给对手。
                        if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionId)
                            TryForfeitActiveDuel();
                        if (!aggregate.RemoveAction(actionId)) return Fail("ACTION_NOT_FOUND", "行动不存在。");
                        Touch();
                        break;
                    }
                    case "queue.configure":
                    {
                        if (!payload.TryGetProperty("loopEnabled", out var loopEnabled) || loopEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return Fail("INVALID_PAYLOAD", "循环设置无效。");
                        aggregate.SetLoopEnabled(loopEnabled.GetBoolean());
                        TouchDirtyOnly();
                        break;
                    }
                    case "queue.move":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionInstanceId)
                            || !payload.TryGetProperty("direction", out var directionElement)
                            || !directionElement.TryGetInt32(out var direction))
                            return Fail("INVALID_PAYLOAD", "行动移动参数无效。");
                        var now = DateTimeOffset.UtcNow;
                        if (now > aggregate.LastSimulatedAt)
                        {
                            var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                            if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                        }
                        // 把斗法中的队首行动挪下首位等于结束当前斗法行动，按弃战判负。
                        if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionInstanceId)
                            TryForfeitActiveDuel();
                        if (!aggregate.MoveAction(actionInstanceId, direction)) return Fail("ACTION_NOT_FOUND", "行动不存在或已在边界位置。");
                        TouchDirtyOnly();
                        break;
                    }
                    case "queue.auto_use.configure":
                    {
                        var actionType = payload.TryGetProperty("actionType", out var actionTypeElement) ? actionTypeElement.GetString() : null;
                        var slotIndex = payload.TryGetProperty("slotIndex", out var slotIndexElement) && slotIndexElement.TryGetInt32(out var parsedSlot) ? parsedSlot : -1;
                        var itemId = payload.TryGetProperty("itemId", out var itemElement) && itemElement.ValueKind != JsonValueKind.Null ? itemElement.GetString() : null;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : 1d;
                        var enabled = payload.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
                        var group = payload.TryGetProperty("group", out var groupElement) ? groupElement.GetString() : null;
                        if (!Enum.TryParse<ActionKind>(actionType, true, out var slotKind))
                            return Fail("INVALID_ACTION_TYPE", "自动使用行动类型无效。");
                        ItemDefinition? slotItem = null;
                        if (itemId is not null && (!gameData.Items.TryGetValue(new ItemId(itemId), out slotItem)
                            || slotItem.Consumable is null || !slotItem.Consumable.UsableInActionKinds.Contains(slotKind)))
                            return Fail("INVALID_CONSUMABLE", "物品不适用于该自动使用槽。");
                        // 战斗域药品栏仅接受药品类即时消耗品；通用栏不限物品分类（符咒/阵法/特殊道具/药品皆可）。
                        if (group == AutoUseSlotState.SlotGroupPotion
                            && (slotKind != ActionKind.Combat
                                || itemId is not null && (slotItem!.Category != ConsumableDefinition.CategoryPill
                                    || slotItem.Consumable!.IsContinuous)))
                            return Fail("INVALID_CONSUMABLE", "药品栏只能装配药品类即时消耗品。");
                        aggregate.ConfigureAutoUse(actionType ?? string.Empty, slotIndex, itemId, quantity, enabled, group);
                        TouchDirtyOnly();
                        break;
                    }
                    case "loadout.replace":
                    {
                        LoadoutCommand.Apply(payload, aggregate, wallet, gameData);
                        TouchDirtyOnly();
                        break;
                    }
                    case "ability.learn":
                    {
                        if (!requestId.HasValue) return Fail("INVALID_REQUEST_ID", "学习功法必须提供请求 ID。");
                        var itemId = payload.TryGetProperty("itemId", out var manualElement) && manualElement.ValueKind == JsonValueKind.String
                            ? manualElement.GetString() : null;
                        var isTome = itemId?.StartsWith("item.tome.", StringComparison.Ordinal) == true;
                        var isFragment = itemId?.StartsWith("item.fragment.", StringComparison.Ordinal) == true;
                        if (itemId is null || (!isTome && !isFragment) || !gameData.RawItems.ContainsKey(itemId))
                            return Fail("INVALID_MANUAL", "请选择完整功法秘籍或残卷。");
                        var prefix = isTome ? "item.tome." : "item.fragment.";
                        var abilityId = new AbilityId("ability." + itemId[prefix.Length..]);
                        if (!gameData.Abilities.ContainsKey(abilityId.Value)) return Fail("INVALID_ABILITY", "秘籍对应功法不存在。");
                        // 已习得秘籍复读 +10000 技能经验；残卷 +500 且要求已习得。
                        var alreadyLearned = aggregate.Loadout.LearnedAbilities.Contains(abilityId);
                        if (!isTome && !alreadyLearned)
                            return Fail("ABILITY_NOT_LEARNED", "必须先习得对应功法才能研读残卷。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        var before = aggregate.ExportState();
                        var walletBefore = wallet.Balances.ToArray();
                        var dirtyBefore = aggregate.DirtySections.ToArray();
                        var dirtyAtBefore = _dirtyAt.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        var logicalBefore = _logicalVersion;
                        var committed = false;
                        try
                        {
                            if (alreadyLearned)
                                aggregate.GrantAbilityExperience(abilityId, isTome ? 10_000 : 500);
                            else
                                aggregate.LearnAbility(abilityId);
                            var now = DateTimeOffset.UtcNow;
                            var version = Math.Max(_logicalVersion + 1, aggregate.StateVersion);
                            var writes = SectionCodec.EncodeAggregate(aggregate, now, version);
                            var receipt = new CommandReceipt(aggregate.PlayerId.Value, requestId.Value, command, payloadHash, version, now, now.AddMinutes(30));
                            var result = await economy.CommitAsync(new EconomyTransactionRequest(
                                requestId.Value, $"ability-learn:{aggregate.PlayerId.Value:D}:{requestId.Value:D}", command,
                                aggregate.PlayerId.Value, [new(aggregate.PlayerId.Value, itemId, string.Empty, -1)], now,
                                JsonSerializer.SerializeToUtf8Bytes(new { abilityId = abilityId.Value }), writes, [receipt]), cancellationToken);
                            if (!result.Applied) throw new InvalidOperationException("秘籍学习事务未提交。");
                            committed = true;
                            _logicalVersion = version;
                            foreach (var write in writes)
                            {
                                _sectionVersions[write.SectionKind] = write.StateVersion;
                                _dirtyAt.Remove(write.SectionKind);
                            }
                            await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        }
                        catch
                        {
                            if (!committed) RestoreSimulationState(before, walletBefore, dirtyBefore, dirtyAtBefore, logicalBefore);
                            throw;
                        }
                        break;
                    }
                    case "progression.breakthrough":
                    {
                        aggregate.Breakthrough(gameData.RealmProgression);
                        TouchDirtyOnly();
                        break;
                    }
                    case "world.travel":
                    {
                        var targetNodeId = payload.TryGetProperty("targetNodeId", out var targetNode) ? targetNode.GetString() : null;
                        if (string.IsNullOrWhiteSpace(targetNodeId))
                            return Fail("INVALID_TARGET", "目标世界节点无效。");
                        targetNodeId = targetNodeId.Trim();

                        var worldGraph = gameData.WorldGraph;
                        if (!worldGraph.ContainsNode(targetNodeId))
                            return Fail("NODE_NOT_FOUND", $"目标世界节点不存在：{targetNodeId}。");

                        // 在入队移动前，先将玩家当前进度推进到 UtcNow，确保原行动已流逝的时间不被移动行动挪用
                        var now = DateTimeOffset.UtcNow;
                        if (now > aggregate.LastSimulatedAt)
                        {
                            var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                            if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                        }

                        // 若已经在目标节点且未处于边上，直接成功，并清除任何残留的同类移动行动
                        if (string.Equals(aggregate.World.NodeId, targetNodeId, StringComparison.Ordinal)
                            && string.IsNullOrWhiteSpace(aggregate.World.EdgeId))
                        {
                            for (var i = 0; i < aggregate.Actions.Count; i++)
                            {
                                if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                {
                                    aggregate.RemoveAction(aggregate.Actions[i].Id);
                                    break;
                                }
                            }
                            TouchDirtyOnly();
                            break;
                        }

                        // 从当前位置（包含处于某条边中间的情况）规划到达 targetNodeId 的最短路径
                        var plan = worldGraph.PlanFromPosition(
                            aggregate.World.EdgeId,
                            aggregate.World.EdgeProgress,
                            aggregate.World.NodeId,
                            targetNodeId);

                        if (plan.Segments.Count == 0 && plan.TotalTravelSeconds <= 0)
                        {
                            for (var i = 0; i < aggregate.Actions.Count; i++)
                            {
                                if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                {
                                    aggregate.RemoveAction(aggregate.Actions[i].Id);
                                    break;
                                }
                            }
                            if (worldGraph.TryGetNode(targetNodeId, out var targetNodeObj))
                            {
                                aggregate.TravelTo(targetNodeId, targetNodeObj.X, targetNodeObj.Y);
                            }
                            TouchDirtyOnly();
                            break;
                        }

                        var travelState = new TravelActionState(
                            targetNodeId,
                            plan.TotalTravelSeconds,
                            plan.EdgeIds,
                            plan.Segments);

                        // 移动行动插队到队首会打断进行中的斗法，按弃战判负。
                        TryForfeitActiveDuel();
                        var travelEnqueuedAt = aggregate.LastSimulatedAt > now ? aggregate.LastSimulatedAt : now;
                        var travelIntent = new ActionIntent(new ActionId("world.travel"), 1, targetNodeId);
                        aggregate.EnqueuePriority(travelIntent, travelEnqueuedAt, actions, travelState);
                        Touch();
                        break;
                    }
                    case "building.build":
                    {
                        if (nodeBuildings is null) return Fail("BUILDING_UNAVAILABLE", "建筑服务未装配。");
                        var buildingId = payload.TryGetProperty("buildingId", out var buildingElement) && buildingElement.ValueKind == JsonValueKind.String
                            ? buildingElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.TryGetValue(buildingId, out var buildingDefinition))
                            return Fail("BUILDING_NOT_FOUND", "建筑定义不存在。");
                        if (aggregate.World.EdgeId is not null)
                            return Fail("WRONG_LOCATION", "旅途中无法建造建筑。");
                        if (buildingDefinition.AvailableNodeIds is { Count: > 0 } && !buildingDefinition.AvailableNodeIds.Contains(aggregate.World.NodeId))
                            return Fail("WRONG_LOCATION", "此地无法建造该建筑。");
                        if (aggregate.Progression.RealmLevel < buildingDefinition.RequiredRealmLevel)
                            return Fail("BUILDING_LOCKED", "境界不足，尚未解锁该建筑。");
                        var buildNow = DateTimeOffset.UtcNow;
                        var buildResult = await nodeBuildings.StartConstructionAsync(aggregate.World.NodeId, buildingDefinition, buildNow, cancellationToken);
                        if (!buildResult.Success) return Fail(buildResult.ErrorCode ?? "BUILD_FAILED", buildResult.Message ?? "建造发起失败。");
                        await RefreshNodeBuildingsAsync(buildNow, cancellationToken);
                        TouchDirtyOnly();
                        break;
                    }
                    case "building.upgrade":
                    {
                        if (nodeBuildings is null) return Fail("BUILDING_UNAVAILABLE", "建筑服务未装配。");
                        var buildingId = payload.TryGetProperty("buildingId", out var upgradeElement) && upgradeElement.ValueKind == JsonValueKind.String
                            ? upgradeElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.ContainsKey(buildingId))
                            return Fail("BUILDING_NOT_FOUND", "建筑定义不存在。");
                        if (aggregate.World.EdgeId is not null)
                            return Fail("WRONG_LOCATION", "旅途中无法升级建筑。");
                        var upgradeNow = DateTimeOffset.UtcNow;
                        var upgradeResult = await nodeBuildings.StartUpgradeAsync(aggregate.World.NodeId, buildingId, upgradeNow, cancellationToken);
                        if (!upgradeResult.Success) return Fail(upgradeResult.ErrorCode ?? "UPGRADE_FAILED", upgradeResult.Message ?? "升级发起失败。");
                        await RefreshNodeBuildingsAsync(upgradeNow, cancellationToken);
                        TouchDirtyOnly();
                        break;
                    }
                    case "building.contribute":
                    {
                        if (nodeBuildings is null) return Fail("BUILDING_UNAVAILABLE", "建筑服务未装配。");
                        var buildingId = payload.TryGetProperty("buildingId", out var contributeElement) && contributeElement.ValueKind == JsonValueKind.String
                            ? contributeElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.ContainsKey(buildingId))
                            return Fail("BUILDING_NOT_FOUND", "建筑定义不存在。");
                        if (aggregate.World.EdgeId is not null)
                            return Fail("WRONG_LOCATION", "旅途中无法提交建筑材料。");
                        var requested = new Dictionary<string, double>(StringComparer.Ordinal);
                        if (payload.TryGetProperty("materials", out var materialsElement) && materialsElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in materialsElement.EnumerateObject())
                            {
                                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out var quantity)
                                    && double.IsFinite(quantity) && quantity > 0)
                                    requested[entry.Name] = quantity;
                            }
                        }
                        if (requested.Count == 0) return Fail("INVALID_MATERIALS", "提交材料为空。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        var contributeResult = await nodeBuildings.ContributeAsync(
                            aggregate.World.NodeId,
                            buildingId,
                            requested,
                            async applied =>
                            {
                                // 注册表锁内先校验背包余额再原子提交扣减事务，避免并发贡献把材料压成负数。
                                foreach (var (itemId, quantity) in applied)
                                {
                                    if (wallet.Get(new AssetKey(new ItemId(itemId), string.Empty)) + 1e-9 < quantity)
                                        return false;
                                }
                                var debitNow = DateTimeOffset.UtcNow;
                                var transactionId = requestId ?? Guid.NewGuid();
                                var committed = await economy.CommitAsync(new EconomyTransactionRequest(
                                    transactionId,
                                    $"building-contribute:{aggregate.PlayerId.Value:D}:{transactionId:D}",
                                    command,
                                    aggregate.PlayerId.Value,
                                    applied.Select(pair => new AssetDelta(aggregate.PlayerId.Value, pair.Key, string.Empty, -pair.Value)).ToArray(),
                                    debitNow,
                                    JsonSerializer.SerializeToUtf8Bytes(new { nodeId = aggregate.World.NodeId, buildingId })), cancellationToken);
                                return committed.Applied;
                            },
                            cancellationToken);
                        if (!contributeResult.Success)
                            return Fail(contributeResult.ErrorCode ?? "CONTRIBUTE_FAILED", contributeResult.Message ?? "材料提交失败。");
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        await RefreshNodeBuildingsAsync(DateTimeOffset.UtcNow, cancellationToken);
                        TouchDirtyOnly();
                        break;
                    }
                    case "market.place":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        if (!TryReadGuid(payload, "orderId", out var orderId)) return Fail("INVALID_ORDER", "订单标识无效。");
                        var assetId = payload.TryGetProperty("itemId", out var itemElement) ? itemElement.GetString() : null;
                        var sideText = payload.TryGetProperty("side", out var sideElement) ? sideElement.GetString() : null;
                        var variant = payload.TryGetProperty("variant", out var variantElement) && variantElement.ValueKind != JsonValueKind.Null ? variantElement.GetString() ?? string.Empty : string.Empty;
                        var price = payload.TryGetProperty("price", out var priceElement) && priceElement.TryGetDouble(out var parsedPrice) ? parsedPrice : double.NaN;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : double.NaN;
                        if (string.IsNullOrWhiteSpace(assetId) || !Enum.TryParse<MarketSide>(sideText, true, out var side)) return Fail("INVALID_ORDER", "订单品种或方向无效。");
                        await marketRuntime.PlaceAsync(new PlaceOrderRequest(new OrderId(orderId), aggregate.PlayerId, new MarketInstrument(new ItemId(assetId), variant), side, price, quantity, DateTimeOffset.UtcNow, new IdempotencyKey($"market:{aggregate.PlayerId.Value:D}:{orderId:D}")), cancellationToken);
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        break;
                    }
                    case "market.cancel":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        if (!TryReadGuid(payload, "orderId", out var orderId)) return Fail("INVALID_ORDER", "订单标识无效。");
                        var order = await marketRuntime.GetOrderAsync(orderId, aggregate.PlayerId, cancellationToken);
                        if (order is null) return Fail("ORDER_NOT_FOUND", "订单不存在。");
                        await marketRuntime.CancelAsync(new OrderId(orderId), aggregate.PlayerId, order.Instrument, $"market-cancel:{aggregate.PlayerId.Value:D}:{orderId:D}", cancellationToken);
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        break;
                    }
                    case "market.claim":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        if (marketRuntime.PartitionCount != 1) return Fail("MARKET_CLAIM_UNAVAILABLE", "多市场分片尚未提供跨分片结算领取路由。");
                        if (!requestId.HasValue) return Fail("INVALID_REQUEST_ID", "领取结算必须提供请求 ID。");
                        await marketRuntime.ClaimSettlementAsync(aggregate.PlayerId, requestId.Value, cancellationToken);
                        await ReloadWalletAsync(cancellationToken);
                        break;
                    }
                    case "item.discard":
                    case "item.sell":
                    {
                        // 物品处置（丢弃/系统回收）只动钱包资产：校验定义与持有量后，
                        // 由经济事务原子扣减；回收入灵石在同一事务内贷记。
                        if (!requestId.HasValue) return Fail("INVALID_REQUEST_ID", "物品处置必须提供请求 ID。");
                        var disposeItemId = payload.TryGetProperty("itemId", out var disposeItemElement)
                            && disposeItemElement.ValueKind == JsonValueKind.String
                            ? disposeItemElement.GetString() : null;
                        var disposeQuantity = payload.TryGetProperty("quantity", out var disposeQuantityElement)
                            && disposeQuantityElement.TryGetDouble(out var parsedDisposeQuantity)
                            ? parsedDisposeQuantity : double.NaN;
                        if (string.IsNullOrWhiteSpace(disposeItemId) || !double.IsFinite(disposeQuantity) || disposeQuantity <= 0)
                            return Fail("INVALID_PAYLOAD", "物品或数量无效。");
                        var hasItemDefinition = gameData.Items.TryGetValue(new ItemId(disposeItemId), out var disposeDefinition);
                        if (!hasItemDefinition && !gameData.RawEquipments.ContainsKey(disposeItemId))
                            return Fail("ITEM_NOT_FOUND", "物品不存在。");
                        if (aggregate.Loadout.Equipment.Any(slot => slot.ItemId == disposeItemId))
                            return Fail("ITEM_EQUIPPED", "已装备的物品无法处置，请先卸下。");
                        var spiritCredit = 0d;
                        if (command == "item.sell")
                        {
                            // 系统回收只认配表 sellPrice 正数的普通物品；装备无回收价，货币不可回收换货币。
                            if (!hasItemDefinition || disposeDefinition!.SellPrice <= 0
                                || disposeItemId.StartsWith("currency.", StringComparison.Ordinal))
                                return Fail("ITEM_NOT_SELLABLE", "该物品不支持系统回收。");
                            spiritCredit = disposeDefinition.SellPrice * disposeQuantity;
                        }
                        await EnsureWalletFreshAsync(cancellationToken);
                        if (wallet.Get(new AssetKey(new ItemId(disposeItemId))) + 1e-9 < disposeQuantity)
                            return Fail("INSUFFICIENT_ITEM", "物品数量不足。");
                        var disposeNow = DateTimeOffset.UtcNow;
                        var disposeDeltas = new List<AssetDelta>
                        {
                            new(aggregate.PlayerId.Value, disposeItemId, string.Empty, -disposeQuantity)
                        };
                        if (spiritCredit > 0)
                            disposeDeltas.Add(new(aggregate.PlayerId.Value, "currency.spirit-stone", string.Empty, spiritCredit));
                        var disposeResult = await economy.CommitAsync(new EconomyTransactionRequest(
                            requestId.Value,
                            $"{command}:{aggregate.PlayerId.Value:D}:{requestId.Value:D}",
                            command,
                            aggregate.PlayerId.Value,
                            disposeDeltas,
                            disposeNow,
                            JsonSerializer.SerializeToUtf8Bytes(new { itemId = disposeItemId, quantity = disposeQuantity }),
                            Receipts: [new CommandReceipt(aggregate.PlayerId.Value, requestId.Value, command, payloadHash,
                                Math.Max(_logicalVersion, aggregate.StateVersion), disposeNow, disposeNow.AddMinutes(30))]),
                            cancellationToken);
                        if (!disposeResult.Applied) return Fail("TRANSACTION_REJECTED", "物品处置事务未提交。");
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        break;
                    }
                    case "player.rename":
                    {
                        var name = payload.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
                        var now = DateTimeOffset.UtcNow;
                        // 先做无副作用的格式与冷却校验，再由唯一索引原子查重，最后才落聚合。
                        aggregate.EnsureCanRename(name ?? string.Empty, now);
                        if (accounts is not null
                            && !string.Equals(aggregate.DisplayName, name?.Trim(), StringComparison.Ordinal)
                            && !await accounts.RenamePlayerAsync(aggregate.PlayerId.Value, name!.Trim(), cancellationToken))
                            return Fail("NAME_TAKEN", "该道号已被占用。");
                        aggregate.Rename(name ?? string.Empty, now);
                        Touch();
                        break;
                    }
                    case "simulation.advance":
                    {
                        var target = payload.TryGetProperty("targetTime", out var targetElement) && targetElement.TryGetDateTimeOffset(out var parsed)
                            ? parsed : DateTimeOffset.UtcNow;
                        simulation = await AdvanceSimulationInternalAsync(target, cancellationToken);
                        break;
                    }
                    case "player.presence":
                    {
                        var value = payload.TryGetProperty("presence", out var presence) ? presence.GetString() : null;
                        aggregate.SetPresence(value?.ToLowerInvariant() switch
                        {
                            "online" => PresenceState.Online,
                            "suspended" => PresenceState.Suspended,
                            _ => PresenceState.Offline
                        });
                        Touch();
                        break;
                    }
                    default:
                        return Fail("UNKNOWN_COMMAND", "未知领域命令。");
                }

                if (requestId.HasValue)
                {
                    RememberRequest(requestId.Value, command, payloadHash);
                    if (ShouldPersistReceipt(command))
                    {
                        var createdAt = DateTimeOffset.UtcNow;
                        _pendingReceipts.Add(new CommandReceipt(
                            aggregate.PlayerId.Value,
                            requestId.Value,
                            command,
                            payloadHash,
                            Math.Max(_logicalVersion, aggregate.StateVersion),
                            createdAt,
                            createdAt.AddMinutes(30)));
                    }
                }
                // 锁内只完成模板映射与缓冲写入；网络推送由外层在锁外触发。
                IReadOnlyList<LogEventDto>? logEvents = null;
                if (simulation is { Events.Count: > 0 })
                    logEvents = SimulationLogMapper.ToLogEvents(simulation.Events, gameData, logBuffer);
                RefreshNextVisibleBoundary();
                return new PlayerCommandResult(true, "OK", "命令已提交。", Snapshot(), simulation, logEvents);
            }
            catch (DomainException ex)
            {
                return Fail(ex.Code.ToString().ToUpperInvariant(), ex.Message);
            }
            catch (EconomyInsufficientBalanceException)
            {
                return Fail("INSUFFICIENT_BALANCE", "资产余额不足。");
            }
            catch (MarketLeaseLostException)
            {
                return Fail("MARKET_LEASE_LOST", "市场分片写者租约已失效。");
            }
            catch (PlayerOwnershipLostException)
            {
                // 所有权已丢失后无法在本实例恢复：返回明确错误，缓存清理由运行时在
                // 命令返回后、锁外执行（见 ExecuteCoreAsync），不在锁内触发释放或标记销毁。
                return Fail("PLAYER_OWNERSHIP_LOST", "玩家当前由其他服务实例持有，请稍后重试。");
            }
            catch (JsonException ex)
            {
                return Fail("INVALID_PAYLOAD", ex.Message);
            }
            finally { _gate.Release(); }
        }

        private async ValueTask<SimulationResult> AdvanceSimulationInternalAsync(DateTimeOffset target, CancellationToken cancellationToken)
        {
            await EnsureWalletFreshAsync(cancellationToken);
            await RefreshNodeBuildingsAsync(target, cancellationToken);
            var before = aggregate.ExportState();
            var walletBefore = wallet.Balances.ToArray();
            var dirtyBefore = aggregate.DirtySections.ToArray();
            var dirtyAtBefore = _dirtyAt.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var logicalVersionBefore = _logicalVersion;
            var economyCommitted = false;
            try
            {
                // 战斗遭遇与持续消耗品都需要完整配表；钱包仅作开封库存判断，实际扣减仍随 AssetDeltas 原子提交。
                // 在线推进路径启用斗法撮合；离线补算（ComputeNode 等）不传该选项以保持确定性。
                var result = SimulationEngine.AdvanceTo(aggregate, target, gameData, wallet, new DuelAdvanceOptions(MatchingEnabled: true));
                if (result.AssetDeltas.Count > 0)
                {
                    var transaction = new EconomyTransactionRequest(
                        Guid.NewGuid(),
                        $"simulation:{aggregate.PlayerId.Value:D}:{result.StateVersion}:{result.To.ToUnixTimeMilliseconds()}",
                        "simulation.reward",
                        aggregate.PlayerId.Value,
                        result.AssetDeltas.Select(delta => new Server.Clean.AssetDelta(
                            delta.PlayerId.Value,
                            delta.Asset.ItemId.Value,
                            delta.Asset.Variant,
                            delta.QuantityDelta)).ToArray(),
                        result.To,
                        JsonSerializer.SerializeToUtf8Bytes(new { result.StateVersion, result.To }));
                    var committed = await economy.CommitAsync(transaction, cancellationToken);
                    if (!committed.Applied)
                        throw new InvalidOperationException("模拟资产事务未提交。");
                    economyCommitted = true;
                    try
                    {
                        await ReloadWalletAsync(cancellationToken);
                    }
                    catch (Exception reloadException)
                    {
                        // 资产事务已 durable commit，不能回滚 LastSimulatedAt，否则下一次目标时间
                        // 会把同一段奖励再次结算。保留聚合进度，标记钱包待刷新；后续资产命令
                        // 会先强制从 PostgreSQL 读取，期间尽量用本地 delta 保持界面可见值。
                        _walletReloadRequired = true;
                        foreach (var delta in result.AssetDeltas)
                        {
                            try { wallet.Apply(delta); }
                            catch (Exception applyException)
                            {
                                if (_walletErrorLog.TryAcquire())
                                    logger.LogError(applyException, "模拟资产已提交但本地钱包无法应用 delta，等待下次刷新 player={PlayerId}", aggregate.PlayerId.Value);
                                break;
                            }
                        }
                        if (reloadException is not OperationCanceledException)
                        {
                            if (_walletErrorLog.TryAcquire())
                                logger.LogError(reloadException, "模拟资产已提交但钱包刷新失败 player={PlayerId}", aggregate.PlayerId.Value);
                        }
                        if (reloadException is OperationCanceledException) throw;
                    }
                }
                _logicalVersion = Math.Max(_logicalVersion, aggregate.StateVersion);
                TouchDirtyOnly();
                return result;
            }
            catch
            {
                if (!economyCommitted)
                {
                    RestoreSimulationState(before, walletBefore, dirtyBefore, dirtyAtBefore, logicalVersionBefore);
                }
                throw;
            }
        }

        /// <summary>
        /// 把该玩家确定性模拟推进到 target；内部复用 simulation.advance 命令路径以保持同一锁与结算语义。
        /// </summary>
        /// <param name="target">目标模拟时刻（通常为 UtcNow）。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>推进成功时返回含 Simulation 的命令回执；推进失败（无 Simulation）时返回 null 并节流记录错误。</returns>
        public async ValueTask<PlayerCommandResult?> AdvanceSimulationAsync(DateTimeOffset target, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToElement(new { targetTime = target }, ServerJson.Options);
            var result = await ExecuteAsync("simulation.advance", payload, 0, null, cancellationToken);
            if (result.Simulation is null)
            {
                // 推进失败时模拟时钟无法越过边界：清空缓存边界让该玩家退化为 1 秒兜底节拍，
                // 避免过期边界把宿主循环拖进亚秒级空转重试。
                Interlocked.Exchange(ref _nextVisibleBoundaryUtcTicks, 0);
                if (_advanceErrorLog.TryAcquire())
                    logger.LogError("玩家模拟推进失败 player={PlayerId} code={Code} message={Message}",
                        aggregate.PlayerId.Value, result.Code, result.Message);
                return null;
            }
            return result;
        }

        /// <summary>重算并缓存下一个可见事件边界；必须在单写者锁内调用。</summary>
        private void RefreshNextVisibleBoundary()
        {
            var boundary = SimulationSchedule.NextVisibleBoundaryUtc(aggregate, gameData);
            Interlocked.Exchange(ref _nextVisibleBoundaryUtcTicks, boundary?.UtcTicks ?? 0);
        }

        /// <summary>
        /// 会话卸载路径：先把在线状态落为离线再强制刷盘，保证 player_projection 的
        /// presence/last_active_at 能区分在线玩家与可冷挂机玩家。
        /// 仅当当前为 Online 才改写，避免覆盖系统置下的 Suspended 状态。
        /// </summary>
        public async ValueTask MarkOfflineAndFlushAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_disposed && aggregate.Presence == PresenceState.Online)
                    aggregate.SetPresence(PresenceState.Offline);
                // 卸载前强制 Progression 落盘：道龄随空转时间累积但不标脏，需在此收口。
                aggregate.MarkDirty(PlayerSectionKind.Progression);
            }
            finally { _gate.Release(); }
            await FlushAsync(true, cancellationToken);
        }

        /// <summary>
        /// 把脏 section 与待落盘命令回执编码入 durable 写队列。
        /// 非 force 时每个 section 需满足 15 秒最小脏龄才写；有待落盘 receipt 时整批提前，
        /// 避免回执先落盘而对应状态仍停留在内存造成跨重启重放。
        /// </summary>
        /// <param name="force">是否忽略脏龄门槛写全部脏段。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public async ValueTask FlushAsync(bool force, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (ownership is not null && !await ownership.RenewAsync(aggregate.PlayerId.Value, ownerInstanceId, ownershipLease, cancellationToken))
                {
                    // 所有权已转移给其他实例：本实例内存脏状态不再权威，跳过本次刷盘；
                    // actor 由运行时在下一次命令或推进时失效移除，durable 队列中已入队的写不受影响。
                    logger.LogError("所有权已丢失，跳过玩家刷盘 player={PlayerId}", aggregate.PlayerId.Value);
                    return;
                }
                var dirty = aggregate.DirtySections;
                if (dirty.Count == 0 && _pendingReceipts.Count == 0) return;
                var now = DateTimeOffset.UtcNow;
                var receiptPending = _pendingReceipts.Count > 0;
                var eligibleAll = receiptPending || force;
                PlayerSectionKind[] eligible;
                if (eligibleAll)
                {
                    eligible = dirty.Count == 0 ? [] : dirty.ToArray();
                }
                else
                {
                    var list = new List<PlayerSectionKind>(dirty.Count);
                    foreach (var section in dirty)
                    {
                        if (!_dirtyAt.TryGetValue(SectionCodec.ToStorageKind(section), out var at) || now - at >= TimeSpan.FromSeconds(15))
                        {
                            list.Add(section);
                        }
                    }
                    eligible = list.Count == 0 ? [] : list.ToArray();
                }

                // 需要跨重连去重的低频写命令必须把本次所有脏 section 与 receipt
                // 一起进入 durable queue，避免回执先落盘而其他状态仍停留在内存。
                if (eligible.Length == 0 && dirty.Count > 0 && !receiptPending) return;

                // EncodeAggregate consumes all dirty flags; restore flags for sections not yet eligible.
                var encoded = SectionCodec.EncodeAggregate(aggregate, now, Math.Max(1, _logicalVersion));
                IReadOnlyList<SectionWrite> writes;
                if (eligibleAll || eligible.Length == encoded.Count)
                {
                    writes = encoded;
                }
                else
                {
                    var filteredWrites = new List<SectionWrite>(encoded.Count);
                    for (var i = 0; i < encoded.Count; i++)
                    {
                        var write = encoded[i];
                        var kind = SectionCodec.FromStorageKind(write.SectionKind);
                        var isEligible = false;
                        for (var j = 0; j < eligible.Length; j++)
                        {
                            if (eligible[j] == kind)
                            {
                                isEligible = true;
                                break;
                            }
                        }

                        if (isEligible)
                        {
                            filteredWrites.Add(write);
                        }
                        else
                        {
                            aggregate.MarkDirty(kind);
                        }
                    }
                    writes = filteredWrites;
                }

                if (writes.Count == 0 && _pendingReceipts.Count == 0) return;
                try
                {
                    await repository.EnqueueSectionsAsync(writes, _pendingReceipts.Count == 0 ? [] : _pendingReceipts.ToArray(), cancellationToken);
                }
                catch
                {
                    // 队列写入失败时恢复所有刚刚消费的 dirty 标记，确保下一周期仍会重试。
                    foreach (var write in encoded)
                    {
                        aggregate.MarkDirty(SectionCodec.FromStorageKind(write.SectionKind));
                        _dirtyAt[write.SectionKind] = now.Subtract(TimeSpan.FromSeconds(15));
                    }

                    throw;
                }
                foreach (var write in writes)
                {
                    _sectionVersions[write.SectionKind] = write.StateVersion;
                    _dirtyAt.Remove(write.SectionKind);
                }
                _pendingReceipts.Clear();
            }
            finally { _gate.Release(); }
        }

        /// <summary>斗法弃战：队首行动存在未裁决回放时判负记账，并把对手通知记入锁外路由队列。</summary>
        private void TryForfeitActiveDuel()
        {
            if (aggregate.ApplyDuelForfeit(DateTimeOffset.UtcNow, gameData.Duel) is { } forfeit)
                _pendingDuelForfeits.Add(forfeit);
        }

        /// <summary>取走并清空本侧产生的弃战通知（命令路径锁内记录、锁外由运行时路由）。</summary>
        public async ValueTask<IReadOnlyList<(Guid DuelId, Guid PeerId)>> DrainDuelForfeitsAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var list = _pendingDuelForfeits.ToArray();
                _pendingDuelForfeits.Clear();
                return list;
            }
            finally { _gate.Release(); }
        }

        /// <summary>撮合读取：队首处于斗法匹配等待相位时返回自身实力，否则 null。</summary>
        public async ValueTask<(Guid PlayerId, double Power)?> ReadDuelPendingAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return aggregate.IsDuelPending ? (aggregate.PlayerId.Value, aggregate.DuelPower) : null; }
            finally { _gate.Release(); }
        }

        /// <summary>撮合读取：满足斗法入场资格（勾选斗法、有遭遇计数、无冷却、无进行中斗法）时返回实力。</summary>
        public async ValueTask<(Guid PlayerId, double Power)?> ReadDuelCandidateAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return aggregate.CanJoinDuel(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d)
                    ? (aggregate.PlayerId.Value, aggregate.DuelPower)
                    : null;
            }
            finally { _gate.Release(); }
        }

        /// <summary>斗法预约：锁定进行中标识并消耗一点遭遇计数，成功时返回本侧开战镜像快照。</summary>
        public async ValueTask<DuelCombatant?> ReserveDuelAsync(Guid duelId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!aggregate.TryReserveDuel(duelId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d)) return null;
                TouchDirtyOnly();
                return aggregate.BuildDuelMirror(gameData);
            }
            finally { _gate.Release(); }
        }

        /// <summary>解除斗法预约：撮合中断回滚，归还遭遇计数。</summary>
        public async ValueTask ReleaseDuelReservationAsync(Guid duelId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { aggregate.ReleaseDuelReservation(duelId); TouchDirtyOnly(); }
            finally { _gate.Release(); }
        }

        /// <summary>挂载斗法回放：把撮合产出的确定性剧本接入队首战斗远征（预约须先成功）。</summary>
        public async ValueTask<bool> AttachDuelAsync(DuelPlaybackState playback, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                aggregate.AttachDuel(playback);
                TouchDirtyOnly();
                return true;
            }
            catch (DomainException) { return false; }
            finally { _gate.Release(); }
        }

        /// <summary>撤销斗法挂接：对手挂载失败时把本侧从 Duel 相位回滚为搜寻完成态。</summary>
        public async ValueTask DetachDuelAsync(Guid duelId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { aggregate.DetachDuel(duelId); TouchDirtyOnly(); }
            finally { _gate.Release(); }
        }

        /// <summary>撮合无可用对手：退出匹配等待相位，本波转正常怪物波次。</summary>
        public async ValueTask AbortDuelSeekAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { aggregate.AbortDuelSeek(); TouchDirtyOnly(); }
            finally { _gate.Release(); }
        }

        /// <summary>对手弃战通知：本地回放标记为已裁决胜利，下一拍推进结算缴获。</summary>
        public async ValueTask<bool> ApplyPeerForfeitAsync(Guid duelId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!aggregate.ApplyPeerForfeit(duelId)) return false;
                TouchDirtyOnly();
                return true;
            }
            finally { _gate.Release(); }
        }

        private PlayerCommandResult Fail(string code, string message) => new(false, code, message, Snapshot());

        private void RememberRequest(Guid requestId, string command, string payloadHash)
        {
            _recentRequests[requestId] = (command, payloadHash);
            _recentRequestOrder.Enqueue(requestId);
            while (_recentRequestOrder.Count > 1024)
            {
                var expired = _recentRequestOrder.Dequeue();
                _recentRequests.Remove(expired);
            }
        }

        private static string ComputePayloadHash(JsonElement payload) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, ServerJson.Options))).ToLowerInvariant();

        private static bool ShouldPersistReceipt(string command) => command is
            "action.enqueue" or "action.remove" or
            "queue.configure" or "queue.move" or "queue.auto_use.configure" or
            "loadout.replace" or "ability.learn" or "progression.breakthrough" or "world.travel" or
            "building.build" or "building.upgrade" or "building.contribute" or "player.rename" or "market.claim" or
            "item.discard" or "item.sell";

        /// <summary>
        /// 按当前位置从共享注册表刷新节点公共建筑投影与汇总效果：注册表是跨玩家权威，
        /// 聚合内仅保留最近已知副本（离线/冷挂机补算沿用）。投影内容变化时才标记 World 分段脏。
        /// </summary>
        private async ValueTask RefreshNodeBuildingsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (nodeBuildings is null) return;
            if (!string.IsNullOrWhiteSpace(aggregate.World.EdgeId) || string.IsNullOrWhiteSpace(aggregate.World.NodeId))
            {
                if (_nodeBuildingSignature == 0) return;
                _nodeBuildingSignature = 0;
                aggregate.World.NodeBuildings = [];
                aggregate.World.NodeBuildingEffects = BuildingEffectSet.Empty;
                aggregate.World.NodeBuildingNodeId = null;
                aggregate.MarkDirty(PlayerSectionKind.World);
                TouchDirtyOnly();
                return;
            }
            var buildings = await nodeBuildings.BuildingsForAsync(aggregate.World.NodeId, now, cancellationToken);
            var signature = NodeBuildingSignature(buildings);
            if (signature == _nodeBuildingSignature) return;
            _nodeBuildingSignature = signature;
            aggregate.World.NodeBuildings = buildings.ToList();
            aggregate.World.NodeBuildingEffects = NodeBuildingRules.AggregateEffects(buildings, gameData.Buildings, now.ToUnixTimeMilliseconds() / 1000d);
            aggregate.World.NodeBuildingNodeId = aggregate.World.NodeId;
            aggregate.MarkDirty(PlayerSectionKind.World);
            TouchDirtyOnly();
        }

        /// <summary>节点建筑投影的轻量指纹：任何等级/工程/材料投入变化都会改写签名，用于跳过重复投影。</summary>
        private static long NodeBuildingSignature(IReadOnlyList<NodeBuildingState> buildings)
        {
            unchecked
            {
                var hash = 17L;
                foreach (var entry in buildings)
                {
                    hash = hash * 31 + entry.BuildingId.GetHashCode();
                    hash = hash * 31 + entry.Level;
                    hash = hash * 31 + entry.TargetLevel;
                    hash = hash * 31 + (long)(entry.StartedAtUnixSeconds * 1000);
                    if (entry.ContributedMaterials is not null)
                    {
                        foreach (var pair in entry.ContributedMaterials.OrderBy(item => item.Key, StringComparer.Ordinal))
                            hash = hash * 31 + pair.Key.GetHashCode() * 7 + (long)(pair.Value * 1000);
                    }
                }
                return hash;
            }
        }

        private async ValueTask ReloadWalletAsync(CancellationToken cancellationToken)
        {
            var refreshed = await assetRepository.LoadBalancesAsync(aggregate.PlayerId.Value, cancellationToken);
            wallet.Restore(refreshed.Select(balance => new AssetBalance(
                new PlayerId(balance.PlayerId),
                new AssetKey(new ItemId(balance.AssetId), balance.Variant),
                balance.Quantity,
                    balance.Version)));
            _walletReloadRequired = false;
        }

        private async ValueTask EnsureWalletFreshAsync(CancellationToken cancellationToken)
        {
            if (_walletReloadRequired) await ReloadWalletAsync(cancellationToken);
        }

        private async ValueTask RefreshWalletAfterDurableMutationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ReloadWalletAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _walletReloadRequired = true;
                if (exception is OperationCanceledException) throw;
                // 市场事务已经提交，不能把成功状态改报成失败并让客户端以另一订单重试；
                // 标记钱包 stale，下一次资产相关命令会先强制刷新。
                if (_walletErrorLog.TryAcquire())
                    logger.LogError(exception, "市场资产事务已提交但本地钱包刷新失败 player={PlayerId}", aggregate.PlayerId.Value);
            }
        }

        private void RestoreSimulationState(
            PlayerAggregateState state,
            IReadOnlyCollection<AssetBalance> balances,
            IReadOnlyCollection<PlayerSectionKind> dirtySections,
            IReadOnlyDictionary<string, DateTimeOffset> dirtyAt,
            long logicalVersion)
        {
            aggregate.RestoreFrom(state);
            foreach (var section in dirtySections) aggregate.MarkDirty(section);
            _dirtyAt.Clear();
            foreach (var pair in dirtyAt) _dirtyAt[pair.Key] = pair.Value;
            _logicalVersion = logicalVersion;
            wallet.Restore(balances);
        }

        private static bool TryReadGuid(JsonElement payload, string property, out Guid value)
        {
            value = Guid.Empty;
            return payload.TryGetProperty(property, out var element)
                && element.ValueKind == JsonValueKind.String
                && Guid.TryParse(element.GetString(), out value)
                && value != Guid.Empty;
        }

        private static List<Guid> ReadGuidList(JsonElement payload, string property)
        {
            var result = new List<Guid>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty) result.Add(id);
            return result;
        }

        private static List<ItemId> ReadItemIdList(JsonElement payload, string property)
        {
            var result = new List<ItemId>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) result.Add(new ItemId(value.GetString()!));
            return result;
        }

        private static List<AbilityId> ReadAbilityIdList(JsonElement payload, string property)
        {
            var result = new List<AbilityId>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) result.Add(new AbilityId(value.GetString()!));
            return result;
        }

        private void Touch()
        {
            _logicalVersion = Math.Max(_logicalVersion, aggregate.StateVersion);
            TouchDirtyOnly();
        }

        private void TouchDirtyOnly()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var section in aggregate.DirtySections) _dirtyAt.TryAdd(SectionCodec.ToStorageKind(section), now);
        }

        private IReadOnlyList<StoredAction> BuildStoredActions()
        {
            return aggregate.Actions
                .Select((action, index) => new StoredAction(
                    aggregate.PlayerId.Value,
                    index,
                    action.Intent.ActionId.Value,
                    JsonSerializer.SerializeToUtf8Bytes(action.Intent, ServerJson.Options),
                    Math.Max(1, aggregate.StateVersion),
                    action.Status.ToString().ToLowerInvariant(),
                    null,
                    $"action:{action.Id:D}"))
                .ToArray();
        }

        /// <summary>
        /// 销毁 Actor：幂等；等待在途命令完成后释放信号量并归还所有权租约。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // 所有权丢失清理与会话移除可能并发触发释放，幂等退出。
            if (Interlocked.Exchange(ref _disposeStarted, 1) == 1) return;
            _disposed = true;
            // 先等待在途命令完成再销毁信号量，避免并发命令在已释放的 SemaphoreSlim 上抛 ObjectDisposedException。
            await _gate.WaitAsync(CancellationToken.None);
            _gate.Dispose();
            if (ownership is not null) await ownership.ReleaseAsync(aggregate.PlayerId.Value, ownerInstanceId, CancellationToken.None);
        }
    }
}

/// <summary>每玩家战报日志环形缓冲与单调递增 sequence；仅进程内存，不持久化。连续完全相同模板日志自动合并且仅占用 1 条容量配额。</summary>
public sealed class PlayerLogBuffer(int capacity)
{
    private readonly object _lock = new();
    private readonly List<LogEventDto> _events = [];
    private long _sequence;

    /// <summary>分配下一条战报日志的单调递增 sequence（线程安全）。</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// 追加一条战报日志；与缓冲末尾模板相同则合并计数并更新 sequence/时间戳，
    /// 超出容量时丢弃最旧条目。
    /// </summary>
    /// <param name="logEvent">待写入的战报日志 DTO。</param>
    public void Append(LogEventDto logEvent)
    {
        lock (_lock)
        {
            if (_events.Count > 0 && _events[^1].Template == logEvent.Template && _events[^1].Channel == logEvent.Channel)
            {
                var last = _events[^1];
                _events[^1] = last with
                {
                    Sequence = logEvent.Sequence,
                    TimestampUnixMilliseconds = Math.Max(last.TimestampUnixMilliseconds, logEvent.TimestampUnixMilliseconds),
                    Count = last.Count + logEvent.Count
                };
                return;
            }

            _events.Add(logEvent);
            while (_events.Count > capacity)
            {
                _events.RemoveAt(0);
            }
        }
    }

    /// <summary>返回缓冲内全部日志的快照副本，供重连回放。</summary>
    public IReadOnlyList<LogEventDto> SnapshotRecent()
    {
        lock (_lock) return _events.ToArray();
    }
}

/// <summary>
/// 在线玩家脏 section 的周期落盘后台服务：按 SectionFlushInterval 触发 FlushAllAsync(false)，
/// 把达到脏龄门槛的分段批量写入 durable 队列。
/// </summary>
/// <param name="runtime">玩家运行时。</param>
/// <param name="options">服务端配置选项。</param>
/// <param name="logger">日志记录器。</param>
public sealed class PlayerFlushHostedService(PlayerRuntime runtime, ServerOptions options, ILogger<PlayerFlushHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.FlushAllAsync(false, stoppingToken); await Task.Delay(options.SectionFlushInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家 section 刷新失败。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

/// <summary>在线玩家模拟时钟。推进只发生在内存单写者内，section 仍按低频批次落盘。</summary>
public sealed class PlayerSimulationHostedService(PlayerRuntime runtime, ILogger<PlayerSimulationHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 边界驱动调度：战斗出手、相位切换、行动完成等时刻是确定性可知的，
        // 在边界时刻醒来推进并推送，事件到达≈发生时刻；无边界按 1 秒兜底，
        // 斗法撮合、暂存结算与在线心跳等每拍逻辑不受影响。
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家模拟推进失败。");
            }
            var boundary = runtime.NextAdvanceBoundaryUtc;
            var delay = boundary is { } next ? next - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1);
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            else if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
