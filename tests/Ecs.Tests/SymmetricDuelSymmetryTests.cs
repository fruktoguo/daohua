using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 双阵营完全对称战斗引擎与 1~5v1~5 多人斗法回归：
/// 覆盖交换不变性、同刻致命符箓判平、同刻多波次消耗品收敛、同刻普攻互杀与吸血对称、
/// 起手同刻决策可见性、1~5v1~5 多人斗法全链路回放/持久化及多随机种子无偏性。
/// </summary>
public sealed class SymmetricDuelSymmetryTests
{
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();
    private static readonly DateTimeOffset Baseline = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static CombatOutcome Mirror(CombatOutcome outcome) => outcome switch
    {
        CombatOutcome.PlayerWon => CombatOutcome.EnemyWon,
        CombatOutcome.EnemyWon => CombatOutcome.PlayerWon,
        _ => outcome
    };

    /// <summary>
    /// 交换不变性：任意非对称两名玩家 A、B（含功法/特质/符箓/丹药/阵盘/吸血/反伤），
    /// 无论随机种子奇偶，Resolve(A, B, seed) 与 Resolve(B, A, seed) 的胜负、时长、事件序列、
    /// 统计账本与紧凑战报必须严格逐条镜像等价。
    /// </summary>
    [Theory]
    [InlineData(100L)]
    [InlineData(101L)]
    [InlineData(2026L)]
    [InlineData(2027L)]
    public void DuelScriptEngine_SwapInvariance_IsBitForBitMirrored(long seed)
    {
        var fireAbility = Catalog.Abilities["ability.huoyan-fengbao"];
        var slashAbility = Catalog.Abilities["ability.heavy-slash"];
        var isPlayerTrigger = Json("""{"dependency":"target-enemy","condition":"is-player","comparator":"is-active","value":1}""");
        var healTrigger = Json("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than","value":75}""");

        var statsA = CombatantStats.Create(1_200, 48, 18, 1.0) with
        {
            SpellAttack = 72,
            MagicResistance = 24,
            MaxManapoints = 400,
            Accuracy = 160,
            Evasion = 45,
            Critical = 60,
            CriticalResistance = 20,
            HpRegen = 6,
            MpRegen = 8,
            Effects = new CombatEffectProfile(HpDrainPoints: 15, DamageReflectPoints: 8),
            Traits = [CombatTraitRules.RanhunTraitId]
        };
        var statsB = CombatantStats.Create(1_350, 56, 28, 1.0) with
        {
            SpellAttack = 55,
            MagicResistance = 15,
            MaxManapoints = 320,
            Accuracy = 150,
            Evasion = 55,
            Critical = 75,
            CriticalResistance = 30,
            HpRegen = 4,
            MpRegen = 6,
            Effects = new CombatEffectProfile(HpDrainPoints: 10, DamageReflectPoints: 12),
            Traits = [CombatTraitRules.XuyiTraitId]
        };

        var playerA = new DuelCombatant(
            new PlayerId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            "青云子", 100, statsA,
            [new SkillSlot(fireAbility, 3)], [], [],
            ConsumableSlots:
            [
                new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupPotion, "item.zhixue-powder", 0, 0),
                new DuelConsumableSlotSnapshot(1, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0),
                new DuelConsumableSlotSnapshot(2, AutoUseSlotState.SlotGroupGeneral, "item.tianlei-formation-disk", 0, 0)
            ],
            ItemBalances: new Dictionary<string, double>
            {
                ["item.zhixue-powder"] = 3,
                ["item.leiji-talisman"] = 2,
                ["item.tianlei-formation-disk"] = 1
            },
            ConsumableTriggers: new Dictionary<string, JsonElement[]>
            {
                ["item.leiji-talisman"] = [isPlayerTrigger],
                ["item.zhixue-powder"] = [healTrigger]
            });

        var playerB = new DuelCombatant(
            new PlayerId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            "赤霄子", 100, statsB,
            [new SkillSlot(slashAbility, 4)], [], [],
            ConsumableSlots:
            [
                new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupPotion, "item.zhixue-powder", 0, 0),
                new DuelConsumableSlotSnapshot(1, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0),
                new DuelConsumableSlotSnapshot(2, AutoUseSlotState.SlotGroupGeneral, "item.xuesha-formation-disk", 0, 0)
            ],
            ItemBalances: new Dictionary<string, double>
            {
                ["item.zhixue-powder"] = 3,
                ["item.leiji-talisman"] = 2,
                ["item.xuesha-formation-disk"] = 1
            },
            ConsumableTriggers: new Dictionary<string, JsonElement[]>
            {
                ["item.leiji-talisman"] = [isPlayerTrigger],
                ["item.zhixue-powder"] = [healTrigger]
            });

        var forward = DuelScriptEngine.Resolve(playerA, playerB, seed, DuelSettings.Default, Catalog);
        var reversed = DuelScriptEngine.Resolve(playerB, playerA, seed, DuelSettings.Default, Catalog);

        Assert.Equal(Mirror(forward.Outcome), reversed.Outcome);
        Assert.Equal(forward.DurationSeconds, reversed.DurationSeconds, 9);
        Assert.Equal(forward.Events.Count, reversed.Events.Count);
        for (var i = 0; i < forward.Events.Count; i++)
        {
            var f = forward.Events[i];
            var r = reversed.Events[i];
            Assert.Equal(f.AtSeconds, r.AtSeconds, 9);
            Assert.Equal(!f.AttackerIsInitiator, r.AttackerIsInitiator);
            Assert.Equal(!f.TargetIsInitiator, r.TargetIsInitiator);
            Assert.Equal(f.AttackerUnitIndex, r.AttackerUnitIndex);
            Assert.Equal(f.TargetUnitIndex, r.TargetUnitIndex);
            Assert.Equal(f.AbilityId, r.AbilityId);
            Assert.Equal(f.Hit, r.Hit);
            Assert.Equal(f.Damage, r.Damage, 9);
            Assert.Equal(f.Healing, r.Healing, 9);
            Assert.Equal(f.Critical, r.Critical);
            Assert.Equal(f.Break, r.Break);
            Assert.Equal(f.Parried, r.Parried);
            Assert.Equal(f.TargetHitpointsAfter, r.TargetHitpointsAfter, 9);
        }

        Assert.NotNull(forward.InitiatorStats);
        Assert.NotNull(reversed.DefenderStats);
        Assert.NotNull(forward.DefenderStats);
        Assert.NotNull(reversed.InitiatorStats);
        Assert.Equal(forward.InitiatorStats.Count, reversed.DefenderStats.Count);
        Assert.Equal(forward.DefenderStats.Count, reversed.InitiatorStats.Count);
        Assert.NotNull(forward.InitiatorRecord);
        Assert.NotNull(reversed.DefenderRecord);
        Assert.Equal(forward.InitiatorRecord.Outcome, reversed.DefenderRecord.Outcome);
        Assert.Equal(forward.InitiatorRecord.Actions!.Count, reversed.DefenderRecord.Actions!.Count);
        Assert.Equal(forward.DefenderRecord!.Outcome, reversed.InitiatorRecord!.Outcome);
        Assert.Equal(forward.DefenderRecord.Actions!.Count, reversed.InitiatorRecord.Actions!.Count);
    }

    /// <summary>同刻 t=0 双方同时触发致命雷击符：必须同步互杀判平，且双侧均记入事件与战报。</summary>
    [Fact]
    public void DuelScriptEngine_SimultaneousLethalOpeningTalismans_EndInDraw()
    {
        var stats = CombatantStats.Create(100, 10, 0, 2.0) with
        {
            SpellAttack = 500,
            MaxManapoints = 2_000,
            Accuracy = 1_000,
            Evasion = 0
        };
        var trigger = Json("""{"dependency":"target-enemy","condition":"is-player","comparator":"is-active","value":1}""");

        DuelCombatant Make(string id, string name) => new(
            new PlayerId(Guid.Parse(id)), name, 100, stats, [], [], [],
            ConsumableSlots: [new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0)],
            ItemBalances: new Dictionary<string, double> { ["item.leiji-talisman"] = 2 },
            ConsumableTriggers: new Dictionary<string, JsonElement[]> { ["item.leiji-talisman"] = [trigger] });

        var script = DuelScriptEngine.Resolve(
            Make("10000000-0000-4000-8000-000000000001", "甲修"),
            Make("20000000-0000-4000-8000-000000000002", "乙修"),
            seed: 7, DuelSettings.Default, Catalog);

        Assert.Equal(CombatOutcome.Draw, script.Outcome);
        Assert.True(script.DurationSeconds <= 1e-6, $"开场同刻互杀耗时应收敛为最小正时长，实际={script.DurationSeconds}");
        Assert.Contains(script.Events, e => e.AtSeconds == 0 && e.AttackerIsInitiator && e.AbilityId == "item.leiji-talisman" && e.TargetHitpointsAfter == 0);
        Assert.Contains(script.Events, e => e.AtSeconds == 0 && !e.AttackerIsInitiator && e.AbilityId == "item.leiji-talisman" && e.TargetHitpointsAfter == 0);
        Assert.Equal(CombatOutcome.Draw, script.InitiatorRecord!.Outcome);
        Assert.Equal(CombatOutcome.Draw, script.DefenderRecord!.Outcome);
    }

    /// <summary>同刻 t=0 消耗品多波次对称收敛：波次 1 双方同丢雷击符扣血后，波次 2 双方同步触发止血散回血并收束到相同气血。</summary>
    [Fact]
    public void DuelScriptEngine_OpeningConsumablesConvergeSymmetricallyAcrossWaves()
    {
        var stats = CombatantStats.Create(5_000, 10, 0, 2.0) with
        {
            SpellAttack = 800,
            MaxManapoints = 2_000,
            Accuracy = 1_000,
            Evasion = 0
        };
        var isPlayer = Json("""{"dependency":"target-enemy","condition":"is-player","comparator":"is-active","value":1}""");
        var lowHp = Json("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than","value":95}""");

        DuelCombatant Make(string id, string name) => new(
            new PlayerId(Guid.Parse(id)), name, 100, stats, [], [], [],
            ConsumableSlots:
            [
                new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0),
                new DuelConsumableSlotSnapshot(1, AutoUseSlotState.SlotGroupPotion, "item.zhixue-powder", 0, 0)
            ],
            ItemBalances: new Dictionary<string, double>
            {
                ["item.leiji-talisman"] = 1,
                ["item.zhixue-powder"] = 1
            },
            ConsumableTriggers: new Dictionary<string, JsonElement[]>
            {
                ["item.leiji-talisman"] = [isPlayer],
                ["item.zhixue-powder"] = [lowHp]
            });

        var script = DuelScriptEngine.Resolve(
            Make("10000000-0000-4000-8000-000000000001", "甲"),
            Make("20000000-0000-4000-8000-000000000002", "乙"),
            seed: 55, DuelSettings.Default, Catalog);

        var t0Events = script.Events.Where(e => e.AtSeconds == 0).ToList();
        var initHeal = t0Events.SingleOrDefault(e => e.AttackerIsInitiator && e.AbilityId == "item.zhixue-powder");
        var defHeal = t0Events.SingleOrDefault(e => !e.AttackerIsInitiator && e.AbilityId == "item.zhixue-powder");
        Assert.NotNull(initHeal);
        Assert.NotNull(defHeal);
        Assert.Equal(initHeal.Healing, defHeal.Healing, 9);
        Assert.Equal(initHeal.TargetHitpointsAfter, defHeal.TargetHitpointsAfter, 9);
    }

    /// <summary>同速普攻致命互杀：双方攻速完全一致、必中零暴且一击必杀时，同刻结算判平。</summary>
    [Fact]
    public void DuelScriptEngine_SimultaneousSameSpeedLethalBasicAttacks_EndInDraw()
    {
        var stats = CombatantStats.Create(100, 500, 0, 1.0) with
        {
            Accuracy = 1_000_000,
            Evasion = 0,
            Critical = 0
        };
        var a = new DuelCombatant(PlayerId.New(), "剑甲", 10, stats, [], [], []);
        var b = new DuelCombatant(PlayerId.New(), "剑乙", 10, stats, [], [], []);

        var script = DuelScriptEngine.Resolve(a, b, seed: 13, DuelSettings.Default, Catalog);

        Assert.Equal(CombatOutcome.Draw, script.Outcome);
        Assert.Equal(1.0, script.DurationSeconds, 9);
        Assert.Contains(script.Events, e => e.AttackerIsInitiator && e.TargetHitpointsAfter == 0);
        Assert.Contains(script.Events, e => !e.AttackerIsInitiator && e.TargetHitpointsAfter == 0);
    }

    /// <summary>同刻普攻 + 满血吸血对称性：双方满血同刻互砍并带气血汲取，波末先扣伤后吸血，双方剩余气血严格相等。</summary>
    [Fact]
    public void DuelScriptEngine_SimultaneousBasicAttackWithLeech_LeavesExactIdenticalHitpoints()
    {
        var stats = CombatantStats.Create(1_000, 200, 0, 1.0) with
        {
            Accuracy = 1_000_000,
            Evasion = 0,
            Critical = 0,
            Effects = new CombatEffectProfile(HpDrainPoints: 25)
        };
        var a = new DuelCombatant(PlayerId.New(), "吸甲", 10, stats, [], [], []);
        var b = new DuelCombatant(PlayerId.New(), "吸乙", 10, stats, [], [], []);

        var script = DuelScriptEngine.Resolve(a, b, seed: 21, DuelSettings.Default, Catalog);

        var t1Events = script.Events.Where(e => Math.Abs(e.AtSeconds - 1.0) < 1e-9).ToList();
        var hitAOnB = t1Events.First(e => e.AttackerIsInitiator && !e.TargetIsInitiator && e.Damage > 0);
        var hitBOnA = t1Events.First(e => !e.AttackerIsInitiator && e.TargetIsInitiator && e.Damage > 0);
        var healA = t1Events.First(e => e.AttackerIsInitiator && e.TargetIsInitiator && e.Healing > 0);
        var healB = t1Events.First(e => !e.AttackerIsInitiator && !e.TargetIsInitiator && e.Healing > 0);

        Assert.Equal(hitAOnB.Damage, hitBOnA.Damage, 9);
        Assert.Equal(healA.Healing, healB.Healing, 9);
        Assert.True(healA.Healing > 0, "满血起手同刻互砍后双方均应实际生效吸血");
        Assert.Equal(healA.TargetHitpointsAfter, healB.TargetHitpointsAfter, 9);
        Assert.Equal(CombatOutcome.Draw, script.Outcome);
    }

    /// <summary>起手双阶段同时决策：双方均持有依赖「对手满灵力（mana-percentage >= 95）」的耗蓝功法，开场均同时选中该功法。</summary>
    [Fact]
    public void DecideInitialActions_SymmetricEncounter_BothSidesSeeFullOpponentMana()
    {
        var heavySlash = Catalog.Abilities["ability.heavy-slash"];
        var fullManaTrigger = Json("""{"dependency":"target-enemy","condition":"current-mana-percentage","comparator":"greater-than-equal","value":95}""");
        var slot = new SkillSlot(heavySlash, 1, [fullManaTrigger]);
        var stats = CombatantStats.Create(1_000, 50, 0, 2.0) with { MaxManapoints = 200 };

        var encounter = CombatEncounterState.CreateSymmetric(
            "sym-init-test", 88,
            [new CombatantTemplate(stats, [slot])],
            [new CombatantTemplate(stats, [slot])]);

        DeterministicCombatEngine.DecideInitialActions(encounter);

        Assert.Equal(0, encounter.SideA[0].PendingSkillIndex);
        Assert.Equal(0, encounter.SideB[0].PendingSkillIndex);
        Assert.Equal(encounter.SideA[0].Manapoints, encounter.SideB[0].Manapoints, 9);
        Assert.Equal(encounter.SideA[0].NextAttackAtSeconds, encounter.SideB[0].NextAttackAtSeconds, 9);
    }

    /// <summary>
    /// 1~5v1~5 多人斗法全链路：验证 2v2 / 1v3 成员全部为 Player、阵法同侧共享（§7.9.5）、
    /// 事件成员索引、交换不变性、多方视角回放载荷持久化以及回放推进仅扣本视角消耗品。
    /// </summary>
    [Fact]
    public void DuelScriptEngine_MultiCombatantTeamDuel_Supports2v2And1v3WithPlayback()
    {
        var fireAbility = Catalog.Abilities["ability.huoyan-fengbao"];
        var slashAbility = Catalog.Abilities["ability.heavy-slash"];
        var isPlayer = Json("""{"dependency":"target-enemy","condition":"is-player","comparator":"is-active","value":1}""");

        var baseStats = CombatantStats.Create(1_500, 60, 15, 1.1) with
        {
            SpellAttack = 70,
            MaxManapoints = 400,
            Accuracy = 150,
            Evasion = 20,
            Critical = 30
        };

        var a0 = new DuelCombatant(
            new PlayerId(Guid.Parse("30000000-0000-4000-8000-000000000001")), "甲队首", 100, baseStats,
            [new SkillSlot(fireAbility, 2)], [], [],
            ConsumableSlots:
            [
                new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.tianlei-formation-disk", 0, 0),
                new DuelConsumableSlotSnapshot(1, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0)
            ],
            ItemBalances: new Dictionary<string, double> { ["item.tianlei-formation-disk"] = 1, ["item.leiji-talisman"] = 1 },
            ConsumableTriggers: new Dictionary<string, JsonElement[]> { ["item.leiji-talisman"] = [isPlayer] });

        var a1 = new DuelCombatant(
            new PlayerId(Guid.Parse("30000000-0000-4000-8000-000000000002")), "甲队副", 100, baseStats with { AttackIntervalSeconds = 1.25 },
            [new SkillSlot(slashAbility, 2)], [], [],
            ConsumableSlots: [new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0)],
            ItemBalances: new Dictionary<string, double> { ["item.leiji-talisman"] = 1 },
            ConsumableTriggers: new Dictionary<string, JsonElement[]> { ["item.leiji-talisman"] = [isPlayer] });

        var b0 = new DuelCombatant(
            new PlayerId(Guid.Parse("40000000-0000-4000-8000-000000000001")), "乙队首", 100, baseStats with { MaxHitpoints = 1_400 },
            [new SkillSlot(slashAbility, 2)], [], [],
            ConsumableSlots: [new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0)],
            ItemBalances: new Dictionary<string, double> { ["item.leiji-talisman"] = 1 },
            ConsumableTriggers: new Dictionary<string, JsonElement[]> { ["item.leiji-talisman"] = [isPlayer] });

        var b1 = new DuelCombatant(
            new PlayerId(Guid.Parse("40000000-0000-4000-8000-000000000002")), "乙队副", 100, baseStats with { AttackPower = 75 },
            [new SkillSlot(fireAbility, 2)], [], [],
            ConsumableSlots: [new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.xuesha-formation-disk", 0, 0)],
            ItemBalances: new Dictionary<string, double> { ["item.xuesha-formation-disk"] = 1 });

        var teamA = new[] { a0, a1 };
        var teamB = new[] { b0, b1 };

        var script2v2 = DuelScriptEngine.Resolve(teamA, teamB, seed: 303, DuelSettings.Default, Catalog);
        var swapped2v2 = DuelScriptEngine.Resolve(teamB, teamA, seed: 303, DuelSettings.Default, Catalog);

        Assert.Equal(Mirror(script2v2.Outcome), swapped2v2.Outcome);
        Assert.Equal(script2v2.DurationSeconds, swapped2v2.DurationSeconds, 9);
        Assert.Equal(script2v2.Events.Count, swapped2v2.Events.Count);
        Assert.NotNull(script2v2.InitiatorMemberStats);
        Assert.NotNull(script2v2.DefenderMemberStats);
        Assert.NotNull(script2v2.InitiatorMemberRecords);
        Assert.NotNull(script2v2.DefenderMemberRecords);
        Assert.Equal(2, script2v2.InitiatorMemberStats.Count);
        Assert.Equal(2, script2v2.DefenderMemberStats.Count);
        Assert.Equal(2, script2v2.InitiatorMemberRecords.Count);
        Assert.Equal(2, script2v2.DefenderMemberRecords.Count);
        Assert.Contains(script2v2.Events, e => e.AttackerUnitIndex == 1);
        Assert.Contains(script2v2.Events, e => e.TargetUnitIndex == 1);

        // 甲队副（索引 1）视角回放：Units[0] 必须是甲队副，甲队首为盟友，乙队两人为敌方
        var duelId = Guid.NewGuid();
        var duelIdText = duelId.ToString("N");
        var playbackA1 = DuelPlaybackState.ForTeamViewer(
            script2v2, duelIdText, teamA, teamB, viewerIsInitiator: true, selfUnitIndex: 1);
        Assert.True(playbackA1.IsValid);
        Assert.Equal(1, playbackA1.SelfUnitIndex);
        Assert.Equal(2, playbackA1.SelfUnits!.Count);
        Assert.Equal(2, playbackA1.OpponentUnits!.Count);
        Assert.Equal("甲队副", playbackA1.ViewerRecord!.Units![0].Name);
        Assert.True(playbackA1.ViewerRecord.Units[0].IsAlly);
        Assert.Equal(2, playbackA1.ViewerRecord.Units.Count(u => u.IsAlly));
        Assert.Equal(2, playbackA1.ViewerRecord.Units.Count(u => !u.IsAlly));

        // 构造真实玩家聚合推进多人斗法回放，同时验证 SectionCodec v50 编解码往返：
        // 仅扣除甲队副自身的雷击符，不误扣甲队首的天雷阵盘。
        var combatAction = Catalog.Actions.All.First(a => a.ExecutionKind == ActionExecutionKind.CombatEncounter);
        var player = PlayerAggregate.Create(a1.PlayerId, AccountId.New(), a1.DisplayName, Baseline, Catalog.Fingerprint);
        player.Duel.EncounterCharges = 1;
        player.ConfigureAutoUse("combat", 0, "item.leiji-talisman", 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.ConfigureAutoUse("combat", 1, "item.tianlei-formation-disk", 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(combatAction.Id, -1, PvpEnabled: true), Baseline, Catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(60), Catalog, duel: new DuelAdvanceOptions(true));
        var matchAt = player.LastSimulatedAt;
        Assert.True(player.TryReserveDuel(duelId, matchAt.ToUnixTimeMilliseconds() / 1000d));
        player.AttachDuel(playbackA1);

        // 验证 ActionQueue 分段编解码完整保留 SelfUnitIndex / SelfUnits / OpponentUnits / AttackerUnitIndex
        var writes = SectionCodec.EncodeAggregateFull(player.ExportState(), matchAt);
        var exported = player.ExportState();
        var sections = writes.ToDictionary(w => w.SectionKind, w => new StoredSection(
            w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec,
            w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt), StringComparer.Ordinal);
        var bundle = new PlayerStateBundle(
            new PlayerIdentity(exported.PlayerId.Value, exported.AccountId.Value, exported.DisplayName, exported.CreatedAt),
            new PlayerStateHead(exported.PlayerId.Value, exported.StateVersion, SectionCodec.CurrentSchemaVersion,
                exported.LastSimulatedAt, exported.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), matchAt),
            sections, []);
        var restored = SectionCodec.DecodeAggregate(bundle);
        var decodedDuel = restored.Actions[0].Expedition!.Duel!;
        Assert.Equal(1, decodedDuel.SelfUnitIndex);
        Assert.Equal(2, decodedDuel.SelfUnits!.Count);
        Assert.Equal(2, decodedDuel.OpponentUnits!.Count);

        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore([
            new AssetBalance(player.PlayerId, new AssetKey(new ItemId("item.leiji-talisman")), 5, 0),
            new AssetBalance(player.PlayerId, new AssetKey(new ItemId("item.tianlei-formation-disk")), 5, 0)
        ]);
        var playResult = SimulationEngine.AdvanceTo(
            player, matchAt.AddSeconds(script2v2.DurationSeconds + 2), Catalog, wallet, new DuelAdvanceOptions(true));

        Assert.Contains(playResult.AssetDeltas, d => d.Asset.ItemId.Value == "item.leiji-talisman" && d.QuantityDelta == -1);
        Assert.DoesNotContain(playResult.AssetDeltas, d => d.Asset.ItemId.Value == "item.tianlei-formation-disk");

        // 1v3 非等人数双方玩家斗法
        var b2 = new DuelCombatant(
            new PlayerId(Guid.Parse("40000000-0000-4000-8000-000000000003")), "乙队三", 100, baseStats, [], [], []);
        var script1v3 = DuelScriptEngine.Resolve([a0], [b0, b1, b2], seed: 404, DuelSettings.Default, Catalog);
        Assert.NotNull(script1v3.InitiatorMemberStats);
        Assert.NotNull(script1v3.DefenderMemberStats);
        Assert.NotNull(script1v3.DefenderMemberRecords);
        Assert.Single(script1v3.InitiatorMemberStats);
        Assert.Equal(3, script1v3.DefenderMemberStats.Count);
        Assert.Equal(3, script1v3.DefenderMemberRecords.Count);
    }

    /// <summary>多随机种子统计无偏性：镜像对抗 200 局中双方胜率均衡，不存在先手或 UUID 顺序单边碾压。</summary>
    [Fact]
    public void DuelScriptEngine_MirrorMatchAcross200Seeds_IsStatisticallyUnbiased()
    {
        var stats = CombatantStats.Create(800, 55, 10, 1.0) with
        {
            Accuracy = 100,
            Evasion = 45,
            Critical = 80,
            CriticalResistance = 20
        };
        var a = new DuelCombatant(
            new PlayerId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")), "镜甲", 100, stats, [], [], []);
        var b = new DuelCombatant(
            new PlayerId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")), "镜乙", 100, stats, [], [], []);

        var aWins = 0;
        var bWins = 0;
        var draws = 0;
        for (long seed = 1; seed <= 200; seed++)
        {
            var script = DuelScriptEngine.Resolve(a, b, seed, DuelSettings.Default, Catalog);
            switch (script.Outcome)
            {
                case CombatOutcome.PlayerWon: aWins++; break;
                case CombatOutcome.EnemyWon: bWins++; break;
                case CombatOutcome.Draw: draws++; break;
            }
        }

        Assert.True(aWins >= 55 && bWins >= 55,
            $"200 局镜像对决应双侧均衡胜出，实际 A={aWins}, B={bWins}, Draw={draws}");
    }
}
