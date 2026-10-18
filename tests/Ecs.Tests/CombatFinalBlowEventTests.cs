using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using DaoHua.Ecs;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 波次清空帧回归：致死一击必须随相位切换保留进搜寻快照。
/// 终局结算与 combat→search 相位切换在同一次补算步进内完成，若新建搜寻状态丢弃
/// <c>RecentEvents</c>，客户端拿到的下一条快照里敌人已清空、事件已归零，
/// 击杀那一次的伤害数字与受击反馈永远不会出现。
/// </summary>
public sealed class CombatFinalBlowEventTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("10000000-0000-4000-8000-00000000bb01");
    private static readonly Guid AccountIdValue = Guid.Parse("20000000-0000-4000-8000-00000000bb01");
    private const string ActionId = "combat.hunt-qi-beast";

    private static PlayerAggregateState CreateState(GameDataCatalog catalog)
    {
        var state = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "终局帧", SeedTime, catalog.Fingerprint).ExportState();
        // 高阶境界保证一波在数十秒内清空：本用例只关心清波帧的事件承载，不测战斗平衡。
        return state with { Progression = state.Progression with { RealmLevel = 11 } };
    }

    [Fact]
    public async Task WaveClearSnapshotCarriesLethalHitAndNextWaveClearsIt()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(CreateState(catalog));
        runtime.EnqueueAction(PlayerIdValue, new ActionId(ActionId), -1);

        CombatExpeditionState? cleared = null;
        var at = SeedTime;
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < 600 && cleared is null; i++)
        {
            at = at.AddSeconds(1);
            await runtime.RunPlayerOnceAsync(PlayerIdValue, at);
            var snapshot = runtime.ExportSnapshot(PlayerIdValue).State;
            if (snapshot.Actions.Count == 0 || snapshot.Actions[0].Expedition is not { } expedition) continue;
            var key = $"{expedition.Phase}/enemies={expedition.Enemies?.Count ?? -1}/events={expedition.RecentEvents?.Count ?? -1}";
            seen[key] = seen.GetValueOrDefault(key) + 1;
            // 清波后进入搜寻相位：敌方阵容已清空，只剩终局帧事件承接击杀跳字。
            if (expedition.Phase == CombatExpeditionPhase.Search
                && expedition.Enemies is not { Count: > 0 }
                && expedition.RecentEvents is { Count: > 0 } events
                && events.Any(e => e.Damage > 0 && e.TargetHitpointsAfter <= 0))
            {
                cleared = expedition;
            }
        }

        Assert.True(cleared is not null,
            "未观察到清波后的搜寻快照。相位分布：" + string.Join("；", seen.Select(pair => $"{pair.Key}×{pair.Value}")));
        Assert.True(cleared!.RecentEvents!.Any(e => e.Damage > 0 && e.TargetHitpointsAfter <= 0),
            "波次清空的搜寻快照没有携带致死一击事件，客户端无法弹出击杀伤害数字。");

        // 下一波入场必须清空上一波的终局事件：新一波敌人单位序号从 0 重新编号，
        // 残留事件会命中新目标重放幽灵跳字与受击。
        var lethal = cleared.RecentEvents!;
        CombatExpeditionState? nextCombat = null;
        for (var i = 0; i < 60 && nextCombat is null; i++)
        {
            at = at.AddSeconds(1);
            await runtime.RunPlayerOnceAsync(PlayerIdValue, at);
            var snapshot = runtime.ExportSnapshot(PlayerIdValue).State;
            if (snapshot.Actions.Count == 0 || snapshot.Actions[0].Expedition is not { } expedition) continue;
            if (expedition.Phase == CombatExpeditionPhase.Combat) nextCombat = expedition;
        }

        Assert.NotNull(nextCombat);
        Assert.DoesNotContain(nextCombat!.RecentEvents ?? [], e => lethal.Contains(e));
    }
}
