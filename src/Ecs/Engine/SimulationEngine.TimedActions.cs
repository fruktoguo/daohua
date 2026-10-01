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

/// <summary>同一资源窗口内的行动收益累加器；不拥有玩家或钱包。</summary>
public sealed class ActionMutationBatch
{
    internal Dictionary<ItemId, double>? Deltas;
    internal SimulationEngine.SimulationEventTail? EventTail;
    internal double ActionCount;
    internal double RepeatCount;

    public IReadOnlyDictionary<ItemId, double> ItemDeltas => Deltas is { } deltas ? deltas : EmptyDeltas;
    public IReadOnlyList<SimulationEvent> Events => EventTail?.View ?? [];
    public long DroppedEventCount => EventTail?.Dropped ?? 0;
    public double CompletedActions => ActionCount;
    public double CompletedRepeats => RepeatCount;


    /// <summary>窗口资产按原 Core 口径过滤后排序；请求汇总保持首次入账顺序。</summary>
    public IReadOnlyList<AssetDelta> ToAssetDeltas(PlayerId playerId, bool sortByItem = true)
    {
        if (Deltas is null || Deltas.Count == 0) return [];
        var rows = Deltas.Where(pair => sortByItem ? Math.Abs(pair.Value) > 1e-12 : pair.Value != 0).ToList();
        if (sortByItem) rows.Sort(static (a, b) => string.CompareOrdinal(a.Key.Value, b.Key.Value));
        return rows.Select(pair => new AssetDelta(playerId, new AssetKey(pair.Key), pair.Value, "simulation")).ToArray();
    }

    /// <summary>记录队列事件；时间与实例准入顺序由状态所有者确定。</summary>
    public void AddEvent(SimulationEvent entry) => (EventTail ??= new()).Add(entry);

    internal void AddAsset(AssetDelta delta)
    {
        Deltas ??= new();
        Deltas[delta.Asset.ItemId] = Deltas.GetValueOrDefault(delta.Asset.ItemId) + delta.QuantityDelta;
    }

    internal void MergeEvents(ActionMutationBatch other)
    {
        if (other.EventTail is not { } tail) return;
        EventTail ??= new();
        EventTail.Fold(tail.Dropped);
        foreach (var entry in tail.View) EventTail.Add(entry);
    }

    private static readonly IReadOnlyDictionary<ItemId, double> EmptyDeltas = new Dictionary<ItemId, double>();
}

public static partial class SimulationEngine
{
    /// <summary>受阻队首只执行一次出队或轮转；false 表示当前窗口剩余时间保持停泊。</summary>
    public static bool AdvanceBlockedAction(IPlayerActionContext player, IPlayerActionState action, DateTimeOffset at)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AdvanceBlockedAction(player, action, at);

    /// <summary>按窗口工作钱包与尚未提交的材料增量检查地点和库存，再建账并激活队首。</summary>
    public static bool TryStartAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => DaoHua.Ecs.Systems.ActionExecutionRules.TryStartAction(player, action, definition, gameData, assets, at, mutations);

    private static bool TryStartActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
        => DaoHua.Ecs.Systems.ActionExecutionRules.TryStartActionCore(player, action, definition, gameData, assets, at, deltas, ref events);


    /// <summary>计时行动的建筑加速只修正本轮时长，不改变按轮扣除的持续用品时长。</summary>
    public static double TimedActionDuration(IPlayerActionContext player, ActionDefinition definition)
        => DaoHua.Ecs.Systems.ActionExecutionRules.TimedActionDuration(player, definition);

    /// <summary>本轮开始前补充持续用品并解析收益倍率；调用方已完成地点和材料准入。</summary>
    public static double PrepareTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => DaoHua.Ecs.Systems.ActionExecutionRules.PrepareTimedAction(player, action, definition, gameData, assets, at, mutations);

    private static double PrepareTimedActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, bool isActionStart, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
        => DaoHua.Ecs.Systems.ActionExecutionRules.PrepareTimedActionCore(player, action, definition, isActionStart, gameData, assets, at, ref deltas, ref events);

    /// <summary>推进已由时钟裁剪的本轮片段，按原整秒序列写入进度与连续收益。</summary>
    public static void AdvanceTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        double stepSeconds, DateTimeOffset at)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AdvanceTimedAction(player, action, definition, multiplier, gameData, stepSeconds, at);

    /// <summary>单轮完成结算：保留原物品、材料、用品、统计和队列修改的先后顺序。</summary>
    public static void CompleteTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        DateTimeOffset at, ActionMutationBatch mutations)
        => DaoHua.Ecs.Systems.ActionExecutionRules.CompleteTimedAction(player, action, definition, multiplier, gameData, at, mutations);
}
