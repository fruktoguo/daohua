namespace Server.Clean;

/// <summary>PostgreSQL 就绪探针：以短超时 SELECT 1 验证数据库可达，供 /readyz 使用。</summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>执行探针；2 秒内未响应判 Unhealthy，避免探针自身拖垮就绪检查。</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = (int)ProbeTimeout.TotalSeconds };
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL 探针超时。", data: new Dictionary<string, object>
            {
                ["timeoutMilliseconds"] = ProbeTimeout.TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            // 健康端点只返回状态，不把数据库连接串、SQL 或驱动细节暴露给外部。
            return HealthCheckResult.Unhealthy("PostgreSQL 不可用。", exception: null,
                data: new Dictionary<string, object> { ["errorType"] = ex.GetType().Name });
        }
    }
}
