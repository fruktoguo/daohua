using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 触发条件→依赖事实注册表（TASK-20261112-15）的词表全量覆盖与唤醒语义门禁：
/// 每条合法（对象, 条件）对必须映射为选择性订阅而非全量兜底；词表外输入必须兜底全量，
/// 杜绝「漏注册即永不唤醒」。映射正确性以 SkillTriggerEvaluator 读取的可变输入为准。
/// </summary>
public sealed class TriggerWakeTests
{
    private static JsonElement Trigger(string dependency, string condition, string comparator = "less-than",
        double value = 50, string? buffId = null)
    {
        var json = "{\"dependency\":\"" + dependency + "\",\"condition\":\"" + condition
            + "\",\"comparator\":\"" + comparator + "\","
            + (buffId is null ? "\"value\":" + value : "\"buffId\":\"" + buffId + "\"") + "}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>求值器词表内全部合法（单体对象, 单体条件）对必须映射为选择性订阅。</summary>
    [Fact]
    public void UnitDependencyConditions_AllResolveSelective()
    {
        string[] units = ["self", "target", "target-enemy", "ally-lowest-hp", "ally-highest-hp",
            "enemy-lowest-hp", "enemy-highest-hp"];
        string[] numeric = ["current-hp", "missing-hp", "current-hp-percentage", "missing-hp-percentage",
            "current-mana", "missing-mana", "current-mana-percentage", "missing-mana-percentage", "buff-stacks"];
        string[] boolean = ["is-player", "is-elite", "is-boss", "is-summon",
            "stun-status", "blind-status", "silence-status"];
        foreach (var dep in units)
        {
            foreach (var cond in numeric)
            {
                var mask = TriggerWakePlanner.ResolveMask([Trigger(dep, cond, buffId: cond == "buff-stacks" ? "b1" : null)], false);
                Assert.False(mask.Facts == (BattleFact)0xFF, $"{dep}/{cond} 词表内条件不得兜底全量");
            }
            foreach (var cond in boolean)
            {
                var mask = TriggerWakePlanner.ResolveMask(
                    [Trigger(dep, cond, "is-active")], false);
                Assert.False(mask.Facts == (BattleFact)0xFF, $"{dep}/{cond} 词表内条件不得兜底全量");
            }
        }
    }

    /// <summary>集合对象词表内全部条件必须映射为选择性订阅（含集合事实位）。</summary>
    [Fact]
    public void SetDependencyConditions_AllResolveSelectiveWithSetFact()
    {
        string[] deps = ["ally", "allies", "enemy", "enemies"];
        string[] conditions = ["alive-count", "dead-count", "total-count", "total-hp-percentage",
            "total-mana-percentage", "lowest-hp-percentage", "highest-hp-percentage",
            "has-player", "has-elite", "has-boss", "has-summon"];
        foreach (var dep in deps)
            foreach (var cond in conditions)
            {
                var mask = TriggerWakePlanner.ResolveMask(
                    [Trigger(dep, cond, cond.StartsWith("has-") ? "is-active" : "less-than")], false);
                var expectedList = dep is "ally" or "allies" ? BattleFact.AllyList : BattleFact.EnemyList;
                Assert.True((mask.Facts & expectedList) != 0,
                    $"{dep}/{cond} 必须订阅集合事实位");
                Assert.False(mask.Facts == (BattleFact)0xFF, $"{dep}/{cond} 词表内条件不得兜底全量");
            }
    }

    /// <summary>battle/elapsed-seconds 订阅时钟事实位（每边界恒置位，等价旧全扫语义）。</summary>
    [Fact]
    public void BattleElapsedCondition_SubscribesElapsed()
    {
        var mask = TriggerWakePlanner.ResolveMask([Trigger("battle", "elapsed-seconds", "greater-than-equal", 3)], false);
        Assert.Equal(BattleFact.Elapsed, mask.Facts);
        Assert.True(mask.IsEmpty == false);
    }

    /// <summary>词表外/非法输入必须兜底全量订阅（永不漏唤醒）。</summary>
    [Fact]
    public void UnknownConditions_FallBackToFull()
    {
        var badDep = TriggerWakePlanner.ResolveMask([Trigger("mystery", "current-hp")], false);
        Assert.True(badDep.WakesOn(CombatFactDelta.Full, 0));
        var badCond = TriggerWakePlanner.ResolveMask([Trigger("self", "moon-phase")], false);
        Assert.Equal((UnitChannel)0xFF, badCond.Self == 0 ? badCond.AllySide : badCond.Self | badCond.AllySide | badCond.EnemySide);
        Assert.Equal((BattleFact)0xFF, badCond.Facts);
        var nonObject = JsonDocument.Parse("\"oops\"").RootElement;
        var mask = TriggerWakePlanner.ResolveMask([nonObject], false);
        Assert.Equal((BattleFact)0xFF, mask.Facts);
    }

    /// <summary>自身血量变化只唤醒订阅血量/集合的槽，不唤醒纯灵力订阅的槽。</summary>
    [Fact]
    public void WakeDelta_SelectivelyWakesSubscribedSlots()
    {
        var hpMask = TriggerWakePlanner.ResolveMask([Trigger("self", "current-hp-percentage")], false);
        var manaMask = TriggerWakePlanner.ResolveMask([Trigger("self", "current-mana-percentage")], false);
        var eliteMask = TriggerWakePlanner.ResolveMask([Trigger("enemy", "has-elite", "is-active")], false);
        var delta = new CombatFactDelta
        {
            Allies = [UnitChannel.Hp],
            Enemies = [UnitChannel.None],
            AllyUnion = UnitChannel.Hp,
            EnemyUnion = UnitChannel.None,
            Facts = BattleFact.Elapsed,
        };
        Assert.True(hpMask.WakesOn(delta, 0));
        Assert.False(manaMask.WakesOn(delta, 0));
        Assert.False(eliteMask.WakesOn(delta, 0));
    }

    /// <summary>集火目标条件对敌方存活翻转与威胁变化敏感，对友方灵力变化无感。</summary>
    [Fact]
    public void TargetEnemyCondition_WakesOnEnemyThreatAndAlive()
    {
        var mask = TriggerWakePlanner.ResolveMask([Trigger("target-enemy", "current-hp-percentage")], false);
        var threatDelta = new CombatFactDelta
        {
            Allies = [UnitChannel.None], Enemies = [UnitChannel.Threat],
            AllyUnion = UnitChannel.None, EnemyUnion = UnitChannel.Threat,
        };
        var manaDelta = new CombatFactDelta
        {
            Allies = [UnitChannel.Mana], Enemies = [UnitChannel.None],
            AllyUnion = UnitChannel.Mana, EnemyUnion = UnitChannel.None,
        };
        Assert.True(mask.WakesOn(threatDelta, 0));
        Assert.False(mask.WakesOn(manaDelta, 0));
        Assert.True((mask.Facts & BattleFact.EnemyList) != 0);
    }

    /// <summary>敌方施放方的订阅位折算到遭遇绝对侧：enemy caster 的 ally 依赖落在 EnemySide。</summary>
    [Fact]
    public void EnemyCasterMask_SwapsSides()
    {
        var mask = TriggerWakePlanner.ResolveMask([Trigger("ally", "alive-count", "greater-than-equal", 1)], true);
        Assert.True(mask.SelfInEnemies);
        Assert.True((mask.Facts & BattleFact.EnemyList) != 0);
        Assert.True(mask.EnemySide != 0);
    }

    /// <summary>追踪器逐边界 diff：血量翻转产生 Hp 增量，无变化边界产空增量。</summary>
    [Fact]
    public void FactTracker_DiffsHpAndEmptyBoundaries()
    {
        var stats = CombatantStats.Create(5_000, 50, 10, 1.0);
        var encounter = CombatEncounterState.CreateAllied("wake.test", 42,
            [new CombatantTemplate(stats)], 0, [new CombatantTemplate(stats)]);
        var tracker = new CombatFactTracker(encounter);
        // 无变化边界：只有时钟事实。
        var d0 = tracker.Advance(encounter);
        Assert.Equal(BattleFact.Elapsed, d0.Facts);
        Assert.Equal(UnitChannel.None, d0.AllyUnion);
        // 血量翻转：产生 Hp 通道增量。
        encounter.PlayerAllies[0].Hitpoints = 2_000;
        var d1 = tracker.Advance(encounter);
        Assert.True((d1.Allies![0] & UnitChannel.Hp) != 0);
        Assert.True((d1.AllyUnion & UnitChannel.Hp) != 0);
        Assert.True((d1.EnemyUnion & UnitChannel.Hp) == 0);
    }
}
