using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using DaoHua.Ecs.Systems;
using Domain.Combat;
using Xunit;
using YuoCore.Ecs;
using YuoCore.Runtime;

namespace Ecs.Tests;

/// <summary>机制特质的结算边界、双路径与恢复确定性回归。</summary>
[Collection("ECS telemetry")]
public sealed class CombatMechanicTraitTests
{
    private static CombatantStats Stats(double hp = 1000, double attack = 10, double interval = 100)
        => CombatantStats.Create(hp, attack, 0, interval) with { Accuracy = 0, Evasion = 0 };

    [Fact]
    public void TraitCatalog_RegistersRequestedGradesAndCosts()
    {
        Assert.Equal(36, CombatTraitRules.AllTraitIds.Count);
        foreach (var (id, quality, cost) in new[]
        {
            ("trait.chujian", 3, 8), ("trait.yusuzebuda", 4, 10), ("trait.woyaodashige", 4, 12),
            ("trait.dantiaowang", 5, 16), ("trait.xuyi", 6, 25), ("trait.ranhun", 5, 16),
            ("trait.bukandaowo", 3, 8), ("trait.liweifanfou", 3, 8)
        })
        {
            var definition = CombatTraitRules.AllTraits[id];
            Assert.Equal(quality, definition.Quality);
            Assert.Equal(cost, definition.Cost);
        }
    }

    [Fact]
    public void BattleStartBuffs_GrowPositiveValuesAndExpireWithoutRefreshing()
    {
        var stats = Stats(interval: 1) with
        {
            Evasion = 20, Traits = ["trait.chujian", "trait.yusuzebuda"],
            TraitLevels = new Dictionary<string, int> { ["trait.chujian"] = 3, ["trait.yusuzebuda"] = 3 }
        };
        var battle = CombatEventAdvanceSystem.Start("start", 1, stats, Stats());
        Assert.Equal(86, battle.Player.EffectiveStats.Evasion, 8);
        Assert.Equal(110, battle.Player.EffectiveStats.ActionSpeedPoints, 8);
        Assert.Equal(-100, battle.Player.BuffTotal(BuffEffectChannel.DamageBonusPoints));
        Assert.Equal(-100, battle.Player.EffectiveStats.Effects.HealingBonusPoints);
        Assert.Equal(1 / 2.1, battle.Player.NextAttackAtSeconds, 8);
        CombatEventAdvanceSystem.AdvanceTo(battle, 2);
        Assert.DoesNotContain(battle.Player.Buffs, buff => buff.BuffId == "buff.chujian");
        Assert.Equal(20, battle.Player.EffectiveStats.Evasion);
    }

    [Fact]
    public void Haste_LeavesOneHitpointThenAllowsNextHitToKill()
    {
        var battle = CombatEventAdvanceSystem.Start("haste", 1,
            Stats(attack: 1000, interval: 1) with { Traits = ["trait.yusuzebuda"] }, Stats(hp: 10));
        var first = CombatEventAdvanceSystem.AdvanceTo(battle, 0.5);
        Assert.Equal(1, battle.Enemies[0].Hitpoints);
        Assert.Equal(9, Assert.Single(first.Events).Damage);
        var second = CombatEventAdvanceSystem.AdvanceTo(battle, 1);
        Assert.False(battle.Enemies[0].IsAlive);
        Assert.Equal(CombatOutcome.PlayerWon, second.Outcome);
    }

    [Fact]
    public void BuffChannels_ModifySkillCastingAndHealingInTheExistingPointPool()
    {
        var ability = JsonDocument.Parse("""{"id":"ability.test-heal","category":"spell","effectType":"heal","healingRatio":1,"castMilliseconds":1000,"cooldownMilliseconds":10000,"targetType":"self"}""").RootElement.Clone();
        var stats = Stats(interval: 1) with
        {
            SpellAttack = 100, Traits = ["trait.yusuzebuda"],
            Effects = new CombatEffectProfile(HealingBonusPoints: 50)
        };
        var battle = CombatEventAdvanceSystem.Start("heal", 1, new CombatantTemplate(stats, [new SkillSlot(ability, Triggers: [])]), [new(Stats())]);
        battle.Player.Hitpoints = 500;
        Assert.Equal(0.5, battle.Player.NextAttackAtSeconds);
        var result = CombatEventAdvanceSystem.AdvanceTo(battle, 0.5);
        Assert.Equal(100 / 1.5, Assert.Single(result.Events).Healing, 8);
    }

    [Fact]
    public void ConditionalBuffs_TrackAliveUnitsOnBothSidesAndRevival()
    {
        var stats = Stats() with { Traits = ["trait.woyaodashige", "trait.dantiaowang"] };
        var battle = CombatEncounterState.CreateAllied("counts", 1, [new(stats), new(Stats())], 0, [new(Stats()), new(Stats()), new(Stats())]);
        Assert.Equal(2, battle.Player.BuffStacks("buff.woyaodashige"));
        Assert.Equal(20, battle.Player.BuffTotal(BuffEffectChannel.DamageBonusPoints));
        CombatDamageSettlement.Apply(battle, battle.Player, battle.Enemies[0], 2000);
        Assert.Equal(1, battle.Player.BuffStacks("buff.woyaodashige"));
        CombatDamageSettlement.Apply(battle, battle.Player, battle.Enemies[1], 2000);
        Assert.Equal(0, battle.Player.BuffStacks("buff.woyaodashige"));
        Assert.Equal(0, battle.Player.BuffStacks("buff.dantiaowang"));
        CombatDamageSettlement.Apply(battle, battle.Enemies[2], battle.PlayerAllies[1], 2000);
        Assert.Equal(1, battle.Player.BuffStacks("buff.dantiaowang"));
        Assert.Equal(70, battle.Player.EffectiveStats.CriticalDamagePoints);
        Assert.Equal(20, battle.Player.EffectiveStats.ActionSpeedPoints);
        var phase = new DaoHua.Ecs.Combat.CombatPhaseContext { Encounter = battle, Ally = battle.PlayerAllies[1], AllyIndex = 1 };
        CombatAllyRespawnSystem.Apply(phase);
        Assert.Equal(0, battle.Player.BuffStacks("buff.dantiaowang"));
        Assert.Equal(50, battle.Player.EffectiveStats.CriticalDamagePoints);
    }

    [Fact]
    public void AliveCountListeners_OnlyBindEquippedTraitsAndDoNotPollIds()
    {
        var active = new CountingTraits(CombatTraitRules.DuelKingTraitId);
        var passive = new CountingTraits("trait.tianshengmanli");
        var battle = CombatEncounterState.CreateAllied("listeners", 1,
            [new(Stats() with { Traits = active }), new(Stats() with { Traits = passive })], 0,
            [new(Stats() with { Traits = passive }), new(Stats())]);
        // 入战会复制装配集合；计数探针必须接在实际定格输入上，才能发现事件路径重新枚举。
        battle.Player.RestoreTraits(new CombatTraitState(active));
        battle.PlayerAllies[1].RestoreTraits(new CombatTraitState(passive));
        battle.Enemies[0].RestoreTraits(new CombatTraitState(passive));
        battle.InitializeTraitListeners();
        Assert.Equal(1, battle.TraitListeners.AliveCountListenerCount);
        var activeReads = active.Reads;
        var passiveReads = passive.Reads;

        battle.Enemies[0].Hitpoints = 0;
        battle.NotifyAliveCountsChanged();
        battle.PlayerAllies[1].Hitpoints = 0;
        battle.NotifyAliveCountsChanged();

        Assert.Equal(1, battle.Player.BuffStacks("buff.dantiaowang"));
        Assert.Equal(activeReads, active.Reads);
        Assert.Equal(passiveReads, passive.Reads);
        Assert.Empty(battle.Enemies[1].Buffs);
    }

    [Fact]
    public void ConditionalListeners_KeepEntryEquipmentAndLevelUntilNextBattle()
    {
        var stats = Stats();
        var equipped = stats with
        {
            Traits = [CombatTraitRules.DuelKingTraitId, CombatTraitRules.OutnumberedTraitId],
            TraitLevels = new Dictionary<string, int> { [CombatTraitRules.DuelKingTraitId] = 3 }
        };
        var battle = CombatEventAdvanceSystem.Start("equipment-listeners", 1, equipped, stats);
        var nextAction = battle.Player.NextAttackAtSeconds;
        Assert.Equal(2, battle.TraitListeners.AliveCountListenerCount);
        Assert.Equal(22, battle.Player.EffectiveStats.ActionSpeedPoints, 8);
        battle.Player.RefreshStats(equipped with
        {
            TraitLevels = new Dictionary<string, int> { [CombatTraitRules.DuelKingTraitId] = 5 }
        });
        battle.NotifyAliveCountsChanged();
        Assert.Equal(22, battle.Player.EffectiveStats.ActionSpeedPoints, 8);
        battle.Player.RefreshStats(stats);
        battle.NotifyAliveCountsChanged();
        Assert.Equal(2, battle.TraitListeners.AliveCountListenerCount);
        Assert.Equal(22, battle.Player.EffectiveStats.ActionSpeedPoints, 8);
        Assert.Equal(nextAction, battle.Player.NextAttackAtSeconds);

        var nextBattle = CombatEventAdvanceSystem.Start("next-equipment-listeners", 2, stats, stats);
        nextBattle.Player.RefreshStats(equipped);
        nextBattle.NotifyAliveCountsChanged();
        Assert.Equal(0, nextBattle.TraitListeners.AliveCountListenerCount);
        Assert.Empty(nextBattle.Player.TraitState.Ids);
        Assert.Equal(0, nextBattle.Player.EffectiveStats.ActionSpeedPoints);
        var equippedNextBattle = CombatEventAdvanceSystem.Start("equipped-next-battle", 3, equipped, stats);
        Assert.Equal(22, equippedNextBattle.Player.EffectiveStats.ActionSpeedPoints, 8);
    }

    [Fact]
    public void ConditionalListeners_RestoreEntryBindingsDespiteEquipmentChanges()
    {
        var stats = Stats(interval: 1);
        var player = new CombatantTemplate(stats with
        {
            Traits = [CombatTraitRules.DuelKingTraitId, CombatTraitRules.OutnumberedTraitId]
        });
        CombatantTemplate[] enemies = [new(stats), new(stats)];
        var original = CombatEventAdvanceSystem.Start("restore-listeners", 7, player, enemies);
        CombatEventAdvanceSystem.AdvanceTo(original, 0.5);
        var playerSnapshot = JsonSerializer.Deserialize<CombatantSnapshot>(
            JsonSerializer.Serialize(CombatEncounterState.Snapshot(original.Player)))!;
        var enemySnapshots = original.Enemies.Select(CombatEncounterState.Snapshot).ToArray();
        var restored = CombatEncounterState.Restore("restore-listeners", 7, new(stats), playerSnapshot,
            enemies, enemySnapshots, 0.5, CombatOutcome.Running);
        Assert.Equal(2, restored.TraitListeners.AliveCountListenerCount);
        Assert.Equal(1, restored.Player.BuffStacks("buff.woyaodashige"));

        var expected = CombatEventAdvanceSystem.AdvanceTo(original, 3);
        var actual = CombatEventAdvanceSystem.AdvanceTo(restored, 3);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(JsonSerializer.Serialize(CombatEncounterState.Snapshot(original.Player)),
            JsonSerializer.Serialize(CombatEncounterState.Snapshot(restored.Player)));

    }

    /// <summary>记录特质集合读取次数，防止人数事件重新轮询已装配或无关特质的资格。</summary>
    private sealed class CountingTraits(params string[] ids) : IReadOnlyList<string>
    {
        public int Reads { get; private set; }
        public int Count => ids.Length;
        public string this[int index] { get { Reads++; return ids[index]; } }
        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < ids.Length; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Xuyi_AccumulatesActualLossWithLevelGrowth()
    {
        var battle = CombatEncounterState.Create("accumulate", 1, new(Stats(hp: 100) with
        {
            Traits = ["trait.xuyi"], TraitLevels = new Dictionary<string, int> { ["trait.xuyi"] = 3 }
        }), [new(Stats())]);
        battle.Player.Hitpoints = 50;
        CombatDamageSettlement.Apply(battle, battle.Enemies[0], battle.Player, 40);
        Assert.Equal(44, battle.Player.BuffStacks("buff.xuyi"), 8);
        battle.Player.Hitpoints = 100;
        CombatDamageSettlement.Apply(battle, battle.Enemies[0], battle.Player, 60);
        Assert.Equal(110, battle.Player.BuffStacks("buff.xuyi"), 8);
    }

    [Fact]
    public void Xuyi_UsesStrictThresholdTruePrimaryAndPhysicalSplashWithoutRecursion()
    {
        var battle = CombatEventAdvanceSystem.Start("burst", 1,
            Stats(hp: 100, attack: 0, interval: 1) with { Traits = ["trait.xuyi"] },
            [new(Stats() with { Defense = 100, Traits = ["trait.xuyi"] }), new(Stats() with { Defense = 100, Traits = ["trait.xuyi"] })]);
        battle.StatsBook = new CombatUnitStatsBook();
        battle.Player.SetBuff(new("buff.xuyi", double.MaxValue, 100, ResetOnWave: true));
        Assert.DoesNotContain(CombatEventAdvanceSystem.AdvanceTo(battle, 1).Events, e => e.AbilityId == "trait.xuyi");
        battle.Player.SetBuff(new("buff.xuyi", double.MaxValue, 101, ResetOnWave: true));
        var bursts = CombatEventAdvanceSystem.AdvanceTo(battle, 2).Events.Where(e => e.AbilityId == "trait.xuyi").ToArray();
        Assert.Equal(2, bursts.Length);
        Assert.Equal(CombatDamageKind.True, bursts[0].DamageKind);
        Assert.Equal(101, bursts[0].Damage);
        Assert.Equal(CombatDamageKind.Physical, bursts[1].DamageKind);
        Assert.Equal(101d * 101 / 202, bursts[1].Damage, 8);
        Assert.Equal(0, battle.Player.BuffStacks("buff.xuyi"));
        Assert.All(battle.Enemies, enemy => Assert.Equal(0, enemy.BuffStacks("buff.xuyi")));
        Assert.Equal(101, battle.StatsBook.Entry("player").DamageDealtTrue);
    }

    [Fact]
    public void Ranhun_RoundsDownStacksRefreshesAndCannotProcOnZeroOrDeadTargets()
    {
        var battle = CombatEncounterState.Create("burn", 1, new(Stats() with { Traits = ["trait.ranhun"] }), [new(Stats())]);
        var target = battle.Enemies[0];
        CombatTraitReactions.ApplyRanhun(battle.Player, target, CombatElement.Fire, 0, 0);
        Assert.Empty(target.Buffs);
        CombatTraitReactions.ApplyRanhun(battle.Player, target, CombatElement.Fire, 25, 0.5);
        CombatTraitReactions.ApplyRanhun(battle.Player, target, CombatElement.Fire, 149, 1.5);
        var buff = Assert.Single(target.Buffs);
        Assert.Equal(3, buff.Stacks);
        Assert.Equal(6.5, buff.ExpiresAtSeconds);
        Assert.Equal("player", buff.SourceUnitId);
        target.Hitpoints = 0;
        CombatTraitReactions.ApplyRanhun(battle.Player, target, CombatElement.Fire, 200, 2);
        Assert.Equal(buff, Assert.Single(target.Buffs));
    }

    [Fact]
    public void Ranhun_TicksThroughExpiryWithoutSelfStackingAndRestoresDeterministically()
    {
        var player = new CombatantTemplate(Stats() with { Traits = ["trait.ranhun", "trait.yusuzebuda"] });
        var enemy = new CombatantTemplate(Stats());
        var original = CombatEventAdvanceSystem.Start("restore", 9, player, [enemy]);
        original.Player.NextAttackAtSeconds = 100;
        CombatTraitReactions.ApplyRanhun(original.Player, original.Enemies[0], CombatElement.Fire, 100, 0);
        var early = CombatEventAdvanceSystem.AdvanceTo(original, 2.4);
        var playerSnapshot = JsonSerializer.Deserialize<CombatantSnapshot>(JsonSerializer.Serialize(CombatEncounterState.Snapshot(original.Player)))!;
        var enemySnapshot = JsonSerializer.Deserialize<CombatantSnapshot>(JsonSerializer.Serialize(CombatEncounterState.Snapshot(original.Enemies[0])))!;
        var restored = CombatEncounterState.Restore("restore", 9, player, playerSnapshot, [enemy], [enemySnapshot], 2.4, original.Outcome, original.Sequence);
        Assert.Equal("player", Assert.Single(restored.Enemies[0].Buffs).SourceUnitId);
        var expected = CombatEventAdvanceSystem.AdvanceTo(original, 6);
        var actual = CombatEventAdvanceSystem.AdvanceTo(restored, 6);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(original.Enemies[0].Hitpoints, restored.Enemies[0].Hitpoints);
        Assert.Equal(5, early.Events.Count + actual.Events.Count);
        Assert.Empty(restored.Enemies[0].Buffs);
        // 两层威力 2，零抗性穿甲后 4/3，再受 -100 点增伤折半；不会自叠层。
        Assert.All(early.Events.Concat(actual.Events), e => Assert.Equal(2d / 3, e.Damage, 8));
    }

    [Fact]
    public void DirectFireHit_UsesActualLossAndDiscoversTheEntityReaction()
    {
        CombatEncounterState Create()
        {
            var result = CombatEventAdvanceSystem.Start("parity", 7,
                Stats(hp: 100, attack: 500, interval: 1) with { Element = CombatElement.Fire, Traits = ["trait.ranhun", "trait.xuyi", "trait.yusuzebuda"] },
                [new(Stats(hp: 10000)), new(Stats(hp: 10000))]);
            result.Player.SetBuff(new("buff.xuyi", double.MaxValue, 101, ResetOnWave: true));
            return result;
        }
        var inline = Create();
        var ecs = Create();
        using var fibers = new YuoFiberManager();
        var world = fibers.CreateFiber("trait-parity", setWorldAsDefault: false).World;
        foreach (var unit in ecs.PlayerAllies.Concat(ecs.Enemies))
        {
            var entity = new YuoEntity(world);
            entity.AddComponent<CombatantComponent>().State = unit;
            CombatTriggerSurface.Attach(entity, unit);
        }
        ecs.TriggerSink = new EcsCombatTriggerSink(world);
        var expected = CombatEventAdvanceSystem.AdvanceTo(inline, 3);
        var actual = CombatEventAdvanceSystem.AdvanceTo(ecs, 3);
        Assert.Contains(expected.Events, e => e.AbilityId == "trait.xuyi");
        Assert.Contains(expected.Events, e => e.AbilityId == "trait.ranhun");
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(JsonSerializer.Serialize(inline.Enemies.Select(CombatEncounterState.Snapshot)), JsonSerializer.Serialize(ecs.Enemies.Select(CombatEncounterState.Snapshot)));
        Assert.Contains(typeof(InlineTraitReactionHandler), InlineCombatTriggerRegistry.OnHitHandlerTypes);
    }
}
