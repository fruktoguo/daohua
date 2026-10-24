using Domain.Economy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 系统商店每日限购集成测试（PostgreSQL）：配额认领与资产增减同事务、超限整体回滚、
/// 幂等重放不重复占用配额、跨自然日重新放开。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class ShopPurchaseTests
{
    private const string DailyOffer = "shop.awakening-stone.daily";

    /// <summary>首次购买在同事务写入余额、流水与限购认领行；二次购买被守卫拒绝且资产不变。</summary>
    [Fact]
    public async Task PurchaseClaimsQuotaAndRejectsSecondPurchaseSameDay()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        await CreditSpiritStonesAsync(repository, player, 10_000);
        var dayIndex = ShopDailyWindow.CurrentDayIndex();

        var first = await repository.CommitAsync(BuildRequest(player, dayIndex, 1));

        Assert.True(first.Applied);
        Assert.False(first.AlreadyCommitted);
        Assert.Equal(9_000d, await BalanceAsync(source, player, "currency.spirit-stone"));
        Assert.Equal(1d, await BalanceAsync(source, player, "item.awakening-stone"));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(source,
            "SELECT quantity FROM economy.shop_daily_purchases WHERE player_id=$1 AND offer_id=$2 AND day_index=$3",
            player, DailyOffer, dayIndex)));
        Assert.Equal("shop.buy", await ScalarAsync(source,
            "SELECT transaction_type FROM economy.transactions WHERE transaction_id=$1", first.TransactionId));

        var secondId = Guid.NewGuid();
        await Assert.ThrowsAsync<ShopDailyLimitExceededException>(async () => await repository.CommitAsync(
            BuildRequest(player, dayIndex, 1) with { TransactionId = secondId, IdempotencyKey = $"shop:{secondId:D}" }));

        // 守卫拒绝后整个事务回滚：灵石与物品余额、限购行均保持首次购买后的值。
        Assert.Equal(9_000d, await BalanceAsync(source, player, "currency.spirit-stone"));
        Assert.Equal(1d, await BalanceAsync(source, player, "item.awakening-stone"));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(source,
            "SELECT quantity FROM economy.shop_daily_purchases WHERE player_id=$1 AND offer_id=$2 AND day_index=$3",
            player, DailyOffer, dayIndex)));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(source,
            "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", secondId)));
    }

    /// <summary>同一幂等键重放返回历史结果，不重复扣款、不重复占用限购配额。</summary>
    [Fact]
    public async Task ReplayReturnsCommittedResultWithoutDoubleClaim()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        await CreditSpiritStonesAsync(repository, player, 10_000);
        var dayIndex = ShopDailyWindow.CurrentDayIndex();
        var request = BuildRequest(player, dayIndex, 1);

        Assert.True((await repository.CommitAsync(request)).Applied);
        var replay = await repository.CommitAsync(request);

        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(9_000d, await BalanceAsync(source, player, "currency.spirit-stone"));
        Assert.Equal(1d, await BalanceAsync(source, player, "item.awakening-stone"));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(source,
            "SELECT quantity FROM economy.shop_daily_purchases WHERE player_id=$1 AND offer_id=$2 AND day_index=$3",
            player, DailyOffer, dayIndex)));
    }

    /// <summary>跨自然日后配额重新放开；旧自然日的认领行保留作对账底账。</summary>
    [Fact]
    public async Task NextDayAllowsPurchaseAgain()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        await CreditSpiritStonesAsync(repository, player, 10_000);
        var today = ShopDailyWindow.CurrentDayIndex();

        Assert.True((await repository.CommitAsync(BuildRequest(player, today, 1))).Applied);
        var tomorrow = Guid.NewGuid();
        var request = BuildRequest(player, today + 1, 1) with { TransactionId = tomorrow, IdempotencyKey = $"shop:{tomorrow:D}" };
        Assert.True((await repository.CommitAsync(request)).Applied);

        Assert.Equal(8_000d, await BalanceAsync(source, player, "currency.spirit-stone"));
        Assert.Equal(2d, await BalanceAsync(source, player, "item.awakening-stone"));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(source,
            "SELECT count(*) FROM economy.shop_daily_purchases WHERE player_id=$1 AND offer_id=$2",
            player, DailyOffer)));
    }

    /// <summary>不限购商品不带认领参数，既不写限购行也不受配额限制。</summary>
    [Fact]
    public async Task UnlimitedOfferWritesNoQuotaRow()
    {
        var (source, repository, player) = await ArrangeAsync();
        await using var ownedSource = source;
        await CreditSpiritStonesAsync(repository, player, 10_000);
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, $"shop:{id:D}", "shop.buy", player,
        [
            new(player, "currency.spirit-stone", string.Empty, -4_000),
            new(player, "item.awakening-stone", string.Empty, 2),
        ], DateTimeOffset.UtcNow);

        Assert.True((await repository.CommitAsync(request)).Applied);

        Assert.Equal(2d, await BalanceAsync(source, player, "item.awakening-stone"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(source,
            "SELECT count(*) FROM economy.shop_daily_purchases WHERE player_id=$1", player)));
    }

    private static EconomyTransactionRequest BuildRequest(Guid player, long dayIndex, int quantity)
    {
        var transactionId = Guid.NewGuid();
        return new EconomyTransactionRequest(
            transactionId,
            $"shop:{transactionId:D}",
            "shop.buy",
            player,
            [
                new(player, "currency.spirit-stone", string.Empty, -1_000d * quantity),
                new(player, "item.awakening-stone", string.Empty, quantity),
            ],
            DateTimeOffset.UtcNow,
            ShopPurchase: new ShopPurchaseClaim(player, DailyOffer, dayIndex, quantity, 1));
    }

    private static async Task CreditSpiritStonesAsync(PostgresEconomyRepository repository, Guid player, double amount)
    {
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, $"grant:{id:D}", "admin.adjustment", player,
            [new(player, "currency.spirit-stone", string.Empty, amount)], DateTimeOffset.UtcNow);
        Assert.True((await repository.CommitAsync(request)).Applied);
    }

    private static async Task<double> BalanceAsync(NpgsqlDataSource source, Guid player, string assetId)
    {
        var value = await ScalarAsync(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2 AND variant=''",
            player, assetId);
        return value is null ? 0d : Convert.ToDouble(value);
    }

    private static async Task<(NpgsqlDataSource Source, PostgresEconomyRepository Repository, Guid Player)> ArrangeAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("系统商店限购集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await ScalarAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())",
            player, player.ToString());
        await ScalarAsync(source,
            "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)",
            player, player.ToString());
        return (source, new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance), player);
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }
}
