using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Channels;
using DaoHua.ComputeNode;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Server.Tests;

/// <summary>用可控慢 socket 驱动真实收发与排空路径，区分结果入队和发送完成。</summary>
public sealed class ComputeNodeDrainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAndServerDrainWaitForActualLastSend(bool serverInitiated)
    {
        using var gate = new SemaphoreSlim(1);
        using var worker = CreateWorker(gate);
        using var session = new CancellationTokenSource();
        using var work = new CancellationTokenSource();
        var queue = Channel.CreateBounded<byte[]>(64);
        var result = ResultFrame();
        await queue.Writer.WriteAsync(result);
        using var socket = new ControlledSocket(serverInitiated ? LinkCodec.Encode(new LinkDrain("test")) : null);
        var sending = Send(socket, queue.Reader, session.Token);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task? draining = null;
        Task? receiving = null;
        if (serverInitiated)
        {
            var drainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiving = Receive(worker, socket, queue.Writer, session, work,
                () =>
                {
                    draining = Drain(worker, session, work, queue.Writer, sending, notifyUpstream: false);
                    drainStarted.TrySetResult();
                });
            await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        else
        {
            draining = Drain(worker, session, work, queue.Writer, sending, notifyUpstream: true);
        }

        Assert.NotNull(draining);
        Assert.False(draining.IsCompleted);
        Assert.False(session.IsCancellationRequested);
        Assert.Empty(socket.Sent);
        socket.AllowSend.TrySetResult();
        await Task.WhenAll(draining, sending).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.IsCancellationRequested);
        Assert.True(work.IsCancellationRequested);
        Assert.Contains(socket.Sent, frame => frame.SequenceEqual(result));
        Assert.Equal(serverInitiated ? 1 : 2, socket.Sent.Count);
        if (receiving is not null)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
    }

    [Fact]
    public async Task DrainWaitsForLastBatchToQueueItsResultBeforeCompletingTheWriter()
    {
        using var gate = new SemaphoreSlim(1);
        using var worker = CreateWorker(gate);
        using var session = new CancellationTokenSource();
        using var work = new CancellationTokenSource();
        var inFlight = typeof(UpstreamLinkWorker).GetField("_inFlightBatches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        inFlight.SetValue(worker, 1);
        var queue = Channel.CreateBounded<byte[]>(64);
        using var socket = new ControlledSocket(null);
        socket.AllowSend.TrySetResult();
        var sending = Send(socket, queue.Reader, session.Token);
        var draining = Drain(worker, session, work, queue.Writer, sending, notifyUpstream: true);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(draining.IsCompleted);
        Assert.False(session.IsCancellationRequested);

        var result = ResultFrame();
        await queue.Writer.WriteAsync(result);
        inFlight.SetValue(worker, 0);
        await Task.WhenAll(draining, sending).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(socket.Sent, frame => frame.SequenceEqual(result));
        Assert.True(session.IsCancellationRequested);
    }

    [Fact]
    public async Task StuckSendUsesTheExistingTenSecondWindowThenCancels()
    {
        using var gate = new SemaphoreSlim(1);
        using var worker = CreateWorker(gate);
        using var session = new CancellationTokenSource();
        using var work = new CancellationTokenSource();
        var queue = Channel.CreateBounded<byte[]>(64);
        await queue.Writer.WriteAsync(ResultFrame());
        using var socket = new ControlledSocket(null);
        var sending = Send(socket, queue.Reader, session.Token);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var started = Stopwatch.StartNew();
        await Drain(worker, session, work, queue.Writer, sending, notifyUpstream: true)
            .WaitAsync(TimeSpan.FromSeconds(13));

        Assert.InRange(started.Elapsed.TotalSeconds, 9, 13);
        Assert.True(session.IsCancellationRequested);
        Assert.True(work.IsCancellationRequested);
        Assert.Empty(socket.Sent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    [Fact]
    public async Task BrokenSendFailsImmediatelyAndCancelsRemainingWork()
    {
        using var gate = new SemaphoreSlim(1);
        using var worker = CreateWorker(gate);
        using var session = new CancellationTokenSource();
        using var work = new CancellationTokenSource();
        var queue = Channel.CreateBounded<byte[]>(64);
        await queue.Writer.WriteAsync(ResultFrame());
        using var socket = new ControlledSocket(null);
        var sending = Send(socket, queue.Reader, session.Token);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var draining = Drain(worker, session, work, queue.Writer, sending, notifyUpstream: true);
        socket.AllowSend.TrySetException(new WebSocketException("本地替身断线"));

        await Assert.ThrowsAsync<WebSocketException>(() => draining.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<WebSocketException>(() => sending);
        Assert.True(session.IsCancellationRequested);
        Assert.True(work.IsCancellationRequested);
        Assert.Empty(socket.Sent);
    }

    private static UpstreamLinkWorker CreateWorker(SemaphoreSlim gate) => new(
        ["http://127.0.0.1:1"], "local-test", "drain-test", true,
        null!, null!, new ComputeMetrics(), gate, 64, 1, NullLoggerFactory.Instance);

    private static byte[] ResultFrame() => LinkCodec.Encode(new LinkResult(Guid.NewGuid(),
        new AdvanceBatchResponse(ComputeContract.Version, "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [])));

    private static Task Send(WebSocket socket, ChannelReader<byte[]> queue, CancellationToken token)
        => (Task)Method("SendLoopAsync", isStatic: true).Invoke(null, [socket, queue, token])!;

    private static Task Drain(UpstreamLinkWorker worker, CancellationTokenSource session, CancellationTokenSource work,
        ChannelWriter<byte[]> queue, Task sending, bool notifyUpstream)
        => (Task)Method("BeginNodeDrain").Invoke(worker, [session, work, queue, sending, notifyUpstream, CancellationToken.None])!;

    private static Task Receive(UpstreamLinkWorker worker, WebSocket socket, ChannelWriter<byte[]> queue,
        CancellationTokenSource session, CancellationTokenSource work, Action beginDrain)
        => (Task)Method("ReceiveLoopAsync").Invoke(worker,
            [socket, queue, session, work, new ConcurrentDictionary<long, Task>(), beginDrain])!;

    private static MethodInfo Method(string name, bool isStatic = false)
        => typeof(UpstreamLinkWorker).GetMethod(name, BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance))!;

    /// <summary>控制最后一次发送何时完成，并按需投递一帧真实主服 drain。</summary>
    private sealed class ControlledSocket(byte[]? control) : WebSocket
    {
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Sent { get; } = [];
        private byte[]? _control = control;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            SendStarted.TrySetResult();
            await AllowSend.Task.WaitAsync(token);
            Sent.Add(buffer.ToArray());
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (_control is not { } frame)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            frame = _control!;
            _control = null;
            frame.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true);
        }
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            => SendAsync(buffer.AsMemory(), type, end, token).AsTask();
    }
}
