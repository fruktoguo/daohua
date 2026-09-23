using Domain.Player;
using Domain.GameData;
using Domain.Combat;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 协议快照映射器：将内部领域运行时状态投影为公开稳定的 Protocol DTO。
/// 严禁把 ECS Entity、Fiber、数据库模型或未授权内部异常直接暴露给网络客户端。
/// </summary>
public static class ProtocolSnapshotMapper
{
    /// <summary>对单个分段载荷执行规范化 JSON 序列化并生成 PlayerSectionDto。</summary>
    public static PlayerSectionDto EncodeSection(Guid playerId, string kind, long stateVersion, object value, DateTimeOffset updatedAt)
    {
        var encoded = SectionCodec.Encode(playerId, kind, Math.Max(1, stateVersion), value, updatedAt, compress: false);
        return new PlayerSectionDto(
            kind,
            encoded.StateVersion,
            encoded.SchemaVersion,
            encoded.Codec,
            Convert.ToBase64String(encoded.Payload.Span),
            encoded.RawBytes,
            encoded.StoredBytes,
            encoded.Checksum,
            encoded.UpdatedAt);
    }

    /// <summary>
    /// 将玩家运行时快照（PlayerRuntimeSnapshot）转换为公开的全量快照 DTO。
    /// 自动结合激活的 GameDataCatalog 透传地图与节点动态生态。
    /// </summary>
    public static PlayerSnapshotDto ToSnapshot(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog gameData)
    {
        var state = runtime.State;
        var sections = new List<PlayerSectionDto>(8);
        foreach (var (kind, value) in EnumerateSections(state, gameData))
        {
            // WebSocket v1 的浏览器载荷保持 JSON；Brotli 只用于 PostgreSQL section，避免
            // 客户端依赖不一致的 Brotli WASM/浏览器实现。
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
            Domain.Player.PlayerStateHead.CurrentSchemaVersion,
            runtime.LastSimulatedAt,
            gameDataFingerprint,
            checksum,
            DateTimeOffset.UtcNow);

        AssetBalanceDto[] assets;
        if (runtime.Assets.Count == 0)
        {
            assets = [];
        }
        else
        {
            assets = new AssetBalanceDto[runtime.Assets.Count];
            for (var i = 0; i < runtime.Assets.Count; i++)
            {
                var asset = runtime.Assets[i];
                assets[i] = new AssetBalanceDto(
                    asset.AssetId,
                    string.IsNullOrEmpty(asset.Variant) ? null : asset.Variant,
                    asset.Quantity,
                    asset.Version);
            }
        }

        return new PlayerSnapshotDto(
            runtime.PlayerId.ToString("D"),
            runtime.DisplayName,
            runtime.LastSimulatedAt,
            runtime.StateVersion,
            head,
            sections,
            assets);
    }

    /// <summary>构造增量快照 DTO（仅携带受影响的变更分段与资产变更）</summary>
    public static PlayerSnapshotDeltaDto ToDelta(
        PlayerRuntimeSnapshot runtime,
        string gameDataFingerprint,
        GameDataCatalog gameData,
        IReadOnlyDictionary<string, string>? previousSectionChecksums = null,
        IReadOnlyList<AssetBalanceDto>? previousAssets = null)
    {
        var snapshot = ToSnapshot(runtime, gameDataFingerprint, gameData);
        var changedSections = DiffSections(previousSectionChecksums, snapshot.Sections);
        var changedAssets = DiffAssets(previousAssets, snapshot.Assets);
        return new PlayerSnapshotDeltaDto(
            snapshot.PlayerId,
            snapshot.StateVersion,
            snapshot.LastSimulatedAt,
            changedSections,
            changedAssets.Count == 0 ? null : changedAssets);
    }

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

    private static IEnumerable<(string Kind, object Value)> EnumerateSections(PlayerAggregateState state, GameDataCatalog? gameData)
    {
        yield return (PlayerSectionKinds.Progression, new
        {
            state.Progression.RealmLevel, state.Progression.Qi, state.Progression.Experience, state.Progression.LifetimeQi,
            state.Progression.CompletedActions, state.Progression.SimulationCarrySeconds,
            state.Progression.NormalKills, state.Progression.EliteKills, state.Progression.BossKills,
            state.Progression.TotalDefeats, state.Progression.CultivationSeconds, state.Progression.AgeSeconds,
            lifespanYears = gameData?.RealmProgression is { } realm && gameData.BaseAttributes is { } baseAttributes
                ? realm.LifespanYearsFor(baseAttributes.TierAt(state.Progression.RealmLevel))
                : 0
        });
        yield return (PlayerSectionKinds.World, ToWorldSection(state.World, gameData));
        yield return (PlayerSectionKinds.Combat, ToCombatSection(state, gameData ?? throw new InvalidOperationException("协议映射必须提供激活配表。")));
        // Buff 通道唯一形状：ActiveBuff 的平铺权威字段 + 通用属性比率字典（statRatios），
        // 前端只做展示渲染，所有收益结算均在服务端领域层完成。
        yield return (PlayerSectionKinds.Buffs, state.Buffs.Select(buff => new
        {
            buff.BuffId, buff.Name, buff.Stacks, buff.ExpiresAtUnixSeconds, buff.SourceItemId,
            buff.HpRegen, buff.MpRegen, buff.AttackSpeedPoints, buff.DamageAbsorptionPoints, buff.StatRatios
        }).ToArray());
        yield return (PlayerSectionKinds.Loadout, ToLoadoutSection(state));
        yield return (PlayerSectionKinds.Settings, new { state.Settings, state.Presence });
        yield return (PlayerSectionKinds.ActionQueue, ToActionQueueSection(state.Actions, gameData));
        // 斗法分段白名单：ActiveReports（在队行动实例账本）不面向客户端，协议只下发
        // 当前账本/归档/离线/每日四类已消费通道。
        yield return (PlayerSectionKinds.Duel, new
        {
            state.Duel.EncounterCharges,
            state.Duel.CooldownUntilUnixSeconds,
            state.Duel.ActiveDuelId,
            state.Duel.StagedItems,
            state.Duel.CurrentReport,
            state.Duel.Reports,
            state.Duel.OfflineReport,
            state.Duel.DailyReport
        });
    }

    private static object ToLoadoutSection(PlayerAggregateState state) => new
    {
        state.Loadout.CoreAttributes,
        state.Loadout.AbilitySlots,
        state.Loadout.Equipment,
        learnedAbilityIds = state.LearnedAbilities.Select(id => id.Value).ToArray(),
        abilityExperiences = state.Loadout.AbilityExperiences ?? new Dictionary<string, double>()
    };

    /// <summary>
    /// 统一战斗属性投影：玩家、妖兽与斗法镜像共用同一字段集与语义。
    /// 全部百分比属性按点数下发（100 点 = 100%，总纲第七篇 §7.7.5），前端只按对应曲线渲染不得二次换算。
    /// </summary>
    public static object ToAttributesDto(PlayerAttributes a) => new
    {
        a.MaxHitpoints, a.MaxManapoints,
        physicalAttack = a.PhysicalAttack, a.SpellAttack,
        a.Armor, a.MagicResistance,
        a.Accuracy, a.Evasion,
        critical = a.Critical, criticalResistance = a.CriticalAvoidance,
        criticalMultiplier = a.CriticalMultiplier,
        breakPower = a.BreakPower, parryResolve = a.Parry,
        hpRegenPerFiveSeconds = a.HpRegen, mpRegenPerFiveSeconds = a.MpRegen,
        attackIntervalMilliseconds = a.AttackIntervalSeconds * 1000,
        basicAttackRatio = a.BasicAttackRatio, a.SkillIntensity,
        a.CooldownFactor, a.CastTimeFactor,
        a.PhysicalDamageBonusPoints, a.SpellDamageBonusPoints,
        a.MetalDamageBonusPoints, a.WoodDamageBonusPoints, a.WaterDamageBonusPoints,
        a.FireDamageBonusPoints, a.EarthDamageBonusPoints,
        a.TotalDamageBonusPoints, a.BasicAttackBonusPoints, a.SkillBonusPoints,
        a.PhysicalDamageReductionPoints, a.SpellDamageReductionPoints,
        a.MetalDamageReductionPoints, a.WoodDamageReductionPoints, a.WaterDamageReductionPoints,
        a.FireDamageReductionPoints, a.EarthDamageReductionPoints,
        a.DamageAbsorptionPoints,
        a.AttackSpeedPoints, a.ActionSpeedPoints, a.CooldownReductionPoints,
        a.ThreatBonusPoints,
        hpDrainPoints = a.Effects.HpDrainPoints, mpDrainPoints = a.Effects.MpDrainPoints,
        damageReflectPoints = a.Effects.DamageReflectPoints, healingBonusPoints = a.Effects.HealingBonusPoints
    };

    /// <summary>战斗单位静态属性（怪物/斗法镜像）到统一属性投影的重载。</summary>
    public static object ToAttributesDto(CombatantStats s) => new
    {
        s.MaxHitpoints, s.MaxManapoints,
        physicalAttack = s.AttackPower, s.SpellAttack,
        armor = s.Defense, s.MagicResistance,
        s.Accuracy, s.Evasion,
        critical = s.Critical, criticalResistance = s.CriticalResistance,
        criticalMultiplier = s.CriticalMultiplier,
        s.BreakPower, s.ParryResolve,
        hpRegenPerFiveSeconds = s.HpRegen, mpRegenPerFiveSeconds = s.MpRegen,
        attackIntervalMilliseconds = s.AttackIntervalSeconds * 1000,
        s.BasicAttackRatio, s.SkillIntensity,
        s.CooldownFactor, s.CastTimeFactor,
        s.PhysicalDamageBonusPoints, s.SpellDamageBonusPoints,
        s.MetalDamageBonusPoints, s.WoodDamageBonusPoints, s.WaterDamageBonusPoints,
        s.FireDamageBonusPoints, s.EarthDamageBonusPoints,
        s.TotalDamageBonusPoints, s.BasicAttackBonusPoints, s.SkillBonusPoints,
        s.PhysicalDamageReductionPoints, s.SpellDamageReductionPoints,
        s.MetalDamageReductionPoints, s.WoodDamageReductionPoints, s.WaterDamageReductionPoints,
        s.FireDamageReductionPoints, s.EarthDamageReductionPoints,
        s.DamageAbsorptionPoints,
        s.AttackSpeedPoints, s.ActionSpeedPoints, s.CooldownReductionPoints,
        s.ThreatBonusPoints,
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
            if (slot.ActionType == "combat" && slot.SlotIndex <= AutoUseSlotState.MaxSlotIndex)
            {
                var index = slot.Group == AutoUseSlotState.SlotGroupPotion
                    ? slot.SlotIndex
                    : slot.SlotIndex + AutoUseSlotState.MaxSlotIndex + 1;
                consumableCooldowns[index] = Math.Max(0, (slot.CooldownUntilUnixSeconds - nowUnix) * 1000);
            }
        }
        // 吟唱总时长 = 配表吟唱毫秒 × 施法时间乘区，仅在吟唱中下发；前端行动条不得回读配表推算。
        double? castTotalMilliseconds = null;
        if (state.Combat.Skills.CastingAbilityId is { } castingId
            && catalog.Abilities.TryGetValue(castingId, out var castingAbility)
            && castingAbility.TryGetProperty("castMilliseconds", out var castProperty)
            && castProperty.TryGetDouble(out var castMilliseconds))
            castTotalMilliseconds = castMilliseconds * a.CastTimeFactor
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
            attributes = ToAttributesDto(a)
        };
    }

    /// <summary>
    /// 把行动队列分段投影为协议匿名对象数组：在队列状态之上补充配表解析出的
    /// 名称、类型、时长与执行种类；travel 行动的时长与类型以 TravelState 为准。
    /// </summary>
    /// <param name="actions">聚合当前的行动队列。</param>
    /// <param name="gameData">激活配表；为 null 时仅输出队列原始字段（名称等降级为 null）。</param>
    public static object[] ToActionQueueSection(IReadOnlyList<QueuedActionState> actions, GameDataCatalog? gameData)
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
            var durationSeconds = action.Travel is not null
                ? action.Travel.TotalDurationSeconds
                : (def?.DurationSeconds ?? 0);
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
                status = (byte)action.Status,
                progressSeconds = action.ProgressSeconds,
                durationSeconds,
                remainingRepeats = action.RemainingRepeats,
                executionKind,
                combatEncounterId = def?.CombatEncounterId ?? action.Expedition?.EncounterId,
                expedition = action.Expedition is null ? null : ToExpeditionDto(action.Expedition, gameData),
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
    /// 敌方条目携带完整权威属性（名称/品阶/立绘/属性面板），前端不再反查怪物目录。
    /// </summary>
    private static object ToExpeditionDto(CombatExpeditionState expedition, GameDataCatalog? gameData) => new
    {
        expedition.EncounterId,
        phase = MapExpeditionPhase(expedition.Phase),
        expedition.PhaseElapsedSeconds, expedition.PhaseDurationSeconds,
        expedition.BattleElapsedSeconds,
        expedition.PlayerHitpoints, expedition.PlayerManapoints,
        expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount, expedition.PlayerThreat,
        expedition.WaveNumber,
        outcome = (byte)expedition.Outcome,
        expedition.Seed,
        // 倒地窗口（原地复活遭遇）：复活截止点与读条时长（战斗时钟秒），前端据此渲染阵亡置灰与复活读条。
        playerRespawnAtSeconds = expedition.PlayerRespawnAtSeconds,
        playerRespawnDurationSeconds = expedition.PlayerRespawnAtSeconds is not null
            && gameData?.CombatEncounters.TryGetValue(expedition.EncounterId, out var respawnEncounter) == true
                ? respawnEncounter.PlayerRespawnSeconds : (double?)null,
        enemies = (expedition.Enemies ?? []).Select(enemy => ToExpeditionEnemyDto(enemy, gameData)).ToArray(),
        duel = expedition.Duel is null ? null : ToDuelPlaybackDto(expedition.Duel)
    };

    private static object ToExpeditionEnemyDto(ExpeditionEnemyState enemy, GameDataCatalog? gameData)
    {
        var definition = gameData is null ? null : gameData.CombatMonsters.GetValueOrDefault(enemy.EnemyId);
        var found = definition is not null;
        JsonElement? visual = null;
        if (gameData is not null
            && gameData.Monsters.TryGetValue(enemy.EnemyId, out var raw)
            && raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("visual", out var visualElement)
            && visualElement.ValueKind == JsonValueKind.Object)
            visual = visualElement.Clone();
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
            enemy.State.NextAttackAtSeconds, enemy.State.AttackCount, enemy.State.Threat,
            enemy.PendingAbilityId, enemy.PendingActionDurationSeconds,
            attributes = found ? ToAttributesDto(definition!.Stats) : null
        };
    }

    /// <summary>斗法回放白名单投影：对手完整战斗属性与资源、事件级 MP 快照随剧本下发。</summary>
    private static object ToDuelPlaybackDto(DuelPlaybackState duel) => new
    {
        duel.DuelId,
        duel.OpponentPlayerId,
        duel.OpponentName,
        duel.ViewerIsInitiator,
        viewerOutcome = (byte)duel.ViewerOutcome,
        duel.DurationSeconds,
        duel.OpponentHitpoints,
        duel.OpponentManapoints,
        opponentAttributes = ToAttributesDto(duel.OpponentStats),
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
            duelEvent.TargetHitpointsAfter,
            duelEvent.AbilityId,
            duelEvent.Healing,
            duelEvent.AttackerManapointsAfter,
            duelEvent.TargetManapointsAfter
        }).ToArray()
    };

    private static object ToWorldSection(PlayerWorldStateDocument world, GameDataCatalog? gameData)
    {
        var hasMap = false;
        var hasNode = false;
        var map = default(JsonElement);
        var node = default(JsonElement);

        if (gameData?.Maps is not null)
        {
            for (var i = 0; i < gameData.Maps.Count; i++)
            {
                var candidate = gameData.Maps[i];
                if (candidate.ValueKind == JsonValueKind.Object
                    && candidate.TryGetProperty("nodes", out var candidateNodes)
                    && candidateNodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in candidateNodes.EnumerateArray())
                    {
                        if (ReadString(n, "id") == world.NodeId)
                        {
                            map = candidate;
                            hasMap = true;
                            node = n;
                            hasNode = true;
                            break;
                        }
                    }
                    if (hasNode) break;
                }
            }
        }

        var edgeId = world.EdgeId;
        var edgeProgress = world.EdgeProgress;
        var x = !string.IsNullOrWhiteSpace(edgeId) ? world.X : (hasNode ? ReadDouble(node, "x", world.X) : world.X);
        var y = !string.IsNullOrWhiteSpace(edgeId) ? world.Y : (hasNode ? ReadDouble(node, "y", world.Y) : world.Y);

        // 节点生态：遭遇池弱引用解析出栖息生灵与危险度区间，供 NearbyPanel 直接消费。
        var onEdge = !string.IsNullOrWhiteSpace(edgeId);
        var encounterIds = hasNode && !onEdge ? ReadStringArray(node, "encounterIds") : Array.Empty<string>();
        var nodeEncounters = gameData is null
            ? Array.Empty<JsonElement>()
            : encounterIds
                .Select(id => gameData.Encounters.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && ReadString(e, "id") == id))
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .ToArray();
        var normalIds = new List<string>();
        var eliteIds = new List<string>();
        var bossIds = new List<string>();
        foreach (var e in nodeEncounters)
        {
            normalIds.AddRange(ReadArray(e, "randomSpawns").Select(s => ReadString(s, "enemyId")));
            normalIds.AddRange(ReadStringArray(e, "normalEnemyIds"));
            eliteIds.AddRange(ReadStringArray(e, "eliteEnemyIds"));
            bossIds.AddRange(ReadStringArray(e, "bossEnemyIds"));
        }
        var minBudget = nodeEncounters.Select(e => ReadDouble(e, "minBudget", double.NaN))
            .Where(double.IsFinite).DefaultIfEmpty().Min();
        var maxBudget = nodeEncounters.Select(e => ReadDouble(e, "maxBudget", double.NaN))
            .Where(double.IsFinite).DefaultIfEmpty().Max();
        var eliteInterval = nodeEncounters.Select(e => ReadDouble(e, "battlesPerElite", ReadDouble(e, "eliteInterval", double.NaN)))
            .Where(double.IsFinite).DefaultIfEmpty().Min();
        var bossInterval = nodeEncounters.Select(e => ReadDouble(e, "battlesPerBoss", ReadDouble(e, "bossInterval", double.NaN)))
            .Where(double.IsFinite).DefaultIfEmpty().Min();
        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        return new
        {
            mapId = hasMap ? ReadString(map, "id", "map.qingyun-mountains") : "map.qingyun-mountains",
            nodeId = !string.IsNullOrWhiteSpace(edgeId) ? null : world.NodeId,
            edgeId,
            edgeProgress,
            x,
            y,
            locationName = !string.IsNullOrWhiteSpace(edgeId)
                ? "旅途中"
                : (hasNode ? ReadString(node, "name", world.NodeId) : world.NodeId),
            cultivationMultiplier = hasNode ? ReadDouble(node, "cultivationMultiplier", 1) : 1d,
            availableActionIds = !string.IsNullOrWhiteSpace(edgeId) ? Array.Empty<string>() : ReadStringArray(node, "availableActionIds"),
            encounterIds,
            npcs = !string.IsNullOrWhiteSpace(edgeId) ? Array.Empty<object>() : ReadEntities(node, "npcs"),
            buildings = !string.IsNullOrWhiteSpace(edgeId) ? Array.Empty<object>() : ReadEntities(node, "buildings"),
            // 节点可建列表：buildableList/buildableTemplate 二选一解析；null 表示该节点不可建（前端隐藏加号卡）。
            buildableBuildingIds = onEdge || !hasNode
                ? null
                : Domain.World.NodeBuildingRules.ResolveBuildableIds(node, gameData?.Buildings ?? new Dictionary<string, BuildingDefinition>(StringComparer.Ordinal))
                    ?.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            // 节点公共建筑：共享注册表投影随 world 分段持久化，快照映射按当前时刻推算双进度。
            nodeBuildings = onEdge
                ? Array.Empty<object>()
                : (world.NodeBuildings ?? [])
                    .Select(entry => ToNodeBuilding(gameData, entry, nowUnixSeconds))
                    .Where(entry => entry is not null)
                    .ToArray(),
            minBudget = double.IsFinite(minBudget) ? minBudget : (double?)null,
            maxBudget = double.IsFinite(maxBudget) ? maxBudget : (double?)null,
            eliteInterval = double.IsFinite(eliteInterval) ? eliteInterval : (double?)null,
            bossInterval = double.IsFinite(bossInterval) ? bossInterval : (double?)null,
            monsters = normalIds.Distinct(StringComparer.Ordinal).Select(id => ToNearbyMonster(gameData, id)).Where(m => m is not null).ToArray(),
            eliteEnemies = eliteIds.Distinct(StringComparer.Ordinal).Select(id => ToNearbyMonster(gameData, id)).Where(m => m is not null).ToArray(),
            bossEnemies = bossIds.Distinct(StringComparer.Ordinal).Select(id => ToNearbyMonster(gameData, id)).Where(m => m is not null).ToArray()
        };
    }

    /// <summary>
    /// 把节点公共建筑记录投影为附近面板消费的建筑 DTO：
    /// 含等级、建造中目标等级、双进度（材料/时间）与逐项材料需求-已交明细；配表缺失的定义直接丢弃。
    /// </summary>
    private static object? ToNodeBuilding(GameDataCatalog? gameData, Domain.World.NodeBuildingState state, double nowUnixSeconds)
    {
        if (gameData is null || !gameData.Buildings.TryGetValue(state.BuildingId, out var definition)) return null;
        var required = state.TargetLevel > 0
            ? Domain.World.NodeBuildingRules.RequiredMaterials(definition, state.TargetLevel)
            : new Dictionary<string, double>(StringComparer.Ordinal);
        var materialProgress = Domain.World.NodeBuildingRules.MaterialFraction(state, definition);
        // 工期条显示有效工时：材料不足时工程停滞，已流逝的墙钟时间不计入进度，封顶于材料进度。
        var timeProgress = Math.Min(Domain.World.NodeBuildingRules.TimeFraction(state, nowUnixSeconds), materialProgress);
        return new
        {
            entityId = definition.Id,
            name = definition.Name,
            kind = "building",
            description = definition.Description,
            effect = definition.Effect,
            group = definition.Group,
            level = Domain.World.NodeBuildingRules.EffectiveLevel(state, definition, nowUnixSeconds),
            targetLevel = state.TargetLevel,
            buildProgress = Domain.World.NodeBuildingRules.BuildProgress(state, definition, nowUnixSeconds),
            materialProgress,
            timeProgress,
            requiredSeconds = state.TargetLevel > 0 ? Domain.World.NodeBuildingRules.RequiredBuildSeconds(state.TargetLevel) : 0,
            elapsedSeconds = state.TargetLevel > 0 ? Math.Max(0, nowUnixSeconds - state.StartedAtUnixSeconds) : 0,
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
