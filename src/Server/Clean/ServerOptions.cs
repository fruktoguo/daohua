namespace Server.Clean;

/// <summary>
/// 服务端宿主统一运行时配置模型。
/// 所有字段均在应用启动阶段完成严格的取值范围与有效性校验，快速失败。
/// </summary>
/// <param name="ServiceName">进程服务名（日志与部署标识）。</param>
/// <param name="ConnectionString">PostgreSQL 连接串。</param>
/// <param name="SectionFlushBatchSize">section 落盘单批上限。</param>
/// <param name="SectionFlushInterval">脏 section 周期落盘间隔。</param>
/// <param name="SectionFlushLease">durable 写队列行租约时长；市场分片续租也复用该值。</param>
/// <param name="OutboxBatchSize">outbox 单批投递上限。</param>
/// <param name="OutboxPollInterval">outbox 轮询间隔。</param>
/// <param name="SessionLifetime">账号会话有效期。</param>
/// <param name="MaxWebSocketMessageBytes">单条 WebSocket 上行消息最大字节数。</param>
/// <param name="WebSocketOutboundCapacity">每会话下行有界通道容量（帧数）。</param>
/// <param name="MarketPartitionCount">市场分片总数。</param>
/// <param name="MarketPartitionId">本进程持有的市场分片序号。</param>
/// <param name="EnableSchemaBootstrap">启动时是否执行 PostgresSchema 建库自检。</param>
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
    /// <summary>ECS 运行时的固定 World/Fiber 分片数；仅 ECS 宿主消费此配置。</summary>
    public int EcsShardCount { get; init; } = 1;

    /// <summary>进程运行角色（API、Worker 或全部）</summary>
    public ServerProcessRole ProcessRole { get; init; } = ServerProcessRole.Api;
    /// <summary>历史过期数据单次清理批次大小</summary>
    public int RetentionBatchSize { get; init; } = 256;
    /// <summary>已投递的 Outbox 消息保留期限</summary>
    public TimeSpan DeliveredOutboxRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>会话凭据最长保留期限</summary>
    public TimeSpan SessionRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>审计日志最长保留期限</summary>
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(30);
    /// <summary>账号检测判定事件保留期，独立于需要长期归档的业务审计。</summary>
    public TimeSpan AccountRiskEventRetention { get; init; } = TimeSpan.FromDays(30);
    /// <summary>离线补算任务记录保留期限</summary>
    public TimeSpan ComputeJobRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>ASP.NET 数据保护密钥保留期限</summary>
    public TimeSpan DataProtectionKeyRetention { get; init; } = TimeSpan.FromDays(180);
    /// <summary>坊市结算认领凭证保留期限</summary>
    public TimeSpan SettlementClaimRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>是否启用主服务 → ComputeNode 冷挂机派发链</summary>
    public bool ComputeNodeEnabled { get; init; }
    /// <summary>内部计算令牌（X-Compute-Token，兼作 compute-link 握手共享凭证）；只从配置/环境变量读取，绝不写入日志</summary>
    public string? ComputeNodeToken { get; init; }
    /// <summary>
    /// 每节点独立凭证（ComputeNode:Tokens 节点名→令牌映射）：compute-link hello 拨入按节点名解析期望令牌，
    /// 命中映射时该节点只认映射令牌（共享令牌对其失效）；未命中映射的节点回退 <see cref="ComputeNodeToken"/>。
    /// 单节点令牌泄露只需轮换该节点映射项，不波及全节点组。
    /// </summary>
    public IReadOnlyDictionary<string, string> ComputeNodeTokens { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>单次派发领取的最大任务数</summary>
    public int ComputeBatchSize { get; init; } = 64;
    /// <summary>
    /// 单批次解码后字节预算：装配批次时按该上限截断，装不下的任务立即退回 pending 下轮再派。
    /// 上限取 12 MiB：Base64 编码放大 4/3 后约 16 MiB 帧体，仍低于 compute-link 单帧 20 MiB 上限并保留结果帧冗余。
    /// </summary>
    public long ComputeBatchMaxBytes { get; init; } = 8L * 1024 * 1024;
    /// <summary>节点出站帧队列写入超时：bounded 64 满时防止派发任务被挂死，超时按批次失败退回租约</summary>
    public TimeSpan ComputeOutboundWriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>派发轮询间隔</summary>
    public TimeSpan ComputePollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>compute_jobs 行租约时长</summary>
    public TimeSpan ComputeLease { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>单次批次 HTTP 请求超时</summary>
    public TimeSpan ComputeRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>任务最大领取次数；超过后进入 dead 终态保留诊断窗口</summary>
    public int ComputeMaxAttempts { get; init; } = 5;
    /// <summary>冷挂机扫描间隔</summary>
    public TimeSpan ColdCatchUpInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>冷挂机单次扫描玩家上限</summary>
    public int ColdCatchUpBatchSize { get; init; } = 128;
    /// <summary>离线宽限与最小补算步长：断线不足该时长或上次模拟时间距今不足该时长不入队</summary>
    public TimeSpan ColdCatchUpMinIdle { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>冷挂机筛选窗口：只处理最近活跃不超过该时长的玩家（对齐 48 小时离线收益封顶）</summary>
    public TimeSpan ColdCatchUpWindow { get; init; } = TimeSpan.FromHours(48);

    /// <summary>派发链是否具备可运行配置（启用 + 共享令牌或任一节点映射令牌齐备；节点组经 compute-link 动态拨入）</summary>
    public bool ComputeNodeConfigured =>
        ComputeNodeEnabled && (!string.IsNullOrWhiteSpace(ComputeNodeToken) || ComputeNodeTokens.Count > 0);

    /// <summary>
    /// 从 IConfiguration 绑定并校验全量服务端配置。
    /// 校验失败时抛出 InvalidOperationException 阻止服务启动。
    /// </summary>
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
        var ecsShardCount = configuration.GetValue("Ecs:ShardCount", 1);
        var bootstrap = configuration.GetValue("Storage:EnableSchemaBootstrap", true);
        var retentionBatch = configuration.GetValue("Storage:RetentionBatchSize", 256);
        var outboxRetentionDays = configuration.GetValue("Storage:DeliveredOutboxRetentionDays", 7);
        var sessionRetentionDays = configuration.GetValue("Storage:SessionRetentionDays", 7);
        var auditRetentionDays = configuration.GetValue("Storage:AuditRetentionDays", 30);
        var riskRetentionDays = configuration.GetValue("Storage:AccountRiskEventRetentionDays", 30);
        var computeRetentionDays = configuration.GetValue("Storage:ComputeJobRetentionDays", 7);
        var dataProtectionRetentionDays = configuration.GetValue("Storage:DataProtectionKeyRetentionDays", 180);
        var settlementClaimRetentionDays = configuration.GetValue("Storage:SettlementClaimRetentionDays", 30);
        var computeEnabled = configuration.GetValue("ComputeNode:Enabled", true);
        var computeToken = configuration["ComputeNode:Token"];
        var computeBatchSize = configuration.GetValue("ComputeNode:BatchSize", 64);
        var computeBatchMaxBytes = configuration.GetValue("ComputeNode:BatchMaxBytes", 8L * 1024 * 1024);
        var computeWriteTimeoutSeconds = configuration.GetValue("ComputeNode:OutboundWriteTimeoutSeconds", 10);
        var computePollMs = configuration.GetValue("ComputeNode:PollIntervalMilliseconds", 5_000);
        var computeLeaseSeconds = configuration.GetValue("ComputeNode:LeaseSeconds", 120);
        var computeTimeoutSeconds = configuration.GetValue("ComputeNode:RequestTimeoutSeconds", 30);
        var computeMaxAttempts = configuration.GetValue("ComputeNode:MaxAttempts", 5);
        var coldScanSeconds = configuration.GetValue("ComputeNode:ColdCatchUpIntervalSeconds", 60);
        var coldBatchSize = configuration.GetValue("ComputeNode:ColdCatchUpBatchSize", 128);
        var coldMinIdleMinutes = configuration.GetValue("ComputeNode:ColdCatchUpMinIdleMinutes", 5);
        var coldWindowHours = configuration.GetValue("ComputeNode:ColdCatchUpWindowHours", 48);

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
        if (ecsShardCount is < 1 or > 64) throw new InvalidOperationException("Ecs:ShardCount 必须在 1..64。");
        if (retentionBatch is < 1 or > 10_000) throw new InvalidOperationException("Storage:RetentionBatchSize 必须在 1..10000。");
        if (outboxRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:DeliveredOutboxRetentionDays 必须在 1..3650。");
        if (sessionRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:SessionRetentionDays 必须在 1..3650。");
        if (auditRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:AuditRetentionDays 必须在 1..3650。");
        if (riskRetentionDays is < 1 or > 365) throw new InvalidOperationException("Storage:AccountRiskEventRetentionDays 必须在 1..365。");
        if (computeRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:ComputeJobRetentionDays 必须在 1..3650。");
        if (dataProtectionRetentionDays is < 30 or > 3650) throw new InvalidOperationException("Storage:DataProtectionKeyRetentionDays 必须在 30..3650。");
        if (settlementClaimRetentionDays is < 1 or > 3650) throw new InvalidOperationException("Storage:SettlementClaimRetentionDays 必须在 1..3650。");
        if (computeBatchSize is < 1 or > 256) throw new InvalidOperationException("ComputeNode:BatchSize 必须在 1..256。");
        if (computeBatchMaxBytes is < 64 * 1024 or > 12L * 1024 * 1024)
            throw new InvalidOperationException("ComputeNode:BatchMaxBytes 必须在 64KiB..12MiB（帧体须低于链路 20MiB 上限）。");
        if (computeWriteTimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("ComputeNode:OutboundWriteTimeoutSeconds 必须在 1..120。");
        if (computePollMs is < 500 or > 300_000) throw new InvalidOperationException("ComputeNode:PollIntervalMilliseconds 必须在 500..300000。");
        if (computeLeaseSeconds is < 10 or > 3600) throw new InvalidOperationException("ComputeNode:LeaseSeconds 必须在 10..3600。");
        if (computeTimeoutSeconds is < 5 or > 600) throw new InvalidOperationException("ComputeNode:RequestTimeoutSeconds 必须在 5..600。");
        if (computeMaxAttempts is < 1 or > 100) throw new InvalidOperationException("ComputeNode:MaxAttempts 必须在 1..100。");
        if (coldScanSeconds is < 10 or > 86_400) throw new InvalidOperationException("ComputeNode:ColdCatchUpIntervalSeconds 必须在 10..86400。");
        if (coldBatchSize is < 1 or > 2_000) throw new InvalidOperationException("ComputeNode:ColdCatchUpBatchSize 必须在 1..2000。");
        if (coldMinIdleMinutes is < 1 or > 1440) throw new InvalidOperationException("ComputeNode:ColdCatchUpMinIdleMinutes 必须在 1..1440。");
        if (coldWindowHours is < 1 or > 720) throw new InvalidOperationException("ComputeNode:ColdCatchUpWindowHours 必须在 1..720。");
        if (computeToken is { Length: > 512 }) throw new InvalidOperationException("ComputeNode:Token 长度不能超过 512。");
        // 每节点独立凭证映射：ComputeNode:Tokens:<节点名> = 令牌（环境变量 ComputeNode__Tokens__<名> 同口径）。
        var computeNodeTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection("ComputeNode:Tokens").GetChildren())
        {
            var tokenNodeName = entry.Key.Trim();
            var tokenValue = entry.Value?.Trim();
            if (tokenNodeName.Length is 0 || tokenNodeName.Length > ComputeLinkContract.MaxNodeNameLength)
                throw new InvalidOperationException("ComputeNode:Tokens 节点名不能为空且不超过 128 字符。");
            if (string.IsNullOrWhiteSpace(tokenValue))
                throw new InvalidOperationException($"ComputeNode:Tokens:{tokenNodeName} 令牌不能为空。");
            if (tokenValue.Length > 512)
                throw new InvalidOperationException($"ComputeNode:Tokens:{tokenNodeName} 令牌长度不能超过 512。");
            computeNodeTokens[tokenNodeName] = tokenValue;
        }
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
            AccountRiskEventRetention = TimeSpan.FromDays(riskRetentionDays),
            ComputeJobRetention = TimeSpan.FromDays(computeRetentionDays),
            DataProtectionKeyRetention = TimeSpan.FromDays(dataProtectionRetentionDays),
            SettlementClaimRetention = TimeSpan.FromDays(settlementClaimRetentionDays),
            ComputeNodeEnabled = computeEnabled,
            ComputeNodeToken = string.IsNullOrWhiteSpace(computeToken) ? null : computeToken.Trim(),
            ComputeNodeTokens = computeNodeTokens,
            ComputeBatchSize = computeBatchSize,
            ComputeBatchMaxBytes = computeBatchMaxBytes,
            ComputeOutboundWriteTimeout = TimeSpan.FromSeconds(computeWriteTimeoutSeconds),
            ComputePollInterval = TimeSpan.FromMilliseconds(computePollMs),
            ComputeLease = TimeSpan.FromSeconds(computeLeaseSeconds),
            ComputeRequestTimeout = TimeSpan.FromSeconds(computeTimeoutSeconds),
            ComputeMaxAttempts = computeMaxAttempts,
            ColdCatchUpInterval = TimeSpan.FromSeconds(coldScanSeconds),
            ColdCatchUpBatchSize = coldBatchSize,
            ColdCatchUpMinIdle = TimeSpan.FromMinutes(coldMinIdleMinutes),
            ColdCatchUpWindow = TimeSpan.FromHours(coldWindowHours),
            EcsShardCount = ecsShardCount
        };
    }

}

/// <summary>
/// 进程运行角色：决定装配哪些宿主服务与仓储。
/// </summary>
public enum ServerProcessRole : byte
{
    /// <summary>在线请求角色：承载 HTTP API、WebSocket 与市场/玩家运行时。</summary>
    Api = 0,
    /// <summary>持久化角色：只跑 section 落盘、outbox、回执与留存清理等后台任务，不装配 GameDataCatalog 与冷挂机派发链。</summary>
    PersistenceWorker = 1
}

/// <summary>
/// 服务端内部 JSON 序列化口径（camelCase、忽略 null、严格数字、大小写不敏感读取），
/// 用于持久化载荷、compute.v1 契约等非协议边界；协议 DTO 走 ProtocolJson.Options。
/// </summary>
public static class ServerJson
{
    /// <summary>共享序列化配置；全局只读单例，避免多处各建实例导致口径漂移。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict
    };
}
