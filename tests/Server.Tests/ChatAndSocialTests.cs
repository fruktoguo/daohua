using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 传音频道与社交（好友/队伍）门禁：
/// 1. 日志分组频道分类与好友私聊作用域键（纯函数，不依赖数据库）；
/// 2. 聊天保留策略、好友双向关系流转、队伍邀请/接受/离开与队长移交（依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库）。
/// 集成用例共享同一张 social.chat_messages，因此保留策略用例使用唯一 nearby 作用域，避免与其他用例互相干扰。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ChatAndSocialTests
{
    /// <summary>日志分组按事件码前缀分类：combat.* 归战斗，auto-use.* 排除不输出（null），其余归系统。</summary>
    [Theory]
    [InlineData("combat.started", LogChannels.Combat)]
    [InlineData("combat.attack", LogChannels.Combat)]
    [InlineData("combat.won", LogChannels.Combat)]
    [InlineData("duel.won", LogChannels.System)]
    [InlineData("action.completed", LogChannels.System)]
    [InlineData("world.arrived", LogChannels.System)]
    [InlineData("offline.capped", LogChannels.System)]
    [InlineData("auto-use.activated", null)]
    [InlineData("auto-use.depleted", null)]
    public void LogChannels_ClassifyByEventCodePrefix(string eventCode, string? expected) =>
        Assert.Equal(expected, LogChannels.FromEventCode(eventCode));

    /// <summary>好友私聊作用域键与双方顺序无关，同一对好友始终落在同一作用域。</summary>
    [Fact]
    public void FriendScopeKey_IsSymmetric()
    {
        var left = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var right = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var forward = ChatRuntime.BuildFriendScopeKey(left, right);
        var backward = ChatRuntime.BuildFriendScopeKey(right, left);

        Assert.Equal(forward, backward);
        Assert.Contains(left.ToString("D"), forward, StringComparison.Ordinal);
        Assert.Contains(right.ToString("D"), forward, StringComparison.Ordinal);
    }

    /// <summary>非末尾异步订阅必须完成后才调用下一订阅，整次推送等待最后的异步订阅完成。</summary>
    [Fact]
    public async Task SocialPushAsync_AwaitsEveryAsyncSubscriberInOrder()
    {
        // 通知回归仅使用在线注册表与受控订阅，不访问持久化边界。
        await using var source = NpgsqlDataSource.Create("Host=unused;Database=unused");
        var presence = new OnlinePresenceRegistry();
        var playerId = Guid.NewGuid();
        await presence.RegisterAsync(playerId, "通知修士", null);
        var social = CreateSocialRuntime(source, new PostgresSocialRepository(source), presence);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        social.StateChanged += async _ =>
        {
            firstEntered.SetResult();
            await firstCompleted.Task;
        };
        social.StateChanged += async _ =>
        {
            lastEntered.SetResult();
            await lastCompleted.Task;
        };

        var push = social.PushAsync(playerId).AsTask();
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(push.IsCompleted);
            Assert.False(lastEntered.Task.IsCompleted);
            firstCompleted.SetResult();
            await lastEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(push.IsCompleted);
            lastCompleted.SetResult();
            await push.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstCompleted.TrySetResult();
            lastCompleted.TrySetResult();
            await push.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>构建被阻塞时的重复刷新合并，最后一次位置变化仍可见，订阅不得并发乱序。</summary>
    [Fact]
    public async Task ConcurrentSocialRefreshDeliversLatestPresenceWithoutParallelSubscriberCalls()
    {
        var (source, repository) = await OpenAsync();
        await using var disposedSource = source;
        var presence = new OnlinePresenceRegistry();
        var (player, _) = await SeedPlayerAsync(source, "迁地修士");
        await presence.RegisterAsync(player, "迁地修士", "node.first");
        var social = CreateSocialRuntime(source, repository, presence);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = new List<string?>();
        var running = 0;
        social.StateChanged += async id =>
        {
            Assert.Equal(1, Interlocked.Increment(ref running));
            nodes.Add(presence.GetNodeId(id));
            if (nodes.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
            Interlocked.Decrement(ref running);
        };
        var first = social.PushAsync(player).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshes = Enumerable.Range(0, 40).Select(_ => social.PushAsync(player).AsTask()).ToArray();
        var moved = presence.UpdateNodeAsync(player, "node.final").AsTask();
        release.SetResult();
        await Task.WhenAll(refreshes.Append(first).Append(moved)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "node.first", "node.final" }, nodes);
    }

    /// <summary>非末尾订阅在异步等待后失败时，异常必须传播到主动推送调用方而不是被丢弃。</summary>
    [Fact]
    public async Task SocialPushAsync_PropagatesNonLastAsyncSubscriberFailure()
    {
        await using var source = NpgsqlDataSource.Create("Host=unused;Database=unused");
        var presence = new OnlinePresenceRegistry();
        var playerId = Guid.NewGuid();
        await presence.RegisterAsync(playerId, "通知修士", null);
        var social = CreateSocialRuntime(source, new PostgresSocialRepository(source), presence);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("受控通知失败");
        var lastCalls = 0;
        social.StateChanged += async _ =>
        {
            entered.SetResult();
            await completion.Task;
            throw expected;
        };
        social.StateChanged += _ =>
        {
            lastCalls++;
            return ValueTask.CompletedTask;
        };

        var push = social.PushAsync(playerId).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        completion.SetResult();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => push.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
        Assert.Equal(0, lastCalls);
    }

    /// <summary>并发主动推送共享实例并发上限，不能各自占用 4 个额外通知槽位。</summary>
    [Fact]
    public async Task SocialPushAsync_ConcurrentPushesShareFourNotificationSlots()
    {
        await using var source = NpgsqlDataSource.Create("Host=unused;Database=unused");
        var presence = new OnlinePresenceRegistry();
        var playerIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var playerId in playerIds)
            await presence.RegisterAsync(playerId, "通知修士", null);
        var social = CreateSocialRuntime(source, new PostgresSocialRepository(source), presence);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counterGate = new object();
        var started = 0;
        var active = 0;
        var peak = 0;
        social.StateChanged += NotifyAsync;

        var pushes = playerIds.Select(playerId => social.PushAsync(playerId).AsTask()).ToArray();
        var all = Task.WhenAll(pushes);
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (counterGate) Assert.Equal(4, started);
            Assert.False(all.IsCompleted);
        }
        finally
        {
            completion.SetResult();
            await all.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(5, started);
        Assert.Equal(4, peak);
        Assert.Equal(0, active);

        async ValueTask NotifyAsync(Guid _)
        {
            lock (counterGate)
            {
                started++;
                active++;
                peak = Math.Max(peak, active);
                if (started == 4) fourStarted.TrySetResult();
            }
            try
            {
                await completion.Task;
            }
            finally
            {
                lock (counterGate) active--;
            }
        }
    }

    /// <summary>
    /// 聊天裁剪按作用域写计数每 16 条摊销一次：周期内最多超限 15 条，触发裁剪后回到恰好 100 条；
    /// 历史回放始终只取最近 100 条并按时间正序。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChatRepository_KeepsOnlyLatestHundredPerScope()
    {
        var (source, _) = await OpenAsync();
        await using var __ = source;
        var chatRepository = new PostgresChatRepository(source);
        var (sender, senderName) = await SeedPlayerAsync(source, "传音真人");
        // 唯一作用域：world/nearby 的公共作用域会被并行用例写入，这里用独占节点键隔离。
        var scopeKey = $"node-{Guid.NewGuid():N}";

        var baseTime = DateTimeOffset.UtcNow;
        // 127 条写入：第 112 条（112 % 16 == 0）触发一次裁剪回到 100，此后累积到 115 条。
        for (var index = 0; index < 127; index++)
        {
            await chatRepository.AppendAsync(new ChatMessageRecord(
                Guid.NewGuid(), ChatChannels.Nearby, scopeKey, sender, senderName, $"第 {index} 条", baseTime.AddSeconds(index)), 100);
        }
        var storedBeforeTrim = await ScalarAsync(source, "SELECT count(*) FROM social.chat_messages WHERE channel = 'nearby' AND scope_key = $1", scopeKey);
        Assert.Equal(115L, Convert.ToInt64(storedBeforeTrim));

        // 第 128 条（128 % 16 == 0）再次触发裁剪：回收到恰好 100 条，丢弃最旧的 28 条。
        await chatRepository.AppendAsync(new ChatMessageRecord(
            Guid.NewGuid(), ChatChannels.Nearby, scopeKey, sender, senderName, "第 127 条", baseTime.AddSeconds(127)), 100);

        var recent = await chatRepository.LoadRecentAsync(ChatChannels.Nearby, scopeKey, 100);

        Assert.Equal(100, recent.Count);
        Assert.Equal("第 28 条", recent[0].Body);
        Assert.Equal("第 127 条", recent[^1].Body);
        var stored = await ScalarAsync(source, "SELECT count(*) FROM social.chat_messages WHERE channel = 'nearby' AND scope_key = $1", scopeKey);
        Assert.Equal(100L, Convert.ToInt64(stored));
    }

    /// <summary>社交状态构建一次往返取齐好友/双向申请/队伍/邀请，并按存在感投影在线标记与附近修士。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BuildStateAsync_AssemblesAllSectionsInOneSnapshot()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var presence = new OnlinePresenceRegistry();
        var (alice, aliceName) = await SeedPlayerAsync(source, "观星者");
        var (bob, bobName) = await SeedPlayerAsync(source, "听潮客");
        var (carol, carolName) = await SeedPlayerAsync(source, "拾穗人");
        var social = CreateSocialRuntime(source, repository, presence);
        var nodeId = $"node-{Guid.NewGuid():N}";

        // alice↔bob 互成好友；carol 自建队伍邀请 alice，同时发来一条待处理好友申请。
        Assert.True((await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRequest, bob.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(bob, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendAccept, alice.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(carol, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyInvite, alice.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(carol, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRequest, alice.ToString("D")))).Success);

        await presence.RegisterAsync(alice, aliceName, nodeId);
        await presence.RegisterAsync(bob, bobName, nodeId);

        var state = await social.BuildStateAsync(alice, "req-1");

        Assert.Equal("req-1", state.RequestId);
        var friend = Assert.Single(state.Friends);
        Assert.Equal(bob.ToString("D"), friend.PlayerId);
        Assert.Equal(bobName, friend.DisplayName);
        Assert.True(friend.Online);
        var incoming = Assert.Single(state.IncomingRequests);
        Assert.Equal(carol.ToString("D"), incoming.PlayerId);
        Assert.Equal(carolName, incoming.DisplayName);
        Assert.Empty(state.OutgoingRequests);
        Assert.NotNull(state.PartyInvitation);
        Assert.Equal(carol.ToString("D"), state.PartyInvitation!.InviterId);
        Assert.Equal(carolName, state.PartyInvitation.InviterName);
        Assert.Null(state.Party);
        Assert.Equal(2, state.NearbyPlayers.Count);

        // 接受邀请入队后再构建：队伍视图与成员在线标记齐备。
        Assert.True((await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyAccept))).Success);
        var inParty = await social.BuildStateAsync(alice, null);
        Assert.Null(inParty.RequestId);
        Assert.NotNull(inParty.Party);
        Assert.Equal(carol.ToString("D"), inParty.Party!.LeaderId);
        Assert.Equal(2, inParty.Party.Members.Count);
        Assert.Contains(inParty.Party.Members, member => member.PlayerId == alice.ToString("D") && member.Online);
        Assert.Contains(inParty.Party.Members, member => member.PlayerId == carol.ToString("D") && !member.Online);
        Assert.Null(inParty.PartyInvitation);
    }

    /// <summary>
    /// 修士更新形象：同节点 NearbyPlayerDto 的 Avatar 发生变更，且触发存在感变化通知。
    /// </summary>
    [Fact]
    public async Task OnlinePresence_UpdateAvatar_ReflectsInNearbyAndNotifies()
    {
        var presence = new OnlinePresenceRegistry();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var nodeId = "node.yunxi-village";

        var affectedList = new List<IReadOnlyList<Guid>>();
        presence.PresenceChanged += affected =>
        {
            affectedList.Add(affected);
            return ValueTask.CompletedTask;
        };

        await presence.RegisterAsync(alice, "Alice", nodeId, avatar: "player");
        await presence.RegisterAsync(bob, "Bob", nodeId, avatar: "player");

        var initialNearby = presence.SnapshotAtNode(nodeId);
        Assert.Equal(2, initialNearby.Count);
        Assert.All(initialNearby, p => Assert.Equal("player", p.Avatar));

        affectedList.Clear();
        await presence.UpdateAvatarAsync(alice, "player-male");

        var updatedNearby = presence.SnapshotAtNode(nodeId);
        var aliceNearby = Assert.Single(updatedNearby, p => p.PlayerId == alice.ToString("D"));
        var bobNearby = Assert.Single(updatedNearby, p => p.PlayerId == bob.ToString("D"));
        Assert.Equal("player-male", aliceNearby.Avatar);
        Assert.Equal("player", bobNearby.Avatar);

        Assert.Single(affectedList);
        Assert.Contains(alice, affectedList[0]);
        Assert.Contains(bob, affectedList[0]);
    }

    /// <summary>好友申请经接受后双向成立，解除后双向失效。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FriendRequest_AcceptThenRemove_MaintainsBidirectionalRelationship()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (alice, aliceName) = await SeedPlayerAsync(source, "青莲子");
        var (bob, _) = await SeedPlayerAsync(source, "白云客");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());

        var requested = await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRequest, bob.ToString("D")));
        Assert.True(requested.Success);
        Assert.False(await repository.AreFriendsAsync(alice, bob));

        // 重复申请被拒。
        var duplicate = await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRequest, bob.ToString("D")));
        Assert.Equal(SocialFailure.RequestExists, duplicate.Failure);

        var incoming = await repository.ListPendingRequestsAsync(bob, true);
        Assert.Single(incoming);
        Assert.Equal(alice, incoming[0].PlayerId);
        Assert.Equal(aliceName, incoming[0].DisplayName);

        var accepted = await social.ApplyAsync(bob, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendAccept, alice.ToString("D")));
        Assert.True(accepted.Success);
        Assert.True(await repository.AreFriendsAsync(alice, bob));
        Assert.True(await repository.AreFriendsAsync(bob, alice));

        // 已是好友后再次申请被拒。
        var again = await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRequest, bob.ToString("D")));
        Assert.Equal(SocialFailure.AlreadyFriends, again.Failure);

        var removed = await social.ApplyAsync(alice, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.FriendRemove, bob.ToString("D")));
        Assert.True(removed.Success);
        Assert.False(await repository.AreFriendsAsync(alice, bob));
        Assert.False(await repository.AreFriendsAsync(bob, alice));
    }

    /// <summary>邀请即自建队伍并任队长；被邀请人接受后入队；队长离开时队长位移交剩余成员。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyInviteAcceptAndLeave_TransfersLeadership()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (leader, leaderName) = await SeedPlayerAsync(source, "队长甲");
        var (member, _) = await SeedPlayerAsync(source, "队员乙");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());

        Assert.Null(await repository.GetPartyIdAsync(leader));

        var invited = await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyInvite, member.ToString("D")));
        Assert.True(invited.Success);
        var partyId = await repository.GetPartyIdAsync(leader);
        Assert.NotNull(partyId);

        var invitation = await repository.GetPartyInvitationAsync(member);
        Assert.NotNull(invitation);
        Assert.Equal(leader, invitation!.InviterId);
        Assert.Equal(leaderName, invitation.InviterName);

        var accepted = await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyAccept));
        Assert.True(accepted.Success);
        var party = await repository.GetPartyAsync(partyId!.Value);
        Assert.NotNull(party);
        Assert.Equal(2, party!.Members.Count);
        Assert.Null(await repository.GetPartyInvitationAsync(member));

        // 已在队伍中的非队长成员不能邀请他人（只有队长可以）。
        var (third, _) = await SeedPlayerAsync(source, "路人丙");
        var memberInvite = await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyInvite, third.ToString("D")));
        Assert.Equal(SocialFailure.NotLeader, memberInvite.Failure);

        var left = await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyLeave));
        Assert.True(left.Success);
        var afterLeave = await repository.GetPartyAsync(partyId.Value);
        Assert.NotNull(afterLeave);
        Assert.Single(afterLeave!.Members);
        Assert.Equal(member, afterLeave.LeaderId);

        var lastLeft = await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyLeave));
        Assert.True(lastLeft.Success);
        Assert.Null(await repository.GetPartyAsync(partyId.Value));
    }

    /// <summary>创建公开队伍后进入组队大厅：档案字段、成员预览与请求者身家齐备；非法档案被拒。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyCreate_PublicParty_AppearsInLobby()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (leader, leaderName) = await SeedPlayerAsync(source, "队长甲");
        var (seeker, _) = await SeedPlayerAsync(source, "散修乙");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        // 大厅是全局视图：清掉历史用例遗留的队伍/散人行，断言只认本用例建的队。
        await ResetPartyTablesAsync(source);

        // 非法档案：未知活动类型 / 超长队名 / 负身家门槛 / 未知地图一律拒绝。
        foreach (var bad in new[]
        {
            new PartyProfileDto(ActivityKind: "dungeon"),
            new PartyProfileDto(Name: new string('队', 33)),
            new PartyProfileDto(MinValuation: -1),
            new PartyProfileDto(NodeId: "node.not-exist"),
        })
        {
            var rejected = await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate, Party: bad));
            Assert.Equal(SocialFailure.InvalidPartyProfile, rejected.Failure);
        }

        var profile = new PartyProfileDto("听潮小队", PartyActivityKinds.Exploration, string.Empty, 0, true);
        var created = await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate, Party: profile));
        Assert.True(created.Success);
        var partyId = (await repository.GetPartyIdAsync(leader))!.Value;

        var lobby = await social.BuildLobbyAsync(seeker, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D")));
        var entry = Assert.Single(lobby.Parties, party => party.PartyId == partyId.ToString("D"));
        Assert.Equal("听潮小队", entry.Name);
        Assert.Equal(leader.ToString("D"), entry.LeaderId);
        Assert.Equal(leaderName, entry.LeaderName);
        Assert.Equal(PartyActivityKinds.Exploration, entry.ActivityKind);
        Assert.True(entry.Members.Count == 1 && entry.Members[0].PlayerId == leader.ToString("D"));
        Assert.Equal(0, lobby.MyValuation);

        // 活动类型「不限」双向通配：本队目标为 exploration，combat 筛选不命中，any 筛选命中。
        var combatOnly = await social.BuildLobbyAsync(seeker, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D"), PartyActivityKinds.Combat));
        Assert.DoesNotContain(combatOnly.Parties, party => party.PartyId == partyId.ToString("D"));
        var anyFilter = await social.BuildLobbyAsync(seeker, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D"), PartyActivityKinds.Any));
        Assert.Contains(anyFilter.Parties, party => party.PartyId == partyId.ToString("D"));
    }

    /// <summary>角色信息卡（§21.14）：本人查询自身返回完整身份与默认聚合投影，无授权拦截。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_Self_ReturnsIdentityAndDefaultProjection()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (alice, aliceName) = await SeedPlayerAsync(source, "自照修士");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());

        var card = await social.BuildPlayerCardAsync(alice,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), alice.ToString("D")));

        Assert.Null(card.Error);
        Assert.Equal(alice.ToString("D"), card.PlayerId);
        Assert.Equal(aliceName, card.DisplayName);
        Assert.True(card.PlayerSeq > 0);
        Assert.False(card.Online);
        Assert.Equal(1, card.RealmLevel);
        Assert.Empty(card.Equipment!);
        Assert.Empty(card.AbilitySlots!);
        Assert.Empty(card.AutoUseSlots!);
    }

    /// <summary>同队成员可查离线队友角色卡：从持久化分段真源投影境界/六维/装备/功法/战斗物品配置。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_PartyMember_ProjectsOfflineSections()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (viewer, _) = await SeedPlayerAsync(source, "观礼修士");
        var (target, targetName) = await SeedPlayerAsync(source, "藏锋修士");
        var catalog = GameDataCatalog.LoadEmbedded();
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        await ResetPartyTablesAsync(source);

        // 离线队友的持久化真源：境界/六维/装备/功法/战斗物品全量播种后整版落盘。
        var now = DateTimeOffset.UtcNow;
        var baseState = PlayerAggregate.Create(new PlayerId(target), new AccountId(Guid.NewGuid()),
            targetName, now.AddMinutes(-5), catalog.Fingerprint).ExportState();
        var seeded = baseState with
        {
            Progression = baseState.Progression with { RealmLevel = 12, Experience = 5000, CombatExperience = 321 },
            LearnedAbilities = [new AbilityId("ability.palm-lightning")],
            Loadout = new PlayerLoadoutDocument(
                new PlayerCoreAttributes(StaminaExp: 1_000_000, MeridiansExp: 200, AttackExp: 100, BoneExp: 50, AgilityExp: 25, ConsciousnessExp: 10),
                [new PlayerAbilitySlot("ability.palm-lightning", 3), new PlayerAbilitySlot(null, 0)],
                [new PlayerEquipmentSlot("equipment.qingfeng-sword", 2), new PlayerEquipmentSlot("equipment.tool-herb-hoe", 0)],
                new Dictionary<string, double> { ["ability.palm-lightning"] = 2700 }),
            Settings = baseState.Settings with
            {
                AutoUseSlots =
                [
                    new AutoUseSlotState(Domain.GameData.AutoUseBar.Combat, 0, "item.qi-gathering-pill", 2, true,
                        Group: AutoUseSlotState.SlotGroupPotion),
                    new AutoUseSlotState(Domain.GameData.AutoUseBar.Combat, 1, "item.gathering-incense", 1, false,
                        Group: AutoUseSlotState.SlotGroupGeneral),
                ],
            },
        };
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        await sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(seeded, now));
        await FlushSectionsAsync(sections, source, target);
        await SqlAsync(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES($1,'item.qi-gathering-pill','',2,1,$2)", target, now);

        Assert.True((await social.ApplyAsync(viewer, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyInvite, target.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(target, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyAccept))).Success);

        var card = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));

        Assert.Null(card.Error);
        Assert.Equal(targetName, card.DisplayName);
        Assert.Equal(12, card.RealmLevel);
        Assert.Equal(5000, card.Experience);
        Assert.Equal(321, card.CombatExperience);
        Assert.True(card.Valuation >= 0);
        Assert.NotNull(card.CoreAttributes);
        Assert.Equal(1_000_000, card.CoreAttributes!.StaminaExp);
        Assert.True(card.CoreAttributes.StaminaLevel > 0);
        Assert.True(card.CoreAttributes.TotalLevel > 0);
        Assert.Contains(card.Equipment!, entry => entry.ItemId == "equipment.qingfeng-sword" && entry.EnhancementLevel == 2);
        Assert.Contains(card.Equipment!, entry => entry.ItemId == "equipment.tool-herb-hoe");
        Assert.Contains(card.AbilitySlots!, slot => slot.AbilityId == "ability.palm-lightning" && slot.Level == 3);
        Assert.Contains(card.AbilitySlots!, slot => slot.AbilityId is null);
        Assert.Contains(card.LearnedAbilityIds!, id => id == "ability.palm-lightning");
        Assert.Equal(2700, card.AbilityExperiences!["ability.palm-lightning"]);
        Assert.Contains(card.AutoUseSlots!, slot => slot.Group == "potion" && slot.SlotIndex == 0
            && slot.ItemId == "item.qi-gathering-pill" && slot.Quantity == 2 && slot.Enabled);
        Assert.Contains(card.AutoUseSlots!, slot => slot.Group == "general" && slot.SlotIndex == 1
            && slot.ItemId == "item.gathering-incense" && !slot.Enabled);
    }

    /// <summary>已装载队友读运行时内存态而非持久化分段：装载口径优先。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_LoadedMember_UsesRuntimeState()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (viewer, _) = await SeedPlayerAsync(source, "旁观者");
        var (target, targetName) = await SeedPlayerAsync(source, "在线修士");
        var catalog = GameDataCatalog.LoadEmbedded();
        await ResetPartyTablesAsync(source);

        var now = DateTimeOffset.UtcNow;
        var runtimeState = PlayerAggregate.Create(new PlayerId(target), new AccountId(Guid.NewGuid()),
            targetName, now, catalog.Fingerprint).ExportState();
        runtimeState = runtimeState with
        {
            StateVersion = 9,
            Progression = runtimeState.Progression with { RealmLevel = 30, CombatExperience = 88 },
        };
        var snapshot = new PlayerRuntimeSnapshot(target, "acct.test", targetName, 9, now, runtimeState, 0, []);
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry(),
            new EmptyPlayerRuntime(new Dictionary<Guid, PlayerRuntimeSnapshot> { [target] = snapshot }));

        Assert.True((await social.ApplyAsync(viewer, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyInvite, target.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(target, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyAccept))).Success);

        var card = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));

        Assert.Null(card.Error);
        Assert.Equal(30, card.RealmLevel);
        Assert.Equal(88, card.CombatExperience);
    }

    /// <summary>观察卡显示权威钱包和唯一会话离线储备，不能回读槽配置数量或把两份余额合并。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_SeparatesAvailableWalletFromOfflineSessionReserve()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (target, targetName) = await SeedPlayerAsync(source, "储备修士");
        var (ally, allyName) = await SeedPlayerAsync(source, "储备同道");
        var catalog = GameDataCatalog.LoadEmbedded();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var state = PlayerAggregate.Create(new PlayerId(target), new AccountId(Guid.NewGuid()), targetName, now, catalog.Fingerprint).ExportState();
        state = state with { Settings = state.Settings with { AutoUseSlots = [
            new AutoUseSlotState(AutoUseBar.Combat, 0, "item.qi-gathering-pill", 99, true, Group: AutoUseSlotState.SlotGroupPotion)
        ] } };
        var allyState = PlayerAggregate.Create(new PlayerId(ally), new AccountId(Guid.NewGuid()), allyName, now, catalog.Fingerprint).ExportState();
        var sessionId = $"action:{Guid.NewGuid():N}";
        var spec = new ActionParticipationSpec(sessionId, "encounter.qi-beast", "node.test", 97, 0,
            now.ToUnixTimeMilliseconds() / 1000d,
            [ActionParticipantSnapshot.FromState(state, catalog), ActionParticipantSnapshot.FromState(allyState, catalog)])
        { ActionId = new ActionId("combat.hunt-qi-beast") };
        var checkpoint = ActionParticipationSessionEngine.Create(spec, catalog).Export() with
        {
            InventoryMode = ActionParticipationInventoryMode.Offline,
            OfflineReserves = [new(0, "item.qi-gathering-pill", "", 7)]
        };
        await SqlAsync(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES($1,'item.qi-gathering-pill','',3,1,$2)", target, now);
        await SqlAsync(source, "INSERT INTO social.party_sessions(session_id,party_id,spec,started_at,runtime_state,last_simulated_at) VALUES($1,$2,$3::jsonb,$4,$5::jsonb,$4)",
            sessionId, Guid.NewGuid(), JsonSerializer.Serialize(spec, Domain.Persistence.PersistenceJson.Options), now,
            JsonSerializer.Serialize(checkpoint, Domain.Persistence.PersistenceJson.Options));
        try
        {
            var presence = new OnlinePresenceRegistry();
            var participation = new global::Server.ActionParticipationRuntime(catalog, new PostgresParticipationSessionStore(source),
                presence, PlayerSimulationClock.FromEnvironment(), NullLogger<global::Server.ActionParticipationRuntime>.Instance);
            var snapshot = new PlayerRuntimeSnapshot(target, "acct.test", targetName, state.StateVersion, now, state, 0, []);
            var social = CreateSocialRuntime(source, repository, presence,
                new EmptyPlayerRuntime(new Dictionary<Guid, PlayerRuntimeSnapshot> { [target] = snapshot }), participation);
            var card = await social.BuildPlayerCardAsync(target,
                new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));
            Assert.Null(card.Error);
            var slot = Assert.Single(card.AutoUseSlots!);
            Assert.Equal(3, slot.Quantity);
            Assert.Equal(7, slot.OfflineReserveQuantity);
            Assert.Equal(3d, await ScalarAsync(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.qi-gathering-pill'", target));
        }
        finally
        {
            await SqlAsync(source, "DELETE FROM social.party_sessions WHERE session_id=$1", sessionId);
        }
    }

    /// <summary>授权边界：非本人且不同队一律 forbidden；请求者有队但目标不在队同样拒绝。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_Stranger_ReturnsForbidden()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (viewer, _) = await SeedPlayerAsync(source, "门外修士");
        var (target, _) = await SeedPlayerAsync(source, "陌生修士");
        var (ally, _) = await SeedPlayerAsync(source, "同道修士");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        await ResetPartyTablesAsync(source);

        var noParty = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));
        Assert.Equal("forbidden", noParty.Error);
        Assert.Equal(target.ToString("D"), noParty.PlayerId);
        Assert.Null(noParty.DisplayName);
        Assert.Null(noParty.CoreAttributes);

        // 请求者已有队伍、目标不在队内：仍属越权查询。
        Assert.True((await social.ApplyAsync(viewer, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyInvite, ally.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(ally, new SocialActionDto(Guid.NewGuid().ToString("D"),
            SocialActions.PartyAccept))).Success);
        var wrongParty = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));
        Assert.Equal("forbidden", wrongParty.Error);

        // 同队成员正向口径：ally 与 viewer 同队，可被查。
        var allowed = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), ally.ToString("D")));
        Assert.Null(allowed.Error);
    }

    /// <summary>不存在的玩家身份（无队伍关系的陌生目标）按 forbidden 拦截，不暴露存在性差异。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_NonExistentStranger_ReturnsForbidden()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (viewer, _) = await SeedPlayerAsync(source, "探查修士");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());

        var card = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")));

        Assert.Equal("forbidden", card.Error);
    }

    /// <summary>同节点附近修士（非同队）可查角色卡：展示六维、战斗属性与生活技艺等级。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PlayerCard_NearbyPlayer_AllowedAndReturnsAttributesAndTechniques()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (viewer, _) = await SeedPlayerAsync(source, "同地观者");
        var (target, targetName) = await SeedPlayerAsync(source, "附近道友");
        var presence = new OnlinePresenceRegistry();
        // 两人登记在同一个世界节点（云溪村 node.yunxi-village）
        await presence.RegisterAsync(viewer, "同地观者", "node.yunxi-village");
        await presence.RegisterAsync(target, targetName, "node.yunxi-village");
        var social = CreateSocialRuntime(source, repository, presence);

        var card = await social.BuildPlayerCardAsync(viewer,
            new PlayerCardRequestDto(Guid.NewGuid().ToString("D"), target.ToString("D")));

        Assert.Null(card.Error);
        Assert.Equal(targetName, card.DisplayName);
        Assert.NotNull(card.Attributes);
        Assert.NotNull(card.TechniqueLevels);
        Assert.Contains("alchemy", card.TechniqueLevels!.Keys);
        Assert.Contains("forging", card.TechniqueLevels!.Keys);
    }

    /// <summary>不公开队伍不进大厅；持有 partyId 的链接点击仍可加入，身家门槛照旧裁决。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyJoin_PrivatePartyViaId_NotInLobbyButJoinable()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (leader, _) = await SeedPlayerAsync(source, "隐者");
        var (joiner, _) = await SeedPlayerAsync(source, "投帖客");
        var (gate, _) = await SeedPlayerAsync(source, "门外汉");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        await ResetPartyTablesAsync(source);

        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate,
            Party: new PartyProfileDto("暗流", PartyActivityKinds.Any, string.Empty, 0, false)))).Success);
        var partyId = await repository.GetPartyIdAsync(leader);
        Assert.NotNull(partyId);

        var lobby = await social.BuildLobbyAsync(joiner, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D")));
        Assert.DoesNotContain(lobby.Parties, party => party.PartyId == partyId.Value.ToString("D"));

        // 不存在的队伍 ID：链接失效路径。
        var ghost = await social.ApplyAsync(joiner, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyJoin, PartyId: Guid.NewGuid().ToString("D")));
        Assert.Equal(SocialFailure.PartyNotFound, ghost.Failure);

        // 队长把门槛抬到 1：散人身家为 0，链接加入被门槛拒。
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyUpdate,
            Party: new PartyProfileDto(MinValuation: 1)))).Success);
        var blocked = await social.ApplyAsync(gate, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyJoin, PartyId: partyId.Value.ToString("D")));
        Assert.Equal(SocialFailure.ValuationTooLow, blocked.Failure);

        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyUpdate,
            Party: new PartyProfileDto(MinValuation: 0)))).Success);
        var joined = await social.ApplyAsync(joiner, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyJoin, PartyId: partyId.Value.ToString("D")));
        Assert.True(joined.Success);
        Assert.Equal(partyId, await repository.GetPartyIdAsync(joiner));
    }

    /// <summary>匹配登记：命中公开队伍时自动并入并清除登记；未命中保持「匹配中」，撤下后从大厅散人列表消失。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyMatchSet_JoinsMatchingPartyOrStaysSeeking()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (leader, _) = await SeedPlayerAsync(source, "组头");
        var (hit, _) = await SeedPlayerAsync(source, "顺风耳");
        var (miss, _) = await SeedPlayerAsync(source, "独行者");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        // 自动并入的候选池是全库公开队伍：先清场，保证唯一候选就是本用例建的队。
        await ResetPartyTablesAsync(source);

        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate,
            Party: new PartyProfileDto("历练队", PartyActivityKinds.Exploration, string.Empty, 0, true)))).Success);
        var partyId = await repository.GetPartyIdAsync(leader);
        Assert.NotNull(partyId);

        // 目标一致的散人自动并入：登记清除、直接入队。
        var autoJoin = await social.ApplyAsync(hit, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyMatchSet,
            Party: new PartyProfileDto(ActivityKind: PartyActivityKinds.Exploration)));
        Assert.True(autoJoin.Success);
        Assert.Equal(partyId, await repository.GetPartyIdAsync(hit));
        Assert.Null(await repository.GetPartySeekerAsync(hit));

        // 目标不一致的散人保持匹配中：seeking 下发、大厅散人列表可见。
        var staying = await social.ApplyAsync(miss, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyMatchSet,
            Party: new PartyProfileDto(ActivityKind: PartyActivityKinds.Cultivation, MinValuation: 5)));
        Assert.True(staying.Success);
        Assert.Null(await repository.GetPartyIdAsync(miss));
        var seeker = await repository.GetPartySeekerAsync(miss);
        Assert.NotNull(seeker);
        var state = await social.BuildStateAsync(miss, null);
        Assert.NotNull(state.Seeking);
        Assert.Equal(PartyActivityKinds.Cultivation, state.Seeking!.ActivityKind);
        var lobby = await social.BuildLobbyAsync(leader, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D")));
        Assert.Contains(lobby.Seekers, entry => entry.PlayerId == miss.ToString("D"));

        var cleared = await social.ApplyAsync(miss, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyMatchClear));
        Assert.True(cleared.Success);
        Assert.Null(await repository.GetPartySeekerAsync(miss));
        var again = await social.ApplyAsync(miss, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyMatchClear));
        Assert.Equal(SocialFailure.SeekingNotFound, again.Failure);
    }

    /// <summary>并发加入由行锁事务裁决：5 人满员上限不被并发击穿，超额者收到明确失败。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyJoin_ConcurrentJoins_RespectCapacity()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var (leader, _) = await SeedPlayerAsync(source, "擂主");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate,
            Party: new PartyProfileDto("满编擂台", PartyActivityKinds.Any, string.Empty, 0, true)))).Success);
        var partyId = (await repository.GetPartyIdAsync(leader))!.Value;

        var joiners = new List<Guid>();
        for (var index = 0; index < 6; index++)
            joiners.Add((await SeedPlayerAsync(source, $"抢位{index}")).PlayerId);

        var outcomes = await Task.WhenAll(joiners.Select(joiner =>
            social.ApplyAsync(joiner, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyJoin, PartyId: partyId.ToString("D"))).AsTask()));

        Assert.Equal(4, outcomes.Count(outcome => outcome.Success));
        Assert.All(outcomes.Where(outcome => !outcome.Success), outcome => Assert.Equal(SocialFailure.PartyFull, outcome.Failure));
        var party = await repository.GetPartyAsync(partyId);
        Assert.Equal(SocialRuntime.MaxPartyMembers, party!.Members.Count);
    }

    /// <summary>队长更新档案与移出成员；非队长操作被拒；解散后大厅与聊天链接一并失效。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartyManage_UpdateKickDisband_AndLinkExpiry()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var chatRepository = new PostgresChatRepository(source);
        var (leader, _) = await SeedPlayerAsync(source, "执掌");
        var (member, _) = await SeedPlayerAsync(source, "随行");
        var social = CreateSocialRuntime(source, repository, new OnlinePresenceRegistry());

        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyCreate,
            Party: new PartyProfileDto("行脚团", PartyActivityKinds.Any, string.Empty, 0, true)))).Success);
        var partyId = (await repository.GetPartyIdAsync(leader))!.Value;
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyInvite, member.ToString("D")))).Success);
        Assert.True((await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyAccept))).Success);

        // 非队长更新/移出/解散一律 NotLeader。
        Assert.Equal(SocialFailure.NotLeader, (await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyUpdate, Party: new PartyProfileDto(Name: "篡名")))).Failure);
        Assert.Equal(SocialFailure.NotLeader, (await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyKick, leader.ToString("D")))).Failure);
        Assert.Equal(SocialFailure.NotLeader, (await social.ApplyAsync(member, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyDisband))).Failure);

        // 队长改名并公开化，档案下发到位。
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyUpdate,
            Party: new PartyProfileDto(Name: "改旗", MinValuation: 3)))).Success);
        var updated = await repository.GetPartyAsync(partyId);
        Assert.Equal("改旗", updated!.Profile.Name);
        Assert.Equal(3, updated.Profile.MinValuation);

        // 分享卡片到世界频道：消息带 partyLink 快照。
        var share = await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyShare, Channel: ChatChannels.World));
        Assert.True(share.Success);
        var worldLog = await chatRepository.LoadRecentAsync(ChatChannels.World, string.Empty, 100);
        var card = worldLog.LastOrDefault(message => message.SenderId == leader);
        Assert.NotNull(card?.PartyLink);
        Assert.Equal(partyId, card!.PartyLink!.PartyId);
        Assert.Equal("改旗", card.PartyLink.Name);

        // 移出成员后其队伍清空。
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyKick, member.ToString("D")))).Success);
        Assert.Null(await repository.GetPartyIdAsync(member));

        // 解散：成员清空、大厅不再列出、历史消息保留但链接失效。
        Assert.True((await social.ApplyAsync(leader, new SocialActionDto(Guid.NewGuid().ToString("D"), SocialActions.PartyDisband))).Success);
        Assert.Null(await repository.GetPartyAsync(partyId));
        Assert.Null(await repository.GetPartyIdAsync(leader));
        var afterDisband = await social.BuildLobbyAsync(member, new PartyLobbyRequestDto(Guid.NewGuid().ToString("D")));
        Assert.DoesNotContain(afterDisband.Parties, party => party.PartyId == partyId.ToString("D"));
        var staleLog = await chatRepository.LoadRecentAsync(ChatChannels.World, string.Empty, 100);
        var staleCard = staleLog.LastOrDefault(message => message.SenderId == leader);
        Assert.NotNull(staleCard);
        Assert.Null(staleCard!.PartyLink);
    }

    /// <summary>好友频道私聊要求双方已是好友；世界/附近频道按作用域解析收件人；宗门与空正文被拒。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChatRuntime_RejectsFriendChannelForNonFriendsAndKeepsWorldOpen()
    {
        var (source, repository) = await OpenAsync();
        await using var _ = source;
        var chatRepository = new PostgresChatRepository(source);
        var presence = new OnlinePresenceRegistry();
        var (alice, aliceName) = await SeedPlayerAsync(source, "甲修士");
        var (bob, bobName) = await SeedPlayerAsync(source, "乙修士");
        var chat = new ChatRuntime(chatRepository, repository, presence);
        // 唯一节点：与其他用例的附近广播隔离。
        var nodeId = $"node-{Guid.NewGuid():N}";
        await presence.RegisterAsync(alice, aliceName, nodeId);
        await presence.RegisterAsync(bob, bobName, nodeId);

        var world = await chat.SendAsync(alice, aliceName, new ChatSendDto(Guid.NewGuid().ToString("D"), ChatChannels.World, "诸位道友安好"));
        Assert.Equal(ChatSendStatus.Ok, world.Status);
        Assert.Equal(2, world.Recipients!.Count);

        var nearby = await chat.SendAsync(alice, aliceName, new ChatSendDto(Guid.NewGuid().ToString("D"), ChatChannels.Nearby, "此地灵气充裕"));
        Assert.Equal(ChatSendStatus.Ok, nearby.Status);
        Assert.Equal(2, nearby.Recipients!.Count);
        Assert.Equal(nodeId, nearby.Message!.ScopeKey);

        var friend = await chat.SendAsync(alice, aliceName, new ChatSendDto(Guid.NewGuid().ToString("D"), ChatChannels.Friend, "私下相谈", bob.ToString("D")));
        Assert.Equal(ChatSendStatus.ChannelUnavailable, friend.Status);

        var sect = await chat.SendAsync(alice, aliceName, new ChatSendDto(Guid.NewGuid().ToString("D"), ChatChannels.Sect, "宗门频道"));
        Assert.Equal(ChatSendStatus.ChannelUnavailable, sect.Status);

        var empty = await chat.SendAsync(alice, aliceName, new ChatSendDto(Guid.NewGuid().ToString("D"), ChatChannels.World, "   "));
        Assert.Equal(ChatSendStatus.InvalidBody, empty.Status);

        // 附近频道历史回放限定在请求者所在节点作用域。
        var history = await chat.LoadHistoryAsync(bob, new ChatHistoryRequestDto(ChatChannels.Nearby));
        Assert.NotNull(history);
        Assert.Equal(nodeId, history!.ScopeKey);
        Assert.Contains(history.Messages, message => message.Body == "此地灵气充裕");
    }

    /// <summary>
    /// 清空队伍域三张表（成员/邀请/散人/链接引用随外键一并清理）。
    /// 大厅查询是无作用域的全局视图，历史用例遗留行会污染断言——PostgresIntegration 集合内用例串行执行，清场安全。
    /// </summary>
    private static async Task ResetPartyTablesAsync(NpgsqlDataSource source) =>
        await SqlAsync(source, "TRUNCATE social.party_seekers, social.party_invitations, social.party_members, social.parties CASCADE");

    private static async Task<(NpgsqlDataSource Source, PostgresSocialRepository Repository)> OpenAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("传音/社交集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        return (source, new PostgresSocialRepository(source));
    }

    /// <summary>
    /// 装配集成测试用的社交运行时：市场分片不启动（未就绪时卖一档价返回 null，身家全部回落回收价）。
    /// players 缺省为永不装载替身（离线估值与角色卡只走持久化分段真源路径）。
    /// </summary>
    private static SocialRuntime CreateSocialRuntime(NpgsqlDataSource source, PostgresSocialRepository repository, OnlinePresenceRegistry presence,
        IPlayerRuntime? players = null, IActionParticipationRuntime? participation = null)
    {
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var market = new MarketRuntime(
            new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance),
            options,
            NullLogger<MarketRuntime>.Instance);
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        var catalog = GameDataCatalog.LoadEmbedded();
        var runtime = players ?? new EmptyPlayerRuntime();
        var valuation = new PartyValuationService(runtime, sections, market, catalog);
        var chat = new ChatRuntime(new PostgresChatRepository(source), repository, presence);
        return new SocialRuntime(repository, presence, valuation, chat, catalog, NullLogger<SocialRuntime>.Instance,
            players: runtime, sections: sections, participation: participation,
            assetBalances: new PostgresAssetRepository(source));
    }

    /// <summary>估值/角色卡测试路径的运行时替身；loaded 表内玩家按已装载返回快照。</summary>
    private sealed class EmptyPlayerRuntime(IReadOnlyDictionary<Guid, PlayerRuntimeSnapshot>? loaded = null) : IPlayerRuntime
    {
        public int LoadedCount => loaded?.Count ?? 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated { add { } remove { } }
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted { add { } remove { } }
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => loaded?.GetValueOrDefault(playerId);
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
            long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>播种一个玩家；道号带随机后缀，避免与既有测试数据或历史运行撞唯一索引。</summary>
    private static async Task<(Guid PlayerId, string DisplayName)> SeedPlayerAsync(NpgsqlDataSource source, string baseName)
    {
        var playerId = Guid.NewGuid();
        var displayName = $"{baseName}-{playerId:N}";
        await SqlAsync(source,
            "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())",
            playerId, $"{playerId:N}@test");
        await SqlAsync(source,
            "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$3,(left(replace($1::text,'-',''),16)),now(),now(),1)",
            playerId, displayName, displayName.ToUpperInvariant());
        return (playerId, displayName);
    }

    /// <summary>刷空指定玩家的 section 落盘队列：Enqueue 走异步收件箱，断言前必须确认落库。</summary>
    private static async Task FlushSectionsAsync(PostgresSectionRepository repository, NpgsqlDataSource source, Guid playerId)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var result = await repository.FlushPendingAsync(256, TimeSpan.FromMinutes(1));
            Assert.Equal(0, result.Failed);
            var pending = (long)(await ScalarAsync(source,
                "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", playerId))!;
            if (pending == 0) return;
        }
        throw new InvalidOperationException("本玩家 flush 队列未在有限批次内清空。");
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
