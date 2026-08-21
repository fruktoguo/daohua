using System.Reflection;
using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Market;
using Daoxuan.Domain.Simulation;
using Daoxuan.Protocol;
using Daoxuan.Server.Infrastructure;
using Daoxuan.Server.Runtime;
using YuoCore.Persistence;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var configuredGameDataDirectory = builder.Configuration["GameData:Directory"];
var gameData = string.IsNullOrWhiteSpace(configuredGameDataDirectory)
    ? DaoxuanGameData.Current
    : DaoxuanGameData.LoadFromDirectory(ResolveGameDataPath(builder.Environment.ContentRootPath, configuredGameDataDirectory));

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
var persistenceQueueCapacity = builder.Configuration.GetValue<int?>("Storage:PersistenceQueueCapacity") ?? 65_536;
if (persistenceQueueCapacity is < 1 or > 1_000_000)
{
    throw new InvalidOperationException("Storage:PersistenceQueueCapacity 必须在 1 到 1000000 之间。");
}

var durableOutboxEnabled = builder.Configuration.GetValue<bool>("Storage:DurableOutboxEnabled");
var storagePaths = new DaoxuanStoragePaths(builder.Configuration, builder.Environment);
var databaseOptions = DaoxuanDatabaseOptions.From(builder.Configuration);
var distributedComputeOptions = DaoxuanDistributedComputeOptions.From(builder.Configuration);
if (databaseOptions.IsPostgres && string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
{
    throw new InvalidOperationException("Storage:Provider=postgres 时必须配置 Storage:ConnectionString 或 ConnectionStrings:Daoxuan。");
}

var marketLedgerProvider = builder.Configuration["Market:LedgerProvider"]
    ?? (databaseOptions.IsPostgres ? "postgres" : "local-json");
if (!string.Equals(marketLedgerProvider, "local-json", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(marketLedgerProvider, "postgres", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"不支持的 Market:LedgerProvider={marketLedgerProvider}。");
}
if (string.Equals(marketLedgerProvider, "postgres", StringComparison.OrdinalIgnoreCase)
    && !databaseOptions.IsPostgres)
{
    throw new InvalidOperationException("Market:LedgerProvider=postgres 必须同时启用 Storage:Provider=postgres。");
}

var marketPartitionCount = builder.Configuration.GetValue<int?>("Market:PartitionCount") ?? 1;
var marketPartitionId = builder.Configuration.GetValue<int?>("Market:PartitionId") ?? 0;
var marketTopology = new DaoxuanMarketTopology(marketPartitionId, marketPartitionCount);
if (string.Equals(marketLedgerProvider, "local-json", StringComparison.OrdinalIgnoreCase)
    && (marketTopology.PartitionCount != 1 || marketTopology.PartitionId != 0))
{
    throw new InvalidOperationException(
        "当前 local-json 市场账本只允许 Market:PartitionId=0、Market:PartitionCount=1；多主分片需启用 PostgreSQL 账本。");
}
if (distributedComputeOptions.Enabled && !databaseOptions.IsPostgres)
{
    throw new InvalidOperationException("DistributedCompute:Enabled=true 必须同时启用 Storage:Provider=postgres。");
}


builder.Services.AddSingleton(storagePaths);
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton(distributedComputeOptions);
builder.Services.AddSingleton(marketTopology);
if (databaseOptions.IsPostgres)
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(databaseOptions.ConnectionString!));
    builder.Services.AddHostedService<DaoxuanPostgresSchemaHostedService>();
    builder.Services.AddSingleton<IYuoSnapshotStore, DaoxuanPostgresSnapshotStore>();
    builder.Services.AddSingleton<IDaoxuanAccountStore, DaoxuanPostgresAccountStore>();
    if (durableOutboxEnabled)
    {
        builder.Services.AddSingleton<IYuoPersistenceOutbox, DaoxuanPostgresPersistenceOutbox>();
    }
}
else
{
    builder.Services.AddSingleton<IYuoSnapshotStore, DaoxuanSnapshotStore>();
    builder.Services.AddSingleton<IDaoxuanAccountStore, DaoxuanLocalAccountStore>();
    if (durableOutboxEnabled)
    {
        builder.Services.AddSingleton<IYuoPersistenceOutbox>(
            new YuoFilePersistenceOutbox(storagePaths.OutboxDirectory, "daoxuan-player-snapshots"));
    }
}

if (string.Equals(marketLedgerProvider, "postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDaoxuanMarketLedgerStore, DaoxuanPostgresMarketLedgerStore>();
}
else
{
    builder.Services.AddSingleton<IDaoxuanMarketLedgerStore, DaoxuanLocalMarketLedgerStore>();
}

builder.Services.AddSingleton(new YuoPersistenceQueue(persistenceQueueCapacity));
if (durableOutboxEnabled)
{
    builder.Services.AddSingleton<YuoDurablePersistenceQueue>();
}

builder.Services.AddSingleton<IDaoxuanPersistenceQueue, DaoxuanPersistenceDispatcher>();
builder.Services.AddSingleton(new DaoxuanMarketEngine(shardCount: 64));
builder.Services.AddSingleton<DaoxuanMarketCoordinator>();
builder.Services.AddSingleton<DaoxuanMarketRuntime>();
builder.Services.AddSingleton<DaoxuanSimulationScheduleSignal>();
builder.Services.AddSingleton<DaoxuanSimulationClock>();
builder.Services.AddSingleton<DaoxuanGameHost>();
builder.Services.AddSingleton<DaoxuanAccessControl>();
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

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
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

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "daoxuan-server",
    version = releaseVersion,
    gameDataVersion = gameData.ContentVersion,
    gameDataFingerprint = gameData.Fingerprint
}));
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
        singleWriter = !databaseOptions.IsPostgres,
        scaleOutReady = databaseOptions.IsPostgres && market.SupportsDistributedPartitions,
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
app.MapGet("/api/actions", () => Results.Ok(DaoxuanActionCatalog.All.Select(DaoxuanStateMapper.ToAction)));
app.MapGet("/api/items", () => Results.Ok(DaoxuanItemCatalog.All.Select(DaoxuanStateMapper.ToItemDefinition)));
app.MapGet("/api/combat/abilities", () => Results.Ok(DaoxuanCombatCatalog.AllAbilities.Select(DaoxuanStateMapper.ToCombatAbility)));
app.MapGet("/api/combat/equipment", () => Results.Ok(DaoxuanCombatCatalog.AllEquipment.Select(DaoxuanStateMapper.ToCombatEquipment)));
app.MapGet("/api/combat/encounters", () => Results.Ok(DaoxuanCombatCatalog.AllEncounters.Select(DaoxuanStateMapper.ToCombatEncounter)));
app.MapGet("/api/world/maps", () => Results.Ok(DaoxuanWorldCatalog.All.Select(DaoxuanStateMapper.ToWorldMap)));
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
app.MapGet("/api/metrics", (DaoxuanGameHost host) => Results.Ok(host.GetMetrics()));
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
app.MapGet("/api/market/topology", (DaoxuanMarketRuntime market) => Results.Ok(new
{
    provider = market.StorageProvider,
    partitionId = market.PartitionId,
    partitionCount = market.PartitionCount,
    algorithm = market.PartitionAlgorithm
}));
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
app.MapGet("/api/market/orders/{orderId}", (
    string orderId,
    DaoxuanMarketEngine market) =>
{
    return market.TryGetOrder(orderId, out var order)
        ? Results.Ok(DaoxuanMarketMapper.ToDto(order!))
        : Results.NotFound(new { code = "ORDER_NOT_FOUND", message = "订单不存在或已离开活动订单路由。" });
});
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
app.Map("/ws", DaoxuanWebSocketEndpoint.HandleAsync);
app.MapFallback("/api/{**path}", () => Results.NotFound(new
{
    code = "NOT_FOUND",
    message = "接口不存在。"
}));
app.MapFallbackToFile("index.html");

app.Run();

public sealed record FastForwardRequest(Guid PlayerId, int Seconds);
public partial class Program;
