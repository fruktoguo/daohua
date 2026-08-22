// ==============================================================================
// 文件名称: StateMapper.cs
// 主要职责: 玩家领域模型与网络传输协议 DTO 之间的投影转换映射
// 核心逻辑 / 定义内容:
//   1. 将权威领域玩家状态（PlayerState）完整投影为全量快照 DTO。
//   2. 基于脏区位标记（DirtySections）将局部变更打包为轻量级增量补丁 DTO。
//   3. 提供游戏静态配置、图鉴数据与各类子模块状态（背包、属性、战斗、Buff 等）的投影转换。
// ==============================================================================

using Domain.Models;
using Domain.Simulation;
using Protocol;

namespace Server.Runtime;

/// <summary>
/// 领域模型与网络协议 DTO 转换映射器 (StateMapper)。
/// <para>
/// 职责：
/// 1. 将服务端的权威 ECS/领域玩家状态 (<see cref="PlayerState"/>) 投影为用于客户端初始化/全量同步的 <see cref="PlayerSnapshotDto"/>。
/// 2. 基于脏区位标记 (<see cref="DirtySections"/>) 将局部变化精确打包为轻量级增量补丁 <see cref="PlayerPatchDto"/>。
/// 3. 将游戏静态配置与图鉴（行动、物品、技能、装备、历练、地图等）投影为协议 DTO。
/// </para>
/// </summary>
public static class StateMapper
{
    /// <summary>
    /// 将玩家领域状态投影为完整的全量快照 DTO。
    /// </summary>
    public static PlayerSnapshotDto ToSnapshot(PlayerState state, DateTimeOffset now)
    {
        return new PlayerSnapshotDto(
            state.PlayerId.ToString("D"),
            state.Name,
            state.Version,
            now.ToUnixTimeSeconds(),
            ToWire(state.PresenceMode),
            ToQueue(state),
            ToInventory(state.Inventory),
            ToBuffs(state.Buffs),
            ToStats(state.Stats),
            ToLogEvents(state.StructuredEventLog),
            ToCombat(state.Combat),
            ToWorld(state.World),
            ToCombatResources(state, now),
            ToCombatAttributes(state, now),
            ToVisual(state.Visual),
            now.ToUnixTimeMilliseconds(),
            state.IsPendingBreakthrough,
            state.Account,
            state.SequenceId,
            state.CreatedAt.ToUnixTimeSeconds(),
            CalculateRenameCooldown(state, now));
    }

    /// <summary>
    /// 根据脏区位标记将变更部分投影为增量补丁 DTO。
    /// </summary>
    public static PlayerPatchDto ToPatch(
        PlayerState state,
        DirtySections sections,
        DateTimeOffset now)
    {
        return new PlayerPatchDto(
            state.PlayerId.ToString("D"),
            state.Version,
            now.ToUnixTimeSeconds(),
            sections.HasFlag(DirtySections.Presence) ? ToWire(state.PresenceMode) : null,
            sections.HasFlag(DirtySections.Queue) ? ToQueue(state) : null,
            sections.HasFlag(DirtySections.Inventory) ? ToInventory(state.Inventory) : null,
            sections.HasFlag(DirtySections.Buffs) ? ToBuffs(state.Buffs) : null,
            sections.HasFlag(DirtySections.Stats) ? ToStats(state.Stats) : null,
            sections.HasFlag(DirtySections.QueueProgress)
                && !sections.HasFlag(DirtySections.Queue)
                    ? ToQueueProgress(state)
                    : null,
            sections.HasFlag(DirtySections.Events)
                ? ToLogEvents(state.PendingEvents)
                : null,
            sections.HasFlag(DirtySections.Combat) ? ToCombat(state.Combat) : null,
            sections.HasFlag(DirtySections.World) ? ToWorld(state.World) : null,
            sections.HasFlag(DirtySections.Combat) ? ToCombatResources(state, now) : null,
            sections.HasFlag(DirtySections.Combat) || sections.HasFlag(DirtySections.Buffs)
                ? ToCombatAttributes(state, now)
                : null,
            sections.HasFlag(DirtySections.Combat) ? ToVisual(state.Visual) : null,
            now.ToUnixTimeMilliseconds(),
            sections.HasFlag(DirtySections.Stats) ? state.IsPendingBreakthrough : null,
            sections.HasFlag(DirtySections.Stats) ? state.Name : null,
            sections.HasFlag(DirtySections.Stats) ? state.Account : null,
            sections.HasFlag(DirtySections.Stats) ? state.SequenceId : null,
            sections.HasFlag(DirtySections.Stats) ? state.CreatedAt.ToUnixTimeSeconds() : null,
            sections.HasFlag(DirtySections.Stats) ? CalculateRenameCooldown(state, now) : null);
    }

    /// <summary>
    /// 计算改名剩余冷却时间（秒）。
    /// </summary>
    public static long CalculateRenameCooldown(PlayerState state, DateTimeOffset now)
    {
        if (!state.LastRenamedAt.HasValue) return 0;
        var remaining = (state.LastRenamedAt.Value.AddHours(24) - now).TotalSeconds;
        return remaining > 0 ? (long)Math.Ceiling(remaining) : 0;
    }

    /// <summary>
    /// 将行动配置投影为行动 DTO。
    /// </summary>
    public static ActionDto ToAction(ActionDefinition definition)
    {
        return new ActionDto(
            definition.Id,
            definition.Name,
            definition.Description,
            ToWire(definition.Type),
            definition.DurationSeconds,
            ToWire(definition.ExecutionKind),
            definition.CombatEncounterId,
            definition.Inputs,
            definition.Outputs,
            definition.AvailableNodeIds.OrderBy(nodeId => nodeId, StringComparer.Ordinal).ToArray());
    }

    public static ItemDefinitionDto ToItemDefinition(ItemDefinition definition)
    {
        var consumable = definition.Consumable;
        return new ItemDefinitionDto(
            definition.Id,
            definition.Name,
            definition.Description,
            ToWire(definition.Kind),
            ToWire(definition.Category),
            definition.Tier,
            definition.SellPrice,
            definition.IsTradable,
            consumable?.UsableInActionTypes.Select(ToWire).OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [],
            consumable?.CooldownSeconds ?? 0,
            consumable?.RecoverySeconds ?? 0,
            consumable?.HitpointRestore ?? 0,
            consumable?.ManapointRestore ?? 0,
            consumable?.TriggerHpRatio ?? 0.6,
            consumable?.TriggerAtCombatStart ?? false,
            consumable?.DefaultCombatTriggers.Select(ToTrigger).ToArray(),
            consumable?.Buffs.Select(buff => new ItemBuffEffectDto(
                buff.Name,
                buff.DurationSeconds,
                buff.MaxStacks,
                buff.QiGainMultiplier,
                buff.AttackLevelRatio,
                buff.DamageRatio,
                buff.AttackSpeedRatio,
                buff.GatheringOutputMultiplier,
                buff.AlchemyOutputMultiplier)).ToArray());
    }

    public static CombatAbilityDto ToCombatAbility(CombatAbilityDefinition definition)
    {
        return new CombatAbilityDto(
            definition.Id,
            definition.Name,
            ToWire(definition.Style),
            ToWire(definition.DamageType),
            definition.CooldownMilliseconds,
            definition.CastMilliseconds,
            definition.ManaCost,
            definition.DamageRatio,
            definition.FlatDamage,
            definition.PenetrationRatio,
            definition.TriggerHpRatio,
            ToWire(definition.EffectType),
            definition.HealingRatio,
            definition.FlatHealing,
            definition.DefaultTriggers.Select(ToTrigger).ToArray(),
            definition.DamageOverTimeRatio,
            definition.DamageOverTimeMilliseconds,
            definition.DamageOverTimeTickMilliseconds,
            definition.StunChance,
            definition.StunMilliseconds,
            definition.BlindChance,
            definition.BlindMilliseconds,
            definition.SilenceChance,
            definition.SilenceMilliseconds,
            definition.DamageRatioLevelBonus,
            definition.FlatDamageLevelBonus,
            definition.HealingRatioLevelBonus,
            definition.FlatHealingLevelBonus,
            ToWire(definition.TargetType),
            definition.PierceChance,
            definition.ArmorDamageRatio,
            definition.HpDrainRatio);
    }

    public static CombatEquipmentDto ToCombatEquipment(CombatEquipmentDefinition definition)
    {
        return new CombatEquipmentDto(
            definition.Id,
            definition.Name,
            ToWire(definition.Slot),
            definition.Tier,
            ToEquipmentStats(definition.Stats),
            ToEquipmentStats(definition.EnhancementBonus));
    }

    public static CombatEncounterDto ToCombatEncounter(CombatEncounterDefinition definition)
    {
        return new CombatEncounterDto(
            definition.Id,
            definition.Name,
            ToCombatant(definition.Enemy),
            definition.RandomSpawns
                .Select(spawn => new CombatSpawnDto(
                    ToCombatant(spawn.Enemy),
                    spawn.Weight,
                    spawn.Strength))
                .ToArray(),
            definition.MaxSpawnCount,
            definition.MaxTotalStrength,
            definition.BossEnemies.Select(ToCombatant).ToArray(),
            definition.BattlesPerBoss,
            definition.SearchMilliseconds,
            definition.RespawnMilliseconds,
            definition.PlayerRespawnMilliseconds);
    }

    private static NearbyMonsterDto ToNearbyMonster(CombatantDefinition definition) => new(
        definition.Id,
        definition.Name,
        (int)definition.AttackLevel,
        definition.Tier,
        ToWire(definition.Rank),
        definition.BudgetCost,
        definition.MaxHitpoints,
        definition.MaxDamage,
        definition.Armor,
        definition.DropTable.Select(d => d.ItemId).Distinct().ToArray(),
        definition.Visual?.Source,
        definition.Visual?.Renderer.ToString().ToLowerInvariant());

    private static (double MinBudget, double MaxBudget, int EliteInterval, int BossInterval, IReadOnlyList<NearbyMonsterDto> Monsters, IReadOnlyList<NearbyMonsterDto> EliteEnemies, IReadOnlyList<NearbyMonsterDto> BossEnemies) ResolveNodeCombatEcologies(WorldNodeDefinition node)
    {
        var encounters = node.EncounterIds
            .Where(id => CombatCatalog.TryGetEncounter(id, out _))
            .Select(CombatCatalog.GetEncounter)
            .ToArray();
        if (encounters.Length == 0)
        {
            return (0, 0, 5, 20, [], [], []);
        }

        var minBudget = encounters.Min(e => e.MinBudget);
        var maxBudget = encounters.Max(e => e.MaxBudget);
        var eliteInterval = encounters.Min(e => e.EliteInterval);
        var bossInterval = encounters.Min(e => e.BattlesPerBoss);
        var monsters = encounters
            .SelectMany(e => e.RandomSpawns.Select(s => s.Enemy))
            .DistinctBy(e => e.Id)
            .Select(ToNearbyMonster)
            .ToArray();
        var eliteEnemies = encounters
            .SelectMany(e => e.EliteEnemies)
            .DistinctBy(e => e.Id)
            .Select(ToNearbyMonster)
            .ToArray();
        var bossEnemies = encounters
            .SelectMany(e => e.BossEnemies)
            .DistinctBy(e => e.Id)
            .Select(ToNearbyMonster)
            .ToArray();

        return (minBudget, maxBudget, eliteInterval, bossInterval, monsters, eliteEnemies, bossEnemies);
    }

    public static WorldMapDto ToWorldMap(WorldMapDefinition definition)
    {
        return new WorldMapDto(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.BackgroundSeed,
            definition.Nodes.Select(node =>
            {
                var (minBudget, maxBudget, eliteInterval, bossInterval, monsters, eliteEnemies, bossEnemies) = ResolveNodeCombatEcologies(node);
                return new WorldNodeDto(
                    node.Id,
                    node.Name,
                    node.Description,
                    node.X,
                    node.Y,
                    node.CultivationMultiplier,
                    node.AvailableActionIds.ToArray(),
                    node.EncounterIds.ToArray(),
                    node.Npcs.Select(ToNearbyEntity).ToArray(),
                    node.Buildings.Select(ToNearbyEntity).ToArray(),
                    minBudget,
                    maxBudget,
                    eliteInterval,
                    bossInterval,
                    monsters,
                    eliteEnemies,
                    bossEnemies);
            }).ToArray(),
            definition.Edges.Select(edge => new WorldEdgeDto(
                edge.Id,
                edge.FromNodeId,
                edge.ToNodeId,
                edge.DurationSeconds)).ToArray());
    }

    private static CombatantDto ToCombatant(CombatantDefinition definition) => new(
        definition.Id,
        definition.Name,
        ToVisual(definition.Visual),
        ToWire(definition.Style),
        ToWire(definition.DamageType),
        definition.MaxHitpoints,
        definition.MaxManapoints,
        definition.AttackIntervalMilliseconds,
        definition.AccuracyRating,
        definition.EvasionRating,
        definition.MaxDamage,
        definition.Armor,
        definition.WaterResistance,
        definition.NatureResistance,
        definition.FireResistance,
        definition.Tenacity,
        definition.PierceChance,
        definition.CurseRatio,
        definition.FuryRatio,
        definition.WeakenRatio,
        definition.LifeStealRatio,
        definition.ArmorDamageRatio,
        definition.HpDrainRatio,
        definition.CriticalChance,
        definition.CriticalDamageRatio,
        definition.CriticalAvoidance,
        definition.DamageAbsorptionRatio,
        definition.HpRegenPerTenSeconds,
        definition.MpRegenPerTenSeconds,
        definition.EnrageTimeMilliseconds,
        definition.Experience,
        definition.Abilities.Select(ability => ability.Id).ToArray());

    private static CharacterVisualDto ToVisual(CharacterVisualDefinition visual) => new(
        ToWire(visual.Renderer),
        visual.Source ?? string.Empty,
        visual.Poster);

    private static EquipmentCombatStatsDto ToEquipmentStats(EquipmentCombatStats stats) => new(
        stats.AccuracyRatio,
        stats.DamageRatio,
        stats.AttackSpeedRatio,
        stats.EvasionRatio,
        stats.Armor,
        stats.WaterResistance,
        stats.NatureResistance,
        stats.FireResistance,
        stats.Tenacity,
        stats.PenetrationRatio,
        stats.PierceChance,
        stats.CurseRatio,
        stats.FuryRatio,
        stats.WeakenRatio,
        stats.LifeStealRatio,
        stats.ArmorDamageRatio,
        stats.HpDrainRatio,
        stats.CriticalChance,
        stats.CriticalDamageRatio,
        stats.CriticalAvoidance,
        stats.DamageAbsorptionRatio,
        stats.AttackIntervalMilliseconds,
        stats.Style is null ? null : ToWire(stats.Style.Value),
        stats.DamageType is null ? null : ToWire(stats.DamageType.Value));

    private static QueueDto ToQueue(PlayerState state)
    {
        var queue = state.Queue;
        var entries = queue.Entries.Select(entry =>
        {
            var definition = ActionCatalog.Get(entry.ActionId);
            var travelPlan = entry.TravelPlan;
            var actionName = travelPlan is null
                ? definition.Name
                : $"赶往{WorldCatalog.GetNode(travelPlan.MapId, travelPlan.DestinationNodeId).Name}";
            return new QueueEntryDto(
                entry.EntryId.ToString("D"),
                entry.ActionId,
                actionName,
                ToWire(definition.Type),
                ToWire(entry.Status),
                entry.ProgressSeconds,
                travelPlan?.TotalDurationSeconds ?? definition.DurationSeconds,
                entry.RemainingRepeats,
                ToWire(definition.ExecutionKind),
                travelPlan?.DestinationNodeId,
                travelPlan?.Segments.Select(segment => segment.EdgeId).ToArray(),
                ToExpeditionProgress(state, entry, definition));
        }).ToArray();

        var slots = queue.AutoUseSlots.ToDictionary(
            pair => ToWire(pair.Key),
            pair => (IReadOnlyList<AutoUseSlotDto>)pair.Value
                .Select(slot => new AutoUseSlotDto(
                    slot.ItemId,
                    slot.Quantity,
                    slot.Enabled,
                    (slot.Triggers ?? []).Select(ToTrigger).ToArray()))
                .ToArray(),
            StringComparer.Ordinal);
        return new QueueDto(entries, queue.LoopEnabled, slots);
    }

    private static QueueProgressDto? ToQueueProgress(PlayerState state)
    {
        var queue = state.Queue;
        if (queue.Entries.Count == 0) return null;
        var current = queue.Entries[0];
        var definition = ActionCatalog.Get(current.ActionId);
        return new QueueProgressDto(
            current.EntryId.ToString("D"),
            ToWire(current.Status),
            current.ProgressSeconds,
            current.DurationSeconds,
            ToExpeditionProgress(state, current, definition));
    }

    private static ExpeditionProgressDto? ToExpeditionProgress(
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition)
    {
        if (definition.ExecutionKind == ActionExecutionKind.Travel
            && current.TravelPlan is { } travelPlan)
        {
            var elapsed = Math.Clamp(current.ProgressSeconds, 0, travelPlan.TotalDurationSeconds);
            var duration = Math.Max(1, travelPlan.TotalDurationSeconds);
            var (travelledDistance, totalDistance) = GetTravelDistance(travelPlan, elapsed);
            return new ExpeditionProgressDto(
                "travel",
                Math.Clamp(elapsed / (double)duration, 0, 1),
                elapsed,
                duration,
                travelledDistance,
                totalDistance,
                Activity: state.World.NodeId is null ? state.World.EdgeId : state.World.NodeId);
        }

        if (definition.ExecutionKind != ActionExecutionKind.CombatEncounter)
        {
            return null;
        }

        var configuredEncounterId = definition.CombatEncounterId ?? throw new InvalidOperationException($"战斗行动 {definition.Id} 缺少 CombatEncounterId 配置。");
        var configuredEncounter = CombatCatalog.GetEncounter(configuredEncounterId);
        var expedition = current.CombatExpedition ?? new CombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, configuredEncounter.SearchMilliseconds)
        };
        var persistedResources = state.CombatResources;
        var projectedPlayer = CombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            state.LastSimulatedAt,
            persistedResources?.Initialized == true ? persistedResources.Hitpoints : null,
            persistedResources?.Initialized == true ? persistedResources.Manapoints : null,
            state.Stats.Spirit);

        if (expedition.Phase != CombatExpeditionPhase.Combat)
        {
            var phaseDuration = Math.Max(1, expedition.PhaseDurationMilliseconds);
            var phaseElapsed = Math.Clamp(expedition.PhaseElapsedMilliseconds, 0, phaseDuration);
            var phase = expedition.Phase switch
            {
                CombatExpeditionPhase.InitialSearch => "initial-search",
                _ => "search"
            };
            Dictionary<string, long>? nonCombatAbilityCooldowns = null;
            List<long>? nonCombatConsumableCooldowns = null;
            if (persistedResources is not null)
            {
                if (persistedResources.AbilityCooldowns.Count > 0)
                {
                    nonCombatAbilityCooldowns = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var (abilityId, readyAt) in persistedResources.AbilityCooldowns)
                    {
                        var remaining = Math.Max(0, (long)Math.Ceiling((readyAt - state.LastSimulatedAt).TotalMilliseconds));
                        if (remaining > 0)
                        {
                            nonCombatAbilityCooldowns[abilityId] = remaining;
                        }
                    }
                }
                if (persistedResources.ConsumableSlotCooldowns.Count > 0)
                {
                    nonCombatConsumableCooldowns = persistedResources.ConsumableSlotCooldowns
                        .Select(readyAt => Math.Max(0, (long)Math.Ceiling((readyAt - state.LastSimulatedAt).TotalMilliseconds)))
                        .ToList();
                }
            }
            return new ExpeditionProgressDto(
                phase,
                Math.Clamp(phaseElapsed / (double)phaseDuration, 0, 1),
                (int)Math.Floor(phaseElapsed / 1_000d),
                Math.Max(1, (int)Math.Ceiling(phaseDuration / 1_000d)),
                PlayerHitpoints: projectedPlayer.Hitpoints,
                PlayerMaxHitpoints: projectedPlayer.MaxHitpoints,
                PlayerManapoints: projectedPlayer.Manapoints,
                PlayerMaxManapoints: projectedPlayer.MaxManapoints,
                EnemyHitpoints: configuredEncounter.Enemy.MaxHitpoints,
                EnemyMaxHitpoints: configuredEncounter.Enemy.MaxHitpoints,
                EnemyManapoints: configuredEncounter.Enemy.MaxManapoints,
                EnemyMaxManapoints: configuredEncounter.Enemy.MaxManapoints,
                EnemyId: configuredEncounter.Enemy.Id,
                EnemyName: configuredEncounter.Enemy.Name,
                PlayerAction: null,
                BattleSequence: expedition.BattleSequence,
                AbilityCooldownMilliseconds: nonCombatAbilityCooldowns,
                ConsumableCooldownMilliseconds: nonCombatConsumableCooldowns,
                EncounterId: configuredEncounterId);
        }

        var snapshot = expedition.BattleSnapshot
            ?? throw new InvalidOperationException("历练处于战斗阶段但缺少 BattleSnapshot 战斗入场快照。");
        var combatEncounter = CombatCatalog.GetEncounter(snapshot.EncounterId);
        var result = CombatSimulator.Run(
            state.PlayerId,
            snapshot,
            TimeSpan.FromMilliseconds(Math.Max(0, expedition.PhaseElapsedMilliseconds)),
            state.Stats.Spirit,
            stopAfterFirstVictory: true);
        var player = result.FinalPlayer;
        var enemies = result.FinalEnemies ?? [];
        var activeEnemy = enemies.FirstOrDefault(enemy => !enemy.IsDefeated && enemy.Hitpoints > 0) ?? enemies.LastOrDefault();
        var enemyHitpoints = activeEnemy?.Hitpoints ?? 0;
        var enemyMaxHitpoints = activeEnemy?.MaxHitpoints ?? combatEncounter.Enemy.MaxHitpoints;
        var enemyManapoints = activeEnemy?.Manapoints ?? 0;
        var enemyMaxManapoints = activeEnemy?.MaxManapoints ?? combatEncounter.Enemy.MaxManapoints;
        return new ExpeditionProgressDto(
            "combat",
            0,
            (int)Math.Floor(expedition.PhaseElapsedMilliseconds / 1_000d),
            0,
            PlayerHitpoints: player?.Hitpoints,
            PlayerMaxHitpoints: player?.MaxHitpoints,
            PlayerManapoints: player?.Manapoints,
            PlayerMaxManapoints: player?.MaxManapoints,
            EnemyHitpoints: enemyHitpoints,
            EnemyMaxHitpoints: enemyMaxHitpoints,
            EnemyManapoints: enemyManapoints,
            EnemyMaxManapoints: enemyMaxManapoints,
            EnemyId: activeEnemy?.EnemyId ?? combatEncounter.Enemy.Id,
            EnemyName: activeEnemy?.Name ?? combatEncounter.Enemy.Name,
            PlayerAction: ToCombatAction(
                result.PlayerAction,
                snapshot.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds)),
            EnemyAction: ToCombatAction(
                result.EnemyAction,
                snapshot.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds)),
            BattleSequence: expedition.BattleSequence,
            AbilityCooldownMilliseconds: result.PlayerAbilityCooldowns,
            ConsumableCooldownMilliseconds: result.PlayerConsumableCooldowns,
            EncounterId: snapshot.EncounterId);
    }

    private static CombatResourceDto ToCombatResources(PlayerState state, DateTimeOffset now)
    {
        var persisted = state.CombatResources;
        var player = CombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            now,
            persisted?.Initialized == true ? persisted.Hitpoints : null,
            persisted?.Initialized == true ? persisted.Manapoints : null,
            state.Stats.Spirit);
        Dictionary<string, long>? abilityCooldowns = null;
        List<long>? consumableCooldowns = null;
        if (persisted is not null)
        {
            if (persisted.AbilityCooldowns.Count > 0)
            {
                abilityCooldowns = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (abilityId, readyAt) in persisted.AbilityCooldowns)
                {
                    var remaining = Math.Max(0, (long)Math.Ceiling((readyAt - now).TotalMilliseconds));
                    if (remaining > 0)
                    {
                        abilityCooldowns[abilityId] = remaining;
                    }
                }
            }
            if (persisted.ConsumableSlotCooldowns.Count > 0)
            {
                consumableCooldowns = persisted.ConsumableSlotCooldowns
                    .Select(readyAt => Math.Max(0, (long)Math.Ceiling((readyAt - now).TotalMilliseconds)))
                    .ToList();
            }
        }
        return new CombatResourceDto(
            player.Hitpoints,
            player.MaxHitpoints,
            player.Manapoints,
            player.MaxManapoints,
            abilityCooldowns,
            consumableCooldowns);
    }

    private static CombatAttributesDto ToCombatAttributes(PlayerState state, DateTimeOffset now)
    {
        var attributes = CombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, now, state.Stats.Spirit);
        return new CombatAttributesDto(
            attributes.MaxHitpoints,
            attributes.MaxManapoints,
            attributes.AttackIntervalMilliseconds,
            attributes.AccuracyRating,
            attributes.MaxDamage,
            attributes.EvasionRating,
            attributes.Armor,
            attributes.WaterResistance,
            attributes.NatureResistance,
            attributes.FireResistance,
            ToWire(attributes.Style),
            ToWire(attributes.DamageType),
            attributes.Tenacity,
            attributes.HpRegenPerTenSeconds,
            attributes.MpRegenPerTenSeconds,
            attributes.PenetrationRatio,
            attributes.PierceChance,
            attributes.CurseRatio,
            attributes.FuryRatio,
            attributes.WeakenRatio,
            attributes.LifeStealRatio,
            attributes.ArmorDamageRatio,
            attributes.HpDrainRatio,
            attributes.DamageTakenRatio,
            attributes.CriticalChance,
            attributes.CriticalDamageRatio,
            attributes.CriticalAvoidance,
            attributes.DamageAbsorptionRatio,
            attributes.CriticalRating,
            attributes.CriticalAvoidanceRating);
    }

    private static CombatActionStateDto? ToCombatAction(
        CombatActionState? action,
        DateTimeOffset simulatedAt)
    {
        if (action is null) return null;

        var remainingMilliseconds = Math.Max(0d, (double)action.DurationMilliseconds - action.ElapsedMilliseconds);
        var readyAtUnixMilliseconds = remainingMilliseconds <= 0
            ? simulatedAt.ToUnixTimeMilliseconds()
            : remainingMilliseconds > DateTimeOffset.MaxValue.Subtract(simulatedAt).TotalMilliseconds
                ? DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                : simulatedAt.AddMilliseconds(remainingMilliseconds).ToUnixTimeMilliseconds();

        return new CombatActionStateDto(
            action.Name,
            ToWire(action.Kind),
            action.ElapsedMilliseconds,
            action.DurationMilliseconds,
            readyAtUnixMilliseconds);
    }

    private static (double TravelledDistance, double TotalDistance) GetTravelDistance(
        TravelPlan plan,
        int elapsedSeconds)
    {
        var remainingSeconds = Math.Max(0, elapsedSeconds);
        var travelled = 0d;
        var total = 0d;
        foreach (var segment in plan.Segments)
        {
            var edge = WorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
            var from = WorldCatalog.GetNode(plan.MapId, edge.FromNodeId);
            var to = WorldCatalog.GetNode(plan.MapId, edge.ToNodeId);
            var edgeDistance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            var segmentDistance = edgeDistance * Math.Abs(segment.EndProgress - segment.StartProgress);
            total += segmentDistance;
            var segmentElapsed = Math.Clamp(remainingSeconds, 0, segment.DurationSeconds);
            travelled += segment.DurationSeconds <= 0
                ? segmentDistance
                : segmentDistance * segmentElapsed / segment.DurationSeconds;
            remainingSeconds = Math.Max(0, remainingSeconds - segment.DurationSeconds);
        }

        return (travelled, total);
    }

    private static IReadOnlyList<InventoryItemDto> ToInventory(InventoryState inventory)
    {
        return inventory.Items
            .Where(pair => pair.Value > 0)
            .Select(pair =>
            {
                var metadata = GetItemMetadata(pair.Key);
                return new InventoryItemDto(
                    pair.Key,
                    metadata.Name,
                    metadata.Category,
                    pair.Value,
                    metadata.Tone,
                    metadata.Glyph,
                    metadata.Tier,
                    metadata.Description,
                    metadata.IsTradable);
            })
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<BuffDto> ToBuffs(IReadOnlyList<BuffState> buffs)
    {
        return buffs.Select(buff => new BuffDto(
            buff.BuffId,
            buff.Name,
            buff.Stacks,
            buff.QiGainMultiplier,
            buff.ExpiresAt.ToUnixTimeSeconds(),
            buff.AttackLevelRatio,
            buff.DamageRatio,
            buff.AttackSpeedRatio,
            buff.GatheringOutputMultiplier,
            buff.AlchemyOutputMultiplier,
            buff.SourceItemId)).ToArray();
    }

    private static IReadOnlyList<LogEventDto> ToLogEvents(IReadOnlyList<LogEvent> events)
    {
        return events.Select(entry => new LogEventDto(
            entry.Sequence,
            entry.OccurredAt.ToUnixTimeMilliseconds(),
            entry.Template)).ToArray();
    }

    private static StatsDto ToStats(PlayerStats stats)
    {
        return new StatsDto(
            stats.Qi,
            stats.Spirit,
            stats.HerbsCollected,
            stats.PillsCrafted,
            stats.CultivationCycles,
            stats.CompletedActions,
            stats.BlockedActions,
            stats.CombatWins,
            stats.CombatLosses,
            stats.CombatExperience,
            stats.CombatDamageDealt,
            stats.CombatDamageTaken,
            stats.CombatEnemiesDefeated,
            stats.CombatPlayersDefeated,
            stats.FoundationExp);
    }

    private static CombatLoadoutDto ToCombat(CombatLoadout combat)
    {
        return new CombatLoadoutDto(
            ToWire(combat.Style),
            ToWire(combat.DamageType),
            combat.StaminaLevel,
            combat.IntelligenceLevel,
            combat.AttackLevel,
            combat.DefenseLevel,
            combat.SwordsmanshipLevel,
            combat.TalismanLevel,
            combat.AbilitySlots.Select(slot => new CombatAbilitySlotDto(
                slot.AbilityId,
                slot.Level,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.ConsumableSlots.Select(slot => new ConsumableSlotDto(
                slot.ItemId,
                slot.Quantity,
                slot.Enabled,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.CombatDropRate,
            combat.CombatDropQuantity,
            combat.CombatRareFind,
            combat.Tenacity,
            combat.SearchSpeedRatio,
            (combat.Equipment ?? []).Select(item => new EquippedItemDto(item.ItemId, item.EnhancementLevel)).ToArray(),
            combat.LearnedAbilityIds.ToArray());
    }

    private static WorldStateDto ToWorld(WorldState world)
    {
        var position = WorldCatalog.GetPosition(world);
        if (world.NodeId is not null)
        {
            var node = WorldCatalog.GetNode(world.MapId, world.NodeId);
            var (minBudget, maxBudget, eliteInterval, bossInterval, monsters, eliteEnemies, bossEnemies) = ResolveNodeCombatEcologies(node);
            return new WorldStateDto(
                world.MapId,
                node.Id,
                null,
                0,
                position.X,
                position.Y,
                node.Name,
                node.CultivationMultiplier,
                node.AvailableActionIds.ToArray(),
                node.EncounterIds.ToArray(),
                node.Npcs.Select(ToNearbyEntity).ToArray(),
                node.Buildings.Select(ToNearbyEntity).ToArray(),
                minBudget,
                maxBudget,
                eliteInterval,
                bossInterval,
                monsters,
                eliteEnemies,
                bossEnemies);
        }

        var edge = WorldCatalog.GetEdge(world.MapId, world.EdgeId!);
        var from = WorldCatalog.GetNode(world.MapId, edge.FromNodeId);
        var to = WorldCatalog.GetNode(world.MapId, edge.ToNodeId);
        return new WorldStateDto(
            world.MapId,
            null,
            edge.Id,
            world.EdgeProgress,
            position.X,
            position.Y,
            $"{from.Name}至{to.Name}途中",
            1,
            [],
            [],
            [],
            [],
            0,
            0,
            5,
            20,
            [],
            [],
            []);
    }

    private static NearbyEntityDto ToNearbyEntity(NearbyEntityDefinition definition) => new(
        definition.Id,
        definition.Name,
        ToWire(definition.Kind),
        definition.Description,
        definition.Effect);

    private static CombatTriggerDto ToTrigger(CombatTrigger trigger) => new(
        ToWire(trigger.Dependency),
        ToWire(trigger.Condition),
        ToWire(trigger.Comparator),
        trigger.Value,
        trigger.BuffId);

    private static (string Name, string Category, string Tone, string Glyph, int Tier, string Description, bool IsTradable)
        GetItemMetadata(string itemId)
    {
        if (!ItemCatalog.TryGet(itemId, out var definition))
        {
            return (itemId, "物品", "gold", "·", 1, "尚未登记详细信息。", false);
        }

        var tone = definition.Kind switch
        {
            ItemKind.Currency => "gold",
            ItemKind.Resource => "cyan",
            ItemKind.Consumable when definition.Tier >= 2 => "orange",
            ItemKind.Consumable => "violet",
            ItemKind.Material => "green",
            _ => "silver"
        };
        var glyph = definition.Kind switch
        {
            ItemKind.Currency => "◈",
            ItemKind.Resource => "◉",
            ItemKind.Consumable => "✺",
            ItemKind.Material => "✤",
            _ => "◇"
        };
        return (definition.Name, ToWire(definition.Category), tone, glyph, definition.Tier, definition.Description, definition.IsTradable);
    }

    private static string ToWire(ActionType type) => type switch
    {
        ActionType.Cultivation => "cultivation",
        ActionType.Exploration => "exploration",
        ActionType.Alchemy => "alchemy",
        ActionType.Combat => "combat",
        _ => "unknown"
    };

    private static string ToWire(ItemKind kind) => kind switch
    {
        ItemKind.Material => "material",
        ItemKind.Consumable => "consumable",
        ItemKind.Currency => "currency",
        ItemKind.Resource => "resource",
        ItemKind.Equipment => "equipment",
        _ => "unknown"
    };

    private static string ToWire(ActionExecutionKind kind) => kind switch
    {
        ActionExecutionKind.CombatEncounter => "combat-encounter",
        ActionExecutionKind.Travel => "travel",
        _ => "timed"
    };

    private static string ToWire(CombatStyle style) => style switch
    {
        CombatStyle.Sword => "sword",
        CombatStyle.Blade => "blade",
        CombatStyle.Talisman => "talisman",
        CombatStyle.FiveElements => "five-elements",
        CombatStyle.Body => "body",
        _ => "sword"
    };

    private static string ToWire(DamageType type) => type switch
    {
        DamageType.Physical => "physical",
        DamageType.Water => "water",
        DamageType.Nature => "nature",
        DamageType.Fire => "fire",
        _ => "physical"
    };

    private static string ToWire(CombatAbilityEffectType type) => type switch
    {
        CombatAbilityEffectType.Heal => "heal",
        _ => "damage"
    };

    private static string ToWire(CombatAbilityTargetType type) => type switch
    {
        CombatAbilityTargetType.AllEnemies => "all-enemies",
        CombatAbilityTargetType.Self => "self",
        CombatAbilityTargetType.AllAllies => "all-allies",
        CombatAbilityTargetType.LowestHitpointsAlly => "lowest-hp-ally",
        _ => "enemy"
    };

    private static string ToWire(CombatTriggerDependency dependency) => dependency switch
    {
        CombatTriggerDependency.TargetEnemy => "target-enemy",
        CombatTriggerDependency.AllyLowestHitpoints => "ally-lowest-hp",
        CombatTriggerDependency.AllyHighestHitpoints => "ally-highest-hp",
        CombatTriggerDependency.EnemyLowestHitpoints => "enemy-lowest-hp",
        CombatTriggerDependency.EnemyHighestHitpoints => "enemy-highest-hp",
        _ => "self"
    };

    private static string ToWire(CombatTriggerCondition condition) => condition switch
    {
        CombatTriggerCondition.CurrentManapoints => "current-mana",
        CombatTriggerCondition.MissingHitpoints => "missing-hp",
        CombatTriggerCondition.MissingManapoints => "missing-mana",
        CombatTriggerCondition.BuffStacks => "buff-stacks",
        CombatTriggerCondition.StunStatus => "stun-status",
        CombatTriggerCondition.BlindStatus => "blind-status",
        CombatTriggerCondition.SilenceStatus => "silence-status",
        CombatTriggerCondition.CurrentHitpointsPercentage => "current-hp-percentage",
        CombatTriggerCondition.MissingHitpointsPercentage => "missing-hp-percentage",
        CombatTriggerCondition.CurrentManapointsPercentage => "current-mana-percentage",
        CombatTriggerCondition.MissingManapointsPercentage => "missing-mana-percentage",
        _ => "current-hp"
    };

    private static string ToWire(CombatTriggerComparator comparator) => comparator switch
    {
        CombatTriggerComparator.LessThanOrEqual => "less-than-equal",
        CombatTriggerComparator.IsActive => "is-active",
        CombatTriggerComparator.IsInactive => "is-inactive",
        CombatTriggerComparator.LessThan => "less-than",
        CombatTriggerComparator.GreaterThan => "greater-than",
        _ => "greater-than-equal"
    };

    private static string ToWire(PresenceMode mode) => mode switch
    {
        PresenceMode.Online => "online",
        PresenceMode.IdleOffline => "idle-offline",
        PresenceMode.FullyOffline => "fully-offline",
        _ => "unknown"
    };

    private static string ToWire(ActionStatus status) => status switch
    {
        ActionStatus.Queued => "queued",
        ActionStatus.Running => "running",
        ActionStatus.Blocked => "blocked",
        _ => "unknown"
    };

    private static string ToWire(ItemCategory category) => category switch
    {
        ItemCategory.Material => "material",
        ItemCategory.Herb => "herb",
        ItemCategory.Mineral => "mineral",
        ItemCategory.Pill => "pill",
        ItemCategory.Food => "food",
        ItemCategory.Drink => "drink",
        ItemCategory.Talisman => "talisman",
        ItemCategory.Manual => "manual",
        ItemCategory.Equipment => "equipment",
        ItemCategory.Chest => "chest",
        ItemCategory.Currency => "currency",
        ItemCategory.Resource => "resource",
        _ => "general"
    };

    private static string ToWire(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.TwoHand => "two-hand",
        EquipmentSlot.OffHand => "off-hand",
        EquipmentSlot.Head => "head",
        EquipmentSlot.Armor => "armor",
        EquipmentSlot.Legs => "legs",
        EquipmentSlot.Boots => "boots",
        EquipmentSlot.Ring => "ring",
        EquipmentSlot.Neck => "neck",
        EquipmentSlot.Trinket => "trinket",
        EquipmentSlot.Talisman => "talisman",
        _ => "weapon"
    };

    private static string ToWire(VisualRendererKind renderer) => renderer switch
    {
        VisualRendererKind.Glyph => "glyph",
        VisualRendererKind.Image => "image",
        VisualRendererKind.Live2D => "live2d",
        VisualRendererKind.Model3D => "model3d",
        _ => "text"
    };

    private static string ToWire(CombatActionKind kind) => kind switch
    {
        CombatActionKind.Attack => "attack",
        CombatActionKind.Cast => "cast",
        CombatActionKind.Control => "control",
        CombatActionKind.Respawn => "respawn",
        _ => "wait"
    };

    private static string ToWire(NearbyEntityKind kind) => kind switch
    {
        NearbyEntityKind.Building => "building",
        NearbyEntityKind.Portal => "portal",
        NearbyEntityKind.ResourceNode => "resource-node",
        _ => "npc"
    };

    private static string ToWire(UnitRank rank) => rank switch
    {
        UnitRank.Boss => "boss",
        UnitRank.Leader => "leader",
        UnitRank.Elite => "elite",
        UnitRank.Summon => "summon",
        UnitRank.Pet => "pet",
        UnitRank.PlayerMirror => "player-mirror",
        UnitRank.Npc => "npc",
        UnitRank.Normal => "normal",
        _ => "minion"
    };

    public static string ToWire(MarketResultCode code) => code switch
    {
        MarketResultCode.Ok => "OK",
        MarketResultCode.InvalidRequest => "INVALID_REQUEST",
        MarketResultCode.OrderForbidden => "ORDER_FORBIDDEN",
        MarketResultCode.DuplicateOrderId => "DUPLICATE_ORDER_ID",
        MarketResultCode.OrderNotFound => "ORDER_NOT_FOUND",
        MarketResultCode.OrderNotActive => "ORDER_NOT_ACTIVE",
        MarketResultCode.InsufficientItem => "INSUFFICIENT_ITEM",
        MarketResultCode.InsufficientSpirit => "INSUFFICIENT_SPIRIT",
        MarketResultCode.OrderTooLarge => "ORDER_TOO_LARGE",
        MarketResultCode.InvalidOrderId => "INVALID_ORDER_ID",
        MarketResultCode.InvalidInstrument => "INVALID_INSTRUMENT",
        MarketResultCode.ItemNotTradable => "ITEM_NOT_TRADABLE",
        MarketResultCode.InvalidTaxPolicy => "INVALID_TAX_POLICY",
        MarketResultCode.InvalidVendorPrice => "INVALID_VENDOR_PRICE",
        MarketResultCode.PriceBelowVendor => "PRICE_BELOW_VENDOR",
        MarketResultCode.InvalidPriceIncrement => "INVALID_PRICE_INCREMENT",
        MarketResultCode.InvalidCreatedAt => "INVALID_CREATED_AT",
        _ => code.ToString()
    };
}
