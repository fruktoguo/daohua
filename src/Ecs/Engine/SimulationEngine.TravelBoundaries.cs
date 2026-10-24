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
    /// <summary>只规划或校验当前移动路径，实际耗时交由移动系统推进。</summary>
    public static bool PrepareTravelAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset at, ActionMutationBatch mutations)
        => DaoHua.Ecs.Systems.ActionExecutionRules.PrepareTravelAction(player, action, definition, gameData, at, mutations);

    /// <summary>按原资源窗口预算推进移动，跨路段坐标与到达结算使用原浮点顺序。</summary>
    public static double AdvanceTravelBoundary(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset from, DateTimeOffset target,
        double remainingSeconds, ActionMutationBatch mutations)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AdvanceTravelBoundary(player, action, definition, gameData, from, target, remainingSeconds, mutations);

}
