using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using DaoHua.Ecs.Systems;
using Domain.Combat;
using Domain.Common;
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
        Assert.Equal(38, CombatTraitRules.AllTraitIds.Count);
        foreach (var (id, quality, cost) in new[]
        {
            ("trait.chujian", 3, 8), ("trait.yusuzebuda", 4, 10), ("trait.woyaodashige", 4, 12),
            ("trait.dantiaowang", 5, 16), ("trait.xuyi", 6, 25), ("trait.ranhun", 5, 16),
            ("trait.bukandaowo", 3, 8), ("trait.liweifanfou", 3, 8), ("trait.jianyuxin", 5, 12),
            ("trait.huoshen", 5, 12)
        })
        {
            var definition = CombatTraitRules.AllTraits[id];
            Assert.Equal(quality, definition.Quality);
            Assert.Equal(cost, definition.Cost);
        }

        var ratiosLv1 = new Dictionary<string, double>();
        var pointsLv1 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers([CombatTraitRules.JianyuxinTraitId], ratiosLv1, pointsLv1);
        Assert.Equal(0.10, ratiosLv1["physicalAttack"], 12);
        Assert.Equal(0.10, ratiosLv1["spellAttack"], 12);

        var ratiosLv3 = new Dictionary<string, double>();
        var pointsLv3 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(
            [CombatTraitRules.JianyuxinTraitId],
            new Dictionary<string, int> { [CombatTraitRules.JianyuxinTraitId] = 3 },
            ratiosLv3,
            pointsLv3);
        Assert.Equal(0.11, ratiosLv3["physicalAttack"], 12);
        Assert.Equal(0.11, ratiosLv3["spellAttack"], 12);
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

    private static JsonElement TriggerJson(string dependency, string condition, string comparator, double value, string? buffId = null) =>
        JsonDocument.Parse(
            $$"""{"dependency":"{{dependency}}","condition":"{{condition}}","comparator":"{{comparator}}","value":{{value}}{{(buffId is null ? "" : $",\"buffId\":\"{buffId}\"")}}}""")
            .RootElement.Clone();

    [Fact]
    public void Yueqian_GrantsTeamYixingRefreshesDurationAndConsumesOncePerDamageSkillCast()
    {
        var catalog = Domain.GameData.GameDataCatalog.LoadEmbedded();
        var yueqian = catalog.Abilities["ability.yueqian"];
        var zhanyu = catalog.Abilities["ability.zhanyu"];

        // 1. 盟友施放「跃迁」：3s 后全体友方获得 5s「移形」（固定 ×2 闪避），并在下一次决策点唤醒依赖 self buff-stacks>=1 buff.yixing 的慢攻队友。
        var yixingGateTrigger = TriggerJson("self", "buff-stacks", "greater-than-equal", 1, CombatTraitRules.YixingBuffId);
        var slowAllyStats = Stats(attack: 100, interval: 1.6) with { Evasion = 10, MaxManapoints = 500, Accuracy = 1000 };
        var casterStats = Stats(attack: 10, interval: 100) with { Evasion = 15, MaxManapoints = 500 };
        var enemyStats = Stats(hp: 50_000, attack: 0, interval: 100);

        var alliedBattle = CombatEventAdvanceSystem.StartAllied(
            "yueqian-team-wake", 7,
            [
                new(slowAllyStats, [new SkillSlot(zhanyu, Triggers: [yixingGateTrigger])]),
                new(casterStats, [new SkillSlot(yueqian, Triggers: [])])
            ],
            selfIndex: 0,
            [new(enemyStats), new(enemyStats)]);

        // 0s 时 player 未拥有移形，进入 1.6s 普攻等待；3s 时 ally:1 完成跃迁并通过 Buff 变更唤醒缓存判定。
        Assert.Equal(1.6, alliedBattle.Player.NextAttackAtSeconds, 8);
        CombatEventAdvanceSystem.AdvanceTo(alliedBattle, 3.0);

        var playerYixing = Assert.Single(alliedBattle.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId);
        var allyYixing = Assert.Single(alliedBattle.PlayerAllies[1].Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId);
        Assert.Equal(1, playerYixing.Stacks);
        Assert.Equal(8.0, playerYixing.ExpiresAtSeconds, 8);
        Assert.Equal(20.0, alliedBattle.Player.EffectiveStats.Evasion, 8);
        Assert.Equal(30.0, alliedBattle.PlayerAllies[1].EffectiveStats.Evasion, 8);

        // 3.2s 普攻落刀决策被唤醒：player 立即起手斩玉（0.5s 吟唱），于 3.7s 触发切态「踏云」打全体 2 敌，且仅消耗 1 层移形。
        var followUp = CombatEventAdvanceSystem.AdvanceTo(alliedBattle, 3.8);
        var tayunHits = followUp.Events.Where(e => e.AbilityId == "ability.zhanyu").ToArray();
        Assert.Equal(2, tayunHits.Length);
        Assert.All(tayunHits, e => Assert.Equal("踏云", e.AbilityNameOverride));
        Assert.DoesNotContain(alliedBattle.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId);
        Assert.Equal(10.0, alliedBattle.Player.EffectiveStats.Evasion, 8);

        // 2. 叠层刷新持续时间但保持固定 2× 闪避；普攻与治疗不消耗也不吃 2× 命中；多段伤害只消耗 1 层且 2× 命中等价于双倍面板命中。
        var healSkill = JsonDocument.Parse("""{"id":"ability.test-heal-yixing","category":"spell","effectType":"heal","healingRatio":1,"castMilliseconds":500,"cooldownMilliseconds":50000,"targetType":"self"}""").RootElement.Clone();
        var multiHitSkill = JsonDocument.Parse("""{"id":"ability.test-multihit-yixing","category":"physical","damageRatio":1,"hitCount":3,"castMilliseconds":500,"cooldownMilliseconds":50000,"targetType":"enemy"}""").RootElement.Clone();

        var dummyYixing = new CombatBuffState(CombatTraitRules.YixingBuffId, 100.0, 2, "移形", ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.EvasionPostRatio, 1.0)]);

        // 对比 A（Accuracy=50 + 移形 vs Accuracy=100 无移形）在多段打击下的命中等价性
        var withYixing = CombatEventAdvanceSystem.Start("acc-yixing", 42,
            new CombatantTemplate(Stats(attack: 100, interval: 100) with { Accuracy = 50, Evasion = 10, MaxManapoints = 500 }, [new SkillSlot(multiHitSkill, Triggers: [])]),
            [new(Stats(hp: 10_000, interval: 100) with { Evasion = 200 })]);
        withYixing.Player.SetBuff(dummyYixing);
        Assert.Equal(20.0, withYixing.Player.EffectiveStats.Evasion, 8);

        var doubledAccNoYixing = CombatEventAdvanceSystem.Start("acc-double", 42,
            new CombatantTemplate(Stats(attack: 100, interval: 100) with { Accuracy = 100, Evasion = 10, MaxManapoints = 500 }, [new SkillSlot(multiHitSkill, Triggers: [])]),
            [new(Stats(hp: 10_000, interval: 100) with { Evasion = 200 })]);

        var yixingHits = CombatEventAdvanceSystem.AdvanceTo(withYixing, 0.6).Events;
        var baselineHits = CombatEventAdvanceSystem.AdvanceTo(doubledAccNoYixing, 0.6).Events;
        Assert.Equal(3, yixingHits.Count);
        Assert.Equal(baselineHits.Select(e => e.Hit), yixingHits.Select(e => e.Hit));
        // 3 段全部命中/结算后，移形 2 层只消耗 1 层 -> 剩余 1 层
        Assert.Equal(1, Assert.Single(withYixing.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId).Stacks);

        // 普攻与治疗不消耗移形
        var healAndBasic = CombatEventAdvanceSystem.Start("heal-basic-no-consume", 9,
            new CombatantTemplate(Stats(attack: 50, interval: 1.0) with { SpellAttack = 50, MaxManapoints = 500 }, [new SkillSlot(healSkill, Triggers: [])]),
            [new(Stats(hp: 50_000, interval: 100))]);
        healAndBasic.Player.Hitpoints = 500;
        healAndBasic.Player.SetBuff(dummyYixing);
        var mixedEvents = CombatEventAdvanceSystem.AdvanceTo(healAndBasic, 2.0).Events;
        Assert.Contains(mixedEvents, e => e.AbilityId == "ability.test-heal-yixing");
        Assert.Contains(mixedEvents, e => e.AbilityId is null);
        Assert.Equal(2, Assert.Single(healAndBasic.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId).Stacks);

        // 再用跃迁叠层并验证持续时间刷新且仍为 2× 闪避
        var refreshBattle = CombatEventAdvanceSystem.Start("yueqian-refresh", 11,
            new CombatantTemplate(Stats(interval: 100) with { Evasion = 10, MaxManapoints = 500 }, [new SkillSlot(yueqian, Triggers: [])]),
            [new(Stats(hp: 10_000, interval: 100))]);
        refreshBattle.Player.SetBuff(new CombatBuffState(CombatTraitRules.YixingBuffId, 4.0, 1, "移形", ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.EvasionPostRatio, 1.0)]));
        CombatEventAdvanceSystem.AdvanceTo(refreshBattle, 3.1);
        var refreshed = Assert.Single(refreshBattle.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId);
        Assert.Equal(2, refreshed.Stacks);
        Assert.Equal(8.0, refreshed.ExpiresAtSeconds, 8);
        Assert.Equal(20.0, refreshBattle.Player.EffectiveStats.Evasion, 8);
    }

    [Fact]
    public void Zhanyu_MorphsIntoTayunAoEUnderYixingAndPreservesAbilityIdWithNameOverride()
    {
        var catalog = Domain.GameData.GameDataCatalog.LoadEmbedded();
        var zhanyu = catalog.Abilities["ability.zhanyu"];
        var stats = Stats(attack: 100, interval: 100) with { Accuracy = 1_000_000, MaxManapoints = 500 };
        var e1 = new CombatantTemplate(Stats(hp: 5_000, interval: 100));
        var e2 = new CombatantTemplate(Stats(hp: 5_000, interval: 100));

        // 无移形：单攻主目标，AbilityNameOverride 为空
        var plain = CombatEventAdvanceSystem.Start("zhanyu-plain", 5,
            new CombatantTemplate(stats, [new SkillSlot(zhanyu, Triggers: [])]), [e1, e2]);
        var plainEvents = CombatEventAdvanceSystem.AdvanceTo(plain, 0.6).Events;
        var plainHit = Assert.Single(plainEvents);
        Assert.Equal("ability.zhanyu", plainHit.AbilityId);
        Assert.Null(plainHit.AbilityNameOverride);
        Assert.Equal(CombatDamageKind.Physical, plainHit.DamageKind);
        Assert.Equal("metal", JsonRead.Text(zhanyu, "element"));

        // 有移形：切态「踏云」群攻全体存活敌人，消耗 1 层，保留 AbilityId 且携带 AbilityNameOverride
        var morphed = CombatEventAdvanceSystem.Start("zhanyu-tayun", 5,
            new CombatantTemplate(stats, [new SkillSlot(zhanyu, Triggers: [])]), [e1, e2]);
        morphed.Player.SetBuff(new CombatBuffState(CombatTraitRules.YixingBuffId, 10.0, 2, "移形", ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.EvasionPostRatio, 1.0)]));
        var morphEvents = CombatEventAdvanceSystem.AdvanceTo(morphed, 0.6).Events;
        Assert.Equal(2, morphEvents.Count);
        Assert.Equal(["enemy:0", "enemy:1"], morphEvents.Select(e => e.TargetId).OrderBy(x => x).ToArray());
        Assert.All(morphEvents, e =>
        {
            Assert.Equal("ability.zhanyu", e.AbilityId);
            Assert.Equal("踏云", e.AbilityNameOverride);
            Assert.Equal(110.0 * 110.0 / 111.0, e.Damage, 8);
        });
        Assert.Equal(1, Assert.Single(morphed.Player.Buffs, b => b.BuffId == CombatTraitRules.YixingBuffId).Stacks);

        // 斗法路径同样保留 AbilityId 与 AbilityNameOverride = "踏云"
        var duelInit = new DuelCombatant(PlayerId.New(), "甲", 100, stats, [new SkillSlot(zhanyu, Triggers: [])], [], []);
        var duelDef = new DuelCombatant(PlayerId.New(), "乙", 100, Stats(hp: 5_000, interval: 100),
            [new SkillSlot(catalog.Abilities["ability.yueqian"], Triggers: [])], [], []);
        // 让发起方开战通过跃迁或自带移形验证切态透传（此处直接用 DuelCombatant 携带跃迁 + 斩玉）
        var duelWithYueqian = duelInit with
        {
            Skills =
            [
                new SkillSlot(catalog.Abilities["ability.yueqian"],
                    Triggers: [TriggerJson("self", "buff-stacks", "equal", 0, CombatTraitRules.YixingBuffId)]),
                new SkillSlot(zhanyu, Triggers: [])
            ]
        };
        var duelScript = DuelScriptEngine.Resolve(duelWithYueqian, duelDef, 3, new DuelSettings(MaxDuelSeconds: 10));
        var duelTayun = duelScript.Events.FirstOrDefault(e => e.AttackerIsInitiator && e.AbilityNameOverride == "踏云");
        Assert.NotNull(duelTayun);
        Assert.Equal("ability.zhanyu", duelTayun!.AbilityId);
    }

    [Fact]
    public void Jianyuxin_RandomizesPrimaryTargetIgnoringThreatOnlyWhileHavingYixing()
    {
        var catalog = Domain.GameData.GameDataCatalog.LoadEmbedded();
        var zhanyu = catalog.Abilities["ability.zhanyu"];
        var singleSpell = JsonDocument.Parse("""{"id":"ability.test-single","category":"physical","damageRatio":1,"castMilliseconds":500,"cooldownMilliseconds":50000,"targetType":"enemy"}""").RootElement.Clone();
        var aoeWithXuyi = catalog.Abilities["ability.huoyan-fengbao"];

        CombatEncounterState SetupMultiThreat(long seed, bool withYixing, JsonElement skill, bool equipXuyi = false)
        {
            var traits = equipXuyi
                ? new[] { CombatTraitRules.JianyuxinTraitId, CombatTraitRules.XuyiTraitId }
                : new[] { CombatTraitRules.JianyuxinTraitId };
            var playerStats = Stats(hp: 100, attack: 100, interval: 100) with
            {
                SpellAttack = 100,
                Accuracy = 1_000_000,
                MaxManapoints = 500,
                Traits = traits
            };
            var enemyTemplate = new CombatantTemplate(Stats(hp: 10_000, interval: 100));
            var battle = CombatEventAdvanceSystem.Start("jianyuxin", seed,
                new CombatantTemplate(playerStats, [new SkillSlot(skill, Triggers: [])]),
                [enemyTemplate, enemyTemplate, enemyTemplate]);
            battle.Enemies[0].Threat = 0;
            battle.Enemies[1].Threat = 1_000_000;
            battle.Enemies[2].Threat = 0;
            if (withYixing)
            {
                battle.Player.SetBuff(new CombatBuffState(CombatTraitRules.YixingBuffId, 10.0, 2, "移形", ResetOnWave: true,
                    Effects: [new BuffEffect(BuffEffectChannel.EvasionPostRatio, 1.0)]));
            }
            if (equipXuyi)
            {
                battle.Player.SetBuff(new CombatBuffState(CombatTraitRules.XuyiBuffId, double.MaxValue, 200, ResetOnWave: true));
            }
            return battle;
        }

        // 无移形时，单体技能跨多颗种子全部锁定高威胁 enemy:1
        for (long seed = 1; seed <= 15; seed++)
        {
            var noYixing = SetupMultiThreat(seed, withYixing: false, singleSpell);
            var hit = Assert.Single(CombatEventAdvanceSystem.AdvanceTo(noYixing, 0.6).Events);
            Assert.Equal("enemy:1", hit.TargetId);
        }

        // 有移形时，单体技能无视仇恨且跳过外层预选 target，跨多种子覆盖非 enemy:1
        var singleTargets = new HashSet<string>();
        for (long seed = 1; seed <= 25; seed++)
        {
            var withYixing = SetupMultiThreat(seed, withYixing: true, singleSpell);
            var hit = Assert.Single(CombatEventAdvanceSystem.AdvanceTo(withYixing, 0.6).Events);
            singleTargets.Add(hit.TargetId);
        }
        Assert.Contains("enemy:0", singleTargets);
        Assert.Contains("enemy:2", singleTargets);

        // 群体技能（如火焰风暴 / 踏云）主目标同样无视仇恨随机，且联动蓄意对随机主目标打出真实伤害
        var xuyiTrueTargets = new HashSet<string>();
        for (long seed = 1; seed <= 25; seed++)
        {
            var aoeNoYixing = SetupMultiThreat(seed, withYixing: false, aoeWithXuyi, equipXuyi: true);
            var noYixingTrue = CombatEventAdvanceSystem.AdvanceTo(aoeNoYixing, 3.1).Events
                .Single(e => e.AbilityId == CombatTraitRules.XuyiTraitId && e.DamageKind == CombatDamageKind.True);
            Assert.Equal("enemy:1", noYixingTrue.TargetId);

            var aoeWithYixing = SetupMultiThreat(seed, withYixing: true, aoeWithXuyi, equipXuyi: true);
            var withYixingTrue = CombatEventAdvanceSystem.AdvanceTo(aoeWithYixing, 3.1).Events
                .Single(e => e.AbilityId == CombatTraitRules.XuyiTraitId && e.DamageKind == CombatDamageKind.True);
            xuyiTrueTargets.Add(withYixingTrue.TargetId);
        }
        Assert.Contains("enemy:0", xuyiTrueTargets);
        Assert.Contains("enemy:2", xuyiTrueTargets);
        Assert.Equal("ability.zhanyu", JsonRead.Text(zhanyu, "id"));
    }

    [Fact]
    public void HuoyanFengbao_AppliesRanhunStacksInSharedPoolAndMatchesEcsWorld()
    {
        var catalog = Domain.GameData.GameDataCatalog.LoadEmbedded();
        var huoyan = catalog.Abilities["ability.huoyan-fengbao"];
        var yueqian = catalog.Abilities["ability.yueqian"];
        var zhanyu = catalog.Abilities["ability.zhanyu"];

        // 1. 验证层数公式：floor((AP * (1 + skillBonus/100) * 0.20 + 5) * levelGrowth + 1e-9) = floor((100 * 1.5 * 0.20 + 5) * 1.02) = 35 层，并与特质燃魂同池叠加刷新
        var stats = Stats(attack: 100, interval: 100) with
        {
            SpellAttack = 100,
            SkillBonusPoints = 50,
            Accuracy = 1_000_000,
            MaxManapoints = 500,
            Traits = [CombatTraitRules.RanhunTraitId]
        };
        var enemy = new CombatantTemplate(Stats(hp: 20_000, interval: 100));
        var battle = CombatEventAdvanceSystem.Start("huoyan-formula", 7,
            new CombatantTemplate(stats, [new SkillSlot(huoyan, Level: 2, Triggers: [])]),
            [enemy, enemy]);
        var castEvents = CombatEventAdvanceSystem.AdvanceTo(battle, 3.1).Events;
        Assert.Equal(2, castEvents.Count);
        foreach (var e in battle.Enemies)
        {
            // 火焰风暴 (35 层) + 特质燃魂 (actual = (100*1.5 + 50)*1.02 = 204 -> floor(204 * 0.02) = 4 层) = 39 层，到期 3.0 + 5.0 = 8.0s
            var ranhun = Assert.Single(e.Buffs, b => b.BuffId == CombatTraitRules.RanhunBuffId);
            Assert.Equal("燃烧", ranhun.Name);
            Assert.Equal(39, ranhun.Stacks);
            Assert.Equal(8.0, ranhun.ExpiresAtSeconds, 8);
            Assert.Equal("player", ranhun.SourceUnitId);
        }
        // 推进到 4.0s 验证 39 层燃魂每秒火法 DoT 正常生效（含基础 +1 抗性修正：39 * 39 / 40 = 38.025）
        var tickResult = CombatEventAdvanceSystem.AdvanceTo(battle, 4.1);
        var dots = tickResult.Events.Where(e => e.AbilityId == CombatTraitRules.RanhunTraitId).ToArray();
        Assert.Equal(2, dots.Length);
        Assert.All(dots, d => Assert.Equal(39.0 * 39.0 / 40.0, d.Damage, 8));

        // 2. Inline 与 YuoFiberManager ECS World 双路径在「跃迁 + 斩玉(踏云) + 火焰风暴 + 剑与心」复杂战斗下的逐帧一致性
        CombatEncounterState CreateParityEncounter() => CombatEventAdvanceSystem.Start("yixing-ecs-parity", 19,
            new CombatantTemplate(
                Stats(hp: 5000, attack: 120, interval: 100) with
                {
                    SpellAttack = 120,
                    Accuracy = 1000,
                    Evasion = 20,
                    MaxManapoints = 1000,
                    Traits = [CombatTraitRules.JianyuxinTraitId, CombatTraitRules.RanhunTraitId]
                },
                [
                    new SkillSlot(yueqian, Triggers: [TriggerJson("self", "buff-stacks", "equal", 0, CombatTraitRules.YixingBuffId)]),
                    new SkillSlot(zhanyu, Triggers: []),
                    new SkillSlot(huoyan, Triggers: [])
                ]),
            [new(Stats(hp: 20_000, interval: 2.0)), new(Stats(hp: 20_000, interval: 2.0))]);

        var inline = CreateParityEncounter();
        var ecs = CreateParityEncounter();
        using var fibers = new YuoFiberManager();
        var world = fibers.CreateFiber("yixing-parity", setWorldAsDefault: false).World;
        foreach (var unit in ecs.PlayerAllies.Concat(ecs.Enemies))
        {
            var entity = new YuoEntity(world);
            entity.AddComponent<CombatantComponent>().State = unit;
            CombatTriggerSurface.Attach(entity, unit);
        }
        ecs.TriggerSink = new EcsCombatTriggerSink(world);

        var expected = CombatEventAdvanceSystem.AdvanceTo(inline, 12.0);
        var actual = CombatEventAdvanceSystem.AdvanceTo(ecs, 12.0);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Contains(expected.Events, e => e.AbilityId == "ability.zhanyu" && e.AbilityNameOverride == "踏云");
        Assert.Contains(expected.Events, e => e.AbilityId == "ability.huoyan-fengbao");
        Assert.Equal(
            JsonSerializer.Serialize(inline.Enemies.Select(CombatEncounterState.Snapshot)),
            JsonSerializer.Serialize(ecs.Enemies.Select(CombatEncounterState.Snapshot)));
    }

    [Fact]
    public void Huoshen_ConvertsElementToFireWithoutChangingKindScalesLevelAndTriggersRanhun()
    {
        // 1. 等级缩放：10 点火属性增伤每级 +5%（Lv1=10, Lv2=10.5, Lv3=11）
        foreach (var (level, expectedBonus) in new[] { (1, 10.0), (2, 10.5), (3, 11.0) })
        {
            var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
            var points = new Dictionary<string, double>(StringComparer.Ordinal);
            CombatTraitRules.AccumulateModifiers(
                [CombatTraitRules.HuoshenTraitId],
                new Dictionary<string, int>(StringComparer.Ordinal) { [CombatTraitRules.HuoshenTraitId] = level },
                ratios,
                points);
            Assert.Empty(ratios);
            Assert.Equal(expectedBonus, points["fireDamageBonusPoints"], 8);
        }

        // 2. 普通攻击保留 Physical 大类（照常吃物抗/物理甲），但元素转为 Fire（吃火伤加成与敌方火减伤，并触发燃魂）
        var huoshenBasicStats = Stats(hp: 5000, attack: 100, interval: 1.0) with
        {
            Accuracy = 1_000_000,
            FireDamageBonusPoints = 10,
            Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId]
        };
        var armoredAndFireReducedEnemy = CombatantStats.Create(10_000, 10, 100, 100) with
        {
            MagicResistance = 0,
            FireDamageReductionPoints = 100
        };
        var basicBattle = CombatEventAdvanceSystem.Start("huoshen-basic", 1,
            new CombatantTemplate(huoshenBasicStats),
            [new CombatantTemplate(armoredAndFireReducedEnemy)]);
        var basicHit = Assert.Single(CombatEventAdvanceSystem.AdvanceTo(basicBattle, 1.1).Events);
        // 物理 100 经 100 物甲穿甲公式 100*100/201，转火后 ×(1+0.10) ÷(1+1.00)；燃魂向下取整 >= 1 层
        var expectedBasic = (100.0 * 100.0 / 201.0) * 1.1 / 2.0;
        Assert.Equal(CombatDamageKind.Physical, basicHit.DamageKind);
        Assert.Equal(expectedBasic, basicHit.Damage, 8);
        Assert.Equal(1, basicBattle.Enemies[0].BuffStacks(CombatTraitRules.RanhunBuffId));

        // 3. 木系法术「掌心雷」保留 Spell 大类（吃法抗），但元素由木转为火，触发燃魂而无火神时不触发
        var catalog = Domain.GameData.GameDataCatalog.LoadEmbedded();
        var palmLightning = catalog.Abilities["ability.palm-lightning"];
        var spellEnemy = CombatantStats.Create(10_000, 10, 0, 100) with
        {
            MagicResistance = 100,
            FireDamageReductionPoints = 0
        };
        var casterBase = Stats(hp: 5000, attack: 10, interval: 100) with
        {
            SpellAttack = 100,
            Accuracy = 1_000_000,
            MaxManapoints = 500,
            FireDamageBonusPoints = 10,
            Traits = [CombatTraitRules.RanhunTraitId]
        };
        var noHuoshenSpell = CombatEventAdvanceSystem.Start("no-huoshen-spell", 2,
            new CombatantTemplate(casterBase, [new SkillSlot(palmLightning, Triggers: [])]),
            [new CombatantTemplate(spellEnemy)]);
        var noHuoshenSpellHit = Assert.Single(CombatEventAdvanceSystem.AdvanceTo(noHuoshenSpell, 2.1).Events);
        // 掌心雷 raw = 100*0.7 + 40 = 110，法抗 100 -> 110*110/211；木系不触发燃魂也不吃火增伤
        var expectedWoodSpell = 110.0 * 110.0 / 211.0;
        Assert.Equal(CombatDamageKind.Spell, noHuoshenSpellHit.DamageKind);
        Assert.Equal(expectedWoodSpell, noHuoshenSpellHit.Damage, 8);
        Assert.Equal(0, noHuoshenSpell.Enemies[0].BuffStacks(CombatTraitRules.RanhunBuffId));

        var withHuoshenSpell = CombatEventAdvanceSystem.Start("with-huoshen-spell", 2,
            new CombatantTemplate(casterBase with { Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId] },
                [new SkillSlot(palmLightning, Triggers: [])]),
            [new CombatantTemplate(spellEnemy)]);
        var withHuoshenSpellHit = Assert.Single(CombatEventAdvanceSystem.AdvanceTo(withHuoshenSpell, 2.1).Events);
        // 掌心雷 110 经法抗 100 后再吃 +10% 火伤，且触发 1 层燃魂
        Assert.Equal(CombatDamageKind.Spell, withHuoshenSpellHit.DamageKind);
        Assert.Equal(expectedWoodSpell * 1.1, withHuoshenSpellHit.Damage, 8);
        Assert.Equal(1, withHuoshenSpell.Enemies[0].BuffStacks(CombatTraitRules.RanhunBuffId));

        // 4. 阵盘反击（天雷原木法、血煞原无属性物理）均转火、吃火增伤并联动燃魂
        var diskEncounter = CombatEncounterState.Create("huoshen-disks", 17,
            new CombatantTemplate(Stats(hp: 2000, attack: 50, interval: 100) with
            {
                SpellAttack = 100,
                MaxManapoints = 200,
                FireDamageBonusPoints = 10,
                Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId]
            }),
            [new CombatantTemplate(CombatantStats.Create(10_000, 10, 0, 100) with { MagicResistance = 0 })]);
        diskEncounter.Player.Manapoints = 200;
        diskEncounter.Player.AddBuff(new CombatBuffState("formation.tianlei", 300, 1));
        diskEncounter.Player.AddBuff(new CombatBuffState("formation.xuesha", 300, 1));
        var diskEvents = new List<CombatEvent>();
        for (var count = 0; count < 40; count++)
        {
            diskEncounter.Player.LastTianleiTriggerAtSeconds = -1;
            diskEncounter.Player.LastXueshaTriggerAtSeconds = -1;
            var hitCtx = new CombatHitContext
            {
                Encounter = diskEncounter,
                Attacker = diskEncounter.Player,
                Target = diskEncounter.Enemies[0],
                AttackStats = diskEncounter.Player.EffectiveStats,
                Side = 0,
                Count = count,
                AtSeconds = count + 1.0,
                Hit = true,
                Damage = 10,
                RawDamage = 10
            };
            CombatTriggerMath.FormationReactiveStrike(hitCtx);
            if (hitCtx.DerivedEvents is { Count: > 0 })
                diskEvents.AddRange(hitCtx.DerivedEvents);
            if (diskEvents.Any(e => e.AbilityId == "item.tianlei-formation-disk")
                && diskEvents.Any(e => e.AbilityId == "item.xuesha-formation-disk"))
                break;
        }
        var tianleiHit = diskEvents.First(e => e.AbilityId == "item.tianlei-formation-disk");
        var xueshaHit = diskEvents.First(e => e.AbilityId == "item.xuesha-formation-disk");
        // 天雷：(60*60/61)*1.1；血煞：(200*200/201)*1.1
        Assert.Equal(CombatDamageKind.Spell, tianleiHit.DamageKind);
        Assert.Equal((60.0 * 60.0 / 61.0) * 1.1, tianleiHit.Damage, 8);
        Assert.Equal(CombatDamageKind.Physical, xueshaHit.DamageKind);
        Assert.Equal((200.0 * 200.0 / 201.0) * 1.1, xueshaHit.Damage, 8);
        Assert.True(diskEncounter.Enemies[0].BuffStacks(CombatTraitRules.RanhunBuffId) > 0);

        // 5. 蓄意爆发：主目标真实伤害跳过火增减伤（固定 200），副目标物理溅射转为火系物理并受火增减伤（200*200/201 * 1.1 / 2.0）
        var xuyiEnemy = CombatantStats.Create(10_000, 10, 0, 100) with
        {
            MagicResistance = 0,
            FireDamageReductionPoints = 100
        };
        var xuyiBattle = CombatEventAdvanceSystem.Start("huoshen-xuyi", 3,
            new CombatantTemplate(Stats(hp: 100, attack: 10, interval: 0.5) with
            {
                Accuracy = 1_000_000,
                FireDamageBonusPoints = 10,
                Traits = [CombatTraitRules.HuoshenTraitId, CombatTraitRules.XuyiTraitId]
            }),
            [new CombatantTemplate(xuyiEnemy), new CombatantTemplate(xuyiEnemy)]);
        xuyiBattle.Player.SetBuff(new CombatBuffState(CombatTraitRules.XuyiBuffId, double.MaxValue, 200, ResetOnWave: true));
        var xuyiEvents = CombatEventAdvanceSystem.AdvanceTo(xuyiBattle, 0.6).Events
            .Where(e => e.AbilityId == CombatTraitRules.XuyiTraitId)
            .ToArray();
        var primaryBurst = Assert.Single(xuyiEvents, e => e.DamageKind == CombatDamageKind.True);
        var splashBurst = Assert.Single(xuyiEvents, e => e.DamageKind == CombatDamageKind.Physical);
        Assert.Equal("enemy:0", primaryBurst.TargetId);
        Assert.Equal(200.0, primaryBurst.Damage, 8);
        Assert.Equal("enemy:1", splashBurst.TargetId);
        Assert.Equal((200.0 * 200.0 / 201.0) * 1.1 / 2.0, splashBurst.Damage, 8);
    }
}
