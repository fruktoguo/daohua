using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using DaoHua.ComputeNode;
using Domain.GameData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Server.Clean;
using Xunit;
using ServerLinkContract = Server.Clean.ComputeLinkContract;

namespace Server.Tests;

public sealed class ComputeLinkTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactLimitTextWithEmptyFinalFragmentIsDecodedAcrossBufferGrowth(bool serverSide)
    {
        const int limit = 32 * 1024;
        var prefix = Encoding.UTF8.GetBytes("{\"type\":\"draining\",\"reason\":\"完成\"}");
        var payload = new byte[limit];
        payload.AsSpan().Fill((byte)' ');
        prefix.CopyTo(payload, 0);
        // 将 UTF-8 字符拆到两次读取，并使用空结束分片验证准确上限。
        using var socket = new ScriptedSocket(payload, new[] { 30, limit - 30, 0 });
        var message = await ReceiveAsync(serverSide, socket, limit, CancellationToken.None);
        var reason = serverSide
            ? Assert.IsType<ComputeLinkDraining>(message).Reason
            : Assert.IsType<LinkDraining>(message).Reason;
        Assert.Equal("完成", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedTextIsRejectedBeforeReadingBeyondProbeByte(bool serverSide)
    {
        const int limit = 32 * 1024;
        using var socket = new ScriptedSocket(new byte[limit + 128], new[] { limit, 128 });
        await Assert.ThrowsAsync<InvalidDataException>(() => ReceiveAsync(serverSide, socket, limit, CancellationToken.None));
        Assert.Equal(limit + 1, socket.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingAFragmentedReceiveEndsTheSocketRead(bool serverSide)
    {
        using var deadline = new CancellationTokenSource();
        using var socket = new ScriptedSocket("{"u8.ToArray(), new[] { 1, 1 });
        var pending = ReceiveAsync(serverSide, socket, 32 * 1024, deadline.Token);
        Assert.False(pending.IsCompleted);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ServerRejectsOversizedHelloAndDoesNotWaitForCloseAcknowledgment()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(GameDataCatalog.LoadEmbedded())
            .AddSingleton<ComputeLinkRegistry>()
            .AddSingleton(ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:ConnectionString"] = "unused",
                    ["ComputeNode:Token"] = "unit-test"
                }).Build()))
            .BuildServiceProvider();
        var hello = ComputeLinkCodec.Encode(new ComputeLinkHello(ServerLinkContract.Version, "node-test", 1,
            services.GetRequiredService<GameDataCatalog>().Fingerprint));
        Array.Resize(ref hello, 16 * 1024 + 1);
        hello.AsSpan(hello.AsSpan().IndexOf((byte)0)).Fill((byte)' ');
        using var socket = new ScriptedSocket(hello, new[] { hello.Length });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers[ServerLinkContract.TokenHeader] = "unit-test";
        context.Features.Set<IHttpWebSocketFeature>(new AcceptSocket(socket));

        await ComputeLinkEndpoint.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, services.GetRequiredService<ComputeLinkRegistry>().NodeCount);
        Assert.Equal("LINK_ERROR", Assert.IsType<ComputeLinkError>(ComputeLinkCodec.Decode(Assert.Single(socket.Sent))).Code);
        Assert.True(socket.CloseOutputSent);
    }

    [Fact]
    public async Task TimedOutQueueWriteCannotBeSentAfterCapacityReturns()
    {
        var queue = Channel.CreateBounded<byte[]>(1);
        byte[] first = [1];
        byte[] stale = [2];
        byte[] next = [3];
        Assert.True(queue.Writer.TryWrite(first));

        await Assert.ThrowsAsync<TimeoutException>(() => ComputeDispatcherWorker.WriteBatchFrameAsync(
            queue.Writer, stale, TimeSpan.FromMilliseconds(20), CancellationToken.None).AsTask());

        Assert.Same(first, await queue.Reader.ReadAsync());
        Assert.False(queue.Reader.TryRead(out _));
        await ComputeDispatcherWorker.WriteBatchFrameAsync(queue.Writer, next, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Same(next, await queue.Reader.ReadAsync());
    }

    private static Task<object> ReceiveAsync(bool serverSide, WebSocket socket, int maxBytes, CancellationToken token)
    {
        if (serverSide) return ComputeLinkEndpoint.ReceiveMessageAsync(socket, maxBytes, token);
        var receive = typeof(UpstreamLinkWorker).GetMethod("ReceiveMessageAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task<object>)receive.Invoke(null, [socket, maxBytes, token])!;
    }

    private sealed class AcceptSocket(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class ScriptedSocket(byte[] payload, int[] fragmentLengths) : WebSocket
    {
        private int _fragment;
        private int _fragmentRead;
        public int BytesRead { get; private set; }
        public List<byte[]> Sent { get; } = [];
        public bool CloseOutputSent { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (BytesRead == payload.Length && fragmentLengths[_fragment] != _fragmentRead)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var count = Math.Min(buffer.Length, fragmentLengths[_fragment] - _fragmentRead);
            payload.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            _fragmentRead += count;
            if (_fragmentRead == fragmentLengths[_fragment])
            {
                _fragment++;
                _fragmentRead = 0;
            }
            return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, _fragment == fragmentLengths.Length);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseOutputSent = true;
            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public override void Abort() { }
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
