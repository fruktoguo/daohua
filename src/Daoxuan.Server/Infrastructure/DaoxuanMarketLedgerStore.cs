using System.Text.Json;
using Daoxuan.Domain.Market;
using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

public sealed class DaoxuanMarketTopology
{
    public DaoxuanMarketTopology(int partitionId, int partitionCount)
    {
        if (partitionCount is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount),
                "市场主分片数量必须在 1 到 4096 之间。");
        }

        if (partitionId < 0 || partitionId >= partitionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionId),
                "市场主分片编号必须落在分片数量范围内。");
        }

        PartitionId = partitionId;
        PartitionCount = partitionCount;
    }

    public int PartitionId { get; }
    public int PartitionCount { get; }
    public string Algorithm => DaoxuanMarketPartitioner.Algorithm;

    public int GetOwner(DaoxuanMarketInstrument instrument) =>
        DaoxuanMarketPartitioner.GetPartition(instrument, PartitionCount);

    public bool Owns(DaoxuanMarketInstrument instrument) =>
        GetOwner(instrument) == PartitionId;
}

/// <summary>
/// 市场账本持久化边界。数据库实现必须把账本 CAS、玩家快照和 Outbox
/// 放入同一事务；本地实现只用于单实例开发与恢复验证。
/// </summary>
public interface IDaoxuanMarketLedgerStore
{
    string ProviderName { get; }
    bool SupportsDistributedPartitions { get; }
    bool PersistsPlayerSnapshotAtomically { get; }

    ValueTask AcquireWriterAsync(
        DaoxuanMarketTopology topology,
        string instanceId,
        CancellationToken cancellationToken = default);

    ValueTask<DaoxuanMarketLedgerState?> LoadAsync(
        DaoxuanMarketTopology topology,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        DaoxuanMarketTopology topology,
        long expectedRevision,
        DaoxuanMarketLedgerState ledger,
        YuoSnapshotRecord? playerSnapshot,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseWriterAsync(CancellationToken cancellationToken = default);
}

public sealed class DaoxuanMarketLedgerConcurrencyException : InvalidOperationException
{
    public DaoxuanMarketLedgerConcurrencyException(long expectedRevision, long actualRevision)
        : base($"市场账本版本冲突：期望 {expectedRevision}，实际 {actualRevision}。")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}

/// <summary>
/// 单机原子 JSON 账本。文件租约保护整个生命周期，revision CAS 防止调用方
/// 用旧内存状态覆盖新账本；它不声称提供跨文件玩家快照事务。
/// </summary>
public sealed class DaoxuanLocalMarketLedgerStore : IDaoxuanMarketLedgerStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    // 热重启交接窗口：前任实例优雅停服（保存最终账本并释放租约）通常在数秒内完成；
    // 超过该窗口仍被占用才判定为多实例分裂并拒绝启动。
    private static readonly TimeSpan LeaseHandoffWaitTimeout = TimeSpan.FromSeconds(30);
    private const int LeaseRetryDelayMilliseconds = 250;

    private readonly IYuoSnapshotStore _snapshotStore;
    private readonly ILogger<DaoxuanLocalMarketLedgerStore> _logger;
    private readonly string _ownershipLockPath;
    private FileStream? _ownershipLock;
    private long _persistedRevision;
    private bool _disposed;

    public DaoxuanLocalMarketLedgerStore(
        IYuoSnapshotStore snapshotStore,
        DaoxuanStoragePaths paths,
        ILogger<DaoxuanLocalMarketLedgerStore> logger)
    {
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(paths);
        var snapshotDirectory = paths.SnapshotDirectory;
        Directory.CreateDirectory(snapshotDirectory);
        _ownershipLockPath = Path.Combine(snapshotDirectory, "market-owner.lock");
    }

    public string ProviderName => "local-json";
    public bool SupportsDistributedPartitions => false;
    public bool PersistsPlayerSnapshotAtomically => false;

    public async ValueTask AcquireWriterAsync(
        DaoxuanMarketTopology topology,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        if (topology.PartitionCount != 1)
        {
            throw new InvalidOperationException(
                "local-json 市场账本只支持单实例单主分片；多实例必须使用支持事务和分布式租约的数据库存储。");
        }

        if (_ownershipLock is not null)
        {
            throw new InvalidOperationException("当前市场存储已经持有写者租约。");
        }

        // 开发热重启与受控更新时，前任实例可能仍在优雅停服中持有租约；
        // 在有限窗口内等待其释放属于正常交接，超时仍被占用才判定为多实例分裂。
        var deadline = DateTimeOffset.UtcNow + LeaseHandoffWaitTimeout;
        var waited = false;
        while (true)
        {
            try
            {
                _ownershipLock = new FileStream(
                    _ownershipLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 128,
                    options: FileOptions.Asynchronous);
                break;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                if (!waited)
                {
                    waited = true;
                    _logger.LogInformation(
                        "市场写者租约由其他实例持有，最多等待 {TimeoutSeconds}s 完成交接",
                        LeaseHandoffWaitTimeout.TotalSeconds);
                }

                await Task.Delay(LeaseRetryDelayMilliseconds, cancellationToken);
            }
        }

        try
        {
            _ownershipLock.SetLength(0);
            _ownershipLock.Position = 0;
            using var writer = new StreamWriter(_ownershipLock, leaveOpen: true);
            writer.Write(instanceId);
            writer.Flush();
            _ownershipLock.Flush(flushToDisk: true);
            _logger.LogInformation("市场写者租约已取得 {InstanceId}", instanceId);
            return;
        }
        catch (IOException exception)
        {
            ReleaseWriterCore();
            throw new InvalidOperationException(
                "市场账本目录已有其他实例持有写者租约；为避免多实例分裂，当前实例不会启动市场。",
                exception);
        }
    }

    public async ValueTask<DaoxuanMarketLedgerState?> LoadAsync(
        DaoxuanMarketTopology topology,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLeaseAndTopology(topology);
        var stored = await _snapshotStore.LoadAsync("market", "ledger", cancellationToken);
        if (stored is null || stored.Payload.Length == 0)
        {
            Interlocked.Exchange(ref _persistedRevision, 0);
            return null;
        }

        try
        {
            var ledger = JsonSerializer.Deserialize<DaoxuanMarketLedgerState>(stored.Payload.Span, JsonOptions)
                ?? throw new InvalidDataException("市场账本快照为空。");
            Interlocked.Exchange(ref _persistedRevision, ledger.Revision);
            return ledger;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("市场账本快照无法解析。", exception);
        }
    }

    public async ValueTask CommitAsync(
        DaoxuanMarketTopology topology,
        long expectedRevision,
        DaoxuanMarketLedgerState ledger,
        YuoSnapshotRecord? playerSnapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLeaseAndTopology(topology);
        ArgumentNullException.ThrowIfNull(ledger);
        _ = playerSnapshot;

        var actualRevision = Volatile.Read(ref _persistedRevision);
        if (actualRevision != expectedRevision)
        {
            throw new DaoxuanMarketLedgerConcurrencyException(expectedRevision, actualRevision);
        }

        if (ledger.Revision != checked(expectedRevision + 1))
        {
            throw new InvalidOperationException("新市场账本版本必须严格递增 1。");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
        await _snapshotStore.SaveAsync(
            new YuoSnapshotRecord("market", "ledger", payload, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _persistedRevision, ledger.Revision);
    }

    public ValueTask ReleaseWriterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseWriterCore();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        ReleaseWriterCore();
        return ValueTask.CompletedTask;
    }

    private void EnsureLeaseAndTopology(DaoxuanMarketTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (_ownershipLock is null)
        {
            throw new InvalidOperationException("市场写者租约尚未取得。");
        }

        if (topology.PartitionCount != 1 || topology.PartitionId != 0)
        {
            throw new InvalidOperationException("local-json 市场账本只支持主分片 0/1。");
        }
    }

    private void ReleaseWriterCore()
    {
        var lease = Interlocked.Exchange(ref _ownershipLock, null);
        if (lease is null) return;
        lease.Dispose();
        _logger.LogInformation("市场写者租约已释放");
    }
}
