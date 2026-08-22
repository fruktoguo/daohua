// ============================================================================
// Daoxuan.Server 宿主启动入口 (Program.cs)
// 职责：
// 1. 初始化 WebApplication 宿主环境，加载游戏数据与静态配置。
// 2. 装配依赖注入拓扑（数据库、存储适配器、市场引擎、模拟时钟、持久化队列与后台服务）。
// 3. 配置 HTTP 中间件（CORS、静态文件、WebSocket 协议升级、HTTP 缓存）。
// 4. 注册 RESTful API 路由（健康检查、图鉴配置、账号鉴权、市场交易、监控指标）与 WebSocket 入口。
// ============================================================================

using System.Reflection;
using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Daoxuan.Protocol;
using Daoxuan.Server.Infrastructure;
using Daoxuan.Server.Runtime;
using YuoCore.Persistence;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ----------------------------------------------------------------------------
// 1. 游戏数据与配置加载
// ----------------------------------------------------------------------------
var configuredGameDataDirectory = builder.Configuration["GameData:Directory"];
var gameData = string.IsNullOrWhiteSpace(configuredGameDataDirectory)
    ? DaoxuanGameData.Current
    : DaoxuanGameData.LoadFromDirectory(ResolveGameDataPath(builder.Environment.ContentRootPath, configuredGameDataDirectory));

/// <summary>
/// 解析游戏数据目录路径，按优先级从 ContentRoot、上级目录、工作目录与程序基目录查找 manifest.json。
/// </summary>
static string ResolveGameDataPath(string contentRoot, string configuredPath)
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
// ----------------------------------------------------------------------------
// 2. 宿主网络绑定与 Kestrel 配置
// ----------------------------------------------------------------------------
var configuredServerUrls = builder.Configuration["Server:Urls"];
var hasStandardAspNetBinding =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"))
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS"));
if (!string.IsNullOrWhiteSpace(configuredServerUrls))
{
    builder.WebHost.UseUrls(configuredServerUrls);
}
else if (!hasStandardAspNetBinding)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5128");
}
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
// 连接排空需要有明确上限；持久化服务自身另有 60 秒 WAL drain 预算，不依赖这里的 token。
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Cors.Infrastructure.CorsService", LogLevel.Warning);

// ----------------------------------------------------------------------------
// 3. 跨域安全策略 (CORS)
// ----------------------------------------------------------------------------
var corsOrigins = builder.Configuration
    .GetSection("Server:CorsOrigins")
    .Get<string[]>()?
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Where(origin => origin.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray() ?? [];
if (corsOrigins.Any(origin =>
        !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || uri.Scheme is not ("http" or "https")))
{
    throw new InvalidOperationException("Server:CorsOrigins 只接受完整的 http/https 来源地址。");
}

if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy => policy
            .WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("ETag")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))
            .AllowCredentials());
    });
}

// ----------------------------------------------------------------------------
// 4. 存储、市场与分布式计算选项配置
// ----------------------------------------------------------------------------
var persistenceQueueCapacity = builder.Configuration.GetValue<int?>("Storage:PersistenceQueueCapacity") ?? 65_536;
if (persistenceQueueCapacity is < 1 or > 1_000_000)
{
    throw new InvalidOperationException("Storage:PersistenceQueueCapacity 必须在 1 到 1000000 之间。");
}

var durableOutboxEnabled = builder.Configuration.GetValue<bool>("Storage:DurableOutboxEnabled");
var databaseOptions = DaoxuanDatabaseOptions.From(builder.Configuration);
var distributedComputeOptions = DaoxuanDistributedComputeOptions.From(builder.Configuration);
if (!databaseOptions.IsPostgres)
{
    throw new InvalidOperationException("Storage:Provider 必须配置为 postgres。");
}
if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
{
    throw new InvalidOperationException("Storage:Provider=postgres 时必须配置 Storage:ConnectionString 或 ConnectionStrings:Daoxuan。");
}

var marketLedgerProvider = builder.Configuration["Market:LedgerProvider"] ?? "postgres";
if (!string.Equals(marketLedgerProvider, "postgres", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Market:LedgerProvider 必须配置为 postgres。");
}

var marketPartitionCount = builder.Configuration.GetValue<int?>("Market:PartitionCount") ?? 1;
var marketPartitionId = builder.Configuration.GetValue<int?>("Market:PartitionId") ?? 0;
var marketTopology = new DaoxuanMarketTopology(marketPartitionId, marketPartitionCount);

// ----------------------------------------------------------------------------
// 5. 依赖注入拓扑装配 (DI Registration)
// ----------------------------------------------------------------------------
// 数据库连接池与基础选项
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton(distributedComputeOptions);
builder.Services.AddSingleton(marketTopology);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(databaseOptions.ConnectionString!));

// 数据库架构迁移后台服务
builder.Services.AddHostedService<DaoxuanPostgresSchemaHostedService>();

// 仓储层实现：快照存储、账号存储、出件箱、市场账本
builder.Services.AddSingleton<IYuoSnapshotStore, DaoxuanPostgresSnapshotStore>();
builder.Services.AddSingleton<IDaoxuanAccountStore, DaoxuanPostgresAccountStore>();
if (durableOutboxEnabled)
{
    builder.Services.AddSingleton<IYuoPersistenceOutbox, DaoxuanPostgresPersistenceOutbox>();
}
builder.Services.AddSingleton<IDaoxuanMarketLedgerStore, DaoxuanPostgresMarketLedgerStore>();

// 持久化调度队列
builder.Services.AddSingleton(new YuoPersistenceQueue(persistenceQueueCapacity));
if (durableOutboxEnabled)
{
    builder.Services.AddSingleton<YuoDurablePersistenceQueue>();
}
builder.Services.AddSingleton<IDaoxuanPersistenceQueue, DaoxuanPersistenceDispatcher>();

// 领域与运行时服务：市场引擎、协调器、时钟、宿主与安全控制
builder.Services.AddSingleton(new DaoxuanMarketEngine(shardCount: 64));
builder.Services.AddSingleton<DaoxuanMarketCoordinator>();
builder.Services.AddSingleton<DaoxuanMarketRuntime>();
builder.Services.AddSingleton<DaoxuanSimulationScheduleSignal>();
builder.Services.AddSingleton<DaoxuanSimulationClock>();
builder.Services.AddSingleton<DaoxuanGameHost>();
builder.Services.AddSingleton<DaoxuanAccessControl>();

// 后台生命周期托管服务 (IHostedService)
if (durableOutboxEnabled)
{
    builder.Services.AddHostedService<DaoxuanPersistenceReplayHostedService>();
}
builder.Services.AddHostedService<DaoxuanPersistenceHostedService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<DaoxuanMarketRuntime>());
builder.Services.AddHostedService<DaoxuanSimulationHostedService>();
if (distributedComputeOptions.Enabled)
{
    builder.Services.AddHostedService<DaoxuanDistributedComputeHostedService>();
}
builder.Services.AddHostedService<DaoxuanMaintenanceHostedService>();

// ----------------------------------------------------------------------------
// 6. WebApplication 构建与启动日志
// ----------------------------------------------------------------------------
var app = builder.Build();
var releaseVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";
app.Logger.LogInformation(
    "玩家持久化模式 {Mode}，队列容量 {Capacity}",
    durableOutboxEnabled ? "durable-outbox" : "memory-queue",
    persistenceQueueCapacity);
app.Logger.LogInformation(
    "市场账本存储 {Provider}，主分片 {PartitionId}/{PartitionCount}，哈希算法 {Algorithm}",
    marketLedgerProvider,
    marketTopology.PartitionId,
    marketTopology.PartitionCount,
    marketTopology.Algorithm);
app.Logger.LogInformation(
    "游戏数据版本 {ContentVersion}，配置指纹 {Fingerprint}",
    gameData.ContentVersion,
    gameData.Fingerprint);
if (!app.Services.GetRequiredService<DaoxuanAccessControl>().Enabled)
{
    app.Logger.LogWarning("未配置 Server:AccessToken，玩家级接口无鉴权，仅建议本地开发使用");
}
if (corsOrigins.Length > 0)
{
    app.UseCors();
}

// ----------------------------------------------------------------------------
// 7. HTTP 中间件管道配置
// ----------------------------------------------------------------------------
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // 针对 Vite 打包的静态资产使用长期不可变缓存，其余 HTML/JSON 默认禁止缓存
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/assets")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    }
});
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

// ----------------------------------------------------------------------------
// 8. 健康检查与就绪探针 (Health Check & Readiness)
// ----------------------------------------------------------------------------
// 存活探针：用于容器探活与暴露非敏感版本及数据指纹
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "daoxuan-server",
    version = releaseVersion,
    gameDataVersion = gameData.ContentVersion,
    gameDataFingerprint = gameData.Fingerprint
}));

// 就绪探针：用于监控市场主分片、在线玩家数、会话数与版本修订号
app.MapGet("/readyz", (DaoxuanGameHost host, DaoxuanMarketRuntime market) => Results.Ok(new
{
    status = "ready",
    version = releaseVersion,
    gameDataVersion = gameData.ContentVersion,
    gameDataFingerprint = gameData.Fingerprint,
    deployment = new
    {
        snapshotProvider = databaseOptions.Provider,
        marketProvider = marketLedgerProvider,
        durableOutbox = durableOutboxEnabled,
        singleWriter = false,
        scaleOutReady = market.SupportsDistributedPartitions,
        zeroDowntimeReady = false
    },
    simulationRevision = host.SimulationRevision,
    host.LoadedPlayerCount,
    host.ConnectedSessionCount,
    market = new
    {
        market.StorageProvider,
        market.PartitionId,
        market.PartitionCount,
        market.Revision
    }
}));

// ----------------------------------------------------------------------------
// 9. 静态只读图鉴与配置 API
// ----------------------------------------------------------------------------
app.MapGet("/api/actions", () => Results.Ok(DaoxuanActionCatalog.All.Select(DaoxuanStateMapper.ToAction)));
app.MapGet("/api/items", () => Results.Ok(DaoxuanItemCatalog.All.Select(DaoxuanStateMapper.ToItemDefinition)));
app.MapGet("/api/combat/abilities", () => Results.Ok(DaoxuanCombatCatalog.AllAbilities.Select(DaoxuanStateMapper.ToCombatAbility)));
app.MapGet("/api/combat/equipment", () => Results.Ok(DaoxuanCombatCatalog.AllEquipment.Select(DaoxuanStateMapper.ToCombatEquipment)));
app.MapGet("/api/combat/encounters", () => Results.Ok(DaoxuanCombatCatalog.AllEncounters.Select(DaoxuanStateMapper.ToCombatEncounter)));
app.MapGet("/api/world/maps", () => Results.Ok(DaoxuanWorldCatalog.All.Select(DaoxuanStateMapper.ToWorldMap)));

// ----------------------------------------------------------------------------
// 10. 账号注册与登录鉴权 API
// ----------------------------------------------------------------------------
app.MapPost("/api/auth/register", async (
    RegisterAccountRequest request,
    IDaoxuanAccountStore accountStore,
    DaoxuanGameHost gameHost,
    CancellationToken cancellationToken) =>
{
    var result = await accountStore.RegisterAsync(
        request.Account,
        request.Password,
        request.CharacterName,
        name => gameHost.IsPlayerNameTaken(name),
        () => gameHost.GetNextSequenceId(),
        cancellationToken);

    if (result.Success && result.PlayerId is Guid playerId)
    {
        await gameHost.EnsureRegisteredPlayerSnapshotAsync(
            playerId,
            result.CharacterName ?? result.Account ?? "无名修士",
            result.Account ?? result.CharacterName ?? "无名修士",
            result.SequenceId,
            result.CreatedAt ?? DateTimeOffset.UtcNow,
            cancellationToken);
    }

    return Results.Ok(new AuthResponse(
        result.Success,
        result.Account,
        result.CharacterName,
        result.PlayerId?.ToString("D"),
        result.SequenceId,
        result.Message));
});

app.MapPost("/api/auth/login", async (
    LoginAccountRequest request,
    IDaoxuanAccountStore accountStore,
    CancellationToken cancellationToken) =>
{
    var result = await accountStore.LoginAsync(request.Account, request.Password, cancellationToken);
    return Results.Ok(new AuthResponse(
        result.Success,
        result.Account,
        result.CharacterName,
        result.PlayerId?.ToString("D"),
        result.SequenceId,
        result.Message));
});

// ----------------------------------------------------------------------------
// 11. 运行时指标与监控 API
// ----------------------------------------------------------------------------
app.MapGet("/api/metrics", (DaoxuanGameHost host) => Results.Ok(host.GetMetrics()));

// ----------------------------------------------------------------------------
// 12. 市场交易 API (订单簿、拓扑、指标、挂单、撤单、资产提取与订单查询)
// ----------------------------------------------------------------------------
// 查询指定交易品种的订单簿深度快照（带 ETag 弱缓存验证）
app.MapGet("/api/market/book", (
    string? itemId,
    int? enhancementLevel,
    int? depth,
    HttpRequest httpRequest,
    HttpResponse httpResponse,
    DaoxuanMarketEngine market,
    DaoxuanMarketRuntime marketRuntime) =>
{
    if (string.IsNullOrWhiteSpace(itemId)
        || !DaoxuanItemCatalog.TryGet(itemId, out var item)
        || !item.IsTradable
        || enhancementLevel is < 0)
    {
        return Results.BadRequest(new { code = "INVALID_MARKET_INSTRUMENT", message = "市场品种无效或不可交易。" });
    }

    var instrument = new DaoxuanMarketInstrument(itemId, enhancementLevel ?? 0);
    if (!marketRuntime.Owns(instrument))
    {
        return Results.Conflict(new
        {
            code = "MARKET_WRONG_PARTITION",
            message = "该市场品种由其他主分片负责。",
            ownerPartition = marketRuntime.GetOwnerPartition(instrument),
            partitionCount = marketRuntime.PartitionCount
        });
    }

    var normalizedDepth = Math.Clamp(depth ?? 20, 1, 100);
    var version = market.GetBookVersion(instrument);
    var etag = DaoxuanHttpCaching.CreateWeakTag(
        "market-book",
        marketRuntime.Revision,
        instrument.ItemId,
        instrument.EnhancementLevel,
        normalizedDepth,
        version.Sequence,
        version.ActiveOrderCount,
        version.PlacementCount,
        version.MatchCount,
        version.CancelCount,
        version.RejectedCount,
        version.TradedQuantity,
        version.TradedGrossAmount);
    if (DaoxuanHttpCaching.IsNotModified(httpRequest, etag))
    {
        return DaoxuanHttpCaching.NotModified(httpResponse, etag);
    }

    var responseRevision = marketRuntime.Revision;
    var read = market.GetBookWithVersion(instrument, normalizedDepth);
    etag = DaoxuanHttpCaching.CreateWeakTag(
        "market-book",
        responseRevision,
        instrument.ItemId,
        instrument.EnhancementLevel,
        normalizedDepth,
        read.Version.Sequence,
        read.Version.ActiveOrderCount,
        read.Version.PlacementCount,
        read.Version.MatchCount,
        read.Version.CancelCount,
        read.Version.RejectedCount,
        read.Version.TradedQuantity,
        read.Version.TradedGrossAmount);
    DaoxuanHttpCaching.ApplyHeaders(httpResponse, etag);
    return Results.Ok(DaoxuanMarketMapper.ToDto(read.Snapshot));
});

// 查询当前市场分片拓扑元数据
app.MapGet("/api/market/topology", (DaoxuanMarketRuntime market) => Results.Ok(new
{
    provider = market.StorageProvider,
    partitionId = market.PartitionId,
    partitionCount = market.PartitionCount,
    algorithm = market.PartitionAlgorithm
}));

// 查询市场撮合引擎运行指标
app.MapGet("/api/market/metrics", (
    HttpRequest httpRequest,
    HttpResponse httpResponse,
    DaoxuanMarketEngine market,
    DaoxuanMarketRuntime marketRuntime) =>
{
    var etag = DaoxuanHttpCaching.CreateWeakTag("market-metrics", marketRuntime.Revision);
    if (DaoxuanHttpCaching.IsNotModified(httpRequest, etag))
    {
        return DaoxuanHttpCaching.NotModified(httpResponse, etag);
    }

    DaoxuanHttpCaching.ApplyHeaders(httpResponse, etag);
    return Results.Ok(DaoxuanMarketMapper.ToDto(market.GetMetrics()));
});

// 提交玩家市场限价挂单
app.MapPost("/api/players/{playerId:guid}/market/orders", async (
    Guid playerId,
    DaoxuanMarketPlaceRequest request,
    HttpRequest httpRequest,
    DaoxuanGameHost host,
    DaoxuanAccessControl accessControl,
    CancellationToken cancellationToken) =>
{
    if (!accessControl.IsValid(httpRequest.Headers["X-Access-Token"]))
    {
        return Results.Json(
            new { code = "UNAUTHORIZED", message = "缺少或错误的访问令牌。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    if (request is null
        || string.IsNullOrWhiteSpace(request.OrderId)
        || string.IsNullOrWhiteSpace(request.ItemId)
        || request.Price <= 0
        || request.Quantity <= 0
        || request.EnhancementLevel < 0
        || !DaoxuanItemCatalog.TryGet(request.ItemId, out var item)
        || !item.IsTradable)
    {
        return Results.BadRequest(new { code = "INVALID_MARKET_ORDER", message = "市场订单参数无效或物品不可交易。" });
    }

    var side = request.Side?.Trim().ToLowerInvariant() switch
    {
        "buy" => DaoxuanMarketSide.Buy,
        "sell" => DaoxuanMarketSide.Sell,
        _ => (DaoxuanMarketSide?)null
    };
    if (side is null)
    {
        return Results.BadRequest(new { code = "INVALID_MARKET_SIDE", message = "订单方向必须是 buy 或 sell。" });
    }

    var outcome = await host.PlaceMarketOrderAsync(
        playerId,
        new DaoxuanMarketOrderRequest(
            request.OrderId,
            playerId,
            new DaoxuanMarketInstrument(request.ItemId, request.EnhancementLevel),
            side.Value,
            request.Price,
            request.Quantity),
        cancellationToken);
    return Results.Ok(DaoxuanMarketMapper.ToDto(outcome));
});

// 撤销玩家在市场上的挂单
app.MapDelete("/api/players/{playerId:guid}/market/orders/{orderId}", async (
    Guid playerId,
    string orderId,
    HttpRequest httpRequest,
    DaoxuanGameHost host,
    DaoxuanAccessControl accessControl,
    CancellationToken cancellationToken) =>
{
    if (!accessControl.IsValid(httpRequest.Headers["X-Access-Token"]))
    {
        return Results.Json(
            new { code = "UNAUTHORIZED", message = "缺少或错误的访问令牌。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    var outcome = await host.CancelMarketOrderAsync(playerId, orderId, cancellationToken);
    return Results.Ok(DaoxuanMarketMapper.ToDto(outcome));
});

// 提取玩家在市场已撮合或已撤单归还的暂存资产
app.MapPost("/api/players/{playerId:guid}/market/claim", async (
    Guid playerId,
    HttpRequest httpRequest,
    DaoxuanGameHost host,
    DaoxuanAccessControl accessControl,
    CancellationToken cancellationToken) =>
{
    if (!accessControl.IsValid(httpRequest.Headers["X-Access-Token"]))
    {
        return Results.Json(
            new { code = "UNAUTHORIZED", message = "缺少或错误的访问令牌。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    var outcome = await host.ClaimMarketAssetsAsync(playerId, cancellationToken);
    return Results.Ok(DaoxuanMarketMapper.ToDto(outcome));
});

// 查询单笔市场订单状态
app.MapGet("/api/market/orders/{orderId}", (
    string orderId,
    DaoxuanMarketEngine market) =>
{
    return market.TryGetOrder(orderId, out var order)
        ? Results.Ok(DaoxuanMarketMapper.ToDto(order!))
        : Results.NotFound(new { code = "ORDER_NOT_FOUND", message = "订单不存在或已离开活动订单路由。" });
});

// 查询指定玩家当前在市场的挂单列表
app.MapGet("/api/players/{playerId:guid}/market/orders", (
    Guid playerId,
    int? limit,
    HttpRequest httpRequest,
    HttpResponse httpResponse,
    DaoxuanMarketEngine market,
    DaoxuanMarketRuntime marketRuntime) =>
{
    var normalizedLimit = Math.Clamp(limit ?? 100, 1, 500);
    var etag = DaoxuanHttpCaching.CreateWeakTag(
        "market-orders",
        marketRuntime.Revision,
        playerId,
        normalizedLimit);
    if (DaoxuanHttpCaching.IsNotModified(httpRequest, etag))
    {
        return DaoxuanHttpCaching.NotModified(httpResponse, etag);
    }

    DaoxuanHttpCaching.ApplyHeaders(httpResponse, etag);
    return Results.Ok(market.GetActiveOrders(playerId, normalizedLimit).Select(DaoxuanMarketMapper.ToDto));
});

// ----------------------------------------------------------------------------
// 13. 玩家快照与调试接口
// ----------------------------------------------------------------------------
// 获取指定玩家当前的完整快照状态
app.MapGet("/api/players/{playerId:guid}", async (
    Guid playerId,
    HttpRequest httpRequest,
    DaoxuanGameHost host,
    DaoxuanAccessControl accessControl,
    CancellationToken cancellationToken) =>
{
    if (!accessControl.IsValid(httpRequest.Headers["X-Access-Token"]))
    {
        return Results.Json(
            new { code = "UNAUTHORIZED", message = "缺少或错误的访问令牌。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(await host.GetSnapshotAsync(playerId, cancellationToken));
});

// 开发环境专用：快进指定玩家的离线模拟时间
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/debug/fast-forward", async (
        FastForwardRequest request,
        DaoxuanGameHost host,
        CancellationToken cancellationToken) =>
    {
        if (request.Seconds is <= 0 or > 2_592_000)
        {
            return Results.BadRequest(new { code = "INVALID_DURATION", message = "seconds 必须在 1 到 2592000 之间。" });
        }

        var snapshot = await host.FastForwardAsync(request.PlayerId, TimeSpan.FromSeconds(request.Seconds), cancellationToken);
        return Results.Ok(snapshot);
    });
}

// ----------------------------------------------------------------------------
// 14. WebSocket 终结点与单页应用 Fallback
// ----------------------------------------------------------------------------
app.Map("/ws", DaoxuanWebSocketEndpoint.HandleAsync);
app.MapFallback("/api/{**path}", () => Results.NotFound(new
{
    code = "NOT_FOUND",
    message = "接口不存在。"
}));
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// 调试快进请求包体。
/// </summary>
public sealed record FastForwardRequest(Guid PlayerId, int Seconds);

public partial class Program;
