namespace Server.Clean.Social;

/// <summary>每秒裁决斗地主回合超时并推进已开启的托管座位。</summary>
public sealed class DouDiZhuTurnWatchdog(
    SocialRuntime social,
    ILogger<DouDiZhuTurnWatchdog> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
                await social.AdvanceDouDiZhuTurnsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_loopErrorLog.TryAcquire())
                    logger.LogError(exception, "斗地主回合看门狗周期失败，下个周期重试。");
            }
        }
    }
}
