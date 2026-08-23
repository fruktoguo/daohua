// ==============================================================================
// 文件名称: PersistenceDispatcher.cs
// 主要职责: 提供快照持久化命令的统一队列分发与 Durable Outbox 路由调度。
// 核心逻辑 / 定义内容:
//   1. 持久化队列调度契约抽象 (IPersistenceQueue, IBatchPersistenceOutbox)。
//   2. 依据配置将持久化命令路由至内存有界队列 (YuoPersistenceQueue) 或先写入 Outbox 表。
//   3. 维护持久化队列监控指标与停服排空完成标记。
// ==============================================================================

using YuoCore.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure;

/// <summary>
/// 持久化命令队列分发契约 (IPersistenceQueue)。
/// <para>
/// 职责：
/// 1. 统一封装快照写入等异步持久化命令的入队操作。
/// 2. 向上层屏蔽底层使用的是纯内存有界队列还是持久化出件箱 (Durable Outbox)。
/// </para>
/// </summary>
public interface IPersistenceQueue
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

public interface IBatchPersistenceOutbox : IYuoPersistenceOutbox
{
    ValueTask<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化命令分发器 (PersistenceDispatcher)。
/// <para>
/// 当配置启用 <c>Storage:DurableOutboxEnabled</c> 时，将命令路由至 <see cref="YuoDurablePersistenceQueue"/> 先写 PostgreSQL Outbox 表防丢；
/// 未启用时直接写入内存高性能有界队列 <see cref="YuoPersistenceQueue"/>。
/// </para>
/// </summary>
public sealed class PersistenceDispatcher : IPersistenceQueue
{
    private readonly YuoPersistenceQueue _queue;
    private readonly YuoDurablePersistenceQueue? _durableQueue;
    private readonly IBatchPersistenceOutbox? _batchOutbox;

    public PersistenceDispatcher(
        YuoPersistenceQueue queue,
        IServiceProvider services)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(services);
        _durableQueue = services.GetService<YuoDurablePersistenceQueue>();
        _batchOutbox = services.GetService<IYuoPersistenceOutbox>() as IBatchPersistenceOutbox;
    }

    public async ValueTask EnqueueAsync(
        YuoPersistenceCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_batchOutbox is not null)
        {
            var sequence = await _batchOutbox.AppendAsync(command, cancellationToken).ConfigureAwait(false);
            await _queue.EnqueueAsync(
                new YuoOutboxPersistenceCommand(sequence, command),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (_durableQueue is null)
        {
            await _queue.EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _durableQueue.EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnqueueBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) return;

        // 快照合并去重：同一批次中同一主键快照只保留最后一份最新快照，减轻 Outbox 与数据库压力
        commands = CoalesceSnapshots(commands);

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

            await _queue.EnqueueBatchAsync(wrapped, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        foreach (var command in commands)
        {
            await EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public YuoPersistenceQueueStats GetStats() => _queue.GetStats();

    public void Complete() => _queue.TryComplete();

    private static IReadOnlyList<YuoPersistenceCommand> CoalesceSnapshots(IReadOnlyList<YuoPersistenceCommand> commands)
    {
        if (commands.Count <= 1) return commands;
        var hasDuplicates = false;
        var seen = new HashSet<(string Kind, string Id)>();
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            if (commands[i] is SaveSnapshotCommand snap)
            {
                var key = (snap.Snapshot.Kind, snap.Snapshot.Id);
                if (!seen.Add(key))
                {
                    hasDuplicates = true;
                    break;
                }
            }
        }

        if (!hasDuplicates) return commands;

        var result = new List<YuoPersistenceCommand>(commands.Count);
        var added = new HashSet<(string Kind, string Id)>();
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            var cmd = commands[i];
            if (cmd is SaveSnapshotCommand snap)
            {
                var key = (snap.Snapshot.Kind, snap.Snapshot.Id);
                if (added.Add(key))
                {
                    result.Add(cmd);
                }
            }
            else
            {
                result.Add(cmd);
            }
        }

        result.Reverse();
        return result;
    }
}

/// <summary>
/// 出件箱未确认命令启动重放后台服务 (PersistenceReplayHostedService)。
/// <para>
/// 必须在通用持久化写回后台服务启动前执行，确保在系统异常重启后优先恢复并重放未确认的快照命令。
/// </para>
/// </summary>
public sealed class PersistenceReplayHostedService(
    YuoDurablePersistenceQueue queue,
    ILogger<PersistenceReplayHostedService> logger) : IHostedService
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

/// <summary>
/// Durable outbox 空闲重放后台服务。
/// <para>
/// 正常启动重放只能覆盖进程重启前的未确认记录；运行期如果请求取消恰好发生在
/// outbox 追加成功之后、内存队列入队或 ack 之前，数据库会留下少量未确认快照。
/// 本服务在持久化队列连续空闲后小批量重放这些记录，保证无需重启也能最终收敛。
/// </para>
/// </summary>
public sealed class OutboxReconciliationHostedService(
    YuoDurablePersistenceQueue durableQueue,
    YuoPersistenceQueue queue,
    ILogger<OutboxReconciliationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequiredIdle = TimeSpan.FromSeconds(30);
    private const int ReplayBatchSize = 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleSince = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            var stats = queue.GetStats();
            if (stats.PendingCount != 0 || stats.TotalEnqueued != stats.TotalDequeued)
            {
                idleSince = DateTimeOffset.MinValue;
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (idleSince == DateTimeOffset.MinValue)
            {
                idleSince = now;
                continue;
            }

            if (now - idleSince < RequiredIdle)
            {
                continue;
            }

            var replayed = await durableQueue
                .ReplayPendingAsync(ReplayBatchSize, stoppingToken)
                .ConfigureAwait(false);
            if (replayed > 0)
            {
                logger.LogInformation("空闲重放 {Count} 条未确认 outbox 命令", replayed);
                idleSince = DateTimeOffset.MinValue;
            }
        }
    }
}
