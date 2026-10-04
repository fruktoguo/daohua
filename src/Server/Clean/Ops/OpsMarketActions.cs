using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Ops;

/// <summary>系统身份批量下单请求。</summary>
/// <param name="Side">订单方向：sell（挂售）或 buy（收购）。</param>
/// <param name="Scope">范围：item（单个物品）/ category（按主类型）/ all（全部可回收物品）。</param>
/// <param name="ItemId">Scope=item 时的物品 ID。</param>
/// <param name="Category">Scope=category 时的物品主类型。</param>
/// <param name="Price">限价；缺省用回收价（sellPrice）。</param>
/// <param name="Quantity">委托数量；缺省 1 亿份（对齐种子任务口径）。</param>
public sealed record OpsSystemOrderRequest(
    string Side,
    string Scope,
    string? ItemId,
    string? Category,
    double? Price,
    double? Quantity);

/// <summary>系统挂单撤销请求。</summary>
/// <param name="AssetId">目标品种；空表示撤销全部活动系统挂单。</param>
public sealed record OpsSystemCancelRequest(string? AssetId);

/// <summary>
/// 运维面板市场快捷操作（Development-only）。
/// 以系统身份（与 scripts/seed-market-system-listings.sh 同一固定 UUID）批量挂售/收购与撤单；
/// 下单走 MarketRuntime 正常链路：先经 IEconomyTransactionStore 注入资金（system-grant 流水），
/// 再 PlaceAsync——其持久化事务自带冻结扣减、冻结预留、经济流水与即时撮合，即时上架无需重启。
/// </summary>
public static class OpsMarketActions
{
    /// <summary>系统身份固定 UUID：账号（f 前缀保留段，与种子脚本对齐）。</summary>
    public static readonly Guid SystemAccountId = new("f0000000-0000-4000-8000-000000000001");

    /// <summary>系统身份固定 UUID：货架玩家（市场卖方/买方主体）。</summary>
    public static readonly Guid SystemPlayerId = new("f0000000-0000-4000-8000-000000000002");

    private const string SystemLogin = "system-market@daohua.internal";
    private const string SystemDisplayName = "系统坊市";
    private const string CoinsAsset = "currency.spirit-stone";
    private const double DefaultQuantity = 100_000_000;
    private const double MaxQuantity = 1e12;
    private const int CancelRounds = 10;

    /// <summary>映射市场快捷操作端点（由 OpsPanel 在面板装配时统一挂载）。</summary>
    internal static void MapOpsMarketActions(this IEndpointRouteBuilder group)
    {
        // 可挂售物品目录：非货币 + 可交易 + 有回收价，与种子脚本「可回收物品」口径一致；
        // 装备无回收价目，不在目录内（系统挂售无定价基准）。
        group.MapGet("/api/market/catalog", (GameDataCatalog gameData) =>
        {
            var items = ListRecyclableItems(gameData)
                .Select(item => new
                {
                    id = item.Id.Value,
                    name = item.Name,
                    category = item.Category,
                    subCategory = item.DisplaySubCategory,
                    sellPrice = item.SellPrice
                })
                .ToArray();
            var categories = items.Select(item => item.category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return Results.Ok(new { items, categories });
        });

        group.MapPost("/api/actions/market-system-orders", async (
            HttpContext context,
            OpsSystemOrderRequest request,
            OpsOptions opts,
            CancellationToken cancellationToken) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            // 经济事务存储经 RequestServices 解析：面板路由整组一次性构建，
            // 端点参数在最小测试宿主可能未注册会让整组推断失败（与 compute 端点同口径）。
            var services = context.RequestServices;
            var economy = services.GetRequiredService<IEconomyTransactionStore>();
            if (!Enum.TryParse<MarketSide>(request.Side, true, out var side))
                return Results.BadRequest(new { code = "INVALID_SIDE", message = "订单方向必须是 sell 或 buy。" });
            var gameData = services.GetRequiredService<GameDataCatalog>();
            var targets = ResolveTargets(gameData, request, out var scopeError);
            if (targets is null)
                return Results.BadRequest(new { code = "INVALID_SCOPE", message = scopeError });
            if (request.Price is { } price && (!double.IsFinite(price) || price <= 0 || price > MaxQuantity))
                return Results.BadRequest(new { code = "INVALID_PRICE", message = "价格必须是有限正数。" });
            if (request.Quantity is { } quantity && (!double.IsFinite(quantity) || quantity <= 0 || quantity > MaxQuantity))
                return Results.BadRequest(new { code = "INVALID_QUANTITY", message = "数量必须是有限正数。" });
            var market = services.GetRequiredService<MarketRuntime>();
            if (!market.IsReady)
                return Results.Json(new { code = "MARKET_NOT_READY", message = "市场分片尚未就绪。" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var sideText = side == MarketSide.Buy ? "buy" : "sell";
            var batchId = Guid.NewGuid();
            var outcomes = new List<OpsItemOutcome>(targets.Count);
            await EnsureSystemIdentityAsync(services.GetRequiredService<NpgsqlDataSource>(), cancellationToken);
            foreach (var item in targets)
            {
                var itemPrice = request.Price ?? item.SellPrice;
                var itemQuantity = request.Quantity ?? DefaultQuantity;
                outcomes.Add(await PlaceSystemOrderAsync(
                    market, economy, batchId, side, item, itemPrice, itemQuantity, cancellationToken));
            }
            OpsPanel.Audit(services.GetRequiredService<ILoggerFactory>(), context, $"market-system-orders:{sideText}", null);
            return Results.Ok(new
            {
                batchId,
                side = sideText,
                scope = request.Scope,
                requested = targets.Count,
                placed = outcomes.Count(outcome => outcome.Status == "placed"),
                failed = outcomes.Count(outcome => outcome.Status == "failed"),
                results = outcomes
            });
        });

        group.MapPost("/api/actions/market-system-cancel", async (
            HttpContext context,
            OpsSystemCancelRequest request,
            OpsOptions opts,
            MarketRuntime market,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var assetFilter = string.IsNullOrWhiteSpace(request.AssetId) ? null : request.AssetId.Trim();
            if (assetFilter is { Length: > 128 })
                return Results.BadRequest(new { code = "INVALID_ASSET", message = "品种 ID 无效。" });
            if (!market.IsReady)
                return Results.Json(new { code = "MARKET_NOT_READY", message = "市场分片尚未就绪。" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var owner = new PlayerId(SystemPlayerId);
            var outcomes = new List<OpsItemOutcome>();
            // 分轮拉取活动系统订单逐单撤销，直到无匹配活动订单或达到轮次上限；
            // OrdersByOwner 只返回活动订单，撤单后下一轮自然收敛。
            for (var round = 0; round < CancelRounds; round++)
            {
                var orders = market.GetOrders(owner, 500)
                    .Where(order => assetFilter is null
                        || string.Equals(order.Instrument.ItemId.Value, assetFilter, StringComparison.Ordinal))
                    .ToArray();
                if (orders.Length == 0) break;
                foreach (var order in orders)
                {
                    try
                    {
                        await market.CancelAsync(order.OrderId, owner, order.Instrument,
                            $"market-cancel:{SystemPlayerId:D}:{order.OrderId.Value:D}", cancellationToken);
                        outcomes.Add(new OpsItemOutcome(
                            order.Instrument.ItemId.Value, string.Empty, order.Price, order.RemainingQuantity,
                            "cancelled", order.OrderId.Value, 0, null));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or DomainException or PostgresException)
                    {
                        outcomes.Add(new OpsItemOutcome(
                            order.Instrument.ItemId.Value, string.Empty, order.Price, order.RemainingQuantity,
                            "failed", order.OrderId.Value, 0, exception.Message));
                    }
                }
            }
            OpsPanel.Audit(loggerFactory, context, "market-system-cancel", null);
            return Results.Ok(new
            {
                action = "market-system-cancel",
                scope = assetFilter ?? "all",
                cancelled = outcomes.Count(outcome => outcome.Status == "cancelled"),
                failed = outcomes.Count(outcome => outcome.Status == "failed"),
                results = outcomes
            });
        });
    }

    /// <summary>单品种下单/撤单回执。</summary>
    /// <param name="AssetId">品种资产 ID。</param>
    /// <param name="Name">物品名（撤单场景无配表上下文，为空串）。</param>
    /// <param name="Price">限价。</param>
    /// <param name="Quantity">委托数量（撤单为剩余数量）。</param>
    /// <param name="Status">placed / cancelled / failed。</param>
    /// <param name="OrderId">订单标识（失败且未建单时为 null）。</param>
    /// <param name="Fills">下单即时撮合产生的成交笔数。</param>
    /// <param name="Error">失败原因。</param>
    private sealed record OpsItemOutcome(
        string AssetId,
        string Name,
        double Price,
        double Quantity,
        string Status,
        Guid? OrderId,
        int Fills,
        string? Error);

    /// <summary>可挂售物品口径：非货币 + 可交易 + 有回收价，按 ID 排序保证输出稳定。</summary>
    private static IReadOnlyList<ItemDefinition> ListRecyclableItems(GameDataCatalog gameData) => gameData.Items.Values
        .Where(item => item.IsTradable
            && item.SellPrice > 0
            && !item.Id.Value.StartsWith("currency.", StringComparison.Ordinal))
        .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
        .ToArray();

    /// <summary>按范围解析目标物品；解析失败返回 null 并给出错误信息。</summary>
    private static IReadOnlyList<ItemDefinition>? ResolveTargets(GameDataCatalog gameData, OpsSystemOrderRequest request, out string? error)
    {
        var recyclable = ListRecyclableItems(gameData);
        switch (request.Scope)
        {
            case "item":
                var target = recyclable.FirstOrDefault(candidate => candidate.Id.Value == request.ItemId?.Trim());
                if (target is null)
                {
                    error = "物品不存在或不在可挂售目录（口径：非货币、可交易、有回收价）。";
                    return null;
                }
                error = null;
                return [target];
            case "category":
                var category = request.Category?.Trim();
                if (string.IsNullOrEmpty(category))
                {
                    error = "必须提供品类。";
                    return null;
                }
                var matched = recyclable.Where(candidate => candidate.Category == category).ToArray();
                if (matched.Length == 0)
                {
                    error = $"品类 {category} 下没有可挂售物品。";
                    return null;
                }
                error = null;
                return matched;
            case "all":
                if (recyclable.Count == 0)
                {
                    error = "配表中没有可挂售物品。";
                    return null;
                }
                error = null;
                return recyclable;
            default:
                error = "范围必须是 item、category 或 all。";
                return null;
        }
    }

    /// <summary>
    /// 为单个品种注入资金并以系统身份下单：卖单注入物品份数、买单注入灵石金额，
    /// 随后 PlaceAsync 在同一持久化事务内完成冻结扣减、预留与撮合。
    /// </summary>
    private static async ValueTask<OpsItemOutcome> PlaceSystemOrderAsync(
        MarketRuntime market,
        IEconomyTransactionStore economy,
        Guid batchId,
        MarketSide side,
        ItemDefinition item,
        double price,
        double quantity,
        CancellationToken cancellationToken)
    {
        var sideText = side == MarketSide.Buy ? "buy" : "sell";
        try
        {
            var instrument = new MarketInstrument(item.Id, string.Empty);
            if (!market.Owns(instrument))
                throw new InvalidOperationException("该品种不属于当前市场分片。");
            var orderId = DeterministicGuid(batchId, $"order|{sideText}|{item.Id.Value}");
            // 先注入资金再下单：PlaceAsync 的持久化事务会冻结扣减，余额不足会快速失败。
            var funding = side == MarketSide.Buy
                ? new AssetDelta(SystemPlayerId, CoinsAsset, string.Empty, price * quantity)
                : new AssetDelta(SystemPlayerId, item.Id.Value, string.Empty, quantity);
            await economy.CommitAsync(new EconomyTransactionRequest(
                DeterministicGuid(batchId, $"grant|{sideText}|{item.Id.Value}"),
                $"system-grant:{orderId:D}",
                "system-grant",
                SystemPlayerId,
                [funding]), cancellationToken);
            var placement = await market.PlaceAsync(new PlaceOrderRequest(
                new OrderId(orderId),
                new PlayerId(SystemPlayerId),
                instrument,
                side,
                price,
                quantity,
                DateTimeOffset.UtcNow,
                new IdempotencyKey($"system-ops:{batchId:D}:{sideText}:{item.Id.Value}")), cancellationToken);
            return new OpsItemOutcome(item.Id.Value, item.Name, price, quantity, "placed", orderId, placement.Fills.Count, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DomainException or PostgresException)
        {
            return new OpsItemOutcome(item.Id.Value, item.Name, price, quantity, "failed", null, 0, exception.Message);
        }
    }

    /// <summary>确保系统身份行存在（不可登录账号 + 货架玩家，固定 UUID 幂等）。</summary>
    private static async ValueTask EnsureSystemIdentityAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var account = new NpgsqlCommand("""
            INSERT INTO identity.accounts
                (account_id, login_normalized, password_hash, password_salt, password_iterations, status, created_at, updated_at)
            VALUES ($1, $2, $3, $4, 100000, 1, now(), now())
            ON CONFLICT (account_id) DO NOTHING
            """, connection, transaction))
        {
            account.Parameters.Add(new NpgsqlParameter { Value = SystemAccountId });
            account.Parameters.Add(new NpgsqlParameter { Value = SystemLogin });
            account.Parameters.Add(new NpgsqlParameter { Value = RandomNumberGenerator.GetBytes(32) });
            account.Parameters.Add(new NpgsqlParameter { Value = RandomNumberGenerator.GetBytes(32) });
            await account.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var player = new NpgsqlCommand("""
            INSERT INTO identity.players
                (player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
            VALUES ($1, $2, $3, $3, now(), now(), 1)
            ON CONFLICT (player_id) DO NOTHING
            """, connection, transaction))
        {
            player.Parameters.Add(new NpgsqlParameter { Value = SystemPlayerId });
            player.Parameters.Add(new NpgsqlParameter { Value = SystemAccountId });
            player.Parameters.Add(new NpgsqlParameter { Value = SystemDisplayName });
            await player.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>按 (batchId, 名称) 确定性生成 UUIDv4 形态标识：同批请求重试得到同一订单/流水。</summary>
    private static Guid DeterministicGuid(Guid batchId, string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"daohua:ops-market:v1:{batchId:D}|{name}"));
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
