using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Server.Clean;
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
}
