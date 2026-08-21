using System.Threading.Channels;

namespace Daoxuan.Server.Runtime;

public sealed class DaoxuanSession
{
    private int _needsResync;
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public DaoxuanSession(Guid playerId)
    {
        PlayerId = playerId;
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid PlayerId { get; }
    public ChannelReader<byte[]> Outbound => _outbound.Reader;
    public bool NeedsResync => Volatile.Read(ref _needsResync) != 0;

    public bool TryEnqueue(byte[] packet)
    {
        if (_outbound.Writer.TryWrite(packet))
        {
            return true;
        }

        Volatile.Write(ref _needsResync, 1);
        return false;
    }

    public bool TryEnqueueSnapshot(byte[] packet)
    {
        if (!_outbound.Writer.TryWrite(packet)) return false;
        Volatile.Write(ref _needsResync, 0);
        return true;
    }

    public void Complete() => _outbound.Writer.TryComplete();
}
