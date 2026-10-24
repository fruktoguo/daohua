using DaoHua.Ecs;
using Server.Clean;
using Server.Clean.Social;

namespace Server;

/// <summary>
/// 组队讨伐唯一权威宿主：按会话串行推进，并把检查点与成员结算交付到同一事务。
/// 玩家状态只由成员运行时消费收件箱后更新；本宿主不获取玩家写锁或玩家聚合。
/// </summary>
public sealed class ActionParticipationRuntime(
    GameDataCatalog gameData,
    IParticipationSessionStore store,
    OnlinePresenceRegistry presence,
    IPlayerSimulationClock clock,
    ILogger<ActionParticipationRuntime> logger) : IActionParticipationRuntime, IParticipationAssetBoundary
{
    private readonly TimeSpan _offlineLimit = Domain.Player.PlayerLifecycleRules.OfflineIdleLimit;
    private readonly object _gateRegistryLock = new();
    private readonly Dictionary<string, SessionGate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> _fencedSessions = new(StringComparer.Ordinal);
    private long _nextBoundaryTicks;
    private readonly ConcurrentDictionary<Guid, string> _participants = new();
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private bool _relationsRestored;
    private static readonly SemaphoreSlim[] RelationGates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<Guid, int> _connectingMembers = new();

    /// <inheritdoc />
    public event Action<Guid>? WalletChanged;

    public async ValueTask CreateAsync(Guid partyId, ActionParticipationSpec spec, CancellationToken cancellationToken = default)
    {
        await EnsureRelationsRestoredAsync(cancellationToken);
        using var relations = await AcquireRelationGatesAsync(spec.Members.Select(member => Guid.Parse(member.PlayerId)), cancellationToken);
        using var gate = await AcquireGateAsync(spec.SessionId, cancellationToken);
        if (_fencedSessions.TryGetValue(spec.SessionId, out var fence))
            throw new InvalidOperationException($"会话 {spec.SessionId} 开场提交结局未确认。", fence);
        if (await store.LoadAsync(spec.SessionId, cancellationToken) is { } existing)
        {
            if (existing.PartyId != partyId || !JsonEquals(JsonSerializer.Serialize(ReadSpec(existing), PersistenceJson.Options), JsonSerializer.Serialize(spec, PersistenceJson.Options)))
                throw new InvalidOperationException($"会话开场输入冲突 session={spec.SessionId}。");
            RegisterMembers(spec);
            return;
        }
        for (var attempt = 0; ; attempt++)
        {
            var balances = await store.LoadConsumableBalancesForItemsAsync(ConsumableKeys(spec), cancellationToken);
            var engine = ActionParticipationSessionEngine.Create(spec, gameData, ToWorkingWallet(balances));
            var settlements = engine.AdvanceTo(AtUnixSeconds(spec.StartedAtUnixSeconds));
            if (!spec.Members.Any(member => IsMemberOnline(Guid.Parse(member.PlayerId)))) engine.SwitchToOffline();
            var document = engine.Export();
            var deltas = engine.TakeAssetDeltas();
            var candidate = new ActionParticipationSessionRecord(spec.SessionId, partyId,
                JsonSerializer.Serialize(spec, PersistenceJson.Options),
                JsonSerializer.Serialize(document, PersistenceJson.Options), document.Sequence,
                AtUnixSeconds(document.AdvancedAtUnixSeconds));
            ActionParticipationSessionRecord stored;
            try
            {
                stored = await store.CreateOrInitializeAsync(candidate,
                    EconomyCommit(spec.SessionId, document.Sequence, candidate.LastSimulatedAt, balances, deltas),
                    Inbox(spec.SessionId, settlements), cancellationToken);
            }
            catch (ParticipationSessionEconomyConflictException) when (attempt < 2) { continue; }
            catch (ParticipationSessionCommitOutcomeUnknownException exception)
            {
                try
                {
                    stored = await store.LoadAsync(spec.SessionId, CancellationToken.None)
                        ?? throw new InvalidOperationException($"会话初始化提交结局尚未确认 session={spec.SessionId}。", exception);
                }
                catch (Exception failure)
                {
                    _fencedSessions[spec.SessionId] = failure;
                    throw;
                }
                logger.LogWarning(exception, "会话 {SessionId} 开场提交响应丢失，已读取版本 {Version} 确认权威检查点。", spec.SessionId, stored.Version);
            }
            if (stored.PartyId != partyId || !JsonEquals(JsonSerializer.Serialize(ReadSpec(stored), PersistenceJson.Options), candidate.SpecJson))
                throw new InvalidOperationException($"会话初始化输入冲突 session={spec.SessionId} version={stored.Version}。");
            foreach (var member in spec.Members) _participants[Guid.Parse(member.PlayerId)] = spec.SessionId;
            NotifyWalletChanges(deltas);
            return;
        }
    }

    /// <inheritdoc />
    public bool RequiresReconciliation(Guid playerId) => _participants.ContainsKey(playerId);

    /// <inheritdoc />
    public DateTimeOffset? NextAdvanceBoundaryUtc => Volatile.Read(ref _nextBoundaryTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;


    /// <inheritdoc />
    public async ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var online = presence.SnapshotOnlinePlayerIds().ToHashSet();
        online.UnionWith(_connectingMembers.Keys);
        Volatile.Write(ref _nextBoundaryTicks, 0);
        var sessions = await store.ListActiveAsync(cancellationToken);
        List<Exception>? failures = null;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var spec = ReadSpec(session);
                RegisterMembers(spec);
                var memberOnline = spec.Members.Any(member => online.Contains(Guid.Parse(member.PlayerId)));
                if (!memberOnline && ReadDocument(session).InventoryMode == ActionParticipationInventoryMode.Offline) continue;
                var boundary = await AdvanceSessionAsync(session.SessionId, target, endReason: null, desiredOnline: memberOnline, replenishOffline: false, cancellationToken);
                if (memberOnline && boundary is { } next)
                {
                    var current = Volatile.Read(ref _nextBoundaryTicks);
                    if (current == 0 || next.UtcTicks < current) Volatile.Write(ref _nextBoundaryTicks, next.UtcTicks);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) throw new AggregateException("部分在线组队会话推进失败。", failures);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Guid>> AdvanceOfflineDueAsync(DateTimeOffset now,
        TimeSpan batchInterval, CancellationToken cancellationToken = default)
    {
        var online = presence.SnapshotOnlinePlayerIds().ToHashSet();
        online.UnionWith(_connectingMembers.Keys);
        var sessions = await store.ListActiveAsync(cancellationToken);
        var advanced = new HashSet<Guid>();
        List<Exception>? failures = null;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var spec = ReadSpec(session);
                var memberIds = spec.Members.Select(member => Guid.Parse(member.PlayerId)).ToArray();
                RegisterMembers(spec);
                if (memberIds.Any(online.Contains)) continue;
                var document = ReadDocument(session);
                var due = document.InventoryMode == ActionParticipationInventoryMode.Online
                    || now - session.LastSimulatedAt >= batchInterval
                    || await store.GetOfflineExpiryAsync(session.SessionId, _offlineLimit, online, cancellationToken) is { } expiry && expiry <= now;
                if (!due) continue;
                await AdvanceSessionAsync(session.SessionId, now, endReason: null, desiredOnline: false,
                    replenishOffline: document.InventoryMode == ActionParticipationInventoryMode.Offline, cancellationToken);
                foreach (var memberId in memberIds) advanced.Add(memberId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add(exception);
            }
        }
        // 与在线推进同口径：单会话故障不阻塞其他队伍，批次结束聚合抛出。
        if (failures is not null) throw new AggregateException("部分全员离线组队会话推进失败。", failures);
        return [.. advanced];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParticipationInboxItem>> AdvanceForMemberAsync(Guid playerId,
        DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var sessions = await store.ListActiveForMemberAsync(playerId, cancellationToken);
        foreach (var session in sessions)
        {
            var spec = ReadSpec(session);
            RegisterMembers(spec);
            var memberOnline = spec.Members.Any(member => IsMemberOnline(Guid.Parse(member.PlayerId)));
            if (memberOnline)
                await AdvanceSessionAsync(session.SessionId, target, endReason: null, desiredOnline: true, replenishOffline: false, cancellationToken);
        }
        return await LoadPendingAsync(playerId, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId,
        CancellationToken cancellationToken = default)
    {
        var initial = await store.LoadPendingAsync(playerId, cancellationToken);
        if (initial.Count == 0 && _participants.TryGetValue(playerId, out var knownSession))
        {
            using var gate = await AcquireGateAsync(knownSession, cancellationToken);
            // 空收件箱读可能先于散场提交；在同一会话门内重读后才能清除提示。
            initial = await store.LoadPendingAsync(playerId, cancellationToken);
            if (initial.Count == 0 && (await store.ListActiveForMemberAsync(playerId, cancellationToken)).Count == 0)
                _participants.TryRemove(new KeyValuePair<Guid, string>(playerId, knownSession));
        }
        if (initial.Count == 0) return initial;
        _participants[playerId] = initial[^1].SessionId;
        var result = new List<ParticipationInboxItem>(initial.Count);
        foreach (var sessionId in initial.Select(item => item.SessionId).Distinct(StringComparer.Ordinal))
        {
            using var gate = await AcquireGateAsync(sessionId, cancellationToken);
            if (_fencedSessions.TryGetValue(sessionId, out var fence))
                throw new InvalidOperationException($"会话已隔离，不能消费不确定的结算 session={sessionId}。", fence);
            // 再读一次，避免等待会话门时已产生新批次，却把更晚检查点投影到旧批次终值。
            var pending = (await store.LoadPendingAsync(playerId, cancellationToken))
                .Where(item => item.SessionId == sessionId).OrderBy(item => item.Sequence).ToArray();
            if (pending.Length == 0) continue;
            var checkpoint = await store.LoadAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException($"成员收件箱缺少会话检查点 session={sessionId} player={playerId:D}。");
            var document = ReadDocument(checkpoint);
            if (pending[^1].Sequence != document.Sequence)
                throw new InvalidOperationException($"成员结算与检查点序号不一致 session={sessionId} player={playerId:D} inbox={pending[^1].Sequence} checkpoint={document.Sequence}。");
            var memberId = new PlayerId(playerId);
            var expedition = ActionParticipationSessionEngine.ProjectFor(document, memberId);
            var combatStats = ActionParticipationSessionEngine.ProjectCombatStatsFor(document, memberId);
            foreach (var item in pending)
            {
                var settlement = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(item.PayloadJson, PersistenceJson.Options)
                    ?? throw new InvalidOperationException($"成员结算载荷为空 session={sessionId} player={playerId:D} sequence={item.Sequence}。");
                result.Add(item with { PayloadJson = JsonSerializer.Serialize(settlement with
                {
                    Expedition = expedition,
                    ProgressSeconds = expedition.Phase == CombatExpeditionPhase.Combat ? 0 : expedition.PhaseElapsedSeconds,
                    CombatStats = combatStats
                }, PersistenceJson.Options) });
            }
        }
        return result;
    }

    /// <inheritdoc />
    public async ValueTask EndAsync(string sessionId, DateTimeOffset at, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64)
            throw new ArgumentException("散场原因不能为空且不得超过 64 字符。", nameof(reason));
        await AdvanceSessionAsync(sessionId, at, reason, cancellationToken);
    }

    private ValueTask<DateTimeOffset?> AdvanceSessionAsync(string sessionId, DateTimeOffset target, string? endReason,
        CancellationToken cancellationToken) => AdvanceSessionAsync(sessionId, target, endReason, null, false, cancellationToken);

    private async ValueTask<DateTimeOffset?> AdvanceSessionAsync(string sessionId, DateTimeOffset target, string? endReason,
        bool? desiredOnline, bool replenishOffline, CancellationToken cancellationToken)
    {
        using var gate = await AcquireGateAsync(sessionId, cancellationToken);
        return await AdvanceSessionUnderGateAsync(sessionId, target, endReason, desiredOnline, replenishOffline, cancellationToken);
    }

    /// <summary>临时引擎只从持久化检查点与精确钱包恢复；读集冲突丢弃整批，绝不保留未提交推进。</summary>
    private async ValueTask<DateTimeOffset?> AdvanceSessionUnderGateAsync(string sessionId, DateTimeOffset requestedTarget,
        string? requestedEndReason, bool? desiredOnline, bool replenishOffline, CancellationToken cancellationToken)
    {
        if (_fencedSessions.TryGetValue(sessionId, out var fence))
            throw new InvalidOperationException($"会话 {sessionId} 提交结局未确认，禁止推进。", fence);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = AtSimulationPrecision(requestedTarget);
            var endReason = requestedEndReason;
            var previous = await store.LoadAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException($"会话检查点尚未初始化 session={sessionId}。");
            if (previous.EndedAt is not null) return null;
            var document = ReadDocument(previous);
            RegisterMembers(document.Spec);
            if (endReason is null && await store.GetInvalidationReasonAsync(sessionId, cancellationToken) is { } invalidationReason)
            {
                target = previous.LastSimulatedAt;
                endReason = invalidationReason;
                logger.LogWarning("会话 {SessionId} 条件失效，按最后确定检查点 {CheckpointAt} 收尾：{Reason}。", sessionId, target, endReason);
            }
            else
            {
                var online = presence.SnapshotOnlinePlayerIds().ToHashSet();
                online.UnionWith(_connectingMembers.Keys);
                if (await store.GetOfflineExpiryAsync(sessionId, _offlineLimit, online, cancellationToken) is { } expiry && expiry <= target)
                {
                    target = AtSimulationPrecision(expiry);
                    endReason = "member-offline-timeout";
                }
            }
            var compatible = string.Equals(document.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal);
            // 扫描存在感仅是候选提示；等待查询/会话门时可能已完成相反切换，不能回写旧模式。
            var onlineMode = desiredOnline.HasValue
                ? document.Spec.Members.Any(member => IsMemberOnline(Guid.Parse(member.PlayerId)))
                : document.InventoryMode == ActionParticipationInventoryMode.Online;
            var modeChanges = onlineMode != (document.InventoryMode == ActionParticipationInventoryMode.Online);
            if (endReason is null && target <= previous.LastSimulatedAt && !modeChanges && !replenishOffline && compatible)
                return ActionParticipationSessionEngine.Restore(document, gameData).NextVisibleBoundaryUtc;
            if (target < previous.LastSimulatedAt)
                throw new InvalidOperationException($"会话目标早于已提交检查点 session={sessionId} at={target:O} checkpoint={previous.LastSimulatedAt:O}。");
            IReadOnlyList<ActionParticipationMemberSettlement> settlements;
            IReadOnlyList<Domain.Economy.AssetDelta> deltas;
            ActionParticipationSessionDocument nextDocument;
            DateTimeOffset? nextBoundary = null;
            IReadOnlyList<AssetBalanceRecord> balances;
            if (!compatible)
            {
                var finalization = ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(document);
                settlements = finalization.Settlements;
                deltas = finalization.AssetDeltas;
                nextDocument = finalization.Document;
                balances = await store.LoadConsumableBalancesForItemsAsync(deltas.Select(delta =>
                    new ParticipationAssetKey(delta.PlayerId.Value, delta.Asset.ItemId.Value, delta.Asset.Variant)).ToArray(), cancellationToken);
                target = previous.LastSimulatedAt;
                endReason = "game-data-changed";
                logger.LogError("会话 {SessionId} 配表指纹不一致，按最后确定检查点 {CheckpointAt} 收尾。", sessionId, target);
            }
            else
            {
                balances = await store.LoadConsumableBalancesForItemsAsync(ConsumableKeys(document.Spec), cancellationToken);
                var engine = ActionParticipationSessionEngine.Restore(document, gameData);
                engine.SetWorkingWallet(ToWorkingWallet(balances));
                settlements = endReason is null ? engine.AdvanceTo(target) : engine.EndAt(target);
                if (endReason is null)
                {
                    if (onlineMode) engine.SwitchToOnline();
                    else if (document.InventoryMode == ActionParticipationInventoryMode.Online) engine.SwitchToOffline();
                    else if (replenishOffline) engine.ReplenishOfflineReserve();
                }
                nextDocument = engine.Export();
                nextBoundary = onlineMode ? engine.NextVisibleBoundaryUtc : null;
                deltas = engine.TakeAssetDeltas();
            }
            var stateJson = JsonSerializer.Serialize(nextDocument, PersistenceJson.Options);
            var lastSimulatedAt = AtUnixSeconds(nextDocument.AdvancedAtUnixSeconds);
            DateTimeOffset? endedAt = endReason is null ? null : target;
            try
            {
                if (!await store.CommitAdvanceAsync(sessionId, previous.Version, stateJson, lastSimulatedAt,
                    Inbox(sessionId, settlements), endedAt, endReason,
                    EconomyCommit(sessionId, nextDocument.Sequence, lastSimulatedAt, balances, deltas), cancellationToken))
                    continue;
            }
            catch (ParticipationSessionCommitOutcomeUnknownException exception)
            {
                ActionParticipationSessionRecord? current;
                try { current = await store.LoadAsync(sessionId, CancellationToken.None); }
                catch (Exception readFailure)
                {
                    var unresolved = new AggregateException("会话未知提交的持久化证据读取失败。", exception, readFailure);
                    _fencedSessions[sessionId] = unresolved;
                    throw unresolved;
                }
                if (current is null || current.Version != nextDocument.Sequence || current.LastSimulatedAt != lastSimulatedAt
                    || current.EndedAt != endedAt || current.EndReason != endReason || !JsonEquals(current.StateJson, stateJson))
                {
                    var unresolved = new InvalidOperationException($"会话提交结局无法确认 session={sessionId} expectedVersion={nextDocument.Sequence} actualVersion={current?.Version}。", exception);
                    _fencedSessions[sessionId] = unresolved;
                    throw unresolved;
                }
                logger.LogWarning(exception, "会话 {SessionId} 提交响应丢失，已确认版本 {Version}。", sessionId, current.Version);
            }
            NotifyWalletChanges(deltas);
            return nextBoundary;
        }
        throw new InvalidOperationException($"会话检查点或工作钱包持续冲突 session={sessionId}，整批未发布。");
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable> AcquireAsync(IReadOnlyList<ParticipationAssetKey> keys,
        CancellationToken cancellationToken = default)
    {
        var relevant = keys.Where(key => IsManagedConsumable(key.AssetId)).Distinct().ToArray();
        if (relevant.Length == 0) return EmptyLease.Instance;
        await EnsureRelationsRestoredAsync(cancellationToken);
        var leases = new List<IDisposable>();
        try
        {
            leases.Add(await AcquireRelationGatesAsync(relevant.Select(key => key.PlayerId), cancellationToken));
            var sessions = new Dictionary<string, ActionParticipationSessionRecord>(StringComparer.Ordinal);
            foreach (var playerId in relevant.Select(key => key.PlayerId).Distinct())
                foreach (var row in await store.ListActiveForMemberAsync(playerId, cancellationToken))
                {
                    var spec = ReadSpec(row);
                    if (ConsumableKeys(spec).Any(slot => relevant.Any(key => key.PlayerId == slot.PlayerId && key.AssetId == slot.AssetId)))
                        sessions.TryAdd(row.SessionId, row);
                }
            foreach (var sessionId in sessions.Keys.Order(StringComparer.Ordinal))
                leases.Add(await AcquireGateAsync(sessionId, cancellationToken));
            foreach (var row in sessions.Values)
            {
                var current = await store.LoadAsync(row.SessionId, cancellationToken);
                if (current is null || current.EndedAt is not null) continue;
                var spec = ReadSpec(current);
                // Offline 的外部写入只能改变普通钱包，不提前批算或把新货补到旧时间。
                if (ReadDocument(current).InventoryMode == ActionParticipationInventoryMode.Online)
                    await AdvanceSessionUnderGateAsync(row.SessionId, clock.UtcNow, null,
                        spec.Members.Any(member => IsMemberOnline(Guid.Parse(member.PlayerId))), false, cancellationToken);
            }
            return new CompositeLease(leases);
        }
        catch
        {
            for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask SynchronizePresenceAsync(Guid playerId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await EnsureRelationsRestoredAsync(cancellationToken);
        using var relations = await AcquireRelationGatesAsync([playerId], cancellationToken);
        if (!_participants.ContainsKey(playerId)) return;
        foreach (var row in await store.ListActiveForMemberAsync(playerId, cancellationToken))
        {
            using var gate = await AcquireGateAsync(row.SessionId, cancellationToken);
            var current = await store.LoadAsync(row.SessionId, cancellationToken);
            if (current is null || current.EndedAt is not null) continue;
            var document = ReadDocument(current);
            var online = document.Spec.Members.Any(member => IsMemberOnline(Guid.Parse(member.PlayerId)));
            if (online == (document.InventoryMode == ActionParticipationInventoryMode.Online)) continue;
            var boundary = clock.UtcNow > at ? clock.UtcNow : at;
            if (boundary < current.LastSimulatedAt) boundary = current.LastSimulatedAt;
            await AdvanceSessionUnderGateAsync(row.SessionId, boundary, null, online, false, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> PrepareConnectionAsync(Guid playerId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        _connectingMembers.AddOrUpdate(playerId, 1, static (_, count) => count + 1);
        var lease = new ConnectionLease(this, playerId);
        try { await SynchronizePresenceAsync(playerId, at, cancellationToken); }
        catch { await lease.DisposeAsync(); throw; }
        return lease;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParticipationOfflineReserve>> LoadOfflineReservesAsync(Guid playerId,
        CancellationToken cancellationToken = default)
    {
        var reserves = new List<ParticipationOfflineReserve>();
        foreach (var row in await store.ListActiveForMemberAsync(playerId, cancellationToken))
        {
            using var gate = await AcquireGateAsync(row.SessionId, cancellationToken);
            var current = await store.LoadAsync(row.SessionId, cancellationToken);
            if (current is null || current.EndedAt is not null) continue;
            var document = ReadDocument(current);
            var index = document.Spec.SelfIndexOf(new PlayerId(playerId));
            if (document.OfflineReserves is null) continue;
            foreach (var balance in document.OfflineReserves)
                if (balance.MemberIndex == index && balance.Quantity > 0)
                    reserves.Add(new(balance.ItemId, balance.Variant, balance.Quantity));
        }
        return reserves;
    }

    private async ValueTask EnsureRelationsRestoredAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _relationsRestored)) return;
        await _restoreGate.WaitAsync(cancellationToken);
        try
        {
            if (_relationsRestored) return;
            foreach (var row in await store.ListActiveAsync(cancellationToken)) RegisterMembers(ReadSpec(row));
            Volatile.Write(ref _relationsRestored, true);
        }
        finally { _restoreGate.Release(); }
    }

    private void RegisterMembers(ActionParticipationSpec spec)
    {
        foreach (var member in spec.Members) _participants[Guid.Parse(member.PlayerId)] = spec.SessionId;
    }

    private bool IsMemberOnline(Guid playerId) => presence.IsOnline(playerId) || _connectingMembers.ContainsKey(playerId);
    private DateTimeOffset Now => clock.UtcNow;

    private bool IsManagedConsumable(string itemId) => gameData.Items.TryGetValue(new ItemId(itemId), out var item)
        && item.Category == ItemDefinition.CategoryConsumable && item.Consumable is { Durable: false } consumable
        && consumable.UsableInBars.Contains(AutoUseBar.Combat);

    private ParticipationAssetKey[] ConsumableKeys(ActionParticipationSpec spec) => spec.Members
        .SelectMany(member => (member.ConsumableSlots ?? []).Where(slot => IsManagedConsumable(slot.ItemId))
            .Select(slot => new ParticipationAssetKey(Guid.Parse(member.PlayerId), slot.ItemId, ""))).Distinct().ToArray();

    private static Domain.Economy.AssetBalance[] ToWorkingWallet(IReadOnlyList<AssetBalanceRecord> balances) => balances.Select(row =>
        new Domain.Economy.AssetBalance(new PlayerId(row.PlayerId), new Domain.Economy.AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)).ToArray();

    private static ParticipationInboxItem[] Inbox(string sessionId, IReadOnlyList<ActionParticipationMemberSettlement> settlements) =>
        settlements.Select(settlement => new ParticipationInboxItem(sessionId, settlement.PlayerId.Value, settlement.Sequence,
            JsonSerializer.Serialize(settlement with { Expedition = null, CombatStats = null }, PersistenceJson.Options), settlement.To)).ToArray();

    private static ParticipationSessionEconomyCommit EconomyCommit(string sessionId, long sequence, DateTimeOffset at,
        IReadOnlyList<AssetBalanceRecord> balances, IReadOnlyList<Domain.Economy.AssetDelta> deltas)
    {
        EconomyTransactionRequest? transaction = null;
        if (deltas.Count > 0)
        {
            var key = $"participation.assets:{sessionId}:{sequence}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            transaction = new EconomyTransactionRequest(new Guid(hash.AsSpan(0, 16)), key, "participation.assets",
                deltas[0].PlayerId.Value, deltas.Select(delta => new Server.Clean.AssetDelta(delta.PlayerId.Value,
                    delta.Asset.ItemId.Value, delta.Asset.Variant, delta.QuantityDelta)).ToArray(), at);
        }
        return new(transaction, balances);
    }

    private void NotifyWalletChanges(IReadOnlyList<Domain.Economy.AssetDelta> deltas)
    {
        foreach (var playerId in deltas.Select(delta => delta.PlayerId.Value).Distinct()) WalletChanged?.Invoke(playerId);
    }

    private static async ValueTask<IDisposable> AcquireRelationGatesAsync(IEnumerable<Guid> players, CancellationToken cancellationToken)
    {
        var stripes = players.Select(player => (int)((uint)player.GetHashCode() % (uint)RelationGates.Length)).Distinct().Order().ToArray();
        var acquired = 0;
        try
        {
            foreach (var stripe in stripes) { await RelationGates[stripe].WaitAsync(cancellationToken); acquired++; }
            return new RelationLease(stripes);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--) RelationGates[stripes[i]].Release();
            throw;
        }
    }

    private sealed class RelationLease(int[] stripes) : IDisposable
    {
        public void Dispose() { for (var i = stripes.Length - 1; i >= 0; i--) RelationGates[stripes[i]].Release(); }
    }

    private sealed class CompositeLease(List<IDisposable> leases) : IDisposable
    {
        public void Dispose() { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }

    private sealed class ConnectionLease(ActionParticipationRuntime owner, Guid playerId) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (owner._connectingMembers.TryGetValue(playerId, out var count))
            {
                if (count == 1)
                {
                    if (owner._connectingMembers.TryRemove(new KeyValuePair<Guid, int>(playerId, count))) break;
                }
                else if (owner._connectingMembers.TryUpdate(playerId, count - 1, count)) break;
            }
            await owner.SynchronizePresenceAsync(playerId, owner.Now, CancellationToken.None);
        }
    }

    /// <summary>等待者也持有注册表引用，只有最后一个调用退出后才移除会话锁，避免锁表随历史会话无限增长。</summary>
    private async ValueTask<GateLease> AcquireGateAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionGate gate;
        lock (_gateRegistryLock)
        {
            if (!_gates.TryGetValue(sessionId, out gate!)) _gates[sessionId] = gate = new SessionGate();
            gate.Users++;
        }
        try { await gate.Semaphore.WaitAsync(cancellationToken); }
        catch
        {
            ReleaseGate(sessionId, gate, acquired: false);
            throw;
        }
        return new GateLease(this, sessionId, gate);
    }

    private void ReleaseGate(string sessionId, SessionGate gate, bool acquired)
    {
        lock (_gateRegistryLock)
        {
            if (acquired) gate.Semaphore.Release();
            if (--gate.Users == 0)
            {
                _gates.Remove(sessionId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class SessionGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class GateLease(ActionParticipationRuntime owner, string sessionId, SessionGate gate) : IDisposable
    {
        public void Dispose() => owner.ReleaseGate(sessionId, gate, acquired: true);
    }

    private static ActionParticipationSpec ReadSpec(ActionParticipationSessionRecord session) =>
        JsonSerializer.Deserialize<ActionParticipationSpec>(session.SpecJson, PersistenceJson.Options)
        ?? throw new InvalidOperationException($"会话冻结输入为空 session={session.SessionId} version={session.Version}。");

    private static ActionParticipationSessionDocument ReadDocument(ActionParticipationSessionRecord session)
    {
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(session.StateJson, PersistenceJson.Options)
            ?? throw new InvalidOperationException($"会话检查点载荷为空 session={session.SessionId} version={session.Version}。");
        // 冻结输入按解码后的规范化形态比较：新增可缺省字段不构成输入漂移。
        if (document.Spec.SessionId != session.SessionId
            || !JsonEquals(JsonSerializer.Serialize(ReadSpec(session), PersistenceJson.Options),
                JsonSerializer.Serialize(document.Spec, PersistenceJson.Options))
            || AtUnixSeconds(document.AdvancedAtUnixSeconds) != session.LastSimulatedAt)
            throw new InvalidOperationException($"会话检查点输入或时间索引不一致 session={session.SessionId} version={session.Version}。");
        return document;
    }

    private static DateTimeOffset AtUnixSeconds(double value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(value * 1000d)));

    // 会话引擎检查点统一到毫秒；提交前统一目标，避免恢复后重走不足一毫秒的窗口。
    private static DateTimeOffset AtSimulationPrecision(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static bool JsonEquals(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }
}
