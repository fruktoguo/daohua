using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// WebSocketEndpoint 异常处理与正常断开降级日志门禁：
/// 验证客户端断开或 socket 进入 Aborted 状态时，SendLoopAsync 与 WebSocket 异常不会向外抛出致命错误，
/// 且日志被降级为 Debug 级别，杜绝线上巡检捕获到 fail: 误报。
/// </summary>
public sealed class WebSocketRuntimeTests
{
    private sealed class TestLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class FaultyWebSocket : WebSocket
    {
        private WebSocketState _state;
        private readonly Exception? _exceptionOnSend;

        public FaultyWebSocket(WebSocketState state, Exception? exceptionOnSend = null)
        {
            _state = state;
            _exceptionOnSend = exceptionOnSend;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return Task.FromException<WebSocketReceiveResult>(new WebSocketException("Receive faulted"));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (_exceptionOnSend is not null)
            {
                return Task.FromException(_exceptionOnSend);
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SendLoopAsync_WhenWebSocketExceptionThrown_LogsDebugAndCompletesGracefully()
    {
        var playerId = Guid.NewGuid();
        var session = new WebSocketSession(playerId, 10);
        var logger = new TestLogger();
        var wsEx = new WebSocketException("The WebSocket is in an invalid state ('Aborted') for this operation.");
        var socket = new FaultyWebSocket(WebSocketState.Aborted, wsEx);

        Assert.True(session.TrySend(new byte[] { 1, 2, 3 }));

        // SendLoopAsync 必须优雅捕获 WebSocketException 并退出，而不是向调用方抛出未捕获异常
        await WebSocketEndpoint.SendLoopAsync(socket, session, () => { }, CancellationToken.None, logger);

        // 验证日志中记录了 Debug，且没有产生 Error/Critical
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Debug && l.Exception == wsEx);
        Assert.DoesNotContain(logger.Logs, l => l.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task FragmentedInput_AllowsExactLimitAndReusesReaderForNextMessage()
    {
        var payload = new byte[4096];
        payload[0] = 0x42;
        payload[^1] = 0x2A;
        using var frames = new MemoryStream();
        // 不带掩码的服务器帧：未结束的二进制分片、空结束分片、下一条消息。
        frames.Write(new byte[] { 0x02, 126, 0x10, 0x00 });
        frames.Write(payload);
        frames.Write(new byte[] { 0x80, 0x00, 0x82, 0x01, 0x55 });
        frames.Position = 0;
        using var socket = WebSocket.CreateFromStream(frames, false, null, Timeout.InfiniteTimeSpan);
        using var reader = new PlayerWebSocketMessageReader(payload.Length);
        Assert.Equal(payload, (await reader.ReadAsync(socket, CancellationToken.None))!.Value.ToArray());
        Assert.Equal(new byte[] { 0x55 }, (await reader.ReadAsync(socket, CancellationToken.None))!.Value.ToArray());

        frames.Position = 0;
        using var socketAgain = WebSocket.CreateFromStream(frames, false, null, Timeout.InfiniteTimeSpan);
        using var smallerReader = new PlayerWebSocketMessageReader(payload.Length - 1);
        await Assert.ThrowsAsync<WebSocketException>(() => smallerReader.ReadAsync(socketAgain, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SaturatedQueue_DoesNotEncodeDroppedFrames_AndKeepsReliableOrder()
    {
        var session = new WebSocketSession(Guid.NewGuid(), 1);
        var first = Guid.NewGuid();
        var required = Guid.NewGuid();
        var last = Guid.NewGuid();
        Assert.True(session.TrySendJsonDroppable(WireMessageType.LogBatch, first, new { }));
        var write = session.SendJsonAsync(WireMessageType.Pong, required, new { }, CancellationToken.None);
        Assert.False(write.IsCompleted);
        // 这个负载若被投影/编码会抛异常；不应为注定丢弃的帧消耗 CPU。
        Assert.False(session.TrySendJsonDroppable(WireMessageType.LogBatch, Guid.NewGuid(), new MustNotSerialize()));
        Assert.True(session.Outbound.TryRead(out var firstPacket));
        await write.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Outbound.TryRead(out var requiredPacket));
        Assert.True(session.TrySendJsonDroppable(WireMessageType.LogBatch, last, new { }));
        Assert.True(session.Outbound.TryRead(out var lastPacket));
        var envelopes = new[] { firstPacket, requiredPacket, lastPacket }.Select(packet => WireEnvelopeCodec.Decode(packet)).ToArray();
        Assert.Equal(new[] { first, required, last }, envelopes.Select(e => e.RequestId));
        Assert.Equal(new long[] { 1, 2, 3 }, envelopes.Select(e => e.Sequence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendSuccess_EchoesOriginalRequestOnceAndDoesNotBlockPeerBroadcast(bool senderQueueFull)
    {
        var sender = new WebSocketSession(Guid.NewGuid(), 1);
        var peer = new WebSocketSession(Guid.NewGuid(), 1);
        var sessions = new SessionRegistry();
        Assert.True(sessions.TryAdd(sender));
        Assert.True(sessions.TryAdd(peer));
        var requestId = Guid.NewGuid();
        var message = new ChatMessageDto(Guid.NewGuid().ToString("D"), ChatChannels.World, "", sender.PlayerId.ToString("D"),
            "发言者", "回复", 100, Quote: new(Guid.NewGuid().ToString("D"), "原作者", "原文"));
        if (senderQueueFull)
            Assert.True(sender.TrySendJsonDroppable(WireMessageType.LogBatch, Guid.NewGuid(), new { }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var completion = WebSocketEndpoint.CompleteChatSendAsync(sender, sessions, requestId,
            new(ChatSendStatus.Ok, message, [sender.PlayerId, peer.PlayerId]), timeout.Token);
        Assert.True(peer.Outbound.TryRead(out var broadcastPacket));
        var broadcast = WireEnvelopeCodec.Decode(broadcastPacket);
        Assert.Equal(WireMessageType.ChatMessage, broadcast.MessageType);
        Assert.NotEqual(requestId, broadcast.RequestId);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<ChatMessageDto>(broadcast, out var broadcastMessage));
        Assert.Equal(message, broadcastMessage);

        if (senderQueueFull)
        {
            Assert.False(completion.IsCompleted);
            Assert.True(sender.Outbound.TryRead(out var queuedPacket));
            Assert.Equal(WireMessageType.LogBatch, WireEnvelopeCodec.Decode(queuedPacket).MessageType);
        }
        else
        {
            // 空队列若先收到一份给自己的广播，可靠回显会等待，且编号错误。
            Assert.True(completion.IsCompletedSuccessfully);
        }
        await completion;
        Assert.True(sender.Outbound.TryRead(out var echoPacket));
        var echo = WireEnvelopeCodec.Decode(echoPacket);
        Assert.Equal(WireMessageType.ChatMessage, echo.MessageType);
        Assert.Equal(requestId, echo.RequestId);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<ChatMessageDto>(echo, out var echoMessage));
        Assert.Equal(message, echoMessage);
        Assert.False(sender.Outbound.TryRead(out _));
        Assert.False(peer.Outbound.TryRead(out _));
    }

    private sealed class MustNotSerialize
    {
        public int Value => throw new InvalidOperationException();
    }

    [Fact]
    public async Task SendLoopAsync_WhenOperationCanceled_ExitsWithoutError()
    {
        var playerId = Guid.NewGuid();
        var session = new WebSocketSession(playerId, 10);
        var logger = new TestLogger();
        var socket = new FaultyWebSocket(WebSocketState.Open);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await WebSocketEndpoint.SendLoopAsync(socket, session, () => { }, cts.Token, logger);

        Assert.DoesNotContain(logger.Logs, l => l.Level >= LogLevel.Error);
    }

    /// <summary>ActionIntentResponse 拒绝应答不得伪造 accepted=true/status=queued；PartyLobbyEntryDto 正确序列化 openSlots。</summary>
    [Fact]
    public void ProtocolContracts_ActionIntentResponseAndPartyLobbyEntry_SerializeRejectionAndOpenSlots()
    {
        var queued = new ActionIntentResponse("req-1", "act-1", "cultivation.draw-qi", DateTimeOffset.UnixEpoch, 5);
        var queuedJson = JsonSerializer.Serialize(queued, ProtocolJson.Options);
        using (var queuedDoc = JsonDocument.Parse(queuedJson))
        {
            Assert.True(queuedDoc.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("queued", queuedDoc.RootElement.GetProperty("status").GetString());
        }

        var rejected = new ActionIntentResponse(
            "req-2", string.Empty, "secret_art.liquefy", DateTimeOffset.UnixEpoch, 0,
            Accepted: false, Status: "rejected", Message: "目标物品无效");
        var rejectedJson = JsonSerializer.Serialize(rejected, ProtocolJson.Options);
        using (var rejectedDoc = JsonDocument.Parse(rejectedJson))
        {
            Assert.False(rejectedDoc.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("rejected", rejectedDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal("目标物品无效", rejectedDoc.RootElement.GetProperty("message").GetString());
        }

        var lobbyEntry = new PartyLobbyEntryDto(
            Guid.NewGuid().ToString("D"),
            "青云小队",
            "combat",
            "node.cloud-creek",
            Guid.NewGuid().ToString("D"),
            "队长",
            100,
            1_700_000_000_000L,
            [],
            OpenSlots: 2);
        var lobbyJson = JsonSerializer.Serialize(lobbyEntry, ProtocolJson.Options);
        using var lobbyDoc = JsonDocument.Parse(lobbyJson);
        Assert.Equal(2, lobbyDoc.RootElement.GetProperty("openSlots").GetInt32());
    }
}
