using Xiuxian.Domain.Market;
using Xiuxian.Server.Infrastructure;

namespace Xiuxian.Server.Runtime;

/// <summary>
/// 市场领域服务及其账本生命周期。
/// 订单簿的撮合仍在内存中完成，账本快照只在写操作和停服阶段落盘，读盘口不会触碰磁盘。
/// </summary>
public sealed class XiuxianMarketRuntime : IHostedService, IAsyncDisposable
{
    private const int LedgerSchemaVersion = 1;
    private readonly IXiuxianMarketLedgerStore _ledgerStore;
    private readonly XiuxianMarketTopology _topology;
    private readonly ILogger<XiuxianMarketRuntime> _logger;
    private readonly SemaphoreSlim _marketWriteGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private long _revision;
    private bool _started;
    private bool _disposed;

    public XiuxianMarketRuntime(
        XiuxianMarketEngine engine,
        XiuxianMarketCoordinator coordinator,
        IXiuxianMarketLedgerStore ledgerStore,
        XiuxianMarketTopology topology,
        ILogger<XiuxianMarketRuntime> logger)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public XiuxianMarketEngine Engine { get; }
    public XiuxianMarketCoordinator Coordinator { get; }
    public bool IsReady => Volatile.Read(ref _started);
    public long Revision => Volatile.Read(ref _revision);
    public string InstanceId => _instanceId;
    public int PartitionId => _topology.PartitionId;
    public int PartitionCount => _topology.PartitionCount;
    public string PartitionAlgorithm => _topology.Algorithm;
    public string StorageProvider => _ledgerStore.ProviderName;
    public bool PersistsPlayerSnapshotAtomically => _ledgerStore.PersistsPlayerSnapshotAtomically;

    public int GetOwnerPartition(XiuxianMarketInstrument instrument) => _topology.GetOwner(instrument);
    public bool Owns(XiuxianMarketInstrument instrument) => _topology.Owns(instrument);

    public async ValueTask<XiuxianMarketWriteLease> AcquireWriteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _marketWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new XiuxianMarketWriteLease(_marketWriteGate);
    }

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

    internal async ValueTask<long> PersistUnderWriteLockAsync(
        YuoCore.Persistence.YuoSnapshotRecord? playerSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = checked(_revision + 1);
            var ledger = new XiuxianMarketLedgerState(
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

public readonly struct XiuxianMarketWriteLease : IDisposable
{
    private readonly SemaphoreSlim? _gate;

    internal XiuxianMarketWriteLease(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public void Dispose()
    {
        _gate?.Release();
    }
}
