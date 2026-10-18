using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Protocol;
using Server.Clean;
using Server.Clean.Ops;
using Server.Clean.AccountAbuse;
using DaoHua.Ecs;
using Server;

var builder = WebApplication.CreateBuilder(args);
var processRole = ResolveProcessRole(args);
var startedAt = DateTimeOffset.UtcNow;
var opsOptions = OpsOptions.From(builder.Configuration);
// 运维面板在 API 角色且 Ops:Enabled 时装配（支持本地开发与正式生产环境）。
var opsPanelEnabled = opsOptions.Enabled && processRole == ServerProcessRole.Api;
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
    // 未显式配置时绑定本地回环默认端口；运维面板已并入该端口（页面由前端承载），不再单独监听。
    if (string.IsNullOrWhiteSpace(explicitUrls) && !hasExplicitPorts)
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
    // 生产环境中容器经宿主机 Caddy 反代接入（端口仅绑 127.0.0.1 回环），
    // 容器网络收到的 TCP 源地址为 Docker 虚拟网桥网关（如 172.16.0.0/12），
    // 必须清空默认的 KnownIPNetworks/KnownProxies 以信任反向代理注入的 X-Forwarded-* 标头。
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var options = ServerOptions.From(builder.Configuration) with { ProcessRole = processRole };
builder.Services.AddSingleton(options);
var benchmark = BenchmarkHost.FromEnvironment(builder.Environment, options);
if (benchmark?.Mode == "replay")
    builder.Services.AddSingleton<ISimulationTransactionIdentity, DeterministicSimulationTransactionIdentity>();
// 显式先实例化监听器，确保 bootstrap、首次载入和实体创建都进入同一观测窗口。
using var benchmarkTelemetry = benchmark is null ? null : new BenchmarkTelemetry(benchmark.ObservabilityEnabled);
if (benchmarkTelemetry is not null) builder.Services.AddSingleton(benchmarkTelemetry);
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
    using (var riskKeyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        builder.Services.AddSingleton(await AccountRiskHasher.CreateAsync(dataSource, riskKeyTimeout.Token));
    builder.Services.AddSingleton<AccountRiskPolicyRegistry>();
    builder.Services.AddSingleton<PostgresAccountRiskRepository>();
    builder.Services.AddSingleton<AccountRiskRequestContext>();
    builder.Services.AddSingleton<IAccountRepository, PostgresAccountRepository>();
    builder.Services.AddSingleton<IAssetBalanceRepository, PostgresAssetRepository>();
    builder.Services.AddSingleton<IEconomyTransactionStore, PostgresEconomyRepository>();
    builder.Services.AddSingleton<IPlayerOwnershipStore, PostgresPlayerOwnershipRepository>();
    builder.Services.AddSingleton<PostgresMarketRepository>();
    builder.Services.AddSingleton<IMarketTransactionStore>(sp => sp.GetRequiredService<PostgresMarketRepository>());
    builder.Services.AddSingleton<MarketRuntime>();
    builder.Services.AddSingleton<IPlayerSimulationClock>(_ => PlayerSimulationClock.FromEnvironment());
    builder.Services.AddSingleton<EcsGameRuntime>(sp =>
    {
        var catalog = sp.GetRequiredService<GameDataCatalog>();
        var simulationClock = sp.GetRequiredService<IPlayerSimulationClock>();
        var rawEpoch = Environment.GetEnvironmentVariable("DAOHUA_BENCH_EPOCH");
        var epoch = DateTimeOffset.TryParse(rawEpoch, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime() : simulationClock.UtcNow;
        // 在线主服开启斗法撮合：遭遇计数耗尽后进入匹配等待相位，由 AdvanceAllAsync 内的撮合循环配对。
        return new EcsGameRuntime(catalog, epoch, options.EcsShardCount, new DuelAdvanceOptions(MatchingEnabled: true));
    });
    builder.Services.AddSingleton<Server.Clean.Social.IParticipationSessionStore, Server.Clean.Social.PostgresParticipationSessionStore>();
    builder.Services.AddSingleton<Server.Clean.Social.IActionParticipationRuntime, ActionParticipationRuntime>();
    builder.Services.AddSingleton<EcsPlayerRuntimeAdapter>();
    builder.Services.AddSingleton<IPlayerRuntime>(sp => sp.GetRequiredService<EcsPlayerRuntimeAdapter>());
    builder.Services.AddSingleton<SessionRegistry>();
    // 传音与社交：不属于玩家聚合，独立单例运行时；聊天记录落 PostgreSQL（每作用域保留 100 条）。
    builder.Services.AddSingleton<Server.Clean.Social.OnlinePresenceRegistry>();
    // 在线断线宽限（总纲18 §14.3）：断线后在线位与实体常驻宽限期，到期按断线时刻收尾。
    builder.Services.AddSingleton<Server.Clean.DisconnectGraceRegistry>();
    builder.Services.AddSingleton<Server.Clean.Social.ISocialRepository, Server.Clean.Social.PostgresSocialRepository>();
    builder.Services.AddSingleton<Server.Clean.Social.IInvitationRepository, Server.Clean.Social.PostgresInvitationRepository>();
    builder.Services.AddSingleton<Server.Clean.Social.IChatRepository, Server.Clean.Social.PostgresChatRepository>();
    builder.Services.AddSingleton<Server.Clean.Social.PartyValuationService>();
    builder.Services.AddSingleton<Server.Clean.Social.ChatRuntime>();
    builder.Services.AddSingleton<Server.Clean.Social.SocialRuntime>();
    builder.Services.AddSingleton<Server.Clean.Social.IPlayerPresenceStatsService, Server.Clean.Social.PlayerPresenceStatsService>();
    builder.Services.AddSingleton<Server.Clean.AntiMultiOpen.DeviceQuotaRegistry>();
    builder.Services.AddSingleton<Server.Clean.AntiMultiOpen.AntiMultiOpenAdmission>();
    builder.Services.AddDaohuaAuthentication();
    if (opsLogBuffer is not null) builder.Services.AddSingleton(opsLogBuffer);
    if (opsPanelEnabled)
    {
        // 面板配置与存储侧只读查询：在 API 角色且面板启用时装配。
        builder.Services.AddSingleton(opsOptions);
        builder.Services.AddSingleton<Server.Clean.Ops.OpsStorageInsights>();
    }
}

// 两种进程都需要 section/outbox 存储；worker 只装配这两个后台消费边界。
builder.Services.AddSingleton<PostgresSectionRepository>();
builder.Services.AddSingleton<IPlayerStateRepository>(sp => sp.GetRequiredService<PostgresSectionRepository>());
builder.Services.AddSingleton<PostgresNodeBuildingRepository>();
builder.Services.AddSingleton<PostgresNodeActivityRepository>();
builder.Services.AddSingleton<NodeBuildingRegistry>();
builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
builder.Services.AddSingleton<ICommandReceiptStore, PostgresCommandReceiptRepository>();
builder.Services.AddSingleton<PostgresRetentionRepository>();
builder.Services.AddSingleton<IIntegrationEventDispatcher, AuditIntegrationEventDispatcher>();
// 冷挂机派发链仅 Api 角色装配：/internal/compute-link 只在 Api map，PersistenceWorker
// 装配注册表只会空转，ColdCatchUpWorker 还会与 Api 重复生产任务。
if (processRole == ServerProcessRole.Api)
{
    builder.Services.AddSingleton<IComputeResultVerifier, EcsComputeResultVerifier>();
    if (benchmark is null) builder.Services.AddComputeDispatch();
    else
    {
        // 基准只保留链路依赖供路由/健康检查解析，冷挂机不得提前消费未加载种子。
        builder.Services.AddSingleton<PostgresComputeJobRepository>();
        builder.Services.AddSingleton<ComputeLinkMetrics>();
        builder.Services.AddSingleton<ComputeLinkRegistry>();
    }
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
    if (benchmark?.IsReplay != true)
    {
        builder.Services.AddHostedService<PlayerFlushHostedService>();
        builder.Services.AddHostedService<PlayerSimulationHostedService>();
        // 组队讨伐看门狗（总纲 §21.13）：周期逐出 48h 离线成员并对账参与绑定；重启保留检查点。
        builder.Services.AddHostedService<Server.Clean.Social.PartySessionWatchdog>();
    }
    // 同一宽限注册表单例兼任 hosted service：停机时立即收尾全部在途宽限条目。
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Server.Clean.DisconnectGraceRegistry>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketRuntime>());
}

var app = builder.Build();
var releaseVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

app.UseExceptionHandler();
app.UseForwardedHeaders();

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
    // 市场入账事件 → 在线玩家钱包失效：成交对手方/退款属主的余额差量
    // 由下一次玩家门内推进经推送带给客户端（DisposeAsync 中解绑）。
    var marketRuntime = app.Services.GetRequiredService<MarketRuntime>();
    marketRuntime.WalletCredited += app.Services.GetRequiredService<EcsPlayerRuntimeAdapter>().NotifyMarketWalletCredited;
    // 断线宽限到期卸载的玩家若仍在有人在线的会话名册中，重新常驻（总纲18 §14.6）；
    // 在装配处订阅避免 SocialRuntime 构造依赖环。
    var disconnectGrace = app.Services.GetRequiredService<Server.Clean.DisconnectGraceRegistry>();
    disconnectGrace.Expired += _ => app.Services.GetRequiredService<Server.Clean.Social.SocialRuntime>()
        .ReconcileResidentMembersAsync(CancellationToken.None);

    // 生产镜像把前端构建产物拷入 wwwroot；本地开发由 Vite 提供前端，wwwroot 不存在。
    // 仅在静态站点根存在时装配静态文件与 SPA 回退，避免开发态刷出 WebRootPath 告警。
    var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var hasWebRoot = Directory.Exists(webRootPath);
    var staticFileOptions = new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
            }
        }
    };
    if (hasWebRoot) app.UseStaticFiles(staticFileOptions);
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

    app.MapGet("/api/v1/version", (HttpContext context, GameDataCatalog gameData) =>
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Ok(new ServerVersionDto(releaseVersion, gameData.Fingerprint, ProtocolConstants.CurrentVersion));
    });

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
                technique = action.Technique,
                durationSeconds = action.DurationSeconds,
                level = action.Level,
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
                    ratio = entry.Ratio,
                    percentage = entry.Ratio is not null ? entry.Ratio.Value * 100 : (double?)null,
                    aspect = entry.Aspect?.ToString().ToLowerInvariant(),
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
                availableNodeIds = action.AllowedNodeIds?.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                excludedNodeIds = action.ExcludedNodeIds?.OrderBy(value => value, StringComparer.Ordinal).ToArray()
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

    // 全图节点公共建筑索引：地图地点详情合并展示玩家建筑；
    // 数据来自注册表读路径（含惰性完工结算），不落任何玩家分段。
    app.MapGet("/api/v1/world/node-buildings", async (NodeBuildingRegistry nodeBuildings, GameDataCatalog gameData, CancellationToken ct) =>
    {
        var now = DateTimeOffset.UtcNow;
        var index = await nodeBuildings.AllBuildingsAsync(now, ct);
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        var result = new Dictionary<string, object[]>(StringComparer.Ordinal);
        foreach (var (nodeId, snapshot) in index)
        {
            var entries = snapshot.Buildings
                .Select(state => gameData.Buildings.TryGetValue(state.BuildingId, out var definition)
                    ? new
                    {
                        buildingId = state.BuildingId,
                        level = Domain.World.NodeBuildingRules.EffectiveLevel(state, definition, nowUnixSeconds),
                        targetLevel = state.TargetLevel
                    }
                    : null)
                .Where(entry => entry is not null)
                .ToArray();
            if (entries.Length > 0) result[nodeId] = entries!;
        }
        return Results.Ok(result);
    }).RequireAuthorization();

    var auth = app.MapGroup("/api/v1/auth");
    auth.MapGet("/client-policy", async (PostgresAccountRiskRepository risk, HttpContext context, CancellationToken ct) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var policy = await risk.LoadPolicyAsync(ct);
        return Results.Ok(new { tabGuardEnabled = policy.Enabled && policy.Modules.Single(item => item.Id == "tabGuard").Enabled });
    });
    auth.MapPost("/register", async (RegisterRequest request, IAccountRepository accounts, PostgresAccountRiskRepository risk, AccountRiskRequestContext riskContext, IPlayerRuntime runtime, EcsPlayerRuntimeAdapter adapter, ServerOptions serverOptions, HttpContext context, CancellationToken ct) =>
    {
        AccountRiskSignals signals;
        try { signals = riskContext.Collect(context, request.RiskSignals); }
        catch (ArgumentException)
        {
            await risk.RecordInvalidSignalsAsync("register", null, ct);
            return Results.BadRequest(Problem(400, "INVALID_RISK_SIGNALS", "设备校验信息无效。"));
        }
        var policy = await risk.LoadPolicyAsync(ct);
        var result = await accounts.RegisterAsync(request.Account, request.Password, request.DisplayName, request.InvitationCode, signals, ct, policy);
        if (!result.Success)
            return Results.BadRequest(Problem(400, result.ErrorCode ?? "REGISTRATION_FAILED", result.Message ?? "注册失败。"));
        // 邀请者的注册奖励已随注册事务入账：在线邀请者标记钱包失效并推送入账日志。
        if (result.InviterPlayerId is { } inviterId)
            await adapter.NotifyInvitationRewardAsync(inviterId,
                Server.Clean.Social.InvitationRewards.InviterRewardLogTemplate(
                    Server.Clean.Social.InvitationRewards.MilestoneSignup, result.DisplayName!));
        await runtime.LoadAsync(result.PlayerId!.Value, ct);
        await runtime.FlushAsync(result.PlayerId.Value, true, ct);
        StampSessionCookie(context, result.SessionToken!, serverOptions.SessionLifetime);
        return Results.Created($"/api/v1/players/{result.PlayerId:D}", new
        {
            result.AccountId,
            result.PlayerId,
            result.Account,
            result.DisplayName,
            result.SequenceId
        });
    }).RequireRateLimiting("auth");
    auth.MapPost("/login", async (LoginRequest request, IAccountRepository accounts, PostgresAccountRiskRepository risk, AccountRiskRequestContext riskContext, ServerOptions serverOptions, HttpContext context, CancellationToken ct) =>
    {
        AccountRiskSignals signals;
        try { signals = riskContext.Collect(context, request.RiskSignals); }
        catch (ArgumentException)
        {
            await risk.RecordInvalidSignalsAsync("login", null, ct);
            return Results.BadRequest(Problem(400, "INVALID_RISK_SIGNALS", "设备校验信息无效。"));
        }
        var policy = await risk.LoadPolicyAsync(ct);
        var result = await accounts.LoginAsync(request.Account, request.Password, signals, ct, policy);
        if (!result.Success)
        {
            var status = result.ErrorCode == "INVALID_CREDENTIALS" ? 401 : 403;
            return Results.Json(Problem(status, result.ErrorCode!, result.Message!), statusCode: status);
        }
        StampSessionCookie(context, result.SessionToken!, serverOptions.SessionLifetime);
        return Results.Ok(new { result.AccountId, result.PlayerId, result.Account, result.DisplayName, result.SequenceId });
    }).RequireRateLimiting("auth");
    auth.MapPost("/logout", async (IAccountRepository accounts, HttpContext context, CancellationToken ct) =>
    {
        if (context.Request.Cookies.TryGetValue(SessionAuthenticationDefaults.CookieName, out var token))
            await accounts.RevokeSessionAsync(token, ct);
        context.Response.Cookies.Delete(SessionAuthenticationDefaults.CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Results.NoContent();
    });
    auth.MapPost("/change-password", async (ChangePasswordRequest request, ClaimsPrincipal user, IAccountRepository accounts, HttpContext context, CancellationToken ct) =>
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId))
            return Results.Json(Problem(401, "UNAUTHENTICATED", "当前没有有效会话。"), statusCode: 401);
        // 改密吊销其他会话：当前会话令牌哈希作为豁免项传入仓储。
        byte[]? keepTokenHash = null;
        if (context.Request.Cookies.TryGetValue(SessionAuthenticationDefaults.CookieName, out var currentToken))
            keepTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(currentToken));
        var result = await accounts.ChangePasswordAsync(accountId, request.CurrentPassword, request.NewPassword, keepTokenHash, ct);
        return result.Success
            ? Results.NoContent()
            : Results.BadRequest(Problem(400, result.ErrorCode ?? "CHANGE_PASSWORD_FAILED", result.Message ?? "密码修改失败。"));
    }).RequireAuthorization().RequireRateLimiting("auth");
    auth.MapGet("/session", (ClaimsPrincipal user) =>
    {
        if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirstValue("player_id"), out var playerId))
            return Results.Json(Problem(401, "UNAUTHENTICATED", "当前没有有效会话。"), statusCode: 401);
        return Results.Ok(new
        {
            accountId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            account = user.FindFirstValue(ClaimTypes.Name),
            playerId,
            displayName = user.FindFirstValue("display_name"),
            sequenceId = long.TryParse(user.FindFirstValue("sequence_id"), out var sequenceId) ? sequenceId : 0L
        });
    }).RequireAuthorization();

    // 邀请总览：本人邀请码 + 被邀请人进度列表；低频拉取，不走 WebSocket 帧。
    app.MapGet("/api/v1/invitation", async (ClaimsPrincipal user, Server.Clean.Social.IInvitationRepository invitations, CancellationToken ct) =>
    {
        if (user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirstValue("player_id"), out var playerId))
            return Results.Json(Problem(401, "UNAUTHENTICATED", "当前没有有效会话。"), statusCode: 401);
        var overview = await invitations.GetOverviewAsync(playerId, ct);
        if (overview is null) return Results.NotFound(Problem(404, "PLAYER_NOT_FOUND", "玩家不存在。"));
        return Results.Ok(new
        {
            overview.InviteCode,
            InviteeCount = overview.Invitees.Count,
            Invitees = overview.Invitees.Select(row => new
            {
                row.DisplayName,
                row.HighestRealmLevel,
                row.QiRewarded,
                row.FoundationRewarded,
                row.CreatedAt,
            }).ToArray(),
            overview.InvitedByName,
        });
    }).RequireAuthorization();

    var players = app.MapGroup("/api/v1/players").RequireAuthorization();
    players.MapGet("/{playerId:guid}", async (Guid playerId, ClaimsPrincipal user, IPlayerRuntime runtime, GameDataCatalog gameData, CancellationToken ct) =>
    {
        if (!IsPlayer(user, playerId)) return Results.Forbid();
        var snapshot = await runtime.LoadAsync(playerId, ct);
        return Results.Ok(ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData));
    });
    players.MapPost("/{playerId:guid}/commands", async (Guid playerId, CommandEnvelopeDto request, ClaimsPrincipal user, IPlayerRuntime runtime, GameDataCatalog gameData, CancellationToken ct) =>
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
    market.MapGet("/variants", (string assetId, MarketRuntime marketRuntime) =>
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128)
            return Results.BadRequest(Problem(400, "INVALID_INSTRUMENT", "市场品种无效。"));
        try
        {
            // 不做 Owns 前置校验：_books 本身只含本进程分片品种，返回天然是分片局部视图。
            var summaries = marketRuntime.GetVariantSummaries(new ItemId(assetId));
            return Results.Ok(summaries.Select(summary => new MarketVariantSummaryDto(
                assetId,
                summary.Variant.Length == 0 ? null : summary.Variant,
                summary.BestAskPrice,
                summary.BestBidPrice)));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(Problem(409, "MARKET_NOT_READY", exception.Message));
        }
    });
    market.MapGet("/stats", async (string? assetId, PostgresMarketRepository marketStore, CancellationToken ct) =>
    {
        // 行情统计直读 market.item_stats 小表：悬浮行情卡轮询走全表（数百行内），单品种传 assetId 过滤。
        if (assetId is not null && (assetId.Length == 0 || assetId.Length > 128))
            return Results.BadRequest(Problem(400, "INVALID_INSTRUMENT", "市场品种无效。"));
        var rows = await marketStore.LoadItemStatsAsync(assetId, ct);
        return Results.Ok(rows.Select(MarketDtoMapper.ToStats).ToArray());
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
        if (!double.IsFinite(request.Price) || request.Price <= 0 || request.Price != Math.Floor(request.Price)
            || !double.IsFinite(request.Quantity) || request.Quantity <= 0 || request.Quantity != Math.Floor(request.Quantity))
            return Results.BadRequest(Problem(400, "INVALID_ORDER", "订单价格与数量必须为正整数。"));
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

    app.MapGet("/api/v1/metrics", (ClaimsPrincipal user, IPlayerRuntime playerRuntime, SessionRegistry sessions, MarketRuntime marketRuntime) =>
    {
        if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        return Results.Ok(new
        {
            loadedPlayers = playerRuntime.LoadedCount,
            sessions = sessions.Count,
            market = marketRuntime.GetMetrics()
        });
    }).RequireAuthorization();
    benchmark?.MapEndpoints(app);
    app.Map("/ws", WebSocketEndpoint.HandleAsync);
    // 计算节点反向拨入链路：节点主动 WebSocket 注册，主服经连接下发冷挂机批次。
    app.Map(Server.Clean.ComputeLinkContract.Path, Server.Clean.ComputeLinkEndpoint.HandleAsync);
    if (opsPanelEnabled)
    {
        app.MapOpsPanel(opsOptions, releaseVersion, startedAt);
        app.Logger.LogWarning("运维面板已启用，访问路径：{Path}", opsOptions.Path);
        // 密码未配置时本进程随机生成，必须在控制台可见一次；已显式配置时不再回显明文。
        if (string.IsNullOrWhiteSpace(builder.Configuration["Ops:Token"]))
            app.Logger.LogWarning("运维面板访问密码（本进程随机生成）：{Token}", opsOptions.Token);
    }
    // 拦截所有未命中当前激活 Ops 路径的 /ops 与 /ops-* 请求，防止落入 SPA fallback 暴露后台入口。
    app.Use(async (context, next) =>
    {
        if (OpsPanel.IsBlockedOpsPath(context.Request.Path, opsOptions, app.Environment.IsDevelopment()))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next();
    });
    app.MapFallback("/api/{**path}", () => Results.NotFound(Problem(404, "NOT_FOUND", "接口不存在。")));
    if (hasWebRoot) app.MapFallbackToFile("index.html", staticFileOptions);
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
/// <param name="InvitationCode">可选邀请码；无效码使注册失败（INVALID_INVITATION_CODE）。</param>
/// <param name="RiskSignals">客户端采集的设备摘要；IP 和主体键由服务端生成。</param>
public sealed record RegisterRequest(string Account, string Password, string? DisplayName = null, string? InvitationCode = null, AccountRiskClientSignals? RiskSignals = null);
/// <summary>登录请求体。</summary>
/// <param name="Account">登录账号。</param>
/// <param name="Password">明文密码，仅用于与服务端哈希比对。</param>
/// <param name="RiskSignals">客户端采集的设备摘要，用于登录账号关联。</param>
public sealed record LoginRequest(string Account, string Password, AccountRiskClientSignals? RiskSignals = null);
/// <summary>修改密码请求体；CurrentPassword 为改密前的身份二次确认，明文仅存在于传输层。</summary>
/// <param name="CurrentPassword">当前密码明文。</param>
/// <param name="NewPassword">新密码明文。</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
/// <summary>顶级语句入口的 Program 类型占位，供测试项目引用入口程序集。</summary>
public partial class Program;
