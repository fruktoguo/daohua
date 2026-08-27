namespace Domain.Simulation;

public sealed record SimulationEvent(
    DateTimeOffset At,
    string Code,
    string Message,
    ActionId? ActionId = null,
    IReadOnlyDictionary<ItemId, double>? ItemDeltas = null);

public sealed record SimulationResult(
    PlayerId PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    double ElapsedSeconds,
    double CompletedActions,
    double CompletedRepeats,
    IReadOnlyList<AssetDelta> AssetDeltas,
    IReadOnlyList<SimulationEvent> Events,
    long StateVersion)
{
    public bool Changed => ElapsedSeconds > 0 || CompletedRepeats > 0 || AssetDeltas.Count > 0;
}

/// <summary>
/// 唯一确定性快进入口。算法按行动完成边界推进，不按离线秒数写历史事件。
/// </summary>
public static class SimulationEngine
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxTransitionsPerAdvance = 100_000;

    public static SimulationResult AdvanceTo(
        PlayerAggregate player,
        DateTimeOffset target,
        ActionCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        catalog ??= ActionCatalog.Default;
        target = target.ToUniversalTime();
        if (target < player.LastSimulatedAt)
            throw new DomainException(DomainErrorCode.InvalidArgument, "目标时间不能早于玩家上次模拟时间。");

        var from = player.LastSimulatedAt;
        var remainingSeconds = (target - from).TotalSeconds;
        var completedActions = 0d;
        var completedRepeats = 0d;
        var transitions = 0;
        var deltas = new Dictionary<ItemId, double>();
        var events = new List<SimulationEvent>();

        while (remainingSeconds > 0 && player.Actions.Count > 0)
        {
            if (++transitions > MaxTransitionsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");

            var action = player.Actions[0];
            var definition = catalog.Get(action.Intent.ActionId);
            action.Status = ActionQueueStatus.Running;
            var duration = definition.DurationSeconds;
            if (duration <= 0)
            {
                CompleteRepeat(player, action, definition, from.AddSeconds((target - from).TotalSeconds - remainingSeconds), deltas, events, ref completedActions, ref completedRepeats);
                continue;
            }

            var needed = Math.Max(0, duration - action.ProgressSeconds);
            var step = Math.Min(remainingSeconds, needed);
            action.ProgressSeconds += step;
            remainingSeconds -= step;
            ApplyContinuousReward(player, definition, step);
            if (step > 0)
            {
                player.MarkDirty(definition.Kind switch
                {
                    ActionKind.Cultivation => PlayerSectionKind.Progression,
                    ActionKind.Exploration => PlayerSectionKind.World,
                    ActionKind.Combat => PlayerSectionKind.Combat,
                    _ => PlayerSectionKind.ActionQueue
                });
            }
            if (action.ProgressSeconds + 1e-9 < duration) break;
            CompleteRepeat(player, action, definition, from.AddSeconds((target - from).TotalSeconds - remainingSeconds), deltas, events, ref completedActions, ref completedRepeats);
        }

        player.LastSimulatedAt = target;
        if (remainingSeconds < 0) throw new DomainException(DomainErrorCode.InvariantViolation, "模拟时间出现负余量。");
        if (completedRepeats > 0 || (target - from).TotalSeconds > 0)
        {
            player.StateVersion = checked(player.StateVersion + 1);
            player.MarkDirty(PlayerSectionKind.Progression);
            player.MarkDirty(PlayerSectionKind.ActionQueue);
        }

        var assetDeltas = deltas
            .Where(pair => Math.Abs(pair.Value) > 1e-12)
            .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => new AssetDelta(player.PlayerId, new AssetKey(pair.Key), pair.Value, "simulation"))
            .ToArray();
        return new SimulationResult(player.PlayerId, from, target, (target - from).TotalSeconds, completedActions, completedRepeats, assetDeltas, events, player.StateVersion);
    }

    private static void ApplyContinuousReward(PlayerAggregate player, ActionDefinition definition, double seconds)
    {
        if (seconds <= 0) return;
        if (definition.QiPerSecond > 0)
        {
            var amount = definition.QiPerSecond * seconds;
            player.Progression.Qi += amount;
            player.Progression.LifetimeQi += amount;
        }
        if (definition.ExperiencePerSecond > 0) player.Progression.Experience += definition.ExperiencePerSecond * seconds;
    }

    private static void CompleteRepeat(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        DateTimeOffset target,
        IDictionary<ItemId, double> deltas,
        List<SimulationEvent> events,
        ref double completedActions,
        ref double completedRepeats)
    {
        action.ProgressSeconds = 0;
        action.CompletedRepeats += 1;
        action.RemainingRepeats -= 1;
        completedActions += 1;
        completedRepeats += 1;
        foreach (var (item, quantity) in definition.Outputs) AddDelta(deltas, item, quantity);
        foreach (var (item, quantity) in definition.Inputs) AddDelta(deltas, item, -quantity);
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        events.Add(new SimulationEvent(
            target,
            "action.completed",
            definition.Name,
            definition.Id,
            definition.Outputs));
        if (action.RemainingRepeats <= 0)
        {
            player.RemoveAction(action.Id, incrementVersion: false);
        }
    }

    private static void AddDelta(IDictionary<ItemId, double> deltas, ItemId item, double amount)
    {
        if (!item.IsValid || !double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源增量无效。");
        deltas[item] = deltas.TryGetValue(item, out var current) ? current + amount : amount;
    }
}
