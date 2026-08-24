// ==============================================================================
// 文件名称: CombatGroupSimulator.cs
// 主要职责: 多敌人遭遇战的独立事件时间轴模拟核心逻辑与掉落结算。
// 核心逻辑 / 定义内容:
//   1. 多目标战斗时间轴循环推进（RunGroupEncounter），支持技能施法、吟唱打断与冷却管理。
//   2. 目标选择策略、多敌人仇恨管理与群体/单体技能效果判定。
//   3. 战斗控制状态、持续伤害（DoT）、狂暴机制与战利品掉落结算。
// ==============================================================================

using Domain.Models;


namespace Domain.Simulation;

public static partial class CombatSimulator
{
    /// <summary>
    /// 多敌人遭遇战独立事件时间轴模拟核心逻辑。
    /// 推进攻击、吟唱、控制、狂暴、恢复与掉落结算。
    /// </summary>
    /// <param name="playerId">玩家唯一 GUID。</param>
    /// <param name="loadout">玩家战斗配置装配。</param>
    /// <param name="activeBuffs">活跃 Buff 列表。</param>
    /// <param name="inventory">背包物品字典。</param>
    /// <param name="encounter">遭遇副本关卡配置原型。</param>
    /// <param name="startedAt">战斗开始时间戳。</param>
    /// <param name="duration">推进模拟时长。</param>
    /// <param name="completedEncountersBeforeRun">此前通关遭遇次数。</param>
    /// <param name="initialPlayerState">玩家入场初始状态。</param>
    /// <param name="stopAfterFirstOutcome">分出胜负时是否立刻终止。</param>
    /// <param name="stopAfterFirstVictory">首次胜利时是否立刻终止。</param>
    /// <param name="playerSpirit">玩家修为总点数。</param>
    /// <param name="initialAbilityCooldowns">初始技能冷却字典。</param>
    /// <param name="initialConsumableCooldowns">初始消耗品槽位冷却列表。</param>
    /// <returns>多目标战斗推演结果。</returns>
    private static CombatRunResult RunGroupEncounter(
        Guid playerId,
        CombatLoadout loadout,
        IReadOnlyList<BuffState> activeBuffs,
        IReadOnlyDictionary<string, long> inventory,
        CombatEncounterDefinition encounter,
        DateTimeOffset startedAt,
        TimeSpan duration,
        long completedEncountersBeforeRun,
        CombatPlayerState? initialPlayerState,
        bool stopAfterFirstOutcome,
        bool stopAfterFirstVictory,
        long playerSpirit = 0,
        IReadOnlyDictionary<string, long>? initialAbilityCooldowns = null,
        IReadOnlyList<long>? initialConsumableCooldowns = null)
    {
        var durationMilliseconds = Math.Max(0, checked((long)Math.Floor(duration.TotalMilliseconds)));
        var random = new DeterministicRandom(CreateSeed(playerId, startedAt, encounter.Id));
        var dropRateMultiplier = NormalizeCombatMultiplier(loadout.CombatDropRate);
        var dropQuantityMultiplier = NormalizeCombatMultiplier(loadout.CombatDropQuantity);
        var rareFindMultiplier = NormalizeCombatMultiplier(loadout.CombatRareFind);
        var localInventory = new Dictionary<string, long>(inventory, StringComparer.Ordinal);
        var consumedItems = new Dictionary<string, long>(StringComparer.Ordinal);
        var buffs = CreateBuffRuntime(activeBuffs, startedAt);
        var recoveries = new List<RecoveryRuntime>();
        var damageOverTimes = new List<GroupDamageOverTimeRuntime>();
        var playerControl = new CombatControlRuntime();
        var events = new List<string>(capacity: 24);
        var player = BuildPlayerStats(loadout, buffs, 0, playerSpirit);
        var playerHitpoints = Math.Clamp(initialPlayerState?.Hitpoints ?? player.MaxHitpoints, 0, player.MaxHitpoints);
        var playerManapoints = Math.Clamp(initialPlayerState?.Manapoints ?? player.MaxManapoints, 0, player.MaxManapoints);
        var playerDead = false;
        var playerRespawnAt = InfiniteTime;
        var nextPlayerAction = 0L;
        var playerActionName = "灵击";
        var playerActionKind = CombatActionKind.Attack;
        var playerActionStartedAt = 0L;
        CombatAbilityDefinition? pendingPlayerAbility = null;
        var pendingPlayerAbilityIndex = -1;
        var nextRegenTick = (long)RegenTickMilliseconds;
        var encounterRespawnAt = InfiniteTime;
        var encounterDefeated = false;
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
            .Select(slot => CombatCatalog.TryGetAbility(slot.AbilityId, out var ability)
                ? CombatCatalog.GetAbilityAtLevel(ability.Id, Math.Max(slot.Level, loadout.GetAbilityLevel(ability.Id)))
                : null)
            .ToArray();
        var completedEncounterCount = Math.Max(0, completedEncountersBeforeRun);
        var enemies = CreateEnemyGroup(encounter, completedEncounterCount, ref random, 0);
        var encounters = 1;
        var wins = 0;
        var losses = 0;
        var enemiesDefeated = 0;
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
        var now = 0L;
        var lastProcessedAt = -1L;
        var processedEvents = 0;
        long? outcomeMilliseconds = null;
        long? victoryMilliseconds = null;

        GroupEnemyRuntime? GetTarget() => enemies.FirstOrDefault(enemy => !enemy.IsDefeated && enemy.Hitpoints > 0);

        void MergeRewardDrops(
            IReadOnlyDictionary<string, long> regularDrops,
            IReadOnlyDictionary<string, long> rareDrops)
        {
            MergeDrops(droppedItems, regularDrops);
            if (rareDrops.Count <= 0) return;
            rareDroppedItems ??= new Dictionary<string, long>(StringComparer.Ordinal);
            MergeDrops(rareDroppedItems, rareDrops);
        }

        void GrantEnemyRewards(GroupEnemyRuntime enemy)
        {
            var definition = enemy.Definition;
            var playerLevel = Math.Max(1.0, (double)loadout.AttackLevel);
            var enemyLevel = Math.Max(1.0, (double)definition.AttackLevel);
            var gainedExperience = CombatFormula.CalculateCombatExperience(playerLevel, enemyLevel) * (definition.ExperienceMultiplier > 0 ? definition.ExperienceMultiplier : 1.0);
            experience += gainedExperience;
            var spiritMin = Math.Max(0, definition.SpiritMin);
            var spiritMax = Math.Max(spiritMin, definition.SpiritMax);
            var spiritReward = spiritMin == spiritMax
                ? spiritMin
                : random.NextInt64(spiritMin, checked(spiritMax + 1));
            spirit = checked(spirit + spiritReward);
            var regularDrops = RollDropTable(
                definition.DropTable,
                rare: false,
                dropRateMultiplier,
                rareFindMultiplier,
                dropQuantityMultiplier,
                ref random,
                ref droppedQuantity);
            var rareDrops = RollDropTable(
                definition.RareDropTable,
                rare: true,
                dropRateMultiplier,
                rareFindMultiplier,
                dropQuantityMultiplier,
                ref random,
                ref droppedQuantity);
            MergeRewardDrops(regularDrops, rareDrops);
            if (spiritReward > 0)
            {
                AddDrop(droppedItems, ItemIds.Spirit, spiritReward, ref droppedQuantity);
            }

            var dropSummary = events.Count < MaxResultEvents
                ? FormatDropSummary(regularDrops, rareDrops)
                : string.Empty;
            AddEvent(events, $"击败 {{{{enemy:{definition.Id}|{definition.Name}}}}}，获得 {gainedExperience:F1} 点战斗经验{dropSummary}");
        }

        void FinalizeEnemyDefeat(GroupEnemyRuntime enemy)
        {
            if (enemy.IsDefeated || enemy.Hitpoints > 0) return;

            enemy.IsDefeated = true;
            enemy.NextActionAt = InfiniteTime;
            enemy.PendingAbility = null;
            enemy.PendingAbilityIndex = -1;
            enemy.Control.Clear();
            damageOverTimes.RemoveAll(dot => ReferenceEquals(dot.TargetEnemy, enemy));
            enemiesDefeated++;
            GrantEnemyRewards(enemy);

            if (enemies.Any(current => !current.IsDefeated && current.Hitpoints > 0)) return;

            wins++;
            outcomeMilliseconds ??= now;
            victoryMilliseconds ??= now;
            completedEncounterCount++;

            AddEvent(events, $"完成遭遇 {{{{encounter:{encounter.Id}|{encounter.Name}}}}}，共击败 {enemies.Count} 名敌人");
            encounterDefeated = true;
            encounterRespawnAt = checked(now + Math.Max(1, encounter.RespawnMilliseconds));
            nextPlayerAction = encounterRespawnAt;
            playerActionName = "等待敌群";
            playerActionKind = CombatActionKind.Wait;
            playerActionStartedAt = now;
            pendingPlayerAbility = null;
            pendingPlayerAbilityIndex = -1;
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
            recoveries.Clear();
            damageOverTimes.RemoveAll(dot => dot.TargetsPlayer);
            playerControl.Clear();
            playerRespawnAt = checked(now + Math.Max(1, encounter.PlayerRespawnMilliseconds));
            nextPlayerAction = playerRespawnAt;
            playerActionName = "调息复起";
            playerActionKind = CombatActionKind.Respawn;
            playerActionStartedAt = now;
            foreach (var enemy in enemies.Where(enemy => !enemy.IsDefeated))
            {
                enemy.PendingAbility = null;
                enemy.PendingAbilityIndex = -1;
                enemy.NextActionAt = InfiniteTime;
                enemy.ActionName = "待机";
                enemy.ActionKind = CombatActionKind.Wait;
                enemy.ActionStartedAt = now;
            }
        }

        void ApplyPlayerHealing(CombatResolution resolution)
        {
            if (resolution.Healing > 0)
            {
                var restored = Math.Min(Math.Max(0, player.MaxHitpoints - playerHitpoints), resolution.Healing);
                playerHitpoints = checked(playerHitpoints + restored);
                hitpointsRecoveredFromAbilities = checked(hitpointsRecoveredFromAbilities + restored);
                AddEvent(events, $"功法恢复：生命 +{restored}");
            }

            if (resolution.DrainHealing <= 0) return;
            var drained = Math.Min(Math.Max(0, player.MaxHitpoints - playerHitpoints), resolution.DrainHealing);
            playerHitpoints = checked(playerHitpoints + drained);
            hitpointsRecoveredFromAbilities = checked(hitpointsRecoveredFromAbilities + drained);
            if (drained > 0) AddEvent(events, $"汲取恢复：生命 +{drained}");
        }

        if (playerHitpoints <= 0)
        {
            FinalizePlayerDefeat();
        }

        long GetNextEventAt(long currentTime)
        {
            return MinTime(
                nextPlayerAction,
                GetNextEnemyActionAt(enemies),
                playerDead ? playerRespawnAt : InfiniteTime,
                encounterDefeated ? encounterRespawnAt : InfiniteTime,
                nextRegenTick,
                GetNextConsumableEventTime(currentTime, nextConsumableUse),
                GetNextRecoveryEventTime(currentTime, recoveries),
                GetNextGroupDamageOverTimeEventTime(currentTime, damageOverTimes),
                GetNextGroupControlEventTime(currentTime, playerControl, enemies));
        }

        while ((!stopAfterFirstOutcome || outcomeMilliseconds is null)
            && (!stopAfterFirstVictory || victoryMilliseconds is null)
            && now <= durationMilliseconds
            && processedEvents < MaxEventsPerRun)
        {
            processedEvents++;
            var nextEnemyAction = GetNextEnemyActionAt(enemies);
            var nextPlayerRespawn = playerDead ? playerRespawnAt : InfiniteTime;
            var nextEncounterRespawn = encounterDefeated ? encounterRespawnAt : InfiniteTime;
            var nextConsumable = GetNextConsumableEventTime(now, nextConsumableUse);
            var nextRecovery = GetNextRecoveryEventTime(now, recoveries);
            var nextDamageOverTime = GetNextGroupDamageOverTimeEventTime(now, damageOverTimes);
            var nextControlExpiration = GetNextGroupControlEventTime(now, playerControl, enemies);
            var next = GetNextEventAt(now);

            if (next > durationMilliseconds || next < now) break;
            if (next == lastProcessedAt)
            {
                throw new InvalidOperationException(
                    $"多敌人战斗事件未推进：遭遇={encounter.Id}, 时间={now}, "
                    + $"玩家行动={nextPlayerAction}, 敌人行动={nextEnemyAction}, "
                    + $"玩家复活={nextPlayerRespawn}, 敌群刷新={nextEncounterRespawn}, "
                    + $"恢复={nextRecovery}, 持续伤害={nextDamageOverTime}, "
                    + $"控制={nextControlExpiration}。");
            }
            now = next;
            lastProcessedAt = now;

            foreach (var enemy in enemies.Where(enemy => !enemy.IsDefeated))
            {

                if (enemy.Control.Expire(now) && enemy.PendingAbility is null)
                {
                    enemy.NextActionAt = now;
                    enemy.ActionName = "攻击";
                    enemy.ActionKind = CombatActionKind.Attack;
                    enemy.ActionStartedAt = now;
                }
            }

            if (playerControl.Expire(now)
                && !playerDead
                && !encounterDefeated
                && pendingPlayerAbility is null)
            {
                nextPlayerAction = now;
                playerActionName = "灵击";
                playerActionKind = CombatActionKind.Attack;
                playerActionStartedAt = now;
            }

            if (playerDead && now >= playerRespawnAt)
            {
                playerDead = false;
                pendingPlayerAbility = null;
                pendingPlayerAbilityIndex = -1;
                recoveries.Clear();
                damageOverTimes.RemoveAll(dot => dot.TargetsPlayer);
                playerControl.Clear();
                player = BuildPlayerStats(loadout, buffs, now, playerSpirit);
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
                playerActionName = "灵击";
                playerActionKind = CombatActionKind.Attack;
                playerActionStartedAt = now;
                foreach (var enemy in enemies.Where(enemy => !enemy.IsDefeated))
                {
                    enemy.NextActionAt = now;
                    enemy.ActionName = "攻击";
                    enemy.ActionKind = CombatActionKind.Attack;
                    enemy.ActionStartedAt = now;
                }
                AddEvent(events, $"战斗复起：{{{{encounter:{encounter.Id}|{encounter.Name}}}}}");
            }

            if (encounterDefeated && now >= encounterRespawnAt)
            {
                encounterDefeated = false;
                damageOverTimes.RemoveAll(dot => !dot.TargetsPlayer);
                enemies = CreateEnemyGroup(encounter, completedEncounterCount, ref random, now);
                nextPlayerAction = now;
                playerActionName = "灵击";
                playerActionKind = CombatActionKind.Attack;
                playerActionStartedAt = now;
                encounters++;
                AddEvent(events, $"新的敌群进入 {{{{encounter:{encounter.Id}|{encounter.Name}}}}}");
            }

            if (now >= nextRegenTick)
            {
                if (!playerDead)
                {
                    var restoredHitpoints = Math.Min(
                        Math.Max(0, player.MaxHitpoints - playerHitpoints),
                        player.HpRegen > 0 ? (long)Math.Round(player.HpRegen) : CombatFormula.CalculateRegeneration(player.MaxHitpoints, player.HpRegenPerTenSeconds));
                    playerHitpoints = checked(playerHitpoints + restoredHitpoints);
                    hitpointsRegenerated = checked(hitpointsRegenerated + restoredHitpoints);
                }

                var restoredManapoints = Math.Min(
                    Math.Max(0, player.MaxManapoints - playerManapoints),
                    player.MpRegen > 0 ? (long)Math.Round(player.MpRegen) : CombatFormula.CalculateRegeneration(player.MaxManapoints, player.MpRegenPerTenSeconds));
                playerManapoints = checked(playerManapoints + restoredManapoints);
                manapointsRegenerated = checked(manapointsRegenerated + restoredManapoints);

                foreach (var enemy in enemies.Where(enemy => !enemy.IsDefeated))
                {
                    var restoredEnemyHitpoints = Math.Min(
                        Math.Max(0, enemy.Stats.MaxHitpoints - enemy.Hitpoints),
                        enemy.Stats.HpRegen > 0 ? (long)Math.Round(enemy.Stats.HpRegen) : CombatFormula.CalculateRegeneration(enemy.Stats.MaxHitpoints, enemy.Stats.HpRegenPerTenSeconds));
                    var restoredEnemyManapoints = Math.Min(
                        Math.Max(0, enemy.Stats.MaxManapoints - enemy.Manapoints),
                        enemy.Stats.MpRegen > 0 ? (long)Math.Round(enemy.Stats.MpRegen) : CombatFormula.CalculateRegeneration(enemy.Stats.MaxManapoints, enemy.Stats.MpRegenPerTenSeconds));
                    enemy.Hitpoints = checked(enemy.Hitpoints + restoredEnemyHitpoints);
                    enemy.Manapoints = checked(enemy.Manapoints + restoredEnemyManapoints);
                }

                nextRegenTick = checked(now + RegenTickMilliseconds);
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
                    var hitpointShare = TakeRecoveryShare(ref remainingHitpoints, recovery.RemainingTicks);
                    var manapointShare = TakeRecoveryShare(ref remainingManapoints, recovery.RemainingTicks);
                    recovery.RemainingHitpoints = remainingHitpoints;
                    recovery.RemainingManapoints = remainingManapoints;
                    var restoredHitpoints = Math.Min(Math.Max(0, player.MaxHitpoints - playerHitpoints), hitpointShare);
                    var restoredManapoints = Math.Min(Math.Max(0, player.MaxManapoints - playerManapoints), manapointShare);
                    playerHitpoints = checked(playerHitpoints + restoredHitpoints);
                    playerManapoints = checked(playerManapoints + restoredManapoints);
                    hitpointsRecoveredFromConsumables = checked(hitpointsRecoveredFromConsumables + restoredHitpoints);
                    manapointsRecoveredFromConsumables = checked(manapointsRecoveredFromConsumables + restoredManapoints);
                    if (restoredHitpoints > 0 || restoredManapoints > 0)
                    {
                        AddEvent(events, $"持续恢复：生命 +{restoredHitpoints}，灵力 +{restoredManapoints}");
                    }

                    recovery.RemainingTicks--;
                    if (recovery.RemainingTicks <= 0) recoveries.RemoveAt(index);
                    else recovery.NextTickAt = checked(now + ConsumableRecoveryTickMilliseconds);
                }
            }

            var nextDamageOverTimeTick = GetNextGroupDamageOverTimeEventTime(now, damageOverTimes);
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
                    var targetDefeated = false;
                    if (tickDamage > 0 && damageOverTime.TargetsPlayer && !playerDead && playerHitpoints > 0)
                    {
                        var actualDamage = Math.Min(playerHitpoints, tickDamage);
                        playerHitpoints = Math.Max(0, playerHitpoints - actualDamage);
                        damageTaken = checked(damageTaken + actualDamage);
                        AddEvent(events, $"持续伤害：生命 -{actualDamage}");
                        targetDefeated = playerHitpoints <= 0;
                    }
                    else if (tickDamage > 0 && damageOverTime.TargetEnemy is { IsDefeated: false } dotTarget && dotTarget.Hitpoints > 0)
                    {
                        var actualDamage = Math.Min(dotTarget.Hitpoints, tickDamage);
                        dotTarget.Hitpoints = Math.Max(0, dotTarget.Hitpoints - actualDamage);
                        damageDealt = checked(damageDealt + actualDamage);
                        AddEvent(events, $"持续伤害：{{{{enemy:{dotTarget.Definition.Id}|{dotTarget.Definition.Name}}}}} 生命 -{actualDamage}");
                        targetDefeated = dotTarget.Hitpoints <= 0;
                    }

                    damageOverTime.CurrentTick++;
                    if (damageOverTime.CurrentTick > damageOverTime.TotalTicks || targetDefeated)
                    {
                        damageOverTimes.RemoveAt(index);
                    }
                    else
                    {
                        damageOverTime.NextTickAt = checked(now + Math.Max(1, damageOverTime.TickMilliseconds));
                    }
                }

                foreach (var enemy in enemies.Where(enemy => !enemy.IsDefeated && enemy.Hitpoints <= 0).ToArray())
                {
                    FinalizeEnemyDefeat(enemy);
                }
                if (!playerDead && playerHitpoints <= 0) FinalizePlayerDefeat();
            }

            if (playerDead || encounterDefeated) continue;
            var target = GetTarget();
            if (target is null) continue;

            ApplyConsumables(
                now,
                loadout,
                localInventory,
                consumedItems,
                buffs,
                target.Hitpoints,
                target.Stats.MaxHitpoints,
                target.Manapoints,
                target.Stats.MaxManapoints,
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
                target.Control);
            player = BuildPlayerStats(loadout, buffs, now, playerSpirit);

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
                        target.Hitpoints,
                        target.Stats.MaxHitpoints,
                        target.Manapoints,
                        target.Stats.MaxManapoints,
                        buffs,
                        playerControl,
                        target.Control,
                        now,
                        out abilityIndex);
                    if (ability is not null && ability.CastMilliseconds > 0)
                    {
                        pendingPlayerAbility = ability;
                        pendingPlayerAbilityIndex = abilityIndex;
                        var castTime = Math.Max(100, (int)Math.Round(ability.CastMilliseconds * (1.0 - player.ActionSpeedReductionRatio)));
                        nextPlayerAction = checked(now + castTime);
                        playerActionName = ability.Name;
                        playerActionKind = CombatActionKind.Cast;
                        playerActionStartedAt = now;
                        AddEvent(events, $"施放 {{{{ability:{ability.Id}|{ability.Name}}}}}");
                        ability = null;
                    }
                }

                if (pendingPlayerAbility is null)
                {
                    if (ability?.EffectType == CombatAbilityEffectType.Heal)
                    {
                        var resolution = ResolvePlayerAttack(player, target.Stats, ability, ref playerManapoints, ref random);
                        ApplyPlayerHealing(resolution);
                    }
                    else
                    {
                        IReadOnlyList<GroupEnemyRuntime> damageTargets = ability?.TargetType == CombatAbilityTargetType.AllEnemies
                            ? enemies.Where(enemy => !enemy.IsDefeated && enemy.Hitpoints > 0).ToArray()
                            : [target];
                        if (ability is not null) playerManapoints = Math.Max(0, playerManapoints - ability.ManaCost);
                        foreach (var currentTarget in damageTargets)
                        {
                            var resolution = ability is null && playerControl.IsBlinded(now)
                                ? new CombatResolution(0, 0)
                                : ResolveDamage(player, currentTarget.Stats, ability, ref random);
                            if (ability is null && playerControl.IsBlinded(now))
                            {
                                AddEvent(events, "致盲状态：本次灵力攻击落空");
                            }
                            if (resolution.Damage > 0)
                            {
                                currentTarget.Hitpoints = Math.Max(0, currentTarget.Hitpoints - resolution.Damage);
                                damageDealt = checked(damageDealt + resolution.Damage);
                                var actionName = ability is null
                                    ? "灵击"
                                    : $"{{{{ability:{ability.Id}|{ability.Name}}}}}";
                                var critical = resolution.IsCritical ? "{{mechanic:critical|暴击}} " : string.Empty;
                                AddEvent(events, $"{critical}{actionName}命中 {{{{enemy:{currentTarget.Definition.Id}|{currentTarget.Definition.Name}}}}}，造成 {resolution.Damage} 点伤害");
                            }
                            else if (!resolution.DidHit)
                            {
                                AddEvent(events, ability is null
                                    ? $"灵击未能命中 {{{{enemy:{currentTarget.Definition.Id}|{currentTarget.Definition.Name}}}}}"
                                    : $"{{{{ability:{ability.Id}|{ability.Name}}}}}未能命中 {{{{enemy:{currentTarget.Definition.Id}|{currentTarget.Definition.Name}}}}}" );
                            }
                            ApplyPlayerHealing(resolution);


                            if (ability is not null && resolution.Damage > 0)
                            {
                                ScheduleGroupDamageOverTime(damageOverTimes, ability, resolution.Damage, currentTarget, now);
                                ApplyControlEffects(
                                    ability,
                                    currentTarget.Control,
                                    currentTarget.Stats.Tenacity,
                                    now,
                                    ref random,
                                    events);
                                if (currentTarget.Control.IsStunned(now))
                                {
                                    currentTarget.PendingAbility = null;
                                    currentTarget.PendingAbilityIndex = -1;
                                    currentTarget.NextActionAt = Math.Max(currentTarget.NextActionAt, currentTarget.Control.StunUntil);
                                    currentTarget.ActionName = "受控";
                                    currentTarget.ActionKind = CombatActionKind.Control;
                                    currentTarget.ActionStartedAt = now;
                                }
                            }

                            if (currentTarget.Hitpoints <= 0) FinalizeEnemyDefeat(currentTarget);
                        }
                    }

                    if (!encounterDefeated)
                    {
                        nextPlayerAction = checked(now + player.AttackIntervalMilliseconds);
                        playerActionName = ability is null ? "普通攻击" : ability.Name;
                        playerActionKind = CombatActionKind.Attack;
                        playerActionStartedAt = now;
                    }
                    if (ability is not null && abilityIndex >= 0)
                    {
                        var cooldownTime = Math.Max(1, (int)Math.Round(ability.CooldownMilliseconds * (1.0 - player.CooldownReductionRatio)));
                        abilityReadyAt[abilityIndex] = checked(now + cooldownTime);
                    }
                    if (encounterDefeated) continue;
                }
            }

            if (nextPlayerAction == now && playerControl.IsStunned(now))
            {
                nextPlayerAction = playerControl.StunUntil;
                playerActionName = "受控";
                playerActionKind = CombatActionKind.Control;
                playerActionStartedAt = now;
            }

            foreach (var enemy in enemies
                         .Where(enemy => !enemy.IsDefeated && enemy.NextActionAt == now)
                         .OrderBy(enemy => enemy.Slot))
            {
                if (playerDead) break;
                if (enemy.Control.IsStunned(now))
                {
                    enemy.NextActionAt = enemy.Control.StunUntil;
                    enemy.ActionName = "受控";
                    enemy.ActionKind = CombatActionKind.Control;
                    enemy.ActionStartedAt = now;
                    continue;
                }

                var enemyAbility = enemy.PendingAbility;
                enemy.PendingAbility = null;
                var enemyAbilityIndex = enemy.PendingAbilityIndex;
                enemy.PendingAbilityIndex = -1;
                if (enemyAbility is null)
                {
                    enemyAbility = SelectEnemyAbility(
                        enemy.Stats,
                        enemy.AbilityReadyAt,
                        enemy.Hitpoints,
                        enemy.Stats.MaxHitpoints,
                        enemy.Manapoints,
                        playerHitpoints,
                        player.MaxHitpoints,
                        playerManapoints,
                        player.MaxManapoints,
                        buffs,
                        enemy.Control,
                        playerControl,
                        now,
                        out enemyAbilityIndex);
                    if (enemyAbility is not null && enemyAbility.CastMilliseconds > 0)
                    {
                        enemy.PendingAbility = enemyAbility;
                        enemy.PendingAbilityIndex = enemyAbilityIndex;
                        var enemyCastTime = Math.Max(100, (int)Math.Round(enemyAbility.CastMilliseconds * (1.0 - enemy.Stats.ActionSpeedReductionRatio)));
                        enemy.NextActionAt = checked(now + enemyCastTime);
                        enemy.ActionName = enemyAbility.Name;
                        enemy.ActionKind = CombatActionKind.Cast;
                        enemy.ActionStartedAt = now;
                        continue;
                    }
                }

                var enemyManapoints = enemy.Manapoints;
                var enemyResolution = enemyAbility is null && enemy.Control.IsBlinded(now)
                    ? new CombatResolution(0, 0)
                    : ResolveEnemyAttack(enemy.Stats, player, enemyAbility, ref enemyManapoints, ref random);
                enemy.Manapoints = enemyManapoints;
                if (enemyAbility is null && enemy.Control.IsBlinded(now))
                {
                    AddEvent(events, $"{{{{enemy:{enemy.Definition.Id}|{enemy.Definition.Name}}}}} 处于致盲状态，本次攻击落空");
                }
                if (enemyResolution.Damage > 0)
                {
                    playerHitpoints = Math.Max(0, playerHitpoints - enemyResolution.Damage);
                    damageTaken = checked(damageTaken + enemyResolution.Damage);
                    var actionName = enemyAbility is null
                        ? "攻击"
                        : $"施展 {{{{ability:{enemyAbility.Id}|{enemyAbility.Name}}}}}";
                    var critical = enemyResolution.IsCritical ? "{{mechanic:critical|暴击}} " : string.Empty;
                    AddEvent(events, $"{critical}{{{{enemy:{enemy.Definition.Id}|{enemy.Definition.Name}}}}}{actionName}，造成 {enemyResolution.Damage} 点伤害");
                }
                else if (!enemyResolution.DidHit)
                {
                    AddEvent(events, $"{{{{enemy:{enemy.Definition.Id}|{enemy.Definition.Name}}}}}的攻击未能命中");
                }
                if (enemyResolution.Healing > 0)
                {
                    enemy.Hitpoints = Math.Min(enemy.Stats.MaxHitpoints, checked(enemy.Hitpoints + enemyResolution.Healing));
                }
                if (enemyResolution.DrainHealing > 0)
                {
                    enemy.Hitpoints = Math.Min(enemy.Stats.MaxHitpoints, checked(enemy.Hitpoints + enemyResolution.DrainHealing));
                }

                if (enemyAbility is not null && enemyResolution.Damage > 0)
                {
                    ScheduleGroupDamageOverTime(damageOverTimes, enemyAbility, enemyResolution.Damage, null, now);
                    ApplyControlEffects(enemyAbility, playerControl, player.Tenacity, now, ref random, events);
                    if (playerControl.IsStunned(now))
                    {
                        pendingPlayerAbility = null;
                        pendingPlayerAbilityIndex = -1;
                        nextPlayerAction = Math.Max(nextPlayerAction, playerControl.StunUntil);
                        playerActionName = "受控";
                        playerActionKind = CombatActionKind.Control;
                        playerActionStartedAt = now;
                    }
                }

                if (enemyAbility is not null && enemyAbilityIndex >= 0)
                {
                    var enemyCooldownTime = Math.Max(1, (int)Math.Round(enemyAbility.CooldownMilliseconds * (1.0 - enemy.Stats.CooldownReductionRatio)));
                    enemy.AbilityReadyAt[enemyAbilityIndex] = checked(now + enemyCooldownTime);
                }
                enemy.NextActionAt = checked(now + enemy.Stats.AttackIntervalMilliseconds);
                enemy.ActionName = enemyAbility is null ? "普通攻击" : enemyAbility.Name;
                enemy.ActionKind = CombatActionKind.Attack;
                enemy.ActionStartedAt = now;
                if (playerHitpoints <= 0) FinalizePlayerDefeat();
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
            .Select(buff => new CombatBuffApplication(
                buff.BuffId,
                buff.Name,
                buff.Stacks,
                buff.MaxStacks,
                buff.QiGainMultiplier,
                buff.AttackLevelRatio,
                buff.DamageRatio,
                buff.AttackSpeedRatio,
                buff.SourceItemId!,
                buff.ExpiresAtMilliseconds - simulatedMilliseconds,
                buff.Modifiers))
            .ToArray();
        var finalEnemies = enemies
            .OrderBy(enemy => enemy.Slot)
            .Select(enemy => new CombatEnemyState(
                enemy.Slot,
                enemy.Definition.Id,
                enemy.Definition.Name,
                enemy.Hitpoints,
                enemy.Stats.MaxHitpoints,
                enemy.Manapoints,
                enemy.Stats.MaxManapoints,
                enemy.IsDefeated))
            .ToArray();
        var activeEnemy = enemies
            .Where(enemy => !enemy.IsDefeated)
            .OrderBy(enemy => enemy.NextActionAt)
            .ThenBy(enemy => enemy.Slot)
            .FirstOrDefault();

        var nextEvent = selectedOutcomeMilliseconds is null
            ? GetNextEventAt(durationMilliseconds)
            : InfiniteTime;
        return new CombatRunResult(
            encounter.Id,
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
            hitpointsRegenerated,
            manapointsRegenerated,
            processedEvents,
            hitpointsRecoveredFromConsumables,
            manapointsRecoveredFromConsumables,
            hitpointsRecoveredFromAbilities,
            droppedItems,
            rareDroppedItems,
            enemiesDefeated,
            finalEnemies,
            new CombatPlayerState(
                playerHitpoints,
                player.MaxHitpoints,
                playerManapoints,
                player.MaxManapoints,
                playerDead),
            CreateActionState(playerActionName, playerActionKind, playerActionStartedAt, nextPlayerAction, simulatedMilliseconds),
            activeEnemy is null
                ? null
                : CreateActionState(
                    activeEnemy.ActionName,
                    activeEnemy.ActionKind,
                    activeEnemy.ActionStartedAt,
                    activeEnemy.NextActionAt,
                    simulatedMilliseconds),
            selectedOutcomeMilliseconds,
            CreatePlayerAbilityCooldowns(loadout, abilityReadyAt, simulatedMilliseconds),
            nextEvent < InfiniteTime ? nextEvent : null,
            CreatePlayerConsumableCooldowns(nextConsumableUse, simulatedMilliseconds));
    }

    private static List<GroupEnemyRuntime> CreateEnemyGroup(
        CombatEncounterDefinition encounter,
        long completedEncounterCount,
        ref DeterministicRandom random,
        long spawnedAt)
    {
        IReadOnlyList<CombatantDefinition> definitions;
        var nextEncounterNumber = checked(completedEncounterCount + 1);
        if (encounter.BattlesPerBoss > 0
            && encounter.BossEnemies.Count > 0
            && nextEncounterNumber % encounter.BattlesPerBoss == 0)
        {
            definitions = encounter.BossEnemies;
        }
        else if (encounter.EliteInterval > 0
            && encounter.EliteEnemies.Count > 0
            && nextEncounterNumber % encounter.EliteInterval == 0)
        {
            var eliteIndex = (int)random.NextInt64(0, encounter.EliteEnemies.Count);
            var elite = encounter.EliteEnemies[eliteIndex];
            var spawned = new List<CombatantDefinition> { elite };

            var minBudget = Math.Max(1.0, encounter.MinBudget > 0 ? encounter.MinBudget : 10.0);
            var maxBudget = Math.Max(minBudget, encounter.MaxBudget > 0 ? encounter.MaxBudget : minBudget);
            var totalBudget = minBudget + random.NextDouble() * (maxBudget - minBudget);
            var remainBudget = totalBudget - elite.BudgetCost;

            var spawnTable = encounter.RandomSpawns
                .Where(spawn => double.IsFinite(spawn.Weight) && spawn.Weight > 0)
                .ToArray();
            if (spawnTable.Length > 0 && remainBudget > 0)
            {
                var totalWeight = spawnTable.Sum(spawn => spawn.Weight);
                var failCount = 0;
                while (failCount < 3 && remainBudget > 0 && spawned.Count < 5)
                {
                    var roll = random.NextDouble() * totalWeight;
                    var cumulativeWeight = 0d;
                    var selected = spawnTable[^1];
                    foreach (var spawn in spawnTable)
                    {
                        cumulativeWeight += spawn.Weight;
                        if (roll <= cumulativeWeight)
                        {
                            selected = spawn;
                            break;
                        }
                    }

                    var cost = Math.Max(1.0, selected.Enemy.BudgetCost > 0 ? selected.Enemy.BudgetCost : selected.Strength);
                    if (cost <= remainBudget)
                    {
                        spawned.Add(selected.Enemy);
                        remainBudget -= cost;
                        failCount = 0;
                    }
                    else
                    {
                        failCount++;
                    }
                }
            }

            definitions = spawned;
        }
        else
        {
            var spawnTable = encounter.RandomSpawns
                .Where(spawn => double.IsFinite(spawn.Weight) && spawn.Weight > 0)
                .ToArray();
            if (spawnTable.Length == 0)
            {
                definitions = [encounter.Enemy];
            }
            else
            {
                var minBudget = Math.Max(1.0, encounter.MinBudget > 0 ? encounter.MinBudget : 10.0);
                var maxBudget = Math.Max(minBudget, encounter.MaxBudget > 0 ? encounter.MaxBudget : minBudget);
                var totalBudget = minBudget + random.NextDouble() * (maxBudget - minBudget);
                var remainBudget = totalBudget;
                var totalWeight = spawnTable.Sum(spawn => spawn.Weight);
                var spawned = new List<CombatantDefinition>();
                var failCount = 0;
                while (failCount < 3 && remainBudget > 0 && spawned.Count < 5)
                {
                    var roll = random.NextDouble() * totalWeight;
                    var cumulativeWeight = 0d;
                    var selected = spawnTable[^1];
                    foreach (var spawn in spawnTable)
                    {
                        cumulativeWeight += spawn.Weight;
                        if (roll <= cumulativeWeight)
                        {
                            selected = spawn;
                            break;
                        }
                    }

                    var cost = Math.Max(1.0, selected.Enemy.BudgetCost > 0 ? selected.Enemy.BudgetCost : selected.Strength);
                    if (cost <= remainBudget)
                    {
                        spawned.Add(selected.Enemy);
                        remainBudget -= cost;
                        failCount = 0;
                    }
                    else
                    {
                        failCount++;
                    }
                }

                definitions = spawned.Count > 0 ? spawned : [spawnTable[0].Enemy];
            }
        }

        return definitions
            .Select((definition, slot) => new GroupEnemyRuntime(slot, definition, encounter, spawnedAt))
            .ToList();
    }

    private static IReadOnlyDictionary<string, long> RollDropTable(
        IReadOnlyList<CombatDropDefinition> table,
        bool rare,
        double dropRateMultiplier,
        double rareFindMultiplier,
        double dropQuantityMultiplier,
        ref DeterministicRandom random,
        ref long droppedQuantity)
    {
        Dictionary<string, long>? result = null;
        foreach (var drop in table)
        {
            var chanceMultiplier = rare ? rareFindMultiplier : dropRateMultiplier;
            var chance = Math.Clamp(drop.DropChance * chanceMultiplier, 0, 1);
            if (chance <= 0 || random.NextDouble() >= chance) continue;
            var minimum = Math.Max(0, drop.MinQuantity);
            var maximum = Math.Max(minimum, drop.MaxQuantity);
            var quantity = minimum == maximum
                ? minimum
                : random.NextInt64(minimum, checked(maximum + 1));
            quantity = ScaleDropQuantity(quantity, dropQuantityMultiplier, ref random);
            if (quantity <= 0) continue;
            result ??= new Dictionary<string, long>(StringComparer.Ordinal);
            AddDrop(result, drop.ItemId, quantity, ref droppedQuantity);
        }

        return result ?? EmptyDrops;
    }

    private static long MinTime(params long[] values)
    {
        var minimum = InfiniteTime;
        foreach (var value in values) minimum = Math.Min(minimum, value);
        return minimum;
    }

    private static long GetNextEnemyActionAt(IReadOnlyList<GroupEnemyRuntime> enemies)
    {
        var next = InfiniteTime;
        foreach (var enemy in enemies)
        {
            if (!enemy.IsDefeated) next = Math.Min(next, enemy.NextActionAt);
        }
        return next;
    }

    private static long GetNextGroupControlEventTime(
        long now,
        CombatControlRuntime playerControl,
        IReadOnlyList<GroupEnemyRuntime> enemies)
    {
        var next = playerControl.NextExpirationAt;
        foreach (var enemy in enemies)
        {
            if (!enemy.IsDefeated) next = Math.Min(next, enemy.Control.NextExpirationAt);
        }
        return next > now ? next : InfiniteTime;
    }

    private static long GetNextGroupDamageOverTimeEventTime(
        long now,
        IReadOnlyList<GroupDamageOverTimeRuntime> damageOverTimes)
    {
        var next = InfiniteTime;
        foreach (var damageOverTime in damageOverTimes)
        {
            if (damageOverTime.NextTickAt >= now) next = Math.Min(next, damageOverTime.NextTickAt);
        }
        return next;
    }


    private static void ScheduleGroupDamageOverTime(
        ICollection<GroupDamageOverTimeRuntime> damageOverTimes,
        CombatAbilityDefinition ability,
        long directDamage,
        GroupEnemyRuntime? targetEnemy,
        long now)
    {
        if (ability.DamageOverTimeRatio <= 0 || ability.DamageOverTimeMilliseconds <= 0) return;
        var tickMilliseconds = ability.DamageOverTimeTickMilliseconds > 0
            ? ability.DamageOverTimeTickMilliseconds
            : DamageOverTimeTickMilliseconds;
        var totalTicks = Math.Max(1, (int)Math.Ceiling(ability.DamageOverTimeMilliseconds / (double)tickMilliseconds));
        var totalDamage = Math.Max(0, (long)Math.Floor(directDamage * ability.DamageOverTimeRatio));
        if (totalDamage <= 0) return;
        damageOverTimes.Add(new GroupDamageOverTimeRuntime(
            checked(now + tickMilliseconds),
            totalDamage,
            totalTicks,
            tickMilliseconds,
            targetEnemy));
    }

    private sealed class GroupEnemyRuntime
    {
        public GroupEnemyRuntime(
            int slot,
            CombatantDefinition definition,
            CombatEncounterDefinition encounter,
            long spawnedAt)
        {
            Slot = slot;
            Definition = definition;
            SpawnedAt = spawnedAt;
            Stats = BuildEnemyStats(definition);
            Hitpoints = Stats.MaxHitpoints;
            Manapoints = Stats.MaxManapoints;
            NextActionAt = spawnedAt;
            ActionName = "攻击";
            ActionKind = CombatActionKind.Attack;
            ActionStartedAt = spawnedAt;
            AbilityReadyAt = new long[Math.Max(0, definition.Abilities.Count)];
            Array.Fill(AbilityReadyAt, spawnedAt);
        }

        public int Slot { get; }
        public CombatantDefinition Definition { get; }
        public long SpawnedAt { get; }
        public CombatStats Stats { get; private set; }
        public long Hitpoints { get; set; }
        public long Manapoints { get; set; }
        public long NextActionAt { get; set; }
        public string ActionName { get; set; }
        public CombatActionKind ActionKind { get; set; }
        public long ActionStartedAt { get; set; }
        public bool IsDefeated { get; set; }
        public long[] AbilityReadyAt { get; }
        public CombatControlRuntime Control { get; } = new();
        public CombatAbilityDefinition? PendingAbility { get; set; }
        public int PendingAbilityIndex { get; set; } = -1;

        public void RebuildStats()
        {
            Stats = BuildEnemyStats(Definition);
        }
    }

    private sealed class GroupDamageOverTimeRuntime
    {
        public GroupDamageOverTimeRuntime(
            long nextTickAt,
            long totalDamage,
            int totalTicks,
            int tickMilliseconds,
            GroupEnemyRuntime? targetEnemy)
        {
            NextTickAt = nextTickAt;
            TotalDamage = totalDamage;
            TotalTicks = totalTicks;
            TickMilliseconds = tickMilliseconds;
            TargetEnemy = targetEnemy;
        }

        public long NextTickAt { get; set; }
        public long TotalDamage { get; }
        public int TotalTicks { get; }
        public int CurrentTick { get; set; } = 1;
        public int TickMilliseconds { get; }
        public GroupEnemyRuntime? TargetEnemy { get; }
        public bool TargetsPlayer => TargetEnemy is null;
    }
}
