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
    ILogger<ActionParticipationRuntime> logger,
    ServerOptions? options = null) : IActionParticipationRuntime
{
    private readonly TimeSpan _offlineLimit = options?.ColdCatchUpWindow ?? TimeSpan.FromHours(48);
    private readonly object _gateRegistryLock = new();
    private readonly Dictionary<string, SessionGate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> _fencedSessions = new(StringComparer.Ordinal);
    private long _nextBoundaryTicks;
    private readonly ConcurrentDictionary<Guid, string> _participants = new();

    /// <inheritdoc />
    public bool RequiresReconciliation(Guid playerId) => _participants.ContainsKey(playerId);

    /// <inheritdoc />
    public DateTimeOffset? NextAdvanceBoundaryUtc => Volatile.Read(ref _nextBoundaryTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <inheritdoc />
    public async ValueTask CreateAsync(Guid partyId, ActionParticipationSpec spec, CancellationToken cancellationToken = default)
    {
        using var gate = await AcquireGateAsync(spec.SessionId, cancellationToken);
        try
        {
            var engine = ActionParticipationSessionEngine.Create(spec, gameData);
            var document = engine.Export();
            var candidate = new ActionParticipationSessionRecord(spec.SessionId, partyId,
                JsonSerializer.Serialize(spec, PersistenceJson.Options),
                JsonSerializer.Serialize(document, PersistenceJson.Options), 0,
                AtUnixSeconds(document.AdvancedAtUnixSeconds));
            ActionParticipationSessionRecord stored;
            try
            {
                stored = await store.CreateOrInitializeAsync(candidate, cancellationToken);
            }
            catch (ParticipationSessionCommitOutcomeUnknownException exception)
            {
                // 初始化没有任何战斗收益；若开场输入一致，已存在的权威检查点即可继续使用。
                stored = await store.LoadAsync(spec.SessionId, CancellationToken.None)
                    ?? throw new InvalidOperationException($"会话初始化提交结局尚未确认 session={spec.SessionId}，保留冻结输入供重试。", exception);
                logger.LogWarning(exception, "会话 {SessionId} 初始化提交响应丢失，已读取版本 {Version} 确认权威检查点。", spec.SessionId, stored.Version);
            }
            if (stored.PartyId != partyId || !JsonEquals(stored.SpecJson, candidate.SpecJson))
                throw new InvalidOperationException($"会话初始化输入冲突 session={spec.SessionId} version={stored.Version}。");
            foreach (var member in spec.Members) _participants[Guid.Parse(member.PlayerId)] = spec.SessionId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "会话 {SessionId} 开场检查点交付失败。", spec.SessionId);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var online = presence.SnapshotOnlinePlayerIds().ToHashSet();
        Volatile.Write(ref _nextBoundaryTicks, 0);
        if (online.Count == 0) return;
        var sessions = await store.ListActiveAsync(cancellationToken);
        List<Exception>? failures = null;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var spec = ReadSpec(session);
                foreach (var member in spec.Members) _participants[Guid.Parse(member.PlayerId)] = session.SessionId;
                if (!spec.Members.Any(member => online.Contains(Guid.Parse(member.PlayerId)))) continue;
                var boundary = await AdvanceSessionAsync(session.SessionId, target, endReason: null, cancellationToken);
                if (boundary is { } next)
                {
                    var current = Volatile.Read(ref _nextBoundaryTicks);
                    if (current == 0 || next.UtcTicks < current)
                        Volatile.Write(ref _nextBoundaryTicks, next.UtcTicks);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add(exception);
            }
        }
        // 故障会话的 fence 不能阻塞其他队伍，批次结束再统一向后台报告持久化故障。
        if (failures is not null) throw new AggregateException("部分在线组队会话推进失败。", failures);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParticipationInboxItem>> AdvanceForMemberAsync(Guid playerId,
        DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var sessions = await store.ListActiveForMemberAsync(playerId, cancellationToken);
        foreach (var session in sessions) _participants[playerId] = session.SessionId;
        foreach (var session in sessions)
            await AdvanceSessionAsync(session.SessionId, target, endReason: null, cancellationToken);
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

    /// <summary>只在已提交检查点上建立临时引擎；持久化失败时直接丢弃，下一次仍从权威状态恢复。</summary>
    private async ValueTask<DateTimeOffset?> AdvanceSessionAsync(string sessionId, DateTimeOffset target, string? endReason,
        CancellationToken cancellationToken)
    {
        target = AtSimulationPrecision(target);
        using var gate = await AcquireGateAsync(sessionId, cancellationToken);
        try
        {
            if (_fencedSessions.TryGetValue(sessionId, out var fence))
                throw new InvalidOperationException($"会话 {sessionId} 已因检查点冲突或提交结局未知停止推进，请先核对持久化证据。", fence);
            var previous = await store.LoadAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException($"会话检查点尚未初始化 session={sessionId}。");
            if (previous.EndedAt is not null) return null;
            if (endReason is null && await store.GetInvalidationReasonAsync(sessionId, cancellationToken) is { } invalidationReason)
            {
                // 没有显式散场事实时，条件失效时刻不可恢复；只收尾已确定的检查点，不补算不确定区间。
                target = previous.LastSimulatedAt;
                endReason = invalidationReason;
                logger.LogWarning("会话 {SessionId} 持久化组织条件失效，按检查点版本 {Version}、最后确定时刻 {CheckpointAt} 收尾，原因 {Reason}；未重演不确定区间。",
                    sessionId, previous.Version, previous.LastSimulatedAt, invalidationReason);
            }
            else
            {
                var expiresAt = await store.GetOfflineExpiryAsync(sessionId, _offlineLimit,
                    presence.SnapshotOnlinePlayerIds(), cancellationToken);
                if (expiresAt is { } expiry && expiry <= target)
                {
                    target = AtSimulationPrecision(expiry);
                    endReason = "member-offline-timeout";
                }
            }
            var document = ReadDocument(previous);
            foreach (var member in document.Spec.Members) _participants[Guid.Parse(member.PlayerId)] = sessionId;
            if (endReason is null && target <= previous.LastSimulatedAt
                && string.Equals(document.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal))
                return ActionParticipationSessionEngine.Restore(document, gameData).NextVisibleBoundaryUtc;
            if (endReason is not null && target < previous.LastSimulatedAt)
                throw new InvalidOperationException($"会话散场时刻早于已提交检查点 session={sessionId} version={previous.Version} at={target:O} checkpoint={previous.LastSimulatedAt:O}。");

            IReadOnlyList<ActionParticipationMemberSettlement> settlements;
            ActionParticipationSessionDocument nextDocument;
            DateTimeOffset? nextBoundary = null;
            if (!string.Equals(document.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal))
            {
                var finalization = ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(document);
                settlements = finalization.Settlements;
                nextDocument = finalization.Document;
                target = previous.LastSimulatedAt;
                endReason = "game-data-changed";
                logger.LogError("会话 {SessionId} 配表指纹不一致，按检查点版本 {Version}、最后确定时刻 {CheckpointAt} 收尾；未使用新配表重演。",
                    sessionId, previous.Version, previous.LastSimulatedAt);
            }
            else
            {
                var engine = ActionParticipationSessionEngine.Restore(document, gameData);
                settlements = endReason is null ? engine.AdvanceTo(target) : engine.EndAt(target);
                nextDocument = engine.Export();
                nextBoundary = engine.NextVisibleBoundaryUtc;
            }
            var stateJson = JsonSerializer.Serialize(nextDocument, PersistenceJson.Options);
            var lastSimulatedAt = AtUnixSeconds(nextDocument.AdvancedAtUnixSeconds);
            var inbox = settlements.Select(settlement => new ParticipationInboxItem(sessionId,
                settlement.PlayerId.Value, settlement.Sequence,
                // 完整战斗态与累计战报只在检查点保存一次；收件箱保留个人结算必需的增量与终值。
                JsonSerializer.Serialize(settlement with { Expedition = null, CombatStats = null }, PersistenceJson.Options),
                settlement.To)).ToArray();
            DateTimeOffset? endedAt = endReason is null ? null : target;
            try
            {
                var committed = await store.CommitAdvanceAsync(sessionId, previous.Version, stateJson,
                    lastSimulatedAt, inbox, endedAt, endReason, cancellationToken);
                if (!committed)
                {
                    var current = await store.LoadAsync(sessionId, CancellationToken.None);
                    var conflict = new InvalidOperationException($"会话检查点写入冲突 session={sessionId} expectedVersion={previous.Version} actualVersion={current?.Version}，未重复推演或投递。");
                    _fencedSessions[sessionId] = conflict;
                    throw conflict;
                }
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
                if (current is not null && current.Version == checked(previous.Version + 1)
                    && current.LastSimulatedAt == lastSimulatedAt && current.EndedAt == endedAt
                    && current.EndReason == endReason && JsonEquals(current.StateJson, stateJson))
                {
                    logger.LogWarning(exception, "会话 {SessionId} 检查点提交响应丢失，已读取版本 {Version} 确认结算原子提交。", sessionId, current.Version);
                    return nextBoundary;
                }
                logger.LogError(exception, "会话 {SessionId} 检查点提交结局无法确认，预期版本 {ExpectedVersion}，当前版本 {ActualVersion}；未重复推演或投递。",
                    sessionId, previous.Version + 1, current?.Version);
                var unresolvedCommit = new InvalidOperationException($"会话检查点提交结局无法确认 session={sessionId} expectedVersion={previous.Version + 1} actualVersion={current?.Version}，必须核对持久化证据。", exception);
                _fencedSessions[sessionId] = unresolvedCommit;
                throw unresolvedCommit;
            }
            return nextBoundary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "会话 {SessionId} 推进失败，业务时钟 {SimulationNow}；未保留未提交的内存推进状态。", sessionId, clock.UtcNow);
            throw;
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
        if (document.Spec.SessionId != session.SessionId
            || !JsonEquals(session.SpecJson, JsonSerializer.Serialize(document.Spec, PersistenceJson.Options))
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
