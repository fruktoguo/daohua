using System.Collections.ObjectModel;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
namespace DaoHua.Ecs;

public static partial class SimulationEngine
{
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

    private static CombatEncounterState? AdvanceCombatPreparationCore(IPlayerResourceContext player,
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

    private static void CompleteCombatAdvanceCore(IPlayerResourceContext player, IPlayerActionState action,
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
                player.RecordStats(action.Id, deathAt.ToUnixTimeMilliseconds() / 1000d, report => report.Deaths += 1);
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
                report => report.Deaths += 1);
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
        player.RecordStats(action.Id, killAtUnix, report =>
        {
            report.Cycles += 1;
            foreach (var enemy in waveEnemies)
            {
                switch (gameData.CombatMonsters[enemy.EnemyId].Rank)
                {
                    case CombatMonsterRank.Elite: report.EliteKills += 1; break;
                    case CombatMonsterRank.Boss: report.BossKills += 1; break;
                    default: report.NormalKills += 1; break;
                }
            }
        });
        player.MarkDirty(PlayerSectionKind.Loadout);
        player.MarkDirty(PlayerSectionKind.Progression);
        AddCombatEvent(ref events, from, target, remainingSeconds, "combat.won",
            $"讨伐成功：{string.Join("、", waveEnemies.Select(enemy => gameData.CombatMonsters[enemy.EnemyId].Name))}。", definition.Id);

        completedActions += 1;
        completedRepeats += 1;
        action.CompletedRepeats += 1;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
        if (action.RemainingRepeats == 0)
        {
            if (player.Settings.LoopEnabled)
            {
                action.RemainingRepeats = action.Intent.RepeatCount;
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(updatedBattle.Seed), waveNumber: 1);
                action.ProgressSeconds = 0;
                if (player.Actions.Count > 1)
                {
                    player.RotateActionToTail(action.Id);
                }
                return;
            }
            player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: killAtUnix);
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

}

/// <summary>战斗准备阶段实际消费后的窗口余量与可选新战斗。</summary>
public readonly record struct CombatEntryPreparationStep(CombatEncounterState? Battle, double RemainingSeconds);
