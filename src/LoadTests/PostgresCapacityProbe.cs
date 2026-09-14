namespace DaoHua.LoadTests;

/// <summary>
/// 在临时 schema 中对 PostgreSQL/WAL/latest-only Worker 进行真实边界探测。
/// 该 schema 只用于容量门禁，执行结束默认删除，不读取或写入生产业务表。
/// </summary>
internal static class PostgresCapacityProbe
{
    /// <summary>
    /// 在临时 schema 中创建账号、section 与刷新队列表，并执行批量写入与最新值冲刷，
    /// 最后估算数据与 WAL 占用。
    /// </summary>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>PostgreSQL 探测结果。</returns>
    public static async Task<PostgresProbeResult> RunAsync(
        LoadTestOptions options,
        CancellationToken cancellationToken)
    {
        var schema = $"capacity_probe_{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(options.DatabaseConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var qualifiedSchema = QuoteIdentifier(schema);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {qualifiedSchema}", cancellationToken);
            await CreateTablesAsync(connection, qualifiedSchema, cancellationToken);
            var beforeLsn = await ScalarStringAsync(connection, "SELECT pg_current_wal_lsn()", cancellationToken);
            var started = Stopwatch.GetTimestamp();

            await CopyAccountsAsync(connection, qualifiedSchema, cancellationToken);
            await CopySectionsAsync(connection, qualifiedSchema, cancellationToken);
            var queued = await EnqueueLatestOnlyAsync(connection, qualifiedSchema, options, cancellationToken);
            var flushed = await FlushQueueAsync(connection, qualifiedSchema, cancellationToken);
            var afterLsn = await ScalarStringAsync(connection, "SELECT pg_current_wal_lsn()", cancellationToken);

            var walBytes = await WalDifferenceAsync(connection, beforeLsn, afterLsn, cancellationToken);
            var relationBytes = await RelationSizeAsync(connection, schema, cancellationToken);
            var queueCount = await ScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM {qualifiedSchema}.section_flush_queue",
                cancellationToken);
            var accountCount = await ScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM {qualifiedSchema}.accounts",
                cancellationToken);
            var sectionCount = await ScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM {qualifiedSchema}.player_state_sections",
                cancellationToken);

            return new PostgresProbeResult(
                schema,
                accountCount,
                sectionCount,
                queued,
                flushed,
                queueCount,
                relationBytes,
                walBytes,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            if (!options.KeepProbeSchema && connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", CancellationToken.None);
                }
                catch
                {
                    // 清理失败不覆盖容量探测的原始异常；schema 名称已在报告/日志中可定位。
                }
            }
        }
    }

    /// <summary>
    /// 在指定 schema 中创建账号、section 与刷新队列表。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">已转义的 schema 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示建表完成的任务。</returns>
    private static async Task CreateTablesAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE TABLE {schema}.accounts (
                account_id uuid PRIMARY KEY,
                login_normalized text NOT NULL UNIQUE,
                created_at timestamptz NOT NULL
            );
            CREATE TABLE {schema}.player_state_sections (
                player_id uuid NOT NULL,
                section_kind text NOT NULL,
                section_version bigint NOT NULL,
                payload bytea NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (player_id, section_kind)
            );
            CREATE TABLE {schema}.section_flush_queue (
                player_id uuid NOT NULL,
                section_kind text NOT NULL,
                state_version bigint NOT NULL,
                payload bytea NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (player_id, section_kind)
            );
            CREATE INDEX section_flush_queue_due_idx
                ON {schema}.section_flush_queue (updated_at, player_id);
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    /// <summary>
    /// 使用 COPY BINARY 批量导入测试账号。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">已转义的 schema 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示导入完成的任务。</returns>
    private static async Task CopyAccountsAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
            $"COPY {schema}.accounts (account_id, login_normalized, created_at) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < CapacityTargetScenario.RegisteredAccountTarget; index++)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(CapacityTargetScenarioTestId(index), NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync($"capacity-account-{index:D6}", NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(createdAt, NpgsqlDbType.TimestampTz, cancellationToken);
        }

        await importer.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// 使用 COPY BINARY 批量导入测试 section 数据。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">已转义的 schema 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示导入完成的任务。</returns>
    private static async Task CopySectionsAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
            $"COPY {schema}.player_state_sections (player_id, section_kind, section_version, payload, updated_at) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < CapacityTargetScenario.RegisteredAccountTarget; index++)
        {
            var playerId = CapacityTargetScenarioTestId(index);
            foreach (var kind in CapacityTargetScenario.SectionKinds)
            {
                var payload = CapacityTargetScenarioPayload(index, kind);
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(playerId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(kind, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(1L, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(payload, NpgsqlDbType.Bytea, cancellationToken);
                await importer.WriteAsync(createdAt, NpgsqlDbType.TimestampTz, cancellationToken);
            }
        }

        await importer.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// 模拟 latest-only Worker 的 upsert 行为，将状态变更写入刷新队列。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">已转义的 schema 名称。</param>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>入队行数。</returns>
    private static async Task<long> EnqueueLatestOnlyAsync(
        NpgsqlConnection connection,
        string schema,
        LoadTestOptions options,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {schema}.section_flush_queue
                (player_id, section_kind, state_version, payload, updated_at)
            VALUES (@player_id, @section_kind, @state_version, @payload, @updated_at)
            ON CONFLICT (player_id, section_kind) DO UPDATE SET
                state_version = EXCLUDED.state_version,
                payload = EXCLUDED.payload,
                updated_at = EXCLUDED.updated_at
            """;
        command.Parameters.Add("player_id", NpgsqlDbType.Uuid);
        command.Parameters.Add("section_kind", NpgsqlDbType.Text);
        command.Parameters.Add("state_version", NpgsqlDbType.Bigint);
        command.Parameters.Add("payload", NpgsqlDbType.Bytea);
        command.Parameters.Add("updated_at", NpgsqlDbType.TimestampTz);
        await command.PrepareAsync(cancellationToken);

        var count = 0L;
        var rounds = checked(options.StateChangesPerSecond * options.StateChangeSeconds);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < rounds; index++)
        {
            var playerIndex = index % Math.Min(CapacityTargetScenario.PeakNetworkTarget, CapacityTargetScenario.RegisteredAccountTarget);
            command.Parameters["player_id"].Value = CapacityTargetScenarioTestId(playerIndex);
            command.Parameters["section_kind"].Value = CapacityTargetScenario.SectionKinds[index % CapacityTargetScenario.SectionKinds.Length];
            command.Parameters["state_version"].Value = index + 2L;
            command.Parameters["payload"].Value = CapacityTargetScenarioPayload(playerIndex, "hot");
            command.Parameters["updated_at"].Value = now.AddMilliseconds(index);
            await command.ExecuteNonQueryAsync(cancellationToken);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 按时间顺序将刷新队列中的行批量 upsert 到 section 表并删除已处理队列行。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">已转义的 schema 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已冲刷行数。</returns>
    private static async Task<long> FlushQueueAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        const int batchSize = 256;
        long flushed = 0;
        while (true)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var rows = new List<QueueRow>(batchSize);
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"""
                    SELECT player_id, section_kind, state_version, payload
                    FROM {schema}.section_flush_queue
                    ORDER BY updated_at, player_id
                    FOR UPDATE SKIP LOCKED
                    LIMIT {batchSize}
                    """;
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new QueueRow(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        await reader.GetFieldValueAsync<byte[]>(3, cancellationToken)));
                }
            }

            if (rows.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                break;
            }

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = $"""
                    INSERT INTO {schema}.player_state_sections
                        (player_id, section_kind, section_version, payload, updated_at)
                    VALUES (@player_id, @section_kind, @state_version, @payload, CURRENT_TIMESTAMP)
                    ON CONFLICT (player_id, section_kind) DO UPDATE SET
                        section_version = EXCLUDED.section_version,
                        payload = EXCLUDED.payload,
                        updated_at = EXCLUDED.updated_at
                    """;
                upsert.Parameters.Add("player_id", NpgsqlDbType.Uuid);
                upsert.Parameters.Add("section_kind", NpgsqlDbType.Text);
                upsert.Parameters.Add("state_version", NpgsqlDbType.Bigint);
                upsert.Parameters.Add("payload", NpgsqlDbType.Bytea);
                await upsert.PrepareAsync(cancellationToken);
                foreach (var row in rows)
                {
                    upsert.Parameters["player_id"].Value = row.PlayerId;
                    upsert.Parameters["section_kind"].Value = row.SectionKind;
                    upsert.Parameters["state_version"].Value = row.StateVersion;
                    upsert.Parameters["payload"].Value = row.Payload;
                    await upsert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {schema}.section_flush_queue WHERE player_id = @player_id AND section_kind = @section_kind AND state_version = @state_version";
                delete.Parameters.Add("player_id", NpgsqlDbType.Uuid);
                delete.Parameters.Add("section_kind", NpgsqlDbType.Text);
                delete.Parameters.Add("state_version", NpgsqlDbType.Bigint);
                await delete.PrepareAsync(cancellationToken);
                foreach (var row in rows)
                {
                    delete.Parameters["player_id"].Value = row.PlayerId;
                    delete.Parameters["section_kind"].Value = row.SectionKind;
                    delete.Parameters["state_version"].Value = row.StateVersion;
                    flushed += await delete.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return flushed;
    }

    /// <summary>
    /// 查询指定 schema 下所有表与索引的总占用字节数。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="schema">原始 schema 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总字节数。</returns>
    private static async Task<long> RelationSizeAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(pg_total_relation_size(c.oid)), 0)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
            """;
        command.Parameters.AddWithValue("schema", schema);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// 计算两个 PostgreSQL WAL LSN 之间的字节差值。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="before">起始 LSN。</param>
    /// <param name="after">结束 LSN。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>WAL 增长字节数。</returns>
    private static async Task<long> WalDifferenceAsync(
        NpgsqlConnection connection,
        string before,
        string after,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(pg_wal_lsn_diff(@after::pg_lsn, @before::pg_lsn), 0)";
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("before", before);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// 执行无返回 SQL。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示执行完成的任务。</returns>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 执行 SQL 并以字符串形式返回标量结果。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标量字符串。</returns>
    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    /// <summary>
    /// 执行 SQL 并以长整型形式返回标量结果。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标量长整型。</returns>
    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// 将标识符转义为 PostgreSQL 引用形式。
    /// </summary>
    /// <param name="value">原始标识符。</param>
    /// <returns>转义后的引用字符串。</returns>
    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// 基于索引生成容量探测用的测试账号 ID。
    /// </summary>
    /// <param name="index">玩家索引。</param>
    /// <returns>生成的测试 <see cref="Guid"/>。</returns>
    private static Guid CapacityTargetScenarioTestId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0xD2;
        return new Guid(bytes);
    }

    /// <summary>
    /// 生成用于 PostgreSQL 探测的 synthetic 测试载荷。
    /// </summary>
    /// <param name="index">玩家索引。</param>
    /// <param name="kind">section 类型。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    private static byte[] CapacityTargetScenarioPayload(int index, string kind) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            player = CapacityTargetScenarioTestId(index),
            section = kind,
            stateVersion = index + 1,
            values = new[] { index % 101, index % 37, index % 13 }
        });

    /// <summary>
    /// 刷新队列中的一行数据。
    /// </summary>
    /// <param name="PlayerId">玩家 ID。</param>
    /// <param name="SectionKind">section 类型。</param>
    /// <param name="StateVersion">状态版本。</param>
    /// <param name="Payload">载荷。</param>
    private sealed record QueueRow(Guid PlayerId, string SectionKind, long StateVersion, byte[] Payload);
}

/// <summary>
/// PostgreSQL 容量探测结果。
/// </summary>
/// <param name="Schema">探测使用的临时 schema 名称。</param>
/// <param name="AccountCount">导入账号数。</param>
/// <param name="SectionCount">导入 section 数。</param>
/// <param name="QueueUpserts">刷新队列 upsert 数。</param>
/// <param name="QueueRowsFlushed">已冲刷队列行数。</param>
/// <param name="RemainingQueueRows">剩余队列行数。</param>
/// <param name="DataBytes">数据表占用字节数。</param>
/// <param name="WalBytes">WAL 增长字节数。</param>
/// <param name="ElapsedMilliseconds">探测耗时（毫秒）。</param>
internal sealed record PostgresProbeResult(
    string Schema,
    long AccountCount,
    long SectionCount,
    long QueueUpserts,
    long QueueRowsFlushed,
    long RemainingQueueRows,
    long DataBytes,
    long WalBytes,
    double ElapsedMilliseconds);
