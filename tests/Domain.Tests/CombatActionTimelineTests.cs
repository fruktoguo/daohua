namespace Domain.Tests;

/// <summary>
/// 单时间线出手模型回归：普攻全单位统一 1 秒基准双乘区、AD 物理伤害；
/// 技能吟唱占用整条动作时间线（仅吃行动速度），0CD 填充技循环施放直至蓝尽回落普攻。
/// </summary>
public sealed class CombatActionTimelineTests
{
    /// <summary>
    /// 木灵（术法系怪物）普攻与物理系完全同构：基准 1.0s ×(1-ATR)×(1-攻速成长)、
    /// 100% 物理攻击力、物理伤害；掌心雷的 2 秒只属于技能吟唱，不得污染普攻间隔。
    /// </summary>
    [Fact]
    public void SpellCategoryMonsterBasicAttackIsUnifiedOneSecondAdPhysical()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var stats = catalog.CombatMonsters["enemy.wood-spirit"].Stats;
        var progress = Math.Pow(2d / 126d, 2);
        var actionFactor = 1 - 0.5 * progress;
        Assert.Equal(actionFactor * actionFactor, stats.AttackIntervalSeconds, 9);
        Assert.Equal(CombatDamageKind.Physical, stats.DamageKind);
        Assert.Equal(1 + progress, stats.BasicAttackRatio, 9);
        Assert.Equal(actionFactor, stats.CastTimeFactor, 9);
        Assert.True(stats.AttackPower > 0 && stats.SpellAttack > 0,
            "木灵普攻应走 AD，AP 仅保留给掌心雷结算。");
    }

    /// <summary>
    /// 0CD 掌心雷作为填充技完全顶替普攻：相邻敌方出手间隔恒为吟唱时长（2s×(1-ATR)），
    /// 吟唱期间不产生并行普攻；蓝尽后自动回落为普通攻击。
    /// </summary>
    [Fact]
    public void PalmLightningOccupiesTimelineAndRecastsUntilManaExhausted()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var spirit = catalog.CombatMonsters["enemy.wood-spirit"];
        // 足量高血量沙包玩家保证战斗不被提前终结。
        var player = CombatantStats.Create(1_000_000, 30, 0, 1);
        // 限制灵力上限：仅够 4 发掌心雷（4×4 灵力），之后必须回落普攻。
        var spiritStats = spirit.Stats with { MaxManapoints = 17 };
        var state = CombatEncounterState.Start("timeline", 7, player,
            [new CombatantTemplate(spiritStats, spirit.Skills)]);
        var castSeconds = 2 * spirit.Stats.CastTimeFactor;
        var result = DeterministicCombatEngine.AdvanceTo(state, 30);
        var enemyEvents = result.Events.Where(item => item.AttackerId == "enemy:0").ToArray();
        var casts = enemyEvents.Where(item => item.AbilityId == "ability.palm-lightning").ToArray();
        Assert.Equal(4, casts.Length);
        Assert.All(casts, item => Assert.Equal(CombatDamageKind.Spell, item.DamageKind));
        // 单时间线：任意相邻两次敌方出手的间隔不小于当次动作时长（普攻间隔或吟唱时长）。
        for (var i = 1; i < enemyEvents.Length; i++)
            Assert.True(enemyEvents[i].AtSeconds - enemyEvents[i - 1].AtSeconds >=
                Math.Min(state.Enemies[0].Stats.AttackIntervalSeconds, castSeconds) - 1e-9,
                "吟唱期间不得并行普通攻击。");
        // 灵力耗尽后回落普攻：事件序列尾部出现无技能 ID 的物理普攻。
        Assert.Contains(enemyEvents, item => item.AbilityId is null && item.DamageKind == CombatDamageKind.Physical);
        var firstCastAt = casts[0].AtSeconds;
        Assert.Equal(castSeconds, firstCastAt, 9);
    }

    /// <summary>
    /// 吟唱时长只受行动速度影响：普攻间隔（攻击速度乘区）不同而 CastTimeFactor 相同的单位，
    /// 掌心雷完成时刻完全一致。
    /// </summary>
    [Fact]
    public void CastDurationDependsOnlyOnActionSpeedNotAttackSpeed()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var ability = catalog.Abilities["ability.palm-lightning"];
        var slot = new MonsterSkillSlot(ability, 1, 1);
        var player = CombatantStats.Create(1_000_000, 30, 0, 1);
        CombatantStats Caster(double interval) => CombatantStats.Create(1000, 20, 0, interval) with
        {
            SpellAttack = 40, MaxManapoints = 100, CastTimeFactor = 0.8
        };
        var slow = CombatEncounterState.Start("cast-slow", 3, player, [new CombatantTemplate(Caster(2.5), [slot])]);
        var fast = CombatEncounterState.Start("cast-fast", 3, player, [new CombatantTemplate(Caster(0.4), [slot])]);
        var slowCast = DeterministicCombatEngine.AdvanceTo(slow, 1.61).Events
            .Single(item => item.AttackerId == "enemy:0" && item.AbilityId is not null);
        var fastCast = DeterministicCombatEngine.AdvanceTo(fast, 1.61).Events
            .Single(item => item.AttackerId == "enemy:0" && item.AbilityId is not null);
        // 吟唱 2s × CastTimeFactor 0.8 = 1.6s 完成，与普攻间隔无关。
        Assert.Equal(1.6, slowCast.AtSeconds, 9);
        Assert.Equal(1.6, fastCast.AtSeconds, 9);
    }

    /// <summary>
    /// 吟唱中切片的快照恢复必须与整段推进逐帧一致：PendingSkillIndex 与逐槽冷却
    /// 完整持久化，恢复后不重复扣蓝、不重排随机序列。
    /// </summary>
    [Fact]
    public void PendingCastSurvivesSnapshotRestoreWithIdenticalEvents()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var spirit = catalog.CombatMonsters["enemy.wood-spirit"];
        var player = CombatantStats.Create(1_000_000, 30, 0, 1);
        var template = new CombatantTemplate(spirit.Stats, spirit.Skills);
        var whole = CombatEncounterState.Start("restore", 11, player, [template]);
        var split = CombatEncounterState.Start("restore", 11, player, [template]);
        var expected = DeterministicCombatEngine.AdvanceTo(whole, 30).Events;
        var first = DeterministicCombatEngine.AdvanceTo(split, 1.0).Events;
        Assert.True(split.Enemies[0].PendingSkillIndex >= 0, "1s 时木灵应正处于掌心雷吟唱中。");
        var restored = CombatEncounterState.Restore("restore", split.Seed, player,
            CombatEncounterState.Snapshot(split.Player),
            [template],
            [CombatEncounterState.Snapshot(split.Enemies[0])],
            split.ElapsedSeconds, split.Outcome, split.Sequence);
        var second = DeterministicCombatEngine.AdvanceTo(restored, 30).Events;
        Assert.Equal(expected, first.Concat(second));
    }
}
