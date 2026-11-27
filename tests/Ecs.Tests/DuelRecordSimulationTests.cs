using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 斗法紧凑行动录制器 ECS 回归：验证普攻/技能/物品/阵盘/特质/周期效果全记录、
/// 物理·术法·真实与五行分类、双视角单位翻转、衍生合并配额及 1000 次上限截断。
/// </summary>
public sealed class DuelRecordSimulationTests
{
    [Fact]
    public void DuelScriptEngine_RecordsAllActionKindsAndFlipsPerspective()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var fireAbility = catalog.Abilities["ability.huoyan-fengbao"];

        var initStats = CombatantStats.Create(600, 30, 0, 1.0) with
        {
            SpellAttack = 50,
            MaxManapoints = 300,
            HpRegen = 5,
            MpRegen = 5,
            Accuracy = 100,
            Evasion = 0,
            Effects = new CombatEffectProfile(HpDrainPoints: 20),
            Traits = [CombatTraitRules.RanhunTraitId, CombatTraitRules.UnseenTraitId]
        };
        var defStats = CombatantStats.Create(550, 25, 0, 1.1) with
        {
            MaxManapoints = 200,
            Accuracy = 100,
            Evasion = 0,
            Effects = new CombatEffectProfile(DamageReflectPoints: 10),
            Traits = [CombatTraitRules.XuyiTraitId]
        };

        var initiator = new DuelCombatant(
            PlayerId.New(), "甲修", 100, initStats,
            [new SkillSlot(fireAbility, 1)], [], [],
            ConsumableSlots:
            [
                new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupPotion, "item.zhixue-powder", 0, 0),
                new DuelConsumableSlotSnapshot(1, AutoUseSlotState.SlotGroupGeneral, "item.tianlei-formation-disk", 0, 0)
            ],
            ItemBalances: new Dictionary<string, double>
            {
                ["item.zhixue-powder"] = 5,
                ["item.tianlei-formation-disk"] = 2
            });

        var defender = new DuelCombatant(
            PlayerId.New(), "乙修", 100, defStats,
            [], [], []);

        var script = DuelScriptEngine.Resolve(initiator, defender, 42, DuelSettings.Default, catalog);

        Assert.NotNull(script.InitiatorRecord);
        Assert.NotNull(script.DefenderRecord);
        Assert.True(script.InitiatorRecord.IsValid);
        Assert.True(script.DefenderRecord.IsValid);

        // 双视角各自以本方为 Units[0]、对手为 Units[1]
        Assert.Equal("甲修", script.InitiatorRecord.Units![0].Name);
        Assert.True(script.InitiatorRecord.Units[0].IsAlly);
        Assert.Equal("乙修", script.InitiatorRecord.Units[1].Name);
        Assert.False(script.InitiatorRecord.Units[1].IsAlly);

        Assert.Equal("乙修", script.DefenderRecord.Units![0].Name);
        Assert.True(script.DefenderRecord.Units[0].IsAlly);
        Assert.Equal("甲修", script.DefenderRecord.Units[1].Name);
        Assert.False(script.DefenderRecord.Units[1].IsAlly);

        var actions = script.InitiatorRecord.Actions!;
        // 涵盖：技能、普攻、阵法、物品/周期、特质
        Assert.Contains(actions, a => a.Kind == DuelRecordActionKind.Skill);
        Assert.Contains(actions, a => a.Kind == DuelRecordActionKind.BasicAttack);
        Assert.Contains(actions, a => a.Kind == DuelRecordActionKind.Formation);
        Assert.Contains(actions, a => a.Kind == DuelRecordActionKind.Trait);

        // 验证火属性术法伤害与物理伤害、吸血/反伤合并挂载在同一行动下
        var skillAction = actions.First(a => a.Kind == DuelRecordActionKind.Skill);
        Assert.Contains(skillAction.Effects!, e =>
            e.Kind == DuelRecordEffectKind.Damage
            && e.DamageKind == CombatDamageKind.Spell
            && e.Element == CombatElement.Fire);
        Assert.Contains(skillAction.Effects!, e => e.Kind == DuelRecordEffectKind.HpDrain);
        Assert.Contains(skillAction.Effects!, e => e.Kind == DuelRecordEffectKind.Reflect);
    }

    [Fact]
    public void DuelActionRecorder_CapsActionsAt1000AndSetsTruncated()
    {
        var initiator = new DuelCombatant(PlayerId.New(), "甲", 10, CombatantStats.Create(100, 10, 0, 1), [], [], []);
        var defender = new DuelCombatant(PlayerId.New(), "乙", 10, CombatantStats.Create(100, 10, 0, 1), [], [], []);
        var recorder = new DuelActionRecorder(initiator, defender);
        var unitA = new CombatantState("player", initiator.Stats);
        var unitB = new CombatantState("enemy:0", defender.Stats);

        for (var i = 0; i < 1050; i++)
        {
            recorder.BeginAction(i * 0.1, unitA, DuelRecordActionKind.BasicAttack);
            recorder.RecordDamage(unitA, unitB, DuelRecordEffectKind.Damage,
                CombatDamageKind.Physical, CombatElement.None, DuelRecordHitMark.Hit, 5);
            recorder.EndAction();
        }

        var record = recorder.BuildRecord(CombatOutcome.PlayerWon, 105, viewerIsInitiator: true);
        Assert.True(record.Truncated);
        Assert.Equal(DuelBattleRecordLimits.MaxActionsPerRecord, record.Actions!.Count);
        Assert.True(record.IsValid);
    }

    /// <summary>
    /// PVP 斗法攻防双方均为玩家：无论发起方或应战方配置 target-enemy/is-player 还是 enemies/has-player，
    /// 双方均可在开场正常触发雷击符等玩家条件消耗品，杜绝「发起方哑火、仅应战方触发」的不对称回归。
    /// </summary>
    [Fact]
    public void DuelScriptEngine_BothInitiatorAndDefenderTriggerIsPlayerConsumables()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var stats = CombatantStats.Create(50_000, 100, 0, 2.0) with
        {
            SpellAttack = 100,
            MaxManapoints = 1_000,
            Accuracy = 100,
            Evasion = 0
        };
        var targetIsPlayerTrigger = JsonDocument.Parse(
            """{"dependency":"target-enemy","condition":"is-player","comparator":"is-active","value":1}""")
            .RootElement.Clone();
        var enemiesHasPlayerTrigger = JsonDocument.Parse(
            """{"dependency":"enemies","condition":"has-player","comparator":"is-active","value":1}""")
            .RootElement.Clone();

        DuelCombatant MakeCombatant(string name, JsonElement trigger) => new(
            PlayerId.New(), name, 100, stats, [], [], [],
            ConsumableSlots: [new DuelConsumableSlotSnapshot(0, AutoUseSlotState.SlotGroupGeneral, "item.leiji-talisman", 0, 0)],
            ItemBalances: new Dictionary<string, double> { ["item.leiji-talisman"] = 5 },
            ConsumableTriggers: new Dictionary<string, JsonElement[]> { ["item.leiji-talisman"] = [trigger] });

        var script = DuelScriptEngine.Resolve(
            MakeCombatant("发起甲", targetIsPlayerTrigger),
            MakeCombatant("应战乙", enemiesHasPlayerTrigger),
            seed: 99, DuelSettings.Default, catalog);

        Assert.Contains(script.Events, e => e.AttackerIsInitiator && e.AbilityId == "item.leiji-talisman");
        Assert.Contains(script.Events, e => !e.AttackerIsInitiator && e.AbilityId == "item.leiji-talisman");
        Assert.NotNull(script.InitiatorRecord);
        var talismanDefIdx = script.InitiatorRecord.Defs!.ToList().IndexOf("item.leiji-talisman");
        Assert.True(talismanDefIdx >= 0);
        Assert.Contains(script.InitiatorRecord.Actions!, a => a.Actor == 0 && a.Kind == DuelRecordActionKind.Item && a.DefIndex == talismanDefIdx);
        Assert.Contains(script.InitiatorRecord.Actions!, a => a.Actor == 1 && a.Kind == DuelRecordActionKind.Item && a.DefIndex == talismanDefIdx);
    }

    /// <summary>
    /// 火神 + 燃魂在同刻对称斗法中：多次普攻及技能（火焰风暴）叠加燃烧时，
    /// 斗法战报记录的是累计总层数（与 GrantBuff 口径一致），且后续每秒灼烧伤害随层数递增。
    /// </summary>
    [Fact]
    public void DuelScriptEngine_HuoshenAndRanhun_RecordsCumulativeBurnStacksAcrossHitsAndWaves()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var huoyan = catalog.Abilities["ability.huoyan-fengbao"];

        // 1. 双侧 1.0s 同刻普攻（进入 SimultaneousCombatWave）：甲修携带火神 + 燃魂，单击实伤 < 100 每次 +1 层，战报记录应按 1 -> 2 -> 3 递增。
        var attackerStats = CombatantStats.Create(50_000, 40, 0, 1.0) with
        {
            Accuracy = 1_000_000,
            Evasion = 0,
            Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId]
        };
        var dummyStats = CombatantStats.Create(50_000, 10, 0, 1.0) with
        {
            Accuracy = 1_000_000,
            Evasion = 0
        };
        var basicScript = DuelScriptEngine.Resolve(
            new DuelCombatant(PlayerId.New(), "炎修", 100, attackerStats, [], [], []),
            new DuelCombatant(PlayerId.New(), "木桩", 100, dummyStats, [], [], []),
            seed: 7, new DuelSettings(MaxDuelSeconds: 4.5), catalog);

        var basicRecord = basicScript.InitiatorRecord!;
        var burnDefIdx = basicRecord.Defs!.ToList().IndexOf(CombatTraitRules.RanhunBuffId);
        Assert.True(burnDefIdx >= 0);

        var basicBurnGrants = basicRecord.Actions!
            .Where(a => a.Actor == 0 && a.Kind == DuelRecordActionKind.BasicAttack)
            .SelectMany(a => a.Effects!)
            .Where(e => e.Kind == DuelRecordEffectKind.BuffGrant && e.DefIndex == burnDefIdx)
            .Select(e => e.Value)
            .ToArray();

        Assert.Equal([1d, 2d, 3d, 4d], basicBurnGrants);

        var burnTickDamages = basicRecord.Actions!
            .Where(a => a.Actor == 0 && a.Kind == DuelRecordActionKind.Trait)
            .SelectMany(a => a.Effects!)
            .Where(e => e.Kind == DuelRecordEffectKind.Damage && e.Element == CombatElement.Fire)
            .Select(e => e.Value)
            .ToArray();
        Assert.True(burnTickDamages.Length >= 3);
        Assert.True(burnTickDamages[1] > burnTickDamages[0]);
        Assert.True(burnTickDamages[2] > burnTickDamages[1]);

        // 2. 同刻波次内「火焰风暴」单次施放触发特质燃魂 + 技能附着两次叠加：战报两次 BuffGrant 依次记录累加后的总层数。
        var casterStats = CombatantStats.Create(50_000, 40, 0, 3.0) with
        {
            SpellAttack = 100,
            MaxManapoints = 500,
            Accuracy = 1_000_000,
            Evasion = 0,
            Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId]
        };
        var slowDummyStats = CombatantStats.Create(50_000, 10, 0, 3.0) with
        {
            Accuracy = 1_000_000,
            Evasion = 0
        };
        var skillScript = DuelScriptEngine.Resolve(
            new DuelCombatant(PlayerId.New(), "火法", 100, casterStats, [new SkillSlot(huoyan, 1, Triggers: [])], [], []),
            new DuelCombatant(PlayerId.New(), "木桩", 100, slowDummyStats, [], [], []),
            seed: 11, new DuelSettings(MaxDuelSeconds: 3.5), catalog);

        var skillRecord = skillScript.InitiatorRecord!;
        var skillBurnDefIdx = skillRecord.Defs!.ToList().IndexOf(CombatTraitRules.RanhunBuffId);
        var stormGrants = skillRecord.Actions!
            .Single(a => a.Actor == 0 && a.Kind == DuelRecordActionKind.Skill)
            .Effects!
            .Where(e => e.Kind == DuelRecordEffectKind.BuffGrant && e.DefIndex == skillBurnDefIdx)
            .Select(e => e.Value)
            .ToArray();

        Assert.Equal(2, stormGrants.Length);
        Assert.True(stormGrants[0] >= 1);
        Assert.Equal(stormGrants[0] + 25, stormGrants[1]);
    }
}
