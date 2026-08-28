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
    long StateVersion,
    bool Changed);

/// <summary>
/// 唯一确定性快进入口。算法按行动完成边界推进，不按离线秒数写历史事件。
/// </summary>
public static class SimulationEngine
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxTransitionsPerAdvance = 100_000;

    /// <summary>整秒量化的贴整容差：吸收 0.1 这类二进制无法精确表示的秒数在累加中的尾差。</summary>
    private const double SecondQuantizationEpsilon = 1e-9;

    /// <summary>单次连续奖励可施加的整秒上限：正常推进远低于该值，越界说明配表或时钟已破坏，快速失败避免巨量循环。</summary>
    private const double MaxWholeSecondsPerApply = 1_000_000_000;

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
                // 开发期容错自愈：Enqueue 拒绝“-1 且时长≤0”，但配表热更可能把存量行动时长改为 0，
                // 队头空转会耗尽转换上限并让玩家永久卡死。此处移出失效行动并留事件证据，继续推进后续行动。
                player.RemoveAction(action.Id, incrementVersion: false);
                events.Add(new SimulationEvent(
                    from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                    "action.invalid-dropped",
                    $"行动 {definition.Name} 的时长配置无效（{duration:0.######} 秒），已自动移出队列。",
                    definition.Id));
                continue;
            }

            var needed = Math.Max(0, duration - action.ProgressSeconds);
            var step = Math.Min(remainingSeconds, needed);
            remainingSeconds -= step;
            var wholeSeconds = QuantizeElapsedSeconds(player, step);
            action.ProgressSeconds += wholeSeconds;
            ApplyContinuousReward(player, definition, wholeSeconds);
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
        // 空队列只更新内存中的时钟，不递增状态版本，也不标记 section，避免每秒产生无意义的磁盘写入。
        if (transitions > 0)
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
        return new SimulationResult(player.PlayerId, from, target, (target - from).TotalSeconds, completedActions, completedRepeats, assetDeltas, events, player.StateVersion, transitions > 0);
    }

    /// <summary>
    /// 把任意切分的模拟秒数累进持久化进位余数（carry）并取整整秒。
    /// 1e-9 量级贴整后再向下取整，使“一次推进 N 秒”与“逐秒推进 N 次”产生完全相同的整秒序列，
    /// 连续奖励的浮点操作序列因此位级一致；余数保留到下次推进。
    /// </summary>
    private static long QuantizeElapsedSeconds(PlayerAggregate player, double seconds)
    {
        if (seconds <= 0) return 0;
        var carry = player.Progression.SimulationCarrySeconds + seconds;
        var nearest = Math.Round(carry);
        if (Math.Abs(carry - nearest) <= SecondQuantizationEpsilon) carry = nearest;
        var floorSeconds = Math.Floor(carry);
        player.Progression.SimulationCarrySeconds = carry - floorSeconds;
        if (floorSeconds > MaxWholeSecondsPerApply)
            throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟整秒数超出可推进范围。");
        return (long)floorSeconds;
    }

    private static void ApplyContinuousReward(PlayerAggregate player, ActionDefinition definition, long wholeSeconds)
    {
        if (wholeSeconds <= 0) return;
        // 溢出预检查沿用“先校验后变更”语义：rate * 整秒数越界时不得污染任何进度数值。
        var seconds = (double)wholeSeconds;
        if (definition.QiPerSecond > 0)
        {
            var rate = definition.QiPerSecond;
            var total = rate * seconds;
            if (!double.IsFinite(total) || !double.IsFinite(player.Progression.Qi + total) || !double.IsFinite(player.Progression.LifetimeQi + total))
                throw new DomainException(DomainErrorCode.InvariantViolation, "气运奖励超出数值范围。");
            // 逐整秒累加，与在线逐秒推进保持完全相同的浮点操作序列。
            for (var i = 0; i < wholeSeconds; i++)
            {
                player.Progression.Qi += rate;
                player.Progression.LifetimeQi += rate;
            }
        }
        if (definition.ExperiencePerSecond > 0)
        {
            var rate = definition.ExperiencePerSecond;
            var total = rate * seconds;
            if (!double.IsFinite(total) || !double.IsFinite(player.Progression.Experience + total))
                throw new DomainException(DomainErrorCode.InvariantViolation, "经验奖励超出数值范围。");
            for (var i = 0; i < wholeSeconds; i++)
                player.Progression.Experience += rate;
        }
        if (!double.IsFinite(player.Progression.Qi) || !double.IsFinite(player.Progression.LifetimeQi) || !double.IsFinite(player.Progression.Experience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "模拟后的玩家进度数值无效。");
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
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
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
        // -1 表示无限行动：每轮完成后保留队列项并继续下一轮。
        if (action.RemainingRepeats == 0)
        {
            player.RemoveAction(action.Id, incrementVersion: false);
        }
    }

    private static void AddDelta(IDictionary<ItemId, double> deltas, ItemId item, double amount)
    {
        if (!item.IsValid || !double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源增量无效。");
        var next = deltas.TryGetValue(item, out var current) ? current + amount : amount;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源累计增量超出数值范围。");
        deltas[item] = next;
    }
}
