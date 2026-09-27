using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 经济提交集成测试（PostgreSQL）：单语句条件 upsert+RETURNING 的余额正确性、
/// ON CONFLICT 冲突路径、幂等去重/冲突语义与流水行合批写入的同库验证。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EconomyCommitTests
{
    /// <summary>首次提交插入新余额行：RETURNING 回传提交后余额与版本，流水行按原始行序落库。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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
    [Trait("Category", "PostgresIntegration")]
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

    private static async Task<(NpgsqlDataSource Source, PostgresEconomyRepository Repository, Guid Player)> ArrangeAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("经济提交集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, "INSERT INTO identity.players VALUES ($1,$1,$2,$2,now(),now(),1)", player, player.ToString());
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
