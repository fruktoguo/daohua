using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;
using Xiuxian.Protocol;

namespace Xiuxian.Server.Runtime;

public static class XiuxianStateMapper
{
    public static XiuxianPlayerSnapshotDto ToSnapshot(XiuxianPlayerState state, DateTimeOffset now)
    {
        return new XiuxianPlayerSnapshotDto(
            state.PlayerId.ToString("D"),
            state.Name,
            state.Version,
            now.ToUnixTimeSeconds(),
            ToWire(state.PresenceMode),
            ToQueue(state),
            ToInventory(state.Inventory),
            ToBuffs(state.Buffs),
            ToStats(state.Stats),
            state.EventLog.ToArray(),
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

    public static XiuxianPlayerPatchDto ToPatch(
        XiuxianPlayerState state,
        XiuxianDirtySections sections,
        DateTimeOffset now)
    {
        return new XiuxianPlayerPatchDto(
            state.PlayerId.ToString("D"),
            state.Version,
            now.ToUnixTimeSeconds(),
            sections.HasFlag(XiuxianDirtySections.Presence) ? ToWire(state.PresenceMode) : null,
            sections.HasFlag(XiuxianDirtySections.Queue) ? ToQueue(state) : null,
            sections.HasFlag(XiuxianDirtySections.Inventory) ? ToInventory(state.Inventory) : null,
            sections.HasFlag(XiuxianDirtySections.Buffs) ? ToBuffs(state.Buffs) : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? ToStats(state.Stats) : null,
            null,
            sections.HasFlag(XiuxianDirtySections.QueueProgress)
                && !sections.HasFlag(XiuxianDirtySections.Queue)
                    ? ToQueueProgress(state)
                    : null,
            sections.HasFlag(XiuxianDirtySections.Events)
                ? ToLogEvents(state.PendingEvents)
                : null,
            sections.HasFlag(XiuxianDirtySections.Combat) ? ToCombat(state.Combat) : null,
            sections.HasFlag(XiuxianDirtySections.World) ? ToWorld(state.World) : null,
            sections.HasFlag(XiuxianDirtySections.Combat) ? ToCombatResources(state, now) : null,
            sections.HasFlag(XiuxianDirtySections.Combat) || sections.HasFlag(XiuxianDirtySections.Buffs)
                ? ToCombatAttributes(state, now)
                : null,
            sections.HasFlag(XiuxianDirtySections.Combat) ? ToVisual(state.Visual) : null,
            now.ToUnixTimeMilliseconds(),
            sections.HasFlag(XiuxianDirtySections.Stats) ? state.IsPendingBreakthrough : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? state.Name : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? state.Account : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? state.SequenceId : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? state.CreatedAt.ToUnixTimeSeconds() : null,
            sections.HasFlag(XiuxianDirtySections.Stats) ? CalculateRenameCooldown(state, now) : null);
    }

    public static long CalculateRenameCooldown(XiuxianPlayerState state, DateTimeOffset now)
    {
        if (!state.LastRenamedAt.HasValue) return 0;
        var remaining = (state.LastRenamedAt.Value.AddHours(24) - now).TotalSeconds;
        return remaining > 0 ? (long)Math.Ceiling(remaining) : 0;
    }

    public static XiuxianActionDto ToAction(XiuxianActionDefinition definition)
    {
        return new XiuxianActionDto(
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

    public static XiuxianItemDefinitionDto ToItemDefinition(XiuxianItemDefinition definition)
    {
        var consumable = definition.Consumable;
        return new XiuxianItemDefinitionDto(
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
            consumable?.Buffs.Select(buff => new XiuxianItemBuffEffectDto(
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

    public static XiuxianCombatAbilityDto ToCombatAbility(XiuxianCombatAbilityDefinition definition)
    {
        return new XiuxianCombatAbilityDto(
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

    public static XiuxianCombatEquipmentDto ToCombatEquipment(XiuxianCombatEquipmentDefinition definition)
    {
        return new XiuxianCombatEquipmentDto(
            definition.Id,
            definition.Name,
            definition.Slot,
            definition.Tier,
            ToEquipmentStats(definition.Stats),
            ToEquipmentStats(definition.EnhancementBonus));
    }

    public static XiuxianCombatEncounterDto ToCombatEncounter(XiuxianCombatEncounterDefinition definition)
    {
        return new XiuxianCombatEncounterDto(
            definition.Id,
            definition.Name,
            ToCombatant(definition.Enemy),
            definition.RandomSpawns
                .Select(spawn => new XiuxianCombatSpawnDto(
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

    public static XiuxianWorldMapDto ToWorldMap(XiuxianWorldMapDefinition definition)
    {
        return new XiuxianWorldMapDto(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.BackgroundSeed,
            definition.Nodes.Select(node => new XiuxianWorldNodeDto(
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
            definition.Edges.Select(edge => new XiuxianWorldEdgeDto(
                edge.Id,
                edge.FromNodeId,
                edge.ToNodeId,
                edge.DurationSeconds)).ToArray());
    }

    private static XiuxianCombatantDto ToCombatant(XiuxianCombatantDefinition definition) => new(
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

    private static XiuxianCharacterVisualDto ToVisual(XiuxianCharacterVisualDefinition visual) => new(
        string.IsNullOrWhiteSpace(visual.Renderer) ? "text" : visual.Renderer,
        visual.Source ?? string.Empty,
        visual.Poster);

    private static XiuxianEquipmentCombatStatsDto ToEquipmentStats(XiuxianEquipmentCombatStats stats) => new(
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

    private static XiuxianQueueDto ToQueue(XiuxianPlayerState state)
    {
        var queue = state.Queue;
        var entries = queue.Entries.Select(entry =>
        {
            var definition = XiuxianActionCatalog.Get(entry.ActionId);
            var travelPlan = entry.TravelPlan;
            var actionName = travelPlan is null
                ? definition.Name
                : $"赶往{XiuxianWorldCatalog.GetNode(travelPlan.MapId, travelPlan.DestinationNodeId).Name}";
            return new XiuxianQueueEntryDto(
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
            pair => (IReadOnlyList<XiuxianAutoUseSlotDto>)pair.Value
                .Select(slot => new XiuxianAutoUseSlotDto(
                    slot.ItemId,
                    slot.Quantity,
                    slot.Enabled,
                    (slot.Triggers ?? []).Select(ToTrigger).ToArray()))
                .ToArray(),
            StringComparer.Ordinal);
        return new XiuxianQueueDto(entries, queue.LoopEnabled, slots);
    }

    private static XiuxianQueueProgressDto? ToQueueProgress(XiuxianPlayerState state)
    {
        var queue = state.Queue;
        if (queue.Entries.Count == 0) return null;
        var current = queue.Entries[0];
        var definition = XiuxianActionCatalog.Get(current.ActionId);
        return new XiuxianQueueProgressDto(
            current.EntryId.ToString("D"),
            ToWire(current.Status),
            current.ProgressSeconds,
            current.DurationSeconds,
            ToExpeditionProgress(state, current, definition));
    }

    private static XiuxianExpeditionProgressDto? ToExpeditionProgress(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition)
    {
        if (definition.ExecutionKind == XiuxianActionExecutionKind.Travel
            && current.TravelPlan is { } travelPlan)
        {
            var elapsed = Math.Clamp(current.ProgressSeconds, 0, travelPlan.TotalDurationSeconds);
            var duration = Math.Max(1, travelPlan.TotalDurationSeconds);
            var (travelledDistance, totalDistance) = GetTravelDistance(travelPlan, elapsed);
            return new XiuxianExpeditionProgressDto(
                "travel",
                Math.Clamp(elapsed / (double)duration, 0, 1),
                elapsed,
                duration,
                travelledDistance,
                totalDistance,
                Activity: state.World.NodeId is null ? state.World.EdgeId : state.World.NodeId);
        }

        if (definition.ExecutionKind != XiuxianActionExecutionKind.CombatEncounter)
        {
            return null;
        }

        var fallbackEncounterId = definition.CombatEncounterId ?? XiuxianCombatEncounterIds.QiBeast;
        var fallbackEncounter = XiuxianCombatCatalog.GetEncounter(fallbackEncounterId);
        var expedition = current.CombatExpedition ?? new XiuxianCombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, fallbackEncounter.SearchMilliseconds)
        };
        var encounterId = expedition.EncounterId is { Length: > 0 } selectedEncounterId
            && XiuxianCombatCatalog.TryGetEncounter(selectedEncounterId, out _)
                ? selectedEncounterId
                : fallbackEncounterId;
        var encounter = XiuxianCombatCatalog.GetEncounter(encounterId);
        var persistedResources = state.CombatResources;
        var projectedPlayer = XiuxianCombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            state.LastSimulatedAt,
            persistedResources?.Initialized == true ? persistedResources.Hitpoints : null,
            persistedResources?.Initialized == true ? persistedResources.Manapoints : null,
            state.Stats.Spirit);

        if (expedition.Phase != XiuxianCombatExpeditionPhase.Combat)
        {
            var phaseDuration = Math.Max(1, expedition.PhaseDurationMilliseconds);
            var phaseElapsed = Math.Clamp(expedition.PhaseElapsedMilliseconds, 0, phaseDuration);
            var isDead = expedition.Phase == XiuxianCombatExpeditionPhase.Death;
            var phase = expedition.Phase switch
            {
                XiuxianCombatExpeditionPhase.InitialSearch => "initial-search",
                XiuxianCombatExpeditionPhase.Death => "death",
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
            return new XiuxianExpeditionProgressDto(
                phase,
                Math.Clamp(phaseElapsed / (double)phaseDuration, 0, 1),
                (int)Math.Floor(phaseElapsed / 1_000d),
                Math.Max(1, (int)Math.Ceiling(phaseDuration / 1_000d)),
                PlayerHitpoints: projectedPlayer.Hitpoints,
                PlayerMaxHitpoints: projectedPlayer.MaxHitpoints,
                PlayerManapoints: projectedPlayer.Manapoints,
                PlayerMaxManapoints: projectedPlayer.MaxManapoints,
                EnemyName: encounter.Name,
                PlayerAction: isDead
                    ? new XiuxianCombatActionStateDto(
                        "复活",
                        "respawn",
                        phaseElapsed,
                        phaseDuration,
                        state.LastSimulatedAt
                            .AddMilliseconds(Math.Max(0, phaseDuration - phaseElapsed))
                            .ToUnixTimeMilliseconds())
                    : null,
                BattleSequence: expedition.BattleSequence,
                AbilityCooldownMilliseconds: nonCombatAbilityCooldowns,
                ConsumableCooldownMilliseconds: nonCombatConsumableCooldowns,
                EncounterId: encounterId);
        }

        var battleLoadout = expedition.BattleLoadout ?? state.Combat;
        var battleBuffs = expedition.BattleBuffs ?? state.Buffs;
        var battleInventory = expedition.BattleInventory ?? state.Inventory.Items;
        var initialPlayer = XiuxianCombatSimulator.CreatePlayerState(
            battleLoadout,
            battleBuffs,
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints,
            state.Stats.Spirit);
        var result = XiuxianCombatSimulator.Run(
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
        return new XiuxianExpeditionProgressDto(
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

    private static XiuxianCombatResourceDto ToCombatResources(XiuxianPlayerState state, DateTimeOffset now)
    {
        var persisted = state.CombatResources;
        var player = XiuxianCombatSimulator.CreatePlayerState(
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
        return new XiuxianCombatResourceDto(
            player.Hitpoints,
            player.MaxHitpoints,
            player.Manapoints,
            player.MaxManapoints,
            abilityCooldowns,
            consumableCooldowns);
    }

    private static XiuxianCombatAttributesDto ToCombatAttributes(XiuxianPlayerState state, DateTimeOffset now)
    {
        var attributes = XiuxianCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, now, state.Stats.Spirit);
        return new XiuxianCombatAttributesDto(
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

    private static XiuxianCombatActionStateDto? ToCombatAction(
        XiuxianCombatActionState? action,
        DateTimeOffset simulatedAt)
    {
        return action is null
            ? null
            : new XiuxianCombatActionStateDto(
                action.Name,
                action.Kind,
                action.ElapsedMilliseconds,
                action.DurationMilliseconds,
                simulatedAt
                    .AddMilliseconds(Math.Max(0, action.DurationMilliseconds - action.ElapsedMilliseconds))
                    .ToUnixTimeMilliseconds());
    }

    private static (double TravelledDistance, double TotalDistance) GetTravelDistance(
        XiuxianTravelPlan plan,
        int elapsedSeconds)
    {
        var remainingSeconds = Math.Max(0, elapsedSeconds);
        var travelled = 0d;
        var total = 0d;
        foreach (var segment in plan.Segments)
        {
            var edge = XiuxianWorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
            var from = XiuxianWorldCatalog.GetNode(plan.MapId, edge.FromNodeId);
            var to = XiuxianWorldCatalog.GetNode(plan.MapId, edge.ToNodeId);
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

    private static IReadOnlyList<XiuxianInventoryItemDto> ToInventory(XiuxianInventoryState inventory)
    {
        return inventory.Items
            .Where(pair => pair.Value > 0)
            .Select(pair =>
            {
                var metadata = GetItemMetadata(pair.Key);
                return new XiuxianInventoryItemDto(
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

    private static IReadOnlyList<XiuxianBuffDto> ToBuffs(IReadOnlyList<XiuxianBuffState> buffs)
    {
        return buffs.Select(buff => new XiuxianBuffDto(
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

    private static IReadOnlyList<XiuxianLogEventDto> ToLogEvents(IReadOnlyList<XiuxianLogEvent> events)
    {
        return events.Select(entry => new XiuxianLogEventDto(
            entry.Sequence,
            entry.OccurredAt.ToUnixTimeMilliseconds(),
            entry.Template)).ToArray();
    }

    private static XiuxianStatsDto ToStats(XiuxianPlayerStats stats)
    {
        return new XiuxianStatsDto(
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

    private static XiuxianCombatLoadoutDto ToCombat(XiuxianCombatLoadout combat)
    {
        return new XiuxianCombatLoadoutDto(
            ToWire(combat.Style),
            ToWire(combat.DamageType),
            combat.StaminaLevel,
            combat.IntelligenceLevel,
            combat.AttackLevel,
            combat.DefenseLevel,
            combat.SwordsmanshipLevel,
            combat.TalismanLevel,
            combat.FiveElementsLevel,
            combat.AbilitySlots.Select(slot => new XiuxianCombatAbilitySlotDto(
                slot.AbilityId,
                slot.Level,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.ConsumableSlots.Select(slot => new XiuxianConsumableSlotDto(
                slot.ItemId,
                slot.Quantity,
                slot.Enabled,
                (slot.Triggers ?? []).Select(ToTrigger).ToArray())).ToArray(),
            combat.CombatDropRate,
            combat.CombatDropQuantity,
            combat.CombatRareFind,
            combat.Tenacity,
            combat.SearchSpeedRatio,
            (combat.Equipment ?? []).Select(item => new XiuxianEquippedItemDto(item.ItemId, item.EnhancementLevel)).ToArray(),
            combat.LearnedAbilityIds.ToArray());
    }

    private static XiuxianWorldStateDto ToWorld(XiuxianWorldState world)
    {
        var position = XiuxianWorldCatalog.GetPosition(world);
        if (world.NodeId is not null)
        {
            var node = XiuxianWorldCatalog.GetNode(world.MapId, world.NodeId);
            return new XiuxianWorldStateDto(
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

        var edge = XiuxianWorldCatalog.GetEdge(world.MapId, world.EdgeId!);
        var from = XiuxianWorldCatalog.GetNode(world.MapId, edge.FromNodeId);
        var to = XiuxianWorldCatalog.GetNode(world.MapId, edge.ToNodeId);
        return new XiuxianWorldStateDto(
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

    private static XiuxianNearbyEntityDto ToNearbyEntity(XiuxianNearbyEntityDefinition definition) => new(
        definition.Id,
        definition.Name,
        definition.Kind,
        definition.Description,
        definition.Effect);

    private static XiuxianCombatTriggerDto ToTrigger(XiuxianCombatTrigger trigger) => new(
        ToWire(trigger.Dependency),
        ToWire(trigger.Condition),
        ToWire(trigger.Comparator),
        trigger.Value,
        trigger.BuffId);

    private static (string Name, string Category, string Tone, string Glyph, int Tier, string Description, bool IsTradable)
        GetItemMetadata(string itemId)
    {
        if (!XiuxianItemCatalog.TryGet(itemId, out var definition))
        {
            return (itemId, "物品", "gold", "·", 1, "尚未登记详细信息。", false);
        }

        var tone = definition.Kind switch
        {
            XiuxianItemKind.Currency => "gold",
            XiuxianItemKind.Resource => "cyan",
            XiuxianItemKind.Consumable when definition.Tier >= 2 => "orange",
            XiuxianItemKind.Consumable => "violet",
            XiuxianItemKind.Material => "green",
            _ => "silver"
        };
        var glyph = definition.Kind switch
        {
            XiuxianItemKind.Currency => "◈",
            XiuxianItemKind.Resource => "◉",
            XiuxianItemKind.Consumable => "✺",
            XiuxianItemKind.Material => "✤",
            _ => "◇"
        };
        return (definition.Name, definition.Category, tone, glyph, definition.Tier, definition.Description, definition.IsTradable);
    }

    private static string ToWire(XiuxianActionType type) => type switch
    {
        XiuxianActionType.Cultivation => "cultivation",
        XiuxianActionType.Exploration => "exploration",
        XiuxianActionType.Alchemy => "alchemy",
        XiuxianActionType.Combat => "combat",
        _ => "unknown"
    };

    private static string ToWire(XiuxianItemKind kind) => kind switch
    {
        XiuxianItemKind.Material => "material",
        XiuxianItemKind.Consumable => "consumable",
        XiuxianItemKind.Currency => "currency",
        XiuxianItemKind.Resource => "resource",
        XiuxianItemKind.Equipment => "equipment",
        _ => "unknown"
    };

    private static string ToWire(XiuxianActionExecutionKind kind) => kind switch
    {
        XiuxianActionExecutionKind.CombatEncounter => "combat-encounter",
        XiuxianActionExecutionKind.Travel => "travel",
        _ => "timed"
    };

    private static string ToWire(XiuxianCombatStyle style) => style switch
    {
        XiuxianCombatStyle.Sword => "sword",
        XiuxianCombatStyle.Blade => "blade",
        XiuxianCombatStyle.Talisman => "talisman",
        XiuxianCombatStyle.FiveElements => "five-elements",
        XiuxianCombatStyle.Body => "body",
        _ => "sword"
    };

    private static string ToWire(XiuxianDamageType type) => type switch
    {
        XiuxianDamageType.Physical => "physical",
        XiuxianDamageType.Water => "water",
        XiuxianDamageType.Nature => "nature",
        XiuxianDamageType.Fire => "fire",
        _ => "physical"
    };

    private static string ToWire(XiuxianCombatAbilityEffectType type) => type switch
    {
        XiuxianCombatAbilityEffectType.Heal => "heal",
        _ => "damage"
    };

    private static string ToWire(XiuxianCombatAbilityTargetType type) => type switch
    {
        XiuxianCombatAbilityTargetType.AllEnemies => "all-enemies",
        XiuxianCombatAbilityTargetType.Self => "self",
        XiuxianCombatAbilityTargetType.AllAllies => "all-allies",
        XiuxianCombatAbilityTargetType.LowestHitpointsAlly => "lowest-hp-ally",
        _ => "enemy"
    };

    private static string ToWire(XiuxianCombatTriggerDependency dependency) => dependency switch
    {
        XiuxianCombatTriggerDependency.TargetEnemy => "target-enemy",
        XiuxianCombatTriggerDependency.AllyLowestHitpoints => "ally-lowest-hp",
        XiuxianCombatTriggerDependency.AllyHighestHitpoints => "ally-highest-hp",
        XiuxianCombatTriggerDependency.EnemyLowestHitpoints => "enemy-lowest-hp",
        XiuxianCombatTriggerDependency.EnemyHighestHitpoints => "enemy-highest-hp",
        _ => "self"
    };

    private static string ToWire(XiuxianCombatTriggerCondition condition) => condition switch
    {
        XiuxianCombatTriggerCondition.CurrentManapoints => "current-mana",
        XiuxianCombatTriggerCondition.MissingHitpoints => "missing-hp",
        XiuxianCombatTriggerCondition.MissingManapoints => "missing-mana",
        XiuxianCombatTriggerCondition.BuffStacks => "buff-stacks",
        XiuxianCombatTriggerCondition.StunStatus => "stun-status",
        XiuxianCombatTriggerCondition.BlindStatus => "blind-status",
        XiuxianCombatTriggerCondition.SilenceStatus => "silence-status",
        XiuxianCombatTriggerCondition.CurrentHitpointsPercentage => "current-hp-percentage",
        XiuxianCombatTriggerCondition.MissingHitpointsPercentage => "missing-hp-percentage",
        XiuxianCombatTriggerCondition.CurrentManapointsPercentage => "current-mana-percentage",
        XiuxianCombatTriggerCondition.MissingManapointsPercentage => "missing-mana-percentage",
        _ => "current-hp"
    };

    private static string ToWire(XiuxianCombatTriggerComparator comparator) => comparator switch
    {
        XiuxianCombatTriggerComparator.LessThanOrEqual => "less-than-equal",
        XiuxianCombatTriggerComparator.IsActive => "is-active",
        XiuxianCombatTriggerComparator.IsInactive => "is-inactive",
        XiuxianCombatTriggerComparator.LessThan => "less-than",
        XiuxianCombatTriggerComparator.GreaterThan => "greater-than",
        _ => "greater-than-equal"
    };

    private static string ToWire(XiuxianPresenceMode mode) => mode switch
    {
        XiuxianPresenceMode.Online => "online",
        XiuxianPresenceMode.IdleOffline => "idle-offline",
        XiuxianPresenceMode.FullyOffline => "fully-offline",
        _ => "unknown"
    };

    private static string ToWire(XiuxianActionStatus status) => status switch
    {
        XiuxianActionStatus.Queued => "queued",
        XiuxianActionStatus.Running => "running",
        XiuxianActionStatus.Blocked => "blocked",
        _ => "unknown"
    };
}
