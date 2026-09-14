namespace Domain.Tests;

/// <summary>
/// 玩家聚合测试：快照往返、队列推进、重复次数与状态恢复的约束回归。
/// </summary>
public sealed class PlayerAggregateTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 验证 <c>RestoreRoundTripKeepsStateAndQueue</c>（Restore Round Trip Keeps State And Queue）所描述的领域行为。
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
        Assert.Equal(player.Progression.Qi, restored.Progression.Qi);
        Assert.Single(restored.Actions);
        Assert.Equal(action.Id, restored.Actions[0].Id);
        Assert.Equal(player.Actions[0].ProgressSeconds, restored.Actions[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证 <c>AutoUseRemainingDurationPersistsAndClearsWhenDisabledOrSwitched</c>（Auto Use Remaining Duration Persists And Clears When Disabled Or Switched）所描述的领域行为。
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
    /// 验证 <c>AdvanceToIsDeterministicForSameInput</c>（Advance To Is Deterministic For Same Input）所描述的领域行为。
    /// </summary>
    [Fact]
    public void AdvanceToIsDeterministicForSameInput()
    {
        var first = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var second = PlayerAggregate.Create(first.PlayerId, first.AccountId, "甲", Baseline, "catalog-a");
        var intent = new ActionIntent(new ActionId("cultivation.draw-qi"), 3);
        first.Enqueue(intent, Baseline, ActionCatalog.Default);
        second.Enqueue(intent, Baseline, ActionCatalog.Default);

        var firstResult = SimulationEngine.AdvanceTo(first, Baseline.AddSeconds(31), ActionCatalog.Default);
        var secondResult = SimulationEngine.AdvanceTo(second, Baseline.AddSeconds(31), ActionCatalog.Default);

        Assert.Equal(firstResult.CompletedRepeats, secondResult.CompletedRepeats);
        Assert.Equal(firstResult.AssetDeltas, secondResult.AssetDeltas);
        Assert.All(firstResult.Events, entry => Assert.InRange(entry.At, Baseline, Baseline.AddSeconds(31)));
        Assert.Equal(first.LastSimulatedAt, second.LastSimulatedAt);
        Assert.Equal(first.Progression.CompletedActions, second.Progression.CompletedActions);
        Assert.Equal(first.Actions.Count, second.Actions.Count);
        if (first.Actions.Count > 0)
            Assert.Equal(first.Actions[0].ProgressSeconds, second.Actions[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证 <c>CannotAdvanceBackwards</c>（Cannot Advance Backwards）所描述的领域行为。
    /// </summary>
    [Fact]
    public void CannotAdvanceBackwards()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        Assert.Throws<DomainException>(() => SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(-1), ActionCatalog.Default));
    }

    /// <summary>
    /// 验证 <c>EmptyQueueClockAdvanceDoesNotCreateStateVersion</c>（Empty Queue Clock Advance Does Not Create State Version）所描述的领域行为。
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
    /// 验证 <c>PartialActionAdvanceIsReportedAsChanged</c>（Partial Action Advance Is Reported As Changed）所描述的领域行为。
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
        Assert.Equal(1, player.Progression.Qi);
    }

    /// <summary>
    /// 验证 <c>InfiniteActionKeepsQueueEntryAfterCompletedRounds</c>（Infinite Action Keeps Queue Entry After Completed Rounds）所描述的领域行为。
    /// </summary>
    [Fact]
    public void InfiniteActionKeepsQueueEntryAfterCompletedRounds()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var action = player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), Baseline, ActionCatalog.Default);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(17), ActionCatalog.Default);

        Assert.Equal(2, result.CompletedRepeats);
        Assert.Single(player.Actions);
        Assert.Equal(action.Id, player.Actions[0].Id);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);
    }

    /// <summary>
    /// 验证 <c>RestoreKeepsInfiniteActionState</c>（Restore Keeps Infinite Action State）所描述的领域行为。
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
    /// 验证 <c>InfiniteZeroDurationActionIsRejected</c>（Infinite Zero Duration Action Is Rejected）所描述的领域行为。
    /// </summary>
    [Fact]
    public void InfiniteZeroDurationActionIsRejected()
    {
        var instant = new ActionCatalog([
            new ActionDefinition(new ActionId("test.instant"), "瞬时", ActionKind.Alchemy, 0, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>())
        ]);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var error = Assert.Throws<DomainException>(() => player.Enqueue(new ActionIntent(new ActionId("test.instant"), -1), Baseline, instant));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 验证 <c>EnqueueRejectsNonIntegerRepeatCount</c>（Enqueue Rejects Non Integer Repeat Count）所描述的领域行为。
    /// </summary>
    [Fact]
    public void EnqueueRejectsNonIntegerRepeatCount()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var error = Assert.Throws<DomainException>(() => player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2.5), Baseline, ActionCatalog.Default));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 验证 <c>RestoreHealsNonIntegerRepeatCountsFromLegacySnapshots</c>（Restore Heals Non Integer Repeat Counts From Legacy Snapshots）所描述的领域行为。
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
    /// 验证 <c>EnqueueCopiesMutableIntentParameters</c>（Enqueue Copies Mutable Intent Parameters）所描述的领域行为。
    /// </summary>
    [Fact]
    public void EnqueueCopiesMutableIntentParameters()
    {
        var parameters = new Dictionary<string, double> { ["power"] = 1 };
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var action = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1, Parameters: parameters), Baseline, ActionCatalog.Default);

        parameters["power"] = 99;

        Assert.Equal(1, action.Intent.Parameters!["power"]);
    }

    /// <summary>
    /// 验证 <c>RestoreRejectsUndefinedActionStatus</c>（Restore Rejects Undefined Action Status）所描述的领域行为。
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
    /// 验证 <c>RestoreRejectsDuplicateActionIds</c>（Restore Rejects Duplicate Action Ids）所描述的领域行为。
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
    /// 验证 <c>SimulationRejectsNonFiniteRewardBeforeMutatingProgress</c>（Simulation Rejects Non Finite Reward Before Mutating Progress）所描述的领域行为。
    /// </summary>
    [Fact]
    public void SimulationRejectsNonFiniteRewardBeforeMutatingProgress()
    {
        var catalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.overflow"), "溢出", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), double.MaxValue)
        ]);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("test.overflow")), Baseline, catalog);

        var error = Assert.Throws<DomainException>(() => SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog));

        Assert.Equal(DomainErrorCode.InvariantViolation, error.Code);
        Assert.Equal(0, player.Progression.Qi);
        Assert.Equal(Baseline, player.LastSimulatedAt);
    }

    /// <summary>
    /// 验证 <c>RestoreFromReplacesMutableStateButKeepsIdentity</c>（Restore From Replaces Mutable State But Keeps Identity）所描述的领域行为。
    /// </summary>
    [Fact]
    public void RestoreFromReplacesMutableStateButKeepsIdentity()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var state = player.ExportState() with
        {
            DisplayName = "乙",
            StateVersion = 9,
            Progression = new PlayerProgressionState(2, 99, 3, 99, 7)
        };
        player.RestoreFrom(state);

        Assert.Equal("乙", player.DisplayName);
        Assert.Equal(9, player.StateVersion);
        Assert.Equal(99, player.Progression.Qi);
        Assert.Empty(player.DirtySections);
    }
}
