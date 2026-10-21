using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 参与引用（ActionParticipation）在聚合层的恢复与入队不变量。
/// 这些用例只经 PlayerAggregate 的公共入口，不涉及任何 ECS 实体、分片或运行时，
/// 因此归领域层回归；ECS 侧另有适配器边界门禁覆盖实体投影。
/// </summary>
public sealed class PlayerParticipationQueueTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>参与引用只允许一个、只允许出现在队首且必须带合法收尾时刻，任何违规形态读档即拒绝。</summary>
    [Theory]
    [InlineData(0)] // 257 个个人行动，不能冒充参与额外槽位。
    [InlineData(1)] // 即使总数未越界，也只允许一个参与引用。
    [InlineData(2)] // 队首不是 Running，次位不得持有参与引用。
    [InlineData(3)] // 收尾时刻为负。
    [InlineData(4)] // 队首带参与引用却仍是 Pending。
    [InlineData(5)] // 队首带参与引用却仍是 Blocked。
    [InlineData(6)] // 次位不得为 Running。
    [InlineData(7)] // 重复的行动实例 Id。
    public void RestoreRejectsInvalidParticipationCapacityOrQueueStatus(int invalidCase)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions);
        var spec = CreateParticipationSpec(player, catalog);
        Assert.NotNull(player.StartActionParticipation(spec, From, catalog.Actions));
        var state = player.ExportState();
        var actions = state.Actions.ToArray();
        switch (invalidCase)
        {
            case 0:
                actions[0] = actions[0] with { Participation = null };
                break;
            case 1:
                actions = actions.Take(2).ToArray();
                actions[1] = actions[1] with { Participation = actions[0].Participation };
                break;
            case 2:
                actions[0] = actions[0] with { Participation = null, Status = ActionQueueStatus.Pending };
                actions[1] = actions[1] with { Participation = state.Actions[0].Participation, Status = ActionQueueStatus.Running };
                break;
            case 3:
                actions[0] = actions[0] with { Participation = actions[0].Participation! with { EndsAtUnixSeconds = -1 } };
                break;
            case 4:
                actions[0] = actions[0] with { Status = ActionQueueStatus.Pending };
                break;
            case 5:
                actions[0] = actions[0] with { Status = ActionQueueStatus.Blocked };
                break;
            case 6:
                actions[1] = actions[1] with { Status = ActionQueueStatus.Running };
                break;
            case 7:
                actions = [.. actions, actions[1] with { Id = Guid.NewGuid() }];
                break;
        }
        Assert.Equal(DomainErrorCode.InvalidArgument,
            Assert.Throws<DomainException>(() => PlayerAggregate.Restore(state with { Actions = actions })).Code);
    }

    /// <summary>已持有参与引用时，插队入队不得重复或顶掉该引用，且失败后聚合逐字节不变。</summary>
    [Fact]
    public void PriorityEnqueueCannotDuplicateOrDisplaceParticipationReference()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var spec = CreateParticipationSpec(player, catalog);
        Assert.NotNull(player.StartActionParticipation(spec, From, catalog.Actions));
        var before = JsonSerializer.Serialize(player.ExportState());
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => player.EnqueuePriority(
            new(spec.ActionId, -1, spec.NodeId), From, catalog.Actions,
            participation: new ActionParticipationBinding(spec, double.PositiveInfinity))).Code);
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => player.EnqueuePriority(
            new(spec.ActionId), From, catalog.Actions)).Code);
        Assert.Equal(before, JsonSerializer.Serialize(player.ExportState()));
    }

    private static ActionParticipationSpec CreateParticipationSpec(PlayerAggregate player, GameDataCatalog catalog)
        => new("session.capacity", "encounter.qi-beast", "node.test", 97, 0, From.ToUnixTimeSeconds(),
            [player.CaptureActionParticipantSnapshot(catalog)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "队列回归", From, catalog.Fingerprint);
        // 种子按「已断线存档」播种：装载规范化对持久化 Online 一律转离线，
        // 离线种子让 expected=Restore(种子) 镜像与实体逐位一致。
        player.SetPresence(PresenceState.Offline, From);
        return player;
    }
}
