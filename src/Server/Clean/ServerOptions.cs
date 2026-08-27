namespace Server.Clean;

/// <summary>新宿主的唯一运行配置；所有值在启动时完成边界校验。</summary>
public sealed record ServerOptions(
    string ServiceName,
    string ConnectionString,
    int SectionFlushBatchSize,
    TimeSpan SectionFlushInterval,
    TimeSpan SectionFlushLease,
    int OutboxBatchSize,
    TimeSpan OutboxPollInterval,
    TimeSpan SessionLifetime,
    int MaxWebSocketMessageBytes,
    int WebSocketOutboundCapacity,
    int MarketPartitionCount,
    int MarketPartitionId,
    bool EnableSchemaBootstrap)
{
    public ServerProcessRole ProcessRole { get; init; } = ServerProcessRole.Api;
    public int RetentionBatchSize { get; init; } = 256;
    public TimeSpan DeliveredOutboxRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan SessionRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ComputeJobRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan DataProtectionKeyRetention { get; init; } = TimeSpan.FromDays(180);

    public static ServerOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("daohua")
            ?? configuration["Storage:ConnectionString"]
            ?? throw new InvalidOperationException("必须配置 ConnectionStrings:daohua 或 Storage:ConnectionString。");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PostgreSQL 连接串不能为空。");

        var batchSize = configuration.GetValue("Storage:SectionFlushBatchSize", 128);
        var flushIntervalMs = configuration.GetValue("Storage:SectionFlushIntervalMilliseconds", 1_000);
        var leaseSeconds = configuration.GetValue("Storage:SectionFlushLeaseSeconds", 30);
        var outboxBatch = configuration.GetValue("Storage:OutboxBatchSize", 64);
        var outboxPollMs = configuration.GetValue("Storage:OutboxPollIntervalMilliseconds", 1_000);
        var sessionDays = configuration.GetValue("Auth:SessionLifetimeDays", 30);
        var maxWs = configuration.GetValue("Server:MaxWebSocketMessageBytes", 64 * 1024);
        var outbound = configuration.GetValue("Server:WebSocketOutboundCapacity", 128);
        var partitionCount = configuration.GetValue("Market:PartitionCount", 1);
        var partitionId = configuration.GetValue("Market:PartitionId", 0);
        var bootstrap = configuration.GetValue("Storage:EnableSchemaBootstrap", true);
        var retentionBatch = configuration.GetValue("Storage:RetentionBatchSize", 256);
        var outboxRetentionDays = configuration.GetValue("Storage:DeliveredOutboxRetentionDays", 7);
        var sessionRetentionDays = configuration.GetValue("Storage:SessionRetentionDays", 7);
        var auditRetentionDays = configuration.GetValue("Storage:AuditRetentionDays", 30);
        var computeRetentionDays = configuration.GetValue("Storage:ComputeJobRetentionDays", 7);
        var dataProtectionRetentionDays = configuration.GetValue("Storage:DataProtectionKeyRetentionDays", 180);

        if (batchSize is < 1 or > 10_000) throw new InvalidOperationException("Storage:SectionFlushBatchSize 必须在 1..10000。");
        if (flushIntervalMs is < 100 or > 60_000) throw new InvalidOperationException("Storage:SectionFlushIntervalMilliseconds 必须在 100..60000。");
        if (leaseSeconds is < 5 or > 600) throw new InvalidOperationException("Storage:SectionFlushLeaseSeconds 必须在 5..600。");
        if (outboxBatch is < 1 or > 1_000) throw new InvalidOperationException("Storage:OutboxBatchSize 必须在 1..1000。");
        if (outboxPollMs is < 100 or > 60_000) throw new InvalidOperationException("Storage:OutboxPollIntervalMilliseconds 必须在 100..60000。");
        if (sessionDays is < 1 or > 3650) throw new InvalidOperationException("Auth:SessionLifetimeDays 必须在 1..3650。");
        if (maxWs is < 1024 or > 1024 * 1024) throw new InvalidOperationException("Server:MaxWebSocketMessageBytes 必须在 1KiB..1MiB。");
        if (outbound is < 8 or > 10_000) throw new InvalidOperationException("Server:WebSocketOutboundCapacity 必须在 8..10000。");
        if (partitionCount is < 1 or > 4096 || partitionId < 0 || partitionId >= partitionCount)
            throw new InvalidOperationException("Market 分片配置无效。");
        if (retentionBatch is < 1 or > 10_000) throw new InvalidOperationException("Storage:RetentionBatchSize 必须在 1..10000。");
        if (outboxRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:DeliveredOutboxRetentionDays 必须在 1..3650。");
        if (sessionRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:SessionRetentionDays 必须在 1..3650。");
        if (auditRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:AuditRetentionDays 必须在 1..3650。");
        if (computeRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:ComputeJobRetentionDays 必须在 1..3650。");
        if (dataProtectionRetentionDays is < 30 or > 3650) throw new InvalidOperationException("Storage:DataProtectionKeyRetentionDays 必须在 30..3650。");

        return new ServerOptions(
            configuration["Service:Name"] ?? "daohua",
            connectionString,
            batchSize,
            TimeSpan.FromMilliseconds(flushIntervalMs),
            TimeSpan.FromSeconds(leaseSeconds),
            outboxBatch,
            TimeSpan.FromMilliseconds(outboxPollMs),
            TimeSpan.FromDays(sessionDays),
            maxWs,
            outbound,
            partitionCount,
            partitionId,
            bootstrap)
        {
            RetentionBatchSize = retentionBatch,
            DeliveredOutboxRetention = TimeSpan.FromDays(outboxRetentionDays),
            SessionRetention = TimeSpan.FromDays(sessionRetentionDays),
            AuditRetention = TimeSpan.FromDays(auditRetentionDays),
            ComputeJobRetention = TimeSpan.FromDays(computeRetentionDays),
            DataProtectionKeyRetention = TimeSpan.FromDays(dataProtectionRetentionDays)
        };
    }
}

public enum ServerProcessRole : byte
{
    Api = 0,
    PersistenceWorker = 1
}

public static class ServerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict
    };
}
