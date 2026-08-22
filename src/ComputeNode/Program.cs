// ==============================================================================
// 文件名称: Program.cs
// 主要职责: 分布式冷挂机确定性计算节点的应用程序入口与轻量 HTTP 宿主。
// 核心逻辑 / 定义内容:
//   1. 初始化 ASP.NET Core Minimal API 宿主并加载只读游戏配置数据 (GameData)。
//   2. 暴露 /healthz 健康检查端点，报告计算节点状态、配置版本与数据指纹。
//   3. 暴露 /internal/compute/advance-batch 内部批处理接口，安全校验 Token 后执行确定性离线模拟 (SimulationEngine.AdvanceTo)。
//   4. 采用无状态纯计算设计，不直连生产数据库、不持有在线玩家权威状态。
// ==============================================================================

using System.Text.Json;
using Domain.GameData;
using Domain.Models;
using Domain.Simulation;

var builder = WebApplication.CreateBuilder(args);

// 加载游戏配置数据与指纹校验
var configuredGameDataDirectory = builder.Configuration["GameData:Directory"];
var gameData = string.IsNullOrWhiteSpace(configuredGameDataDirectory)
    ? GameDataCatalog.Current
    : GameDataCatalog.LoadFromDirectory(ResolveGameDataPath(builder.Environment.ContentRootPath, configuredGameDataDirectory));
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = false
};

// 配置服务监听端点（默认 http://127.0.0.1:5129）
var configuredUrls = builder.Configuration["ComputeNode:Urls"];
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls(configuredUrls);
}
else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5129");
}

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = builder.Build();
var token = builder.Configuration["ComputeNode:Token"];
if (string.IsNullOrWhiteSpace(token))
{
    app.Logger.LogWarning("未配置 ComputeNode:Token，计算节点只建议在本机开发使用");
}

/// <summary>
/// 计算节点健康检查端点（返回当前加载的游戏数据版本与指纹）。
/// </summary>
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "daohua-compute-node",
    gameDataVersion = gameData.ContentVersion,
    gameDataFingerprint = gameData.Fingerprint
}));

/// <summary>
/// 内部批处理冷计算端点：校验 Token、反序列化玩家快照、执行确定性时间快进并返回新快照。
/// </summary>
app.MapPost("/internal/compute/advance-batch", (
    HttpRequest httpRequest,
    AdvanceBatchRequest request,
    CancellationToken cancellationToken) =>
{
    if (!IsTokenValid(httpRequest, token))
    {
        return Results.Json(
            new { code = "UNAUTHORIZED", message = "缺少或错误的计算节点令牌。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (request.Jobs is null || request.Jobs.Count == 0 || request.Jobs.Count > 256)
    {
        return Results.BadRequest(new { code = "INVALID_BATCH", message = "批次必须包含 1 到 256 个任务。" });
    }

    var results = new List<AdvanceJobResult>(request.Jobs.Count);
    foreach (var job in request.Jobs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        results.Add(AdvanceJob(job));
    }

    return Results.Ok(new AdvanceBatchResponse(results, gameData.Fingerprint));
});

app.Run();

/// <summary>
/// 探测并解析游戏数据目录物理路径。
/// </summary>
string ResolveGameDataPath(string contentRoot, string configuredPath)
{
    if (Path.IsPathRooted(configuredPath)) return configuredPath;
    var candidates = new[]
    {
        Path.Combine(contentRoot, configuredPath),
        Path.Combine(contentRoot, "../../", configuredPath),
        Path.Combine(Directory.GetCurrentDirectory(), configuredPath),
        Path.Combine(AppContext.BaseDirectory, configuredPath)
    };
    foreach (var candidate in candidates)
    {
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "manifest.json")))
        {
            return Path.GetFullPath(candidate);
        }
    }
    return Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
}

/// <summary>
/// 校验请求头中的 X-Compute-Token。
/// </summary>
bool IsTokenValid(HttpRequest request, string? configuredToken)
{
    if (string.IsNullOrWhiteSpace(configuredToken)) return true;
    var supplied = request.Headers["X-Compute-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(supplied)
        && CryptographicEquals(supplied, configuredToken);
}

/// <summary>
/// 恒定时间比较两个字符串，防止时序侧信道攻击。
/// </summary>
bool CryptographicEquals(string left, string right)
{
    var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
    var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

/// <summary>
/// 执行单个玩家快照的确定性模拟快进。
/// </summary>
AdvanceJobResult AdvanceJob(AdvanceJobRequest job)
{
    if (job.JobId == Guid.Empty || job.PlayerId == Guid.Empty || string.IsNullOrWhiteSpace(job.PayloadBase64))
    {
        return AdvanceJobResult.Fail(job.JobId, job.PlayerId, "INVALID_JOB", "计算任务缺少必要字段。");
    }

    try
    {
        var target = DateTimeOffset.FromUnixTimeMilliseconds(job.TargetUnixMilliseconds);
        var payload = Convert.FromBase64String(job.PayloadBase64);
        var state = JsonSerializer.Deserialize<PlayerState>(payload, jsonOptions);
        if (state is null || state.PlayerId != job.PlayerId)
        {
            return AdvanceJobResult.Fail(job.JobId, job.PlayerId, "INVALID_SNAPSHOT", "玩家快照为空或玩家 ID 不匹配。");
        }

        SimulationEngine.NormalizeState(state);
        var report = SimulationEngine.AdvanceTo(state, target);
        state.PendingEvents.Clear();
        state.DirtySections = DirtySections.None;
        var advancedPayload = JsonSerializer.SerializeToUtf8Bytes(state, jsonOptions);
        return AdvanceJobResult.Ok(
            job.JobId,
            job.PlayerId,
            Convert.ToBase64String(advancedPayload),
            report.CompletedActions,
            report.TransitionCount,
            report.CombatEvents);
    }
    catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or ArgumentException)
    {
        return AdvanceJobResult.Fail(job.JobId, job.PlayerId, "ADVANCE_FAILED", exception.Message);
    }
}

/// <summary>
/// 批处理任务请求。
/// </summary>
public sealed record AdvanceBatchRequest(IReadOnlyList<AdvanceJobRequest> Jobs);

/// <summary>
/// 单个玩家快照计算任务。
/// </summary>
public sealed record AdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    string PayloadBase64,
    long TargetUnixMilliseconds);

/// <summary>
/// 批处理任务响应。
/// </summary>
public sealed record AdvanceBatchResponse(
    IReadOnlyList<AdvanceJobResult> Jobs,
    string GameDataFingerprint);

/// <summary>
/// 单个玩家快照计算结果。
/// </summary>
public sealed record AdvanceJobResult(
    Guid JobId,
    Guid PlayerId,
    bool Success,
    string? PayloadBase64,
    int CompletedActions,
    int Transitions,
    int CombatEvents,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static AdvanceJobResult Ok(
        Guid jobId,
        Guid playerId,
        string payloadBase64,
        int completedActions,
        int transitions,
        int combatEvents) =>
        new(jobId, playerId, true, payloadBase64, completedActions, transitions, combatEvents, null, null);

    public static AdvanceJobResult Fail(Guid jobId, Guid playerId, string errorCode, string errorMessage) =>
        new(jobId, playerId, false, null, 0, 0, 0, errorCode, errorMessage);
}
