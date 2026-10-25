using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 入天门 후 战斗收益瓶颈回归：本层修为瓶颈必须含灵根乘区（总纲 §7.7.5.1）。
/// 根因：战斗结算曾使用裸配表瓶颈，入天门后击杀收益在裸瓶颈处全部沉淀为底蕴，
/// 玩家永远够不到乘区后的突破门槛（配表 800000 → 五系乘区 3.0 → 2400000）。
/// </summary>
public sealed class HeavenGateCombatExpCapTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("10000000-0000-4000-8000-00000000cc01");
    private static readonly Guid AccountIdValue = Guid.Parse("20000000-0000-4000-8000-00000000cc01");

    /// <summary>
    /// 入天门五系齐全（乘区 3.0）时，击杀收益必须能超过裸配表瓶颈，
    /// 否则玩家停在裸瓶颈无法继续积累到乘区后的突破门槛。
    /// </summary>
    [Fact]
    public async Task CombatExperienceExceedsBareCapAfterEnteringHeavenGate()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var realm = catalog.RealmProgression ?? throw new InvalidOperationException("配表缺少境界进度定义。");
        var bareCap = realm.ExpCapFor(HeavenGateRules.HeavenGateRealmLevel);

        var created = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "天门瓶颈", SeedTime, catalog.Fingerprint).ExportState();
        var entered = created with
        {
            Progression = created.Progression with
            {
                RealmLevel = HeavenGateRules.HeavenGateRealmLevel,
                Experience = bareCap,
                HeavenGateRolled = new LinggenValues(60, 60, 60, 60, 60),
                HeavenGateConfirmed = new LinggenValues(60, 60, 60, 60, 60),
                HeavenGateEntered = true,
            },
        };

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(entered);
        runtime.EnqueueAction(PlayerIdValue, new ActionId("combat.hunt-qi-beast"), -1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await runtime.RunPlayerOnceAsync(PlayerIdValue, SeedTime.AddMinutes(30), cts.Token);

        var progression = runtime.ExportSnapshot(PlayerIdValue).State.Progression;
        Assert.True(progression.NormalKills + progression.EliteKills + progression.BossKills > 0,
            $"30 分钟狩猎未产生击杀（kills={progression.NormalKills}/{progression.EliteKills}/{progression.BossKills}）");
        Assert.True(progression.Experience > bareCap,
            $"入天门后修为被裸瓶颈截断：experience={progression.Experience} 未超过裸瓶颈 {bareCap}");
        Assert.True(progression.Experience <= bareCap * HeavenGateRules.ResolveCultivationMultiplier(
                new LinggenValues(60, 60, 60, 60, 60), true) + 1,
            $"修为越过了乘区后瓶颈：experience={progression.Experience}");
    }

    /// <summary>
    /// 入天门后战斗收益可以真正填满乘区后的突破门槛并成功突破：
    /// 仅仅「不被裸瓶颈截断」还不够，玩家必须能在战斗挂机中打到门槛、再突破。
    /// </summary>
    [Fact]
    public async Task CombatExperienceReachesLinggenCapAndThenBreakthroughSucceeds()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var realm = catalog.RealmProgression ?? throw new InvalidOperationException("配表缺少境界进度定义。");
        var linggen = new LinggenValues(60, 60, 60, 60, 60);
        var capWithLinggen = realm.ExpCapFor(HeavenGateRules.HeavenGateRealmLevel)
            * HeavenGateRules.ResolveCultivationMultiplier(linggen, true);

        var created = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "天门突破", SeedTime, catalog.Fingerprint).ExportState();
        var entered = created with
        {
            Progression = created.Progression with
            {
                RealmLevel = HeavenGateRules.HeavenGateRealmLevel,
                Experience = capWithLinggen - 300,
                HeavenGateRolled = linggen,
                HeavenGateConfirmed = linggen,
                HeavenGateEntered = true,
            },
        };

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(entered);
        runtime.EnqueueAction(PlayerIdValue, new ActionId("combat.hunt-qi-beast"), -1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await runtime.RunPlayerOnceAsync(PlayerIdValue, SeedTime.AddMinutes(30), cts.Token);

        var snapshot = runtime.ExportSnapshot(PlayerIdValue);
        Assert.True(snapshot.State.Progression.Experience >= capWithLinggen,
            $"战斗收益未填满乘区后瓶颈：experience={snapshot.State.Progression.Experience} < {capWithLinggen}");

        // 门槛已满 → 突破必须放行（修前修为永远停在裸瓶颈，此处会抛「修为未满」）。
        var aggregate = PlayerAggregate.Restore(snapshot.State);
        aggregate.Breakthrough(realm);
        Assert.Equal(HeavenGateRules.HeavenGateRealmLevel + 1, aggregate.Progression.RealmLevel);
        Assert.Equal(0d, aggregate.Progression.Experience);
    }
}
