// ==============================================================================
// 文件名称: WebSocketEndpoint.cs
// 主要职责: WebSocket 连接接入管理与网络消息双工调度终结点
// 核心逻辑 / 定义内容:
//   1. 验证客户端连接请求（协议升级、鉴权令牌、玩家 GUID 格式解析）。
//   2. 创建并注册客户端会话（Session），启动独立的发送与接收双工循环。
//   3. 内存池（ArrayPool）分片拼包管理，限制单包上限并解码路由上行指令。
//   4. 处理网络断开、超时与异常捕获，确保会话优雅注销与资源释放。
// ==============================================================================

using System.Buffers;
using System.Net.WebSockets;
using Protocol;
using Server.Infrastructure;

namespace Server.Runtime;

/// <summary>
/// WebSocket 连接接入与消息调度终结点 (WebSocketEndpoint)。
/// <para>
/// 职责：
/// 1. 验证客户端连接请求（协议升级、鉴权令牌、玩家 GUID 格式）。
/// 2. 为连接创建并注册 <see cref="Session"/>，并启动双工收发循环 (SendLoop / ReceiveLoop)。
/// 3. 处理网络层数据分片拼包（基于 ArrayPool 内存池复用，限制单包最大 64 KiB），解码并路由指令。
/// 4. 在客户端断开或异常时执行会话注销与资源优雅释放，提供完善的异常隔离保护。
/// </para>
/// </summary>
public static class WebSocketEndpoint
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaxMessageSize = 64 * 1024;

    /// <summary>认证失败专用应用层关闭码（4000~4999 应用可自定义区间），前端按 1008/4001~4004 精准熔断。</summary>
    private const int AuthCloseInvalidToken = 4001;
    private const int AuthCloseInvalidPlayerId = 4002;

    /// <summary>
    /// 处理 HTTP 到 WebSocket 升级请求并托管会话完整生命周期。
    /// </summary>
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required.");
            return;
        }

        var gameHost = context.RequestServices.GetRequiredService<GameHost>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocket");
        var accessControl = context.RequestServices.GetRequiredService<AccessControl>();
        // 先完成协议升级再校验身份：浏览器对升级前拒绝只会报 1006，无法区分认证失败与网络抖动；
        // 升级后以应用层关闭码(4001/4002)下发，前端据此精准熔断，不再无限重连。
        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        // playerId 即权限：解析失败直接关闭，绝不回退到固定玩家，防止公网访客借未知参数接管默认账号。
        if (!Guid.TryParse(context.Request.Query["playerId"], out var playerId))
        {
            await CloseWithAuthCodeAsync(socket, AuthCloseInvalidPlayerId, "playerId 无效。");
            return;
        }
        if (!accessControl.IsValid(context.Request.Query["token"]))
        {
            await CloseWithAuthCodeAsync(socket, AuthCloseInvalidToken, "缺少或错误的访问令牌。");
            return;
        }

        var playerName = SanitizePlayerName(context.Request.Query["name"].FirstOrDefault());
        var account = SanitizePlayerName(context.Request.Query["account"].FirstOrDefault()) ?? playerName;
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Session? session = null;

        try
        {
            session = await gameHost.RegisterSessionAsync(playerId, playerName, account, connectionCts.Token);
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

    /// <summary>净化玩家显示名：Trim 后剔除控制字符并截断到 32 字符，空结果归为 null。</summary>
    private static string? SanitizePlayerName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;
        var sanitized = new string(rawName.Trim().Where(ch => !char.IsControl(ch)).Take(32).ToArray());
        return sanitized.Length == 0 ? null : sanitized;
    }

    /// <summary>
    /// 以认证失败应用层关闭码优雅关闭连接；对端已断开等关闭握手异常时直接中止。
    /// </summary>
    private static async Task CloseWithAuthCodeAsync(WebSocket socket, int closeCode, string reason)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseAsync((WebSocketCloseStatus)closeCode, reason, cts.Token);
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
    }

    /// <summary>
    /// 下行发送循环：从 Session 的 Channel 中消费二进制数据包并写入 WebSocket。
    /// </summary>
    private static async Task SendLoopAsync(
        WebSocket socket,
        Session session,
        CancellationToken cancellationToken)
    {
        await foreach (var packet in session.Outbound.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    /// <summary>
    /// 上行接收循环：从 WebSocket 持续读取数据包、解码请求并转发 GameHost 执行，随后下发 Ack 响应。
    /// </summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        Session session,
        GameHost gameHost,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var packetBytes = await ReceivePacketAsync(socket, cancellationToken);
            if (packetBytes is null) return;

            if (!PacketCodec.TryDecode(packetBytes, out var packet))
            {
                session.TryEnqueue(PacketCodec.Encode(
                    PacketKinds.Error,
                    string.Empty,
                    gameHost.SimulationRevision,
                    new ErrorDto("INVALID_PACKET", "无法解析数据包。")));
                continue;
            }

            if (packet.Kind == PacketKinds.Hello)
            {
                session.TryEnqueue(PacketCodec.Encode(
                    PacketKinds.Ack,
                    packet.RequestId,
                    gameHost.SimulationRevision,
                    new AckDto(true, "OK", "会话已建立。", DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                continue;
            }

            if (packet.Kind == PacketKinds.Ping)
            {
                session.TryEnqueue(PacketCodec.Encode(
                    PacketKinds.Pong,
                    packet.RequestId,
                    gameHost.SimulationRevision,
                    new AckDto(true, "OK", "pong", DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                continue;
            }

            var outcome = await gameHost.HandleCommandAsync(session.PlayerId, packet, cancellationToken);
            session.TryEnqueue(PacketCodec.Encode(
                PacketKinds.Ack,
                packet.RequestId,
                gameHost.SimulationRevision,
                new AckDto(
                    outcome.Result.Success,
                    outcome.Result.Code,
                    outcome.Result.Message,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
    }

    /// <summary>
    /// 从 WebSocket 异步读取一个完整应用层数据包，支持分片重组与 ArrayPool 缓冲区复用。
    /// </summary>
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

    /// <summary>
    /// 忽略任务取消异常与非正常断开时的传输异常。
    /// </summary>
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
