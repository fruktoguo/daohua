using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Ops;

/// <summary>系统身份批量下单请求。</summary>
/// <param name="Side">订单方向：sell（挂售）或 buy（收购）。</param>
/// <param name="Scope">范围：item（单个品种）/ category（按主类型）/ all（全部可挂售品种：可回收物品 + 装备）。</param>
/// <param name="ItemId">Scope=item 时的品种 ID（item.* 或 equipment.*）。</param>
/// <param name="Category">Scope=category 时的品种主类型（装备为 equipment）。</param>
/// <param name="Price">限价；缺省物品用回收价（sellPrice）、装备用锻造材料成本价。</param>
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
        // 可挂售品种目录：非货币 + 可交易 + 有回收价的物品，与全部装备（category=equipment、
        // subCategory=槽位）；sellPrice 字段语义为「缺省挂单价」——物品=回收价、装备=锻造材料成本价，
        // 与种子脚本 seed-market-system-listings.sh 口径一致。
        group.MapGet("/api/market/catalog", (GameDataCatalog gameData) =>
        {
            var items = ListOrderableTargets(gameData)
                .Select(target => new
                {
                    id = target.AssetId,
                    name = target.Name,
                    category = target.Category,
                    subCategory = target.SubCategory,
                    sellPrice = target.Price
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
            foreach (var target in targets)
            {
                var itemPrice = request.Price ?? target.Price;
                var itemQuantity = request.Quantity ?? DefaultQuantity;
                outcomes.Add(await PlaceSystemOrderAsync(
                    market, economy, batchId, side, target, itemPrice, itemQuantity, cancellationToken));
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

    /// <summary>
    /// 系统下单目标品种：可回收物品与装备的统一口径。
    /// </summary>
    /// <param name="AssetId">品种资产 ID（item.* 或 equipment.*）。</param>
    /// <param name="Name">品种展示名。</param>
    /// <param name="Category">主类型；装备固定为 equipment。</param>
    /// <param name="SubCategory">子类型；装备为槽位（weapon/head/armor/…）。</param>
    /// <param name="Price">缺省挂单价：物品=回收价，装备=锻造材料成本价。</param>
    private sealed record OpsMarketTarget(string AssetId, string Name, string Category, string SubCategory, double Price);

    /// <summary>
    /// 可挂售品种口径：非货币 + 可交易 + 有回收价的物品，以及全部装备
    /// （category=equipment、subCategory=槽位、缺省价=锻造材料成本价），按 ID 排序保证输出稳定。
    /// </summary>
    private static IReadOnlyList<OpsMarketTarget> ListOrderableTargets(GameDataCatalog gameData)
    {
        var targets = new List<OpsMarketTarget>(gameData.Items.Count + gameData.RawEquipments.Count);
        targets.AddRange(gameData.Items.Values
            .Where(item => item.IsTradable
                && item.SellPrice > 0
                && !item.Id.Value.StartsWith("currency.", StringComparison.Ordinal))
            .Select(item => new OpsMarketTarget(item.Id.Value, item.Name, item.Category, item.DisplaySubCategory, item.SellPrice)));
        // 装备无回收价目，以锻造配方材料成本价定价；无配方或材料无价的装备无法定价，跳过。
        var materialCosts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, equipment) in gameData.RawEquipments)
        {
            if (ComputeEquipmentMaterialCost(gameData, id, materialCosts, 0) is not { } cost) continue;
            var name = equipment.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? id : id;
            var slot = equipment.TryGetProperty("slot", out var slotProperty) ? slotProperty.GetString() ?? "equipment" : "equipment";
            targets.Add(new OpsMarketTarget(id, name, "equipment", slot, cost));
        }
        return targets.OrderBy(target => target.AssetId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 装备材料成本价：定位 yields 产出该装备的锻造行动，inputs 中 item.* 按回收价×数量求和、
    /// equipment.* 投入递归展开（T2 配方含 T1 装备投入）；递归深度上限对齐种子脚本口径。
    /// </summary>
    private static double? ComputeEquipmentMaterialCost(GameDataCatalog gameData, string equipmentId,
        Dictionary<string, double> cache, int depth)
    {
        const int MaxRecipeDepth = 4;
        if (depth > MaxRecipeDepth)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"装备配方递归超过深度上限（疑似配表成环）：{equipmentId}。");
        if (cache.TryGetValue(equipmentId, out var cached)) return cached;
        var recipe = gameData.Actions.All.FirstOrDefault(action =>
            action.Yields.Any(yield => yield.Item is { } product && product.Value == equipmentId));
        if (recipe is null) return null;
        double cost = 0;
        foreach (var (input, quantity) in recipe.Inputs)
        {
            if (input.Value.StartsWith("equipment.", StringComparison.Ordinal))
            {
                if (ComputeEquipmentMaterialCost(gameData, input.Value, cache, depth + 1) is not { } nested) return null;
                cost += nested * quantity;
            }
            else if (gameData.Items.TryGetValue(input, out var material) && material.SellPrice > 0)
            {
                cost += material.SellPrice * quantity;
            }
            else
            {
                // 配方材料不在物品目录或无回收价 → 该装备无法定价。
                return null;
            }
        }
        cache[equipmentId] = cost;
        return cost;
    }

    /// <summary>按范围解析目标品种；解析失败返回 null 并给出错误信息。</summary>
    private static IReadOnlyList<OpsMarketTarget>? ResolveTargets(GameDataCatalog gameData, OpsSystemOrderRequest request, out string? error)
    {
        var orderable = ListOrderableTargets(gameData);
        switch (request.Scope)
        {
            case "item":
                var target = orderable.FirstOrDefault(candidate => candidate.AssetId == request.ItemId?.Trim());
                if (target is null)
                {
                    error = "品种不存在或不在可挂售目录（口径：非货币可交易物品，或装备）。";
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
                var matched = orderable.Where(candidate => candidate.Category == category).ToArray();
                if (matched.Length == 0)
                {
                    error = $"品类 {category} 下没有可挂售品种。";
                    return null;
                }
                error = null;
                return matched;
            case "all":
                if (orderable.Count == 0)
                {
                    error = "配表中没有可挂售品种。";
                    return null;
                }
                error = null;
                return orderable;
            default:
                error = "范围必须是 item、category 或 all。";
                return null;
        }
    }

    /// <summary>
    /// 为单个品种注入资金并以系统身份下单：卖单注入品种份数、买单注入灵石金额，
    /// 随后 PlaceAsync 在同一持久化事务内完成冻结扣减、预留与撮合。
    /// 装备只挂基础变体（variant 恒为空串），+N 强化簿不在系统下单范围。
    /// </summary>
    private static async ValueTask<OpsItemOutcome> PlaceSystemOrderAsync(
        MarketRuntime market,
        IEconomyTransactionStore economy,
        Guid batchId,
        MarketSide side,
        OpsMarketTarget target,
        double price,
        double quantity,
        CancellationToken cancellationToken)
    {
        var sideText = side == MarketSide.Buy ? "buy" : "sell";
        try
        {
            var instrument = new MarketInstrument(new ItemId(target.AssetId), string.Empty);
            if (!market.Owns(instrument))
                throw new InvalidOperationException("该品种不属于当前市场分片。");
            var orderId = DeterministicGuid(batchId, $"order|{sideText}|{target.AssetId}");
            // 先注入资金再下单：PlaceAsync 的持久化事务会冻结扣减，余额不足会快速失败。
            var funding = side == MarketSide.Buy
                ? new AssetDelta(SystemPlayerId, CoinsAsset, string.Empty, price * quantity)
                : new AssetDelta(SystemPlayerId, target.AssetId, string.Empty, quantity);
            await economy.CommitAsync(new EconomyTransactionRequest(
                DeterministicGuid(batchId, $"grant|{sideText}|{target.AssetId}"),
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
                new IdempotencyKey($"system-ops:{batchId:D}:{sideText}:{target.AssetId}")), cancellationToken);
            return new OpsItemOutcome(target.AssetId, target.Name, price, quantity, "placed", orderId, placement.Fills.Count, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DomainException or PostgresException)
        {
            return new OpsItemOutcome(target.AssetId, target.Name, price, quantity, "failed", null, 0, exception.Message);
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
