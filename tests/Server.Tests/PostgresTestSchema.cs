using Domain.Persistence;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 共享同一 PostgreSQL 库的集成测试集合：全部串行执行。
/// 市场链路使用 Serializable 事务，任何并发写 platform.section_flush_queue / game.* 的
/// 测试都会与其构成 SSI 谓词冲突（40001），进而触发 MarketRuntime 的就绪熔断；
/// 统一收进本集合后跨类不再并行落库，各自仍共享同一 schema。
/// </summary>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection;

/// <summary>
/// 集成测试建库辅助。
///
/// 背景：多个集成测试类会被 xUnit 并行执行，而 `PostgresSchema.Sql` 里的
/// `CREATE SCHEMA IF NOT EXISTS` 在并发下并不安全——两个事务同时插入同一个
/// `pg_namespace` 行会撞唯一索引（23505），失败方还可能继续在无表状态下执行用例（42P01）。
///
/// 处理方式：用会话级 advisory lock 把建库串行化，并对并发 DDL 的瞬时冲突（23505 / 40P01 / 40001）
/// 做有限退避重试。这样无论多少个测试类同时建库，最终都会收敛到「schema 已就绪」。
/// </summary>
internal static class PostgresTestSchema
{
    /// <summary>专用 advisory lock 键，仅用于测试建库串行化。</summary>
    private const long AdvisoryLockKey = 772301;

    /// <summary>在专用测试库上建立/校验当前 schema；可安全地被多个测试类并发调用。</summary>
    /// <param name="source">目标数据源。</param>
    public static async Task EnsureAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({AdvisoryLockKey})", connection))
            await acquire.ExecuteNonQueryAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var ddl = new NpgsqlCommand(PostgresSchema.Sql, connection) { CommandTimeout = 60 };
                    await ddl.ExecuteNonQueryAsync();
                    await using var patch = new NpgsqlCommand("""
                        ALTER TABLE IF EXISTS market.item_stats ADD COLUMN IF NOT EXISTS reference_price double precision;
                        """, connection) { CommandTimeout = 60 };
                    await patch.ExecuteNonQueryAsync();
                    await using var updateVersions = new NpgsqlCommand($"""
                        UPDATE game.player_state_heads SET schema_version = {SectionCodec.CurrentSchemaVersion} WHERE schema_version <> {SectionCodec.CurrentSchemaVersion};
                        UPDATE game.player_state_sections SET schema_version = {SectionCodec.CurrentSchemaVersion} WHERE schema_version <> {SectionCodec.CurrentSchemaVersion};
                        UPDATE platform.section_flush_queue SET schema_version = {SectionCodec.CurrentSchemaVersion} WHERE schema_version <> {SectionCodec.CurrentSchemaVersion};
                        INSERT INTO platform.schema_version(version, applied_at) VALUES ({PostgresSchema.Version}, now()) ON CONFLICT (version) DO NOTHING;
                        """, connection) { CommandTimeout = 60 };
                    await updateVersions.ExecuteNonQueryAsync();
                    // 测试库按清零口径每轮供给即净态:几十轮门禁累积的投影/玩家/会话行会让
                    // 冷扫描(ORDER BY last_simulated_at LIMIT 2048)被历史残留玩家占满,本轮
                    // 新入队玩家被挤出候选集,ScanColdPlayers 间歇乃至稳定假红(实测 projection
                    // 残留 5212 行)。统一清空全部业务表,仅保留 schema_version 与密钥环。
                    await using var truncate = new NpgsqlCommand("""
                        TRUNCATE identity.accounts, identity.players, identity.auth_sessions, identity.audit_events,
                            identity.account_risk_events, identity.account_risk_signals,
                            identity.data_protection_keys,
                            game.player_state_heads, game.player_state_sections, game.player_action_queue,
                            game.player_projection, game.player_runtime_leases, game.node_buildings,
                            platform.compute_jobs, platform.section_flush_queue, platform.command_receipts,
                            platform.integration_outbox, platform.account_risk_policy, platform.account_risk_key,
                            social.parties, social.party_members, social.party_sessions, social.party_session_inbox,
                            social.party_seekers, social.party_invitations, social.friendships, social.friend_requests,
                            social.chat_messages, social.player_invitations,
                            economy.transactions, economy.transaction_lines, economy.player_asset_balances,
                            economy.player_asset_instances, economy.reservations, economy.shop_daily_purchases,
                            market.orders, market.trades, market.item_stats, market.partition_leases,
                            market.settlement_claims, market.settlement_cursors
                        RESTART IDENTITY CASCADE;
                        """, connection) { CommandTimeout = 60 };
                    await truncate.ExecuteNonQueryAsync();
                    return;
                }
                catch (PostgresException exception) when (attempt < 5 && exception.SqlState is "23505" or "40P01" or "40001")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
                }
            }
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({AdvisoryLockKey})", connection);
            await release.ExecuteNonQueryAsync();
        }
    }

    /// <summary>读取集成测试专用库连接串；未配置时抛出带说明的异常。</summary>
    /// <param name="purpose">调用方用途描述，用于异常文案。</param>
    /// <returns>连接串。</returns>
    public static string RequireConnectionString(string purpose) =>
        Environment.GetEnvironmentVariable("DAOHUA_MARKET_TEST_CONNECTION")
        ?? throw new InvalidOperationException($"{purpose}需要专用 PostgreSQL 库：DAOHUA_MARKET_TEST_CONNECTION。");
}
