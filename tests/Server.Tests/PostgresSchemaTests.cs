using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>包含针对 Postgres Schema Tests 的测试用例与断言集合。</summary>
public sealed class PostgresSchemaTests
{
    /// <summary>验证 Schema V1 Contains Authoritative Tables And Latest Only Queue。</summary>
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
