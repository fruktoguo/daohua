using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Common;
using Domain.Player;
using DomainState = Domain.Player.PlayerAggregateState;
using DomainSectionKind = Domain.Player.PlayerSectionKind;

namespace Domain.Persistence;

/// <summary>
/// 角色分段（Section）编解码边界适配器。
/// 职责：
/// 1. 将内存对象序列化为稳定 JSON 字节流，并计算 SHA-256 小写校验和；
/// 2. 当数据体积超过阈值（512B）且能有效降低体积时，自适应启用 Brotli 压缩；
/// 3. 是 Server 宿主、ComputeNode 计算节点与 Domain 领域层及数据库存储之间唯一需要约定的存档落盘边界。
/// </summary>
public static class SectionCodec
{
    /// <summary>当前分段编解码模式版本；v3：新增斗法分段（duel），旧存档按本地开发清零规则直接拒绝。</summary>
    public const int CurrentSchemaVersion = 3;
    /// <summary>未压缩 JSON 编码标识</summary>
    public const string RawJson = "json";
    /// <summary>Brotli 压缩 JSON 编码标识</summary>
    public const string BrotliJson = "br-json";
    /// <summary>自适应启用压缩的体积阈值（512 字节）</summary>
    private const int CompressionThreshold = 512;
    /// <summary>单分段最大允许原始字节数（4MB）</summary>
    private const int MaxRawBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 对指定分段载荷执行 JSON 序列化、校验和计算及自适应 Brotli 压缩。
    /// </summary>
    public static SectionWrite Encode(
        Guid playerId,
        string sectionKind,
        long stateVersion,
        object payload,
        DateTimeOffset updatedAt,
        int schemaVersion = CurrentSchemaVersion,
        bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var raw = JsonSerializer.SerializeToUtf8Bytes(payload, PersistenceJson.Options);
        if (raw.Length == 0 || raw.Length > MaxRawBytes)
            throw new InvalidDataException("section 原始载荷超过大小上限。");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        var encoded = raw;
        var codec = RawJson;
        if (compress && raw.Length >= CompressionThreshold)
        {
            using var output = new MemoryStream(raw.Length);
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(raw);
            }

            if (output.Length < raw.Length)
            {
                encoded = output.ToArray();
                codec = BrotliJson;
            }
        }

        var result = new SectionWrite(
            playerId,
            sectionKind,
            stateVersion,
            schemaVersion,
            codec,
            encoded,
            raw.Length,
            checksum,
            updatedAt);
        result.Validate();
        return result;
    }

    /// <summary>
    /// 解码单个 section：校验 schema 版本、解压载荷、核对原始长度与 SHA-256 校验和后反序列化。
    /// 任一项不匹配均抛 <see cref="InvalidDataException"/>，拒绝把损坏存档当作有效状态加载。
    /// </summary>
    public static T Decode<T>(StoredSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"section {section.SectionKind} schema 版本不受支持。");
        if (section.Payload.Length == 0 || section.Payload.Length > MaxRawBytes)
            throw new InvalidDataException($"section {section.SectionKind} 存储载荷大小无效。");
        var raw = DecodeRaw(section.Codec, section.Payload);
        if (raw.Length > MaxRawBytes)
            throw new InvalidDataException($"section {section.SectionKind} 原始载荷超过大小上限。");
        if (raw.Length != section.RawBytes)
            throw new InvalidDataException($"section {section.SectionKind} 原始长度不匹配。");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        Span<byte> computedBytes = stackalloc byte[64];
        Span<byte> expectedBytes = stackalloc byte[64];
        if (Encoding.ASCII.GetBytes(checksum, computedBytes) != 64
            || Encoding.ASCII.GetBytes(section.Checksum, expectedBytes) != 64
            || !CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes))
        {
            throw new InvalidDataException($"section {section.SectionKind} 校验和不匹配。");
        }
        return JsonSerializer.Deserialize<T>(raw, PersistenceJson.Options)
            ?? throw new InvalidDataException($"section {section.SectionKind} 载荷为空。");
    }

    /// <summary>按 codec 标识把存储载荷还原为原始 JSON 字节；不认识的 codec 直接拒绝。</summary>
    public static byte[] DecodeRaw(string codec, ReadOnlyMemory<byte> payload)
    {
        return codec switch
        {
            RawJson => payload.ToArray(),
            BrotliJson => Decompress(payload),
            _ => throw new InvalidDataException($"不支持的 section codec: {codec}。")
        };
    }

    private static byte[] Decompress(ReadOnlyMemory<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(MaxRawBytes, Math.Max(payload.Length * 2, 1024)));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxRawBytes) throw new InvalidDataException("section 解压后超过大小上限。");
                output.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return output.ToArray();
    }

    /// <summary>将领域聚合按稳定 section 边界编码；资产钱包不包含在这里。</summary>
    public static IReadOnlyList<SectionWrite> EncodeAggregate(PlayerAggregate aggregate, DateTimeOffset updatedAt, long? stateVersionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return EncodeAggregateDirty(aggregate.ExportState(), aggregate.ConsumeDirtySections(), updatedAt, stateVersionOverride);
    }

    /// <summary>
    /// 将不可变聚合状态按显式脏分段集合编码；ECS 运行时的脏标记挂在身份组件上，
    /// 由宿主推进结果显式传入，与聚合路径共用同一段编码面保证字节一致。
    /// </summary>
    public static IReadOnlyList<SectionWrite> EncodeAggregate(PlayerAggregateState state,
        IReadOnlyCollection<DomainSectionKind> dirtySections, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dirtySections);
        return EncodeAggregateDirty(state, dirtySections, updatedAt, null);
    }

    private static List<SectionWrite> EncodeAggregateDirty(PlayerAggregateState state,
        IReadOnlyCollection<DomainSectionKind> dirty, DateTimeOffset updatedAt, long? stateVersionOverride)
    {
        var result = new List<SectionWrite>(dirty.Count);
        foreach (var section in dirty)
        {
            var key = ToStorageKind(section);
            object value = section switch
            {
                DomainSectionKind.Progression => state.Progression,
                DomainSectionKind.World => state.World,
                DomainSectionKind.Combat => state.Combat,
                DomainSectionKind.Buffs => state.Buffs,
                DomainSectionKind.Loadout => new LoadoutDocument(state.EquippedInstances, state.EquippedItemIds ?? [], state.LearnedAbilities, state.Loadout),
                DomainSectionKind.Settings => new SettingsDocument(state.Settings.Locale, state.Settings.NotificationsEnabled, state.Settings.LoopEnabled, state.Settings.AutoUseSlots ?? [], state.Settings.RenameCooldownUntilUnixSeconds, state.Presence),
                DomainSectionKind.ActionQueue => state.Actions,
                DomainSectionKind.Duel => state.Duel,
                _ => throw new InvalidDataException($"未知玩家 section: {section}")
            };
            result.Add(Encode(state.PlayerId.Value, key, Math.Max(1, stateVersionOverride ?? state.StateVersion), value, updatedAt) with
            {
                GameDataFingerprint = state.GameDataFingerprint,
                LastSimulatedAt = state.LastSimulatedAt
            });
        }
        return result;
    }

    /// <summary>
    /// 从不可变状态完整编码全部玩家分段。ECS 运行时没有 OOP 聚合的 dirty 标记，
    /// 因此持久化边界必须显式请求全量编码，而不是伪造一次领域命令来污染 dirty 集合。
    /// </summary>
    public static IReadOnlyList<SectionWrite> EncodeAggregateFull(PlayerAggregateState state, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        var values = new (string Kind, object Value)[]
        {
            (PlayerSectionKinds.Progression, state.Progression),
            (PlayerSectionKinds.World, state.World),
            (PlayerSectionKinds.Combat, state.Combat),
            (PlayerSectionKinds.Buffs, state.Buffs),
            (PlayerSectionKinds.Loadout, new LoadoutDocument(state.EquippedInstances, state.EquippedItemIds ?? [], state.LearnedAbilities, state.Loadout)),
            (PlayerSectionKinds.Settings, new SettingsDocument(state.Settings.Locale, state.Settings.NotificationsEnabled, state.Settings.LoopEnabled, state.Settings.AutoUseSlots ?? [], state.Settings.RenameCooldownUntilUnixSeconds, state.Presence)),
            (PlayerSectionKinds.ActionQueue, state.Actions),
            (PlayerSectionKinds.Duel, state.Duel)
        };
        return values.Select(value => Encode(state.PlayerId.Value, value.Kind, Math.Max(1, state.StateVersion), value.Value, updatedAt) with
        {
            GameDataFingerprint = state.GameDataFingerprint,
            LastSimulatedAt = state.LastSimulatedAt
        }).ToArray();
    }

    /// <summary>
    /// 解码当前 schema 的玩家状态。配表指纹仅记录该状态上次运行时使用的内容版本，
    /// 不参与存档结构兼容判断；结构不兼容统一由 SchemaVersion 明确拒绝。
    /// </summary>
    public static DomainState DecodeAggregate(PlayerStateBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var head = bundle.Head;
        if (head.StateVersion <= 0 || head.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("玩家持久化头版本无效。");
        if (string.IsNullOrWhiteSpace(head.GameDataFingerprint))
            throw new InvalidDataException("玩家持久化头缺少游戏数据指纹。");
        var fingerprint = head.GameDataFingerprint;

        var required = PlayerSectionKinds.All;
        if (bundle.Sections.Count != required.Count || required.Any(key => !bundle.Sections.ContainsKey(key)) || bundle.Sections.Keys.Any(key => !required.Contains(key)))
            throw new InvalidDataException("玩家持久化 section 不完整。");
        if (bundle.Sections.Values.Any(section => section.PlayerId != bundle.Identity.PlayerId || section.StateVersion > head.StateVersion))
            throw new InvalidDataException("玩家 section 版本或归属无效。");
        var computedHeadChecksum = ComputeHeadChecksum(bundle.Sections.Values);
        Span<byte> computedHeadBytes = stackalloc byte[64];
        Span<byte> expectedHeadBytes = stackalloc byte[64];
        if (Encoding.ASCII.GetBytes(computedHeadChecksum, computedHeadBytes) != 64
            || Encoding.ASCII.GetBytes(head.Checksum, expectedHeadBytes) != 64
            || !CryptographicOperations.FixedTimeEquals(computedHeadBytes, expectedHeadBytes))
        {
            throw new InvalidDataException("玩家持久化头校验和不匹配。");
        }

        var progression = Decode<Domain.Player.PlayerProgressionState>(bundle.Sections[PlayerSectionKinds.Progression]);
        var world = Decode<Domain.Player.PlayerWorldStateDocument>(bundle.Sections[PlayerSectionKinds.World]);
        var combat = Decode<Domain.Player.PlayerCombatStateDocument>(bundle.Sections[PlayerSectionKinds.Combat]);
        var buffs = Decode<List<ActiveBuff>>(bundle.Sections[PlayerSectionKinds.Buffs]);
        var loadout = Decode<LoadoutDocument>(bundle.Sections[PlayerSectionKinds.Loadout]);
        var settings = Decode<SettingsDocument>(bundle.Sections[PlayerSectionKinds.Settings]);
        var actions = Decode<List<QueuedActionState>>(bundle.Sections[PlayerSectionKinds.ActionQueue]);
        var duel = Decode<Domain.Player.PlayerDuelStateDocument>(bundle.Sections[PlayerSectionKinds.Duel]);
        return new DomainState(
            new PlayerId(bundle.Identity.PlayerId),
            new AccountId(bundle.Identity.AccountId),
            bundle.Identity.DisplayName,
            bundle.Identity.CreatedAt,
            head.LastSimulatedAt < bundle.Identity.CreatedAt ? bundle.Identity.CreatedAt : head.LastSimulatedAt,
            settings.Presence,
            Math.Max(0, head.StateVersion),
            fingerprint,
            progression,
            world,
            combat,
            buffs,
            loadout.EquippedInstances,
            loadout.LearnedAbilities,
            new(settings.Locale, settings.NotificationsEnabled, settings.LoopEnabled, settings.AutoUseSlots, settings.RenameCooldownUntilUnixSeconds),
            actions,
            loadout.State,
            duel,
            loadout.EquippedItemIds);
    }

    /// <summary>领域分段枚举到持久化键的单向映射。</summary>
    public static string ToStorageKind(DomainSectionKind section) => section switch
    {
        DomainSectionKind.Progression => PlayerSectionKinds.Progression,
        DomainSectionKind.World => PlayerSectionKinds.World,
        DomainSectionKind.Combat => PlayerSectionKinds.Combat,
        DomainSectionKind.Buffs => PlayerSectionKinds.Buffs,
        DomainSectionKind.Loadout => PlayerSectionKinds.Loadout,
        DomainSectionKind.Settings => PlayerSectionKinds.Settings,
        DomainSectionKind.ActionQueue => "action_queue",
        DomainSectionKind.Duel => PlayerSectionKinds.Duel,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };

    /// <summary>持久化键反解为领域分段枚举；未知键抛 <see cref="InvalidDataException"/>。</summary>
    public static DomainSectionKind FromStorageKind(string section) => section switch
    {
        PlayerSectionKinds.Progression => DomainSectionKind.Progression,
        PlayerSectionKinds.World => DomainSectionKind.World,
        PlayerSectionKinds.Combat => DomainSectionKind.Combat,
        PlayerSectionKinds.Buffs => DomainSectionKind.Buffs,
        PlayerSectionKinds.Loadout => DomainSectionKind.Loadout,
        PlayerSectionKinds.Settings => DomainSectionKind.Settings,
        "action_queue" => DomainSectionKind.ActionQueue,
        PlayerSectionKinds.Duel => DomainSectionKind.Duel,
        _ => throw new InvalidDataException($"未知 section: {section}")
    };

    /// <summary>
    /// 计算提交头校验和：按 section 键排序拼接 kind:version:checksum 后取 SHA-256。
    /// 提交头借此绑定整组 section 内容，任何单段篡改都会破坏头校验。
    /// </summary>
    public static string ComputeHeadChecksum(IEnumerable<StoredSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var list = sections as IList<StoredSection> ?? sections.ToList();
        var sorted = new StoredSection[list.Count];
        for (var i = 0; i < list.Count; i++) sorted[i] = list[i];
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.SectionKind, b.SectionKind));
        var sb = new StringBuilder(sorted.Length * 96);
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0) sb.Append('|');
            var s = sorted[i];
            sb.Append(s.SectionKind).Append(':').Append(s.StateVersion).Append(':').Append(s.Checksum);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>loadout 分段的落盘文档形状：把装备实例、已学技能与配装状态合并为一个 JSON 载荷。</summary>
    private sealed record LoadoutDocument(
        IReadOnlyList<Domain.Common.InstanceId> EquippedInstances,
        IReadOnlyList<Domain.Common.ItemId> EquippedItemIds,
        IReadOnlyList<Domain.Common.AbilityId> LearnedAbilities,
        PlayerLoadoutDocument State);
    /// <summary>settings 分段的落盘文档形状：设置项与在线状态合并持久化。</summary>
    private sealed record SettingsDocument(
        string Locale,
        bool NotificationsEnabled,
        bool LoopEnabled,
        IReadOnlyList<AutoUseSlotState> AutoUseSlots,
        double RenameCooldownUntilUnixSeconds,
        PresenceState Presence);
}
