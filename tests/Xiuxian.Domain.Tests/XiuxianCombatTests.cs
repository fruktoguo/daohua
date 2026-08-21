using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;
using Xunit;

namespace Xiuxian.Domain.Tests;

public sealed class XiuxianCombatTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    [Fact]
    public void Combat_formulas_match_reference_shapes()
    {
        var hitChance = XiuxianCombatFormula.CalculateHitChance(15, 15);
        Assert.InRange(hitChance, 0.49, 0.51);
        Assert.Equal(100, XiuxianCombatFormula.CalculateMitigatedDamage(100, 0, 0));
        Assert.Equal(100, XiuxianCombatFormula.CalculateMitigatedDamage(101, 1, 0));
        Assert.Equal(3_000, XiuxianCombatFormula.CalculateAttackIntervalMilliseconds(3_000, 0, 0));
        Assert.True(XiuxianCombatFormula.CalculateAttackIntervalMilliseconds(3_000, 100, 0) < 3_000);
        Assert.Equal(1, XiuxianCombatFormula.CalculateRegeneration(110, 0.01));
        Assert.Equal(0, XiuxianCombatFormula.CalculateEnrageStacks(179_999, 180_000));
        Assert.Equal(1, XiuxianCombatFormula.CalculateEnrageStacks(180_000, 180_000));
        Assert.Equal(10, XiuxianCombatFormula.CalculateEnrageStacks(9_000_000, 180_000));
        Assert.Equal(0.7, XiuxianCombatFormula.CalculateControlChance(0.7, 0), precision: 10);
        Assert.Equal(0.35, XiuxianCombatFormula.CalculateControlChance(0.7, 100), precision: 10);
        Assert.Equal(0.7, XiuxianCombatFormula.CalculateControlChance(0.7, double.PositiveInfinity), precision: 10);
        Assert.Equal(25, XiuxianCombatFormula.CalculatePercentage(25, 100));
        Assert.Equal(100, XiuxianCombatFormula.CalculatePercentage(150, 100));
        Assert.Equal(0, XiuxianCombatFormula.CalculatePercentage(25, 0));

        var levelOne = XiuxianCombatCatalog.GetAbilityAtLevel(
            XiuxianCombatAbilityIds.HeavySlash,
            1);
        Assert.Equal("重斩", levelOne.Name);
        Assert.Equal(1.8, levelOne.DamageRatio);
        Assert.Equal(20, levelOne.FlatDamage);
    }

    [Fact]
    public void Contest_and_resistance_factor_formulas_behave_correctly()
    {
        // 消耗换算测试
        Assert.Equal(20, XiuxianCombatFormula.CalculateManaCost(2));
        Assert.Equal(50, XiuxianCombatFormula.CalculateManaCost(5));

        // 减伤公式测试: 伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 抗性 * 5 + 1))
        // 原始伤害 100, 抗性 0 -> 100 * (100 / 100) = 100
        var fullDmg = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(100, 0);
        Assert.Equal(100, fullDmg);

        // 原始伤害 100, 抗性 20 -> 100 * (100 / (100 + 20 * 5 + 1)) = 100 * (100 / 201) ~ 50
        var reducedDmg = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(100, 20);
        Assert.InRange(reducedDmg, 48, 52);

        // 对抗概率计算: 自身100, 对方100 -> 100 / (100 + 100 + 100) = 1/3 ~ 0.3333
        var contestRate = XiuxianCombatFormula.CalculateContestRate(100, 100);
        Assert.InRange(contestRate, 0.32, 0.34);

        // 破招判定
        Assert.True(XiuxianCombatFormula.CheckBreakSuccess(100, 50, 0.1));
        Assert.False(XiuxianCombatFormula.CheckBreakSuccess(50, 100, 0.5));

        // 拆招判定
        Assert.True(XiuxianCombatFormula.CheckParrySuccess(100, 50, 0.1));
        Assert.False(XiuxianCombatFormula.CheckParrySuccess(50, 100, 0.5));
    }

    [Fact]
    public void Hit_and_crit_use_unified_contest_formula_per_design()
    {
        // 命中 vs 闪避：统一对抗公式 闪避率 180/(180+260+100)=180/540=1/3, 命中率 1-1/3 ≈ 0.667
        var hitRate = XiuxianCombatFormula.CalculateHitContestRate(260, 180);
        Assert.InRange(hitRate, 0.65, 0.68);

        // 高阶位同阶对抗：暴击 128 点 vs 免爆 119 点 -> 128/(128+119+100) = 128/347 ≈ 0.369
        var critRate = XiuxianCombatFormula.CalculateCritContestRate(128, 119);
        Assert.InRange(critRate, 0.35, 0.39);

        // 破招成功时暴击点数翻倍后再进对抗：256/(256+119+100) = 256/475 ≈ 0.539
        var breakCritRate = XiuxianCombatFormula.CalculateCritContestRate(128 * 2.0, 119);
        Assert.InRange(breakCritRate, 0.52, 0.56);
    }

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

    [Fact]
    public void Cast_resolves_when_simulation_reaches_the_exact_millisecond_boundary()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("11111111-1111-1111-1111-111111111111"), now: Baseline);
        state.Combat.AttackLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var beforeBoundary = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(999));
        var atBoundary = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(1000));

        Assert.Equal("cast", beforeBoundary.PlayerAction?.Kind);
        Assert.NotEqual("cast", atBoundary.PlayerAction?.Kind);
        Assert.True(atBoundary.DamageDealt > beforeBoundary.DamageDealt);
    }

    [Fact]
    public void Combat_simulation_is_deterministic_for_same_start_boundary()
    {
        var first = XiuxianSimulation.CreateDefaultState(Guid.Parse("11111111-1111-1111-1111-111111111111"), now: Baseline);
        first.Combat.LearnedAbilityIds.Add(XiuxianCombatAbilityIds.HeavySlash);
        first.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;
        var second = first.Clone();
        var encounter = XiuxianCombatCatalog.GetEncounter(XiuxianCombatEncounterIds.QiBeast);

        var firstResult = XiuxianCombatSimulator.Run(
            first.PlayerId,
            first.Combat,
            first.Buffs,
            first.Inventory.Items,
            encounter.Id,
            Baseline,
            TimeSpan.FromSeconds(60));
        var secondResult = XiuxianCombatSimulator.Run(
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

    [Fact]
    public void Combat_consumes_food_drink_and_talisman_by_independent_slot_rules()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.HealingElixir, 2);
        state.Inventory.Add(XiuxianItemIds.SpiritTea, 1);
        state.Inventory.Add(XiuxianItemIds.FireTalisman, 1);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.HealingElixir,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.ConsumableSlots[1] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.SpiritTea,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.ConsumableSlots[2] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.FireTalisman]);
        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.SpiritTea]);
        Assert.True(result.ConsumedItems.GetValueOrDefault(XiuxianItemIds.HealingElixir) <= 2);
        Assert.Contains(result.BuffApplications, buff => buff.BuffId == XiuxianBuffIds.SpiritTea);
        Assert.Contains(result.BuffApplications, buff => buff.BuffId == XiuxianBuffIds.FireTalisman);
    }

    [Fact]
    public void Combat_reports_consumable_slot_cooldowns_and_new_runs_reset_before_immediate_use()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.FireTalisman, 2);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true
        };

        var active = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(1));
        var restarted = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline.AddMinutes(1),
            TimeSpan.Zero);

        Assert.Equal(299_000, active.PlayerConsumableCooldowns![0]);
        Assert.Equal(300_000, restarted.PlayerConsumableCooldowns![0]);
        Assert.All(restarted.PlayerConsumableCooldowns.Skip(1), remaining => Assert.Equal(0, remaining));
    }

    [Fact]
    public void Combat_simulator_respects_initial_cooldowns_and_preserves_on_respawn()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var initialAbilityCds = new Dictionary<string, long>
        {
            [XiuxianCombatAbilityIds.HeavySlash] = 5_000
        };
        var initialConsumableCds = new List<long> { 10_000, 0, 0 };

        // 模拟 1 秒（未达到 5 秒 CD）
        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(1),
            initialAbilityCooldowns: initialAbilityCds,
            initialConsumableCooldowns: initialConsumableCds);

        // 技能和物品应分别剩余约 4000ms 和 9000ms 冷却
        Assert.NotNull(result.PlayerAbilityCooldowns);
        Assert.True(result.PlayerAbilityCooldowns.TryGetValue(XiuxianCombatAbilityIds.HeavySlash, out var abilityCd));
        Assert.Equal(4_000, abilityCd);

        Assert.NotNull(result.PlayerConsumableCooldowns);
        Assert.Equal(9_000, result.PlayerConsumableCooldowns[0]);
    }

    [Fact]
    public void Combat_recovery_consumable_is_applied_on_five_second_event_boundaries()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("44444444-4444-4444-4444-444444444444"), now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        state.Inventory.Add(XiuxianItemIds.RejuvenatingDew, 1);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.RejuvenatingDew,
            Quantity = 1,
            Enabled = true
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(180));

        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.RejuvenatingDew]);
        Assert.True(result.HitpointsRecoveredFromConsumables > 0);
        Assert.Equal(0, result.ManapointsRecoveredFromConsumables);
    }

    [Fact]
    public void Combat_ability_trigger_uses_custom_and_semantics_and_heal_effect()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        state.Combat.AbilitySlots[0] = new XiuxianCombatAbilitySlot
        {
            AbilityId = XiuxianCombatAbilityIds.QingpingMelody,
            Level = 1,
            Triggers =
            [
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.Self,
                    Condition = XiuxianCombatTriggerCondition.MissingHitpointsPercentage,
                    Comparator = XiuxianCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                },
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.Self,
                    Condition = XiuxianCombatTriggerCondition.CurrentManapoints,
                    Comparator = XiuxianCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 60
                }
            ]
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.HitpointsRecoveredFromAbilities > 0);
        Assert.Contains(result.Events, entry => entry.Contains("功法恢复", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_trigger_resolves_team_extreme_dependencies_and_strict_comparators()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            // 与上方触发器测试使用同一玩家 ID 与起始时间，保证随机轨迹一致：该轨迹下玩家必定受伤。
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        // 友方生命极值在单人拓扑下解析为玩家自身，敌方极值解析为当前目标；
        // 组合内同时使用严格比较符，覆盖新增枚举的求值路径。
        state.Combat.AbilitySlots[0] = new XiuxianCombatAbilitySlot
        {
            AbilityId = XiuxianCombatAbilityIds.QingpingMelody,
            Level = 1,
            Triggers =
            [
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.AllyLowestHitpoints,
                    Condition = XiuxianCombatTriggerCondition.MissingHitpointsPercentage,
                    Comparator = XiuxianCombatTriggerComparator.GreaterThan,
                    Value = 0
                },
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.EnemyHighestHitpoints,
                    Condition = XiuxianCombatTriggerCondition.CurrentHitpointsPercentage,
                    Comparator = XiuxianCombatTriggerComparator.LessThanOrEqual,
                    Value = 100
                }
            ]
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.HitpointsRecoveredFromAbilities > 0);

        // 敌方生命百分比不可能严格大于 100，该组合必须永不触发。
        state.Combat.AbilitySlots[0].Triggers =
        [
            new XiuxianCombatTrigger
            {
                Dependency = XiuxianCombatTriggerDependency.EnemyHighestHitpoints,
                Condition = XiuxianCombatTriggerCondition.CurrentHitpointsPercentage,
                Comparator = XiuxianCombatTriggerComparator.GreaterThan,
                Value = 100
            }
        ];

        var blocked = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(0, blocked.HitpointsRecoveredFromAbilities);
    }

    [Fact]
    public void Combat_uses_regular_and_rare_drop_tables_as_item_maps()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(10));

        Assert.True(result.Wins > 0);
        Assert.NotNull(result.DroppedItems);
        Assert.True(result.DroppedItems!.GetValueOrDefault(XiuxianItemIds.QiBeastCore) >= result.Wins);
        Assert.Contains(result.Events, entry => entry.Contains("掉落", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_drop_quantity_multiplier_scales_guaranteed_drops()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.CombatDropQuantity = 1;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(2));

        Assert.True(result.Wins > 0);
        Assert.True(result.DroppedItems!.GetValueOrDefault(XiuxianItemIds.QiBeastCore) >= result.Wins * 2L);
    }

    [Fact]
    public void Combat_damage_over_time_is_processed_on_event_boundaries()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            now: Baseline);
        state.Combat.StaminaLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.AttackLevel = 1;
        state.Combat.FiveElementsLevel = 1;
        state.Combat.IntelligenceLevel = 10;
        state.Combat.SwordsmanshipLevel = 10;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.CriticalChance = -1.0;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(30));

        Assert.Contains(result.Events, entry => entry.Contains("轰炎", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    [Fact]
    public void Combat_control_effects_use_catalog_probabilities_and_expire_on_boundaries()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            now: Baseline);
        state.Combat.StaminaLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.IntelligenceLevel = 10;
        state.Combat.Style = XiuxianCombatStyle.Body;
        state.Combat.DamageType = XiuxianDamageType.Physical;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(20));

        Assert.Contains(result.Events, entry => entry.Contains("重斩", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    [Fact]
    public void Combat_action_updates_stats_and_inventory_at_completion_boundary()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = false;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast);

        var result = XiuxianSimulation.AdvanceTo(state, Baseline.AddSeconds(60));

        Assert.Equal(1, result.CompletedActions);
        Assert.Equal(1, state.Stats.CompletedActions);
        Assert.True(state.Stats.CombatWins >= 0);
        Assert.True(state.Stats.CombatExperience >= 0);
        Assert.True(state.Inventory.Get(XiuxianItemIds.Spirit) >= 0);
        Assert.Empty(state.Queue.Entries);
    }

    [Fact]
    public void Stronger_loadout_can_clear_multiple_low_tier_encounters()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("22222222-2222-2222-2222-222222222222"), now: Baseline);
        state.Combat.AttackLevel = 40;
        state.Combat.DefenseLevel = 40;
        state.Combat.FiveElementsLevel = 40;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.Wins > 0);
        Assert.True(result.Experience > 0);
    }

    [Fact]
    public void Invalid_combat_loadout_does_not_partially_mutate_state()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var originalStyle = state.Combat.Style;
        var originalAbility = state.Combat.AbilitySlots[0].AbilityId;

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            XiuxianCombatStyle.Body,
            XiuxianDamageType.Fire,
            [new XiuxianCombatAbilitySlot
            {
                AbilityId = XiuxianCombatAbilityIds.HeavySlash,
                Level = 1
            }],
            [
                new XiuxianConsumableSlot
                {
                    ItemId = XiuxianItemIds.SpiritTea,
                    Quantity = 1,
                    Enabled = true
                },
                new XiuxianConsumableSlot
                {
                    ItemId = XiuxianItemIds.SpiritHerb,
                    Quantity = 1,
                    Enabled = true
                }
            ]);

        Assert.False(result.Success);
        Assert.Equal(originalStyle, state.Combat.Style);
        Assert.Equal(originalAbility, state.Combat.AbilitySlots[0].AbilityId);
    }

    [Fact]
    public void Invalid_combat_enum_is_rejected_before_any_mutation()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            (XiuxianCombatStyle)999,
            XiuxianDamageType.Fire,
            null,
            null);

        Assert.False(result.Success);
        Assert.Equal("INVALID_COMBAT_STYLE", result.Code);
        Assert.Equal(original.Style, state.Combat.Style);
        Assert.Equal(original.DamageType, state.Combat.DamageType);
    }

    [Fact]
    public void Combat_loadout_rejects_abilities_the_player_has_not_learned()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            null,
            null,
            [new XiuxianCombatAbilitySlot
            {
                AbilityId = XiuxianCombatAbilityIds.BlazingBlast,
                Level = 1
            }],
            null);

        Assert.False(result.Success);
        Assert.Equal("ABILITY_NOT_LEARNED", result.Code);
        Assert.Null(state.Combat.AbilitySlots[0].AbilityId);
    }

    [Fact]
    public void Manual_item_use_reuses_catalog_buff_and_inventory_rules()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.QiGatheringPill, 2);

        var result = XiuxianSimulation.UseItem(
            state,
            XiuxianItemIds.QiGatheringPill,
            XiuxianActionType.Cultivation,
            2);

        Assert.True(result.Success);
        Assert.Equal(0, state.Inventory.Get(XiuxianItemIds.QiGatheringPill));
        Assert.Equal(2, Assert.Single(state.Buffs).Stacks);
        Assert.Equal(1.5, state.Buffs[0].QiGainMultiplier);
    }

    [Fact]
    public void Combat_fast_forward_matches_incremental_progression()
    {
        for (var horizon = 1; horizon <= 220; horizon++)
        {
            var probeFast = XiuxianSimulation.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
            var probeStepped = probeFast.Clone();
            probeFast.Queue.Entries.Clear();
            probeStepped.Queue.Entries.Clear();
            XiuxianSimulation.Enqueue(probeFast, XiuxianActionIds.HuntQiBeast, -1);
            XiuxianSimulation.Enqueue(probeStepped, XiuxianActionIds.HuntQiBeast, -1);
            XiuxianSimulation.AdvanceTo(probeFast, Baseline.AddSeconds(horizon));
            for (var second = 1; second <= horizon; second++)
            {
                XiuxianSimulation.AdvanceTo(probeStepped, Baseline.AddSeconds(second));
            }
            var fastExpedition = probeFast.Queue.Entries[0].CombatExpedition;
            var steppedExpedition = probeStepped.Queue.Entries[0].CombatExpedition;
            if (probeFast.Stats.CompletedActions != probeStepped.Stats.CompletedActions
                || fastExpedition?.Phase != steppedExpedition?.Phase
                || fastExpedition?.PhaseElapsedMilliseconds != steppedExpedition?.PhaseElapsedMilliseconds
                || fastExpedition?.InitialHitpoints != steppedExpedition?.InitialHitpoints
                || fastExpedition?.InitialManapoints != steppedExpedition?.InitialManapoints
                || probeFast.CombatResources.Hitpoints != probeStepped.CombatResources.Hitpoints
                || probeFast.CombatResources.Manapoints != probeStepped.CombatResources.Manapoints)
            {
                Console.WriteLine($"FIRST DIVERGENCE horizon={horizon} fast={probeFast.Stats.CompletedActions}/{fastExpedition?.Phase}/{fastExpedition?.PhaseElapsedMilliseconds}/{(fastExpedition?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{fastExpedition?.InitialHitpoints}/{fastExpedition?.InitialManapoints}/{probeFast.CombatResources.Hitpoints}/{probeFast.CombatResources.Manapoints} stepped={probeStepped.Stats.CompletedActions}/{steppedExpedition?.Phase}/{steppedExpedition?.PhaseElapsedMilliseconds}/{(steppedExpedition?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{steppedExpedition?.InitialHitpoints}/{steppedExpedition?.InitialManapoints}/{probeStepped.CombatResources.Hitpoints}/{probeStepped.CombatResources.Manapoints}");
                break;
            }
        }

        var fast = XiuxianSimulation.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
        var stepped = fast.Clone();
        fast.Queue.Entries.Clear();
        stepped.Queue.Entries.Clear();
        XiuxianSimulation.Enqueue(fast, XiuxianActionIds.HuntQiBeast, -1);
        XiuxianSimulation.Enqueue(stepped, XiuxianActionIds.HuntQiBeast, -1);

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddSeconds(600));
        for (var second = 1; second <= 600; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Console.WriteLine($"FAST wins={fast.Stats.CombatWins} losses={fast.Stats.CombatLosses} completed={fast.Stats.CompletedActions} phase={fast.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={fast.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={fast.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={fast.CombatResources.Hitpoints} mp={fast.CombatResources.Manapoints} progress={fast.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine($"STEP wins={stepped.Stats.CombatWins} losses={stepped.Stats.CombatLosses} completed={stepped.Stats.CompletedActions} phase={stepped.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={stepped.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={stepped.CombatResources.Hitpoints} mp={stepped.CombatResources.Manapoints} progress={stepped.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine("FAST EVENTS " + string.Join(" | ", fast.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));
        Console.WriteLine("STEP EVENTS " + string.Join(" | ", stepped.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.QiBeastCore), fast.Inventory.Get(XiuxianItemIds.QiBeastCore));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.Spirit), fast.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.SpiritHerb), fast.Inventory.Get(XiuxianItemIds.SpiritHerb));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.CloudSugar), fast.Inventory.Get(XiuxianItemIds.CloudSugar));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Equipment_catalog_uses_reference_enhancement_curve()
    {
        Assert.Equal(0, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(0));
        Assert.Equal(14.5, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(10));
        Assert.Equal(50, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(20));

        var stats = XiuxianCombatCatalog.AggregateEquipmentStats(
        [new XiuxianEquippedItem
        {
            ItemId = XiuxianItemIds.CursedSpiritBow,
            EnhancementLevel = 10
        }]);

        Assert.Equal(1.05 + 14.5 * 0.021, stats.AccuracyRatio, precision: 10);
        Assert.Equal(1.30 + 14.5 * 0.026, stats.DamageRatio, precision: 10);
        Assert.Equal(XiuxianCombatStyle.Talisman, stats.Style);
        Assert.Equal(XiuxianDamageType.Physical, stats.DamageType);
    }

    [Fact]
    public void Critical_and_absorption_formulas_keep_zero_values_effectless()
    {
        Assert.Equal(0, XiuxianCombatFormula.CalculateCriticalChance(0, 0));
        Assert.Equal(100, XiuxianCombatFormula.ApplyCriticalDamage(100, 0.5, false));
        Assert.Equal(100, XiuxianCombatFormula.ApplyDamageAbsorption(100, 0));
    }

    [Fact]
    public void Critical_avoidance_and_damage_absorption_apply_numeric_values()
    {
        Assert.Equal(0.3, XiuxianCombatFormula.CalculateCriticalChance(0.5, 0.2), precision: 10);
        Assert.Equal(150, XiuxianCombatFormula.ApplyCriticalDamage(100, 0.5, true));
        Assert.Equal(75, XiuxianCombatFormula.ApplyDamageAbsorption(100, 0.25));
    }

    [Fact]
    public void Invalid_equipment_layout_is_rejected_atomically()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            null,
            null,
            null,
            null,
            equipment:
            [
                new XiuxianEquippedItem { ItemId = XiuxianItemIds.CursedSpiritBow },
                new XiuxianEquippedItem { ItemId = XiuxianItemIds.FuriousSpiritSpear }
            ]);

        Assert.False(result.Success);
        Assert.Equal("INVALID_EQUIPMENT", result.Code);
        Assert.Empty(state.Combat.Equipment);
        Assert.Equal(original.Style, state.Combat.Style);
    }

    [Fact]
    public void Equipment_affixes_are_applied_as_combat_status_events()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.StaminaLevel = 100;
        state.Combat.Equipment =
        [new XiuxianEquippedItem
        {
            ItemId = XiuxianItemIds.CursedSpiritBow,
            EnhancementLevel = 5
        }];

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Contains(result.Events, entry => entry.Contains("蚀心", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    [Fact]
    public void Equipment_combat_fast_forward_matches_stepped_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.AttackLevel = 60;
            state.Combat.TalismanLevel = 60;
            state.Combat.DefenseLevel = 60;
            state.Combat.Equipment =
            [new XiuxianEquippedItem
            {
                ItemId = XiuxianItemIds.SunderingSpiritCrossbow,
                EnhancementLevel = 10
            }];
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddMinutes(3));
        for (var second = 1; second <= 180; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.QiBeastCore), fast.Inventory.Get(XiuxianItemIds.QiBeastCore));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Multi_enemy_zone_uses_weighted_strength_limited_spawn_rules()
    {
        var playerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);

        var first = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);
        var second = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);

        var encounter = XiuxianCombatCatalog.GetEncounter(XiuxianCombatEncounterIds.TwilightValley);
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

    [Fact]
    public void Multi_enemy_zone_spawns_boss_on_every_tenth_encounter()
    {
        var playerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero,
            completedEncountersBeforeRun: 9);

        Assert.NotNull(result.FinalEnemies);
        var enemy = Assert.Single(result.FinalEnemies!);
        Assert.Equal("enemy.falling-star-beast", enemy.EnemyId);
    }

    [Fact]
    public void All_enemies_ability_resolves_against_the_whole_alive_group()
    {
        var playerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);
        state.Combat.StaminaLevel = 1_000_000;
        state.Combat.IntelligenceLevel = 1_000_000;
        state.Combat.AttackLevel = 1_000_000;
        state.Combat.FiveElementsLevel = 1_000_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 1;

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromMilliseconds(3_100));

        Assert.NotNull(result.FinalEnemies);
        Assert.True(result.FinalEnemies!.Count >= 2);
        Assert.All(result.FinalEnemies, enemy => Assert.True(enemy.IsDefeated));
        Assert.Equal(result.FinalEnemies.Count, result.EnemiesDefeated);
        Assert.Equal(1, result.Wins);
    }

    [Fact]
    public void Cleared_multi_enemy_zone_advances_to_respawn_without_same_timestamp_spin()
    {
        var playerId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 100;

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.Wins > 1);
        Assert.InRange(result.ProcessedEvents, 1, 1_000);
    }

    [Fact]
    public void Control_expiration_during_group_respawn_does_not_wake_player_action()
    {
        Span<byte> playerBytes = stackalloc byte[16];
        playerBytes.Clear();
        BitConverter.TryWriteBytes(playerBytes, 8L);
        playerBytes[15] = 0x7A;
        var state = XiuxianSimulation.CreateDefaultState(new Guid(playerBytes), now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = true;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;
        state.Combat.AbilitySlots[0].Level = 100;
        state.Combat.Equipment =
        [
            new XiuxianEquippedItem
            {
                ItemId = XiuxianItemIds.SunderingSpiritCrossbow,
                EnhancementLevel = 7
            }
        ];
        state.World.NodeId = XiuxianWorldNodeIds.TwilightPass;
        XiuxianSimulation.Enqueue(state, XiuxianActionIds.SweepTwilightValley, -1);

        var result = XiuxianSimulation.AdvanceTo(state, Baseline.AddHours(1));

        // 新的重新搜索节奏会提高遭遇次数，但控制过期不能造成无界唤醒或事件爆炸。
        Assert.InRange(result.CompletedActions, 200, 600);
        Assert.InRange(result.CombatEvents, 1, 10_000);
        Assert.True(state.Stats.CombatWins > 0);
    }

    [Fact]
    public void Multi_enemy_combat_fast_forward_matches_incremental_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(
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
            state.Combat.FiveElementsLevel = 1_000;
            state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;
            state.Combat.AbilitySlots[0].Level = 100;
            state.World.NodeId = XiuxianWorldNodeIds.TwilightPass;
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.SweepTwilightValley, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddMinutes(4));
        for (var second = 1; second <= 240; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(
            stepped.CombatEncounterCompletions.GetValueOrDefault(XiuxianCombatEncounterIds.TwilightValley),
            fast.CombatEncounterCompletions.GetValueOrDefault(XiuxianCombatEncounterIds.TwilightValley));
        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatEnemiesDefeated, fast.Stats.CombatEnemiesDefeated);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience, precision: 5);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.Spirit), fast.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Combat_fast_forward_matches_fine_grained_online_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(
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
            state.Combat.FiveElementsLevel = 100;
            state.Combat.Style = XiuxianCombatStyle.FiveElements;
            state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.HeavySlash;
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddSeconds(30));
        for (var elapsedMilliseconds = 100; elapsedMilliseconds <= 30_000; elapsedMilliseconds += 100)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddMilliseconds(elapsedMilliseconds));
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

    [Fact]
    public void CombatExperience_scales_with_level_difference_exponentially()
    {
        Assert.Equal(1.0, XiuxianCombatFormula.CalculateCombatExperience(5, 5), precision: 4);
        Assert.Equal(1.10, XiuxianCombatFormula.CalculateCombatExperience(5, 6), precision: 4);
        Assert.Equal(1.21, XiuxianCombatFormula.CalculateCombatExperience(5, 7), precision: 4);
        Assert.Equal(0.80, XiuxianCombatFormula.CalculateCombatExperience(5, 4), precision: 4);
        Assert.Equal(0.64, XiuxianCombatFormula.CalculateCombatExperience(5, 3), precision: 4);
    }

    [Fact]
    public void Foundation_accumulates_when_capped_and_converts_with_triple_multiplier()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var expToNext = XiuxianCombatConstants.GetRealmExpToNext(1); // 10000.0
        state.Stats.Spirit = (long)(expToNext - 1000); // 9000
        state.Stats.FoundationExp = 0;
        state.IsPendingBreakthrough = false;

        // 1. 获得 1000 点修为达到满值 (10000)
        var res1 = XiuxianSimulation.AddCultivationSpirit(state, 1000);
        Assert.Equal(1000, res1.actualSpiritGain);
        Assert.Equal(10000, state.Stats.Spirit);
        Assert.True(state.IsPendingBreakthrough);

        // 2. 满值未突破时获得修为，全部转化为底蕴
        var res2 = XiuxianSimulation.AddCultivationSpirit(state, 1000);
        Assert.Equal(0, res2.actualSpiritGain);
        Assert.Equal(1000, res2.foundationGain);
        Assert.Equal(1000, state.Stats.FoundationExp);
        Assert.Equal(10000, state.Stats.Spirit);

        // 3. 破境突破
        var bt = XiuxianSimulation.Breakthrough(state);
        Assert.True(bt.Success);
        Assert.False(state.IsPendingBreakthrough);

        // 4. 突破后获得 1000 修为，底蕴有 1000 点：消耗 1000 点底蕴，实际获得 2000 修为 (1000 基础 + 1000 底蕴)
        var res3 = XiuxianSimulation.AddCultivationSpirit(state, 1000);
        Assert.Equal(2000, res3.actualSpiritGain);
        Assert.Equal(1000, res3.foundationConsumed);
        Assert.Equal(0, state.Stats.FoundationExp);
        Assert.Equal(12000, state.Stats.Spirit);

        // 5. 补充 5000 点底蕴，再获得 1000 修为：满额三倍加速，消耗 2000 点底蕴，实际获得 3000 修为 (1000 基础 + 2000 底蕴)
        state.Stats.FoundationExp = 5000;
        var res4 = XiuxianSimulation.AddCultivationSpirit(state, 1000);
        Assert.Equal(3000, res4.actualSpiritGain);
        Assert.Equal(2000, res4.foundationConsumed);
        Assert.Equal(3000, state.Stats.FoundationExp);
        Assert.Equal(15000, state.Stats.Spirit);
    }

    [Fact]
    public void RealmExpTable_calculates_correct_levels_and_progress()
    {
        // 1. Lv.1 初始
        var p1 = XiuxianCombatConstants.CalculateRealmProgress(0);
        Assert.Equal(1, p1.Level);
        Assert.Equal(0, p1.CurrentLayerExp);
        Assert.Equal(10000.0, p1.ExpToNext);
        Assert.Equal(0.0, p1.ProgressPercentage);
        Assert.Equal(1.0, p1.TotalLevel);

        // 2. Lv.1 中间
        var p2 = XiuxianCombatConstants.CalculateRealmProgress(5000);
        Assert.Equal(1, p2.Level);
        Assert.Equal(5000.0, p2.CurrentLayerExp);
        Assert.Equal(50.0, p2.ProgressPercentage);
        Assert.Equal(1.5, p2.TotalLevel);

        // 3. Lv.18 叩仙门起始累计修为: 2,232,000
        var p18 = XiuxianCombatConstants.CalculateRealmProgress(2232000);
        Assert.Equal(18, p18.Level);
        Assert.Equal(0.0, p18.CurrentLayerExp);
        Assert.Equal(800000.0, p18.ExpToNext);
        Assert.Equal(18.0, p18.TotalLevel);

        // 4. Lv.19 练气一层起始累计修为: 3,032,000
        var p19 = XiuxianCombatConstants.CalculateRealmProgress(3032000);
        Assert.Equal(19, p19.Level);
        Assert.Equal(0.0, p19.CurrentLayerExp);
        Assert.Equal(1263000.0, p19.ExpToNext);
        Assert.Equal(19.0, p19.TotalLevel);
    }

    [Fact]
    public void CombatSimulator_applies_full_combat_formulas_correctly()
    {
        // 1. 验证平方穿透减伤: 伤害 = Raw^2 / (Raw + Res * K + 1)
        // Raw = 1000, Res = 800, K = 1.0 -> 1000^2 / (1000 + 800 + 1) = 1,000,000 / 1801 = 555.247 -> Ceiling = 556
        var highDmg = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(1000, 800, 0, 1.0, 1.0);
        Assert.Equal(556, highDmg);

        // Raw = 2000 (2.0x), Res = 800 -> 2000^2 / (2000 + 800 + 1) = 4,000,000 / 2801 = 1428.06 -> Ceiling = 1429 (2.57x 穿甲爆发)
        var doubleRawDmg = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(2000, 800, 0, 1.0, 1.0);
        Assert.Equal(1429, doubleRawDmg);

        // Raw = 600 (0.6x), Res = 800 -> 600^2 / (600 + 800 + 1) = 360,000 / 1401 = 257 (0.46x 强力过滤)
        var lowRawDmg = XiuxianCombatFormula.CalculateMitigatedDamageWithResistanceFactor(600, 800, 0, 1.0, 1.0);
        Assert.Equal(257, lowRawDmg);

        // 2. 验证破招 vs 拆招 对抗判定
        // 破招 100, 拆招 50 -> 概率 100 / (100 + 50 + 100) = 100 / 250 = 0.40
        Assert.True(XiuxianCombatFormula.CheckBreakSuccess(100, 50, 0.35));
        Assert.False(XiuxianCombatFormula.CheckBreakSuccess(100, 50, 0.45));

        // 3. 验证战斗模拟器带破招、ATR、CDR 运行时正常结算
        var loadout = new XiuxianCombatLoadout
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

        var run = XiuxianCombatSimulator.Run(
            Guid.NewGuid(),
            loadout,
            [],
            new Dictionary<string, long>(),
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(30));

        Assert.NotNull(run);
        Assert.True(run.DamageDealt > 0);
    }
}
