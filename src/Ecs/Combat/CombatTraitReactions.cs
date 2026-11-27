using Domain.Combat;
using Domain.Common;

namespace DaoHua.Ecs;

/// <summary>特质命中与周期反应的共享规则，实体和内联路径只负责薄派发。</summary>
internal static class CombatTraitReactions
{
    /// <summary>直接命中落账后先施加燃魂，再判定蓄意释放；衍生伤害不重新派发命中。</summary>
    internal static void OnHit(CombatHitContext context)
    {
        if (!context.Hit || !context.Attacker.IsAlive) return;
        var wave = context.Encounter.ActiveWave as SimultaneousCombatWave;
        ApplyRanhun(context.Attacker, context.Target, context.AttackStats.Element, context.ActualDamage, context.AtSeconds, context.Encounter);
        var attacker = context.Attacker;
        if (!CombatTraitRules.ContainsTrait(attacker.Stats.Traits, CombatTraitRules.XuyiTraitId)) return;
        var buff = attacker.Buffs.FirstOrDefault(current => current.BuffId == CombatTraitRules.XuyiBuffId);
        if (buff is null || buff.Stacks <= attacker.Stats.MaxHitpoints) return;
        if (wave is not null)
        {
            if (!wave.TryConsumeXuyi(attacker)) return;
            wave.DeferBuffOp(() => attacker.RemoveBuffsWhere(current => current.BuffId == CombatTraitRules.XuyiBuffId));
        }
        else
        {
            attacker.RemoveBuffsWhere(current => current.BuffId == CombatTraitRules.XuyiBuffId);
        }
        var opponents = context.Encounter.OpponentsOf(attacker);
        // 主目标先结算，随后按阵容序溅射；消费先于落账，且爆发不重新积蓄意。
        if (CombatDamageSettlement.IsTargetAlive(context.Encounter, context.Target)) Burst(context.Target, true);
        for (var i = 0; i < opponents.Count; i++)
        {
            var target = opponents[i];
            if (CombatDamageSettlement.IsTargetAlive(context.Encounter, target) && !ReferenceEquals(target, context.Target))
                Burst(target, false);
        }

        void Burst(CombatantState target, bool primary)
        {
            var wasAlive = CombatDamageSettlement.IsTargetAlive(context.Encounter, target);
            var strikeElement = primary ? CombatElement.None : CombatTraitRules.ResolveDamageElement(attacker.Stats.Traits, CombatElement.None);
            var damage = primary ? buff.Stacks : DeterministicCombatEngine.ResolveStrikeDamage(
                CombatDamageSettlement.StrikeStats(attacker, buff.Stacks, CombatDamageKind.Physical, strikeElement),
                target);
            var settled = CombatDamageSettlement.Apply(context.Encounter, attacker, target, damage, accumulateXuyi: false);
            if (wave is not null) wave.AddThreatDelta(attacker, settled.Damage);
            else attacker.Threat += settled.Damage;
            context.Encounter.StatsBook?.RecordHit(attacker, target, primary ? CombatDamageKind.True : CombatDamageKind.Physical, settled.Damage, primary ? 0 : Math.Max(0, buff.Stacks - settled.Damage));
            context.Encounter.ActionRecorder?.RecordDamage(attacker, target, DuelRecordEffectKind.TraitBurst,
                primary ? CombatDamageKind.True : CombatDamageKind.Physical,
                strikeElement, DuelRecordHitMark.Hit, settled.Damage, CombatTraitRules.XuyiTraitId,
                targetDowned: wasAlive && !CombatDamageSettlement.IsTargetAlive(context.Encounter, target));
            context.Emit(new CombatEvent(context.AtSeconds, attacker.Id, target.Id, settled.Damage, true,
                primary ? CombatDamageKind.True : CombatDamageKind.Physical, target.Hitpoints) { AbilityId = CombatTraitRules.XuyiTraitId });
        }
    }

    /// <summary>向目标直接追加指定层数的燃烧状态，并按保高不降原则刷新到期时间与伤害归属。</summary>
    internal static void AddRanhunStacks(CombatantState attacker, CombatantState target, double stacks, double at,
        CombatEncounterState? encounter = null)
    {
        if (stacks <= 0 || !CombatDamageSettlement.IsTargetAlive(encounter, target)) return;
        if (encounter?.ActiveWave is SimultaneousCombatWave wave)
        {
            var previewTotal = wave.AddPreviewRanhunStacks(target, stacks);
            wave.DeferBuffOp(() =>
            {
                if (!target.IsAlive) return;
                var existing = target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.RanhunBuffId);
                var exp = CombatTraitRules.MergeBuffExpiry(existing?.ExpiresAtSeconds, at + 5);
                target.SetBuff(new CombatBuffState(CombatTraitRules.RanhunBuffId, exp, stacks + (existing?.Stacks ?? 0),
                    "燃烧", ResetOnWave: true, Effects: [new(BuffEffectChannel.FireSpellDamagePerSecond, 1)], SourceUnitId: attacker.Id));
            });
            encounter.ActionRecorder?.RecordBuff(attacker, target, CombatTraitRules.RanhunBuffId, previewTotal,
                DuelRecordActionKind.Trait, at, CombatTraitRules.RanhunTraitId);
            return;
        }
        var existingNow = target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.RanhunBuffId);
        var totalNow = stacks + (existingNow?.Stacks ?? 0);
        var expiresAtNow = CombatTraitRules.MergeBuffExpiry(existingNow?.ExpiresAtSeconds, at + 5);
        target.SetBuff(new CombatBuffState(CombatTraitRules.RanhunBuffId, expiresAtNow, totalNow,
            "燃烧", ResetOnWave: true, Effects: [new(BuffEffectChannel.FireSpellDamagePerSecond, 1)], SourceUnitId: attacker.Id));
        encounter?.ActionRecorder?.RecordBuff(attacker, target, CombatTraitRules.RanhunBuffId, totalNow,
            DuelRecordActionKind.Trait, at, CombatTraitRules.RanhunTraitId);
    }

    /// <summary>按实际扣血叠层，最后施加者取得同一燃烧状态的伤害归属。</summary>
    internal static void ApplyRanhun(CombatantState attacker, CombatantState target, CombatElement element, double actual, double at,
        CombatEncounterState? encounter = null)
    {
        if (actual <= 0 || !CombatDamageSettlement.IsTargetAlive(encounter, target) || element != CombatElement.Fire
            || !CombatTraitRules.ContainsTrait(attacker.Stats.Traits, CombatTraitRules.RanhunTraitId)) return;
        var stacks = Math.Max(1, Math.Floor(actual * 0.02 * CombatTraitRules.LevelScale(attacker, CombatTraitRules.RanhunTraitId)));
        AddRanhunStacks(attacker, target, stacks, at, encounter);
    }

    /// <summary>开战第 0 秒特质反应统一执行（血气化盾等依赖真实入场生命值的机制）；恢复快照不重复触发。</summary>
    internal static void ApplyBattleStartReactions(CombatEncounterState encounter)
    {
        if (encounter.BattleStartReactionsApplied || encounter.ElapsedSeconds > 1e-12) return;
        encounter.BattleStartReactionsApplied = true;
        for (var i = 0; i < encounter.PlayerAllies.Count; i++)
            ApplyXueqihuadun(encounter, encounter.PlayerAllies[i], 0);
        for (var i = 0; i < encounter.Enemies.Count; i++)
            ApplyXueqihuadun(encounter, encounter.Enemies[i], 0);
    }

    /// <summary>血气化盾：将超出 20% 最大生命的气血作为绕过自身护盾的受击伤害扣除（触发蓄意与受击涨威胁），转化为战内永久护盾。</summary>
    internal static double ApplyXueqihuadun(CombatEncounterState? encounter, CombatantState unit, double at)
    {
        if (!unit.IsAlive || !CombatTraitRules.ContainsTrait(unit.Stats.Traits, CombatTraitRules.XueqihuadunTraitId))
            return 0;
        var threshold = unit.Stats.MaxHitpoints * 0.20;
        if (unit.Hitpoints <= threshold + 1e-9) return 0;
        var excess = unit.Hitpoints - threshold;
        var settled = CombatDamageSettlement.Apply(encounter, unit, unit, excess, accumulateXuyi: true, bypassShield: true);
        if (settled.Actual <= 0 || !unit.IsAlive) return 0;
        var threatOnTaken = unit.Stats.Effects.ThreatOnTakenPoints;
        if (threatOnTaken > 0)
            unit.Threat += excess * threatOnTaken / 100.0;
        var shieldGain = settled.Actual * CombatTraitRules.XueqihuadunConversionRatio(unit.Stats.Traits, unit.Stats.TraitLevels);
        unit.Shield += shieldGain;
        if (encounter?.ActionRecorder is { } recorder)
        {
            recorder.BeginAction(at, unit, DuelRecordActionKind.Trait, CombatTraitRules.XueqihuadunTraitId);
            try
            {
                recorder.RecordUpkeep(unit, settled.Actual, CombatTraitRules.XueqihuadunTraitId);
            }
            finally
            {
                recorder.EndAction();
            }
        }
        return shieldGain;
    }

    /// <summary>波次战斗终结收尾：将存活血气化盾持有者的剩余护盾按治疗增幅转化为生命恢复，并清空全场战内护盾。</summary>
    internal static void SettleWaveEndShields(CombatEncounterState encounter, ref List<CombatEvent>? events)
    {
        if (encounter.WaveEndShieldsSettled || encounter.Outcome == CombatOutcome.Running) return;
        encounter.WaveEndShieldsSettled = true;
        SettleSide(encounter, encounter.PlayerAllies, ref events);
        SettleSide(encounter, encounter.Enemies, ref events);

        static void SettleSide(CombatEncounterState enc, IReadOnlyList<CombatantState> units, ref List<CombatEvent>? ev)
        {
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                var remainingShield = unit.Shield;
                unit.Shield = 0;
                if (remainingShield <= 1e-9 || !unit.IsAlive
                    || !CombatTraitRules.ContainsTrait(unit.Stats.Traits, CombatTraitRules.XueqihuadunTraitId))
                    continue;
                var rawHeal = remainingShield * PercentValue.Multiplier(unit.EffectiveStats.Effects.HealingBonusPoints, PercentCurveMode.Linear);
                var healed = Math.Min(unit.Stats.MaxHitpoints - unit.Hitpoints, Math.Max(0, rawHeal));
                if (healed <= 1e-9) continue;
                unit.Hitpoints += healed;
                unit.SyncDerivedStacks();
                enc.StatsBook?.RecordHealing(unit, autoRegen: false, healed);
                if (enc.ActionRecorder is { } recorder)
                {
                    recorder.BeginAction(enc.ElapsedSeconds, unit, DuelRecordActionKind.Trait, CombatTraitRules.XueqihuadunTraitId);
                    try
                    {
                        recorder.RecordHeal(unit, unit, DuelRecordEffectKind.Heal, healed, CombatTraitRules.XueqihuadunTraitId);
                    }
                    finally
                    {
                        recorder.EndAction();
                    }
                }
                (ev ??= []).Add(new CombatEvent(enc.ElapsedSeconds, unit.Id, unit.Id, 0, true, CombatDamageKind.Physical, unit.Hitpoints)
                {
                    AbilityId = CombatTraitRules.XueqihuadunTraitId,
                    Healing = healed,
                    AttackerManapointsAfter = unit.Manapoints,
                    TargetManapointsAfter = unit.Manapoints,
                    AttackerShieldAfter = 0,
                    TargetShieldAfter = 0
                });
            }
        }
    }

    /// <summary>整秒自然边界结算火属性术法持续伤害与养剑术蓄意，不回流命中触发器。</summary>
    internal static void Tick(CombatEncounterState encounter, CombatantState target, double at, ref List<CombatEvent>? events)
    {
        if (!target.IsAlive) return;
        foreach (var buff in target.Buffs)
        {
            var power = buff.EffectValue(BuffEffectChannel.FireSpellDamagePerSecond) * buff.Stacks;
            if (power <= 0 || buff.ExpiresAtSeconds + 1e-9 < at) continue;
            var source = encounter.FindUnit(buff.SourceUnitId)
                ?? throw new DomainException(DomainErrorCode.InvariantViolation, "持续伤害缺少战斗来源。");
            var wasAlive = target.IsAlive;
            var attack = CombatDamageSettlement.StrikeStats(source, power, CombatDamageKind.Spell, CombatElement.Fire);
            var damage = DeterministicCombatEngine.ResolveStrikeDamage(attack, target);
            var settled = CombatDamageSettlement.Apply(encounter, source, target, damage);
            source.Threat += settled.Damage;
            encounter.StatsBook?.RecordHit(source, target, CombatDamageKind.Spell, settled.Damage, Math.Max(0, power - settled.Damage));
            if (encounter.ActionRecorder is { } recorder)
            {
                recorder.BeginAction(at, source, DuelRecordActionKind.Trait, CombatTraitRules.RanhunTraitId);
                try
                {
                    recorder.RecordDamage(source, target, DuelRecordEffectKind.Damage,
                        CombatDamageKind.Spell, CombatElement.Fire, DuelRecordHitMark.Hit,
                        settled.Damage, CombatTraitRules.RanhunTraitId, targetDowned: wasAlive && !target.IsAlive);
                }
                finally
                {
                    recorder.EndAction();
                }
            }
            (events ??= []).Add(new CombatEvent(at, source.Id, target.Id, settled.Damage, true, CombatDamageKind.Spell, target.Hitpoints)
            {
                AbilityId = CombatTraitRules.RanhunTraitId,
                AttackerManapointsAfter = source.Manapoints,
                TargetManapointsAfter = target.Manapoints,
                AttackerShieldAfter = source.Shield,
                TargetShieldAfter = target.Shield
            });
            // 积蓄意可能修改目标 Buff 列表；燃魂当前仅一条，收尾后跳出循环避免枚举失效。
            break;
        }
        if (at > 1e-9 && target.IsAlive
            && CombatTraitRules.ContainsTrait(target.Stats.Traits, CombatTraitRules.YangjianshuTraitId)
            && (target.LastDirectDamageAtSeconds < 0 || at - target.LastDirectDamageAtSeconds >= 1.0 - 1e-9))
        {
            var scale = CombatTraitRules.LevelScale(target, CombatTraitRules.YangjianshuTraitId);
            var existing = target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.JianyiBuffId);
            var nextStacks = (existing?.Stacks ?? 0) + 1;
            var expiresAt = CombatTraitRules.MergeBuffExpiry(existing?.ExpiresAtSeconds, at + 10.0);
            target.SetBuff(new CombatBuffState(CombatTraitRules.JianyiBuffId, expiresAt, nextStacks, "剑意",
                ResetOnWave: true,
                Effects:
                [
                    new(BuffEffectChannel.AttackPowerPostRatio, 0.30 * scale),
                    new(BuffEffectChannel.CriticalPostRatio, 0.30 * scale),
                    new(BuffEffectChannel.CriticalDamagePoints, 30.0 * scale)
                ]));
            encounter.ActionRecorder?.RecordBuff(target, target, CombatTraitRules.JianyiBuffId, nextStacks,
                DuelRecordActionKind.Trait, at, CombatTraitRules.YangjianshuTraitId);
        }
    }
}

/// <summary>内联特质命中反应，位于汲取与反伤之前。</summary>
[CombatTriggerOrder(50)]
public sealed class InlineTraitReactionHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>只消费攻击方的命中条目。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) CombatTraitReactions.OnHit(trigger.Context);
    }
}
