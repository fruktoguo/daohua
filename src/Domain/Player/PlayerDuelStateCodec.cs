namespace Domain.Player;

/// <summary>斗法与行动账本分段的单一文档边界；允许组件直接导出/恢复该分段。</summary>
public static class PlayerDuelStateCodec
{
    /// <summary>按队首实例投影 CurrentReport；保留集合原顺序与未脏账本文档缓存。</summary>
    public static PlayerDuelStateDocument Export(PlayerDuelState state, Guid? headActionId)
    {
        var current = headActionId is { } id ? state.ReportFor(id) : null;
        return new PlayerDuelStateDocument(
            state.EncounterCharges,
            state.CooldownUntilUnixSeconds,
            state.ActiveDuelId,
            state.StagedItems.ToArray(),
            current is null ? null : ExportReportCached(current),
            ExportReportSequence(state.Reports),
            ExportReportSequence(state.ActionReports.Values),
            ExportReportCached(state.OfflineReport),
            ExportReportCached(state.DailyReport));
    }

    /// <summary>在经过存储边界校验后就地恢复分段；热路径不调用此文档操作。</summary>
    public static void Restore(PlayerDuelState state, PlayerDuelStateDocument document)
    {
        state.EncounterCharges = document.EncounterCharges;
        state.CooldownUntilUnixSeconds = document.CooldownUntilUnixSeconds;
        state.ActiveDuelId = document.ActiveDuelId;
        state.StagedItems.Clear();
        state.StagedItems.AddRange(document.StagedItems);
        // 实例账本以 ActiveReports 为准重建；文档 CurrentReport 是冗余导出字段，不参与恢复。
        state.ActionReports.Clear();
        foreach (var document_ in document.ActiveReports ?? [])
        {
            var report = ImportReport(document_);
            if (report.ActionInstanceId != Guid.Empty)
                state.ActionReports[report.ActionInstanceId] = report;
        }
        state.OfflineReport = document.OfflineReport is null
            ? new PlayerBattleReport { ReportId = "offline" }
            : ImportReport(document.OfflineReport);
        state.DailyReport = document.DailyReport is null
            ? new PlayerBattleReport { ReportId = "daily" }
            : ImportReport(document.DailyReport);
        state.Reports.Clear();
        state.Reports.AddRange(document.Reports.Select(ImportReport));
    }

    private static PlayerBattleReportDocument ExportReport(PlayerBattleReport report) => new(
        report.ReportId, report.ActionId, report.ActionName,
        report.StartedAtUnixSeconds, report.EndedAtUnixSeconds,
        report.Experience,
        new PlayerRootExpDocument(report.RootExp.Stamina, report.RootExp.Meridians, report.RootExp.Attack,
            report.RootExp.Bone, report.RootExp.Agility, report.RootExp.Consciousness),
        report.Items.ToArray(), report.DuelWins, report.DuelLosses,
        report.ItemsLost.ToArray(), report.AbilityExp.ToArray(), report.LevelUps.ToArray(),
        report.Cycles, report.Encounters, report.NormalKills, report.EliteKills, report.BossKills,
        report.Deaths, report.DayIndex, report.Foundation, report.CombatExperience);

    /// <summary>逐账本导出：未脏账本复用已登记文档；记账路径仅令被写账本重建。</summary>
    private static PlayerBattleReportDocument ExportReportCached(PlayerBattleReport report)
        => !report.ExportDirty && report.ExportedDocument is { } cached ? cached : report.CacheExported(ExportReport(report));

    /// <summary>按原枚举顺序逐账本导出（插入序/归档序保持逐位一致）。</summary>
    private static PlayerBattleReportDocument[] ExportReportSequence(IEnumerable<PlayerBattleReport> reports)
        => reports.Select(ExportReportCached).ToArray();

    private static PlayerBattleReport ImportReport(PlayerBattleReportDocument document)
    {
        var report = new PlayerBattleReport
        {
            ReportId = document.ReportId,
            ActionInstanceId = Guid.TryParse(document.ReportId, out var guid) ? guid : Guid.Empty,
            ActionId = document.ActionId,
            ActionName = document.ActionName,
            StartedAtUnixSeconds = document.StartedAtUnixSeconds,
            EndedAtUnixSeconds = document.EndedAtUnixSeconds,
            Experience = document.Experience,
            Foundation = document.Foundation,
            CombatExperience = document.CombatExperience,
            DuelWins = document.DuelWins,
            DuelLosses = document.DuelLosses,
            Cycles = document.Cycles,
            Encounters = document.Encounters,
            NormalKills = document.NormalKills,
            EliteKills = document.EliteKills,
            BossKills = document.BossKills,
            Deaths = document.Deaths,
            DayIndex = document.DayIndex,
            RootExp =
            {
                Stamina = document.RootExp.Stamina,
                Meridians = document.RootExp.Meridians,
                Attack = document.RootExp.Attack,
                Bone = document.RootExp.Bone,
                Agility = document.RootExp.Agility,
                Consciousness = document.RootExp.Consciousness
            }
        };
        report.Items.AddRange(document.Items);
        report.ItemsLost.AddRange(document.ItemsLost ?? []);
        report.AbilityExp.AddRange(document.AbilityExp ?? []);
        report.LevelUps.AddRange(document.LevelUps ?? []);
        return report;
    }
}
