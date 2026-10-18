using System.Text.Json;
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
    private SessionConsumableSlot[]? _consumables;
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
            // 封魔阵类怪物削弱经 spec 冻结注入会话成员建筑投影：后续波次入场/恢复/求值
            // 统一经 EffectiveNodeBuildingEffects 读取，各成员推演逐位一致。
            member.World.NodeBuildingEffects =
                new Domain.World.BuildingEffectSet(MonsterWeakenRatio: spec.MonsterWeakenRatio);
            member.World.NodeBuildingNodeId = spec.NodeId;
            member.Resources.MaxHitpoints = frozen.Stats.MaxHitpoints;
            member.Resources.MaxManapoints = frozen.Stats.MaxManapoints;
            member.Resources.Hitpoints = frozen.InitialHitpoints;
            member.Resources.Manapoints = frozen.InitialManapoints;
            member.Resources.RegenCarrySeconds = frozen.RegenCarrySeconds;
            member.Combat.Skills = frozen.CombatSkills ?? PlayerCombatSkillState.Empty;
            member.Combat.Buffs = frozen.CombatBuffs ?? PlayerCombatBuffState.Empty;
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
        // 消耗品托管台账初始值 = 定格 spec：全部槽行载入（零配额槽同样保留冷却钟——
        // 同物品共享池消费时，无配额槽行的冷却与触发判定仍要按槽推进）；
        // 持续槽已充能剩余折算为墙钟到期时刻。
        List<SessionConsumableSlot>? ledger = null;
        for (var i = 0; i < spec.Members.Count; i++)
            if (spec.Members[i].ConsumableSlots is { Count: > 0 } slots)
                foreach (var slot in slots)
                    (ledger ??= []).Add(new SessionConsumableSlot(i, slot.ItemId, slot.Group, slot.SlotIndex,
                        slot.EscrowQuantity, slot.CooldownUntilUnixSeconds,
                        slot.SustainedRemainingSeconds > 0
                            ? spec.StartedAtUnixSeconds + slot.SustainedRemainingSeconds : 0));
        _consumables = ledger?.ToArray();
        // 开打托管一次性入账通知：成员钱包全额实扣后日志需可核对「物品去了哪」——
        // 托管期间不可售用，散场末批退回余量。
        if (ledger is { } rows)
            for (var i = 0; i < spec.Members.Count; i++)
            {
                var held = rows.Where(row => row.MemberIndex == i && row.RemainingQuantity > 0)
                    .GroupBy(row => row.ItemId)
                    .Select(group => (_catalog.Items.TryGetValue(new ItemId(group.Key), out var heldItem)
                            ? heldItem.Name : group.Key, group.Sum(row => row.RemainingQuantity)))
                    .ToArray();
                if (held.Length > 0)
                    _collectors[i].Events.Add(new(_advancedAt, "auto-use.escrowed",
                        $"组队托管锁定：{string.Join("、", held.Select(entry => $"{entry.Item1} ×{entry.Item2:0.##}"))}；战斗期间不可售用，散场退回余量。", _spec.ActionId));
            }
        EvaluateConsumables(_advancedAt);
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
            _endsAt = document.EndsAtUnixSeconds,
            _consumables = document.ConsumableLedger?.ToArray()
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
        engine.Canonical.Combat.Buffs = document.Buffs ?? PlayerCombatBuffState.Empty;
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
            var view = ProjectExpedition(expedition, i, document.Skills, document.Buffs);
            var skills = i == 0 ? document.Skills
                : expedition.Allies!.First(ally => ally.MemberIndex == i).CombatSkills ?? PlayerCombatSkillState.Empty;
            var buffs = i == 0 ? document.Buffs
                : expedition.Allies!.First(ally => ally.MemberIndex == i).CombatBuffs ?? PlayerCombatBuffState.Empty;
            settlements[i] = new(document.Spec.SessionId, ended.Sequence,
                new PlayerId(Guid.Parse(document.Spec.Members[i].PlayerId)), at, at,
                view.PlayerHitpoints, view.PlayerManapoints, document.MemberRegenCarrySeconds[i], skills, buffs, view,
                view.Phase == CombatExpeditionPhase.Combat ? 0 : view.PhaseElapsedSeconds,
                document.CompletedRepeats, [], RefundConsumableEscrow(document, i), [],
                ProjectStats(document.CombatStats, i),
                EndsAtUnixSeconds: document.AdvancedAtUnixSeconds);
        }
        return new(ended, settlements);
    }

    /// <summary>末批结算返还的未耗托管余量：台账剩余按成员聚合为正向资产差量。</summary>
    private static AssetDelta[] RefundConsumableEscrow(ActionParticipationSessionDocument document, int memberIndex)
    {
        if (document.ConsumableLedger is not { } ledger) return [];
        Dictionary<AssetKey, double>? refunds = null;
        for (var i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (row.MemberIndex != memberIndex || row.RemainingQuantity <= 0) continue;
            var key = new AssetKey(new ItemId(row.ItemId));
            (refunds ??= [])[key] = refunds.GetValueOrDefault(key) + row.RemainingQuantity;
        }
        return refunds?.Select(pair => new AssetDelta(
            new PlayerId(Guid.Parse(document.Spec.Members[memberIndex].PlayerId)), pair.Key, pair.Value,
            "simulation")).ToArray() ?? [];
    }

    private static void ValidateCheckpoint(ActionParticipationSessionDocument document)
    {
        if (!document.Spec.IsValid || string.IsNullOrWhiteSpace(document.GameDataFingerprint)
            || !double.IsFinite(document.AdvancedAtUnixSeconds)
            || document.AdvancedAtUnixSeconds < document.Spec.StartedAtUnixSeconds || document.Sequence < 0
            || document.Expedition is not { IsValid: true, SelfIndex: 0 } || !document.Skills.IsValid
            || (document.Buffs is not null && !document.Buffs.IsValid)
            || (document.ConsumableLedger is { } ledger && (ledger.Any(row =>
                    !row.IsValid || row.MemberIndex >= document.Spec.Members.Count)
                || ledger.Select(row => (row.MemberIndex, row.Group, row.SlotIndex)).Distinct().Count() != ledger.Count))
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
        var cappedTarget = target > from.Add(PlayerLifecycleRules.OfflineIdleLimit)
            ? from.Add(PlayerLifecycleRules.OfflineIdleLimit) : target;
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
            // 消耗品托管按会话窗口判定（与单人 5 秒资源边界同格族）；战内判定用 canonical 单位。
            EvaluateConsumables(cursor);
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
            // 散场把未耗尽的托管余量作为末批结算资产差量退回成员钱包（开打已实扣）。
            if (_consumables is { } ledger)
                for (var i = 0; i < ledger.Length; i++)
                {
                    var row = ledger[i];
                    if (row.RemainingQuantity <= 0) continue;
                    var key = new AssetKey(new ItemId(row.ItemId));
                    var collector = _collectors[row.MemberIndex];
                    collector.Assets[key] = collector.Assets.GetValueOrDefault(key) + row.RemainingQuantity;
                    ledger[i] = row with { RemainingQuantity = 0 };
                }
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
                member.Combat.Buffs,
                expedition, action.ProgressSeconds, action.CompletedRepeats, collector.Stats.ToArray(),
                collector.Assets.Select(pair => new AssetDelta(member.PlayerId, pair.Key, pair.Value, "simulation")).ToArray(),
                collector.Events.View.ToArray(), ProjectStats(canonicalStats, i), collector.Events.Dropped, _endsAt,
                collector.StagedItems.Count > 0 ? collector.StagedItems.ToArray() : null);
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
                    if (entry.Code is not ("combat.lost" or "combat.downed")) _collectors[i].Events.Add(entry);
                _collectors[i].Events.Fold(mutation.DroppedEventCount);
                if (step.Battle is { } entered && !entered.PlayerAllies[i].IsAlive)
                {
                    var names = string.Join("、", entered.Enemies.Select(enemy => _catalog.CombatMonsters[enemy.DefinitionId!].Name));
                    _collectors[i].Events.Add(new(from.AddSeconds((target - from).TotalSeconds - step.RemainingSeconds),
                        "combat.downed", $"你以倒地状态随队迎战：{names}。", _spec.ActionId));
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
            Canonical.Combat.Buffs = PlayerCombatBuffState.FromCombatant(enteredBattle.Player, enteredBattle.ElapsedSeconds);
            // 波次入场判定托管消耗品（与单人入场充能同口径）：耗竭/冷却在台账上走墙钟。
            var entryAt = from.AddSeconds((target - from).TotalSeconds - step.RemainingSeconds);
            EvaluateConsumables(entryAt);
            OverlaySessionSustained(ToUnix(entryAt));
        }
        SynchronizeViews();
        return step.RemainingSeconds;
    }

    /// <summary>
    /// 消耗品托管判定：会话唯一副本按墙钟时钟逐槽推进（与单人 5 秒资源边界同格族）。
    /// 战斗相位以 canonical 战内单位为施放方求值，非战斗相位以冻结面板 + 成员资源池装配求值单位；
    /// 扣减的是开打定格托管配额而非成员钱包，实耗经「失去」账本与自动使用事件随结算下发。
    /// </summary>
    private void EvaluateConsumables(DateTimeOffset at)
    {
        if (_consumables is null) return;
        var now = ToUnix(at);
        for (var s = 0; s < _consumables.Length; s++)
        {
            var row = _consumables[s];
            var itemId = new ItemId(row.ItemId);
            if (!_catalog.Items.TryGetValue(itemId, out var item)
                || item.Consumable is not { } consumable || consumable.Durable
                || !consumable.UsableInBars.Contains(AutoUseBar.Combat)) continue;
            if (consumable is { IsContinuous: true, DurationSeconds: > 0 })
                EvaluateSustained(s, row, item, consumable, at, now);
            else EvaluateInstant(s, row, item, consumable, at, now);
        }
    }

    /// <summary>持续消耗品：耗竭按墙钟记账、冷却自耗竭时刻起算；再充能只在战斗相位判定并就地注入 Buff。</summary>
    private void EvaluateSustained(int index, SessionConsumableSlot row, ItemDefinition item,
        ConsumableDefinition consumable, DateTimeOffset at, double now)
    {
        if (row.SustainedExpiresAtUnixSeconds > 0 && row.SustainedExpiresAtUnixSeconds <= now)
        {
            _consumables![index] = row = row with { SustainedExpiresAtUnixSeconds = 0,
                CooldownUntilUnixSeconds = row.SustainedExpiresAtUnixSeconds + consumable.CooldownSeconds };
            _collectors[row.MemberIndex].Events.Add(new(at, "auto-use.depleted",
                $"{item.Name} 的持续时间已耗尽。", _spec.ActionId));
        }
        if (_battle is null || row.SustainedExpiresAtUnixSeconds > 0
            || row.CooldownUntilUnixSeconds > now
            || ItemEscrowRemaining(row.MemberIndex, row.ItemId) < 1) return;
        var caster = _battle.PlayerAllies[row.MemberIndex];
        // 倒地成员不再充能新周期（与单人 Hitpoints<=0 跳过同口径）；已生效周期按墙钟照常耗竭。
        if (!caster.IsAlive) return;
        if (consumable.Triggers is { Count: > 0 } triggers
            && !SkillTriggerEvaluator.MatchesAll(triggers, caster, HighestThreatAlive(_battle.Enemies),
                _battle.PlayerAllies, _battle.Enemies)) return;
        // 托管余量按（成员, 物品）共享池扣件：同物品多槽与单人钱包共享同口径。
        SpendEscrow(row.MemberIndex, row.ItemId);
        _consumables![index] = _consumables![index] with {
            SustainedExpiresAtUnixSeconds = now + consumable.DurationSeconds,
            CooldownUntilUnixSeconds = 0 };
        InjectSustained(row.MemberIndex, item, consumable.DurationSeconds);
        SpendConsumable(row.MemberIndex, item, at, now,
            $"自动使用 {item.Name}，可生效 {consumable.DurationSeconds:0.######} 秒。");
    }

    /// <summary>即时消耗品：触发器求值后恢复/增益/打击就地落账；无存活目标或资源不足时不耗冷却与配额。</summary>
    private void EvaluateInstant(int index, SessionConsumableSlot row, ItemDefinition item,
        ConsumableDefinition consumable, DateTimeOffset at, double now)
    {
        if (row.CooldownUntilUnixSeconds > now
            || ItemEscrowRemaining(row.MemberIndex, row.ItemId) < 1) return;
        if (!_catalog.RawItems.TryGetValue(item.Id.Value, out var rawItem)
            || !rawItem.TryGetProperty("consumable", out var raw)) return;
        var hp = Read(raw, "hitpointRestore");
        var mp = Read(raw, "manapointRestore");
        var hasBuffs = raw.TryGetProperty("buffs", out var buffs) && buffs.ValueKind == JsonValueKind.Array;
        var strike = consumable.Strike;
        if (!hasBuffs && hp == 0 && mp == 0 && strike is null) return;
        var memberIndex = row.MemberIndex;
        // 施放方与敌方上下文：战斗相位取 canonical 单位；非战斗相位以冻结面板+成员资源装配（无敌方目标）。
        // 求值单位只是只读替身，不能进入回血/Buff/打击的写路径——那些只认战内真单位或成员资源池。
        CombatantState? caster = _battle?.PlayerAllies[memberIndex];
        // 倒地单位不消耗托管（与单人 Hitpoints<=0 整段跳过同口径）：尸体不吃药、不打符。
        if (caster is not null ? !caster.IsAlive : _members[memberIndex].Resources.Hitpoints <= 0) return;
        CombatantState? target = _battle is { } battle ? HighestThreatAlive(battle.Enemies) : null;
        if (consumable.Triggers is { Count: > 0 } triggers)
        {
            var evalCaster = caster ?? BuildSessionEvalCaster(memberIndex);
            var allies = _battle?.PlayerAllies ?? (IReadOnlyList<CombatantState>)[evalCaster];
            var enemies = _battle?.Enemies ?? (IReadOnlyList<CombatantState>)[];
            if (!SkillTriggerEvaluator.MatchesAll(triggers, evalCaster, target, allies, enemies)) return;
        }
        var cooldown = consumable.CooldownSeconds;
        if (cooldown <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
        if (strike is { } s)
        {
            // 雷击符打击（总纲 §20.2）：扣施放者灵力后按 Spell 链对最高威胁存活敌造成一次
            // 无命中/暴击掷点的伤害；灵力不足或无存活目标则本次不激活。
            if (caster is null) return;
            var manaCost = s.ManaCostRatio * caster.Stats.MaxManapoints;
            if (target is null || caster.Manapoints < manaCost - 1e-9) return;
            var strikeStats = caster.Stats with
            {
                AttackPower = (s.ManaDamageRatio * caster.Stats.MaxManapoints
                    + s.SpellAttackRatio * caster.Stats.SpellAttack)
                    * PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear),
                DamageKind = CombatDamageKind.Spell,
                Element = s.Element
            };
            var damage = DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, target);
            caster.Manapoints = Math.Max(0, caster.Manapoints - manaCost);
            target.Hitpoints = Math.Max(0, target.Hitpoints - damage);
            if (_battle is { } liveBattle && !liveBattle.Enemies.Any(static e => e.IsAlive))
                liveBattle.Outcome = CombatOutcome.PlayerWon;
            if (target.DefinitionId is { } monsterId
                && Canonical.Duel.ReportFor(Action.Id) is { } strikeReport)
            {
                var book = strikeReport.CombatStats;
                var attackerKey = memberIndex == 0 ? "player" : $"ally:{memberIndex}";
                book.Register(attackerKey, _spec.Members[memberIndex].DisplayName);
                book.Register($"monster:{monsterId}",
                    _catalog.CombatMonsters.TryGetValue(monsterId, out var monster) ? monster.Name : monsterId);
                book.RecordHit(attackerKey, $"monster:{monsterId}", CombatDamageKind.Spell,
                    damage, Math.Max(0, strikeStats.AttackPower - damage));
            }
            SpendEscrow(memberIndex, row.ItemId);
            _consumables![index] = _consumables![index] with { CooldownUntilUnixSeconds = now + cooldown };
            SpendConsumable(memberIndex, item, at, now, $"自动使用 {item.Name}，造成 {damage:0.##} 点伤害。");
            return;
        }
        // 恢复分支：战内写 canonical 单位，战外写成员资源池（并回写远征标量镜像）。
        var healed = 0d;
        if (caster is not null)
        {
            var before = caster.Hitpoints;
            caster.Hitpoints = Math.Min(caster.Stats.MaxHitpoints, caster.Hitpoints + hp);
            caster.Manapoints = Math.Min(caster.Stats.MaxManapoints, caster.Manapoints + mp);
            healed = caster.Hitpoints - before;
        }
        else
        {
            var member = _members[memberIndex];
            var before = member.Resources.Hitpoints;
            member.Resources.Hitpoints = Math.Min(member.Resources.MaxHitpoints, member.Resources.Hitpoints + hp);
            member.Resources.Manapoints = Math.Min(member.Resources.MaxManapoints, member.Resources.Manapoints + mp);
            healed = member.Resources.Hitpoints - before;
            SyncSessionExpeditionResources(memberIndex);
        }
        // 药品瞬回计「其他治疗」输出，与单人账本同口径（autoRegen: false）。
        if (healed > 0 && Canonical.Duel.ReportFor(Action.Id) is { } healReport)
            healReport.CombatStats.RecordHealing(memberIndex == 0 ? "player" : $"ally:{memberIndex}",
                _spec.Members[memberIndex].DisplayName, autoRegen: false, healed);
        // Buff 分支：战内直接注入单位 Buff（战斗域效果就地生效），战外折算跨波携带、入场重注入。
        if (hasBuffs)
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                var duration = Read(buff, "durationSeconds");
                if (duration <= 0) continue;
                if (caster is not null)
                {
                    caster.Buffs.RemoveAll(current => current.BuffId == buffId);
                    caster.Buffs.Add(new CombatBuffState(buffId, _battle!.ElapsedSeconds + duration, 1,
                        Read(buff, "hpRegen"), Read(buff, "mpRegen"), Read(buff, "attackSpeedPoints"),
                        Read(buff, "damageAbsorptionPoints"), 0, Read(buff, "hpRegenPostRatio"),
                        Read(buff, "mpRegenPostRatio"), 0, 0, BuffName(buff, buffId)));
                }
                else AddCarryBuff(memberIndex, new CombatBuffCarry(buffId, duration, 1,
                    Read(buff, "hpRegen"), Read(buff, "mpRegen"), Read(buff, "attackSpeedPoints"),
                    Read(buff, "damageAbsorptionPoints"), 0, Read(buff, "hpRegenPostRatio"),
                    Read(buff, "mpRegenPostRatio"), 0, 0, BuffName(buff, buffId)));
            }
        SpendEscrow(memberIndex, row.ItemId);
        _consumables![index] = _consumables![index] with { CooldownUntilUnixSeconds = now + cooldown };
        SpendConsumable(memberIndex, item, at, now, $"自动使用 {item.Name}。");
    }

    /// <summary>（成员, 物品）托管池剩余合计：同物品多槽共享配额，与单人钱包口径一致。</summary>
    private double ItemEscrowRemaining(int memberIndex, string itemId)
    {
        var total = 0d;
        for (var i = 0; i < _consumables!.Length; i++)
            if (_consumables[i].MemberIndex == memberIndex && _consumables[i].ItemId == itemId)
                total += _consumables[i].RemainingQuantity;
        return total;
    }

    /// <summary>共享池扣 1 件：从同物品最早非空台账行扣减（确定性落账序）。</summary>
    private void SpendEscrow(int memberIndex, string itemId)
    {
        for (var i = 0; i < _consumables!.Length; i++)
            if (_consumables[i].MemberIndex == memberIndex && _consumables[i].ItemId == itemId
                && _consumables[i].RemainingQuantity > 0)
            {
                _consumables[i] = _consumables[i] with { RemainingQuantity = _consumables[i].RemainingQuantity - 1 };
                return;
            }
    }

    /// <summary>
    /// 波次入场后的台账覆盖：spec 定格只注入开打时已充能的持续 Buff，
    /// 台账是权威——未充能/已耗竭槽对应 Buff 清除，存续/新充能槽按墙钟剩余重注入。
    /// </summary>
    private void OverlaySessionSustained(double nowUnix)
    {
        if (_consumables is null || _battle is null) return;
        for (var s = 0; s < _consumables.Length; s++)
        {
            var row = _consumables[s];
            var itemId = new ItemId(row.ItemId);
            if (!_catalog.Items.TryGetValue(itemId, out var item)
                || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0, Durable: false }) continue;
            InjectSustained(row.MemberIndex, item, row.SustainedExpiresAtUnixSeconds - nowUnix);
        }
    }

    /// <summary>持续 Buff 就地注入/清除：阵法类全队生效；remainingSeconds &gt; 0 时按剩余重注入，否则仅清除。</summary>
    private void InjectSustained(int memberIndex, ItemDefinition item, double remainingSeconds)
    {
        if (_battle is null || !_catalog.RawItems.TryGetValue(item.Id.Value, out var rawItem)
            || !rawItem.TryGetProperty("consumable", out var raw)
            || !raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array) return;
        var teamWide = string.Equals(item.SubCategory, AutoUseBar.Formation, StringComparison.Ordinal);
        var expiresAt = _battle.ElapsedSeconds + remainingSeconds;
        foreach (var buff in buffs.EnumerateArray())
        {
            var buffId = buff.GetProperty("buffId").GetString()!;
            for (var u = 0; u < _battle.PlayerAllies.Count; u++)
            {
                if (!teamWide && u != memberIndex) continue;
                var unit = _battle.PlayerAllies[u];
                unit.Buffs.RemoveAll(current => current.BuffId == buffId);
                if (remainingSeconds > 0)
                    unit.Buffs.Add(new CombatBuffState(buffId, expiresAt, 1,
                        Read(buff, "hpRegen"), Read(buff, "mpRegen"), Read(buff, "attackSpeedPoints"),
                        Read(buff, "damageAbsorptionPoints"), 0, Read(buff, "hpRegenPostRatio"),
                        Read(buff, "mpRegenPostRatio"), 0, 0, BuffName(buff, buffId), ResetOnWave: true));
            }
        }
    }

    /// <summary>托管实耗登记：记「失去」物品账本与自动使用事件（台账数量-1 由调用点落账），
    /// 事件附带共享池余量——背包已被开打实扣清零，余量是玩家核对「为何还在使用」的唯一可见凭据。
    /// 结算载荷走收件箱 JSON——ItemDeltas 的 ItemId 字典键不在持久化词表，机器可读扣件由 LostItem 账本承载。</summary>
    private void SpendConsumable(int memberIndex, ItemDefinition item, DateTimeOffset at, double now, string message)
    {
        var member = _members[memberIndex];
        member.RecordStats(member.Actions[0].Id, now, new PlayerStatsDelta { LostItem = item.Id, LostQuantity = 1 });
        _collectors[memberIndex].Events.Add(new SimulationEvent(at, "auto-use.activated",
            $"{message}（托管余 {ItemEscrowRemaining(memberIndex, item.Id.Value):0.##}）", _spec.ActionId));
    }

    /// <summary>非战斗相位求值单位：冻结面板 + 成员资源池当前值；外部 Buff 层数取定格视图。</summary>
    private CombatantState BuildSessionEvalCaster(int memberIndex)
    {
        var member = _members[memberIndex];
        var frozen = _spec.Members[memberIndex];
        var caster = new CombatantState(memberIndex == 0 ? "player" : $"ally:{memberIndex}", frozen.Stats)
        {
            Hitpoints = member.Resources.Hitpoints,
            Manapoints = member.Resources.Manapoints
        };
        var external = frozen.ExternalBuffs;
        caster.ExternalBuffStacks = buffId => CombatActionRules.SumFrozenBuffStacks(external, buffId);
        return caster;
    }

    /// <summary>非战斗相位的恢复量回写远征标量镜像（与单人战外资源压回文档路径同口径）。</summary>
    private void SyncSessionExpeditionResources(int memberIndex)
    {
        if (Action.Expedition is not { } expedition) return;
        var member = _members[memberIndex];
        if (memberIndex == 0)
        {
            Action.Expedition = expedition with
            {
                PlayerHitpoints = member.Resources.Hitpoints,
                PlayerManapoints = member.Resources.Manapoints,
                PlayerState = expedition.PlayerState is { } snapshot
                    ? snapshot with { Hitpoints = member.Resources.Hitpoints,
                        Manapoints = member.Resources.Manapoints }
                    : expedition.PlayerState
            };
            return;
        }
        if (expedition.Allies is not { } allies) return;
        for (var i = 0; i < allies.Count; i++)
            if (allies[i].MemberIndex == memberIndex)
            {
                var updated = allies.ToArray();
                updated[i] = allies[i] with { State = allies[i].State with
                    { Hitpoints = member.Resources.Hitpoints, Manapoints = member.Resources.Manapoints } };
                Action.Expedition = expedition with { Allies = updated };
                return;
            }
    }

    /// <summary>非战斗相位的即时 Buff 按跨波携带记账：canonical 落 Combat.Buffs，成员落远征盟友镜像。</summary>
    private void AddCarryBuff(int memberIndex, CombatBuffCarry carry)
    {
        if (memberIndex == 0)
        {
            Canonical.Combat.Buffs = new PlayerCombatBuffState(
                [.. Canonical.Combat.Buffs.Buffs.Where(entry => entry.BuffId != carry.BuffId), carry]);
            return;
        }
        if (Action.Expedition?.Allies is not { } allies) return;
        for (var i = 0; i < allies.Count; i++)
            if (allies[i].MemberIndex == memberIndex)
            {
                var current = allies[i].CombatBuffs?.Buffs ?? (IReadOnlyList<CombatBuffCarry>)[];
                var updated = allies.ToArray();
                updated[i] = allies[i] with { CombatBuffs = new PlayerCombatBuffState(
                    [.. current.Where(entry => entry.BuffId != carry.BuffId), carry]) };
                Action.Expedition = Action.Expedition with { Allies = updated };
                return;
            }
    }

    /// <summary>存活敌中有效威胁最高者（与单人求值目标解析同口径：严格更大才替换，平局保留先见者）。</summary>
    private static CombatantState? HighestThreatAlive(IReadOnlyList<CombatantState> enemies)
    {
        CombatantState? best = null;
        for (var i = 0; i < enemies.Count; i++)
            if (enemies[i].IsAlive && (best is null || enemies[i].EffectiveThreat > best.EffectiveThreat))
                best = enemies[i];
        return best;
    }

    /// <summary>物品原始 JSON 数值字段读取（缺省 0）；与单人消耗品求值同口径。</summary>
    private static double Read(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    /// <summary>Buff 显示名读取（缺省回 BuffId）。</summary>
    private static string BuffName(JsonElement buff, string buffId)
        => buff.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? buffId : buffId;

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
            Canonical.Combat.Skills, Canonical.Combat.Buffs,
            _members.Select(member => member.Resources.RegenCarrySeconds).ToArray(), Action.CompletedRepeats,
            StatsDocuments(Canonical.Duel.ReportFor(Action.Id)!.CombatStats), _endsAt, _consumables);
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
        return ProjectExpedition(document.Expedition!, index, document.Skills, document.Buffs);
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
            member.Combat.Buffs = canonical.Allies!.First(ally => ally.MemberIndex == i).CombatBuffs
                ?? _spec.Members[i].CombatBuffs ?? PlayerCombatBuffState.Empty;
        }
    }

    private CombatExpeditionState ProjectExpedition(CombatExpeditionState canonical, int memberIndex)
        => ProjectExpedition(canonical, memberIndex, Canonical.Combat.Skills, Canonical.Combat.Buffs);

    private static CombatExpeditionState ProjectExpedition(CombatExpeditionState canonical, int memberIndex,
        PlayerCombatSkillState canonicalSkills, PlayerCombatBuffState canonicalBuffs)
    {
        if (memberIndex == 0) return canonical;
        var self = canonical.Allies!.First(ally => ally.MemberIndex == memberIndex);
        var previousSelf = canonical.PlayerState ?? new CombatantSnapshot(canonical.PlayerHitpoints,
            canonical.PlayerManapoints, canonical.PlayerNextAttackAtSeconds, canonical.PlayerAttackCount,
            canonical.PlayerThreat);
        var others = canonical.Allies!.Where(ally => ally.MemberIndex != memberIndex).ToList();
        others.Add(new(0, previousSelf, CombatSkills: canonicalSkills, CombatBuffs: canonicalBuffs));
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
        public readonly List<DuelStagedItem> StagedItems = [];
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
            if (mutation.StagedItems is { Count: > 0 } staged) StagedItems.AddRange(staged);
            if (!includeAssets) return;
            foreach (var delta in mutation.ToAssetDeltas(playerId, sortByItem: false))
                Assets[delta.Asset] = Assets.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
        }

        public void Reset()
        {
            Stats.Clear();
            Assets.Clear();
            StagedItems.Clear();
            Events = new();
        }
    }
}
