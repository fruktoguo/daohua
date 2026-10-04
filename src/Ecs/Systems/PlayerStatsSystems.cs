using DaoHua.Ecs.Components;
using Domain.Player;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Systems;

/// <summary>指定行动账本先入账；没有已建立的账本时不隐式建账。</summary>
[SystemOrder(100)]
public sealed class ActionStatsSystem : YuoSystem<PlayerStatsInboxComponent, PlayerDuelComponent>, IRecordPlayerStats
{
    /// <summary>Tag 派发入口：指定行动账本已建立时入账并刷新结束时刻；没有账本不隐式建账。</summary>
    public override void Run(PlayerStatsInboxComponent inbox, PlayerDuelComponent duel)
    {
        if (inbox.ActionInstanceId is not { } instanceId
            || !duel.Duel.ActionReports.TryGetValue(instanceId, out var report)) return;
        inbox.Delta.ApplyTo(report);
        report.EndedAtUnixSeconds = inbox.AtUnixSeconds;
        report.MarkExportDirty();
    }
}

/// <summary>离线会话账本第二个入账；在线收益不写入离线账本。</summary>
[SystemOrder(200)]
public sealed class OfflineStatsSystem : YuoSystem<PlayerStatsInboxComponent, PlayerDuelComponent>, IRecordPlayerStats
{
    /// <summary>Tag 派发入口：玩家处于离线态时入账离线会话账本；在线收益不写入。</summary>
    public override void Run(PlayerStatsInboxComponent inbox, PlayerDuelComponent duel)
    {
        if (inbox.Player!.Presence != PresenceState.Offline) return;
        var report = duel.Duel.OfflineReport;
        inbox.Delta.ApplyTo(report);
        report.EndedAtUnixSeconds = inbox.AtUnixSeconds;
        report.MarkExportDirty();
    }
}

/// <summary>每日账本最后入账；先切换 UTC+8 自然日，完成三账本记账后只标脏一次。</summary>
[SystemOrder(300)]
public sealed class DailyStatsSystem : YuoSystem<PlayerStatsInboxComponent, PlayerDuelComponent>, IRecordPlayerStats
{
    /// <summary>Tag 派发入口：按 UTC+8 归日入账每日账本；三账本记账完成后只标脏一次 Duel 分段。</summary>
    public override void Run(PlayerStatsInboxComponent inbox, PlayerDuelComponent duel)
    {
        var report = duel.Duel.DailyReport;
        var dayIndex = (long)Math.Floor((inbox.AtUnixSeconds + 28800) / 86400);
        if (report.DayIndex != dayIndex) report.ResetBucket(dayIndex, inbox.AtUnixSeconds);
        inbox.Delta.ApplyTo(report);
        report.EndedAtUnixSeconds = inbox.AtUnixSeconds;
        report.MarkExportDirty();
        inbox.Player!.MarkDirty(PlayerSectionKind.Duel);
    }
}
