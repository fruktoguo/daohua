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

/// <summary>资源系统选出的一个自然窗口，普通行动可在该窗口内继续切分单轮边界。</summary>
public readonly record struct ResourceBoundaryPlan(double StepSeconds, bool ReachesNaturalBoundary, bool Changed);

public static partial class SimulationEngine
{
    /// <summary>只推进一次搜寻读条，不创建战斗实体；返回实际消耗的秒数。</summary>
    public static double AdvanceCombatSearchPhase(IPlayerActionState action, double availableSeconds)
        => Systems.CombatActionRules.AdvanceCombatSearchPhase(action, availableSeconds);

    /// <summary>清理到期增益并激活即时用品，选择回复、Buff、用品冷却与请求末端的最近窗口。</summary>
    public static ResourceBoundaryPlan PrepareResourceBoundary(IPlayerResourceContext player,
        GameDataCatalog gameData, AssetWallet wallet, DateTimeOffset computeTarget, ActionMutationBatch request)
        => DaoHua.Ecs.Systems.ActionExecutionRules.PrepareResourceBoundary(player, gameData, wallet, computeTarget, request);

    private static ResourceBoundaryPlan PrepareResourceBoundaryCore(IPlayerResourceContext player,
        GameDataCatalog gameData, AssetWallet wallet, DateTimeOffset computeTarget,
        SimulationEventTail events, LiveCombatBattle? live, out List<AssetDelta> instantDeltas)
        => DaoHua.Ecs.Systems.ActionExecutionRules.PrepareResourceBoundaryCore(player, gameData, wallet, computeTarget, events, live, out instantDeltas);

    /// <summary>资源窗口收尾按暂存入账、技能时钟、行动资产、事件、回复的既有顺序执行。</summary>
    public static bool CompleteResourceBoundary(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet wallet, DateTimeOffset windowStart, double stepSeconds,
        ActionMutationBatch core, ActionMutationBatch request)
        => DaoHua.Ecs.Systems.ActionExecutionRules.CompleteResourceBoundary(player, gameData, wallet, windowStart, stepSeconds, core, request);

    /// <summary>应用属性解析结果；分段由调用方直接持有，面板夹取顺序与聚合入口一致。</summary>
    public static PlayerAttributes ApplyResourceAttributes(IPlayerResourceContext player, GameDataCatalog gameData)
        => DaoHua.Ecs.Systems.ActionExecutionRules.ApplyResourceAttributes(player, gameData);

    private static DuelStagedItem[] SettleResourceItems(IPlayerResourceContext player,
        double nowUnixSeconds, double settleSeconds)
        => DaoHua.Ecs.Systems.ActionExecutionRules.SettleResourceItems(player, nowUnixSeconds, settleSeconds);

    private static bool AdvanceIdleSkillResources(IPlayerResourceContext player, double stepSeconds)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AdvanceIdleSkillResources(player, stepSeconds);

    private static bool CompleteResourceRegeneration(IPlayerResourceContext player, GameDataCatalog gameData, double stepSeconds)
        => DaoHua.Ecs.Systems.ActionExecutionRules.CompleteResourceRegeneration(player, gameData, stepSeconds);

    /// <summary>超过48小时收益上限后只清理过期状态与推进时钟，不推进任何行动。</summary>
    public static bool ApplyOfflineCap(IPlayerResourceContext player, DateTimeOffset target, ActionMutationBatch request)
        => DaoHua.Ecs.Systems.ActionExecutionRules.ApplyOfflineCap(player, target, request);
}
