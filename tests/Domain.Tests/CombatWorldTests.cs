namespace Domain.Tests;

public sealed class CombatWorldTests
{
    [Fact]
    public void CombatAdvanceIsDeterministicAndStopsAtDeath()
    {
        var playerStats = CombatantStats.Create(100, 20, 0, 1);
        var enemyStats = CombatantStats.Create(30, 1, 0, 2);
        var first = CombatEncounterState.Start("encounter.test", 7, playerStats, enemyStats);
        var second = CombatEncounterState.Start("encounter.test", 7, playerStats, enemyStats);

        var firstResult = DeterministicCombatEngine.AdvanceTo(first, 10);
        var secondResult = DeterministicCombatEngine.AdvanceTo(second, 10);

        Assert.Equal(CombatOutcome.PlayerWon, firstResult.Outcome);
        Assert.Equal(firstResult.Events, secondResult.Events);
        Assert.Equal(firstResult.Sequence, secondResult.Sequence);
        Assert.Equal(0, first.Enemy.Hitpoints);
    }

    [Fact]
    public void TravelPlannerUsesShortestDeterministicPath()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("a", "甲", 0, 0));
        graph.AddNode(WorldNode.Create("b", "乙", 1, 0));
        graph.AddNode(WorldNode.Create("c", "丙", 2, 0));
        graph.AddEdge(WorldEdge.Create("ab", "a", "b", 1, 10));
        graph.AddEdge(WorldEdge.Create("bc", "b", "c", 1, 10));
        graph.AddEdge(WorldEdge.Create("ac", "a", "c", 5, 30));

        var plan = graph.Plan("a", "c");
        var progress = new TravelProgress(plan);
        var result = TravelEngine.Advance(graph, progress, 15);

        Assert.Equal(new[] { "ab", "bc" }, plan.EdgeIds);
        Assert.False(result.Completed);
        Assert.Equal("b", result.CurrentNodeId);
        Assert.Equal(15, result.ElapsedSeconds);
        Assert.Equal(1, result.CompletedEdges);
    }
}
