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
        await fixture.Adapter.RemoveAsync(offline, flush: true);
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
        await fixture.Adapter.RemoveAsync(player, flush: true);
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
        await fixture.Adapter.RemoveAsync(player, flush: true);
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
        await fixture.Adapter.RemoveAsync(player, flush: true);
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
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused"
        }).Build());
        var market = new MarketRuntime(new PostgresMarketRepository(fixture.Source, options,
            NullLogger<PostgresMarketRepository>.Instance), options, NullLogger<MarketRuntime>.Instance);
        var opening = new EndDuringOpeningRuntime(fixture);
        var social = new SocialRuntime(fixture.Social, fixture.Presence,
            new PartyValuationService(fixture.Adapter, fixture.Sections, market, fixture.Catalog),
            new ChatRuntime(new PostgresChatRepository(fixture.Source), fixture.Social, fixture.Presence),
            fixture.Catalog, NullLogger<SocialRuntime>.Instance, fixture.Adapter, fixture.Clock, opening);
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

    // 在检查点已提交、开场调用尚未返回的边界插入真实数据库取消准备及后台散场。
    private sealed class EndDuringOpeningRuntime(Fixture fixture) : IActionParticipationRuntime
    {
        public string? SessionId { get; private set; }
        public DateTimeOffset? NextAdvanceBoundaryUtc => fixture.Runtime.NextAdvanceBoundaryUtc;
        public bool RequiresReconciliation(Guid playerId) => fixture.Runtime.RequiresReconciliation(playerId);
        public async ValueTask CreateAsync(Guid partyId, ActionParticipationSpec spec, CancellationToken cancellationToken = default)
        {
            SessionId = spec.SessionId;
            await fixture.Runtime.CreateAsync(partyId, spec, cancellationToken);
            await fixture.Social.TrySetPartyMemberReadyAsync(fixture.Players[1], false, cancellationToken);
            await fixture.Runtime.AdvanceOnlineAsync(fixture.Clock.UtcNow, cancellationToken);
        }
        public ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default) =>
            fixture.Runtime.AdvanceOnlineAsync(target, cancellationToken);
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

        public static async Task<Fixture> CreateAsync(bool owned = false, bool startSession = true)
        {
            var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("三成员会话装配集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
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
