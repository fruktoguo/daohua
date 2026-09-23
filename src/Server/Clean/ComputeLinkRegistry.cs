namespace Server.Clean;

/// <summary>节点断线时在飞批次统一收到的失败信号。</summary>
public sealed class ComputeNodeDisconnectedException(string nodeName)
    : InvalidOperationException($"计算节点 {nodeName} 已断开。");

/// <summary>
/// 一个已注册计算节点的服务端视图：发送队列、声明容量与在飞批次跟踪。
/// 在飞批次以 batchId → TaskCompletionSource 登记，结果帧到达或链路断开时统一了结。
/// </summary>
public sealed class ComputeLinkNode(
    string name,
    int capacity,
    int outboundCapacity = 64)
{
    /// <summary>服务端分配的节点实例标识（每次拨入唯一）。</summary>
    public Guid NodeId { get; } = Guid.NewGuid();
    /// <summary>节点自报名称（仅用于日志与诊断）。</summary>
    public string Name { get; } = name;
    /// <summary>节点声明的最大并发批次数。</summary>
    public int Capacity { get; } = capacity;
    /// <summary>待发送帧队列：派发器投递，端点发送循环消费。</summary>
    public Channel<byte[]> Outbound { get; } = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(outboundCapacity) { SingleReader = true });
    /// <summary>在飞批次：batchId → 结果等待源。</summary>
    public ConcurrentDictionary<Guid, TaskCompletionSource<ComputeAdvanceBatchResponse>> Pending { get; } = new();
    /// <summary>当前在飞批次数。</summary>
    public int InFlight => Pending.Count;
    /// <summary>剩余可承接批次数。</summary>
    public int Available => Math.Max(0, Capacity - InFlight);

    /// <summary>把一个批次结果投递给等待方；batchId 未知（超时已清理等）时返回 false。</summary>
    public bool CompleteResult(Guid batchId, ComputeAdvanceBatchResponse response)
        => Pending.TryRemove(batchId, out var waiter) && waiter.TrySetResult(response);

    /// <summary>链路关闭时让所有在飞批次立即失败，派发器按租约语义退回。</summary>
    public void FailAllPending()
    {
        foreach (var (batchId, waiter) in Pending)
        {
            if (Pending.TryRemove(batchId, out _)) waiter.TrySetException(new ComputeNodeDisconnectedException(Name));
        }
        Outbound.Writer.TryComplete();
    }
}

/// <summary>
/// 在线计算节点注册表：拨入即注册、断开即注销，派发器按剩余容量挑节点。
/// 节点组完全为空时 Acquire 返回 null，任务自然留 pending，不消耗领取次数。
/// </summary>
public sealed class ComputeLinkRegistry
{
    private readonly ConcurrentDictionary<Guid, ComputeLinkNode> _nodes = new();

    /// <summary>当前在线节点数。</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>当前在线节点快照（诊断用）。</summary>
    public IReadOnlyCollection<ComputeLinkNode> Nodes => _nodes.Values.ToArray();

    /// <summary>注册一个已完成握手校验的节点；超过在线上限时拒绝。</summary>
    public bool TryRegister(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.Count >= ComputeLinkContract.MaxConnectedNodes) return false;
        return _nodes.TryAdd(node.NodeId, node);
    }

    /// <summary>注销节点并让其全部在飞批次失败。</summary>
    public void Unregister(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.TryRemove(node.NodeId, out _)) node.FailAllPending();
    }

    /// <summary>
    /// 取一个剩余容量最大的在线节点；无可用节点返回 null。
    /// 总是派给最闲节点本身就是负载均衡：节点越忙 Available 越小，自动让位给其他节点。
    /// </summary>
    public ComputeLinkNode? Acquire()
    {
        ComputeLinkNode? best = null;
        foreach (var node in _nodes.Values)
            if (node.Available > 0 && (best is null || node.Available > best.Available))
                best = node;
        return best;
    }
}
