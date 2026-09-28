using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using DaoHua.Ecs;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 战斗中下线→恢复→再推进的回归用例（线上玩家 13c49fce 卡死复现）。
/// 根因：ECS 移植曾把每个战斗自然边界放大为一次外层 Tick，长时间连续狩猎补算
/// 会合法耗尽 MaxTransitionsPerAdvance；修复后战斗段与 OOP 同粒度按窗口整段结算。
/// </summary>
public sealed class CombatRestoreReproTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("10000000-0000-4000-8000-00000000aa01");
    private static readonly Guid AccountIdValue = Guid.Parse("20000000-0000-4000-8000-00000000aa01");
    private const string ActionId = "combat.hunt-qi-beast";

    private static PlayerAggregateState CreateState(GameDataCatalog catalog)
        => PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "战斗恢复", SeedTime, catalog.Fingerprint).ExportState();

    [Fact]
    public async Task MidCombatSnapshotRestoreThenAdvanceDoesNotDeadlock()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var state = CreateState(catalog);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(state);
        runtime.EnqueueAction(PlayerIdValue, new ActionId(ActionId), -1);
        // 推进到战斗中（搜寻若干秒后进入交战）
        await runtime.RunPlayerOnceAsync(PlayerIdValue, SeedTime.AddSeconds(40));
        var midCombat = runtime.ExportSnapshot(PlayerIdValue);
        Assert.Equal(CombatExpeditionPhase.Combat, midCombat.State.Actions[0].Expedition?.Phase);

        // 模拟下线重载：快照解码 → 全新运行时恢复 → 再推进
        var restored = PlayerAggregate.Restore(midCombat.State).ExportState();
        var lastSimulated = midCombat.State.LastSimulatedAt;
        using var runtime2 = new EcsGameRuntime(catalog, lastSimulated);
        runtime2.CreatePlayer(restored, []);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await runtime2.RunPlayerOnceAsync(PlayerIdValue, lastSimulated.AddSeconds(30), cts.Token);

        var after = runtime2.ExportSnapshot(PlayerIdValue);
        Assert.True(after.State.LastSimulatedAt >= lastSimulated.AddSeconds(30));
    }

    /// <summary>
    /// 长时间离线狩猎补算回归：连续战斗窗口的边界总量远超最大转换数按请求计的预算。
    /// 战斗段按窗口整段结算后，30 小时补算只需数千次 Tick；若退回逐边界 Tick 会立即触顶。
    /// </summary>
    [Fact]
    public async Task LongOfflineCombatCatchUpDoesNotExhaustTransitionBudget()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        async Task<EcsPlayerSnapshot> RunOnce()
        {
            using var runtime = new EcsGameRuntime(catalog, SeedTime);
            runtime.CreatePlayer(CreateState(catalog));
            runtime.EnqueueAction(PlayerIdValue, new ActionId(ActionId), -1);
            var target = SeedTime.AddHours(30);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await runtime.RunPlayerOnceAsync(PlayerIdValue, target, cts.Token);
            return runtime.ExportSnapshot(PlayerIdValue);
        }

        var first = await RunOnce();
        Assert.True(first.State.LastSimulatedAt >= SeedTime.AddHours(30));
        // 无限循环行动不得因长补算出队
        Assert.Single(first.State.Actions);
        Assert.Equal(-1, first.State.Actions[0].RemainingRepeats);
        // 非空转证据：30 小时狩猎必须产生真实的击杀/败北/完成轮次
        var activity = first.State.Progression;
        Assert.True(activity.NormalKills + activity.EliteKills + activity.BossKills + activity.TotalDefeats > 0
            || first.State.Actions[0].CompletedRepeats > 0,
            $"狩猎 30h 未产生战斗活动（kills={activity.NormalKills},defeats={activity.TotalDefeats},repeats={first.State.Actions[0].CompletedRepeats}）");

        // 同一输入两次补算必须逐位一致（确定性回归）
        var second = await RunOnce();
        Assert.Equal(first.State.StateVersion, second.State.StateVersion);
        Assert.Equal(first.State.LastSimulatedAt, second.State.LastSimulatedAt);
        Assert.Equal(first.State.Progression.Experience, second.State.Progression.Experience);
        Assert.Equal(first.State.Progression.NormalKills, second.State.Progression.NormalKills);
        Assert.Equal(first.State.Progression.TotalDefeats, second.State.Progression.TotalDefeats);
        Assert.Equal(first.State.Progression.CompletedActions, second.State.Progression.CompletedActions);
    }
}
