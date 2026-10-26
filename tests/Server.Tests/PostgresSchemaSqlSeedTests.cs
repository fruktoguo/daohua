using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 空库直建守护:PostgresSchema.Sql 尾部必须为当前 <see cref="PostgresSchema.Version"/> 携带版本种子行。
/// 版本推进时若只改 Version 常量与 migrations/ 而漏改 Sql 种子,全新空库启动会在
/// PostgresSchemaBootstrap.VerifyAsync fail-fast(2026-10-27 容量门禁 tmpfs 空库实测回归);
/// 存量库靠迁移链推进不会暴露,故以此纯文本断言锚定两者的同步耦合。
/// </summary>
public sealed class PostgresSchemaSqlSeedTests
{
    [Fact]
    public void Sql_SeedsCurrentSchemaVersion_ForFreshDatabaseBootstrap()
    {
        Assert.Contains($"({PostgresSchema.Version}, now())", PostgresSchema.Sql, StringComparison.Ordinal);
    }
}
