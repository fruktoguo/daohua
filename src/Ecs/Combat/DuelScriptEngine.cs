using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 斗法剧本推演器：撮合成功后把双方镜像一次性确定性推演至终结，
/// 产出双方共享的 DuelScript；战斗期间任何配置变更不影响结果。
/// </summary>
public static class DuelScriptEngine
{
    /// <summary>推演一场斗法；initiator 固定映射为剧本中的 player 视角。</summary>
    public static DuelScript Resolve(
        DuelCombatant initiator,
        DuelCombatant defender,
        long seed,
        DuelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(defender);
        ArgumentNullException.ThrowIfNull(settings);
        var encounter = Systems.CombatEventAdvanceSystem.Start("duel", seed,
            new CombatantTemplate(initiator.Stats, initiator.Skills),
            [new CombatantTemplate(defender.Stats, defender.Skills)]);
        var result = Systems.CombatEventAdvanceSystem.AdvanceTo(encounter, settings.MaxDuelSeconds);
        var outcome = encounter.Outcome == CombatOutcome.Running ? CombatOutcome.Draw : encounter.Outcome;
        var events = new List<DuelScriptEvent>(result.Events.Count);
        // 双方灵力沿事件流滚动携带：主事件带结算后真值，衍生事件沿用最近已知值，
        // 回放侧无需领域推算即可逐帧还原双资源条。
        var initiatorMp = initiator.Stats.MaxManapoints;
        var defenderMp = defender.Stats.MaxManapoints;
        foreach (var combatEvent in result.Events)
        {
            var attackerIsInitiator = combatEvent.AttackerId == "player";
            var targetIsInitiator = combatEvent.TargetId == "player";
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
                targetIsInitiator ? initiatorMp : defenderMp));
        }
        return new DuelScript(outcome, Math.Max(encounter.ElapsedSeconds, 1e-9), events,
            initiator.StagedItems, defender.StagedItems);
    }
}
