using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 批次三市场与冷挂机计算韧性回归测试：
/// 1. EcsComputeResultVerifier 对 Overflow/KeyNotFound 等重演异常统一转 Rejected；
/// 2. MarketRuntime 到期合批遇毒订单时自动降级逐笔结算并隔离毒单、自愈健康订单与盘口就绪态；
/// 3. ClaimSettlementAsync 高并发同 requestId 重放由 ON CONFLICT 收敛，不抛 23505；
/// 4. ReturnOverflowAsync 带退避延时时设置 next_attempt_at，阻止小节点 0ms 紧循环空转。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class MarketAndComputeResilienceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EcsComputeResultVerifier_MalformedRequest_ReturnsRejectedGracefully()
    {
        var verifier = new EcsComputeResultVerifier(GameDataCatalog.BuiltIn);
        var request = new ComputeAdvanceJobRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "异常道友",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceStateVersion: 1,
            FromUnixMilliseconds: 0,
            TargetUnixMilliseconds: 1000,
            HeadChecksum: "bad",
            HeadGameDataFingerprint: GameDataCatalog.BuiltIn.Fingerprint,
            Sections: [],
            AssetBalances: [],
            IdempotencyKey: "bad-key");

        var execResult = await verifier.ExecuteAsync(request);
        Assert.False(execResult.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(execResult.Error));

        var candidate = new ComputeAdvanceJobResult(
            request.JobId,
            request.PlayerId,
            Success: true,
            SourceStateVersion: 1,
            TargetStateVersion: 1,
            LastSimulatedAtUnixMilliseconds: 0,
            Sections: [],
            AssetDeltas: [],
            Metrics: null,
            ErrorCode: null,
            ErrorMessage: null,
            Retryable: false);
        var verifyResult = await verifier.VerifyAsync(request, candidate);
        Assert.False(verifyResult.Accepted);

        // 采样率为 0 时跳过重演直接信任有效候选结果，但仍拦截时钟越界与非法 section。
        using var sampleZeroVerifier = new EcsComputeResultVerifier(GameDataCatalog.BuiltIn, verifySampleRate: 0.0);
        var acceptedZero = await sampleZeroVerifier.VerifyAsync(request, candidate);
        Assert.True(acceptedZero.Accepted);

        var outOfWindow = candidate with { LastSimulatedAtUnixMilliseconds = request.TargetUnixMilliseconds + 1 };
        var rejectedWindow = await sampleZeroVerifier.VerifyAsync(request, outOfWindow);
        Assert.False(rejectedWindow.Accepted);
    }

    [Fact]
    public async Task MarketRuntime_ExpireWithPoisonedOrder_QuarantinesPoisonAndRefundsHealthyOrder()
    {
        var cs = PostgresTestSchema.RequireConnectionString("市场到期隔离测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var options = CreateMarketOptions(cs);

        var poisonSeller = PlayerId.New();
        var healthySeller = PlayerId.New();
        var buyer = PlayerId.New();
        await SeedBalanceAsync(source, poisonSeller.Value, "item.market-expire-poison", 10);
        await SeedBalanceAsync(source, healthySeller.Value, "item.market-expire-healthy", 10);
        await SeedBalanceAsync(source, buyer.Value, "currency.spirit-stone", 5000);

        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var poisonOrderId = OrderId.New();
        var healthyOrderId = OrderId.New();
        var poisonInstrument = new MarketInstrument(new ItemId("item.market-expire-poison"));
        var healthyInstrument = new MarketInstrument(new ItemId("item.market-expire-healthy"));

        await using (var initial = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance))
        {
            await initial.StartAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            await initial.PlaceAsync(new PlaceOrderRequest(poisonOrderId, poisonSeller, poisonInstrument, MarketSide.Sell, 100, 10, now, new IdempotencyKey(Guid.NewGuid().ToString())));
            await initial.PlaceAsync(new PlaceOrderRequest(healthyOrderId, healthySeller, healthyInstrument, MarketSide.Sell, 100, 10, now, new IdempotencyKey(Guid.NewGuid().ToString())));
            await initial.StopAsync(CancellationToken.None);
        }

        // 让两张单同时过期，但蓄意删掉 poisonOrder 的冻结明细制造非瞬态闭合异常
        await SqlAsync(source, "UPDATE market.orders SET created_at = now() - interval '31 days', expires_at = now() - interval '1 day' WHERE order_id = ANY($1)",
            new[] { poisonOrderId.Value, healthyOrderId.Value });
        await SqlAsync(source, "DELETE FROM economy.reservations WHERE order_id = $1", poisonOrderId.Value);

        await using var recovered = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await recovered.StartAsync(CancellationToken.None);

        Assert.True(recovered.IsReady);
        Assert.Equal("expired", (await repository.LoadOrderAsync(healthyOrderId.Value))!.Status);
        Assert.Equal(10d, await ReadBalanceAsync(source, healthySeller.Value, "item.market-expire-healthy"));
        Assert.Equal("active", (await repository.LoadOrderAsync(poisonOrderId.Value))!.Status);

        // 分片保持就绪，后续正常挂单照常推进不被隔离的毒单卡死
        var buyResult = await recovered.PlaceAsync(new PlaceOrderRequest(
            OrderId.New(), buyer, healthyInstrument, MarketSide.Buy, 50, 1, DateTimeOffset.UtcNow, new IdempotencyKey(Guid.NewGuid().ToString())));
        Assert.Equal(MarketOrderStatus.Active, buyResult.IncomingOrder.Status);
        await recovered.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClaimSettlementAsync_ConcurrentSameRequestId_ConvergesWithoutUniqueViolation()
    {
        var cs = PostgresTestSchema.RequireConnectionString("市场结算并发测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var options = CreateMarketOptions(cs);

        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await SeedPlayerAsync(source, playerId);
        await SqlAsync(source, """
            INSERT INTO market.settlement_cursors(player_id, pending_count, settled_count, updated_at)
            VALUES ($1, 3, 0, now())
            ON CONFLICT (player_id) DO UPDATE SET pending_count = 3, settled_count = 0, updated_at = now()
            """, playerId);

        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            repository.ClaimSettlementAsync(playerId, requestId).AsTask()));

        Assert.All(results, r => Assert.Equal(3, r.SettledTradeCount));
        Assert.Equal(1, results.Count(r => !r.AlreadyClaimed));
        Assert.Equal(5, results.Count(r => r.AlreadyClaimed));
    }

    [Fact]
    public async Task ComputeJobRepository_ReturnOverflowWithDelay_SetsNextAttemptAtAndSkipsImmediateReclaim()
    {
        var cs = PostgresTestSchema.RequireConnectionString("溢出退避测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var jobs = new PostgresComputeJobRepository(source);
        var playerId = Guid.NewGuid();
        await SeedPlayerAsync(source, playerId);

        await jobs.EnqueueAsync(playerId, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1, 2, 3 });
        var claim = Assert.Single(
            await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == playerId);

        Assert.Equal(1, await jobs.ReturnOverflowAsync(claim.LeaseToken, [claim.JobId], TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain(
            await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);

        await using var checkConnection = await source.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand("SELECT status, attempts, next_attempt_at FROM platform.compute_jobs WHERE job_id = $1", checkConnection);
        checkCmd.Parameters.Add(new NpgsqlParameter { Value = claim.JobId });
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.False(await reader.IsDBNullAsync(2));
    }

    private static ServerOptions CreateMarketOptions(string connectionString) =>
        ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };

    private static async Task SeedPlayerAsync(NpgsqlDataSource source, Guid playerId)
    {
        await SqlAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now()) ON CONFLICT DO NOTHING", playerId, playerId.ToString("N"));
        await SqlAsync(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1) ON CONFLICT DO NOTHING", playerId, playerId.ToString("N"));
    }

    private static async Task SeedBalanceAsync(NpgsqlDataSource source, Guid playerId, string assetId, double quantity)
    {
        await SeedPlayerAsync(source, playerId);
        await SqlAsync(source, "INSERT INTO economy.player_asset_balances VALUES ($1,$2,'',$3,1,now())", playerId, assetId, quantity);
    }

    private static async Task<double> ReadBalanceAsync(NpgsqlDataSource source, Guid playerId, string assetId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COALESCE((SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2 AND variant=''), 0)", connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = assetId });
        return Convert.ToDouble(await cmd.ExecuteScalarAsync());
    }

    private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        await cmd.ExecuteNonQueryAsync();
    }
}
