using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 技能学习持久化集成测试（PostgreSQL）：扣减技能书与 loadout 分段入队必须在
/// 同一经济事务中原子提交、幂等重放，任一侧失败整笔回滚。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AbilityLearningPersistenceTests
{
    /// <summary>扣款与习得分段同事务落库；重复提交幂等命中；分段外键失败时扣款与流水一并回滚。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ManualDebitAndLoadoutQueueCommitTogetherAndReplayOnce()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("学习集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        // 本用例直接写 identity/economy 表，必须先确保 schema 就绪（不能依赖其他测试类的执行顺序）。
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", player, player.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'item.tome.heavy-slash','',2,1,now())", player);
        var repository = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var now = DateTimeOffset.UtcNow;
        var section = SectionCodec.Encode(player, PlayerSectionKinds.Loadout, 1, new { learnedAbilityIds = new[] { "ability.heavy-slash" } }, now);
        var id = Guid.NewGuid();
        var request = new EconomyTransactionRequest(id, id.ToString(), "ability.learn", player,
            [new(player, "item.tome.heavy-slash", "", -1)], now, Sections: [section]);
        await repository.CommitAsync(request);
        Assert.True((await repository.CommitAsync(request)).AlreadyCommitted);
        Assert.Equal(1d, Convert.ToDouble(await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1", player)));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM platform.section_flush_queue WHERE player_id=$1", player));
        // 快照外键失败发生在资产更新之后，仍必须回滚扣款与流水。
        var badId = Guid.NewGuid();
        await Assert.ThrowsAsync<PostgresException>(async () => await repository.CommitAsync(request with
        {
            TransactionId = badId, IdempotencyKey = badId.ToString(), Sections = [section with { PlayerId = Guid.NewGuid() }]
        }));
        Assert.Equal(1d, Convert.ToDouble(await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1", player)));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", badId));
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
