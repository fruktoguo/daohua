// ComputeNode：可选的高延迟冷挂机补算节点，部署在远程个人设备上。
// 边界：只做确定性 section 推进，不持玩家权威状态、不直连生产数据库；
// 唯一业务入口是 /internal/v1/compute/advance-batch，且仅凭 X-Compute-Token 内部令牌鉴权。
using DaoHua.ComputeNode;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    // 请求体上限与契约上限对齐：超限批次在 Kestrel 层即以 413 拒绝，不进入模型绑定。
    options.Limits.MaxRequestBodySize = ComputeContract.MaxBatchBytes;
    // 节点部署在不可控的远程网络环境，收紧空闲与请求头超时以抵御低速连接占用。
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});

var configuredUrls = builder.Configuration["ComputeNode:Urls"]
                    ?? Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_URLS");
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls(configuredUrls);
}
else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8090");
}

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton(new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    MaxDepth = 32
});
builder.Services.AddSingleton<GameDataFingerprint>();
builder.Services.AddSingleton<DeterministicBatchEngine>();
builder.Services.AddSingleton<ComputeMetrics>();

// 令牌是节点唯一的安全边界：允许无令牌启动等于把补算接口裸露给任何可达者，宁可启动失败。
var token = builder.Configuration["ComputeNode:Token"]
            ?? Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    throw new InvalidOperationException(
        "未配置 ComputeNode:Token/DAOHUA_COMPUTE_TOKEN。ComputeNode 必须在显式内部鉴权下启动。");
}

var maxJobs = Math.Clamp(
    builder.Configuration.GetValue("ComputeNode:MaxJobsPerBatch", ComputeContract.DefaultMaxJobs),
    1,
    ComputeContract.HardMaxJobs);
var maxConcurrentBatches = Math.Clamp(
    builder.Configuration.GetValue("ComputeNode:MaxConcurrentBatches", Math.Max(1, Environment.ProcessorCount)),
    1,
    64);
// 并发闸：超限时立即以 429 拒绝而非排队——节点可被主服务水平替换，快速失败更利于重试调度。
var batchGate = new SemaphoreSlim(maxConcurrentBatches, maxConcurrentBatches);

var app = builder.Build();
var fingerprint = app.Services.GetRequiredService<GameDataFingerprint>();
var metrics = app.Services.GetRequiredService<ComputeMetrics>();
var engine = app.Services.GetRequiredService<DeterministicBatchEngine>();

// 统一异常出口：对外只回通用 ProblemDetails + traceId，内部异常细节仅留在节点日志。
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "计算节点内部错误",
            detail: "计算节点未能完成请求；请通过 traceId 查询内部日志。",
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = context.TraceIdentifier
            }).ExecuteAsync(context);
    });
});

// 健康/就绪端点对运维可达，载荷只允许放契约版本、指纹与计数器，不得携带令牌或玩家数据。
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "daohua-compute-node",
    contractVersion = ComputeContract.Version,
    gameDataFingerprint = fingerprint.Value,
    maxJobsPerBatch = maxJobs,
    maxBatchBytes = ComputeContract.MaxBatchBytes
}));

app.MapGet("/readyz", () => Results.Ok(new
{
    status = "ready",
    service = "daohua-compute-node",
    contractVersion = ComputeContract.Version,
    gameDataFingerprint = fingerprint.Value,
    activeBatches = metrics.ActiveBatches,
    completedJobs = metrics.CompletedJobs,
    failedJobs = metrics.FailedJobs
}));

app.MapPost("/internal/v1/compute/advance-batch", async (
    HttpRequest request,
    AdvanceBatchRequest batch,
    CancellationToken cancellationToken,
    ILoggerFactory loggerFactory) =>
{
    if (!TokenValidator.IsValid(request, token))
    {
        metrics.RecordUnauthorized();
        return Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "未授权的计算节点请求",
            detail: "需要有效的内部计算令牌。",
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = request.HttpContext.TraceIdentifier
            });
    }

    if (!await batchGate.WaitAsync(TimeSpan.Zero, cancellationToken))
    {
        metrics.RecordRejected();
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "计算节点繁忙",
            detail: "当前批处理并发已达到上限，请稍后重试。",
            extensions: new Dictionary<string, object?>
            {
                ["retryAfterMilliseconds"] = 250
            });
    }

    var started = Stopwatch.GetTimestamp();
    metrics.BeginBatch();
    try
    {
        var validation = ComputeContract.Validate(batch, maxJobs);
        if (validation is not null)
        {
            metrics.RecordFailedBatch();
            return Results.BadRequest(validation);
        }

        // 配表指纹不一致的补算结果对主服务无效：以 409 显式拒绝，让主服务停止向该节点派发直到配置对齐。
        if (!string.IsNullOrWhiteSpace(batch.GameDataFingerprint)
            && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(batch.GameDataFingerprint),
                Encoding.UTF8.GetBytes(fingerprint.Value)))
        {
            metrics.RecordFailedBatch();
            return Results.Conflict(new
            {
                code = "GAME_DATA_FINGERPRINT_MISMATCH",
                expected = fingerprint.Value,
                actual = batch.GameDataFingerprint,
                traceId = request.HttpContext.TraceIdentifier
            });
        }

        var results = new List<AdvanceJobResult>(batch.Jobs.Count);
        foreach (var job in batch.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = engine.Advance(job, fingerprint.Value);
            results.Add(result);
            if (result.Success) metrics.RecordCompletedJob();
            else metrics.RecordFailedJob();
        }

        metrics.RecordCompletedBatch(Stopwatch.GetElapsedTime(started));
        return Results.Ok(new AdvanceBatchResponse(
            ComputeContract.Version,
            fingerprint.Value,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            results));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        metrics.RecordCancelledBatch();
        throw;
    }
    catch (Exception exception)
    {
        metrics.RecordFailedBatch();
        loggerFactory.CreateLogger("ComputeNode.Batch").LogError(
            exception,
            "批处理执行失败，traceId={TraceId}",
            request.HttpContext.TraceIdentifier);
        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "批处理执行失败",
            detail: "计算节点未能完成批处理。",
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = request.HttpContext.TraceIdentifier
            });
    }
    finally
    {
        metrics.EndBatch();
        batchGate.Release();
    }
});

app.Run();
