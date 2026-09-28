using Domain.Simulation;

namespace Domain.Player;

/// <summary>
/// 玩家战斗账本与掉落暂存的聚合级记账原语：行动报告建账/记账/归档与斗法暂存物品
/// 进出都是纯领域状态操作，聚合（PlayerDuel 分部）与 ECS 上下文共用同一实现。
/// </summary>
public static class ActionReportBook
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

    /// <summary>暂存掉落与聚合入口共用同一账本和标脏顺序。</summary>
    public static void StageCombatItem(IPlayerResourceContext player, ItemId itemId, double quantity, double acquiredAtUnixSeconds)
    {
        if (!itemId.IsValid || !double.IsFinite(quantity) || quantity <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "暂存物品无效。");
        player.Duel.StagedItems.Add(new(itemId.Value, quantity, acquiredAtUnixSeconds));
        player.RecordStats(player.Actions.Count > 0 ? player.Actions[0].Id : (Guid?)null, acquiredAtUnixSeconds,
            report => report.AddItem(itemId, quantity));
        player.MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>斗法掉落移除与败绩结算共用原暂存账本顺序。</summary>
    public static void RemoveStagedCombatItems(IPlayerResourceContext player, IReadOnlyList<DuelStagedItem> snapshot, double atUnixSeconds)
    {
        if (snapshot.Count == 0) return;
        foreach (var item in snapshot)
        {
            var index = player.Duel.StagedItems.IndexOf(item);
            if (index >= 0) player.Duel.StagedItems.RemoveAt(index);
        }
        var headId = player.Actions.Count > 0 ? player.Actions[0].Id : (Guid?)null;
        player.RecordStats(headId, atUnixSeconds, report =>
        {
            foreach (var item in snapshot)
                report.AddItemLost(new ItemId(item.ItemId), item.Quantity);
        });
        player.MarkDirty(PlayerSectionKind.Duel);
    }
}
