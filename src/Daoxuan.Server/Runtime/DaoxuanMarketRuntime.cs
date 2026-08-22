using Daoxuan.Domain.Market;
using Daoxuan.Server.Infrastructure;

namespace Daoxuan.Server.Runtime;

/// <summary>
/// 市场领域服务及其账本持久化运行时 (DaoxuanMarketRuntime)。
/// <para>
/// 职责与并发架构：
/// 1. 撮合与挂单全程在内存中完成，读盘口与查深度纯内存无 I/O，保证极低延迟。
/// 2. 独占写者租约：启动时向存储层获取主分片写者租约 (<see cref="IDaoxuanMarketLedgerStore.AcquireWriterAsync"/>)，防止多实例写冲突。
/// 3. 快照与状态恢复：启动时从数据库加载最新账本恢复订单簿与暂存资产；每次发生挂单、撤单、领奖或停机时原子写回账本（支持与玩家快照同事务原子提交）。
/// 4. 读写互斥锁：通过 <see cref="DaoxuanMarketWriteLease"/> 保证撮合与落盘串行。
/// </para>
/// </summary>
public sealed class DaoxuanMarketRuntime : IHostedService, IAsyncDisposable
{
    private const int LedgerSchemaVersion = 1;
    private readonly IDaoxuanMarketLedgerStore _ledgerStore;
    private readonly DaoxuanMarketTopology _topology;
    private readonly ILogger<DaoxuanMarketRuntime> _logger;
    private readonly SemaphoreSlim _marketWriteGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private long _revision;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// 初始化市场运行时。
    /// </summary>
    public DaoxuanMarketRuntime(
        DaoxuanMarketEngine engine,
        DaoxuanMarketCoordinator coordinator,
        IDaoxuanMarketLedgerStore ledgerStore,
        DaoxuanMarketTopology topology,
        ILogger<DaoxuanMarketRuntime> logger)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>市场订单簿撮合引擎。</summary>
    public DaoxuanMarketEngine Engine { get; }

    /// <summary>市场结算与暂存资产协调器。</summary>
    public DaoxuanMarketCoordinator Coordinator { get; }

    /// <summary>市场账本是否已完成恢复并处于就绪状态。</summary>
    public bool IsReady => Volatile.Read(ref _started);

    /// <summary>当前市场账本提交版本号。</summary>
    public long Revision => Volatile.Read(ref _revision);

    /// <summary>当前服务实例唯一标识。</summary>
    public string InstanceId => _instanceId;

    /// <summary>当前实例负责的市场主分片 ID。</summary>
    public int PartitionId => _topology.PartitionId;

    /// <summary>市场主分片总数。</summary>
    public int PartitionCount => _topology.PartitionCount;

    /// <summary>分片路由哈希算法。</summary>
    public string PartitionAlgorithm => _topology.Algorithm;

    /// <summary>市场账本底层存储实现名。</summary>
    public string StorageProvider => _ledgerStore.ProviderName;

    /// <summary>底层存储是否支持玩家快照与市场账本原子提交。</summary>
    public bool PersistsPlayerSnapshotAtomically => _ledgerStore.PersistsPlayerSnapshotAtomically;

    /// <summary>底层存储是否支持多主分片分布式扩展。</summary>
    public bool SupportsDistributedPartitions => _ledgerStore.SupportsDistributedPartitions;

    /// <summary>
    /// 获取负责指定交易品种的市场分片 ID。
    /// </summary>
    public int GetOwnerPartition(DaoxuanMarketInstrument instrument) => _topology.GetOwner(instrument);

    /// <summary>
    /// 检查指定交易品种是否由当前主分片负责。
    /// </summary>
    public bool Owns(DaoxuanMarketInstrument instrument) => _topology.Owns(instrument);

    /// <summary>
    /// 获取市场写互斥租约（用于挂单、撤单、提取或持久化）。
    /// </summary>
    public async ValueTask<DaoxuanMarketWriteLease> AcquireWriteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _marketWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DaoxuanMarketWriteLease(_marketWriteGate);
    }

    /// <summary>
    /// 托管服务启动：抢占分片写者租约，并从存储中恢复市场历史账本。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        await _ledgerStore.AcquireWriterAsync(_topology, _instanceId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _ledgerStore.ReleaseWriterAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {

        var ledger = await _ledgerStore.LoadAsync(_topology, cancellationToken).ConfigureAwait(false);
        if (ledger is not null)
        {
            if (ledger.SchemaVersion != LedgerSchemaVersion
                || ledger.Revision < 0
                || ledger.Engine is null
                || ledger.Coordinator is null
                || ledger.Engine.Books.Any(book => !_topology.Owns(book.Instrument)))
            {
                throw new InvalidDataException("市场账本版本不受当前服务支持。");
            }

            Engine.RestoreState(ledger.Engine);
            Coordinator.RestoreState(ledger.Coordinator);
            Interlocked.Exchange(ref _revision, ledger.Revision);
            _logger.LogInformation(
                "市场账本已恢复，版本 {Revision}，活动订单 {ActiveOrders}",
                ledger.Revision,
                ledger.Engine.Books.Sum(book => book.ActiveOrders.Count));
        }
        else
        {
            _logger.LogInformation("未找到市场账本，使用空订单簿启动。");
        }

        Volatile.Write(ref _started, true);
    }

    /// <summary>
    /// 托管服务停止：将最终内存账本刷盘入库，并释放写者租约。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started || _disposed) return;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation("开始保存最终市场账本");
        // 停服时再写一份完整账本，确保没有仅存在于内存的撮合结果。
        try
        {
            // 宿主的停服令牌可能在默认超时后取消，但账本写入不能因为客户端请求
            // 或宿主取消而半途退出；文件快照本身是原子替换，完成后再释放写者租约。
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("市场停服令牌已取消，继续完成最终账本快照");
            }

            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _started, false);
            try
            {
                await _ledgerStore.ReleaseWriterAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "释放市场写者租约失败");
            }
            _logger.LogInformation(
                "最终市场账本已保存，耗时 {ElapsedMilliseconds:F1}ms",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    /// <summary>
    /// 在写锁保护下提交账本快照并自增版本号。
    /// </summary>
    public async ValueTask<long> PersistAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var writeLease = await AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        return await PersistUnderWriteLockAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 低频回收过期挂牌。只有实际发生过期时才提交新账本版本，空扫描不产生磁盘写入。
    /// </summary>
    public async ValueTask<int> ExpireOrdersAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!IsReady) return 0;
        using var writeLease = await AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        if (!IsReady) return 0;

        var expiredCount = Coordinator.Expire(now);
        if (expiredCount == 0) return 0;

        // 领域状态已经发生变化，不能再使用可能随请求或停服取消的令牌。
        await PersistUnderWriteLockAsync(null, CancellationToken.None).ConfigureAwait(false);
        return expiredCount;
    }

    /// <summary>
    /// 在已持有写互斥锁的环境下，导出撮合引擎与协调器内存状态，递增修订号并提交至底层存储。
    /// 可选地携带玩家快照一同进行事务性原子写入。
    /// </summary>
    internal async ValueTask<long> PersistUnderWriteLockAsync(
        YuoCore.Persistence.YuoSnapshotRecord? playerSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = checked(_revision + 1);
            var ledger = new DaoxuanMarketLedgerState(
                LedgerSchemaVersion,
                revision,
                Engine.ExportState(),
                Coordinator.ExportState());
            if (ledger.Engine.Books.Any(book => !_topology.Owns(book.Instrument)))
            {
                throw new InvalidOperationException("当前市场内存状态包含不属于本实例主分片的订单簿。");
            }

            await _ledgerStore.CommitAsync(
                _topology,
                _revision,
                ledger,
                playerSnapshot,
                cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _revision, revision);
            return revision;
        }
        finally
        {
            _persistGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _ledgerStore.ReleaseWriterAsync(CancellationToken.None).ConfigureAwait(false);
        _marketWriteGate.Dispose();
        _persistGate.Dispose();
    }
}

/// <summary>
/// 市场写互斥租约句柄（RAII 模式：Dispose 时自动释放 SemaphoreSlim）。
/// </summary>
public readonly struct DaoxuanMarketWriteLease : IDisposable
{
    private readonly SemaphoreSlim? _gate;

    internal DaoxuanMarketWriteLease(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public void Dispose()
    {
        _gate?.Release();
    }
}
