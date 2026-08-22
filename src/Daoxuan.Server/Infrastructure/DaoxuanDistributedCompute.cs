using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

/// <summary>
/// 分布式冷挂机计算选项配置 (DaoxuanDistributedComputeOptions)。
/// </summary>
public sealed class DaoxuanDistributedComputeOptions
{
    /// <summary>是否启用分布式冷计算。</summary>
    public bool Enabled { get; init; }

    /// <summary>远程计算节点 HTTP 端点列表。</summary>
    public string[] NodeEndpoints { get; init; } = [];

    /// <summary>调用远程计算节点的安全 Token (X-Compute-Token)。</summary>
    public string? Token { get; init; }

    /// <summary>每批次发送给计算节点的玩家快照上限。</summary>
    public int BatchSize { get; init; } = 64;

    /// <summary>玩家最小离线滞后时长（秒），超过此时长未模拟的离线玩家才会被选为冷计算候选。</summary>
    public int MinimumIdleLagSeconds { get; init; } = 300;

    /// <summary>轮询数据库候选玩家的间隔时间（秒）。</summary>
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>向计算节点发起批处理 HTTP 请求的超时时间（秒）。</summary>
    public int RequestTimeoutSeconds { get; init; } = 60;

    public static DaoxuanDistributedComputeOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoints = configuration.GetSection("DistributedCompute:NodeEndpoints").Get<string[]>()
            ?? configuration["DistributedCompute:NodeEndpoints"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        return new DaoxuanDistributedComputeOptions
        {
            Enabled = configuration.GetValue("DistributedCompute:Enabled", false),
            NodeEndpoints = endpoints
                .Where(endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Token = configuration["DistributedCompute:Token"],
            BatchSize = Math.Clamp(configuration.GetValue("DistributedCompute:BatchSize", 64), 1, 256),
            MinimumIdleLagSeconds = Math.Clamp(configuration.GetValue("DistributedCompute:MinimumIdleLagSeconds", 300), 60, 86_400),
            PollIntervalSeconds = Math.Clamp(configuration.GetValue("DistributedCompute:PollIntervalSeconds", 30), 5, 3_600),
            RequestTimeoutSeconds = Math.Clamp(configuration.GetValue("DistributedCompute:RequestTimeoutSeconds", 60), 5, 600)
        };
    }
}

/// <summary>
/// 分布式冷挂机计算协调后台服务 (DaoxuanDistributedComputeHostedService)。
/// <para>
/// 职责与安全拓扑：
/// 1. 扫描数据库中长时间处于离线/闲置挂机状态且滞后时长超过阈值的玩家快照。
/// 2. 严格过滤掉当前常驻在本地内存或正连接的玩家（防止状态覆写）。
/// 3. 将快照打包为批处理任务，轮询分发给外部无状态冷计算节点 (<c>Daoxuan.ComputeNode</c>) 进行纯 CPU 确定性演进。
/// 4. 校验计算结果并将演进后的新快照写回数据库，同时记录任务流水至 <c>distributed_compute_jobs</c> 表。
/// </para>
/// </summary>
public sealed class DaoxuanDistributedComputeHostedService(
    DaoxuanDistributedComputeOptions options,
    DaoxuanDatabaseOptions databaseOptions,
    NpgsqlDataSource? dataSource,
    IYuoSnapshotStore snapshotStore,
    Runtime.DaoxuanGameHost gameHost,
    ILogger<DaoxuanDistributedComputeHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private int _nextNodeIndex;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("分布式冷挂机计算未启用");
            return;
        }

        if (!databaseOptions.IsPostgres || dataSource is null)
        {
            logger.LogWarning("分布式冷挂机计算需要 Storage:Provider=postgres，当前配置已跳过");
            return;
        }

        if (options.NodeEndpoints.Length == 0)
        {
            logger.LogWarning("分布式冷挂机计算已启用，但没有配置 DistributedCompute:NodeEndpoints");
            return;
        }

        logger.LogInformation(
            "分布式冷挂机计算已启用：节点 {NodeCount}，批次 {BatchSize}，最小滞后 {LagSeconds}s",
            options.NodeEndpoints.Length,
            options.BatchSize,
            options.MinimumIdleLagSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
                if (count > 0)
                {
                    logger.LogInformation("分布式冷挂机计算完成 {Count} 个玩家快照", count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "分布式冷挂机计算调度失败");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<int> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        var jobs = new List<RemoteAdvanceJobRequest>(Math.Min(candidates.Count, options.BatchSize));
        var byJobId = new Dictionary<Guid, SnapshotCandidate>();
        foreach (var candidate in candidates)
        {
            if (await gameHost.IsPlayerLocallyResidentAsync(candidate.PlayerId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var jobId = Guid.NewGuid();
            await InsertJobAsync(jobId, candidate, cancellationToken).ConfigureAwait(false);
            jobs.Add(new RemoteAdvanceJobRequest(
                jobId,
                candidate.PlayerId,
                Convert.ToBase64String(candidate.Payload),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            byJobId[jobId] = candidate;
            if (jobs.Count >= options.BatchSize) break;
        }

        if (jobs.Count == 0) return 0;
        var endpoint = SelectEndpoint();
        RemoteAdvanceBatchResponse? response;
        try
        {
            response = await SendBatchAsync(endpoint, jobs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            foreach (var job in jobs)
            {
                await MarkJobFailedAsync(job.JobId, "NODE_REQUEST_FAILED", exception.Message, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }

        var completed = 0;
        foreach (var result in response.Jobs)
        {
            if (!byJobId.TryGetValue(result.JobId, out var candidate)) continue;
            if (!result.Success || string.IsNullOrWhiteSpace(result.PayloadBase64))
            {
                await MarkJobFailedAsync(
                    result.JobId,
                    result.ErrorCode ?? "ADVANCE_FAILED",
                    result.ErrorMessage ?? "计算节点未返回成功结果。",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var payload = Convert.FromBase64String(result.PayloadBase64);
            await snapshotStore.SaveAsync(new YuoSnapshotRecord(
                "player",
                candidate.PlayerId.ToString("D"),
                payload,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await MarkJobSucceededAsync(result.JobId, payload, cancellationToken).ConfigureAwait(false);
            completed++;
        }

        return completed;
    }

    private Uri SelectEndpoint()
    {
        var index = Interlocked.Increment(ref _nextNodeIndex);
        return new Uri(options.NodeEndpoints[Math.Abs(index % options.NodeEndpoints.Length)]);
    }

    private async ValueTask<IReadOnlyList<SnapshotCandidate>> LoadCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource!.CreateCommand(
            """
            SELECT player_id, payload_encoding, payload
            FROM player_snapshots
            WHERE kind = 'player'
              AND player_id IS NOT NULL
              AND presence_mode IN (1, 2)
              AND last_simulated_at IS NOT NULL
              AND last_simulated_at < NOW() - (@lagSeconds * INTERVAL '1 second')
            ORDER BY last_simulated_at
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("lagSeconds", options.MinimumIdleLagSeconds);
        command.Parameters.AddWithValue("limit", options.BatchSize * 2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SnapshotCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var playerId = reader.GetGuid(0);
            var encoding = reader.GetString(1);
            var payload = (byte[])reader[2];
            result.Add(new SnapshotCandidate(playerId, DaoxuanPostgresPayloadCodec.Decode(encoding, payload)));
        }

        return result;
    }

    private async ValueTask<RemoteAdvanceBatchResponse> SendBatchAsync(
        Uri endpoint,
        IReadOnlyList<RemoteAdvanceJobRequest> jobs,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/compute/advance-batch")
        {
            Content = JsonContent.Create(new RemoteAdvanceBatchRequest(jobs), options: JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            request.Headers.Add("X-Compute-Token", options.Token);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemoteAdvanceBatchResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("计算节点返回空响应。 ");
    }

    private async ValueTask InsertJobAsync(Guid jobId, SnapshotCandidate candidate, CancellationToken cancellationToken)
    {
        await using var command = dataSource!.CreateCommand(
            """
            INSERT INTO distributed_compute_jobs (
                job_id, player_id, status, target_time, input_payload, created_at, updated_at)
            VALUES (@jobId, @playerId, 0, NOW(), @payload, NOW(), NOW())
            """);
        command.Parameters.AddWithValue("jobId", jobId);
        command.Parameters.AddWithValue("playerId", candidate.PlayerId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, candidate.Payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MarkJobSucceededAsync(Guid jobId, byte[] payload, CancellationToken cancellationToken)
    {
        await using var command = dataSource!.CreateCommand(
            """
            UPDATE distributed_compute_jobs
            SET status = 1,
                output_payload = @payload,
                updated_at = NOW()
            WHERE job_id = @jobId
            """);
        command.Parameters.AddWithValue("jobId", jobId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MarkJobFailedAsync(Guid jobId, string code, string message, CancellationToken cancellationToken)
    {
        await using var command = dataSource!.CreateCommand(
            """
            UPDATE distributed_compute_jobs
            SET status = 2,
                error_code = @code,
                error_message = @message,
                updated_at = NOW()
            WHERE job_id = @jobId
            """);
        command.Parameters.AddWithValue("jobId", jobId);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("message", message.Length > 512 ? message[..512] : message);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct SnapshotCandidate(Guid PlayerId, byte[] Payload);
}

public sealed record RemoteAdvanceBatchRequest(IReadOnlyList<RemoteAdvanceJobRequest> Jobs);

public sealed record RemoteAdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    string PayloadBase64,
    long TargetUnixMilliseconds);

public sealed record RemoteAdvanceBatchResponse(
    IReadOnlyList<RemoteAdvanceJobResult> Jobs,
    string GameDataFingerprint);

public sealed record RemoteAdvanceJobResult(
    Guid JobId,
    Guid PlayerId,
    bool Success,
    string? PayloadBase64,
    int CompletedActions,
    int Transitions,
    int CombatEvents,
    string? ErrorCode,
    string? ErrorMessage);
