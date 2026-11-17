using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>同一测试宿主内串行访问独享库，避免市场 Serializable 事务的跨测试 SSI 冲突。</summary>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection;

/// <summary>仅在与本轮标识匹配、显式授权的空库上执行权威建表，不迁移或清表。</summary>
internal static class PostgresTestSchema
{
    private const string ConnectionVariable = "DAOHUA_MARKET_TEST_CONNECTION";
    private const string RunIdVariable = "DAOHUA_POSTGRES_TEST_RUN_ID";
    private static readonly string ProcessId = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> SharedSources = new(StringComparer.Ordinal);

    public static async Task EnsureAsync(NpgsqlDataSource source)
    {
        var expected = RequireConnectionString("PostgreSQL 集成测试");
        var target = ValidateTarget(source.ConnectionString, RequireRunId(), requirePassword: false);
        var allowed = new NpgsqlConnectionStringBuilder(expected);
        // NpgsqlDataSource 的公开连接串可脱敏；实际库和登录身份另由数据库回查。
        target.Remove("Password");
        allowed.Remove("Password");
        if (!target.EquivalentTo(allowed))
            throw new InvalidOperationException("数据源不是本轮配置的一次性测试库。");

        var ready = $"daohua-postgres-test-v1:{RequireRunId()}:ready";
        var claimed = $"daohua-postgres-test-v1:{RequireRunId()}:claimed:{ProcessId}";
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(772301)", connection, transaction))
            await acquire.ExecuteNonQueryAsync();

        // 服务器侧验证真实库名、登录角色、库所有者和预置标识。已领取的库只认当前宿主。
        await using var identity = new NpgsqlCommand("""
            SELECT pg_catalog.shobj_description(d.oid, 'pg_database')
            FROM pg_catalog.pg_database d
            WHERE d.datname = pg_catalog.current_database() AND d.datname = @database
              AND current_user = @owner AND session_user = @owner
              AND d.datdba = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
            """, connection, transaction);
        identity.Parameters.AddWithValue("database", target.Database!);
        identity.Parameters.AddWithValue("owner", target.Username!);
        var marker = await identity.ExecuteScalarAsync() as string;
        if (marker == claimed)
        {
            await transaction.CommitAsync();
            return;
        }
        if (marker != ready)
            throw new InvalidOperationException("拒绝建表：测试库缺少本轮 ready 标识、所有者不匹配或已被另一测试宿主领取。");

        await using var isEmpty = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT FROM pg_catalog.pg_namespace
                WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'public')
            ) AND NOT EXISTS (
                SELECT FROM pg_catalog.pg_depend
                WHERE refclassid = 'pg_catalog.pg_namespace'::regclass
                  AND refobjid = 'public'::regnamespace
            );
            """, connection, transaction);
        if (await isEmpty.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("拒绝建表：只接受来自未修改模板的空库。");

        await using var ddl = new NpgsqlCommand(PostgresSchema.Sql, connection, transaction) { CommandTimeout = 60 };
        await ddl.ExecuteNonQueryAsync();
        // 名称与标识只包含校验过的十六进制和常量；与建表一起提交，其他进程不能复用。
        await using var claim = new NpgsqlCommand($"COMMENT ON DATABASE {target.Database} IS '{claimed}';", connection, transaction);
        await claim.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    /// <summary>测试专用轻量 PBKDF2 迭代数（贴合数据库约束）；生产默认仍为 600,000。</summary>
    internal const int TestPbkdf2Iterations = 100_000;

    public static NpgsqlDataSource GetSharedDataSource(string connectionString)
        => SharedSources.GetOrAdd(connectionString, static cs => NpgsqlDataSource.Create(cs));

    public static string RequireConnectionString(string purpose)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException($"{purpose}需要一次性测试库：通过 scripts/verify.sh --with-postgres -- 供给 {ConnectionVariable}。");
        var target = ValidateTarget(connection, RequireRunId());
        target.MaxPoolSize = 16;
        target.Timeout = 5;
        target.CommandTimeout = 30;
        return target.ConnectionString;
    }

    private static string RequireRunId() => Environment.GetEnvironmentVariable(RunIdVariable)
        ?? throw new InvalidOperationException($"一次性测试库必须配置 {RunIdVariable}。");

    internal static NpgsqlConnectionStringBuilder ValidateTarget(string connectionString, string runId, bool requirePassword = true)
    {
        var error = "拒绝集成测试连接：要求 127.0.0.1、显式端口与密码，以及与本轮随机标识对应的库名和独占角色。";
        NpgsqlConnectionStringBuilder input;
        try { input = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException(error); } // 不回显连接串或解析异常。
        if (!Regex.IsMatch(runId, "\\A[0-9a-f]{32}\\z")
            || input.Host != "127.0.0.1" || !input.ShouldSerialize("Port") || input.Port is < 1 or > 65535
            || input.Database != $"daohua_test_{runId}" || input.Username != input.Database
            || (requirePassword && string.IsNullOrWhiteSpace(input.Password))
            || input.Keys.Cast<string>().Any(key => key is not ("Host" or "Port" or "Database" or "Username"
                or "Password" or "Maximum Pool Size" or "Timeout" or "Command Timeout")))
            throw new InvalidOperationException(error);
        return input;
    }
}
