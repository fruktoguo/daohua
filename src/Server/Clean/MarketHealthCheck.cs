namespace Server.Clean;

/// <summary>市场分片未持有有效 fence 时，API 不应被标记为可接收写请求。</summary>
public sealed class MarketHealthCheck(MarketRuntime runtime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(runtime.IsReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("市场分片尚未取得有效写者租约。"));
}
