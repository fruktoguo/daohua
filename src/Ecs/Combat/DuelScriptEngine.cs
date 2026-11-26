using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;

namespace DaoHua.Ecs;

/// <summary>
/// 斗法剧本推演器：撮合成功后把双方镜像（含属性、功法、已启用战斗消耗品与库存）一次性确定性推演至终结，
/// 产出双方共享的 DuelScript；战斗期间任何配置变更不影响结果。
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
    }

    /// <summary>推演一场斗法；initiator 固定映射为剧本中的 player 视角。</summary>
    public static DuelScript Resolve(
        DuelCombatant initiator,
        DuelCombatant defender,
        long seed,
        DuelSettings settings,
        GameDataCatalog? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(defender);
        ArgumentNullException.ThrowIfNull(settings);
        var encounter = CombatEncounterState.Create("duel", seed,
            new CombatantTemplate(initiator.Stats, initiator.Skills),
            [new CombatantTemplate(defender.Stats, defender.Skills)]);
        // 撮合推演期间挂接一份私有统计簿与紧凑行动录制器：
        // 同一条战斗结算管线顺路产出双方各自的单位统计与斗法行动记录。
        var statsBook = new CombatUnitStatsBook();
        encounter.StatsBook = statsBook;
        statsBook.Register("player", "");
        statsBook.Register("enemy:0", "");
        var recorder = new DuelActionRecorder(initiator, defender);
        encounter.ActionRecorder = recorder;
        RecordBattleStartTraits(recorder, encounter.Player);
        RecordBattleStartTraits(recorder, encounter.Enemies[0]);

        if (initiator.ExternalBuffs is { Count: > 0 } initExternal)
        {
            encounter.Player.ExternalBuffStacks = buffId => Systems.ActionExecutionRules.SumUnmergedActiveBuffStacks(
                encounter.Player, initExternal, buffId, initiator.SnapshotAtUnixSeconds + encounter.ElapsedSeconds);
        }
        if (defender.ExternalBuffs is { Count: > 0 } defExternal)
        {
            encounter.Enemies[0].ExternalBuffStacks = buffId => Systems.ActionExecutionRules.SumUnmergedActiveBuffStacks(
                encounter.Enemies[0], defExternal, buffId, defender.SnapshotAtUnixSeconds + encounter.ElapsedSeconds);
        }

        var initConsumables = BuildConsumableRuntime(initiator, gameData);
        var defConsumables = BuildConsumableRuntime(defender, gameData);
        var hasConsumables = initConsumables is not null || defConsumables is not null;

        if (initConsumables is not null) InjectActiveSustainedBuffs(encounter.Player, initConsumables, 0, recorder);
        if (defConsumables is not null) InjectActiveSustainedBuffs(encounter.Enemies[0], defConsumables, 0, recorder);

        var events = new List<DuelScriptEvent>();
        var initiatorHp = initiator.Stats.MaxHitpoints;
        var defenderHp = defender.Stats.MaxHitpoints;
        var initiatorMp = initiator.Stats.MaxManapoints;
        var defenderMp = defender.Stats.MaxManapoints;

        // 与 PVE PrepareCombatEntry 同序：开打前先对空持续槽补能并注入阵盘 Buff，再决策双方首次出手。
        if (initConsumables is not null)
            EvaluateSideConsumables(encounter, true, initConsumables, evaluateSustained: true, evaluateInstant: false,
                statsBook, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
        if (defConsumables is not null)
            EvaluateSideConsumables(encounter, false, defConsumables, evaluateSustained: true, evaluateInstant: false,
                statsBook, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);

        Systems.CombatActSystem.DecideInitialActions(encounter);

        // 与 PVE AdvanceCombatEncounter 同序：开打首帧复查即时消耗品（雷击符抢先手 / 战斗丹药等）。
        if (hasConsumables)
        {
            EvaluateBothSidesConsumables(encounter, initConsumables, defConsumables, statsBook, events,
                ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
        }

        if (!hasConsumables)
        {
            var result = Systems.CombatEventAdvanceSystem.AdvanceTo(encounter, settings.MaxDuelSeconds);
            AppendCombatEvents(result.Events, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
            EmitResourceSyncIfChanged(encounter, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
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
                    NextConsumableWake(encounter.ElapsedSeconds, initConsumables, defConsumables, initiator, defender));
                var step = Systems.CombatEventAdvanceSystem.AdvanceOneBoundary(encounter, stepTarget);
                AppendCombatEvents(step.Events, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
                EmitResourceSyncIfChanged(encounter, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
                if (encounter.Outcome != CombatOutcome.Running) break;
                ExpireSustainedSlots(initConsumables, encounter.ElapsedSeconds);
                ExpireSustainedSlots(defConsumables, encounter.ElapsedSeconds);
                EvaluateBothSidesConsumables(encounter, initConsumables, defConsumables, statsBook, events,
                    ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
            }
        }

        var outcome = encounter.Outcome == CombatOutcome.Running ? CombatOutcome.Draw : encounter.Outcome;
        var defenderOutcome = outcome switch
        {
            CombatOutcome.PlayerWon => CombatOutcome.EnemyWon,
            CombatOutcome.EnemyWon => CombatOutcome.PlayerWon,
            var other => other
        };
        var duration = Math.Max(encounter.ElapsedSeconds, 1e-9);
        var initiatorStats = statsBook.Entries.Where(unit => unit.UnitKey == "player")
            .Select(ToDocument).ToArray();
        var defenderStats = statsBook.Entries.Where(unit => unit.UnitKey == "enemy:0")
            .Select(unit => ToDocument(unit) with { UnitKey = "player", Kind = CombatStatUnitKind.Self })
            .ToArray();
        return new DuelScript(outcome, duration, events,
            initiator.StagedItems, defender.StagedItems, initiatorStats, defenderStats,
            recorder.BuildRecord(outcome, duration, viewerIsInitiator: true),
            recorder.BuildRecord(defenderOutcome, duration, viewerIsInitiator: false));
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

    private static void InjectActiveSustainedBuffs(CombatantState unit, DuelConsumableRuntime runtime, double elapsed,
        ICombatActionRecorder? recorder = null)
    {
        foreach (var slot in runtime.SustainedSlots)
        {
            if (slot.SustainedExpiresAtSeconds <= elapsed + 1e-9) continue;
            var actionKind = string.Equals(slot.Item.SubCategory, AutoUseBar.Formation, StringComparison.Ordinal)
                ? DuelRecordActionKind.Formation
                : DuelRecordActionKind.Item;
            recorder?.BeginAction(elapsed, unit, actionKind, slot.Item.Id.Value);
            try
            {
                InjectSustainedSlotBuffs(unit, slot, slot.SustainedExpiresAtSeconds, recorder);
            }
            finally
            {
                recorder?.EndAction();
            }
        }
    }

    private static void InjectSustainedSlotBuffs(CombatantState unit, DuelSlotRuntime slot, double expiresAtSeconds,
        ICombatActionRecorder? recorder = null)
    {
        if (!slot.RawConsumable.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var buff in buffs.EnumerateArray())
        {
            var buffId = buff.GetProperty("buffId").GetString()!;
            var effects = BuffEffectParser.Parse(buff, 1.0);
            unit.SetBuff(new CombatBuffState(buffId, expiresAtSeconds, 1,
                JsonRead.Text(buff, "name") ?? buffId, ResetOnWave: true, Effects: effects));
            recorder?.RecordBuff(unit, unit, buffId, 1);
        }
    }

    private static void ExpireSustainedSlots(DuelConsumableRuntime? runtime, double elapsed)
    {
        if (runtime is null) return;
        foreach (var slot in runtime.SustainedSlots)
            if (slot.SustainedExpiresAtSeconds > 0 && slot.SustainedExpiresAtSeconds <= elapsed + 1e-9)
                slot.SustainedExpiresAtSeconds = 0;
    }

    private static double NextConsumableWake(double elapsed,
        DuelConsumableRuntime? initRuntime, DuelConsumableRuntime? defRuntime,
        DuelCombatant initiator, DuelCombatant defender)
    {
        var next = double.PositiveInfinity;
        ScanRuntime(initRuntime);
        ScanRuntime(defRuntime);
        ScanExternal(initiator.ExternalBuffs, initiator.SnapshotAtUnixSeconds);
        ScanExternal(defender.ExternalBuffs, defender.SnapshotAtUnixSeconds);
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

    private static void EvaluateBothSidesConsumables(
        CombatEncounterState encounter,
        DuelConsumableRuntime? initRuntime,
        DuelConsumableRuntime? defRuntime,
        CombatUnitStatsBook statsBook,
        List<DuelScriptEvent> events,
        ref double initiatorHp,
        ref double defenderHp,
        ref double initiatorMp,
        ref double defenderMp)
    {
        if (initRuntime is not null && encounter.Outcome == CombatOutcome.Running)
            EvaluateSideConsumables(encounter, true, initRuntime, evaluateSustained: true, evaluateInstant: true,
                statsBook, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
        if (defRuntime is not null && encounter.Outcome == CombatOutcome.Running)
            EvaluateSideConsumables(encounter, false, defRuntime, evaluateSustained: true, evaluateInstant: true,
                statsBook, events, ref initiatorHp, ref defenderHp, ref initiatorMp, ref defenderMp);
    }

    private static void EvaluateSideConsumables(
        CombatEncounterState encounter,
        bool isInitiator,
        DuelConsumableRuntime runtime,
        bool evaluateSustained,
        bool evaluateInstant,
        CombatUnitStatsBook statsBook,
        List<DuelScriptEvent> events,
        ref double initiatorHp,
        ref double defenderHp,
        ref double initiatorMp,
        ref double defenderMp)
    {
        var caster = isInitiator ? encounter.Player : encounter.Enemies[0];
        var opponent = isInitiator ? encounter.Enemies[0] : encounter.Player;
        IReadOnlyList<CombatantState> allies = isInitiator ? encounter.PlayerAllies : encounter.Enemies;
        IReadOnlyList<CombatantState> opponents = isInitiator ? encounter.Enemies : encounter.PlayerAllies;

        for (var pass = 0; pass < 256; pass++)
        {
            if (!caster.IsAlive || encounter.Outcome != CombatOutcome.Running) break;
            var activated = false;

            if (evaluateSustained)
            {
                for (var i = 0; i < runtime.SustainedSlots.Count; i++)
                {
                    var slot = runtime.SustainedSlots[i];
                    if (slot.SustainedExpiresAtSeconds > encounter.ElapsedSeconds + 1e-9) continue;
                    var itemId = slot.Item.Id.Value;
                    if (runtime.Balances.GetValueOrDefault(itemId) < 1d) continue;
                    var triggers = ConsumableTriggerResolver.Resolve(itemId, slot.Consumable, runtime.Triggers);
                    if (triggers.Count > 0 && !SkillTriggerEvaluator.MatchesAll(triggers, caster,
                            opponent.IsAlive ? opponent : null, allies, opponents, encounter.ElapsedSeconds))
                        continue;

                    runtime.Balances[itemId] = runtime.Balances.GetValueOrDefault(itemId) - 1d;
                    slot.SustainedExpiresAtSeconds = encounter.ElapsedSeconds + slot.Consumable.DurationSeconds;
                    var sustainedActionKind = string.Equals(slot.Item.SubCategory, AutoUseBar.Formation, StringComparison.Ordinal)
                        ? DuelRecordActionKind.Formation
                        : DuelRecordActionKind.Item;
                    encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, caster, sustainedActionKind, itemId);
                    try
                    {
                        InjectSustainedSlotBuffs(caster, slot, slot.SustainedExpiresAtSeconds, encounter.ActionRecorder);
                    }
                    finally
                    {
                        encounter.ActionRecorder?.EndAction();
                    }
                    events.Add(new DuelScriptEvent(
                        encounter.ElapsedSeconds,
                        isInitiator,
                        isInitiator,
                        0,
                        true,
                        false,
                        caster.Hitpoints,
                        itemId,
                        0,
                        caster.Manapoints,
                        caster.Manapoints,
                        AbilityNameOverride: DuelScriptEvent.SustainedActivateMarker));
                    activated = true;
                }
            }

            if (evaluateInstant)
            {
                for (var i = 0; i < runtime.InstantSlots.Count; i++)
                {
                    var slot = runtime.InstantSlots[i];
                    if (!caster.IsAlive || encounter.Outcome != CombatOutcome.Running) break;
                    if (slot.AvailableAtSeconds > encounter.ElapsedSeconds + 1e-9) continue;
                    var itemId = slot.Item.Id.Value;
                    if (runtime.Balances.GetValueOrDefault(itemId) < 1d) continue;
                    var triggers = ConsumableTriggerResolver.Resolve(itemId, slot.Consumable, runtime.Triggers);
                    if (triggers.Count > 0 && !SkillTriggerEvaluator.MatchesAll(triggers, caster,
                            opponent.IsAlive ? opponent : null, allies, opponents, encounter.ElapsedSeconds))
                        continue;

                    if (slot.Consumable.Strike is { } strike)
                    {
                        var manaCost = strike.ManaCostRatio * caster.Stats.MaxManapoints;
                        if (!opponent.IsAlive || caster.Manapoints < manaCost - 1e-9) continue;
                        var strikeElement = CombatTraitRules.ResolveDamageElement(caster.Stats.Traits, strike.Element);
                        // 物品打击非玩家自身技能，不吃技能增幅，但归属符箓时吃符箓伤害增幅（总纲 §7.7.1 / §20.1.3 / §20.2）。
                        var strikeStats = caster.Stats with
                        {
                            AttackPower = DeterministicCombatEngine.ResolveConsumableStrikePower(slot.Item, strike, caster.Stats),
                            DamageKind = CombatDamageKind.Spell,
                            Element = strikeElement
                        };
                        strikeStats = strikeStats with
                        {
                            TotalDamageBonusPoints = caster.Stats.TotalDamageBonusPoints
                                + caster.BuffTotal(BuffEffectChannel.DamageBonusPoints)
                        };
                        var damage = DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, opponent);
                        var wasOpponentAlive = opponent.IsAlive;
                        caster.Manapoints = Math.Max(0, caster.Manapoints - manaCost);
                        var settled = CombatDamageSettlement.Apply(encounter, caster, opponent, damage);
                        damage = settled.Damage;
                        if (encounter.ActionRecorder is { } strikeRecorder)
                        {
                            strikeRecorder.BeginAction(encounter.ElapsedSeconds, caster, DuelRecordActionKind.Item, itemId);
                            try
                            {
                                strikeRecorder.RecordDamage(caster, opponent, DuelRecordEffectKind.Damage,
                                    CombatDamageKind.Spell, strikeElement, DuelRecordHitMark.Hit,
                                    damage, itemId, targetDowned: wasOpponentAlive && !opponent.IsAlive);
                                CombatTraitReactions.ApplyRanhun(caster, opponent, strikeElement, settled.Actual, encounter.ElapsedSeconds, encounter);
                            }
                            finally
                            {
                                strikeRecorder.EndAction();
                            }
                        }
                        else
                        {
                            CombatTraitReactions.ApplyRanhun(caster, opponent, strikeElement, settled.Actual, encounter.ElapsedSeconds);
                        }
                        statsBook.RecordHit(caster.Id, opponent.Id, CombatDamageKind.Spell,
                            damage, Math.Max(0, strikeStats.AttackPower - damage));

                        runtime.Balances[itemId] = runtime.Balances.GetValueOrDefault(itemId) - 1d;
                        slot.AvailableAtSeconds = encounter.ElapsedSeconds + slot.Consumable.CooldownSeconds;
                        if (isInitiator)
                        {
                            initiatorMp = caster.Manapoints;
                            defenderHp = opponent.Hitpoints;
                            defenderMp = opponent.Manapoints;
                        }
                        else
                        {
                            defenderMp = caster.Manapoints;
                            initiatorHp = opponent.Hitpoints;
                            initiatorMp = opponent.Manapoints;
                        }
                        events.Add(new DuelScriptEvent(
                            encounter.ElapsedSeconds,
                            isInitiator,
                            !isInitiator,
                            damage,
                            true,
                            false,
                            opponent.Hitpoints,
                            itemId,
                            0,
                            caster.Manapoints,
                            opponent.Manapoints));
                        UpdateDuelOutcome(encounter);
                        activated = true;
                        continue;
                    }

                    var hpRestore = JsonRead.Number(slot.RawConsumable, "hitpointRestore");
                    var mpRestore = JsonRead.Number(slot.RawConsumable, "manapointRestore");
                    var hasBuffs = slot.RawConsumable.TryGetProperty("buffs", out var buffsEl)
                        && buffsEl.ValueKind == JsonValueKind.Array;
                    if (!hasBuffs && hpRestore == 0 && mpRestore == 0) continue;

                    var hpBefore = caster.Hitpoints;
                    var mpBefore = caster.Manapoints;
                    caster.Hitpoints = Math.Min(caster.Stats.MaxHitpoints, caster.Hitpoints + hpRestore);
                    caster.Manapoints = Math.Min(caster.Stats.MaxManapoints, caster.Manapoints + mpRestore);
                    var healed = caster.Hitpoints - hpBefore;
                    var mpRestored = caster.Manapoints - mpBefore;
                    if (healed > 0)
                        statsBook.RecordHealing(caster.Id, "", autoRegen: false, healed);
                    encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, caster, DuelRecordActionKind.Item, itemId);
                    try
                    {
                        if (healed > 0) encounter.ActionRecorder?.RecordHeal(caster, caster, DuelRecordEffectKind.Heal, healed, itemId);
                        if (mpRestored > 0) encounter.ActionRecorder?.RecordMana(caster, caster, DuelRecordEffectKind.ManaRestore, mpRestored, itemId);
                        if (hasBuffs)
                        {
                            foreach (var buff in buffsEl.EnumerateArray())
                            {
                                var buffId = buff.GetProperty("buffId").GetString()!;
                                var duration = JsonRead.Number(buff, "durationSeconds");
                                if (duration <= 0) continue;
                                var effects = BuffEffectParser.Parse(buff, 1.0);
                                caster.SetBuff(new CombatBuffState(buffId, encounter.ElapsedSeconds + duration, 1,
                                    JsonRead.Text(buff, "name") ?? buffId, ResetOnWave: true, Effects: effects));
                                encounter.ActionRecorder?.RecordBuff(caster, caster, buffId, 1);
                            }
                        }
                    }
                    finally
                    {
                        encounter.ActionRecorder?.EndAction();
                    }

                    runtime.Balances[itemId] = runtime.Balances.GetValueOrDefault(itemId) - 1d;
                    slot.AvailableAtSeconds = encounter.ElapsedSeconds + slot.Consumable.CooldownSeconds;
                    if (isInitiator)
                    {
                        initiatorHp = caster.Hitpoints;
                        initiatorMp = caster.Manapoints;
                    }
                    else
                    {
                        defenderHp = caster.Hitpoints;
                        defenderMp = caster.Manapoints;
                    }
                    events.Add(new DuelScriptEvent(
                        encounter.ElapsedSeconds,
                        isInitiator,
                        isInitiator,
                        0,
                        true,
                        false,
                        caster.Hitpoints,
                        itemId,
                        healed,
                        caster.Manapoints,
                        caster.Manapoints));
                    activated = true;
                }
            }

            if (!activated) break;
        }
    }

    private static void UpdateDuelOutcome(CombatEncounterState encounter)
    {
        encounter.Outcome = (encounter.Player.IsAlive, encounter.Enemies[0].IsAlive) switch
        {
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }

    private static void EmitResourceSyncIfChanged(
        CombatEncounterState encounter,
        List<DuelScriptEvent> events,
        ref double initiatorHp,
        ref double defenderHp,
        ref double initiatorMp,
        ref double defenderMp)
    {
        var init = encounter.Player;
        if (Math.Abs(init.Hitpoints - initiatorHp) > 1e-9 || Math.Abs(init.Manapoints - initiatorMp) > 1e-9)
        {
            var healed = Math.Max(0, init.Hitpoints - initiatorHp);
            initiatorHp = init.Hitpoints;
            initiatorMp = init.Manapoints;
            events.Add(new DuelScriptEvent(encounter.ElapsedSeconds, true, true, 0, true, false,
                init.Hitpoints, null, healed, init.Manapoints, init.Manapoints));
        }

        var def = encounter.Enemies[0];
        if (Math.Abs(def.Hitpoints - defenderHp) > 1e-9 || Math.Abs(def.Manapoints - defenderMp) > 1e-9)
        {
            var healed = Math.Max(0, def.Hitpoints - defenderHp);
            defenderHp = def.Hitpoints;
            defenderMp = def.Manapoints;
            events.Add(new DuelScriptEvent(encounter.ElapsedSeconds, false, false, 0, true, false,
                def.Hitpoints, null, healed, def.Manapoints, def.Manapoints));
        }
    }

    private static void AppendCombatEvents(
        IReadOnlyList<CombatEvent> combatEvents,
        List<DuelScriptEvent> events,
        ref double initiatorHp,
        ref double defenderHp,
        ref double initiatorMp,
        ref double defenderMp)
    {
        foreach (var combatEvent in combatEvents)
        {
            var attackerIsInitiator = combatEvent.AttackerId == "player";
            var targetIsInitiator = combatEvent.TargetId == "player";
            if (targetIsInitiator) initiatorHp = combatEvent.TargetHitpointsAfter;
            else defenderHp = combatEvent.TargetHitpointsAfter;
            if (combatEvent.AttackerManapointsAfter is { } attackerMp)
            {
                if (attackerIsInitiator) initiatorMp = attackerMp; else defenderMp = attackerMp;
            }
            if (combatEvent.TargetManapointsAfter is { } targetMp)
            {
                if (targetIsInitiator) initiatorMp = targetMp; else defenderMp = targetMp;
            }
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
                attackerIsInitiator ? initiatorMp : defenderMp,
                targetIsInitiator ? initiatorMp : defenderMp,
                combatEvent.Break,
                combatEvent.Parried,
                combatEvent.AbilityNameOverride));
        }
    }

    private static CombatUnitStatDocument ToDocument(CombatUnitStatEntry unit) => new(
        unit.UnitKey, unit.Kind, unit.DisplayName,
        unit.DamageDealtPhysical, unit.DamageDealtSpell,
        unit.DamageTakenPhysical, unit.DamageTakenSpell,
        unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
        unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue);
}
