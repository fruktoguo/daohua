using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

/// <summary>
/// 异步快照持久化后台写回服务 (DaoxuanPersistenceHostedService)。
/// <para>
/// 职责与容灾机制：
/// 1. 批量出队：单循环批量消费 <see cref="YuoPersistenceQueue"/>（每批最大 256 条），减少数据库交互开销。
/// 2. 状态沿高低水位监控：队列积压达 80% 记录 Warning，达 95% 记录 Error；回落至 70% 以下自动重置告警状态。
/// 3. 重试与 Outbox 确认：保存快照到存储时支持短暂 IO 重试；成功后递增推进 Outbox Ack，若某条失败则阻塞后续 Ack (<see cref="MarkOutboxAckBlocked"/>) 确保崩溃后全量重放。
/// 4. 停机排空 (Drain)：停服时提供 60 秒预算排空队列中全部剩余命令并执行 Outbox 压缩。
/// </para>
/// </summary>
public sealed class DaoxuanPersistenceHostedService(
    YuoPersistenceQueue queue,
    IYuoSnapshotStore store,
    ILogger<DaoxuanPersistenceHostedService> logger,
    IYuoPersistenceOutbox? outbox = null) : BackgroundService
{
    private const int BatchSize = 256;
    private const int MaxWriteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    // 队列水位告警阈值：积压达容量 80% 打警告、95% 打错误；回落到 70% 以下才重置武装状态，
    // 采用状态沿触发，避免磁盘卡顿期间每个批次都重复刷屏。仅观测，不改变持久化行为。
    private const double WarningRatio = 0.80;
    private const double ErrorRatio = 0.95;
    private const double RearmRatio = 0.70;
    private long _outboxAckBlockedAt;
    // 水位告警状态：0=正常，1=警告，2=错误；仅由 ExecuteAsync 单读循环访问，无需并发保护。
    private int _watermarkLevel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await queue.DequeueBatchAsync(BatchSize, stoppingToken);
                CheckQueueWatermark();
                // 命令一旦出队就必须完成原子快照写入，不能让停服取消造成 in-flight 命令丢失。
                await PersistBatchAsync(batch, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停服阶段由 StopAsync 继续 drain 已经进入队列的快照。
        }
        catch (ChannelClosedException)
        {
            // 写入端关闭后，剩余命令由 StopAsync drain。
        }
    }

    /// <summary>
    /// 出队后检查持久化队列水位：跨越 80%/95% 阈值时各告警一次，回落到 70% 以下重置。
    /// 队列空闲时本循环阻塞在 DequeueBatchAsync 上，不会忙等或重复输出。
    /// </summary>
    private void CheckQueueWatermark()
    {
        var stats = queue.GetStats();
        if (stats.Capacity <= 0) return;

        var ratio = stats.BacklogRatio;
        if (ratio < RearmRatio)
        {
            _watermarkLevel = 0;
            return;
        }

        var nextLevel = ratio >= ErrorRatio ? 2 : ratio >= WarningRatio ? 1 : 0;
        if (nextLevel == 0 || _watermarkLevel >= nextLevel) return;

        _watermarkLevel = nextLevel;
        if (nextLevel == 2)
        {
            logger.LogError(
                "持久化队列积压达到错误水位：pending {Pending}/{Capacity}（{Ratio:P1}），磁盘写入可能严重受阻",
                stats.PendingCount,
                stats.Capacity,
                ratio);
        }
        else
        {
            logger.LogWarning(
                "持久化队列积压达到警告水位：pending {Pending}/{Capacity}（{Ratio:P1}）",
                stats.PendingCount,
                stats.Capacity,
                ratio);
        }
    }

    /// <summary>
    /// 停机排空：关闭队列输入、消费排空全部在途快照并执行 Outbox 压缩整理。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        logger.LogInformation("开始清空玩家持久化队列");
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        queue.TryComplete();
        try
        {
            await base.StopAsync(shutdownCts.Token);

            while (!shutdownCts.IsCancellationRequested)
            {
                var batch = queue.TryDequeueBatch(BatchSize);
                if (batch.Count == 0) break;
                await PersistBatchAsync(batch, shutdownCts.Token);
            }

            if (outbox is not null)
            {
                await outbox.CompactAsync(shutdownCts.Token);
            }

            logger.LogInformation(
                "玩家持久化队列已清空，耗时 {ElapsedMilliseconds:F1}ms",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
            logger.LogError(
                "持久化停服清空超过 60 秒；已确认前的命令保留在 durable outbox，下一次启动将自动重放");
        }
    }

    private async Task PersistBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> batch,
        CancellationToken cancellationToken)
    {
        foreach (var command in batch)
        {
            var outboxCommand = command as YuoOutboxPersistenceCommand;
            var actualCommand = outboxCommand?.Inner ?? command;
            if (actualCommand is not SaveSnapshotCommand snapshotCommand)
            {
                MarkOutboxAckBlocked(outboxCommand);
                logger.LogWarning("忽略未知持久化命令 {CommandType}", actualCommand.GetType().Name);
                continue;
            }

            try
            {
                await SaveWithRetryAsync(snapshotCommand.Snapshot, cancellationToken);
                if (outboxCommand is not null
                    && outbox is not null
                    && !IsOutboxAckBlocked(outboxCommand.OutboxSequence))
                {
                    // 快照已原子替换后再 ack；ack 失败只会造成幂等重放，不会丢快照。
                    await outbox.AcknowledgeThroughAsync(
                        outboxCommand.OutboxSequence,
                        CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkOutboxAckBlocked(outboxCommand);
                throw;
            }
            catch (Exception exception)
            {
                MarkOutboxAckBlocked(outboxCommand);
                logger.LogError(exception, "保存玩家快照失败 {Kind}/{Id}", snapshotCommand.Snapshot.Kind, snapshotCommand.Snapshot.Id);
            }
        }
    }

    private async ValueTask SaveWithRetryAsync(
        YuoSnapshotRecord snapshot,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.SaveAsync(snapshot, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                attempt < MaxWriteAttempts
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private void MarkOutboxAckBlocked(YuoOutboxPersistenceCommand? command)
    {
        if (command is null) return;
        while (true)
        {
            var current = Volatile.Read(ref _outboxAckBlockedAt);
            if (current != 0 && current <= command.OutboxSequence) return;
            if (Interlocked.CompareExchange(
                    ref _outboxAckBlockedAt,
                    command.OutboxSequence,
                    current) == current)
            {
                return;
            }
        }
    }

    private bool IsOutboxAckBlocked(long sequence)
    {
        var blockedAt = Volatile.Read(ref _outboxAckBlockedAt);
        return blockedAt != 0 && sequence >= blockedAt;
    }
}
