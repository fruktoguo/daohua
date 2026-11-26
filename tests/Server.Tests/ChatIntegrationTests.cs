using System.Text.Json;
using Domain.GameData;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Leaderboards;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class ChatIntegrationTests
{
    [Fact]
    public async Task QuoteCopiesRawBodyWithoutNestingAndSurvivesTrimOfOriginalAndItsLikes()
    {
        var fixture = await Fixture.CreateAsync();
        var (author, replier, _) = fixture.Players;
        var originalBody = "{{item:item.qi-gathering-pill|聚气丹}}";
        var original = await fixture.Repository.AppendAsync(new(Guid.NewGuid(), ChatChannels.Nearby, fixture.Node, author,
            "旧道号", originalBody, DateTimeOffset.UtcNow.AddMinutes(-1)), ChatRuntime.RetentionPerScope);
        Assert.NotNull(original);
        Assert.Equal(ChatLikeStatus.Ok, (await fixture.Chat.SetLikeAsync(replier,
            new(ChatChannels.Nearby, original.MessageId.ToString("D"), true))).Status);

        var fullBody = new string('道', 200);
        var reply = await fixture.Chat.SendAsync(replier, "回帖者", new("reply", ChatChannels.Nearby, fullBody,
            QuoteMessageId: original.MessageId.ToString("D")));
        Assert.Equal(ChatSendStatus.Ok, reply.Status);
        Assert.Equal(fullBody, reply.Message!.Body);
        Assert.Equal(new ChatQuoteDto(original.MessageId.ToString("D"), "旧道号", originalBody), reply.Message.Quote);
        Assert.Contains(replier, reply.Recipients!);
        var nested = await fixture.Chat.SendAsync(author, "新道号", new("nested", ChatChannels.Nearby, "再次回复",
            QuoteMessageId: reply.Message.MessageId));
        Assert.Equal(ChatSendStatus.Ok, nested.Status);
        Assert.Equal(new ChatQuoteDto(reply.Message.MessageId, "回帖者", fullBody), nested.Message!.Quote);
        Assert.Equal(ChatSendStatus.InvalidBody, (await fixture.Chat.SendAsync(author, "新道号",
            new("long", ChatChannels.Nearby, fullBody + "道", QuoteMessageId: reply.Message.MessageId))).Status);

        // 推到同一作用域的第 16 次写入，使用较小留存上限触发真正的裁剪 SQL。
        for (var i = 0; i < 13; i++)
            Assert.NotNull(await fixture.Repository.AppendAsync(original with
            {
                MessageId = Guid.NewGuid(), SentAt = original.SentAt.AddMinutes(-1)
            }, retention: 2));

        var history = await fixture.Chat.LoadHistoryAsync(replier, new(ChatChannels.Nearby));
        Assert.Equal(new[] { reply.Message, nested.Message }, history!.Messages);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM social.chat_message_likes WHERE message_id = $1", original.MessageId));
        Assert.Equal(ChatSendStatus.QuoteUnavailable, (await fixture.Chat.SendAsync(author, "新道号",
            new("old", ChatChannels.Nearby, "引用已裁剪", QuoteMessageId: original.MessageId.ToString("D")))).Status);
        Assert.Equal(ChatLikeStatus.Unavailable, (await fixture.Chat.SetLikeAsync(author,
            new(ChatChannels.Nearby, original.MessageId.ToString("D"), true))).Status);
    }

    [Fact]
    public async Task GuessingAnIdCannotCrossNodeChannelOrPrivateConversation()
    {
        var fixture = await Fixture.CreateAsync();
        var (author, peer, outsider) = fixture.Players;
        await fixture.SqlAsync("""
            INSERT INTO social.friendships(player_id, friend_id, created_at)
            VALUES ($1, $2, now()), ($2, $1, now()), ($1, $3, now()), ($3, $1, now());
            """, author, peer, outsider);
        var nearby = await fixture.Chat.SendAsync(author, "作者", new("near", ChatChannels.Nearby, "附近正文"));
        var privateMessage = await fixture.Chat.SendAsync(author, "作者", new("private", ChatChannels.Friend, "私聊正文", peer.ToString("D")));
        Assert.Equal(ChatSendStatus.Ok, nearby.Status);
        Assert.Equal(ChatSendStatus.Ok, privateMessage.Status);
        var nearbyId = nearby.Message!.MessageId;
        var privateId = privateMessage.Message!.MessageId;

        Assert.Equal(ChatSendStatus.QuoteUnavailable, (await fixture.Chat.SendAsync(outsider, "外人",
            new("wrong-node", ChatChannels.Nearby, "试图引用", QuoteMessageId: nearbyId))).Status);
        Assert.Equal(ChatLikeStatus.Unavailable, (await fixture.Chat.SetLikeAsync(outsider,
            new(ChatChannels.Nearby, nearbyId, true))).Status);
        Assert.Equal(ChatLikeStatus.Unavailable, (await fixture.Chat.SetLikeAsync(peer,
            new(ChatChannels.World, nearbyId, true))).Status);
        Assert.Equal(ChatSendStatus.QuoteUnavailable, (await fixture.Chat.SendAsync(outsider, "外人",
            new("wrong-pair", ChatChannels.Friend, "试图引用", author.ToString("D"), privateId))).Status);
        Assert.Equal(ChatLikeStatus.Unavailable, (await fixture.Chat.SetLikeAsync(outsider,
            new(ChatChannels.Friend, privateId, true, author.ToString("D")))).Status);

        var ownLike = await fixture.Chat.SetLikeAsync(peer, new(ChatChannels.Friend, privateId, true, author.ToString("D")));
        Assert.Equal(ChatLikeStatus.Ok, ownLike.Status);
        Assert.Equal(new[] { peer, author }, ownLike.Recipients);
        Assert.Equal(ChatRuntime.BuildFriendScopeKey(author, peer), ownLike.Update!.ScopeKey);
        var nearbyLike = await fixture.Chat.SetLikeAsync(peer, new(ChatChannels.Nearby, nearbyId, true));
        Assert.Equal(ChatLikeStatus.Ok, nearbyLike.Status);
        Assert.Contains(author, nearbyLike.Recipients!);
        Assert.Contains(peer, nearbyLike.Recipients!);
        Assert.DoesNotContain(outsider, nearbyLike.Recipients!);

        await fixture.SqlAsync("DELETE FROM social.friendships WHERE (player_id, friend_id) IN (($1, $2), ($2, $1))", author, peer);
        Assert.Equal(ChatSendStatus.QuoteUnavailable, (await fixture.Chat.SendAsync(peer, "好友",
            new("revoked", ChatChannels.Friend, "友谊已解除", author.ToString("D"), privateId))).Status);
        Assert.Equal(ChatLikeStatus.Unavailable, (await fixture.Chat.SetLikeAsync(peer,
            new(ChatChannels.Friend, privateId, false, author.ToString("D")))).Status);
        Assert.Null(await fixture.Chat.LoadHistoryAsync(peer, new(ChatChannels.Friend, author.ToString("D"))));
    }

    [Fact]
    public async Task ConcurrentLikeSetsSerializeAndHistoryAgreesWithCommittedVersionPerViewer()
    {
        var fixture = await Fixture.CreateAsync();
        var (author, peer, _) = fixture.Players;
        var message = (await fixture.Chat.SendAsync(author, "作者", new("near", ChatChannels.Nearby, "点赞测试"))).Message!;
        ChatLikeSetDto Request(bool liked) => new(ChatChannels.Nearby, message.MessageId, liked);
        var attempts = await Task.WhenAll(
            fixture.Chat.SetLikeAsync(author, Request(true)).AsTask(),
            fixture.Chat.SetLikeAsync(peer, Request(true)).AsTask(),
            fixture.Chat.SetLikeAsync(peer, Request(true)).AsTask());
        Assert.All(attempts, result => Assert.Equal(ChatLikeStatus.Ok, result.Status));
        Assert.Equal(new long[] { 1, 2 }, attempts.Select(result => result.Update!.LikeVersion).Distinct().Order().ToArray());
        Assert.All(attempts, result => Assert.Equal((double)result.Update!.LikeVersion, result.Update.LikeCount));
        Assert.Single(attempts, result => result.Recipients!.Count == 1);

        var before = Assert.Single((await fixture.Chat.LoadHistoryAsync(peer, new(ChatChannels.Nearby)))!.Messages);
        Assert.True(before.LikedByMe);
        Assert.Equal(2, before.LikeCount);
        Assert.Equal(2, before.LikeVersion);

        var removed = await fixture.Chat.SetLikeAsync(peer, Request(false));
        Assert.Equal(ChatLikeStatus.Ok, removed.Status);
        Assert.False(removed.Update!.Liked);
        Assert.Equal(1, removed.Update.LikeCount);
        Assert.Equal(3, removed.Update.LikeVersion);
        var ownHistory = Assert.Single((await fixture.Chat.LoadHistoryAsync(author, new(ChatChannels.Nearby)))!.Messages);
        var peerHistory = Assert.Single((await fixture.Chat.LoadHistoryAsync(peer, new(ChatChannels.Nearby)))!.Messages);
        Assert.Equal(removed.Update.LikeVersion, ownHistory.LikeVersion);
        Assert.Equal(removed.Update.LikeCount, ownHistory.LikeCount);
        Assert.True(ownHistory.LikedByMe);
        Assert.Equal(ownHistory with { LikedByMe = false }, peerHistory);
    }

    [Fact]
    public async Task LikeSpamUsesItsOwnRateBudgetInsteadOfConsumingChatSends()
    {
        var fixture = await Fixture.CreateAsync();
        var (author, _, _) = fixture.Players;
        var message = (await fixture.Chat.SendAsync(author, "作者", new("near", ChatChannels.Nearby, "点赞限频"))).Message!;
        var request = new ChatLikeSetDto(ChatChannels.Nearby, message.MessageId, false);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Chat.SetLikeAsync(author, request).AsTask()));
        Assert.Equal(ChatLikeStatus.RateLimited, attempts[3].Status);
        Assert.All(attempts.Take(3), result =>
        {
            Assert.Equal(ChatLikeStatus.Ok, result.Status);
            Assert.Equal(0, result.Update!.LikeVersion);
        });
        Assert.Equal(ChatSendStatus.Ok,
            (await fixture.Chat.SendAsync(author, "作者", new("after-spam", ChatChannels.Nearby, "仍可发言"))).Status);
    }

    [Fact]
    public async Task SendAndLoadHistory_AttachesBestPersonalTopRankWithinTop50AndExcludesZongmen()
    {
        var fixture = await Fixture.CreateAsync();
        var (author, peer, outsider) = fixture.Players;
        var now = DateTimeOffset.UtcNow;

        // author 同时在 4 个个人榜并列第 2（炼丹/身家/灵石/斗法）且炼器第 10：
        // 并列优先序为 境界 > 斗法 > 身家 > 灵石 > 七技艺，故应取 duel #2。
        // peer 仅持有宗门榜第 1（排除）与境界榜第 51（超 Top 50）：应返回 null。
        // outsider 持有炼丹榜第 1 与境界榜第 3：最小名次为 1，故应取 technique.alchemy #1。
        await fixture.SqlAsync("""
            INSERT INTO game.leaderboard_ranks(player_id, board_id, rank, score, updated_at)
            VALUES ($1, 'technique.alchemy', 2, 1000, $4),
                   ($1, 'valuation', 2, 2000, $4),
                   ($1, 'spirit-stone', 2, 3000, $4),
                   ($1, 'duel', 2, 15, $4),
                   ($1, 'technique.forging', 10, 500, $4),
                   ($2, 'zongmen', 1, 50, $4),
                   ($2, 'realm', 51, 5, $4),
                   ($3, 'technique.alchemy', 1, 5000, $4),
                   ($3, 'realm', 3, 25, $4)
            """, author, peer, outsider, now);

        var chatWithRanks = fixture.CreateChatWithLeaderboards();

        var authorSend = await chatWithRanks.SendAsync(author, "作者", new("rank-1", ChatChannels.Nearby, "并列测试"));
        Assert.Equal(ChatSendStatus.Ok, authorSend.Status);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Duel, "duel", null, 2), authorSend.Message!.SenderTopRank);

        var peerSend = await chatWithRanks.SendAsync(peer, "好友", new("rank-2", ChatChannels.Nearby, "宗门与榜外测试"));
        Assert.Equal(ChatSendStatus.Ok, peerSend.Status);
        Assert.Null(peerSend.Message!.SenderTopRank);

        await fixture.Presence.RegisterAsync(outsider, "外人", fixture.Node);
        var outsiderSend = await chatWithRanks.SendAsync(outsider, "外人", new("rank-3", ChatChannels.Nearby, "技艺第一测试"));
        Assert.Equal(ChatSendStatus.Ok, outsiderSend.Status);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Technique("alchemy"), "technique", "alchemy", 1), outsiderSend.Message!.SenderTopRank);

        // 历史回放同样按发送者实时批量派生，不依赖聊天表额外列。
        var history = await chatWithRanks.LoadHistoryAsync(author, new(ChatChannels.Nearby));
        Assert.NotNull(history);
        Assert.Equal(3, history!.Messages.Count);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Duel, "duel", null, 2), history.Messages[0].SenderTopRank);
        Assert.Null(history.Messages[1].SenderTopRank);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Technique("alchemy"), "technique", "alchemy", 1), history.Messages[2].SenderTopRank);
    }

    private sealed class Fixture
    {
        private Fixture(NpgsqlDataSource source)
        {
            Source = source;
            Repository = new PostgresChatRepository(source);
            Chat = new ChatRuntime(Repository, new PostgresSocialRepository(source), Presence);
        }

        private NpgsqlDataSource Source { get; }
        public OnlinePresenceRegistry Presence { get; } = new();
        public PostgresChatRepository Repository { get; }
        public ChatRuntime Chat { get; }
        public string Node { get; } = Guid.NewGuid().ToString("N");
        public (Guid Author, Guid Peer, Guid Outsider) Players { get; } = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        public ChatRuntime CreateChatWithLeaderboards()
        {
            var sections = new PostgresSectionRepository(Source, NullLogger<PostgresSectionRepository>.Instance);
            var leaderboards = new LeaderboardRuntime(
                Source,
                sections,
                new StubPlayerRuntime(),
                market: null,
                GameDataCatalog.LoadEmbedded(),
                NullLogger<LeaderboardRuntime>.Instance);
            return new ChatRuntime(Repository, new PostgresSocialRepository(Source), Presence, leaderboards: leaderboards);
        }

        private sealed class StubPlayerRuntime : IPlayerRuntime
        {
            public int LoadedCount => 0;
            public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
            public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
            public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
            public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken ct = default)
                => throw new NotSupportedException();
            public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken ct = default)
                => throw new NotSupportedException();
            public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
            public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command,
                JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken ct = default)
                => throw new NotSupportedException();
            public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
            public ValueTask FlushAllAsync(bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
            public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken ct = default) => ValueTask.CompletedTask;
            public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken ct = default)
                => ValueTask.CompletedTask;
            public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId,
                Action<PlayerAggregate> mutate, CancellationToken ct = default) => throw new NotSupportedException();
            public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
            public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default)
                => new([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("传音集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var fixture = new Fixture(source);
            var (author, peer, outsider) = fixture.Players;
            var ids = new[] { author, peer, outsider };
            await fixture.SqlAsync("""
                INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
                SELECT id, id::text, '\x00'::bytea, '\x00'::bytea, 100000, now(), now() FROM unnest($1::uuid[]) AS t(id)
                """, ids);
            await fixture.SqlAsync("""
                INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
                SELECT id, id, id::text, id::text, now(), now() FROM unnest($1::uuid[]) AS t(id)
                """, ids);
            await fixture.Presence.RegisterAsync(author, "作者", fixture.Node);
            await fixture.Presence.RegisterAsync(peer, "好友", fixture.Node);
            await fixture.Presence.RegisterAsync(outsider, "外人", fixture.Node + "-other");
            return fixture;
        }

        public async Task SqlAsync(string sql, params object[] args)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var arg in args) command.Parameters.AddWithValue(arg);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql, Guid id)
        {
            await using var command = Source.CreateCommand(sql);
            command.Parameters.AddWithValue(id);
            return await command.ExecuteScalarAsync();
        }
    }
}
