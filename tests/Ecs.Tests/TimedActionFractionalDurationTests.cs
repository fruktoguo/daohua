using DaoHua.Ecs;
using Domain.Common;
using Domain.Combat;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

public sealed class TimedActionFractionalDurationTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FractionalDurationTimedAction_BulkAdvance_DoesNotExceedMaxTransitions()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(
            new PlayerId(Guid.Parse("30000000-0000-4000-8000-000000000001")),
            new AccountId(Guid.Parse("40000000-0000-4000-8000-000000000001")),
            "加速采伐测试",
            SeedTime,
            catalog.Fingerprint);

        // 设置位于有采伐行动的节点
        player.World.NodeId = "node.peach-forest";
        player.World.EdgeId = null;

        // 设置采伐技艺经验，使其单次耗时成为带小数的非整秒（基准 6 秒 / 加速）
        player.Loadout.TechniqueExperiences["gathering"] = 5000;

        // 入队多次采伐
        player.Enqueue(new(new ActionId("explore.chop-peach-wood"), 300), SeedTime, catalog.Actions);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var playerId = runtime.CreatePlayer(player.ExportState());

        // 离线推进 1 小时
        var target = SeedTime.AddHours(1);
        await runtime.RunPlayerOnceAsync(playerId, target);

        var snapshot = runtime.ExportSnapshot(playerId);
        Assert.True(snapshot.State.Progression.CompletedActions > 0);
        Assert.Equal(target, snapshot.State.LastSimulatedAt);
    }
}
