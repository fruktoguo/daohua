namespace Domain.Simulation;

public static partial class SimulationEngine
{
    /// <summary>共享行动、离线与每日三账本记账；导出脏标与日期切换随实际入账更新。</summary>
    public static void RecordActionStats(IPlayerResourceContext player, Guid? actionInstanceId, double atUnixSeconds, Action<PlayerBattleReport> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (actionInstanceId is { } instanceId
            && player.Duel.ActionReports.TryGetValue(instanceId, out var actionReport))
        {
            apply(actionReport);
            actionReport.EndedAtUnixSeconds = atUnixSeconds;
            actionReport.MarkExportDirty();
        }
        if (player.Presence == PresenceState.Offline)
        {
            apply(player.Duel.OfflineReport);
            player.Duel.OfflineReport.EndedAtUnixSeconds = atUnixSeconds;
            player.Duel.OfflineReport.MarkExportDirty();
        }
        var dayIndex = (long)Math.Floor((atUnixSeconds + 28800) / 86400);
        if (player.Duel.DailyReport.DayIndex != dayIndex)
            player.Duel.DailyReport.ResetBucket(dayIndex, atUnixSeconds);
        apply(player.Duel.DailyReport);
        player.Duel.DailyReport.EndedAtUnixSeconds = atUnixSeconds;
        player.Duel.DailyReport.MarkExportDirty();
        player.MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>共享行动实例建账，已存在的账本继续使用。</summary>
    public static void EnsureActionReport(IPlayerResourceContext player, Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
    {
        if (player.Duel.ActionReports.ContainsKey(instanceId)) return;
        player.Duel.GetOrCreateReport(instanceId, actionId.Value, actionName, now.ToUnixTimeMilliseconds() / 1000d);
        player.MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>共享行动出队归档，按既有次序保留有内容的账本并裁剪数量。</summary>
    public static void ArchiveActionReport(IPlayerResourceContext player, Guid instanceId, double endedAtUnixSeconds, int maxBattleReports)
    {
        if (!player.Duel.ActionReports.Remove(instanceId, out var report)) return;
        report.EndedAtUnixSeconds = endedAtUnixSeconds;
        report.MarkExportDirty();
        if (report.HasContent)
        {
            player.Duel.Reports.Insert(0, report);
            while (player.Duel.Reports.Count > maxBattleReports)
                player.Duel.Reports.RemoveAt(player.Duel.Reports.Count - 1);
        }
        player.MarkDirty(PlayerSectionKind.Duel);
    }
}
