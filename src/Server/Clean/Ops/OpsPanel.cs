using System.Diagnostics;
using Protocol;
using Server.Clean.AccountAbuse;

namespace Server.Clean.Ops;

/// <summary>踢下线请求。</summary>
/// <param name="PlayerId">目标玩家 ID。</param>
public sealed record OpsKickRequest(Guid PlayerId);

/// <summary>强制存档请求。</summary>
/// <param name="PlayerId">目标玩家 ID；为空表示全部已加载玩家。</param>
/// <param name="Force">是否强制落盘（忽略落盘节流）。</param>
public sealed record OpsFlushRequest(Guid? PlayerId, bool Force = true);

/// <summary>卸载玩家运行时请求。</summary>
/// <param name="PlayerId">目标玩家 ID。</param>
/// <param name="Flush">是否先落盘再卸载；默认 true（安全默认，避免丢弃内存改动）。</param>
public sealed record OpsUnloadRequest(Guid PlayerId, bool Flush = true);

/// <summary>计算节点排空请求。</summary>
/// <param name="NodeId">目标节点 ID；为空表示全部在线节点。</param>
public sealed record OpsDrainRequest(Guid? NodeId);

/// <summary>运维面板可修改的账号检测策略；所有阈值由服务端校验。</summary>
public sealed record OpsAccountRiskPolicyRequest(bool Enabled, bool RequireDeviceSignal, bool RegistrationEnabled, int MaxAccountsPerPerson,
    int MaxAccountsPerDevice, int RiskThreshold, string HighRiskAction, IReadOnlyList<AccountRiskModulePolicy> Modules);

/// <summary>
/// 本地运维面板（Development-only）。
/// 面板并入主 API 端口，页面由前端 React 应用承载；本类只提供 `/ops/api/*` 数据端点。
/// 只读视图依赖环境装配判定；写操作要求一次性令牌（`X-Ops-Token`），并记录审计日志。
/// </summary>
public static class OpsPanel
{
    /// <summary>运维踢出会话使用的自定义关闭码（私有段，避开防多开 4005-4008 与慢消费者 4009）。</summary>
    public const int KickCloseCode = 4010;

    /// <summary>写操作令牌请求头名。</summary>
    public const string TokenHeader = "X-Ops-Token";

    /// <summary>运维写操作审计日志类别。</summary>
    public const string AuditCategory = "Ops.Audit";

    /// <summary>
    /// 映射面板只读视图与令牌保护的写操作端点。
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

        group.MapGet("/api/account-abuse", async (PostgresAccountRiskRepository risk, CancellationToken ct) =>
            Results.Ok(await risk.GetOpsOverviewAsync(ct)));
        group.MapPost("/api/account-abuse/policy", async (HttpContext context, OpsAccountRiskPolicyRequest request,
            OpsOptions opts, PostgresAccountRiskRepository risk, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var current = await risk.LoadPolicyAsync(ct);
            var policy = current with { Enabled = request.Enabled, RequireDeviceSignal = request.RequireDeviceSignal, RegistrationEnabled = request.RegistrationEnabled,
                MaxAccountsPerPerson = request.MaxAccountsPerPerson, MaxAccountsPerDevice = request.MaxAccountsPerDevice,
                RiskThreshold = request.RiskThreshold, HighRiskAction = request.HighRiskAction, Modules = request.Modules };
            try { policy.Validate(); }
            catch (InvalidOperationException) { return Results.BadRequest(new { code = "INVALID_RISK_POLICY", message = "账号检测策略无效。" }); }
            await risk.ReplacePolicyAsync(policy, ct);
            Audit(loggerFactory, context, "account-risk-policy", null);
            return Results.Ok(new { action = "account-risk-policy", updated = true });
        });

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

        // 玩家详情只覆盖「已加载」玩家：运维面板不得因查看详情而触发状态加载（那会把冷玩家拉进内存并占租约）。
        group.MapGet("/api/players/{playerId:guid}", async (Guid playerId, IPlayerRuntime players, CancellationToken cancellationToken) =>
        {
            var snapshots = await players.GetLoadedSnapshotsAsync(cancellationToken);
            var snapshot = snapshots.FirstOrDefault(item => item.PlayerId == playerId);
            if (snapshot is null) return Results.Json(new { code = "PLAYER_NOT_LOADED", message = "该玩家当前未加载到运行时。" }, statusCode: StatusCodes.Status404NotFound);

            var state = snapshot.State;
            return Results.Ok(new
            {
                playerId = snapshot.PlayerId,
                accountId = snapshot.AccountId,
                displayName = snapshot.DisplayName,
                stateVersion = snapshot.StateVersion,
                presence = state.Presence.ToString().ToLowerInvariant(),
                createdAt = state.CreatedAt,
                lastSimulatedAt = snapshot.LastSimulatedAt,
                gameDataFingerprint = state.GameDataFingerprint,
                progression = new
                {
                    realmLevel = state.Progression.RealmLevel,
                    experience = state.Progression.Experience,
                    completedActions = state.Progression.CompletedActions,
                    cultivationSeconds = state.Progression.CultivationSeconds,
                    normalKills = state.Progression.NormalKills,
                    eliteKills = state.Progression.EliteKills,
                    bossKills = state.Progression.BossKills,
                    totalDefeats = state.Progression.TotalDefeats
                },
                combat = new
                {
                    hitpoints = state.Combat.Hitpoints,
                    maxHitpoints = state.Combat.MaxHitpoints,
                    manapoints = state.Combat.Manapoints,
                    maxManapoints = state.Combat.MaxManapoints,
                    attackPower = state.Combat.AttackPower,
                    defense = state.Combat.Defense
                },
                world = new
                {
                    nodeId = state.World.NodeId,
                    x = state.World.X,
                    y = state.World.Y,
                    edgeId = state.World.EdgeId,
                    edgeProgress = state.World.EdgeProgress
                },
                counts = new
                {
                    actions = state.Actions.Count,
                    assets = snapshot.Assets.Count,
                    buffs = state.Buffs.Count,
                    learnedAbilities = state.LearnedAbilities.Count,
                    equippedInstances = state.EquippedInstances.Count
                },
                actions = state.Actions.Select(action => new
                {
                    id = action.Id,
                    actionId = action.Intent.ActionId.Value,
                    status = action.Status.ToString(),
                    enqueuedAt = action.EnqueuedAt,
                    progressSeconds = action.ProgressSeconds,
                    remainingRepeats = action.RemainingRepeats,
                    completedRepeats = action.CompletedRepeats,
                    targetNodeId = action.Intent.TargetNodeId,
                    anchorNodeId = action.Intent.AnchorNodeId
                }),
                assets = snapshot.Assets.Select(asset => new
                {
                    assetId = asset.AssetId,
                    variant = asset.Variant,
                    quantity = asset.Quantity,
                    version = asset.Version
                }),
                touchedSections = snapshot.TouchedSections?.Select(kind => kind.ToString()).ToArray(),
                recentLogs = players.GetRecentLogEvents(playerId).TakeLast(50).Select(entry => new
                {
                    sequence = entry.Sequence,
                    timestampUnixMilliseconds = entry.TimestampUnixMilliseconds,
                    template = entry.Template,
                    count = entry.Count,
                    channel = entry.Channel
                })
            });
        });

        group.MapGet("/api/sessions", (SessionRegistry sessions) => Results.Ok(
            sessions.SnapshotPlayerIds().Select(playerId => DescribeSession(sessions, playerId))));

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

        group.MapGet("/api/market/metrics", (MarketRuntime market) =>
        {
            try
            {
                return Results.Ok(new
                {
                    ready = market.IsReady,
                    partitionId = market.PartitionId,
                    partitionCount = market.PartitionCount,
                    algorithm = market.Algorithm,
                    metrics = market.GetMetrics()
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new { code = "MARKET_NOT_READY", message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/api/market/orders", (Guid? playerId, int? limit, MarketRuntime market) =>
        {
            if (playerId is null || playerId == Guid.Empty)
                return Results.BadRequest(new { code = "INVALID_PLAYER", message = "必须提供 playerId。" });
            try
            {
                return Results.Ok(market.GetOrders(new PlayerId(playerId.Value), Math.Clamp(limit ?? 100, 1, 500))
                    .Select(MarketDtoMapper.ToOrder));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new { code = "MARKET_NOT_READY", message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/api/market/trades", async (int? limit, OpsStorageInsights insights, CancellationToken cancellationToken) =>
            Results.Ok(await insights.GetRecentTradesAsync(limit ?? 50, cancellationToken)));

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

        group.MapGet("/api/storage", async (OpsStorageInsights insights, CancellationToken cancellationToken) =>
            Results.Ok(await insights.GetStorageAsync(cancellationToken)));

        // 调度视图只做「已加载玩家快照」的聚合，不触发任何加载，也不进入模拟循环。
        group.MapGet("/api/scheduler", async (IPlayerRuntime players, CancellationToken cancellationToken) =>
        {
            var snapshots = await players.GetLoadedSnapshotsAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var lags = snapshots.Select(snapshot => Math.Max(0, (now - snapshot.LastSimulatedAt).TotalSeconds)).OrderBy(value => value).ToArray();
            var queueLengths = snapshots.Select(snapshot => (double)snapshot.QueuedActionCount).OrderBy(value => value).ToArray();
            return Results.Ok(new
            {
                loadedPlayers = snapshots.Count,
                nextAdvanceBoundaryUtc = players.NextAdvanceBoundaryUtc,
                secondsToNextBoundary = players.NextAdvanceBoundaryUtc is { } boundary ? Math.Max(0, (boundary - now).TotalSeconds) : (double?)null,
                lag = new
                {
                    p50Seconds = Percentile(lags, 0.50),
                    p95Seconds = Percentile(lags, 0.95),
                    maxSeconds = lags.Length == 0 ? 0 : lags[^1]
                },
                queuedActions = new
                {
                    total = queueLengths.Sum(),
                    p50 = Percentile(queueLengths, 0.50),
                    max = queueLengths.Length == 0 ? 0 : queueLengths[^1]
                },
                byPresence = snapshots
                    .GroupBy(snapshot => snapshot.State.Presence.ToString().ToLowerInvariant())
                    .Select(group => new { presence = group.Key, count = group.Count() })
                    .OrderBy(item => item.presence)
                    .ToArray(),
                slowest = snapshots
                    .OrderByDescending(snapshot => snapshot.LastSimulatedAt)
                    .Take(20)
                    .Select(snapshot => new
                    {
                        playerId = snapshot.PlayerId,
                        displayName = snapshot.DisplayName,
                        lastSimulatedAt = snapshot.LastSimulatedAt,
                        lagSeconds = Math.Max(0, (now - snapshot.LastSimulatedAt).TotalSeconds),
                        queuedActions = snapshot.QueuedActionCount
                    })
                    .ToArray()
            });
        });

        group.MapGet("/api/config", (GameDataCatalog gameData, ServerOptions serverOptions, AccountRiskPolicyRegistry riskPolicies) => Results.Ok(new
        {
            service = serverOptions.ServiceName,
            environment = app.Environment.EnvironmentName,
            processRole = serverOptions.ProcessRole.ToString(),
            protocolVersion = ProtocolConstants.CurrentVersion,
            schemaVersion = PostgresSchema.Version,
            gameDataVersion = gameData.ContentVersion,
            gameDataFingerprint = gameData.Fingerprint,
            storage = new
            {
                target = DescribeConnection(serverOptions.ConnectionString),
                schemaBootstrap = serverOptions.EnableSchemaBootstrap,
                sectionFlushBatchSize = serverOptions.SectionFlushBatchSize,
                sectionFlushIntervalMs = serverOptions.SectionFlushInterval.TotalMilliseconds,
                sectionFlushLeaseSeconds = serverOptions.SectionFlushLease.TotalSeconds,
                outboxBatchSize = serverOptions.OutboxBatchSize,
                outboxPollIntervalMs = serverOptions.OutboxPollInterval.TotalMilliseconds,
                retentionBatchSize = serverOptions.RetentionBatchSize
            },
            auth = new { sessionLifetimeDays = serverOptions.SessionLifetime.TotalDays },
            server = new
            {
                maxWebSocketMessageBytes = serverOptions.MaxWebSocketMessageBytes,
                webSocketOutboundCapacity = serverOptions.WebSocketOutboundCapacity,
                ecsShardCount = serverOptions.EcsShardCount
            },
            market = new { partitionCount = serverOptions.MarketPartitionCount, partitionId = serverOptions.MarketPartitionId },
            compute = new
            {
                enabled = serverOptions.ComputeNodeConfigured,
                batchSize = serverOptions.ComputeBatchSize,
                pollIntervalMs = serverOptions.ComputePollInterval.TotalMilliseconds,
                leaseSeconds = serverOptions.ComputeLease.TotalSeconds,
                maxAttempts = serverOptions.ComputeMaxAttempts,
                overflowAfterSeconds = serverOptions.ComputeOverflowAfter.TotalSeconds,
                coldCatchUpIntervalSeconds = serverOptions.ColdCatchUpInterval.TotalSeconds,
                coldCatchUpBatchSize = serverOptions.ColdCatchUpBatchSize,
                offlineBatchIntervalMinutes = serverOptions.OfflineBatchInterval.TotalMinutes,
                offlineIdleLimitHours = Domain.Player.PlayerLifecycleRules.OfflineIdleLimit.TotalHours
            },
            antiMultiOpen = new
            {
                tabGuardEnabled = riskPolicies.Current.Enabled && riskPolicies.Current.Modules.Single(item => item.Id == "tabGuard").Enabled,
                singleSessionKickEnabled = riskPolicies.Current.Enabled && riskPolicies.Current.Modules.Single(item => item.Id == "singleSession").Enabled,
                deviceQuotaEnabled = riskPolicies.Current.Enabled && riskPolicies.Current.Modules.Single(item => item.Id == "onlineDeviceQuota").Enabled,
                deviceQuotaMaxAccounts = riskPolicies.Current.MaxAccountsPerPerson
            }
        }));

        group.MapGet("/api/logs", (OpsLogBuffer buffer, string? level, string? category, string? q, int? limit) =>
        {
            var minLevel = Enum.TryParse<LogLevel>(level, true, out var parsed) ? parsed : LogLevel.Information;
            return Results.Ok(buffer.Snapshot(minLevel, limit ?? 200, category, q));
        });

        group.MapPost("/api/actions/kick", (HttpContext context, OpsKickRequest request, OpsOptions opts, SessionRegistry sessions, ILoggerFactory loggerFactory) =>
        {
            if (RejectWriteRequest(context, opts) is { } rejected) return rejected;
            if (!sessions.TryGet(request.PlayerId, out var session))
                return Results.Json(new { code = "SESSION_NOT_FOUND", message = "该玩家当前没有在线会话。" }, statusCode: StatusCodes.Status404NotFound);
            session.RequestClose(KickCloseCode, "ops kick");
            Audit(loggerFactory, context, "kick", request.PlayerId);
            return Results.Ok(new { action = "kick", playerId = request.PlayerId, closeCode = KickCloseCode });
        });

        group.MapPost("/api/actions/flush", async (HttpContext context, OpsFlushRequest request, OpsOptions opts, IPlayerRuntime players, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (RejectWriteRequest(context, opts) is { } rejected) return rejected;
            try
            {
                if (request.PlayerId is { } playerId && playerId != Guid.Empty)
                {
                    await players.FlushAsync(playerId, request.Force, cancellationToken);
                    Audit(loggerFactory, context, "flush", playerId);
                    return Results.Ok(new { action = "flush", scope = "player", playerId, force = request.Force });
                }
                await players.FlushAllAsync(request.Force, cancellationToken);
                Audit(loggerFactory, context, "flush-all", null);
                return Results.Ok(new { action = "flush", scope = "all", loadedPlayers = players.LoadedCount, force = request.Force });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new { code = "FLUSH_FAILED", message = exception.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapPost("/api/actions/unload", async (HttpContext context, OpsUnloadRequest request, OpsOptions opts, IPlayerRuntime players, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (RejectWriteRequest(context, opts) is { } rejected) return rejected;
            if (request.PlayerId == Guid.Empty)
                return Results.BadRequest(new { code = "INVALID_PLAYER", message = "必须提供 playerId。" });
            await players.RemoveAsync(request.PlayerId, request.Flush, null, cancellationToken);
            Audit(loggerFactory, context, "unload", request.PlayerId);
            return Results.Ok(new { action = "unload", playerId = request.PlayerId, flushed = request.Flush });
        });

        group.MapPost("/api/actions/compute-drain", (HttpContext context, OpsDrainRequest request, OpsOptions opts, ComputeLinkRegistry registry, ILoggerFactory loggerFactory) =>
        {
            if (RejectWriteRequest(context, opts) is { } rejected) return rejected;
            if (request.NodeId is { } nodeId && nodeId != Guid.Empty)
            {
                var node = registry.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
                if (node is null)
                    return Results.Json(new { code = "NODE_NOT_FOUND", message = "该计算节点当前不在线。" }, statusCode: StatusCodes.Status404NotFound);
                registry.BeginDrain(node);
                Audit(loggerFactory, context, "compute-drain", nodeId);
                return Results.Ok(new { action = "compute-drain", scope = "node", nodeId, name = node.Name });
            }
            registry.DrainAll("ops drain");
            Audit(loggerFactory, context, "compute-drain-all", null);
            return Results.Ok(new { action = "compute-drain", scope = "all", nodeCount = registry.NodeCount });
        });

        // 市场快捷操作：系统身份批量挂售/收购与撤单（端点实现在 OpsMarketActions）。
        group.MapOpsMarketActions();
    }

    /// <summary>会话只读视图：玩家 ID、准入设备指纹与是否已在关闭流程。</summary>
    private static object DescribeSession(SessionRegistry sessions, Guid playerId)
    {
        if (!sessions.TryGet(playerId, out var session)) return new { playerId, deviceFingerprint = (string?)null, closing = false };
        return new { playerId, deviceFingerprint = session.DeviceFingerprint, closing = session.CloseHint is not null };
    }

    /// <summary>
    /// 校验写操作来源与令牌：要求回环/私网来源（纵深防御）且令牌匹配。
    /// 不通过时返回拒绝结果，通过时返回 null。
    /// </summary>
    internal static IResult? RejectWriteRequest(HttpContext context, OpsOptions options)
    {
        if (!OpsNetwork.IsPrivateOrLoopback(context.Connection.RemoteIpAddress))
            return Results.Json(new { code = "OPS_SOURCE_REJECTED", message = "运维写操作仅允许本机或私有网段来源。" }, statusCode: StatusCodes.Status403Forbidden);
        var provided = context.Request.Headers[TokenHeader].ToString();
        var expected = options.Token;
        var matches = provided.Length > 0
            && expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
        return matches
            ? null
            : Results.Json(new { code = "OPS_TOKEN_INVALID", message = "运维令牌缺失或无效。" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>记录运维写操作审计（含来源地址，绝不记录令牌）。</summary>
    internal static void Audit(ILoggerFactory loggerFactory, HttpContext context, string action, Guid? target) =>
        loggerFactory.CreateLogger(AuditCategory).LogWarning(
            "运维面板写操作 {Action} 目标 {Target} 来源 {Remote}",
            action,
            target?.ToString() ?? "all",
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>连接串脱敏摘要：只保留主机、端口、库名与用户名，绝不输出口令。</summary>
    private static string DescribeConnection(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}@{builder.Username}";
        }
        catch (ArgumentException)
        {
            return "unparsable";
        }
    }

    /// <summary>有序数组的最近秩分位数；空数组返回 0。</summary>
    private static double Percentile(double[] sorted, double ratio)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(Math.Ceiling(ratio * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
