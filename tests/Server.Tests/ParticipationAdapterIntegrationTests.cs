using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>真实 PostgreSQL、会话宿主与跨分片成员适配器的完整结算装配链。</summary>
[Collection("PostgresIntegration")]
public sealed class ParticipationAdapterIntegrationTests
{
    /// <summary>三人只推进一个会话；离线回线消费既有结算，散场恢复各自原行动且不重奖。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ThreeMembersAcrossShardsConsumeOneAuthorityAndResumePersonalQueues()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(2, fixture.Players.Select(player => fixture.Ecs.GetWorld(player)).Distinct().Count());
        foreach (var player in fixture.Players)
        {
            var state = fixture.Adapter.TryGetLoaded(player)!.State;
            Assert.Equal(fixture.SessionId, state.Actions[0].Participation!.Spec!.SessionId);
            Assert.Equal(fixture.PersonalActions[player], state.Actions[1].Id);
        }

        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(60));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        var checkpoint = (await fixture.Store.LoadAsync(fixture.SessionId))!;
        Assert.Equal(1, checkpoint.Version);
        Assert.Equal(3L, await fixture.SettlementTransactionCountAsync());
        foreach (var player in fixture.Players)
        {
            Assert.Empty(await fixture.Store.LoadPendingAsync(player));
            Assert.Equal(1, fixture.Adapter.TryGetLoaded(player)!.State.Actions[0].Participation!.AppliedSequence);
        }
        Assert.True(fixture.Players.Sum(player => fixture.Adapter.TryGetLoaded(player)!.State.Progression.CombatExperience) > 0);
        var afterFirst = fixture.Players.ToDictionary(player => player, fixture.RewardSnapshot);
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Equal(checkpoint.Version, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(3L, await fixture.SettlementTransactionCountAsync());
        foreach (var player in fixture.Players) Assert.Equal(afterFirst[player], fixture.RewardSnapshot(player));

        var offline = fixture.Players[2];
        await fixture.Presence.UnregisterAsync(offline);
        await fixture.Adapter.RemoveAsync(offline, flush: true, offlineSince: null);
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(90));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(120));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Equal(2, (await fixture.Store.LoadPendingAsync(offline)).Count);
        var beforeReturn = (await fixture.Store.LoadAsync(fixture.SessionId))!;
        var returned = await fixture.Adapter.LoadAsync(offline);
        await fixture.Presence.RegisterAsync(offline, returned.State.DisplayName, returned.State.World.NodeId);
        Assert.Equal(beforeReturn.Version, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(3, returned.State.Actions[0].Participation!.AppliedSequence);
        Assert.Empty(await fixture.Store.LoadPendingAsync(offline));
        Assert.Equal(9L, await fixture.SettlementTransactionCountAsync());
        var waves = fixture.Players.Select(player => fixture.Adapter.TryGetLoaded(player)!.State.Actions[0].CompletedRepeats).ToArray();
        Assert.All(waves, wave => Assert.Equal(waves[0], wave));
        var returnedRewards = fixture.RewardSnapshot(offline);
        await fixture.Adapter.LoadAsync(offline);
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Equal(returnedRewards, fixture.RewardSnapshot(offline));
        Assert.Equal(9L, await fixture.SettlementTransactionCountAsync());

        await fixture.Runtime.EndAsync(fixture.SessionId, fixture.Clock.UtcNow, "member-unready");
        var terminal = new Dictionary<Guid, ActionParticipationMemberSettlement>();
        foreach (var player in fixture.Players)
        {
            var item = Assert.Single(await fixture.Store.LoadPendingAsync(player));
            terminal[player] = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(item.PayloadJson, PersistenceJson.Options)!;
        }
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Equal(12L, await fixture.SettlementTransactionCountAsync());
        foreach (var player in fixture.Players)
        {
            var state = fixture.Adapter.TryGetLoaded(player)!.State;
            var resumed = Assert.Single(state.Actions);
            Assert.Equal(fixture.PersonalActions[player], resumed.Id);
            Assert.Equal(-1, resumed.RemainingRepeats);
            Assert.Null(resumed.Participation);
            Assert.Equal(terminal[player].Hitpoints, state.Combat.Hitpoints);
            Assert.Equal(terminal[player].Manapoints, state.Combat.Manapoints);
            Assert.True(state.Combat.Hitpoints > 0);
            Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        }
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Equal(12L, await fixture.SettlementTransactionCountAsync());
    }

    /// <summary>赶路先领取会话终局，不能把参与引用挂在队尾并阻断后续操作。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TravelConsumesTerminalSessionBeforePriorityActionAndReachesDestination()
    {
        await using var fixture = await Fixture.CreateAsync();
        var player = fixture.Players[0];
        var graph = fixture.Catalog.WorldGraph;
        var targetNode = graph.Nodes.First(node => node.Id != "node.yunxi-village"
            && graph.PlanFromPosition(null, 0, "node.yunxi-village", node.Id).TotalTravelSeconds > 0).Id;
        var duration = graph.PlanFromPosition(null, 0, "node.yunxi-village", targetNode).TotalTravelSeconds;
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(60));
        var result = await fixture.Adapter.ExecuteAsync(player, "world.travel",
            JsonSerializer.SerializeToElement(new { targetNodeId = targetNode }), 0, Guid.NewGuid());
        Assert.True(result.Success, result.Message);
        Assert.Equal("member-node-changed", (await fixture.Store.LoadAsync(fixture.SessionId))!.EndReason);
        Assert.DoesNotContain(fixture.Adapter.TryGetLoaded(player)!.State.Actions, action => action.Participation is not null);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        fixture.Clock.AdvanceTo(fixture.Clock.UtcNow.AddSeconds(duration + 1));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        var arrived = fixture.Adapter.TryGetLoaded(player)!.State;
        Assert.Equal(targetNode, arrived.World.NodeId);
        Assert.Null(arrived.World.EdgeId);
        Assert.Equal(fixture.PersonalActions[player], Assert.Single(arrived.Actions).Id);
    }

    /// <summary>租约已被其他实例接管时，旧宿主不能领取收件箱或覆盖个人分段。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LostOwnerCannotCommitPendingParticipationReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(owned: true);
        var player = fixture.Players[0];
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(60));
        await fixture.Runtime.AdvanceOnlineAsync(fixture.Clock.UtcNow);
        await fixture.SqlAsync("UPDATE game.player_runtime_leases SET lease_until=now()-interval '1 second' WHERE player_id=$1", player);
        Assert.True(await new PostgresPlayerOwnershipRepository(fixture.Source)
            .AcquireAsync(player, Guid.NewGuid(), TimeSpan.FromSeconds(45)));
        await Assert.ThrowsAsync<PlayerOwnershipLostException>(async () =>
            await fixture.Adapter.MutateOwnedAsync(player, aggregate => aggregate.SetQueuePaused(true)));
        Assert.Equal(0L, await fixture.SettlementTransactionCountAsync());
        Assert.Single(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(0, fixture.Ecs.ExportSnapshot(player).State.Actions[0].Participation!.AppliedSequence);
    }

    /// <summary>会话回执不能跳过个人暂存物品到期；资产、回执与清空暂存必须同时落盘。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ParticipationReceiptSettlesPersonalStagedAssetsBeforeAdvancingClock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var player = fixture.Players[0];
        await fixture.Adapter.MutateOwnedAsync(player, aggregate =>
            aggregate.Duel.StagedItems.Add(new("item.refined-copper", 7,
                fixture.StartedAt.AddSeconds(20).ToUnixTimeMilliseconds() / 1000d - fixture.Catalog.Duel.StagedSettleSeconds)));
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(60));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        var consumed = fixture.Adapter.TryGetLoaded(player)!;
        Assert.Empty(consumed.State.Duel.StagedItems);
        Assert.Equal(7, Assert.Single(consumed.Assets, asset => asset.AssetId == "item.refined-copper").Quantity);
        Assert.Equal(1, consumed.State.Actions[0].Participation!.AppliedSequence);
        await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: null);
        var restored = await fixture.Adapter.LoadAsync(player);
        Assert.Empty(restored.State.Duel.StagedItems);
        Assert.Equal(7, Assert.Single(restored.Assets, asset => asset.AssetId == "item.refined-copper").Quantity);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
    }

    /// <summary>一个成员收件失败或会话检查点损坏不能冻结其他成员及未参战玩家。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BrokenMemberAndSessionStillAllowHealthyPlayersToAdvance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var personalPlayer = Guid.NewGuid();
        await fixture.SeedPersonalPlayerAsync(personalPlayer);
        var firstTarget = fixture.StartedAt.AddSeconds(60);
        fixture.Clock.AdvanceTo(firstTarget);
        await fixture.Runtime.AdvanceOnlineAsync(firstTarget);
        await fixture.SqlAsync("UPDATE social.party_session_inbox SET payload = jsonb_set(payload,'{playerId}',to_jsonb($3::text)) WHERE session_id=$1 AND player_id=$2",
            fixture.SessionId, fixture.Players[0], Guid.NewGuid().ToString("D"));
        var errors = await Assert.ThrowsAsync<AggregateException>(async () => await fixture.Adapter.AdvanceAllAsync(firstTarget));
        Assert.Contains(errors.InnerExceptions, error => error.Message.Contains(fixture.Players[0].ToString("D"), StringComparison.Ordinal));
        Assert.Equal(2L, await fixture.SettlementTransactionCountAsync());
        Assert.Equal(0, fixture.Adapter.TryGetLoaded(fixture.Players[0])!.State.Actions[0].Participation!.AppliedSequence);
        foreach (var player in fixture.Players.Skip(1))
            Assert.Equal(1, fixture.Adapter.TryGetLoaded(player)!.State.Actions[0].Participation!.AppliedSequence);
        var personalBefore = fixture.Adapter.TryGetLoaded(personalPlayer)!;
        Assert.Equal(firstTarget, personalBefore.LastSimulatedAt);
        Assert.Equal(6, personalBefore.State.Actions[0].CompletedRepeats);

        await fixture.SqlAsync("UPDATE social.party_sessions SET runtime_state = jsonb_set(runtime_state,'{memberRegenCarrySeconds}','[-1,-1,-1]') WHERE session_id=$1", fixture.SessionId);
        var secondTarget = firstTarget.AddSeconds(10);
        fixture.Clock.AdvanceTo(secondTarget);
        await Assert.ThrowsAsync<AggregateException>(async () => await fixture.Adapter.AdvanceAllAsync(secondTarget));
        var personalAfter = fixture.Adapter.TryGetLoaded(personalPlayer)!;
        Assert.Equal(secondTarget, personalAfter.LastSimulatedAt);
        Assert.Equal(7, personalAfter.State.Actions[0].CompletedRepeats);
        Assert.Equal(2L, await fixture.SettlementTransactionCountAsync());
    }

    /// <summary>未收到开场引用的成员仍能消费已退休行动的终局，不因临时屏蔽散场丢失归档依据。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RetiredSessionActionMissingBindingConsumesTerminalReceiptAndKeepsPersonalQueue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var player = fixture.Players[0];
        await fixture.Adapter.MutateOwnedAsync(player, aggregate => aggregate.RemoveAction(aggregate.Actions[0].Id));
        await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: null);
        var row = (await fixture.Store.LoadAsync(fixture.SessionId))!;
        var document = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(row.StateJson, PersistenceJson.Options)!;
        var retiredSpec = document.Spec with { ActionId = new("combat.retired-session") };
        Assert.False(fixture.Catalog.Actions.Contains(retiredSpec.ActionId));
        var oldCheckpoint = document with { Spec = retiredSpec, GameDataFingerprint = "retired-fingerprint" };
        await fixture.SqlAsync("UPDATE social.party_sessions SET spec=$2::jsonb, runtime_state=$3::jsonb WHERE session_id=$1",
            fixture.SessionId, JsonSerializer.Serialize(retiredSpec, PersistenceJson.Options),
            JsonSerializer.Serialize(oldCheckpoint, PersistenceJson.Options));
        await fixture.Runtime.AdvanceOnlineAsync(fixture.StartedAt);
        Assert.Equal("game-data-changed", (await fixture.Store.LoadAsync(fixture.SessionId))!.EndReason);
        var restored = await fixture.Adapter.LoadAsync(player);
        Assert.Equal(fixture.PersonalActions[player], Assert.Single(restored.State.Actions).Id);
        Assert.Null(restored.State.Actions[0].Participation);
        Assert.Equal(document.Expedition!.PlayerHitpoints, restored.State.Combat.Hitpoints);
        Assert.Equal(document.Expedition.PlayerManapoints, restored.State.Combat.Manapoints);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(1L, await fixture.SettlementTransactionCountAsync());
        await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: null);
        var reloaded = await fixture.Adapter.LoadAsync(player);
        Assert.Equal(restored.State.Progression.CombatExperience, reloaded.State.Progression.CombatExperience);
        Assert.Equal(fixture.PersonalActions[player], Assert.Single(reloaded.State.Actions).Id);
        Assert.Equal(1L, await fixture.SettlementTransactionCountAsync());
    }

    /// <summary>终局先被对账消费后，过时的开场投递不能重新插入永不结束的参与引用。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OpeningDeliveryDoesNotReinsertSessionEndedBeforeMemberMutation()
    {
        await using var fixture = await Fixture.CreateAsync(startSession: false);
        var opening = new EndDuringOpeningRuntime(fixture);
        var social = await fixture.CreateSocialRuntimeAsync(opening);
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyReady))).Success);
        Assert.NotNull(opening.SessionId);
        try
        {
            foreach (var player in fixture.Players)
            {
                var state = fixture.Adapter.TryGetLoaded(player)!.State;
                Assert.Equal(fixture.PersonalActions[player], Assert.Single(state.Actions).Id);
                Assert.Empty(await fixture.Store.LoadPendingAsync(player));
            }
            Assert.Equal(3L, await fixture.SettlementTransactionCountAsync());
        }
        finally
        {
            await fixture.SqlAsync("DELETE FROM social.party_session_inbox WHERE session_id=$1", opening.SessionId!);
            await fixture.SqlAsync("DELETE FROM social.party_sessions WHERE session_id=$1", opening.SessionId!);
        }
    }

    /// <summary>末位就绪的 party.ready 走完整仲裁链路：会话行与成员挂载在同指令内完成，
    /// 首个推进边界后成员远征投影即刻显示搜寻相位。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LastReadyStartsSessionAndShowsSearchOnFirstAdvance()
    {
        await using var fixture = await Fixture.CreateAsync(startSession: false);
        var social = await fixture.CreateSocialRuntimeAsync();
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyReady))).Success);
        var session = await fixture.Social.GetActivePartySessionAsync(fixture.PartyId);
        Assert.NotNull(session);
        try
        {
            foreach (var player in fixture.Players)
                Assert.Equal(session.SessionId,
                    fixture.Adapter.TryGetLoaded(player)!.State.Actions[0].Participation?.SessionId);
            fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(3));
            await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
            foreach (var player in fixture.Players)
            {
                var head = fixture.Adapter.TryGetLoaded(player)!.State.Actions[0];
                Assert.NotNull(head.Expedition);
                Assert.Equal(CombatExpeditionPhase.InitialSearch, head.Expedition!.Phase);
            }
        }
        finally
        {
            await fixture.SqlAsync("DELETE FROM social.party_session_inbox WHERE session_id=$1", session.SessionId);
            await fixture.SqlAsync("DELETE FROM social.party_sessions WHERE session_id=$1", session.SessionId);
        }
    }

    /// <summary>未装载成员的开场冻结直接解码持久化真源（冻结采集不强拉装载）；
    /// 开打后名册有人在线，离线成员按 §14.6 对账为常驻实体并就地完成参与挂载。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UnloadedMemberFreezesFromPersistedStateWithoutForceLoad()
    {
        await using var fixture = await Fixture.CreateAsync(startSession: false);
        var offline = fixture.Players[1];
        // 模拟离线成员：卸载实体并注销在线位，仅留持久化状态参与仲裁。
        await fixture.Adapter.RemoveAsync(offline, flush: true, offlineSince: null);
        await fixture.Presence.UnregisterAsync(offline);
        var social = await fixture.CreateSocialRuntimeAsync();
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyReady))).Success);
        var session = await fixture.Social.GetActivePartySessionAsync(fixture.PartyId);
        Assert.NotNull(session);
        try
        {
            // 未装载成员冻结面板入 spec；开打后名册有人在线，离线成员按 §14.6 常驻装载并就地完成挂载。
            var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(session.SpecJson, PersistenceJson.Options)!;
            Assert.Equal(3, spec.Members.Count);
            Assert.Contains(spec.Members, member => member.PlayerId == offline.ToString("D"));
            var resident = fixture.Adapter.TryGetLoaded(offline);
            Assert.NotNull(resident);
            Assert.Equal(session.SessionId, resident.State.Actions[0].Participation?.SessionId);
        }
        finally
        {
            await fixture.SqlAsync("DELETE FROM social.party_session_inbox WHERE session_id=$1", session.SessionId);
            await fixture.SqlAsync("DELETE FROM social.party_sessions WHERE session_id=$1", session.SessionId);
        }
    }

    /// <summary>离线成员散场时被强拉消费终局结算（总纲18 §14.6）：会话内倒地成员随散场复活回血定格为 1 点气血，
    /// 持久化状态即时恢复，下一场会话冻结面板直接读到消费后状态，不能从旧态开下一场（总纲 §21.13）。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UnloadedMemberFreezesFromConsumedTerminalState()
    {
        await using var fixture = await Fixture.CreateAsync(startSession: false);
        var offline = fixture.Players[1];
        // 开打前压低气血：会话战斗内倒地，终局结算记倒地值，消费后复活回血为 1 点气血落盘。
        await fixture.Adapter.MutateOwnedAsync(offline, aggregate => aggregate.Resources.Hitpoints = 1);
        await StartSessionAtAsync(fixture, fixture.StartedAt);
        await fixture.Adapter.RemoveAsync(offline, flush: true, offlineSince: null);
        await fixture.Presence.UnregisterAsync(offline);
        var social = await fixture.CreateSocialRuntimeAsync();
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyUnready))).Success);
        Assert.Equal("member-unready", (await fixture.Store.LoadAsync(fixture.SessionId))!.EndReason);
        // 散场已强拉消费：收件箱清空、实体卸载、持久化气血按倒地复活规则定格。
        Assert.Empty(await fixture.Store.LoadPendingAsync(offline));
        Assert.Null(fixture.Adapter.TryGetLoaded(offline));
        // 取消者重新准备，常驻就绪位让仲裁直接重开。
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyReady))).Success);
        var session = await fixture.Social.GetActivePartySessionAsync(fixture.PartyId);
        Assert.NotNull(session);
        try
        {
            var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(session.SpecJson, PersistenceJson.Options)!;
            var frozen = Assert.Single(spec.Members, member => member.PlayerId == offline.ToString("D"));
            Assert.Equal(1d, frozen.InitialHitpoints);
            Assert.Empty(await fixture.Store.LoadPendingAsync(offline));
            // 新会话名册仍有人在线：离线成员被对账为常驻装载（§14.6）。
            Assert.NotNull(fixture.Adapter.TryGetLoaded(offline));
        }
        finally
        {
            await fixture.SqlAsync("DELETE FROM social.party_session_inbox WHERE session_id=$1", session.SessionId);
            await fixture.SqlAsync("DELETE FROM social.party_sessions WHERE session_id=$1", session.SessionId);
        }
    }

    /// <summary>任一成员取消准备触发散场时，就绪位跨会话常驻：仅取消者被清位，
    /// 其余名册成员保持 ready，条件再齐备可由下一次仲裁直接重开。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SessionDissolveKeepsReadyForOtherMembers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var social = await fixture.CreateSocialRuntimeAsync();
        Assert.True((await social.ApplyAsync(fixture.Players[1],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyUnready))).Success);
        Assert.Equal("member-unready", (await fixture.Store.LoadAsync(fixture.SessionId))!.EndReason);
        var party = await fixture.Social.GetPartyAsync(fixture.PartyId);
        Assert.NotNull(party);
        foreach (var member in party.Members)
            Assert.Equal(member.PlayerId != fixture.Players[1], member.Ready);
    }

    /// <summary>名册有人在线的会话，离线成员常驻装载并实时消费结算（总纲18 §14.6）：
    /// 常驻不改变在线态与离线锚点；名册全员离线后对账卸载归还，锚点不回拨。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ResidentMembersStayLoadedWhileRosterHasOnlineMember()
    {
        await using var fixture = await Fixture.CreateAsync();
        var social = await fixture.CreateSocialRuntimeAsync();
        var online = fixture.Players[0];
        var first = fixture.Players[1];
        var second = fixture.Players[2];
        // 两名离线成员各带确定断线锚点卸载：先走连接建立转在线（清锚），再按断线时刻收尾。
        var firstAnchor = await TakeOfflineAsync(fixture, first, DateTimeOffset.UtcNow.AddMinutes(-10));
        var secondAnchor = await TakeOfflineAsync(fixture, second, DateTimeOffset.UtcNow.AddMinutes(-8));
        Assert.Null(fixture.Adapter.TryGetLoaded(first));

        await social.ReconcileResidentMembersAsync(CancellationToken.None);
        Assert.NotNull(fixture.Adapter.TryGetLoaded(first));
        Assert.NotNull(fixture.Adapter.TryGetLoaded(second));
        Assert.False(fixture.Presence.IsOnline(first));
        Assert.Equal(firstAnchor, await ReadOfflineSinceAsync(fixture, first));
        Assert.Equal(secondAnchor, await ReadOfflineSinceAsync(fixture, second));

        // 在线批推进一轮：常驻成员实时消费结算，收件箱不积压。
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddSeconds(60));
        await fixture.Adapter.AdvanceAllAsync(fixture.Clock.UtcNow);
        Assert.Empty(await fixture.Store.LoadPendingAsync(first));
        Assert.Empty(await fixture.Store.LoadPendingAsync(second));

        // 名册全员离线后再对账：常驻成员卸载归还，离线锚点不回拨。
        await fixture.Presence.UnregisterAsync(online);
        await social.ReconcileResidentMembersAsync(CancellationToken.None);
        Assert.Null(fixture.Adapter.TryGetLoaded(first));
        Assert.Null(fixture.Adapter.TryGetLoaded(second));
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
        Assert.Equal(firstAnchor, await ReadOfflineSinceAsync(fixture, first));
        Assert.Equal(secondAnchor, await ReadOfflineSinceAsync(fixture, second));
    }

    /// <summary>名册全员离线的会话按批算间隔推进到当前，推进后逐成员强拉消费结算再卸载（§14.6）；
    /// 不足批算间隔不推进。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OfflineSessionAdvancesOnBatchIntervalAndMembersConsume()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var player in fixture.Players)
        {
            await fixture.Presence.UnregisterAsync(player);
            await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: null);
        }
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
        var social = await fixture.CreateSocialRuntimeAsync();
        // 会话检查点停在 startedAt：模拟时钟推进 30 分钟，40 分钟批算间隔不足期不推进。
        fixture.Clock.AdvanceTo(fixture.StartedAt.AddMinutes(30));
        var before = (await fixture.Store.LoadAsync(fixture.SessionId))!.LastSimulatedAt;
        await social.AdvanceOfflineSessionsAsync(TimeSpan.FromMinutes(40), CancellationToken.None);
        Assert.Equal(before, (await fixture.Store.LoadAsync(fixture.SessionId))!.LastSimulatedAt);

        await social.AdvanceOfflineSessionsAsync(TimeSpan.FromMinutes(20), CancellationToken.None);
        var advanced = await fixture.Store.LoadAsync(fixture.SessionId);
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(fixture.Clock.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(expected, advanced!.LastSimulatedAt);
        Assert.Null(advanced.EndedAt);
        foreach (var player in fixture.Players)
        {
            Assert.Empty(await fixture.Store.LoadPendingAsync(player));
            Assert.Null(fixture.Adapter.TryGetLoaded(player));
        }
    }

    /// <summary>全员离线批算不得越过名册最早离线边界：某成员 offline_since+48h 已过期时，
    /// 会话收尾在该边界并以 member-offline-timeout 散场，成员结算即时消费（§14.6/§14.5）。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OfflineSessionEndsAtEarliestOfflineBoundary()
    {
        // 夹具时钟锚定 2 小时前：会话检查点合法停在 now-2h（名册全员离线后无人推进）。
        await using var fixture = await Fixture.CreateAsync(age: TimeSpan.FromHours(2));
        var laggard = fixture.Players[2];
        var laggardAnchor = await TakeOfflineAsync(fixture, laggard, DateTimeOffset.UtcNow.AddHours(-49));
        foreach (var player in fixture.Players.Take(2))
        {
            await fixture.Presence.UnregisterAsync(player);
            await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: null);
        }
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
        var social = await fixture.CreateSocialRuntimeAsync();
        fixture.Clock.AdvanceTo(DateTimeOffset.UtcNow);
        await social.AdvanceOfflineSessionsAsync(TimeSpan.FromMinutes(60), CancellationToken.None);
        var session = await fixture.Store.LoadAsync(fixture.SessionId);
        Assert.Equal("member-offline-timeout", session!.EndReason);
        Assert.Equal(laggardAnchor.Add(PlayerLifecycleRules.OfflineIdleLimit), session.EndedAt);
        foreach (var player in fixture.Players)
        {
            Assert.Empty(await fixture.Store.LoadPendingAsync(player));
            Assert.Null(fixture.Adapter.TryGetLoaded(player));
        }
    }

    /// <summary>散场后未装载的离线成员同样强拉消费终局结算再卸载（§14.6）：
    /// 收件箱清空、持久化状态按终局规则恢复，倒地成员气血定格为 1。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DissolveForceLoadsOfflineMembersToConsumeTerminal()
    {
        await using var fixture = await Fixture.CreateAsync(startSession: false);
        var offline = fixture.Players[1];
        // 倒地成员：开打前把气血压到 1，会话战斗内阵亡，终局结算记倒地值。
        await fixture.Adapter.MutateOwnedAsync(offline, aggregate => aggregate.Resources.Hitpoints = 1);
        await StartSessionAtAsync(fixture, fixture.StartedAt);
        await fixture.Adapter.RemoveAsync(offline, flush: true, offlineSince: null);
        await fixture.Presence.UnregisterAsync(offline);
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));

        var social = await fixture.CreateSocialRuntimeAsync();
        Assert.True((await social.ApplyAsync(fixture.Players[0],
            new Protocol.SocialActionDto(Guid.NewGuid().ToString("D"), Protocol.SocialActions.PartyUnready))).Success);
        Assert.NotNull((await fixture.Store.LoadAsync(fixture.SessionId))!.EndedAt);
        Assert.Empty(await fixture.Store.LoadPendingAsync(offline));
        Assert.Null(fixture.Adapter.TryGetLoaded(offline));
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
        var bundle = await fixture.Sections.LoadAsync(offline);
        Assert.Equal(1d, SectionCodec.DecodeAggregate(bundle!).Combat.Hitpoints);
    }

    /// <summary>按指定开场时刻创建讨伐会话并逐成员挂载参与行动（fixture 开场块的可变时刻版本）。</summary>
    private static async Task StartSessionAtAsync(Fixture fixture, DateTimeOffset startedAt)
    {
        var frozen = new List<ActionParticipantSnapshot>();
        foreach (var player in fixture.Players)
            await fixture.Adapter.MutateOwnedAsync(player,
                aggregate => frozen.Add(aggregate.CaptureActionParticipantSnapshot(fixture.Catalog)));
        var spec = new ActionParticipationSpec(fixture.SessionId, "encounter.qi-beast", "node.yunxi-village", 97,
            frozen.Max(member => member.SearchSpeedPoints), startedAt.ToUnixTimeMilliseconds() / 1000d, frozen)
        { ActionId = new("combat.hunt-qi-beast") };
        await fixture.Runtime.CreateAsync(fixture.PartyId, spec);
        foreach (var player in fixture.Players)
            await fixture.Adapter.MutateOwnedAsync(player,
                aggregate => aggregate.StartActionParticipation(spec, startedAt, fixture.Catalog.Actions));
    }

    /// <summary>模拟真实断线收尾：先经连接建立转在线（清锚），再按断线时刻注销卸载，返回写入的离线锚点。</summary>
    private static async Task<DateTimeOffset> TakeOfflineAsync(Fixture fixture, Guid player, DateTimeOffset disconnectedAt)
    {
        var anchor = DateTimeOffset.FromUnixTimeMilliseconds(disconnectedAt.ToUnixTimeMilliseconds());
        await fixture.Adapter.AttachConnectionAsync(player);
        await fixture.Presence.UnregisterAsync(player);
        await fixture.Adapter.RemoveAsync(player, flush: true, offlineSince: anchor);
        await fixture.Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
        return anchor;
    }

    /// <summary>只读读取投影离线锚点；无列值返回 null。</summary>
    private static async Task<DateTimeOffset?> ReadOfflineSinceAsync(Fixture fixture, Guid player)
    {
        await using var command = fixture.Source.CreateCommand(
            "SELECT offline_since FROM game.player_projection WHERE player_id = $1");
        command.Parameters.AddWithValue(player);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() && !await reader.IsDBNullAsync(0)
            ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    // 在检查点已提交、开场调用尚未返回的边界插入真实数据库取消准备及后台散场。
    private sealed class EndDuringOpeningRuntime(Fixture fixture) : IActionParticipationRuntime
    {
        public string? SessionId { get; private set; }
        public DateTimeOffset? NextAdvanceBoundaryUtc => fixture.Runtime.NextAdvanceBoundaryUtc;
        public bool RequiresReconciliation(Guid playerId) => fixture.Runtime.RequiresReconciliation(playerId);
        public async ValueTask CreateAsync(Guid partyId, ActionParticipationSpec spec,
            EconomyTransactionRequest? escrow = null, CancellationToken cancellationToken = default)
        {
            SessionId = spec.SessionId;
            await fixture.Runtime.CreateAsync(partyId, spec, escrow, cancellationToken);
            await fixture.Social.TrySetPartyMemberReadyAsync(fixture.Players[1], false, cancellationToken);
            await fixture.Runtime.AdvanceOnlineAsync(fixture.Clock.UtcNow, cancellationToken);
        }
        public ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default) =>
            fixture.Runtime.AdvanceOnlineAsync(target, cancellationToken);
        public ValueTask<IReadOnlyList<Guid>> AdvanceOfflineDueAsync(DateTimeOffset now, TimeSpan batchInterval,
            CancellationToken cancellationToken = default) =>
            fixture.Runtime.AdvanceOfflineDueAsync(now, batchInterval, cancellationToken);
        public ValueTask<IReadOnlyList<ParticipationInboxItem>> AdvanceForMemberAsync(Guid playerId,
            DateTimeOffset target, CancellationToken cancellationToken = default) =>
            fixture.Runtime.AdvanceForMemberAsync(playerId, target, cancellationToken);
        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId,
            CancellationToken cancellationToken = default) => fixture.Runtime.LoadPendingAsync(playerId, cancellationToken);
        public ValueTask EndAsync(string sessionId, DateTimeOffset at, string reason,
            CancellationToken cancellationToken = default) => fixture.Runtime.EndAsync(sessionId, at, reason, cancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(NpgsqlDataSource source, DateTimeOffset startedAt, Guid[] players, bool owned)
        {
            Source = source;
            StartedAt = startedAt;
            Players = players;
            Clock = new ManualClock(startedAt);
            Sections = new(source, NullLogger<PostgresSectionRepository>.Instance);
            Store = new(source);
            Social = new(source);
            Runtime = new(Catalog, Store, Presence, Clock, NullLogger<ActionParticipationRuntime>.Instance);
            Ecs = new(Catalog, startedAt, shardCount: 2);
            Adapter = new(Ecs, Sections, new PostgresAssetRepository(source),
                new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance), Catalog,
                NullLogger<EcsPlayerRuntimeAdapter>.Instance, simulationClock: Clock,
                ownership: owned ? new PostgresPlayerOwnershipRepository(source) : null,
                socialRepository: Social, participationRuntime: Runtime, participationSessions: Store);
        }

        public NpgsqlDataSource Source { get; }
        public GameDataCatalog Catalog { get; } = GameDataCatalog.LoadEmbedded();
        public DateTimeOffset StartedAt { get; }
        public Guid[] Players { get; }
        public Guid PartyId { get; private set; }
        public string SessionId { get; } = $"action:{Guid.NewGuid():N}";
        public Dictionary<Guid, Guid> PersonalActions { get; } = [];
        public ManualClock Clock { get; }
        public OnlinePresenceRegistry Presence { get; } = new();
        public PostgresSectionRepository Sections { get; }
        public PostgresParticipationSessionStore Store { get; }
        public PostgresSocialRepository Social { get; }
        public ActionParticipationRuntime Runtime { get; }
        public EcsGameRuntime Ecs { get; }
        public EcsPlayerRuntimeAdapter Adapter { get; }

        public static async Task<Fixture> CreateAsync(bool owned = false, bool startSession = true, TimeSpan? age = null)
        {
            var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("三成员会话装配集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            // age 让整场夹具时钟锚定到过去：会话/成员时钟自该时刻起算，模拟「会话已停更 age」的合法形态。
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                DateTimeOffset.UtcNow.Subtract(age ?? TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds());
            var bytes = Guid.NewGuid().ToByteArray();
            var players = Enumerable.Range(0, 3).Select(index =>
            {
                bytes[^1] = (byte)index;
                return new Guid(bytes);
            }).ToArray();
            var fixture = new Fixture(source, startedAt, players, owned);
            foreach (var player in players) await fixture.SeedPersonalPlayerAsync(player);
            fixture.PartyId = await fixture.Social.CreatePartyAsync(players[0], PartyProfileRecord.Default, startedAt);
            foreach (var player in players.Skip(1))
                Assert.Equal(PartyJoinResult.Joined, await fixture.Social.TryJoinPartyAsync(fixture.PartyId, player, startedAt));
            await fixture.Social.TryUpdatePartyOpenSlotsAsync(fixture.PartyId, 3, startedAt);
            foreach (var player in players)
                await fixture.Social.TrySetPartyMemberReadyAsync(player, true);
            if (!startSession) return fixture;
            var frozen = new List<ActionParticipantSnapshot>();
            foreach (var player in players)
                await fixture.Adapter.MutateOwnedAsync(player, aggregate => frozen.Add(aggregate.CaptureActionParticipantSnapshot(fixture.Catalog)));
            var spec = new ActionParticipationSpec(fixture.SessionId, "encounter.qi-beast", "node.yunxi-village", 97,
                frozen.Max(member => member.SearchSpeedPoints), startedAt.ToUnixTimeMilliseconds() / 1000d, frozen)
            { ActionId = new("combat.hunt-qi-beast") };
            await fixture.Runtime.CreateAsync(fixture.PartyId, spec);
            foreach (var player in players)
                await fixture.Adapter.MutateOwnedAsync(player, aggregate => aggregate.StartActionParticipation(spec, startedAt, fixture.Catalog.Actions));
            return fixture;
        }

        public async Task SeedPersonalPlayerAsync(Guid player)
        {
            await SqlAsync("INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,$3,$3)", player, player.ToString(), StartedAt);
            await SqlAsync("INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,$3,$3,1)", player, player.ToString(), StartedAt);
            var aggregate = PlayerAggregate.Create(new(player), new(player), player.ToString(), StartedAt, Catalog.Fingerprint);
            aggregate.World.NodeId = "node.yunxi-village";
            // 高境界挑战低阶遭遇，保证实际产生掉落与经验。
            aggregate.Progression.RealmLevel = 50;
            PlayerAttributeResolver.Apply(aggregate, Catalog);
            aggregate.Resources.Hitpoints = aggregate.Resources.MaxHitpoints;
            aggregate.Resources.Manapoints = aggregate.Resources.MaxManapoints;
            var personal = aggregate.Enqueue(new ActionIntent(new("cultivation.draw-qi"), -1), StartedAt, Catalog.Actions);
            PersonalActions[player] = personal.Id;
            await Sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(aggregate.ExportState(), DateTimeOffset.UtcNow));
            await Sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
            await Adapter.LoadAsync(player);
            await Presence.RegisterAsync(player, aggregate.DisplayName, aggregate.World.NodeId);
        }

        /// <summary>装配真实社交运行时：完整仲裁链路（开打/散场/成员就绪位常驻）与生产同构。</summary>
        public async Task<SocialRuntime> CreateSocialRuntimeAsync(IActionParticipationRuntime? participation = null)
        {
            var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = "Host=unused;Database=unused"
            }).Build());
            var market = new MarketRuntime(new PostgresMarketRepository(Source, options,
                NullLogger<PostgresMarketRepository>.Instance), options, NullLogger<MarketRuntime>.Instance);
            return await Task.FromResult(new SocialRuntime(Social, Presence,
                new PartyValuationService(Adapter, Sections, market, Catalog),
                new ChatRuntime(new PostgresChatRepository(Source), Social, Presence),
                Catalog, NullLogger<SocialRuntime>.Instance, Adapter, Clock,
                participation ?? Runtime, Sections));
        }

        public string RewardSnapshot(Guid player)
        {
            var snapshot = Adapter.TryGetLoaded(player)!;
            return JsonSerializer.Serialize(new
            {
                snapshot.State.Progression.CombatExperience,
                snapshot.State.Progression.Experience,
                snapshot.State.Progression.Foundation,
                Assets = snapshot.Assets.OrderBy(asset => asset.AssetId).ThenBy(asset => asset.Variant).Select(asset => new { asset.AssetId, asset.Variant, asset.Quantity })
            });
        }

        public async Task<long> SettlementTransactionCountAsync()
        {
            await using var command = Source.CreateCommand("SELECT count(*) FROM economy.transactions WHERE actor_id = ANY($1) AND transaction_type = 'participation.settlement'");
            command.Parameters.AddWithValue(Players);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task SqlAsync(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await SqlAsync("DELETE FROM social.party_session_inbox WHERE session_id = $1", SessionId);
            await SqlAsync("DELETE FROM social.party_sessions WHERE session_id = $1", SessionId);
            await SqlAsync("DELETE FROM social.parties WHERE party_id = $1", PartyId);
            await Source.DisposeAsync();
        }
    }

    private sealed class ManualClock(DateTimeOffset at) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; private set; } = at;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
    }
}
