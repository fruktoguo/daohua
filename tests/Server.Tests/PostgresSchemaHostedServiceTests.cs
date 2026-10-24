using Server.Clean;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Server.Tests;

public sealed class PostgresSchemaHostedServiceTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("DAOHUA_MARKET_TEST_CONNECTION");

    [Fact]
    public async Task StartAsync_WhenSchemaMatches_CompletesWithoutThrowingEvenIfSectionLabelsStale()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var options = new ServerOptions(
            ServiceName: "test",
            ConnectionString: ConnectionString,
            SectionFlushBatchSize: 100,
            SectionFlushInterval: TimeSpan.FromSeconds(1),
            SectionFlushLease: TimeSpan.FromSeconds(30),
            OutboxBatchSize: 100,
            OutboxPollInterval: TimeSpan.FromSeconds(1),
            SessionLifetime: TimeSpan.FromMinutes(10),
            MaxWebSocketMessageBytes: 65536,
            WebSocketOutboundCapacity: 100,
            MarketPartitionCount: 1,
            MarketPartitionId: 0,
            EnableSchemaBootstrap: false);
        var service = new PostgresSchemaHostedService(
            dataSource,
            options,
            NullLogger<PostgresSchemaHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // 注入一条旧标签：验证 WarnStaleSectionLabelsAsync 仅告警、不抛异常拖死启动
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            var expected = Domain.Persistence.SectionCodec.CurrentSchemaVersion;
            await using var cmd = new NpgsqlCommand($"""
                UPDATE game.player_state_sections
                SET schema_version = {expected - 1}
                WHERE section_kind = 'progression'
                  AND schema_version = {expected}
                  AND player_id IN (SELECT player_id FROM game.player_state_sections LIMIT 1);
                """, conn);
            var updated = await cmd.ExecuteNonQueryAsync();
            try
            {
                // 必须平稳完成、不抛异常
                await service.StartAsync(CancellationToken.None);
            }
            finally
            {
                // 恢复现场
                if (updated > 0)
                {
                    await using var fix = new NpgsqlCommand($"""
                        UPDATE game.player_state_sections
                        SET schema_version = {expected}
                        WHERE schema_version <> {expected};
                        """, conn);
                    await fix.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
