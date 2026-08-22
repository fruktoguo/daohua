// ==============================================================================
// 文件名称: DaoHuaPersistenceDispatcher.cs
// 主要职责: 提供快照持久化命令的统一队列分发与 Durable Outbox 路由调度。
// 核心逻辑 / 定义内容:
//   1. 持久化队列调度契约抽象 (IDaoHuaPersistenceQueue, IDaoHuaBatchPersistenceOutbox)。
//   2. 依据配置将持久化命令路由至内存有界队列 (YuoPersistenceQueue) 或先写入 Outbox 表。
//   3. 维护持久化队列监控指标与停服排空完成标记。
// ==============================================================================

using YuoCore.Persistence;

namespace DaoHua.Server.Infrastructure;

/// <summary>
/// 持久化命令队列分发契约 (IDaoHuaPersistenceQueue)。
/// <para>
/// 职责：
/// 1. 统一封装快照写入等异步持久化命令的入队操作。
/// 2. 向上层屏蔽底层使用的是纯内存有界队列还是持久化出件箱 (Durable Outbox)。
/// </para>
/// </summary>
public interface IDaoHuaPersistenceQueue
{
    /// <summary>异步将单条持久化命令加入调度队列。</summary>
    ValueTask EnqueueAsync(YuoPersistenceCommand command, CancellationToken cancellationToken = default);

    /// <summary>异步批量将多条持久化命令加入调度队列。</summary>
    ValueTask EnqueueBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default);

    /// <summary>获取当前持久化队列监控统计指标（已入队、已处理、失败与丢弃计数）。</summary>
    YuoPersistenceQueueStats GetStats();

    /// <summary>标记队列输入完成，通知后台工作线程排空剩余命令。</summary>
    void Complete();
}

public interface IDaoHuaBatchPersistenceOutbox : IYuoPersistenceOutbox
{
    ValueTask<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化命令分发器 (DaoHuaPersistenceDispatcher)。
/// <para>
/// 当配置启用 <c>Storage:DurableOutboxEnabled</c> 时，将命令路由至 <see cref="YuoDurablePersistenceQueue"/> 先写 PostgreSQL Outbox 表防丢；
/// 未启用时直接写入内存高性能有界队列 <see cref="YuoPersistenceQueue"/>。
/// </para>
/// </summary>
public sealed class DaoHuaPersistenceDispatcher : IDaoHuaPersistenceQueue
{
    private readonly YuoPersistenceQueue _queue;
    private readonly YuoDurablePersistenceQueue? _durableQueue;
    private readonly IDaoHuaBatchPersistenceOutbox? _batchOutbox;

    public DaoHuaPersistenceDispatcher(
        YuoPersistenceQueue queue,
        IServiceProvider services)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(services);
        _durableQueue = services.GetService<YuoDurablePersistenceQueue>();
        _batchOutbox = services.GetService<IYuoPersistenceOutbox>() as IDaoHuaBatchPersistenceOutbox;
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
        if (commands.Count == 0) return;
        if (_batchOutbox is not null)
        {
            var sequences = await _batchOutbox.AppendBatchAsync(commands, cancellationToken).ConfigureAwait(false);
            if (sequences.Count != commands.Count)
            {
                throw new InvalidOperationException("PostgreSQL outbox 批量写入返回的 sequence 数量不一致。");
            }

            var wrapped = new YuoPersistenceCommand[commands.Count];
            for (var index = 0; index < commands.Count; index++)
            {
                wrapped[index] = new YuoOutboxPersistenceCommand(sequences[index], commands[index]);
            }

            await _queue.EnqueueBatchAsync(wrapped, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var command in commands)
        {
            await EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public YuoPersistenceQueueStats GetStats() => _queue.GetStats();

    public void Complete() => _queue.TryComplete();
}

/// <summary>
/// 出件箱未确认命令启动重放后台服务 (DaoHuaPersistenceReplayHostedService)。
/// <para>
/// 必须在通用持久化写回后台服务启动前执行，确保在系统异常重启后优先恢复并重放未确认的快照命令。
/// </para>
/// </summary>
public sealed class DaoHuaPersistenceReplayHostedService(
    YuoDurablePersistenceQueue queue,
    ILogger<DaoHuaPersistenceReplayHostedService> logger) : IHostedService
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
