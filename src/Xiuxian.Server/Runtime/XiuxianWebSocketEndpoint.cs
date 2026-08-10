using System.Buffers;
using System.Net.WebSockets;
using Xiuxian.Protocol;

namespace Xiuxian.Server.Runtime;

public static class XiuxianWebSocketEndpoint
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaxMessageSize = 64 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required.");
            return;
        }

        var gameHost = context.RequestServices.GetRequiredService<XiuxianGameHost>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("XiuxianWebSocket");
        var playerId = Guid.TryParse(context.Request.Query["playerId"], out var parsedPlayerId)
            ? parsedPlayerId
            : Guid.Parse("75d1ffb6-2f03-42e0-bdc1-cdce8b5d9a31");
        var playerName = context.Request.Query["name"].FirstOrDefault();
        context.Response.Cookies.Append("xiuxian_player_id", playerId.ToString("D"), new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(3650),
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            HttpOnly = false,
            IsEssential = true
        });

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        XiuxianSession? session = null;

        try
        {
            session = await gameHost.RegisterSessionAsync(playerId, playerName, connectionCts.Token);
            var sendTask = SendLoopAsync(socket, session, connectionCts.Token);
            var receiveTask = ReceiveLoopAsync(socket, session, gameHost, connectionCts.Token);
            await Task.WhenAny(sendTask, receiveTask);
            connectionCts.Cancel();
            await Task.WhenAll(
                IgnoreCancellation(sendTask, connectionCts.Token),
                IgnoreCancellation(receiveTask, connectionCts.Token));
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
            // 浏览器断开或服务停机。
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "WebSocket 会话异常 {PlayerId}", playerId);
        }
        finally
        {
            if (session is not null)
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await gameHost.UnregisterSessionAsync(session, cleanupCts.Token);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "清理 WebSocket 会话失败 {PlayerId}", playerId);
                }
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", closeCts.Token);
                }
                catch (WebSocketException)
                {
                    socket.Abort();
                }
            }
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        XiuxianSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var packet in session.Outbound.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        XiuxianSession session,
        XiuxianGameHost gameHost,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var packetBytes = await ReceivePacketAsync(socket, cancellationToken);
            if (packetBytes is null) return;

            if (!XiuxianPacketCodec.TryDecode(packetBytes, out var packet))
            {
                session.TryEnqueue(XiuxianPacketCodec.Encode(
                    XiuxianPacketKinds.Error,
                    string.Empty,
                    gameHost.SimulationRevision,
                    new XiuxianErrorDto("INVALID_PACKET", "无法解析数据包。")));
                continue;
            }

            if (packet.Kind == XiuxianPacketKinds.Hello)
            {
                session.TryEnqueue(XiuxianPacketCodec.Encode(
                    XiuxianPacketKinds.Ack,
                    packet.RequestId,
                    gameHost.SimulationRevision,
                    new XiuxianAckDto(true, "OK", "会话已建立。", DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                continue;
            }

            var outcome = await gameHost.HandleCommandAsync(session.PlayerId, packet, cancellationToken);
            session.TryEnqueue(XiuxianPacketCodec.Encode(
                XiuxianPacketKinds.Ack,
                packet.RequestId,
                gameHost.SimulationRevision,
                new XiuxianAckDto(
                    outcome.Result.Success,
                    outcome.Result.Code,
                    outcome.Result.Message,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
    }

    private static async Task<byte[]?> ReceivePacketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var stream = new MemoryStream(ReceiveBufferSize);
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType is not (WebSocketMessageType.Binary or WebSocketMessageType.Text))
                {
                    throw new WebSocketException("Unsupported WebSocket message type.");
                }

                if (stream.Length + result.Count > MaxMessageSize)
                {
                    throw new WebSocketException("WebSocket message exceeds 64 KiB.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return stream.ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task IgnoreCancellation(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // 连接生命周期内的预期取消。
        }
        catch (WebSocketException) when (cancellationToken.IsCancellationRequested)
        {
            // 对端直接断开时，底层可能不会完成标准 close handshake；这属于正常断线。
        }
    }
}
