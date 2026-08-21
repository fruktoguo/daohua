using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 无帧、可复现的战斗事件模拟器。
/// 只在攻击、施法、复活和消耗品触发等边界推进，离线快进不会按秒创建事件。
/// </summary>
public static partial class XiuxianCombatSimulator
{
    private const int MaxEventsPerRun = 2_000_000;
    private const int MaxResultEvents = 4_096;
    private const int RegenTickMilliseconds = 10_000;
    private const int ConsumableRecoveryTickMilliseconds = 5_000;
    private const int DamageOverTimeTickMilliseconds = 3_000;
    private const int DefaultEnrageTickMilliseconds = 60_000;
    private const int CombatModifierDurationMilliseconds = 15_000;
    private const long InfiniteTime = long.MaxValue / 4;

    public static XiuxianCombatPlayerState CreatePlayerState(
        XiuxianCombatLoadout loadout,
        IReadOnlyList<XiuxianBuffState> activeBuffs,
        DateTimeOffset at,
        long? hitpoints = null,
        long? manapoints = null,
        long spirit = 0)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        var buffs = CreateBuffRuntime(activeBuffs, at);
        var player = BuildPlayerStats(loadout, buffs, new CombatModifierRuntime(), 0, spirit);
        return new XiuxianCombatPlayerState(
            Math.Clamp(hitpoints ?? player.MaxHitpoints, 0, player.MaxHitpoints),
            player.MaxHitpoints,
            Math.Clamp(manapoints ?? player.MaxManapoints, 0, player.MaxManapoints),
            player.MaxManapoints,
            (hitpoints ?? player.MaxHitpoints) <= 0);
    }

    public static XiuxianCombatAttributeState CreatePlayerAttributes(
        XiuxianCombatLoadout loadout,
        IReadOnlyList<XiuxianBuffState> activeBuffs,
        DateTimeOffset at,
        long spirit = 0)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        var buffs = CreateBuffRuntime(activeBuffs, at);
        var player = BuildPlayerStats(loadout, buffs, new CombatModifierRuntime(), 0, spirit);
        return new XiuxianCombatAttributeState(
            player.MaxHitpoints,
            player.MaxManapoints,
            player.AttackIntervalMilliseconds,
            player.AccuracyRating,
            player.MaxDamage,
            player.EvasionRating,
            player.Armor,
            player.WaterResistance,
            player.NatureResistance,
            player.FireResistance,
            player.Style,
            player.DamageType,
            player.Tenacity,
            player.HpRegenPerTenSeconds,
            player.MpRegenPerTenSeconds,
            player.PenetrationRatio,
            player.PierceChance,
            player.CurseRatio,
            player.FuryRatio,
            player.WeakenRatio,
            player.LifeStealRatio,
            player.ArmorDamageRatio,
            player.HpDrainRatio,
            player.DamageTakenRatio,
            player.CriticalChance,
            player.CriticalDamageRatio,
            player.CriticalAvoidance,
            player.DamageAbsorptionRatio);
    }

    public static XiuxianCombatRunResult Run(
        Guid playerId,
        XiuxianCombatLoadout loadout,
        IReadOnlyList<XiuxianBuffState> activeBuffs,
        IReadOnlyDictionary<string, long> inventory,
        string encounterId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        long completedEncountersBeforeRun = 0,
        XiuxianCombatPlayerState? initialPlayerState = null,
        bool stopAfterFirstOutcome = false,
        bool stopAfterFirstVictory = false,
        long playerSpirit = 0,
        IReadOnlyDictionary<string, long>? initialAbilityCooldowns = null,
        IReadOnlyList<long>? initialConsumableCooldowns = null)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        ArgumentNullException.ThrowIfNull(inventory);

        if (!XiuxianCombatCatalog.TryGetEncounter(encounterId, out var encounter))
        {
            throw new KeyNotFoundException($"未知战斗遭遇: {encounterId}");
        }

        if (encounter.RandomSpawns.Count > 0 || encounter.BossEnemies.Count > 0)
        {
            return RunGroupEncounter(
                playerId,
                loadout,
                activeBuffs,
                inventory,
                encounter,
                startedAt,
                duration,
                completedEncountersBeforeRun,
                initialPlayerState,
                stopAfterFirstOutcome,
                stopAfterFirstVictory,
                playerSpirit,
                initialAbilityCooldowns,
                initialConsumableCooldowns);
        }

        var durationMilliseconds = Math.Max(0, checked((long)Math.Floor(duration.TotalMilliseconds)));
        var random = new DeterministicRandom(CreateSeed(playerId, startedAt, encounterId));
        var dropRateMultiplier = NormalizeCombatMultiplier(loadout.CombatDropRate);
        var dropQuantityMultiplier = NormalizeCombatMultiplier(loadout.CombatDropQuantity);
        var rareFindMultiplier = NormalizeCombatMultiplier(loadout.CombatRareFind);
        var localInventory = new Dictionary<string, long>(inventory, StringComparer.Ordinal);
        var consumedItems = new Dictionary<string, long>(StringComparer.Ordinal);
        var buffs = CreateBuffRuntime(activeBuffs, startedAt);
        var recoveries = new List<RecoveryRuntime>();
        var damageOverTimes = new List<DamageOverTimeRuntime>();
        var playerControl = new CombatControlRuntime();
        var enemyControl = new CombatControlRuntime();
        var playerModifiers = new CombatModifierRuntime();
        var enemyModifiers = new CombatModifierRuntime();
        var events = new List<string>(capacity: 16);
        var player = BuildPlayerStats(loadout, buffs, playerModifiers, 0, playerSpirit);
        var enemyEnrageStacks = 0;
        var enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
        var playerHitpoints = Math.Clamp(initialPlayerState?.Hitpoints ?? player.MaxHitpoints, 0, player.MaxHitpoints);
        var playerManapoints = Math.Clamp(initialPlayerState?.Manapoints ?? player.MaxManapoints, 0, player.MaxManapoints);
        var enemyHitpoints = enemy.MaxHitpoints;
        var enemyManapoints = enemy.MaxManapoints;
        var playerDead = false;
        var enemyDead = false;
        var playerRespawnAt = InfiniteTime;
        var enemyRespawnAt = InfiniteTime;
        var nextPlayerAction = 0L;
        var nextEnemyAction = 0L;
        var playerActionName = "灵击";
        var playerActionKind = "attack";
        var playerActionStartedAt = 0L;
        var enemyActionName = "攻击";
        var enemyActionKind = "attack";
        var enemyActionStartedAt = 0L;
        XiuxianCombatAbilityDefinition? pendingPlayerAbility = null;
        var pendingPlayerAbilityIndex = -1;
        XiuxianCombatAbilityDefinition? pendingEnemyAbility = null;
        var pendingEnemyAbilityIndex = -1;
        var nextConsumableUse = new long[Math.Max(0, loadout.ConsumableSlots.Count)];
        for (var i = 0; i < nextConsumableUse.Length; i++)
        {
            if (initialConsumableCooldowns is not null
                && i < initialConsumableCooldowns.Count
                && initialConsumableCooldowns[i] > 0)
            {
                nextConsumableUse[i] = initialConsumableCooldowns[i];
            }
            else
            {
                nextConsumableUse[i] = 0L;
            }
        }
        long nextRegenTick = RegenTickMilliseconds;
        long nextEnrageTick = Math.Max(
            1,
            encounter.EnrageTickMilliseconds <= 0
                ? DefaultEnrageTickMilliseconds
                : encounter.EnrageTickMilliseconds);
        var enemySpawnAt = 0L;
        var abilityReadyAt = new long[Math.Max(0, loadout.AbilitySlots.Count)];
        for (var i = 0; i < abilityReadyAt.Length; i++)
        {
            var abilityId = loadout.AbilitySlots[i].AbilityId;
            if (!string.IsNullOrWhiteSpace(abilityId)
                && initialAbilityCooldowns is not null
                && initialAbilityCooldowns.TryGetValue(abilityId, out var cd)
                && cd > 0)
            {
                abilityReadyAt[i] = cd;
            }
            else
            {
                abilityReadyAt[i] = 0L;
            }
        }
        var playerAbilities = loadout.AbilitySlots
            .Select(slot => XiuxianCombatCatalog.TryGetAbility(slot.AbilityId, out var ability)
                ? XiuxianCombatCatalog.GetAbilityAtLevel(ability.Id, slot.Level)
                : null)
            .ToArray();
        var enemyAbilityReadyAt = new long[Math.Max(0, enemy.Definition?.Abilities.Count ?? 0)];
        Array.Fill(enemyAbilityReadyAt, 0L);
        var encounters = 1;
        var wins = 0;
        var losses = 0;
        long damageDealt = 0;
        long damageTaken = 0;
        double experience = 0;
        long spirit = 0;
        long droppedQuantity = 0;
        long hitpointsRegenerated = 0;
        long manapointsRegenerated = 0;
        long hitpointsRecoveredFromConsumables = 0;
        long manapointsRecoveredFromConsumables = 0;
        long hitpointsRecoveredFromAbilities = 0;
        var droppedItems = new Dictionary<string, long>(StringComparer.Ordinal);
        Dictionary<string, long>? rareDroppedItems = null;
        var maxEnrageStacks = 0;
        var now = 0L;
        var processedEvents = 0;
        long? outcomeMilliseconds = null;
        long? victoryMilliseconds = null;

        void FinalizeEnemyDefeat()
        {
            if (enemyDead || enemyHitpoints > 0) return;

            wins++;
            outcomeMilliseconds ??= now;
            victoryMilliseconds ??= now;
            var playerLevel = Math.Max(1.0, (double)loadout.AttackLevel);
            var enemyLevel = Math.Max(1.0, (double)encounter.Enemy.AttackLevel);
            var gainedExperience = XiuxianCombatFormula.CalculateCombatExperience(playerLevel, enemyLevel);
            experience += gainedExperience;
            var spiritMin = Math.Max(0, encounter.SpiritMinPerWin);
            var spiritMax = Math.Max(spiritMin, encounter.SpiritMaxPerWin);
            var spiritReward = spiritMin == spiritMax
                ? spiritMin
                : random.NextInt64(spiritMin, checked(spiritMax + 1));
            spirit = checked(spirit + spiritReward);
            var regularDrops = RollDrops(
                encounter,
                rare: false,
                dropRateMultiplier,
                rareFindMultiplier,
                dropQuantityMultiplier,
                ref random,
                ref droppedQuantity);
            var rareDrops = RollDrops(
                encounter,
                rare: true,
                dropRateMultiplier,
                rareFindMultiplier,
                dropQuantityMultiplier,
                ref random,
                ref droppedQuantity);
            MergeDrops(droppedItems, regularDrops);
            if (rareDrops.Count > 0)
            {
                rareDroppedItems ??= new Dictionary<string, long>(StringComparer.Ordinal);
                MergeDrops(rareDroppedItems, rareDrops);
            }
            if (spiritReward > 0)
            {
                AddDrop(droppedItems, XiuxianItemIds.Spirit, spiritReward, ref droppedQuantity);
            }

            var dropSummary = events.Count < MaxResultEvents
                ? FormatDropSummary(regularDrops, rareDrops)
                : string.Empty;
            AddEvent(events, $"击败 {{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}，获得 {gainedExperience:F1} 点战斗经验{dropSummary}");
            enemyDead = true;
            pendingEnemyAbility = null;
            pendingEnemyAbilityIndex = -1;
            pendingPlayerAbility = null;
            pendingPlayerAbilityIndex = -1;
            damageOverTimes.RemoveAll(dot => !dot.TargetsPlayer);
            enemyControl.Clear();
            enemyModifiers.Clear();
            enemyRespawnAt = checked(now + Math.Max(1, encounter.RespawnMilliseconds));
            nextEnemyAction = enemyRespawnAt;
            enemyActionName = "妖气重聚";
            enemyActionKind = "respawn";
            enemyActionStartedAt = now;
            if (!playerDead)
            {
                nextPlayerAction = enemyRespawnAt;
                playerActionName = "等待敌群";
                playerActionKind = "wait";
                playerActionStartedAt = now;
            }
        }

        void FinalizePlayerDefeat()
        {
            if (playerDead || playerHitpoints > 0) return;

            losses++;
            outcomeMilliseconds ??= now;
            AddEvent(events, $"在 {{{{encounter:{encounter.Id}|{encounter.Name}}}}} 中暂时败退");
            playerDead = true;
            pendingPlayerAbility = null;
            pendingPlayerAbilityIndex = -1;
            pendingEnemyAbility = null;
            pendingEnemyAbilityIndex = -1;
            recoveries.Clear();
            damageOverTimes.RemoveAll(dot => dot.TargetsPlayer);
            playerControl.Clear();
            playerModifiers.Clear();
            playerRespawnAt = checked(now + Math.Max(1, encounter.PlayerRespawnMilliseconds));
            nextPlayerAction = playerRespawnAt;
            playerActionName = "调息复起";
            playerActionKind = "respawn";
            playerActionStartedAt = now;
            if (!enemyDead)
            {
                nextEnemyAction = InfiniteTime;
                enemyActionName = "待机";
                enemyActionKind = "wait";
                enemyActionStartedAt = now;
            }
        }

        if (playerHitpoints <= 0)
        {
            FinalizePlayerDefeat();
        }

        long GetNextEventAt(long currentTime)
        {
            return Math.Min(
                Math.Min(nextPlayerAction, nextEnemyAction),
                Math.Min(
                    Math.Min(
                        playerDead ? playerRespawnAt : InfiniteTime,
                        enemyDead ? enemyRespawnAt : InfiniteTime),
                    Math.Min(
                        Math.Min(nextRegenTick, nextEnrageTick),
                        Math.Min(
                            GetNextConsumableEventTime(currentTime, nextConsumableUse),
                            Math.Min(
                                GetNextRecoveryEventTime(currentTime, recoveries),
                                Math.Min(
                                    GetNextDamageOverTimeEventTime(currentTime, damageOverTimes),
                                    Math.Min(
                                        GetNextControlEventTime(currentTime, playerControl, enemyControl),
                                        GetNextModifierEventTime(currentTime, playerModifiers, enemyModifiers))))))));
        }

        while ((!stopAfterFirstOutcome || outcomeMilliseconds is null)
            && (!stopAfterFirstVictory || victoryMilliseconds is null)
            && now <= durationMilliseconds
            && processedEvents < MaxEventsPerRun)
        {
            processedEvents++;
            var next = GetNextEventAt(now);

            if (next > durationMilliseconds || next < now)
            {
                break;
            }

            now = next;
            if (playerModifiers.Expire(now))
            {
                player = BuildPlayerStats(loadout, buffs, playerModifiers, now, playerSpirit);
                AddEvent(events, "战斗状态消退：自身词缀层数已更新");
            }

            if (enemyModifiers.Expire(now))
            {
                enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
                AddEvent(events, "战斗状态消退：目标词缀层数已更新");
            }
            if (playerControl.Expire(now)
                && !playerDead
                && pendingPlayerAbility is null)
            {
                nextPlayerAction = now;
            }

            if (enemyControl.Expire(now)
                && !enemyDead
                && pendingEnemyAbility is null)
            {
                nextEnemyAction = now;
            }

            if (playerDead && now >= playerRespawnAt)
            {
                playerDead = false;
                pendingPlayerAbility = null;
                pendingPlayerAbilityIndex = -1;
                recoveries.Clear();
                damageOverTimes.RemoveAll(dot => dot.TargetsPlayer);
                playerControl.Clear();
                playerModifiers.Clear();
                player = BuildPlayerStats(loadout, buffs, playerModifiers, now, playerSpirit);
                playerHitpoints = player.MaxHitpoints;
                playerManapoints = player.MaxManapoints;
                for (var i = 0; i < abilityReadyAt.Length; i++)
                {
                    abilityReadyAt[i] = Math.Max(abilityReadyAt[i], now);
                }
                for (var i = 0; i < nextConsumableUse.Length; i++)
                {
                    nextConsumableUse[i] = Math.Max(nextConsumableUse[i], now);
                }
                nextPlayerAction = now;
                nextEnemyAction = now;
                playerActionStartedAt = now;
                enemyActionStartedAt = now;
                AddEvent(events, $"战斗复起：{{{{encounter:{encounter.Id}|{encounter.Name}}}}}");
            }

            if (enemyDead && now >= enemyRespawnAt)
            {
                enemyDead = false;
                pendingEnemyAbility = null;
                pendingEnemyAbilityIndex = -1;
                damageOverTimes.RemoveAll(dot => !dot.TargetsPlayer);
                enemyControl.Clear();
                enemyModifiers.Clear();
                enemyEnrageStacks = 0;
                enemySpawnAt = now;
                enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
                enemyHitpoints = enemy.MaxHitpoints;
                enemyManapoints = enemy.MaxManapoints;
                Array.Fill(enemyAbilityReadyAt, now);
                nextPlayerAction = now;
                nextEnemyAction = now;
                playerActionStartedAt = now;
                enemyActionStartedAt = now;
                nextEnrageTick = checked(now + Math.Max(
                    1,
                    encounter.EnrageTickMilliseconds <= 0
                        ? DefaultEnrageTickMilliseconds
                        : encounter.EnrageTickMilliseconds));
                encounters++;
            }

            if (now >= nextRegenTick)
            {
                if (!playerDead)
                {
                    var restoredHitpoints = Math.Min(
                        Math.Max(0, player.MaxHitpoints - playerHitpoints),
                        XiuxianCombatFormula.CalculateRegeneration(
                            player.MaxHitpoints,
                            player.HpRegenPerTenSeconds));
                    playerHitpoints = checked(playerHitpoints + restoredHitpoints);
                    hitpointsRegenerated = checked(hitpointsRegenerated + restoredHitpoints);
                }

                var restoredManapoints = Math.Min(
                    Math.Max(0, player.MaxManapoints - playerManapoints),
                    XiuxianCombatFormula.CalculateRegeneration(
                        player.MaxManapoints,
                        player.MpRegenPerTenSeconds));
                playerManapoints = checked(playerManapoints + restoredManapoints);
                manapointsRegenerated = checked(manapointsRegenerated + restoredManapoints);

                if (!enemyDead)
                {
                    // 敌人也遵循公开模拟器的基础恢复规则；装备/增益扩展只需替换目录字段。
                    var restoredEnemyHitpoints = Math.Min(
                        Math.Max(0, enemy.MaxHitpoints - enemyHitpoints),
                        XiuxianCombatFormula.CalculateRegeneration(
                            enemy.MaxHitpoints,
                            enemy.HpRegenPerTenSeconds));
                    var restoredEnemyManapoints = Math.Min(
                        Math.Max(0, enemy.MaxManapoints - enemyManapoints),
                        XiuxianCombatFormula.CalculateRegeneration(
                            enemy.MaxManapoints,
                            enemy.MpRegenPerTenSeconds));
                    enemyHitpoints = checked(enemyHitpoints + restoredEnemyHitpoints);
                    enemyManapoints = checked(enemyManapoints + restoredEnemyManapoints);
                }

                nextRegenTick = checked(now + RegenTickMilliseconds);
            }

            if (now >= nextEnrageTick)
            {
                if (!enemyDead)
                {
                    var enrageTime = Math.Max(1, encounter.EnrageTimeMilliseconds);
                    var nextStacks = XiuxianCombatFormula.CalculateEnrageStacks(
                        Math.Max(0, now - enemySpawnAt),
                        enrageTime,
                        Math.Max(0, encounter.MaxEnrageStacks));
                    if (nextStacks != enemyEnrageStacks)
                    {
                        enemyEnrageStacks = nextStacks;
                        maxEnrageStacks = Math.Max(maxEnrageStacks, enemyEnrageStacks);
                        enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
                    }
                }

                nextEnrageTick = checked(now + Math.Max(
                    1,
                    encounter.EnrageTickMilliseconds <= 0
                        ? DefaultEnrageTickMilliseconds
                        : encounter.EnrageTickMilliseconds));
            }

            var nextRecoveryTick = GetNextRecoveryEventTime(now, recoveries);
            if (!playerDead && now >= nextRecoveryTick)
            {
                for (var index = recoveries.Count - 1; index >= 0; index--)
                {
                    var recovery = recoveries[index];
                    if (recovery.NextTickAt > now) continue;

                    var remainingHitpoints = recovery.RemainingHitpoints;
                    var remainingManapoints = recovery.RemainingManapoints;
                    var hitpointShare = TakeRecoveryShare(
                        ref remainingHitpoints,
                        recovery.RemainingTicks);
                    var manapointShare = TakeRecoveryShare(
                        ref remainingManapoints,
                        recovery.RemainingTicks);
                    recovery.RemainingHitpoints = remainingHitpoints;
                    recovery.RemainingManapoints = remainingManapoints;
                    var restoredHitpoints = Math.Min(
                        Math.Max(0, player.MaxHitpoints - playerHitpoints),
                        hitpointShare);
                    var restoredManapoints = Math.Min(
                        Math.Max(0, player.MaxManapoints - playerManapoints),
                        manapointShare);
                    playerHitpoints = checked(playerHitpoints + restoredHitpoints);
                    playerManapoints = checked(playerManapoints + restoredManapoints);
                    hitpointsRecoveredFromConsumables = checked(
                        hitpointsRecoveredFromConsumables + restoredHitpoints);
                    manapointsRecoveredFromConsumables = checked(
                        manapointsRecoveredFromConsumables + restoredManapoints);
                    if (restoredHitpoints > 0 || restoredManapoints > 0)
                    {
                        AddEvent(events, $"持续恢复：生命 +{restoredHitpoints}，灵力 +{restoredManapoints}");
                    }

                    recovery.RemainingTicks--;
                    if (recovery.RemainingTicks <= 0)
                    {
                        recoveries.RemoveAt(index);
                    }
                    else
                    {
                        recovery.NextTickAt = checked(now + ConsumableRecoveryTickMilliseconds);
                    }
                }
            }

            var nextDamageOverTimeTick = GetNextDamageOverTimeEventTime(now, damageOverTimes);
            if (now >= nextDamageOverTimeTick)
            {
                for (var index = damageOverTimes.Count - 1; index >= 0; index--)
                {
                    var damageOverTime = damageOverTimes[index];
                    if (damageOverTime.NextTickAt > now) continue;

                    var tickDamage = CalculateDamageOverTimeTick(
                        damageOverTime.TotalDamage,
                        damageOverTime.TotalTicks,
                        damageOverTime.CurrentTick);
                    if (tickDamage > 0)
                    {
                        if (damageOverTime.TargetsPlayer && !playerDead && playerHitpoints > 0)
                        {
                            var actualDamage = Math.Min(playerHitpoints, tickDamage);
                            playerHitpoints = Math.Max(0, playerHitpoints - actualDamage);
                            damageTaken = checked(damageTaken + actualDamage);
                            AddEvent(events, $"持续伤害：生命 -{actualDamage}");
                        }
                        else if (!damageOverTime.TargetsPlayer && !enemyDead && enemyHitpoints > 0)
                        {
                            var actualDamage = Math.Min(enemyHitpoints, tickDamage);
                            enemyHitpoints = Math.Max(0, enemyHitpoints - actualDamage);
                            damageDealt = checked(damageDealt + actualDamage);
                            AddEvent(events, $"持续伤害：{{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}} 生命 -{actualDamage}");
                        }
                    }

                    damageOverTime.CurrentTick++;
                    if (damageOverTime.CurrentTick > damageOverTime.TotalTicks
                        || (damageOverTime.TargetsPlayer && playerHitpoints <= 0)
                        || (!damageOverTime.TargetsPlayer && enemyHitpoints <= 0))
                    {
                        damageOverTimes.RemoveAt(index);
                    }
                    else
                    {
                        damageOverTime.NextTickAt = checked(
                            now + Math.Max(1, damageOverTime.TickMilliseconds));
                    }
                }

                if (!enemyDead && enemyHitpoints <= 0)
                {
                    FinalizeEnemyDefeat();
                }

                if (!playerDead && playerHitpoints <= 0)
                {
                    FinalizePlayerDefeat();
                }
            }

            if (playerDead || enemyDead)
            {
                // 恢复和狂暴事件已经在上面处理；死亡状态不生成攻击/消耗品事件。
                continue;
            }

            ApplyConsumables(
                now,
                loadout,
                localInventory,
                consumedItems,
                buffs,
                enemyHitpoints,
                enemy.MaxHitpoints,
                enemyManapoints,
                enemy.MaxManapoints,
                ref playerHitpoints,
                ref playerManapoints,
                player.MaxHitpoints,
                player.MaxManapoints,
                nextConsumableUse,
                recoveries,
                ref hitpointsRecoveredFromConsumables,
                ref manapointsRecoveredFromConsumables,
                events,
                playerControl,
                enemyControl);
            player = BuildPlayerStats(loadout, buffs, playerModifiers, now, playerSpirit);

            if (nextPlayerAction == now && !playerControl.IsStunned(now))
            {
                var ability = pendingPlayerAbility;
                pendingPlayerAbility = null;
                var abilityIndex = pendingPlayerAbilityIndex;
                pendingPlayerAbilityIndex = -1;
                if (ability is null)
                {
                    ability = SelectAbility(
                        loadout,
                        playerAbilities,
                        abilityReadyAt,
                        playerHitpoints,
                        player.MaxHitpoints,
                        playerManapoints,
                        player.MaxManapoints,
                        enemyHitpoints,
                        enemy.MaxHitpoints,
                        enemyManapoints,
                        enemy.MaxManapoints,
                        buffs,
                        playerControl,
                        enemyControl,
                        now,
                        out abilityIndex);
                    if (ability is not null && ability.CastMilliseconds > 0)
                    {
                        pendingPlayerAbility = ability;
                        pendingPlayerAbilityIndex = abilityIndex;
                        var castTime = Math.Max(100, (int)Math.Round(ability.CastMilliseconds * (1.0 - player.ActionSpeedReductionRatio)));
                        nextPlayerAction = checked(now + castTime);
                        playerActionName = ability.Name;
                        playerActionKind = "cast";
                        playerActionStartedAt = now;
                        AddEvent(events, $"施放 {{{{ability:{ability.Id}|{ability.Name}}}}}");
                        ability = null;
                    }
                }

                if (pendingPlayerAbility is null)
                {
                    var resolution = ability is null && playerControl.IsBlinded(now)
                        ? new CombatResolution(0, 0)
                        : ResolvePlayerAttack(player, enemy, ability, ref playerManapoints, ref random);
                    if (ability is null && playerControl.IsBlinded(now))
                    {
                        AddEvent(events, "致盲状态：本次灵力攻击落空");
                    }
                    if (resolution.Damage > 0)
                    {
                        enemyHitpoints = Math.Max(0, enemyHitpoints - resolution.Damage);
                        damageDealt = checked(damageDealt + resolution.Damage);
                        var actionName = ability is null
                            ? "灵击"
                            : $"{{{{ability:{ability.Id}|{ability.Name}}}}}";
                        var critical = resolution.IsCritical ? "{{mechanic:critical|暴击}} " : string.Empty;
                        AddEvent(events, $"{critical}{actionName}命中 {{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}，造成 {resolution.Damage} 点伤害");
                    }
                    else if (!resolution.DidHit)
                    {
                        AddEvent(events, ability is null
                            ? $"灵击未能命中 {{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}"
                            : $"{{{{ability:{ability.Id}|{ability.Name}}}}}未能命中 {{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}");
                    }

                    if (resolution.Healing > 0)
                    {
                        var restored = Math.Min(
                            Math.Max(0, player.MaxHitpoints - playerHitpoints),
                            resolution.Healing);
                        playerHitpoints = checked(playerHitpoints + restored);
                        hitpointsRecoveredFromAbilities = checked(hitpointsRecoveredFromAbilities + restored);
                        AddEvent(events, $"功法恢复：生命 +{restored}");
                    }

                    if (resolution.DrainHealing > 0)
                    {
                        var restored = Math.Min(
                            Math.Max(0, player.MaxHitpoints - playerHitpoints),
                            resolution.DrainHealing);
                        playerHitpoints = checked(playerHitpoints + restored);
                        hitpointsRecoveredFromAbilities = checked(hitpointsRecoveredFromAbilities + restored);
                        if (restored > 0) AddEvent(events, $"汲取恢复：生命 +{restored}");
                    }

                    if (resolution.Pierced)
                    {
                        AddEvent(events, "破阵触发：本次攻击无视目标抗性");
                    }

                    if (ability?.EffectType != XiuxianCombatAbilityEffectType.Heal
                        && ApplyCombatModifierEffects(
                            player,
                            enemy,
                            playerModifiers,
                            enemyModifiers,
                            resolution.DidHit,
                            now,
                            events))
                    {
                        player = BuildPlayerStats(loadout, buffs, playerModifiers, now, playerSpirit);
                        enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
                    }

                    if (ability is not null && resolution.Damage > 0)
                    {
                        ScheduleDamageOverTime(damageOverTimes, ability, resolution.Damage, targetsPlayer: false, now);
                        ApplyControlEffects(
                            ability,
                            enemyControl,
                            enemy.Tenacity,
                            now,
                            ref random,
                            events);
                    if (enemyControl.IsStunned(now))
                    {
                        pendingEnemyAbility = null;
                        pendingEnemyAbilityIndex = -1;
                        nextEnemyAction = Math.Max(nextEnemyAction, enemyControl.StunUntil);
                        enemyActionName = "受控";
                        enemyActionKind = "control";
                        enemyActionStartedAt = now;
                    }
                    }

                    nextPlayerAction = checked(now + player.AttackIntervalMilliseconds);
                    playerActionName = ability is null ? "普通攻击" : ability.Name;
                    playerActionKind = "attack";
                    playerActionStartedAt = now;
                    if (ability is not null)
                    {
                        if (abilityIndex >= 0)
                        {
                            var cooldownTime = Math.Max(1, (int)Math.Round(ability.CooldownMilliseconds * (1.0 - player.CooldownReductionRatio)));
                            abilityReadyAt[abilityIndex] = checked(now + cooldownTime);
                        }
                    }

                    if (enemyHitpoints <= 0)
                    {
                        FinalizeEnemyDefeat();
                        continue;
                    }
                }
            }

            if (nextPlayerAction == now && playerControl.IsStunned(now))
            {
                nextPlayerAction = playerControl.StunUntil;
                playerActionName = "受控";
                playerActionKind = "control";
                playerActionStartedAt = now;
            }

            if (!enemyDead && nextEnemyAction == now && !enemyControl.IsStunned(now))
            {
                var enemyAbility = pendingEnemyAbility;
                pendingEnemyAbility = null;
                var enemyAbilityIndex = pendingEnemyAbilityIndex;
                pendingEnemyAbilityIndex = -1;
                if (enemyAbility is null)
                {
                    enemyAbility = SelectEnemyAbility(
                        enemy,
                        enemyAbilityReadyAt,
                        enemyHitpoints,
                        enemy.MaxHitpoints,
                        enemyManapoints,
                        playerHitpoints,
                        player.MaxHitpoints,
                        playerManapoints,
                        player.MaxManapoints,
                        buffs,
                        enemyControl,
                        playerControl,
                        now,
                        out enemyAbilityIndex);
                    if (enemyAbility is not null && enemyAbility.CastMilliseconds > 0)
                    {
                        pendingEnemyAbility = enemyAbility;
                        pendingEnemyAbilityIndex = enemyAbilityIndex;
                        var enemyCastTime = Math.Max(100, (int)Math.Round(enemyAbility.CastMilliseconds * (1.0 - enemy.ActionSpeedReductionRatio)));
                        nextEnemyAction = checked(now + enemyCastTime);
                        enemyActionName = enemyAbility.Name;
                        enemyActionKind = "cast";
                        enemyActionStartedAt = now;
                        enemyAbility = null;
                    }
                }

                if (pendingEnemyAbility is null)
                {
                    var resolution = enemyAbility is null && enemyControl.IsBlinded(now)
                        ? new CombatResolution(0, 0)
                        : ResolveEnemyAttack(enemy, player, enemyAbility, ref enemyManapoints, ref random);
                    if (enemyAbility is null && enemyControl.IsBlinded(now))
                    {
                        AddEvent(events, $"{{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}处于致盲状态，本次攻击落空");
                    }
                    if (resolution.Damage > 0)
                    {
                        playerHitpoints = Math.Max(0, playerHitpoints - resolution.Damage);
                        damageTaken = checked(damageTaken + resolution.Damage);
                        var actionName = enemyAbility is null
                            ? "攻击"
                            : $"施展 {{{{ability:{enemyAbility.Id}|{enemyAbility.Name}}}}}";
                        var critical = resolution.IsCritical ? "{{mechanic:critical|暴击}} " : string.Empty;
                        AddEvent(events, $"{critical}{{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}{actionName}，造成 {resolution.Damage} 点伤害");
                    }
                    else if (!resolution.DidHit)
                    {
                        AddEvent(events, $"{{{{enemy:{encounter.Enemy.Id}|{encounter.Enemy.Name}}}}}的攻击未能命中");
                    }

                    if (resolution.Healing > 0)
                    {
                        enemyHitpoints = Math.Min(enemy.MaxHitpoints, checked(enemyHitpoints + resolution.Healing));
                    }

                    if (resolution.DrainHealing > 0)
                    {
                        enemyHitpoints = Math.Min(enemy.MaxHitpoints, checked(enemyHitpoints + resolution.DrainHealing));
                    }

                    if (enemyAbility?.EffectType != XiuxianCombatAbilityEffectType.Heal
                        && ApplyCombatModifierEffects(
                            enemy,
                            player,
                            enemyModifiers,
                            playerModifiers,
                            resolution.DidHit,
                            now,
                            events))
                    {
                        player = BuildPlayerStats(loadout, buffs, playerModifiers, now, playerSpirit);
                        enemy = BuildEnemyStats(encounter.Enemy, enemyEnrageStacks, encounter, enemyModifiers);
                    }

                    if (enemyAbility is not null && resolution.Damage > 0)
                    {
                        ScheduleDamageOverTime(damageOverTimes, enemyAbility, resolution.Damage, targetsPlayer: true, now);
                        ApplyControlEffects(
                            enemyAbility,
                            playerControl,
                            player.Tenacity,
                            now,
                            ref random,
                            events);
                        if (playerControl.IsStunned(now))
                        {
                        pendingPlayerAbility = null;
                        pendingPlayerAbilityIndex = -1;
                        nextPlayerAction = Math.Max(nextPlayerAction, playerControl.StunUntil);
                        playerActionName = "受控";
                        playerActionKind = "control";
                        playerActionStartedAt = now;
                    }
                    }

                    if (enemyAbility is not null && enemyAbilityIndex >= 0)
                    {
                        var enemyCooldownTime = Math.Max(1, (int)Math.Round(enemyAbility.CooldownMilliseconds * (1.0 - enemy.CooldownReductionRatio)));
                        enemyAbilityReadyAt[enemyAbilityIndex] = checked(now + enemyCooldownTime);
                    }

                    nextEnemyAction = checked(now + enemy.AttackIntervalMilliseconds);
                    enemyActionName = enemyAbility is null ? "普通攻击" : enemyAbility.Name;
                    enemyActionKind = "attack";
                    enemyActionStartedAt = now;
                    if (playerHitpoints <= 0)
                    {
                        FinalizePlayerDefeat();
                    }
                }
            }

            if (!enemyDead && nextEnemyAction == now && enemyControl.IsStunned(now))
            {
                nextEnemyAction = enemyControl.StunUntil;
                enemyActionName = "受控";
                enemyActionKind = "control";
                enemyActionStartedAt = now;
            }

            if ((stopAfterFirstOutcome && outcomeMilliseconds is not null)
                || (stopAfterFirstVictory && victoryMilliseconds is not null)) break;
        }

        var selectedOutcomeMilliseconds = stopAfterFirstVictory
            ? victoryMilliseconds
            : outcomeMilliseconds;
        var simulatedMilliseconds = selectedOutcomeMilliseconds ?? durationMilliseconds;

        var applications = buffs.Values
            .Where(buff => buff.SourceItemId is not null && buff.ExpiresAtMilliseconds > simulatedMilliseconds)
            .Select(buff => new XiuxianCombatBuffApplication(
                buff.BuffId,
                buff.Name,
                buff.Stacks,
                buff.MaxStacks,
                buff.QiGainMultiplier,
                buff.AttackLevelRatio,
                buff.DamageRatio,
                buff.AttackSpeedRatio,
                buff.SourceItemId!,
                buff.ExpiresAtMilliseconds - simulatedMilliseconds))
            .ToArray();

        var nextEvent = selectedOutcomeMilliseconds is null
            ? GetNextEventAt(durationMilliseconds)
            : InfiniteTime;
        return new XiuxianCombatRunResult(
            encounterId,
            simulatedMilliseconds,
            encounters,
            wins,
            losses,
            damageDealt,
            damageTaken,
            experience,
            spirit,
            droppedQuantity,
            consumedItems,
            applications,
            events,
            maxEnrageStacks,
            hitpointsRegenerated,
            manapointsRegenerated,
            processedEvents,
            hitpointsRecoveredFromConsumables,
            manapointsRecoveredFromConsumables,
            hitpointsRecoveredFromAbilities,
            droppedItems,
            rareDroppedItems,
            wins,
            [new XiuxianCombatEnemyState(
                0,
                encounter.Enemy.Id,
                encounter.Enemy.Name,
                enemyHitpoints,
                enemy.MaxHitpoints,
                enemyManapoints,
                enemy.MaxManapoints,
                enemyDead,
                enemyEnrageStacks)],
            new XiuxianCombatPlayerState(
                playerHitpoints,
                player.MaxHitpoints,
                playerManapoints,
                player.MaxManapoints,
                playerDead),
            CreateActionState(playerActionName, playerActionKind, playerActionStartedAt, nextPlayerAction, simulatedMilliseconds),
            CreateActionState(enemyActionName, enemyActionKind, enemyActionStartedAt, nextEnemyAction, simulatedMilliseconds),
            selectedOutcomeMilliseconds,
            CreatePlayerAbilityCooldowns(loadout, abilityReadyAt, simulatedMilliseconds),
            nextEvent < InfiniteTime ? nextEvent : null,
            CreatePlayerConsumableCooldowns(nextConsumableUse, simulatedMilliseconds));
    }

    private static IReadOnlyDictionary<string, long> CreatePlayerAbilityCooldowns(
        XiuxianCombatLoadout loadout,
        IReadOnlyList<long> abilityReadyAt,
        long simulatedMilliseconds)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var index = 0; index < loadout.AbilitySlots.Count && index < abilityReadyAt.Count; index++)
        {
            var abilityId = loadout.AbilitySlots[index].AbilityId;
            if (string.IsNullOrWhiteSpace(abilityId)) continue;
            var remaining = Math.Max(0, abilityReadyAt[index] - simulatedMilliseconds);
            if (remaining <= 0) continue;
            result[abilityId] = Math.Max(result.GetValueOrDefault(abilityId), remaining);
        }
        return result;
    }

    private static IReadOnlyList<long> CreatePlayerConsumableCooldowns(
        IReadOnlyList<long> nextConsumableUse,
        long simulatedMilliseconds)
    {
        return nextConsumableUse
            .Select(readyAt => Math.Max(0, readyAt - simulatedMilliseconds))
            .ToArray();
    }

    private static XiuxianCombatActionState CreateActionState(
        string name,
        string kind,
        long startedAtMilliseconds,
        long completesAtMilliseconds,
        long simulatedMilliseconds)
    {
        var duration = Math.Max(1, completesAtMilliseconds - startedAtMilliseconds);
        var elapsed = Math.Clamp(simulatedMilliseconds - startedAtMilliseconds, 0, duration);
        return new XiuxianCombatActionState(name, kind, elapsed, duration);
    }

    private static void ApplyConsumables(
        long now,
        XiuxianCombatLoadout loadout,
        IDictionary<string, long> inventory,
        IDictionary<string, long> consumed,
        Dictionary<string, BuffRuntime> buffs,
        long enemyHitpoints,
        long enemyMaxHitpoints,
        long enemyManapoints,
        long enemyMaxManapoints,
        ref long hitpoints,
        ref long manapoints,
        long maxHitpoints,
        long maxManapoints,
        long[] nextConsumableUse,
        ICollection<RecoveryRuntime> recoveries,
        ref long hitpointsRecoveredFromConsumables,
        ref long manapointsRecoveredFromConsumables,
        IList<string> events,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl)
    {
        for (var index = 0; index < loadout.ConsumableSlots.Count; index++)
        {
            var slot = loadout.ConsumableSlots[index];
            if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.ItemId) || now < nextConsumableUse[index]) continue;
            if (!XiuxianItemCatalog.TryGet(slot.ItemId, out var item) || item.Consumable is null) continue;
            var configuredTriggers = slot.Triggers ?? [];
            // 灵食保留旧版行为：允许“战斗开始可用”，且没有触发条件时按血量阈值兜底；
            // 灵饮、符箓等其他品类只看触发条件。
            var isFood = string.Equals(item.Category, "灵食", StringComparison.Ordinal);
            if (isFood && item.Consumable.TriggerAtCombatStart && now == 0)
            {
                // 旧版目录曾用该字段表达“战斗开始可用”，保留它以兼容已有快照。
            }
            else if (!AreTriggersActive(
                         configuredTriggers,
                         item.Consumable.DefaultCombatTriggers,
                         hitpoints,
                         maxHitpoints,
                         manapoints,
                         maxManapoints,
                         enemyHitpoints,
                         enemyMaxHitpoints,
                         enemyManapoints,
                         enemyMaxManapoints,
                         buffs,
                         selfControl,
                         targetControl,
                         now)
                     || (isFood
                         && configuredTriggers.Count == 0
                         && item.Consumable.DefaultCombatTriggers.Count == 0
                         && hitpoints > Math.Floor(maxHitpoints * item.Consumable.TriggerHpRatio))) continue;
            if (TryConsume(
                    slot.ItemId,
                    slot.Quantity,
                    XiuxianActionType.Combat,
                    inventory,
                    consumed,
                    buffs,
                    now,
                    maxHitpoints,
                    maxManapoints,
                    ref hitpoints,
                    ref manapoints,
                    recoveries,
                    ref hitpointsRecoveredFromConsumables,
                    ref manapointsRecoveredFromConsumables,
                    events))
            {
                nextConsumableUse[index] = checked(now + Math.Max(1, item.Consumable.CooldownSeconds * 1_000L));
            }
        }
    }

    private static bool TryConsume(
        string itemId,
        int quantity,
        XiuxianActionType actionType,
        IDictionary<string, long> inventory,
        IDictionary<string, long> consumed,
        Dictionary<string, BuffRuntime> buffs,
        long now,
        long maxHitpoints,
        long maxManapoints,
        ref long hitpoints,
        ref long manapoints,
        ICollection<RecoveryRuntime> recoveries,
        ref long hitpointsRecoveredFromConsumables,
        ref long manapointsRecoveredFromConsumables,
        IList<string> events)
    {
        if (quantity < 1
            || !XiuxianItemCatalog.TryGet(itemId, out var item)
            || item.Consumable is null
            || !item.Consumable.UsableInActionTypes.Contains(actionType)
            || GetAmount(inventory, itemId) < quantity)
        {
            return false;
        }

        inventory[itemId] = GetAmount(inventory, itemId) - quantity;
        consumed[itemId] = checked(GetAmount(consumed, itemId) + quantity);
        var totalHitpointRestore = checked(item.Consumable.HitpointRestore * quantity);
        var totalManapointRestore = checked(item.Consumable.ManapointRestore * quantity);
        if (item.Consumable.RecoverySeconds > 0
            && (totalHitpointRestore > 0 || totalManapointRestore > 0))
        {
            var recoveryDurationMilliseconds = checked(item.Consumable.RecoverySeconds * 1_000L);
            var recoveryTicks = Math.Max(
                1,
                (int)Math.Ceiling(recoveryDurationMilliseconds / (double)ConsumableRecoveryTickMilliseconds));
            recoveries.Add(new RecoveryRuntime(
                checked(now + Math.Min(ConsumableRecoveryTickMilliseconds, recoveryDurationMilliseconds)),
                recoveryTicks,
                totalHitpointRestore,
                totalManapointRestore));
        }
        else
        {
            var restoredHitpoints = Math.Min(
                Math.Max(0, maxHitpoints - hitpoints),
                totalHitpointRestore);
            var restoredManapoints = Math.Min(
                Math.Max(0, maxManapoints - manapoints),
                totalManapointRestore);
            hitpoints = checked(hitpoints + restoredHitpoints);
            manapoints = checked(manapoints + restoredManapoints);
            hitpointsRecoveredFromConsumables = checked(
                hitpointsRecoveredFromConsumables + restoredHitpoints);
            manapointsRecoveredFromConsumables = checked(
                manapointsRecoveredFromConsumables + restoredManapoints);
        }

        foreach (var effect in item.Consumable.Buffs)
        {
            if (!buffs.TryGetValue(effect.BuffId, out var buff))
            {
                buff = new BuffRuntime(
                    effect.BuffId,
                    effect.Name,
                    0,
                    Math.Max(1, effect.MaxStacks),
                    effect.QiGainMultiplier,
                    effect.AttackLevelRatio,
                    effect.DamageRatio,
                    effect.AttackSpeedRatio,
                    itemId,
                    0);
                buffs[effect.BuffId] = buff;
            }

            buff.Stacks = effect.Stackable
                ? Math.Min(buff.MaxStacks, buff.Stacks + 1)
                : 1;
            buff.ExpiresAtMilliseconds = checked(now + Math.Max(1, effect.DurationSeconds * 1_000L));
        }

        if (events.Count < MaxResultEvents)
        {
            events.Add($"使用 {{{{item:{item.Id}|{item.Name}}}}} × {quantity}");
        }

        return true;
    }

    private static Dictionary<string, BuffRuntime> CreateBuffRuntime(
        IReadOnlyList<XiuxianBuffState> activeBuffs,
        DateTimeOffset startedAt)
    {
        var result = new Dictionary<string, BuffRuntime>(StringComparer.Ordinal);
        foreach (var buff in activeBuffs)
        {
            var remaining = (long)Math.Floor((buff.ExpiresAt - startedAt).TotalMilliseconds);
            if (remaining <= 0) continue;
            result[buff.BuffId] = new BuffRuntime(
                buff.BuffId,
                buff.Name,
                Math.Max(1, buff.Stacks),
                Math.Max(1, buff.Stacks),
                buff.QiGainMultiplier,
                buff.AttackLevelRatio,
                buff.DamageRatio,
                buff.AttackSpeedRatio,
                buff.SourceItemId,
                remaining);
        }

        return result;
    }

    private static CombatStats BuildPlayerStats(
        XiuxianCombatLoadout loadout,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatModifierRuntime modifiers,
        long now,
        long spirit = 0)
    {
        var basePool = XiuxianCombatFormula.CalculateBaseAttributes(spirit);
        var equipment = XiuxianCombatCatalog.AggregateEquipmentStats(loadout.Equipment);
        var attackRatio = buffs.Values.Where(buff => buff.ExpiresAtMilliseconds > now).Sum(buff => buff.AttackLevelRatio)
            + equipment.AccuracyRatio
            + modifiers.FuryStacks * (loadout.FuryRatio + equipment.FuryRatio);
        var damageRatio = buffs.Values.Where(buff => buff.ExpiresAtMilliseconds > now).Sum(buff => buff.DamageRatio)
            + equipment.DamageRatio
            + modifiers.FuryStacks * (loadout.FuryRatio + equipment.FuryRatio)
            - modifiers.WeakenStacks * modifiers.WeakenRatioPerStack;
        var attackSpeedRatio = buffs.Values.Where(buff => buff.ExpiresAtMilliseconds > now).Sum(buff => buff.AttackSpeedRatio)
            + equipment.AttackSpeedRatio;
        var stamina = Math.Max(10, loadout.StaminaLevel);
        var intelligence = Math.Max(10, loadout.IntelligenceLevel);
        var attack = Math.Max(10, loadout.AttackLevel);
        var defense = Math.Max(10, loadout.DefenseLevel);
        var swordsmanship = Math.Max(10, loadout.SwordsmanshipLevel);
        var talisman = Math.Max(10, loadout.TalismanLevel);
        var combatStyle = equipment.Style ?? loadout.Style;
        var styleLevel = Math.Max(1, GetStyleLevel(loadout, combatStyle));

        // 六维根基百分比加成乘区 (每点提供 1% 对应的领域增益)
        var staminaBonus = stamina * 0.01;
        var intelligenceBonus = intelligence * 0.01;
        var defenseBonus = defense * 0.01;
        var attackBonus = attack * 0.01;
        var swordsmanshipBonus = swordsmanship * 0.01;
        var talismanBonus = talisman * 0.01;

        // 1. 生命与灵力 (以当前境界基础池为底座，叠加六维乘区加成)
        var maxHitpoints = (long)Math.Max(1, Math.Ceiling(basePool.BaseHitpoints * (1.0 + staminaBonus) * (1.0 + defenseBonus)));
        var maxManapoints = (long)Math.Max(1, Math.Ceiling(basePool.BaseManapoints * (1.0 + intelligenceBonus) * (1.0 + defenseBonus)));

        // 2. 命中与闪避
        var accuracy = (basePool.BaseAccuracy + attack * 0.5 + swordsmanship * 0.5) * (1.0 + swordsmanshipBonus) * Math.Max(0.01, 1 + attackRatio);
        var evasion = (basePool.BaseEvasion + defense * 0.5 + talisman * 0.5) * (1.0 + talismanBonus) * Math.Max(0.01, 1 + equipment.EvasionRatio + attackRatio * 0.5);

        // 3. 攻击力 (根据流派决定物攻/法攻基础池与加成)
        var isMagic = combatStyle == XiuxianCombatStyle.FiveElements;
        var baseDmg = isMagic ? basePool.BaseSpellAttack : basePool.BasePhysicalAttack;
        var scaledAttack = isMagic
            ? (baseDmg + styleLevel * 0.5 + swordsmanship * 0.5) * (1.0 + swordsmanshipBonus * 2.0)
            : (baseDmg + styleLevel * 0.5 + attack * 0.5) * (1.0 + attackBonus);
        var maxDamage = scaledAttack * Math.Max(0.01, 1.0 + damageRatio);

        // 4. 双抗
        var armor = (basePool.BaseArmor + 0.2 * defense + 0.2 * stamina + equipment.Armor) * (1.0 + staminaBonus);
        var waterResistance = (basePool.BaseMagicResistance + 0.2 * defense + 0.2 * intelligence + equipment.WaterResistance) * (1.0 + intelligenceBonus);
        var natureResistance = (basePool.BaseMagicResistance + 0.2 * defense + 0.2 * intelligence + equipment.NatureResistance) * (1.0 + intelligenceBonus);
        var fireResistance = (basePool.BaseMagicResistance + 0.2 * defense + 0.2 * intelligence + equipment.FireResistance) * (1.0 + intelligenceBonus);

        // 5. 破招与拆招 (根骨与身法各提供每点 1% 化解加成，同乘区叠加)
        var breakRating = (basePool.BaseBreak + loadout.BreakRating + equipment.BreakRating + attack * 1.0) * (1.0 + attackBonus);
        var parryRating = (basePool.BaseParry + loadout.ParryRating + equipment.ParryRating + defense * 1.0) * (1.0 + defenseBonus + talismanBonus);

        // 6. 暴击与免爆：以点数参与统一对抗公式 (规约 §7.3)，装备/词条百分比按 K=100 折算为点数；
        //    力道每点 +1% 暴击、身法每点 +1% 免爆折算为对应点数
        var criticalRating = Math.Max(0, basePool.BaseCritical + (loadout.CriticalChance + equipment.CriticalChance) * 100.0 + attack);
        var criticalAvoidanceRating = Math.Max(0, basePool.BaseCriticalAvoidance + (loadout.CriticalAvoidance + equipment.CriticalAvoidance) * 100.0 + talisman);
        var criticalChance = XiuxianCombatFormula.CalculateCritContestRate(criticalRating, criticalAvoidanceRating);
        var criticalAvoidance = XiuxianCombatFormula.CalculateCritContestRate(criticalAvoidanceRating, criticalRating);

        // 7. 回复速率 (每 5 秒基础回复 2% 基础上限，底层属性记录 10 秒比率)
        var baseHpRegenRatio = basePool.BaseHitpoints > 0 ? (basePool.BaseHpRegen * 2.0) / basePool.BaseHitpoints : 0.04;
        var baseMpRegenRatio = basePool.BaseManapoints > 0 ? (basePool.BaseMpRegen * 2.0) / basePool.BaseManapoints : 0.02;
        var hpRegen = Math.Max(0.0, (baseHpRegenRatio + loadout.HpRegenPerTenSeconds) * (1.0 + staminaBonus));
        var mpRegen = Math.Max(0.0, (baseMpRegenRatio + loadout.MpRegenPerTenSeconds) * (1.0 + intelligenceBonus));

        // 8. 攻击间隔
        var baseInterval = equipment.AttackIntervalMilliseconds > 0
            ? equipment.AttackIntervalMilliseconds
            : 1_000;
        var interval = XiuxianCombatFormula.CalculateAttackIntervalMilliseconds(baseInterval, attack, attackSpeedRatio);

        // 9. 高级机制 (高级机制为独立词条属性体系，动态抗性因子 K 由当前阶位决定)
        var cdr = Math.Clamp(loadout.CooldownReductionRatio + equipment.CooldownReductionRatio, 0, 0.75);
        var atr = Math.Clamp(loadout.ActionSpeedReductionRatio + equipment.ActionSpeedRatio, 0, 0.50);
        var critDamageRatio = Math.Max(0.0, loadout.CriticalDamageRatio + equipment.CriticalDamageRatio);
        var skillIntensity = Math.Max(1.0, loadout.SkillIntensity + equipment.SkillIntensity);
        var kFactor = Math.Max((double)basePool.Tier, loadout.DynamicResistanceFactorK);

        return new CombatStats(
            maxHitpoints,
            maxManapoints,
            interval,
            accuracy,
            maxDamage,
            evasion,
            armor,
            waterResistance,
            natureResistance,
            fireResistance,
            combatStyle,
            equipment.DamageType ?? loadout.DamageType,
            null,
            Math.Max(0, loadout.Tenacity + equipment.Tenacity),
            hpRegen,
            mpRegen,
            Math.Max(0, loadout.PenetrationRatio + equipment.PenetrationRatio),
            Math.Clamp(loadout.PierceChance + equipment.PierceChance, 0, 1),
            Math.Max(0, loadout.CurseRatio + equipment.CurseRatio),
            Math.Max(0, loadout.FuryRatio + equipment.FuryRatio),
            Math.Max(0, loadout.WeakenRatio + equipment.WeakenRatio),
            Math.Max(0, loadout.LifeStealRatio + equipment.LifeStealRatio),
            Math.Max(0, loadout.ArmorDamageRatio + equipment.ArmorDamageRatio),
            Math.Max(0, loadout.HpDrainRatio + equipment.HpDrainRatio),
            Math.Max(0, modifiers.CurseStacks * modifiers.CurseRatioPerStack),
            criticalChance,
            critDamageRatio,
            criticalAvoidance,
            Math.Clamp(loadout.DamageAbsorptionRatio + equipment.DamageAbsorptionRatio, 0, 1),
            Math.Max(0, breakRating),
            Math.Max(0, parryRating),
            skillIntensity,
            kFactor,
            atr,
            cdr,
            criticalRating,
            criticalAvoidanceRating);
    }

    private static CombatStats BuildEnemyStats(
        XiuxianCombatantDefinition definition,
        int enrageStacks,
        XiuxianCombatEncounterDefinition encounter,
        CombatModifierRuntime modifiers)
    {
        var enrageRatio = Math.Max(0, enrageStacks) * Math.Max(0, encounter.EnrageRatioPerStack);
        var furyRatio = modifiers.FuryStacks * Math.Max(0, definition.FuryRatio);
        var weakenRatio = modifiers.WeakenStacks * modifiers.WeakenRatioPerStack;
        return new CombatStats(
            definition.MaxHitpoints,
            definition.MaxManapoints,
            definition.AttackIntervalMilliseconds,
            definition.AccuracyRating * Math.Max(0.01, 1 + enrageRatio + furyRatio),
            definition.MaxDamage * Math.Max(0.01, 1 + enrageRatio + furyRatio - weakenRatio),
            definition.DefensiveMaxDamage,
            definition.Armor,
            definition.WaterResistance,
            definition.NatureResistance,
            definition.FireResistance,
            definition.Style,
            definition.DamageType,
            definition,
            Math.Max(0, definition.Tenacity),
            Math.Max(0, definition.HpRegenPerTenSeconds),
            Math.Max(0, definition.MpRegenPerTenSeconds),
            0,
            Math.Clamp(definition.PierceChance, 0, 1),
            Math.Max(0, definition.CurseRatio),
            Math.Max(0, definition.FuryRatio),
            Math.Max(0, definition.WeakenRatio),
            Math.Max(0, definition.LifeStealRatio),
            Math.Max(0, definition.ArmorDamageRatio),
            Math.Max(0, definition.HpDrainRatio),
            Math.Max(0, modifiers.CurseStacks * modifiers.CurseRatioPerStack),
            Math.Clamp(definition.CriticalChance, 0, 1),
            Math.Max(0, definition.CriticalDamageRatio),
            Math.Clamp(definition.CriticalAvoidance, 0, 1),
            Math.Clamp(definition.DamageAbsorptionRatio, 0, 1),
            Math.Max(0, definition.BreakRating),
            Math.Max(0, definition.ParryRating),
            Math.Max(1.0, definition.SkillIntensity),
            Math.Max(1.0, definition.DynamicResistanceFactorK),
            Math.Clamp(definition.ActionSpeedReductionRatio, 0, 0.99),
            Math.Clamp(definition.CooldownReductionRatio, 0, 0.99),
            Math.Max(0, definition.CriticalChance * 100.0),
            Math.Max(0, definition.CriticalAvoidance * 100.0));
    }

    private static CombatResolution ResolvePlayerAttack(
        CombatStats source,
        CombatStats target,
        XiuxianCombatAbilityDefinition? ability,
        ref long mana,
        ref DeterministicRandom random)
    {
        if (ability is null || mana < ability.ManaCost)
        {
            return ResolveDamage(source, target, null, ref random);
        }

        mana -= ability.ManaCost;
        return ability.EffectType == XiuxianCombatAbilityEffectType.Heal
            ? new CombatResolution(
                0,
                Math.Max(0, checked((long)Math.Floor(
                    source.MaxDamage * Math.Max(0, ability.HealingRatio) + ability.FlatHealing))))
            : ResolveDamage(source, target, ability, ref random);
    }

    private static CombatResolution ResolveEnemyAttack(
        CombatStats source,
        CombatStats target,
        XiuxianCombatAbilityDefinition? ability,
        ref long mana,
        ref DeterministicRandom random)
    {
        if (ability is null || mana < ability.ManaCost)
        {
            return ResolveDamage(source, target, null, ref random);
        }

        mana -= ability.ManaCost;
        return ability.EffectType == XiuxianCombatAbilityEffectType.Heal
            ? new CombatResolution(
                0,
                Math.Max(0, checked((long)Math.Floor(
                    source.MaxDamage * Math.Max(0, ability.HealingRatio) + ability.FlatHealing))))
            : ResolveDamage(source, target, ability, ref random);
    }

    private static CombatResolution ResolveDamage(
        CombatStats source,
        CombatStats target,
        XiuxianCombatAbilityDefinition? ability,
        ref DeterministicRandom random)
    {
        // 1. 破招 vs 拆招 对抗判定
        var isBreak = false;
        var isParry = false;
        if (source.BreakRating > target.ParryRating && source.BreakRating > 0)
        {
            var breakRate = XiuxianCombatFormula.CalculateContestRate(source.BreakRating, target.ParryRating);
            if (random.NextDouble() < breakRate)
            {
                isBreak = true;
            }
        }
        else if (target.ParryRating > source.BreakRating && target.ParryRating > 0)
        {
            var parryRate = XiuxianCombatFormula.CalculateContestRate(target.ParryRating, source.BreakRating);
            if (random.NextDouble() < parryRate)
            {
                isParry = true;
            }
        }

        // 破招成功：命中与暴击点数翻倍后再进对抗；拆招成功：闪避点数与抗性效果翻倍
        var effectiveHit = source.AccuracyRating * (isBreak ? XiuxianCombatConstants.BreakSuccessEffectMultiplier : 1.0);
        var effectiveCrit = Math.Max(0.0, source.CriticalRating) * (isBreak ? XiuxianCombatConstants.BreakSuccessEffectMultiplier : 1.0);
        var effectiveEva = target.EvasionRating * (isParry ? XiuxianCombatConstants.ParrySuccessEffectMultiplier : 1.0);

        // 2. 命中 vs 闪避 统一对抗判定 (规约 §7.3)
        var hitChance = XiuxianCombatFormula.CalculateHitContestRate(effectiveHit, effectiveEva);
        if (random.NextDouble() > hitChance)
        {
            return new CombatResolution(0, 0, DidHit: false);
        }

        // 3. 基础伤害与技能强度放大
        var maximum = Math.Max(1, (long)Math.Floor(source.MaxDamage));
        var minimum = Math.Max(1, maximum / 2);
        var rolled = random.NextInt64(minimum, checked(maximum + 1));
        var damageRatio = ability?.DamageRatio ?? 1;
        var flatDamage = ability?.FlatDamage ?? 0;
        var armorDamage = source.Armor * Math.Max(0, source.ArmorDamageRatio + (ability?.ArmorDamageRatio ?? 0));
        var skillIntensity = Math.Max(1.0, source.SkillIntensity);
        var amplifiedDouble = (rolled * Math.Max(0, damageRatio) + flatDamage) * skillIntensity + armorDamage;

        // 4. 抗性穿透与动态抗性平方减伤公式
        var damageType = ability?.DamageType ?? source.DamageType;
        var pierceChance = Math.Clamp(source.PierceChance + (ability?.PierceChance ?? 0), 0, 1);
        var pierced = pierceChance > 0 && random.NextDouble() < pierceChance;
        var baseResistance = pierced ? 0 : GetResistance(target, damageType);
        var effectiveResistance = baseResistance * (isParry ? XiuxianCombatConstants.ParrySuccessEffectMultiplier : 1.0);
        var penetrationRatio = Math.Max(0, source.PenetrationRatio + (ability?.PenetrationRatio ?? 0));
        var kFactor = Math.Max(1.0, source.DynamicResistanceFactorK);

        var rawDamageLong = amplifiedDouble >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1, (long)Math.Floor(amplifiedDouble));
        var mitigated = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(
            rawDamageLong,
            effectiveResistance,
            penetrationRatio,
            kFactor,
            XiuxianCombatConstants.DefaultDamageBaseFactor);

        // 5. 暴击 vs 免爆 统一对抗判定 (规约 §7.3) 与伤害放大
        var criticalChance = XiuxianCombatFormula.CalculateCritContestRate(effectiveCrit, target.CriticalAvoidanceRating);
        var isCritical = source.CriticalDamageRatio > 0
            && criticalChance > 0
            && random.NextDouble() < criticalChance;
        var critMultiplier = XiuxianCombatConstants.BaseCriticalDamageMultiplier + source.CriticalDamageRatio;
        var withCrit = isCritical
            ? mitigated * critMultiplier
            : (double)mitigated;

        // 6. 易伤、减伤与伤害吸收
        var withDamageTaken = withCrit * Math.Max(0, 1 + target.DamageTakenRatio);
        var absorbed = XiuxianCombatFormula.ApplyDamageAbsorption(
            withDamageTaken,
            target.DamageAbsorptionRatio);
        var damage = absorbed >= long.MaxValue
            ? long.MaxValue
            : Math.Max(0, (long)Math.Ceiling(absorbed));

        // 7. 吸血与汲取
        var drainRatio = ability is null
            ? source.LifeStealRatio
            : source.HpDrainRatio + ability.HpDrainRatio;
        var drainHealing = damage > 0 && drainRatio > 0
            ? Math.Max(0, (long)Math.Floor(damage * drainRatio))
            : 0;
        return new CombatResolution(damage, 0, true, drainHealing, pierced, isCritical);
    }

    private static double GetResistance(CombatStats target, XiuxianDamageType damageType)
    {
        return damageType switch
        {
            XiuxianDamageType.Water => target.WaterResistance,
            XiuxianDamageType.Nature => target.NatureResistance,
            XiuxianDamageType.Fire => target.FireResistance,
            _ => target.Armor
        };
    }

    private static XiuxianCombatAbilityDefinition? SelectAbility(
        XiuxianCombatLoadout loadout,
        IReadOnlyList<XiuxianCombatAbilityDefinition?> abilities,
        IReadOnlyList<long> readyAt,
        long hitpoints,
        long maxHitpoints,
        long manapoints,
        long maxManapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (selfControl.IsStunned(now) || selfControl.IsSilenced(now)) return null;
        for (var index = 0; index < loadout.AbilitySlots.Count; index++)
        {
            var slot = loadout.AbilitySlots[index];
            var configuredTriggers = slot.Triggers ?? [];
            var ability = abilities[index];
            if (ability is null || now < readyAt[index])
            {
                continue;
            }
            if (manapoints < ability.ManaCost
                || !AreTriggersActive(
                    configuredTriggers,
                    ability.DefaultTriggers,
                    hitpoints,
                    maxHitpoints,
                    manapoints,
                    maxManapoints,
                    targetHitpoints,
                    targetMaxHitpoints,
                    targetManapoints,
                    targetMaxManapoints,
                    buffs,
                    selfControl,
                    targetControl,
                    now)
                || (configuredTriggers.Count == 0
                    && ability.DefaultTriggers.Count == 0
                    && hitpoints > Math.Floor(maxHitpoints * ability.TriggerHpRatio)))
            {
                continue;
            }

            selectedIndex = index;
            return ability;
        }

        return null;
    }

    private static XiuxianCombatAbilityDefinition? SelectEnemyAbility(
        CombatStats enemy,
        IReadOnlyList<long> readyAt,
        long selfHitpoints,
        long selfMaxHitpoints,
        long manapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (enemy.Definition is null) return null;

        for (var index = 0; index < enemy.Definition.Abilities.Count; index++)
        {
            var ability = enemy.Definition.Abilities[index];
            if (now < readyAt[index]
                || manapoints < ability.ManaCost
                || !AreTriggersActive(
                    [],
                    ability.DefaultTriggers,
                    selfHitpoints,
                    selfMaxHitpoints,
                    manapoints,
                    enemy.MaxManapoints,
                    targetHitpoints,
                    targetMaxHitpoints,
                    targetManapoints,
                    targetMaxManapoints,
                    buffs,
                    selfControl,
                    targetControl,
                    now)) continue;

            selectedIndex = index;
            return ability;
        }

        return null;
    }

    private static bool AreTriggersActive(
        IReadOnlyList<XiuxianCombatTrigger>? configured,
        IReadOnlyList<XiuxianCombatTrigger> defaults,
        long selfHitpoints,
        long selfMaxHitpoints,
        long selfManapoints,
        long selfMaxManapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now)
    {
        var triggers = configured is { Count: > 0 } ? configured : defaults;
        if (triggers.Count == 0) return true;

        foreach (var trigger in triggers)
        {
            // 友方极值依赖在当前单人战斗拓扑下解析为玩家自身；敌方极值依赖解析为当前目标。
            // 组队战斗接入后，极值依赖应改为在对应队伍存活成员集合上求值。
            var isSelf = trigger.Dependency is XiuxianCombatTriggerDependency.Self
                or XiuxianCombatTriggerDependency.AllyLowestHitpoints
                or XiuxianCombatTriggerDependency.AllyHighestHitpoints;
            var currentHitpoints = isSelf ? selfHitpoints : targetHitpoints;
            var maxHitpoints = isSelf ? selfMaxHitpoints : targetMaxHitpoints;
            var currentManapoints = isSelf ? selfManapoints : targetManapoints;
            var maxManapoints = isSelf ? selfMaxManapoints : targetMaxManapoints;
            var control = isSelf ? selfControl : targetControl;
            var value = trigger.Condition switch
            {
                XiuxianCombatTriggerCondition.CurrentHitpoints => currentHitpoints,
                XiuxianCombatTriggerCondition.CurrentManapoints => currentManapoints,
                XiuxianCombatTriggerCondition.MissingHitpoints => Math.Max(0, maxHitpoints - currentHitpoints),
                XiuxianCombatTriggerCondition.MissingManapoints => Math.Max(0, maxManapoints - currentManapoints),
                XiuxianCombatTriggerCondition.BuffStacks => GetBuffStacks(
                    buffs,
                    trigger.BuffId,
                    now),
                XiuxianCombatTriggerCondition.CurrentHitpointsPercentage =>
                    XiuxianCombatFormula.CalculatePercentage(currentHitpoints, maxHitpoints),
                XiuxianCombatTriggerCondition.MissingHitpointsPercentage =>
                    XiuxianCombatFormula.CalculatePercentage(Math.Max(0, maxHitpoints - currentHitpoints), maxHitpoints),
                XiuxianCombatTriggerCondition.CurrentManapointsPercentage =>
                    XiuxianCombatFormula.CalculatePercentage(currentManapoints, maxManapoints),
                XiuxianCombatTriggerCondition.MissingManapointsPercentage =>
                    XiuxianCombatFormula.CalculatePercentage(Math.Max(0, maxManapoints - currentManapoints), maxManapoints),
                XiuxianCombatTriggerCondition.StunStatus =>
                    control.IsStunned(now) || control.WasStunExpiredAt(now) ? 1 : 0,
                XiuxianCombatTriggerCondition.BlindStatus =>
                    control.IsBlinded(now) || control.WasBlindExpiredAt(now) ? 1 : 0,
                XiuxianCombatTriggerCondition.SilenceStatus =>
                    control.IsSilenced(now) || control.WasSilenceExpiredAt(now) ? 1 : 0,
                _ => 0
            };

            var active = value > 0;
            var matches = trigger.Comparator switch
            {
                XiuxianCombatTriggerComparator.LessThanOrEqual => value <= trigger.Value,
                XiuxianCombatTriggerComparator.GreaterThanOrEqual => value >= trigger.Value,
                XiuxianCombatTriggerComparator.LessThan => value < trigger.Value,
                XiuxianCombatTriggerComparator.GreaterThan => value > trigger.Value,
                XiuxianCombatTriggerComparator.IsActive => active,
                XiuxianCombatTriggerComparator.IsInactive => !active,
                _ => false
            };
            if (!matches) return false;
        }

        return true;
    }

    private static long GetBuffStacks(
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        string? buffId,
        long now)
    {
        return !string.IsNullOrWhiteSpace(buffId)
            && buffs.TryGetValue(buffId, out var buff)
            && buff.ExpiresAtMilliseconds > now
            ? buff.Stacks
            : 0;
    }

    private static int GetStyleLevel(XiuxianCombatLoadout loadout, XiuxianCombatStyle style)
    {
        return style switch
        {
            XiuxianCombatStyle.Sword => loadout.SwordsmanshipLevel,
            XiuxianCombatStyle.Talisman => loadout.TalismanLevel,
            XiuxianCombatStyle.FiveElements => loadout.FiveElementsLevel,
            _ => loadout.AttackLevel
        };
    }

    private static void AddEvent(ICollection<string> events, string message)
    {
        if (events.Count < MaxResultEvents) events.Add(message);
    }

    private static long GetNextConsumableEventTime(
        long now,
        IReadOnlyList<long> nextConsumableUse)
    {
        var next = InfiniteTime;
        foreach (var timestamp in nextConsumableUse)
        {
            if (timestamp > now) next = Math.Min(next, timestamp);
        }

        return next;
    }

    private static long GetNextRecoveryEventTime(
        long now,
        IReadOnlyList<RecoveryRuntime> recoveries)
    {
        var next = InfiniteTime;
        foreach (var recovery in recoveries)
        {
            if (recovery.NextTickAt >= now) next = Math.Min(next, recovery.NextTickAt);
        }

        return next;
    }

    private static long GetNextDamageOverTimeEventTime(
        long now,
        IReadOnlyList<DamageOverTimeRuntime> damageOverTimes)
    {
        var next = InfiniteTime;
        foreach (var damageOverTime in damageOverTimes)
        {
            if (damageOverTime.NextTickAt >= now)
            {
                next = Math.Min(next, damageOverTime.NextTickAt);
            }
        }

        return next;
    }

    private static long GetNextControlEventTime(
        long now,
        CombatControlRuntime playerControl,
        CombatControlRuntime enemyControl)
    {
        var next = Math.Min(playerControl.NextExpirationAt, enemyControl.NextExpirationAt);
        return next > now ? next : InfiniteTime;
    }

    private static long GetNextModifierEventTime(
        long now,
        CombatModifierRuntime playerModifiers,
        CombatModifierRuntime enemyModifiers)
    {
        var next = Math.Min(playerModifiers.NextExpirationAt, enemyModifiers.NextExpirationAt);
        return next > now ? next : InfiniteTime;
    }

    private static bool ApplyCombatModifierEffects(
        CombatStats source,
        CombatStats target,
        CombatModifierRuntime sourceModifiers,
        CombatModifierRuntime targetModifiers,
        bool didHit,
        long now,
        IList<string> events)
    {
        var changed = false;
        if (source.FuryRatio > 0)
        {
            var before = sourceModifiers.FuryStacks;
            sourceModifiers.ApplyFury(didHit, source.FuryRatio, now, CombatModifierDurationMilliseconds);
            var furyChanged = Math.Abs(before - sourceModifiers.FuryStacks) > 0.000_001;
            changed |= furyChanged;
            if (furyChanged)
            {
                AddEvent(events, $"沸血：{sourceModifiers.FuryStacks:0.##} 层");
            }
        }

        if (didHit && source.CurseRatio > 0)
        {
            targetModifiers.ApplyCurse(source.CurseRatio, now, CombatModifierDurationMilliseconds);
            changed = true;
            AddEvent(events, $"蚀心：{targetModifiers.CurseStacks} 层");
        }

        if (target.WeakenRatio > 0)
        {
            sourceModifiers.ApplyWeaken(target.WeakenRatio, now, CombatModifierDurationMilliseconds);
            changed = true;
            AddEvent(events, $"衰弱：{sourceModifiers.WeakenStacks} 层");
        }

        return changed;
    }

    private static void ApplyControlEffects(
        XiuxianCombatAbilityDefinition ability,
        CombatControlRuntime target,
        double targetTenacity,
        long now,
        ref DeterministicRandom random,
        IList<string> events)
    {
        if (ability.StunMilliseconds > 0
            && RollControlChance(ability.StunChance, targetTenacity, ref random))
        {
            target.ApplyStun(now, ability.StunMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：定身 {FormatDuration(ability.StunMilliseconds)}");
        }

        if (ability.BlindMilliseconds > 0
            && RollControlChance(ability.BlindChance, targetTenacity, ref random))
        {
            target.ApplyBlind(now, ability.BlindMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：致盲 {FormatDuration(ability.BlindMilliseconds)}");
        }

        if (ability.SilenceMilliseconds > 0
            && RollControlChance(ability.SilenceChance, targetTenacity, ref random))
        {
            target.ApplySilence(now, ability.SilenceMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：沉默 {FormatDuration(ability.SilenceMilliseconds)}");
        }
    }

    private static bool RollControlChance(
        double baseChance,
        double targetTenacity,
        ref DeterministicRandom random)
    {
        return random.NextDouble() < XiuxianCombatFormula.CalculateControlChance(baseChance, targetTenacity);
    }

    private static string FormatDuration(int milliseconds)
    {
        return $"{milliseconds / 1_000d:0.##}秒";
    }

    private static void ScheduleDamageOverTime(
        ICollection<DamageOverTimeRuntime> damageOverTimes,
        XiuxianCombatAbilityDefinition ability,
        long directDamage,
        bool targetsPlayer,
        long now)
    {
        if (ability.DamageOverTimeRatio <= 0 || ability.DamageOverTimeMilliseconds <= 0) return;
        var tickMilliseconds = ability.DamageOverTimeTickMilliseconds > 0
            ? ability.DamageOverTimeTickMilliseconds
            : DamageOverTimeTickMilliseconds;
        var totalTicks = Math.Max(1, (int)Math.Ceiling(
            ability.DamageOverTimeMilliseconds / (double)tickMilliseconds));
        var totalDamage = Math.Max(0, (long)Math.Floor(directDamage * ability.DamageOverTimeRatio));
        if (totalDamage <= 0) return;

        damageOverTimes.Add(new DamageOverTimeRuntime(
            checked(now + tickMilliseconds),
            totalDamage,
            totalTicks,
            1,
            tickMilliseconds,
            targetsPlayer));
    }

    private static long CalculateDamageOverTimeTick(
        long totalDamage,
        int totalTicks,
        int currentTick)
    {
        if (totalDamage <= 0 || totalTicks <= 0 || currentTick <= 0) return 0;
        var currentSum = (long)Math.Floor(currentTick * (double)totalDamage / totalTicks);
        var previousSum = (long)Math.Floor((currentTick - 1) * (double)totalDamage / totalTicks);
        return Math.Max(0, currentSum - previousSum);
    }

    private static IReadOnlyDictionary<string, long> RollDrops(
        XiuxianCombatEncounterDefinition encounter,
        bool rare,
        double dropRateMultiplier,
        double rareFindMultiplier,
        double dropQuantityMultiplier,
        ref DeterministicRandom random,
        ref long droppedQuantity)
    {
        var table = rare ? encounter.RareDropTable : encounter.DropTable;
        if (table.Count == 0 && !rare && !string.IsNullOrWhiteSpace(encounter.DropItemId))
        {
            table =
            [
                new XiuxianCombatDropDefinition
                {
                    ItemId = encounter.DropItemId,
                    DropChance = encounter.DropChance,
                    MinQuantity = encounter.DropQuantity,
                    MaxQuantity = encounter.DropQuantity
                }
            ];
        }

        Dictionary<string, long>? result = null;
        foreach (var drop in table)
        {
            var chanceMultiplier = rare ? rareFindMultiplier : dropRateMultiplier;
            var chance = Math.Clamp(drop.DropChance * chanceMultiplier, 0, 1);
            if (chance <= 0 || random.NextDouble() >= chance) continue;
            var min = Math.Max(0, drop.MinQuantity);
            var max = Math.Max(min, drop.MaxQuantity);
            var quantity = min == max
                ? min
                : random.NextInt64(min, checked(max + 1));
            quantity = ScaleDropQuantity(quantity, dropQuantityMultiplier, ref random);
            if (quantity <= 0) continue;
            result ??= new Dictionary<string, long>(StringComparer.Ordinal);
            AddDrop(result, drop.ItemId, quantity, ref droppedQuantity);
        }

        return result ?? EmptyDrops;
    }

    private static long ScaleDropQuantity(
        long quantity,
        double quantityMultiplier,
        ref DeterministicRandom random)
    {
        if (quantity <= 0 || quantityMultiplier <= 0) return 0;
        if (quantityMultiplier == 1) return quantity;

        var scaled = quantity * quantityMultiplier;
        if (scaled >= long.MaxValue) return long.MaxValue;
        var whole = (long)Math.Floor(scaled);
        var fraction = scaled - whole;
        if (fraction > 0 && random.NextDouble() < fraction)
        {
            whole = checked(whole + 1);
        }

        return Math.Max(0, whole);
    }

    private static double NormalizeCombatMultiplier(double bonus)
    {
        return !double.IsFinite(bonus)
            ? 1
            : Math.Max(0, 1 + Math.Clamp(bonus, -1, 1_000));
    }

    private static void MergeDrops(
        IDictionary<string, long> destination,
        IReadOnlyDictionary<string, long> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = checked(
                (destination.TryGetValue(pair.Key, out var existing) ? existing : 0) + pair.Value);
        }
    }

    private static void AddDrop(
        IDictionary<string, long> destination,
        string itemId,
        long quantity,
        ref long droppedQuantity)
    {
        destination[itemId] = checked(
            (destination.TryGetValue(itemId, out var existing) ? existing : 0) + quantity);
        droppedQuantity = checked(droppedQuantity + quantity);
    }

    private static readonly IReadOnlyDictionary<string, long> EmptyDrops =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private static string FormatDropSummary(
        IReadOnlyDictionary<string, long> regularDrops,
        IReadOnlyDictionary<string, long> rareDrops)
    {
        if (regularDrops.Count == 0 && rareDrops.Count == 0) return string.Empty;

        var parts = new List<string>(regularDrops.Count + rareDrops.Count);
        foreach (var pair in regularDrops)
        {
            if (XiuxianItemCatalog.TryGet(pair.Key, out var item))
            {
                parts.Add($"{{{{item:{pair.Key}|{item.Name}|{pair.Value}}}}}");
            }
        }

        foreach (var pair in rareDrops)
        {
            if (XiuxianItemCatalog.TryGet(pair.Key, out var item))
            {
                parts.Add($"稀有 {{{{item:{pair.Key}|{item.Name}|{pair.Value}}}}}");
            }
        }

        return parts.Count == 0 ? string.Empty : $"，掉落：{string.Join("、", parts)}";
    }

    private static long TakeRecoveryShare(ref long remaining, int remainingTicks)
    {
        if (remaining <= 0 || remainingTicks <= 0) return 0;
        var share = remaining / remainingTicks;
        if (remaining % remainingTicks != 0) share++;
        remaining -= share;
        return share;
    }

    private static long GetAmount(IReadOnlyDictionary<string, long> amounts, string itemId)
    {
        return amounts.TryGetValue(itemId, out var amount) ? amount : 0;
    }

    private static long GetAmount(IDictionary<string, long> amounts, string itemId)
    {
        return amounts.TryGetValue(itemId, out var amount) ? amount : 0;
    }

    private static ulong CreateSeed(Guid playerId, DateTimeOffset startedAt, string encounterId)
    {
        var bytes = playerId.ToByteArray();
        ulong seed = 1469598103934665603UL;
        foreach (var value in bytes)
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        foreach (var value in BitConverter.GetBytes(startedAt.ToUnixTimeMilliseconds()))
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        foreach (var value in encounterId)
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        return seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    private sealed class BuffRuntime
    {
        public BuffRuntime(
            string buffId,
            string name,
            int stacks,
            int maxStacks,
            double qiGainMultiplier,
            double attackLevelRatio,
            double damageRatio,
            double attackSpeedRatio,
            string? sourceItemId,
            long expiresAtMilliseconds)
        {
            BuffId = buffId;
            Name = name;
            Stacks = stacks;
            MaxStacks = maxStacks;
            QiGainMultiplier = qiGainMultiplier;
            AttackLevelRatio = attackLevelRatio;
            DamageRatio = damageRatio;
            AttackSpeedRatio = attackSpeedRatio;
            SourceItemId = sourceItemId;
            ExpiresAtMilliseconds = expiresAtMilliseconds;
        }

        public string BuffId { get; }
        public string Name { get; }
        public int Stacks { get; set; }
        public int MaxStacks { get; }
        public double QiGainMultiplier { get; }
        public double AttackLevelRatio { get; }
        public double DamageRatio { get; }
        public double AttackSpeedRatio { get; }
        public string? SourceItemId { get; }
        public long ExpiresAtMilliseconds { get; set; }
    }

    /// <summary>
    /// 战斗内短时词缀状态。每个方向独立维护，死亡/重生时清空；15 秒刷新边界与公开模拟器一致。
    /// </summary>
    private sealed class CombatModifierRuntime
    {
        public double FuryStacks { get; private set; }
        public int CurseStacks { get; private set; }
        public int WeakenStacks { get; private set; }
        public double CurseRatioPerStack { get; private set; }
        public double WeakenRatioPerStack { get; private set; }
        public long NextExpirationAt { get; private set; } = InfiniteTime;

        private long FuryUntil { get; set; }
        private long CurseUntil { get; set; }
        private long WeakenUntil { get; set; }

        public void ApplyFury(bool didHit, double ratio, long now, int durationMilliseconds)
        {
            FuryStacks = didHit
                ? Math.Min(5, FuryStacks + 1)
                : FuryStacks / 2;
            if (FuryStacks <= 0)
            {
                FuryStacks = 0;
                FuryUntil = 0;
            }
            else
            {
                FuryUntil = checked(now + Math.Max(1, durationMilliseconds));
            }

            RecalculateNextExpiration(now);
        }

        public void ApplyCurse(double ratio, long now, int durationMilliseconds)
        {
            CurseStacks = Math.Min(5, CurseStacks + 1);
            CurseRatioPerStack = Math.Max(CurseRatioPerStack, Math.Max(0, ratio));
            CurseUntil = checked(now + Math.Max(1, durationMilliseconds));
            RecalculateNextExpiration(now);
        }

        public void ApplyWeaken(double ratio, long now, int durationMilliseconds)
        {
            WeakenStacks = Math.Min(5, WeakenStacks + 1);
            WeakenRatioPerStack = Math.Max(WeakenRatioPerStack, Math.Max(0, ratio));
            WeakenUntil = checked(now + Math.Max(1, durationMilliseconds));
            RecalculateNextExpiration(now);
        }

        public bool Expire(long now)
        {
            var changed = false;
            if (FuryUntil > 0 && FuryUntil <= now)
            {
                FuryUntil = 0;
                FuryStacks = 0;
                changed = true;
            }

            if (CurseUntil > 0 && CurseUntil <= now)
            {
                CurseUntil = 0;
                CurseStacks = 0;
                CurseRatioPerStack = 0;
                changed = true;
            }

            if (WeakenUntil > 0 && WeakenUntil <= now)
            {
                WeakenUntil = 0;
                WeakenStacks = 0;
                WeakenRatioPerStack = 0;
                changed = true;
            }

            RecalculateNextExpiration(now);
            return changed;
        }

        public void Clear()
        {
            FuryStacks = 0;
            CurseStacks = 0;
            WeakenStacks = 0;
            CurseRatioPerStack = 0;
            WeakenRatioPerStack = 0;
            FuryUntil = 0;
            CurseUntil = 0;
            WeakenUntil = 0;
            NextExpirationAt = InfiniteTime;
        }

        private void RecalculateNextExpiration(long now)
        {
            NextExpirationAt = InfiniteTime;
            if (FuryUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, FuryUntil);
            if (CurseUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, CurseUntil);
            if (WeakenUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, WeakenUntil);
        }
    }

    private sealed class CombatControlRuntime
    {
        public long StunUntil { get; private set; }
        public long BlindUntil { get; private set; }
        public long SilenceUntil { get; private set; }
        public long NextExpirationAt { get; private set; } = InfiniteTime;
        private long StunExpiredAt { get; set; }
        private long BlindExpiredAt { get; set; }
        private long SilenceExpiredAt { get; set; }

        public bool IsStunned(long now) => StunUntil > now;
        public bool IsBlinded(long now) => BlindUntil > now;
        public bool IsSilenced(long now) => SilenceUntil > now;
        public bool WasStunExpiredAt(long now) => StunExpiredAt == now;
        public bool WasBlindExpiredAt(long now) => BlindExpiredAt == now;
        public bool WasSilenceExpiredAt(long now) => SilenceExpiredAt == now;

        public void ApplyStun(long now, int durationMilliseconds)
        {
            StunExpiredAt = 0;
            StunUntil = Math.Max(StunUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, StunUntil);
        }

        public void ApplyBlind(long now, int durationMilliseconds)
        {
            BlindExpiredAt = 0;
            BlindUntil = Math.Max(BlindUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, BlindUntil);
        }

        public void ApplySilence(long now, int durationMilliseconds)
        {
            SilenceExpiredAt = 0;
            SilenceUntil = Math.Max(SilenceUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, SilenceUntil);
        }

        public bool Expire(long now)
        {
            if (NextExpirationAt > now) return false;

            var changed = false;
            if (StunUntil > 0 && StunUntil <= now)
            {
                StunExpiredAt = StunUntil;
                StunUntil = 0;
                changed = true;
            }

            if (BlindUntil > 0 && BlindUntil <= now)
            {
                BlindExpiredAt = BlindUntil;
                BlindUntil = 0;
                changed = true;
            }

            if (SilenceUntil > 0 && SilenceUntil <= now)
            {
                SilenceExpiredAt = SilenceUntil;
                SilenceUntil = 0;
                changed = true;
            }

            NextExpirationAt = InfiniteTime;
            if (StunUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, StunUntil);
            if (BlindUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, BlindUntil);
            if (SilenceUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, SilenceUntil);

            return changed;
        }

        public void Clear()
        {
            StunUntil = 0;
            BlindUntil = 0;
            SilenceUntil = 0;
            StunExpiredAt = 0;
            BlindExpiredAt = 0;
            SilenceExpiredAt = 0;
            NextExpirationAt = InfiniteTime;
        }
    }

    private sealed class RecoveryRuntime
    {
        public RecoveryRuntime(
            long nextTickAt,
            int remainingTicks,
            long remainingHitpoints,
            long remainingManapoints)
        {
            NextTickAt = nextTickAt;
            RemainingTicks = remainingTicks;
            RemainingHitpoints = remainingHitpoints;
            RemainingManapoints = remainingManapoints;
        }

        public long NextTickAt { get; set; }
        public int RemainingTicks { get; set; }
        public long RemainingHitpoints { get; set; }
        public long RemainingManapoints { get; set; }
    }

    private sealed class DamageOverTimeRuntime
    {
        public DamageOverTimeRuntime(
            long nextTickAt,
            long totalDamage,
            int totalTicks,
            int currentTick,
            int tickMilliseconds,
            bool targetsPlayer)
        {
            NextTickAt = nextTickAt;
            TotalDamage = totalDamage;
            TotalTicks = totalTicks;
            CurrentTick = currentTick;
            TickMilliseconds = tickMilliseconds;
            TargetsPlayer = targetsPlayer;
        }

        public long NextTickAt { get; set; }
        public long TotalDamage { get; }
        public int TotalTicks { get; }
        public int CurrentTick { get; set; }
        public int TickMilliseconds { get; }
        public bool TargetsPlayer { get; }
    }

    private sealed record CombatStats(
        long MaxHitpoints,
        long MaxManapoints,
        int AttackIntervalMilliseconds,
        double AccuracyRating,
        double MaxDamage,
        double EvasionRating,
        double Armor,
        double WaterResistance,
        double NatureResistance,
        double FireResistance,
        XiuxianCombatStyle Style,
        XiuxianDamageType DamageType,
        XiuxianCombatantDefinition? Definition,
        double Tenacity,
        double HpRegenPerTenSeconds,
        double MpRegenPerTenSeconds,
        double PenetrationRatio,
        double PierceChance,
        double CurseRatio,
        double FuryRatio,
        double WeakenRatio,
        double LifeStealRatio,
        double ArmorDamageRatio,
        double HpDrainRatio,
        double DamageTakenRatio,
        double CriticalChance,
        double CriticalDamageRatio,
        double CriticalAvoidance,
        double DamageAbsorptionRatio,
        double BreakRating = 0,
        double ParryRating = 0,
        double SkillIntensity = 1.0,
        double DynamicResistanceFactorK = 1.0,
        double ActionSpeedReductionRatio = 0,
        double CooldownReductionRatio = 0,
        double CriticalRating = 0,
        double CriticalAvoidanceRating = 0);

    private readonly record struct CombatResolution(
        long Damage,
        long Healing,
        bool DidHit = false,
        long DrainHealing = 0,
        bool Pierced = false,
        bool IsCritical = false);

    private struct DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public double NextDouble()
        {
            var value = NextUInt64() >> 11;
            return value / (double)(1UL << 53);
        }

        public long NextInt64(long minimumInclusive, long maximumExclusive)
        {
            if (maximumExclusive <= minimumInclusive) return minimumInclusive;
            var range = checked((ulong)(maximumExclusive - minimumInclusive));
            var value = NextUInt64() % range;
            return checked(minimumInclusive + (long)value);
        }

        private ulong NextUInt64()
        {
            var x = _state;
            x ^= x << 7;
            x ^= x >> 9;
            x ^= x << 8;
            _state = x;
            return x;
        }
    }
}

internal static class XiuxianCombatReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> source, T value)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(source[index], value)) return index;
        }

        return -1;
    }
}
