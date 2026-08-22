using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

/// <summary>
/// Daoxuan 的 PostgreSQL 持久化装配边界。默认写入内存队列，开启 durable outbox 后先进入数据库 outbox，
/// 后台写回成功后按序 ack，重启会 replay 未确认命令。
/// </summary>
public interface IDaoxuanPersistenceQueue
{
    ValueTask EnqueueAsync(YuoPersistenceCommand command, CancellationToken cancellationToken = default);

    ValueTask EnqueueBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default);

    YuoPersistenceQueueStats GetStats();

    void Complete();
}

public sealed class DaoxuanPersistenceDispatcher : IDaoxuanPersistenceQueue
{
    private readonly YuoPersistenceQueue _queue;
    private readonly YuoDurablePersistenceQueue? _durableQueue;

    public DaoxuanPersistenceDispatcher(
        YuoPersistenceQueue queue,
        IServiceProvider services)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(services);
        _durableQueue = services.GetService<YuoDurablePersistenceQueue>();
    }

    public ValueTask EnqueueAsync(
        YuoPersistenceCommand command,
        CancellationToken cancellationToken = default)
    {
        return _durableQueue is null
            ? _queue.EnqueueAsync(command, cancellationToken)
            : _durableQueue.EnqueueAsync(command, cancellationToken);
    }

    public async ValueTask EnqueueBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (var command in commands)
        {
            await EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public YuoPersistenceQueueStats GetStats() => _queue.GetStats();

    public void Complete() => _queue.TryComplete();
}

/// <summary>
/// durable outbox 必须在通用后台写回服务之前 replay，避免启动窗口丢失未确认快照。
/// </summary>
public sealed class DaoxuanPersistenceReplayHostedService(
    YuoDurablePersistenceQueue queue,
    ILogger<DaoxuanPersistenceReplayHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var replayed = await queue.ReplayPendingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (replayed > 0)
        {
            logger.LogInformation("已重放 {Count} 条待确认持久化命令", replayed);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
