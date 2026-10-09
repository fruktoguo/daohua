using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Server.Clean;
using Server.Clean.Social;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

/// <summary>覆盖会话宿主的所有权、挂起恢复与持久化失败边界；战斗数学由引擎测试单独验证。</summary>
public sealed class ActionParticipationRuntimeTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>全员离线不推进，三名成员同时回线共享一个检查点，宿主重建后从该检查点续写。</summary>
    [Fact]
    public async Task OfflineSessionResumesOnceForConcurrentMembersAndSurvivesHostRestart()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var firstTarget = StartedAt.AddSeconds(60);
        await fixture.Runtime.AdvanceOnlineAsync(firstTarget);
        Assert.Equal(0, fixture.Store.CommittedCount);

        var deliveries = await Task.WhenAll(fixture.Members.Select(member =>
            fixture.Runtime.AdvanceForMemberAsync(member, firstTarget).AsTask()));
        Assert.Equal(1, fixture.Store.CommittedCount);
        Assert.All(deliveries, items => Assert.Single(items));
        Assert.Single(deliveries.SelectMany(items => items).Select(item => item.Sequence).Distinct());

        var checkpoint = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        var nextTarget = firstTarget.AddSeconds(15);
        await fixture.Presence.RegisterAsync(fixture.Members[1], "乙", fixture.Spec.NodeId);
        await fixture.NewRuntime().AdvanceOnlineAsync(nextTarget);
        var resumed = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        Assert.Equal(checkpoint!.Version + 1, resumed!.Version);
        Assert.Equal(nextTarget, resumed.LastSimulatedAt);
        Assert.Equal(2, fixture.Store.CommittedCount);
        var pending = await fixture.Store.LoadPendingAsync(fixture.Members[0]);
        Assert.Equal(2, pending.Count);
        Assert.True(pending[1].Sequence > pending[0].Sequence);
    }

    /// <summary>COMMIT 前失败不污染下次推进；COMMIT 已成功但响应丢失时读取检查点确认，不重复投递。</summary>
    [Fact]
    public async Task CommitFailureDiscardsTransientEngineAndLostResponseConfirmsPersistedState()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        fixture.Store.FailBeforeCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60)).AsTask());
        Assert.Equal(0, fixture.Store.CommittedCount);

        fixture.Store.LoseCommitResponse = true;
        var inbox = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60));
        Assert.Single(inbox);
        Assert.Equal(1, fixture.Store.CommittedCount);
        var unchanged = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[2], StartedAt.AddSeconds(60));
        Assert.Single(unchanged);
        Assert.Equal(1, fixture.Store.CommittedCount);
    }

    /// <summary>未知提交无法确认时停止该会话后续推进，保留原检查点供重启恢复与人工核对。</summary>
    [Fact]
    public async Task UnconfirmedCommitFencesSessionWithoutReplayingOnNextTick()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        fixture.Store.UnknownBeforeCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60)).AsTask());
        var attempts = fixture.Store.CommitAttempts;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[1], StartedAt.AddSeconds(60)).AsTask());
        Assert.Equal(attempts, fixture.Store.CommitAttempts);
        Assert.Equal(0, fixture.Store.CommittedCount);
        Assert.Empty(await fixture.Store.LoadPendingAsync(fixture.Members[0]));
    }

    /// <summary>散场最终结算与 ended 检查点一起交付；重复散场不再生成个人结算。</summary>
    [Fact]
    public async Task EndPersistsFinalSettlementOnceForEveryFrozenMember()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var endedAt = StartedAt.AddSeconds(60);
        // 正常仲裁先提交取消准备，再调用带真实时刻的显式散场；该路径不得截回旧检查点。
        fixture.Store.InvalidationReason = "member-unready";
        await fixture.Runtime.EndAsync(fixture.Spec.SessionId, endedAt, "member-left");
        var checkpoint = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        Assert.Equal(endedAt, checkpoint!.EndedAt);
        Assert.Equal("member-left", checkpoint.EndReason);
        foreach (var member in fixture.Members)
        {
            var inbox = await fixture.Store.LoadPendingAsync(member);
            var final = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(inbox[^1].PayloadJson, PersistenceJson.Options)!;
            Assert.Equal(endedAt.ToUnixTimeMilliseconds() / 1000d, final.EndsAtUnixSeconds);
            Assert.Null(final.Expedition);
            Assert.Null(final.CombatStats);
            var projected = await fixture.Runtime.LoadPendingAsync(member);
            var hydrated = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(projected[^1].PayloadJson, PersistenceJson.Options)!;
            Assert.Equal(final.Sequence, hydrated.Sequence);
            Assert.NotNull(hydrated.Expedition);
            Assert.NotNull(hydrated.CombatStats);
        }
        var count = fixture.Store.CommittedCount;
        await fixture.Runtime.EndAsync(fixture.Spec.SessionId, endedAt.AddSeconds(10), "member-left");
        Assert.Equal(count, fixture.Store.CommittedCount);
    }

    /// <summary>首次回线冷算不得越过冻结成员的离线逐出边界，先按该时刻散场再读取个人结算。</summary>
    [Fact]
    public async Task ReconnectingMemberStopsAtOfflineExpiryBeforeRequestedTarget()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        fixture.Store.OfflineExpiry = StartedAt.AddSeconds(30);
        var inbox = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60));
        var checkpoint = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        Assert.Equal(fixture.Store.OfflineExpiry, checkpoint!.EndedAt);
        Assert.Equal(fixture.Store.OfflineExpiry, checkpoint.LastSimulatedAt);
        Assert.Equal("member-offline-timeout", checkpoint.EndReason);
        Assert.All(inbox, item => Assert.True(item.CreatedAt <= fixture.Store.OfflineExpiry));
    }

    /// <summary>取消准备已落库但仲裁前崩溃，首次成员装载只按旧检查点散场，不追加离线区间收益。</summary>
    [Fact]
    public async Task RestartAfterPersistedUnreadyEndsAtCheckpointWithoutNewRewards()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var checkpointAt = StartedAt.AddSeconds(60);
        await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], checkpointAt);
        var previous = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var before = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(previous.StateJson, PersistenceJson.Options)!;
        var priorDeliveries = await fixture.Store.LoadPendingAsync(fixture.Members[0]);
        fixture.Store.InvalidationReason = "member-unready";

        var recovered = fixture.NewRuntime();
        var deliveries = await recovered.AdvanceForMemberAsync(fixture.Members[0], checkpointAt.AddHours(1));
        var ended = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        Assert.Equal(checkpointAt, ended.LastSimulatedAt);
        Assert.Equal(checkpointAt, ended.EndedAt);
        Assert.Equal("member-unready", ended.EndReason);
        Assert.Equal(previous.Version + 1, ended.Version);
        Assert.Equal(priorDeliveries.Count + 1, deliveries.Count);
        Assert.Equal(priorDeliveries.Select(item => item.Sequence), deliveries.Take(priorDeliveries.Count).Select(item => item.Sequence));
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(ended.StateJson, PersistenceJson.Options)!;
        Assert.Equal(before.CompletedRepeats, document.CompletedRepeats);
        foreach (var member in fixture.Members)
        {
            var final = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(
                (await fixture.Store.LoadPendingAsync(member))[^1].PayloadJson, PersistenceJson.Options)!;
            Assert.Equal(checkpointAt, final.From);
            Assert.Equal(checkpointAt, final.To);
            Assert.Equal(checkpointAt.ToUnixTimeMilliseconds() / 1000d, final.EndsAtUnixSeconds);
            Assert.Empty(final.AssetDeltas);
            Assert.Empty(final.Stats);
            Assert.Empty(final.Events);
        }
        await recovered.AdvanceForMemberAsync(fixture.Members[1], checkpointAt.AddHours(2));
        Assert.Equal(2, fixture.Store.CommittedCount);
        Assert.Equal(ended, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
    }

    /// <summary>旧配表检查点按最后确定时刻收尾；三名倒地成员起身，不重演战斗或产生收益。</summary>
    [Fact]
    public async Task IncompatibleCheckpointEndsAtCommittedTimeAndHydratesWithoutMatchingCatalog()
    {
        var fixture = new Fixture();
        var initial = ActionParticipationSessionEngine.Create(fixture.Spec, fixture.Catalog).Export();
        var checkpointAt = StartedAt.AddSeconds(10);
        var oldSpec = fixture.Spec with
        {
            ActionId = new ActionId("combat.retired-session"),
            EncounterId = "encounter.retired-session"
        };
        var fallen = new CombatantSnapshot(0, 0, 1, 0, 0, RespawnAtSeconds: 30);
        var oldDocument = initial with
        {
            Spec = oldSpec,
            GameDataFingerprint = "retired-game-data",
            AdvancedAtUnixSeconds = checkpointAt.ToUnixTimeMilliseconds() / 1000d,
            Sequence = 7,
            CompletedRepeats = 3,
            Expedition = initial.Expedition! with
            {
                EncounterId = oldSpec.EncounterId,
                Phase = CombatExpeditionPhase.Combat,
                PhaseElapsedSeconds = 0,
                PhaseDurationSeconds = 0,
                BattleElapsedSeconds = 0,
                PlayerHitpoints = 0,
                PlayerManapoints = 0,
                PlayerState = fallen,
                PlayerRespawnAtSeconds = fallen.RespawnAtSeconds,
                Allies = initial.Expedition.Allies!.Select(ally => ally with { State = fallen }).ToArray(),
                Enemies = [new ExpeditionEnemyState("enemy:0", new CombatantSnapshot(100, 0, 1, 0, 0))]
            }
        };
        var before = new ActionParticipationSessionRecord(oldSpec.SessionId, Guid.NewGuid(),
            JsonSerializer.Serialize(oldSpec, PersistenceJson.Options),
            JsonSerializer.Serialize(oldDocument, PersistenceJson.Options), 4, checkpointAt);
        await fixture.Store.CreateOrInitializeAsync(before);

        var delivered = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], checkpointAt.AddMinutes(1));
        Assert.Single(delivered);
        var checkpoint = (await fixture.Store.LoadAsync(oldSpec.SessionId))!;
        Assert.Equal(checkpointAt, checkpoint.LastSimulatedAt);
        Assert.Equal(checkpointAt, checkpoint.EndedAt);
        Assert.Equal("game-data-changed", checkpoint.EndReason);
        Assert.Equal(before.Version + 1, checkpoint.Version);
        var ended = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(checkpoint.StateJson, PersistenceJson.Options)!;
        Assert.Equal(oldDocument.Sequence + 1, ended.Sequence);
        Assert.Equal(oldDocument.AdvancedAtUnixSeconds, ended.AdvancedAtUnixSeconds);
        Assert.Equal(oldDocument.AdvancedAtUnixSeconds, ended.EndsAtUnixSeconds);
        Assert.Equal(oldDocument.GameDataFingerprint, ended.GameDataFingerprint);
        Assert.Equal(oldDocument.CompletedRepeats, ended.CompletedRepeats);

        for (var index = 0; index < fixture.Members.Length; index++)
        {
            var member = fixture.Members[index];
            var stored = Assert.Single(await fixture.Store.LoadPendingAsync(member));
            var final = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(stored.PayloadJson, PersistenceJson.Options)!;
            Assert.Equal(new PlayerId(member), final.PlayerId);
            Assert.Equal(ended.Sequence, stored.Sequence);
            Assert.Equal(ended.Sequence, final.Sequence);
            Assert.Equal(checkpointAt, stored.CreatedAt);
            Assert.Equal(checkpointAt, final.From);
            Assert.Equal(checkpointAt, final.To);
            Assert.Equal(oldDocument.AdvancedAtUnixSeconds, final.EndsAtUnixSeconds);
            Assert.Equal(1d, final.Hitpoints);
            Assert.Equal(oldDocument.CompletedRepeats, final.CompletedRepeats);
            Assert.Empty(final.AssetDeltas);
            Assert.Empty(final.Stats);
            Assert.Empty(final.Events);
            Assert.Null(final.Expedition);
            Assert.Null(final.CombatStats);

            var projected = Assert.Single(await fixture.NewRuntime().LoadPendingAsync(member));
            var hydrated = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(projected.PayloadJson, PersistenceJson.Options)!;
            Assert.Equal(final.Sequence, hydrated.Sequence);
            Assert.Equal(final.EndsAtUnixSeconds, hydrated.EndsAtUnixSeconds);
            Assert.Equal(1d, hydrated.Hitpoints);
            Assert.Empty(hydrated.AssetDeltas);
            Assert.Empty(hydrated.Stats);
            Assert.Empty(hydrated.Events);
            Assert.Equal(oldSpec.EncounterId, hydrated.Expedition!.EncounterId);
            Assert.Equal(index, hydrated.Expedition.SelfIndex);
            Assert.Equal(1d, hydrated.Expedition.PlayerHitpoints);
            Assert.Equal(1d, hydrated.Expedition.PlayerState!.Hitpoints);
            Assert.Null(hydrated.Expedition.PlayerRespawnAtSeconds);
            Assert.Null(hydrated.Expedition.PlayerState.RespawnAtSeconds);
            Assert.All(hydrated.Expedition.Allies!, ally =>
            {
                Assert.Equal(1d, ally.State.Hitpoints);
                Assert.Null(ally.State.RespawnAtSeconds);
            });
            // CombatStats 是旧检查点展示账本，允许保留；终态收益统计由 Stats 断言为空。
        }
        await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[2], checkpointAt.AddHours(1));
        Assert.Equal(1, fixture.Store.CommittedCount);
        Assert.Equal(checkpoint, await fixture.Store.LoadAsync(oldSpec.SessionId));
        foreach (var member in fixture.Members)
            Assert.Single(await fixture.Store.LoadPendingAsync(member));
    }

    /// <summary>单名在线成员也唤醒唯一战斗自然边界；全员离线后既不推进也不保留唤醒时刻。</summary>
    [Fact]
    public async Task OneOnlineMemberSchedulesNearCombatBoundaryAndAllOfflineClearsIt()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var initial = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var initialDocument = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(initial.StateJson, PersistenceJson.Options)!;
        var target = StartedAt.AddSeconds(initialDocument.Expedition!.PhaseDurationSeconds).AddMilliseconds(100);
        await fixture.Presence.RegisterAsync(fixture.Members[1], "乙", fixture.Spec.NodeId);

        await fixture.Runtime.AdvanceOnlineAsync(target);
        var checkpoint = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(checkpoint.StateJson, PersistenceJson.Options)!;
        Assert.Equal(target, checkpoint.LastSimulatedAt);
        Assert.Equal(CombatExpeditionPhase.Combat, document.Expedition!.Phase);
        var nextAttackAt = checkpoint.LastSimulatedAt.AddSeconds(
            document.Expedition.PlayerNextAttackAtSeconds - document.Expedition.BattleElapsedSeconds);
        var boundary = Assert.IsType<DateTimeOffset>(fixture.Runtime.NextAdvanceBoundaryUtc);
        Assert.True(boundary > checkpoint.LastSimulatedAt);
        Assert.True(boundary <= nextAttackAt);
        Assert.True(boundary <= checkpoint.LastSimulatedAt.AddSeconds(1));
        Assert.Equal(1, fixture.Store.CommittedCount);
        foreach (var member in fixture.Members)
            Assert.Equal(document.Sequence, Assert.Single(await fixture.Store.LoadPendingAsync(member)).Sequence);

        await fixture.Presence.UnregisterAsync(fixture.Members[1]);
        await fixture.Runtime.AdvanceOnlineAsync(target.AddMinutes(1));
        Assert.Null(fixture.Runtime.NextAdvanceBoundaryUtc);
        Assert.Equal(checkpoint, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(1, fixture.Store.CommittedCount);
        foreach (var member in fixture.Members)
            Assert.Single(await fixture.Store.LoadPendingAsync(member));
    }

    private sealed class Fixture
    {
        public readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();
        public readonly MemoryStore Store = new();
        public readonly OnlinePresenceRegistry Presence = new();
        public readonly Guid[] Members = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        public readonly ActionParticipationSpec Spec;
        public readonly ActionParticipationRuntime Runtime;

        public Fixture()
        {
            var stats = CombatantStats.Create(50_000, 2_000, 500, 1);
            Spec = new ActionParticipationSpec($"action:{Guid.NewGuid():N}", "encounter.qi-beast", "node.test", 97,
                0, StartedAt.ToUnixTimeMilliseconds() / 1000d,
                Members.Select((member, index) => new ActionParticipantSnapshot(member.ToString("D"),
                    new[] { "甲", "乙", "丙" }[index], 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints,
                    stats, [], CombatSkills: PlayerCombatSkillState.Empty)).ToArray())
            { ActionId = new ActionId("combat.hunt-qi-beast") };
            Runtime = NewRuntime();
        }

        public ActionParticipationRuntime NewRuntime() => new(Catalog, Store, Presence,
            new FixedClock(), NullLogger<ActionParticipationRuntime>.Instance);
    }

    private sealed class FixedClock : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow => StartedAt;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => throw new NotSupportedException();
    }

    /// <summary>模拟检查点与收件箱同事务成功/失败，故障开关只作用于单次提交。</summary>
    private sealed class MemoryStore : IParticipationSessionStore
    {
        private readonly ConcurrentDictionary<string, ActionParticipationSessionRecord> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, List<ParticipationInboxItem>> _inbox = new();
        public int CommittedCount;
        public int CommitAttempts;
        public bool FailBeforeCommit;
        public bool LoseCommitResponse;
        public bool UnknownBeforeCommit;
        public DateTimeOffset? OfflineExpiry;
        public string? InvalidationReason;

        public ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));

        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ActionParticipationSessionRecord>>(_sessions.Values.Where(item => item.EndedAt is null).ToArray());

        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ActionParticipationSessionRecord>>(_sessions.Values.Where(item => item.EndedAt is null
                && JsonSerializer.Deserialize<ActionParticipationSpec>(item.SpecJson, PersistenceJson.Options)!.Members
                    .Any(member => member.PlayerId == playerId.ToString("D"))).ToArray());

        public ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit,
            IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(OfflineExpiry);

        public ValueTask<string?> GetInvalidationReasonAsync(string sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InvalidationReason);

        public ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_sessions.GetOrAdd(session.SessionId, session));

        public async ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
            DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
            DateTimeOffset? endedAt = null, string? endReason = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CommitAttempts);
            await Task.Yield();
            if (FailBeforeCommit)
            {
                FailBeforeCommit = false;
                throw new InvalidOperationException("受控事务回滚");
            }
            if (UnknownBeforeCommit)
            {
                UnknownBeforeCommit = false;
                throw new ParticipationSessionCommitOutcomeUnknownException(sessionId, expectedVersion + 1, new IOException("受控提交结局未知"));
            }
            var before = _sessions[sessionId];
            if (before.Version != expectedVersion || before.EndedAt is not null) return false;
            _sessions[sessionId] = before with { Version = expectedVersion + 1, StateJson = stateJson,
                LastSimulatedAt = lastSimulatedAt, EndedAt = endedAt, EndReason = endReason };
            foreach (var item in inbox) _inbox.GetOrAdd(item.PlayerId, _ => []).Add(item);
            Interlocked.Increment(ref CommittedCount);
            if (LoseCommitResponse)
            {
                LoseCommitResponse = false;
                throw new ParticipationSessionCommitOutcomeUnknownException(sessionId, expectedVersion + 1, new IOException("受控提交响应丢失"));
            }
            return true;
        }

        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ParticipationInboxItem>>(_inbox.TryGetValue(playerId, out var items) ? items.ToArray() : []);
    }
}
