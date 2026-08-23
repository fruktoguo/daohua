// ==============================================================================
// 文件名称: CombatTests.cs
// 主要职责: 战斗系统（Combat）全链路规则、公式计算与战斗状态机单元测试。
// 核心逻辑 / 定义内容:
//   1. 验证命中对抗、抗性减伤、攻击间隔、回血回蓝、狂暴层数等战斗核心公式。
//   2. 验证技能冷却、法力消耗、控制效果（眩晕/打断）、多敌人遭遇战波次流转与胜负结算。
//   3. 验证法宝装备词条属性加成、境界修为突破加成与战斗日志事件生成。
// ==============================================================================

namespace Domain.Tests;

/// <summary>
/// 验证 Combat（战斗系统、战斗公式、技能触发器、伤害穿透、多敌人遭遇战、装备词条与境界修为成长）。
/// </summary>
public sealed class CombatTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    /// <summary>
    /// 验证各项基础战斗公式的基准曲线形态：
    /// 包含命中率对抗、抗性减伤、攻击间隔换算、生命/灵力回复、狂暴层数累加、控制抗性衰减及百分比计算。
    /// </summary>
    [Fact]
    public void Combat_formulas_match_reference_shapes()
    {
        var hitChance = CombatFormula.CalculateHitContestRate(15, 15);
        Assert.InRange(hitChance, 0.87, 0.90);
        Assert.Equal(100, CombatFormula.CalculateMitigatedDamageWithResistanceFactor(100, 0));
        Assert.Equal(3_000, CombatFormula.CalculateAttackIntervalMilliseconds(3_000, 0));
        Assert.True(CombatFormula.CalculateAttackIntervalMilliseconds(3_000, 0.5) < 3_000);
        Assert.Equal(1, CombatFormula.CalculateRegeneration(110, 0.01));
        Assert.Equal(0, CombatFormula.CalculateEnrageStacks(179_999, 180_000));
        Assert.Equal(1, CombatFormula.CalculateEnrageStacks(180_000, 180_000));
        Assert.Equal(10, CombatFormula.CalculateEnrageStacks(9_000_000, 180_000));
        Assert.Equal(0.7, CombatFormula.CalculateControlChance(0.7, 0), precision: 10);
        Assert.Equal(0.35, CombatFormula.CalculateControlChance(0.7, 100), precision: 10);
        Assert.Equal(0.7, CombatFormula.CalculateControlChance(0.7, double.PositiveInfinity), precision: 10);
        Assert.Equal(25, CombatFormula.CalculatePercentage(25, 100));
        Assert.Equal(100, CombatFormula.CalculatePercentage(150, 100));
        Assert.Equal(0, CombatFormula.CalculatePercentage(25, 0));

        var levelOne = CombatCatalog.GetAbilityAtLevel(
            CombatAbilityIds.HeavySlash,
            1);
        Assert.Equal("重斩", levelOne.Name);
        Assert.Equal(1.8, levelOne.DamageRatio);
        Assert.Equal(20, levelOne.FlatDamage);
    }

    /// <summary>
    /// 验证抗性减伤公式与破招/拆招对抗判定的数学行为。
    /// 减伤公式：伤害 = Raw^2 / (Raw + Res * K + 1)，破招与拆招根据属性点进行概率对抗。
    /// </summary>
    [Fact]
    public void Contest_and_resistance_factor_formulas_behave_correctly()
    {
        // 减伤公式测试: 伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 抗性 * 5 + 1))
        // 原始伤害 100, 抗性 0 -> 100 * (100 / 100) = 100
        var fullDmg = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(100, 0);
        Assert.Equal(100, fullDmg);

        // 原始伤害 100, 抗性 20 -> 100 * (100 / (100 + 20 * 5 + 1)) = 100 * (100 / 201) ~ 50
        var reducedDmg = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(100, 20);
        Assert.InRange(reducedDmg, 48, 52);

        // 对抗概率计算: 自身100, 对方100 -> 100 / (100 + 100 + 100) = 1/3 ~ 0.3333
        var contestRate = CombatFormula.CalculateContestRate(100, 100);
        Assert.InRange(contestRate, 0.32, 0.34);

        // 破招判定
        Assert.True(CombatFormula.CheckBreakSuccess(100, 50, 0.1));
        Assert.False(CombatFormula.CheckBreakSuccess(50, 100, 0.5));

        // 拆招判定
        Assert.True(CombatFormula.CheckParrySuccess(100, 50, 0.1));
        Assert.False(CombatFormula.CheckParrySuccess(50, 100, 0.5));
    }

    /// <summary>
    /// 验证命中 vs 闪避、暴击 vs 免暴全面采用统一对抗公式（Contest Formula）：
    /// 率 = 进攻点数 / (进攻点数 + 防守点数 + 100)，破招成功时暴击点数翻倍。
    /// </summary>
    [Fact]
    public void Hit_and_crit_use_unified_contest_formula_per_design()
    {
        // 命中 vs 闪避：
        // 0 闪避 vs 0 命中 -> 闪避率 0/100 = 0%, 命中率 100%
        Assert.Equal(0.0, CombatFormula.CalculateDodgeContestRate(0, 0));
        Assert.Equal(1.0, CombatFormula.CalculateHitContestRate(0, 0));

        // 100 闪避 vs 0 命中 -> 闪避率 100/(100+0+100) = 0.5 (50%), 命中率 50%
        Assert.Equal(0.5, CombatFormula.CalculateDodgeContestRate(100, 0));
        Assert.Equal(0.5, CombatFormula.CalculateHitContestRate(0, 100));

        // 统一对抗公式 闪避 180 vs 命中 260：闪避率 180/(180+260+100)=180/540=1/3, 命中率 1-1/3 ≈ 0.667
        var dodgeRate = CombatFormula.CalculateDodgeContestRate(180, 260);
        var hitRate = CombatFormula.CalculateHitContestRate(260, 180);
        Assert.InRange(dodgeRate, 0.32, 0.34);
        Assert.InRange(hitRate, 0.65, 0.68);

        // 暴击 vs 免暴：
        // 0 暴击 -> 暴击率 0%
        Assert.Equal(0.0, CombatFormula.CalculateCritContestRate(0, 0));
        Assert.Equal(0.0, CombatFormula.CalculateCritContestRate(0, 100));

        // 100 暴击 vs 0 免暴 -> 100/(100+0+100) = 0.5 (50%)
        Assert.Equal(0.5, CombatFormula.CalculateCritContestRate(100, 0));

        // 高阶位同阶对抗：暴击 128 点 vs 免爆 119 点 -> 128/(128+119+100) = 128/347 ≈ 0.369
        var critRate = CombatFormula.CalculateCritContestRate(128, 119);
        Assert.InRange(critRate, 0.35, 0.39);

        // 破招成功时暴击点数翻倍后再进对抗：256/(256+119+100) = 256/475 ≈ 0.539
        var breakCritRate = CombatFormula.CalculateCritContestRate(128 * 2.0, 119);
        Assert.InRange(breakCritRate, 0.52, 0.56);
    }

    /// <summary>
    /// 验证 PercentValue 百分比值对象在 线性（Linear：增伤免伤）与 非线性（NonLinear：CDR、行动速度）两种模式下的乘数计算与显示。
    /// </summary>
    [Fact]
    public void Percent_value_type_behaves_correctly_for_linear_and_nonlinear_modes()
    {
        // 1. 线性模式 (Linear): 增伤、免伤、仇恨
        // 0 -> 0%, multiplier = 1.0
        var linearZero = PercentValue.Linear(0);
        Assert.Equal(0, linearZero.DisplayPercentage);
        Assert.Equal(1.0, linearZero.Multiplier);

        // +100 -> +100%, multiplier = 2.0
        var linearPositive = PercentValue.Linear(100);
        Assert.Equal(100, linearPositive.DisplayPercentage);
        Assert.Equal(2.0, linearPositive.Multiplier);

        // -100 -> -50%, multiplier = 0.5
        var linearNegative = PercentValue.Linear(-100);
        Assert.Equal(-50, linearNegative.DisplayPercentage);
        Assert.Equal(0.5, linearNegative.Multiplier);

        // 2. 非线性模式 (NonLinear): 冷却速度、行动速度、灵力消耗减免
        // 0 -> 0% 缩减, multiplier = 1.0
        var nonLinearZero = PercentValue.NonLinear(0);
        Assert.Equal(0, nonLinearZero.DisplayPercentage);
        Assert.Equal(1.0, nonLinearZero.Multiplier);

        // +100 -> 50% 缩减, multiplier = 0.5
        var nonLinearPositive = PercentValue.NonLinear(100);
        Assert.Equal(50, nonLinearPositive.DisplayPercentage);
        Assert.Equal(0.5, nonLinearPositive.Multiplier);

        // -100 -> -100% 惩罚, multiplier = 2.0
        var nonLinearNegative = PercentValue.NonLinear(-100);
        Assert.Equal(-100, nonLinearNegative.DisplayPercentage);
        Assert.Equal(2.0, nonLinearNegative.Multiplier);
    }

    /// <summary>
    /// 验证技能吟唱在精确到达设定的毫秒边界时顺利完成并结算伤害，未达边界时保持吟唱状态。
    /// </summary>
    [Fact]
    public void Cast_resolves_when_simulation_reaches_the_exact_millisecond_boundary()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.Parse("11111111-1111-1111-1111-111111111111"), now: Baseline);
        state.Combat.AttackLevel = 1_000_000;
        state.Combat.IntelligenceLevel = 1_000_000;
        state.Combat.SwordsmanshipLevel = 1_000_000;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var beforeBoundary = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(999));
        var atBoundary = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(1000));

        Assert.Equal(CombatActionKind.Cast, beforeBoundary.PlayerAction?.Kind);
        Assert.NotEqual(CombatActionKind.Cast, atBoundary.PlayerAction?.Kind);
        Assert.True(atBoundary.DamageDealt > beforeBoundary.DamageDealt);
    }

    /// <summary>
    /// 验证在相同的初始随机种子、角色装配与起始时间下，战斗模拟器执行结果严格一致（完全确定性）。
    /// </summary>
    [Fact]
    public void Combat_simulation_is_deterministic_for_same_start_boundary()
    {
        var first = SimulationEngine.CreateDefaultState(Guid.Parse("11111111-1111-1111-1111-111111111111"), now: Baseline);
        first.Combat.LearnedAbilityIds.Add(CombatAbilityIds.HeavySlash);
        first.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;
        var second = first.Clone();
        var encounter = CombatCatalog.GetEncounter(CombatEncounterIds.QiBeast);

        var firstResult = CombatSimulator.Run(
            first.PlayerId,
            first.Combat,
            first.Buffs,
            first.Inventory.Items,
            encounter.Id,
            Baseline,
            TimeSpan.FromSeconds(60));
        var secondResult = CombatSimulator.Run(
            second.PlayerId,
            second.Combat,
            second.Buffs,
            second.Inventory.Items,
            encounter.Id,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(firstResult.EncounterId, secondResult.EncounterId);
        Assert.Equal(firstResult.SimulatedMilliseconds, secondResult.SimulatedMilliseconds);
        Assert.Equal(firstResult.Encounters, secondResult.Encounters);
        Assert.Equal(firstResult.Wins, secondResult.Wins);
        Assert.Equal(firstResult.Losses, secondResult.Losses);
        Assert.Equal(firstResult.DamageDealt, secondResult.DamageDealt);
        Assert.Equal(firstResult.DamageTaken, secondResult.DamageTaken);
        Assert.Equal(firstResult.Experience, secondResult.Experience);
        Assert.Equal(firstResult.Spirit, secondResult.Spirit);
        Assert.Equal(firstResult.DroppedQuantity, secondResult.DroppedQuantity);
        Assert.Equal(firstResult.ConsumedItems, secondResult.ConsumedItems);
        Assert.Equal(firstResult.Events, secondResult.Events);
        Assert.Equal(firstResult.MaxEnrageStacks, secondResult.MaxEnrageStacks);
        Assert.Equal(firstResult.HitpointsRegenerated, secondResult.HitpointsRegenerated);
        Assert.Equal(firstResult.ManapointsRegenerated, secondResult.ManapointsRegenerated);
        Assert.Equal(firstResult.HitpointsRecoveredFromConsumables, secondResult.HitpointsRecoveredFromConsumables);
        Assert.Equal(firstResult.ManapointsRecoveredFromConsumables, secondResult.ManapointsRecoveredFromConsumables);
        Assert.Equal(firstResult.ProcessedEvents, secondResult.ProcessedEvents);
        Assert.Equal(firstResult.PlayerAction, secondResult.PlayerAction);
        Assert.Equal(firstResult.EnemyAction, secondResult.EnemyAction);
        Assert.NotNull(firstResult.PlayerAction);
        Assert.NotNull(firstResult.EnemyAction);
        Assert.InRange(
            firstResult.PlayerAction!.ElapsedMilliseconds,
            0,
            firstResult.PlayerAction.DurationMilliseconds);
        Assert.InRange(
            firstResult.EnemyAction!.ElapsedMilliseconds,
            0,
            firstResult.EnemyAction.DurationMilliseconds);
        Assert.InRange(firstResult.ProcessedEvents, 1, 1_000);
        Assert.True(firstResult.HitpointsRegenerated >= 0);
        Assert.True(firstResult.ManapointsRegenerated >= 0);
        Assert.Contains(firstResult.Events, entry => entry.Contains("施放", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证丹药、灵茶与灵符三个独立消耗品槽位的消耗逻辑与 Buff 施加生效。
    /// </summary>
    [Fact]
    public void Combat_consumes_food_drink_and_talisman_by_independent_slot_rules()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(ItemIds.ZhixuePowder, 2);
        state.Inventory.Add(ItemIds.ShunqiPill, 2);
        state.Combat.ConsumableSlots[0] = new ConsumableSlot
        {
            ItemId = ItemIds.ZhixuePowder,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.ConsumableSlots[1] = new ConsumableSlot
        {
            ItemId = ItemIds.ShunqiPill,
            Quantity = 1,
            Enabled = true
        };

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(10));
        Assert.True(result.ConsumedItems.ContainsKey(ItemIds.ZhixuePowder) || result.ConsumedItems.ContainsKey(ItemIds.ShunqiPill));
        Assert.NotEmpty(result.BuffApplications);
    }

    /// <summary>
    /// 验证战斗正确上报消耗品槽位剩余冷却时间，且重新运行且冷却未过时不发生即时复用。
    /// </summary>
    [Fact]
    public void Combat_reports_consumable_slot_cooldowns_and_new_runs_reset_before_immediate_use()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(ItemIds.ZhixuePowder, 2);
        state.Combat.ConsumableSlots[0] = new ConsumableSlot
        {
            ItemId = ItemIds.ZhixuePowder,
            Quantity = 1,
            Enabled = true
        };

        var active = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(1));
        var restarted = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline.AddMinutes(1),
            TimeSpan.Zero);

        Assert.Equal(59_000, active.PlayerConsumableCooldowns![0]);
        Assert.Equal(60_000, restarted.PlayerConsumableCooldowns![0]);
        Assert.All(restarted.PlayerConsumableCooldowns.Skip(1), remaining => Assert.Equal(0, remaining));
    }

    /// <summary>
    /// 验证战斗模拟器严格遵守外部传入的技能与物品初始冷却时间，且波次重生时正确延续。
    /// </summary>
    [Fact]
    public void Combat_simulator_respects_initial_cooldowns_and_preserves_on_respawn()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var initialAbilityCds = new Dictionary<string, long>
        {
            [CombatAbilityIds.HeavySlash] = 5_000
        };
        var initialConsumableCds = new List<long> { 10_000, 0, 0 };

        // 模拟 1 秒（未达到 5 秒 CD）
        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(1),
            initialAbilityCooldowns: initialAbilityCds,
            initialConsumableCooldowns: initialConsumableCds);

        // 技能和物品应分别剩余约 4000ms 和 9000ms 冷却
        Assert.NotNull(result.PlayerAbilityCooldowns);
        Assert.True(result.PlayerAbilityCooldowns.TryGetValue(CombatAbilityIds.HeavySlash, out var abilityCd));
        Assert.Equal(4_000, abilityCd);

        Assert.NotNull(result.PlayerConsumableCooldowns);
        Assert.Equal(9_000, result.PlayerConsumableCooldowns[0]);
    }

    /// <summary>
    /// 验证恢复类消耗品在 5 秒事件边界处按需判定使用并回复生命值。
    /// </summary>
    [Fact]
    public void Combat_recovery_consumable_is_applied_on_five_second_event_boundaries()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.Parse("44444444-4444-4444-4444-444444444444"), now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        state.Inventory.Add(ItemIds.ZhixuePowder, 1);
        state.Combat.ConsumableSlots[0] = new ConsumableSlot
        {
            ItemId = ItemIds.ZhixuePowder,
            Quantity = 1,
            Enabled = true
        };

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(180));

        Assert.True(result.ConsumedItems.ContainsKey(ItemIds.ZhixuePowder));
        Assert.True(result.HitpointsRecoveredFromConsumables >= 0);
    }

    /// <summary>
    /// 验证功法技能触发器支持复合 AND 语义（如自身缺失生命百分比 >= 1% 且 当前法力 >= 60），满足时成功施法并产生治疗。
    /// </summary>
    [Fact]
    public void Combat_ability_trigger_uses_custom_and_semantics_and_heal_effect()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        state.Combat.AbilitySlots[0] = new CombatAbilitySlot
        {
            AbilityId = CombatAbilityIds.QingpingMelody,
            Level = 1,
            Triggers =
            [
                new CombatTrigger
                {
                    Dependency = CombatTriggerDependency.Self,
                    Condition = CombatTriggerCondition.MissingHitpointsPercentage,
                    Comparator = CombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                },
                new CombatTrigger
                {
                    Dependency = CombatTriggerDependency.Self,
                    Condition = CombatTriggerCondition.CurrentManapoints,
                    Comparator = CombatTriggerComparator.GreaterThanOrEqual,
                    Value = 60
                }
            ]
        };

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.HitpointsRecoveredFromAbilities > 0);
        Assert.Contains(result.Events, entry => entry.Contains("功法恢复", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证战斗触发器解析团队极值依赖（友方最低血量、敌方最高血量）与严格比较符（严格大于/小于等于）的求值准确性。
    /// </summary>
    [Fact]
    public void Combat_trigger_resolves_team_extreme_dependencies_and_strict_comparators()
    {
        var state = SimulationEngine.CreateDefaultState(
            // 与上方触发器测试使用同一玩家 ID 与起始时间，保证随机轨迹一致：该轨迹下玩家必定受伤。
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        // 友方生命极值在单人拓扑下解析为玩家自身，敌方极值解析为当前目标；
        // 组合内同时使用严格比较符，覆盖新增枚举的求值路径。
        state.Combat.AbilitySlots[0] = new CombatAbilitySlot
        {
            AbilityId = CombatAbilityIds.QingpingMelody,
            Level = 1,
            Triggers =
            [
                new CombatTrigger
                {
                    Dependency = CombatTriggerDependency.AllyLowestHitpoints,
                    Condition = CombatTriggerCondition.MissingHitpointsPercentage,
                    Comparator = CombatTriggerComparator.GreaterThan,
                    Value = 0
                },
                new CombatTrigger
                {
                    Dependency = CombatTriggerDependency.EnemyHighestHitpoints,
                    Condition = CombatTriggerCondition.CurrentHitpointsPercentage,
                    Comparator = CombatTriggerComparator.LessThanOrEqual,
                    Value = 100
                }
            ]
        };

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.HitpointsRecoveredFromAbilities > 0);

        // 敌方生命百分比不可能严格大于 100，该组合必须永不触发。
        state.Combat.AbilitySlots[0].Triggers =
        [
            new CombatTrigger
            {
                Dependency = CombatTriggerDependency.EnemyHighestHitpoints,
                Condition = CombatTriggerCondition.CurrentHitpointsPercentage,
                Comparator = CombatTriggerComparator.GreaterThan,
                Value = 100
            }
        ];

        var blocked = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(0, blocked.HitpointsRecoveredFromAbilities);
    }

    /// <summary>
    /// 验证战斗掉落系统正确读取常规与稀有掉落表映射，在击败怪物后按概率产出对应战利品。
    /// </summary>
    [Fact]
    public void Combat_uses_regular_and_rare_drop_tables_as_item_maps()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            now: Baseline);
        state.Stats.Spirit = 20_000_000;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.SwordsmanshipLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(10),
            playerSpirit: state.Stats.Spirit);

        Assert.True(result.Wins > 0, $"Wins: {result.Wins}, Losses: {result.Losses}, Events: {string.Join(" | ", result.Events)}");
        Assert.NotNull(result.DroppedItems);
        Assert.True(result.DroppedItems!.GetValueOrDefault(ItemIds.QiBeastCore) >= result.Wins);
        Assert.Contains(result.Events, entry => entry.Contains("掉落", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证掉落数量倍率加成（CombatDropQuantity）按比例线性放大保底战利品掉落数量。
    /// </summary>
    [Fact]
    public void Combat_drop_quantity_multiplier_scales_guaranteed_drops()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            now: Baseline);
        state.Stats.Spirit = 20_000_000;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.SwordsmanshipLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.CombatDropQuantity = 1;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(10),
            playerSpirit: state.Stats.Spirit);

        Assert.True(result.Wins > 0);
        Assert.True(result.DroppedItems!.GetValueOrDefault(ItemIds.QiBeastCore) >= result.Wins * 2L);
    }

    /// <summary>
    /// 验证持续伤害（DoT / 灼烧）在固定事件周期边界上被正确驱动与结算。
    /// </summary>
    [Fact]
    public void Combat_damage_over_time_is_processed_on_event_boundaries()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            now: Baseline);
        state.Combat.StaminaLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.AttackLevel = 1;
        state.Combat.IntelligenceLevel = 10;
        state.Combat.SwordsmanshipLevel = 10;
        state.Combat.CriticalChance = -1.0;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.BlazingBlast;

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(30));

        Assert.Contains(result.Events, entry => entry.Contains("轰炎", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    /// <summary>
    /// 验证控制类效果（如眩晕）依据配置概率生效并在到达持续时间边界后准时失效解除。
    /// </summary>
    [Fact]
    public void Combat_control_effects_use_catalog_probabilities_and_expire_on_boundaries()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            now: Baseline);
        state.Combat.StaminaLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.IntelligenceLevel = 10;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(20));

        Assert.Contains(result.Events, entry => entry.Contains("重斩", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    /// <summary>
    /// 验证战斗历练行动完成时，累计获胜次数、战斗经验与战利品结算同步持久化写回状态统计与背包。
    /// </summary>
    [Fact]
    public void Combat_action_updates_stats_and_inventory_at_completion_boundary()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            now: Baseline);
        state.Stats.Spirit = 100_000;
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = false;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.SwordsmanshipLevel = 100;
        state.Combat.TalismanLevel = 100;
        SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast);

        var result = SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(300));

        Assert.Equal(1, result.CompletedActions);
        Assert.Equal(1, state.Stats.CompletedActions);
        Assert.True(state.Stats.CombatWins >= 0);
        Assert.True(state.Stats.CombatExperience >= 0);
        Assert.True(state.Inventory.Get(ItemIds.Spirit) >= 0);
        Assert.Empty(state.Queue.Entries);
    }

    /// <summary>
    /// 验证高属性强力角色能够连续扫荡击败多波低阶怪物并持续累积经验收益。
    /// </summary>
    [Fact]
    public void Stronger_loadout_can_clear_multiple_low_tier_encounters()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.Parse("22222222-2222-2222-2222-222222222222"), now: Baseline);
        state.Stats.Spirit = 20_000_000;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.SwordsmanshipLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(300),
            playerSpirit: state.Stats.Spirit);

        Assert.True(result.Wins > 0);
        Assert.True(result.Experience > 0);
    }

    /// <summary>
    /// 验证包含非法参数（如无效消耗品物品）的战斗配置下发时被整体验证拒绝，不产生部分修改脏状态。
    /// </summary>
    [Fact]
    public void Invalid_combat_loadout_does_not_partially_mutate_state()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var originalAbility = state.Combat.AbilitySlots[0].AbilityId;

        var result = SimulationEngine.SetCombatLoadout(
            state,
            [new CombatAbilitySlot
            {
                AbilityId = CombatAbilityIds.HeavySlash,
                Level = 1
            }],
            [
                new ConsumableSlot
                {
                    ItemId = ItemIds.ShunqiPill,
                    Quantity = 1,
                    Enabled = true
                },
                new ConsumableSlot
                {
                    ItemId = ItemIds.SpiritHerb,
                    Quantity = 1,
                    Enabled = true
                }
            ]);

        Assert.False(result.Success);
        Assert.Equal(originalAbility, state.Combat.AbilitySlots[0].AbilityId);
    }

    /// <summary>
    /// 验证非法枚举值（如不存在的 CombatStyle）在最外层被立即拦截，不修改角色战斗配置。
    /// </summary>
    [Fact]
    public void Invalid_combat_multiplier_is_rejected_before_any_mutation()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = SimulationEngine.SetCombatLoadout(
            state,
            null,
            null,
            combatDropRate: 9999999);

        Assert.False(result.Success);
        Assert.Equal("INVALID_COMBAT_DROP_BONUS", result.Code);
        Assert.Equal(original.CombatDropRate, state.Combat.CombatDropRate);
    }

    /// <summary>
    /// 验证角色尝试装配未习得的技能时被安全拦截（ABILITY_NOT_LEARNED），槽位不被篡改。
    /// </summary>
    [Fact]
    public void Combat_loadout_rejects_abilities_the_player_has_not_learned()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);

        var result = SimulationEngine.SetCombatLoadout(
            state,
            [new CombatAbilitySlot
            {
                AbilityId = CombatAbilityIds.BlazingBlast,
                Level = 1
            }],
            null);

        Assert.False(result.Success);
        Assert.Equal("ABILITY_NOT_LEARNED", result.Code);
        Assert.Null(state.Combat.AbilitySlots[0].AbilityId);
    }

    /// <summary>
    /// 验证手动使用道具复用统一的目录 Buff 规则与背包扣除逻辑，正确叠加 Buff 层数与乘数。
    /// </summary>
    [Fact]
    public void Manual_item_use_reuses_catalog_buff_and_inventory_rules()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(ItemIds.ZhixuePowder, 2);

        var result = SimulationEngine.UseItem(
            state,
            ItemIds.ZhixuePowder,
            ActionType.Combat,
            2);

        Assert.True(result.Success);
        Assert.Equal(0, state.Inventory.Get(ItemIds.ZhixuePowder));
        Assert.Single(state.Buffs);

        // 持续消耗品（如引气丹）禁止手动使用，必须装配至自动使用槽位
        state.Inventory.Add(ItemIds.QiGatheringPill, 1);
        var pillResult = SimulationEngine.UseItem(state, ItemIds.QiGatheringPill, ActionType.Cultivation, 1);
        Assert.False(pillResult.Success);
        Assert.Equal("ITEM_REQUIRES_SLOT", pillResult.Code);
    }

    /// <summary>
    /// 验证长达 600 秒的战斗大步快进与逐秒推进在胜负场次、经验、伤害及物品产出上严格一致。
    /// </summary>
    [Fact]
    public void Combat_fast_forward_matches_incremental_progression()
    {
        for (var horizon = 1; horizon <= 220; horizon++)
        {
            var probeFast = SimulationEngine.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
            var probeStepped = probeFast.Clone();
            probeFast.Queue.Entries.Clear();
            probeStepped.Queue.Entries.Clear();
            SimulationEngine.Enqueue(probeFast, ActionIds.HuntQiBeast, -1);
            SimulationEngine.Enqueue(probeStepped, ActionIds.HuntQiBeast, -1);
            SimulationEngine.AdvanceTo(probeFast, Baseline.AddSeconds(horizon));
            for (var second = 1; second <= horizon; second++)
            {
                SimulationEngine.AdvanceTo(probeStepped, Baseline.AddSeconds(second));
            }
            var fastExpedition = probeFast.Queue.Entries[0].CombatExpedition;
            var steppedExpedition = probeStepped.Queue.Entries[0].CombatExpedition;
            if (probeFast.Stats.CompletedActions != probeStepped.Stats.CompletedActions
                || fastExpedition?.Phase != steppedExpedition?.Phase
                || fastExpedition?.PhaseElapsedMilliseconds != steppedExpedition?.PhaseElapsedMilliseconds
                || fastExpedition?.BattleSnapshot?.InitialHitpoints != steppedExpedition?.BattleSnapshot?.InitialHitpoints
                || fastExpedition?.BattleSnapshot?.InitialManapoints != steppedExpedition?.BattleSnapshot?.InitialManapoints
                || probeFast.CombatResources.Hitpoints != probeStepped.CombatResources.Hitpoints
                || probeFast.CombatResources.Manapoints != probeStepped.CombatResources.Manapoints)
            {
                Console.WriteLine($"FIRST DIVERGENCE horizon={horizon} fast={probeFast.Stats.CompletedActions}/{fastExpedition?.Phase}/{fastExpedition?.PhaseElapsedMilliseconds}/{(fastExpedition?.BattleSnapshot?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{fastExpedition?.BattleSnapshot?.InitialHitpoints}/{fastExpedition?.BattleSnapshot?.InitialManapoints}/{probeFast.CombatResources.Hitpoints}/{probeFast.CombatResources.Manapoints} stepped={probeStepped.Stats.CompletedActions}/{steppedExpedition?.Phase}/{steppedExpedition?.PhaseElapsedMilliseconds}/{(steppedExpedition?.BattleSnapshot?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{steppedExpedition?.BattleSnapshot?.InitialHitpoints}/{steppedExpedition?.BattleSnapshot?.InitialManapoints}/{probeStepped.CombatResources.Hitpoints}/{probeStepped.CombatResources.Manapoints}");
                break;
            }
        }

        var fast = SimulationEngine.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
        var stepped = fast.Clone();
        fast.Queue.Entries.Clear();
        stepped.Queue.Entries.Clear();
        SimulationEngine.Enqueue(fast, ActionIds.HuntQiBeast, -1);
        SimulationEngine.Enqueue(stepped, ActionIds.HuntQiBeast, -1);

        SimulationEngine.AdvanceTo(fast, Baseline.AddSeconds(600));
        for (var second = 1; second <= 600; second++)
        {
            SimulationEngine.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Console.WriteLine($"FAST wins={fast.Stats.CombatWins} losses={fast.Stats.CombatLosses} completed={fast.Stats.CompletedActions} phase={fast.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={fast.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={fast.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={fast.CombatResources.Hitpoints} mp={fast.CombatResources.Manapoints} progress={fast.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine($"STEP wins={stepped.Stats.CombatWins} losses={stepped.Stats.CombatLosses} completed={stepped.Stats.CompletedActions} phase={stepped.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={stepped.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={stepped.CombatResources.Hitpoints} mp={stepped.CombatResources.Manapoints} progress={stepped.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine("FAST EVENTS " + string.Join(" | ", fast.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));
        Console.WriteLine("STEP EVENTS " + string.Join(" | ", stepped.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(ItemIds.QiBeastCore), fast.Inventory.Get(ItemIds.QiBeastCore));
        Assert.Equal(stepped.Inventory.Get(ItemIds.Spirit), fast.Inventory.Get(ItemIds.Spirit));
        Assert.Equal(stepped.Inventory.Get(ItemIds.SpiritHerb), fast.Inventory.Get(ItemIds.SpiritHerb));
        Assert.Equal(stepped.Inventory.Get(ItemIds.BeastHide), fast.Inventory.Get(ItemIds.BeastHide));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证装备强化倍率曲线（0级 0%、10级 14.5倍、20级 50倍）以及装备属性的综合聚合算法。
    /// </summary>
    [Fact]
    public void Equipment_catalog_uses_reference_enhancement_curve()
    {
        Assert.Equal(0, CombatCatalog.GetEnhancementTotalBonusMultiplier(0));
        Assert.Equal(14.5, CombatCatalog.GetEnhancementTotalBonusMultiplier(10));
        Assert.Equal(50, CombatCatalog.GetEnhancementTotalBonusMultiplier(20));

        var stats = CombatCatalog.AggregateEquipmentStats(
        [new EquippedItem
        {
            ItemId = ItemIds.QingfengSword,
            EnhancementLevel = 10
        }]);

        Assert.Equal(0.10 + 14.5 * 0.002, stats.AccuracyRatio, precision: 10);
        Assert.Equal(0.20 + 14.5 * 0.004, stats.DamageRatio, precision: 10);
        Assert.Equal(CombatStyle.Sword, stats.Style);
        Assert.Equal(DamageType.Physical, stats.DamageType);
    }

    /// <summary>
    /// 验证当暴击或护盾吸收参数为 0 时，相关公式保持无额外影响（零值无害性）。
    /// </summary>
    [Fact]
    public void Critical_and_absorption_formulas_keep_zero_values_effectless()
    {
        Assert.Equal(0, CombatFormula.CalculateCritContestRate(0, 0));
        Assert.Equal(100, CombatFormula.ApplyDamageAbsorption(100, 0));
    }

    /// <summary>
    /// 验证免暴率与伤害吸收护盾比例能够正确削减实际受到的伤害。
    /// </summary>
    [Fact]
    public void Critical_avoidance_and_damage_absorption_apply_numeric_values()
    {
        var critRate = CombatFormula.CalculateCritContestRate(100, 50);
        Assert.True(critRate > 0);
        var baseCritDmg = 100 * (CombatConstants.BaseCriticalDamageMultiplier + 0.5);
        Assert.Equal(200, baseCritDmg);
        Assert.Equal(75, CombatFormula.ApplyDamageAbsorption(100, 0.25));
    }

    /// <summary>
    /// 验证非法装备槽位冲突（例如同时装备两把双手武器）时被原子性拒绝，原装配不被破坏。
    /// </summary>
    [Fact]
    public void Invalid_equipment_layout_is_rejected_atomically()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = SimulationEngine.SetCombatLoadout(
            state,
            null,
            null,
            equipment:
            [
                new EquippedItem { ItemId = ItemIds.QingfengSword },
                new EquippedItem { ItemId = ItemIds.PogangSword }
            ]);

        Assert.False(result.Success);
        Assert.Equal("INVALID_EQUIPMENT", result.Code);
        Assert.Empty(state.Combat.Equipment);
    }

    /// <summary>
    /// 验证法宝专属词条（如蚀心等）作为战斗状态事件正常触发并影响伤害输出。
    /// </summary>
    [Fact]
    public void Equipment_affixes_are_applied_as_combat_status_events()
    {
        var state = SimulationEngine.CreateDefaultState(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.StaminaLevel = 100;
        state.Combat.Equipment =
        [new EquippedItem
        {
            ItemId = ItemIds.PishanBlade,
            EnhancementLevel = 5
        }];

        var result = CombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.DamageDealt > 0);
    }

    /// <summary>
    /// 验证佩戴强化装备时，大步长战斗快进与逐秒推进轨迹严格一致。
    /// </summary>
    [Fact]
    public void Equipment_combat_fast_forward_matches_stepped_progression()
    {
        var fast = SimulationEngine.CreateDefaultState(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.AttackLevel = 60;
            state.Combat.TalismanLevel = 60;
            state.Combat.DefenseLevel = 60;
            state.Combat.Equipment =
            [new EquippedItem
            {
                ItemId = ItemIds.ChaoyuanStaff,
                EnhancementLevel = 10
            }];
            SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, -1);
        }

        SimulationEngine.AdvanceTo(fast, Baseline.AddMinutes(3));
        for (var second = 1; second <= 180; second++)
        {
            SimulationEngine.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(ItemIds.QiBeastCore), fast.Inventory.Get(ItemIds.QiBeastCore));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证多怪物区域（如暮影谷）根据各怪物的强度权重和区域总战力上限随机生成怪物组，且同种子生成结果完全一致。
    /// </summary>
    [Fact]
    public void Multi_enemy_zone_uses_weighted_strength_limited_spawn_rules()
    {
        var playerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var state = SimulationEngine.CreateDefaultState(playerId, now: Baseline);

        var first = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);
        var second = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);

        var encounter = CombatCatalog.GetEncounter(CombatEncounterIds.TwilightValley);
        var strengthByEnemy = encounter.RandomSpawns.ToDictionary(spawn => spawn.Enemy.Id, spawn => spawn.Strength);
        Assert.NotNull(first.FinalEnemies);
        Assert.NotNull(second.FinalEnemies);
        var enemies = first.FinalEnemies!;
        Assert.InRange(enemies.Count, 2, encounter.MaxSpawnCount);
        Assert.True(enemies.Sum(enemy => strengthByEnemy[enemy.EnemyId]) <= encounter.MaxTotalStrength);
        Assert.Equal(
            enemies.Select(enemy => enemy.EnemyId),
            second.FinalEnemies!.Select(enemy => enemy.EnemyId));
        Assert.Equal(first.PlayerAction, second.PlayerAction);
        Assert.Equal(first.EnemyAction, second.EnemyAction);
        Assert.NotNull(first.PlayerAction);
        Assert.NotNull(first.EnemyAction);
    }

    /// <summary>
    /// 验证多怪物区域逢第十场（completedEncountersBeforeRun = 9）必定生成守关 Boss（坠星兽）。
    /// </summary>
    [Fact]
    public void Multi_enemy_zone_spawns_boss_on_every_twentieth_encounter_and_elite_on_fifth()
    {
        var playerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var state = SimulationEngine.CreateDefaultState(playerId, now: Baseline);

        // 验证第 5 场（精英波次）
        var eliteResult = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero,
            completedEncountersBeforeRun: 4);
        Assert.NotNull(eliteResult.FinalEnemies);
        Assert.Contains(eliteResult.FinalEnemies!, e => e.EnemyId is "enemy.blade-corpse" or "enemy.fallen-devil-priest");

        // 验证第 20 场（Boss 波次）
        var bossResult = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero,
            completedEncountersBeforeRun: 19);

        Assert.NotNull(bossResult.FinalEnemies);
        var enemy = Assert.Single(bossResult.FinalEnemies!);
        Assert.Equal("enemy.falling-star-beast", enemy.EnemyId);
    }

    /// <summary>
    /// 验证全体范围伤害技能（AllEnemies）在施法完成时对所有存活敌人同时结算伤害与击杀。
    /// </summary>
    [Fact]
    public void All_enemies_ability_resolves_against_the_whole_alive_group()
    {
        var playerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var state = SimulationEngine.CreateDefaultState(playerId, now: Baseline);
        state.Combat.StaminaLevel = 1_000_000;
        state.Combat.IntelligenceLevel = 1_000_000;
        state.Combat.AttackLevel = 1_000_000;
        state.Combat.DefenseLevel = 1_000_000;
        state.Combat.SwordsmanshipLevel = 1_000_000;
        state.Combat.TalismanLevel = 1_000_000;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 1;

        var result = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromMilliseconds(3_100));

        Assert.NotNull(result.FinalEnemies);
        Assert.True(result.FinalEnemies!.Count >= 2);
        Assert.All(result.FinalEnemies, enemy => Assert.True(enemy.IsDefeated));
        Assert.Equal(result.FinalEnemies.Count, result.EnemiesDefeated);
        Assert.Equal(1, result.Wins);
    }

    /// <summary>
    /// 验证清空整波怪物后，模拟器平滑过渡到重生等待阶段，不会在同一时间戳发生空转死循环。
    /// </summary>
    [Fact]
    public void Cleared_multi_enemy_zone_advances_to_respawn_without_same_timestamp_spin()
    {
        var playerId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var state = SimulationEngine.CreateDefaultState(playerId, now: Baseline);
        state.Stats.Spirit = 100_000;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 100;

        var result = CombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            CombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromSeconds(300),
            playerSpirit: state.Stats.Spirit);

        Assert.True(result.Wins > 1);
        Assert.InRange(result.ProcessedEvents, 1, 1_000);
    }

    /// <summary>
    /// 验证在怪物群体重生期间控制效果过期不会意外提前唤醒玩家动作或引发事件爆炸。
    /// </summary>
    [Fact]
    public void Control_expiration_during_group_respawn_does_not_wake_player_action()
    {
        Span<byte> playerBytes = stackalloc byte[16];
        playerBytes.Clear();
        BitConverter.TryWriteBytes(playerBytes, 8L);
        playerBytes[15] = 0x7A;
        var state = SimulationEngine.CreateDefaultState(new Guid(playerBytes), now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = true;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 100;
        state.Combat.Equipment =
        [
            new EquippedItem
            {
                ItemId = ItemIds.ChaoyuanStaff,
                EnhancementLevel = 7
            }
        ];
        state.World.NodeId = WorldNodeIds.TwilightPass;
        SimulationEngine.Enqueue(state, ActionIds.SweepTwilightValley, -1);

        var result = SimulationEngine.AdvanceTo(state, Baseline.AddHours(1));

        // 新的重新搜索节奏会提高遭遇次数，但控制过期不能造成无界唤醒或事件爆炸。
        Assert.InRange(result.CompletedActions, 5, 600);
        Assert.InRange(result.CombatEvents, 1, 10_000);
        Assert.True(state.Stats.CombatWins > 0);
    }

    /// <summary>
    /// 验证多怪物区域在 4 分钟长时段快进推进与逐秒递增推进在遭遇数、胜负、杀敌数与经验上完全等价。
    /// </summary>
    [Fact]
    public void Multi_enemy_combat_fast_forward_matches_incremental_progression()
    {
        var fast = SimulationEngine.CreateDefaultState(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.StaminaLevel = 1_000;
            state.Combat.IntelligenceLevel = 1_000;
            state.Combat.AttackLevel = 1_000;
            state.Combat.DefenseLevel = 1_000;
            state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.BlazingBlast;
            state.Combat.AbilitySlots[0].Level = 100;
            state.World.NodeId = WorldNodeIds.TwilightPass;
            SimulationEngine.Enqueue(state, ActionIds.SweepTwilightValley, -1);
        }

        SimulationEngine.AdvanceTo(fast, Baseline.AddMinutes(4));
        for (var second = 1; second <= 240; second++)
        {
            SimulationEngine.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(
            stepped.CombatEncounterCompletions.GetValueOrDefault(CombatEncounterIds.TwilightValley),
            fast.CombatEncounterCompletions.GetValueOrDefault(CombatEncounterIds.TwilightValley));
        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatEnemiesDefeated, fast.Stats.CombatEnemiesDefeated);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience, precision: 5);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(ItemIds.Spirit), fast.Inventory.Get(ItemIds.Spirit));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证战斗大步快进与在线 100ms 细粒度微步推进在胜负场、承伤、输出及残余血蓝资源上完全一致。
    /// </summary>
    [Fact]
    public void Combat_fast_forward_matches_fine_grained_online_progression()
    {
        var fast = SimulationEngine.CreateDefaultState(
            Guid.Parse("abababab-abab-abab-abab-abababababab"),
            now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.StaminaLevel = 100;
            state.Combat.IntelligenceLevel = 100;
            state.Combat.AttackLevel = 100;
            state.Combat.DefenseLevel = 100;
            state.Combat.AbilitySlots[0].AbilityId = CombatAbilityIds.HeavySlash;
            SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, -1);
        }

        SimulationEngine.AdvanceTo(fast, Baseline.AddSeconds(30));
        for (var elapsedMilliseconds = 100; elapsedMilliseconds <= 30_000; elapsedMilliseconds += 100)
        {
            SimulationEngine.AdvanceTo(stepped, Baseline.AddMilliseconds(elapsedMilliseconds));
        }

        Assert.Equal(fast.Stats.CombatWins, stepped.Stats.CombatWins);
        Assert.Equal(fast.Stats.CombatLosses, stepped.Stats.CombatLosses);
        Assert.Equal(fast.Stats.CombatDamageDealt, stepped.Stats.CombatDamageDealt);
        Assert.Equal(fast.Stats.CombatDamageTaken, stepped.Stats.CombatDamageTaken);
        Assert.Equal(fast.CombatResources.Hitpoints, stepped.CombatResources.Hitpoints);
        Assert.Equal(fast.CombatResources.Manapoints, stepped.CombatResources.Manapoints);
        Assert.Equal(
            fast.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds,
            stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds);
    }

    /// <summary>
    /// 验证战斗经验随角色与怪物等级差呈指数级缩放（越级挑战获得更多经验，碾压低级怪物经验衰减）。
    /// </summary>
    [Fact]
    public void CombatExperience_scales_with_level_difference_exponentially()
    {
        Assert.Equal(1.0, CombatFormula.CalculateCombatExperience(5, 5), precision: 4);
        Assert.Equal(1.10, CombatFormula.CalculateCombatExperience(5, 6), precision: 4);
        Assert.Equal(1.21, CombatFormula.CalculateCombatExperience(5, 7), precision: 4);
        Assert.Equal(0.80, CombatFormula.CalculateCombatExperience(5, 4), precision: 4);
        Assert.Equal(0.64, CombatFormula.CalculateCombatExperience(5, 3), precision: 4);
    }

    /// <summary>
    /// 验证底蕴机制：修为达到层级上限待突破时，溢出修为 100% 积攒为底蕴；突破成功后，底蕴按最高 3 倍加速转化为实际修为。
    /// </summary>
    [Fact]
    public void Foundation_accumulates_when_capped_and_converts_with_triple_multiplier()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var expToNext = CombatConstants.GetRealmExpToNext(1); // 10000.0
        state.Stats.Spirit = (long)(expToNext - 1000); // 9000
        state.Stats.FoundationExp = 0;
        state.IsPendingBreakthrough = false;

        // 1. 获得 1000 点修为达到满值 (10000)
        var res1 = SimulationEngine.AddCultivationSpirit(state, 1000);
        Assert.Equal(1000, res1.actualSpiritGain);
        Assert.Equal(10000, state.Stats.Spirit);
        Assert.True(state.IsPendingBreakthrough);

        // 2. 满值未突破时获得修为，全部转化为底蕴
        var res2 = SimulationEngine.AddCultivationSpirit(state, 1000);
        Assert.Equal(0, res2.actualSpiritGain);
        Assert.Equal(1000, res2.foundationGain);
        Assert.Equal(1000, state.Stats.FoundationExp);
        Assert.Equal(10000, state.Stats.Spirit);

        // 3. 破境突破
        var bt = SimulationEngine.Breakthrough(state);
        Assert.True(bt.Success);
        Assert.False(state.IsPendingBreakthrough);

        // 4. 突破后获得 1000 修为，底蕴有 1000 点：消耗 1000 点底蕴，实际获得 2000 修为 (1000 基础 + 1000 底蕴)
        var res3 = SimulationEngine.AddCultivationSpirit(state, 1000);
        Assert.Equal(2000, res3.actualSpiritGain);
        Assert.Equal(1000, res3.foundationConsumed);
        Assert.Equal(0, state.Stats.FoundationExp);
        Assert.Equal(12000, state.Stats.Spirit);

        // 5. 补充 5000 点底蕴，再获得 1000 修为：满额三倍加速，消耗 2000 点底蕴，实际获得 3000 修为 (1000 基础 + 2000 底蕴)
        state.Stats.FoundationExp = 5000;
        var res4 = SimulationEngine.AddCultivationSpirit(state, 1000);
        Assert.Equal(3000, res4.actualSpiritGain);
        Assert.Equal(2000, res4.foundationConsumed);
        Assert.Equal(3000, state.Stats.FoundationExp);
        Assert.Equal(15000, state.Stats.Spirit);
    }

    /// <summary>
    /// 验证境界经验表计算逻辑：根据累计修为精确换算出当前境界层级、本层累计经验与升级进度百分比。
    /// </summary>
    [Fact]
    public void RealmExpTable_calculates_correct_levels_and_progress()
    {
        // 1. Lv.1 初始
        var p1 = CombatConstants.CalculateRealmProgress(0);
        Assert.Equal(1, p1.Level);
        Assert.Equal(0, p1.CurrentLayerExp);
        Assert.Equal(10000.0, p1.ExpToNext);
        Assert.Equal(0.0, p1.ProgressPercentage);
        Assert.Equal(1.0, p1.TotalLevel);

        // 2. Lv.1 中间
        var p2 = CombatConstants.CalculateRealmProgress(5000);
        Assert.Equal(1, p2.Level);
        Assert.Equal(5000.0, p2.CurrentLayerExp);
        Assert.Equal(50.0, p2.ProgressPercentage);
        Assert.Equal(1.5, p2.TotalLevel);

        // 3. Lv.18 叩仙门起始累计修为: 2,232,000
        var p18 = CombatConstants.CalculateRealmProgress(2232000);
        Assert.Equal(18, p18.Level);
        Assert.Equal(0.0, p18.CurrentLayerExp);
        Assert.Equal(800000.0, p18.ExpToNext);
        Assert.Equal(18.0, p18.TotalLevel);

        // 4. Lv.19 练气一层起始累计修为: 3,032,000
        var p19 = CombatConstants.CalculateRealmProgress(3032000);
        Assert.Equal(19, p19.Level);
        Assert.Equal(0.0, p19.CurrentLayerExp);
        Assert.Equal(1263000.0, p19.ExpToNext);
        Assert.Equal(19.0, p19.TotalLevel);
    }

    /// <summary>
    /// 验证全套战斗公式集成：包含平方穿透减伤（高伤爆发穿透/低伤过滤）、破招 vs 拆招对抗及 ATR/CDR 缩减运行时结算。
    /// </summary>
    [Fact]
    public void CombatSimulator_applies_full_combat_formulas_correctly()
    {
        // 1. 验证平方穿透减伤: 伤害 = Raw^2 / (Raw + Res * K + 1)
        // Raw = 1000, Res = 800, K = 1.0 -> 1000^2 / (1000 + 800 + 1) = 1,000,000 / 1801 = 555.247 -> Ceiling = 556
        var highDmg = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(1000, 800, 0, 1.0, 1.0);
        Assert.Equal(556, highDmg);

        // Raw = 2000 (2.0x), Res = 800 -> 2000^2 / (2000 + 800 + 1) = 4,000,000 / 2801 = 1428.06 -> Ceiling = 1429 (2.57x 穿甲爆发)
        var doubleRawDmg = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(2000, 800, 0, 1.0, 1.0);
        Assert.Equal(1429, doubleRawDmg);

        // Raw = 600 (0.6x), Res = 800 -> 600^2 / (600 + 800 + 1) = 360,000 / 1401 = 257 (0.46x 强力过滤)
        var lowRawDmg = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(600, 800, 0, 1.0, 1.0);
        Assert.Equal(257, lowRawDmg);

        // 2. 验证破招 vs 拆招 对抗判定
        // 破招 100, 拆招 50 -> 概率 100 / (100 + 50 + 100) = 100 / 250 = 0.40
        Assert.True(CombatFormula.CheckBreakSuccess(100, 50, 0.35));
        Assert.False(CombatFormula.CheckBreakSuccess(100, 50, 0.45));

        // 3. 验证战斗模拟器带破招、ATR、CDR 运行时正常结算
        var loadout = new CombatLoadout
        {
            BreakRating = 100,
            ParryRating = 50,
            SkillIntensity = 1.5,
            DynamicResistanceFactorK = 2.0,
            ActionSpeedReductionRatio = 0.3,
            CooldownReductionRatio = 0.5,
            CriticalChance = 0.5,
            CriticalDamageRatio = 1.0,
        };

        var run = CombatSimulator.Run(
            Guid.NewGuid(),
            loadout,
            [],
            new Dictionary<string, long>(),
            CombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(30));

        Assert.NotNull(run);
        Assert.True(run.DamageDealt > 0);
    }

    /// <summary>
    /// 验证怪物数值预算解析器（DataBudgetResolver）根据品质阶位（小怪/精英/Boss）与属性权重确定性生成怪物属性。
    /// </summary>
    [Fact]
    public void Monster_weight_budget_resolution_calculates_attributes_deterministically()
    {
        // 1. 验证小怪 (80% 属性, 300% HP)
        var minionConfig = new CombatantConfig(
            Id: "test.rift-wolf",
            Name: "测试妖狼",
            Tier: 2,
            Rank: UnitRank.Minion,
            Weights: new Dictionary<string, double>
            {
                ["hp"] = 1.0,
                ["ad"] = 1.0,
                ["armor"] = 1.0
            });
        var minion = DataBudgetResolver.ResolveCombatant(minionConfig, CombatCatalog.AllAbilities.ToDictionary(a => a.Id, StringComparer.Ordinal));
        // Tier 2 baseline: HP 435.97, AD 28.19, Armor 24.43
        // HP = floor(435.97 * 1.0 * 0.8 * 3.0) = floor(1046.328) = 1046
        // AD = floor(28.19 * 1.0 * 0.8) = floor(22.552) = 22
        // Armor = floor(24.43 * 1.0 * 0.8) = 19
        Assert.Equal(1046, minion.MaxHitpoints);
        Assert.Equal(22, minion.MaxDamage);
        Assert.Equal(19.0, minion.Armor);

        // 2. 验证精英 (120% 属性, 500% HP)
        var eliteConfig = new CombatantConfig(
            Id: "test.blade-corpse",
            Name: "测试尸煞",
            Tier: 2,
            Rank: UnitRank.Elite,
            Weights: new Dictionary<string, double>
            {
                ["hp"] = 1.0,
                ["ad"] = 1.0
            });
        var elite = DataBudgetResolver.ResolveCombatant(eliteConfig, CombatCatalog.AllAbilities.ToDictionary(a => a.Id, StringComparer.Ordinal));
        // HP = floor(435.97 * 1.0 * 1.2 * 5.0) = floor(2615.82) = 2615
        // AD = floor(28.19 * 1.0 * 1.2) = floor(33.828) = 33
        Assert.Equal(2615, elite.MaxHitpoints);
        Assert.Equal(33, elite.MaxDamage);

        // 3. 验证 Boss (150% 属性, 2000% HP)
        var bossConfig = new CombatantConfig(
            Id: "test.falling-star-beast",
            Name: "测试坠星兽",
            Tier: 2,
            Rank: UnitRank.Boss,
            Weights: new Dictionary<string, double>
            {
                ["hp"] = 1.0,
                ["ad"] = 1.0
            });
        var boss = DataBudgetResolver.ResolveCombatant(bossConfig, CombatCatalog.AllAbilities.ToDictionary(a => a.Id, StringComparer.Ordinal));
        // HP = floor(435.97 * 1.0 * 1.5 * 20.0) = floor(13079.1) = 13079
        // AD = floor(28.19 * 1.0 * 1.5) = floor(42.285) = 42
        Assert.Equal(13079, boss.MaxHitpoints);
        Assert.Equal(42, boss.MaxDamage);
    }

    /// <summary>
    /// 验证怪物技能的生命值触发比例（TriggerHpRatio）与等级技能伤害加成的正确解析与缩放。
    /// </summary>
    [Fact]
    public void Monster_skill_hp_trigger_ratio_is_configured_and_scaled_correctly()
    {
        var bossConfig = new CombatantConfig(
            Id: "test.boss-with-skills",
            Name: "测试Boss",
            Tier: 2,
            Rank: UnitRank.Boss,
            Skills:
            [
                new MonsterSkillConfig("ability.heavy-slash", Level: 1, TriggerHpRatio: 1.0),
                new MonsterSkillConfig("ability.chain-strike", Level: 2, TriggerHpRatio: 0.8),
                new MonsterSkillConfig("ability.star-piercer", Level: 3, TriggerHpRatio: 0.3)
            ]);

        var abilityDict = new Dictionary<string, CombatAbilityDefinition>(StringComparer.Ordinal)
        {
            ["ability.heavy-slash"] = new() { Id = "ability.heavy-slash", Name = "重斩", FlatDamage = 20, TriggerHpRatio = 1.0 },
            ["ability.chain-strike"] = new() { Id = "ability.chain-strike", Name = "连环", FlatDamage = 15, TriggerHpRatio = 1.0 },
            ["ability.star-piercer"] = new() { Id = "ability.star-piercer", Name = "贯星", FlatDamage = 50, FlatDamageLevelBonus = 15, TriggerHpRatio = 1.0 }
        };

        var monster = DataBudgetResolver.ResolveCombatant(bossConfig, abilityDict);
        Assert.Equal(3, monster.Abilities.Count);
        Assert.Equal(1.0, monster.Abilities[0].TriggerHpRatio);
        Assert.Equal(0.8, monster.Abilities[1].TriggerHpRatio);
        Assert.Equal(0.3, monster.Abilities[2].TriggerHpRatio);

        // 验证等级加成 (star-piercer lv3: flatDamage 50 + 2*15 = 80)
        var starPiercer = monster.Abilities[2];
        Assert.Equal(80, starPiercer.FlatDamage);
    }

    /// <summary>
    /// 验证寻敌结束后进入战斗时只创建一次固定 BattleSnapshot，在后续战斗微步推进中始终复用该快照而不重复创建。
    /// </summary>
    [Fact]
    public void Combat_search_creates_battle_snapshot_exactly_once_and_reuses_it()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, 1);

        // 1. 推进 25 秒：跨过寻敌阶段（20s），正式进入 Combat 阶段
        SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(25));
        var expedition = state.Queue.Entries[0].CombatExpedition;
        Assert.NotNull(expedition);
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);

        var snapshot = expedition.BattleSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.BattleSequence);
        Assert.False(string.IsNullOrEmpty(snapshot.ConfigFingerprint));
        Assert.Equal(GameDataCatalog.Current.Fingerprint, snapshot.ConfigFingerprint);
        Assert.Equal(CombatEncounterIds.QiBeast, snapshot.EncounterId);

        // 2. 微步推进 500 毫秒：仍处于该场战斗中，验证 BattleSnapshot 引用未被重新创建
        SimulationEngine.AdvanceTo(state, Baseline.AddMilliseconds(25_500));
        Assert.Same(snapshot, expedition.BattleSnapshot);
        Assert.NotNull(expedition.BattleSnapshot);
        Assert.Equal(1, expedition.BattleSnapshot.BattleSequence);
        Assert.Equal(5_500, expedition.PhaseElapsedMilliseconds);
    }

    /// <summary>
    /// 验证战斗中途克隆与同一时间戳重放不会重复扣减消耗品、不重复增加资产/经验/灵石与统计数据。
    /// </summary>
    [Fact]
    public void Combat_clone_and_replay_does_not_duplicate_assets_or_stats()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, 1);

        // 推进到战斗中途
        SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(26));
        var expedition = state.Queue.Entries[0].CombatExpedition;
        Assert.NotNull(expedition);
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);

        var initialSpirit = state.Inventory.Get(ItemIds.Spirit);
        var initialWins = state.Stats.CombatWins;
        var initialDamageDealt = state.Stats.CombatDamageDealt;
        var initialExp = state.Stats.CombatExperience;

        // 克隆并向同一时间戳重复推进
        var cloned = state.Clone();
        var replayResult = SimulationEngine.AdvanceTo(cloned, cloned.LastSimulatedAt);

        Assert.Equal(0, replayResult.CompletedActions);
        Assert.Equal(0, replayResult.CombatEvents);
        Assert.Equal(initialSpirit, cloned.Inventory.Get(ItemIds.Spirit));
        Assert.Equal(initialWins, cloned.Stats.CombatWins);
        Assert.Equal(initialDamageDealt, cloned.Stats.CombatDamageDealt);
        Assert.Equal(initialExp, cloned.Stats.CombatExperience);

        // 分别推进到相同未来时间戳，状态必须严格一致
        var targetTime = Baseline.AddSeconds(50);
        SimulationEngine.AdvanceTo(state, targetTime);
        SimulationEngine.AdvanceTo(cloned, targetTime);

        Assert.Equal(state.Stats.CombatWins, cloned.Stats.CombatWins);
        Assert.Equal(state.Stats.CombatLosses, cloned.Stats.CombatLosses);
        Assert.Equal(state.Stats.CombatDamageDealt, cloned.Stats.CombatDamageDealt);
        Assert.Equal(state.Stats.CombatExperience, cloned.Stats.CombatExperience);
        Assert.Equal(state.Inventory.Get(ItemIds.Spirit), cloned.Inventory.Get(ItemIds.Spirit));
        Assert.Equal(state.CombatResources.Hitpoints, cloned.CombatResources.Hitpoints);
        Assert.Equal(state.CombatResources.Manapoints, cloned.CombatResources.Manapoints);
    }

    /// <summary>
    /// 验证战斗分段微步推进与单次跨度快进结算结果完全等价。
    /// </summary>
    [Fact]
    public void Combat_segmented_advance_matches_single_shot_advance_deterministically()
    {
        var singleShot = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var stepped = singleShot.Clone();
        singleShot.Queue.Entries.Clear();
        stepped.Queue.Entries.Clear();
        SimulationEngine.Enqueue(singleShot, ActionIds.HuntQiBeast, -1);
        SimulationEngine.Enqueue(stepped, ActionIds.HuntQiBeast, -1);

        // 单次直接推进 150 秒
        SimulationEngine.AdvanceTo(singleShot, Baseline.AddSeconds(150));

        // 分段按 3 秒逐步推进到 150 秒
        for (var sec = 3; sec <= 150; sec += 3)
        {
            SimulationEngine.AdvanceTo(stepped, Baseline.AddSeconds(sec));
        }

        Assert.Equal(singleShot.Stats.CombatWins, stepped.Stats.CombatWins);
        Assert.Equal(singleShot.Stats.CombatLosses, stepped.Stats.CombatLosses);
        Assert.Equal(singleShot.Stats.CompletedActions, stepped.Stats.CompletedActions);
        Assert.Equal(singleShot.Stats.CombatDamageDealt, stepped.Stats.CombatDamageDealt);
        Assert.Equal(singleShot.Stats.CombatDamageDealt, stepped.Stats.CombatDamageDealt);
        Assert.Equal(singleShot.Stats.CombatExperience, stepped.Stats.CombatExperience);
        Assert.Equal(singleShot.Inventory.Get(ItemIds.Spirit), stepped.Inventory.Get(ItemIds.Spirit));
        Assert.Equal(singleShot.Inventory.Get(ItemIds.QiBeastCore), stepped.Inventory.Get(ItemIds.QiBeastCore));
        Assert.Equal(singleShot.CombatResources.Hitpoints, stepped.CombatResources.Hitpoints);
        Assert.Equal(singleShot.CombatResources.Manapoints, stepped.CombatResources.Manapoints);
        Assert.Equal(
            singleShot.Queue.Entries[0].CombatExpedition?.Phase,
            stepped.Queue.Entries[0].CombatExpedition?.Phase);
        Assert.Equal(
            singleShot.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds,
            stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds);
    }

    /// <summary>
    /// 验证战斗入场快照配置指纹不一致时快速失败并拒绝继续推进。
    /// </summary>
    [Fact]
    public void Combat_snapshot_fingerprint_mismatch_fails_fast()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, 1);

        // 进入战斗阶段
        SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(25));
        var expedition = state.Queue.Entries[0].CombatExpedition;
        Assert.NotNull(expedition?.BattleSnapshot);

        // 篡改配置指纹为过期/非法值
        expedition.BattleSnapshot.ConfigFingerprint = "mismatched_fake_hash_999";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(30)));

        Assert.Contains("战斗快照配置指纹不一致", ex.Message);
        Assert.Contains("mismatched_fake_hash_999", ex.Message);
    }

    /// <summary>
    /// 验证历练处于 Combat 阶段但缺少 BattleSnapshot 时快速失败，绝不静默合成或回退。
    /// </summary>
    [Fact]
    public void Combat_missing_snapshot_in_combat_phase_fails_fast()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        SimulationEngine.Enqueue(state, ActionIds.HuntQiBeast, 1);

        // 进入战斗阶段
        SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(25));
        var expedition = state.Queue.Entries[0].CombatExpedition;
        Assert.NotNull(expedition);
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);

        // 强行置空快照（破坏不变量）
        expedition.BattleSnapshot = null;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SimulationEngine.AdvanceTo(state, Baseline.AddSeconds(30)));

        Assert.Contains("历练处于战斗阶段但缺少 BattleSnapshot 战斗入场快照", ex.Message);
    }

    /// <summary>
    /// 验证 CombatExpeditionState 纯数据模型承载 BattleSnapshot 并正确执行深克隆。
    /// </summary>
    [Fact]
    public void Combat_expedition_state_holds_battle_snapshot_and_clones_independently()
    {
        var expedition = new CombatExpeditionState();

        // 1. 初始阶段：快照为空
        Assert.Null(expedition.BattleSnapshot);

        // 2. 挂载快照后：持有指定快照
        var snapshot = new BattleSnapshot
        {
            EncounterId = CombatEncounterIds.QiBeast,
            BattleSequence = 3,
            CompletedEncountersBeforeBattle = 5,
            BattleStartedAt = Baseline,
            InitialHitpoints = 500,
            InitialManapoints = 200,
            ConfigFingerprint = GameDataCatalog.Current.Fingerprint
        };
        expedition.BattleSnapshot = snapshot;

        Assert.NotNull(expedition.BattleSnapshot);
        Assert.Equal(CombatEncounterIds.QiBeast, expedition.BattleSnapshot.EncounterId);
        Assert.Equal(5, expedition.BattleSnapshot.CompletedEncountersBeforeBattle);
        Assert.Equal(Baseline, expedition.BattleSnapshot.BattleStartedAt);
        Assert.Equal(500, expedition.BattleSnapshot.InitialHitpoints);
        Assert.Equal(200, expedition.BattleSnapshot.InitialManapoints);

        // 3. 深克隆验证：克隆对象与源对象快照独立
        var cloned = expedition.Clone();
        Assert.NotNull(cloned.BattleSnapshot);
        Assert.NotSame(expedition.BattleSnapshot, cloned.BattleSnapshot);
        Assert.Equal(expedition.BattleSnapshot.EncounterId, cloned.BattleSnapshot.EncounterId);
    }
}

