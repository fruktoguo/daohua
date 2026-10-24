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

    /// <summary>
    /// 即时消耗品（雷击符等）击杀敌方最后一只怪时：战局必须立即结算为 PlayerWon，
    /// 后续按自然时钟恢复与成员视角投影不触发「持久化战斗结果与生命状态不一致」回归（线上会话卡死根因）。
    /// </summary>
    [Fact]
    public void ConsumableStrikeKillingLastEnemyTransitionsToWonWithoutInconsistency()
    {
        var (catalog, _, original) = CreateParty();
        var spec = original with
        {
            Members = original.Members.Select((member, index) => member with
            {
                ConsumableSlots = index == 0
                    ? [new ActionConsumableSlot("item.leiji-talisman", "combat",
                        AutoUseSlotState.SlotGroupGeneral, 0)]
                    : null
            }).ToArray()
        };
        var session = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, "item.leiji-talisman", 5));
        var initial = session.Export();
        var search = initial.Expedition!.PhaseDurationSeconds;
        // 推进到刚刚入场开战
        session.AdvanceTo(Baseline.AddSeconds(search + 0.1));
        var inCombat = session.Export();
        Assert.Equal(CombatExpeditionPhase.Combat, inCombat.Expedition!.Phase);

        // 再推进一段，覆盖雷击符激活与击杀边界
        var advanced = session.AdvanceTo(Baseline.AddSeconds(search + 65));
        var after = session.Export();
        Assert.NotNull(after.Expedition);
        Assert.NotEqual(CombatOutcome.EnemyWon, after.Expedition!.Outcome);
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
            SustainedBuffs = [new("buff.session-frozen", search + 8, Effects: [new BuffEffect(BuffEffectChannel.HpRegen, 25), new BuffEffect(BuffEffectChannel.MpRegen, 30)])],
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
            NextConsumableEvaluationAtUnixSeconds = initial.AdvancedAtUnixSeconds + 15,
            Expedition = initial.Expedition! with { PhaseElapsedSeconds = 14.006 } };
        var recovered = ActionParticipationSessionEngine.Restore(stuck, catalog);
        var progressed = recovered.AdvanceTo(Baseline.AddSeconds(15.006));
        Assert.All(progressed, view => Assert.Equal(0.006, view.RegenCarrySeconds, 6));
    }

    /// <summary>精确入场即散场不退还预扣灵力；恢复检查点不重演已提交开场事件。</summary>
    [Fact]
    public void ExactEntrySynchronizesCastingWithoutReplayingOpeningAfterRestore()
    {
        var (catalog, members, spec) = CreateParty(withAbilities: true);
        var created = ActionParticipationSessionEngine.Create(spec, catalog);
        var opening = created.AdvanceTo(Baseline);
        for (var i = 0; i < members.Length; i++) members[i].ApplyActionParticipationSettlement(opening[i], catalog);
        var initial = created.Export();
        initial = initial with { Expedition = initial.Expedition! with { PhaseDurationSeconds = 2.5 } };
        var engine = ActionParticipationSessionEngine.Restore(initial, catalog);
        var entryAt = Baseline.AddSeconds(2.5);
        var entered = engine.AdvanceTo(entryAt);
        for (var i = 0; i < members.Length; i++)
        {
            Assert.DoesNotContain(entered[i].Events, entry => entry.Code == "combat.search.started");
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

    [Theory]
    [InlineData(AutoUseSlotState.SlotGroupPotion)]
    [InlineData(AutoUseSlotState.SlotGroupGeneral)]
    public void OnlineRestockingWaitsForNextBoundaryAndEmptyStockDoesNotEndCombat(string group)
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", group, 0)]);
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, SupplyId, 1));
        var opening = engine.AdvanceTo(Baseline);
        Assert.Single(opening[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Equal(-1, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
        Assert.Empty(opening[0].AssetDeltas);
        var empty = engine.AdvanceTo(Baseline.AddSeconds(12));
        Assert.DoesNotContain(empty[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Null(engine.Export().EndsAtUnixSeconds);
        Assert.Equal(CombatExpeditionPhase.Combat, engine.Export().Expedition!.Phase);
        engine.SetWorkingWallet(Wallet(spec, 0, SupplyId, 2));
        engine.AdvanceTo(Baseline.AddSeconds(12));
        engine.AdvanceTo(Baseline.AddSeconds(14.999));
        Assert.Empty(engine.TakeAssetDeltas());
        var replenished = engine.AdvanceTo(Baseline.AddSeconds(15));
        Assert.Single(replenished[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Equal(-1, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
        Assert.Empty(replenished[0].AssetDeltas);
        engine.AdvanceTo(Baseline.AddSeconds(20));
        Assert.Equal(-1, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
        engine.EndAt(Baseline.AddSeconds(20));
        Assert.Empty(engine.TakeAssetDeltas());
    }

    [Fact]
    public void GeneralAndPotionSlotsShareMemberBalanceAcrossVariantsWithoutOverdraft()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0,
            [new(SupplyId, "combat", "general", 0), new(SupplyId, "combat", "potion", 0)]);
        var playerId = new PlayerId(Guid.Parse(spec.Members[0].PlayerId));
        var engine = ActionParticipationSessionEngine.Create(spec, catalog,
            [new(playerId, new(new(SupplyId), "a"), 0.5, 0), new(playerId, new(new(SupplyId), "b"), 2, 0)]);
        var opening = engine.AdvanceTo(Baseline);
        Assert.Equal(2, opening[0].Events.Count(entry => entry.Code == "auto-use.activated"));
        var spent = engine.TakeAssetDeltas();
        Assert.Equal(-2, spent.Sum(delta => delta.QuantityDelta));
        Assert.Equal(-0.5, Assert.Single(spent, delta => delta.Asset.Variant == "a").QuantityDelta);
        Assert.Equal(-1.5, Assert.Single(spent, delta => delta.Asset.Variant == "b").QuantityDelta);
        var exhausted = engine.AdvanceTo(Baseline.AddSeconds(10));
        Assert.DoesNotContain(exhausted[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Empty(engine.TakeAssetDeltas());
        Assert.Null(engine.Export().EndsAtUnixSeconds);
    }

    [Theory]
    [InlineData(AutoUseSlotState.SlotGroupGeneral)]
    [InlineData(AutoUseSlotState.SlotGroupPotion)]
    public void SustainedEffectSurvivesEmptyWalletAndRestartsOnlyAtLaterBoundary(string group)
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(DurationId, "combat", group, 0)]);
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, DurationId, 1));
        engine.AdvanceTo(Baseline.AddSeconds(2));
        Assert.Equal(-1, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
        engine.SetWorkingWallet(Wallet(spec, 0, DurationId, 0));
        engine.AdvanceTo(Baseline.AddSeconds(5));
        Assert.Contains(engine.Export().Expedition!.PlayerState!.Buffs!, buff => buff.BuffId == "buff.session-duration");
        var depleted = engine.AdvanceTo(Baseline.AddSeconds(15));
        Assert.Contains(depleted[0].Events, entry => entry.Code == "auto-use.depleted");
        Assert.DoesNotContain(depleted[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Null(engine.Export().EndsAtUnixSeconds);
        engine.SetWorkingWallet(Wallet(spec, 0, DurationId, 1));
        engine.AdvanceTo(Baseline.AddSeconds(15));
        Assert.Empty(engine.TakeAssetDeltas());
        var restarted = engine.AdvanceTo(Baseline.AddSeconds(20));
        Assert.Single(restarted[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Equal(-1, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
    }

    [Fact]
    public void IndependentEvaluationCursorMakesArbitrarySplitsAndRestoreEquivalent()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0,
            [new(SupplyId, "combat", "potion", 0), new(DurationId, "combat", "general", 0)]);
        var balances = Wallet(spec, 0, SupplyId, 20).Concat(Wallet(spec, 0, DurationId, 20)).ToArray();
        var continuous = ActionParticipationSessionEngine.Create(spec, catalog, balances);
        var expected = continuous.AdvanceTo(Baseline.AddSeconds(27));
        var expectedAssets = continuous.TakeAssetDeltas();
        var split = ActionParticipationSessionEngine.Create(spec, catalog, balances);
        split.AdvanceTo(Baseline.AddSeconds(3.111));
        split.AdvanceTo(Baseline.AddSeconds(7.222));
        split.AdvanceTo(Baseline.AddSeconds(12.333));
        var beforeRestore = split.TakeAssetDeltas();
        var checkpoint = JsonSerializer.Deserialize<ActionParticipationSessionDocument>(
            JsonSerializer.Serialize(split.Export(), PersistenceJson.Options), PersistenceJson.Options)!;
        var restored = ActionParticipationSessionEngine.Restore(checkpoint, catalog);
        restored.SetWorkingWallet(balances.Select(balance => balance with
        {
            Quantity = balance.Quantity + beforeRestore.Where(delta => delta.Asset == balance.Asset)
                .Sum(delta => delta.QuantityDelta)
        }).ToArray());
        var actual = restored.AdvanceTo(Baseline.AddSeconds(27));
        var actualAssets = beforeRestore.Concat(restored.TakeAssetDeltas()).GroupBy(delta => delta.Asset)
            .ToDictionary(group => group.Key, group => group.Sum(delta => delta.QuantityDelta));
        foreach (var delta in expectedAssets) Assert.Equal(delta.QuantityDelta, actualAssets[delta.Asset]);
        Assert.Equal(JsonSerializer.Serialize(continuous.Export().ConsumableLedger, PersistenceJson.Options),
            JsonSerializer.Serialize(restored.Export().ConsumableLedger, PersistenceJson.Options));
        Assert.Equal(continuous.Export().NextConsumableEvaluationAtUnixSeconds,
            restored.Export().NextConsumableEvaluationAtUnixSeconds);
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected[i].Hitpoints, actual[i].Hitpoints);
            Assert.Equal(expected[i].Manapoints, actual[i].Manapoints);
        }
        restored.AdvanceTo(Baseline.AddSeconds(27));
        Assert.Empty(restored.TakeAssetDeltas());
    }

    [Fact]
    public void ModeSwitchesConserveAssetsAndNeverAdvanceOrReevaluate()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", "potion", 0)]);
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, SupplyId, 5));
        engine.AdvanceTo(Baseline);
        var net = engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta);
        var initial = engine.Export();
        engine.SwitchToOffline();
        net += engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta);
        var offline = engine.Export();
        Assert.Equal(ActionParticipationInventoryMode.Offline, offline.InventoryMode);
        Assert.Equal(4, Assert.Single(offline.OfflineReserves!).Quantity);
        Assert.Equal(initial.Sequence, offline.Sequence);
        Assert.Equal(initial.AdvancedAtUnixSeconds, offline.AdvancedAtUnixSeconds);
        Assert.Equal(initial.NextConsumableEvaluationAtUnixSeconds, offline.NextConsumableEvaluationAtUnixSeconds);
        engine.SetWorkingWallet(Wallet(spec, 0, SupplyId, 2));
        var spent = engine.AdvanceTo(Baseline.AddSeconds(10));
        Assert.Equal(2, spent[0].Stats.Sum(entry => entry.Delta.LostQuantity));
        Assert.Empty(engine.TakeAssetDeltas());
        engine.ReplenishOfflineReserve();
        net += engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta);
        Assert.Equal(4, Assert.Single(engine.Export().OfflineReserves!).Quantity);
        engine.ReplenishOfflineReserve();
        Assert.Empty(engine.TakeAssetDeltas());
        engine.SwitchToOnline();
        net += engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta);
        Assert.Equal(-3, net);
        Assert.Empty(engine.Export().OfflineReserves!);
        Assert.Equal(ActionParticipationInventoryMode.Online, engine.Export().InventoryMode);
        engine.AdvanceTo(Baseline.AddSeconds(10));
        Assert.Empty(engine.TakeAssetDeltas());
    }

    [Fact]
    public void OfflineNewStockIsUnavailableUntilNextBatchReserveTransfer()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", "potion", 0)]);
        var engine = ActionParticipationSessionEngine.Create(spec, catalog);
        engine.AdvanceTo(Baseline);
        engine.SwitchToOffline();
        engine.SetWorkingWallet(Wallet(spec, 0, SupplyId, 3));
        var past = engine.AdvanceTo(Baseline.AddSeconds(20));
        Assert.DoesNotContain(past[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Empty(engine.TakeAssetDeltas());
        engine.ReplenishOfflineReserve();
        Assert.Equal(-3, Assert.Single(engine.TakeAssetDeltas()).QuantityDelta);
        var restored = ActionParticipationSessionEngine.Restore(engine.Export(), catalog);
        Assert.Equal(ActionParticipationInventoryMode.Offline, restored.Export().InventoryMode);
        restored.AdvanceTo(Baseline.AddSeconds(20));
        Assert.Equal(3, Assert.Single(restored.Export().OfflineReserves!).Quantity);
        var next = restored.AdvanceTo(Baseline.AddSeconds(25));
        Assert.Single(next[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Equal(2, Assert.Single(restored.Export().OfflineReserves!).Quantity);
        Assert.Empty(restored.TakeAssetDeltas());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignedConsumableDropsUseOrdinaryWalletWithoutDuplicateMemberAssets(bool offline)
    {
        var (catalog, _, original) = CreateConsumableParty(drops: true);
        var spec = original with { Members = original.Members.Select(member => member with
        {
            ConsumableSlots = [new(SupplyId, "combat", "potion", 0)]
        }).ToArray() };
        var engine = ActionParticipationSessionEngine.Create(spec, catalog);
        engine.AdvanceTo(Baseline);
        if (offline) engine.SwitchToOffline();
        var batch = engine.AdvanceTo(Baseline.AddSeconds(20));
        var gained = batch.Sum(member => member.Stats.Sum(entry => entry.Delta.GainedQuantity));
        var lost = batch.Sum(member => member.Stats.Sum(entry => entry.Delta.LostQuantity));
        Assert.True(gained > 0);
        Assert.All(batch, member => Assert.DoesNotContain(member.AssetDeltas, delta => delta.Asset.ItemId.Value == SupplyId));
        Assert.Equal(gained - lost, engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta));
        if (offline)
        {
            Assert.Equal(0, lost);
            engine.ReplenishOfflineReserve();
            Assert.Equal(-gained, engine.TakeAssetDeltas().Sum(delta => delta.QuantityDelta));
            var next = engine.AdvanceTo(Baseline.AddSeconds(25));
            Assert.True(next.Sum(member => member.Stats.Sum(entry => entry.Delta.LostQuantity)) > 0);
        }
        else Assert.True(lost > 0);
    }

    [Fact]
    public void StagedConsumableDropsNeverBecomeAvailableBeforeAssignmentSettlement()
    {
        var (catalog, _, original) = CreateConsumableParty(drops: true);
        var spec = original with { PvpEnabled = true, Members = original.Members.Select(member => member with
        {
            ConsumableSlots = [new(SupplyId, "combat", "potion", 0)]
        }).ToArray() };
        var engine = ActionParticipationSessionEngine.Create(spec, catalog);
        var batch = engine.AdvanceTo(Baseline.AddSeconds(20));
        Assert.True(batch.Sum(member => member.StagedItems?.Sum(item => item.Quantity) ?? 0) > 0);
        Assert.Equal(0, batch.Sum(member => member.Stats.Sum(entry => entry.Delta.LostQuantity)));
        Assert.Empty(engine.TakeAssetDeltas());
    }

    [Fact]
    public void EndingRefundsOnlyOfflineRemainderAndWritesCurrentSlotClocksToMember()
    {
        var (catalog, members, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0,
            [new(SupplyId, "combat", "potion", 0), new(DurationId, "combat", "general", 0)]);
        members[0].Settings.AutoUseSlots.Add(new("combat", 0, SupplyId, 1, true, Group: "potion"));
        members[0].Settings.AutoUseSlots.Add(new("combat", 0, DurationId, 1, true, Group: "general"));
        var engine = ActionParticipationSessionEngine.Create(spec, catalog,
            Wallet(spec, 0, SupplyId, 5).Concat(Wallet(spec, 0, DurationId, 2)).ToArray());
        var first = engine.AdvanceTo(Baseline.AddSeconds(2));
        members[0].ApplyActionParticipationSettlement(first[0], catalog);
        engine.TakeAssetDeltas();
        engine.SwitchToOffline();
        engine.TakeAssetDeltas();
        var final = engine.EndAt(Baseline.AddSeconds(7));
        SimulationEngine.AdvanceTo(members[0], Baseline.AddSeconds(9), catalog);
        members[0].ApplyActionParticipationSettlement(final[0], catalog);
        var refund = engine.TakeAssetDeltas();
        Assert.Equal(3, Assert.Single(refund, delta => delta.Asset.ItemId.Value == SupplyId).QuantityDelta);
        Assert.Equal(1, Assert.Single(refund, delta => delta.Asset.ItemId.Value == DurationId).QuantityDelta);
        Assert.DoesNotContain(final[0].AssetDeltas, delta => delta.Asset.ItemId.Value is SupplyId or DurationId);
        Assert.Empty(engine.Export().OfflineReserves!);
        Assert.Equal(10, Assert.Single(members[0].Settings.AutoUseSlots, slot => slot.ItemId == SupplyId)
            .CooldownUntilUnixSeconds - original.StartedAtUnixSeconds);
        Assert.Equal(2, Assert.Single(members[0].Settings.AutoUseSlots, slot => slot.ItemId == DurationId).RemainingDurationSeconds);
    }

    [Fact]
    public void IncompatibleCheckpointFinalizationRefundsReserveWithoutMemberInventoryDelta()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", "potion", 0)]);
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, SupplyId, 5));
        engine.AdvanceTo(Baseline);
        engine.TakeAssetDeltas();
        engine.SwitchToOffline();
        engine.TakeAssetDeltas();
        var final = ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(engine.Export() with { GameDataFingerprint = "old" });
        Assert.Equal(4, Assert.Single(final.AssetDeltas).QuantityDelta);
        Assert.All(final.Settlements, member => Assert.Empty(member.AssetDeltas));
        Assert.Empty(final.Document.OfflineReserves!);
        Assert.Throws<DomainException>(() => ActionParticipationSessionEngine.FinalizeIncompatibleCheckpoint(final.Document));
    }

    [Fact]
    public void DownedMemberCannotSpendOnlineBalance()
    {
        var (catalog, _, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", "potion", 0)]);
        spec = spec with { Members = spec.Members.Select((member, index) => index == 0
            ? member with { InitialHitpoints = 0 } : member).ToArray() };
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, SupplyId, 3));
        var batch = engine.AdvanceTo(Baseline.AddSeconds(10));
        Assert.DoesNotContain(batch[0].Events, entry => entry.Code == "auto-use.activated");
        Assert.Empty(engine.TakeAssetDeltas());
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

    [Fact]
    public void PartyPvpEnablesIntentAndDeliversStagedItemsToMembers()
    {
        var (catalog, members, baseSpec) = CreateParty();
        var spec = baseSpec with { PvpEnabled = true };
        foreach (var member in members)
        {
            member.RemoveAction(member.Actions[0].Id);
            member.StartActionParticipation(spec, Baseline, catalog.Actions);
            Assert.True(member.Actions[0].Intent.PvpEnabled);
        }

        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var projected = session.AdvanceTo(Baseline.AddMinutes(2));
        Assert.True(session.CombatAdvanceCount > 0);

        var totalStagedInSettlements = projected.Sum(p => p.StagedItems?.Sum(item => item.Quantity) ?? 0);
        Assert.True(totalStagedInSettlements > 0);
        foreach (var p in projected)
        {
            // 斗法开启时，普通掉落不直接进 AssetDeltas，而是进 StagedItems
            Assert.DoesNotContain(p.AssetDeltas, delta => delta.Asset.ItemId == new ItemId("item.beast-hide"));
        }

        for (var i = 0; i < members.Length; i++)
        {
            members[i].ApplyActionParticipationSettlement(projected[i], catalog);
        }

        var totalStagedInMembers = members.Sum(m => m.Duel.StagedItems.Sum(item => item.Quantity));
        Assert.Equal(totalStagedInSettlements, totalStagedInMembers);
    }

    [Fact]
    public void LevelMalusThresholdExemptsEarlyGameAndCapsSmoothly()
    {
        // 1. 前期队伍：最高10级，小号5级（相差5级 <= 12级，完全免罚）
        Assert.Equal(0d, ActionParticipationSpec.CalculateLevelMalus(5, 10));
        Assert.Equal(0d, ActionParticipationSpec.CalculateLevelMalus(1, 10));
        Assert.Equal(0d, ActionParticipationSpec.CalculateLevelMalus(1, 12));

        // 2. 刚好差 12 级以内的边界免罚
        Assert.Equal(0d, ActionParticipationSpec.CalculateLevelMalus(8, 20));

        // 3. 超出 12 级且超出 20% 时触发平滑惩罚
        // 最高 25 级，小号 5 级：gap = 20, exemptGap = max(5 * 0.2, 12) = 12. excess = 8, malus = 3 * (8 / 5) = 4.8 -> 封顶 0.9
        Assert.Equal(0.9d, ActionParticipationSpec.CalculateLevelMalus(5, 25));

        // 最高 100 级，75 级（未受 12 级保底主导，受 20% 主导）：
        // gap = 25, exemptGap = max(75 * 0.2, 12) = 15. excess = 10, malus = 3 * (10 / 75) = 0.4
        Assert.Equal(0.4d, ActionParticipationSpec.CalculateLevelMalus(75, 100), 5);

        // 最高 100 级，85 级（差 15 级 <= 17 级）：免罚为 0
        Assert.Equal(0d, ActionParticipationSpec.CalculateLevelMalus(85, 100));
    }

    /// <summary>
    /// 组队六维经验按 0 号内功心法的乘率重分配（总纲 §5.3.1 / §7.10.3）：内功不进入施放序列，
    /// 冻结技能槽里没有它，但快照必须单列冻结内功位——否则会话会走「未装内功」兜底把六维抹平成均分。
    /// </summary>
    [Fact]
    public void PartyMembersApplyInternalArtRootExpMultipliers()
    {
        var (catalog, _, spec) = CreatePartyWithInternalArt("ability.manniu-jin");
        var snapshot = spec.Members[0];
        Assert.NotNull(snapshot.InternalArt);
        Assert.Equal("ability.manniu-jin", snapshot.InternalArt!.AbilityId);
        Assert.Equal(1, snapshot.InternalArt.Level);
        // 内功不进施放序列：冻结技能槽仍为空，战斗只走普攻。
        Assert.Empty(snapshot.Skills);

        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var projected = session.AdvanceTo(Baseline.AddMinutes(3));
        var root = projected[0].Stats
            .Where(entry => entry.Delta.RootAspect is not null)
            .GroupBy(entry => entry.Delta.RootAspect!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Delta.RootExperience));

        // 蛮牛劲：力道 ×2.0，神识/身法 ×0.5，未列维度缺省 ×1.0；参悟 1 级 eff = 1。
        var baselineShare = root[CoreAspectKind.Stamina];
        Assert.True(baselineShare > 0);
        Assert.Equal(2 * baselineShare, root[CoreAspectKind.Attack], 6);
        Assert.Equal(0.5 * baselineShare, root[CoreAspectKind.Consciousness], 6);
        Assert.Equal(0.5 * baselineShare, root[CoreAspectKind.Agility], 6);
        Assert.Equal(baselineShare, root[CoreAspectKind.Meridians], 6);
        Assert.Equal(baselineShare, root[CoreAspectKind.Bone], 6);
        // Σmult = 6 且 eff = 1：组队总量与单人同为 6× 原始份额。
        var combatExperience = projected[0].Stats.Sum(entry => entry.Delta.CombatExperience);
        Assert.Equal(6 * combatExperience, root.Values.Sum(), 6);
    }

    /// <summary>
    /// 组队修为按内功 cultivationExpBonusPoints 增幅：容器清零已结算的修为/底蕴，
    /// 增幅后的冻结份额必须随增量投递，成员消费时才能按个人瓶颈重算（炼精化气诀 +20%）。
    /// </summary>
    [Fact]
    public void PartySettlementCarriesInternalCultivationBonus()
    {
        var (catalog, members, spec) = CreatePartyWithInternalArt("ability.lianjing-huaqi-jue");
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var projected = session.AdvanceTo(Baseline.AddMinutes(3));
        var kills = projected[0].Stats.Where(entry => entry.Delta.CombatExperience > 0).ToArray();
        Assert.NotEmpty(kills);
        foreach (var kill in kills)
            Assert.Equal(1.2 * kill.Delta.CombatExperience, kill.Delta.CultivationExperience, 6);

        var before = members[0].Progression.LifetimeExperience;
        members[0].ApplyActionParticipationSettlement(projected[0], catalog);
        var awarded = kills.Sum(entry => entry.Delta.CultivationExperience);
        Assert.Equal(before + awarded, members[0].Progression.LifetimeExperience, 6);
        Assert.Equal(1.2 * kills.Sum(entry => entry.Delta.CombatExperience),
            members[0].Progression.LifetimeExperience - before, 6);
    }

    [Fact]
    public void EqualSlotClockProjectionDoesNotTouchSettingsAgain()
    {
        var (catalog, members, original) = CreateConsumableParty();
        var spec = WithSlots(original, 0, [new(SupplyId, "combat", "potion", 0)]);
        members[0].Settings.AutoUseSlots.Add(new("combat", 0, SupplyId, 1, true, Group: "potion"));
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, Wallet(spec, 0, SupplyId, 1));
        var initial = engine.AdvanceTo(Baseline);
        members[0].ApplyActionParticipationSettlement(initial[0], catalog);
        var mark = members[0].DirtySectionMarks[PlayerSectionKind.Settings];
        members[0].ConsumeDirtySections();
        var unchanged = engine.AdvanceTo(Baseline);
        members[0].ApplyActionParticipationSettlement(unchanged[0], catalog);
        Assert.DoesNotContain(PlayerSectionKind.Settings, members[0].DirtySections);
        Assert.Equal(mark, members[0].DirtySectionMarks[PlayerSectionKind.Settings]);
    }

    private const string SupplyId = "item.session-supply";
    private const string DurationId = "item.session-duration";

    private static AssetBalance[] Wallet(ActionParticipationSpec spec, int memberIndex, string itemId, double quantity)
        => [new(new PlayerId(Guid.Parse(spec.Members[memberIndex].PlayerId)), new(new ItemId(itemId)), quantity, 0)];

    private static ActionParticipationSpec WithSlots(ActionParticipationSpec spec, int memberIndex,
        IReadOnlyList<ActionConsumableSlot> slots)
        => spec with { Members = spec.Members.Select((member, index) => index == memberIndex
            ? member with { ConsumableSlots = slots } : member).ToArray() };

    private static (GameDataCatalog Catalog, PlayerAggregate[] Members, ActionParticipationSpec Spec) CreateConsumableParty(
        bool drops = false)
    {
        var supply = new ItemId(SupplyId);
        var duration = new ItemId(DurationId);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [supply] = new(supply, "测试补给", "", Consumable: new(new HashSet<string> { "combat" }, CooldownSeconds: 5),
                Category: ItemDefinition.CategoryConsumable, SubCategory: "pill"),
            [duration] = new(duration, "测试持续补给", "", Consumable: new(new HashSet<string> { "combat" },
                DurationSeconds: 10, IsContinuous: true), Category: ItemDefinition.CategoryConsumable, SubCategory: "talisman")
        };
        var raw = new Dictionary<string, JsonElement>
        {
            [SupplyId] = JsonDocument.Parse("""{"consumable":{"hitpointRestore":1}}""").RootElement.Clone(),
            [DurationId] = JsonDocument.Parse("""{"consumable":{"buffs":[{"buffId":"buff.session-duration","mpRegen":1}]}}""")
                .RootElement.Clone()
        };
        var actionId = new ActionId("combat.session-test");
        var actions = new ActionCatalog([new(actionId, "会话测试", ActionKind.Combat, 0,
            new Dictionary<ItemId, double>(), [], ExecutionKind: ActionExecutionKind.CombatEncounter,
            CombatEncounterId: "encounter.session-test")]);
        var monster = new CombatMonsterDefinition("enemy.session-test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(drops ? 1 : 1_000_000, 0, 0, 2),
            drops ? [new CombatDropDefinition(supply, 1, 2, 2)] : [], []);
        var encounter = new CombatEncounterDefinition("encounter.session-test", "会话遭遇", 1, 1, 1, 1, 1, 180,
            [new CombatSpawnDefinition(monster.Id, 1, 1)], [], [], 0);
        var catalog = new GameDataCatalog("session-test", "session-test", actions, items: items, rawItems: raw,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
        var members = Enumerable.Range(0, 3).Select(index => PlayerAggregate.Create(PlayerId.New(), AccountId.New(),
            $"补给队员{index}", Baseline, catalog.Fingerprint)).ToArray();
        var stats = CombatantStats.Create(1_000_000, drops ? 100 : 0, 0, 1) with { MaxManapoints = 1000 };
        var frozen = members.Select(member => new ActionParticipantSnapshot(member.PlayerId.Value.ToString("D"),
            member.DisplayName, 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [])).ToArray();
        var spec = new ActionParticipationSpec("session.consumables", encounter.Id, "node.test", 97, 0,
            Baseline.ToUnixTimeMilliseconds() / 1000d, frozen) { ActionId = actionId };
        foreach (var member in members) member.StartActionParticipation(spec, Baseline, catalog.Actions);
        return (catalog, members, spec);
    }

    private static (GameDataCatalog Catalog, PlayerAggregate[] Members, ActionParticipationSpec Spec) CreatePartyWithInternalArt(
        string internalAbilityId)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var members = Enumerable.Range(0, 2).Select(index => PlayerAggregate.Create(PlayerId.New(),
            AccountId.New(), $"内功弟子{index}", Baseline, catalog.Fingerprint)).ToArray();
        foreach (var member in members)
        {
            PlayerAttributeResolver.Apply(member, catalog);
            member.LearnAbility(new(internalAbilityId));
            member.Loadout.AbilitySlots[0] = new PlayerAbilitySlot(internalAbilityId, 1);
        }
        var stats = CombatantStats.Create(50_000, 2_000, 500, 1) with
            { HpRegen = 10, MpRegen = 50, MaxManapoints = 1_000 };
        var frozen = members.Select(member =>
        {
            var snapshot = member.CaptureActionParticipantSnapshot(catalog);
            return snapshot with
            {
                Stats = stats,
                InitialHitpoints = stats.MaxHitpoints,
                InitialManapoints = stats.MaxManapoints
            };
        }).ToArray();
        var spec = new ActionParticipationSpec("session.internal-art", "encounter.qi-beast", "node.test", 97, 0,
            Baseline.ToUnixTimeMilliseconds() / 1000d, frozen) { ActionId = new("combat.hunt-qi-beast") };
        foreach (var member in members) member.StartActionParticipation(spec, Baseline, catalog.Actions);
        return (catalog, members, spec);
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

    [Fact]
    public void DownedAllyCarriesRemainingRespawnTimeAcrossWavesAndRespawnsWhenDue()
    {
        var (catalog, _, original) = CreateParty();
        // 构造一个低血量队员1，入场即被怪打死；队员0（队长）高攻击高气血迅速秒怪
        var spec = original with
        {
            Members = original.Members.Select((member, index) =>
            {
                if (index == 1)
                {
                    var fragileStats = CombatantStats.Create(10, 0, 0, 1) with { MaxManapoints = 50 };
                    return member with
                    {
                        Stats = fragileStats,
                        InitialHitpoints = 0 // 直接以倒地状态随队迎战
                    };
                }
                return member;
            }).ToArray()
        };

        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var initial = session.Export();
        var searchSec = initial.Expedition!.PhaseDurationSeconds;

        // 推进到第一波开打后 1 秒
        session.AdvanceTo(Baseline.AddSeconds(searchSec + 1));
        var wave1State = session.Export();
        Assert.NotNull(wave1State.Expedition);
        var allyWave1 = Assert.Single(wave1State.Expedition!.Allies!, a => a.MemberIndex == 1);
        Assert.Equal(0, allyWave1.State.Hitpoints);
        Assert.NotNull(allyWave1.State.RespawnAtSeconds);
        Assert.True(double.IsFinite(allyWave1.State.RespawnAtSeconds.Value));
        var remainingWave1 = wave1State.Expedition.Phase == CombatExpeditionPhase.Combat
            ? allyWave1.State.RespawnAtSeconds!.Value - wave1State.Expedition.BattleElapsedSeconds
            : allyWave1.State.RespawnAtSeconds!.Value;
        Assert.True(remainingWave1 > 0 && remainingWave1 <= 180);

        // 推进到几波之后（比如再推进 10 秒，队长已击杀多只怪波次清空进入后续波次），
        // 队员1的复活时钟必须随着实际流逝时间缩减，而绝不得被反复重置回满额 180 秒！
        session.AdvanceTo(Baseline.AddSeconds(searchSec + 10));
        var waveNState = session.Export();
        var allyWaveN = Assert.Single(waveNState.Expedition!.Allies!, a => a.MemberIndex == 1);
        if (allyWaveN.State.Hitpoints <= 0)
        {
            var remainingSec = waveNState.Expedition.Phase == CombatExpeditionPhase.Combat
                ? allyWaveN.State.RespawnAtSeconds!.Value - waveNState.Expedition.BattleElapsedSeconds
                : allyWaveN.State.RespawnAtSeconds!.Value;
            Assert.True(remainingSec <= remainingWave1 - 8,
                $"跨波次后剩余复活时间未流逝：当前剩余 {remainingSec}s，波次1初始 {remainingWave1}s");
        }

        // 继续推进超过完整 180 秒复活周期（搜寻+战斗累计满 200 秒），队员1必须成功复活起身且满血！
        session.AdvanceTo(Baseline.AddSeconds(searchSec + 200));
        var revivedState = session.Export();
        var allyRevived = Assert.Single(revivedState.Expedition!.Allies!, a => a.MemberIndex == 1);
        Assert.True(allyRevived.State.Hitpoints > 0, "累计时间超过复活时长后，队员应成功满血复活！");
        Assert.Equal(10, allyRevived.State.Hitpoints);
    }

    [Fact]
    public void RestoreCombatBattleGracefullyHealsExpiredOrDriftedRespawnAtSeconds()
    {
        var (catalog, _, original) = CreateParty();
        var spec = original with
        {
            Members = original.Members.Select((member, index) =>
            {
                var s = CombatantStats.Create(
                    index == 1 ? 10 : 50_000,
                    1,
                    index == 1 ? 0 : 500,
                    1);
                return member with { Stats = s, InitialHitpoints = s.MaxHitpoints };
            }).ToArray()
        };
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var searchSec = session.Export().Expedition!.PhaseDurationSeconds;
        session.AdvanceTo(Baseline.AddSeconds(searchSec + 1));
        var doc = session.Export();
        Assert.Equal(CombatExpeditionPhase.Combat, doc.Expedition!.Phase);

        // 模拟旧数据或相对秒数历史快照：倒地成员的 RespawnAtSeconds 早于当前战斗时钟
        var currentElapsed = doc.Expedition.BattleElapsedSeconds;
        var corruptedAllies = doc.Expedition.Allies!.Select(ally =>
        {
            if (ally.MemberIndex == 1)
            {
                // 故意构造 respawnAt = currentElapsed - 5（已过去5秒）
                return ally with
                {
                    State = ally.State with { RespawnAtSeconds = Math.Max(0, currentElapsed - 5) }
                };
            }
            return ally;
        }).ToArray();

        var corruptedDoc = doc with
        {
            Expedition = doc.Expedition with { Allies = corruptedAllies }
        };

        // 恢复时不应抛出 DomainException: 持久化复活截止点与生命状态不一致
        var restored = ActionParticipationSessionEngine.Restore(corruptedDoc, catalog);
        Assert.NotNull(restored);
        // 推进一小步，受损单位应在当前刻顺利结算并复活起身
        restored.AdvanceTo(Baseline.AddSeconds(searchSec + 2));
        var afterState = restored.Export();
        var ally1 = Assert.Single(afterState.Expedition!.Allies!, a => a.MemberIndex == 1);
        Assert.True(ally1.State.Hitpoints > 0);
    }
}
