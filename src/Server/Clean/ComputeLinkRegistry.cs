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
    private int _draining;
    private long _batchesSent;
    private long _batchesCompleted;
    private long _batchesFailed;
    private long _jobsApplied;
    private long _jobsFailed;
    private long _roundTripTicksTotal;
    private long _lastRoundTripMs;
    private long _maxRoundTripMs;
    private long _bytesSent;

    /// <summary>服务端分配的节点实例标识（每次拨入唯一）。</summary>
    public Guid NodeId { get; } = Guid.NewGuid();
    /// <summary>本次拨入的接入时刻（UTC）：运维视图的拨入时长由此计算。</summary>
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
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
    /// <summary>节点是否已进入排空：停止承接新批次，在飞结果照常回收。</summary>
    public bool Draining => Volatile.Read(ref _draining) != 0;
    /// <summary>剩余可承接批次数；排空中的节点对外表现为零可用。</summary>
    public int Available => Draining ? 0 : Math.Max(0, Capacity - InFlight);
    /// <summary>槽位归还回调：由注册表在节点上线时挂接，用于唤醒派发循环。</summary>
    internal Action? SlotReleased { get; set; }

    /// <summary>出站队列当前积压帧数；通道不支持计数时返回 -1。</summary>
    public int OutboundDepth => Outbound.Reader.CanCount ? Outbound.Reader.Count : -1;
    /// <summary>累计写入出站队列的批次数。</summary>
    public long BatchesSent => Interlocked.Read(ref _batchesSent);
    /// <summary>累计拿到合法响应的批次数。</summary>
    public long BatchesCompleted => Interlocked.Read(ref _batchesCompleted);
    /// <summary>累计失败批次数（超时/断线/契约/写失败）。</summary>
    public long BatchesFailed => Interlocked.Read(ref _batchesFailed);
    /// <summary>累计落库 Applied 的任务数。</summary>
    public long JobsApplied => Interlocked.Read(ref _jobsApplied);
    /// <summary>累计判失败的任务数（节点报错/缺结果/产出污染判死）。</summary>
    public long JobsFailed => Interlocked.Read(ref _jobsFailed);
    /// <summary>最近一次批次往返毫秒。</summary>
    public double LastRoundTripMs => Interlocked.Read(ref _lastRoundTripMs);
    /// <summary>批次往返均值毫秒；无完成批次为 0。</summary>
    public double AvgRoundTripMs
    {
        get
        {
            var completed = BatchesCompleted;
            return completed == 0 ? 0 : Interlocked.Read(ref _roundTripTicksTotal) / (double)completed / TimeSpan.TicksPerMillisecond;
        }
    }
    /// <summary>批次往返最大毫秒。</summary>
    public double MaxRoundTripMs => Interlocked.Read(ref _maxRoundTripMs);
    /// <summary>累计发送批次帧字节数。</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>批次帧写入出站队列时打点（派发器调用）。</summary>
    internal void RecordBatchSent(int jobs, long frameBytes)
    {
        Interlocked.Increment(ref _batchesSent);
        Interlocked.Add(ref _bytesSent, frameBytes);
    }

    /// <summary>批次拿到合法响应时打点：累计往返时延、最近值与最大值。</summary>
    internal void RecordBatchCompleted(TimeSpan roundTrip)
    {
        Interlocked.Increment(ref _batchesCompleted);
        Interlocked.Add(ref _roundTripTicksTotal, roundTrip.Ticks);
        var ms = (long)Math.Ceiling(roundTrip.TotalMilliseconds);
        Interlocked.Exchange(ref _lastRoundTripMs, ms);
        while (true)
        {
            var current = Interlocked.Read(ref _maxRoundTripMs);
            if (ms <= current || Interlocked.CompareExchange(ref _maxRoundTripMs, ms, current) == current) break;
        }
    }

    /// <summary>批次失败（未拿到合法响应）时打点。</summary>
    internal void RecordBatchFailed() => Interlocked.Increment(ref _batchesFailed);
    /// <summary>任务结果落库 Applied 时打点。</summary>
    internal void RecordJobApplied() => Interlocked.Increment(ref _jobsApplied);
    /// <summary>任务判失败（节点报错/缺结果/产出污染）时打点。</summary>
    internal void RecordJobFailed() => Interlocked.Increment(ref _jobsFailed);

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

    /// <summary>标记节点进入排空：Acquire 不再向其预留槽位，幂等。</summary>
    public void BeginDrain() => Interlocked.Exchange(ref _draining, 1);

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
public sealed class ComputeLinkRegistry(ComputeLinkMetrics? metrics = null)
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
        metrics?.RecordNodeConnected();
        SignalCapacity();
        return true;
    }

    /// <summary>注销节点并让其全部在飞批次失败。</summary>
    public void Unregister(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryRemove(node.NodeId, out _)) return;
        metrics?.RecordNodeDisconnected();
        node.FailAllPending();
    }

    /// <summary>节点上报排空：标记停派并唤醒派发循环重选其他节点。</summary>
    public void BeginDrain(ComputeLinkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.BeginDrain();
        SignalCapacity();
    }

    /// <summary>
    /// 主服停机/滚动升级排空：全部在线节点标记停派，并向各节点出站队列投递 drain 帧。
    /// 队列满或已完结时提示帧可被放弃——drain 是提示语义，底层正确性由任务租约兜底。
    /// </summary>
    public void DrainAll(string? reason = null)
    {
        var frame = ComputeLinkCodec.Encode(new ComputeLinkDrain(reason));
        foreach (var node in _nodes.Values)
        {
            node.BeginDrain();
            node.Outbound.Writer.TryWrite(frame);
        }
        SignalCapacity();
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
