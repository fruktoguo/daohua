using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 经济提交集成测试（PostgreSQL）：单语句条件 upsert+RETURNING 的余额正确性、
/// ON CONFLICT 冲突路径、幂等去重/冲突语义与流水行合批写入的同库验证。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EconomyCommitTests
{
    /// <summary>零资产变化的模拟进度仍须与回执 durable，重放不重复写；坏回执不能留下孤立 section。</summary>
    [Fact]
    public async Task ClockOnlyTransactionCommitsSectionsAndReceiptWithoutAssetLines()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var simulatedAt = now.AddHours(-1);
        var section = SectionCodec.Encode(player, PlayerSectionKinds.Progression, 7,
            new { ageSeconds = 60d }, now) with { LastSimulatedAt = simulatedAt };
        var receipt = new CommandReceipt(player, id, "simulation.advance", new string('a', 64), 7, now, now.AddMinutes(30));
        var request = new EconomyTransactionRequest(id, $"clock:{id:D}", "simulation.reward", player, [], simulatedAt,
            Sections: [section], Receipts: [receipt]);
        Assert.True((await repository.CommitAsync(request)).Applied);
        Assert.True((await repository.CommitAsync(request)).AlreadyCommitted);
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", id));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM platform.command_receipts WHERE request_id=$1", id));
        var queued = Assert.Single(await Rows(source,
            "SELECT last_simulated_at, next_attempt_at, updated_at FROM platform.section_flush_queue WHERE player_id=$1", player));
        Assert.Equal(simulatedAt.ToUnixTimeMilliseconds(), new DateTimeOffset(Assert.IsType<DateTime>(queued[0])).ToUnixTimeMilliseconds());
        Assert.Equal(now.ToUnixTimeMilliseconds(), new DateTimeOffset(Assert.IsType<DateTime>(queued[1])).ToUnixTimeMilliseconds());
        Assert.Equal(now.ToUnixTimeMilliseconds(), new DateTimeOffset(Assert.IsType<DateTime>(queued[2])).ToUnixTimeMilliseconds());
        var rejectedId = Guid.NewGuid();
        await Assert.ThrowsAsync<PostgresException>(async () => await repository.CommitAsync(request with
        {
            TransactionId = rejectedId,
            IdempotencyKey = $"clock:{rejectedId:D}",
            Sections = [section with { StateVersion = 8 }],
            Receipts = [receipt with { PlayerId = Guid.NewGuid(), RequestId = rejectedId }]
        }));
        Assert.Equal(7L, await Scalar(source, "SELECT state_version FROM platform.section_flush_queue WHERE player_id=$1", player));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", rejectedId));
    }

    /// <summary>首次提交插入新余额行：RETURNING 回传提交后余额与版本，流水行按原始行序落库。</summary>
    [Fact]
    public async Task FirstCommitInsertsBalancesAndReturnsThem()
    {
        var (source, repository, player) = await ArrangeAsync();
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "simulation.reward", player,
        [
            new(player, "item.test-herb", "", 5),
            new(player, "currency.spirit-stone", "", 12.5),
        ], DateTimeOffset.UtcNow);

        var result = await repository.CommitAsync(request);

        Assert.True(result.Applied);
        Assert.False(result.AlreadyCommitted);
        var balances = Assert.IsAssignableFrom<IReadOnlyList<AssetBalanceRecord>>(result.Balances);
        Assert.Equal(2, balances.Count);
        // 新行 version=1；回传值必须与库中一致。
        foreach (var balance in balances)
        {
            Assert.Equal(1L, balance.Version);
            Assert.Equal(balance.Quantity, Convert.ToDouble(await Scalar(source,
                "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2 AND variant=$3",
                balance.PlayerId, balance.AssetId, balance.Variant)));
        }
        Assert.Equal(5d, balances.Single(b => b.AssetId == "item.test-herb").Quantity);
        Assert.Equal(12.5d, balances.Single(b => b.AssetId == "currency.spirit-stone").Quantity);
        // 流水行：行号按请求原始次序 0..N-1。
        var lines = await Rows(source,
            "SELECT line_no, asset_id, delta FROM economy.transaction_lines WHERE transaction_id=$1 ORDER BY line_no", id);
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, Convert.ToInt32(lines[0][0]));
        Assert.Equal("item.test-herb", lines[0][1]);
        Assert.Equal(5d, Convert.ToDouble(lines[0][2]));
        Assert.Equal("currency.spirit-stone", lines[1][1]);
    }

    /// <summary>ON CONFLICT 既有行路径：数量累加、版本递增，RETURNING 回传合并后值。</summary>
    [Fact]
    public async Task ConflictPathAccumulatesQuantityAndBumpsVersion()
    {
        var (source, repository, player) = await ArrangeAsync();
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'item.test-herb','',10,5,now())", player);
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "simulation.reward", player,
            [new(player, "item.test-herb", "", -4)], DateTimeOffset.UtcNow);

        var result = await repository.CommitAsync(request);

        var balance = Assert.Single(result.Balances!);
        Assert.Equal(6d, balance.Quantity);
        Assert.Equal(6L, balance.Version);
        Assert.Equal(6d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));
        Assert.Equal(6L, await Scalar(source,
            "SELECT version FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player));
    }

    /// <summary>幂等重放不重复入账：同请求二次提交返回历史结果且不带余额，余额与流水行数不变。</summary>
    [Fact]
    public async Task IdempotentReplayDoesNotDoubleApply()
    {
        var (source, repository, player) = await ArrangeAsync();
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "item.sell", player,
            [new(player, "item.test-herb", "", -3), new(player, "currency.spirit-stone", "", 7.5)],
            DateTimeOffset.UtcNow, ResultPayload: new byte[] { 1, 2, 3 });
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'item.test-herb','',10,1,now())", player);

        var first = await repository.CommitAsync(request);
        var replay = await repository.CommitAsync(request);

        Assert.True(first.Applied);
        Assert.True(replay.Applied);
        Assert.True(replay.AlreadyCommitted);
        Assert.Null(replay.Balances);
        Assert.Equal(first.TransactionId, replay.TransactionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, replay.ResultPayload.ToArray());
        Assert.Equal(7d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE actor_id=$1", player));
        Assert.Equal(2L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", id));
    }

    /// <summary>同一幂等键绑定不同请求必须抛冲突，且不产生任何入账。</summary>
    [Fact]
    public async Task SameIdempotencyKeyWithDifferentRequestThrowsConflict()
    {
        var (source, repository, player) = await ArrangeAsync();
        var key = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        await repository.CommitAsync(new EconomyTransactionRequest(
            Guid.NewGuid(), key, "item.sell", player, [new(player, "item.test-herb", "", 3)], now));

        await Assert.ThrowsAsync<EconomyIdempotencyConflictException>(() =>
            repository.CommitAsync(new EconomyTransactionRequest(
                Guid.NewGuid(), key, "item.sell", player, [new(player, "item.test-herb", "", 9)], now)).AsTask());

        Assert.Equal(3d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE actor_id=$1", player));
    }

    /// <summary>余额不足时抛 EconomyInsufficientBalanceException 且整笔回滚：余额、事务头与流水行均不留痕。</summary>
    [Fact]
    public async Task InsufficientBalanceThrowsAndRollsBackAtomically()
    {
        var (source, repository, player) = await ArrangeAsync();
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'item.test-herb','',4,2,now())", player);
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "item.discard", player,
            [new(player, "item.test-herb", "", -9)], DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<EconomyInsufficientBalanceException>(() => repository.CommitAsync(request).AsTask());

        Assert.Equal(4d, error.Available);
        Assert.Equal("item.test-herb", error.Delta.AssetId);
        Assert.Equal(4d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));
        Assert.Equal(2L, await Scalar(source,
            "SELECT version FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", id));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", id));
    }

    /// <summary>同一资产多行增量先合并再落库：余额只变更一次、流水保留原始行序与增量。</summary>
    [Fact]
    public async Task DuplicateAssetDeltasMergeIntoSingleBalanceUpdate()
    {
        var (source, repository, player) = await ArrangeAsync();
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "simulation.reward", player,
        [
            new(player, "item.test-herb", "", 3),
            new(player, "item.test-herb", "", 5),
            new(player, "item.test-herb", "", -1),
        ], DateTimeOffset.UtcNow);

        var result = await repository.CommitAsync(request);

        var balance = Assert.Single(result.Balances!);
        Assert.Equal(7d, balance.Quantity);
        Assert.Equal(1L, balance.Version);
        var lines = await Rows(source,
            "SELECT delta FROM economy.transaction_lines WHERE transaction_id=$1 ORDER BY line_no", id);
        Assert.Equal(new[] { 3d, 5d, -1d }, lines.Select(row => Convert.ToDouble(row[0])).ToArray());
    }

    /// <summary>
    /// 并发 Serializable 提交命中同一余额行时，瞬态 40001/40P01 必须重开整笔事务重试；
    /// 所有唯一幂等请求最终各入账一次，不能因重试重复写事务头或流水。
    /// </summary>
    [Fact]
    public async Task ConcurrentSerializableCommitsRetryAsWholeTransaction()
    {
        var (source, repository, player) = await ArrangeAsync();
        const int commitCount = 8;
        await Sql(source,
            "INSERT INTO economy.player_asset_balances VALUES ($1,'currency.test','',0,1,now())",
            player);

        var requests = Enumerable.Range(0, commitCount)
            .Select(index => new EconomyTransactionRequest(
                Guid.NewGuid(), $"concurrent-economy-{Guid.NewGuid():N}", "simulation.concurrent", player,
                [new(player, "currency.test", "", 1)], DateTimeOffset.UtcNow))
            .ToArray();
        var results = await Task.WhenAll(requests.Select(request => repository.CommitAsync(request).AsTask()));

        Assert.Equal(commitCount, results.Length);
        Assert.All(results, result =>
        {
            Assert.True(result.Applied);
            Assert.False(result.AlreadyCommitted);
            Assert.Single(result.Balances!);
        });
        Assert.Equal(commitCount,
            Convert.ToInt32(await Scalar(source,
                "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='currency.test' AND variant=''",
                player)));
        Assert.Equal(commitCount, Convert.ToInt32(await Scalar(source,
            "SELECT count(*) FROM economy.transactions WHERE actor_id=$1 AND transaction_type='simulation.concurrent'", player)));
        Assert.Equal(commitCount, Convert.ToInt32(await Scalar(source,
            "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id IN (SELECT transaction_id FROM economy.transactions WHERE actor_id=$1 AND transaction_type='simulation.concurrent')",
            player)));
    }
    /// <summary>两层持久化 + 5 类白名单留存测试：非白名单流水立即排空，5 类白名单/斗法日志/上下线审计/坊市与终态冻结保留 30 天（活跃挂单/活跃冻结永不删除）。</summary>
    [Fact]
    public async Task WhitelistRetention_DrainsNonWhitelistImmediatelyAndRetainsWhitelistThirtyDays()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        var peer = Guid.NewGuid();
        await Sql(source,
            "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$2,$3,$3,(left(replace($1::text,'-',''),16)),now(),now(),1)",
            peer, player, peer.ToString());
        var now = DateTimeOffset.UtcNow;

        var shortTx1 = Guid.NewGuid();
        var shortTx2 = Guid.NewGuid();
        var marketTx = Guid.NewGuid();
        var shopTx = Guid.NewGuid();
        var mailTx = Guid.NewGuid();
        var doudizhuTx = Guid.NewGuid();

        // 1. 写入非白名单事务（item.sell / simulation.reward）与白名单事务（market / shop.buy / mail.claim / doudizhu.settlement）
        await repository.CommitAsync(new EconomyTransactionRequest(shortTx1, $"sell:{shortTx1:D}", "item.sell", player,
            [new(player, "currency.spirit-stone", "", 10)], OccurredAt: now));
        await repository.CommitAsync(new EconomyTransactionRequest(shortTx2, $"sim:{shortTx2:D}", "simulation.reward", player,
            [new(player, "item.ore", "", 2)], OccurredAt: now));
        await repository.CommitAsync(new EconomyTransactionRequest(marketTx, $"mkt:{marketTx:D}", "market", player,
            [new(player, "item.sword", "", 1)], OccurredAt: now));
        await repository.CommitAsync(new EconomyTransactionRequest(shopTx, $"shop:{shopTx:D}", "shop.buy", player,
            [new(player, "item.awakening-stone", "", 1)], OccurredAt: now));
        await repository.CommitAsync(new EconomyTransactionRequest(mailTx, $"mail:{mailTx:D}", "mail.claim", player,
            [new(player, "currency.spirit-stone", "", 50)], OccurredAt: now));
        await repository.CommitAsync(new EconomyTransactionRequest(doudizhuTx, $"ddz:{doudizhuTx:D}", "doudizhu.settlement", player,
            [new(player, "currency.spirit-stone", "", 30)], OccurredAt: now));

        // 2. 写入上下线审计（验证 accountId=Guid.Empty 时自动关联真实 account_id）与 PVP 斗法日志（验证幂等写入）
        var lifecycleRepo = new PostgresPlayerLifecycleAuditRepository(source);
        await lifecycleRepo.RecordOnlineAsync(player, Guid.Empty, "主角", "node.yunxi-village", now);
        await lifecycleRepo.RecordOfflineAsync(player, Guid.Empty, "主角", "node.yunxi-village", now.AddMinutes(1));
        Assert.Equal(2L, await Scalar(source,
            "SELECT count(*) FROM identity.audit_events WHERE actor_id=$1 AND target_id=$1 AND event_type IN ('player.online','player.offline')",
            player));

        var duelRepo = new PostgresDuelLogRepository(source);
        var duelId = $"duel-{Guid.NewGuid():N}";
        var duelEntry = new DuelLogEntry(
            duelId, player, "主角", 150, peer, "对手", 140,
            "node.yunxi-village", player, "initiator_win", false,
            [new("item.test-herb", 2)], [], [], [new("item.test-herb", 2)],
            12.5, now);
        await duelRepo.RecordAsync(duelEntry);
        await duelRepo.RecordAsync(duelEntry);
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM game.duel_logs WHERE duel_id=$1", duelId));

        // 3. 写入坊市挂单（1 active + 2 filled）、成交、冻结预占与商店每日限购
        var activeOrderId = Guid.NewGuid();
        var buyOrderId = Guid.NewGuid();
        var sellOrderId = Guid.NewGuid();
        var tradeId = Guid.NewGuid();
        var baseSeq = Convert.ToInt64(await Scalar(source, "SELECT COALESCE(max(sequence), 0) FROM market.orders WHERE partition_id = 0"));
        await Sql(source, """
            INSERT INTO market.orders (order_id, partition_id, owner_player_id, side, asset_id, variant, price, quantity, filled_quantity, status, version, sequence, idempotency_key, created_at, expires_at, updated_at)
            VALUES
              ($1, 0, $4, 'sell', 'item.ore', '', 10, 1, 0, 'active', 1, $11, $7, $6, $8, $6),
              ($2, 0, $4, 'buy',  'item.ore', '', 10, 1, 1, 'filled', 2, $12, $9, $6, $8, $6),
              ($3, 0, $5, 'sell', 'item.ore', '', 10, 1, 1, 'filled', 2, $13, $10, $6, $8, $6)
            """, activeOrderId, buyOrderId, sellOrderId, player, peer, now, $"ord-a:{activeOrderId:D}", now.AddDays(1), $"ord-b:{buyOrderId:D}", $"ord-s:{sellOrderId:D}", baseSeq + 1, baseSeq + 2, baseSeq + 3);
        await Sql(source, """
            INSERT INTO market.trades (trade_id, partition_id, buy_order_id, sell_order_id, buyer_id, seller_id, asset_id, variant, price, quantity, seller_tax, traded_at)
            VALUES ($1, 0, $2, $3, $4, $5, 'item.ore', '', 10, 1, 1, $6)
            """, tradeId, buyOrderId, sellOrderId, player, peer, now);
        var activeResId = Guid.NewGuid();
        var consumedResId = Guid.NewGuid();
        await Sql(source, """
            INSERT INTO economy.reservations (reservation_id, order_id, player_id, asset_id, variant, side, quantity, reserved_value, status, created_at, updated_at)
            VALUES
              ($1, $3, $5, 'item.ore', '', 'sell', 1, 0, 'active', $6, $6),
              ($2, $4, $5, 'currency.spirit-stone', '', 'buy', 10, 10, 'consumed', $6, $6)
            """, activeResId, consumedResId, activeOrderId, buyOrderId, player, now);
        await Sql(source,
            "INSERT INTO economy.shop_daily_purchases (player_id, offer_id, day_index, quantity, updated_at) VALUES ($1, 'shop.awakening-stone.daily-100', 100, 1, $2)",
            player, now);

        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused"
        }).Build());
        var retention = new PostgresRetentionRepository(source, options, NullLogger<PostgresRetentionRepository>.Instance);

        // 4. 5 分钟后：非白名单历史流水（item.sell / simulation.reward）及其明细被立即排空；白名单与业务热数据完整保留
        await retention.PruneAsync(now.AddMinutes(5), CancellationToken.None);
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id IN ($1,$2)", shortTx1, shortTx2));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id IN ($1,$2)", shortTx1, shortTx2));
        Assert.Equal(4L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id IN ($1,$2,$3,$4)", marketTx, shopTx, mailTx, doudizhuTx));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM game.duel_logs WHERE duel_id=$1", duelId));
        Assert.Equal(2L, await Scalar(source, "SELECT count(*) FROM identity.audit_events WHERE actor_id=$1 AND event_type IN ('player.online','player.offline')", player));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM market.trades WHERE trade_id=$1", tradeId));
        Assert.Equal(3L, await Scalar(source, "SELECT count(*) FROM market.orders WHERE order_id IN ($1,$2,$3)", activeOrderId, buyOrderId, sellOrderId));
        Assert.Equal(2L, await Scalar(source, "SELECT count(*) FROM economy.reservations WHERE reservation_id IN ($1,$2)", activeResId, consumedResId));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.shop_daily_purchases WHERE player_id=$1", player));

        // 5. 第 29 天（未满 30 天）：全部白名单与关联记录依然完整保留
        await retention.PruneAsync(now.AddDays(29), CancellationToken.None);
        Assert.Equal(4L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id IN ($1,$2,$3,$4)", marketTx, shopTx, mailTx, doudizhuTx));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM game.duel_logs WHERE duel_id=$1", duelId));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM market.trades WHERE trade_id=$1", tradeId));

        // 6. 第 31 天（超过 30 天）：白名单账本、斗法日志、上下线审计、坊市成交与终态无引用挂单、终态冻结与商店限购全数清理，活跃单/活跃冻结永不删除
        await retention.PruneAsync(now.AddDays(31), CancellationToken.None);
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id IN ($1,$2,$3,$4)", marketTx, shopTx, mailTx, doudizhuTx));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id IN ($1,$2,$3,$4)", marketTx, shopTx, mailTx, doudizhuTx));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM game.duel_logs WHERE duel_id=$1", duelId));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM identity.audit_events WHERE actor_id=$1 AND event_type IN ('player.online','player.offline')", player));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM market.trades WHERE trade_id=$1", tradeId));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM market.orders WHERE order_id IN ($1,$2)", buyOrderId, sellOrderId));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM market.orders WHERE order_id=$1", activeOrderId));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.reservations WHERE reservation_id=$1", consumedResId));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.reservations WHERE reservation_id=$1", activeResId));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.shop_daily_purchases WHERE player_id=$1", player));
    }

    /// <summary>PersistLedger=false 瞬时经济防重：不写流水账本、同幂等键重放命中短期幂等表不重复加减余额，且 2 小时后由留存任务批量清理。</summary>
    [Fact]
    public async Task PersistLedgerFalse_TransientIdempotency_PreventsDuplicateDeltaAndPrunesAfterTwoHours()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'item.test-herb','',10,1,now())", player);
        var now = DateTimeOffset.UtcNow;
        var key = $"transient:{Guid.NewGuid():N}";
        var request = new EconomyTransactionRequest(
            Guid.NewGuid(), key, "simulation.reward", player,
            [new(player, "item.test-herb", "", 5)],
            now,
            ResultPayload: new byte[] { 7, 8 },
            PersistLedger: false);

        var first = await repository.CommitAsync(request);
        Assert.True(first.Applied);
        Assert.False(first.AlreadyCommitted);
        Assert.Equal(15d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE actor_id=$1", player));
        Assert.Equal(1L, await Scalar(source,
            "SELECT count(*) FROM economy.transient_idempotency WHERE player_id=$1 AND idempotency_key=$2", player, key));

        // 模拟结局未知重放：PersistLedger=false 仍必须命中瞬时幂等表返回 AlreadyCommitted，不能二次入账。
        var replay = await repository.CommitAsync(request);
        Assert.True(replay.Applied);
        Assert.True(replay.AlreadyCommitted);
        Assert.Null(replay.Balances);
        Assert.Equal(15d, Convert.ToDouble(await Scalar(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-herb'", player)));

        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused"
        }).Build());
        var retention = new PostgresRetentionRepository(source, options, NullLogger<PostgresRetentionRepository>.Instance);

        await retention.PruneAsync(now.AddMinutes(110), CancellationToken.None);
        Assert.Equal(1L, await Scalar(source,
            "SELECT count(*) FROM economy.transient_idempotency WHERE player_id=$1 AND idempotency_key=$2", player, key));

        var pruned = await retention.PruneAsync(now.AddHours(3), CancellationToken.None);
        Assert.True(pruned.TransientIdempotency >= 1);
        Assert.Equal(0L, await Scalar(source,
            "SELECT count(*) FROM economy.transient_idempotency WHERE player_id=$1 AND idempotency_key=$2", player, key));
    }

    private static async Task<(NpgsqlDataSource Source, PostgresEconomyRepository Repository, Guid Player)> ArrangeAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("经济提交集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", player, player.ToString());
        return (source, new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance), player);
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task<IReadOnlyList<object?[]>> Rows(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task Sql(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
