namespace Domain.Simulation;

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
    {
        if (!player.Settings.LoopEnabled)
        {
            player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: at.ToUnixTimeMilliseconds() / 1000d);
            return true;
        }
        if (player.Actions.Count <= 1) return false;
        for (var i = 0; i < player.Actions.Count; i++)
            if (player.Actions[i].Status != ActionQueueStatus.Blocked)
            {
                player.RotateActionToTail(action.Id);
                return true;
            }
        return false;
    }

    /// <summary>按窗口工作钱包与尚未提交的材料增量检查地点和库存，再建账并激活队首。</summary>
    public static bool TryStartAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => TryStartActionCore(player, action, definition, gameData, assets, at,
            mutations.Deltas, ref mutations.EventTail);

    private static bool TryStartActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        var availableHere = definition.AllowedNodeIds is null || definition.AllowedNodeIds.Count == 0
            || (player.World.EdgeId is null && definition.AllowedNodeIds.Contains(player.World.NodeId));
        var hasInputs = true;
        foreach (var input in definition.Inputs)
            if ((assets?.Get(new AssetKey(input.Key)) ?? (gameData is null ? double.PositiveInfinity : 0))
                + (deltas?.GetValueOrDefault(input.Key) ?? 0) < input.Value)
            {
                hasInputs = false;
                break;
            }
        if (!availableHere || !hasInputs)
        {
            action.Status = ActionQueueStatus.Blocked;
            events ??= new();
            events.Add(new SimulationEvent(at, "action.blocked",
                availableHere ? "材料不足，行动受阻。" : "当前位置不允许执行该行动。", definition.Id));
            return false;
        }
        player.EnsureActionReport(action.Id, definition.Id, definition.Name, at);
        action.Status = ActionQueueStatus.Running;
        return true;
    }

    /// <summary>计时行动的建筑加速只修正本轮时长，不改变按轮扣除的持续用品时长。</summary>
    public static double TimedActionDuration(IPlayerActionContext player, ActionDefinition definition)
    {
        var duration = definition.DurationSeconds;
        var buildingSpeed = player.World.EffectiveNodeBuildingEffects.SpeedRatioFor(definition.Kind);
        if (buildingSpeed > 0) duration /= 1 + buildingSpeed;
        return duration;
    }

    /// <summary>本轮开始前补充持续用品并解析收益倍率；调用方已完成地点和材料准入。</summary>
    public static double PrepareTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => PrepareTimedActionCore(player, action, definition,
            action.ProgressSeconds <= SecondQuantizationEpsilon, gameData, assets, at,
            ref mutations.Deltas, ref mutations.EventTail);

    private static double PrepareTimedActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, bool isActionStart, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        var multiplier = PrepareSustainedAutoUse(player, definition, isActionStart, gameData,
            assets, at, action.Id, ref deltas, ref events);
        if (definition.Kind == ActionKind.Cultivation && gameData is not null)
            multiplier *= CultivationMultiplier(gameData, player.World.NodeId);
        multiplier *= 1 + player.World.EffectiveNodeBuildingEffects.OutputRatioFor(definition.Kind);
        return multiplier;
    }

    /// <summary>推进已由时钟裁剪的本轮片段，按原整秒序列写入进度与连续收益。</summary>
    public static void AdvanceTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        double stepSeconds, DateTimeOffset at)
    {
        var wholeSeconds = QuantizeElapsedSeconds(player, stepSeconds);
        action.ProgressSeconds += wholeSeconds;
        if (definition.Kind == ActionKind.Cultivation && wholeSeconds > 0)
            player.Progression.CultivationSeconds += wholeSeconds;
        ApplyContinuousReward(player, definition, wholeSeconds, multiplier, gameData,
            action.Id, at.ToUnixTimeMilliseconds() / 1000d);
        if (stepSeconds > 0)
            player.MarkDirty(definition.Kind switch
            {
                ActionKind.Cultivation => PlayerSectionKind.Progression,
                ActionKind.Exploration => PlayerSectionKind.World,
                ActionKind.Combat => PlayerSectionKind.Combat,
                _ => PlayerSectionKind.ActionQueue
            });
    }

    /// <summary>单轮完成结算：保留原物品、材料、用品、统计和队列修改的先后顺序。</summary>
    public static void CompleteTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        DateTimeOffset at, ActionMutationBatch mutations)
        => CompleteRepeat(player, action, definition, multiplier, gameData, at,
            ref mutations.Deltas, ref mutations.EventTail, ref mutations.ActionCount, ref mutations.RepeatCount);
}
