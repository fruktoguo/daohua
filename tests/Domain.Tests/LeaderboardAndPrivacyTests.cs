using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Combat;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;

namespace Domain.Tests;

/// <summary>
/// 角色名片配装隐私与斗法终身胜负统计（Schema v43）领域回归：
/// 覆盖 PublicLoadoutVisible 默认开放与持久化往返、旧版本 SETTINGS/PROGRESSION 缺省兼容性解码，以及斗法弃战累加 DuelLosses。
/// </summary>
public sealed class LeaderboardAndPrivacyTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>新建角色默认开启公开配装可见；显式关闭后标脏 Settings 并随 SectionCodec v43 往返保留。</summary>
    [Fact]
    public void PublicLoadoutVisible_DefaultsTrueAndRoundTripsThroughSectionCodec()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "隐私修士", Baseline, "fp");
        Assert.True(player.Settings.PublicLoadoutVisible);

        player.ConsumeDirtySections();
        // 重复设为 true 应幂等不标脏。
        player.SetPublicLoadoutVisible(true);
        Assert.Empty(player.DirtySections);

        // 设为 false 后必须标脏 Settings 且编码/解码保持 false。
        player.SetPublicLoadoutVisible(false);
        Assert.Contains(PlayerSectionKind.Settings, player.DirtySections);

        var writes = SectionCodec.EncodeAggregateFull(player.ExportState(), Baseline);
        var restoredState = SectionCodec.DecodeAggregate(ToBundle(player, writes, Baseline));
        Assert.False(restoredState.Settings.PublicLoadoutVisible);
        Assert.Equal(0d, restoredState.Settings.LoadoutInspectPrice);

        // 开启付费查阅模式（如 88 灵石），随 SectionCodec v46 往返保留；切回公开时自动清零价格。
        player.SetLoadoutPrivacy(publicLoadoutVisible: false, loadoutInspectPrice: 88d);
        var paidWrites = SectionCodec.EncodeAggregateFull(player.ExportState(), Baseline);
        var restoredPaid = SectionCodec.DecodeAggregate(ToBundle(player, paidWrites, Baseline));
        Assert.False(restoredPaid.Settings.PublicLoadoutVisible);
        Assert.Equal(88d, restoredPaid.Settings.LoadoutInspectPrice);

        player.SetLoadoutPrivacy(publicLoadoutVisible: true, loadoutInspectPrice: 88d);
        Assert.True(player.Settings.PublicLoadoutVisible);
        Assert.Equal(0d, player.Settings.LoadoutInspectPrice);

        // 非整数、负数或超上限价格必须抛出 DomainException。
        Assert.Throws<Domain.Common.DomainException>(() => player.SetLoadoutPrivacy(false, -1d));
        Assert.Throws<Domain.Common.DomainException>(() => player.SetLoadoutPrivacy(false, 12.5d));
        Assert.Throws<Domain.Common.DomainException>(() => player.SetLoadoutPrivacy(false, PlayerSettingsState.MaxLoadoutInspectPrice + 1d));
    }

    /// <summary>不含 publicLoadoutVisible 与 duelWins/duelLosses 的 v42 旧分段载荷必须缺省恢复为 true 与 0。</summary>
    [Fact]
    public void LegacySettingsAndProgression_DefaultPrivacyTrueAndZeroDuelStats()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "旧档修士", Baseline, "fp");
        player.Progression.RealmLevel = 12;
        player.Progression.Experience = 340;
        var writes = SectionCodec.EncodeAggregateFull(player.ExportState(), Baseline).ToList();

        // 从 Settings 分段剥离 publicLoadoutVisible 与 loadoutInspectPrice 模拟经 SQL 升版但尚缺新字段的旧载荷。
        var settingsWrite = writes.Single(w => w.SectionKind == PlayerSectionKinds.Settings);
        var settingsJson = JsonNode.Parse(SectionCodec.Decode<JsonElement>(ToStored(settingsWrite)).GetRawText())!.AsObject();
        Assert.True(settingsJson.Remove("publicLoadoutVisible"));
        settingsJson.Remove("loadoutInspectPrice");
        var legacySettingsWrite = SectionCodec.Encode(
            player.PlayerId.Value,
            PlayerSectionKinds.Settings,
            stateVersion: settingsWrite.StateVersion,
            payload: settingsJson,
            updatedAt: Baseline,
            schemaVersion: SectionCodec.CurrentSchemaVersion);

        // 从 Progression 分段剥离 duelWins / duelLosses 模拟经 SQL 升版但尚缺新字段的旧载荷。
        var progWrite = writes.Single(w => w.SectionKind == PlayerSectionKinds.Progression);
        var progJson = JsonNode.Parse(SectionCodec.Decode<JsonElement>(ToStored(progWrite)).GetRawText())!.AsObject();
        Assert.True(progJson.Remove("duelWins"));
        Assert.True(progJson.Remove("duelLosses"));
        var legacyProgWrite = SectionCodec.Encode(
            player.PlayerId.Value,
            PlayerSectionKinds.Progression,
            stateVersion: progWrite.StateVersion,
            payload: progJson,
            updatedAt: Baseline,
            schemaVersion: SectionCodec.CurrentSchemaVersion);

        var legacyWrites = writes
            .Where(w => w.SectionKind != PlayerSectionKinds.Settings && w.SectionKind != PlayerSectionKinds.Progression)
            .Concat([legacySettingsWrite, legacyProgWrite]);

        var restored = SectionCodec.DecodeAggregate(ToBundle(player, legacyWrites, Baseline));
        Assert.True(restored.Settings.PublicLoadoutVisible);
        Assert.Equal(0d, restored.Settings.LoadoutInspectPrice);
        Assert.Equal(0d, restored.Progression.DuelWins);
        Assert.Equal(0d, restored.Progression.DuelLosses);

        var decodedProgression = SectionCodec.DecodeProgression(ToStored(legacyProgWrite));
        Assert.Equal(12, decodedProgression.RealmLevel);
        Assert.Equal(340d, decodedProgression.Experience);
        Assert.Equal(0d, decodedProgression.DuelWins);
        Assert.Equal(0d, decodedProgression.DuelLosses);
    }

    /// <summary>缺少有效 State 字段的合成 loadout 分段在轻量解码时必须抛出 InvalidDataException，禁止返回 null。</summary>
    [Fact]
    public void DecodeLoadout_ThrowsInvalidDataExceptionWhenStatePayloadMissing()
    {
        var playerId = Guid.NewGuid();
        var synthetic = SectionCodec.Encode(
            playerId,
            PlayerSectionKinds.Loadout,
            stateVersion: 1,
            payload: new { learnedAbilityIds = new[] { "ability.heavy-slash" } },
            updatedAt: Baseline);

        Assert.Throws<InvalidDataException>(() => SectionCodec.DecodeLoadout(ToStored(synthetic)));
    }

    /// <summary>Progression 中的 DuelWins/DuelLosses 随全量快照编解码及 DecodeProgression 准确往返；弃战同步累加终身负场。</summary>
    [Fact]
    public void DuelWinsAndLosses_RoundTripAndForfeitIncrementsLosses()
    {
        var duelSettings = new DuelSettings(DefeatCooldownSeconds: 60);
        var catalog = CreateDuelCatalog(duelSettings);
        var initiator = PreparePendingSeeker(catalog, "斗法修士");
        var defender = PreparePendingSeeker(catalog, "对手");
        initiator.Progression.DuelWins = 14;
        initiator.Progression.DuelLosses = 3;

        var duelId = Guid.NewGuid();
        var nowUnix = Baseline.ToUnixTimeMilliseconds() / 1000d;
        Assert.True(initiator.TryReserveDuel(duelId, nowUnix));
        Assert.True(defender.TryReserveDuel(duelId, nowUnix));

        var initiatorMirror = initiator.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(100, 20, 0, 10) };
        var defenderMirror = defender.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10, 0, 0, 5) };
        var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, 1, duelSettings);
        initiator.AttachDuel(DuelPlaybackState.ForViewer(script, duelId.ToString("N"), initiatorMirror, defenderMirror, true));

        var forfeit = initiator.ApplyDuelForfeit(Baseline.AddSeconds(3), duelSettings);
        Assert.NotNull(forfeit);
        Assert.Equal(14d, initiator.Progression.DuelWins);
        Assert.Equal(4d, initiator.Progression.DuelLosses);

        var writes = SectionCodec.EncodeAggregateFull(initiator.ExportState(), Baseline);
        var progStored = ToStored(writes.Single(w => w.SectionKind == PlayerSectionKinds.Progression));
        var decodedProg = SectionCodec.DecodeProgression(progStored);
        Assert.Equal(14d, decodedProg.DuelWins);
        Assert.Equal(4d, decodedProg.DuelLosses);

        var restored = SectionCodec.DecodeAggregate(ToBundle(initiator, writes, Baseline));
        Assert.Equal(14d, restored.Progression.DuelWins);
        Assert.Equal(4d, restored.Progression.DuelLosses);
    }

    private static PlayerAggregate PreparePendingSeeker(GameDataCatalog catalog, string name)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, catalog.Fingerprint);
        player.Duel.EncounterCharges = 1;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog, duel: new DuelAdvanceOptions(true));
        return player;
    }

    private static GameDataCatalog CreateDuelCatalog(DuelSettings duel)
    {
        var actionId = new ActionId("combat.test");
        var actions = new ActionCatalog([
            new ActionDefinition(
                actionId,
                "讨伐测试",
                ActionKind.Combat,
                0,
                new Dictionary<ItemId, double>(),
                [],
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.test",
            "测试妖兽",
            1,
            1,
            CombatMonsterRank.Minion,
            CombatantStats.Create(1, 0, 0, 2),
            [],
            [],
            0);
        var encounter = new CombatEncounterDefinition(
            "encounter.test",
            "测试遭遇",
            1,
            1,
            1,
            1,
            1,
            180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)],
            [],
            [],
            0);
        return new GameDataCatalog(
            "test-1",
            "test-fingerprint",
            actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            duel: duel);
    }

    private static PlayerStateBundle ToBundle(PlayerAggregate player, IEnumerable<SectionWrite> writes, DateTimeOffset now)
    {
        var state = player.ExportState();
        var sections = writes.ToDictionary(w => w.SectionKind, ToStored, StringComparer.Ordinal);
        return new PlayerStateBundle(
            new PlayerIdentity(state.PlayerId.Value, state.AccountId.Value, state.DisplayName, state.CreatedAt),
            new PlayerStateHead(
                state.PlayerId.Value,
                state.StateVersion,
                SectionCodec.CurrentSchemaVersion,
                state.LastSimulatedAt,
                state.GameDataFingerprint,
                SectionCodec.ComputeHeadChecksum(sections.Values),
                now),
            sections,
            []);
    }

    private static StoredSection ToStored(SectionWrite write) => new(
        write.PlayerId,
        write.SectionKind,
        write.StateVersion,
        write.SchemaVersion,
        write.Codec,
        write.Payload.ToArray(),
        write.RawBytes,
        write.Checksum,
        write.UpdatedAt);
}
