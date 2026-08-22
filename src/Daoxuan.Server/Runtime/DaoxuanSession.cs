using System.Threading.Channels;

namespace Daoxuan.Server.Runtime;

/// <summary>
/// 客户端 WebSocket 会话连接抽象 (DaoxuanSession)。
/// <para>
/// 职责：
/// 1. 为每个客户端 WebSocket 连接维护一个有界出站消息管道 (Channel&lt;byte[]&gt;)，隔离网络 IO 慢发送对 Fiber 线程的阻塞。
/// 2. 具备背压与丢包保护：当出站缓冲区满时丢弃 Delta 补丁并标记 NeedsResync，在下次广播时强制下发全量快照纠偏。
/// </para>
/// </summary>
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

    /// <summary>
    /// 创建绑定至指定玩家 ID 的新会话实例。
    /// </summary>
    public DaoxuanSession(Guid playerId)
    {
        PlayerId = playerId;
    }

    /// <summary>会话唯一标识符。</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>关联的玩家账户/角色 GUID。</summary>
    public Guid PlayerId { get; }

    /// <summary>出站消息管道的读取端（由 WebSocket 发送循环读取）。</summary>
    public ChannelReader<byte[]> Outbound => _outbound.Reader;

    /// <summary>当前会话是否因为缓冲区溢出而需要重新下发全量快照同步。</summary>
    public bool NeedsResync => Volatile.Read(ref _needsResync) != 0;

    /// <summary>
    /// 尝试将增量数据包写入下行管道；若缓冲区已满则放弃并置位 NeedsResync 标志。
    /// </summary>
    public bool TryEnqueue(byte[] packet)
    {
        if (_outbound.Writer.TryWrite(packet))
        {
            return true;
        }

        Volatile.Write(ref _needsResync, 1);
        return false;
    }

    /// <summary>
    /// 尝试将全量快照数据包写入下行管道，写入成功后自动清除 NeedsResync 标志。
    /// </summary>
    public bool TryEnqueueSnapshot(byte[] packet)
    {
        if (!_outbound.Writer.TryWrite(packet)) return false;
        Volatile.Write(ref _needsResync, 0);
        return true;
    }

    /// <summary>
    /// 关闭出站管道，通知读取端正常结束发送循环。
    /// </summary>
    public void Complete() => _outbound.Writer.TryComplete();
}
