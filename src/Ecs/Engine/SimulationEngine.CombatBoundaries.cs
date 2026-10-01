using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;

namespace DaoHua.Ecs;

public static partial class SimulationEngine
{
    /// <summary>无世界调用复用 ECS 战斗准备规则。</summary>
    public static void InitializeCombatAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset at, ActionMutationBatch mutations)
        => Systems.CombatActionRules.InitializeCombatAction(player, action, definition, gameData, at, mutations);

    /// <summary>无世界调用复用 ECS 战斗准备规则。</summary>
    public static CombatEntryPreparationStep AdvanceCombatPreparation(IPlayerResourceContext player,
        IPlayerActionState action, ActionDefinition definition, GameDataCatalog gameData, AssetWallet? wallet,
        DateTimeOffset from, DateTimeOffset target, double remainingSeconds, ActionMutationBatch mutations)
        => Systems.CombatActionRules.AdvanceCombatPreparation(player, action, definition, gameData, wallet,
            from, target, remainingSeconds, mutations);

    private static CombatEncounterState? AdvanceCombatPreparationCore(IPlayerResourceContext player,
        IPlayerActionState action, ActionDefinition definition, GameDataCatalog gameData, AssetWallet? assetBalances,
        DuelAdvanceOptions? duel, DateTimeOffset from, DateTimeOffset target, ref double remainingSeconds,
        ref Dictionary<Domain.Common.ItemId, double>? deltas, ref SimulationEventTail? events)
        => Systems.CombatActionRules.AdvanceCombatPreparationCore(player, action, definition, gameData,
            assetBalances, duel, from, target, ref remainingSeconds, ref deltas, ref events);

    /// <summary>无世界调用复用 ECS 战斗收益结算规则。</summary>
    public static double CompleteCombatAdvance(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, CombatEncounterDefinition encounter, GameDataCatalog gameData,
        CombatEncounterState battle, CombatAdvanceResult result, DateTimeOffset from, DateTimeOffset target,
        double remainingSeconds, ActionMutationBatch mutations)
        => Systems.CombatActionRules.CompleteCombatAdvance(player, action, definition, encounter, gameData,
            battle, result, from, target, remainingSeconds, mutations);

    private static void CompleteCombatAdvanceCore(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, CombatEncounterDefinition encounter, GameDataCatalog gameData,
        CombatEncounterState battleState, CombatAdvanceResult combatResult, DateTimeOffset from, DateTimeOffset target,
        ref double remainingSeconds, ref Dictionary<Domain.Common.ItemId, double>? deltas, ref SimulationEventTail? events,
        ref double completedActions, ref double completedRepeats)
        => Systems.CombatActionRules.CompleteCombatAdvanceCore(player, action, definition, encounter, gameData,
            battleState, combatResult, from, target, ref remainingSeconds, ref deltas, ref events,
            ref completedActions, ref completedRepeats);

    /// <summary>无世界调用复用 ECS 战斗恢复规则。</summary>
    public static CombatEncounterState RestoreCombatBattle(IPlayerResourceContext player, IPlayerActionState action,
        CombatEncounterDefinition encounter, GameDataCatalog gameData)
        => Systems.CombatActionRules.RestoreCombatBattle(player, action, encounter, gameData);
}

/// <summary>战斗准备阶段实际消费后的窗口余量与可选新战斗。</summary>
public readonly record struct CombatEntryPreparationStep(CombatEncounterState? Battle, double RemainingSeconds);
