using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
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
    /// <summary>日志分组按事件码前缀分类：combat.* 归战斗，其余（含 duel.*）归系统。</summary>
    [Theory]
    [InlineData("combat.started", LogChannels.Combat)]
    [InlineData("combat.attack", LogChannels.Combat)]
    [InlineData("combat.won", LogChannels.Combat)]
    [InlineData("duel.won", LogChannels.System)]
    [InlineData("action.completed", LogChannels.System)]
    [InlineData("world.arrived", LogChannels.System)]
    [InlineData("offline.capped", LogChannels.System)]
    [InlineData("auto-use.activated", LogChannels.System)]
    public void LogChannels_ClassifyByEventCodePrefix(string eventCode, string expected) =>
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

    /// <summary>聊天消息按作用域只保留最近 100 条，超出即删除；历史回放按时间正序。</summary>
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
        for (var index = 0; index < 105; index++)
        {
            await chatRepository.AppendAsync(new ChatMessageRecord(
                Guid.NewGuid(), ChatChannels.Nearby, scopeKey, sender, senderName, $"第 {index} 条", baseTime.AddSeconds(index)), 100);
        }

        var recent = await chatRepository.LoadRecentAsync(ChatChannels.Nearby, scopeKey, 100);

        Assert.Equal(100, recent.Count);
        Assert.Equal("第 5 条", recent[0].Body);
        Assert.Equal("第 104 条", recent[^1].Body);
        var stored = await ScalarAsync(source, "SELECT count(*) FROM social.chat_messages WHERE channel = 'nearby' AND scope_key = $1", scopeKey);
        Assert.Equal(100L, Convert.ToInt64(stored));
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
        var social = new SocialRuntime(repository, new OnlinePresenceRegistry(), NullLogger<SocialRuntime>.Instance);

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
        var social = new SocialRuntime(repository, new OnlinePresenceRegistry(), NullLogger<SocialRuntime>.Instance);

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

    private static async Task<(NpgsqlDataSource Source, PostgresSocialRepository Repository)> OpenAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("传音/社交集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        return (source, new PostgresSocialRepository(source));
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
            "INSERT INTO identity.players VALUES ($1,$1,$2,$3,now(),now(),1)",
            playerId, displayName, displayName.ToUpperInvariant());
        return (playerId, displayName);
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
