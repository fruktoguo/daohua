using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 资产事务「COMMIT 结局未知」后的恢复门禁（PostgreSQL）：COMMIT 已发出但连接中断时，
/// 事务要么留下完整幂等记录，要么整体回滚——因此用同一事务 ID/幂等键精确重放即可把
/// 未知变成已知。生产曾因核实重放不可达而把栅栏永久保留，玩家每次握手都被拒绝直到进程重启。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class UnknownCommitRecoveryTests
{
    /// <summary>精确重放同一请求必须命中幂等且只入账一次，这是栅栏解除的唯一依据。</summary>
    [Fact]
    public async Task ExactReplayResolvesOutcomeWithoutDoubleApplying()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("结局核实集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", player, player.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'currency.spirit-stone','',5,1,now())", player);
        var repository = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var now = DateTimeOffset.UtcNow;
        var transactionId = Guid.NewGuid();
        var request = new EconomyTransactionRequest(transactionId, $"fence:{transactionId:D}", "probe.fence", player,
            [new(player, "currency.spirit-stone", string.Empty, 3)], now, ReadOnlyMemory<byte>.Empty);

        var first = await repository.CommitAsync(request);
        Assert.True(first.Applied);
        Assert.False(first.AlreadyCommitted);

        // 结局未知后的核实重放：必须命中幂等、不得二次入账。
        var replay = await repository.CommitAsync(request);
        Assert.True(replay.Applied);
        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(transactionId, replay.TransactionId);
        Assert.Equal(8d, Convert.ToDouble(await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1", player)));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE idempotency_key=$1", request.IdempotencyKey));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", transactionId));
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task Sql(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
