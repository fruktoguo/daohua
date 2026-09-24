// ComputeNode：可选的高延迟冷挂机补算节点，部署在远程个人设备上。
// 边界：只做确定性 section 推进，不持玩家权威状态、不直连生产数据库。
// 两种工作模式可并存：
//   1. 反向拨入（推荐）：配置 ComputeNode:UpstreamUrls 后主动 WebSocket 拨入主服
//      /internal/compute-link，进入动态节点组热插拔取任务，NAT 后免隧道；
//   2. 独立 HTTP 调试口：/internal/v1/compute/advance-batch 仅凭 X-Compute-Token 鉴权，
//      供手动/旁路验证批次执行（生产派发不走该口）。
using DaoHua.ComputeNode;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    // 请求体上限与契约口径对齐：MaxBatchBytes 是解码后字节上限，JSON+Base64 放大 4/3，
    // 取 1.5 倍覆盖信封冗余——合法批次不会被 413 误拒，超限批次仍在 Kestrel 层拦下。
    options.Limits.MaxRequestBodySize = ComputeContract.MaxBatchBytes * 3 / 2;
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
    // 反向拨入节点只需主动拨出，调试口默认只绑回环防误暴露；
    // 需要局域网可达（如 odin 健康探测）时显式配置 ComputeNode:Urls/DAOHUA_COMPUTE_URLS。
    builder.WebHost.UseUrls("http://127.0.0.1:8090");
}

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// 配表随 Domain 程序集内嵌装载：节点与主服跑同一 Domain.dll 时指纹天然同源，
// 无需文件系统配表目录，也不会出现部分文件缺失导致的指纹漂移。
builder.Services.AddSingleton(GameDataCatalog.LoadEmbedded());
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
// 注册为 DI 单例供调试口与 UpstreamLinkWorker 共用：两侧批次合计不超 MaxConcurrentBatches，
// 防止调试批次挤爆节点算力。
builder.Services.AddSingleton(new SemaphoreSlim(maxConcurrentBatches, maxConcurrentBatches));

// 反向拨入模式：配置上游地址列表后节点主动拨出 compute-link 链路进入主服动态节点组。
// ComputeNode:UpstreamUrls 支持配置节数组或分号分隔串（DAOHUA_COMPUTE_UPSTREAM_URLS 同口径）；
// 列表未配置时回退旧单值键 ComputeNode:UpstreamUrl/DAOHUA_COMPUTE_UPSTREAM_URL。
var upstreamUrls = ReadUpstreamUrls(builder.Configuration);
if (upstreamUrls.Count > 0)
{
    var nodeName = builder.Configuration["ComputeNode:NodeName"]
                   ?? Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_NODE_NAME")
                   ?? Environment.MachineName;
    builder.Services.AddHostedService(sp => new UpstreamLinkWorker(
        upstreamUrls, token, nodeName,
        sp.GetRequiredService<DeterministicBatchEngine>(),
        sp.GetRequiredService<GameDataCatalog>(),
        sp.GetRequiredService<ComputeMetrics>(),
        sp.GetRequiredService<SemaphoreSlim>(),
        maxJobs, maxConcurrentBatches,
        sp.GetRequiredService<ILoggerFactory>()));
}

var app = builder.Build();
var gameData = app.Services.GetRequiredService<GameDataCatalog>();
var metrics = app.Services.GetRequiredService<ComputeMetrics>();
var engine = app.Services.GetRequiredService<DeterministicBatchEngine>();
app.Logger.LogInformation(
    "ComputeNode 已装配配表 fingerprint={Fingerprint} runtime={Runtime} contract={Contract}",
    gameData.Fingerprint, NodeRuntime.Fingerprint, ComputeContract.Version);

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
    gameDataFingerprint = gameData.Fingerprint,
    runtimeFingerprint = NodeRuntime.Fingerprint,
    maxJobsPerBatch = maxJobs,
    maxBatchBytes = ComputeContract.MaxBatchBytes
}));

app.MapGet("/readyz", () => Results.Ok(new
{
    status = "ready",
    service = "daohua-compute-node",
    contractVersion = ComputeContract.Version,
    gameDataFingerprint = gameData.Fingerprint,
    runtimeFingerprint = NodeRuntime.Fingerprint,
    activeBatches = metrics.ActiveBatches,
    completedJobs = metrics.CompletedJobs,
    failedJobs = metrics.FailedJobs
}));

app.MapPost("/internal/v1/compute/advance-batch", async (
    HttpRequest request,
    AdvanceBatchRequest batch,
    SemaphoreSlim batchGate,
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
                Encoding.UTF8.GetBytes(gameData.Fingerprint)))
        {
            metrics.RecordFailedBatch();
            return Results.Conflict(new
            {
                code = "GAME_DATA_FINGERPRINT_MISMATCH",
                expected = gameData.Fingerprint,
                actual = batch.GameDataFingerprint,
                traceId = request.HttpContext.TraceIdentifier
            });
        }

        var results = new List<AdvanceJobResult>(batch.Jobs.Count);
        foreach (var job in batch.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = engine.Advance(job);
            results.Add(result);
            if (result.Success) metrics.RecordCompletedJob();
            else metrics.RecordFailedJob();
        }

        metrics.RecordCompletedBatch(Stopwatch.GetElapsedTime(started));
        return Results.Ok(new AdvanceBatchResponse(
            ComputeContract.Version,
            gameData.Fingerprint,
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

/// <summary>
/// 读取主服上游地址列表：ComputeNode:UpstreamUrls 的配置节数组、分号分隔串与
/// DAOHUA_COMPUTE_UPSTREAM_URLS（分号分隔）按序合并去重；三者皆空时回退旧单值键
/// ComputeNode:UpstreamUrl/DAOHUA_COMPUTE_UPSTREAM_URL（内测期保留可读，便于存量部署平滑过渡）。
/// </summary>
static List<string> ReadUpstreamUrls(IConfiguration configuration)
{
    var urls = new List<string>();
    void Add(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!urls.Contains(item, StringComparer.Ordinal)) urls.Add(item);
        }
    }

    Add(configuration["ComputeNode:UpstreamUrls"]);
    Add(Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_UPSTREAM_URLS"));
    foreach (var child in configuration.GetSection("ComputeNode:UpstreamUrls").GetChildren())
        Add(child.Value);
    if (urls.Count == 0)
    {
        Add(configuration["ComputeNode:UpstreamUrl"]);
        Add(Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_UPSTREAM_URL"));
    }
    return urls;
}
