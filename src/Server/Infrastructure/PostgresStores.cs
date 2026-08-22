// ==============================================================================
// 文件名称: PostgresStores.cs
// 主要职责: 提供基于 PostgreSQL 的快照存储、Durable Outbox、市场账本与分区表落盘实现。
// 核心逻辑 / 定义内容:
//   1. 数据库配置与架构自动初始化服务 (DatabaseOptions, PostgresSchemaHostedService)。
//   2. 玩家快照与二进制载荷高效读写存储 (PostgresSnapshotStore)。
//   3. 崩溃恢复与高吞吐持久化出件箱 (PostgresPersistenceOutbox)。
//   4. 市场账本 CAS 乐观锁并发写入与撮合流水按日分区存储 (PostgresMarketLedgerStore)。
// ==============================================================================

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Domain.Market;
using Domain.Models;
using YuoCore.Persistence;

namespace Server.Infrastructure;

/// <summary>
/// PostgreSQL 数据库连接与表结构选项配置 (DatabaseOptions)。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>存储提供者标识（固定为 "postgres"）。</summary>
    public string Provider { get; init; } = "postgres";

    /// <summary>PostgreSQL 数据库连接字符串。</summary>
    public string? ConnectionString { get; init; }

    /// <summary>是否在应用启动时自动检查并创建表结构。</summary>
    public bool EnableSchemaBootstrap { get; init; } = true;

    /// <summary>市场撮合成交流水在数据库分区表中的保留天数（默认 14 天）。</summary>
    public int MarketTradeRetentionDays { get; init; } = 14;

    public static DatabaseOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new DatabaseOptions
        {
            Provider = configuration["Storage:Provider"] ?? "postgres",
            ConnectionString = configuration.GetConnectionString("")
                ?? configuration["Storage:ConnectionString"],
            EnableSchemaBootstrap = configuration.GetValue("Storage:EnableSchemaBootstrap", true),
            MarketTradeRetentionDays = Math.Clamp(
                configuration.GetValue("Market:TradeRetentionDays", 14),
                1,
                90)
        };
    }

    /// <summary>当前存储提供者是否为 PostgreSQL。</summary>
    public bool IsPostgres => string.Equals(Provider, "postgres", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// PostgreSQL 数据库架构自动初始化后台服务 (PostgresSchemaHostedService)。
/// <para>
/// 在服务启动时自动创建 accounts, player_snapshots, persistence_outbox, market_ledgers 等核心表，并自动创建/清理市场成交按日分区表。
/// </para>
/// </summary>
public sealed class PostgresSchemaHostedService(
    NpgsqlDataSource dataSource,
    DatabaseOptions options,
    ILogger<PostgresSchemaHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaBootstrap)
        {
            logger.LogInformation("PostgreSQL 自动建表已关闭，跳过 schema bootstrap");
            return;
        }

        await PostgresSchema.EnsureCreatedAsync(
            dataSource,
            options.MarketTradeRetentionDays,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("PostgreSQL schema 已确认，市场流水保留 {RetentionDays} 天", options.MarketTradeRetentionDays);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class PostgresSchema
{
    public static async Task EnsureCreatedAsync(
        NpgsqlDataSource dataSource,
        int marketTradeRetentionDays = 14,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);

        await EnsureMarketTradePartitionsAsync(
            connection,
            transaction,
            marketTradeRetentionDays,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMarketTradePartitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var offset = -1; offset <= 2; offset++)
        {
            var start = today.AddDays(offset);
            var end = start.AddDays(1);
            var partitionName = $"market_trades_{start:yyyyMMdd}";
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE TABLE IF NOT EXISTS {QuoteIdentifier(partitionName)}
                PARTITION OF market_trades
                FOR VALUES FROM ('{start:yyyy-MM-dd}') TO ('{end:yyyy-MM-dd}')
                """,
                cancellationToken).ConfigureAwait(false);
        }

        var cutoff = today.AddDays(-retentionDays);
        await using var command = new NpgsqlCommand(
            """
            SELECT child.relname
            FROM pg_inherits
            JOIN pg_class child ON child.oid = pg_inherits.inhrelid
            JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
            WHERE parent.relname = 'market_trades'
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var expiredPartitions = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (name.Length != "market_trades_yyyyMMdd".Length) continue;
            if (!DateOnly.TryParseExact(name["market_trades_".Length..], "yyyyMMdd", out var partitionDate)) continue;
            if (partitionDate < cutoff)
            {
                expiredPartitions.Add(name);
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (var name in expiredPartitions)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"DROP TABLE IF EXISTS {QuoteIdentifier(name)}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS accounts (
    player_id UUID PRIMARY KEY,
    account VARCHAR(32) NOT NULL,
    account_normalized VARCHAR(32) NOT NULL,
    password_hash VARCHAR(128) NOT NULL,
    salt VARCHAR(48) NOT NULL,
    character_name VARCHAR(32) NOT NULL,
    character_name_normalized VARCHAR(32) NOT NULL,
    sequence_id BIGINT GENERATED BY DEFAULT AS IDENTITY UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (account_normalized),
    UNIQUE (character_name_normalized)
);

CREATE TABLE IF NOT EXISTS player_snapshots (
    kind VARCHAR(32) NOT NULL,
    id VARCHAR(64) NOT NULL,
    payload_encoding VARCHAR(16) NOT NULL DEFAULT 'br',
    payload BYTEA NOT NULL,
    payload_bytes INT NOT NULL,
    compressed_bytes INT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    player_id UUID,
    account VARCHAR(32),
    character_name VARCHAR(32),
    sequence_id BIGINT,
    version BIGINT,
    presence_mode SMALLINT,
    last_simulated_at TIMESTAMPTZ,
    PRIMARY KEY (kind, id)
) WITH (autovacuum_vacuum_scale_factor = 0.05);

CREATE INDEX IF NOT EXISTS ix_player_snapshots_player_id ON player_snapshots (player_id) WHERE player_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_player_snapshots_idle_backlog ON player_snapshots (presence_mode, last_simulated_at) WHERE kind = 'player';

CREATE TABLE IF NOT EXISTS market_ledgers (
    partition_id INT NOT NULL,
    partition_count INT NOT NULL,
    algorithm VARCHAR(64) NOT NULL,
    revision BIGINT NOT NULL,
    payload_encoding VARCHAR(16) NOT NULL DEFAULT 'br',
    payload BYTEA NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    writer_instance VARCHAR(160) NOT NULL,
    PRIMARY KEY (partition_id, partition_count, algorithm)
) WITH (autovacuum_vacuum_scale_factor = 0.05);

CREATE TABLE IF NOT EXISTS persistence_outbox (
    sequence BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    command_kind SMALLINT NOT NULL,
    snapshot_kind VARCHAR(32),
    snapshot_id VARCHAR(64),
    event_stream VARCHAR(96),
    event_type VARCHAR(64),
    payload BYTEA NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS persistence_outbox_ack (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    acknowledged_through BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS distributed_compute_jobs (
    job_id UUID PRIMARY KEY,
    player_id UUID NOT NULL,
    assigned_node_id VARCHAR(80),
    status SMALLINT NOT NULL DEFAULT 0,
    target_time TIMESTAMPTZ NOT NULL,
    input_payload BYTEA NOT NULL,
    output_payload BYTEA,
    error_code VARCHAR(64),
    error_message VARCHAR(512),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_distributed_compute_jobs_status ON distributed_compute_jobs (status, created_at);

CREATE TABLE IF NOT EXISTS market_trades (
    trade_id BIGINT GENERATED ALWAYS AS IDENTITY,
    buyer_id UUID NOT NULL,
    seller_id UUID NOT NULL,
    item_id VARCHAR(32) NOT NULL,
    enhancement_level SMALLINT NOT NULL DEFAULT 0,
    price BIGINT NOT NULL,
    quantity BIGINT NOT NULL,
    gross_amount BIGINT NOT NULL,
    tax_amount BIGINT NOT NULL DEFAULT 0,
    traded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (trade_id, traded_at)
) PARTITION BY RANGE (traded_at);
""";
}

/// <summary>
/// PostgreSQL 载荷压缩编码工具类（透明集成 Brotli 压缩，小载荷或压缩无收益时自动使用 raw 原始存储）。
/// </summary>
public static class PostgresPayloadCodec
{
    public const string Raw = "raw";
    public const string Brotli = "br";

    /// <summary>
    /// 对二进制载荷进行条件压缩（大于 256 字节且压缩后体积缩小时采用 Brotli，否则保持 raw）。
    /// </summary>
    public static byte[] Encode(ReadOnlyMemory<byte> payload, out string encoding, out int originalBytes)
    {
        originalBytes = payload.Length;
        if (payload.Length < 256)
        {
            encoding = Raw;
            return payload.ToArray();
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(payload.Span);
        }

        var compressed = output.ToArray();
        if (compressed.Length >= payload.Length)
        {
            encoding = Raw;
            return payload.ToArray();
        }

        encoding = Brotli;
        return compressed;
    }

    /// <summary>
    /// 根据编码标识解码载荷。
    /// </summary>
    public static byte[] Decode(string encoding, byte[] payload)
    {
        return encoding switch
        {
            Raw => payload,
            Brotli => DecodeBrotli(payload),
            _ => throw new InvalidDataException($"未知快照压缩格式: {encoding}")
        };
    }

    private static byte[] DecodeBrotli(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// 基于 PostgreSQL 的快照仓储实现 (PostgresSnapshotStore)。
/// <para>
/// 职责：
/// 1. 将快照存入 <c>player_snapshots</c> 表，利用 <c>ON CONFLICT (kind, id) DO UPDATE</c> 实现原子 Upsert。
/// 2. 自动提取核心元数据列（player_id, account, character_name, sequence_id, version, presence_mode, last_simulated_at），便于运维与冷挂机检索。
/// 3. 支持跨模块事务同批次写入（内部提供携带 Connection/Transaction 的重载）。
/// </para>
/// </summary>
public sealed class PostgresSnapshotStore(NpgsqlDataSource dataSource) : IBatchSnapshotStore
{
    public const string UpsertSnapshotSql = """
        INSERT INTO player_snapshots (
            kind, id, payload_encoding, payload, payload_bytes, compressed_bytes, updated_at,
            player_id, account, character_name, sequence_id, version, presence_mode, last_simulated_at)
        VALUES (
            @kind, @id, @encoding, @payload, @payloadBytes, @compressedBytes, @updatedAt,
            @playerId, @account, @characterName, @sequenceId, @version, @presenceMode, @lastSimulatedAt)
        ON CONFLICT (kind, id) DO UPDATE SET
            payload_encoding = EXCLUDED.payload_encoding,
            payload = EXCLUDED.payload,
            payload_bytes = EXCLUDED.payload_bytes,
            compressed_bytes = EXCLUDED.compressed_bytes,
            updated_at = EXCLUDED.updated_at,
            player_id = EXCLUDED.player_id,
            account = EXCLUDED.account,
            character_name = EXCLUDED.character_name,
            sequence_id = EXCLUDED.sequence_id,
            version = EXCLUDED.version,
            presence_mode = EXCLUDED.presence_mode,
            last_simulated_at = EXCLUDED.last_simulated_at
        WHERE player_snapshots.version IS NULL
           OR (EXCLUDED.version IS NOT NULL AND EXCLUDED.version > player_snapshots.version)
        """;

    /// <summary>
    /// 判断传入快照版本是否满足对现有快照版本的更新条件（单调 CAS 规则）。
    /// <para>
    /// 1. 若现有快照无版本号（null），允许更新；
    /// 2. 若传入快照带有版本号且严格大于现有快照版本号（incomingVersion > existingVersion），允许更新；
    /// 3. 其余情况（传入版本号为空但现有已有版本号、传入版本号小于等于现有版本号）均拒绝覆盖。
    /// </para>
    /// </summary>
    public static bool ShouldUpdateSnapshot(long? existingVersion, long? incomingVersion)
    {
        if (!existingVersion.HasValue) return true;
        return incomingVersion.HasValue && incomingVersion.Value > existingVersion.Value;
    }

    public async ValueTask SaveAsync(YuoSnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        var metadata = ExtractPlayerMetadata(snapshot);
        var payload = PostgresPayloadCodec.Encode(snapshot.Payload, out var encoding, out var originalBytes);
        await using var command = dataSource.CreateCommand(UpsertSnapshotSql);
        BindSnapshotParameters(command, snapshot, metadata, encoding, payload, originalBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveBatchAsync(
        IReadOnlyList<YuoSnapshotRecord> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var snapshot in snapshots)
            {
                await SaveAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<YuoSnapshotRecord?> LoadAsync(
        string kind,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT payload_encoding, payload, updated_at
            FROM player_snapshots
            WHERE kind = @kind AND id = @id
            """);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var encoding = reader.GetString(0);
        var storedPayload = (byte[])reader[1];
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);
        return new YuoSnapshotRecord(kind, id, PostgresPayloadCodec.Decode(encoding, storedPayload), updatedAt);
    }

    internal static async ValueTask SaveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        YuoSnapshotRecord snapshot,
        CancellationToken cancellationToken = default)
    {
        var metadata = ExtractPlayerMetadata(snapshot);
        var payload = PostgresPayloadCodec.Encode(snapshot.Payload, out var encoding, out var originalBytes);
        await using var command = new NpgsqlCommand(UpsertSnapshotSql, connection, transaction);
        BindSnapshotParameters(command, snapshot, metadata, encoding, payload, originalBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindSnapshotParameters(
        NpgsqlCommand command,
        YuoSnapshotRecord snapshot,
        PlayerSnapshotMetadata metadata,
        string encoding,
        byte[] payload,
        int originalBytes)
    {
        command.Parameters.AddWithValue("kind", snapshot.Kind);
        command.Parameters.AddWithValue("id", snapshot.Id);
        command.Parameters.AddWithValue("encoding", encoding);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload);
        command.Parameters.AddWithValue("payloadBytes", originalBytes);
        command.Parameters.AddWithValue("compressedBytes", payload.Length);
        command.Parameters.AddWithValue("updatedAt", snapshot.UpdatedAt);
        AddNullable(command, "playerId", NpgsqlDbType.Uuid, metadata.PlayerId);
        AddNullable(command, "account", NpgsqlDbType.Varchar, metadata.Account);
        AddNullable(command, "characterName", NpgsqlDbType.Varchar, metadata.CharacterName);
        AddNullable(command, "sequenceId", NpgsqlDbType.Bigint, metadata.SequenceId);
        AddNullable(command, "version", NpgsqlDbType.Bigint, metadata.Version);
        AddNullable(command, "presenceMode", NpgsqlDbType.Smallint, metadata.PresenceMode);
        AddNullable(command, "lastSimulatedAt", NpgsqlDbType.TimestampTz, metadata.LastSimulatedAt);
    }

    private static PlayerSnapshotMetadata ExtractPlayerMetadata(YuoSnapshotRecord snapshot)
    {
        if (!string.Equals(snapshot.Kind, "player", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        if (!Guid.TryParse(snapshot.Id, out var playerId))
        {
            throw new InvalidDataException($"玩家快照 Id 必须为有效 GUID，实际为: '{snapshot.Id}'");
        }

        var envelope = StateSerializer.DeserializeEnvelope(snapshot.Payload, Domain.GameData.GameDataCatalog.Current.Fingerprint);
        var state = envelope.Payload;
        if (state.PlayerId != playerId)
        {
            throw new InvalidDataException($"快照 Id '{snapshot.Id}' 与玩家状态 PlayerId '{state.PlayerId:D}' 不一致。");
        }

        return new PlayerSnapshotMetadata(
            playerId,
            EmptyToNull(state.Account),
            EmptyToNull(state.Name),
            envelope.SequenceId,
            envelope.PlayerStateVersion,
            (short)state.PresenceMode,
            envelope.LastSimulatedAt);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }

    private readonly record struct PlayerSnapshotMetadata(
        Guid? PlayerId,
        string? Account,
        string? CharacterName,
        long? SequenceId,
        long? Version,
        short? PresenceMode,
        DateTimeOffset? LastSimulatedAt);
}

/// <summary>
/// 基于 PostgreSQL 的持久化出件箱实现 (PostgresPersistenceOutbox)。
/// <para>
/// 职责：
/// 1. 提供可靠消息落库 (<see cref="AppendAsync"/>)，利用自增序列号 sequence 保证全局严格单调递增。
/// 2. 重启时扫描未确认命令 (<see cref="ReplayAsync"/>)，向持久化引擎重放。
/// 3. 执行确认 (<see cref="AcknowledgeThroughAsync"/>)，更新水位并物理清理已消费的 Outbox 行。
/// </para>
/// </summary>
public sealed class PostgresPersistenceOutbox(NpgsqlDataSource dataSource) : IBatchPersistenceOutbox
{
    private const short SnapshotCommand = 1;
    private const short EventCommand = 2;

    public async ValueTask<long> AppendAsync(YuoPersistenceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is YuoOutboxPersistenceCommand)
        {
            throw new ArgumentException("append inner command only", nameof(command));
        }

        var frame = Encode(command);
        await using var dbCommand = dataSource.CreateCommand(
            """
            INSERT INTO persistence_outbox (
                command_kind, snapshot_kind, snapshot_id, event_stream, event_type, payload, occurred_at)
            VALUES (@kind, @snapshotKind, @snapshotId, @eventStream, @eventType, @payload, @occurredAt)
            RETURNING sequence
            """);
        dbCommand.Parameters.AddWithValue("kind", frame.Kind);
        AddNullable(dbCommand, "snapshotKind", NpgsqlDbType.Varchar, frame.SnapshotKind);
        AddNullable(dbCommand, "snapshotId", NpgsqlDbType.Varchar, frame.SnapshotId);
        AddNullable(dbCommand, "eventStream", NpgsqlDbType.Varchar, frame.EventStream);
        AddNullable(dbCommand, "eventType", NpgsqlDbType.Varchar, frame.EventType);
        dbCommand.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, frame.Payload);
        dbCommand.Parameters.AddWithValue("occurredAt", frame.OccurredAt);
        return (long)(await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL outbox 未返回 sequence。"));
    }

    public async ValueTask<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyList<YuoPersistenceCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) return [];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sequences = new long[commands.Count];
        try
        {
            await using var dbCommand = new NpgsqlCommand(
                """
                INSERT INTO persistence_outbox (
                    command_kind, snapshot_kind, snapshot_id, event_stream, event_type, payload, occurred_at)
                VALUES (@kind, @snapshotKind, @snapshotId, @eventStream, @eventType, @payload, @occurredAt)
                RETURNING sequence
                """,
                connection,
                transaction);
            var kind = dbCommand.Parameters.Add("kind", NpgsqlDbType.Smallint);
            var snapshotKind = dbCommand.Parameters.Add("snapshotKind", NpgsqlDbType.Varchar);
            var snapshotId = dbCommand.Parameters.Add("snapshotId", NpgsqlDbType.Varchar);
            var eventStream = dbCommand.Parameters.Add("eventStream", NpgsqlDbType.Varchar);
            var eventType = dbCommand.Parameters.Add("eventType", NpgsqlDbType.Varchar);
            var payload = dbCommand.Parameters.Add("payload", NpgsqlDbType.Bytea);
            var occurredAt = dbCommand.Parameters.Add("occurredAt", NpgsqlDbType.TimestampTz);

            for (var index = 0; index < commands.Count; index++)
            {
                var frame = Encode(commands[index]);
                kind.Value = frame.Kind;
                snapshotKind.Value = (object?)frame.SnapshotKind ?? DBNull.Value;
                snapshotId.Value = (object?)frame.SnapshotId ?? DBNull.Value;
                eventStream.Value = (object?)frame.EventStream ?? DBNull.Value;
                eventType.Value = (object?)frame.EventType ?? DBNull.Value;
                payload.Value = frame.Payload;
                occurredAt.Value = frame.OccurredAt;
                sequences[index] = (long)(await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("PostgreSQL outbox 批量写入未返回 sequence。"));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return sequences;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<YuoPersistenceOutboxEntry> ReplayAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT sequence, command_kind, snapshot_kind, snapshot_id, event_stream, event_type, payload, occurred_at
            FROM persistence_outbox
            WHERE sequence > COALESCE((SELECT acknowledged_through FROM persistence_outbox_ack WHERE id = 1), 0)
            ORDER BY sequence
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(0);
            var frame = new OutboxFrame(
                reader.GetInt16(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                (byte[])reader[6],
                reader.GetFieldValue<DateTimeOffset>(7));
            yield return new YuoPersistenceOutboxEntry(sequence, Decode(frame));
        }
    }

    public async ValueTask AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence <= 0) return;
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO persistence_outbox_ack (id, acknowledged_through, updated_at)
            VALUES (1, @sequence, NOW())
            ON CONFLICT (id) DO UPDATE SET
                acknowledged_through = GREATEST(persistence_outbox_ack.acknowledged_through, EXCLUDED.acknowledged_through),
                updated_at = NOW();
            DELETE FROM persistence_outbox WHERE sequence <= @sequence;
            """);
        command.Parameters.AddWithValue("sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompactAsync(CancellationToken cancellationToken = default) => AcknowledgeThroughAsync(0, cancellationToken);

    private static OutboxFrame Encode(YuoPersistenceCommand command)
    {
        return command switch
        {
            SaveSnapshotCommand snapshot => new OutboxFrame(
                SnapshotCommand,
                snapshot.Snapshot.Kind,
                snapshot.Snapshot.Id,
                null,
                null,
                snapshot.Snapshot.Payload.ToArray(),
                snapshot.Snapshot.UpdatedAt),
            AppendEventCommand append => new OutboxFrame(
                EventCommand,
                null,
                null,
                append.Event.Stream,
                append.Event.EventType,
                append.Event.Payload.ToArray(),
                append.Event.CreatedAt),
            _ => throw new NotSupportedException($"PostgreSQL outbox 不支持命令 {command.GetType().Name}。")
        };
    }

    private static YuoPersistenceCommand Decode(OutboxFrame frame)
    {
        return frame.Kind switch
        {
            SnapshotCommand => new SaveSnapshotCommand(new YuoSnapshotRecord(
                Required(frame.SnapshotKind, nameof(frame.SnapshotKind)),
                Required(frame.SnapshotId, nameof(frame.SnapshotId)),
                frame.Payload,
                frame.OccurredAt)),
            EventCommand => new AppendEventCommand(new YuoEventRecord(
                Required(frame.EventStream, nameof(frame.EventStream)),
                Required(frame.EventType, nameof(frame.EventType)),
                frame.Payload,
                frame.OccurredAt)),
            _ => throw new InvalidDataException($"未知 PostgreSQL outbox 命令类型: {frame.Kind}")
        };
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"outbox 字段 {name} 为空。") : value;

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }

    private readonly record struct OutboxFrame(
        short Kind,
        string? SnapshotKind,
        string? SnapshotId,
        string? EventStream,
        string? EventType,
        byte[] Payload,
        DateTimeOffset OccurredAt);
}

/// <summary>
/// 基于 PostgreSQL 的市场账本仓储实现 (PostgresMarketLedgerStore)。
/// <para>
/// 核心并发与锁机制：
/// 1. 采用 PostgreSQL 会话级咨询排他锁 (<c>pg_try_advisory_lock</c>) 实现主分片单写者分布式租约。
/// 2. 基于版本号 (<c>revision</c>) CAS 乐观锁更新账本，冲突时抛出 <see cref="MarketLedgerConcurrencyException"/>。
/// 3. 支持在同一数据库事务中原子写入市场账本与玩家快照。
/// </para>
/// </summary>
public sealed class PostgresMarketLedgerStore(
    NpgsqlDataSource dataSource,
    ILogger<PostgresMarketLedgerStore> logger) : IMarketLedgerStore, IAsyncDisposable
{
    private NpgsqlConnection? _leaseConnection;
    private long _leaseKey;
    private string _instanceId = string.Empty;

    public string ProviderName => "postgres";
    public bool SupportsDistributedPartitions => true;
    public bool PersistsPlayerSnapshotAtomically => true;

    public async ValueTask AcquireWriterAsync(
        MarketTopology topology,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (_leaseConnection is not null)
        {
            throw new InvalidOperationException("当前实例已经持有 PostgreSQL 市场写者租约。");
        }

        var key = CreateAdvisoryLockKey(topology);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            if (!acquired)
            {
                throw new InvalidOperationException($"市场主分片 {topology.PartitionId}/{topology.PartitionCount} 已由其他实例持有。 ");
            }

            _leaseConnection = connection;
            _leaseKey = key;
            _instanceId = instanceId;
            logger.LogInformation("PostgreSQL 市场写者租约已取得 {PartitionId}/{PartitionCount} {InstanceId}",
                topology.PartitionId,
                topology.PartitionCount,
                instanceId);
            connection = null!;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<MarketLedgerState?> LoadAsync(
        MarketTopology topology,
        CancellationToken cancellationToken = default)
    {
        EnsureLease();
        await using var command = dataSource.CreateCommand(
            """
            SELECT payload_encoding, payload
            FROM market_ledgers
            WHERE partition_id = @partitionId
              AND partition_count = @partitionCount
              AND algorithm = @algorithm
            """);
        AddTopologyParameters(command, topology);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var encoding = reader.GetString(0);
        var payload = (byte[])reader[1];
        return JsonSerializer.Deserialize<MarketLedgerState>(
            PostgresPayloadCodec.Decode(encoding, payload),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("PostgreSQL 市场账本为空。 ");
    }

    public async ValueTask CommitAsync(
        MarketTopology topology,
        long expectedRevision,
        MarketLedgerState ledger,
        YuoSnapshotRecord? playerSnapshot,
        CancellationToken cancellationToken = default)
    {
        EnsureLease();
        ArgumentNullException.ThrowIfNull(ledger);
        if (ledger.Revision != checked(expectedRevision + 1))
        {
            throw new InvalidOperationException("新市场账本版本必须严格递增 1。 ");
        }

        var rawPayload = JsonSerializer.SerializeToUtf8Bytes(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payload = PostgresPayloadCodec.Encode(rawPayload, out var encoding, out _);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (playerSnapshot is not null)
            {
                await PostgresSnapshotStore.SaveAsync(connection, transaction, playerSnapshot, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO market_ledgers (
                    partition_id, partition_count, algorithm, revision, payload_encoding, payload, updated_at, writer_instance)
                VALUES (@partitionId, @partitionCount, @algorithm, @revision, @encoding, @payload, NOW(), @writer)
                ON CONFLICT (partition_id, partition_count, algorithm) DO UPDATE SET
                    revision = EXCLUDED.revision,
                    payload_encoding = EXCLUDED.payload_encoding,
                    payload = EXCLUDED.payload,
                    updated_at = NOW(),
                    writer_instance = EXCLUDED.writer_instance
                WHERE market_ledgers.revision = @expectedRevision
                RETURNING revision
                """,
                connection,
                transaction);
            AddTopologyParameters(command, topology);
            command.Parameters.AddWithValue("revision", ledger.Revision);
            command.Parameters.AddWithValue("expectedRevision", expectedRevision);
            command.Parameters.AddWithValue("encoding", encoding);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload);
            command.Parameters.AddWithValue("writer", _instanceId);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null || result is DBNull)
            {
                var actual = await ReadActualRevisionAsync(connection, transaction, topology, cancellationToken).ConfigureAwait(false);
                throw new MarketLedgerConcurrencyException(expectedRevision, actual);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask ReleaseWriterAsync(CancellationToken cancellationToken = default)
    {
        var connection = Interlocked.Exchange(ref _leaseConnection, null);
        if (connection is null) return;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            command.Parameters.AddWithValue("key", _leaseKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("PostgreSQL 市场写者租约已释放");
        }
    }

    public ValueTask DisposeAsync() => ReleaseWriterAsync(CancellationToken.None);

    private void EnsureLease()
    {
        if (_leaseConnection is null)
        {
            throw new InvalidOperationException("PostgreSQL 市场写者租约尚未取得。 ");
        }
    }

    private const long MarketAdvisoryLockSalt = unchecked((long)0x44414F5855414E4D); // DAOHUAM

    private static long CreateAdvisoryLockKey(MarketTopology topology)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes[..4], topology.PartitionCount);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], topology.PartitionId);
        return BinaryPrimitives.ReadInt64BigEndian(bytes) ^ MarketAdvisoryLockSalt;
    }

    private static void AddTopologyParameters(NpgsqlCommand command, MarketTopology topology)
    {
        command.Parameters.AddWithValue("partitionId", topology.PartitionId);
        command.Parameters.AddWithValue("partitionCount", topology.PartitionCount);
        command.Parameters.AddWithValue("algorithm", topology.Algorithm);
    }

    private static async ValueTask<long> ReadActualRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MarketTopology topology,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision
            FROM market_ledgers
            WHERE partition_id = @partitionId
              AND partition_count = @partitionCount
              AND algorithm = @algorithm
            """,
            connection,
            transaction);
        AddTopologyParameters(command, topology);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long revision ? revision : 0;
    }
}
