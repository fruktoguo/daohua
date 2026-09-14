namespace Server.Clean;

/// <summary>
/// 表示 PostgresHealthCheck 类。
/// </summary>
/// <param name="dataSource">PostgreSQL 数据源。</param>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 执行CheckHealthAsync（异步）。
    /// </summary>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <param name="cancellationToken">取消操作的标记。</param>
    /// <returns>异步操作结果，包含 HealthCheckResult。</returns>
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
