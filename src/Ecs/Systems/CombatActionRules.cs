using System.Text;
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
        // 行动账本在搜寻阶段即建立：搜寻期的自然回复/药品激活等战外结算也要计入本次行动统计。
        player.EnsureActionReport(action.Id, definition.Id, definition.Name, at);
        if (action.Participation?.Spec is { } spec)
        {
            // 行动参与会话：遭遇与首波种子由共享剧本钉死，不走节点池与完成轮次派生（总纲 §21.13）。
            var pinned = gameData.CombatEncounters.TryGetValue(spec.EncounterId, out var found)
                ? found
                : throw new DomainException(DomainErrorCode.NotFound, $"参与会话遭遇 {spec.EncounterId} 不存在。");
            action.Expedition = CreateSearchState(player, action, pinned, CombatExpeditionPhase.InitialSearch, gameData);
            mutations.AddEvent(new SimulationEvent(at, "combat.search.started", $"参与搜寻：{pinned.Name}。", definition.Id));
            return;
        }
        var encounter = ResolveCombatEncounterForAction(player, definition, action.CompletedRepeats, gameData);
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.InitialSearch, gameData);
        mutations.AddEvent(new SimulationEvent(at, "combat.search.started", $"开始搜寻：{encounter.Name}。", definition.Id));
    }

    /// <summary>
    /// 驻留远征遭遇是否仍由当前位置供给：钉死（CreateEncounter 手动入战）或
    /// 斗法相位（匹配等待/剧本回放）的远征与地点无关恒可续跑；池选远征按驻留节点遭遇池判定——
    /// 驻足且有池须命中池内遭遇，旅途或空池节点回落行动钉死遭遇口径。
    /// </summary>
    internal static bool ExpeditionServedHere(IPlayerResourceContext player, ActionDefinition definition,
        CombatExpeditionState expedition, GameDataCatalog gameData)
    {
        if (expedition.IsPinned
            || expedition.Phase is CombatExpeditionPhase.DuelPending or CombatExpeditionPhase.Duel)
            return true;
        var pool = player.NodeEncounterIds
            ?? (gameData.WorldGraph.TryGetNode(player.World.NodeId, out var node) ? node.EncounterIds : []);
        return pool.Count > 0 && string.IsNullOrWhiteSpace(player.World.EdgeId)
            ? pool.Contains(expedition.EncounterId)
            : string.Equals(expedition.EncounterId, definition.CombatEncounterId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 讨伐迁地重锚：远征遭遇不属于当前驻留地点时按新地点重建——
    /// 一般相位直接作废远征，交由初始化就地重开搜寻；团灭复活读条相位保留「仍待起身」语义，
    /// 改挂新地点遭遇的复活读条。返回 true 表示发生重锚（调用方负责释放挂接中的旧战斗实体）。
    /// </summary>
    internal static bool ReanchorExpeditionIfMoved(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData)
    {
        if (action.Expedition is not { } expedition
            || ExpeditionServedHere(player, definition, expedition, gameData))
            return false;
        if (expedition.Phase == CombatExpeditionPhase.Respawning)
        {
            var encounter = ResolveCombatEncounterForAction(player, definition, action.CompletedRepeats, gameData);
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                gameData, DeterministicSequence.NextSeed(expedition.Seed), expedition.WaveNumber) with
            {
                Phase = CombatExpeditionPhase.Respawning,
                PhaseDurationSeconds = encounter.PlayerRespawnSeconds
            };
        }
        else
        {
            action.Expedition = null;
        }
        action.ProgressSeconds = 0;
        return true;
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
        ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
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
            var respawnSpec = action.Participation?.Spec;
            // 参与会话复活上限取冻结面板：资源池展示值可越过实时属性上限，但入场钳制始终以冻结上限为准。
            var respawnMember = respawnSpec is not null && expedition.SelfIndex < respawnSpec.Members.Count
                ? respawnSpec.Members[expedition.SelfIndex] : null;
            player.Resources.Hitpoints = respawnMember?.Stats.MaxHitpoints ?? player.Resources.MaxHitpoints;
            player.Resources.Manapoints = respawnMember?.Stats.MaxManapoints ?? player.Resources.MaxManapoints;
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
                player.Settings.AutoUseSlots[i] = player.Settings.AutoUseSlots[i] with { CooldownUntilUnixSeconds = 0 };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Combat);
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData,
                DeterministicSequence.NextSeed(expedition.Seed),
                allies: respawnSpec is not null ? ResetAllyCarry(respawnSpec, expedition.SelfIndex) : null);
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
            foreach (var (asset, quantity) in preparation.AssetDeltas)
                AddDelta(deltas ??= new Dictionary<AssetKey, double>(), asset, quantity);
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
        ref double remainingSeconds, ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events,
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
        var spec = action.Participation?.Spec;
        // 战斗持续消耗品按战斗存活时钟扣减：仅战斗进行中消耗（含躺尸等待复活）；
        // 参与会话消费开打定格画面，槽位不扣减。
        if (spec is null)
            DrainCombatSustainedSlots(player, gameData, consumed,
                from.AddSeconds(actionElapsedBeforeCombat + consumed), definition.Id, ref events);
        // 玩家技能与普攻共用同一条动作时间线：战后把通用槽位状态折算回跨战持久化形态。
        player.Combat.Skills = PlayerCombatSkillState.FromCombatant(battleState.Player, battleState.ElapsedSeconds);
        remainingSeconds -= consumed;
        player.Resources.Hitpoints = battleState.Player.Hitpoints;
        player.Resources.Manapoints = battleState.Player.Manapoints;
        // 战斗中气血是独立持久化分段；仅标记行动队列会导致重连后回退到旧气血。
        player.MarkDirty(PlayerSectionKind.Combat);
        // 战斗单位统计标脏收口：本次推进内若有伤害/治疗落账，重建行动账本导出文档并推送 duel 分段。
        ActionReportBook.FlushCombatStats(player, action.Id);
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
        string LabelOf(string combatantId) => combatantId == "player" ? "你"
            : combatantId.StartsWith("ally:", StringComparison.Ordinal)
                && int.TryParse(combatantId[5..], out var allyIndex) && spec is not null
                && allyIndex >= 0 && allyIndex < spec.Members.Count
                ? spec.Members[allyIndex].DisplayName
            : combatantId.StartsWith("enemy:", StringComparison.Ordinal)
                && int.TryParse(combatantId[6..], out var enemyIndex) && enemyIndex >= 0 && enemyIndex < waveEnemies.Count
                ? $"{{{{enemy:{waveEnemies[enemyIndex].EnemyId}|{gameData.CombatMonsters[waveEnemies[enemyIndex].EnemyId].Name}}}}}"
                : combatantId;
        // 阵亡/起身与战斗事件共用同一战斗时钟：按时刻交错落日志，每次阵亡逐帧计败走与死亡。
        var deaths = combatResult.PlayerDeathTimestamps ?? [];
        var respawns = combatResult.PlayerRespawnTimestamps ?? [];
        var allyDeaths = combatResult.AllyDeathMarks ?? [];
        var allyRespawns = combatResult.AllyRespawnMarks ?? [];
        // 局部函数无法捕获外层 ref 参数：提前初始化后用普通引用落事件。
        events ??= new SimulationEventTail();
        var eventSink = events;
        int deathCursor = 0, respawnCursor = 0, allyDeathCursor = 0, allyRespawnCursor = 0;
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
            // 队友倒地/起身走同一时钟游标：只落战报不计本机败绩（败绩由各成员自身模拟记）。
            while (allyDeathCursor < allyDeaths.Count && allyDeaths[allyDeathCursor].AtSeconds <= uptoBattleSeconds + 1e-9)
            {
                var mark = allyDeaths[allyDeathCursor++];
                var allyAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, mark.AtSeconds - battleFrom));
                var allyName = spec is not null && mark.MemberIndex >= 0 && mark.MemberIndex < spec.Members.Count
                    ? spec.Members[mark.MemberIndex].DisplayName : $"队友{mark.MemberIndex + 1}";
                eventSink.Add(new SimulationEvent(allyAt,
                    "combat.lost", $"队友 {allyName} 阵亡，原地调息等待复活。", definition.Id));
            }
            while (allyRespawnCursor < allyRespawns.Count && allyRespawns[allyRespawnCursor].AtSeconds <= uptoBattleSeconds + 1e-9)
            {
                var mark = allyRespawns[allyRespawnCursor++];
                var allyAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, mark.AtSeconds - battleFrom));
                var allyName = spec is not null && mark.MemberIndex >= 0 && mark.MemberIndex < spec.Members.Count
                    ? spec.Members[mark.MemberIndex].DisplayName : $"队友{mark.MemberIndex + 1}";
                eventSink.Add(new SimulationEvent(allyAt,
                    "combat.respawned", $"队友 {allyName} 原地起身，气血回满。", definition.Id));
            }
        }
        // 功法经验按帧入账：日志尾窗裁剪与施放合并都只影响展示，每帧结算不丢账。
        foreach (var combatEvent in combatResult.Events)
        {
            if (combatEvent.AttackerId == "player" && combatEvent.AbilityId is { } usedAbility)
                GrantAbilityUseExperience(player, action.Id, usedAbility,
                    from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom)).ToUnixTimeMilliseconds() / 1000d);
        }
        // 尾窗门控按「可产出日志的帧」计容量：普攻与无技能衍生帧不产行不占额；
        // 技能帧按成员帧计数（合并后实际行数只少不多，不超尾窗），超额帧按时间序直接跳过，
        // 不再为它们构造日志文本；窗口内的阵亡/起身衍生帧仍按战斗时钟交错落日志。
        var emittedIndex = 0;
        var keepFrom = double.NegativeInfinity;
        var emittable = 0;
        for (var i = combatResult.Events.Count - 1; i >= 0 && emittable < SimulationEventTail.TailCapacity; i--)
        {
            var frame = combatResult.Events[i];
            if (frame.AbilityId is not null && ++emittable == SimulationEventTail.TailCapacity)
                keepFrom = frame.AtSeconds;
        }
        var eventIndex = 0;
        while (eventIndex < combatResult.Events.Count)
        {
            var combatEvent = combatResult.Events[eventIndex];
            if (combatEvent.AtSeconds < keepFrom)
            {
                // 该帧不可保留：折叠已滞留域事件并推进阵亡/起身游标，跳过名称与消息构造。
                eventSink.Fold(emittedIndex);
                emittedIndex = eventSink.Count;
                FlushDownedEvents(combatEvent.AtSeconds);
                emittedIndex = eventSink.Count;
                eventIndex++;
                continue;
            }
            // 普通攻击与无技能衍生帧不进战报：只推进阵亡/起身游标，不构造名称与文本。
            if (combatEvent.AbilityId is not { } abilityId)
            {
                FlushDownedEvents(combatEvent.AtSeconds);
                emittedIndex = eventSink.Count;
                eventIndex++;
                continue;
            }
            // 同一次施放的连续帧合并为一条战报：AoE 逐目标各产一帧，汲取类衍生帧同属该次施放。
            var castEnd = eventIndex + 1;
            while (castEnd < combatResult.Events.Count
                && combatResult.Events[castEnd].AbilityId == abilityId
                && combatResult.Events[castEnd].AttackerId == combatEvent.AttackerId
                && combatResult.Events[castEnd].AtSeconds == combatEvent.AtSeconds)
                castEnd++;
            var eventAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom));
            var clauses = new List<string>(castEnd - eventIndex);
            for (var i = eventIndex; i < castEnd; i++)
            {
                var frame = combatResult.Events[i];
                AppendCastClause(clauses, LabelOf(frame.TargetId), frame.Hit, frame.Damage, frame.Healing);
            }
            var builder = BeginCastMessage(LabelOf(combatEvent.AttackerId), abilityId, gameData);
            AppendCastClauses(builder, clauses);
            AddCombatEventAt(ref events, eventAt, "combat.skill", builder.Append('。').ToString(), definition.Id);
            emittedIndex = eventSink.Count;
            FlushDownedEvents(combatEvent.AtSeconds);
            emittedIndex = eventSink.Count;
            eventIndex = castEnd;
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
            // 组队散场保留全员倒地镜像供复活读条展示；读条完成时统一重置满状态。
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData, updatedBattle.Seed,
                waveNumber: 1, allies: spec is not null ? CarryAllies(battleState, spec, reviveDowned: false) : null) with
            { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
            action.ProgressSeconds = 0;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.lost", $"讨伐失败：{WaveNames()}。", definition.Id);
            return;
        }

        // 波次清空才结算掉落与完成一次重复；逐只发放击杀奖励并计入生平斩妖。
        // 参与会话：本机倒地被成员带赢时按原地复活口径满状态起身（复活读条同规：气血灵力全满、技能复位）。
        if (spec is not null && !battleState.Player.IsAlive)
        {
            var member = spec.Members[expedition.SelfIndex];
            player.Resources.Hitpoints = member.Stats.MaxHitpoints;
            player.Resources.Manapoints = member.Stats.MaxManapoints;
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            battleState.Player.Hitpoints = member.Stats.MaxHitpoints;
            battleState.Player.Manapoints = member.Stats.MaxManapoints;
        }
        var pvpEnabled = action.Intent.PvpEnabled;
        var killAtUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
        var killSeed = updatedBattle.Seed;
        foreach (var enemy in waveEnemies)
        {
            var monster = gameData.CombatMonsters[enemy.EnemyId];
            ApplyCombatRewards(player, monster, killSeed, pvpEnabled, killAtUnix, gameData, action.Id, ref deltas,
                spec, expedition.SelfIndex);
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
        // 新手引导战斗里程碑口径 = 讨伐行动完成一轮（波次清空），与战报同一时序入账。
        // 行动参与会话不推进个人引导；参与会话结束由组织适配器裁决。
        var tutorialAdvanced = action.Participation is null
            && TutorialRules.OnActionRoundCompleted(player, definition.Id,
                from.AddSeconds((target - from).TotalSeconds - remainingSeconds), ref deltas, ref events);

        completedActions += 1;
        completedRepeats += 1;
        PlayerActionQueueRules.RecordCompletedRound(action);
        // 引导推进时截断剩余轮次并强制离队（loopable=false 压制 LoopEnabled 轮转）：
        // 否则 ∞ 讨伐永驻队首，引导完成后玩家行动队列仍被占着。
        if (tutorialAdvanced) action.RemainingRepeats = 0;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (action.RemainingRepeats == 0)
        {
            if (player.Settings.LoopEnabled && !tutorialAdvanced)
            {
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(updatedBattle.Seed), waveNumber: 1,
                    allies: spec is not null ? CarryAllies(battleState, spec) : null);
                player.FinishActionRound(action.Id, loopable: true, killAtUnix);
                return;
            }
            player.FinishActionRound(action.Id, loopable: !tutorialAdvanced, killAtUnix);
            return;
        }
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
            gameData, DeterministicSequence.NextSeed(updatedBattle.Seed), updatedBattle.WaveNumber + 1,
            allies: spec is not null ? CarryAllies(battleState, spec) : null);
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
        var selfSnapshot = expedition.PlayerState ?? new CombatantSnapshot(
            expedition.PlayerHitpoints, expedition.PlayerManapoints,
            expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount, expedition.PlayerThreat);
        CombatEncounterState battleState;
        if (action.Participation?.Spec is { } spec)
        {
            // 组队恢复：盟友模板/快照按共享剧本成员序重建，战外 Buff 消费开打定格视图。
            var selfIndex = expedition.SelfIndex;
            var allyTemplates = new CombatantTemplate[spec.Members.Count];
            var allySnapshots = new CombatantSnapshot[spec.Members.Count];
            for (var i = 0; i < spec.Members.Count; i++)
            {
                allyTemplates[i] = new CombatantTemplate(spec.Members[i].Stats, spec.Members[i].Skills);
                allySnapshots[i] = i == selfIndex
                    ? selfSnapshot with { RespawnAtSeconds = expedition.PlayerRespawnAtSeconds }
                    : FindAllyCarry(expedition.Allies, i)?.State
                        ?? throw new DomainException(DomainErrorCode.InvariantViolation,
                            $"组队远征缺少成员 {i} 的战斗快照。");
            }
            battleState = CombatEncounterState.RestoreAllied(
                encounter.Id,
                expedition.Seed,
                allyTemplates,
                allySnapshots,
                selfIndex,
                templates,
                waveEnemies.Select(enemy => enemy.State).ToArray(),
                expedition.BattleElapsedSeconds,
                expedition.Outcome,
                SumAllyAttackCounts(allySnapshots) + SumEnemyAttackCounts(waveEnemies),
                InBattleRespawnSeconds(encounter));
            for (var i = 0; i < spec.Members.Count; i++)
            {
                var memberBuffs = spec.Members[i].ExternalBuffs;
                battleState.PlayerAllies[i].ExternalBuffStacks = buffId => SumFrozenBuffStacks(memberBuffs, buffId);
            }
        }
        else
        {
            // 玩家方技能槽与怪物同构：装配功法折叠为通用技能槽进入同一条动作时间线。
            var playerTemplate = new CombatantTemplate(
                player.ResolveAttributes(gameData).ToCombatant(player.Progression.CombatExperience),
                SkillSlot.FromAbilitySlots(player.Loadout.AbilitySlots, gameData.Abilities, player.Loadout.AbilityTriggers));
            battleState = CombatEncounterState.Restore(
                encounter.Id,
                expedition.Seed,
                playerTemplate,
                selfSnapshot,
                templates,
                waveEnemies.Select(enemy => enemy.State).ToArray(),
                expedition.BattleElapsedSeconds,
                expedition.Outcome,
                expedition.PlayerAttackCount + SumEnemyAttackCounts(waveEnemies),
                InBattleRespawnSeconds(encounter),
                expedition.PlayerRespawnAtSeconds ?? double.PositiveInfinity);
            // 玩家战外持久 Buff 注入触发求值视图（镜像语义与开战入口一致）。
            battleState.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(player.Buffs.Active, buffId);
        }
        for (var i = 0; i < waveEnemies.Count; i++)
            battleState.Enemies[i].DefinitionId = waveEnemies[i].EnemyId;
        AttachCombatStats(player, action, battleState, action.Participation?.Spec, gameData);
        return battleState;
    }

    /// <summary>
    /// 把行动实例账本的战斗单位统计簿挂接到遭遇战：预注册本机/盟友单位与本波怪物类型桶
    /// （同类型怪合并为 monster:{defId} 单位），账本不存在时保持 null 由结算点零成本旁路。
    /// </summary>
    private static void AttachCombatStats(IPlayerResourceContext player, IPlayerActionState action,
        CombatEncounterState battle, ActionParticipationSpec? spec, GameDataCatalog gameData)
    {
        battle.StatsBook = player.Duel.ReportFor(action.Id)?.CombatStats;
        if (battle.StatsBook is not { } book) return;
        var selfIndex = battle.SelfIndex;
        for (var i = 0; i < battle.PlayerAllies.Count; i++)
            book.Register(i == selfIndex ? "player" : $"ally:{i}",
                spec?.Members[i].DisplayName ?? (i == selfIndex ? player.DisplayName : ""));
        foreach (var enemy in battle.Enemies)
            if (enemy.DefinitionId is { } definitionId)
                book.Register($"monster:{definitionId}",
                    gameData.CombatMonsters.TryGetValue(definitionId, out var monster) ? monster.Name : definitionId);
    }

    /// <summary>
    /// 创建战斗行动的搜寻阶段快照。该原语只生成可持久化的阶段数据，供 ECS 的搜寻系统
    /// 作为实体组件状态写入；战斗实体创建、边界推进和奖励结算仍由各自 ECS 系统负责。
    /// </summary>
    /// <param name="spec">行动参与共享剧本规格：非空时遭遇钉死、种子取自剧本链、寻敌速度取参与者最大、
    /// 附带盟友镜像；null 为单人池选遭遇。</param>
    /// <param name="allies">跨波携带的盟友镜像（仅参与会话）；null 时按规格冻结面板重建初始镜像。</param>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        double completedRepeats,
        long? seed = null,
        int waveNumber = 1,
        ActionParticipationSpec? spec = null,
        IReadOnlyList<ExpeditionAllyState>? allies = null)
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

        // 首波使用遭遇固定搜寻读条；波间搜寻按行动速度点数除数缩短（境界成长已注入该通道）。
        // 参与会话寻敌速度取共享剧本定格的参与者最大值（探路最快者带路），各成员推演位级一致。
        var actionSpeedPoints = spec?.SearchSpeedPoints ?? player.ResolveAttributes(gameData).ActionSpeedPoints;
        var duration = phase == CombatExpeditionPhase.InitialSearch
            ? encounter.SearchSeconds
            : encounter.RespawnSeconds * PercentValue.Multiplier(actionSpeedPoints, PercentCurveMode.NonLinear);
        if (!double.IsFinite(duration) || duration <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻时长无效。");
        var selfIndex = spec?.SelfIndexOf(player.PlayerId) ?? 0;
        return new CombatExpeditionState(
            encounter.Id,
            phase,
            0,
            duration,
            seed ?? spec?.Seed ?? CreateSeed(encounter.Id, completedRepeats),
            0,
            player.Resources.Hitpoints,
            player.Resources.Manapoints,
            0,
            0,
            0,
            CombatOutcome.Running,
            waveNumber,
            null)
        {
            OriginNodeId = spec?.NodeId ?? player.World.NodeId,
            // 组队远征钉死（会话剧本供给）+ 本机成员下标 + 盟友镜像。
            IsPinned = spec is not null,
            SelfIndex = selfIndex,
            Allies = spec is null ? null : allies ?? BuildAllyCarry(spec, selfIndex)
        };
    }

    /// <summary>按领域行动实例创建搜寻阶段的便捷重载；不会推进行动或修改玩家状态。</summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        IPlayerActionState action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        long? seed = null,
        int waveNumber = 1,
        IReadOnlyList<ExpeditionAllyState>? allies = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber, action.Participation?.Spec, allies);
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
        var enemyTemplates = wave.Select(monster => new CombatantTemplate(monster.Stats, monster.Skills)).ToArray();
        var respawnSeconds = InBattleRespawnSeconds(encounter);
        var spec = action.Participation?.Spec;
        CombatEncounterState battle;
        if (spec is not null)
        {
            // 组队入场：全部成员以开打定格面板为盟友阵列（总纲 §21.13），本机成员也不例外——
            // 战外 Buff/技能冷却一律消费冻结值，否则各成员模拟里同一单位状态不同步。
            var selfIndex = completedSearch.SelfIndex;
            var allyTemplates = new CombatantTemplate[spec.Members.Count];
            for (var i = 0; i < spec.Members.Count; i++)
                allyTemplates[i] = new CombatantTemplate(spec.Members[i].Stats, spec.Members[i].Skills);
            battle = CombatEncounterState.CreateAllied(encounter.Id, completedSearch.Seed,
                allyTemplates, selfIndex, enemyTemplates, respawnSeconds);
            var carried = completedSearch.Allies;
            for (var i = 0; i < spec.Members.Count; i++)
            {
                var member = spec.Members[i];
                var unit = battle.PlayerAllies[i];
                if (i == selfIndex)
                {
                    // 本机成员入场镜像 = 战外资源池（战间回复已入账），上限按冻结面板钳制——
                    // 会话期间换装漂移不进入共享战斗，否则各成员推演的同一单位不逐位一致。
                    unit.Hitpoints = Math.Clamp(player.Resources.Hitpoints, 0, member.Stats.MaxHitpoints);
                    unit.Manapoints = Math.Clamp(player.Resources.Manapoints, 0, member.Stats.MaxManapoints);
                    player.Combat.Skills.ApplyTo(unit);
                }
                else
                {
                    var carry = FindAllyCarry(carried, i);
                    unit.Hitpoints = Math.Clamp(carry?.State.Hitpoints ?? member.InitialHitpoints, 0, member.Stats.MaxHitpoints);
                    unit.Manapoints = Math.Clamp(carry?.State.Manapoints ?? member.InitialManapoints, 0, member.Stats.MaxManapoints);
                    (carry?.CombatSkills ?? member.CombatSkills)?.ApplyTo(unit);
                }
                unit.ExternalBuffStacks = buffId => SumFrozenBuffStacks(member.ExternalBuffs, buffId);
                // 持续消耗品 Buff 按开打定格注入：剩余时长按会话墙钟折算（入场时刻 - 开打时刻），
                // 波间搜索同样消耗 Buff 时长；会话期间不补能、不扣减本地槽位（各成员推演位级一致）。
                if (member.SustainedBuffs is { } sustained)
                {
                    var entryAtUnix = entryAt.ToUnixTimeMilliseconds() / 1000d;
                    var sessionElapsed = Math.Max(0, entryAtUnix - spec.StartedAtUnixSeconds);
                    foreach (var frozen in sustained)
                    {
                        var sustainedRemaining = frozen.RemainingSeconds - sessionElapsed;
                        if (sustainedRemaining <= 0) continue;
                        unit.Buffs.RemoveAll(entry => entry.BuffId == frozen.BuffId);
                        unit.Buffs.Add(new CombatBuffState(frozen.BuffId, battle.ElapsedSeconds + sustainedRemaining,
                            frozen.Stacks, frozen.HpRegen, frozen.MpRegen, frozen.AttackSpeedPoints,
                            frozen.DamageAbsorptionPoints, frozen.ThreatPoints,
                            frozen.HpRegenPostRatio, frozen.MpRegenPostRatio, frozen.DamageBonusPoints));
                    }
                }
            }
        }
        else
        {
            battle = CombatEncounterState.Create(
                encounter.Id,
                completedSearch.Seed,
                new CombatantTemplate(player.ResolveAttributes(gameData).ToCombatant(player.Progression.CombatExperience),
                    SkillSlot.FromAbilitySlots(player.Loadout.AbilitySlots, gameData.Abilities, player.Loadout.AbilityTriggers)),
                enemyTemplates,
                respawnSeconds);
            battle.Player.Hitpoints = Math.Clamp(player.Resources.Hitpoints, 0, player.Resources.MaxHitpoints);
            battle.Player.Manapoints = Math.Clamp(player.Resources.Manapoints, 0, player.Resources.MaxManapoints);
            battle.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(player.Buffs.Active, buffId);
            player.Combat.Skills.ApplyTo(battle.Player);
        }
        for (var i = 0; i < wave.Count; i++) battle.Enemies[i].DefinitionId = wave[i].Id;
        AttachCombatStats(player, action, battle, spec, gameData);

        Dictionary<AssetKey, double>? deltas = null;
        SimulationEventTail? events = null;
        var waveNames = string.Join("、", wave.Select(monster => monster.Name));
        if (battle.Player.Hitpoints <= 0 && spec is null)
        {
            action.Status = ActionQueueStatus.Blocked;
            action.Expedition = completedSearch;
            AddCombatEventAt(ref events, entryAt, "combat.lost", $"气血衰竭，无法迎战：{waveNames}。", definition.Id);
            if (player.Settings.LoopEnabled && player.Actions.Count > 1 && !AllActionsBlocked(player.Actions))
                player.RotateActionToTail(action.Id);
            return new CombatEntryPreparation(null, completedSearch,
                deltas ?? new Dictionary<AssetKey, double>(), events?.View ?? [], true);
        }
        if (battle.Player.Hitpoints <= 0 && spec is not null)
        {
            // 组队入场即倒地（开打定格气血为 0）：共享剧本照常开打，本机成员按倒地口径登记——
            // 原地复活遭遇进入复活读条，否则等波次清空满状态起身；会话行动不可阻塞/轮转离队首。
            if (respawnSeconds > 0)
                battle.Player.RespawnAtSeconds = battle.ElapsedSeconds + respawnSeconds;
            AddCombatEventAt(ref events, entryAt, "combat.lost",
                $"你以倒地状态随队迎战：{waveNames}。", definition.Id);
        }

        var atUnix = entryAt.ToUnixTimeMilliseconds() / 1000d;
        if (spec is null)
        {
            // 持续消耗品补能/注入只对本机钱包生效；参与会话消费开打定格画面，不走实时槽位。
            RechargeCombatSustainedSlots(player, gameData, assetBalances, atUnix, action.Id, battle,
                ref deltas, ref events);
            InjectSustainedCombatBuffs(player, gameData, battle);
        }
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
            deltas ?? new Dictionary<AssetKey, double>(), events?.View ?? [], false);
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
        ref Dictionary<AssetKey, double>? deltas,
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
        // 帧记账与日志合并分离：血蓝镜像与功法经验逐帧结算，战报按同次施放合并产出。
        void ApplyDuelFrame(DuelScriptEvent frame)
        {
            var frameTargetIsViewer = frame.TargetIsInitiator == playback.ViewerIsInitiator;
            var frameAttackerIsViewer = frame.AttackerIsInitiator == playback.ViewerIsInitiator;
            // 斗法本方施放同样计每次 1 点功法经验；镜像方施放不重复入账。
            if (frameAttackerIsViewer && frame.AbilityId is { } frameAbility)
                GrantAbilityUseExperience(player, action.Id, frameAbility,
                    from.AddSeconds(elapsedBefore + Math.Max(0, frame.AtSeconds - expedition.BattleElapsedSeconds)).ToUnixTimeMilliseconds() / 1000d);
            if (frameTargetIsViewer) { viewerHp = frame.TargetHitpointsAfter; viewerMp = frame.TargetManapointsAfter; }
            else { opponentHp = frame.TargetHitpointsAfter; opponentMp = frame.TargetManapointsAfter; }
            if (frameAttackerIsViewer) viewerMp = frame.AttackerManapointsAfter;
            else opponentMp = frame.AttackerManapointsAfter;
        }
        string DuelLabel(bool initiatorSide) => initiatorSide == playback.ViewerIsInitiator ? "你" : playback.OpponentName;
        // 回放游标前的事件已全部应用过；游标到 playbackTo 之间的事件逐条应用并写日志。
        // 普攻帧不落日志；同一次施放的连续帧（同出手方+技能+时刻）合并为一条施放行。
        for (var i = cursor; i < playback.Events.Count; i++)
        {
            var duelEvent = playback.Events[i];
            if (duelEvent.AtSeconds > playbackTo + 1e-9) break;
            cursor = i + 1;
            ApplyDuelFrame(duelEvent);
            if (duelEvent.AbilityId is null) continue;
            var castEnd = i + 1;
            while (castEnd < playback.Events.Count
                && playback.Events[castEnd].AtSeconds == duelEvent.AtSeconds
                && playback.Events[castEnd].AbilityId == duelEvent.AbilityId
                && playback.Events[castEnd].AttackerIsInitiator == duelEvent.AttackerIsInitiator)
            {
                cursor = castEnd + 1;
                ApplyDuelFrame(playback.Events[castEnd]);
                castEnd++;
            }
            var clauses = new List<string>(castEnd - i);
            for (var j = i; j < castEnd; j++)
            {
                var member = playback.Events[j];
                AppendCastClause(clauses, DuelLabel(member.TargetIsInitiator), member.Hit, member.Damage, member.Healing);
            }
            var builder = BeginCastMessage(DuelLabel(duelEvent.AttackerIsInitiator), duelEvent.AbilityId, gameData);
            AppendCastClauses(builder, clauses);
            AddCombatEventAt(ref events, from.AddSeconds(elapsedBefore + Math.Max(0, duelEvent.AtSeconds - expedition.BattleElapsedSeconds)),
                "combat.skill", builder.Append('。').ToString(), definition.Id);
            i = castEnd - 1;
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
                    AddDelta(deltas ??= new Dictionary<AssetKey, double>(), spoilsId, spoils.Quantity);
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
        int waveNumber = 1,
        IReadOnlyList<ExpeditionAllyState>? allies = null)
    {
        if (gameData is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻配表不能为空。");
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber, action.Participation?.Spec, allies);
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
        // 行动参与会话：全部非本机盟友的完整战斗快照随相位持久化（崩溃恢复后序列不漂移）。
        SelfIndex = battle.SelfIndex,
        Allies = battle.PlayerAllies.Count > 1 ? CarryAllies(battle, null, reviveDowned: false) : null,
        Enemies = battle.Enemies.Select(enemy => new ExpeditionEnemyState(
            enemy.DefinitionId ?? enemy.Id,
            CombatEncounterState.Snapshot(enemy),
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityText(enemy.Skills[enemy.PendingSkillIndex].Ability, "id") : null,
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityNumber(enemy.Skills[enemy.PendingSkillIndex].Ability, "castMilliseconds") / 1000d
                    * PercentValue.Multiplier(enemy.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear)
                : enemy.Stats.AttackIntervalSeconds
                    * PercentValue.Multiplier(enemy.Stats.AttackSpeedPoints, PercentCurveMode.NonLinear)
                    * PercentValue.Multiplier(enemy.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear))).ToArray()
    };

    /// <summary>
    /// 击杀单只怪物的全部奖励：修为、底蕴、六维经验与掉落。
    /// 斗法开启时掉落爆率乘以配置加成、掉落物进入暂存区；所有收益计入当前战斗账本。
    /// 行动参与会话：经验按在场人头平分并乘本机成员等级惩罚；掉率乘参与者增幅合计，单件按归属
    /// 权重掷点——本机账本只记归属自己的件（各成员模拟逐位一致地掷出同一归属）。
    /// </summary>
    private static void ApplyCombatRewards(
        IPlayerResourceContext player,
        CombatMonsterDefinition monster,
        long seed,
        bool pvpEnabled,
        double killAtUnixSeconds,
        GameDataCatalog gameData,
        Guid actionInstanceId,
        ref Dictionary<AssetKey, double>? deltas,
        ActionParticipationSpec? spec = null,
        int selfIndex = 0)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.reward");
        if (monster.Experience > 0)
        {
            // 击杀奖励单参数（总纲 §7.11.1）：experience 一笔同时结算——修为入账（瓶颈截断 + 底蕴
            // 1:1 兑换放大 ≤3×、溢量沉淀）、战斗经验按基础量等额入账、六维各归属维度得全额；
            // 满级（cap≤0）修为部分全额沉淀为底蕴，六维与战斗经验不受影响。
            // 组队会话份额 = 基础量 ÷ 在场人头 ×(1-等级惩罚)（总纲 §21.13）。
            var experience = spec is null
                ? monster.Experience
                : monster.Experience / spec.Members.Count * (1 - spec.LevelMalusFor(selfIndex));
            var expCap = gameData.RealmProgression?.ExpCapFor(player.Progression.RealmLevel) ?? double.PositiveInfinity;
            var creditedExp = player.Progression.CreditExperience(experience, experience, expCap);
            player.RecordStats(actionInstanceId, killAtUnixSeconds, new PlayerStatsDelta
            {
                Experience = creditedExp,
                Foundation = experience - creditedExp,
                CombatExperience = experience
            });
            DistributeRootExp(player, experience, gameData, actionInstanceId, killAtUnixSeconds);
        }
        var dropBonus = pvpEnabled ? PercentValue.Multiplier(gameData.Duel.DropChanceBonusPercent, PercentCurveMode.Linear)
            : spec is not null ? 1 + spec.TotalDropBonusPoints / 100d
            : 1;
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
                // 组队掉落归属：按各成员 (1+增幅)×(1-惩罚) 加权抽样，全零退化均等；
                // 归属掷点走独立盐位侧流——全体成员的模拟掷出同一归属，各自账本只记自己的件。
                if (spec is not null
                    && RollDropAssignment(spec, seed ^ DropAssignmentChannelSalt) != selfIndex)
                    continue;
                AddDelta(deltas ??= new Dictionary<AssetKey, double>(), drop.ItemId, quantity);
                player.RecordStats(actionInstanceId, killAtUnixSeconds, new PlayerStatsDelta { GainedItem = drop.ItemId, GainedQuantity = quantity });
            }
        }
    }

    /// <summary>掉落归属掷点专用盐位（与判定/数量侧流互不相同）。</summary>
    private const long DropAssignmentChannelSalt = unchecked((long)0xC2B2AE3D27D4EB4FUL);

    /// <summary>
    /// 共享掉落的单件归属抽样：权重 = 各成员 (1+掉落增幅点数/100)×(1-等级惩罚)，
    /// 全零权重退化为均匀抽样。掷点输入为规格内冻结面板，各成员模拟逐位一致。
    /// </summary>
    private static int RollDropAssignment(ActionParticipationSpec spec, long seed)
    {
        var roll = (unchecked((ulong)DeterministicSequence.NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
        var total = 0d;
        for (var i = 0; i < spec.Members.Count; i++) total += spec.AssignmentWeightFor(i);
        if (total <= 1e-12)
        {
            // 权重全零：均匀抽签。
            return Math.Min(spec.Members.Count - 1, (int)Math.Floor(roll * spec.Members.Count));
        }
        var threshold = roll * total;
        var cumulative = 0d;
        for (var i = 0; i < spec.Members.Count; i++)
        {
            cumulative += spec.AssignmentWeightFor(i);
            if (threshold < cumulative) return i;
        }
        return spec.Members.Count - 1;
    }

    /// <summary>
    /// 六维经验分配：每个归属维度各得全额 amount（总量 = amount × 归属维度数，无内功恒为 6×）；
    /// 仅已装备内功（discipline=internal）按其 rootAttribute 归属集合调整归属范围，
    /// 未装备内功时任何神通/外功都不改变全维归属。内功未声明归属视为全修（仍覆盖全部六维）。
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
        // 六维全额口径：每个归属维度各得 amount，总量 = amount × 归属维度数（总纲 §7.10.3）。
        var share = amount;
        var core = player.Loadout.CoreAttributes;
        foreach (var attribute in targets)
        {
            var expBefore = RootExpOf(core, attribute);
            var levelBefore = RootAttributeCurve.LevelFor(expBefore, gameData.RealmProgression);
            var levelAfter = RootAttributeCurve.LevelFor(expBefore + share, gameData.RealmProgression);
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

    /// <summary>功法显示名：配表缺失或无名时回退 ID 本体，保证日志引用标签不为空。</summary>
    private static string AbilityName(GameDataCatalog gameData, string abilityId)
        => gameData.Abilities.TryGetValue(abilityId, out var ability)
            ? DeterministicCombatEngine.AbilityText(ability, "name") ?? abilityId
            : abilityId;

    /// <summary>施放行起手段：「{出手方}施放 {{ability:id|名称}}」，功法名走引用标记供前端悬浮卡解析。</summary>
    private static StringBuilder BeginCastMessage(string attackerLabel, string abilityId, GameDataCatalog gameData)
        => new StringBuilder(attackerLabel).Append("施放 {{ability:").Append(abilityId).Append('|')
            .Append(AbilityName(gameData, abilityId)).Append("}}");

    /// <summary>施放合并行的单帧子句收集：未命中 / 伤害 / 治疗按帧序入列；零效果帧（纯增益起手）不产生子句。</summary>
    private static void AppendCastClause(List<string> clauses, string targetLabel, bool hit, double damage, double healing)
    {
        if (!hit)
        {
            clauses.Add($"对{targetLabel}未命中");
            return;
        }
        if (damage > 0)
            clauses.Add($"对{targetLabel}造成 {damage:0.##} 点伤害");
        if (healing > 0)
            clauses.Add($"为{targetLabel}回复 {healing:0.##} 点气血");
    }

    /// <summary>拼装施放子句：单子句沿用行内「，」；多子句以 \n 作分隔符逐条开行，
    /// 前端 LogRow 按 \n 拆行并为续行渲染隐藏前缀占位，使所有「对/为」像素级对齐
    /// （对齐语义同 daojie 的多目标合并战报，服务端不下发缩进字符）。</summary>
    private static void AppendCastClauses(StringBuilder builder, List<string> clauses)
    {
        if (clauses.Count == 1)
        {
            builder.Append('，').Append(clauses[0]);
            return;
        }
        builder.Append('，');
        foreach (var clause in clauses)
            builder.Append('\n').Append(clause);
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
            || slot.ActionType != Domain.GameData.AutoUseBar.Combat) return false;
        var itemId = new ItemId(slot.ItemId);
        if (!gameData.Items.TryGetValue(itemId, out item!)
            || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0, Durable: false } found
            || !found.UsableInBars.Contains(Domain.GameData.AutoUseBar.Combat)) return false;
        consumable = found;
        return true;
    }

    /// <summary>遭遇入场时为空的通用栏持续消耗品槽补能：消耗 1 件物品充入整段时长，条件不满足则跳过。</summary>
    private static void RechargeCombatSustainedSlots(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet? assetBalances, double atUnix, Guid actionInstanceId, CombatEncounterState battle,
        ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out var consumable)) continue;
            if (slot.RemainingDurationSeconds > 0) continue;
            // 持续消耗品冷却自耗竭时刻起算（DrainCombatSustainedSlots 写入）：冷却未走完不入战补能。
            if (slot.CooldownUntilUnixSeconds > atUnix) continue;
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
                        triggerTarget, battle.PlayerAllies, battleEnemies)) continue;
            }
            var itemId = item.Id;
            var available = (assetBalances?.Get(new AssetKey(itemId)) ?? 0d)
                + (deltas?.GetValueOrDefault(new AssetKey(itemId)) ?? 0d);
            if (available < 1d) continue;
            AddDelta(deltas ??= new Dictionary<AssetKey, double>(), itemId, -1d);
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
                    JsonRead.Number(buff, "threatPoints"),
                    JsonRead.Number(buff, "hpRegenPostRatio"), JsonRead.Number(buff, "mpRegenPostRatio")));
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
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out var consumable)) continue;
            if (slot.RemainingDurationSeconds <= 0) continue;
            var remaining = Math.Max(0d, slot.RemainingDurationSeconds - consumedBattleSeconds);
            var updated = slot with { RemainingDurationSeconds = remaining };
            if (remaining <= 0 && consumable.CooldownSeconds > 0)
            {
                // 持续消耗品冷却自耗竭时刻起算：耗竭点在战斗时钟上恰好是本次扣减前的剩余秒数，
                // 折回墙钟 = 片末时刻 - (实耗 - 旧剩余)。按激活时刻起算会让冷却恒早于耗竭结束、永不生效。
                var atUnix = at.ToUnixTimeMilliseconds() / 1000d;
                updated = updated with
                {
                    CooldownUntilUnixSeconds = atUnix - (consumedBattleSeconds - slot.RemainingDurationSeconds)
                        + consumable.CooldownSeconds
                };
            }
            player.Settings.AutoUseSlots[index] = updated;
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

    /// <summary>盟友攻击计数求和：与 <see cref="SumEnemyAttackCounts"/> 同序逐项累加，喂给共享剧本的计数维度。</summary>
    private static double SumAllyAttackCounts(IReadOnlyList<CombatantSnapshot> allySnapshots)
    {
        var total = 0d;
        for (var i = 0; i < allySnapshots.Count; i++) total += allySnapshots[i].AttackCount;
        return total;
    }

    /// <summary>按成员下标取跨相位盟友镜像；无镜像返回 null（调用方回落开打定格初值）。</summary>
    private static ExpeditionAllyState? FindAllyCarry(IReadOnlyList<ExpeditionAllyState>? carried, int memberIndex)
    {
        if (carried is null) return null;
        for (var i = 0; i < carried.Count; i++)
            if (carried[i].MemberIndex == memberIndex) return carried[i];
        return null;
    }

    /// <summary>
    /// 冻结战外 Buff 层数合计：会话成员开打定格视图（buffId → 层数）的求值体，
    /// 与 <see cref="ActionExecutionRules.SumActiveBuffStacks"/> 同语义（null 词条合计全部层数）。
    /// </summary>
    internal static double SumFrozenBuffStacks(IReadOnlyDictionary<string, double>? frozen, string? buffId)
    {
        if (frozen is null) return 0;
        if (buffId is not null) return frozen.TryGetValue(buffId, out var stacks) ? stacks : 0;
        var total = 0d;
        foreach (var pair in frozen) total += pair.Value;
        return total;
    }

    /// <summary>
    /// 搜寻相位盟友镜像初始构建：非本机成员以开打定格气血/灵力 + 冻结跨战技能状态入场；
    /// 本机成员由 PlayerState/资源池镜像承载不进列表；搜索相位不推进出手与复活时钟。
    /// </summary>
    private static IReadOnlyList<ExpeditionAllyState> BuildAllyCarry(ActionParticipationSpec spec, int selfIndex)
    {
        var allies = new List<ExpeditionAllyState>(spec.Members.Count - 1);
        for (var i = 0; i < spec.Members.Count; i++)
        {
            if (i == selfIndex) continue;
            var member = spec.Members[i];
            allies.Add(new ExpeditionAllyState(i,
                new CombatantSnapshot(member.InitialHitpoints, member.InitialManapoints, 0, 0, 0),
                CombatSkills: member.CombatSkills));
        }
        return allies;
    }

    /// <summary>
    /// 波次清空时的盟友携带导出：存活成员带出完整战斗快照 + 跨战技能态（折算回冷却轴）；
    /// 倒地成员按原地复活口径满状态起身（气血灵力全满、技能复位），与本机成员的复活同规。
    /// reviveDowned=false 用于团灭拆场：保留倒地镜像供复活读条相位展示。
    /// </summary>
    private static IReadOnlyList<ExpeditionAllyState> CarryAllies(CombatEncounterState battle,
        ActionParticipationSpec? spec, bool reviveDowned = true)
    {
        var allies = battle.PlayerAllies;
        var list = new List<ExpeditionAllyState>(allies.Count - 1);
        for (var i = 0; i < allies.Count; i++)
        {
            if (i == battle.SelfIndex) continue;
            var ally = allies[i];
            if (!ally.IsAlive && reviveDowned && spec is not null)
            {
                var member = spec.Members[i];
                list.Add(new ExpeditionAllyState(i,
                    new CombatantSnapshot(member.Stats.MaxHitpoints, member.Stats.MaxManapoints, 0, 0, 0),
                    CombatSkills: PlayerCombatSkillState.Empty));
                continue;
            }
            list.Add(new ExpeditionAllyState(i,
                CombatEncounterState.Snapshot(ally),
                PendingActionText(ally), PendingActionDurationOf(ally),
                PlayerCombatSkillState.FromCombatant(ally, battle.ElapsedSeconds)));
        }
        return list;
    }

    /// <summary>复活读条完成时的盟友镜像重置：全员满状态（与本机成员复活满血同规）。</summary>
    private static IReadOnlyList<ExpeditionAllyState> ResetAllyCarry(ActionParticipationSpec spec, int selfIndex)
    {
        var allies = new List<ExpeditionAllyState>(spec.Members.Count - 1);
        for (var i = 0; i < spec.Members.Count; i++)
        {
            if (i == selfIndex) continue;
            var member = spec.Members[i];
            allies.Add(new ExpeditionAllyState(i,
                new CombatantSnapshot(member.Stats.MaxHitpoints, member.Stats.MaxManapoints, 0, 0, 0),
                CombatSkills: PlayerCombatSkillState.Empty));
        }
        return allies;
    }

    /// <summary>吟唱技能 ID 展示文本（未吟唱为 null）。</summary>
    private static string? PendingActionText(CombatantState unit)
        => unit.PendingSkillIndex >= 0 && unit.PendingSkillIndex < unit.Skills.Count
            ? DeterministicCombatEngine.AbilityText(unit.Skills[unit.PendingSkillIndex].Ability, "id")
            : null;

    /// <summary>当前动作展示时长：吟唱取技能读条，普攻取攻速结算间隔（与敌人快照导出同一公式）。</summary>
    private static double PendingActionDurationOf(CombatantState unit)
        => unit.PendingSkillIndex >= 0 && unit.PendingSkillIndex < unit.Skills.Count
            ? DeterministicCombatEngine.AbilityNumber(unit.Skills[unit.PendingSkillIndex].Ability, "castMilliseconds") / 1000d
                * PercentValue.Multiplier(unit.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear)
            : unit.Stats.AttackIntervalSeconds
                * PercentValue.Multiplier(unit.Stats.AttackSpeedPoints, PercentCurveMode.NonLinear)
                * PercentValue.Multiplier(unit.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
}
