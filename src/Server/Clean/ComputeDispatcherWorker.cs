using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Server.Clean;

/// <summary>批次 HTTP 派发结局。</summary>
public enum ComputeDispatchStatus : byte
{
    /// <summary>节点返回了合法批处理响应。</summary>
    Ok = 0,
    /// <summary>节点返回了非 2xx 状态码。</summary>
    HttpError = 1,
    /// <summary>网络错误或请求超时。</summary>
    NetworkError = 2,
    /// <summary>响应体不是合法的 compute.v1 批次结果。</summary>
    InvalidResponse = 3
}

/// <summary>一次批次派发的完整结果。</summary>
/// <param name="Status">派发结局分类。</param>
/// <param name="HttpStatus">HTTP 状态码（若已到达节点）。</param>
/// <param name="Response">合法的批次响应。</param>
/// <param name="Error">错误描述（不含令牌等敏感信息）。</param>
public sealed record ComputeDispatchOutcome(
    ComputeDispatchStatus Status,
    HttpStatusCode? HttpStatus,
    ComputeAdvanceBatchResponse? Response,
    string? Error);

/// <summary>
/// ComputeNode 内部批处理 HTTP 客户端。
/// 只负责网络边界：令牌头、契约版本校验与异常分类；不含任何持久化语义，便于注入 fake handler 测试。
/// </summary>
public sealed class ComputeNodeClient(HttpClient http)
{
    /// <summary>向指定节点端点发起一批冷挂机推进请求。</summary>
    public async Task<ComputeDispatchOutcome> AdvanceBatchAsync(
        string endpoint,
        string token,
        ComputeAdvanceBatchRequest batch,
        CancellationToken cancellationToken)
    {
        var url = new Uri(endpoint.TrimEnd('/') + ComputeDispatchContract.AdvanceBatchPath, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation(ComputeDispatchContract.TokenHeader, token);
        var body = JsonSerializer.SerializeToUtf8Bytes(batch, ServerJson.Options);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ComputeDispatchOutcome(ComputeDispatchStatus.NetworkError, null, null, "请求超时");
        }
        catch (HttpRequestException exception)
        {
            return new ComputeDispatchOutcome(ComputeDispatchStatus.NetworkError, null, null, exception.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new ComputeDispatchOutcome(
                    ComputeDispatchStatus.HttpError, response.StatusCode, null, $"HTTP {(int)response.StatusCode}");
            }

            ComputeAdvanceBatchResponse? parsed;
            try
            {
                parsed = await response.Content.ReadFromJsonAsync<ComputeAdvanceBatchResponse>(ServerJson.Options, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return new ComputeDispatchOutcome(ComputeDispatchStatus.InvalidResponse, response.StatusCode, null, "响应反序列化失败");
            }

            if (parsed is null || parsed.Jobs is null
                || !string.Equals(parsed.ContractVersion, ComputeDispatchContract.Version, StringComparison.Ordinal))
            {
                return new ComputeDispatchOutcome(ComputeDispatchStatus.InvalidResponse, response.StatusCode, null, "响应契约无效");
            }

            return new ComputeDispatchOutcome(ComputeDispatchStatus.Ok, response.StatusCode, parsed, null);
        }
    }
}

/// <summary>
/// 主服务 → ComputeNode 冷挂机派发 worker。
/// 轮询领取 platform.compute_jobs 到期任务 → 组装 compute.v1 批次 → POST 到计算节点 →
/// 成功结果经 SourceStateVersion CAS 门控落库并归档 output_payload；
/// HTTP/网络层失败按租约令牌整批退回 pending，attempts 超上限自动进入 dead 终态。
/// 未配置端点或令牌时安全停用（任务保留 pending，等待配置补齐或 retention 策略处理）。
/// </summary>
public sealed class ComputeDispatcherWorker(
    PostgresComputeJobRepository jobs,
    PostgresSectionRepository sections,
    HttpClient http,
    ServerOptions options,
    GameDataCatalog gameData,
    ILogger<ComputeDispatcherWorker> logger) : BackgroundService
{
    /// <summary>共享 HttpClient 注册名。</summary>
    public const string HttpClientName = "compute-node";

    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));
    private int _endpointCursor = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ComputeNodeConfigured)
        {
            logger.LogInformation(
                "冷挂机派发器未启用：需要 ComputeNode:Enabled=true 且配置 ComputeNode:Endpoints 与 ComputeNode:Token；任务保持 pending。");
            return;
        }

        var client = new ComputeNodeClient(http);
        logger.LogInformation("ComputeDispatcherWorker started batch={Batch} interval={Interval} endpoints={Endpoints}.",
            options.ComputeBatchSize, options.ComputePollInterval, options.ComputeNodeEndpoints.Count);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claims = await jobs.ClaimDueAsync(options.ComputeBatchSize, options.ComputeLease, stoppingToken);
                if (claims.Count == 0)
                {
                    await Task.Delay(options.ComputePollInterval, stoppingToken);
                    continue;
                }
                await DispatchBatchAsync(client, claims, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_failureLog.TryAcquire())
                    logger.LogError(exception, "ComputeDispatcherWorker 循环失败，将在下个周期重试。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task DispatchBatchAsync(
        ComputeNodeClient client,
        IReadOnlyList<ComputeJobClaim> claims,
        CancellationToken cancellationToken)
    {
        var ready = new List<(ComputeJobClaim Claim, ComputeAdvanceJobRequest Request)>(claims.Count);
        foreach (var claim in claims)
        {
            ComputeAdvanceJobRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<ComputeAdvanceJobRequest>(claim.InputPayload, ServerJson.Options);
            }
            catch (JsonException)
            {
                // 坏载荷不会自愈：直接进 dead 保留诊断窗口。
            }
            if (request is null || request.Sections is not { Count: > 0 }
                || request.PlayerId != claim.PlayerId)
            {
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, "input_payload 无效", retryable: false,
                    TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
                continue;
            }
            ready.Add((claim, request));
        }
        if (ready.Count == 0) return;

        var endpoint = NextEndpoint();
        var batch = new ComputeAdvanceBatchRequest(
            ComputeDispatchContract.Version,
            gameData.Fingerprint,
            ready.Select(entry => entry.Request).ToArray());
        var outcome = await client.AdvanceBatchAsync(endpoint, options.ComputeNodeToken!, batch, cancellationToken);
        if (outcome.Status != ComputeDispatchStatus.Ok || outcome.Response is null)
        {
            var backoff = outcome.HttpStatus switch
            {
                HttpStatusCode.TooManyRequests => TimeSpan.FromSeconds(10),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TimeSpan.FromMinutes(5),
                HttpStatusCode.Conflict => TimeSpan.FromMinutes(1),
                _ => Backoff(claims[0].Attempts)
            };
            if (_failureLog.TryAcquire())
            {
                logger.LogError("ComputeNode 批次派发失败 status={Status} http={HttpStatus} jobs={Jobs} error={Error}",
                    outcome.Status, outcome.HttpStatus, claims.Count, outcome.Error);
            }
            await jobs.ReleaseLeaseAsync(claims[0].LeaseToken,
                outcome.Error ?? $"http {(int?)outcome.HttpStatus ?? 0}", backoff,
                options.ComputeMaxAttempts, cancellationToken);
            return;
        }

        var results = new Dictionary<Guid, ComputeAdvanceJobResult>(outcome.Response.Jobs.Count);
        foreach (var result in outcome.Response.Jobs) results[result.JobId] = result;

        foreach (var (claim, _) in ready)
        {
            if (!results.TryGetValue(claim.JobId, out var result) || result.PlayerId != claim.PlayerId)
            {
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, "节点响应缺少对应任务结果",
                    retryable: true, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
                continue;
            }
            if (!result.Success)
            {
                await jobs.FailAsync(claim.JobId, claim.LeaseToken,
                    $"{result.ErrorCode}:{result.ErrorMessage}",
                    result.Retryable, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
                continue;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var writes = result.Sections is { Count: > 0 }
                    ? result.Sections.Select(section => ComputeSectionCodec.ToSectionWrite(
                        claim.PlayerId, result.TargetStateVersion, section, now)).ToArray()
                    : throw new InvalidDataException("成功结果缺少 section 输出。");
                var applied = await sections.ApplyComputeResultAsync(
                    claim.PlayerId, result.SourceStateVersion, result.TargetStateVersion,
                    writes, claim.TargetTime, gameData.Fingerprint, cancellationToken);
                var output = JsonSerializer.SerializeToUtf8Bytes(result, ServerJson.Options);
                if (applied != ComputeApplyOutcome.Applied)
                {
                    // 玩家已上线或版本漂移：结果丢弃但任务仍按完成归档，证据留在 output_payload。
                    logger.LogInformation("冷挂机结果因版本漂移被丢弃 job={JobId} player={PlayerId} outcome={Outcome}",
                        claim.JobId, claim.PlayerId, applied);
                }
                await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, output, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                // 节点产出污染不能重试出正确结果：进 dead 保留诊断窗口。
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, $"INVALID_OUTPUT:{exception.Message}",
                    retryable: false, TimeSpan.Zero, options.ComputeMaxAttempts, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_failureLog.TryAcquire())
                    logger.LogError(exception, "冷挂机结果落库失败 job={JobId} player={PlayerId}", claim.JobId, claim.PlayerId);
                await jobs.FailAsync(claim.JobId, claim.LeaseToken, exception.Message,
                    retryable: true, Backoff(claim.Attempts), options.ComputeMaxAttempts, cancellationToken);
            }
        }
    }

    private string NextEndpoint()
    {
        var endpoints = options.ComputeNodeEndpoints;
        var index = (int)((uint)Interlocked.Increment(ref _endpointCursor) % (uint)endpoints.Count);
        return endpoints[index];
    }

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 8))));
}

/// <summary>冷挂机派发链的服务装配扩展。</summary>
public static class ComputeDispatchServiceCollectionExtensions
{
    /// <summary>
    /// 注册冷挂机派发链：任务仓库、HTTP 客户端、派发 worker 与冷挂机扫描 worker。
    /// Api 与 PersistenceWorker 两种进程角色共用；未配置 ComputeNode:* 时 worker 安全停用。
    /// </summary>
    public static IServiceCollection AddComputeDispatch(this IServiceCollection services)
    {
        // PersistenceWorker 角色不注册 GameDataCatalog；派发器只需要指纹，按惰性工厂兜底。
        services.TryAddSingleton(_ => GameDataCatalog.LoadEmbedded());
        services.AddSingleton<PostgresComputeJobRepository>();
        services.AddHttpClient(ComputeDispatcherWorker.HttpClientName, (sp, client) =>
            client.Timeout = sp.GetRequiredService<ServerOptions>().ComputeRequestTimeout);
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<ComputeDispatcherWorker>(
            sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(ComputeDispatcherWorker.HttpClientName)));
        services.AddHostedService<ColdCatchUpWorker>();
        return services;
    }
}
