namespace Domain.Tests;

/// <summary>
/// SimulationResult 事件有界尾窗回归：长跨度推进产出的事件不再无界驻留，
/// 尾窗只折叠最早的事件并计入 DroppedEventCount，保留段恒为产出流的连续后缀；
/// 位级确定性红线不变——同一输入的保留事件逐字段一致。
/// </summary>
public sealed class SimulationEventTailTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>1 秒完成的无限循环行动：每轮恰好产生一条 action.completed，便于按轮次核对丢弃计数。</summary>
    private static ActionCatalog TickerCatalog() => new([
        new ActionDefinition(new ActionId("test.ticker"), "打点", ActionKind.Exploration, 1,
            new Dictionary<ItemId, double>(), [])
    ]);

    [Fact]
    public void LongAdvanceKeepsBoundedEventTail()
    {
        var catalog = TickerCatalog();
        var player = CreatePlayer("尾窗");
        player.Enqueue(new ActionIntent(new ActionId("test.ticker"), -1), Baseline, catalog);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(600), catalog);

        // 600 轮完成 → 600 条 action.completed；尾窗驻留在 [256, 512] 水位内，丢弃数补齐总量。
        Assert.Equal(600, result.CompletedRepeats);
        Assert.InRange(result.Events.Count, 256, 512);
        Assert.Equal(600 - result.Events.Count, result.DroppedEventCount);
        // 保留段是产出流的连续后缀：时间单调不减、事件类别一致、末条落在目标时刻。
        for (var i = 1; i < result.Events.Count; i++)
            Assert.True(result.Events[i].At >= result.Events[i - 1].At);
        Assert.All(result.Events, entry => Assert.Equal("action.completed", entry.Code));
        Assert.Equal(Baseline.AddSeconds(600), result.Events[^1].At);
    }

    [Fact]
    public void TailWindowScalesWithRepeatsNotElapsed()
    {
        var catalog = TickerCatalog();
        var player = CreatePlayer("缩放");
        player.Enqueue(new ActionIntent(new ActionId("test.ticker"), -1), Baseline, catalog);

        // 300 轮低于尾窗容量：不裁剪，DroppedEventCount 为 0。
        var small = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(300), catalog);
        Assert.Equal(300, small.Events.Count);
        Assert.Equal(0, small.DroppedEventCount);

        // 再推进 600 轮：累计 900 条产出越过水位，窗口驻留有界。
        var large = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(900), catalog);
        Assert.Equal(600, large.CompletedRepeats);
        Assert.InRange(large.Events.Count, 1, 512);
        Assert.Equal(600 - large.Events.Count, large.DroppedEventCount);
    }

    [Fact]
    public void TailWindowPreservesLatestEventsAcrossSplitAdvances()
    {
        var catalog = TickerCatalog();
        var single = CreatePlayer("整段");
        var split = CreatePlayer("分段");
        single.Enqueue(new ActionIntent(new ActionId("test.ticker"), -1), Baseline, catalog);
        split.Enqueue(new ActionIntent(new ActionId("test.ticker"), -1), Baseline, catalog);

        var whole = SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(600), catalog);
        SimulationResult? last = null;
        for (var i = 1; i <= 60; i++)
            last = SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(10 * i), catalog);

        // 分段推进的末段产出（最后 10 轮）必须与整段窗口的末尾 10 条逐字段一致。
        var expectedTail = last!.Events;
        Assert.True(whole.Events.Count >= expectedTail.Count);
        var actualTail = whole.Events.Skip(whole.Events.Count - expectedTail.Count).ToArray();
        Assert.Equal(expectedTail.Count, actualTail.Length);
        for (var i = 0; i < expectedTail.Count; i++)
        {
            Assert.Equal(expectedTail[i].At, actualTail[i].At);
            Assert.Equal(expectedTail[i].Code, actualTail[i].Code);
            Assert.Equal(expectedTail[i].Message, actualTail[i].Message);
            Assert.Equal(expectedTail[i].ActionId, actualTail[i].ActionId);
        }
        // 状态面位级一致：推进结果不受事件窗口影响。
        Assert.Equal(single.Progression.Qi, split.Progression.Qi);
        Assert.Equal(single.Progression.LifetimeQi, split.Progression.LifetimeQi);
        Assert.Equal(single.Actions.Count, split.Actions.Count);
    }

    [Fact]
    public void BoundedTailKeepsMemoryFlatOverLongOfflineAdvance()
    {
        var catalog = TickerCatalog();
        var player = CreatePlayer("长夜");
        player.Enqueue(new ActionIntent(new ActionId("test.ticker"), -1), Baseline, catalog);

        // 单次推进逼近 MaxTransitionsPerAdvance(10万) 上限的最大跨度：99999 轮行动，
        // 事件驻留恒有界而不是随轮次线性增长——离线补算的内存水位与推进时长脱钩。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(99999), catalog);
        Assert.Equal(99999, result.CompletedRepeats);
        Assert.InRange(result.Events.Count, 256, 512);
        Assert.Equal(99999 - result.Events.Count, result.DroppedEventCount);
        Assert.Equal("action.completed", result.Events[^1].Code);
        Assert.Equal(Baseline.AddSeconds(99999), result.Events[^1].At);
    }
}
