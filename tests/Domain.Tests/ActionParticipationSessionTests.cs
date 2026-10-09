using System.Text.Json;
using Domain.Persistence;

namespace Domain.Tests;

/// <summary>会话单次推演、个人幂等落账及检查点恢复的独立边界。</summary>
public sealed class ActionParticipationSessionTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MembersWaitWithoutBattleRegenerationAndApplyProjectionOnce()
    {
        var (catalog, members, spec) = CreateParty();
        var hp = members[1].Resources.Hitpoints;
        var mp = members[1].Resources.Manapoints;
        var waited = SimulationEngine.AdvanceTo(members[1], Baseline.AddMinutes(2), catalog);
        Assert.Empty(waited.AssetDeltas);
        Assert.Null(members[1].Actions[0].Expedition);
        Assert.Equal(hp, members[1].Resources.Hitpoints);
        Assert.Equal(mp, members[1].Resources.Manapoints);
        Assert.Equal(0, members[1].Progression.NormalKills);

        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var projected = session.AdvanceTo(Baseline.AddMinutes(1));
        var restoredProjection = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(
            JsonSerializer.Serialize(projected[1], PersistenceJson.Options), PersistenceJson.Options)!;
        Assert.True(session.CombatAdvanceCount > 0);
        var originalSlots = members[1].Loadout.AbilitySlots.ToArray();
        members[1].SetQueuePaused(true);
        var result = members[1].ApplyActionParticipationSettlement(restoredProjection, catalog);
        Assert.True(result.Changed);
        Assert.True(members[1].Progression.NormalKills > 0);
        Assert.Equal(Baseline.AddMinutes(2), members[1].LastSimulatedAt);
        Assert.True(members[1].Settings.QueuePaused);
        Assert.Equal(originalSlots, members[1].Loadout.AbilitySlots);
        var before = members[1].Progression.LifetimeExperience;
        var duplicate = members[1].ApplyActionParticipationSettlement(projected[1], catalog);
        Assert.False(duplicate.Changed);
        Assert.Empty(duplicate.AssetDeltas);
        Assert.Equal(before, members[1].Progression.LifetimeExperience);
        Assert.Equal(1, members[1].Actions[0].Participation!.AppliedSequence);
    }

    [Fact]
    public void CheckpointRestoreContinuesWithoutReplayingRewardsAndAllViewsAgree()
    {
        var (catalog, _, spec) = CreateParty();
        var continuous = ActionParticipationSessionEngine.Create(spec, catalog);
        var first = continuous.AdvanceTo(Baseline.AddSeconds(61));
        var json = JsonSerializer.Serialize(continuous.Export(), PersistenceJson.Options);
        var restored = ActionParticipationSessionEngine.Restore(
            JsonSerializer.Deserialize<ActionParticipationSessionDocument>(json, PersistenceJson.Options)!, catalog);
        var expected = continuous.AdvanceTo(Baseline.AddSeconds(123));
        var actual = restored.AdvanceTo(Baseline.AddSeconds(123));
        Assert.Equal(first[0].Sequence + 1, actual[0].Sequence);
        for (var i = 0; i < spec.Members.Count; i++)
        {
            Assert.Equal(expected[i].Hitpoints, actual[i].Hitpoints);
            Assert.Equal(expected[i].Manapoints, actual[i].Manapoints);
            Assert.Equal(expected[i].CompletedRepeats, actual[i].CompletedRepeats);
            Assert.Equal(expected[i].AssetDeltas, actual[i].AssetDeltas);
            Assert.Equal(expected[i].Stats, actual[i].Stats);
            Assert.Equal(expected[i].Events, actual[i].Events);
            Assert.Equal(expected[i].CombatStats, actual[i].CombatStats);
            var selfView = actual[i].Expedition!;
            var otherView = actual[(i + 1) % spec.Members.Count].Expedition!;
            var mirrored = otherView.Allies!.Single(ally => ally.MemberIndex == i);
            Assert.Equal(selfView.PlayerHitpoints, mirrored.State.Hitpoints);
            Assert.Equal(selfView.PlayerManapoints, mirrored.State.Manapoints);
            Assert.Equal(selfView.WaveNumber, otherView.WaveNumber);
            Assert.Equal(selfView.BattleElapsedSeconds, otherView.BattleElapsedSeconds);
        }
    }

    [Fact]
    public void SkillExperienceAndSharedCombatLedgerProjectWithoutReplacingLiveLoadout()
    {
        var (catalog, members, spec) = CreateParty(withAbilities: true);
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var projected = session.AdvanceTo(Baseline.AddMinutes(3));
        var abilityId = new AbilityId("ability.palm-lightning");
        for (var i = 0; i < members.Length; i++)
        {
            // 冻结战斗继续施法，成员期间改装不能被结算容器的旧装配覆盖。
            members[i].Loadout.AbilitySlots.Clear();
            members[i].ApplyActionParticipationSettlement(projected[i], catalog);
            Assert.Empty(members[i].Loadout.AbilitySlots);
            var awarded = projected[i].Stats.Where(entry => entry.Delta.AbilityId == abilityId.Value)
                .Sum(entry => entry.Delta.AbilityExperience);
            Assert.True(awarded > 0);
            Assert.Equal(awarded, members[i].Loadout.AbilityExperiences[abilityId]);
            var report = members[i].Duel.ReportFor(members[i].Actions[0].Id)!;
            Assert.Equal(awarded, report.AbilityExp.Single(entry => entry.AbilityId == abilityId.Value).Experience);
            var self = report.CombatStats.Entries.Single(row => row.UnitKey == "player");
            var canonicalKey = i == 0 ? "player" : $"ally:{i}";
            var canonical = projected[0].CombatStats!.Single(row => row.UnitKey == canonicalKey);
            Assert.Equal(canonical.DamageDealtSpell, self.DamageDealtSpell);
            Assert.True(self.DamageDealtSpell > 0);
        }
    }

    [Fact]
    public void SameTimestampEndStillProducesTerminalProjection()
    {
        var (catalog, members, spec) = CreateParty();
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var first = session.AdvanceTo(Baseline);
        var ended = session.EndAt(Baseline);
        Assert.Equal(first[0].Sequence + 1, ended[0].Sequence);
        Assert.All(ended, projection => Assert.Equal(Baseline.ToUnixTimeMilliseconds() / 1000d,
            projection.EndsAtUnixSeconds));
        members[0].ApplyActionParticipationSettlement(first[0], catalog);
        members[0].ApplyActionParticipationSettlement(ended[0], catalog);
        Assert.False(members[0].IsInActionParticipation);
        Assert.Throws<DomainException>(() => session.AdvanceTo(Baseline.AddSeconds(1)));
    }

    [Fact]
    public void SearchCombatBoundaryConsumesOnlyIdleSkillTimeAndSplitWindowsAgree()
    {
        var (catalog, _, original) = CreateParty(withAbilities: true);
        var spec = original with { Members = original.Members.Select(member => member with
        {
            InitialHitpoints = member.Stats.MaxHitpoints - 100,
            InitialManapoints = 0,
            CombatSkills = new([new("ability.palm-lightning", 6.25)], null, 0, 0)
        }).ToArray() };
        var initial = ActionParticipationSessionEngine.Create(spec, catalog).Export();
        initial = initial with { Expedition = initial.Expedition! with { PhaseDurationSeconds = 2.5 } };
        var whole = ActionParticipationSessionEngine.Restore(initial, catalog);
        var split = ActionParticipationSessionEngine.Restore(initial, catalog);
        var entered = split.AdvanceTo(Baseline.AddSeconds(2.5));
        Assert.All(entered, view =>
        {
            Assert.Equal(CombatExpeditionPhase.Combat, view.Expedition!.Phase);
            Assert.Equal(3.75, view.Skills.Cooldowns.Single().RemainingSeconds);
            Assert.Equal(0, view.Manapoints);
        });
        Assert.True(split.NextVisibleBoundaryUtc > Baseline.AddSeconds(2.5));
        var count = split.CombatAdvanceCount;
        _ = split.NextVisibleBoundaryUtc;
        Assert.Equal(count, split.CombatAdvanceCount);
        foreach (var seconds in new[] { 4d, 5d, 7.25, 9d, 12.5 }) split.AdvanceTo(Baseline.AddSeconds(seconds));
        whole.AdvanceTo(Baseline.AddSeconds(12.5));
        var expected = whole.Export();
        var actual = split.Export();
        Assert.Equal(JsonSerializer.Serialize(expected.Expedition, PersistenceJson.Options),
            JsonSerializer.Serialize(actual.Expedition, PersistenceJson.Options));
        Assert.Equal(JsonSerializer.Serialize(expected.Skills, PersistenceJson.Options),
            JsonSerializer.Serialize(actual.Skills, PersistenceJson.Options));
        Assert.Equal(expected.CombatStats, actual.CombatStats);
        Assert.Equal(expected.MemberRegenCarrySeconds, actual.MemberRegenCarrySeconds);
    }

    [Fact]
    public void ActiveSustainedBuffAndCastingCheckpointResumeWithoutReplay()
    {
        var (catalog, _, original) = CreateParty(withAbilities: true);
        var search = catalog.CombatEncounters[original.EncounterId].SearchSeconds;
        var spec = original with { Members = original.Members.Select(member => member with
        {
            InitialHitpoints = member.Stats.MaxHitpoints - 100,
            SustainedBuffs = [new("buff.session-frozen", search + 8, HpRegen: 25, MpRegen: 30)],
            CombatSkills = new([new("ability.palm-lightning", search + 2)], null, 0, 0)
        }).ToArray() };
        var continuous = ActionParticipationSessionEngine.Create(spec, catalog);
        continuous.AdvanceTo(Baseline.AddSeconds(search + 3.25));
        var checkpoint = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(
            JsonSerializer.Serialize(continuous.Export(), PersistenceJson.Options), PersistenceJson.Options)!;
        Assert.Contains(checkpoint.Expedition!.PlayerState!.Buffs!, buff => buff.BuffId == "buff.session-frozen");
        var restored = ActionParticipationSessionEngine.Restore(checkpoint, catalog);
        Assert.Equal(continuous.NextVisibleBoundaryUtc, restored.NextVisibleBoundaryUtc);
        var expected = continuous.AdvanceTo(Baseline.AddSeconds(search + 11));
        var actual = restored.AdvanceTo(Baseline.AddSeconds(search + 11));
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(JsonSerializer.Serialize(expected[i], PersistenceJson.Options),
                JsonSerializer.Serialize(actual[i], PersistenceJson.Options));
            Assert.DoesNotContain(actual[i].Events, entry => entry.Code == "combat.search.started");
        }
    }

    [Fact]
    public void MismatchedFingerprintFinalizesOnlySavedStateAndKeepsTerminalProjectionReadable()
    {
        var (catalog, _, spec) = CreateParty();
        var initial = ActionParticipationSessionEngine.Create(spec, catalog).Export();
        var downed = initial with
        {
            GameDataFingerprint = "old-fingerprint",
            Expedition = initial.Expedition! with
            {
                PlayerHitpoints = 0,
                Allies = initial.Expedition.Allies!.Select(ally => ally with
                    { State = ally.State with { Hitpoints = 0 } }).ToArray()
            }
        };
        Assert.Throws<DomainException>(() => ActionParticipationSessionEngine.Restore(downed, catalog));
        var final = ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(downed);
        Assert.Equal(downed.GameDataFingerprint, final.Document.GameDataFingerprint);
        Assert.Equal(downed.AdvancedAtUnixSeconds, final.Document.EndsAtUnixSeconds);
        Assert.All(final.Settlements, projection =>
        {
            Assert.Equal(downed.Sequence + 1, projection.Sequence);
            Assert.Equal(Baseline, projection.From);
            Assert.Equal(projection.From, projection.To);
            Assert.Equal(1, projection.Hitpoints);
            Assert.Empty(projection.Stats);
            Assert.Empty(projection.AssetDeltas);
            Assert.Empty(projection.Events);
            var view = ActionParticipationSessionEngine.ProjectFor(final.Document, projection.PlayerId);
            Assert.Equal(1, view.PlayerHitpoints);
            Assert.All(view.Allies!, ally => Assert.Equal(1, ally.State.Hitpoints));
            Assert.Equal(projection.CombatStats,
                ActionParticipationSessionEngine.ProjectCombatStatsFor(final.Document, projection.PlayerId));
        });
    }

    /// <summary>不同个人回复周期经恢复与散场续写；14.006 秒检查点不能在亚 tick 边界卡死。</summary>
    [Fact]
    public void PersonalRegenCarrySurvivesRestoreAndContinuesAfterEnding()
    {
        var (catalog, members, original) = CreateParty();
        var spec = original with { Members = original.Members.Select((member, index) => member with
        {
            InitialHitpoints = 1,
            InitialManapoints = 0,
            RegenCarrySeconds = index + 1d
        }).ToArray() };
        var initial = ActionParticipationSessionEngine.Create(spec, catalog).Export();
        initial = initial with { Expedition = initial.Expedition! with { PhaseDurationSeconds = 30 } };
        var engine = ActionParticipationSessionEngine.Restore(initial, catalog);
        var first = engine.AdvanceTo(Baseline.AddSeconds(1.5));
        Assert.All(first, view => Assert.Equal(1, view.Hitpoints));
        for (var i = 0; i < members.Length; i++) members[i].ApplyActionParticipationSettlement(first[i], catalog);
        engine = ActionParticipationSessionEngine.Restore(engine.Export(), catalog);
        var second = engine.AdvanceTo(Baseline.AddSeconds(2));
        Assert.Equal(new[] { 1d, 1d, 11d }, second.Select(view => view.Hitpoints));
        for (var i = 0; i < members.Length; i++) members[i].ApplyActionParticipationSettlement(second[i], catalog);
        var end = engine.EndAt(Baseline.AddSeconds(2.5));
        Assert.Equal(new[] { 3.5, 4.5, 0.5 }, end.Select(view => view.RegenCarrySeconds));
        for (var i = 0; i < members.Length; i++)
        {
            SimulationEngine.AdvanceTo(members[i], end[i].To, catalog);
            members[i].ApplyActionParticipationSettlement(end[i], catalog);
            members[i].RemoveAction(members[i].Actions[0].Id);
            var restored = PlayerAggregate.Restore(members[i].ExportState());
            var beforeHp = restored.Resources.Hitpoints;
            var dueAt = restored.LastSimulatedAt.AddSeconds(5 - end[i].RegenCarrySeconds);
            SimulationEngine.AdvanceTo(restored, dueAt.AddMilliseconds(-1), catalog);
            Assert.Equal(beforeHp, restored.Resources.Hitpoints);
            SimulationEngine.AdvanceTo(restored, dueAt, catalog);
            Assert.True(restored.Resources.Hitpoints > beforeHp);
            Assert.Equal(0, restored.Resources.RegenCarrySeconds, 6);
        }
        var stuck = initial with { Sequence = 19, AdvancedAtUnixSeconds = initial.AdvancedAtUnixSeconds + 14.006,
            MemberRegenCarrySeconds = [4.006, 4.006, 4.006],
            Expedition = initial.Expedition! with { PhaseElapsedSeconds = 14.006 } };
        var recovered = ActionParticipationSessionEngine.Restore(stuck, catalog);
        var progressed = recovered.AdvanceTo(Baseline.AddSeconds(15.006));
        Assert.All(progressed, view => Assert.Equal(0.006, view.RegenCarrySeconds, 6));
    }

    /// <summary>精确入场即散场不退还已预扣灵力；首次恢复的搜寻事件只交付一次。</summary>
    [Fact]
    public void ExactEntrySynchronizesCastingAndFirstRestoreKeepsOnlyUnsentOpeningEvent()
    {
        var (catalog, members, spec) = CreateParty(withAbilities: true);
        var initial = ActionParticipationSessionEngine.Create(spec, catalog).Export();
        initial = initial with { Expedition = initial.Expedition! with { PhaseDurationSeconds = 2.5 } };
        var engine = ActionParticipationSessionEngine.Restore(initial, catalog);
        var entryAt = Baseline.AddSeconds(2.5);
        var entered = engine.AdvanceTo(entryAt);
        for (var i = 0; i < members.Length; i++)
        {
            Assert.Single(entered[i].Events, entry => entry.Code == "combat.search.started");
            Assert.Equal("ability.palm-lightning", entered[i].Skills.CastingAbilityId);
            Assert.True(entered[i].Manapoints < spec.Members[i].InitialManapoints);
            Assert.Equal(entered[i].Manapoints, entered[i].Expedition!.PlayerManapoints);
            members[i].ApplyActionParticipationSettlement(entered[i], catalog);
            _ = PlayerAggregate.Restore(members[i].ExportState());
        }
        var resumed = ActionParticipationSessionEngine.Restore(engine.Export(), catalog);
        var ended = resumed.EndAt(entryAt);
        for (var i = 0; i < members.Length; i++)
        {
            Assert.DoesNotContain(ended[i].Events, entry => entry.Code == "combat.search.started");
            Assert.Equal(entered[i].Manapoints, ended[i].Manapoints);
            Assert.Equal(entered[i].Skills.CastingAbilityId, ended[i].Skills.CastingAbilityId);
        }
    }

    /// <summary>配表删行动后终局仍能补投、落账和释放引用，不恢复旧行动执行器。</summary>
    [Fact]
    public void RetiredActionTerminalSettlementCanRestoreMissingReferenceAndArchive()
    {
        var (catalog, members, spec) = CreateParty();
        spec = spec with { Members = members.Select(member => member.CaptureActionParticipantSnapshot(catalog)).ToArray() };
        var document = ActionParticipationSessionEngine.Create(spec, catalog).Export();
        var terminal = ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(document);
        var changedCatalog = new GameDataCatalog("new", "new-fingerprint", new ActionCatalog([]));
        var member = members[0];
        member.RemoveAction(member.Actions[0].Id);
        member.StartActionParticipation(spec, Baseline, changedCatalog.Actions, document.AdvancedAtUnixSeconds);
        member.ApplyActionParticipationSettlement(terminal.Settlements[0], changedCatalog);
        member.RemoveAction(member.Actions[0].Id);
        var restored = PlayerAggregate.Restore(member.ExportState());
        Assert.Empty(restored.Actions);
        Assert.Equal(terminal.Settlements[0].Hitpoints, restored.Resources.Hitpoints);
        Assert.Equal(terminal.Settlements[0].Manapoints, restored.Resources.Manapoints);
    }

    private static (GameDataCatalog Catalog, PlayerAggregate[] Members, ActionParticipationSpec Spec) CreateParty(
        bool withAbilities = false)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var members = Enumerable.Range(0, 3).Select(index => PlayerAggregate.Create(PlayerId.New(),
            AccountId.New(), $"队员{index}", Baseline, catalog.Fingerprint)).ToArray();
        foreach (var member in members) PlayerAttributeResolver.Apply(member, catalog);
        var stats = CombatantStats.Create(50_000, withAbilities ? 40 : 2_000, 500, 1) with
            { HpRegen = 10, MpRegen = 50, MaxManapoints = 1_000, SpellAttack = withAbilities ? 40 : 0 };
        if (withAbilities)
            foreach (var member in members) member.LearnAbility(new("ability.palm-lightning"));
        IReadOnlyList<SkillSlot> skills = withAbilities ? [new(catalog.Abilities["ability.palm-lightning"], 1, [])] : [];
        var frozen = members.Select(member => new ActionParticipantSnapshot(member.PlayerId.Value.ToString("D"),
            member.DisplayName, 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, skills)).ToArray();
        var spec = new ActionParticipationSpec("session.authority", "encounter.qi-beast", "node.test", 97, 0,
            Baseline.ToUnixTimeMilliseconds() / 1000d, frozen) { ActionId = new("combat.hunt-qi-beast") };
        foreach (var member in members) member.StartActionParticipation(spec, Baseline, catalog.Actions);
        return (catalog, members, spec);
    }
}
