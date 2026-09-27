using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Protocol;
using Domain.Player;

namespace DaoHua.Bench;

/// <summary>一名玩家的一条真实长连接：单发送者、独立持续接收、按 RequestId 关联回执。</summary>
internal sealed class BenchWebSocketSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CommandResultDto>> _pending = new();
    private readonly TaskCompletionSource<HelloResponse> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PlayerSnapshotDto> _initialSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiver;
    private long _sequence;
    private Exception? _receiveFailure;
    private long _receivedFrames;
    private long _receivedBytes;
    private long _sentBytes;
    private long _logFrames;
    private int _socketClosed;
    public HttpClient Client { get; }
    public TestCharacter Character { get; }
    public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long LogFrames => Interlocked.Read(ref _logFrames);

    private BenchWebSocketSession(TestCharacter character, Uri url)
    {
        Character = character;
        Client = CreateClient(url);
    }

    public static async Task<HttpClient> CreateControlClientAsync(TestCharacter character, BenchOptions options, CancellationToken cancellationToken)
    {
        var client = CreateClient(options.Url);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            await AuthenticateAsync(client, character, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<BenchWebSocketSession> ConnectAsync(TestCharacter character, BenchOptions options, CancellationToken cancellationToken)
    {
        var session = new BenchWebSocketSession(character, options.Url);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            var token = timeout.Token;
            session._socket.Options.SetRequestHeader("Cookie", await AuthenticateAsync(session.Client, character, token));
            var address = new UriBuilder(options.Url) { Scheme = options.Url.Scheme == "https" ? "wss" : "ws", Path = "/ws" };
            await session._socket.ConnectAsync(address.Uri, token);
            session._receiver = session.ReceiveLoopAsync();
            var helloId = TestCharacterSeeder.StableGuid($"ws:hello:{character.PlayerId:D}:{options.Mode}");
            await session.SendAsync(WireEnvelopeCodec.EncodeJson(WireMessageType.Hello, helloId, ++session._sequence,
                new HelloRequest("bench", character.GameDataFingerprint)), token);
            var hello = await session._hello.Task.WaitAsync(token);
            if (hello.GameDataFingerprint != character.GameDataFingerprint)
                throw new InvalidOperationException("WS HelloAck 配表指纹与 manifest 不符。");
            var snapshot = await session._initialSnapshot.Task.WaitAsync(token);
            if (snapshot.PlayerId != character.PlayerId.ToString("D")) throw new InvalidOperationException("WS 初始快照玩家不符。");
            if (options.Mode is "replay" or "offline-login")
            {
                var expectedTime = options.Mode == "offline-login" ? character.StartAt.AddHours(options.TargetHours) : character.StartAt;
                if (snapshot.LastSimulatedAt != expectedTime)
                    throw new InvalidOperationException("WS 初始时钟不等于预期业务时间，拒绝旧状态/旧缓存。");
            }
            var presenceId = TestCharacterSeeder.StableGuid($"ws:presence:{character.PlayerId:D}:{options.Mode}");
            await session.CommandAsync("player.presence", presenceId,
                JsonSerializer.SerializeToElement(new { requestId = presenceId, presence = "online" }, BenchJson.Options), token);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static HttpClient CreateClient(Uri url)
        => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
        { BaseAddress = new Uri(url, "/"), Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<string> AuthenticateAsync(HttpClient client, TestCharacter character, CancellationToken cancellationToken)
    {
        using var auth = await client.PostAsJsonAsync("api/debug/bench/session", new { playerId = character.PlayerId }, cancellationToken);
        if (!auth.IsSuccessStatusCode) throw new InvalidOperationException($"基准会话创建失败 HTTP {(int)auth.StatusCode}。");
        using var authDocument = JsonDocument.Parse(await auth.Content.ReadAsStreamAsync(cancellationToken));
        if (authDocument.RootElement.GetProperty("playerId").GetGuid() != character.PlayerId)
            throw new InvalidOperationException("基准会话返回了错误玩家。");
        if (!auth.Headers.TryGetValues("Set-Cookie", out var cookies)) throw new InvalidOperationException("基准会话未返回 Cookie。");
        return string.Join("; ", cookies.Select(value => value.Split(';', 2)[0]));
    }

    public async Task<double> AdvanceAsync(DateTimeOffset target, long ordinal, CancellationToken cancellationToken)
    {
        var requestId = TestCharacterSeeder.StableGuid($"ws:advance:{Character.PlayerId:D}:{target:O}:{ordinal}");
        return await CommandAsync("simulation.advance", requestId,
            JsonSerializer.SerializeToElement(new { requestId, targetTime = target }, BenchJson.Options), cancellationToken);
    }

    public async Task SetPresenceAsync(PresenceState presence, string mode, CancellationToken cancellationToken)
    {
        var requestId = TestCharacterSeeder.StableGuid($"ws:presence:{Character.PlayerId:D}:{mode}:{presence}");
        await CommandAsync("player.presence", requestId,
            JsonSerializer.SerializeToElement(new
            {
                requestId,
                presence = presence == PresenceState.Online ? "online" : "offline"
            }, BenchJson.Options), cancellationToken);
    }

    private async Task<double> CommandAsync(string command, Guid requestId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (_receiveFailure is { } failure) throw new InvalidOperationException("WS 接收循环已失败。", failure);
        var completion = new TaskCompletionSource<CommandResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("重复的压测 RequestId。");
        var packet = WireEnvelopeCodec.EncodeJson(WireMessageType.Command, requestId, ++_sequence,
            new CommandEnvelopeDto(requestId.ToString("D"), command, payload));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await SendAsync(packet, cancellationToken);
            var result = await completion.Task.WaitAsync(cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException($"命令失败 {result.Code}: {result.Message}");
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
        Interlocked.Add(ref _sentBytes, packet.Length);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult frame;
                do
                {
                    frame = await _socket.ReceiveAsync(buffer, _lifetime.Token);
                    if (frame.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException($"服务端关闭 WS：{_socket.CloseStatus} {_socket.CloseStatusDescription}");
                    if (frame.MessageType != WebSocketMessageType.Binary) throw new InvalidOperationException("WS 收到非二进制帧。");
                    if (message.Length + frame.Count > ProtocolConstants.MaxFrameBytes + 64) throw new InvalidOperationException("WS 帧超过上限。");
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                Interlocked.Increment(ref _receivedFrames);
                Interlocked.Add(ref _receivedBytes, message.Length);
                if (!WireEnvelopeCodec.TryDecode(message.GetBuffer().AsSpan(0, (int)message.Length), out var envelope))
                    throw new InvalidOperationException("WS 信封不可解码。");
                switch (envelope.MessageType)
                {
                    case WireMessageType.HelloAck:
                        if (!WireEnvelopeCodec.TryDeserializeJson<HelloResponse>(envelope, out var hello) || hello is null)
                            throw new InvalidOperationException("HelloAck 不可解码。");
                        _hello.TrySetResult(hello);
                        break;
                    case WireMessageType.Snapshot:
                        if (!_initialSnapshot.Task.IsCompleted)
                        {
                            if (!WireEnvelopeCodec.TryDeserializeJson<PlayerSnapshotDto>(envelope, out var snapshot) || snapshot is null)
                                throw new InvalidOperationException("初始快照不可解码。");
                            _initialSnapshot.TrySetResult(snapshot);
                        }
                        break;
                    case WireMessageType.CommandResult:
                        if (!WireEnvelopeCodec.TryDeserializeJson<CommandResultDto>(envelope, out var receipt) || receipt is null)
                            throw new InvalidOperationException("命令回执不可解码。");
                        if (_pending.TryGetValue(envelope.RequestId, out var completion)) completion.TrySetResult(receipt);
                        break;
                    case WireMessageType.LogBatch:
                        Interlocked.Increment(ref _logFrames);
                        break;
                    case WireMessageType.Error:
                        throw new InvalidOperationException($"WS 返回协议错误：{System.Text.Encoding.UTF8.GetString(envelope.Payload.Span)}");
                }
            }
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _receiveFailure = exception;
            _hello.TrySetException(exception);
            _initialSnapshot.TrySetException(exception);
            foreach (var pending in _pending.Values) pending.TrySetException(exception);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (WebSocketException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task CloseWebSocketAsync()
    {
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0) return;
        _lifetime.Cancel();
        _socket.Abort();
        if (_receiver is not null) await _receiver;
        _socket.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseWebSocketAsync();
        Client.Dispose();
        _lifetime.Dispose();
    }
}
