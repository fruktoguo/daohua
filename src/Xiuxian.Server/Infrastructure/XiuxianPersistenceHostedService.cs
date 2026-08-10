using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using YuoCore.Persistence;

namespace Xiuxian.Server.Infrastructure;

public sealed class XiuxianPersistenceHostedService(
    YuoPersistenceQueue queue,
    IYuoSnapshotStore store,
    ILogger<XiuxianPersistenceHostedService> logger,
    IYuoPersistenceOutbox? outbox = null) : BackgroundService
{
    private const int BatchSize = 256;
    private const int MaxWriteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private long _outboxAckBlockedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await queue.DequeueBatchAsync(BatchSize, stoppingToken);
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
