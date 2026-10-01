using System.Text.Json;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using static DaoHua.Ecs.SimulationEngine;

namespace DaoHua.Ecs.Systems;

/// <summary>战斗准备与结算 System 共用的规则本体；生产和无世界回归均消费同一套阶段实现。</summary>
internal static class CombatActionRules
{
    private const long DropQuantityChannelSalt = unchecked((long)0x9E3779B97F4A7C15UL);

    /// <summary>只推进一次搜寻读条，不创建战斗实体；返回实际消耗的秒数。</summary>
    internal static double AdvanceCombatSearchPhase(IPlayerActionState action, double availableSeconds)
    {
        var expedition = action.Expedition
            ?? throw new DomainException(DomainErrorCode.InvariantViolation, "战斗搜寻状态缺失。");
        var remaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
        var step = Math.Min(availableSeconds, remaining);
        if (step <= 0 && !expedition.DuelSeekFailed)
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗搜寻阶段没有可推进的时间。");
        action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + step };
        action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
        return step;
    }

    /// <summary>初始化一轮搜索，实际搜索耗时由后续阶段原语推进。</summary>
    public static void InitializeCombatAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset at, ActionMutationBatch mutations)
    {
        if (action.Expedition is not null) return;
        var encounter = ResolveCombatEncounterForAction(player, definition, action.CompletedRepeats, gameData);
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.InitialSearch, gameData);
        mutations.AddEvent(new SimulationEvent(at, "combat.search.started", $"开始搜寻：{encounter.Name}。", definition.Id));
    }

    /// <summary>只完成一次搜索、复活或斗法回放片段；入场返回供实体直接持有的战斗单位。</summary>
    public static CombatEntryPreparationStep AdvanceCombatPreparation(IPlayerResourceContext player,
        IPlayerActionState action, ActionDefinition definition, GameDataCatalog gameData, AssetWallet? wallet,
        DateTimeOffset from, DateTimeOffset target, double remainingSeconds, ActionMutationBatch mutations)
    {
        var battle = AdvanceCombatPreparationCore(player, action, definition, gameData, wallet, null,
            from, target, ref remainingSeconds, ref mutations.Deltas, ref mutations.EventTail);
        return new(battle, remainingSeconds);
    }

    internal static CombatEncounterState? AdvanceCombatPreparationCore(IPlayerResourceContext player,
        IPlayerActionState action, ActionDefinition definition, GameDataCatalog gameData, AssetWallet? assetBalances,
        DuelAdvanceOptions? duel, DateTimeOffset from, DateTimeOffset target, ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.prepare");
        CombatEncounterDefinition encounter;
        var expedition = action.Expedition;
        if (expedition is null)
        {
            var initialization = new ActionMutationBatch();
            InitializeCombatAction(player, action, definition, gameData,
                from.AddSeconds((target - from).TotalSeconds - remainingSeconds), initialization);
            foreach (var entry in initialization.Events) (events ??= new()).Add(entry);
            expedition = action.Expedition!;
            encounter = gameData.CombatEncounters[expedition.EncounterId];
        }
        else if (!gameData.CombatEncounters.TryGetValue(expedition.EncounterId, out encounter!))
        {
            throw new DomainException(DomainErrorCode.NotFound, $"远征状态引用的遭遇 {expedition.EncounterId} 不存在。");
        }

        if (!expedition.IsValid)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"行动 {definition.Id} 的战斗远征状态无效。");

        if (expedition.Phase == CombatExpeditionPhase.Duel)
        {
            AdvanceDuelPlayback(player, action, definition, encounter, gameData, from, target,
                ref remainingSeconds, ref deltas, ref events);
            return null;
        }

        if (expedition.Phase == CombatExpeditionPhase.DuelPending)
        {
            if (duel?.MatchingEnabled == true)
            {
                // 匹配等待不消耗战斗时间：整段剩余时间记为等待，由撮合运行时在下一拍完成挂载。
                action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + remainingSeconds };
                action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
                remainingSeconds = 0;
                return null;
            }
            // 离线补算等无撮合路径：保留遭遇计数，回退为正常波次入口。
            expedition = expedition with
            {
                Phase = CombatExpeditionPhase.Search,
                PhaseElapsedSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
                PhaseDurationSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
                DuelSeekFailed = true
            };
            action.Expedition = expedition;
        }

        var phaseRemaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
        if (expedition.Phase == CombatExpeditionPhase.Respawning)
        {
            var step = Math.Min(remainingSeconds, phaseRemaining);
            remainingSeconds -= step;
            action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + step };
            action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
            if (action.ProgressSeconds + 1e-9 < expedition.PhaseDurationSeconds) return null;
            player.Resources.Hitpoints = player.Resources.MaxHitpoints;
            player.Resources.Manapoints = player.Resources.MaxManapoints;
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
                player.Settings.AutoUseSlots[i] = player.Settings.AutoUseSlots[i] with { CooldownUntilUnixSeconds = 0 };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Combat);
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData, DeterministicSequence.NextSeed(expedition.Seed));
            action.ProgressSeconds = 0;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.respawned", "复活完成，气血与灵力恢复。", definition.Id);
            return null;
        }
        if (expedition.Phase is CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search)
        {
            var step = AdvanceCombatSearchPhase(action, remainingSeconds);
            remainingSeconds -= step;
            var updated = action.Expedition!;
            if (updated.PhaseElapsedSeconds + 1e-9 < updated.PhaseDurationSeconds) return null;

            var seed = updated.Seed;
            // 斗法入场判定：勾选斗法且不在失败冷却中时，每次进入战斗先判定遭遇计数。
            var duelSettings = gameData.Duel;
            if (action.Intent.PvpEnabled)
            {
                var entryAtUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
                if (player.Duel.CooldownUntilUnixSeconds <= entryAtUnix)
                {
                    var chargesBefore = player.Duel.EncounterCharges;
                    var chargeRoll = (unchecked((ulong)DeterministicSequence.NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
                    if (chargeRoll < duelSettings.EncounterChancePerBattle)
                    {
                        player.Duel.EncounterCharges += 1;
                        player.MarkDirty(PlayerSectionKind.Duel);
                        AddCombatEvent(ref events, from, target, remainingSeconds, "duel.charge", "心血来潮：斗法遭遇感应 +1。", definition.Id);
                    }
                    // 计数大于 0 时进入匹配等待（以判定前计数为准，本波新增的计数留给下次入场）。
                    // 匹配等待静默进行：撮合结果未知前不写日志，无候选对手时由运行时回退为正常波次。
                    if (chargesBefore > 0 && !updated.DuelSeekFailed && duel?.MatchingEnabled == true)
                    {
                        action.Expedition = updated with
                        {
                            Phase = CombatExpeditionPhase.DuelPending,
                            PhaseElapsedSeconds = 0,
                            PhaseDurationSeconds = 0,
                            DuelSeekFailed = false
                        };
                        action.ProgressSeconds = 0;
                        return null;
                    }
                }
            }

            var entryAt = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
            var preparation = PrepareCombatEntry(player, action, definition, encounter, updated,
                gameData, assetBalances, entryAt);
            foreach (var (item, quantity) in preparation.AssetDeltas)
                AddDelta(deltas ??= new Dictionary<ItemId, double>(), item, quantity);
            foreach (var simulationEvent in preparation.Events)
                (events ??= new SimulationEventTail()).Add(simulationEvent);
            return preparation.Battle;
        }

        throw new DomainException(DomainErrorCode.InvariantViolation, "战斗准备不能推进活动战斗单位。");
    }

    /// <summary>资源窗口或战斗终局收尾；随机事件已经由战斗单位系统实际执行。</summary>
    public static double CompleteCombatAdvance(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, CombatEncounterDefinition encounter, GameDataCatalog gameData,
        CombatEncounterState battle, CombatAdvanceResult result, DateTimeOffset from, DateTimeOffset target,
        double remainingSeconds, ActionMutationBatch mutations)
    {
        CompleteCombatAdvanceCore(player, action, definition, encounter, gameData, battle, result,
            from, target, ref remainingSeconds, ref mutations.Deltas, ref mutations.EventTail,
            ref mutations.ActionCount, ref mutations.RepeatCount);
        return remainingSeconds;
    }

    internal static void CompleteCombatAdvanceCore(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, CombatEncounterDefinition encounter, GameDataCatalog gameData,
        CombatEncounterState battleState, CombatAdvanceResult combatResult, DateTimeOffset from, DateTimeOffset target,
        ref double remainingSeconds, ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events,
        ref double completedActions, ref double completedRepeats)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.yield");
        var expedition = action.Expedition!;
        var waveEnemies = expedition.Enemies!;
        var battleFrom = combatResult.FromSeconds;
        var actionElapsedBeforeCombat = (target - from).TotalSeconds - remainingSeconds;
        // battleTo 由 battleFrom 加法得到，回减时浮点误差可能让 consumed 比剩余预算大 1e-15 量级；
        // 不收敛到剩余预算会把外层 remainingSeconds 减成负数触发不变量。
        var consumed = Math.Clamp(battleState.ElapsedSeconds - battleFrom, 0, remainingSeconds);
        // 战斗持续消耗品按战斗存活时钟扣减：仅战斗进行中消耗（含躺尸等待复活）；
        // 寻敌、团灭复活读条与退出战斗后均不消耗。
        DrainCombatSustainedSlots(player, gameData, consumed,
            from.AddSeconds(actionElapsedBeforeCombat + consumed), definition.Id, ref events);
        // 玩家技能与普攻共用同一条动作时间线：战后把通用槽位状态折算回跨战持久化形态。
        player.Combat.Skills = PlayerCombatSkillState.FromCombatant(battleState.Player, battleState.ElapsedSeconds);
        remainingSeconds -= consumed;
        player.Resources.Hitpoints = battleState.Player.Hitpoints;
        player.Resources.Manapoints = battleState.Player.Manapoints;
        // 战斗中气血是独立持久化分段；仅标记行动队列会导致重连后回退到旧气血。
        player.MarkDirty(PlayerSectionKind.Combat);
        // 复用会话驻留期间只回写标量镜像字段：玩家/敌人完整快照由 Flush 在补算收尾、
        // 触发器读文档或会话顶替时统一导出，标量部分与 CaptureCombatState 逐位一致。
        var updatedBattle = expedition with
        {
            BattleElapsedSeconds = battleState.ElapsedSeconds,
            PlayerHitpoints = battleState.Player.Hitpoints,
            PlayerManapoints = battleState.Player.Manapoints,
            PlayerNextAttackAtSeconds = battleState.Player.NextAttackAtSeconds,
            PlayerAttackCount = battleState.Player.AttackCount,
            PlayerThreat = battleState.Player.Threat,
            PlayerRespawnAtSeconds = double.IsFinite(battleState.PlayerRespawnAtSeconds)
                ? battleState.PlayerRespawnAtSeconds : null,
            PhaseElapsedSeconds = Math.Min(expedition.PhaseDurationSeconds, expedition.PhaseElapsedSeconds + consumed),
            Outcome = combatResult.Outcome
        };
        action.Expedition = updatedBattle;
        action.ProgressSeconds = updatedBattle.PhaseElapsedSeconds;
        // 战斗日志名称惰性化：只有确实产生日志帧时才解析怪物名，空事件帧战斗零字典/字符串成本。
        string? waveNames = null;
        string WaveNames() => waveNames ??= string.Join("、", waveEnemies.Select(enemy => gameData.CombatMonsters[enemy.EnemyId].Name));
        string NameOf(string combatantId) => combatantId == "player" ? "你"
            : combatantId.StartsWith("enemy:", StringComparison.Ordinal)
                && int.TryParse(combatantId[6..], out var enemyIndex) && enemyIndex >= 0 && enemyIndex < waveEnemies.Count
                ? gameData.CombatMonsters[waveEnemies[enemyIndex].EnemyId].Name
                : combatantId;
        // 阵亡/起身与战斗事件共用同一战斗时钟：按时刻交错落日志，每次阵亡逐帧计败走与死亡。
        var deaths = combatResult.PlayerDeathTimestamps ?? [];
        var respawns = combatResult.PlayerRespawnTimestamps ?? [];
        // 局部函数无法捕获外层 ref 参数：提前初始化后用普通引用落事件。
        events ??= new SimulationEventTail();
        var eventSink = events;
        int deathCursor = 0, respawnCursor = 0;
        void FlushDownedEvents(double uptoBattleSeconds)
        {
            while (deathCursor < deaths.Count && deaths[deathCursor] <= uptoBattleSeconds + 1e-9)
            {
                var deathAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, deaths[deathCursor++] - battleFrom));
                player.Progression.TotalDefeats += 1;
                player.MarkDirty(PlayerSectionKind.Progression);
                player.RecordStats(action.Id, deathAt.ToUnixTimeMilliseconds() / 1000d, new PlayerStatsDelta { Deaths = 1 });
                eventSink.Add(new SimulationEvent(deathAt,
                    "combat.lost", $"阵亡：被 {WaveNames()} 击倒，原地调息等待复活。", definition.Id));
            }
            while (respawnCursor < respawns.Count && respawns[respawnCursor] <= uptoBattleSeconds + 1e-9)
            {
                var respawnAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, respawns[respawnCursor++] - battleFrom));
                eventSink.Add(new SimulationEvent(respawnAt,
                    "combat.respawned", "原地起身：气血回满，战斗继续。", definition.Id));
            }
        }
        // 尾窗门控：战斗事件超过尾窗容量时，按时间序直接跳过注定被丢弃的帧，
        // 不再为它们构造日志文本；窗口内的阵亡/起身衍生帧仍按战斗时钟交错落日志。
        var emittedIndex = 0;
        var keepFrom = combatResult.Events.Count > SimulationEventTail.TailCapacity
            ? combatResult.Events[combatResult.Events.Count - SimulationEventTail.TailCapacity].AtSeconds
            : double.NegativeInfinity;
        foreach (var combatEvent in combatResult.Events)
        {
            // 技能每次成功施放结算 1 点功法经验：日志尾窗裁剪丢帧不丢入账。
            if (combatEvent.AttackerId == "player" && combatEvent.AbilityId is { } usedAbility)
                GrantAbilityUseExperience(player, action.Id, usedAbility,
                    from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom)).ToUnixTimeMilliseconds() / 1000d);
            if (combatEvent.AtSeconds < keepFrom)
            {
                // 该帧不可保留：折叠已滞留域事件并推进阵亡/起身游标，跳过名称与消息构造。
                eventSink.Fold(emittedIndex);
                emittedIndex = eventSink.Count;
                FlushDownedEvents(combatEvent.AtSeconds);
                emittedIndex = eventSink.Count;
                continue;
            }
            var attacker = NameOf(combatEvent.AttackerId);
            var targetName = NameOf(combatEvent.TargetId);
            var eventAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom));
            // 技能施放与普攻共用同一事件流；携带 AbilityId 的帧按施放口径落日志。
            if (combatEvent.AbilityId is { } abilityId)
                AddCombatEventAt(ref events, eventAt,
                    "combat.skill", $"{attacker}施放 {abilityId}，伤害 {combatEvent.Damage:0.##}，治疗 {combatEvent.Healing:0.##}。", definition.Id);
            else
                AddCombatEventAt(ref events, eventAt,
                    "combat.attack", $"{attacker}{(combatEvent.Hit ? $"命中{targetName}，造成 {combatEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
            emittedIndex = eventSink.Count;
            FlushDownedEvents(combatEvent.AtSeconds);
            emittedIndex = eventSink.Count;
        }
        FlushDownedEvents(double.MaxValue);
        if (combatResult.Outcome == CombatOutcome.Running) return;
        // 战斗终局：会话随终局文档替换自然失效，提前释放避免下一波误命中守卫。
        if (combatResult.Outcome != CombatOutcome.PlayerWon)
        {
            // 团灭即败（defeat 分支）：计入生平败走，拆场进入复活点读条，波次重置回第 1 波。
            // 原地复活遭遇的阵亡不经此分支——引擎保持 Running 直至起身续战。
            player.Progression.TotalDefeats += 1;
            player.MarkDirty(PlayerSectionKind.Progression);
            player.RecordStats(action.Id,
                from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d,
                new PlayerStatsDelta { Deaths = 1 });
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData, updatedBattle.Seed,
                waveNumber: 1) with
            { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
            action.ProgressSeconds = 0;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.lost", $"讨伐失败：{WaveNames()}。", definition.Id);
            return;
        }

        // 波次清空才结算掉落与完成一次重复；逐只发放击杀奖励并计入生平斩妖。
        var pvpEnabled = action.Intent.PvpEnabled;
        var killAtUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
        var killSeed = updatedBattle.Seed;
        foreach (var enemy in waveEnemies)
        {
            var monster = gameData.CombatMonsters[enemy.EnemyId];
            ApplyCombatRewards(player, monster, killSeed, pvpEnabled, killAtUnix, gameData, action.Id, ref deltas);
            killSeed = DeterministicSequence.NextSeed(killSeed);
            switch (monster.Rank)
            {
                case CombatMonsterRank.Elite: player.Progression.EliteKills += 1; break;
                case CombatMonsterRank.Boss: player.Progression.BossKills += 1; break;
                default: player.Progression.NormalKills += 1; break;
            }
        }
        // 波次清空计一次完成轮次，并按怪物分级累计击杀数。
        double normalKills = 0, eliteKills = 0, bossKills = 0;
        foreach (var enemy in waveEnemies)
        {
            switch (gameData.CombatMonsters[enemy.EnemyId].Rank)
            {
                case CombatMonsterRank.Elite: eliteKills += 1; break;
                case CombatMonsterRank.Boss: bossKills += 1; break;
                default: normalKills += 1; break;
            }
        }
        player.RecordStats(action.Id, killAtUnix, new PlayerStatsDelta
        {
            Cycles = 1, NormalKills = normalKills, EliteKills = eliteKills, BossKills = bossKills
        });
        player.MarkDirty(PlayerSectionKind.Loadout);
        player.MarkDirty(PlayerSectionKind.Progression);
        AddCombatEvent(ref events, from, target, remainingSeconds, "combat.won",
            $"讨伐成功：{string.Join("、", waveEnemies.Select(enemy => gameData.CombatMonsters[enemy.EnemyId].Name))}。", definition.Id);

        completedActions += 1;
        completedRepeats += 1;
        PlayerActionQueueRules.RecordCompletedRound(action);
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (action.RemainingRepeats == 0)
        {
            if (player.Settings.LoopEnabled)
            {
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(updatedBattle.Seed), waveNumber: 1);
                player.FinishActionRound(action.Id, loopable: true, killAtUnix);
                return;
            }
            player.FinishActionRound(action.Id, loopable: true, killAtUnix);
            return;
        }
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
            gameData, DeterministicSequence.NextSeed(updatedBattle.Seed), updatedBattle.WaveNumber + 1);
        action.ProgressSeconds = 0;
    }

    /// <summary>仅在载入已有远征时恢复战斗单位；运行中的实体持续借用同一状态。</summary>
    public static CombatEncounterState RestoreCombatBattle(IPlayerResourceContext player, IPlayerActionState action,
        CombatEncounterDefinition encounter, GameDataCatalog gameData)
    {
        var expedition = action.Expedition!;
        var waveEnemies = expedition.Enemies!;
        var templates = waveEnemies.Select(enemy =>
        {
            var monster = gameData.CombatMonsters[enemy.EnemyId];
            return new CombatantTemplate(monster.Stats, monster.Skills);
        }).ToArray();
        // 玩家方技能槽与怪物同构：装配功法折叠为通用技能槽进入同一条动作时间线。
        var playerTemplate = new CombatantTemplate(
            player.ResolveAttributes(gameData).ToCombatant(player.Progression.CombatExperience),
            SkillSlot.FromAbilitySlots(player.Loadout.AbilitySlots, gameData.Abilities));
        var battleState = CombatEncounterState.Restore(
            encounter.Id,
            expedition.Seed,
            playerTemplate,
            expedition.PlayerState ?? new CombatantSnapshot(expedition.PlayerHitpoints, expedition.PlayerManapoints,
                expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount, expedition.PlayerThreat),
            templates,
            waveEnemies.Select(enemy => enemy.State).ToArray(),
            expedition.BattleElapsedSeconds,
            expedition.Outcome,
            expedition.PlayerAttackCount + SumEnemyAttackCounts(waveEnemies),
            InBattleRespawnSeconds(encounter),
            expedition.PlayerRespawnAtSeconds ?? double.PositiveInfinity);
        for (var i = 0; i < waveEnemies.Count; i++)
            battleState.Enemies[i].DefinitionId = waveEnemies[i].EnemyId;
        // 玩家战外持久 Buff 注入触发求值视图（镜像语义与开战入口一致）。
        battleState.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(player.Buffs.Active, buffId);
        return battleState;
    }

    /// <summary>
    /// 创建战斗行动的搜寻阶段快照。该原语只生成可持久化的阶段数据，供 ECS 的搜寻系统
    /// 作为实体组件状态写入；战斗实体创建、边界推进和奖励结算仍由各自 ECS 系统负责。
    /// </summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        double completedRepeats,
        long? seed = null,
        int waveNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(gameData);
        if (phase is not (CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻阶段无效。");
        if (!double.IsFinite(completedRepeats) || completedRepeats < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗行动完成轮次无效。");
        if (waveNumber < 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗波次无效。");

        // 首波使用遭遇固定搜寻读条；波间搜寻使用玩家当前寻敌速度修正。
        var delayFactor = player.ResolveAttributes(gameData).SearchDelayFactor;
        var duration = phase == CombatExpeditionPhase.InitialSearch
            ? encounter.SearchSeconds
            : encounter.RespawnSeconds * delayFactor;
        if (!double.IsFinite(duration) || duration <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻时长无效。");
        return new CombatExpeditionState(
            encounter.Id,
            phase,
            0,
            duration,
            seed ?? CreateSeed(encounter.Id, completedRepeats),
            0,
            player.Resources.Hitpoints,
            player.Resources.Manapoints,
            0,
            0,
            0,
            CombatOutcome.Running,
            waveNumber,
            null);
    }

    /// <summary>按领域行动实例创建搜寻阶段的便捷重载；不会推进行动或修改玩家状态。</summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        IPlayerActionState action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        long? seed = null,
        int waveNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(action);
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber);
    }

    /// <summary>
    /// 把已完成的搜寻阶段转换为真实战斗入场状态。
    /// OOP 快进和 ECS CombatActionSystem 必须共用此原语，避免一侧只投影搜寻状态而跳过
    /// 波次选择、技能装配、战外 Buff、持续消耗品、战斗账本和入场日志。
    /// </summary>
    public static CombatEntryPreparation PrepareCombatEntry(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        CombatEncounterDefinition encounter,
        CombatExpeditionState completedSearch,
        GameDataCatalog gameData,
        AssetWallet? assetBalances,
        DateTimeOffset entryAt)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(completedSearch);
        ArgumentNullException.ThrowIfNull(gameData);
        if (completedSearch.Phase is not (CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search)
            || (!completedSearch.DuelSeekFailed
                && completedSearch.PhaseElapsedSeconds + 1e-9 < completedSearch.PhaseDurationSeconds))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻尚未完成，不能进入战斗。");
        var wave = EncounterWaveSelector.Select(encounter, gameData.CombatMonsters,
            completedSearch.Seed, completedSearch.WaveNumber);
        var battle = CombatEncounterState.Create(
            encounter.Id,
            completedSearch.Seed,
            new CombatantTemplate(player.ResolveAttributes(gameData).ToCombatant(player.Progression.CombatExperience),
                SkillSlot.FromAbilitySlots(player.Loadout.AbilitySlots, gameData.Abilities)),
            wave.Select(monster => new CombatantTemplate(monster.Stats, monster.Skills)).ToArray(),
            InBattleRespawnSeconds(encounter));
        for (var i = 0; i < wave.Count; i++) battle.Enemies[i].DefinitionId = wave[i].Id;
        battle.Player.Hitpoints = Math.Clamp(player.Resources.Hitpoints, 0, player.Resources.MaxHitpoints);
        battle.Player.Manapoints = Math.Clamp(player.Resources.Manapoints, 0, player.Resources.MaxManapoints);
        battle.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(player.Buffs.Active, buffId);
        player.Combat.Skills.ApplyTo(battle.Player);

        Dictionary<ItemId, double>? deltas = null;
        SimulationEventTail? events = null;
        var waveNames = string.Join("、", wave.Select(monster => monster.Name));
        if (battle.Player.Hitpoints <= 0)
        {
            action.Status = ActionQueueStatus.Blocked;
            action.Expedition = completedSearch;
            AddCombatEventAt(ref events, entryAt, "combat.lost", $"气血衰竭，无法迎战：{waveNames}。", definition.Id);
            if (player.Settings.LoopEnabled && player.Actions.Count > 1 && !AllActionsBlocked(player.Actions))
                player.RotateActionToTail(action.Id);
            return new CombatEntryPreparation(null, completedSearch,
                deltas ?? new Dictionary<ItemId, double>(), events?.View ?? [], true);
        }

        var atUnix = entryAt.ToUnixTimeMilliseconds() / 1000d;
        RechargeCombatSustainedSlots(player, gameData, assetBalances, atUnix, action.Id, battle,
            ref deltas, ref events);
        InjectSustainedCombatBuffs(player, gameData, battle);
        CombatActSystem.DecideInitialActions(battle);
        var combatState = CaptureCombatState(battle, completedSearch with
        {
            Phase = CombatExpeditionPhase.Combat,
            PhaseElapsedSeconds = 0,
            PhaseDurationSeconds = 0,
            Outcome = CombatOutcome.Running,
            DuelSeekFailed = false
        });
        action.ProgressSeconds = 0;
        action.Expedition = combatState;
        player.RecordStats(action.Id, atUnix, new PlayerStatsDelta { Encounters = 1 });
        AddCombatEventAt(ref events, entryAt, "combat.started", $"遭遇妖兽：{waveNames}。", definition.Id);
        return new CombatEntryPreparation(battle, combatState,
            deltas ?? new Dictionary<ItemId, double>(), events?.View ?? [], false);
    }


    /// <summary>
    /// 斗法剧本回放推进：结果在撮合时已确定性锁定，本相位只按时间轴回放事件并结算归属。
    /// 战斗期间任何配置修改不影响剧本；玩家主动结束行动走 ApplyDuelForfeit 判负路径。
    /// </summary>
    private static void AdvanceDuelPlayback(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        CombatEncounterDefinition encounter,
        GameDataCatalog gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref SimulationEventTail? events)
    {
        var expedition = action.Expedition!;
        var playback = expedition.Duel ?? throw new DomainException(DomainErrorCode.InvariantViolation, "斗法回放载荷缺失。");
        var elapsedBefore = (target - from).TotalSeconds - remainingSeconds;
        var step = playback.Resolved ? 0 : Math.Min(remainingSeconds, Math.Max(0, playback.DurationSeconds - expedition.BattleElapsedSeconds));
        remainingSeconds -= step;
        var playbackTo = Math.Min(expedition.BattleElapsedSeconds + step, playback.DurationSeconds);
        var cursor = (int)playback.EventCursor;
        var viewerHp = expedition.PlayerHitpoints;
        var opponentHp = playback.OpponentHitpoints;
        var viewerMp = expedition.PlayerManapoints;
        var opponentMp = playback.OpponentManapoints;
        // 回放游标前的事件已全部应用过；游标到 playbackTo 之间的事件逐条应用并写日志。
        for (var i = cursor; i < playback.Events.Count; i++)
        {
            var duelEvent = playback.Events[i];
            if (duelEvent.AtSeconds > playbackTo + 1e-9) break;
            cursor = i + 1;
            var targetIsViewer = duelEvent.TargetIsInitiator == playback.ViewerIsInitiator;
            var attackerIsViewer = duelEvent.AttackerIsInitiator == playback.ViewerIsInitiator;
            // 斗法本方施放同样计每次 1 点功法经验；镜像方施放不重复入账。
            if (attackerIsViewer && duelEvent.AbilityId is { } duelAbility)
                GrantAbilityUseExperience(player, action.Id, duelAbility,
                    from.AddSeconds(elapsedBefore + Math.Max(0, duelEvent.AtSeconds - expedition.BattleElapsedSeconds)).ToUnixTimeMilliseconds() / 1000d);
            if (targetIsViewer) { viewerHp = duelEvent.TargetHitpointsAfter; viewerMp = duelEvent.TargetManapointsAfter; }
            else { opponentHp = duelEvent.TargetHitpointsAfter; opponentMp = duelEvent.TargetManapointsAfter; }
            if (attackerIsViewer) viewerMp = duelEvent.AttackerManapointsAfter;
            else opponentMp = duelEvent.AttackerManapointsAfter;
            var attacker = duelEvent.AttackerIsInitiator == playback.ViewerIsInitiator ? "你" : playback.OpponentName;
            var targetName = targetIsViewer ? "你" : playback.OpponentName;
            AddCombatEventAt(ref events, from.AddSeconds(elapsedBefore + Math.Max(0, duelEvent.AtSeconds - expedition.BattleElapsedSeconds)),
                "combat.attack", $"{attacker}{(duelEvent.Hit ? $"命中{targetName}，造成 {duelEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
        }
        var updated = expedition with
        {
            BattleElapsedSeconds = playbackTo,
            PlayerHitpoints = viewerHp,
            PlayerManapoints = viewerMp,
            Duel = playback with { EventCursor = cursor, OpponentHitpoints = opponentHp, OpponentManapoints = opponentMp }
        };
        action.Expedition = updated;
        player.Resources.Hitpoints = Math.Clamp(viewerHp, 0, player.Resources.MaxHitpoints);
        player.Resources.Manapoints = Math.Clamp(viewerMp, 0, player.Resources.MaxManapoints);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (!playback.Resolved && playbackTo + 1e-9 < playback.DurationSeconds) return;

        // 回放结束（或已裁决标记）：结算斗法归属。
        var now = from.AddSeconds(elapsedBefore + Math.Max(0, playbackTo - expedition.BattleElapsedSeconds));
        var nowUnix = now.ToUnixTimeMilliseconds() / 1000d;
        var duelSettings = gameData.Duel;
        player.Duel.ActiveDuelId = null;
        switch (playback.ViewerOutcome)
        {
            case CombatOutcome.PlayerWon:
                foreach (var spoils in playback.OpponentStagedSnapshot)
                {
                    var spoilsId = new ItemId(spoils.ItemId);
                    AddDelta(deltas ??= new Dictionary<ItemId, double>(), spoilsId, spoils.Quantity);
                    player.RecordStats(action.Id, nowUnix, new PlayerStatsDelta { GainedItem = spoilsId, GainedQuantity = spoils.Quantity });
                }
                player.RecordStats(action.Id, nowUnix, new PlayerStatsDelta { DuelWins = 1 });
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.won",
                    playback.Forfeited
                        ? $"斗法获胜：{playback.OpponentName} 弃战离去，其暂存物品尽数落袋。"
                        : $"斗法获胜：击败 {playback.OpponentName}，缴获其暂存物品。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1);
                break;
            case CombatOutcome.EnemyWon:
                // 弃战路径（ApplyDuelForfeit）已即时记账暂存移除、冷却与败绩，回放落点不再重复结算。
                if (!playback.Forfeited)
                {
                    ActionReportBook.RemoveStagedCombatItems(player, playback.SelfStagedSnapshot, nowUnix);
                    player.Duel.CooldownUntilUnixSeconds = nowUnix + duelSettings.DefeatCooldownSeconds;
                    player.RecordStats(action.Id, nowUnix,
                        new PlayerStatsDelta { DuelLosses = 1, Deaths = 1 });
                    player.Progression.TotalDefeats += 1;
                }
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.lost",
                    playback.Forfeited
                        ? $"斗法弃战：你已结束斗法行动，暂存物品被 {playback.OpponentName} 取走。"
                        : $"斗法落败：{playback.OpponentName} 取走了你的暂存物品，一小时内不再进入斗法。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1) with
                { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
                player.Resources.Hitpoints = 0;
                break;
            default:
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.draw",
                    $"斗法平局：与 {playback.OpponentName} 战成平手。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1);
                break;
        }
        player.MarkDirty(PlayerSectionKind.Duel);
        player.MarkDirty(PlayerSectionKind.Progression);
        action.ProgressSeconds = 0;
    }

    /// <summary>
    /// 技能每次成功施放结算 1 点功法经验：写入装配分段账本并可触发参悟升级。
    /// 与 <see cref="PlayerAggregate.GrantAbilityExperience"/> 同一入账口径，面向战斗逐次小额结算。
    /// </summary>
    private static void GrantAbilityUseExperience(IPlayerResourceContext player, Guid actionId,
        string abilityIdValue, double atUnixSeconds)
    {
        var abilityId = new AbilityId(abilityIdValue);
        // 与 GrantAbilityExperience 同规：参悟经验只对已习得功法累计，未习得功法的施放不入账。
        if (!abilityId.IsValid || !player.Loadout.LearnedAbilities.Contains(abilityId)) return;
        var previousExp = player.Loadout.AbilityExperiences.TryGetValue(abilityId, out var stored) ? stored : 0;
        var nextExp = previousExp + 1;
        player.Loadout.AbilityExperiences[abilityId] = nextExp;
        var levelBefore = AbilityExpCurve.LevelFor(previousExp);
        var levelAfter = AbilityExpCurve.LevelFor(nextExp);
        player.RecordStats(actionId, atUnixSeconds, new PlayerStatsDelta
        {
            AbilityId = abilityIdValue,
            AbilityExperience = 1,
            LevelUp = levelAfter > levelBefore
                ? new PlayerReportLevelUp("ability", abilityIdValue, levelBefore, levelAfter) : null
        });
        player.MarkDirty(PlayerSectionKind.Loadout);
    }

    /// <summary>团灭分流到引擎参数：原地复活遭遇携带复活读条秒数，团灭即败遭遇传 0（引擎按 EnemyWon 终局处理）。</summary>
    private static double InBattleRespawnSeconds(CombatEncounterDefinition encounter)
        => encounter.OnPartyWipe == PartyWipeRule.Respawn ? encounter.PlayerRespawnSeconds : 0;

    private static CombatExpeditionState CreateSearchState(
        IPlayerResourceContext player,
        IPlayerActionState action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog? gameData,
        long? seed = null,
        int waveNumber = 1)
    {
        if (gameData is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻配表不能为空。");
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber);
    }

    internal static CombatExpeditionState CaptureCombatState(CombatEncounterState battle, CombatExpeditionState template) => template with
    {
        BattleElapsedSeconds = battle.ElapsedSeconds,
        PlayerHitpoints = battle.Player.Hitpoints,
        PlayerManapoints = battle.Player.Manapoints,
        PlayerNextAttackAtSeconds = battle.Player.NextAttackAtSeconds,
        PlayerAttackCount = battle.Player.AttackCount,
        PlayerThreat = battle.Player.Threat,
        Outcome = battle.Outcome,
        // 倒地窗口的复活截止点随相位持久化；存活或战斗终局时回到 null。
        PlayerRespawnAtSeconds = double.IsFinite(battle.PlayerRespawnAtSeconds) ? battle.PlayerRespawnAtSeconds : null,
        // 玩家单位的完整战斗快照（吟唱槽位/逐槽冷却/施放计数/战内 Buff）随相位持久化，恢复后序列不漂移。
        PlayerState = CombatEncounterState.Snapshot(battle.Player),
        Enemies = battle.Enemies.Select(enemy => new ExpeditionEnemyState(
            enemy.DefinitionId ?? enemy.Id,
            CombatEncounterState.Snapshot(enemy),
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityText(enemy.Skills[enemy.PendingSkillIndex].Ability, "id") : null,
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityNumber(enemy.Skills[enemy.PendingSkillIndex].Ability, "castMilliseconds") / 1000d * enemy.Stats.CastTimeFactor
                    * PercentValue.Multiplier(enemy.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear)
                : enemy.Stats.AttackIntervalSeconds)).ToArray()
    };

    /// <summary>
    /// 击杀单只怪物的全部奖励：修为、底蕴、六维经验与掉落。
    /// 斗法开启时掉落爆率乘以配置加成、掉落物进入暂存区；所有收益计入当前战斗账本。
    /// </summary>
    private static void ApplyCombatRewards(
        IPlayerResourceContext player,
        CombatMonsterDefinition monster,
        long seed,
        bool pvpEnabled,
        double killAtUnixSeconds,
        GameDataCatalog gameData,
        Guid actionInstanceId,
        ref Dictionary<ItemId, double>? deltas)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.reward");
        if (monster.Experience > 0)
        {
            // 击杀奖励单参数（总纲 §7.11.1）：experience 一笔同时结算——修为入账（瓶颈截断 + 底蕴
            // 1:1 兑换放大 ≤3×、溢量沉淀）、战斗经验按基础量等额入账、六维按总量等额分配；
            // 满级（cap≤0）修为部分全额沉淀为底蕴，六维与战斗经验不受影响。
            var expCap = gameData.RealmProgression?.ExpCapFor(player.Progression.RealmLevel) ?? double.PositiveInfinity;
            var creditedExp = player.Progression.CreditExperience(monster.Experience, monster.Experience, expCap);
            player.RecordStats(actionInstanceId, killAtUnixSeconds, new PlayerStatsDelta
            {
                Experience = creditedExp,
                Foundation = monster.Experience - creditedExp,
                CombatExperience = monster.Experience
            });
            DistributeRootExp(player, monster.Experience, gameData, actionInstanceId, killAtUnixSeconds);
        }
        var dropBonus = pvpEnabled ? PercentValue.Multiplier(gameData.Duel.DropChanceBonusPercent, PercentCurveMode.Linear) : 1;
        foreach (var drop in monster.DropTable)
        {
            if (!drop.ItemId.IsValid || drop.MinQuantity <= 0 || drop.MaxQuantity < drop.MinQuantity || drop.DropChance <= 0) continue;
            // 判定与数量抽样分属两条独立通道：判定 roll 来自主流递推，
            // 数量 roll 用固定盐位 XOR 出的侧流，彼此不共享抽样值。
            var roll = (unchecked((ulong)DeterministicSequence.NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
            var quantityRoll = (unchecked((ulong)DeterministicSequence.NextSeed(seed ^ DropQuantityChannelSalt)) % 1_000_000UL) / 1_000_000d;
            seed = DeterministicSequence.NextSeed(seed);
            if (roll > Math.Min(1, drop.DropChance * dropBonus)) continue;
            // 数量为闭区间 [min, max] 整数均匀抽样：跨度 +1 保证最大数量可达。
            var quantity = Math.Floor(drop.MinQuantity + (drop.MaxQuantity - drop.MinQuantity + 1) * quantityRoll);
            quantity = Math.Clamp(quantity, drop.MinQuantity, drop.MaxQuantity);
            if (pvpEnabled)
            {
                // 斗法期间掉落进入暂存区：入账延迟与归属由暂存结算/斗法结算决定；StageItem 内部同步记账。
                ActionReportBook.StageCombatItem(player, drop.ItemId, quantity, killAtUnixSeconds,
                    gameData.Duel.StagedBucketSeconds);
            }
            else
            {
                AddDelta(deltas ??= new Dictionary<ItemId, double>(), drop.ItemId, quantity);
                player.RecordStats(actionInstanceId, killAtUnixSeconds, new PlayerStatsDelta { GainedItem = drop.ItemId, GainedQuantity = quantity });
            }
        }
    }

    /// <summary>
    /// 六维经验分配：默认六个维度严格均分（各得总量 1/6）；
    /// 仅已装备内功（discipline=internal）按其 rootAttribute 归属集合调整分配比例，
    /// 未装备内功时任何神通/外功都不改变均分。内功未声明归属视为全修（仍六维均分）。
    /// </summary>
    private static void DistributeRootExp(IPlayerResourceContext player, double amount, GameDataCatalog gameData,
        Guid actionInstanceId, double atUnixSeconds)
    {
        var targets = new HashSet<CoreAspectKind>();
        var anyInternal = false;
        foreach (var slot in player.Loadout.AbilitySlots)
        {
            if (slot.AbilityId is null) continue;
            if (!gameData.Abilities.TryGetValue(slot.AbilityId, out var ability)
                || DeterministicCombatEngine.AbilityText(ability, "discipline") != "internal") continue;
            anyInternal = true;
            if (TryParseCoreAspect(slot.AbilityId, gameData, out var attribute))
                targets.Add(attribute);
            else
            {
                // 内功未声明归属维度视为全修：覆盖全部六维。
                targets.UnionWith(Enum.GetValues<CoreAspectKind>());
            }
        }
        if (!anyInternal || targets.Count == 0)
            targets.UnionWith(Enum.GetValues<CoreAspectKind>());
        var share = amount / targets.Count;
        var core = player.Loadout.CoreAttributes;
        foreach (var attribute in targets)
        {
            var expBefore = RootExpOf(core, attribute);
            var levelBefore = RootAttributeCurve.LevelFor(expBefore);
            var levelAfter = RootAttributeCurve.LevelFor(expBefore + share);
            core = attribute switch
            {
                CoreAspectKind.Stamina => core with { StaminaExp = core.StaminaExp + share },
                CoreAspectKind.Meridians => core with { MeridiansExp = core.MeridiansExp + share },
                CoreAspectKind.Attack => core with { AttackExp = core.AttackExp + share },
                CoreAspectKind.Bone => core with { BoneExp = core.BoneExp + share },
                CoreAspectKind.Agility => core with { AgilityExp = core.AgilityExp + share },
                _ => core with { ConsciousnessExp = core.ConsciousnessExp + share }
            };
            var aspect = attribute;
            player.RecordStats(actionInstanceId, atUnixSeconds, new PlayerStatsDelta
            {
                RootAspect = aspect,
                RootExperience = share,
                LevelUp = levelAfter > levelBefore
                    ? new PlayerReportLevelUp("root", aspect.ToString().ToLowerInvariant(), levelBefore, levelAfter) : null
            });
        }
        player.Loadout.CoreAttributes = core;
    }

    /// <summary>按根骨维度读取累计经验（六维升级判定用）。</summary>
    private static double RootExpOf(PlayerCoreAttributes core, CoreAspectKind attribute) => attribute switch
    {
        CoreAspectKind.Stamina => core.StaminaExp,
        CoreAspectKind.Meridians => core.MeridiansExp,
        CoreAspectKind.Attack => core.AttackExp,
        CoreAspectKind.Bone => core.BoneExp,
        CoreAspectKind.Agility => core.AgilityExp,
        _ => core.ConsciousnessExp
    };

    private static bool TryParseCoreAspect(string abilityId, GameDataCatalog catalog, out CoreAspectKind attribute)
    {
        attribute = default;
        if (!catalog.Abilities.TryGetValue(abilityId, out var ability)
            || !ability.TryGetProperty("rootAttribute", out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        return Enum.TryParse(property.GetString(), true, out attribute) && Enum.IsDefined(attribute);
    }

    private static void AddCombatEvent(ref SimulationEventTail? events, DateTimeOffset from, DateTimeOffset target, double remainingSeconds, string code, string message, ActionId actionId)
    {
        events ??= new SimulationEventTail();
        var elapsed = Math.Max(0, (target - from).TotalSeconds - remainingSeconds);
        AddCombatEventAt(ref events, from.AddSeconds(elapsed), code, message, actionId);
    }

    private static void AddCombatEventAt(ref SimulationEventTail? events, DateTimeOffset at, string code, string message, ActionId actionId)
    {
        events ??= new SimulationEventTail();
        events.Add(new SimulationEvent(at, code, message, actionId));
    }


    /// <summary>战斗域通用栏持续消耗品槽判定：仅 general 组可挂持续消耗品。</summary>
    /// <remarks>由调用方按索引扫描代替迭代器：战斗中逐帧调用路径不分配枚举器，
    /// 且写回槽位只替换当前索引，实时读与原 ToArray 快照语义等价。</remarks>
    private static bool TryGetCombatSustainedSlot(IPlayerResourceContext player, GameDataCatalog gameData, int index,
        out AutoUseSlotState slot, out ItemDefinition item, out ConsumableDefinition consumable)
    {
        slot = player.Settings.AutoUseSlots[index];
        item = null!;
        consumable = null!;
        if (!slot.Enabled || slot.ItemId is null || slot.Group != AutoUseSlotState.SlotGroupGeneral
            || !string.Equals(slot.ActionType, "combat", StringComparison.OrdinalIgnoreCase)) return false;
        var itemId = new ItemId(slot.ItemId);
        if (!gameData.Items.TryGetValue(itemId, out item!)
            || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0 } found
            || !found.UsableInActionKinds.Contains(ActionKind.Combat)) return false;
        consumable = found;
        return true;
    }

    /// <summary>遭遇入场时为空的通用栏持续消耗品槽补能：消耗 1 件物品充入整段时长，条件不满足则跳过。</summary>
    private static void RechargeCombatSustainedSlots(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet? assetBalances, double atUnix, Guid actionInstanceId, CombatEncounterState battle,
        ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out var consumable)) continue;
            if (slot.RemainingDurationSeconds > 0) continue;
            // 持续消耗品的入场补能同样走共享触发判定：条件不满足本场不启用，不扣件。
            if (consumable.Triggers is { Count: > 0 } triggers)
            {
                // 目标 = 存活敌中有效威胁最高者（MaxBy 语义：严格更大才替换，平局保留先见者）。
                CombatantState? triggerTarget = null;
                var battleEnemies = battle.Enemies;
                for (var i = 0; i < battleEnemies.Count; i++)
                    if (battleEnemies[i].IsAlive
                        && (triggerTarget is null || battleEnemies[i].EffectiveThreat > triggerTarget.EffectiveThreat))
                        triggerTarget = battleEnemies[i];
                if (!Domain.Combat.SkillTriggerEvaluator.MatchesAll(triggers, battle.Player,
                        triggerTarget, [battle.Player], battleEnemies)) continue;
            }
            var itemId = item.Id;
            var available = (assetBalances?.Get(new AssetKey(itemId)) ?? 0d)
                + (deltas?.GetValueOrDefault(itemId) ?? 0d);
            if (available < 1d) continue;
            AddDelta(deltas ??= new Dictionary<ItemId, double>(), itemId, -1d);
            player.RecordStats(actionInstanceId, atUnix, new PlayerStatsDelta { LostItem = itemId, LostQuantity = 1 });
            player.Settings.AutoUseSlots[index] = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
            player.MarkDirty(PlayerSectionKind.Settings);
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(DateTimeOffset.FromUnixTimeMilliseconds((long)(atUnix * 1000)),
                "auto-use.activated", $"自动使用 {item.Name}，可生效 {consumable.DurationSeconds:0.######} 秒。",
                null, new Dictionary<ItemId, double> { [itemId] = -1d }));
        }
    }

    /// <summary>将持续消耗品的生效 Buff 注入本次遭遇：时长对齐槽位剩余时长，耗尽即自然过期。</summary>
    private static void InjectSustainedCombatBuffs(IPlayerResourceContext player, GameDataCatalog gameData,
        CombatEncounterState battle)
    {
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out _)) continue;
            if (slot.RemainingDurationSeconds <= 0
                || !gameData.RawItems.TryGetValue(item.Id.Value, out var rawItem)
                || !rawItem.TryGetProperty("consumable", out var raw)
                || !raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                battle.Player.Buffs.RemoveAll(current => current.BuffId == buffId);
                battle.Player.Buffs.Add(new CombatBuffState(buffId,
                    battle.ElapsedSeconds + slot.RemainingDurationSeconds, 1,
                    JsonRead.Number(buff, "hpRegen"), JsonRead.Number(buff, "mpRegen"),
                    JsonRead.Number(buff, "attackSpeedPoints"), JsonRead.Number(buff, "damageAbsorptionPoints"),
                    JsonRead.Number(buff, "threatPoints")));
            }
        }
    }

    /// <summary>战斗存活时钟扣减：仅对本场实际推进的战斗秒数生效。</summary>
    private static void DrainCombatSustainedSlots(IPlayerResourceContext player, GameDataCatalog gameData,
        double consumedBattleSeconds, DateTimeOffset at, ActionId actionId, ref SimulationEventTail? events)
    {
        if (consumedBattleSeconds <= 0) return;
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out _)) continue;
            if (slot.RemainingDurationSeconds <= 0) continue;
            var remaining = Math.Max(0d, slot.RemainingDurationSeconds - consumedBattleSeconds);
            player.Settings.AutoUseSlots[index] = slot with { RemainingDurationSeconds = remaining };
            player.MarkDirty(PlayerSectionKind.Settings);
            if (remaining > 0) continue;
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(at, "auto-use.depleted", $"{item.Name} 的持续时间已耗尽。", actionId));
        }
    }

    /// <summary>波次敌人攻击计数求和：保持原 LINQ Sum 的逐项累加顺序（double 累加顺序参与位级确定性）。</summary>
    private static double SumEnemyAttackCounts(IReadOnlyList<ExpeditionEnemyState> waveEnemies)
    {
        var total = 0d;
        for (var i = 0; i < waveEnemies.Count; i++) total += waveEnemies[i].State.AttackCount;
        return total;
    }


}
