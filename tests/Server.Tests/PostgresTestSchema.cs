using System.Collections.Concurrent;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>同一测试宿主内串行访问独享库，避免市场 Serializable 事务的跨测试 SSI 冲突。</summary>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection;

/// <summary>在一次性本地隔离测试库（库名须以 daohua_test_ 开头）上幂等建表，不触碰开发或生产库。</summary>
internal static class PostgresTestSchema
{
    private const string ConnectionVariable = "DAOHUA_MARKET_TEST_CONNECTION";
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> SharedSources = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> InitializedDatabases = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim InitGate = new(1, 1);

    public static async Task EnsureAsync(NpgsqlDataSource source)
    {
        var key = ValidateTarget(source.ConnectionString).Database!;
        if (InitializedDatabases.ContainsKey(key)) return;

        await InitGate.WaitAsync();
        try
        {
            if (InitializedDatabases.ContainsKey(key)) return;
            await using var connection = await source.OpenConnectionAsync();
            await using var existsCmd = new NpgsqlCommand(
                "SELECT to_regclass('platform.schema_version') IS NOT NULL", connection);
            if (await existsCmd.ExecuteScalarAsync() is not true)
            {
                await using var ddl = new NpgsqlCommand(PostgresSchema.Sql, connection) { CommandTimeout = 60 };
                await ddl.ExecuteNonQueryAsync();
            }
            InitializedDatabases[key] = 1;
        }
        finally
        {
            InitGate.Release();
        }
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
        var target = ValidateTarget(connection);
        target.MaxPoolSize = 16;
        target.Timeout = 5;
        target.CommandTimeout = 30;
        return target.ConnectionString;
    }

    internal static NpgsqlConnectionStringBuilder ValidateTarget(string connectionString)
    {
        const string error = "拒绝集成测试连接：要求回环地址（127.0.0.1/localhost）且库名以 daohua_test_ 开头。";
        NpgsqlConnectionStringBuilder input;
        try { input = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException(error); }
        if (input.Host is not ("127.0.0.1" or "localhost" or "::1")
            || string.IsNullOrWhiteSpace(input.Database)
            || !input.Database.StartsWith("daohua_test_", StringComparison.Ordinal))
            throw new InvalidOperationException(error);
        return input;
    }
}
