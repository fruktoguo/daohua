namespace Server.Clean;

/// <summary>节点断线时在飞批次统一收到的失败信号。</summary>
public sealed class ComputeNodeDisconnectedException(string nodeName)
    : InvalidOperationException($"计算节点 {nodeName} 已断开。");

/// <summary>
/// 一个已注册计算节点的服务端视图：发送队列、声明能力、批次槽位与在飞批次跟踪。
/// 派发并行化后同一节点可同时承接 Capacity 个批次：槽位经 <see cref="TryReserveSlot"/>
/// 原子预留（Acquire 即占槽），派发任务结束时经 <see cref="ReleaseSlot"/> 归还；
/// 在飞等待方以 batchId → TaskCompletionSource 登记，结果帧到达或链路断开时统一了结。
/// </summary>
public sealed class ComputeLinkNode(
    string name,
    int capacity,
    int maxJobsPerBatch = 0,
    long maxBatchBytes = 0,
    int outboundCapacity = 64,
    string? runtimeFingerprint = null)
{
    private int _inFlightSlots;

    /// <summary>服务端分配的节点实例标识（每次拨入唯一）。</summary>
    public Guid NodeId { get; } = Guid.NewGuid();
    /// <summary>节点自报名称（仅用于日志与诊断）。</summary>
    public string Name { get; } = name;
    /// <summary>节点声明的最大并发批次数。</summary>
    public int Capacity { get; } = capacity;
    /// <summary>节点声明的单批最大任务数；0 表示未声明，由主服配置兜底。</summary>
    public int MaxJobsPerBatch { get; } = maxJobsPerBatch;
    /// <summary>节点声明的单批最大解码后字节数；0 表示未声明，由主服配置兜底。</summary>
    public long MaxBatchBytes { get; } = maxBatchBytes;
    /// <summary>节点 hello 自报的运行时指纹（进程架构/运行时描述，仅诊断展示，不参与裁决）。</summary>
    public string? RuntimeFingerprint { get; } = runtimeFingerprint;
    /// <summary>待发送帧队列：派发器投递，端点发送循环消费。</summary>
    public Channel<byte[]> Outbound { get; } = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(outboundCapacity) { SingleReader = true });
    /// <summary>在飞批次等待方：batchId → 结果等待源。</summary>
    public ConcurrentDictionary<Guid, TaskCompletionSource<ComputeAdvanceBatchResponse>> Pending { get; } = new();
    /// <summary>当前已预留批次槽位数（含已占槽未投递的）。</summary>
    public int InFlight => Volatile.Read(ref _inFlightSlots);
    /// <summary>剩余可承接批次数。</summary>
    public int Available => Math.Max(0, Capacity - InFlight);
    /// <summary>槽位归还回调：由注册表在节点上线时挂接，用于唤醒派发循环。</summary>
    internal Action? SlotReleased { get; set; }

    /// <summary>原子预留一个批次槽位；容量已满返回 false。谁预留谁归还，与 Pending 登记解耦。</summary>
    public bool TryReserveSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _inFlightSlots);
            if (current >= Capacity) return false;
            if (Interlocked.CompareExchange(ref _inFlightSlots, current + 1, current) == current)
                return true;
        }
    }

    /// <summary>批次了结后归还槽位（无论成败），并唤醒可能正在等容量的派发循环。</summary>
    public void ReleaseSlot()
    {
        Interlocked.Decrement(ref _inFlightSlots);
        SlotReleased?.Invoke();
    }

    /// <summary>把一个批次结果投递给等待方；batchId 未知（超时已清理等）时返回 false。</summary>
    public bool CompleteResult(Guid batchId, ComputeAdvanceBatchResponse response)
        => Pending.TryRemove(batchId, out var waiter) && waiter.TrySetResult(response);

    /// <summary>
    /// 链路关闭时让所有在飞批次等待方立即失败；槽位不由这里归还——
    /// 各派发任务观察到等待方异常后在自身收尾路径统一 ReleaseSlot。
    /// </summary>
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
/// 在线计算节点注册表：拨入即注册、断开即注销，派发器按剩余容量挑节点并原子占槽。
/// 节点组完全为空或全忙时 Acquire 返回 null，任务自然留 pending，不消耗领取次数；
/// 派发循环可等待 <see cref="CapacitySignal"/> 在容量归还/新节点注册时被即时唤醒。
/// </summary>
public sealed class ComputeLinkRegistry
{
    private readonly ConcurrentDictionary<Guid, ComputeLinkNode> _nodes = new();
    private readonly object _signalGate = new();
    private TaskCompletionSource _capacitySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>当前在线节点数。</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>当前在线节点快照（诊断与批次预算协商用）。</summary>
    public IReadOnlyCollection<ComputeLinkNode> Nodes => _nodes.Values.ToArray();

    /// <summary>下一次容量变化（节点注册或槽位归还）的唤醒信号；信号是一次性的，等待方需重新取值。</summary>
    public Task CapacitySignal
    {
        get { lock (_signalGate) return _capacitySignal.Task; }
    }

    /// <summary>容量变化时唤醒等待中的派发循环。</summary>
    public void SignalCapacity()
    {
        lock (_signalGate)
        {
            if (_capacitySignal.TrySetResult())
                _capacitySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>注册一个已完成握手校验的节点；超过在线上限时拒绝。</summary>
    public bool TryRegister(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.Count >= ComputeLinkContract.MaxConnectedNodes) return false;
        if (!_nodes.TryAdd(node.NodeId, node)) return false;
        node.SlotReleased = SignalCapacity;
        SignalCapacity();
        return true;
    }

    /// <summary>注销节点并让其全部在飞批次失败。</summary>
    public void Unregister(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.TryRemove(node.NodeId, out _)) node.FailAllPending();
    }

    /// <summary>
    /// 取一个剩余容量最大的在线节点并原子占一个批次槽位；无可用节点返回 null。
    /// 占槽与挑选同一体完成：并行派发下不会因「挑完还没登记」而向同一节点超派。
    /// 总是派给最闲节点本身就是负载均衡：节点越忙 Available 越小，自动让位给其他节点。
    /// 调用方获得节点后必须在批次了结路径调用 <see cref="ComputeLinkNode.ReleaseSlot"/>。
    /// </summary>
    public ComputeLinkNode? Acquire()
    {
        while (true)
        {
            ComputeLinkNode? best = null;
            foreach (var node in _nodes.Values)
                if (node.Available > 0 && (best is null || node.Available > best.Available))
                    best = node;
            if (best is null) return null;
            if (best.TryReserveSlot())
            {
                // 占槽瞬间节点可能已被注销：归还槽位后重扫，避免把批次投给死节点。
                if (_nodes.ContainsKey(best.NodeId)) return best;
                best.ReleaseSlot();
            }
        }
    }
}
