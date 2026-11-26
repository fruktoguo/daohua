using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;

namespace Domain.Tests;

/// <summary>
/// 斗法紧凑行动记录领域回归：Brotli 存档往返、20 场淘汰、幂等防重、归档即弃与时间切片截断。
/// </summary>
public sealed class DuelBattleRecordTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DuelBattleRecordDocument MakeRecord(string duelId, int actionCount = 2)
    {
        var actions = Enumerable.Range(0, actionCount)
            .Select(i => new DuelRecordActionDocument(
                i * 1.5,
                0,
                DuelRecordActionKind.Skill,
                DefIndex: 0,
                Effects:
                [
                    new DuelRecordEffectDocument(0, 1, DuelRecordEffectKind.Damage,
                        CombatDamageKind.Spell, CombatElement.Fire, DuelRecordHitMark.Hit | DuelRecordHitMark.Crit, 42.5)
                ]))
            .ToArray();
        return new DuelBattleRecordDocument(
            duelId,
            1000,
            Math.Max(1.0, actionCount * 1.5),
            CombatOutcome.PlayerWon,
            Units: [new DuelRecordUnitDocument("甲", true, "p1"), new DuelRecordUnitDocument("乙", false, "p2")],
            Defs: ["ability.fireball"],
            Actions: actions);
    }

    private static DuelPlaybackState MakePlayback(string duelId, DuelBattleRecordDocument record, CombatOutcome outcome = CombatOutcome.PlayerWon, bool forfeited = false)
        => new(
            duelId,
            Guid.NewGuid().ToString("N"),
            "乙",
            true,
            outcome,
            10,
            [],
            [],
            [],
            CombatantStats.Create(100, 10, 0, 1),
            100,
            0,
            ViewerRecord: record,
            Forfeited: forfeited);

    [Fact]
    public void DuelSection_BrotliRoundTrip_PreservesCompactRecordsWithoutTextBlobs()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        var actionId = Guid.NewGuid();
        player.EnsureActionReport(actionId, new ActionId("combat.hunt"), "讨伐", Baseline);

        var record = MakeRecord("duel01", actionCount: 3);
        ActionReportBook.RecordDuelBattle(player, actionId, MakePlayback("duel01", record), 1234.5);

        var duelDoc = player.ExportState().Duel;
        var section = SectionCodec.Encode(player.PlayerId.Value, PlayerSectionKinds.Duel, 1, duelDoc, Baseline);
        var json = System.Text.Encoding.UTF8.GetString(SectionCodec.DecodeRaw(section.Codec, section.Payload));
        Assert.DoesNotContain("物理伤害", json, StringComparison.Ordinal);
        Assert.DoesNotContain("火属性", json, StringComparison.Ordinal);
        Assert.Contains("\"defs\":[\"ability.fireball\"]", json, StringComparison.Ordinal);

        var decoded = SectionCodec.Decode<PlayerDuelStateDocument>(new StoredSection(
            section.PlayerId, section.SectionKind, section.StateVersion, section.SchemaVersion,
            section.Codec, section.Payload.ToArray(), section.RawBytes, section.Checksum, section.UpdatedAt));
        var restored = Assert.Single(Assert.Single(decoded.ActiveReports!).DuelRecords!);
        Assert.Equal("duel01", restored.DuelId);
        Assert.Equal(3, restored.Actions!.Count);
        Assert.Equal(CombatElement.Fire, restored.Actions[0].Effects![0].Element);
    }

    [Fact]
    public void RecordDuelBattle_CapsAt20Matches_DeduplicatesAndClearsOnArchive()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        var actionId = Guid.NewGuid();
        player.EnsureActionReport(actionId, new ActionId("combat.hunt"), "讨伐", Baseline);

        for (var i = 1; i <= 25; i++)
        {
            var id = $"duel-{i:D2}";
            ActionReportBook.RecordDuelBattle(player, actionId, MakePlayback(id, MakeRecord(id)), 1000 + i);
        }
        // 幂等防重：重复登记同一场不增加条目。
        ActionReportBook.RecordDuelBattle(player, actionId, MakePlayback("duel-25", MakeRecord("duel-25")), 2000);

        var records = player.Duel.ReportFor(actionId)!.DuelRecords;
        Assert.Equal(DuelBattleRecordLimits.MaxRecordsPerAction, records.Count);
        Assert.Equal("duel-25", records[0].DuelId);
        Assert.Equal("duel-06", records[^1].DuelId);

        // 行动出队归档后清空 DuelRecords，不进入历史战报（先记一笔胜场使账本非空进入归档）。
        player.RecordStats(actionId, 2001, new PlayerStatsDelta { DuelWins = 1 });
        player.ArchiveReportForAction(actionId, 3000);
        var archived = Assert.Single(player.ExportState().Duel.Reports);
        Assert.Null(archived.DuelRecords);
    }

    [Fact]
    public void ForViewerSlice_TruncatesFutureActionsAtCutoff()
    {
        var record = MakeRecord("duel-live", actionCount: 4); // t = 0, 1.5, 3.0, 4.5
        var sliced = record.ForViewerSlice(CombatOutcome.Running, false, 500, maxAtSeconds: 2.0);
        Assert.Equal(CombatOutcome.Running, sliced.Outcome);
        Assert.Equal(2, sliced.Actions!.Count);
        Assert.Equal(1.5, sliced.Actions[^1].AtSeconds);
    }
}
