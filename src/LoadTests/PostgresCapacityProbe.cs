namespace DaoHua.LoadTests;

/// <summary>
/// 在临时 schema 中对 PostgreSQL/WAL/latest-only Worker 进行真实边界探测。
/// 该 schema 只用于容量门禁，执行结束默认删除，不读取或写入生产业务表。
/// </summary>
internal static class PostgresCapacityProbe
{
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static Guid CapacityTargetScenarioTestId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0xD2;
        return new Guid(bytes);
    }

    private static byte[] CapacityTargetScenarioPayload(int index, string kind) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            player = CapacityTargetScenarioTestId(index),
            section = kind,
            stateVersion = index + 1,
            values = new[] { index % 101, index % 37, index % 13 }
        });

    private sealed record QueueRow(Guid PlayerId, string SectionKind, long StateVersion, byte[] Payload);
}

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
