using System.Text.Json;
using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;

var builder = WebApplication.CreateBuilder(args);
var configuredGameDataDirectory = builder.Configuration["GameData:Directory"];
var gameData = string.IsNullOrWhiteSpace(configuredGameDataDirectory)
    ? DaoxuanGameData.Current
    : DaoxuanGameData.LoadFromDirectory(ResolveGameDataPath(builder.Environment.ContentRootPath, configuredGameDataDirectory));
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = false
};

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

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "daoxuan-compute-node",
    gameDataVersion = gameData.ContentVersion,
    gameDataFingerprint = gameData.Fingerprint
}));

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

bool IsTokenValid(HttpRequest request, string? configuredToken)
{
    if (string.IsNullOrWhiteSpace(configuredToken)) return true;
    var supplied = request.Headers["X-Compute-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(supplied)
        && CryptographicEquals(supplied, configuredToken);
}

bool CryptographicEquals(string left, string right)
{
    var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
    var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

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
        var state = JsonSerializer.Deserialize<DaoxuanPlayerState>(payload, jsonOptions);
        if (state is null || state.PlayerId != job.PlayerId)
        {
            return AdvanceJobResult.Fail(job.JobId, job.PlayerId, "INVALID_SNAPSHOT", "玩家快照为空或玩家 ID 不匹配。");
        }

        DaoxuanSimulation.NormalizeState(state);
        var report = DaoxuanSimulation.AdvanceTo(state, target);
        state.PendingEvents.Clear();
        state.DirtySections = DaoxuanDirtySections.None;
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

public sealed record AdvanceBatchRequest(IReadOnlyList<AdvanceJobRequest> Jobs);

public sealed record AdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    string PayloadBase64,
    long TargetUnixMilliseconds);

public sealed record AdvanceBatchResponse(
    IReadOnlyList<AdvanceJobResult> Jobs,
    string GameDataFingerprint);

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
