using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Server.Clean;
using Server.Clean.Social;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

/// <summary>覆盖会话宿主的所有权、挂起恢复与持久化失败边界；战斗数学由引擎测试单独验证。</summary>
public sealed class ActionParticipationRuntimeTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>全员离线的成员读取不推进，真实回线共享一个检查点，宿主重建后确定性续写。</summary>
    [Fact]
    public async Task OfflineSessionResumesOnceForConcurrentMembersAndSurvivesHostRestart()
    {
        var fixture = new Fixture();
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var initial = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        Assert.Equal(1, initial.Version);
        var firstTarget = StartedAt.AddSeconds(60);
        await fixture.Runtime.AdvanceOnlineAsync(firstTarget);
        var offlineDeliveries = await Task.WhenAll(fixture.Members.Select(member =>
            fixture.Runtime.AdvanceForMemberAsync(member, firstTarget).AsTask()));
        Assert.Equal(0, fixture.Store.CommittedCount);
        Assert.Equal(initial, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.All(offlineDeliveries, items => Assert.Equal(1, Assert.Single(items).Sequence));

        var deliveries = await Task.WhenAll(fixture.Members.Select(async member =>
        {
            await using var connection = await fixture.Runtime.PrepareConnectionAsync(member, firstTarget);
            await fixture.Presence.RegisterAsync(member, "成员", fixture.Spec.NodeId);
            return await fixture.Runtime.AdvanceForMemberAsync(member, firstTarget);
        }));
        Assert.Equal(1, fixture.Store.CommittedCount);
        Assert.All(deliveries, items => Assert.Equal(new long[] { 1, 2 }, items.Select(item => item.Sequence)));

        var checkpoint = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        var nextTarget = firstTarget.AddSeconds(15);
        await fixture.NewRuntime().AdvanceOnlineAsync(nextTarget);
        var resumed = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        Assert.Equal(checkpoint!.Version + 1, resumed!.Version);
        Assert.Equal(nextTarget, resumed.LastSimulatedAt);
        Assert.Equal(2, fixture.Store.CommittedCount);
        var pending = await fixture.Store.LoadPendingAsync(fixture.Members[0]);
        Assert.Equal(new long[] { 1, 2, 3 }, pending.Select(item => item.Sequence));

        var expected = ActionParticipationSessionEngine.Create(fixture.Spec, fixture.Catalog);
        expected.AdvanceTo(StartedAt);
        expected.SwitchToOffline();
        expected.AdvanceTo(firstTarget);
        expected.SwitchToOnline();
        expected.AdvanceTo(nextTarget);
        Assert.Equal(JsonSerializer.Serialize(expected.Export(), PersistenceJson.Options), resumed.StateJson);
    }

    /// <summary>COMMIT 前失败不污染下次推进；COMMIT 已成功但响应丢失时读取检查点确认，不重复投递。</summary>
    [Fact]
    public async Task CommitFailureDiscardsTransientEngineAndLostResponseConfirmsPersistedState()
    {
        var fixture = new Fixture(withConsumables: true);
        await fixture.Presence.RegisterAsync(fixture.Members[0], "甲", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var initial = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        var initialBalance = fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId);
        fixture.Store.FailBeforeCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60)).AsTask());
        Assert.Equal(0, fixture.Store.CommittedCount);
        Assert.Equal(initial, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(initialBalance, fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId));
        Assert.Equal(1, Assert.Single(await fixture.Store.LoadPendingAsync(fixture.Members[0])).Sequence);

        fixture.Store.LoseCommitResponse = true;
        var inbox = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60));
        Assert.Equal(new long[] { 1, 2 }, inbox.Select(item => item.Sequence));
        Assert.Equal(1, fixture.Store.CommittedCount);
        var committed = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var balance = fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId);
        Assert.True(balance.Quantity < initialBalance.Quantity);
        var transactions = fixture.Store.EconomyTransactionCount;
        var unchanged = await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[2], StartedAt.AddSeconds(60));
        Assert.Equal(new long[] { 1, 2 }, unchanged.Select(item => item.Sequence));
        Assert.Equal(1, fixture.Store.CommittedCount);
        Assert.Equal(balance, fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId));
        Assert.Equal(transactions, fixture.Store.EconomyTransactionCount);

        var expected = ActionParticipationSessionEngine.Restore(
            JsonSerializer.Deserialize<ActionParticipationSessionDocument>(initial!.StateJson, PersistenceJson.Options)!, fixture.Catalog);
        expected.SetWorkingWallet([new(new PlayerId(initialBalance.PlayerId),
            new(new ItemId(initialBalance.AssetId), initialBalance.Variant), initialBalance.Quantity, initialBalance.Version)]);
        expected.AdvanceTo(StartedAt.AddSeconds(60));
        Assert.Equal(JsonSerializer.Serialize(expected.Export(), PersistenceJson.Options), committed.StateJson);
        Assert.Equal(initialBalance.Quantity + expected.TakeAssetDeltas()
            .Where(delta => delta.PlayerId.Value == fixture.Members[0] && delta.Asset.ItemId.Value == Fixture.ConsumableId)
            .Sum(delta => delta.QuantityDelta), balance.Quantity);
    }

    /// <summary>未知提交无法确认时停止该会话后续推进，保留原检查点供重启恢复与人工核对。</summary>
    [Fact]
    public async Task UnconfirmedCommitFencesSessionWithoutReplayingOnNextTick()
    {
        var fixture = new Fixture(withConsumables: true);
        await fixture.Presence.RegisterAsync(fixture.Members[0], "甲", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var initial = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        var balance = fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId);
        var transactions = fixture.Store.EconomyTransactionCount;
        fixture.Store.UnknownBeforeCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[0], StartedAt.AddSeconds(60)).AsTask());
        var attempts = fixture.Store.CommitAttempts;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime
            .AdvanceForMemberAsync(fixture.Members[1], StartedAt.AddSeconds(60)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.LoadPendingAsync(fixture.Members[0]).AsTask());
        Assert.Equal(attempts, fixture.Store.CommitAttempts);
        Assert.Equal(0, fixture.Store.CommittedCount);
        Assert.Equal(initial, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(balance, fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId));
        Assert.Equal(transactions, fixture.Store.EconomyTransactionCount);
        Assert.Equal(1, Assert.Single(await fixture.Store.LoadPendingAsync(fixture.Members[0])).Sequence);
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
        await using var connection = await fixture.Runtime.PrepareConnectionAsync(fixture.Members[0], StartedAt.AddSeconds(60));
        var inbox = await fixture.Runtime.LoadPendingAsync(fixture.Members[0]);
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
        await fixture.Presence.RegisterAsync(fixture.Members[0], "甲", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var checkpointAt = StartedAt.AddSeconds(60);
        await fixture.Runtime.AdvanceForMemberAsync(fixture.Members[0], checkpointAt);
        await fixture.Presence.UnregisterAsync(fixture.Members[0]);
        await fixture.Runtime.SynchronizePresenceAsync(fixture.Members[0], checkpointAt);
        var previous = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var before = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(previous.StateJson, PersistenceJson.Options)!;
        var priorDeliveries = await fixture.Store.LoadPendingAsync(fixture.Members[0]);
        fixture.Store.InvalidationReason = "member-unready";

        var recovered = fixture.NewRuntime();
        await using var connection = await recovered.PrepareConnectionAsync(fixture.Members[0], checkpointAt.AddHours(1));
        var deliveries = await recovered.LoadPendingAsync(fixture.Members[0]);
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
        Assert.Equal(3, fixture.Store.CommittedCount);
        Assert.Equal(ended, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
    }

    /// <summary>旧配表检查点按最后确定时刻收尾；三名倒地成员起身，不重演战斗或产生收益。</summary>
    [Fact]
    public async Task IncompatibleCheckpointEndsAtCommittedTimeAndHydratesWithoutMatchingCatalog()
    {
        var fixture = new Fixture();
        var engine = ActionParticipationSessionEngine.Create(fixture.Spec, fixture.Catalog);
        engine.SwitchToOffline();
        var initial = engine.Export();
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
            NextConsumableEvaluationAtUnixSeconds = checkpointAt.AddSeconds(5).ToUnixTimeMilliseconds() / 1000d,
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

        await using var connection = await fixture.Runtime.PrepareConnectionAsync(fixture.Members[0], checkpointAt.AddMinutes(1));
        var delivered = await fixture.Runtime.LoadPendingAsync(fixture.Members[0]);
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
        await fixture.Presence.RegisterAsync(fixture.Members[1], "乙", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var initial = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var initialDocument = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(initial.StateJson, PersistenceJson.Options)!;
        var target = StartedAt.AddSeconds(initialDocument.Expedition!.PhaseDurationSeconds).AddMilliseconds(100);

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
            Assert.Equal(new long[] { 1, document.Sequence }, (await fixture.Store.LoadPendingAsync(member)).Select(item => item.Sequence));

        await fixture.Presence.UnregisterAsync(fixture.Members[1]);
        await fixture.Runtime.SynchronizePresenceAsync(fixture.Members[1], target);
        var offline = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        Assert.Equal(target, offline.LastSimulatedAt);
        Assert.Equal(checkpoint.Version + 1, offline.Version);
        Assert.Equal(ActionParticipationInventoryMode.Offline,
            JsonSerializer.Deserialize<ActionParticipationSessionDocument>(offline.StateJson, PersistenceJson.Options)!.InventoryMode);
        await fixture.Runtime.AdvanceOnlineAsync(target.AddMinutes(1));
        Assert.Null(fixture.Runtime.NextAdvanceBoundaryUtc);
        Assert.Equal(offline, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(2, fixture.Store.CommittedCount);
        foreach (var member in fixture.Members)
            Assert.Equal(new long[] { 1, 2, 3 }, (await fixture.Store.LoadPendingAsync(member)).Select(item => item.Sequence));
    }

    [Fact]
    public async Task OfflineOpeningAndConnectionRefundPublishAssetsAndInboxOnlyOnce()
    {
        var fixture = new Fixture(withConsumables: true);
        var changed = new List<Guid>();
        fixture.Runtime.WalletChanged += changed.Add;
        var partyId = Guid.NewGuid();
        await fixture.Runtime.CreateAsync(partyId, fixture.Spec);
        var opening = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        Assert.Equal(1, opening.Version);
        Assert.Equal(0, fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId).Quantity);
        var reserve = Assert.Single(await fixture.Runtime.LoadOfflineReservesAsync(fixture.Members[0]));
        Assert.Equal(Fixture.ConsumableId, reserve.ItemId);
        Assert.Equal("", reserve.Variant);
        Assert.Equal(4, reserve.Quantity);
        foreach (var member in fixture.Members)
            Assert.Equal(1, Assert.Single(await fixture.Store.LoadPendingAsync(member)).Sequence);
        Assert.Equal(new[] { fixture.Members[0] }, changed);
        Assert.Equal(1, fixture.Store.EconomyTransactionCount);

        await fixture.Runtime.CreateAsync(partyId, fixture.Spec);
        Assert.Equal(opening, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(reserve, Assert.Single(await fixture.Runtime.LoadOfflineReservesAsync(fixture.Members[0])));
        Assert.Single(changed);
        Assert.Equal(1, fixture.Store.EconomyTransactionCount);

        await using (var connection = await fixture.Runtime.PrepareConnectionAsync(fixture.Members[0], StartedAt))
        {
            Assert.Equal(4, fixture.Store.Balance(fixture.Members[0], Fixture.ConsumableId).Quantity);
            Assert.Empty(await fixture.Runtime.LoadOfflineReservesAsync(fixture.Members[0]));
            Assert.Equal(new long[] { 1, 2 }, (await fixture.Runtime.LoadPendingAsync(fixture.Members[0])).Select(item => item.Sequence));
            await fixture.Presence.RegisterAsync(fixture.Members[0], "甲", fixture.Spec.NodeId);
        }
        Assert.Equal(new[] { fixture.Members[0], fixture.Members[0] }, changed);
        Assert.Equal(2, fixture.Store.EconomyTransactionCount);
        Assert.Equal(1, fixture.Store.CommittedCount);
        var connected = await fixture.Store.LoadAsync(fixture.Spec.SessionId);
        await fixture.Runtime.SynchronizePresenceAsync(fixture.Members[0], StartedAt.AddSeconds(10));
        Assert.Equal(connected, await fixture.Store.LoadAsync(fixture.Spec.SessionId));
        Assert.Equal(1, fixture.Store.CommittedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WalletReadSetAndCheckpointCasRejectWithoutPartialPublication(bool missingBalanceChanges)
    {
        var fixture = new Fixture(withConsumables: true);
        await fixture.Presence.RegisterAsync(fixture.Members[0], "甲", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);
        var before = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var key = new ParticipationAssetKey(fixture.Members[0], Fixture.ConsumableId, "");
        var missing = key with { Variant = "bound" };
        var readSet = await fixture.Store.LoadConsumableBalancesAsync([key, key, missing]);
        Assert.Equal(2, readSet.Count);
        Assert.Equal(new AssetBalanceRecord(missing.PlayerId, missing.AssetId, missing.Variant, 0, 0),
            Assert.Single(readSet, row => row.Variant == missing.Variant));
        fixture.Store.SeedBalance(key.PlayerId, key.AssetId, missingBalanceChanges ? missing.Variant : key.Variant,
            missingBalanceChanges ? 2 : 4);
        var changedBalance = fixture.Store.Balance(key.PlayerId, key.AssetId);
        var target = StartedAt.AddSeconds(10);
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(before.StateJson, PersistenceJson.Options)!
            with { Sequence = before.Version + 1, AdvancedAtUnixSeconds = target.ToUnixTimeMilliseconds() / 1000d };
        var state = JsonSerializer.Serialize(document, PersistenceJson.Options);
        var initialInbox = Assert.Single(await fixture.Store.LoadPendingAsync(key.PlayerId));
        var settlement = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(initialInbox.PayloadJson, PersistenceJson.Options)!
            with { Sequence = before.Version + 1, From = StartedAt, To = target };
        var nextInbox = initialInbox with { Sequence = settlement.Sequence, CreatedAt = target,
            PayloadJson = JsonSerializer.Serialize(settlement, PersistenceJson.Options) };
        var transaction = new EconomyTransactionRequest(Guid.NewGuid(), $"test:{Guid.NewGuid():N}", "participation.assets",
            key.PlayerId, [new(key.PlayerId, key.AssetId, key.Variant, -1)], target);

        Assert.False(await fixture.Store.CommitAdvanceAsync(before.SessionId, before.Version, state, target, [nextInbox],
            economy: new(transaction, readSet)));
        Assert.Equal(before, await fixture.Store.LoadAsync(before.SessionId));
        Assert.Equal(initialInbox, Assert.Single(await fixture.Store.LoadPendingAsync(key.PlayerId)));
        Assert.Equal(changedBalance, fixture.Store.Balance(key.PlayerId, key.AssetId));
        Assert.Equal(0, fixture.Store.EconomyTransactionCount);
        Assert.Equal(0, fixture.Store.CommittedCount);

        var fresh = await fixture.Store.LoadConsumableBalancesForItemsAsync([missing]);
        Assert.Equal(missingBalanceChanges ? 2 : 1, fresh.Count);
        Assert.Contains(changedBalance, fresh);
        Assert.False(await fixture.Store.CommitAdvanceAsync(before.SessionId, before.Version - 1, state, target, [nextInbox],
            economy: new(transaction, fresh)));
        Assert.Equal(before, await fixture.Store.LoadAsync(before.SessionId));
        Assert.Equal(initialInbox, Assert.Single(await fixture.Store.LoadPendingAsync(key.PlayerId)));
        Assert.Equal(changedBalance, fixture.Store.Balance(key.PlayerId, key.AssetId));

        Assert.True(await fixture.Store.CommitAdvanceAsync(before.SessionId, before.Version, state, target, [nextInbox],
            economy: new(transaction, fresh)));
        var committed = (await fixture.Store.LoadAsync(before.SessionId))!;
        Assert.Equal(before.Version + 1, committed.Version);
        Assert.Equal(state, committed.StateJson);
        Assert.Equal(target, committed.LastSimulatedAt);
        var spent = fixture.Store.Balance(key.PlayerId, key.AssetId);
        Assert.Equal(changedBalance.Quantity - 1, spent.Quantity);
        Assert.Equal(changedBalance.Version + 1, spent.Version);
        Assert.Equal(1, fixture.Store.EconomyTransactionCount);
        Assert.Equal(new long[] { 1, 2 }, (await fixture.Store.LoadPendingAsync(key.PlayerId)).Select(item => item.Sequence));
        Assert.False(await fixture.Store.CommitAdvanceAsync(before.SessionId, before.Version, state, target, [nextInbox],
            economy: new(transaction, fresh)));
        Assert.Equal(committed, await fixture.Store.LoadAsync(before.SessionId));
        Assert.Equal(spent, fixture.Store.Balance(key.PlayerId, key.AssetId));
        Assert.Equal(1, fixture.Store.EconomyTransactionCount);
        Assert.Equal(1, fixture.Store.CommittedCount);
        Assert.Equal(new long[] { 1, 2 }, (await fixture.Store.LoadPendingAsync(key.PlayerId)).Select(item => item.Sequence));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleBackgroundPresenceScanCannotReverseCompletedModeSwitch(bool initiallyOnline)
    {
        var fixture = new Fixture(withConsumables: true);
        var member = fixture.Members[0];
        if (initiallyOnline) await fixture.Presence.RegisterAsync(member, "甲", fixture.Spec.NodeId);
        await fixture.Runtime.CreateAsync(Guid.NewGuid(), fixture.Spec);

        // 扫描已采集存在感，但列表查询返回前另一连接完成模式切换。
        fixture.Store.BeforeListActive = async () =>
        {
            if (initiallyOnline) await fixture.Presence.UnregisterAsync(member);
            else await fixture.Presence.RegisterAsync(member, "甲", fixture.Spec.NodeId);
            await fixture.Runtime.SynchronizePresenceAsync(member, StartedAt);
        };
        if (initiallyOnline) await fixture.Runtime.AdvanceOnlineAsync(StartedAt);
        else await fixture.Runtime.AdvanceOfflineDueAsync(StartedAt, TimeSpan.FromHours(1));

        var checkpoint = (await fixture.Store.LoadAsync(fixture.Spec.SessionId))!;
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(checkpoint.StateJson, PersistenceJson.Options)!;
        Assert.Equal(initiallyOnline ? ActionParticipationInventoryMode.Offline : ActionParticipationInventoryMode.Online,
            document.InventoryMode);
        Assert.Equal(initiallyOnline ? 0 : 4, fixture.Store.Balance(member, Fixture.ConsumableId).Quantity);
        Assert.Equal(initiallyOnline ? 4 : 0,
            (await fixture.Runtime.LoadOfflineReservesAsync(member)).Sum(row => row.Quantity));
    }

    private sealed class Fixture
    {
        public const string ConsumableId = "item.leiji-talisman";
        public readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();
        public readonly MemoryStore Store = new();
        public readonly OnlinePresenceRegistry Presence = new();
        public readonly Guid[] Members = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        public readonly ActionParticipationSpec Spec;
        public readonly ActionParticipationRuntime Runtime;

        public Fixture(bool withConsumables = false)
        {
            var stats = CombatantStats.Create(50_000, 2_000, 500, 1);
            Spec = new ActionParticipationSpec($"action:{Guid.NewGuid():N}", "encounter.qi-beast", "node.test", 97,
                0, StartedAt.ToUnixTimeMilliseconds() / 1000d,
                Members.Select((member, index) => new ActionParticipantSnapshot(member.ToString("D"),
                    new[] { "甲", "乙", "丙" }[index], 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints,
                    stats, [], CombatSkills: PlayerCombatSkillState.Empty,
                    ConsumableSlots: withConsumables && index == 0
                        ? [new(ConsumableId, "combat", AutoUseSlotState.SlotGroupGeneral, 0)] : null)).ToArray())
            { ActionId = new ActionId("combat.hunt-qi-beast") };
            if (withConsumables) Store.SeedBalance(Members[0], ConsumableId, "", 4);
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

    /// <summary>在同一锁内核对检查点 CAS 与钱包读集，原子发布余额、流水和收件箱；故障只影响单次提交。</summary>
    private sealed class MemoryStore : IParticipationSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ActionParticipationSessionRecord> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<ParticipationInboxItem>> _inbox = new();
        private readonly Dictionary<ParticipationAssetKey, AssetBalanceRecord> _balances = new();
        private readonly HashSet<string> _transactions = new(StringComparer.Ordinal);
        public int CommittedCount;
        public int CommitAttempts;
        public bool FailBeforeCommit;
        public bool LoseCommitResponse;
        public bool UnknownBeforeCommit;
        public DateTimeOffset? OfflineExpiry;
        public string? InvalidationReason;
        public Func<ValueTask>? BeforeListActive;

        public int EconomyTransactionCount { get { lock (_gate) return _transactions.Count; } }

        public AssetBalanceRecord Balance(Guid playerId, string assetId, string variant = "")
        {
            lock (_gate) return ReadBalance(new(playerId, assetId, variant));
        }

        public void SeedBalance(Guid playerId, string assetId, string variant, double quantity)
        {
            lock (_gate)
            {
                var key = new ParticipationAssetKey(playerId, assetId, variant);
                _balances[key] = new(playerId, assetId, variant, quantity, ReadBalance(key).Version + 1);
            }
        }

        public ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));
        }

        public async ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
        {
            if (BeforeListActive is { } before)
            {
                BeforeListActive = null;
                await before();
            }
            lock (_gate) return _sessions.Values.Where(item => item.EndedAt is null).ToArray();
        }

        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult<IReadOnlyList<ActionParticipationSessionRecord>>(
                _sessions.Values.Where(item => item.EndedAt is null
                    && JsonSerializer.Deserialize<ActionParticipationSpec>(item.SpecJson, PersistenceJson.Options)!.Members
                        .Any(member => member.PlayerId == playerId.ToString("D"))).ToArray());
        }

        public ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit,
            IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(OfflineExpiry);

        public ValueTask<string?> GetInvalidationReasonAsync(string sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InvalidationReason);

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesAsync(
            IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(keys.Distinct().Select(ReadBalance).ToArray());
        }

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesForItemsAsync(
            IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var rows = keys.Select(key => key with { Variant = "" }).Distinct().SelectMany(key =>
                    _balances.Keys.Where(candidate => candidate.PlayerId == key.PlayerId && candidate.AssetId == key.AssetId)
                        .Append(key).Distinct().Select(ReadBalance))
                    .OrderBy(row => row.PlayerId).ThenBy(row => row.AssetId, StringComparer.Ordinal)
                    .ThenBy(row => row.Variant, StringComparer.Ordinal).ToArray();
                return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(rows);
            }
        }

        public ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session,
            ParticipationSessionEconomyCommit? economy = null, IReadOnlyList<ParticipationInboxItem>? inbox = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(session.SessionId, out var existing)) return ValueTask.FromResult(existing);
                var balances = StageEconomy(economy)
                    ?? throw new ParticipationSessionEconomyConflictException(session.SessionId);
                _sessions.Add(session.SessionId, session);
                Publish(economy, balances, inbox ?? []);
                return ValueTask.FromResult(session);
            }
        }

        public async ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
            DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
            DateTimeOffset? endedAt = null, string? endReason = null,
            ParticipationSessionEconomyCommit? economy = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            lock (_gate)
            {
                CommitAttempts++;
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
                var balances = StageEconomy(economy);
                if (balances is null) return false;
                _sessions[sessionId] = before with { Version = expectedVersion + 1, StateJson = stateJson,
                    LastSimulatedAt = lastSimulatedAt, EndedAt = endedAt, EndReason = endReason };
                Publish(economy, balances, inbox);
                CommittedCount++;
                if (LoseCommitResponse)
                {
                    LoseCommitResponse = false;
                    throw new ParticipationSessionCommitOutcomeUnknownException(sessionId, expectedVersion + 1, new IOException("受控提交响应丢失"));
                }
                return true;
            }
        }

        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult<IReadOnlyList<ParticipationInboxItem>>(
                _inbox.TryGetValue(playerId, out var items) ? items.OrderBy(item => item.Sequence).ToArray() : []);
        }

        private AssetBalanceRecord ReadBalance(ParticipationAssetKey key) => _balances.GetValueOrDefault(key)
            ?? new(key.PlayerId, key.AssetId, key.Variant, 0, 0);

        private AssetBalanceRecord[]? StageEconomy(ParticipationSessionEconomyCommit? economy)
        {
            if (economy is null) return [];
            foreach (var expected in economy.ReadSet)
            {
                var actual = ReadBalance(new(expected.PlayerId, expected.AssetId, expected.Variant));
                if (actual.Quantity != expected.Quantity || actual.Version != expected.Version) return null;
            }
            if (economy.Transaction is not { } transaction) return [];
            if (_transactions.Contains(transaction.IdempotencyKey)) return null;
            var balances = transaction.Deltas.GroupBy(delta => new ParticipationAssetKey(delta.PlayerId, delta.AssetId, delta.Variant))
                .Select(group =>
                {
                    var before = ReadBalance(group.Key);
                    return before with { Quantity = before.Quantity + group.Sum(delta => delta.Delta), Version = before.Version + 1 };
                }).ToArray();
            if (balances.Any(balance => !double.IsFinite(balance.Quantity) || balance.Quantity < 0))
                throw new InvalidOperationException("受控资产事务余额不足或无效");
            return balances;
        }

        private void Publish(ParticipationSessionEconomyCommit? economy, IReadOnlyList<AssetBalanceRecord> balances,
            IReadOnlyList<ParticipationInboxItem> inbox)
        {
            foreach (var balance in balances) _balances[new(balance.PlayerId, balance.AssetId, balance.Variant)] = balance;
            if (economy?.Transaction is { } transaction) _transactions.Add(transaction.IdempotencyKey);
            foreach (var item in inbox)
            {
                if (!_inbox.TryGetValue(item.PlayerId, out var pending)) _inbox.Add(item.PlayerId, pending = []);
                pending.Add(item);
            }
        }
    }
}
