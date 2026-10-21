using Domain.Player;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

public sealed class PlayerPresenceStatsServiceTests
{
    private sealed class StubClock(DateTimeOffset now) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
    }

    private sealed class PresenceStatsSocialStub : ISocialRepository
    {
        public int CallCount { get; private set; }
        public DateTimeOffset LastCutoff { get; private set; }
        public IReadOnlyCollection<Guid>? LastOnlinePlayerIds { get; private set; }
        public int ReturnCount { get; set; } = 42;
        public bool ShouldThrow { get; set; }

        public ValueTask<int> CountOfflineIdlePlayersAsync(
            DateTimeOffset cutoff, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCutoff = cutoff;
            LastOnlinePlayerIds = onlinePlayerIds;
            if (ShouldThrow) throw new InvalidOperationException("模拟数据库异常");
            return ValueTask.FromResult(ReturnCount);
        }

        public ValueTask<PartySessionRecord?> GetPartySessionAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PartySessionRecord?>(null);
        public ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<Guid?>(null);
        public ValueTask<PartySessionRecord?> GetActivePartySessionAsync(Guid partyId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PartySessionRecord?>(null);
        public ValueTask<IReadOnlyList<PartySessionRecord>> ListActivePartySessionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Guid?> FindPlayerByNameAsync(string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> GetDisplayNameAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerIdentityView?> GetPlayerIdentityAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRecord>> ListFriendsAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRequestView>> ListPendingRequestsAsync(Guid playerId, bool incoming, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SocialStateSnapshot> LoadStateSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> AreFriendsAsync(Guid left, Guid right, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryCreateFriendRequestAsync(Guid requestId, Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryAcceptFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRejectFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRemoveFriendAsync(Guid playerId, Guid friendId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Guid> CreatePartyAsync(Guid leaderId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyProfileAsync(Guid partyId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyJoinResult> TryJoinPartyAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberRecord>?> DisbandPartyAsync(Guid partyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryKickPartyMemberAsync(Guid partyId, Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeletePartyIfEmptyAsync(Guid partyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyRecord>> ListPublicPartiesAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask UpsertPartySeekerAsync(Guid playerId, string activityKind, string nodeId, double minValuation, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartySeekerRecord?> GetPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartySeekerRecord>> ListPartySeekersAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TrySetPartyMemberReadyAsync(Guid playerId, bool ready, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyOpenSlotsAsync(Guid partyId, int openSlots, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<Guid, string>> GetPlayerNodeMapAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberEviction>> DeleteInactivePartyMembersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ServiceReturnsRealtimeOnlineCountAndCachedOfflineIdleCount()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var clock = new StubClock(now);
        var presence = new OnlinePresenceRegistry();
        var repo = new PresenceStatsSocialStub { ReturnCount = 15 };
        var service = new PlayerPresenceStatsService(presence, repo, clock);

        var player1 = Guid.NewGuid();
        var player2 = Guid.NewGuid();
        await presence.RegisterAsync(player1, "道友一", "node.1");
        await presence.RegisterAsync(player2, "道友二", "node.1");

        // 第一次调用：查询仓储并缓存
        var stats1 = await service.GetStatsAsync();
        Assert.Equal(2, stats1.OnlineCount);
        Assert.Equal(15, stats1.OfflineIdleCount);
        Assert.Equal(1, repo.CallCount);
        Assert.Equal(now - PlayerLifecycleRules.OfflineIdleLimit, repo.LastCutoff);
        Assert.Equal(2, repo.LastOnlinePlayerIds!.Count);

        // 在线人数变化：注销 1 人
        await presence.UnregisterAsync(player1);

        // 5秒后（仍在 10 秒缓存内）：仓储不重复调用，但在线人数即时更新为 1
        clock.UtcNow = now.AddSeconds(5);
        repo.ReturnCount = 999;
        var stats2 = await service.GetStatsAsync();
        Assert.Equal(1, stats2.OnlineCount);
        Assert.Equal(15, stats2.OfflineIdleCount);
        Assert.Equal(1, repo.CallCount);

        // 11秒后（缓存过期）：重新查询仓储
        clock.UtcNow = now.AddSeconds(11);
        var stats3 = await service.GetStatsAsync();
        Assert.Equal(1, stats3.OnlineCount);
        Assert.Equal(999, stats3.OfflineIdleCount);
        Assert.Equal(2, repo.CallCount);
    }

    [Fact]
    public async Task ServiceFallsBackToLastCacheWhenDatabaseThrows()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var clock = new StubClock(now);
        var presence = new OnlinePresenceRegistry();
        var repo = new PresenceStatsSocialStub { ReturnCount = 20 };
        var service = new PlayerPresenceStatsService(presence, repo, clock);

        // 首次成功装填缓存
        var first = await service.GetStatsAsync();
        Assert.Equal(20, first.OfflineIdleCount);

        // 缓存过期且仓储抛出异常
        clock.UtcNow = now.AddSeconds(15);
        repo.ShouldThrow = true;

        // 优雅降级回退旧缓存
        var second = await service.GetStatsAsync();
        Assert.Equal(20, second.OfflineIdleCount);
    }
}
