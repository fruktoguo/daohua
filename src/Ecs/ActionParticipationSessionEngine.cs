using DaoHua.Ecs.Systems;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;

namespace DaoHua.Ecs;

/// <summary>
/// 队伍会话唯一计算容器：全部成员共享一次战斗推进，结算阶段仅重标已有结果。
/// 宿主负责会话串行所有权及检查点/inbox 原子提交，本类型不访问真实成员或存储。
/// </summary>
public sealed class ActionParticipationSessionEngine
{
    private readonly ActionParticipationSpec _spec;
    private readonly GameDataCatalog _catalog;
    private readonly PlayerAggregate[] _members;
    private readonly SettlementCollector[] _collectors;
    private CombatEncounterState? _battle;
    private DateTimeOffset _advancedAt;
    private long _sequence;
    private double? _endsAt;
    private PlayerAggregate Canonical => _members[0];
    private QueuedAction Action => Canonical.Actions[0];

    /// <summary>实际战斗推进调用次数；用于诊断同场战斗是否被重复计算。</summary>
    public long CombatAdvanceCount { get; private set; }
    /// <summary>唯一会话的下一可见自然边界；读取不会推进或创建成员战斗副本。</summary>
    public DateTimeOffset? NextVisibleBoundaryUtc
    {
        get
        {
            if (_endsAt is not null) return null;
            if (_battle is not null)
            {
                var next = CombatEventAdvanceSystem.NextBoundarySeconds(_battle, double.MaxValue);
                return _advancedAt.AddSeconds(Math.Max(0, next - _battle.ElapsedSeconds));
            }
            var expedition = Action.Expedition!;
            var phaseRemaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
            var untilRegen = 5d;
            foreach (var member in _members) untilRegen = Math.Min(untilRegen, 5 - member.Resources.RegenCarrySeconds);
            var seconds = phaseRemaining > 1e-9 ? Math.Min(phaseRemaining, untilRegen) : untilRegen;
            return _advancedAt.AddSeconds(Math.Max(0, seconds));
        }
    }

    private ActionParticipationSessionEngine(ActionParticipationSpec spec, GameDataCatalog catalog)
    {
        if (!spec.IsValid) throw new DomainException(DomainErrorCode.InvalidArgument, "队伍会话规格无效。");
        _spec = spec;
        _catalog = catalog;
        _advancedAt = FromUnix(spec.StartedAtUnixSeconds);
        _members = new PlayerAggregate[spec.Members.Count];
        _collectors = new SettlementCollector[spec.Members.Count];
        for (var i = 0; i < _members.Length; i++)
        {
            var frozen = spec.Members[i];
            var id = new PlayerId(Guid.Parse(frozen.PlayerId));
            // 容器只持有会话冻结输入和结算工作态；此处账号标识不承担任何认证或持久化职责。
            var member = PlayerAggregate.Create(id, new AccountId(id.Value), frozen.DisplayName,
                _advancedAt, catalog.Fingerprint);
            member.Progression.RealmLevel = frozen.RealmLevel;
            member.World.NodeId = spec.NodeId;
            member.Resources.MaxHitpoints = frozen.Stats.MaxHitpoints;
            member.Resources.MaxManapoints = frozen.Stats.MaxManapoints;
            member.Resources.Hitpoints = frozen.InitialHitpoints;
            member.Resources.Manapoints = frozen.InitialManapoints;
            member.Resources.RegenCarrySeconds = frozen.RegenCarrySeconds;
            member.Combat.Skills = frozen.CombatSkills ?? PlayerCombatSkillState.Empty;
            member.Loadout.AbilitySlots.Clear();
            foreach (var skill in frozen.Skills)
            {
                var abilityId = DeterministicCombatEngine.AbilityText(skill.Ability, "id");
                if (abilityId is null) continue;
                member.Loadout.AbilitySlots.Add(new(abilityId, skill.Level));
                member.Loadout.LearnedAbilities.Add(new AbilityId(abilityId));
            }
            member.StartActionParticipation(spec, _advancedAt, catalog.Actions);
            member.EnsureActionReport(member.Actions[0].Id, spec.ActionId, catalog.Actions.Get(spec.ActionId).Name,
                _advancedAt);
            var collector = new SettlementCollector();
            member.SetParticipationStatsSink(collector);
            _members[i] = member;
            _collectors[i] = collector;
        }
        var initialization = new ActionMutationBatch();
        CombatActionRules.InitializeCombatAction(Canonical, Action, catalog.Actions.Get(spec.ActionId), catalog,
            _advancedAt, initialization);
        foreach (var collector in _collectors) collector.Add(initialization, Canonical.PlayerId, includeAssets: false);
        SynchronizeViews();
    }

    /// <summary>以开打冻结输入创建会话，不推进战斗时钟。</summary>
    public static ActionParticipationSessionEngine Create(ActionParticipationSpec spec, GameDataCatalog catalog)
        => new(spec, catalog);

    /// <summary>从唯一检查点恢复；不重演已提交战斗，也不重复生成开场事件。</summary>
    public static ActionParticipationSessionEngine Restore(ActionParticipationSessionDocument document,
        GameDataCatalog catalog)
    {
        ValidateCheckpoint(document);
        if (!string.Equals(document.GameDataFingerprint, catalog.Fingerprint, StringComparison.Ordinal))
            throw new DomainException(DomainErrorCode.InvariantViolation, "队伍会话配表指纹不一致，必须按最后确定检查点收尾。");
        var engine = new ActionParticipationSessionEngine(document.Spec, catalog)
        {
            _advancedAt = FromUnix(document.AdvancedAtUnixSeconds),
            _sequence = document.Sequence,
            _endsAt = document.EndsAtUnixSeconds
        };
        engine.Action.CompletedRepeats = document.CompletedRepeats;
        engine.Action.Expedition = document.Expedition!;
        engine.Action.ProgressSeconds = document.Expedition!.Phase == CombatExpeditionPhase.Combat
            ? 0 : document.Expedition.PhaseElapsedSeconds;
        engine.Canonical.Resources.Hitpoints = document.Expedition!.PlayerHitpoints;
        engine.Canonical.Resources.Manapoints = document.Expedition!.PlayerManapoints;
        for (var i = 0; i < engine._members.Length; i++)
            engine._members[i].Resources.RegenCarrySeconds = document.MemberRegenCarrySeconds[i];
        engine.Canonical.Combat.Skills = document.Skills;
        var stats = engine.Canonical.Duel.ReportFor(engine.Action.Id)!.CombatStats;
        foreach (var row in document.CombatStats) stats.Import(row);
        if (document.EndsAtUnixSeconds is null && document.Expedition.Phase == CombatExpeditionPhase.Combat)
            engine._battle = CombatActionRules.RestoreCombatBattle(engine.Canonical, engine.Action,
                catalog.CombatEncounters[document.Expedition.EncounterId], catalog);
        if (document.Sequence > 0)
            foreach (var collector in engine._collectors) collector.Reset();
        engine.SynchronizeViews();
        return engine;
    }

    /// <summary>只用旧检查点安全收尾；不读取当前配表、不初始化或重演战斗。</summary>
    public static ActionParticipationSessionFinalization FinalizeIncompatibleCheckpoint(
        ActionParticipationSessionDocument document)
    {
        ValidateCheckpoint(document);
        if (document.EndsAtUnixSeconds is not null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "已结束会话不能再次安全收尾。");
        var original = document.Expedition!;
        var expedition = original with
        {
            PlayerHitpoints = original.PlayerHitpoints <= 0 ? 1 : original.PlayerHitpoints,
            PlayerRespawnAtSeconds = null,
            PlayerState = original.PlayerState is { } self
                ? self with { Hitpoints = self.Hitpoints <= 0 ? 1 : self.Hitpoints, RespawnAtSeconds = null } : null,
            Allies = original.Allies?.Select(ally => ally with
            {
                State = ally.State with
                { Hitpoints = ally.State.Hitpoints <= 0 ? 1 : ally.State.Hitpoints, RespawnAtSeconds = null }
            }).ToArray()
        };
        var ended = document with { Sequence = checked(document.Sequence + 1), Expedition = expedition,
            EndsAtUnixSeconds = document.AdvancedAtUnixSeconds };
        var at = FromUnix(document.AdvancedAtUnixSeconds);
        var settlements = new ActionParticipationMemberSettlement[document.Spec.Members.Count];
        for (var i = 0; i < settlements.Length; i++)
        {
            var view = ProjectExpedition(expedition, i, document.Skills);
            var skills = i == 0 ? document.Skills
                : expedition.Allies!.First(ally => ally.MemberIndex == i).CombatSkills ?? PlayerCombatSkillState.Empty;
            settlements[i] = new(document.Spec.SessionId, ended.Sequence,
                new PlayerId(Guid.Parse(document.Spec.Members[i].PlayerId)), at, at,
                view.PlayerHitpoints, view.PlayerManapoints, document.MemberRegenCarrySeconds[i], skills, view,
                view.Phase == CombatExpeditionPhase.Combat ? 0 : view.PhaseElapsedSeconds,
                document.CompletedRepeats, [], [], [], ProjectStats(document.CombatStats, i),
                EndsAtUnixSeconds: document.AdvancedAtUnixSeconds);
        }
        return new(ended, settlements);
    }

    private static void ValidateCheckpoint(ActionParticipationSessionDocument document)
    {
        if (!document.Spec.IsValid || string.IsNullOrWhiteSpace(document.GameDataFingerprint)
            || !double.IsFinite(document.AdvancedAtUnixSeconds)
            || document.AdvancedAtUnixSeconds < document.Spec.StartedAtUnixSeconds || document.Sequence < 0
            || document.Expedition is not { IsValid: true, SelfIndex: 0 } || !document.Skills.IsValid
            || document.MemberRegenCarrySeconds is null || document.MemberRegenCarrySeconds.Count != document.Spec.Members.Count
            || document.MemberRegenCarrySeconds.Any(value => !double.IsFinite(value) || value < 0 || value >= 5)
            || !double.IsFinite(document.CompletedRepeats) || document.CompletedRepeats < 0
            || document.CombatStats is null
            || (document.Expedition.Allies?.Count ?? 0) != document.Spec.Members.Count - 1
            || (document.Expedition.Allies is { } allies && allies.Any(ally =>
                ally.MemberIndex >= document.Spec.Members.Count
                || (ally.CombatSkills is not null && !ally.CombatSkills.IsValid)))
            || (document.EndsAtUnixSeconds is { } end && (!double.IsFinite(end) || end != document.AdvancedAtUnixSeconds)))
            throw new DomainException(DomainErrorCode.InvalidArgument, "队伍会话检查点无效。");
    }

    /// <summary>将唯一会话推进到墙钟目标，返回本次各成员的个人增量投影。</summary>
    public IReadOnlyList<ActionParticipationMemberSettlement> AdvanceTo(DateTimeOffset target)
        => Advance(target, ending: false);

    /// <summary>推进并产生散场末批；即使墙钟未变化也投递终态，倒地成员以 1 点气血起身。</summary>
    public IReadOnlyList<ActionParticipationMemberSettlement> EndAt(DateTimeOffset at)
        => Advance(at, ending: true);

    private IReadOnlyList<ActionParticipationMemberSettlement> Advance(DateTimeOffset target, bool ending)
    {
        target = FromUnix(ToUnix(target.ToUniversalTime()));
        if (target < _advancedAt || _endsAt is not null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "会话目标时间倒退或已经结束。");
        // 会话层按 5 秒窗口拆分，战斗边界消耗值必须与外层时间游标严格相等。
        var from = _advancedAt;
        var cappedTarget = target > from.AddHours(48) ? from.AddHours(48) : target;
        var cursor = from;
        while (cursor < cappedTarget)
        {
            var untilRegen = 5d;
            foreach (var member in _members) untilRegen = Math.Min(untilRegen, 5 - member.Resources.RegenCarrySeconds);
            // DateTimeOffset 只有 100ns 精度；向最近 tick 贴齐，不能截断后残留一个永远到不了的回复边界。
            var ticks = Math.Min((cappedTarget - cursor).Ticks,
                Math.Max(1L, checked((long)Math.Round(untilRegen * TimeSpan.TicksPerSecond))));
            var windowEnd = cursor.AddTicks(ticks);
            var remaining = (windowEnd - cursor).TotalSeconds;
            var transitions = 0;
            while (remaining > 1e-9)
            {
                if (++transitions > SimulationEngine.MaxTransitionsPerAdvance)
                    throw new DomainException(DomainErrorCode.InvariantViolation, "队伍会话超过单窗口转换上限。");
                if (Action.Expedition!.Phase == CombatExpeditionPhase.Combat)
                    remaining = AdvanceBattle(cursor, windowEnd, remaining);
                else
                    remaining = AdvancePreparation(cursor, windowEnd, remaining);
            }
            cursor = windowEnd;
            _advancedAt = cursor;
            FlushBattle();
            SynchronizeViews();
        }
        _advancedAt = target;
        if (target > cappedTarget)
            foreach (var collector in _collectors)
                collector.Events.Add(new(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。"));
        FlushBattle();
        SynchronizeViews();
        if (ending)
        {
            _endsAt = ToUnix(target);
            foreach (var member in _members)
                if (member.Resources.Hitpoints <= 0) member.Resources.Hitpoints = 1;
            if (_battle is not null)
            {
                foreach (var unit in _battle.PlayerAllies)
                    if (!unit.IsAlive)
                    {
                        unit.Hitpoints = 1;
                        unit.RespawnAtSeconds = double.PositiveInfinity;
                    }
                FlushBattle();
            }
            else
            {
                var expedition = Action.Expedition!;
                Action.Expedition = expedition with
                {
                    PlayerHitpoints = Canonical.Resources.Hitpoints,
                    Allies = expedition.Allies?.Select(ally => ally.State.Hitpoints > 0 ? ally : ally with
                    { State = ally.State with { Hitpoints = 1, RespawnAtSeconds = null } }).ToArray()
                };
            }
            SynchronizeViews();
        }
        _sequence = checked(_sequence + 1);
        var canonicalStats = StatsDocuments(Canonical.Duel.ReportFor(Action.Id)!.CombatStats);
        var results = new ActionParticipationMemberSettlement[_members.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var member = _members[i];
            var collector = _collectors[i];
            var action = member.Actions[0];
            var expedition = action.Expedition!;
            if (ending)
                expedition = expedition with { PlayerHitpoints = member.Resources.Hitpoints };
            results[i] = new(_spec.SessionId, _sequence, member.PlayerId, from, target,
                member.Resources.Hitpoints, member.Resources.Manapoints, member.Resources.RegenCarrySeconds, member.Combat.Skills,
                expedition, action.ProgressSeconds, action.CompletedRepeats, collector.Stats.ToArray(),
                collector.Assets.Select(pair => new AssetDelta(member.PlayerId, pair.Key, pair.Value, "simulation")).ToArray(),
                collector.Events.View.ToArray(), ProjectStats(canonicalStats, i), collector.Events.Dropped, _endsAt);
            collector.Reset();
        }
        return results;
    }

    private double AdvancePreparation(DateTimeOffset from, DateTimeOffset target, double remaining)
    {
        var mutation = new ActionMutationBatch();
        // 搜寻/复活时间必须在入场前折算技能与资源；不能按窗口终态把整段误算为战斗或脱战。
        var expedition = Action.Expedition!;
        var preparationSeconds = Math.Min(remaining,
            Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds));
        ActionExecutionRules.AdvanceIdleSkillResources(Canonical, preparationSeconds);
        AdvancePreparationResources(preparationSeconds);
        var step = CombatActionRules.AdvanceCombatPreparation(Canonical, Action,
            _catalog.Actions.Get(_spec.ActionId), _catalog, null, from, target, remaining, mutation);
        for (var i = 0; i < _collectors.Length; i++)
        {
            if (i == 0) _collectors[i].Add(mutation, _members[i].PlayerId);
            else
            {
                foreach (var entry in mutation.Events)
                    if (entry.Code != "combat.lost") _collectors[i].Events.Add(entry);
                _collectors[i].Events.Fold(mutation.DroppedEventCount);
                if (step.Battle is { } entered && !entered.PlayerAllies[i].IsAlive)
                {
                    var names = string.Join("、", entered.Enemies.Select(enemy => _catalog.CombatMonsters[enemy.DefinitionId!].Name));
                    _collectors[i].Events.Add(new(from.AddSeconds((target - from).TotalSeconds - step.RemainingSeconds),
                        "combat.lost", $"你以倒地状态随队迎战：{names}。", _spec.ActionId));
                }
            }
            if (i > 0 && step.Battle is not null)
                _members[i].RecordStats(_members[i].Actions[0].Id,
                    ToUnix(from.AddSeconds((target - from).TotalSeconds - step.RemainingSeconds)),
                    new PlayerStatsDelta { Encounters = 1 });
        }
        _battle = step.Battle;
        if (_battle is { } enteredBattle)
        {
            Canonical.Resources.Hitpoints = enteredBattle.Player.Hitpoints;
            Canonical.Resources.Manapoints = enteredBattle.Player.Manapoints;
            Canonical.Combat.Skills = PlayerCombatSkillState.FromCombatant(enteredBattle.Player, enteredBattle.ElapsedSeconds);
        }
        SynchronizeViews();
        return step.RemainingSeconds;
    }

    private void AdvancePreparationResources(double seconds)
    {
        var expedition = Action.Expedition!;
        var allies = expedition.Allies?.ToArray() ?? [];
        for (var i = 0; i < _members.Length; i++)
        {
            var member = _members[i];
            var hpBefore = member.Resources.Hitpoints;
            ActionExecutionRules.CompleteResourceRegeneration(member, _catalog, seconds, regenerateAllies: false);
            if (i == 0) continue;
            if (member.Resources.Hitpoints > hpBefore)
                Canonical.Duel.ReportFor(Action.Id)!.CombatStats.RecordHealing($"ally:{i}", member.DisplayName,
                    autoRegen: true, member.Resources.Hitpoints - hpBefore);
            var index = Array.FindIndex(allies, ally => ally.MemberIndex == i);
            allies[index] = allies[index] with { State = allies[index].State with
            { Hitpoints = member.Resources.Hitpoints, Manapoints = member.Resources.Manapoints } };
        }
        if (allies.Length > 0) Action.Expedition = Action.Expedition! with { Allies = allies };
    }

    private double AdvanceBattle(DateTimeOffset from, DateTimeOffset target, double remaining)
    {
        var definition = _catalog.Actions.Get(_spec.ActionId);
        var encounter = _catalog.CombatEncounters[Action.Expedition!.EncounterId];
        _battle ??= CombatActionRules.RestoreCombatBattle(Canonical, Action, encounter, _catalog);
        var before = CombatActionRules.CaptureCombatState(_battle, Action.Expedition!);
        var result = CombatEventAdvanceSystem.AdvanceTo(_battle, _battle.ElapsedSeconds + remaining);
        CombatAdvanceCount++;
        // 仅结算阶段创建成员视角；这些状态从不进入 AdvanceTo，不能再次消费随机流或触发器。
        for (var i = 1; i < _members.Length; i++)
        {
            var member = _members[i];
            var action = member.Actions[0];
            action.Expedition = ProjectExpedition(before, i);
            var view = ProjectBattle(_battle, i);
            var mutation = new ActionMutationBatch();
            CombatActionRules.CompleteCombatAdvance(member, action, definition, encounter, _catalog,
                view, ProjectResult(result, i), from, target, remaining, mutation);
            _collectors[i].Add(mutation, member.PlayerId);
        }
        var canonicalMutation = new ActionMutationBatch();
        var next = CombatActionRules.CompleteCombatAdvance(Canonical, Action, definition, encounter, _catalog,
            _battle, result, from, target, remaining, canonicalMutation);
        _collectors[0].Add(canonicalMutation, Canonical.PlayerId);
        foreach (var member in _members)
        {
            member.Resources.RegenCarrySeconds += remaining - next;
            if (member.Resources.RegenCarrySeconds >= 5 - 1d / TimeSpan.TicksPerSecond)
                member.Resources.RegenCarrySeconds = 0;
        }
        if (_battle.Outcome != CombatOutcome.Running) _battle = null;
        FlushBattle();
        SynchronizeViews();
        return next;
    }

    /// <summary>导出唯一可恢复状态；全部成员的血蓝/技能镜像位于同一远征态中。</summary>
    public ActionParticipationSessionDocument Export()
    {
        FlushBattle();
        return new(_spec, _catalog.Fingerprint, ToUnix(_advancedAt), _sequence, Action.Expedition,
            Canonical.Combat.Skills, _members.Select(member => member.Resources.RegenCarrySeconds).ToArray(), Action.CompletedRepeats,
            StatsDocuments(Canonical.Duel.ReportFor(Action.Id)!.CombatStats), _endsAt);
    }

    /// <summary>读取成员展示投影，不推进时钟或产生结算。</summary>
    public CombatExpeditionState ProjectFor(PlayerId playerId)
    {
        var index = _spec.SelfIndexOf(playerId);
        if (index < 0) throw new DomainException(DomainErrorCode.NotFound, "玩家不在会话中。");
        FlushBattle();
        return ProjectExpedition(Action.Expedition!, index);
    }

    /// <summary>从唯一累计账本读取成员视角，供宿主在消费紧凑 inbox 时装配当前展示。</summary>
    public IReadOnlyList<CombatUnitStatDocument> ProjectCombatStatsFor(PlayerId playerId)
    {
        var index = _spec.SelfIndexOf(playerId);
        if (index < 0) throw new DomainException(DomainErrorCode.NotFound, "玩家不在会话中。");
        return ProjectStats(StatsDocuments(Canonical.Duel.ReportFor(Action.Id)!.CombatStats), index);
    }

    /// <summary>从检查点读取成员展示，不依赖当前配表，允许旧指纹终态装配收件箱。</summary>
    public static CombatExpeditionState ProjectFor(ActionParticipationSessionDocument document, PlayerId playerId)
    {
        ValidateCheckpoint(document);
        var index = document.Spec.SelfIndexOf(playerId);
        if (index < 0) throw new DomainException(DomainErrorCode.NotFound, "玩家不在会话中。");
        return ProjectExpedition(document.Expedition!, index, document.Skills);
    }

    /// <summary>从检查点累计账本读取成员视角，不恢复或推进战斗。</summary>
    public static IReadOnlyList<CombatUnitStatDocument> ProjectCombatStatsFor(
        ActionParticipationSessionDocument document, PlayerId playerId)
    {
        ValidateCheckpoint(document);
        var index = document.Spec.SelfIndexOf(playerId);
        if (index < 0) throw new DomainException(DomainErrorCode.NotFound, "玩家不在会话中。");
        return ProjectStats(document.CombatStats, index);
    }

    private void FlushBattle()
    {
        if (_battle is not null) Action.Expedition = CombatActionRules.CaptureCombatState(_battle, Action.Expedition!);
    }

    private void SynchronizeViews()
    {
        var canonical = Action.Expedition!;
        for (var i = 0; i < _members.Length; i++)
        {
            var member = _members[i];
            member.LastSimulatedAt = _advancedAt;
            var action = member.Actions[0];
            var view = ProjectExpedition(canonical, i);
            action.Expedition = view;
            action.ProgressSeconds = Action.ProgressSeconds;
            action.CompletedRepeats = Action.CompletedRepeats;
            if (i == 0) continue;
            member.Resources.Hitpoints = view.PlayerHitpoints;
            member.Resources.Manapoints = view.PlayerManapoints;
            member.Combat.Skills = canonical.Allies!.First(ally => ally.MemberIndex == i).CombatSkills
                ?? _spec.Members[i].CombatSkills ?? PlayerCombatSkillState.Empty;
        }
    }

    private CombatExpeditionState ProjectExpedition(CombatExpeditionState canonical, int memberIndex)
        => ProjectExpedition(canonical, memberIndex, Canonical.Combat.Skills);

    private static CombatExpeditionState ProjectExpedition(CombatExpeditionState canonical, int memberIndex,
        PlayerCombatSkillState canonicalSkills)
    {
        if (memberIndex == 0) return canonical;
        var self = canonical.Allies!.First(ally => ally.MemberIndex == memberIndex);
        var previousSelf = canonical.PlayerState ?? new CombatantSnapshot(canonical.PlayerHitpoints,
            canonical.PlayerManapoints, canonical.PlayerNextAttackAtSeconds, canonical.PlayerAttackCount,
            canonical.PlayerThreat);
        var others = canonical.Allies!.Where(ally => ally.MemberIndex != memberIndex).ToList();
        others.Add(new(0, previousSelf, CombatSkills: canonicalSkills));
        others.Sort((left, right) => left.MemberIndex.CompareTo(right.MemberIndex));
        return canonical with
        {
            SelfIndex = memberIndex,
            PlayerHitpoints = self.State.Hitpoints,
            PlayerManapoints = self.State.Manapoints,
            PlayerNextAttackAtSeconds = self.State.NextAttackAtSeconds,
            PlayerAttackCount = self.State.AttackCount,
            PlayerThreat = self.State.Threat,
            PlayerState = canonical.Phase == CombatExpeditionPhase.Combat ? self.State : null,
            PlayerRespawnAtSeconds = canonical.Phase == CombatExpeditionPhase.Combat ? self.State.RespawnAtSeconds : null,
            Allies = others
        };
    }

    private CombatEncounterState ProjectBattle(CombatEncounterState canonical, int memberIndex)
    {
        var encounter = CombatEncounterState.RestoreAllied(canonical.EncounterId, canonical.Seed,
            _spec.Members.Select(member => new CombatantTemplate(member.Stats, member.Skills)).ToArray(),
            canonical.PlayerAllies.Select(CombatEncounterState.Snapshot).ToArray(), memberIndex,
            canonical.Enemies.Select(enemy => new CombatantTemplate(enemy.Stats, enemy.Skills)).ToArray(),
            canonical.Enemies.Select(CombatEncounterState.Snapshot).ToArray(), canonical.ElapsedSeconds,
            canonical.Outcome, canonical.Sequence, canonical.PlayerRespawnSeconds);
        for (var i = 0; i < encounter.Enemies.Count; i++)
            encounter.Enemies[i].DefinitionId = canonical.Enemies[i].DefinitionId;
        return encounter;
    }

    private static CombatAdvanceResult ProjectResult(CombatAdvanceResult canonical, int memberIndex)
    {
        var deaths = (canonical.AllyDeathMarks ?? []).ToList();
        deaths.AddRange((canonical.PlayerDeathTimestamps ?? []).Select(at => (0, at)));
        var respawns = (canonical.AllyRespawnMarks ?? []).ToList();
        respawns.AddRange((canonical.PlayerRespawnTimestamps ?? []).Select(at => (0, at)));
        return canonical with
        {
            Events = canonical.Events.Select(frame => frame with
            {
                AttackerId = ProjectUnitKey(frame.AttackerId, memberIndex),
                TargetId = ProjectUnitKey(frame.TargetId, memberIndex)
            }).ToArray(),
            PlayerDeathTimestamps = deaths.Where(mark => mark.MemberIndex == memberIndex).Select(mark => mark.AtSeconds).ToArray(),
            PlayerRespawnTimestamps = respawns.Where(mark => mark.MemberIndex == memberIndex).Select(mark => mark.AtSeconds).ToArray(),
            AllyDeathMarks = deaths.Where(mark => mark.MemberIndex != memberIndex).OrderBy(mark => mark.AtSeconds).ToArray(),
            AllyRespawnMarks = respawns.Where(mark => mark.MemberIndex != memberIndex).OrderBy(mark => mark.AtSeconds).ToArray()
        };
    }

    private static string ProjectUnitKey(string key, int memberIndex)
    {
        var index = key == "player" ? 0
            : key.StartsWith("ally:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(5), out var ally) ? ally : -1;
        return index < 0 ? key : index == memberIndex ? "player" : $"ally:{index}";
    }

    private static CombatUnitStatDocument[] ProjectStats(IReadOnlyList<CombatUnitStatDocument> stats, int memberIndex)
        => stats.Select(row =>
        {
            var key = ProjectUnitKey(row.UnitKey, memberIndex);
            return row with { UnitKey = key, Kind = key == "player" ? CombatStatUnitKind.Self
                : key.StartsWith("ally:", StringComparison.Ordinal) ? CombatStatUnitKind.Ally : CombatStatUnitKind.Monster };
        }).ToArray();

    private static CombatUnitStatDocument[] StatsDocuments(CombatUnitStatsBook book)
        => book.Entries.Select(row => new CombatUnitStatDocument(row.UnitKey, row.Kind, row.DisplayName,
            row.DamageDealtPhysical, row.DamageDealtSpell, row.DamageTakenPhysical, row.DamageTakenSpell,
            row.DamageMitigatedPhysical, row.DamageMitigatedSpell, row.HealingRegen, row.HealingOther)).ToArray();

    private static DateTimeOffset FromUnix(double seconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(seconds * 1000d)));
    private static double ToUnix(DateTimeOffset at) => at.ToUnixTimeMilliseconds() / 1000d;

    private sealed class SettlementCollector : IPlayerStatsSink
    {
        public readonly List<ActionParticipationTimedStats> Stats = [];
        public readonly Dictionary<AssetKey, double> Assets = [];
        public SimulationEngine.SimulationEventTail Events = new();

        public void Record(IPlayerResourceContext player, Guid? actionInstanceId, double atUnixSeconds,
            in PlayerStatsDelta delta)
        {
            // 容器只投递原始份额：个人修为瓶颈、底蕴兑换和升级记录须在成员单写者内结算。
            Stats.Add(new(atUnixSeconds, delta with { Experience = 0, Foundation = 0, LevelUp = null }));
            ActionReportBook.RecordActionStats(player, actionInstanceId, atUnixSeconds, delta);
        }

        public void Add(ActionMutationBatch mutation, PlayerId playerId, bool includeAssets = true)
        {
            Events.Fold(mutation.DroppedEventCount);
            foreach (var entry in mutation.Events) Events.Add(entry);
            if (!includeAssets) return;
            foreach (var delta in mutation.ToAssetDeltas(playerId, sortByItem: false))
                Assets[delta.Asset] = Assets.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
        }

        public void Reset()
        {
            Stats.Clear();
            Assets.Clear();
            Events = new();
        }
    }
}
