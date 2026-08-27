namespace Domain.Player;

/// <summary>玩家快照的稳定分区边界。每个分区共享表中只保留最新版本。</summary>
public enum PlayerSectionKind : byte
{
    Progression = 1,
    World = 2,
    Combat = 3,
    Buffs = 4,
    Loadout = 5,
    Settings = 6,
    ActionQueue = 7
}

public enum SectionCodec : byte
{
    Raw = 0,
    Brotli = 1
}

/// <summary>玩家持久化头；不包含运行时对象或载荷。</summary>
public sealed record PlayerStateHead(
    PlayerId PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>单个 section 的最新持久化载荷元数据。</summary>
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
    public const int CurrentSchemaVersion = 1;
}

/// <summary>刷盘队列中待提交的最新 section。相同玩家/section 只保留最新值。</summary>
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
