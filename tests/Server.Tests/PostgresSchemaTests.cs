using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class PostgresSchemaTests
{
    [Fact]
    public void SchemaV1_ContainsAuthoritativeTablesAndLatestOnlyQueue()
    {
        var sql = PostgresSchema.Sql;
        foreach (var schema in new[] { "identity", "game", "economy", "market", "platform" }) Assert.Contains($"CREATE SCHEMA IF NOT EXISTS {schema}", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("game.player_state_heads", sql, StringComparison.Ordinal);
        Assert.Contains("game.player_state_sections", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (player_id, section_kind)", sql, StringComparison.Ordinal);
        Assert.Contains("platform.section_flush_queue", sql, StringComparison.Ordinal);
        Assert.Contains("lease_token", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("player_snapshots", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("market_ledgers", sql, StringComparison.OrdinalIgnoreCase);
    }
}
