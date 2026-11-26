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
}
