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
        yield return (PlayerSectionKinds.Buffs, state.Buffs.Select(buff => new
        {
            buff.BuffId, buff.ExpiresAtUnixSeconds, buff.Stacks, buff.Name, buff.SourceItemId,
            qiGainMultiplier = 1d, attackLevelRatio = 0d, damageRatio = 0d, buff.AttackSpeedRatio,
            gatheringOutputMultiplier = 1d, alchemyOutputMultiplier = 1d,
            modifiers = new { buff.HpRegen, buff.MpRegen, buff.DamageAbsorptionRatio, buff.AttackSpeedRatio }
        }).ToArray());
        yield return (PlayerSectionKinds.Loadout, ToLoadoutSection(state));
        yield return (PlayerSectionKinds.Settings, new { state.Settings, state.Presence });
        yield return (PlayerSectionKinds.ActionQueue, ToActionQueueSection(state.Actions, gameData));
        yield return (PlayerSectionKinds.Duel, state.Duel);
    }

    private static object ToLoadoutSection(PlayerAggregateState state) => new
    {
        state.Loadout.CoreAttributes,
        state.Loadout.AbilitySlots,
        state.Loadout.Equipment,
        learnedAbilityIds = state.LearnedAbilities.Select(id => id.Value).ToArray(),
        abilityExperiences = state.Loadout.AbilityExperiences ?? new Dictionary<string, double>(),
        consumableSlots = (state.Settings.AutoUseSlots ?? []).Where(slot => slot.ActionType == "combat")
            .OrderBy(slot => slot.SlotIndex).Select(slot => new
            {
                slot.ItemId, slot.Quantity, slot.Enabled, triggers = Array.Empty<object>()
            }).ToArray()
    };

    private static object ToCombatSection(PlayerAggregateState state, GameDataCatalog catalog)
    {
        var a = PlayerAttributeResolver.Resolve(state, catalog);
        var nowUnix = state.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;
        // 消耗品冷却按槽位下标定长下发；剩余毫秒以快照模拟时刻为基准，供前端冷却遮罩倒计时。
        var consumableCooldowns = new double[AutoUseSlotState.MaxSlotIndex + 1];
        foreach (var slot in state.Settings.AutoUseSlots ?? [])
        {
            if (slot.ActionType == "combat" && slot.SlotIndex <= AutoUseSlotState.MaxSlotIndex)
                consumableCooldowns[slot.SlotIndex] = Math.Max(0, (slot.CooldownUntilUnixSeconds - nowUnix) * 1000);
        }
        return new
        {
            hitpoints = Math.Min(state.Combat.Hitpoints, a.MaxHitpoints), a.MaxHitpoints,
            manapoints = Math.Min(state.Combat.Manapoints, a.MaxManapoints), a.MaxManapoints,
            skills = new
            {
                cooldowns = state.Combat.Skills.Cooldowns.ToDictionary(
                    cooldown => cooldown.AbilityId,
                    cooldown => cooldown.RemainingSeconds * 1000,
                    StringComparer.Ordinal),
                castingAbilityId = state.Combat.Skills.CastingAbilityId,
                castRemainingMilliseconds = state.Combat.Skills.CastRemainingSeconds * 1000,
            },
            consumableCooldownMilliseconds = consumableCooldowns,
            attributes = new
            {
                a.MaxHitpoints, a.MaxManapoints, attackIntervalMilliseconds = a.AttackIntervalSeconds * 1000,
                accuracyRating = a.Accuracy, maxDamage = a.PhysicalAttack, evasionRating = a.Evasion,
                a.Armor, a.MagicResistance, a.MetalResistance, a.WoodResistance, a.WaterResistance,
                a.FireResistance, a.EarthResistance, style = "sword", category = "physical", element = "none",
                tenacity = 0d, hpRegenPerFiveSeconds = a.HpRegen, mpRegenPerFiveSeconds = a.MpRegen,
                penetrationRatio = 0d, lifeStealRatio = 0d, armorDamageRatio = 0d,
                hpDrainRatio = a.Effects.HpDrainRatio,
                damageTakenRatio = 0d, criticalChance = a.Critical / (a.Critical + 100),
                criticalDamageRatio = 1.5, criticalAvoidance = a.CriticalAvoidance / (a.CriticalAvoidance + 100),
                criticalRating = a.Critical, criticalAvoidanceRating = a.CriticalAvoidance, a.DamageAbsorptionRatio
            }
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

            result[i] = new
            {
                id = action.Id,
                intent = action.Intent,
                actionName,
                actionType,
                enqueuedAt = action.EnqueuedAt,
                status = (byte)action.Status,
                progressSeconds = action.ProgressSeconds,
                durationSeconds,
                remainingRepeats = action.RemainingRepeats,
                completedRepeats = action.CompletedRepeats,
                executionKind,
                combatEncounterId = def?.CombatEncounterId ?? action.Expedition?.EncounterId,
                expedition = action.Expedition,
                travel = action.Travel,
                travelDestinationNodeId = action.Travel?.TargetNodeId ?? action.Intent.TargetNodeId,
                travelPathEdgeIds = action.Travel?.PathEdgeIds,
                travelSegments = action.Travel?.Segments.Select(s => new
                {
                    edgeId = s.EdgeId,
                    startProgress = s.StartProgress,
                    endProgress = s.EndProgress,
                    durationSeconds = s.DurationSeconds
                })
            };
        }
        return result;
    }

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
            maxDamage = def?.Stats.AttackPower ?? 0,
            armor = def?.Stats.Defense ?? 0,
            drops = ReadArray(monster, "dropTable").Select(d => ReadString(d, "itemId")).Where(s => s.Length > 0).ToArray(),
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
