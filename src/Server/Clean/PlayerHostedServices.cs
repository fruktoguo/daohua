namespace Server.Clean;

/// <summary>在线玩家脏 section 的周期落盘后台服务。</summary>
public sealed class PlayerFlushHostedService(IPlayerRuntime runtime, ServerOptions options, ILogger<PlayerFlushHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.FlushAllAsync(false, stoppingToken); await Task.Delay(options.SectionFlushInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家 section 刷新失败。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

/// <summary>在线玩家模拟时钟，使用运行时边界推进。</summary>
public sealed class PlayerSimulationHostedService(IPlayerRuntime runtime, IPlayerSimulationClock clock, ILogger<PlayerSimulationHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.AdvanceAllAsync(clock.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家模拟推进失败。"); }
            var boundary = runtime.NextAdvanceBoundaryUtc;
            var delay = boundary is { } next ? next - clock.UtcNow : TimeSpan.FromSeconds(1);
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            else if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
