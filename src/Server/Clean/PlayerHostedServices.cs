namespace Server.Clean;

/// <summary>在线玩家脏 section 的周期落盘后台服务。</summary>
public sealed class PlayerFlushHostedService(IPlayerRuntime runtime, ServerOptions options, ILogger<PlayerFlushHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));
    /// <summary>
    /// 刷盘主循环：按 SectionFlushInterval 周期把全部在线玩家的脏分段写入 durable 队列；
    /// 单轮失败经节流记日志后 2 秒退避重试，停机令牌取消即退出（BackgroundService 停止时由宿主等待本任务结束）。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runtime.FlushAllAsync(false, stoppingToken);
                if (!await HostedLoopSupport.DelayQuietlyAsync(options.SectionFlushInterval, stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家 section 刷新失败。");
                if (!await HostedLoopSupport.DelayQuietlyAsync(TimeSpan.FromSeconds(2), stoppingToken)) break;
            }
        }
    }
}

/// <summary>在线玩家模拟时钟，使用运行时边界推进。</summary>
public sealed class PlayerSimulationHostedService(IPlayerRuntime runtime, IPlayerSimulationClock clock, ILogger<PlayerSimulationHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));
    /// <summary>
    /// 模拟推进主循环：把全部在线玩家推进到模拟时钟当前时刻，步进间隔夹紧在
    /// 「下一次行动到期边界」与 50ms~1s 之间——有到期行动时贴边界唤醒，无到期行动时空转 1 秒；
    /// 单轮失败经节流记日志后继续下一轮，停机令牌取消即退出。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var hadError = false;
            try
            {
                await runtime.AdvanceAllAsync(clock.UtcNow, stoppingToken);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                hadError = true;
                consecutiveErrors++;
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家模拟推进失败。");
            }
            DateTimeOffset? boundary = null;
            try
            {
                boundary = runtime.NextAdvanceBoundaryUtc;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // 边界计算绝不允许在 try 外抛出把整个模拟循环打死；出错时记错误并走退避兜底间隔。
                hadError = true;
                consecutiveErrors++;
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "计算下一次在线模拟边界失败，将使用退避间隔继续推进。");
            }

            var delay = boundary is { } next ? next - clock.UtcNow : TimeSpan.FromSeconds(1);
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            else if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (hadError)
            {
                // 连续失败时注入指数退避下限，避免滞留玩家或短暂故障把推进循环打成 50ms 空转占满 CPU。
                var errorFloor = TimeSpan.FromMilliseconds(Math.Min(5_000, 250 * (1 << Math.Min(4, consecutiveErrors - 1))));
                if (delay < errorFloor) delay = errorFloor;
            }
            if (!await HostedLoopSupport.DelayQuietlyAsync(delay, stoppingToken)) break;
        }
    }
}
