using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Daoxuan.Protocol;

namespace Daoxuan.Server.Runtime;

public static class DaoxuanStateMapper
{
    public static DaoxuanPlayerSnapshotDto ToSnapshot(DaoxuanPlayerState state, DateTimeOffset now)
    {
        return new DaoxuanPlayerSnapshotDto(
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

    public static DaoxuanPlayerPatchDto ToPatch(
        DaoxuanPlayerState state,
        DaoxuanDirtySections sections,
        DateTimeOffset now)
    {
        return new DaoxuanPlayerPatchDto(
            state.PlayerId.ToString("D"),
            state.Version,
            now.ToUnixTimeSeconds(),
            sections.HasFlag(DaoxuanDirtySections.Presence) ? ToWire(state.PresenceMode) : null,
            sections.HasFlag(DaoxuanDirtySections.Queue) ? ToQueue(state) : null,
            sections.HasFlag(DaoxuanDirtySections.Inventory) ? ToInventory(state.Inventory) : null,
            sections.HasFlag(DaoxuanDirtySections.Buffs) ? ToBuffs(state.Buffs) : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? ToStats(state.Stats) : null,
            sections.HasFlag(DaoxuanDirtySections.QueueProgress)
                && !sections.HasFlag(DaoxuanDirtySections.Queue)
                    ? ToQueueProgress(state)
                    : null,
            sections.HasFlag(DaoxuanDirtySections.Events)
                ? ToLogEvents(state.PendingEvents)
                : null,
            sections.HasFlag(DaoxuanDirtySections.Combat) ? ToCombat(state.Combat) : null,
            sections.HasFlag(DaoxuanDirtySections.World) ? ToWorld(state.World) : null,
            sections.HasFlag(DaoxuanDirtySections.Combat) ? ToCombatResources(state, now) : null,
            sections.HasFlag(DaoxuanDirtySections.Combat) || sections.HasFlag(DaoxuanDirtySections.Buffs)
                ? ToCombatAttributes(state, now)
                : null,
            sections.HasFlag(DaoxuanDirtySections.Combat) ? ToVisual(state.Visual) : null,
            now.ToUnixTimeMilliseconds(),
            sections.HasFlag(DaoxuanDirtySections.Stats) ? state.IsPendingBreakthrough : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? state.Name : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? state.Account : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? state.SequenceId : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? state.CreatedAt.ToUnixTimeSeconds() : null,
            sections.HasFlag(DaoxuanDirtySections.Stats) ? CalculateRenameCooldown(state, now) : null);
    }

    public static long CalculateRenameCooldown(DaoxuanPlayerState state, DateTimeOffset now)
    {
        if (!state.LastRenamedAt.HasValue) return 0;
        var remaining = (state.LastRenamedAt.Value.AddHours(24) - now).TotalSeconds;
        return remaining > 0 ? (long)Math.Ceiling(remaining) : 0;
    }

    public static DaoxuanActionDto ToAction(DaoxuanActionDefinition definition)
    {
        return new DaoxuanActionDto(
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

    public static DaoxuanItemDefinitionDto ToItemDefinition(DaoxuanItemDefinition definition)
    {
        var consumable = definition.Consumable;
        return new DaoxuanItemDefinitionDto(
            definition.Id,
            definition.Name,
            definition.Description,
            ToWire(definition.Kind),
            definition.Category,
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
            consumable?.Buffs.Select(buff => new DaoxuanItemBuffEffectDto(
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

    public static DaoxuanCombatAbilityDto ToCombatAbility(DaoxuanCombatAbilityDefinition definition)
    {
        return new DaoxuanCombatAbilityDto(
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

    public static DaoxuanCombatEquipmentDto ToCombatEquipment(DaoxuanCombatEquipmentDefinition definition)
    {
        return new DaoxuanCombatEquipmentDto(
            definition.Id,
            definition.Name,
            definition.Slot,
            definition.Tier,
            ToEquipmentStats(definition.Stats),
            ToEquipmentStats(definition.EnhancementBonus));
    }

    public static DaoxuanCombatEncounterDto ToCombatEncounter(DaoxuanCombatEncounterDefinition definition)
    {
        return new DaoxuanCombatEncounterDto(
            definition.Id,
            definition.Name,
            ToCombatant(definition.Enemy),
            definition.RandomSpawns
                .Select(spawn => new DaoxuanCombatSpawnDto(
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

    public static DaoxuanWorldMapDto ToWorldMap(DaoxuanWorldMapDefinition definition)
    {
        return new DaoxuanWorldMapDto(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.BackgroundSeed,
            definition.Nodes.Select(node => new DaoxuanWorldNodeDto(
                node.Id,
                node.Name,
                node.Description,
                node.X,
                node.Y,
                node.CultivationMultiplier,
                node.AvailableActionIds.ToArray(),
                node.EncounterIds.ToArray(),
                node.Npcs.Select(ToNearbyEntity).ToArray(),
                node.Buildings.Select(ToNearbyEntity).ToArray())).ToArray(),
            definition.Edges.Select(edge => new DaoxuanWorldEdgeDto(
                edge.Id,
                edge.FromNodeId,
                edge.ToNodeId,
                edge.DurationSeconds)).ToArray());
    }

    private static DaoxuanCombatantDto ToCombatant(DaoxuanCombatantDefinition definition) => new(
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

    private static DaoxuanCharacterVisualDto ToVisual(DaoxuanCharacterVisualDefinition visual) => new(
        string.IsNullOrWhiteSpace(visual.Renderer) ? "text" : visual.Renderer,
        visual.Source ?? string.Empty,
        visual.Poster);

    private static DaoxuanEquipmentCombatStatsDto ToEquipmentStats(DaoxuanEquipmentCombatStats stats) => new(
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

    private static DaoxuanQueueDto ToQueue(DaoxuanPlayerState state)
    {
        var queue = state.Queue;
        var entries = queue.Entries.Select(entry =>
        {
            var definition = DaoxuanActionCatalog.Get(entry.ActionId);
            var travelPlan = entry.TravelPlan;
            var actionName = travelPlan is null
                ? definition.Name
                : $"赶往{DaoxuanWorldCatalog.GetNode(travelPlan.MapId, travelPlan.DestinationNodeId).Name}";
            return new DaoxuanQueueEntryDto(
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
            pair => (IReadOnlyList<DaoxuanAutoUseSlotDto>)pair.Value
                .Select(slot => new DaoxuanAutoUseSlotDto(
                    slot.ItemId,
                    slot.Quantity,
                    slot.Enabled,
                    (slot.Triggers ?? []).Select(ToTrigger).ToArray()))
                .ToArray(),
            StringComparer.Ordinal);
        return new DaoxuanQueueDto(entries, queue.LoopEnabled, slots);
    }

    private static DaoxuanQueueProgressDto? ToQueueProgress(DaoxuanPlayerState state)
    {
        var queue = state.Queue;
        if (queue.Entries.Count == 0) return null;
        var current = queue.Entries[0];
        var definition = DaoxuanActionCatalog.Get(current.ActionId);
        return new DaoxuanQueueProgressDto(
            current.EntryId.ToString("D"),
            ToWire(current.Status),
            current.ProgressSeconds,
            current.DurationSeconds,
            ToExpeditionProgress(state, current, definition));
    }

    private static DaoxuanExpeditionProgressDto? ToExpeditionProgress(
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition)
    {
        if (definition.ExecutionKind == DaoxuanActionExecutionKind.Travel
            && current.TravelPlan is { } travelPlan)
        {
            var elapsed = Math.Clamp(current.ProgressSeconds, 0, travelPlan.TotalDurationSeconds);
            var duration = Math.Max(1, travelPlan.TotalDurationSeconds);
            var (travelledDistance, totalDistance) = GetTravelDistance(travelPlan, elapsed);
            return new DaoxuanExpeditionProgressDto(
                "travel",
                Math.Clamp(elapsed / (double)duration, 0, 1),
                elapsed,
                duration,
                travelledDistance,
                totalDistance,
                Activity: state.World.NodeId is null ? state.World.EdgeId : state.World.NodeId);
        }

        if (definition.ExecutionKind != DaoxuanActionExecutionKind.CombatEncounter)
        {
            return null;
        }

        var fallbackEncounterId = definition.CombatEncounterId ?? DaoxuanCombatEncounterIds.QiBeast;
        var fallbackEncounter = DaoxuanCombatCatalog.GetEncounter(fallbackEncounterId);
        var expedition = current.CombatExpedition ?? new DaoxuanCombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, fallbackEncounter.SearchMilliseconds)
        };
        var encounterId = expedition.EncounterId is { Length: > 0 } selectedEncounterId
            && DaoxuanCombatCatalog.TryGetEncounter(selectedEncounterId, out _)
                ? selectedEncounterId
                : fallbackEncounterId;
        var encounter = DaoxuanCombatCatalog.GetEncounter(encounterId);
        var persistedResources = state.CombatResources;
        var projectedPlayer = DaoxuanCombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            state.LastSimulatedAt,
            persistedResources?.Initialized == true ? persistedResources.Hitpoints : null,
            persistedResources?.Initialized == true ? persistedResources.Manapoints : null,
            state.Stats.Spirit);

        if (expedition.Phase != DaoxuanCombatExpeditionPhase.Combat)
        {
            var phaseDuration = Math.Max(1, expedition.PhaseDurationMilliseconds);
            var phaseElapsed = Math.Clamp(expedition.PhaseElapsedMilliseconds, 0, phaseDuration);
            var phase = expedition.Phase switch
            {
                DaoxuanCombatExpeditionPhase.InitialSearch => "initial-search",
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
            return new DaoxuanExpeditionProgressDto(
                phase,
                Math.Clamp(phaseElapsed / (double)phaseDuration, 0, 1),
                (int)Math.Floor(phaseElapsed / 1_000d),
                Math.Max(1, (int)Math.Ceiling(phaseDuration / 1_000d)),
                PlayerHitpoints: projectedPlayer.Hitpoints,
                PlayerMaxHitpoints: projectedPlayer.MaxHitpoints,
                PlayerManapoints: projectedPlayer.Manapoints,
                PlayerMaxManapoints: projectedPlayer.MaxManapoints,
                EnemyName: encounter.Name,
                PlayerAction: null,
                BattleSequence: expedition.BattleSequence,
                AbilityCooldownMilliseconds: nonCombatAbilityCooldowns,
                ConsumableCooldownMilliseconds: nonCombatConsumableCooldowns,
                EncounterId: encounterId);
        }

        var battleLoadout = expedition.BattleLoadout ?? state.Combat;
        var battleBuffs = expedition.BattleBuffs ?? state.Buffs;
        var battleInventory = expedition.BattleInventory ?? state.Inventory.Items;
        var initialPlayer = DaoxuanCombatSimulator.CreatePlayerState(
            battleLoadout,
            battleBuffs,
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints,
            state.Stats.Spirit);
        var result = DaoxuanCombatSimulator.Run(
            state.PlayerId,
            battleLoadout,
            battleBuffs,
            battleInventory,
            encounterId,
            expedition.BattleStartedAt,
            TimeSpan.FromMilliseconds(Math.Max(0, expedition.PhaseElapsedMilliseconds)),
            expedition.CompletedEncountersBeforeBattle,
            initialPlayer,
            stopAfterFirstVictory: true,
            playerSpirit: state.Stats.Spirit,
            initialAbilityCooldowns: expedition.InitialAbilityCooldowns,
            initialConsumableCooldowns: expedition.InitialConsumableCooldowns);
        var player = result.FinalPlayer;
        var enemies = result.FinalEnemies ?? [];
        var enemyHitpoints = enemies.Sum(enemy => Math.Max(0, enemy.Hitpoints));
        var enemyMaxHitpoints = enemies.Sum(enemy => Math.Max(0, enemy.MaxHitpoints));
        var enemyManapoints = enemies.Sum(enemy => Math.Max(0, enemy.Manapoints));
        var enemyMaxManapoints = enemies.Sum(enemy => Math.Max(0, enemy.MaxManapoints));
        return new DaoxuanExpeditionProgressDto(
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
            EnemyName: encounter.Name,
            PlayerAction: ToCombatAction(
                result.PlayerAction,
                expedition.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds)),
            EnemyAction: ToCombatAction(
                result.EnemyAction,
                expedition.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds)),
            BattleSequence: expedition.BattleSequence,
            AbilityCooldownMilliseconds: result.PlayerAbilityCooldowns,
            ConsumableCooldownMilliseconds: result.PlayerConsumableCooldowns,
            EncounterId: encounterId);
    }

    private static DaoxuanCombatResourceDto ToCombatResources(DaoxuanPlayerState state, DateTimeOffset now)
    {
        var persisted = state.CombatResources;
        var player = DaoxuanCombatSimulator.CreatePlayerState(
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
        return new DaoxuanCombatResourceDto(
            player.Hitpoints,
            player.MaxHitpoints,
            player.Manapoints,
            player.MaxManapoints,
            abilityCooldowns,
            consumableCooldowns);
    }

    private static DaoxuanCombatAttributesDto ToCombatAttributes(DaoxuanPlayerState state, DateTimeOffset now)
    {
        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, now, state.Stats.Spirit);
        return new DaoxuanCombatAttributesDto(
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

    private static DaoxuanCombatActionStateDto? ToCombatAction(
        DaoxuanCombatActionState? action,
        DateTimeOffset simulatedAt)
    {
        return action is null
            ? null
            : new DaoxuanCombatActionStateDto(
                action.Name,
                action.Kind,
                action.ElapsedMilliseconds,
                action.DurationMilliseconds,
                simulatedAt
                    .AddMilliseconds(Math.Max(0, action.DurationMilliseconds - action.ElapsedMilliseconds))
                    .ToUnixTimeMilliseconds());
    }

    private static (double TravelledDistance, double TotalDistance) GetTravelDistance(
        DaoxuanTravelPlan plan,
        int elapsedSeconds)
    {
        var remainingSeconds = Math.Max(0, elapsedSeconds);
        var travelled = 0d;
        var total = 0d;
        foreach (var segment in plan.Segments)
        {
            var edge = DaoxuanWorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
            var from = DaoxuanWorldCatalog.GetNode(plan.MapId, edge.FromNodeId);
            var to = DaoxuanWorldCatalog.GetNode(plan.MapId, edge.ToNodeId);
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

    private static IReadOnlyList<DaoxuanInventoryItemDto> ToInventory(DaoxuanInventoryState inventory)
    {
        return inventory.Items
            .Where(pair => pair.Value > 0)
            .Select(pair =>
            {
                var metadata = GetItemMetadata(pair.Key);
                return new DaoxuanInventoryItemDto(
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

    private static IReadOnlyList<DaoxuanBuffDto> ToBuffs(IReadOnlyList<DaoxuanBuffState> buffs)
    {
        return buffs.Select(buff => new DaoxuanBuffDto(
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

    private static IReadOnlyList<DaoxuanLogEventDto> ToLogEvents(IReadOnlyList<DaoxuanLogEvent> events)
    {
        return events.Select(entry => new DaoxuanLogEventDto(
            entry.Sequence,
            entry.OccurredAt.ToUnixTimeMilliseconds(),
            entry.Template)).ToArray();
    }

    private static DaoxuanStatsDto ToStats(DaoxuanPlayerStats stats)
    {
        return new DaoxuanStatsDto(
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

    private static DaoxuanCombatLoadoutDto ToCombat(DaoxuanCombatLoadout combat)
    {
        return new DaoxuanCombatLoadoutDto(
            ToWire(combat.Style),
            ToWire(combat.DamageType),
            combat.StaminaLevel,
            combat.IntelligenceLevel,
            combat.AttackLevel,
            combat.DefenseLevel,
            combat.SwordsmanshipLevel,
            combat.TalismanLevel,
            combat.AbilitySlots.Select(slot => new DaoxuanCombatAbilitySlotDto(
                slot.AbilityId,
                slot.Level,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.ConsumableSlots.Select(slot => new DaoxuanConsumableSlotDto(
                slot.ItemId,
                slot.Quantity,
                slot.Enabled,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.CombatDropRate,
            combat.CombatDropQuantity,
            combat.CombatRareFind,
            combat.Tenacity,
            combat.SearchSpeedRatio,
            (combat.Equipment ?? []).Select(item => new DaoxuanEquippedItemDto(item.ItemId, item.EnhancementLevel)).ToArray(),
            combat.LearnedAbilityIds.ToArray());
    }

    private static DaoxuanWorldStateDto ToWorld(DaoxuanWorldState world)
    {
        var position = DaoxuanWorldCatalog.GetPosition(world);
        if (world.NodeId is not null)
        {
            var node = DaoxuanWorldCatalog.GetNode(world.MapId, world.NodeId);
            return new DaoxuanWorldStateDto(
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
                node.Buildings.Select(ToNearbyEntity).ToArray());
        }

        var edge = DaoxuanWorldCatalog.GetEdge(world.MapId, world.EdgeId!);
        var from = DaoxuanWorldCatalog.GetNode(world.MapId, edge.FromNodeId);
        var to = DaoxuanWorldCatalog.GetNode(world.MapId, edge.ToNodeId);
        return new DaoxuanWorldStateDto(
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
            []);
    }

    private static DaoxuanNearbyEntityDto ToNearbyEntity(DaoxuanNearbyEntityDefinition definition) => new(
        definition.Id,
        definition.Name,
        definition.Kind,
        definition.Description,
        definition.Effect);

    private static DaoxuanCombatTriggerDto ToTrigger(DaoxuanCombatTrigger trigger) => new(
        ToWire(trigger.Dependency),
        ToWire(trigger.Condition),
        ToWire(trigger.Comparator),
        trigger.Value,
        trigger.BuffId);

    private static (string Name, string Category, string Tone, string Glyph, int Tier, string Description, bool IsTradable)
        GetItemMetadata(string itemId)
    {
        if (!DaoxuanItemCatalog.TryGet(itemId, out var definition))
        {
            return (itemId, "物品", "gold", "·", 1, "尚未登记详细信息。", false);
        }

        var tone = definition.Kind switch
        {
            DaoxuanItemKind.Currency => "gold",
            DaoxuanItemKind.Resource => "cyan",
            DaoxuanItemKind.Consumable when definition.Tier >= 2 => "orange",
            DaoxuanItemKind.Consumable => "violet",
            DaoxuanItemKind.Material => "green",
            _ => "silver"
        };
        var glyph = definition.Kind switch
        {
            DaoxuanItemKind.Currency => "◈",
            DaoxuanItemKind.Resource => "◉",
            DaoxuanItemKind.Consumable => "✺",
            DaoxuanItemKind.Material => "✤",
            _ => "◇"
        };
        return (definition.Name, definition.Category, tone, glyph, definition.Tier, definition.Description, definition.IsTradable);
    }

    private static string ToWire(DaoxuanActionType type) => type switch
    {
        DaoxuanActionType.Cultivation => "cultivation",
        DaoxuanActionType.Exploration => "exploration",
        DaoxuanActionType.Alchemy => "alchemy",
        DaoxuanActionType.Combat => "combat",
        _ => "unknown"
    };

    private static string ToWire(DaoxuanItemKind kind) => kind switch
    {
        DaoxuanItemKind.Material => "material",
        DaoxuanItemKind.Consumable => "consumable",
        DaoxuanItemKind.Currency => "currency",
        DaoxuanItemKind.Resource => "resource",
        DaoxuanItemKind.Equipment => "equipment",
        _ => "unknown"
    };

    private static string ToWire(DaoxuanActionExecutionKind kind) => kind switch
    {
        DaoxuanActionExecutionKind.CombatEncounter => "combat-encounter",
        DaoxuanActionExecutionKind.Travel => "travel",
        _ => "timed"
    };

    private static string ToWire(DaoxuanCombatStyle style) => style switch
    {
        DaoxuanCombatStyle.Sword => "sword",
        DaoxuanCombatStyle.Blade => "blade",
        DaoxuanCombatStyle.Talisman => "talisman",
        DaoxuanCombatStyle.FiveElements => "five-elements",
        DaoxuanCombatStyle.Body => "body",
        _ => "sword"
    };

    private static string ToWire(DaoxuanDamageType type) => type switch
    {
        DaoxuanDamageType.Physical => "physical",
        DaoxuanDamageType.Water => "water",
        DaoxuanDamageType.Nature => "nature",
        DaoxuanDamageType.Fire => "fire",
        _ => "physical"
    };

    private static string ToWire(DaoxuanCombatAbilityEffectType type) => type switch
    {
        DaoxuanCombatAbilityEffectType.Heal => "heal",
        _ => "damage"
    };

    private static string ToWire(DaoxuanCombatAbilityTargetType type) => type switch
    {
        DaoxuanCombatAbilityTargetType.AllEnemies => "all-enemies",
        DaoxuanCombatAbilityTargetType.Self => "self",
        DaoxuanCombatAbilityTargetType.AllAllies => "all-allies",
        DaoxuanCombatAbilityTargetType.LowestHitpointsAlly => "lowest-hp-ally",
        _ => "enemy"
    };

    private static string ToWire(DaoxuanCombatTriggerDependency dependency) => dependency switch
    {
        DaoxuanCombatTriggerDependency.TargetEnemy => "target-enemy",
        DaoxuanCombatTriggerDependency.AllyLowestHitpoints => "ally-lowest-hp",
        DaoxuanCombatTriggerDependency.AllyHighestHitpoints => "ally-highest-hp",
        DaoxuanCombatTriggerDependency.EnemyLowestHitpoints => "enemy-lowest-hp",
        DaoxuanCombatTriggerDependency.EnemyHighestHitpoints => "enemy-highest-hp",
        _ => "self"
    };

    private static string ToWire(DaoxuanCombatTriggerCondition condition) => condition switch
    {
        DaoxuanCombatTriggerCondition.CurrentManapoints => "current-mana",
        DaoxuanCombatTriggerCondition.MissingHitpoints => "missing-hp",
        DaoxuanCombatTriggerCondition.MissingManapoints => "missing-mana",
        DaoxuanCombatTriggerCondition.BuffStacks => "buff-stacks",
        DaoxuanCombatTriggerCondition.StunStatus => "stun-status",
        DaoxuanCombatTriggerCondition.BlindStatus => "blind-status",
        DaoxuanCombatTriggerCondition.SilenceStatus => "silence-status",
        DaoxuanCombatTriggerCondition.CurrentHitpointsPercentage => "current-hp-percentage",
        DaoxuanCombatTriggerCondition.MissingHitpointsPercentage => "missing-hp-percentage",
        DaoxuanCombatTriggerCondition.CurrentManapointsPercentage => "current-mana-percentage",
        DaoxuanCombatTriggerCondition.MissingManapointsPercentage => "missing-mana-percentage",
        _ => "current-hp"
    };

    private static string ToWire(DaoxuanCombatTriggerComparator comparator) => comparator switch
    {
        DaoxuanCombatTriggerComparator.LessThanOrEqual => "less-than-equal",
        DaoxuanCombatTriggerComparator.IsActive => "is-active",
        DaoxuanCombatTriggerComparator.IsInactive => "is-inactive",
        DaoxuanCombatTriggerComparator.LessThan => "less-than",
        DaoxuanCombatTriggerComparator.GreaterThan => "greater-than",
        _ => "greater-than-equal"
    };

    private static string ToWire(DaoxuanPresenceMode mode) => mode switch
    {
        DaoxuanPresenceMode.Online => "online",
        DaoxuanPresenceMode.IdleOffline => "idle-offline",
        DaoxuanPresenceMode.FullyOffline => "fully-offline",
        _ => "unknown"
    };

    private static string ToWire(DaoxuanActionStatus status) => status switch
    {
        DaoxuanActionStatus.Queued => "queued",
        DaoxuanActionStatus.Running => "running",
        DaoxuanActionStatus.Blocked => "blocked",
        _ => "unknown"
    };
}
