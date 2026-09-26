using System.Globalization;
using System.Net;

namespace Server.Clean;

/// <summary>三种实验宿主共用的显式开发边界，启动校验在数据库连接和后台服务之前执行。</summary>
public sealed record BenchmarkHost(string Mode, string Variant, bool ObservabilityEnabled)
{
    public bool IsReplay => Mode == "replay";

    public static BenchmarkHost? FromEnvironment(IHostEnvironment environment, ServerOptions options)
    {
        var mode = Environment.GetEnvironmentVariable("DAOHUA_BENCH_MODE")?.Trim().ToLowerInvariant();
        var target = Environment.GetEnvironmentVariable("DAOHUA_BENCH_SIMULATION_TARGET");
        if (string.IsNullOrEmpty(mode))
        {
            if (!string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("固定模拟时钟要求显式基准 replay 模式。");
            return null;
        }
        if (!environment.IsDevelopment() || options.ProcessRole != ServerProcessRole.Api)
            throw new InvalidOperationException("基准模式仅允许 Development API 宿主。");
        if (mode is not ("replay" or "online")) throw new InvalidOperationException("基准模式只允许 replay 或 online。");
        var variant = Environment.GetEnvironmentVariable("DAOHUA_BENCH_VARIANT");
        var database = variant switch
        {
            "oop" => "daohua_bench_oop",
            "ecs-current" => "daohua_bench_ecs_current",
            "ecs-optimized" => "daohua_bench_ecs_optimized",
            _ => throw new InvalidOperationException("基准 variant 必须为 oop、ecs-current 或 ecs-optimized。")
        };
        var connection = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        if (connection.Database != database || connection.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("基准模式只允许本机 PostgreSQL 的对应实验数据库。");
        if (mode == "replay" && !DateTimeOffset.TryParse(target, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _))
            throw new InvalidOperationException("replay 模式必须提供有效 DAOHUA_BENCH_SIMULATION_TARGET。");
        if (mode == "online" && !string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("online 模式使用系统业务时钟，不能设置固定模拟时间。");
        var observability = Environment.GetEnvironmentVariable("DAOHUA_BENCH_OBSERVABILITY") ?? "true";
        if (!bool.TryParse(observability, out var enabled))
            throw new InvalidOperationException("DAOHUA_BENCH_OBSERVABILITY 只允许 true 或 false。");
        return new(mode, variant!, enabled);
    }

    public void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/debug/bench");
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext;
            if (request.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || request.Request.Headers.ContainsKey("Origin")
                || request.Request.Headers.ContainsKey("Forwarded")
                || request.Request.Headers.ContainsKey("X-Forwarded-For")
                || request.Request.Headers.ContainsKey("X-Original-For")
                || request.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
                return Results.NotFound();
            request.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapPost("/session", async (BenchmarkSessionRequest request, NpgsqlDataSource dataSource,
            ServerOptions options, HttpContext context, CancellationToken ct) =>
        {
            if (request.PlayerId == Guid.Empty) return Results.BadRequest();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("""
                INSERT INTO identity.auth_sessions(session_id, account_id, token_hash, issued_at, expires_at, last_used_at)
                SELECT $1, a.account_id, $2, $3, $4, $3
                FROM identity.accounts a JOIN identity.players p ON p.account_id=a.account_id
                WHERE p.player_id=$5 AND a.status=1 AND p.status=1 AND starts_with(a.login_normalized, 'bench_')
                """, connection);
            var now = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(now.Add(options.SessionLifetime));
            command.Parameters.AddWithValue(request.PlayerId);
            if (await command.ExecuteNonQueryAsync(ct) != 1) return Results.NotFound();
            context.Response.Cookies.Append(SessionAuthenticationDefaults.CookieName, token, new CookieOptions
            {
                HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
                IsEssential = true, MaxAge = options.SessionLifetime, Path = "/"
            });
            return Results.Ok(new { playerId = request.PlayerId });
        });

        group.MapPost("/simulation-clock", async (JsonElement request, IPlayerSimulationClock clock,
            IPlayerRuntime runtime, CancellationToken ct) =>
        {
            if (!IsReplay) return Results.Conflict(new { code = "BENCH_CLOCK_DISABLED" });
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("targetTime", out var element)
                || !element.TryGetDateTimeOffset(out var target) || target < clock.UtcNow)
                return Results.BadRequest(new { code = "INVALID_TARGET_TIME" });
            clock.AdvanceTo(target);
            var advance = !request.TryGetProperty("advance", out var flag) || flag.ValueKind != JsonValueKind.False;
            if (advance) await runtime.AdvanceAllAsync(clock.UtcNow, ct);
            return Results.Ok(new { targetTime = clock.UtcNow, loadedPlayers = runtime.LoadedCount, advanced = advance });
        }).RequireAuthorization();

        group.MapPost("/flush", async (IPlayerRuntime runtime, PostgresSectionRepository repository,
            ServerOptions options, CancellationToken ct) =>
        {
            await runtime.FlushAllAsync(true, ct);
            var batches = 0;
            var applied = 0;
            var failed = 0;
            var idlePolls = 0;
            while (idlePolls < 4)
            {
                var result = await repository.FlushPendingNowAsync(options.SectionFlushBatchSize, options.SectionFlushLease, ct);
                batches++;
                applied += result.Applied;
                failed += result.Failed;
                if (result.Failed > 0)
                    throw new InvalidOperationException($"基准强制刷盘失败：{failed} 行。");
                if (result.Claimed == 0)
                {
                    idlePolls++;
                    if (idlePolls < 4) await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                }
                else
                {
                    idlePolls = 0;
                }
            }

            return Results.Ok(new { flushed = true, loadedPlayers = runtime.LoadedCount, batches, applied });
        }).RequireAuthorization();

        group.MapGet("/metrics", (IPlayerRuntime runtime, BenchmarkTelemetry telemetry, SessionRegistry sessions, IPlayerSimulationClock clock) =>
        {
            var snapshot = telemetry.Snapshot();
            return Results.Ok(new
            {
                processId = Environment.ProcessId,
                benchmarkMode = Mode, variant = Variant, simulationUtcNow = clock.UtcNow,
                cpuMs = snapshot.CpuMilliseconds, rssBytes = snapshot.RssBytes,
                allocatedBytes = snapshot.AllocatedBytes, gc0 = snapshot.Gc0, gc1 = snapshot.Gc1, gc2 = snapshot.Gc2,
                gcPauseMs = snapshot.GcPauseMilliseconds,
                databaseRequests = snapshot.NpgsqlRequestCount, databaseMs = snapshot.NpgsqlDurationMilliseconds,
                databaseBytesSent = snapshot.NpgsqlBytesSent, databaseBytesReceived = snapshot.NpgsqlBytesReceived,
                loadedPlayers = runtime.LoadedCount, sessions = sessions.Count,
                observabilityEnabled = snapshot.ObservabilityEnabled,
                instrumentationListenerMs = snapshot.InstrumentationListenerMilliseconds,
                histogramUpperBoundsMs = BenchmarkTelemetry.HistogramUpperBoundsMs,
                measurements = snapshot.Measurements, counters = snapshot.Counters
            });
        }).RequireAuthorization();
    }
}

public sealed record BenchmarkSessionRequest(Guid PlayerId);
