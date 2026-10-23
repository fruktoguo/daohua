using System.Runtime.CompilerServices;
using Domain.Player;
using Domain.GameData;
using Domain.Combat;
using Domain.World;
using Domain.Simulation;
using Protocol;
namespace Server.Clean;

/// <summary>
/// 协议快照映射器：将内部领域运行时状态投影为公开稳定的 Protocol DTO。
/// 严禁把 ECS Entity、Fiber、数据库模型或未授权内部异常直接暴露给网络客户端。
/// </summary>
public static class ProtocolSnapshotMapper
{
    /// <summary>增量编码的分段遍历顺序：与全量快照排序后的 wire 顺序一致（按存储段名排序），保证 changed 序列语义不变。</summary>
    private static readonly PlayerSectionKind[] s_deltaSectionOrder =
    [
        PlayerSectionKind.ActionQueue, // action_queue
        PlayerSectionKind.Buffs,       // buffs
        PlayerSectionKind.Combat,      // combat
        PlayerSectionKind.Duel,        // duel
        PlayerSectionKind.Loadout,     // loadout
        PlayerSectionKind.Progression, // progression
        PlayerSectionKind.Settings,    // settings
        PlayerSectionKind.World        // world
    ];

    // combat 属性面板的跨段输入（境界/地图/装备/技法/功法/Buff/设置节点建筑开关）已由
    // PlayerRuntimeSnapshot.WireAttributesChanged 在锁内按解析缓存实例引用精确判定，
    // 不再按输入分段并集放大候选，见 IsDeltaCandidate。
    /// <summary>
    /// 对单个分段载荷执行规范化 JSON 序列化并生成 PlayerSectionDto。
    /// 规范化字节一次编码后既做 checksum 输入也直接内嵌为 data：
    /// JsonElement.WriteTo 按原始文本透传，信封中 data 成员的字节区间与 checksum
    /// 哈希输入逐位一致，客户端可对线上原始字节复算 SHA-256，无需再经 Base64 往返。
    /// </summary>
    public static PlayerSectionDto EncodeSection(Guid playerId, string kind, long stateVersion, object value, DateTimeOffset updatedAt)
    {
        var encoded = SectionCodec.Encode(playerId, kind, Math.Max(1, stateVersion), value, updatedAt, compress: false);
        using var document = JsonDocument.Parse(encoded.Payload);
        return new PlayerSectionDto(
            kind,
            encoded.StateVersion,
            encoded.SchemaVersion,
            document.RootElement.Clone(),
            encoded.Checksum,
            encoded.UpdatedAt);
    }

    /// <summary>
    /// 将玩家运行时快照（PlayerRuntimeSnapshot）转换为公开的全量快照 DTO。
    /// 自动结合激活的 GameDataCatalog 透传地图与节点动态生态。
    /// </summary>
    public static PlayerSnapshotDto ToSnapshot(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog gameData, UnitInspectDto? inspectedUnit = null, Action? onAttributesProjected = null)
    {
        var state = runtime.State;
        var sections = new List<PlayerSectionDto>(8);
        foreach (var (kind, value) in EnumerateSections(state, gameData, inspectedUnit, onAttributesProjected))
        {
            // WebSocket v1 的浏览器载荷为内嵌规范化 JSON；Brotli 只用于 PostgreSQL/compute 链路的
            // 字节通道，避免客户端依赖不一致的 Brotli WASM/浏览器实现。
            sections.Add(EncodeSection(runtime.PlayerId, kind, runtime.StateVersion, value, runtime.LastSimulatedAt));
        }

        sections.Sort(static (a, b) => string.CompareOrdinal(a.Section, b.Section));
        var sb = new StringBuilder(sections.Count * 96);
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) sb.Append('|');
            var s = sections[i];
            sb.Append(s.Section).Append(':').Append(s.SectionVersion).Append(':').Append(s.Checksum);
        }
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));

        var head = new PlayerHeadDto(
            runtime.PlayerId.ToString("D"),
            runtime.StateVersion,
            Domain.Persistence.PlayerStateHead.CurrentSchemaVersion,
            runtime.LastSimulatedAt,
            gameDataFingerprint,
            checksum,
            DateTimeOffset.UtcNow);

        return new PlayerSnapshotDto(
            runtime.PlayerId.ToString("D"),
            runtime.DisplayName,
            runtime.LastSimulatedAt,
            runtime.StateVersion,
            head,
            sections,
            MapAssets(runtime));
    }

    /// <summary>运行时钱包余额投影为协议资产数组；全量快照与增量共用同一映射。</summary>
    private static AssetBalanceDto[] MapAssets(PlayerRuntimeSnapshot runtime)
    {
        if (runtime.Assets.Count == 0) return [];
        var assets = new AssetBalanceDto[runtime.Assets.Count];
        for (var i = 0; i < runtime.Assets.Count; i++)
        {
            var asset = runtime.Assets[i];
            assets[i] = new AssetBalanceDto(
                asset.AssetId,
                string.IsNullOrEmpty(asset.Variant) ? null : asset.Variant,
                asset.Quantity,
                asset.Version);
        }
        return assets;
    }

    /// <summary>
    /// 构造增量快照 DTO：touchedSections 为触发本次快照的操作在锁内新增的脏标记分段差集，
    /// 仅用于窄化 JSON/SHA256/Base64 编码范围；最终是否下发仍由 checksum 与基线裁决，
    /// 语义与全量编码后 DiffSections 完全一致。null 表示未知触及集，回退为全段编码。
    /// inspectedUnit 为会话级待交付观察目标：命中下标的敌怪/斗法镜像/盟友单位才投影属性面板；
    /// onAttributesProjected 在属性面板实际写入载荷时回调，供调用方解除一次性交付武装。
    /// </summary>
    public static PlayerSnapshotDeltaDto BuildDelta(
        PlayerRuntimeSnapshot runtime,
        string gameDataFingerprint,
        GameDataCatalog gameData,
        IReadOnlySet<PlayerSectionKind>? touchedSections,
        IReadOnlyDictionary<string, string>? previousSectionChecksums,
        IReadOnlyList<AssetBalanceDto>? previousAssets,
        out IReadOnlyDictionary<string, string> nextSectionChecksums,
        out IReadOnlyList<AssetBalanceDto> nextAssets,
        UnitInspectDto? inspectedUnit = null,
        Action? onAttributesProjected = null)
    {
        var state = runtime.State;
        var next = new Dictionary<string, string>(8, StringComparer.Ordinal);
        List<PlayerSectionDto>? changed = null;
        foreach (var section in s_deltaSectionOrder)
        {
            var name = SectionCodec.ToStorageKind(section);
            string? previousChecksum = null;
            var hasBaseline = previousSectionChecksums is not null
                && previousSectionChecksums.TryGetValue(name, out previousChecksum);
            if (hasBaseline && !IsDeltaCandidate(section, state, touchedSections, runtime.WireAttributesChanged))
            {
                // 未触及分段沿用基线校验和：不编码、不下发（标脏不变量保证其内容未变）。
                next[name] = previousChecksum!;
                continue;
            }
            var dto = EncodeSection(runtime.PlayerId, name, runtime.StateVersion, SectionPayload(state, section, gameData, inspectedUnit, onAttributesProjected), runtime.LastSimulatedAt);
            next[name] = dto.Checksum;
            // checksum 最终裁决：无论是否被触及候选覆盖，编码后校验和变化必须纳入增量。
            if (!hasBaseline || !string.Equals(previousChecksum, dto.Checksum, StringComparison.Ordinal))
                (changed ??= []).Add(dto);
        }
        nextSectionChecksums = next;
        var assets = MapAssets(runtime);
        nextAssets = assets;
        var changedAssets = DiffAssets(previousAssets, assets);
        return new PlayerSnapshotDeltaDto(
            runtime.PlayerId.ToString("D"),
            runtime.StateVersion,
            runtime.LastSimulatedAt,
            changed ?? [],
            changedAssets.Count == 0 ? null : changedAssets,
            runtime.DisplayName);
    }

    /// <summary>
    /// 判断分段是否为本次增量编码候选：显式触及其本身、无法静态排除的时钟/跨段派生投影
    /// 也算候选。touchedSections 为 null 时全部候选（未知触及集等价旧全量编码路径）。
    /// </summary>
    private static bool IsDeltaCandidate(PlayerSectionKind section, PlayerAggregateState state,
        IReadOnlySet<PlayerSectionKind>? touchedSections, bool? wireAttributesChanged)
    {
        if (touchedSections is null || touchedSections.Contains(section)) return true;
        return section switch
        {
            // 道龄 AgeSeconds 随每次模拟推进无标脏递增，每次推送都必须重新评估。
            PlayerSectionKind.Progression => true,
            // nodeBuildings 工期/材料进度按墙钟实时推算，存在在建条目时世界段内容每拍可变。
            PlayerSectionKind.World => state.World.NodeBuildings is { Count: > 0 },
            // combat 属性面板跨段派生：锁内解析实例引用变化即面板输入有变（null=未知按已变），
            // 消耗品冷却随模拟时钟倒计时同样逐拍改变分段内容。
            PlayerSectionKind.Combat => wireAttributesChanged != false || HasCombatConsumableCooldownSlot(state),
            _ => false
        };
    }

    /// <summary>
    /// 是否存在带冷却时间的战斗消耗品槽：冷却倒计时随模拟时钟逐拍变化投影，
    /// 且「本拍刚到期」一帧（正值→0）同样改变分段内容，故以 CooldownUntil &gt; 0 判定
    /// （冷却槽随自动使用配置存在；槽位清除/变更本身会标脏 Settings 令 combat 重获候选）。
    /// </summary>
    private static bool HasCombatConsumableCooldownSlot(PlayerAggregateState state)
    {
        foreach (var slot in state.Settings.AutoUseSlots ?? [])
        {
            if (slot.ActionType == Domain.GameData.AutoUseBar.Combat && slot.SlotIndex < Domain.GameData.AutoUseBar.DefaultSlotCount
                && slot.CooldownUntilUnixSeconds > 0)
                return true;
        }
        return false;
    }

    /// <summary>构造增量快照 DTO（仅携带受影响的变更分段与资产变更）；未知触及集，等价全量编码后裁决。</summary>
    public static PlayerSnapshotDeltaDto ToDelta(
        PlayerRuntimeSnapshot runtime,
        string gameDataFingerprint,
        GameDataCatalog gameData,
        IReadOnlyDictionary<string, string>? previousSectionChecksums = null,
        IReadOnlyList<AssetBalanceDto>? previousAssets = null,
        UnitInspectDto? inspectedUnit = null,
        Action? onAttributesProjected = null)
        => BuildDelta(runtime, gameDataFingerprint, gameData, null, previousSectionChecksums, previousAssets, out _, out _, inspectedUnit, onAttributesProjected);

    /// <summary>对比上次推送基线，返回校验和发生变化的分段；无基线时返回全部当前分段。</summary>
    public static IReadOnlyList<PlayerSectionDto> DiffSections(IReadOnlyDictionary<string, string>? previousChecksums, IReadOnlyList<PlayerSectionDto> current)
    {
        if (previousChecksums is null || previousChecksums.Count == 0) return current;
        List<PlayerSectionDto>? changed = null;
        for (var i = 0; i < current.Count; i++)
        {
            var section = current[i];
            if (previousChecksums.TryGetValue(section.Section, out var prevChecksum) && string.Equals(prevChecksum, section.Checksum, StringComparison.Ordinal))
                continue;
            (changed ??= new List<PlayerSectionDto>(current.Count)).Add(section);
        }
        return changed ?? [];
    }

    /// <summary>对比上次推送基线，返回余额发生变化的品种当前余额；无基线时返回当前全量。</summary>
    public static IReadOnlyList<AssetBalanceDto> DiffAssets(IReadOnlyList<AssetBalanceDto>? previous, IReadOnlyList<AssetBalanceDto> current)
    {
        if (previous is null || previous.Count == 0) return current;
        var baseline = new Dictionary<(string AssetId, string? Variant), AssetBalanceDto>(previous.Count);
        foreach (var asset in previous) baseline[(asset.AssetId, asset.Variant)] = asset;
        var currentKeys = new HashSet<(string AssetId, string? Variant)>();
        List<AssetBalanceDto>? changed = null;
        foreach (var asset in current)
        {
            var key = (asset.AssetId, asset.Variant);
            currentKeys.Add(key);
            if (baseline.TryGetValue(key, out var prior) && prior.Quantity == asset.Quantity) continue;
            (changed ??= []).Add(asset);
        }
        // 余额归零后，领域钱包会移除该品种；显式发送零余额才能让浏览器删除旧库存项。
        foreach (var (key, prior) in baseline)
        {
            if (currentKeys.Contains(key)) continue;
            (changed ??= []).Add(prior with { Quantity = 0 });
        }
        return changed ?? [];
    }

    private static IEnumerable<(string Kind, object Value)> EnumerateSections(PlayerAggregateState state, GameDataCatalog? gameData, UnitInspectDto? inspectedUnit, Action? onAttributesProjected = null)
    {
        yield return (PlayerSectionKinds.Progression, SectionPayload(state, PlayerSectionKind.Progression, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.World, SectionPayload(state, PlayerSectionKind.World, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.Combat, SectionPayload(state, PlayerSectionKind.Combat, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.Buffs, SectionPayload(state, PlayerSectionKind.Buffs, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.Loadout, SectionPayload(state, PlayerSectionKind.Loadout, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.Settings, SectionPayload(state, PlayerSectionKind.Settings, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.ActionQueue, SectionPayload(state, PlayerSectionKind.ActionQueue, gameData, inspectedUnit, onAttributesProjected));
        yield return (PlayerSectionKinds.Duel, SectionPayload(state, PlayerSectionKind.Duel, gameData, inspectedUnit, onAttributesProjected));
    }

    /// <summary>单段协议载荷投影：全量快照逐段遍历与增量候选编码共用同一构造，保证两边编码内容逐位一致。</summary>
    private static object SectionPayload(PlayerAggregateState state, PlayerSectionKind section, GameDataCatalog? gameData, UnitInspectDto? inspectedUnit, Action? onAttributesProjected = null) => section switch
    {
        PlayerSectionKind.Progression => new
        {
            state.Progression.RealmLevel, state.Progression.Experience,
            state.Progression.Foundation, state.Progression.CombatExperience, state.Progression.LifetimeExperience,
            state.Progression.CompletedActions, state.Progression.SimulationCarrySeconds,
            state.Progression.NormalKills, state.Progression.EliteKills, state.Progression.BossKills,
            state.Progression.TotalDefeats, state.Progression.CultivationSeconds, state.Progression.AgeSeconds,
            traitIds = state.Progression.TraitIds ?? (IReadOnlyList<string>)[],
            unlockedTraits = state.Progression.UnlockedTraits ?? (IReadOnlyList<Domain.Combat.TraitProgressState>)[],
            defeatedMonsterIds = state.Progression.DefeatedMonsterIds ?? (IReadOnlyList<string>)[],
            // 开天门与五行灵根：叩天门满修后的必经流程，缺这段投影会让客户端永远看不到抽取结果。
            heavenGateRolled = state.Progression.HeavenGateRolled,
            heavenGateConfirmed = state.Progression.HeavenGateConfirmed,
            heavenGateEntered = state.Progression.HeavenGateEntered,
            heavenGateRerollCount = state.Progression.HeavenGateRerollCount,
            // 灵根破镜乘区与乘区后权威修为瓶颈（总纲 §7.7.5.1）：前端显示与本地曲线预判同源。
            linggenMultiplier = Domain.Player.HeavenGateRules.ResolveCultivationMultiplier(
                state.Progression.HeavenGateConfirmed ?? Domain.Player.LinggenValues.Empty,
                state.Progression.HeavenGateEntered),
            expToNextLinggen = Domain.Player.HeavenGateRules.ResolveExpCapWithLinggen(
                state.Progression.RealmLevel,
                state.Progression.HeavenGateConfirmed ?? Domain.Player.LinggenValues.Empty,
                state.Progression.HeavenGateEntered,
                gameData?.RealmProgression),
            lifespanYears = gameData?.RealmProgression is { } realm && gameData.BaseAttributes is { } baseAttributes
                ? realm.LifespanYearsFor(baseAttributes.TierAt(state.Progression.RealmLevel))
                : 0
        },
        PlayerSectionKind.World => ToWorldSection(state.World, gameData),
        PlayerSectionKind.Combat => ToCombatSection(state, gameData ?? throw new InvalidOperationException("协议映射必须提供激活配表。")),
        // Buff 通道唯一形状：ActiveBuff 的平铺权威字段 + 通用属性比率字典（statRatios），
        // 前端只做展示渲染，所有收益结算均在服务端领域层完成。
        PlayerSectionKind.Buffs => state.Buffs.Select(buff => new
        {
            buff.BuffId, buff.Name, buff.Stacks,
            // durable 注入的 +∞ 永驻 Buff 投影为有限哨兵：JSON 无法表达非有限数，
            // 前端按 ≥1e15 阈值识别常驻展示，后端剔除逻辑不依赖该投影值。
            expiresAtUnixSeconds = double.IsFinite(buff.ExpiresAtUnixSeconds) ? buff.ExpiresAtUnixSeconds : double.MaxValue,
            buff.SourceItemId,
            buff.HpRegen, buff.MpRegen, buff.AttackSpeedPoints, buff.DamageAbsorptionPoints, buff.StatRatios,
            buff.HpRegenPostRatio, buff.MpRegenPostRatio
        }).ToArray(),
        PlayerSectionKind.Loadout => ToLoadoutSection(state, gameData),
        PlayerSectionKind.Settings => new { state.Settings, state.Presence },
        // 玩家威胁增幅来自面板解析（装备 flatStats.threatBonusPoints）；无配表/无远征时退化为 0。
        PlayerSectionKind.ActionQueue => ToActionQueueSection(state.Actions, gameData, inspectedUnit, onAttributesProjected,
            gameData is not null && state.Actions.Any(action => action.Expedition is not null)
                ? PlayerAttributeResolver.Resolve(state, gameData).ThreatBonusPoints : 0,
            state.Combat.Buffs, state.World.EffectiveNodeBuildingEffects.MonsterWeakenRatio,
            state),
        // 当前账本/归档/离线/每日四类已消费通道。
        PlayerSectionKind.Duel => new
        {
            state.Duel.EncounterCharges,
            state.Duel.CooldownUntilUnixSeconds,
            state.Duel.ActiveDuelId,
            state.Duel.StagedItems,
            state.Duel.CurrentReport,
            state.Duel.Reports,
            state.Duel.OfflineReport,
            state.Duel.DailyReport
        },
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };

    private static object ToLoadoutSection(PlayerAggregateState state, GameDataCatalog? gameData)
    {
        // 六维等级为配表派生值（RootAttributeCurve 查 RealmProgressionCatalog 点成本表），
        // 不再随持久化记录序列化，协议显式投影保持既有 wire 键名不变。
        var core = state.Loadout.CoreAttributes;
        var levels = core.Levels(gameData?.RealmProgression);
        return new
        {
            coreAttributes = new
            {
                core.StaminaExp, core.MeridiansExp, core.AttackExp,
                core.BoneExp, core.AgilityExp, core.ConsciousnessExp,
                staminaLevel = levels.Stamina, meridiansLevel = levels.Meridians, attackLevel = levels.Attack,
                boneLevel = levels.Bone, agilityLevel = levels.Agility, consciousnessLevel = levels.Consciousness,
                totalLevel = levels.Total
            },
            state.Loadout.AbilitySlots,
            state.Loadout.Equipment,
            learnedAbilityIds = state.LearnedAbilities.Select(id => id.Value).ToArray(),
            abilityExperiences = state.Loadout.AbilityExperiences ?? new Dictionary<string, double>(),
            techniqueExperiences = state.Loadout.TechniqueExperiences ?? new Dictionary<string, double>(),
            abilityTriggers = state.Loadout.AbilityTriggers ?? new Dictionary<string, JsonElement[]>(),
            consumableTriggers = state.Loadout.ConsumableTriggers ?? new Dictionary<string, JsonElement[]>(),
        };
    }

    /// <summary>
    /// 统一战斗属性投影：玩家、妖兽与斗法镜像共用同一字段集与语义。
    /// 全部百分比属性按点数下发（100 点 = 100%，总纲第七篇 §7.7.5），前端只按对应曲线渲染不得二次换算。
    /// </summary>
    /// <param name="extraThreatBonusPoints">折入的威胁增幅点数（战内 Buff 合计），与有效威胁判定同乘区。</param>
    public static object ToAttributesDto(PlayerAttributes a, double extraThreatBonusPoints = 0) => new
    {
        a.MaxHitpoints, a.MaxManapoints,
        physicalAttack = a.PhysicalAttack, a.SpellAttack,
        a.Armor, a.MagicResistance,
        a.Accuracy, a.Evasion,
        critical = a.Critical, criticalResistance = a.CriticalAvoidance,
        criticalDamagePoints = a.CriticalDamagePoints,
        breakPower = a.BreakPower, parryResolve = a.Parry,
        hpRegenPerFiveSeconds = a.HpRegen, mpRegenPerFiveSeconds = a.MpRegen,
        attackIntervalMilliseconds = a.AttackIntervalSeconds * 1000
            * PercentValue.Multiplier(a.AttackSpeedPoints, PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(a.ActionSpeedPoints, PercentCurveMode.NonLinear),
        a.PhysicalDamageBonusPoints, a.SpellDamageBonusPoints,
        a.MetalDamageBonusPoints, a.WoodDamageBonusPoints, a.WaterDamageBonusPoints,
        a.FireDamageBonusPoints, a.EarthDamageBonusPoints,
        a.TotalDamageBonusPoints, a.BasicAttackBonusPoints, a.SkillBonusPoints,
        a.PhysicalDamageReductionPoints, a.SpellDamageReductionPoints,
        a.MetalDamageReductionPoints, a.WoodDamageReductionPoints, a.WaterDamageReductionPoints,
        a.FireDamageReductionPoints, a.EarthDamageReductionPoints,
        a.DamageAbsorptionPoints,
        a.FixedDamageBonus, a.FixedDamageReduction,
        a.FixedPhysicalDamageBonus, a.FixedPhysicalDamageReduction,
        a.FixedSpellDamageBonus, a.FixedSpellDamageReduction,
        a.AttackSpeedPoints, a.ActionSpeedPoints, a.CooldownReductionPoints,
        threatBonusPoints = a.ThreatBonusPoints + extraThreatBonusPoints,
        hpDrainPoints = a.Effects.HpDrainPoints, mpDrainPoints = a.Effects.MpDrainPoints,
        damageReflectPoints = a.Effects.DamageReflectPoints, healingBonusPoints = a.Effects.HealingBonusPoints
    };

    /// <summary>战斗单位静态属性（怪物/斗法镜像）到统一属性投影的重载；
    /// <paramref name="extraThreatBonusPoints"/> 折入战内 Buff 威胁点，使详情增幅与有效威胁同口径。</summary>
    public static object ToAttributesDto(CombatantStats s, double extraThreatBonusPoints = 0) => new
    {
        s.MaxHitpoints, s.MaxManapoints,
        physicalAttack = s.AttackPower, s.SpellAttack,
        armor = s.Defense, s.MagicResistance,
        s.Accuracy, s.Evasion,
        critical = s.Critical, criticalResistance = s.CriticalResistance,
        criticalDamagePoints = s.CriticalDamagePoints,
        s.BreakPower, s.ParryResolve,
        hpRegenPerFiveSeconds = s.HpRegen, mpRegenPerFiveSeconds = s.MpRegen,
        attackIntervalMilliseconds = s.AttackIntervalSeconds * 1000
            * PercentValue.Multiplier(s.AttackSpeedPoints, PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(s.ActionSpeedPoints, PercentCurveMode.NonLinear),
        s.PhysicalDamageBonusPoints, s.SpellDamageBonusPoints,
        s.MetalDamageBonusPoints, s.WoodDamageBonusPoints, s.WaterDamageBonusPoints,
        s.FireDamageBonusPoints, s.EarthDamageBonusPoints,
        s.TotalDamageBonusPoints, s.BasicAttackBonusPoints, s.SkillBonusPoints,
        s.PhysicalDamageReductionPoints, s.SpellDamageReductionPoints,
        s.MetalDamageReductionPoints, s.WoodDamageReductionPoints, s.WaterDamageReductionPoints,
        s.FireDamageReductionPoints, s.EarthDamageReductionPoints,
        s.DamageAbsorptionPoints,
        s.FixedDamageBonus, s.FixedDamageReduction,
        s.FixedPhysicalDamageBonus, s.FixedPhysicalDamageReduction,
        s.FixedSpellDamageBonus, s.FixedSpellDamageReduction,
        s.AttackSpeedPoints, s.ActionSpeedPoints, s.CooldownReductionPoints,
        threatBonusPoints = s.ThreatBonusPoints + extraThreatBonusPoints,
        hpDrainPoints = s.Effects.HpDrainPoints, mpDrainPoints = s.Effects.MpDrainPoints,
        damageReflectPoints = s.Effects.DamageReflectPoints, healingBonusPoints = s.Effects.HealingBonusPoints
    };

    private static object ToCombatSection(PlayerAggregateState state, GameDataCatalog catalog)
    {
        var a = PlayerAttributeResolver.Resolve(state, catalog);
        var nowUnix = state.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;
        // 消耗品冷却按战斗域 6 槽定长下发：前 3 位药品栏（potion），后 3 位通用栏（general）；
        // 剩余毫秒以快照模拟时刻为基准，供前端冷却遮罩倒计时。
        var consumableCooldowns = new double[AutoUseSlotState.CombatSlotCount];
        foreach (var slot in state.Settings.AutoUseSlots ?? [])
        {
            if (slot.ActionType == Domain.GameData.AutoUseBar.Combat && slot.SlotIndex < Domain.GameData.AutoUseBar.DefaultSlotCount)
            {
                var index = slot.Group == AutoUseSlotState.SlotGroupPotion
                    ? slot.SlotIndex
                    : slot.SlotIndex + Domain.GameData.AutoUseBar.DefaultSlotCount;
                consumableCooldowns[index] = Math.Max(0, (slot.CooldownUntilUnixSeconds - nowUnix) * 1000);
            }
        }
        // 吟唱总时长 = 配表吟唱毫秒 ÷(1+行动速度点数/100)，仅在吟唱中下发；前端行动条不得回读配表推算。
        double? castTotalMilliseconds = null;
        if (state.Combat.Skills.CastingAbilityId is { } castingId
            && catalog.Abilities.TryGetValue(castingId, out var castingAbility)
            && castingAbility.TryGetProperty("castMilliseconds", out var castProperty)
            && castProperty.TryGetDouble(out var castMilliseconds))
            castTotalMilliseconds = castMilliseconds
                * PercentValue.Multiplier(a.ActionSpeedPoints, PercentCurveMode.NonLinear);
        return new
        {
            resources = new
            {
                hitpoints = Math.Min(state.Combat.Hitpoints, a.MaxHitpoints),
                manapoints = Math.Min(state.Combat.Manapoints, a.MaxManapoints),
                maxHitpoints = a.MaxHitpoints,
                maxManapoints = a.MaxManapoints
            },
            skills = new
            {
                cooldowns = state.Combat.Skills.Cooldowns.ToDictionary(
                    cooldown => cooldown.AbilityId,
                    cooldown => cooldown.RemainingSeconds * 1000,
                    StringComparer.Ordinal),
                castingAbilityId = state.Combat.Skills.CastingAbilityId,
                castRemainingMilliseconds = state.Combat.Skills.CastRemainingSeconds * 1000,
                castTotalMilliseconds
            },
            consumableCooldownMilliseconds = consumableCooldowns,
            // 威胁增幅折入战内 Buff 点数：战斗相位取波中实时快照，脱战取跨波携带态（与有效威胁同乘区）。
            attributes = ToAttributesDto(a,
                state.Actions.Count > 0 && state.Actions[0].Expedition is
                    { Phase: CombatExpeditionPhase.Combat, PlayerState.Buffs: { } liveBuffs }
                    ? CombatantState.SumBuffThreatPoints(liveBuffs)
                    : state.Combat.Buffs.SumThreatPoints())
        };
    }

    /// <summary>
    /// 把行动队列分段投影为协议匿名对象数组：在队列状态之上补充配表解析出的
    /// 名称、类型、时长与执行种类；travel 行动的时长与类型以 TravelState 为准。
    /// </summary>
    /// <param name="actions">聚合当前的行动队列。</param>
    /// <param name="gameData">激活配表；为 null 时仅输出队列原始字段（名称等降级为 null）。</param>
    /// <param name="inspectedUnit">会话级单位详情观察目标；命中下标的敌怪/斗法镜像/盟友单位才投影属性面板。</param>
    /// <param name="onAttributesProjected">属性面板实际写入本次载荷时的出站记账回调；供会话解除一次性交付武装。</param>
    /// <param name="playerThreatBonusPoints">本机面板威胁增幅点数：有效威胁投影口径与目标判定同式（总纲 §7.8.1）。</param>
    /// <param name="playerCarriedBuffs">本机战内 Buff 跨波携带态：非战斗相位下用于 Buff 图标续显。</param>
    /// <param name="monsterWeakenRatio">本机当前节点建筑削弱比（封魔阵类）：敌怪展示面板与实战结算同一口径。</param>
    /// <param name="state">聚合状态快照（用于精确解析技艺/建筑/工具加速后的单轮耗时）。</param>
    public static object[] ToActionQueueSection(IReadOnlyList<QueuedActionState> actions, GameDataCatalog? gameData, UnitInspectDto? inspectedUnit = null, Action? onAttributesProjected = null, double playerThreatBonusPoints = 0, PlayerCombatBuffState? playerCarriedBuffs = null, double monsterWeakenRatio = 0, PlayerAggregateState? state = null)
    {
        if (actions is null || actions.Count == 0) return [];
        var result = new object[actions.Count];
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var def = gameData?.Actions.Contains(action.Intent.ActionId) == true
                ? gameData.Actions.Get(action.Intent.ActionId)
                : null;
            var executionKind = def?.ExecutionKind switch
            {
                ActionExecutionKind.CombatEncounter => "combat-encounter",
                ActionExecutionKind.Travel => "travel",
                _ => "timed"
            };
            double durationSeconds;
            if (action.Travel is not null)
            {
                durationSeconds = action.Travel.TotalDurationSeconds;
            }
            else if (def is not null && executionKind == "timed" && state is not null)
            {
                var timed = TimedActionDurationRules.Calculate(state, action, def, gameData);
                durationSeconds = timed > 0 ? timed : def.DurationSeconds;
            }
            else
            {
                durationSeconds = def?.DurationSeconds ?? 0;
            }
            var actionName = def?.Name ?? (action.Travel is not null ? "赶路" : null);
            var actionType = (def?.Kind ?? (action.Travel is not null ? ActionKind.Travel : ActionKind.Cultivation)).ToString().ToLowerInvariant();

            // 队列条目白名单：只下发前端消费且为权威真值的字段；
            // travel 对象本体不外泄，目标/总里程/分段统一走扁平字段与 travelSegments。
            result[i] = new
            {
                id = action.Id,
                intent = action.Intent,
                actionName,
                actionType,
                // 强化队列显示：目标/辅助物品在 intent 之外再平铺一行字段，前端队列行免回读嵌套对象。
                targetItemId = action.Intent.TargetItemId,
                auxItemId = action.Intent.AuxItemId,
                enhancementCurrentLevel = action.EnhancementCurrentLevel,
                status = (byte)action.Status,
                progressSeconds = action.ProgressSeconds,
                durationSeconds,
                remainingRepeats = action.RemainingRepeats,
                completedRepeats = action.CompletedRepeats,
                executionKind,
                combatEncounterId = def?.CombatEncounterId ?? action.Expedition?.EncounterId,
                expedition = action.Expedition is null ? null : ToExpeditionDto(action.Expedition, action.Participation?.Spec, gameData, inspectedUnit, onAttributesProjected, playerThreatBonusPoints, playerCarriedBuffs, monsterWeakenRatio),
                // 行动参与会话标记：参与会话负责调度，散场时刻为权威值（+∞ 进行中 → null）。
                participation = action.Participation is { } participationBinding ? new
                {
                    sessionId = participationBinding.SessionId,
                    endsAtUnixSeconds = double.IsPositiveInfinity(participationBinding.EndsAtUnixSeconds)
                        ? (double?)null : participationBinding.EndsAtUnixSeconds
                } : null,
                travelDestinationNodeId = action.Travel?.TargetNodeId ?? action.Intent.TargetNodeId,
                travelTotalDistance = action.Travel is null || gameData is null
                    ? (double?)null
                    : action.Travel.Segments.Sum(s => EdgeDistance(gameData, s.EdgeId, s.DurationSeconds)),
                travelSegments = action.Travel?.Segments.Select(s => new
                {
                    edgeId = s.EdgeId,
                    startProgress = s.StartProgress,
                    endProgress = s.EndProgress,
                    durationSeconds = s.DurationSeconds,
                    distance = EdgeDistance(gameData, s.EdgeId, s.DurationSeconds)
                })
            };
        }
        return result;
    }

    /// <summary>世界边里程：优先取图边几何距离，配表缺失时退回通行耗时（距离与耗时同构）。</summary>
    private static double EdgeDistance(GameDataCatalog? gameData, string edgeId, double durationSeconds)
        => gameData is not null && gameData.WorldGraph.TryGetEdge(edgeId, out var edge) ? edge.Distance : durationSeconds;

    /// <summary>战斗远征相位到协议字符串的映射；wire 上只存在六个相位词。</summary>
    private static string MapExpeditionPhase(CombatExpeditionPhase phase) => phase switch
    {
        CombatExpeditionPhase.InitialSearch => "initial-search",
        CombatExpeditionPhase.Search => "search",
        CombatExpeditionPhase.Combat => "combat",
        CombatExpeditionPhase.Respawning => "respawn",
        CombatExpeditionPhase.DuelPending => "duel-pending",
        CombatExpeditionPhase.Duel => "duel",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    /// <summary>
    /// 战斗远征白名单投影：只保留界面渲染与确定性回放所需的字段；
    /// Seed/Outcome 等审计标量保留下发供前端校验，PlayerState/内部撮合标记不外泄。
    /// 敌方/盟友条目实时字段（血蓝/出手/威胁）常驻下发；属性面板为按需投影——仅当
    /// 会话观察目标（UnitInspect）命中该侧下标时才携带 attributes，未点开的单位不占快照带宽。
    /// 威胁字段统一投影有效威胁值（基础实际值×(1+增幅点数/100)），与目标判定口径一致。
    /// </summary>
    private static object ToExpeditionDto(CombatExpeditionState expedition, ActionParticipationSpec? spec, GameDataCatalog? gameData, UnitInspectDto? inspectedUnit, Action? onAttributesProjected = null, double playerThreatBonusPoints = 0, PlayerCombatBuffState? playerCarriedBuffs = null, double monsterWeakenRatio = 0)
    {
        // 敌怪展示面板削弱口径：会话取开打定格冻结值，单人取本机当前节点建筑投影。
        var effectiveMonsterWeaken = spec?.MonsterWeakenRatio ?? monsterWeakenRatio;
        var inspectedEnemyIndex = inspectedUnit is { Side: "enemy", Index: >= 0 } enemyTarget ? enemyTarget.Index.Value : (int?)null;
        var inspectedAllyIndex = inspectedUnit is { Side: "ally", Index: >= 0 } allyTarget ? allyTarget.Index.Value : (int?)null;
        return new
        {
            expedition.EncounterId,
            phase = MapExpeditionPhase(expedition.Phase),
            expedition.PhaseElapsedSeconds, expedition.PhaseDurationSeconds,
            expedition.BattleElapsedSeconds,
            expedition.PlayerHitpoints, expedition.PlayerManapoints,
            expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount,
            // 本机有效威胁：镜像基础值 ×(1+面板增幅+PlayerState 修正/战内 Buff 点数/100)。
            playerThreat = CombatantState.ComputeEffectiveThreat(
                expedition.PlayerThreat, playerThreatBonusPoints,
                expedition.PlayerState?.ThreatBonusPoints ?? 0, expedition.PlayerState?.Buffs),
            expedition.WaveNumber,
            outcome = (byte)expedition.Outcome,
            expedition.Seed,
            // 倒地窗口（原地复活遭遇）：复活截止点与读条时长（战斗时钟秒），前端据此渲染阵亡置灰与复活读条。
            playerRespawnAtSeconds = expedition.PlayerRespawnAtSeconds,
            playerRespawnDurationSeconds = gameData?.CombatEncounters.TryGetValue(expedition.EncounterId, out var respawnEncounter) == true
                ? respawnEncounter.PlayerRespawnSeconds : (double?)null,
            // 读条已走完但复活策略条件未成立的等待复查时刻：前端据此把读条改为「等待条件」态。
            playerRespawnWaitingUntilSeconds = expedition.Phase == CombatExpeditionPhase.Combat
                && expedition.PlayerState?.RespawnWaitingUntilSeconds is { } waitingSelf
                    ? waitingSelf : (double?)null,
            // 本机战内 Buff（含开战注入的消耗品副本）：战斗时钟到期时刻随快照下发，
            // 前端按 battleElapsedSeconds 反推剩余；非战斗相位改投跨波携带态
            // （expiresAt = 冻结时钟基准 + 剩余秒数，前端换算后倒计时即剩余时长）。
            playerBuffs = expedition.Phase == CombatExpeditionPhase.Combat
                ? ToCombatBuffsDto(expedition.PlayerState?.Buffs)
                : ToCombatBuffsDto(playerCarriedBuffs, expedition.BattleElapsedSeconds),
            enemies = (expedition.Enemies ?? []).Select((enemy, i) => ToExpeditionEnemyDto(enemy, gameData, inspectedEnemyIndex == i, onAttributesProjected, effectiveMonsterWeaken)).ToArray(),
            duel = expedition.Duel is null ? null : ToDuelPlaybackDto(expedition.Duel, inspectedEnemyIndex == 0, onAttributesProjected),
            recentEvents = expedition.RecentEvents is null || expedition.RecentEvents.Count == 0 ? null : expedition.RecentEvents.Select(e => new
            {
                at = e.AtSeconds,
                attackerId = e.AttackerId,
                targetId = e.TargetId,
                damage = e.Damage,
                hit = e.Hit,
                critical = e.Critical,
                broken = e.Break,
                parried = e.Parried,
                abilityId = e.AbilityId,
                targetHp = e.TargetHitpointsAfter
            }).ToArray(),
            // 组队讨伐盟友阵列（§21.13）：按规格成员序下发全部在场成员（含本机位由标量镜像合成）。
            selfIndex = spec is null ? (int?)null : expedition.SelfIndex,
            allies = spec is null ? null : ToExpeditionAlliesDto(expedition, spec, inspectedAllyIndex, onAttributesProjected, playerCarriedBuffs)
        };
    }

    /// <summary>
    /// 盟友阵列投影：按会话规格成员序铺满；本机位由远征标量镜像+PlayerState 快照合成，
    /// 其余位取盟友镜像。血蓝上限取自冻结面板（共享剧本唯一公共输入）。
    /// 属性面板为按需一次性交付：会话观察目标（UnitInspect side=ally）命中 memberIndex 时
    /// 才投影开打冻结面板 attributes，未点开的队友不占快照带宽。
    /// </summary>
    private static object[] ToExpeditionAlliesDto(CombatExpeditionState expedition, ActionParticipationSpec spec, int? inspectedAllyIndex, Action? onAttributesProjected = null, PlayerCombatBuffState? playerCarriedBuffs = null)
    {
        var result = new object[spec.Members.Count];
        for (var i = 0; i < spec.Members.Count; i++)
        {
            var member = spec.Members[i];
            CombatantSnapshot state;
            ExpeditionAllyState? ally;
            string? pendingAbilityId;
            double pendingDuration;
            if (i == expedition.SelfIndex)
            {
                ally = null;
                state = expedition.PlayerState ?? new CombatantSnapshot(
                    expedition.PlayerHitpoints, expedition.PlayerManapoints,
                    expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount, expedition.PlayerThreat,
                    RespawnAtSeconds: expedition.PlayerRespawnAtSeconds);
                pendingAbilityId = PendingAbilityIdOf(member, state);
                pendingDuration = PendingDurationOf(member, state);
            }
            else
            {
                ally = FindAllyMirror(expedition.Allies, i);
                // 镜像缺失时以开打定格初值兜底展示（战斗相位内盟友镜像恒在，搜索相位口径一致）。
                state = ally?.State ?? new CombatantSnapshot(member.InitialHitpoints, member.InitialManapoints, 0, 0, 0);
                pendingAbilityId = ally?.PendingAbilityId;
                pendingDuration = ally?.PendingActionDurationSeconds ?? 0;
            }
            // 观察目标命中该成员位才投影属性与装配：冻结面板、功法槽与已穿戴装备均为开打定格的
            // 共享剧本输入，与敌怪同一交付口径（一次性携带，前端按 ally:{playerId} 缓存复用）。
            var inspected = inspectedAllyIndex == i;
            var attributes = inspected ? ToAttributesDto(member.Stats,
                expedition.Phase == CombatExpeditionPhase.Combat
                    ? CombatantState.SumBuffThreatPoints(state.Buffs)
                    : (i == expedition.SelfIndex ? playerCarriedBuffs : ally?.CombatBuffs)?.SumThreatPoints() ?? 0) : null;
            var skills = inspected
                ? member.Skills.Select(slot => new PlayerAbilitySlot(
                    slot.Ability.TryGetProperty("id", out var idElement) ? idElement.GetString() : null,
                    slot.Level)).ToArray()
                : null;
            var equipment = inspected ? member.Equipment : null;
            if (attributes is not null) onAttributesProjected?.Invoke();
            result[i] = new
            {
                memberIndex = i,
                playerId = member.PlayerId,
                member.DisplayName,
                avatar = member.Avatar,
                state.Hitpoints, state.Manapoints,
                maxHitpoints = member.Stats.MaxHitpoints,
                maxManapoints = member.Stats.MaxManapoints,
                state.NextAttackAtSeconds, state.AttackCount,
                // 有效威胁：镜像基础值 ×(1+冻结面板增幅+镜像修正/战内 Buff 点数/100)。
                threat = CombatantState.ComputeEffectiveThreat(
                    state.Threat, member.Stats.ThreatBonusPoints,
                    state.ThreatBonusPoints, state.Buffs),
                pendingAbilityId, pendingActionDurationSeconds = pendingDuration,
                respawnAtSeconds = state.RespawnAtSeconds,
                // 读条已走完但策略条件未成立：等待复查时刻（null 表示未在等待）。
                respawnWaitingUntilSeconds = state.RespawnWaitingUntilSeconds,
                // 战内 Buff：战斗相位投波中实时快照；非战斗相位投跨波携带态
                // （本机位取 playerCarriedBuffs，盟友位取镜像 CombatBuffs）。
                buffs = expedition.Phase == CombatExpeditionPhase.Combat
                    ? ToCombatBuffsDto(state.Buffs)
                    : ToCombatBuffsDto(i == expedition.SelfIndex ? playerCarriedBuffs : ally?.CombatBuffs,
                        expedition.BattleElapsedSeconds),
                attributes,
                skills,
                equipment
            };
        }
        return result;

        static ExpeditionAllyState? FindAllyMirror(IReadOnlyList<ExpeditionAllyState>? allies, int memberIndex)
        {
            if (allies is null) return null;
            for (var i = 0; i < allies.Count; i++)
                if (allies[i].MemberIndex == memberIndex) return allies[i];
            return null;
        }

        /// <summary>本机位吟唱技能 ID：与远征导出同口径——挂起技能槽位反查冻结面板槽位能力。</summary>
        static string? PendingAbilityIdOf(ActionParticipantSnapshot member, CombatantSnapshot state)
            => state.PendingSkillIndex >= 0 && state.PendingSkillIndex < member.Skills.Count
                && member.Skills[state.PendingSkillIndex].Ability.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

        /// <summary>本机位动作时长：吟唱按配表毫秒×行动速度乘率，普攻按间隔×攻速×行动速度（与引擎导出公式一致）。</summary>
        static double PendingDurationOf(ActionParticipantSnapshot member, CombatantSnapshot state)
        {
            var speed = PercentValue.Multiplier(member.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
            if (state.PendingSkillIndex >= 0 && state.PendingSkillIndex < member.Skills.Count
                && member.Skills[state.PendingSkillIndex].Ability is { } ability
                && ability.TryGetProperty("castMilliseconds", out var castElement)
                && castElement.TryGetDouble(out var castMs) && castMs > 0)
                return castMs / 1000 * speed;
            return member.Stats.AttackIntervalSeconds
                * PercentValue.Multiplier(member.Stats.AttackSpeedPoints, PercentCurveMode.NonLinear) * speed;
        }
    }

    private static object ToExpeditionEnemyDto(ExpeditionEnemyState enemy, GameDataCatalog? gameData, bool includeAttributes, Action? onAttributesProjected = null, double monsterWeakenRatio = 0)
    {
        var definition = gameData is null ? null : gameData.CombatMonsters.GetValueOrDefault(enemy.EnemyId);
        var found = definition is not null;
        // 封魔阵类节点削弱随展示口径同步折减：血条分母与按需属性面板与实战结算一致。
        var stats = found ? NodeBuildingRules.WeakenMonsterStats(definition!.Stats, monsterWeakenRatio) : null;
        JsonElement? visual = null;
        if (gameData is not null
            && gameData.Monsters.TryGetValue(enemy.EnemyId, out var raw)
            && raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("visual", out var visualElement)
            && visualElement.ValueKind == JsonValueKind.Object)
            visual = visualElement.Clone();
        var attributes = stats is not null && includeAttributes
            ? ToAttributesDto(stats, CombatantState.SumBuffThreatPoints(enemy.State.Buffs))
            : null;
        if (attributes is not null) onAttributesProjected?.Invoke();
        return new
        {
            enemy.EnemyId,
            name = found ? definition!.Name : enemy.EnemyId,
            rank = !found ? "normal" : definition!.Rank switch
            {
                CombatMonsterRank.Elite => "elite",
                CombatMonsterRank.Boss => "boss",
                _ => "normal"
            },
            visual,
            enemy.State.Hitpoints, enemy.State.Manapoints,
            enemy.State.NextAttackAtSeconds, enemy.State.AttackCount,
            // 有效威胁：镜像基础值 ×(1+配表面板增幅+镜像修正/战内 Buff 点数/100)。
            threat = CombatantState.ComputeEffectiveThreat(
                enemy.State.Threat, definition?.Stats.ThreatBonusPoints ?? 0,
                enemy.State.ThreatBonusPoints, enemy.State.Buffs),
            enemy.PendingAbilityId, enemy.PendingActionDurationSeconds,
            buffs = ToCombatBuffsDto(enemy.State.Buffs),
            // 血蓝条分母常驻：血/蓝当前值没有上限无法渲染进度条，两个标量属实时字段。
            maxHitpoints = stats?.MaxHitpoints,
            maxManapoints = stats?.MaxManapoints,
            attributes
        };
    }

    /// <summary>战内 Buff 投影：名称/层数/战斗时钟到期时刻与效果字段随单位快照下发，结算权威在服务端。</summary>
    private static object[]? ToCombatBuffsDto(IReadOnlyList<CombatBuffState>? buffs)
    {
        if (buffs is null || buffs.Count == 0) return null;
        return buffs.Select(buff => (object)new
        {
            buff.BuffId, buff.Name, buff.Stacks,
            expiresAtSeconds = buff.ExpiresAtSeconds,
            buff.HpRegen, buff.MpRegen, buff.AttackSpeedPoints, buff.DamageAbsorptionPoints,
            buff.ThreatPoints, buff.DamageBonusPoints, buff.ThreatPerSecondMaxHpPoints,
            buff.HpRegenPostRatio, buff.MpRegenPostRatio,
            buff.AttackPower, buff.Accuracy, buff.Critical, buff.ResistancePoints,
            buff.HpRegenPerSecond
        }).ToArray();
    }

    /// <summary>跨波携带 Buff 投影：剩余秒数折算为冻结时钟基准上的到期时刻（expiresAt = battleElapsed + 剩余），
    /// 前端按同一 battleElapsed 反推后倒计时即剩余时长，与战斗相位 DTO 同构。</summary>
    private static object[]? ToCombatBuffsDto(PlayerCombatBuffState? carried, double battleElapsedSeconds)
    {
        if (carried is null || carried.Buffs.Count == 0) return null;
        return carried.Buffs.Select(carry => (object)new
        {
            carry.BuffId, carry.Name, carry.Stacks,
            expiresAtSeconds = double.IsFinite(battleElapsedSeconds + carry.RemainingSeconds)
                ? battleElapsedSeconds + carry.RemainingSeconds : double.MaxValue,
            carry.HpRegen, carry.MpRegen, carry.AttackSpeedPoints, carry.DamageAbsorptionPoints,
            carry.ThreatPoints, carry.DamageBonusPoints, carry.ThreatPerSecondMaxHpPoints,
            carry.HpRegenPostRatio, carry.MpRegenPostRatio,
            carry.AttackPower, carry.Accuracy, carry.Critical, carry.ResistancePoints,
            carry.HpRegenPerSecond
        }).ToArray();
    }

    /// <summary>斗法回放白名单投影：对手资源与事件级 MP 快照常驻下发；属性面板仅在观察目标命中镜像位（enemy:0）时投影。</summary>
    private static object ToDuelPlaybackDto(DuelPlaybackState duel, bool includeOpponentAttributes, Action? onAttributesProjected = null)
    {
        var opponentAttributes = includeOpponentAttributes ? ToAttributesDto(duel.OpponentStats) : null;
        if (opponentAttributes is not null) onAttributesProjected?.Invoke();
        return new
        {
            duel.DuelId,
            duel.OpponentPlayerId,
            duel.OpponentName,
            opponentAvatar = duel.OpponentAvatar,
            viewerOutcome = (byte)duel.ViewerOutcome,
            duel.DurationSeconds,
            duel.OpponentHitpoints,
            duel.OpponentManapoints,
            // 镜像血蓝条分母：常驻标量（与敌怪 maxHitpoints/maxManapoints 同一取舍）。
            opponentMaxHitpoints = duel.OpponentStats.MaxHitpoints,
            opponentMaxManapoints = duel.OpponentStats.MaxManapoints,
            opponentAttributes,
            // 镜像功法槽与已穿戴装备与属性同帧一次性交付（撮合时冻结），前端按 duel:{对手名} 缓存。
            opponentSkills = includeOpponentAttributes ? duel.OpponentSkills : null,
            opponentEquipment = includeOpponentAttributes ? duel.OpponentEquipment : null,
            duel.Resolved,
            duel.Forfeited,
            events = duel.Events.Select(duelEvent => new
            {
                duelEvent.AtSeconds,
                duelEvent.AttackerIsInitiator,
                duelEvent.TargetIsInitiator,
                duelEvent.Damage,
                duelEvent.Hit,
                duelEvent.Critical,
                broken = duelEvent.Break,
                parried = duelEvent.Parried,
                duelEvent.TargetHitpointsAfter,
                duelEvent.AbilityId,
                duelEvent.Healing,
                duelEvent.AttackerManapointsAfter,
                duelEvent.TargetManapointsAfter
            }).ToArray()
        };
    }

    /// <summary>配表实例 → 世界节点静态投影索引缓存：catalog 为 DI 单例启动后不热更，弱引用表随实例回收自然失效。</summary>
    private static readonly ConditionalWeakTable<GameDataCatalog, WorldStaticProjectionIndex> s_worldProjections = new();

    /// <summary>单节点的静态投影：map/节点配置、生态清单、附近生灵、建筑与可建列表等纯配表派生内容。</summary>
    private sealed class WorldNodeStaticProjection
    {
        public required string MapId { get; init; }
        public required string? Name { get; init; }
        public required double? X { get; init; }
        public required double? Y { get; init; }
        public required double CultivationMultiplier { get; init; }
        public required string[] AvailableActionIds { get; init; }
        public required string[] EncounterIds { get; init; }
        public required object[] Npcs { get; init; }
        public required object[] Buildings { get; init; }
        public required string[]? BuildableBuildingIds { get; init; }
        public required double MinBudget { get; init; }
        public required double MaxBudget { get; init; }
        public required double EliteInterval { get; init; }
        public required double BossInterval { get; init; }
        public required object[] Monsters { get; init; }
        public required object[] EliteEnemies { get; init; }
        public required object[] BossEnemies { get; init; }
    }

    /// <summary>整表节点静态投影索引：按 nodeId 首个出现者生效（与原线性扫描首个命中语义一致）。</summary>
    private sealed class WorldStaticProjectionIndex
    {
        public required IReadOnlyDictionary<string, WorldNodeStaticProjection> ByNodeId { get; init; }

        public static WorldStaticProjectionIndex Build(GameDataCatalog catalog)
        {
            var byNodeId = new Dictionary<string, WorldNodeStaticProjection>(StringComparer.Ordinal);
            if (catalog.Maps is null) return new WorldStaticProjectionIndex { ByNodeId = byNodeId };
            for (var i = 0; i < catalog.Maps.Count; i++)
            {
                var map = catalog.Maps[i];
                if (map.ValueKind != JsonValueKind.Object
                    || !map.TryGetProperty("nodes", out var nodes)
                    || nodes.ValueKind != JsonValueKind.Array)
                    continue;
                var mapId = ReadString(map, "id", "map.qingyun-mountains");
                foreach (var node in nodes.EnumerateArray())
                {
                    var nodeId = ReadString(node, "id");
                    if (string.IsNullOrEmpty(nodeId) || byNodeId.ContainsKey(nodeId)) continue;

                    // 节点生态：遭遇池弱引用解析出栖息生灵与危险度区间，供 NearbyPanel 直接消费。
                    var encounterIds = ReadStringArray(node, "encounterIds");
                    var nodeEncounters = encounterIds
                        .Select(id => catalog.Encounters.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && ReadString(e, "id") == id))
                        .Where(e => e.ValueKind == JsonValueKind.Object)
                        .ToArray();
                    var rawNormalIds = new List<string>();
                    var rawEliteIds = new List<string>();
                    var rawBossIds = new List<string>();
                    foreach (var e in nodeEncounters)
                    {
                        rawNormalIds.AddRange(ReadArray(e, "randomSpawns").Select(s => ReadString(s, "enemyId")));
                        rawNormalIds.AddRange(ReadStringArray(e, "normalEnemyIds"));
                        rawEliteIds.AddRange(ReadStringArray(e, "eliteEnemyIds"));
                        rawBossIds.AddRange(ReadStringArray(e, "bossEnemyIds"));
                    }

                    // 生灵排他分流：Boss 优先，其次精英，普通生灵严格排除精英/首领怪，避免同怪跨类别重复展示
                    var bossIds = rawBossIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
                    var eliteIds = rawEliteIds.Where(id => !string.IsNullOrEmpty(id) && !bossIds.Contains(id, StringComparer.Ordinal))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    var normalIds = rawNormalIds.Where(id => !string.IsNullOrEmpty(id)
                        && !bossIds.Contains(id, StringComparer.Ordinal)
                        && !eliteIds.Contains(id, StringComparer.Ordinal)
                        && (!catalog.Monsters.TryGetValue(id, out var m)
                            || (!string.Equals(ReadString(m, "rank", "normal"), "boss", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(ReadString(m, "rank", "normal"), "elite", StringComparison.OrdinalIgnoreCase))))
                        .Distinct(StringComparer.Ordinal).ToArray();

                    // 与原实现同构：空序列经 DefaultIfEmpty().Min/Max 得到 0，投影原样保留该边界值。
                    var minBudget = nodeEncounters.Select(e => ReadDouble(e, "minBudget", double.NaN))
                        .Where(double.IsFinite).DefaultIfEmpty().Min();
                    var maxBudget = nodeEncounters.Select(e => ReadDouble(e, "maxBudget", double.NaN))
                        .Where(double.IsFinite).DefaultIfEmpty().Max();

                    // 精英/Boss 周期：未指定时回退领域默认值（精英 5 场，Boss 20 场），杜绝 0 场出没
                    var eliteIntervals = nodeEncounters
                        .Select(e => ReadDouble(e, "battlesPerElite", ReadDouble(e, "eliteInterval", double.NaN)))
                        .Where(v => double.IsFinite(v) && v > 0)
                        .ToArray();
                    var eliteInterval = eliteIntervals.Length > 0 ? eliteIntervals.Min() : 5.0;

                    var bossIntervals = nodeEncounters
                        .Select(e => ReadDouble(e, "battlesPerBoss", ReadDouble(e, "bossInterval", double.NaN)))
                        .Where(v => double.IsFinite(v) && v > 0)
                        .ToArray();
                    var bossInterval = bossIntervals.Length > 0 ? bossIntervals.Min() : 20.0;

                    byNodeId[nodeId] = new WorldNodeStaticProjection
                    {
                        MapId = mapId,
                        // 与原 ReadString(node,"name",world.NodeId) 同构：缺字段/非字符串 → null 由消费方回退 NodeId；
                        // 字符串原值（含空串）原样保留。
                        Name = node.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString()
                            : null,
                        // 与原 ReadDouble(node,"x/y",world.X/Y) 同构：缺失或无法解析 → null 由消费方回退；
                        // 非 Number 类型与原实现一致抛 InvalidOperationException。
                        X = node.TryGetProperty("x", out var xElement) && xElement.TryGetDouble(out var nx) ? nx : null,
                        Y = node.TryGetProperty("y", out var yElement) && yElement.TryGetDouble(out var ny) ? ny : null,
                        CultivationMultiplier = ReadDouble(node, "cultivationMultiplier", 1),
                        AvailableActionIds = ReadStringArray(node, "availableActionIds"),
                        EncounterIds = encounterIds,
                        Npcs = ReadEntities(node, "npcs"),
                        Buildings = ReadEntities(node, "buildings"),
                        BuildableBuildingIds = Domain.World.NodeBuildingRules.ResolveBuildableIds(node, catalog.Buildings ?? new Dictionary<string, BuildingDefinition>(StringComparer.Ordinal))
                            ?.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        MinBudget = minBudget,
                        MaxBudget = maxBudget,
                        EliteInterval = eliteInterval,
                        BossInterval = bossInterval,
                        Monsters = normalIds.Select(id => ToNearbyMonster(catalog, id)).Where(m => m is not null).ToArray()!,
                        EliteEnemies = eliteIds.Select(id => ToNearbyMonster(catalog, id)).Where(m => m is not null).ToArray()!,
                        BossEnemies = bossIds.Select(id => ToNearbyMonster(catalog, id)).Where(m => m is not null).ToArray()!
                    };
                }
            }
            return new WorldStaticProjectionIndex { ByNodeId = byNodeId };
        }
    }

    private static object ToWorldSection(PlayerWorldStateDocument world, GameDataCatalog? gameData)
    {
        // 静态投影按配表实例缓存：不再逐次扫描整张地图；动态字段（位置/边进度/在建建筑）仍按当前状态投影。
        var projection = gameData is null
            ? null
            : s_worldProjections.GetValue(gameData, static catalog => WorldStaticProjectionIndex.Build(catalog))
                .ByNodeId.GetValueOrDefault(world.NodeId ?? string.Empty);

        var edgeId = world.EdgeId;
        var edgeProgress = world.EdgeProgress;
        var onEdge = !string.IsNullOrWhiteSpace(edgeId);
        var x = onEdge ? world.X : projection?.X ?? world.X;
        var y = onEdge ? world.Y : projection?.Y ?? world.Y;
        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        // 拥挤使用快照按 buildingId 建索引：与建筑投影同节拍下发，缺失条目按零拥挤展示。
        var usageIndex = world.NodeBuildingUsages?.ToDictionary(
            usage => usage.BuildingId, StringComparer.Ordinal);
        return new
        {
            mapId = projection?.MapId ?? "map.qingyun-mountains",
            nodeId = onEdge ? null : world.NodeId,
            edgeId,
            edgeProgress,
            x,
            y,
            locationName = onEdge ? "旅途中" : projection?.Name ?? world.NodeId,
            cultivationMultiplier = projection?.CultivationMultiplier ?? 1d,
            // 当前节点公共建筑生效效果汇总（聚合侧同一口径）：前端行动产出预览按此计入
            // 产出/耗时加成；跨节点投影泄漏由 EffectiveNodeBuildingEffects 统一拦截。
            buildingEffects = new
            {
                explorationSpeedRatio = world.EffectiveNodeBuildingEffects.ExplorationSpeedRatio,
                explorationOutputRatio = world.EffectiveNodeBuildingEffects.ExplorationOutputRatio,
                alchemySpeedRatio = world.EffectiveNodeBuildingEffects.AlchemySpeedRatio,
                alchemyOutputRatio = world.EffectiveNodeBuildingEffects.AlchemyOutputRatio,
                cultivationOutputRatio = world.EffectiveNodeBuildingEffects.CultivationOutputRatio,
                gatheringSpeedRatio = world.EffectiveNodeBuildingEffects.GatheringSpeedRatio,
                miningSpeedRatio = world.EffectiveNodeBuildingEffects.MiningSpeedRatio,
                forgingSpeedRatio = world.EffectiveNodeBuildingEffects.ForgingSpeedRatio,
                talismanSpeedRatio = world.EffectiveNodeBuildingEffects.TalismanSpeedRatio,
                formationSpeedRatio = world.EffectiveNodeBuildingEffects.FormationSpeedRatio,
                enhancementSpeedRatio = world.EffectiveNodeBuildingEffects.EnhancementSpeedRatio,
                combatHitpointRegen = world.EffectiveNodeBuildingEffects.CombatHitpointRegen,
                combatManapointRegen = world.EffectiveNodeBuildingEffects.CombatManapointRegen,
                combatDamageReductionRatio = world.EffectiveNodeBuildingEffects.CombatDamageReductionRatio,
                monsterWeakenRatio = world.EffectiveNodeBuildingEffects.MonsterWeakenRatio,
            },
            availableActionIds = onEdge || projection is null ? Array.Empty<string>() : projection.AvailableActionIds,
            encounterIds = onEdge || projection is null ? Array.Empty<string>() : projection.EncounterIds,
            npcs = onEdge || projection is null ? Array.Empty<object>() : projection.Npcs,
            buildings = onEdge || projection is null ? Array.Empty<object>() : projection.Buildings,
            // 节点可建列表：buildableList/buildableTemplate 二选一解析；null 表示该节点不可建（前端隐藏加号卡）。
            buildableBuildingIds = onEdge ? null : projection?.BuildableBuildingIds,
            // 节点公共建筑：共享注册表投影随 world 分段持久化，快照映射按当前时刻推算双进度。
            nodeBuildings = onEdge
                ? Array.Empty<object>()
                : (world.NodeBuildings ?? [])
                    .Select(entry => ToNodeBuilding(gameData, entry, nowUnixSeconds, usageIndex))
                    .Where(entry => entry is not null)
                    .ToArray(),
            minBudget = projection?.MinBudget ?? 0.0,
            maxBudget = projection?.MaxBudget ?? 0.0,
            eliteInterval = projection is not null && projection.EliteInterval > 0 ? projection.EliteInterval : 5.0,
            bossInterval = projection is not null && projection.BossInterval > 0 ? projection.BossInterval : 20.0,
            monsters = projection?.Monsters ?? [],
            eliteEnemies = projection?.EliteEnemies ?? [],
            bossEnemies = projection?.BossEnemies ?? []
        };
    }

    /// <summary>
    /// 把节点公共建筑记录投影为附近面板消费的建筑 DTO：
    /// 含等级、建造中目标等级、双进度（材料/时间）与逐项材料需求-已交明细；配表缺失的定义直接丢弃。
    /// </summary>
    private static object? ToNodeBuilding(GameDataCatalog? gameData, Domain.World.NodeBuildingState state,
        double nowUnixSeconds, IReadOnlyDictionary<string, Domain.World.NodeBuildingUsage>? usageIndex)
    {
        if (gameData is null || !gameData.Buildings.TryGetValue(state.BuildingId, out var definition)) return null;
        var required = state.TargetLevel > 0
            ? Domain.World.NodeBuildingRules.RequiredMaterials(definition, state.TargetLevel)
            : new Dictionary<string, double>(StringComparer.Ordinal);
        var materialProgress = Domain.World.NodeBuildingRules.MaterialFraction(state, definition);
        var effectiveElapsedSeconds = Domain.World.NodeBuildingRules.EffectiveElapsedSeconds(state, definition, nowUnixSeconds);
        var requiredSeconds = state.TargetLevel > 0 ? Domain.World.NodeBuildingRules.RequiredBuildSeconds(state.TargetLevel) : 0;
        // 工期条显示有效工时比例：材料不足时工程停滞，封顶于材料进度。
        var timeProgress = requiredSeconds > 0 ? Math.Clamp(effectiveElapsedSeconds / requiredSeconds, 0, 1) : 1d;
        var level = Domain.World.NodeBuildingRules.EffectiveLevel(state, definition, nowUnixSeconds);
        // 拥挤快照缺失（旧投影/未刷新）按零人数满效口径展示，不产生误导性超载提示。
        var usage = usageIndex?.GetValueOrDefault(state.BuildingId);
        return new
        {
            entityId = definition.Id,
            name = definition.Name,
            kind = "building",
            description = definition.Description,
            effect = definition.Effect,
            group = definition.Group,
            level,
            targetLevel = state.TargetLevel,
            buildProgress = Domain.World.NodeBuildingRules.BuildProgress(state, definition, nowUnixSeconds),
            materialProgress,
            timeProgress,
            activeUsers = usage?.ActiveUsers ?? 0,
            capacity = usage?.Capacity ?? Domain.World.NodeBuildingRules.CrowdCapacity(level),
            efficiency = usage?.Efficiency ?? 1d,
            requiredSeconds,
            elapsedSeconds = effectiveElapsedSeconds,
            materials = required
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (object)new
                {
                    itemId = pair.Key,
                    required = pair.Value,
                    contributed = Math.Min(pair.Value, state.ContributedMaterials?.GetValueOrDefault(pair.Key) ?? 0)
                })
                .ToArray()
        };
    }

    /// <summary>把怪物配表条目投影为 NearbyPanel 消费的附近生灵 DTO。</summary>
    private static object? ToNearbyMonster(GameDataCatalog? gameData, string id)
    {
        if (gameData is null || string.IsNullOrWhiteSpace(id)
            || !gameData.Monsters.TryGetValue(id, out var monster)
            || monster.ValueKind != JsonValueKind.Object)
            return null;
        gameData.CombatMonsters.TryGetValue(id, out var def);
        var visual = monster.TryGetProperty("visual", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        return new
        {
            monsterId = id,
            name = ReadString(monster, "name", id),
            level = def?.Level ?? ReadDouble(monster, "level", 1),
            tier = gameData.BaseAttributes?.TierAt(def?.Level ?? ReadDouble(monster, "level", 1)) ?? 1,
            rank = ReadString(monster, "rank", "normal"),
            budgetCost = ReadDouble(monster, "budgetCost", 1),
            maxHitpoints = def?.Stats.MaxHitpoints ?? 0,
            physicalAttack = def?.Stats.AttackPower ?? 0,
            armor = def?.Stats.Defense ?? 0,
            drops = ReadArray(monster, "dropTable")
                .Select(d => new
                {
                    itemId = ReadString(d, "itemId"),
                    dropChance = ReadDouble(d, "dropChance", 0),
                    minQuantity = ReadDouble(d, "minQuantity", 0),
                    maxQuantity = ReadDouble(d, "maxQuantity", 0)
                })
                .Where(d => d.itemId.Length > 0)
                .ToArray(),
            visualSource = visual.ValueKind == JsonValueKind.Object ? ReadString(visual, "source") : null,
            visualRenderer = visual.ValueKind == JsonValueKind.Object ? ReadString(visual, "renderer") : null
        };
    }

    private static JsonElement[] ReadArray(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Array
        ? child.EnumerateArray().Select(item => item.Clone()).ToArray() : [];

    private static object[] ReadEntities(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.Array)
            return [];
        var count = child.GetArrayLength();
        if (count == 0) return [];
        var result = new object[count];
        var i = 0;
        foreach (var entity in child.EnumerateArray())
        {
            result[i++] = new
            {
                entityId = ReadString(entity, "id"),
                name = ReadString(entity, "name"),
                kind = ReadString(entity, "kind", "building"),
                description = ReadString(entity, "description"),
                effect = ReadString(entity, "effect")
            };
        }
        return result;
    }

    private static string[] ReadStringArray(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.Array)
            return [];
        var count = child.GetArrayLength();
        if (count == 0) return [];
        var list = new List<string>(count);
        foreach (var item in child.EnumerateArray())
        {
            var str = item.GetString();
            if (!string.IsNullOrEmpty(str)) list.Add(str);
        }
        return list.ToArray();
    }

    private static string ReadString(JsonElement value, string property, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? fallback : fallback;
    private static double ReadDouble(JsonElement value, string property, double fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : fallback;
    private static double? ReadDoubleNullable(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : null;
}
