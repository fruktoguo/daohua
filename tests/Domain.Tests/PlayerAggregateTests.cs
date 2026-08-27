namespace Domain.Tests;

public sealed class PlayerAggregateTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void CannotAdvanceBackwards()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        Assert.Throws<DomainException>(() => SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(-1), ActionCatalog.Default));
    }

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
