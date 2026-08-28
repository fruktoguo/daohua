using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Protocol;
using Server.Clean;

var builder = WebApplication.CreateBuilder(args);
var processRole = ResolveProcessRole(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = ProtocolConstants.MaxFrameBytes;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
if (processRole == ServerProcessRole.PersistenceWorker
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    // 持久化 worker 不接受公网流量；保留本地健康端点便于容器探针使用。
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}
else if (processRole == ServerProcessRole.Api
         && string.IsNullOrWhiteSpace(builder.Configuration["Server:Urls"])
         && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
         && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5128");
}

builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiterOptions.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    rateLimiterOptions.AddPolicy("command", context => RateLimitPartition.GetTokenBucketLimiter(
        context.User.FindFirstValue("player_id") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
});

var options = ServerOptions.From(builder.Configuration) with { ProcessRole = processRole };
builder.Services.AddSingleton(options);
// schema 必须先于 Data Protection/认证服务解析，避免首次请求在建表 HostedService
// 之前触发 key ring 查询。worker 角色在 bootstrap 关闭时也会 fail-fast 校验版本。
var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
// 启动阶段数据库不可达时快速失败，避免容器探针长期卡在 starting 状态且无法自动恢复。
using (var bootstrapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    await PostgresSchemaBootstrap.EnsureAsync(dataSource, options.EnableSchemaBootstrap, bootstrapTimeout.Token);
builder.Services.AddSingleton(dataSource);

if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddSingleton(GameDataCatalog.LoadEmbedded());
    builder.Services.AddDaohuaPostgresDataProtection();
    builder.Services.AddSingleton<IAccountRepository, PostgresAccountRepository>();
    builder.Services.AddSingleton<IAssetBalanceRepository, PostgresAssetRepository>();
    builder.Services.AddSingleton<IEconomyTransactionStore, PostgresEconomyRepository>();
    builder.Services.AddSingleton<IPlayerOwnershipStore, PostgresPlayerOwnershipRepository>();
    builder.Services.AddSingleton<PostgresMarketRepository>();
    builder.Services.AddSingleton<IMarketTransactionStore>(sp => sp.GetRequiredService<PostgresMarketRepository>());
    builder.Services.AddSingleton<MarketRuntime>();
    builder.Services.AddSingleton<PlayerRuntime>();
    builder.Services.AddSingleton<SessionRegistry>();
    builder.Services.AddDaohuaAuthentication();
}

// 两种进程都需要 section/outbox 存储；worker 只装配这两个后台消费边界。
builder.Services.AddSingleton<IPlayerStateRepository, PostgresSectionRepository>();
builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
builder.Services.AddSingleton<ICommandReceiptStore, PostgresCommandReceiptRepository>();
builder.Services.AddSingleton<PostgresRetentionRepository>();
builder.Services.AddSingleton<IIntegrationEventDispatcher, UnconfiguredIntegrationEventDispatcher>();
builder.Services.AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddHealthChecks().AddCheck<MarketHealthCheck>("market", tags: ["ready"]);
}

builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<PostgresSchemaHostedService>();
builder.Services.AddHostedService<SectionFlushWorker>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<CommandReceiptCleanupWorker>();
builder.Services.AddHostedService<PostgresRetentionWorker>();
if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddHostedService<PlayerFlushHostedService>();
    builder.Services.AddHostedService<PlayerSimulationHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketRuntime>());
}

var app = builder.Build();
var releaseVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

app.UseExceptionHandler();
app.UseForwardedHeaders();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse
});
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
});

if (processRole == ServerProcessRole.Api)
{
    app.UseStaticFiles();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
    app.UseAuthentication();
    app.UseAuthorization();
    // 限流分区键需要读取认证后的 player_id；因此必须位于认证中间件之后。
    app.UseRateLimiter();

    app.MapGet("/api/v1/meta", (GameDataCatalog gameData) => Results.Ok(new
    {
        service = "daohua",
        version = releaseVersion,
        protocolVersion = ProtocolConstants.CurrentVersion,
        schemaVersion = PostgresSchema.Version,
        gameDataVersion = gameData.ContentVersion,
        gameDataFingerprint = gameData.Fingerprint
    }));

    var catalog = app.MapGroup("/api/v1/catalog");
    catalog.MapGet("/actions", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(gameData.Actions.All
            .OrderBy(action => action.Id.Value, StringComparer.Ordinal)
            .Select(action => new
            {
                id = action.Id.Value,
                name = action.Name,
                description = string.Empty,
                type = action.Kind.ToString().ToLowerInvariant(),
                durationSeconds = action.DurationSeconds,
                executionKind = action.Kind switch
                {
                    ActionKind.Combat => "combat-encounter",
                    ActionKind.Travel => "travel",
                    _ => "timed"
                },
                inputs = action.Inputs.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
                outputs = action.Outputs.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
                availableNodeIds = action.AllowedNodeIds?.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            }));
    });
    catalog.MapGet("/items", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(StaticGameCatalogMapper.Items(gameData));
    });
    catalog.MapGet("/equipment", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(StaticGameCatalogMapper.Equipments(gameData));
    });
    catalog.MapGet("/maps", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(StaticGameCatalogMapper.Maps(gameData));
    });
    catalog.MapGet("/encounters", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(StaticGameCatalogMapper.Encounters(gameData));
    });

    var auth = app.MapGroup("/api/v1/auth");
    auth.MapPost("/register", async (RegisterRequest request, IAccountRepository accounts, PlayerRuntime runtime, ServerOptions serverOptions, HttpContext context, CancellationToken ct) =>
    {
        var result = await accounts.RegisterAsync(request.Account, request.Password, request.DisplayName, ct);
        if (!result.Success)
            return Results.BadRequest(Problem(400, result.ErrorCode ?? "REGISTRATION_FAILED", result.Message ?? "注册失败。"));
        await runtime.LoadAsync(result.PlayerId!.Value, ct);
        await runtime.FlushAsync(result.PlayerId.Value, true, ct);
        StampSessionCookie(context, result.SessionToken!, serverOptions.SessionLifetime);
        return Results.Created($"/api/v1/players/{result.PlayerId:D}", new
        {
            result.AccountId,
            result.PlayerId,
            result.Account,
            result.DisplayName
        });
    }).RequireRateLimiting("auth");
    auth.MapPost("/login", async (LoginRequest request, IAccountRepository accounts, ServerOptions serverOptions, HttpContext context, CancellationToken ct) =>
    {
        var result = await accounts.LoginAsync(request.Account, request.Password, ct);
        if (!result.Success)
            return Results.Json(Problem(401, result.ErrorCode ?? "INVALID_CREDENTIALS", result.Message ?? "账号或密码错误。"), statusCode: 401);
        StampSessionCookie(context, result.SessionToken!, serverOptions.SessionLifetime);
        return Results.Ok(new { result.AccountId, result.PlayerId, result.Account, result.DisplayName });
    }).RequireRateLimiting("auth");
    auth.MapPost("/logout", async (IAccountRepository accounts, HttpContext context, CancellationToken ct) =>
    {
        if (context.Request.Cookies.TryGetValue(SessionAuthenticationDefaults.CookieName, out var token))
            await accounts.RevokeSessionAsync(token, ct);
        context.Response.Cookies.Delete(SessionAuthenticationDefaults.CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Results.NoContent();
    });
    auth.MapGet("/session", (ClaimsPrincipal user) =>
    {
        if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirstValue("player_id"), out var playerId))
            return Results.Json(Problem(401, "UNAUTHENTICATED", "当前没有有效会话。"), statusCode: 401);
        return Results.Ok(new
        {
            accountId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            account = user.FindFirstValue(ClaimTypes.Name),
            playerId,
            displayName = user.FindFirstValue("display_name")
        });
    }).RequireAuthorization();

    var players = app.MapGroup("/api/v1/players").RequireAuthorization();
    players.MapGet("/{playerId:guid}", async (Guid playerId, ClaimsPrincipal user, PlayerRuntime runtime, GameDataCatalog gameData, CancellationToken ct) =>
    {
        if (!IsPlayer(user, playerId)) return Results.Forbid();
        var snapshot = await runtime.LoadAsync(playerId, ct);
        return Results.Ok(ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData));
    });
    players.MapPost("/{playerId:guid}/commands", async (Guid playerId, CommandEnvelopeDto request, ClaimsPrincipal user, PlayerRuntime runtime, GameDataCatalog gameData, CancellationToken ct) =>
    {
        if (!IsPlayer(user, playerId)) return Results.Forbid();
        if (!Guid.TryParse(request.RequestId, out var requestId) || requestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Command)
            || request.Command.Length > 96 || request.ExpectedStateVersion < 0
            || request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Results.BadRequest(Problem(400, "INVALID_COMMAND", "命令信封无效。"));
        var result = await runtime.ExecuteAsync(playerId, request.Command, request.Payload, request.ExpectedStateVersion, requestId, ct);
        var dto = ToCommandResult(requestId, result, gameData);
        return result.Success
            ? Results.Ok(dto)
            : Results.Json(dto, statusCode: result.Code == "STATE_VERSION_CONFLICT" ? 409 : 400);
    }).RequireRateLimiting("command");
    players.MapGet("/{playerId:guid}/market/orders", (Guid playerId, ClaimsPrincipal user, MarketRuntime marketRuntime) =>
    {
        if (!IsPlayer(user, playerId)) return Results.Forbid();
        try
        {
            return Results.Ok(marketRuntime.GetOrders(new PlayerId(playerId), 100).Select(MarketDtoMapper.ToOrder));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(Problem(503, "MARKET_NOT_READY", exception.Message), statusCode: 503);
        }
    });

    var market = app.MapGroup("/api/v1/market");
    market.MapGet("/book", (string assetId, string? variant, int? depth, MarketRuntime marketRuntime) =>
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128)
            return Results.BadRequest(Problem(400, "INVALID_INSTRUMENT", "市场品种无效。"));
        var instrument = new MarketInstrument(new ItemId(assetId), variant ?? string.Empty);
        if (!marketRuntime.Owns(instrument)) return Results.Conflict(new { code = "WRONG_PARTITION", partition = marketRuntime.PartitionId });
        try
        {
            var snapshot = marketRuntime.GetBook(instrument, Math.Clamp(depth ?? 20, 1, 200));
            return Results.Ok(new MarketBookDto(
                snapshot.Instrument.ItemId.Value,
                string.IsNullOrEmpty(snapshot.Instrument.Variant) ? null : snapshot.Instrument.Variant,
                snapshot.Asks.Select(level => new MarketLevelDto(level.Price, level.Quantity, level.OrderCount)).ToArray(),
                snapshot.Bids.Select(level => new MarketLevelDto(level.Price, level.Quantity, level.OrderCount)).ToArray(),
                snapshot.Version,
                snapshot.CapturedAt));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(Problem(409, "MARKET_NOT_READY", exception.Message));
        }
    });
    market.MapGet("/metrics", (ClaimsPrincipal user, MarketRuntime marketRuntime) =>
    {
        if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        return Results.Ok(marketRuntime.GetMetrics());
    }).RequireAuthorization();
    market.MapPost("/orders", async (MarketOrderRequestDto request, ClaimsPrincipal user, MarketRuntime marketRuntime, CancellationToken ct) =>
    {
        if (!Guid.TryParse(user.FindFirstValue("player_id"), out var playerId)
            || !Guid.TryParse(request.OrderId, out var orderId) || orderId == Guid.Empty)
            return Results.BadRequest(Problem(400, "INVALID_ORDER", "订单标识无效。"));
        if (!Guid.TryParse(request.RequestId, out var requestId) || requestId == Guid.Empty)
            return Results.BadRequest(Problem(400, "INVALID_REQUEST_ID", "请求 ID 无效。"));
        if (!Enum.TryParse<MarketSide>(request.Side, true, out var side))
            return Results.BadRequest(Problem(400, "INVALID_SIDE", "订单方向无效。"));
        var instrument = new MarketInstrument(new ItemId(request.AssetId), request.Variant ?? string.Empty);
        try
        {
            var result = await marketRuntime.PlaceAsync(new PlaceOrderRequest(
                new OrderId(orderId),
                new PlayerId(playerId),
                instrument,
                side,
                request.Price,
                request.Quantity,
                DateTimeOffset.UtcNow,
                new IdempotencyKey($"market:{playerId:D}:{orderId:D}")), ct);
            return Results.Ok(MarketDtoMapper.ToPlacement(result));
        }
        catch (DomainException exception) { return Results.BadRequest(Problem(400, exception.Code.ToString().ToUpperInvariant(), exception.Message)); }
        catch (EconomyInsufficientBalanceException) { return Results.Conflict(Problem(409, "INSUFFICIENT_BALANCE", "资产余额不足。")); }
        catch (MarketLeaseLostException exception) { return Results.Conflict(Problem(409, "MARKET_LEASE_LOST", exception.Message)); }
        catch (InvalidOperationException exception) { return Results.Conflict(Problem(409, "MARKET_NOT_READY", exception.Message)); }
    }).RequireAuthorization().RequireRateLimiting("command");
    market.MapDelete("/orders/{orderId:guid}", async (Guid orderId, string assetId, string? variant, ClaimsPrincipal user, MarketRuntime marketRuntime, CancellationToken ct) =>
    {
        if (!Guid.TryParse(user.FindFirstValue("player_id"), out var playerId)) return Results.Unauthorized();
        var instrument = new MarketInstrument(new ItemId(assetId), variant ?? string.Empty);
        try
        {
            var result = await marketRuntime.CancelAsync(new OrderId(orderId), new PlayerId(playerId), instrument, $"market-cancel:{playerId:D}:{orderId:D}", ct);
            return Results.Ok(MarketDtoMapper.ToCancellation(result));
        }
        catch (DomainException exception) { return Results.BadRequest(Problem(400, exception.Code.ToString().ToUpperInvariant(), exception.Message)); }
        catch (EconomyInsufficientBalanceException) { return Results.Conflict(Problem(409, "BALANCE_UPDATE_FAILED", "资产退款未能完成。")); }
        catch (MarketLeaseLostException exception) { return Results.Conflict(Problem(409, "MARKET_LEASE_LOST", exception.Message)); }
        catch (InvalidOperationException exception) { return Results.Conflict(Problem(409, "MARKET_NOT_READY", exception.Message)); }
    }).RequireAuthorization().RequireRateLimiting("command");

    app.MapGet("/api/v1/metrics", (ClaimsPrincipal user, PlayerRuntime playerRuntime, SessionRegistry sessions, MarketRuntime marketRuntime) =>
    {
        if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        return Results.Ok(new
        {
            loadedPlayers = playerRuntime.LoadedCount,
            sessions = sessions.Count,
            market = marketRuntime.GetMetrics()
        });
    }).RequireAuthorization();
    app.Map("/ws", WebSocketEndpoint.HandleAsync);
    app.MapFallback("/api/{**path}", () => Results.NotFound(Problem(404, "NOT_FOUND", "接口不存在。")));
    app.MapFallbackToFile("index.html");
}

app.Run();

static ServerProcessRole ResolveProcessRole(string[] args)
{
    var raw = Environment.GetEnvironmentVariable("DAOHUA_PROCESS_ROLE");
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], "--role", StringComparison.OrdinalIgnoreCase)) raw = args[index + 1];
    }
    return raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "api" => ServerProcessRole.Api,
        "persistence-worker" => ServerProcessRole.PersistenceWorker,
        _ => throw new InvalidOperationException("DAOHUA_PROCESS_ROLE/--role 只支持 api 或 persistence-worker。")
    };
}

static bool IsPlayer(ClaimsPrincipal principal, Guid playerId) =>
    Guid.TryParse(principal.FindFirstValue("player_id"), out var current) && current == playerId;

static ProblemDetails Problem(int status, string title, string detail) => new()
{
    Status = status,
    Title = title,
    Detail = detail
};

static CommandResultDto ToCommandResult(Guid requestId, PlayerCommandResult result, GameDataCatalog gameData) =>
    new(
        requestId.ToString("D"),
        result.Success,
        result.Code,
        result.Message,
        result.Snapshot.StateVersion,
        JsonSerializer.SerializeToElement(ProtocolSnapshotMapper.ToSnapshot(result.Snapshot, gameData.Fingerprint, gameData), ProtocolJson.Options));

static void StampSessionCookie(HttpContext context, string token, TimeSpan lifetime)
{
    context.Response.Cookies.Append(SessionAuthenticationDefaults.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = lifetime,
        Path = "/"
    });
}

static void ApplyCatalogHeaders(HttpContext context, string fingerprint)
{
    context.Response.Headers.ETag = $"\"{fingerprint}\"";
    context.Response.Headers.CacheControl = "public,max-age=300,stale-while-revalidate=60";
}

static async Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    context.Response.Headers.Pragma = "no-cache";
    var status = report.Status == HealthStatus.Healthy ? "ok" : report.Status == HealthStatus.Degraded ? "degraded" : "unhealthy";
    await JsonSerializer.SerializeAsync(context.Response.Body, new
    {
        status,
        service = "daohua",
        checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString().ToLowerInvariant(), StringComparer.Ordinal)
    }, ProtocolJson.Options, context.RequestAborted);
}

public sealed record RegisterRequest(string Account, string Password, string? DisplayName = null);
public sealed record LoginRequest(string Account, string Password);
public partial class Program;
