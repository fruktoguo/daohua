using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Persistence;

/// <summary>
/// 持久化边界的共享 JSON 序列化口径（camelCase、忽略 null、严格数字、大小写不敏感读取）。
/// 与 Server.Clean.ServerJson.Options 逐字段一致：主服与 ComputeNode 共用同一份
/// SectionCodec 落盘字节格式，任何一侧口径漂移都会破坏 section checksum 与 golden 一致性。
/// </summary>
public static class PersistenceJson
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

/// <summary>玩家状态 section 的稳定命名空间。新增 section 只需增加新键，不修改既有载荷。</summary>
public static class PlayerSectionKinds
{
    /// <summary>境界与修为成长分段。</summary>
    public const string Progression = "progression";
    /// <summary>世界位置与地图状态分段。</summary>
    public const string World = "world";
    /// <summary>战斗属性与出战状态分段。</summary>
    public const string Combat = "combat";
    /// <summary>生效中的增益/减益列表分段。</summary>
    public const string Buffs = "buffs";
    /// <summary>装备实例、已学技能与配装方案分段。</summary>
    public const string Loadout = "loadout";
    /// <summary>玩家设置与在线状态分段。</summary>
    public const string Settings = "settings";
    /// <summary>行动队列分段。</summary>
    public const string ActionQueue = "action_queue";
    /// <summary>斗法分段：遭遇计数、冷却、暂存区与战斗收益记录。</summary>
    public const string Duel = "duel";

    /// <summary>全部合法 section 键集合；完整性校验与落库遍历都以它为准。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Progression, World, Combat, Buffs, Loadout, Settings, ActionQueue, Duel
    };

    /// <summary>判断给定键是否是受支持的 section。</summary>
    public static bool IsValid(string value) => All.Contains(value);
}

/// <summary>单个 section 的可持久化载荷。Payload 已完成编码和压缩。</summary>
public sealed record SectionWrite(
    Guid PlayerId,
    string SectionKind,
    long StateVersion,
    int SchemaVersion,
    string Codec,
    ReadOnlyMemory<byte> Payload,
    int RawBytes,
    string Checksum,
    DateTimeOffset UpdatedAt,
    string GameDataFingerprint = "",
    DateTimeOffset? LastSimulatedAt = null)
{
    /// <summary>压缩后的实际存储字节数。</summary>
    public int StoredBytes => Payload.Length;

    /// <summary>入库前的边界校验：section 键合法、版本为正、载荷与校验和在大小/格式上限内。</summary>
    public void Validate()
    {
        if (PlayerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(PlayerId));
        if (!PlayerSectionKinds.IsValid(SectionKind)) throw new ArgumentException($"未知 section: {SectionKind}。", nameof(SectionKind));
        if (StateVersion <= 0) throw new ArgumentOutOfRangeException(nameof(StateVersion));
        if (SchemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        if (Codec is not (SectionCodec.RawJson or SectionCodec.BrotliJson))
            throw new ArgumentException("Codec 不受支持。", nameof(Codec));
        if (Payload.IsEmpty || Payload.Length > 4 * 1024 * 1024 || RawBytes <= 0 || RawBytes > 4 * 1024 * 1024)
            throw new ArgumentException("section 载荷大小无效。", nameof(Payload));
        if (string.IsNullOrWhiteSpace(Checksum) || Checksum.Length != 64 || Checksum.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Checksum 必须是 64 位十六进制字符串。", nameof(Checksum));
        if (GameDataFingerprint is null || GameDataFingerprint.Length > 128)
            throw new ArgumentException("游戏数据指纹超过长度上限。", nameof(GameDataFingerprint));
    }
}

/// <summary>持久化头元数据，用于跨实例与版本 CAS</summary>
public sealed record PlayerStateHead(
    Guid PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt)
{
    /// <summary>当前头结构模式版本；v3：新增斗法分段（duel）。</summary>
    public const int CurrentSchemaVersion = SectionCodec.CurrentSchemaVersion;
}

/// <summary>玩家不可变基础身份信息</summary>
public sealed record PlayerIdentity(
    Guid PlayerId,
    Guid AccountId,
    string DisplayName,
    DateTimeOffset CreatedAt);

/// <summary>数据库中存储的单个分段载荷行映射</summary>
public sealed record StoredSection(
    Guid PlayerId,
    string SectionKind,
    long StateVersion,
    int SchemaVersion,
    string Codec,
    byte[] Payload,
    int RawBytes,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>聚合读取时从数据库加载出的完整数据绑定包</summary>
public sealed record PlayerStateBundle(
    PlayerIdentity Identity,
    PlayerStateHead Head,
    IReadOnlyDictionary<string, StoredSection> Sections,
    IReadOnlyList<StoredAction> Actions);

/// <summary>数据库持久化的行动队列项</summary>
public sealed record StoredAction(
    Guid PlayerId,
    int Slot,
    string ActionId,
    byte[] Payload,
    long ActionVersion,
    string Status,
    DateTimeOffset? NextDueAt,
    string IdempotencyKey);
