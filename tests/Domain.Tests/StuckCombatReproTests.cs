namespace Domain.Tests;

/// <summary>
/// 线上卡死快照复现：持久化远征状态（战斗中相位、木灵残血、斗法开关开启）
/// 在长时间补算下不得陷入浮点尘埃导致的零消耗状态转换循环，也不得抛出负余量。
/// </summary>
public sealed class StuckCombatReproTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate BuildPersistedCombatPlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, catalog.Fingerprint);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true),
            Baseline,
            ActionQueueStatus.Running,
            0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, unchecked((long)4122577177101012044UL),
                15.472613, 113.48821196563904, 60, 16, 15, 88.20322454180142, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit", 362.3614813405518, 52.89411764705883, 15.997984378936762, 7, 13.111788034361007, 1)]));
        return PlayerAggregate.Restore(exported with
        {
            Actions = [action],
            Combat = new PlayerCombatStateDocument(113.48821196563904, 120, 60, 60, 11, 11, 1.5453399),
        });
    }

    /// <summary>一次性大步长补算路径：模拟时钟必须推进到目标时刻且至少完成一轮讨伐。</summary>
    [Fact]
    public void PersistedCombatExpeditionAdvancesAcrossLongCatchUp()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = BuildPersistedCombatPlayer(catalog);

        // 真实线上跨度：约 9 小时未推进的一次性补算。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(32_000), catalog,
            duel: new DuelAdvanceOptions(MatchingEnabled: true));

        Assert.Equal(Baseline.AddSeconds(32_000), player.LastSimulatedAt);
        Assert.True(result.CompletedRepeats > 0, "长时间补算应完成至少一轮讨伐。");
        Assert.True(player.Progression.NormalKills + player.Progression.EliteKills + player.Progression.BossKills > 0);
    }

    /// <summary>在线小步推进路径：远征战斗计时必须持续增长，不得原地踏步。</summary>
    [Fact]
    public void PersistedCombatExpeditionAdvancesInSmallSteps()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = BuildPersistedCombatPlayer(catalog);

        // 在线逐拍推进：0.25s 步长连续推进 200 拍，覆盖战斗攻击边界与浮点尘埃场景。
        var cursor = Baseline;
        for (var i = 0; i < 200; i++)
        {
            cursor = cursor.AddSeconds(0.25);
            SimulationEngine.AdvanceTo(player, cursor, catalog, duel: new DuelAdvanceOptions(MatchingEnabled: true));
        }
        Assert.Equal(cursor, player.LastSimulatedAt);
        Assert.True(player.Actions[0].Expedition!.BattleElapsedSeconds > 15.472613);
    }
}
