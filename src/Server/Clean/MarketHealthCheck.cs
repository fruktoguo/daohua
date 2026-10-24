namespace Server.Clean;

/// <summary>市场分片未持有有效 fence 时，API 不应被标记为可接收写请求。</summary>
public sealed class MarketHealthCheck(MarketRuntime runtime) : IHealthCheck
{
    /// <summary>
    /// 分片持有有效写者租约（runtime.IsReady）时报告 Healthy，否则报告 Unhealthy 摘除写流量。
    /// </summary>
    /// <param name="context">健康检查上下文。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(runtime.IsReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("市场分片尚未取得有效写者租约。"));
}
