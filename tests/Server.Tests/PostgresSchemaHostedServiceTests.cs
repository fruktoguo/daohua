using Domain.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresSchemaHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenSchemaMatches_CompletesWithoutThrowingEvenIfSectionLabelsStale()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("旧分段标签启动回归");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(dataSource);
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var name = $"stale_{playerId:N}";
        var staleVersion = SectionCodec.CurrentSchemaVersion - 1;
        var logger = new SectionWarningLogger();
        var options = new ServerOptions(
            ServiceName: "test",
            ConnectionString: connectionString,
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
        var service = new PostgresSchemaHostedService(dataSource, options, logger);

        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            await using (var identity = new NpgsqlCommand("""
                INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
                VALUES (@account, @name, decode('00', 'hex'), decode('00', 'hex'), 100000, now(), now());
                INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
                VALUES (@player, @account, @name, @name, now(), now());
                """, conn))
            {
                identity.Parameters.AddWithValue("account", accountId);
                identity.Parameters.AddWithValue("player", playerId);
                identity.Parameters.AddWithValue("name", name);
                Assert.Equal(2, await identity.ExecuteNonQueryAsync());
            }
            // 仅测试标签巡检，不读取载荷。独立播种并校验一行，与其他用例顺序无关。
            await using var seed = new NpgsqlCommand("""
                INSERT INTO game.player_state_sections(player_id, section_kind, section_version, schema_version, codec, payload, raw_bytes, stored_bytes, checksum, updated_at)
                VALUES (@player, 'progression', 1, @version, 'test', decode('00', 'hex'), 1, 1, repeat('0', 64), now());
                """, conn);
            seed.Parameters.AddWithValue("player", playerId);
            seed.Parameters.AddWithValue("version", staleVersion);
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());

            await service.StartAsync(CancellationToken.None);
            Assert.Equal(1L, Assert.Single(logger.StaleSectionCounts));
            await using var read = new NpgsqlCommand("""
                SELECT schema_version FROM game.player_state_sections
                WHERE player_id = @player AND section_kind = 'progression';
                """, conn);
            read.Parameters.AddWithValue("player", playerId);
            Assert.Equal(staleVersion, Assert.IsType<int>(await read.ExecuteScalarAsync()));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM game.player_state_sections WHERE player_id = @player;
                DELETE FROM identity.players WHERE player_id = @player;
                DELETE FROM identity.accounts WHERE account_id = @account;
                """, conn);
            cleanup.Parameters.AddWithValue("player", playerId);
            cleanup.Parameters.AddWithValue("account", accountId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class SectionWarningLogger : ILogger<PostgresSchemaHostedService>
    {
        public List<long> StaleSectionCounts { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && state is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var field in fields)
                    if (field is { Key: "Sections", Value: long count }) StaleSectionCounts.Add(count);
        }
    }
}
