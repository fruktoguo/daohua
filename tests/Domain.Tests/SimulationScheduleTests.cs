namespace Domain.Tests;

/// <summary>
/// 可见事件边界推导门禁：宿主服务按该边界唤醒推送，
/// 边界必须落在真实事件时刻上，否则战斗表现与结算脱节。
/// </summary>
public sealed class SimulationScheduleTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate NewPlayer(GameDataCatalog catalog, string name = "甲")
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, catalog.Fingerprint);

    [Fact]
    public void CombatPhaseBoundaryLandsOnEarliestAttackInstant()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1),
            Baseline, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, 1,
                15.472613, 113, 60, 16, 15, 0, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit",
                    new CombatantSnapshot(362, 4.752941176470588, 15.997984378936762, 7, 0, 1))])
            {
                PlayerState = new CombatantSnapshot(113, 60, 16, 15, 0)
            });
        var restored = PlayerAggregate.Restore(exported with { Actions = [action] });

        var boundary = SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog);

        // 敌人 15.998 先于玩家 16.0 出手：边界 = 模拟时钟 + (15.998 - 15.4726)。
        Assert.NotNull(boundary);
        Assert.Equal(15.997984378936762 - 15.472613, (boundary!.Value - Baseline).TotalSeconds, 6);
    }

    [Fact]
    public void SearchPhaseBoundaryLandsOnPhaseEnd()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1),
            Baseline, ActionQueueStatus.Running, 2, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Search, 2, 5, 1,
                0, 100, 60, 0, 0, 0, CombatOutcome.Running, 1, null));
        var restored = PlayerAggregate.Restore(exported with { Actions = [action] });

        var boundary = SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog);

        Assert.Equal(Baseline.AddSeconds(3), boundary);
    }

    [Fact]
    public void TimedActionBoundaryLandsOnCompletion()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("cultivation.draw-qi"), 1),
            Baseline, ActionQueueStatus.Running, 4, 1, 0, null);
        var restored = PlayerAggregate.Restore(exported with { Actions = [action] });

        var boundary = SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog);

        var duration = catalog.Actions.Get(new ActionId("cultivation.draw-qi")).DurationSeconds;
        Assert.Equal(Baseline.AddSeconds(duration - 4), boundary);
    }

    [Fact]
    public void IdlePlayerHasNoBoundaryButBuffExpiryCounts()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        Assert.Null(SimulationSchedule.NextVisibleBoundaryUtc(player, catalog));

        var expiryUnix = Baseline.AddSeconds(42).ToUnixTimeMilliseconds() / 1000d;
        player.Buffs.Active.Add(new ActiveBuff("buff.test", expiryUnix, 1, "测试增益", null, 0, 0, 0, 0));

        Assert.Equal(Baseline.AddSeconds(42), SimulationSchedule.NextVisibleBoundaryUtc(player, catalog));
    }

    [Fact]
    public void DuelPendingPhaseYieldsNoBoundary()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true),
            Baseline, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.DuelPending, 3, 0, 1,
                0, 100, 60, 0, 0, 0, CombatOutcome.Running, 1, null));
        var restored = PlayerAggregate.Restore(exported with { Actions = [action] });

        // 匹配等待由运行时撮合处理，不排模拟边界（宿主按 1 秒兜底节拍扫描）。
        Assert.Null(SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog));
    }

    [Fact]
    public void PendingHeadActionSchedulesImmediateBoundaryEvenWhenQueuePaused()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("cultivation.draw-qi"), 1),
            Baseline, ActionQueueStatus.Pending, 0, 1, 0, null);
        var restored = PlayerAggregate.Restore(exported with
        {
            Actions = [action],
            Settings = exported.Settings with { QueuePaused = true }
        });

        Assert.Equal(Baseline, SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog));
    }

    [Fact]
    public void TimedActionBoundaryAccountsForCarrySeconds()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = NewPlayer(catalog);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("cultivation.draw-qi"), -1),
            Baseline, ActionQueueStatus.Running, 0, -1, 0, null);
        var restored = PlayerAggregate.Restore(exported with
        {
            Actions = [action],
            Progression = exported.Progression with { SimulationCarrySeconds = 0.4 }
        });

        var duration = catalog.Actions.Get(new ActionId("cultivation.draw-qi")).DurationSeconds;
        var boundary = SimulationSchedule.NextVisibleBoundaryUtc(restored, catalog);
        Assert.NotNull(boundary);
        Assert.Equal(duration - 0.4, (boundary!.Value - Baseline).TotalSeconds, 6);
    }
}
