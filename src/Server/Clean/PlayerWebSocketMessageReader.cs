namespace Server.Clean;

/// <summary>玩家上行消息组包。返回的内存仅在下一次读取或释放前有效。</summary>
internal sealed class PlayerWebSocketMessageReader(int maxMessageBytes) : IDisposable
{
    private const int InitialBufferBytes = 4 * 1024;
    private byte[]? _buffer;

    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        // 数组池可能给出更大的桶；逻辑上限仍是配置值。保留一个字节区分
        // 「恰好达上限后的空结束片」与「超过上限」。
        var limit = maxMessageBytes + 1;
        var initialSize = Math.Min(InitialBufferBytes, limit);
        if (_buffer is null || _buffer.Length > InitialBufferBytes)
        {
            Dispose();
            _buffer = ArrayPool<byte>.Shared.Rent(initialSize);
        }

        var written = 0;
        while (true)
        {
            var buffer = _buffer;
            var result = await socket.ReceiveAsync(buffer.AsMemory(written, Math.Min(buffer.Length, limit) - written), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) throw new WebSocketException("只接受二进制协议帧。");
            written += result.Count;
            if (written > maxMessageBytes) throw new WebSocketException("消息超过大小限制。");
            if (result.EndOfMessage) return buffer.AsMemory(0, written);
            if (written < buffer.Length) continue;

            var grown = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, limit));
            buffer.AsSpan(0, written).CopyTo(grown);
            _buffer = grown;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_buffer is not { } buffer) return;
        _buffer = null;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
