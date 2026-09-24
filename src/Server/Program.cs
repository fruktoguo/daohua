using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Protocol;
using Server.Clean;
using Server.Clean.Ops;

var builder = WebApplication.CreateBuilder(args);
var processRole = ResolveProcessRole(args);
var startedAt = DateTimeOffset.UtcNow;
var opsOptions = OpsOptions.From(builder.Configuration);
// 运维面板严格限定 Development + API 角色：生产环境即便配置误开也不装配。
var opsPanelEnabled = opsOptions.Enabled && processRole == ServerProcessRole.Api && builder.Environment.IsDevelopment();
OpsLogBuffer? opsLogBuffer = null;
if (opsPanelEnabled)
{
    opsLogBuffer = new OpsLogBuffer(opsOptions.LogBufferCapacity);
    builder.Logging.AddProvider(opsLogBuffer);
}

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
else if (processRole == ServerProcessRole.Api)
{
    var explicitUrls = builder.Configuration["Server:Urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var hasExplicitPorts = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    if (string.IsNullOrWhiteSpace(explicitUrls) && !hasExplicitPorts)
    {
        // 未显式配置时绑定本地回环默认端口；面板启用则同时绑定独立运维端口。
        var urls = new List<string> { "http://127.0.0.1:5128" };
        if (opsPanelEnabled) urls.Add(opsOptions.Url);
        builder.WebHost.UseUrls([.. urls]);
    }
    else if (opsPanelEnabled && !string.IsNullOrWhiteSpace(explicitUrls))
    {
        // 已显式配置主端口：保留原地址并追加运维端口。
        var urls = explicitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        urls.Add(opsOptions.Url);
        builder.WebHost.UseUrls([.. urls]);
    }
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
    // 传音与社交：不属于玩家聚合，独立单例运行时；聊天记录落 PostgreSQL（每作用域保留 100 条）。
    builder.Services.AddSingleton<Server.Clean.Social.OnlinePresenceRegistry>();
    builder.Services.AddSingleton<Server.Clean.Social.ISocialRepository, Server.Clean.Social.PostgresSocialRepository>();
    builder.Services.AddSingleton<Server.Clean.Social.IChatRepository, Server.Clean.Social.PostgresChatRepository>();
    builder.Services.AddSingleton<Server.Clean.Social.SocialRuntime>();
    builder.Services.AddSingleton<Server.Clean.Social.ChatRuntime>();
    builder.Services.AddSingleton(options.AntiMultiOpen);
    builder.Services.AddSingleton<Server.Clean.AntiMultiOpen.DeviceQuotaRegistry>();
    builder.Services.AddSingleton<Server.Clean.AntiMultiOpen.AntiMultiOpenAdmission>();
    builder.Services.AddDaohuaAuthentication();
    if (opsLogBuffer is not null) builder.Services.AddSingleton(opsLogBuffer);
}

// 两种进程都需要 section/outbox 存储；worker 只装配这两个后台消费边界。
builder.Services.AddSingleton<PostgresSectionRepository>();
builder.Services.AddSingleton<IPlayerStateRepository>(sp => sp.GetRequiredService<PostgresSectionRepository>());
builder.Services.AddSingleton<PostgresNodeBuildingRepository>();
builder.Services.AddSingleton<NodeBuildingRegistry>();
builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
builder.Services.AddSingleton<ICommandReceiptStore, PostgresCommandReceiptRepository>();
builder.Services.AddSingleton<PostgresRetentionRepository>();
builder.Services.AddSingleton<IIntegrationEventDispatcher, AuditIntegrationEventDispatcher>();
// 冷挂机派发链仅 Api 角色装配：/internal/compute-link 只在 Api map，PersistenceWorker
// 装配注册表只会空转，ColdCatchUpWorker 还会与 Api 重复生产任务。
if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddComputeDispatch();
}
builder.Services.AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddHealthChecks().AddCheck<MarketHealthCheck>("market", tags: ["ready"]);
    // compute-link 不挂 ready 标签：节点未接入是容量事实而非服务不就绪，避免干扰 /readyz；
    // 运维面板 /api/health 全量执行所有检查，节点组健康在面板侧可见。
    builder.Services.AddHealthChecks().AddCheck<ComputeLinkHealthCheck>("compute-link");
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
// 运维面板端口门禁必须早于静态文件与路由：非运维端口访问面板路径一律 404。
if (opsPanelEnabled) app.UseOpsGate(opsOptions);

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = (context, report) => WriteHealthResponse(context, report, releaseVersion)
});
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = (context, report) => WriteHealthResponse(context, report, releaseVersion)
});

if (processRole == ServerProcessRole.Api)
{
    // 生产镜像把前端构建产物拷入 wwwroot；本地开发由 Vite 提供前端，wwwroot 不存在。
    // 仅在静态站点根存在时装配静态文件与 SPA 回退，避免开发态刷出 WebRootPath 告警。
    var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var hasWebRoot = Directory.Exists(webRootPath);
    if (hasWebRoot) app.UseStaticFiles();
    // 全局 WS 心跳口径：20s 主动 ping、60s 无回应判定半开断开。
    // 同时作用于 /ws 玩家连接与 /internal/compute-link 节点链路：
    // 60s 容忍窗足以覆盖弱网抖动，又能让 NAT 后半开的计算链路在分钟级内被回收。
    app.UseWebSockets(new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20),
        KeepAliveTimeout = TimeSpan.FromSeconds(60)
    });
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
    catalog.MapGet("/abilities", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        var result = gameData.Abilities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToAbilityDto(pair.Key, pair.Value)).ToArray();
        return Results.Ok(result);
    });
    catalog.MapGet("/base-attributes", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(gameData.BaseAttributes!.Anchors);
    });
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
                executionKind = action.ExecutionKind switch
                {
                    ActionExecutionKind.CombatEncounter => "combat-encounter",
                    ActionExecutionKind.Travel => "travel",
                    _ => "timed"
                },
                combatEncounterId = action.CombatEncounterId,
                hiddenFromCatalog = action.HiddenFromCatalog,
                inputs = action.Inputs.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
                yields = action.Yields.Select(entry => new
                {
                    kind = entry.Kind.ToString().ToLowerInvariant(),
                    per = entry.Period == ActionYieldPeriod.PerRepeat ? "repeat" : "second",
                    amount = entry.Amount,
                    id = entry.Item?.Value ?? entry.Buff?.BuffId,
                    name = entry.Buff?.Name,
                    durationSeconds = entry.Buff?.DurationSeconds,
                    // Buff 修饰器唯一形状：领域授权字段 + statRatios 通用比率字典。
                    modifiers = entry.Buff is null ? null : new
                    {
                        hpRegen = entry.Buff.HpRegen,
                        mpRegen = entry.Buff.MpRegen,
                        attackSpeedPoints = entry.Buff.AttackSpeedPoints,
                        damageAbsorptionPoints = entry.Buff.DamageAbsorptionPoints,
                        statRatios = entry.Buff.StatRatios
                    }
                }).ToArray(),
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
    catalog.MapGet("/buildings", (HttpContext context, GameDataCatalog gameData) =>
    {
        ApplyCatalogHeaders(context, gameData.Fingerprint);
        return Results.Ok(StaticGameCatalogMapper.Buildings(gameData));
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
        PlayerCommandResult result;
        try
        {
            result = await runtime.ExecuteAsync(playerId, request.Command, request.Payload, request.ExpectedStateVersion, requestId, ct);
        }
        catch (ObjectDisposedException)
        {
            // actor 已随 WebSocket 会话销毁：把释放竞态映射为明确领域错误，而不是 500。
            return Results.Json(Problem(409, "SESSION_CLOSED", "玩家会话已关闭，请重新连接。"), statusCode: 409);
        }
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
    // 计算节点反向拨入链路：节点主动 WebSocket 注册，主服经连接下发冷挂机批次。
    app.Map(Server.Clean.ComputeLinkContract.Path, Server.Clean.ComputeLinkEndpoint.HandleAsync);
    if (opsPanelEnabled) app.MapOpsPanel(opsOptions, releaseVersion, startedAt);
    app.MapFallback("/api/{**path}", () => Results.NotFound(Problem(404, "NOT_FOUND", "接口不存在。")));
    if (hasWebRoot) app.MapFallbackToFile("index.html");
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

// HTTP 命令回执无会话推送基线可借用，固定全量快照形态；前端按 snapshotForm 分派合并。
static CommandResultDto ToCommandResult(Guid requestId, PlayerCommandResult result, GameDataCatalog gameData) =>
    new(
        requestId.ToString("D"),
        result.Success,
        result.Code,
        result.Message,
        result.Snapshot.StateVersion,
        CommandResultSnapshotForms.Snapshot,
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

static object ToAbilityDto(string id, JsonElement raw)
{
    var node = JsonNode.Parse(raw.GetRawText())!.AsObject();
    node["abilityId"] = id;
    if (node["style"] is JsonValue style)
        node["style"] = style.GetValue<string>() switch { "fiveElements" => "five-elements", _ => style.GetValue<string>() };
    node["manaCost"] ??= 0;
    node["cooldownMilliseconds"] ??= 0;
    node["castMilliseconds"] ??= 0;
    node["flatDamage"] ??= 0;
    node["damageRatio"] ??= 0;
    if (node["targetType"] is JsonValue target)
        node["targetType"] = target.GetValue<string>() switch { "allEnemies" => "all-enemies", "allAllies" => "all-allies", "lowestHitpointsAlly" => "lowest-hp-ally", "self" => "self", _ => "enemy" };
    return node;
}

static async Task WriteHealthResponse(HttpContext context, HealthReport report, string version)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    context.Response.Headers.Pragma = "no-cache";
    var status = report.Status == HealthStatus.Healthy ? "ok" : report.Status == HealthStatus.Degraded ? "degraded" : "unhealthy";
    await JsonSerializer.SerializeAsync(context.Response.Body, new
    {
        status,
        service = "daohua",
        version,
        checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString().ToLowerInvariant(), StringComparer.Ordinal)
    }, ProtocolJson.Options, context.RequestAborted);
}

/// <summary>注册请求体。DisplayName 缺省时服务端回退使用账号名作为初始道号。</summary>
/// <param name="Account">登录账号（3-32 位字母、数字或下划线，大小写不敏感）。</param>
/// <param name="Password">明文密码，仅在传输层出现；服务端 PBKDF2 哈希后入库。</param>
/// <param name="DisplayName">可选初始道号。</param>
public sealed record RegisterRequest(string Account, string Password, string? DisplayName = null);
/// <summary>登录请求体。</summary>
/// <param name="Account">登录账号。</param>
/// <param name="Password">明文密码，仅用于与服务端哈希比对。</param>
public sealed record LoginRequest(string Account, string Password);
/// <summary>顶级语句入口的 Program 类型占位，供测试项目引用入口程序集。</summary>
public partial class Program;
