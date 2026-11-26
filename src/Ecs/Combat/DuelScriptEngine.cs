using System.Text;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;

namespace DaoHua.Ecs;

/// <summary>
/// 双阵营完全对称斗法剧本推演器（原生支持 1~5 vs 1~5，总纲 §7.10.2 / §7.10.4）：
/// 撮合成功后把双方全员镜像（属性、功法、已启用战斗消耗品与库存）经 <see cref="CombatEncounterState.CreateSymmetric"/>
/// 一次性确定性推演至终结；内部按规范化双方身份归序并采用同刻同时生效与无偏随机标签，
/// 严格保证 <c>Resolve(A, B, seed)</c> 与 <c>Resolve(B, A, seed)</c> 逐帧镜像等价且零先手偏向。
/// </summary>
public static class DuelScriptEngine
{
    private sealed class DuelSlotRuntime
    {
        public required ItemDefinition Item { get; init; }
        public required ConsumableDefinition Consumable { get; init; }
        public required JsonElement RawConsumable { get; init; }
        public required bool IsSustained { get; init; }
        public double AvailableAtSeconds { get; set; }
        public double SustainedExpiresAtSeconds { get; set; }
    }

    private sealed class DuelConsumableRuntime
    {
        public List<DuelSlotRuntime> SustainedSlots { get; } = [];
        public List<DuelSlotRuntime> InstantSlots { get; } = [];
        public Dictionary<string, double> Balances { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, JsonElement[]>? Triggers { get; init; }
        public double StrikeUseCount { get; set; }
    }

    private readonly record struct PlannedSustained(
        CombatantState Caster,
        DuelConsumableRuntime Runtime,
        DuelSlotRuntime Slot);

    private readonly record struct PlannedStrike(
        CombatantState Caster,
        CombatantState Opponent,
        DuelConsumableRuntime Runtime,
        DuelSlotRuntime Slot,
        double ManaCost,
        CombatElement Element,
        double RawPower,
        double Damage);

    private readonly record struct PlannedPotion(
        CombatantState Caster,
        DuelConsumableRuntime Runtime,
        DuelSlotRuntime Slot,
        double HpRestore,
        double MpRestore,
        bool HasBuffs,
        JsonElement BuffsEl);

    /// <summary>推演一场 1v1 斗法（薄委托至多方对称主实现，initiator 与 defender 地位完全平等）。</summary>
    public static DuelScript Resolve(
        DuelCombatant initiator,
        DuelCombatant defender,
        long seed,
        DuelSettings settings,
        GameDataCatalog? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(defender);
        return Resolve([initiator], [defender], seed, settings, gameData);
    }

    /// <summary>
    /// 推演一场双阵营 1~5 vs 1~5 对称斗法：
    /// 双方全体参战单位均标记为 <see cref="CombatantKind.Player"/>，同刻出手/消耗品/周期效果同时生效，
    /// 返回的 <see cref="DuelScript.Outcome"/> 与事件坐标均翻译回传入的 initiator/defender 视角。
    /// </summary>
    public static DuelScript Resolve(
        IReadOnlyList<DuelCombatant> initiatorSide,
        IReadOnlyList<DuelCombatant> defenderSide,
        long seed,
        DuelSettings settings,
        GameDataCatalog? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(initiatorSide);
        ArgumentNullException.ThrowIfNull(defenderSide);
        ArgumentNullException.ThrowIfNull(settings);
        if (initiatorSide.Count is < 1 or > DuelPlaybackState.MaxTeamUnits || initiatorSide.Any(static c => c is null))
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法发起方阵容无效。");
        if (defenderSide.Count is < 1 or > DuelPlaybackState.MaxTeamUnits || defenderSide.Any(static c => c is null))
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法应战方阵容无效。");

        // 规范化归序：同一对参战阵容 (A, B) 无论以 (A, B) 还是 (B, A) 传入，内部恒映射为同一 (SideA, SideB)，
        // 再配合 seed 奇偶无偏标签与同刻同时生效，严格保证交换不变性且消除 UUID 字典序偏向。
        var swapped = StringComparer.Ordinal.Compare(CanonicalSideKey(initiatorSide), CanonicalSideKey(defenderSide)) > 0;
        var sideACombatants = swapped ? defenderSide : initiatorSide;
        var sideBCombatants = swapped ? initiatorSide : defenderSide;
        var initiatorCombatSide = swapped ? CombatSide.SideB : CombatSide.SideA;
        var defenderCombatSide = CombatEncounterState.Opposite(initiatorCombatSide);

        var encounter = CombatEncounterState.CreateSymmetric("duel", seed,
            BuildTemplates(sideACombatants),
            BuildTemplates(sideBCombatants));

        var statsBook = new CombatUnitStatsBook();
        encounter.StatsBook = statsBook;
        for (var i = 0; i < encounter.SideA.Count; i++)
            statsBook.Register(encounter.SideA[i].Id, sideACombatants[i].DisplayName, CombatStatUnitKind.Ally);
        for (var i = 0; i < encounter.SideB.Count; i++)
            statsBook.Register(encounter.SideB[i].Id, sideBCombatants[i].DisplayName, CombatStatUnitKind.Ally);

        var recorder = new DuelActionRecorder(sideACombatants, sideBCombatants);
        encounter.ActionRecorder = recorder;
        for (var i = 0; i < encounter.SideA.Count; i++)
            RecordBattleStartTraits(recorder, encounter.SideA[i]);
        for (var i = 0; i < encounter.SideB.Count; i++)
            RecordBattleStartTraits(recorder, encounter.SideB[i]);

        WireExternalBuffs(encounter, encounter.SideA, sideACombatants);
        WireExternalBuffs(encounter, encounter.SideB, sideBCombatants);

        var sideARuntimes = BuildSideConsumables(sideACombatants, gameData, out var hasSideAConsumables);
        var sideBRuntimes = BuildSideConsumables(sideBCombatants, gameData, out var hasSideBConsumables);
        var hasConsumables = hasSideAConsumables || hasSideBConsumables;

        InjectSideActiveSustainedBuffs(encounter, encounter.SideA, sideARuntimes, 0, recorder);
        InjectSideActiveSustainedBuffs(encounter, encounter.SideB, sideBRuntimes, 0, recorder);

        var events = new List<DuelScriptEvent>();
        var trackedSideAHp = InitHp(sideACombatants);
        var trackedSideBHp = InitHp(sideBCombatants);
        var trackedSideAMp = InitMp(sideACombatants);
        var trackedSideBMp = InitMp(sideBCombatants);

        // 开打前先对空持续槽双侧同步补能并注入阵盘 Buff（阵法类全队生效），再基于同一开局快照并行决策首次出手。
        if (hasConsumables)
        {
            EvaluateSymmetricConsumables(encounter, initiatorCombatSide, sideARuntimes, sideBRuntimes,
                evaluateSustained: true, evaluateInstant: false,
                statsBook, events, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
        }

        Systems.CombatActSystem.DecideInitialActions(encounter);

        // 开打首帧双侧同步收敛即时消耗品（同刻双雷击符互秒判平，被打残后同刻下一波收敛吃止血散）。
        if (hasConsumables)
        {
            EvaluateSymmetricConsumables(encounter, initiatorCombatSide, sideARuntimes, sideBRuntimes,
                evaluateSustained: true, evaluateInstant: true,
                statsBook, events, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
        }

        if (!hasConsumables)
        {
            var result = Systems.CombatEventAdvanceSystem.AdvanceTo(encounter, settings.MaxDuelSeconds);
            AppendCombatEvents(encounter, initiatorCombatSide, result.Events, events,
                trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
            EmitResourceSyncIfChanged(encounter, initiatorCombatSide, events,
                trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
        }
        else
        {
            UpdateDuelOutcome(encounter);
            var stepCount = 0;
            while (encounter.Outcome == CombatOutcome.Running
                && encounter.ElapsedSeconds < settings.MaxDuelSeconds - 1e-12)
            {
                if (++stepCount > Systems.CombatEventAdvanceSystem.MaxEventsPerAdvance)
                    throw new DomainException(DomainErrorCode.InvariantViolation, "斗法剧本推进超过最大事件数。");
                var stepTarget = Math.Min(settings.MaxDuelSeconds,
                    NextConsumableWake(encounter.ElapsedSeconds, sideARuntimes, sideBRuntimes, sideACombatants, sideBCombatants));
                var step = Systems.CombatEventAdvanceSystem.AdvanceOneBoundary(encounter, stepTarget);
                AppendCombatEvents(encounter, initiatorCombatSide, step.Events, events,
                    trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
                EmitResourceSyncIfChanged(encounter, initiatorCombatSide, events,
                    trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
                if (encounter.Outcome != CombatOutcome.Running) break;
                ExpireSideSustainedSlots(sideARuntimes, encounter.ElapsedSeconds);
                ExpireSideSustainedSlots(sideBRuntimes, encounter.ElapsedSeconds);
                EvaluateSymmetricConsumables(encounter, initiatorCombatSide, sideARuntimes, sideBRuntimes,
                    evaluateSustained: true, evaluateInstant: true,
                    statsBook, events, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
            }
        }

        var rawOutcome = encounter.Outcome == CombatOutcome.Running ? CombatOutcome.Draw : encounter.Outcome;
        var initiatorOutcome = swapped ? FlipOutcome(rawOutcome) : rawOutcome;
        var defenderOutcome = FlipOutcome(initiatorOutcome);
        var duration = Math.Max(encounter.ElapsedSeconds, 1e-9);

        var initiatorUnits = encounter.UnitsOf(initiatorCombatSide);
        var defenderUnits = encounter.UnitsOf(defenderCombatSide);
        var initiatorMemberStats = ProjectSideMemberStats(statsBook, initiatorUnits, initiatorSide);
        var defenderMemberStats = ProjectSideMemberStats(statsBook, defenderUnits, defenderSide);
        var initiatorMemberRecords = BuildSideMemberRecords(recorder, initiatorOutcome, duration, initiatorCombatSide, initiatorSide.Count);
        var defenderMemberRecords = BuildSideMemberRecords(recorder, defenderOutcome, duration, defenderCombatSide, defenderSide.Count);

        return new DuelScript(
            initiatorOutcome,
            duration,
            events,
            CollectSideStagedItems(initiatorSide),
            CollectSideStagedItems(defenderSide),
            InitiatorStats: initiatorMemberStats[0],
            DefenderStats: defenderMemberStats[0],
            InitiatorRecord: initiatorMemberRecords[0],
            DefenderRecord: defenderMemberRecords[0],
            InitiatorMemberStats: initiatorMemberStats,
            DefenderMemberStats: defenderMemberStats,
            InitiatorMemberRecords: initiatorMemberRecords,
            DefenderMemberRecords: defenderMemberRecords);
    }

    private static string CanonicalSideKey(IReadOnlyList<DuelCombatant> side)
    {
        var sb = new StringBuilder(side.Count * 40);
        for (var i = 0; i < side.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(side[i].PlayerId.Value.ToString("N"));
            sb.Append(':');
            sb.Append(side[i].DisplayName);
        }
        return sb.ToString();
    }

    private static CombatantTemplate[] BuildTemplates(IReadOnlyList<DuelCombatant> combatants)
    {
        var templates = new CombatantTemplate[combatants.Count];
        for (var i = 0; i < combatants.Count; i++)
            templates[i] = new CombatantTemplate(combatants[i].Stats, combatants[i].Skills, Kind: CombatantKind.Player);
        return templates;
    }

    private static void WireExternalBuffs(CombatEncounterState encounter, IReadOnlyList<CombatantState> units,
        IReadOnlyList<DuelCombatant> combatants)
    {
        for (var i = 0; i < units.Count; i++)
        {
            if (combatants[i].ExternalBuffs is not { Count: > 0 } external) continue;
            var unit = units[i];
            var snapshotUnix = combatants[i].SnapshotAtUnixSeconds;
            unit.ExternalBuffStacks = buffId => Systems.ActionExecutionRules.SumUnmergedActiveBuffStacks(
                unit, external, buffId, snapshotUnix + encounter.ElapsedSeconds);
        }
    }

    private static DuelConsumableRuntime?[] BuildSideConsumables(IReadOnlyList<DuelCombatant> combatants,
        GameDataCatalog? gameData, out bool any)
    {
        any = false;
        var runtimes = new DuelConsumableRuntime?[combatants.Count];
        for (var i = 0; i < combatants.Count; i++)
        {
            var rt = BuildConsumableRuntime(combatants[i], gameData);
            runtimes[i] = rt;
            if (rt is not null) any = true;
        }
        return runtimes;
    }

    private static double[] InitHp(IReadOnlyList<DuelCombatant> combatants)
    {
        var arr = new double[combatants.Count];
        for (var i = 0; i < combatants.Count; i++) arr[i] = combatants[i].Stats.MaxHitpoints;
        return arr;
    }

    private static double[] InitMp(IReadOnlyList<DuelCombatant> combatants)
    {
        var arr = new double[combatants.Count];
        for (var i = 0; i < combatants.Count; i++) arr[i] = combatants[i].Stats.MaxManapoints;
        return arr;
    }

    private static DuelConsumableRuntime? BuildConsumableRuntime(DuelCombatant combatant, GameDataCatalog? gameData)
    {
        if (gameData is null || combatant.ConsumableSlots is not { Count: > 0 } slots) return null;
        var runtime = new DuelConsumableRuntime { Triggers = combatant.ConsumableTriggers };
        if (combatant.ItemBalances is not null)
            foreach (var pair in combatant.ItemBalances)
                runtime.Balances[pair.Key] = pair.Value;

        foreach (var slot in slots)
        {
            if (!new ItemId(slot.ItemId).IsValid
                || !gameData.Items.TryGetValue(new ItemId(slot.ItemId), out var item)
                || item.Consumable is not { Durable: false } consumable
                || !consumable.UsableInBars.Contains(AutoUseBar.Combat)
                || !gameData.RawItems.TryGetValue(item.Id.Value, out var rawItem)
                || !rawItem.TryGetProperty("consumable", out var rawConsumable))
                continue;
            if (consumable is { IsContinuous: true, DurationSeconds: > 0 })
            {
                if (!string.Equals(slot.Group, AutoUseSlotState.SlotGroupGeneral, StringComparison.Ordinal))
                    continue;
                runtime.SustainedSlots.Add(new DuelSlotRuntime
                {
                    Item = item,
                    Consumable = consumable,
                    RawConsumable = rawConsumable,
                    IsSustained = true,
                    SustainedExpiresAtSeconds = slot.InitialSustainedRemainingSeconds > 0
                        ? slot.InitialSustainedRemainingSeconds : 0
                });
            }
            else if (!consumable.IsContinuous && consumable.CooldownSeconds > 0)
            {
                runtime.InstantSlots.Add(new DuelSlotRuntime
                {
                    Item = item,
                    Consumable = consumable,
                    RawConsumable = rawConsumable,
                    IsSustained = false,
                    AvailableAtSeconds = Math.Max(0, slot.InitialCooldownRemainingSeconds)
                });
            }
        }
        return runtime.SustainedSlots.Count > 0 || runtime.InstantSlots.Count > 0 ? runtime : null;
    }

    private static void RecordBattleStartTraits(DuelActionRecorder recorder, CombatantState unit)
    {
        for (var i = 0; i < unit.Buffs.Count; i++)
        {
            var buff = unit.Buffs[i];
            var traitId = buff.BuffId switch
            {
                CombatTraitRules.UnseenBuffId => CombatTraitRules.UnseenTraitId,
                "buff.chujian" => CombatTraitRules.FirstSightTraitId,
                "buff.yusuzebuda" => CombatTraitRules.HasteTraitId,
                "buff.woyaodashige" => CombatTraitRules.OutnumberedTraitId,
                "buff.dantiaowang" => CombatTraitRules.DuelKingTraitId,
                _ => null
            };
            if (traitId is not null)
                recorder.RecordBuff(unit, unit, buff.BuffId, buff.Stacks, DuelRecordActionKind.Trait, 0, traitId);
        }
    }

    private static void InjectSideActiveSustainedBuffs(CombatEncounterState encounter, IReadOnlyList<CombatantState> units,
        DuelConsumableRuntime?[] runtimes, double elapsed, ICombatActionRecorder? recorder)
    {
        for (var i = 0; i < units.Count; i++)
        {
            var runtime = runtimes[i];
            if (runtime is null) continue;
            var caster = units[i];
            var allies = encounter.AlliesOf(caster);
            foreach (var slot in runtime.SustainedSlots)
            {
                if (slot.SustainedExpiresAtSeconds <= elapsed + 1e-9) continue;
                var isFormation = string.Equals(slot.Item.SubCategory, AutoUseBar.Formation, StringComparison.Ordinal);
                var actionKind = isFormation ? DuelRecordActionKind.Formation : DuelRecordActionKind.Item;
                recorder?.BeginAction(elapsed, caster, actionKind, slot.Item.Id.Value);
                try
                {
                    InjectSustainedSlotBuffs(caster, isFormation ? allies : null, slot, slot.SustainedExpiresAtSeconds, recorder);
                }
                finally
                {
                    recorder?.EndAction();
                }
            }
        }
    }

    /// <summary>注入持续消耗品 Buff：阵法类全队在场存活单位生效（总纲 §7.9.5），其余作用于使用者单体。</summary>
    private static void InjectSustainedSlotBuffs(CombatantState caster, IReadOnlyList<CombatantState>? teamTargets,
        DuelSlotRuntime slot, double expiresAtSeconds, ICombatActionRecorder? recorder = null)
    {
        if (!slot.RawConsumable.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var buff in buffs.EnumerateArray())
        {
            var buffId = buff.GetProperty("buffId").GetString()!;
            var name = JsonRead.Text(buff, "name") ?? buffId;
            var effects = BuffEffectParser.Parse(buff, 1.0);
            if (teamTargets is not null)
            {
                for (var t = 0; t < teamTargets.Count; t++)
                {
                    var target = teamTargets[t];
                    if (!target.IsAlive) continue;
                    target.SetBuff(new CombatBuffState(buffId, expiresAtSeconds, 1, name, ResetOnWave: true, Effects: effects));
                    recorder?.RecordBuff(caster, target, buffId, 1);
                }
            }
            else
            {
                caster.SetBuff(new CombatBuffState(buffId, expiresAtSeconds, 1, name, ResetOnWave: true, Effects: effects));
                recorder?.RecordBuff(caster, caster, buffId, 1);
            }
        }
    }

    private static void ExpireSideSustainedSlots(DuelConsumableRuntime?[] runtimes, double elapsed)
    {
        for (var i = 0; i < runtimes.Length; i++)
        {
            var runtime = runtimes[i];
            if (runtime is null) continue;
            foreach (var slot in runtime.SustainedSlots)
                if (slot.SustainedExpiresAtSeconds > 0 && slot.SustainedExpiresAtSeconds <= elapsed + 1e-9)
                    slot.SustainedExpiresAtSeconds = 0;
        }
    }

    private static double NextConsumableWake(double elapsed,
        DuelConsumableRuntime?[] sideARuntimes, DuelConsumableRuntime?[] sideBRuntimes,
        IReadOnlyList<DuelCombatant> sideACombatants, IReadOnlyList<DuelCombatant> sideBCombatants)
    {
        var next = double.PositiveInfinity;
        for (var i = 0; i < sideARuntimes.Length; i++) ScanRuntime(sideARuntimes[i]);
        for (var i = 0; i < sideBRuntimes.Length; i++) ScanRuntime(sideBRuntimes[i]);
        for (var i = 0; i < sideACombatants.Count; i++) ScanExternal(sideACombatants[i].ExternalBuffs, sideACombatants[i].SnapshotAtUnixSeconds);
        for (var i = 0; i < sideBCombatants.Count; i++) ScanExternal(sideBCombatants[i].ExternalBuffs, sideBCombatants[i].SnapshotAtUnixSeconds);
        return next;

        void ScanRuntime(DuelConsumableRuntime? runtime)
        {
            if (runtime is null) return;
            foreach (var slot in runtime.InstantSlots)
                if (slot.AvailableAtSeconds > elapsed + 1e-9 && slot.AvailableAtSeconds < next)
                    next = slot.AvailableAtSeconds;
            foreach (var slot in runtime.SustainedSlots)
                if (slot.SustainedExpiresAtSeconds > elapsed + 1e-9 && slot.SustainedExpiresAtSeconds < next)
                    next = slot.SustainedExpiresAtSeconds;
        }

        void ScanExternal(IReadOnlyList<ActiveBuff>? external, double snapshotAtUnix)
        {
            if (external is null) return;
            for (var i = 0; i < external.Count; i++)
            {
                var relExpiry = external[i].ExpiresAtUnixSeconds - snapshotAtUnix;
                if (relExpiry > elapsed + 1e-9 && relExpiry < next)
                    next = relExpiry;
            }
        }
    }

    /// <summary>
    /// 双侧完全对称的同刻消耗品波次收敛循环（总纲 §7.10.2）：
    /// 每波基于波初快照同时扫描双侧全部存活成员的持续槽与首个就绪即时槽，
    /// 所有打击按波初面板统一求值并同时落账（同刻双雷击符互秒判平），仍存活单位随后统一落账丹药治疗与增益，
    /// 并循环进入下一波收敛（如被雷击符打残后同刻触发止血散），直至整波双方均无可触发项或战局终结。
    /// </summary>
    private static void EvaluateSymmetricConsumables(
        CombatEncounterState encounter,
        CombatSide initiatorCombatSide,
        DuelConsumableRuntime?[] sideARuntimes,
        DuelConsumableRuntime?[] sideBRuntimes,
        bool evaluateSustained,
        bool evaluateInstant,
        CombatUnitStatsBook statsBook,
        List<DuelScriptEvent> events,
        double[] trackedSideAHp,
        double[] trackedSideBHp,
        double[] trackedSideAMp,
        double[] trackedSideBMp)
    {
        var flipTieOrder = (encounter.Seed & 1L) != 0;
        var firstSide = flipTieOrder ? CombatSide.SideB : CombatSide.SideA;
        var secondSide = CombatEncounterState.Opposite(firstSide);

        for (var wave = 0; wave < 256; wave++)
        {
            if (encounter.Outcome != CombatOutcome.Running) break;

            List<PlannedSustained>? plannedSustained = null;
            List<PlannedStrike>? plannedStrikes = null;
            List<PlannedPotion>? plannedPotions = null;

            // 1. 持续槽双侧同步快照扫描（持续槽只挂增益/消耗库存，先全部同步规划再落账，保证本波即时槽能读到刚充能的持续状态）。
            if (evaluateSustained)
            {
                PlanSideSustained(firstSide);
                PlanSideSustained(secondSide);
                if (plannedSustained is { Count: > 0 })
                {
                    for (var p = 0; p < plannedSustained.Count; p++)
                    {
                        var plan = plannedSustained[p];
                        var itemId = plan.Slot.Item.Id.Value;
                        if (plan.Runtime.Balances.GetValueOrDefault(itemId) < 1d) continue;
                        plan.Runtime.Balances[itemId] = plan.Runtime.Balances.GetValueOrDefault(itemId) - 1d;
                        plan.Slot.SustainedExpiresAtSeconds = encounter.ElapsedSeconds + plan.Slot.Consumable.DurationSeconds;
                        var isFormation = string.Equals(plan.Slot.Item.SubCategory, AutoUseBar.Formation, StringComparison.Ordinal);
                        var sustainedKind = isFormation ? DuelRecordActionKind.Formation : DuelRecordActionKind.Item;
                        encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, plan.Caster, sustainedKind, itemId);
                        try
                        {
                            InjectSustainedSlotBuffs(plan.Caster, isFormation ? encounter.AlliesOf(plan.Caster) : null,
                                plan.Slot, plan.Slot.SustainedExpiresAtSeconds, encounter.ActionRecorder);
                        }
                        finally
                        {
                            encounter.ActionRecorder?.EndAction();
                        }
                        var isInitiator = plan.Caster.Side == initiatorCombatSide;
                        events.Add(new DuelScriptEvent(
                            encounter.ElapsedSeconds,
                            isInitiator,
                            isInitiator,
                            0,
                            true,
                            false,
                            plan.Caster.Hitpoints,
                            itemId,
                            0,
                            plan.Caster.Manapoints,
                            plan.Caster.Manapoints,
                            AbilityNameOverride: DuelScriptEvent.SustainedActivateMarker,
                            AttackerUnitIndex: plan.Caster.SideIndex,
                            TargetUnitIndex: plan.Caster.SideIndex));
                    }
                }
            }

            // 2. 即时槽双侧同步快照扫描（每名存活成员本波至多选取其首个满足条件的即时消耗品）。
            if (evaluateInstant)
            {
                PlanSideInstant(firstSide);
                PlanSideInstant(secondSide);
            }

            var anySustained = plannedSustained is { Count: > 0 };
            var anyStrikes = plannedStrikes is { Count: > 0 };
            var anyPotions = plannedPotions is { Count: > 0 };
            if (!anySustained && !anyStrikes && !anyPotions) break;

            // 3. 同步落账全部即时打击（基于波初已算好的伤害与元素，双侧同时生效、暂缓存活人数特质刷新）。
            if (anyStrikes || anyPotions)
            {
                encounter.DeferAliveCountNotifications = true;
                try
                {
                    if (plannedStrikes is { Count: > 0 })
                    {
                        for (var s = 0; s < plannedStrikes.Count; s++)
                        {
                            var strike = plannedStrikes[s];
                            var itemId = strike.Slot.Item.Id.Value;
                            var wasOpponentAlive = strike.Opponent.IsAlive;
                            strike.Caster.Manapoints = Math.Max(0, strike.Caster.Manapoints - strike.ManaCost);
                            var settled = CombatDamageSettlement.Apply(encounter, strike.Caster, strike.Opponent, strike.Damage);
                            var damage = settled.Damage;
                            if (encounter.ActionRecorder is { } strikeRecorder)
                            {
                                strikeRecorder.BeginAction(encounter.ElapsedSeconds, strike.Caster, DuelRecordActionKind.Item, itemId);
                                try
                                {
                                    strikeRecorder.RecordDamage(strike.Caster, strike.Opponent, DuelRecordEffectKind.Damage,
                                        CombatDamageKind.Spell, strike.Element, DuelRecordHitMark.Hit,
                                        damage, itemId, targetDowned: wasOpponentAlive && !strike.Opponent.IsAlive);
                                    CombatTraitReactions.ApplyRanhun(strike.Caster, strike.Opponent, strike.Element, settled.Actual, encounter.ElapsedSeconds, encounter);
                                }
                                finally
                                {
                                    strikeRecorder.EndAction();
                                }
                            }
                            else
                            {
                                CombatTraitReactions.ApplyRanhun(strike.Caster, strike.Opponent, strike.Element, settled.Actual, encounter.ElapsedSeconds);
                            }
                            statsBook.RecordHit(strike.Caster, strike.Opponent, CombatDamageKind.Spell,
                                damage, Math.Max(0, strike.RawPower - damage));
                            strike.Runtime.Balances[itemId] = strike.Runtime.Balances.GetValueOrDefault(itemId) - 1d;
                            strike.Slot.AvailableAtSeconds = encounter.ElapsedSeconds + strike.Slot.Consumable.CooldownSeconds;
                            strike.Runtime.StrikeUseCount += 1;
                            SyncTrackedUnit(strike.Caster, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
                            SyncTrackedUnit(strike.Opponent, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
                            events.Add(new DuelScriptEvent(
                                encounter.ElapsedSeconds,
                                strike.Caster.Side == initiatorCombatSide,
                                strike.Opponent.Side == initiatorCombatSide,
                                damage,
                                true,
                                false,
                                strike.Opponent.Hitpoints,
                                itemId,
                                0,
                                strike.Caster.Manapoints,
                                strike.Opponent.Manapoints,
                                AttackerUnitIndex: strike.Caster.SideIndex,
                                TargetUnitIndex: strike.Opponent.SideIndex));
                        }
                    }

                    // 4. 同步落账丹药/增益（仅对本波打击后仍存活的施放者生效，防止同刻暴毙者空耗丹药）。
                    if (plannedPotions is { Count: > 0 })
                    {
                        for (var p = 0; p < plannedPotions.Count; p++)
                        {
                            var potion = plannedPotions[p];
                            if (!potion.Caster.IsAlive) continue;
                            var itemId = potion.Slot.Item.Id.Value;
                            var hpBefore = potion.Caster.Hitpoints;
                            var mpBefore = potion.Caster.Manapoints;
                            potion.Caster.Hitpoints = Math.Min(potion.Caster.Stats.MaxHitpoints, potion.Caster.Hitpoints + potion.HpRestore);
                            potion.Caster.Manapoints = Math.Min(potion.Caster.Stats.MaxManapoints, potion.Caster.Manapoints + potion.MpRestore);
                            potion.Caster.SyncDerivedStacks();
                            var healed = potion.Caster.Hitpoints - hpBefore;
                            var mpRestored = potion.Caster.Manapoints - mpBefore;
                            if (healed > 0)
                                statsBook.RecordHealing(potion.Caster, autoRegen: false, healed);
                            encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, potion.Caster, DuelRecordActionKind.Item, itemId);
                            try
                            {
                                if (healed > 0) encounter.ActionRecorder?.RecordHeal(potion.Caster, potion.Caster, DuelRecordEffectKind.Heal, healed, itemId);
                                if (mpRestored > 0) encounter.ActionRecorder?.RecordMana(potion.Caster, potion.Caster, DuelRecordEffectKind.ManaRestore, mpRestored, itemId);
                                if (potion.HasBuffs)
                                {
                                    foreach (var buff in potion.BuffsEl.EnumerateArray())
                                    {
                                        var buffId = buff.GetProperty("buffId").GetString()!;
                                        var duration = JsonRead.Number(buff, "durationSeconds");
                                        if (duration <= 0) continue;
                                        var effects = BuffEffectParser.Parse(buff, 1.0);
                                        potion.Caster.SetBuff(new CombatBuffState(buffId, encounter.ElapsedSeconds + duration, 1,
                                            JsonRead.Text(buff, "name") ?? buffId, ResetOnWave: true, Effects: effects));
                                        encounter.ActionRecorder?.RecordBuff(potion.Caster, potion.Caster, buffId, 1);
                                    }
                                }
                            }
                            finally
                            {
                                encounter.ActionRecorder?.EndAction();
                            }
                            potion.Runtime.Balances[itemId] = potion.Runtime.Balances.GetValueOrDefault(itemId) - 1d;
                            potion.Slot.AvailableAtSeconds = encounter.ElapsedSeconds + potion.Slot.Consumable.CooldownSeconds;
                            SyncTrackedUnit(potion.Caster, trackedSideAHp, trackedSideBHp, trackedSideAMp, trackedSideBMp);
                            var isInitiator = potion.Caster.Side == initiatorCombatSide;
                            events.Add(new DuelScriptEvent(
                                encounter.ElapsedSeconds,
                                isInitiator,
                                isInitiator,
                                0,
                                true,
                                false,
                                potion.Caster.Hitpoints,
                                itemId,
                                healed,
                                potion.Caster.Manapoints,
                                potion.Caster.Manapoints,
                                AttackerUnitIndex: potion.Caster.SideIndex,
                                TargetUnitIndex: potion.Caster.SideIndex));
                        }
                    }
                }
                finally
                {
                    encounter.FlushAliveCountNotifications();
                }

                UpdateDuelOutcome(encounter);
                if (encounter.Outcome != CombatOutcome.Running) break;
            }

            void PlanSideSustained(CombatSide side)
            {
                var units = encounter.UnitsOf(side);
                var runtimes = side == CombatSide.SideA ? sideARuntimes : sideBRuntimes;
                var opponents = encounter.UnitsOf(CombatEncounterState.Opposite(side));
                for (var u = 0; u < units.Count; u++)
                {
                    var caster = units[u];
                    var runtime = runtimes[u];
                    if (!caster.IsAlive || runtime is null) continue;
                    var primaryOpponent = PickConsumableTarget(encounter, caster, opponents, runtime.StrikeUseCount);
                    for (var i = 0; i < runtime.SustainedSlots.Count; i++)
                    {
                        var slot = runtime.SustainedSlots[i];
                        if (slot.SustainedExpiresAtSeconds > encounter.ElapsedSeconds + 1e-9) continue;
                        var itemId = slot.Item.Id.Value;
                        if (runtime.Balances.GetValueOrDefault(itemId) < 1d) continue;
                        var triggers = ConsumableTriggerResolver.Resolve(itemId, slot.Consumable, runtime.Triggers);
                        if (triggers.Count > 0 && !SkillTriggerEvaluator.MatchesAll(triggers, caster,
                                primaryOpponent, units, opponents, encounter.ElapsedSeconds))
                            continue;
                        (plannedSustained ??= []).Add(new PlannedSustained(caster, runtime, slot));
                    }
                }
            }

            void PlanSideInstant(CombatSide side)
            {
                var units = encounter.UnitsOf(side);
                var runtimes = side == CombatSide.SideA ? sideARuntimes : sideBRuntimes;
                var opponents = encounter.UnitsOf(CombatEncounterState.Opposite(side));
                for (var u = 0; u < units.Count; u++)
                {
                    var caster = units[u];
                    var runtime = runtimes[u];
                    if (!caster.IsAlive || runtime is null) continue;
                    var opponent = PickConsumableTarget(encounter, caster, opponents, runtime.StrikeUseCount);
                    for (var i = 0; i < runtime.InstantSlots.Count; i++)
                    {
                        var slot = runtime.InstantSlots[i];
                        if (slot.AvailableAtSeconds > encounter.ElapsedSeconds + 1e-9) continue;
                        var itemId = slot.Item.Id.Value;
                        if (runtime.Balances.GetValueOrDefault(itemId) < 1d) continue;
                        var triggers = ConsumableTriggerResolver.Resolve(itemId, slot.Consumable, runtime.Triggers);
                        if (triggers.Count > 0 && !SkillTriggerEvaluator.MatchesAll(triggers, caster,
                                opponent, units, opponents, encounter.ElapsedSeconds))
                            continue;

                        if (slot.Consumable.Strike is { } strike)
                        {
                            var manaCost = strike.ManaCostRatio * caster.Stats.MaxManapoints;
                            if (opponent is null || !opponent.IsAlive || caster.Manapoints < manaCost - 1e-9) continue;
                            var strikeElement = CombatTraitRules.ResolveDamageElement(caster.Stats.Traits, strike.Element);
                            // 物品打击非玩家自身技能，不吃技能增幅，但归属符箓时吃符箓伤害增幅（总纲 §7.7.1 / §20.1.3 / §20.2）。
                            var rawPower = DeterministicCombatEngine.ResolveConsumableStrikePower(slot.Item, strike, caster.Stats);
                            var strikeStats = caster.Stats with
                            {
                                AttackPower = rawPower,
                                DamageKind = CombatDamageKind.Spell,
                                Element = strikeElement,
                                TotalDamageBonusPoints = caster.Stats.TotalDamageBonusPoints
                                    + caster.BuffTotal(BuffEffectChannel.DamageBonusPoints)
                            };
                            var damage = DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, opponent);
                            (plannedStrikes ??= []).Add(new PlannedStrike(caster, opponent, runtime, slot, manaCost, strikeElement, rawPower, damage));
                            break;
                        }

                        var hpRestore = JsonRead.Number(slot.RawConsumable, "hitpointRestore");
                        var mpRestore = JsonRead.Number(slot.RawConsumable, "manapointRestore");
                        var hasBuffs = slot.RawConsumable.TryGetProperty("buffs", out var buffsEl)
                            && buffsEl.ValueKind == JsonValueKind.Array;
                        if (!hasBuffs && hpRestore == 0 && mpRestore == 0) continue;
                        (plannedPotions ??= []).Add(new PlannedPotion(caster, runtime, slot, hpRestore, mpRestore, hasBuffs, buffsEl));
                        break;
                    }
                }
            }
        }
    }

    private static CombatantState? PickConsumableTarget(CombatEncounterState encounter, CombatantState caster,
        IReadOnlyList<CombatantState> opponents, double strikeUseCount)
    {
        var sideTag = Systems.CombatActSystem.SymmetricUnitSide(encounter.Seed, caster.Side, caster.SideIndex);
        return DeterministicCombatEngine.SelectTarget(opponents, encounter.Seed, sideTag,
            caster.AttackCount + strikeUseCount, 24);
    }

    private static void SyncTrackedUnit(CombatantState unit,
        double[] trackedSideAHp, double[] trackedSideBHp, double[] trackedSideAMp, double[] trackedSideBMp)
    {
        if (unit.Side == CombatSide.SideA)
        {
            trackedSideAHp[unit.SideIndex] = unit.Hitpoints;
            trackedSideAMp[unit.SideIndex] = unit.Manapoints;
        }
        else
        {
            trackedSideBHp[unit.SideIndex] = unit.Hitpoints;
            trackedSideBMp[unit.SideIndex] = unit.Manapoints;
        }
    }

    private static void UpdateDuelOutcome(CombatEncounterState encounter)
    {
        encounter.Outcome = (DeterministicCombatEngine.AnyAlive(encounter.SideA), DeterministicCombatEngine.AnyAlive(encounter.SideB)) switch
        {
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }

    private static CombatOutcome FlipOutcome(CombatOutcome outcome) => outcome switch
    {
        CombatOutcome.PlayerWon => CombatOutcome.EnemyWon,
        CombatOutcome.EnemyWon => CombatOutcome.PlayerWon,
        var other => other
    };

    private static void EmitResourceSyncIfChanged(
        CombatEncounterState encounter,
        CombatSide initiatorCombatSide,
        List<DuelScriptEvent> events,
        double[] trackedSideAHp,
        double[] trackedSideBHp,
        double[] trackedSideAMp,
        double[] trackedSideBMp)
    {
        SyncSide(encounter.SideA, trackedSideAHp, trackedSideAMp);
        SyncSide(encounter.SideB, trackedSideBHp, trackedSideBMp);

        void SyncSide(IReadOnlyList<CombatantState> units, double[] trackedHp, double[] trackedMp)
        {
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (Math.Abs(unit.Hitpoints - trackedHp[i]) <= 1e-9 && Math.Abs(unit.Manapoints - trackedMp[i]) <= 1e-9)
                    continue;
                var healed = Math.Max(0, unit.Hitpoints - trackedHp[i]);
                trackedHp[i] = unit.Hitpoints;
                trackedMp[i] = unit.Manapoints;
                var isInitiator = unit.Side == initiatorCombatSide;
                events.Add(new DuelScriptEvent(
                    encounter.ElapsedSeconds,
                    isInitiator,
                    isInitiator,
                    0,
                    true,
                    false,
                    unit.Hitpoints,
                    null,
                    healed,
                    unit.Manapoints,
                    unit.Manapoints,
                    AttackerUnitIndex: unit.SideIndex,
                    TargetUnitIndex: unit.SideIndex));
            }
        }
    }

    private static void AppendCombatEvents(
        CombatEncounterState encounter,
        CombatSide initiatorCombatSide,
        IReadOnlyList<CombatEvent> combatEvents,
        List<DuelScriptEvent> events,
        double[] trackedSideAHp,
        double[] trackedSideBHp,
        double[] trackedSideAMp,
        double[] trackedSideBMp)
    {
        for (var i = 0; i < combatEvents.Count; i++)
        {
            var combatEvent = combatEvents[i];
            var attacker = encounter.FindUnit(combatEvent.AttackerId);
            var target = encounter.FindUnit(combatEvent.TargetId);
            if (attacker is null || target is null) continue;

            var attackerIsInitiator = attacker.Side == initiatorCombatSide;
            var targetIsInitiator = target.Side == initiatorCombatSide;

            var targetHpArr = target.Side == CombatSide.SideA ? trackedSideAHp : trackedSideBHp;
            var targetMpArr = target.Side == CombatSide.SideA ? trackedSideAMp : trackedSideBMp;
            var attackerMpArr = attacker.Side == CombatSide.SideA ? trackedSideAMp : trackedSideBMp;

            targetHpArr[target.SideIndex] = combatEvent.TargetHitpointsAfter;
            if (combatEvent.AttackerManapointsAfter is { } attackerMp)
                attackerMpArr[attacker.SideIndex] = attackerMp;
            if (combatEvent.TargetManapointsAfter is { } targetMp)
                targetMpArr[target.SideIndex] = targetMp;

            events.Add(new DuelScriptEvent(
                combatEvent.AtSeconds,
                attackerIsInitiator,
                targetIsInitiator,
                combatEvent.Damage,
                combatEvent.Hit,
                combatEvent.Critical,
                combatEvent.TargetHitpointsAfter,
                combatEvent.AbilityId,
                combatEvent.Healing,
                attackerMpArr[attacker.SideIndex],
                targetMpArr[target.SideIndex],
                combatEvent.Break,
                combatEvent.Parried,
                combatEvent.AbilityNameOverride,
                AttackerUnitIndex: attacker.SideIndex,
                TargetUnitIndex: target.SideIndex));
        }
    }

    private static IReadOnlyList<CombatUnitStatDocument>[] ProjectSideMemberStats(
        CombatUnitStatsBook statsBook,
        IReadOnlyList<CombatantState> sideUnits,
        IReadOnlyList<DuelCombatant> sideCombatants)
    {
        var rawEntries = new CombatUnitStatEntry?[sideUnits.Count];
        for (var i = 0; i < sideUnits.Count; i++)
            rawEntries[i] = statsBook.Entries.FirstOrDefault(entry => entry.UnitKey == sideUnits[i].Id);

        var perMember = new IReadOnlyList<CombatUnitStatDocument>[sideUnits.Count];
        for (var selfIdx = 0; selfIdx < sideUnits.Count; selfIdx++)
        {
            var docs = new List<CombatUnitStatDocument>(sideUnits.Count);
            for (var i = 0; i < sideUnits.Count; i++)
            {
                var entry = rawEntries[i];
                if (entry is null) continue;
                var isSelf = i == selfIdx;
                docs.Add(ToDocument(entry) with
                {
                    UnitKey = isSelf ? "player" : $"ally:{i}",
                    Kind = isSelf ? CombatStatUnitKind.Self : CombatStatUnitKind.Ally,
                    DisplayName = isSelf ? "" : sideCombatants[i].DisplayName
                });
            }
            perMember[selfIdx] = docs.ToArray();
        }
        return perMember;
    }

    private static DuelBattleRecordDocument[] BuildSideMemberRecords(
        DuelActionRecorder recorder,
        CombatOutcome sideOutcome,
        double duration,
        CombatSide side,
        int memberCount)
    {
        var records = new DuelBattleRecordDocument[memberCount];
        for (var i = 0; i < memberCount; i++)
            records[i] = recorder.BuildRecord(sideOutcome, duration, side, viewerMemberIndex: i);
        return records;
    }

    private static IReadOnlyList<DuelStagedItem> CollectSideStagedItems(IReadOnlyList<DuelCombatant> combatants)
    {
        if (combatants.Count == 1) return combatants[0].StagedItems;
        var merged = new List<DuelStagedItem>();
        for (var i = 0; i < combatants.Count; i++)
            merged.AddRange(combatants[i].StagedItems);
        return merged;
    }

    private static CombatUnitStatDocument ToDocument(CombatUnitStatEntry unit) => new(
        unit.UnitKey, unit.Kind, unit.DisplayName,
        unit.DamageDealtPhysical, unit.DamageDealtSpell,
        unit.DamageTakenPhysical, unit.DamageTakenSpell,
        unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
        unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue);
}
