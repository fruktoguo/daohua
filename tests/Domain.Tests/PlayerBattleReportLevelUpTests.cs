using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Domain.Tests;

public sealed class PlayerBattleReportLevelUpTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BattleReport_AddLevelUp_MergesSameTargetAndPreservesDistinct()
    {
        var report = new PlayerBattleReport();
        report.AddLevelUp(new PlayerReportLevelUp("ability", "ability.feiyan-gong", 1, 5));
        report.AddLevelUp(new PlayerReportLevelUp("ability", "ability.feiyan-gong", 5, 6));
        report.AddLevelUp(new PlayerReportLevelUp("ability", "ability.feiyan-gong", 6, 101));
        report.AddLevelUp(new PlayerReportLevelUp("root", "meridians", 22, 23));
        report.AddLevelUp(new PlayerReportLevelUp("root", "meridians", 23, 24));

        Assert.Equal(2, report.LevelUps.Count);
        var feiyan = Assert.Single(report.LevelUps, entry => entry.TargetId == "ability.feiyan-gong");
        Assert.Equal(1, feiyan.FromLevel);
        Assert.Equal(101, feiyan.ToLevel);

        var meridians = Assert.Single(report.LevelUps, entry => entry.TargetId == "meridians");
        Assert.Equal(22, meridians.FromLevel);
        Assert.Equal(24, meridians.ToLevel);
    }

    [Fact]
    public void BattleReport_AddRange_MergesSameTargetAutomatically()
    {
        var report = new PlayerBattleReport();
        var levelUps = new List<PlayerReportLevelUp>
        {
            new("ability", "ability.feiyan-gong", 1, 10),
            new("ability", "ability.feiyan-gong", 10, 50),
            new("root", "bone", 1, 5)
        };
        report.LevelUps.AddRange(levelUps);

        Assert.Equal(2, report.LevelUps.Count);
        var feiyan = Assert.Single(report.LevelUps, entry => entry.TargetId == "ability.feiyan-gong");
        Assert.Equal(1, feiyan.FromLevel);
        Assert.Equal(50, feiyan.ToLevel);
    }

    [Fact]
    public void Restore_ToleratesOver512LevelUpsAndCollapsesOnImport()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "测试", Baseline, "catalog-a");
        var state = player.ExportState();

        // 构造含有超过 512 条未合并升级事件的历史战报（如 698 条）
        var unmergedLevelUps = new List<PlayerReportLevelUp>();
        for (int i = 1; i <= 698; i++)
        {
            unmergedLevelUps.Add(new PlayerReportLevelUp("ability", "ability.feiyan-gong", i, i + 1));
        }
        var tamperedDaily = state.Duel.DailyReport! with { LevelUps = unmergedLevelUps };
        var tamperedDuel = state.Duel with { DailyReport = tamperedDaily };
        var tamperedState = state with { Duel = tamperedDuel };

        var restored = PlayerAggregate.Restore(tamperedState);
        Assert.NotNull(restored.Duel.DailyReport);
        var feiyan = Assert.Single(restored.Duel.DailyReport!.LevelUps, entry => entry.TargetId == "ability.feiyan-gong");
        Assert.Equal(1, feiyan.FromLevel);
        Assert.Equal(699, feiyan.ToLevel);
    }
}
