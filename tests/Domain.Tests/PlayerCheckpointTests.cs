using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 分段级回滚检查点门禁：受保护写操作失败时按基线副本就地写回受影响分段，
/// 聚合导出状态、版本号、脏集合与触及流水号必须与操作前逐位一致（含「部分分段已脏」混合场景）。
/// </summary>
public sealed class PlayerCheckpointTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly PlayerSectionKind[] AllSections = Enum.GetValues<PlayerSectionKind>();

    /// <summary>与运行时相同的基线捕获：逐段捕获不可变副本 + 头标量 + 脏集合与流水戳快照。</summary>
    private static (
        Dictionary<PlayerSectionKind, object> Sections,
        PlayerAggregate.PlayerHeadCheckpoint Head,
        PlayerSectionKind[] Dirty,
        Dictionary<PlayerSectionKind, long> Marks,
        string Json) CaptureBaseline(PlayerAggregate aggregate)
    {
        var sections = new Dictionary<PlayerSectionKind, object>();
        foreach (var section in AllSections)
            sections[section] = aggregate.CaptureSectionCheckpoint(section);
        return (sections,
            aggregate.CaptureHeadCheckpoint(),
            aggregate.DirtySections.ToArray(),
            new Dictionary<PlayerSectionKind, long>(aggregate.DirtySectionMarks),
            JsonSerializer.Serialize(aggregate.ExportState()));
    }

    private static void RestoreBaseline(
        PlayerAggregate aggregate,
        Dictionary<PlayerSectionKind, object> sections,
        PlayerAggregate.PlayerHeadCheckpoint head,
        IReadOnlyCollection<PlayerSectionKind> dirty,
        IReadOnlyDictionary<PlayerSectionKind, long> marks)
    {
        foreach (var section in AllSections)
            aggregate.RestoreSectionCheckpoint(section, sections[section]);
        aggregate.RestoreHeadCheckpoint(head);
        aggregate.RestoreDirtyTracking(dirty, marks);
    }

    /// <summary>
    /// 部分分段操作前已脏、本次操作又弄脏其余分段的混合场景：回滚后导出快照逐位一致，
    /// 脏集合与流水戳精确复原——旧脏保留、新脏撤销。
    /// </summary>
    [Fact]
    public void CheckpointRestoreIsBitwiseIdenticalWithMixedDirtySections()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "回滚", Baseline, "catalog-a");
        // 模拟「上一轮已入队刷盘、本轮部分分段又脏」：清空待刷集合但流水戳保留。
        player.ConsumeDirtySections();
        // 操作前已脏：Progression + Settings + ActionQueue。
        player.Progression.Experience = 5;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.SetLoopEnabled(false);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);
        var baseline = CaptureBaseline(player);
        var versionBefore = player.StateVersion;

        // 本次操作弄脏全部 8 个分段（含已脏分段再次标脏）。
        player.Progression.Experience += 7;
        player.Progression.Experience += 3;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.TravelTo("node.other", 3, 4);
        player.Resources.Hitpoints = 42;
        player.MarkDirty(PlayerSectionKind.Combat);
        player.Buffs.Active.Add(new ActiveBuff("buff.test", 999, 2));
        player.MarkDirty(PlayerSectionKind.Buffs);
        player.LearnAbility(new AbilityId("ability.test-strike"));
        player.ConfigureAutoUse("exploration", 0, "item.qi-gathering-pill", 1, true);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 3), Baseline, ActionCatalog.Default);
        player.StageItem(new ItemId("item.iron-ore"), 2, 100);
        player.Duel.EncounterCharges = 3;
        player.MarkDirty(PlayerSectionKind.Duel);

        RestoreBaseline(player, baseline.Sections, baseline.Head, baseline.Dirty, baseline.Marks);

        // 逐位一致：导出快照序列化字节完全相同（浮点/嵌套文档/队列次序均无漂移）。
        Assert.Equal(baseline.Json, JsonSerializer.Serialize(player.ExportState()));
        Assert.Equal(versionBefore, player.StateVersion);
        // 脏集合：操作前已脏的三分段保留，本次新脏全部撤销。
        Assert.Equal(baseline.Dirty.Length, player.DirtySections.Count);
        Assert.True(baseline.Dirty.ToHashSet().SetEquals(player.DirtySections));
        // 流水戳逐键精确复原（含已被 Consume 但留有戳记的分段）。
        Assert.Equal(baseline.Marks.Count, player.DirtySectionMarks.Count);
        foreach (var (section, mark) in baseline.Marks)
            Assert.Equal(mark, player.DirtySectionMarks[section]);
    }

    /// <summary>回滚后聚合仍是健康可变状态：后续标脏取严格更大的流水戳，触及差集判定不受影响。</summary>
    [Fact]
    public void DirtyTrackingAfterRestoreKeepsMonotonicMarks()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "单调", Baseline, "catalog-a");
        player.ConsumeDirtySections();
        var baseline = CaptureBaseline(player);

        player.Progression.Experience += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        RestoreBaseline(player, baseline.Sections, baseline.Head, baseline.Dirty, baseline.Marks);

        var markCeiling = baseline.Marks.Values.DefaultIfEmpty(0L).Max();
        player.Resources.Hitpoints = 10;
        player.MarkDirty(PlayerSectionKind.Combat);
        // 流水游标不回退：新标脏戳号严格大于基线里任何历史戳，差集判定照常工作。
        Assert.True(player.DirtySectionMarks[PlayerSectionKind.Combat] > markCeiling);
        Assert.True(player.DirtySections.Contains(PlayerSectionKind.Combat));
    }
}
