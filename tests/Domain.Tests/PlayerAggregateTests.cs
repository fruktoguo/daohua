namespace Domain.Tests;

/// <summary>
/// 玩家聚合测试：快照往返、队列推进、重复次数与状态恢复的约束回归。
/// </summary>
public sealed class PlayerAggregateTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 快照导出/恢复往返保留身份、版本号、修为与队列中行动的进行中进度。
    /// </summary>
    [Fact]
    public void RestoreRoundTripKeepsStateAndQueue()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "青云", Baseline, "catalog-a");
        var action = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2), Baseline, ActionCatalog.Default);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(12), ActionCatalog.Default);

        var restored = PlayerAggregate.Restore(player.ExportState());

        Assert.Equal(player.PlayerId, restored.PlayerId);
        Assert.Equal(player.StateVersion, restored.StateVersion);
        Assert.Equal(player.Progression.Experience, restored.Progression.Experience);
        Assert.Single(restored.Actions);
        Assert.Equal(action.Id, restored.Actions[0].Id);
        Assert.Equal(player.Actions[0].ProgressSeconds, restored.Actions[0].ProgressSeconds);
    }

    /// <summary>
    /// 自动用药剩余时长随快照持久化：同款药仅调数量时保留，停用或换药时清零，
    /// 防止把旧药时长误算到新药上。
    /// </summary>
    [Fact]
    public void AutoUseRemainingDurationPersistsAndClearsWhenDisabledOrSwitched()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "丹药槽", Baseline, "catalog-a");
        player.ConfigureAutoUse("cultivation", 0, "item.qi-gathering-pill", 1, true);
        player.Settings.AutoUseSlots[0] = player.Settings.AutoUseSlots[0] with { RemainingDurationSeconds = 180d };

        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal(180d, Assert.Single(restored.Settings.AutoUseSlots).RemainingDurationSeconds);

        restored.ConfigureAutoUse("cultivation", 0, "item.qi-gathering-pill", 2, true);
        Assert.Equal(180d, restored.Settings.AutoUseSlots[0].RemainingDurationSeconds);

        restored.ConfigureAutoUse("cultivation", 0, "item.qi-gathering-pill", 2, false);
        Assert.Equal(0d, restored.Settings.AutoUseSlots[0].RemainingDurationSeconds);

        restored.Settings.AutoUseSlots[0] = restored.Settings.AutoUseSlots[0] with { RemainingDurationSeconds = 30d };
        restored.ConfigureAutoUse("cultivation", 0, "item.tongmai-pill", 1, true);
        Assert.Equal(0d, restored.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    /// <summary>
    /// 模拟时钟单调递增：向过去推进必须抛错。
    /// </summary>
    [Fact]
    public void CannotAdvanceBackwards()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        Assert.Throws<DomainException>(() => SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(-1), ActionCatalog.Default));
    }

    /// <summary>
    /// 空队列推进墙钟是无效推进：不产生版本递增、不标脏分段，避免无谓的快照刷写。
    /// </summary>
    [Fact]
    public void EmptyQueueClockAdvanceDoesNotCreateStateVersion()
    {
        var created = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var player = PlayerAggregate.Restore(created.ExportState());
        var version = player.StateVersion;

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(60), ActionCatalog.Default);

        Assert.Equal(version, player.StateVersion);
        Assert.False(result.Changed);
        Assert.Empty(player.DirtySections);
    }

    /// <summary>
    /// 不足一轮的部分推进同样算 Changed：进度秒与按秒收益即时入账，CompletedRepeats 仍为 0。
    /// </summary>
    [Fact]
    public void PartialActionAdvanceIsReportedAsChanged()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, ActionCatalog.Default);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), ActionCatalog.Default);

        Assert.True(result.Changed);
        Assert.Equal(0, result.CompletedRepeats);
        Assert.Equal(1, player.Actions[0].ProgressSeconds);
        Assert.Equal(0, player.Progression.Experience);
    }

    /// <summary>
    /// 无限循环不变量：RemainingRepeats == -1 完成多轮后必须保持队列项不得自动出队。
    /// </summary>
    [Fact]
    public void InfiniteActionKeepsQueueEntryAfterCompletedRounds()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        // 无限重复语义与引导无关：终态化避免教程推进对绑定行动的强制出队截断。
        player.SkipTutorial();
        var action = player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), Baseline, ActionCatalog.Default);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(17), ActionCatalog.Default);

        Assert.Equal(2, result.CompletedRepeats);
        Assert.Single(player.Actions);
        Assert.Equal(action.Id, player.Actions[0].Id);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);
    }

    /// <summary>
    /// -1 无限语义经快照往返不丢失：Intent.RepeatCount 与 RemainingRepeats 均保持 -1。
    /// </summary>
    [Fact]
    public void RestoreKeepsInfiniteActionState()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), Baseline, ActionCatalog.Default);

        var restored = PlayerAggregate.Restore(player.ExportState());

        var action = Assert.Single(restored.Actions);
        Assert.Equal(-1, action.Intent.RepeatCount);
        Assert.Equal(-1, action.RemainingRepeats);
    }

    /// <summary>
    /// 零时长 + 无限次数会让模拟原地空转，必须在入队边界即拒绝。
    /// </summary>
    [Fact]
    public void InfiniteZeroDurationActionIsRejected()
    {
        var instant = new ActionCatalog([
            new ActionDefinition(new ActionId("test.instant"), "瞬时", ActionKind.Alchemy, 0, new Dictionary<ItemId, double>(), [])
        ]);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var error = Assert.Throws<DomainException>(() => player.Enqueue(new ActionIntent(new ActionId("test.instant"), -1), Baseline, instant));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 数值统一 double 但次数语义为整数：非整数 RepeatCount 入队即拒绝。
    /// </summary>
    [Fact]
    public void EnqueueRejectsNonIntegerRepeatCount()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var error = Assert.Throws<DomainException>(() => player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2.5), Baseline, ActionCatalog.Default));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 存量快照中残留的非整数次数在恢复时自愈：向下取整；取整后耗尽或为负的行动安全出队，
    /// 且负值不得被翻转成 -1 无限语义。
    /// </summary>
    [Fact]
    public void RestoreHealsNonIntegerRepeatCountsFromLegacySnapshots()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var healable = new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId("cultivation.draw-qi"), 2.5), Baseline, ActionQueueStatus.Pending, 0, 1.5, 0);
        var exhausted = new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId("cultivation.draw-qi"), 2.5), Baseline, ActionQueueStatus.Pending, 0, 0.5, 2);
        var negative = new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId("cultivation.draw-qi"), 2.5), Baseline, ActionQueueStatus.Pending, 0, -0.5, 3);
        var state = player.ExportState() with { Actions = [healable, exhausted, negative] };

        var restored = PlayerAggregate.Restore(state);

        // 非整数向下取整自愈；已耗尽（取整后为 0）与负值（不得翻转为 -1 无限语义）的行动安全出队。
        var action = Assert.Single(restored.Actions);
        Assert.Equal(2, action.Intent.RepeatCount);
        Assert.Equal(1, action.RemainingRepeats);
    }

    /// <summary>
    /// 快照中未定义的队列状态枚举值恢复时必须拒绝，防止坏数据进入运行时。
    /// </summary>
    [Fact]
    public void RestoreRejectsUndefinedActionStatus()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var state = player.ExportState() with
        {
            Actions = [new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, (ActionQueueStatus)99, 0, 1, 0)]
        };

        var error = Assert.Throws<DomainException>(() => PlayerAggregate.Restore(state));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 快照中重复的行动实例 Id 恢复时必须拒绝。
    /// </summary>
    [Fact]
    public void RestoreRejectsDuplicateActionIds()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var id = Guid.NewGuid();
        var action = new QueuedActionState(id, new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, ActionQueueStatus.Pending, 0, 1, 0);
        var state = player.ExportState() with { Actions = [action, action] };

        var error = Assert.Throws<DomainException>(() => PlayerAggregate.Restore(state));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 收益溢出为非有限值（double.MaxValue 累加溢出）时必须在写入进度前快速失败：
    /// 修为与 LastSimulatedAt 均不得被部分突变。
    /// </summary>
    [Fact]
    public void SimulationRejectsNonFiniteRewardBeforeMutatingProgress()
    {
        var catalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.overflow"), "溢出", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerSecond, double.MaxValue)])
        ]);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("test.overflow")), Baseline, catalog);

        var error = Assert.Throws<DomainException>(() => SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog));

        Assert.Equal(DomainErrorCode.InvariantViolation, error.Code);
        Assert.Equal(0, player.Progression.Experience);
        Assert.Equal(Baseline, player.LastSimulatedAt);
    }

    /// <summary>
    /// 触及流水号差集：操作前后 DirtySectionMarks 差分得到「本次新增标脏」分段；
    /// 重复标脏同段戳号继续前进（已标脏段本次重标仍计入触及），ConsumeDirtySections 不影响流水号。
    /// </summary>
    [Fact]
    public void DirtySectionMarks_TrackPerOperationTouchedSections()
    {
        var player = PlayerAggregate.Restore(
            PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a").ExportState());
        Assert.Empty(player.DirtySectionMarks);
        var marksBefore = new Dictionary<PlayerSectionKind, long>(player.DirtySectionMarks);

        // 操作 1：入队行动 → ActionQueue 出现在差集
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2), Baseline, ActionCatalog.Default);
        var touched = player.DirtySectionMarks
            .Where(pair => !marksBefore.TryGetValue(pair.Key, out var previous) || previous != pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet();
        Assert.Equal([PlayerSectionKind.ActionQueue], touched);

        // 模拟刷盘消费：脏集合清空但流水号保留（网络增量不得消费 DirtySections）
        Assert.NotEmpty(player.ConsumeDirtySections());
        Assert.NotEmpty(player.DirtySectionMarks);

        // 操作 2：再次标脏同段（已标脏过不算豁免）→ 戳号前进仍被差集捕获
        var marksBefore2 = new Dictionary<PlayerSectionKind, long>(player.DirtySectionMarks);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        player.MarkDirty(PlayerSectionKind.Settings);
        var touched2 = player.DirtySectionMarks
            .Where(pair => !marksBefore2.TryGetValue(pair.Key, out var previous) || previous != pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet();
        Assert.Equal([PlayerSectionKind.ActionQueue, PlayerSectionKind.Settings], touched2);

        // 事务回滚重置流水号历史：回滚点之后的标脏视为未发生
        player.RestoreFrom(player.ExportState());
        Assert.Empty(player.DirtySectionMarks);
    }

    /// <summary>
    /// 离线在线状态切换重置斗法离线账本时，Duel 分段必须标脏：重置结果要随刷盘落库。
    /// </summary>
    [Fact]
    public void SetPresenceOffline_MarksDuelSectionForOfflineReportReset()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.ConsumeDirtySections(); // 新建聚合自带全段脏标记，先清空再断言操作增量
        player.SetPresence(PresenceState.Online);
        Assert.DoesNotContain(PlayerSectionKind.Duel, player.DirtySections);

        player.SetPresence(PresenceState.Offline);
        Assert.Contains(PlayerSectionKind.Duel, player.DirtySections);
        Assert.Contains(PlayerSectionKind.Settings, player.DirtySections);
    }

}
