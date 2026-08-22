// ==============================================================================
// 文件名称: PostgresSnapshotCasTests.cs
// 主要职责: 验证 PostgreSQL 玩家快照 version 单调 CAS 规则与 SQL 语法一致性。
// ==============================================================================

using Server.Infrastructure;
using Xunit;

namespace Server.Tests;

public sealed class PostgresSnapshotCasTests
{
    [Theory]
    [InlineData(null, null, true)]      // 现有无版本，新数据无版本 -> 允许覆盖（通用快照覆盖）
    [InlineData(null, 1L, true)]        // 现有无版本，新数据有版本 -> 允许升级
    [InlineData(5L, 6L, true)]          // 现有版本 5，新版本 6 -> 严格递增，允许更新
    [InlineData(5L, 100L, true)]        // 现有版本 5，新版本 100 -> 严格递增，允许更新
    [InlineData(5L, 5L, false)]         // 现有版本 5，新版本 5 -> 相同版本拒绝覆盖
    [InlineData(5L, 4L, false)]         // 现有版本 5，新版本 4 -> 旧版本拒绝覆盖
    [InlineData(5L, 1L, false)]         // 现有版本 5，新版本 1 -> 旧版本拒绝覆盖
    [InlineData(5L, null, false)]       // 现有版本 5，新数据无版本 -> 拒绝覆盖已有版本
    public void ShouldUpdateSnapshot_EnforcesStrictMonotonicCas(long? existingVersion, long? incomingVersion, bool expected)
    {
        var actual = PostgresSnapshotStore.ShouldUpdateSnapshot(existingVersion, incomingVersion);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UpsertSnapshotSql_ContainsMonotonicCasWhereClause()
    {
        var sql = PostgresSnapshotStore.UpsertSnapshotSql;

        Assert.Contains("ON CONFLICT (kind, id) DO UPDATE SET", sql);
        Assert.Contains("WHERE player_snapshots.version IS NULL", sql);
        Assert.Contains("EXCLUDED.version IS NOT NULL AND EXCLUDED.version > player_snapshots.version", sql);
    }

    [Fact]
    public void UpsertSnapshotSql_ContainsAllColumnsAndParameters()
    {
        var sql = PostgresSnapshotStore.UpsertSnapshotSql;

        var expectedParameters = new[]
        {
            "@kind", "@id", "@encoding", "@payload", "@payloadBytes", "@compressedBytes", "@updatedAt",
            "@playerId", "@account", "@characterName", "@sequenceId", "@version", "@presenceMode", "@lastSimulatedAt"
        };

        foreach (var parameter in expectedParameters)
        {
            Assert.Contains(parameter, sql);
        }

        var expectedExcludedFields = new[]
        {
            "payload_encoding = EXCLUDED.payload_encoding",
            "payload = EXCLUDED.payload",
            "payload_bytes = EXCLUDED.payload_bytes",
            "compressed_bytes = EXCLUDED.compressed_bytes",
            "updated_at = EXCLUDED.updated_at",
            "player_id = EXCLUDED.player_id",
            "account = EXCLUDED.account",
            "character_name = EXCLUDED.character_name",
            "sequence_id = EXCLUDED.sequence_id",
            "version = EXCLUDED.version",
            "presence_mode = EXCLUDED.presence_mode",
            "last_simulated_at = EXCLUDED.last_simulated_at"
        };

        foreach (var field in expectedExcludedFields)
        {
            Assert.Contains(field, sql);
        }
    }
}

