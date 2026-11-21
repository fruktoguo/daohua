using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Common;
using Domain.Combat;
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
    /// <summary>当前分段编解码模式版本（v45：progression 新增筑基期根基点数 genjiPoints 与解锁标记 genjiUnlocked，可缺省解码为 0/false；最低可读版本保持 v41）。
    /// 历次版本变动与存量处理台账见 docs/backend/db-version-changes.md。
    /// 升级约定：每次 bump 时同步递增 PostgresSchema.Version，在 docs/backend/db-version-changes.md
    /// 登记字段变动、缺省语义、最低可读版本及存量处理，并随版本编写 migrations/ 单步脚本。
    /// 本地开发由 reset-outdated-players.sh 优先执行已有迁移；缺失时须交互确认重置。运行时只解码当前版本。</summary>
    public const int CurrentSchemaVersion = 45;
    /// <summary>
    /// 仍可解码当前快照载荷的最老客户端 schema 版本——快照协议随 DTO 下发的「读者地板」。
    /// 客户端按 <c>minReaderSchemaVersion &lt;= 自身版本</c> 判定可读性，替代旧的全等门禁：
    /// 可缺省字段（additive）升级维持本值不变，旧客户端透明续用；
    /// 破坏性变更必须把本值推进到 <see cref="CurrentSchemaVersion"/> 并同步强制客户端更新。
    /// </summary>
    public const int MinReadableSchemaVersion = 41;
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

    /// <summary>
    /// 检测持久化分段的校验元数据是否与载荷一致并尝试自愈。
    /// 载荷可解码但 checksum/rawBytes 漂移时产出修正行（<paramref name="corrected"/> 非空）并返回 true；
    /// 完全一致返回 true 且 <paramref name="corrected"/> 为 null；载荷本体不可解码返回 false（不可自愈）。
    /// </summary>
    /// <param name="section">数据库读出的持久化分段行。</param>
    /// <param name="corrected">修正后的分段行：checksum 与 rawBytes 按实际载荷字节重算，其余字段原样保留。</param>
    public static bool TryRepairSection(StoredSection section, out StoredSection? corrected)
    {
        ArgumentNullException.ThrowIfNull(section);
        corrected = null;
        byte[] raw;
        try { raw = DecodeRaw(section.Codec, section.Payload); }
        catch { return false; }
        if (raw.Length == 0 || raw.Length > MaxRawBytes) return false;
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (string.Equals(checksum, section.Checksum, StringComparison.Ordinal) && raw.Length == section.RawBytes)
            return true;
        corrected = new StoredSection(section.PlayerId, section.SectionKind, section.StateVersion,
            section.SchemaVersion, section.Codec, section.Payload, raw.Length, checksum, section.UpdatedAt);
        return true;
    }

    /// <summary>
    /// 落库前深度校验：把存储载荷还原为原始字节后重算 SHA-256，校验 checksum 与 rawBytes
    /// 与真实载荷完全一致，防止「载荷更新而校验字段沿用旧值」的错配行进入数据库。
    /// </summary>
    public static void VerifyIntegrity(string codec, ReadOnlyMemory<byte> payload, int rawBytes, string checksum, string sectionKind)
    {
        var raw = DecodeRaw(codec, payload);
        if (raw.Length != rawBytes
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(raw)), checksum, StringComparison.Ordinal))
            throw new InvalidDataException($"section {sectionKind} 载荷与校验元数据不一致，拒绝落库。");
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
                DomainSectionKind.Settings => new SettingsDocument(state.Settings.Locale, state.Settings.NotificationsEnabled, state.Settings.LoopEnabled, state.Settings.AutoTravelEnabled, state.Settings.AutoUseSlots ?? [], state.Settings.RenameCooldownUntilUnixSeconds, state.Settings.QueuePaused, state.Settings.TutorialStep, state.Settings.TutorialRewardsStep, state.Presence, state.OfflineSinceAt, state.Settings.Avatar, state.Settings.UnlockedAvatars?.ToArray() ?? [PlayerSettingsState.DefaultFemaleAvatar, PlayerSettingsState.DefaultMaleAvatar], state.Settings.RespawnPolicy, state.Settings.RespawnTargetPlayerId, state.Settings.PublicLoadoutVisible),
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
            (PlayerSectionKinds.Settings, new SettingsDocument(state.Settings.Locale, state.Settings.NotificationsEnabled, state.Settings.LoopEnabled, state.Settings.AutoTravelEnabled, state.Settings.AutoUseSlots ?? [], state.Settings.RenameCooldownUntilUnixSeconds, state.Settings.QueuePaused, state.Settings.TutorialStep, state.Settings.TutorialRewardsStep, state.Presence, state.OfflineSinceAt, state.Settings.Avatar, state.Settings.UnlockedAvatars?.ToArray() ?? [PlayerSettingsState.DefaultFemaleAvatar, PlayerSettingsState.DefaultMaleAvatar], state.Settings.RespawnPolicy, state.Settings.RespawnTargetPlayerId, state.Settings.PublicLoadoutVisible)),
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
            settings.OfflineSinceAt,
            Math.Max(0, head.StateVersion),
            fingerprint,
            progression,
            world,
            combat,
            buffs,
            loadout.EquippedInstances,
            loadout.LearnedAbilities,
            new(settings.Locale, settings.NotificationsEnabled, settings.LoopEnabled, settings.AutoTravelEnabled, settings.AutoUseSlots, settings.RenameCooldownUntilUnixSeconds, settings.QueuePaused, settings.TutorialStep,
                // v14 载荷无奖励游标：按 tutorialStep 回填「必然已领过」的最高赠送步下限，
                // 防止升级后重学/推进重复领取阶段赠送（v15 起载荷自带游标，max 不生效）。
                Math.Max(settings.TutorialRewardsStep, ImplyTutorialRewardsStep(settings.TutorialStep)),
                ResolveAvatar(settings.Avatar, settings.UnlockedAvatars),
                ResolveUnlockedAvatars(settings.UnlockedAvatars),
                settings.RespawnPolicy,
                settings.RespawnTargetPlayerId,
                settings.PublicLoadoutVisible),
            actions,
            loadout.State,
            duel,
            loadout.EquippedItemIds);
    }

    /// <summary>
    /// v14 存档的奖励游标下限：tutorialStep 已进过某里程碑 ⇒ 该步赠送必然已入账；
    /// 无赠送的里程碑（3/4/5/6）归并到上一个有赠送的步。
    /// </summary>
    private static int ImplyTutorialRewardsStep(int tutorialStep) => tutorialStep switch
    {
        >= TutorialSteps.Done => TutorialSteps.Done,
        >= TutorialSteps.Forge => TutorialSteps.Forge,
        >= TutorialSteps.Alchemy => TutorialSteps.Alchemy,
        _ => 0
    };
    private static string[] ResolveUnlockedAvatars(IReadOnlyList<string>? raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            PlayerSettingsState.DefaultFemaleAvatar,
            PlayerSettingsState.DefaultMaleAvatar
        };
        if (raw is not null)
        {
            foreach (var a in raw)
                set.Add(PlayerAggregate.NormalizeAvatar(a));
        }
        return set.ToArray();
    }

    private static string ResolveAvatar(string? avatar, IReadOnlyList<string>? unlocked)
    {
        var norm = string.IsNullOrWhiteSpace(avatar) ? PlayerSettingsState.DefaultFemaleAvatar : PlayerAggregate.NormalizeAvatar(avatar);
        var set = ResolveUnlockedAvatars(unlocked);
        return set.Contains(norm) ? norm : PlayerSettingsState.DefaultFemaleAvatar;
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

    /// <summary>解码 loadout 分段的装配文档。
    /// 供身家估值等轻量读取场景只取装备与功法经验，不必校验整包聚合的完整性。</summary>
    /// <param name="section">loadout 分段的持久化行。</param>
    /// <returns>装配文档（装备槽位 + 功法经验等）。</returns>
    public static PlayerLoadoutDocument DecodeLoadout(StoredSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.SectionKind != PlayerSectionKinds.Loadout)
            throw new InvalidDataException($"section {section.SectionKind} 不是 loadout 分段。");
        return Decode<LoadoutDocument>(section).State
            ?? throw new InvalidDataException($"section {section.SectionKind} 缺少有效装配状态。");
    }

    /// <summary>解码 progression 分段的状态文档。
    /// 供全服排行榜计算 Top 50 修士含灵根乘区的本层修为上限。</summary>
    /// <param name="section">progression 分段的持久化行。</param>
    /// <returns>境界与修为状态文档。</returns>
    public static Domain.Player.PlayerProgressionState DecodeProgression(StoredSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.SectionKind != PlayerSectionKinds.Progression)
            throw new InvalidDataException($"section {section.SectionKind} 不是 progression 分段。");
        return Decode<Domain.Player.PlayerProgressionState>(section);
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
        bool AutoTravelEnabled,
        IReadOnlyList<AutoUseSlotState> AutoUseSlots,
        double RenameCooldownUntilUnixSeconds,
        bool QueuePaused,
        /// <summary>新手引导里程碑；v13 及更早载荷缺省解码为 0。</summary>
        int TutorialStep,
        /// <summary>新手引导奖励游标；v14 及更早载荷缺省解码为 0。</summary>
        int TutorialRewardsStep,
        PresenceState Presence,
        /// <summary>离线锚点；v31 前载荷缺省解码为 null。</summary>
        DateTimeOffset? OfflineSinceAt,
        /// <summary>本命形象外观标识；缺省解码为 player。</summary>
        string Avatar = "player",
        /// <summary>已解锁形象标识集合；v33 前载荷缺省解码为 null。</summary>
        IReadOnlyList<string>? UnlockedAvatars = null,
        /// <summary>组队讨伐复活时机策略；v38 及更早载荷缺省解码为直接复活。</summary>
        CombatRespawnPolicy RespawnPolicy = CombatRespawnPolicy.Immediate,
        /// <summary>指定队友存活策略的观察对象玩家 ID；缺省解码为 null（未指定）。</summary>
        Guid? RespawnTargetPlayerId = null,
        /// <summary>非同队修士是否可见角色名片配装详情；缺省解码为 true。</summary>
        bool PublicLoadoutVisible = true);
}
