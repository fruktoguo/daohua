namespace Domain.Player;

/// <summary>
/// 玩家聚合状态的持久化拆分边界（Section）。
/// 各 Section 在 PostgreSQL 存储中按行隔离，各自具备单调版本号与变更脏标记，
/// 允许高频变更（如 ActionQueue、Progression）与低频变更（如 Settings、World）差量刷盘，
/// 避免整个聚合大快照的高频写放大与网络锁竞争。
/// </summary>
public enum PlayerSectionKind : byte
{
    /// <summary>境界等级、灵气修为、历史总修为与连续模拟进位</summary>
    Progression = 1,
    /// <summary>世界坐标、所在地图节点与探索随机数种子</summary>
    World = 2,
    /// <summary>战斗属性快照（生命、灵力、攻防）</summary>
    Combat = 3,
    /// <summary>活跃 Buff 增益/减益状态及其到期时间</summary>
    Buffs = 4,
    /// <summary>已装备物品实例与已领悟功法神通列表</summary>
    Loadout = 5,
    /// <summary>玩家个人偏好设置（语言、通知、循环模式、快捷自动使用槽位）</summary>
    Settings = 6,
    /// <summary>正在排队与执行中的行动队列（支持进度与重复次数）</summary>
    ActionQueue = 7
}

/// <summary>
/// 持久化载荷压缩编解码格式。
/// </summary>
public enum SectionCodec : byte
{
    /// <summary>未压缩的原始 JSON 字节流</summary>
    Raw = 0,
    /// <summary>Brotli 压缩字节流，用于缩减网络传输与落库存储体积</summary>
    Brotli = 1
}

/// <summary>
/// 玩家持久化头（Head）；负责乐观并发控制（CAS）与校验和一致性核对。
/// 仅记录元数据与聚合全局版本，不包含具体的运行时实体或分段载荷。
/// </summary>
public sealed record PlayerStateHead(
    PlayerId PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt)
{
    /// <summary>当前头结构模式版本</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// 单个 Section 的最新持久化载荷快照。
/// 包含版本号、压缩格式、原始字节数、压缩存储字节数及 SHA-256 校验和。
/// </summary>
public sealed record PlayerSectionSnapshot(
    PlayerId PlayerId,
    PlayerSectionKind Section,
    long SectionVersion,
    int SchemaVersion,
    SectionCodec Codec,
    ReadOnlyMemory<byte> Payload,
    int RawBytes,
    int StoredBytes,
    string Checksum,
    DateTimeOffset UpdatedAt)
{
    /// <summary>当前分段数据结构模式版本</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// 刷盘工作队列中的待提交项。
/// 相同 (PlayerId, Section) 会在聚合内合并为最新值，支持租约锁定与重试递增。
/// </summary>
public sealed record SectionFlushItem(
    PlayerId PlayerId,
    PlayerSectionKind Section,
    long StateVersion,
    PlayerSectionSnapshot Snapshot,
    int AttemptCount = 0,
    DateTimeOffset? NextAttemptAt = null,
    DateTimeOffset? LeaseUntil = null)
{
}

