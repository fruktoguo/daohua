using System.Diagnostics;
using Domain.Market;
using Protocol;

namespace Server.Clean.Ops;

/// <summary>
/// 本地运维信息面板（Development-only）。
/// 提供只读的概览、健康、玩家、会话、市场与日志视图；面板端口独立监听，
/// 通过 <see cref="UseOpsGate"/> 强制「仅运维端口 + 仅私网来源 + 仅面板路径」。
/// </summary>
public static class OpsPanel
{
    private static readonly string PageHtml = LoadPage();

    /// <summary>
    /// 注册端口门禁中间件：运维端口只接受面板路径与私网来源，其余端口访问面板路径一律 404。
    /// 必须早于路由与静态文件注册。
    /// </summary>
    /// <param name="app">应用构建器。</param>
    /// <param name="options">面板配置。</param>
    public static IApplicationBuilder UseOpsGate(this IApplicationBuilder app, OpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        return app.Use(async (context, next) =>
        {
            var isOpsPort = context.Connection.LocalPort == options.Port;
            var isOpsPath = context.Request.Path.StartsWithSegments(options.Path, StringComparison.OrdinalIgnoreCase);
            var blockedOnOpsPort = isOpsPort && (!isOpsPath || !OpsNetwork.IsPrivateOrLoopback(context.Connection.RemoteIpAddress));
            var blockedOnMainPort = !isOpsPort && isOpsPath;
            if (blockedOnOpsPort || blockedOnMainPort)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    /// <summary>
    /// 映射面板页面与只读 JSON 接口。
    /// </summary>
    /// <param name="app">应用构建器。</param>
    /// <param name="options">面板配置。</param>
    /// <param name="releaseVersion">服务端发布版本。</param>
    /// <param name="startedAt">进程启动时刻（UTC）。</param>
    public static void MapOpsPanel(this WebApplication app, OpsOptions options, string releaseVersion, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        var group = app.MapGroup(options.Path);

        // 路由匹配对尾斜杠不敏感，单个 "/ops" 路由同时覆盖 "/ops" 与 "/ops/"；
        // 重复注册会产生 AmbiguousMatchException。
        group.MapGet("", () => Results.Content(PageHtml, "text/html; charset=utf-8"));

        group.MapGet("/api/overview", (
            GameDataCatalog gameData,
            IPlayerRuntime players,
            SessionRegistry sessions,
            MarketRuntime market,
            ServerOptions serverOptions) => Results.Ok(new
        {
            service = serverOptions.ServiceName,
            version = releaseVersion,
            environment = app.Environment.EnvironmentName,
            machineName = Environment.MachineName,
            processId = Environment.ProcessId,
            startedAt,
            uptimeSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            workingSetBytes = Environment.WorkingSet,
            gcTotalMemoryBytes = GC.GetTotalMemory(false),
            processorCount = Environment.ProcessorCount,
            threadCount = Process.GetCurrentProcess().Threads.Count,
            protocolVersion = ProtocolConstants.CurrentVersion,
            schemaVersion = PostgresSchema.Version,
            gameDataVersion = gameData.ContentVersion,
            gameDataFingerprint = gameData.Fingerprint,
            loadedPlayers = players.LoadedCount,
            sessions = sessions.Count,
            market = market.GetMetrics()
        }));

        group.MapGet("/api/health", async (HealthCheckService health, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckHealthAsync(cancellationToken);
            return Results.Ok(new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    description = entry.Value.Description,
                    exception = entry.Value.Exception?.Message,
                    tags = entry.Value.Tags
                })
            });
        });

        group.MapGet("/api/players", async (IPlayerRuntime players, CancellationToken cancellationToken) =>
        {
            var snapshots = await players.GetLoadedSnapshotsAsync(cancellationToken);
            return Results.Ok(snapshots.Select(snapshot => new
            {
                playerId = snapshot.PlayerId,
                accountId = snapshot.AccountId,
                displayName = snapshot.DisplayName,
                stateVersion = snapshot.StateVersion,
                presence = snapshot.State.Presence.ToString().ToLowerInvariant(),
                lastSimulatedAt = snapshot.LastSimulatedAt,
                actionCount = snapshot.QueuedActionCount,
                assetCount = snapshot.Assets.Count
            }));
        });

        group.MapGet("/api/sessions", (SessionRegistry sessions) => Results.Ok(sessions.SnapshotPlayerIds()));

        group.MapGet("/api/market/book", (string? assetId, string? variant, int? depth, MarketRuntime market) =>
        {
            if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128)
                return Results.BadRequest(new { code = "INVALID_INSTRUMENT", message = "市场品种无效。" });
            var instrument = new MarketInstrument(new ItemId(assetId), variant ?? string.Empty);
            if (!market.Owns(instrument))
                return Results.Conflict(new { code = "WRONG_PARTITION", message = "该品种不属于当前市场分片。" });
            try
            {
                var snapshot = market.GetBook(instrument, Math.Clamp(depth ?? 20, 1, 200));
                return Results.Ok(new
                {
                    assetId = snapshot.Instrument.ItemId.Value,
                    variant = string.IsNullOrEmpty(snapshot.Instrument.Variant) ? null : snapshot.Instrument.Variant,
                    version = snapshot.Version,
                    capturedAt = snapshot.CapturedAt,
                    asks = snapshot.Asks.Select(level => new { price = level.Price, quantity = level.Quantity, orders = level.OrderCount }),
                    bids = snapshot.Bids.Select(level => new { price = level.Price, quantity = level.Quantity, orders = level.OrderCount })
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { code = "MARKET_NOT_READY", message = exception.Message });
            }
        });

        group.MapGet("/api/logs", (OpsLogBuffer buffer, string? level, int? limit) =>
        {
            var minLevel = Enum.TryParse<LogLevel>(level, true, out var parsed) ? parsed : LogLevel.Information;
            return Results.Ok(buffer.Snapshot(minLevel, limit ?? 200));
        });

        // 冷挂机派发链运维视图：节点组（名称/容量/在飞/拨入时长/运行时指纹）、
        // 链路计数快照（吞吐/失败分类/拒绝原因/批次样本分布）与 compute_jobs 队列积压。
        // 全部经 GetService 可空取用：非 Api 角色或最小测试宿主未装配派发链时返回 enabled=false。
        // 视图只含计数与协商字段，绝不输出令牌、连接串或玩家载荷。
        group.MapGet("/api/compute", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var services = context.RequestServices;
            var serverOptions = services.GetRequiredService<ServerOptions>();
            var registry = services.GetService<ComputeLinkRegistry>();
            var metrics = services.GetService<ComputeLinkMetrics>();
            var jobs = services.GetService<PostgresComputeJobRepository>();
            var nodes = registry?.Nodes ?? [];
            var backlog = jobs is null ? null : await jobs.GetBacklogAsync(cancellationToken);
            return Results.Ok(new
            {
                enabled = serverOptions.ComputeNodeConfigured,
                contractVersion = ComputeLinkContract.Version,
                dispatchContractVersion = ComputeDispatchContract.Version,
                totals = new
                {
                    nodeCount = nodes.Count,
                    capacity = nodes.Sum(node => node.Capacity),
                    inFlight = nodes.Sum(node => node.InFlight),
                    pendingResults = nodes.Sum(node => node.Pending.Count),
                    outboundDepth = nodes.Sum(node => Math.Max(0, node.OutboundDepth))
                },
                nodes = nodes.Select(ComputeLinkNodeView.From).OrderBy(view => view.Name, StringComparer.Ordinal).ToArray(),
                metrics = metrics?.Snapshot(),
                backlog
            });
        });
    }

    private static string LoadPage()
    {
        var assembly = typeof(OpsPanel).Assembly;
        using var stream = assembly.GetManifestResourceStream("Server.Ops.OpsPage.html")
            ?? throw new InvalidOperationException("缺少运维面板页面嵌入资源 Server.Ops.OpsPage.html。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
