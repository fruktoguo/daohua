namespace Domain.Tests;

/// <summary>
/// 队列停泊开关（QueuePaused）与一键清空（ClearActions）测试套件。
/// 验证：暂停时行动进度冻结但墙钟照常流逝、恢复后续跑；
/// 清空按统一变更管线归档账本、单次版本提交、空队列为空操作。
/// </summary>
public sealed class QueuePauseAndClearTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>
    /// 暂停开关等值早退：同值写入不递增版本不标脏；翻转一次只提交一次 Settings 脏段。
    /// </summary>
    [Fact]
    public void SetQueuePaused_TogglesOnceAndTouchesSettings()
    {
        var player = PlayerAggregate.Restore(CreatePlayer("甲").ExportState());
        var version = player.StateVersion;

        player.SetQueuePaused(true);
        Assert.True(player.Settings.QueuePaused);
        Assert.Equal(version + 1, player.StateVersion);
        Assert.Contains(PlayerSectionKind.Settings, player.DirtySections);

        player.SetQueuePaused(true);
        Assert.Equal(version + 1, player.StateVersion);
    }

    /// <summary>
    /// 暂停时行动队列整体冻结：进度秒、完成轮次与队列结构原样保留；
    /// 墙钟照常流逝（LastSimulatedAt/道龄前进），恢复暂停后按原进度继续。
    /// </summary>
    [Fact]
    public void AdvanceTo_PausedFreezesQueueProgressButClockAdvances()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("乙");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 4), Baseline, catalog.Actions);

        // 先正常推进 3 秒建立进度，再暂停。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog);
        Assert.Equal(3, player.Actions[0].ProgressSeconds);
        Assert.Equal(ActionQueueStatus.Running, player.Actions[0].Status);

        player.SetQueuePaused(true);
        var ageBefore = player.Progression.AgeSeconds;

        // 暂停中推进 30 秒：行动进度与修为收益纹丝不动，模拟时钟与道龄照常流逝
        // （墙钟资源窗口如自然回复仍照常结算，Changed 不冻结）。
        var paused = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(33), catalog);

        Assert.Equal(Baseline.AddSeconds(33), player.LastSimulatedAt);
        Assert.Equal(3, player.Actions[0].ProgressSeconds);
        Assert.Equal(0, player.Actions[0].CompletedRepeats);
        Assert.Single(player.Actions);
        Assert.Equal(ageBefore + 30, player.Progression.AgeSeconds);
        Assert.Equal(0, player.Progression.Experience);
        Assert.Equal(0, paused.CompletedRepeats);

        // 恢复后继续按原进度执行：再推进 7 秒恰好完成第 1 轮（draw-qi 按轮结算 10 修为）。
        player.SetQueuePaused(false);
        var resumed = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), catalog);
        Assert.Equal(1, resumed.CompletedRepeats);
        Assert.Equal(1, player.Actions[0].CompletedRepeats);
        Assert.Equal(10, player.Progression.Experience);
    }

    /// <summary>
    /// 暂停期间入队照常受理：队列结构可变，仅不推进。
    /// </summary>
    [Fact]
    public void EnqueueWhilePaused_IsAcceptedButNotAdvanced()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("丙");
        player.SetQueuePaused(true);

        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog);

        Assert.Single(player.Actions);
        Assert.Equal(0, player.Actions[0].ProgressSeconds);
        Assert.Equal(Baseline.AddSeconds(10), player.LastSimulatedAt);
    }

    /// <summary>
    /// 一键清空中队：清空后 Actions 为空、队首行动账本归档、单次版本递增并标脏队列分段。
    /// </summary>
    [Fact]
    public void ClearActions_RemovesAllEntriesAndArchivesLedgerOnce()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("丁");
        var head = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 4), Baseline, catalog.Actions);
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 2), Baseline, catalog.Actions);

        // 推进让队首产生进度与账本内容，第二项保持 Pending。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(12), catalog);
        Assert.Equal(2, player.Actions.Count);
        Assert.True(player.Duel.ActionReports.ContainsKey(head.Id));

        var version = player.StateVersion;
        player.ConsumeDirtySections();

        Assert.True(player.ClearActions());

        Assert.Empty(player.Actions);
        Assert.Empty(player.Duel.ActionReports);
        var archived = Assert.Single(player.Duel.Reports);
        Assert.Equal("cultivation.draw-qi", archived.ActionId);
        Assert.Equal(version + 1, player.StateVersion);
        Assert.Equal([PlayerSectionKind.ActionQueue, PlayerSectionKind.Duel],
            player.ConsumeDirtySections());
    }

    /// <summary>
    /// 空队列清空是空操作：返回 false，不递增版本不标脏。
    /// </summary>
    [Fact]
    public void ClearActions_EmptyQueueIsNoOp()
    {
        var player = PlayerAggregate.Restore(CreatePlayer("戊").ExportState());
        var version = player.StateVersion;

        Assert.False(player.ClearActions());
        Assert.Equal(version, player.StateVersion);
        Assert.Empty(player.DirtySections);
    }

    /// <summary>
    /// QueuePaused 随 Settings 快照往返：导出/恢复逐字段一致。
    /// </summary>
    [Fact]
    public void QueuePaused_SurvivesSnapshotRoundTrip()
    {
        var player = CreatePlayer("己");
        player.SetQueuePaused(true);

        var restored = PlayerAggregate.Restore(player.ExportState());

        Assert.True(restored.Settings.QueuePaused);
        Assert.Equal(player.ExportState().Settings, restored.ExportState().Settings);
    }
}
