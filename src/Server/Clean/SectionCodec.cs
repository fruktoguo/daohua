using Domain.Player;
using Domain.Common;
using DomainState = Domain.Player.PlayerAggregateState;
using DomainSectionKind = Domain.Player.PlayerSectionKind;

namespace Server.Clean;

/// <summary>
/// Section 编解码边界。载荷格式是 Server 与 Domain 之间唯一需要约定的存档边界；
/// 运行时对象绝不直接交给数据库序列化。
/// </summary>
public static class SectionCodec
{
    public const int CurrentSchemaVersion = 1;
    public const string RawJson = "json";
    public const string BrotliJson = "br-json";
    private const int CompressionThreshold = 512;
    private const int MaxRawBytes = 4 * 1024 * 1024;

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
        var raw = JsonSerializer.SerializeToUtf8Bytes(payload, ServerJson.Options);
        if (raw.Length == 0 || raw.Length > MaxRawBytes)
            throw new InvalidDataException("section 原始载荷超过大小上限。");
        var checksum = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
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
        var checksum = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checksum), Encoding.ASCII.GetBytes(section.Checksum)))
            throw new InvalidDataException($"section {section.SectionKind} 校验和不匹配。");
        return JsonSerializer.Deserialize<T>(raw, ServerJson.Options)
            ?? throw new InvalidDataException($"section {section.SectionKind} 载荷为空。");
    }

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
        var state = aggregate.ExportState();
        var dirty = aggregate.ConsumeDirtySections();
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
                DomainSectionKind.Loadout => new LoadoutDocument(state.EquippedInstances, state.EquippedItemIds ?? [], state.LearnedAbilities),
                DomainSectionKind.Settings => new SettingsDocument(state.Settings.Locale, state.Settings.NotificationsEnabled, state.Settings.LoopEnabled, state.Settings.AutoUseSlots ?? [], state.Presence),
                DomainSectionKind.ActionQueue => state.Actions,
                _ => throw new InvalidDataException($"未知玩家 section: {section}")
            };
            result.Add(Encode(aggregate.PlayerId.Value, key, Math.Max(1, stateVersionOverride ?? state.StateVersion), value, updatedAt) with
            {
                GameDataFingerprint = state.GameDataFingerprint,
                LastSimulatedAt = state.LastSimulatedAt
            });
        }
        return result;
    }

    public static DomainState DecodeAggregate(PlayerStateBundle bundle, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var head = bundle.Head;
        if (head.StateVersion <= 0 || head.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("玩家持久化头版本无效。");
        if (string.IsNullOrWhiteSpace(head.GameDataFingerprint))
            throw new InvalidDataException("玩家持久化头缺少游戏数据指纹。");
        var fingerprint = head.GameDataFingerprint;
        if (!string.IsNullOrWhiteSpace(expectedFingerprint) && !string.Equals(expectedFingerprint, head.GameDataFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("游戏数据指纹不一致。");

        var required = PlayerSectionKinds.All;
        if (bundle.Sections.Count != required.Count || required.Any(key => !bundle.Sections.ContainsKey(key)) || bundle.Sections.Keys.Any(key => !required.Contains(key)))
            throw new InvalidDataException("玩家持久化 section 不完整。");
        if (bundle.Sections.Values.Any(section => section.PlayerId != bundle.Identity.PlayerId || section.StateVersion > head.StateVersion))
            throw new InvalidDataException("玩家 section 版本或归属无效。");
        var computedHeadChecksum = ComputeHeadChecksum(bundle.Sections.Values);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computedHeadChecksum), Encoding.ASCII.GetBytes(head.Checksum)))
            throw new InvalidDataException("玩家持久化头校验和不匹配。");

        var progression = Decode<Domain.Player.PlayerProgressionState>(bundle.Sections[PlayerSectionKinds.Progression]);
        var world = Decode<Domain.Player.PlayerWorldStateDocument>(bundle.Sections[PlayerSectionKinds.World]);
        var combat = Decode<Domain.Player.PlayerCombatStateDocument>(bundle.Sections[PlayerSectionKinds.Combat]);
        var buffs = Decode<List<ActiveBuff>>(bundle.Sections[PlayerSectionKinds.Buffs]);
        var loadout = Decode<LoadoutDocument>(bundle.Sections[PlayerSectionKinds.Loadout]);
        var settings = Decode<SettingsDocument>(bundle.Sections[PlayerSectionKinds.Settings]);
        var actions = Decode<List<QueuedActionState>>(bundle.Sections[PlayerSectionKinds.ActionQueue]);
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
            new(settings.Locale, settings.NotificationsEnabled, settings.LoopEnabled, settings.AutoUseSlots),
            actions,
            loadout.EquippedItemIds);
    }

    public static string ToStorageKind(DomainSectionKind section) => section switch
    {
        DomainSectionKind.Progression => PlayerSectionKinds.Progression,
        DomainSectionKind.World => PlayerSectionKinds.World,
        DomainSectionKind.Combat => PlayerSectionKinds.Combat,
        DomainSectionKind.Buffs => PlayerSectionKinds.Buffs,
        DomainSectionKind.Loadout => PlayerSectionKinds.Loadout,
        DomainSectionKind.Settings => PlayerSectionKinds.Settings,
        DomainSectionKind.ActionQueue => "action_queue",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };

    public static DomainSectionKind FromStorageKind(string section) => section switch
    {
        PlayerSectionKinds.Progression => DomainSectionKind.Progression,
        PlayerSectionKinds.World => DomainSectionKind.World,
        PlayerSectionKinds.Combat => DomainSectionKind.Combat,
        PlayerSectionKinds.Buffs => DomainSectionKind.Buffs,
        PlayerSectionKinds.Loadout => DomainSectionKind.Loadout,
        PlayerSectionKinds.Settings => DomainSectionKind.Settings,
        "action_queue" => DomainSectionKind.ActionQueue,
        _ => throw new InvalidDataException($"未知 section: {section}")
    };

    public static string ComputeHeadChecksum(IEnumerable<StoredSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var canonical = string.Join('|', sections
            .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
            .Select(section => $"{section.SectionKind}:{section.StateVersion}:{section.Checksum}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record LoadoutDocument(
        IReadOnlyList<Domain.Common.InstanceId> EquippedInstances,
        IReadOnlyList<Domain.Common.ItemId> EquippedItemIds,
        IReadOnlyList<Domain.Common.AbilityId> LearnedAbilities);
    private sealed record SettingsDocument(
        string Locale,
        bool NotificationsEnabled,
        bool LoopEnabled,
        IReadOnlyList<AutoUseSlotState> AutoUseSlots,
        PresenceState Presence);
}
