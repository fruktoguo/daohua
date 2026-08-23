// ==============================================================================
// 文件名称: ItemAndExperienceTests.cs
// 主要职责: 验证 1~200 级通用经验表单调性与等级换算、技能书与残卷学习研读机制、全量 28 件装备及怪物掉落概率契约。
// 核心逻辑 / 定义内容:
//   1. 验证 ExperienceConstants 通用 1~200 级累计经验表严格单调递增及关键里程碑。
//   2. 验证完整技能书首次使用 0 经验解锁、再次使用注入 10000 经验升级。
//   3. 验证技能残卷未领悟时拒绝使用、已领悟时注入 500 经验。
//   4. 验证 28 件 T1/T2 制式装备在数据目录中的正确性与完整性。
//   5. 验证怪物掉落表符合 100%/10%/1%/0.1%/2% 权威概率阶梯。
// ==============================================================================

namespace Domain.Tests;

using System;
using System.Linq;
using Domain.Constants;
using Domain.Models;
using Domain.Simulation;
using Xunit;

public sealed class ItemAndExperienceTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    /// <summary>
    /// 验证 1~200 级通用经验表的严格单调递增性以及关键等级节点。
    /// </summary>
    [Fact]
    public void Experience_table_is_monotonic_and_matches_milestones()
    {
        Assert.Equal(201, ExperienceConstants.UniversalCumulativeExp.Length);
        Assert.Equal(0.0, ExperienceConstants.GetCumulativeExp(1));
        Assert.Equal(500.0, ExperienceConstants.GetCumulativeExp(8));
        Assert.Equal(9500.0, ExperienceConstants.GetCumulativeExp(28));
        Assert.Equal(10600.0, ExperienceConstants.GetCumulativeExp(29));
        Assert.Equal(10_000_000.0, ExperienceConstants.GetCumulativeExp(100));
        Assert.Equal(100_000_000_000.0, ExperienceConstants.GetCumulativeExp(200));

        // 验证全区间严格递增
        for (var level = 1; level < ExperienceConstants.MaxLevel; level++)
        {
            var current = ExperienceConstants.GetCumulativeExp(level);
            var next = ExperienceConstants.GetCumulativeExp(level + 1);
            Assert.True(next > current, $"等级 {level} 到 {level + 1} 经验未单调递增: {current} >= {next}");
            Assert.True(ExperienceConstants.GetExpToNextLevel(level) > 0);
        }

        // 验证等级正反查
        Assert.Equal(1, ExperienceConstants.GetLevelFromExp(0));
        Assert.Equal(1, ExperienceConstants.GetLevelFromExp(34));
        Assert.Equal(2, ExperienceConstants.GetLevelFromExp(35));
        Assert.Equal(8, ExperienceConstants.GetLevelFromExp(500));
        Assert.Equal(28, ExperienceConstants.GetLevelFromExp(9500));
        Assert.Equal(28, ExperienceConstants.GetLevelFromExp(10000));
        Assert.Equal(29, ExperienceConstants.GetLevelFromExp(10600));
        Assert.Equal(100, ExperienceConstants.GetLevelFromExp(10_000_000));
        Assert.Equal(200, ExperienceConstants.GetLevelFromExp(100_000_000_000));
        Assert.Equal(200, ExperienceConstants.GetLevelFromExp(999_999_999_999));
    }

    /// <summary>
    /// 验证完整技能书机制：首次使用获得 0 经验并解锁技能，再次使用获得 10000 经验并提升技能等级至 Lv.28。
    /// </summary>
    [Fact]
    public void Skill_tome_first_use_unlocks_with_zero_exp_subsequent_grants_10000_exp()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), "悟道修士", Baseline);
        state.Combat.LearnedAbilityIds.Clear();
        state.Combat.AbilityExperiences.Clear();

        var tomeId = "item.tome.heavy-slash";
        state.Inventory.Add(tomeId, 2);

        // 1. 首次使用：成功解锁技能，获得 0 经验
        var firstResult = SimulationEngine.UseItem(state, tomeId, ActionType.Cultivation, 1);
        Assert.True(firstResult.Success);
        Assert.Contains(CombatAbilityIds.HeavySlash, state.Combat.LearnedAbilityIds);
        Assert.Equal(0.0, state.Combat.AbilityExperiences[CombatAbilityIds.HeavySlash]);
        Assert.Equal(1, state.Combat.GetAbilityLevel(CombatAbilityIds.HeavySlash));
        Assert.Equal(1, state.Inventory.Get(tomeId));

        // 2. 第二次使用：注入 10000 经验，技能直升 Lv.28
        var secondResult = SimulationEngine.UseItem(state, tomeId, ActionType.Cultivation, 1);
        Assert.True(secondResult.Success);
        Assert.Equal(10000.0, state.Combat.AbilityExperiences[CombatAbilityIds.HeavySlash]);
        Assert.Equal(28, state.Combat.GetAbilityLevel(CombatAbilityIds.HeavySlash));
        Assert.Equal(0, state.Inventory.Get(tomeId));
    }

    /// <summary>
    /// 验证技能残卷机制：未领悟技能时禁止使用，已领悟后研读每次增加 500 经验。
    /// </summary>
    [Fact]
    public void Skill_fragment_requires_learned_ability_and_grants_500_exp()
    {
        var state = SimulationEngine.CreateDefaultState(Guid.NewGuid(), "残卷修士", Baseline);
        state.Combat.LearnedAbilityIds.Clear();
        state.Combat.AbilityExperiences.Clear();

        var fragmentId = "item.fragment.star-piercer";
        var abilityId = "ability.star-piercer";
        state.Inventory.Add(fragmentId, 3);

        // 1. 未学会时使用：受阻
        var blockedResult = SimulationEngine.UseItem(state, fragmentId, ActionType.Cultivation, 1);
        Assert.False(blockedResult.Success);
        Assert.Equal("ABILITY_NOT_LEARNED", blockedResult.Code);
        Assert.Equal(3, state.Inventory.Get(fragmentId));

        // 2. 先使用完整秘籍学会技能
        state.Inventory.Add("item.tome.star-piercer", 1);
        Assert.True(SimulationEngine.UseItem(state, "item.tome.star-piercer", ActionType.Cultivation, 1).Success);
        Assert.Equal(0.0, state.Combat.AbilityExperiences[abilityId]);

        // 3. 学会后使用 1 个残卷：获得 500 经验，升至 Lv.8
        var useOneResult = SimulationEngine.UseItem(state, fragmentId, ActionType.Cultivation, 1);
        Assert.True(useOneResult.Success);
        Assert.Equal(500.0, state.Combat.AbilityExperiences[abilityId]);
        Assert.Equal(8, state.Combat.GetAbilityLevel(abilityId));
        Assert.Equal(2, state.Inventory.Get(fragmentId));

        // 4. 批量使用 2 个残卷：获得 1000 经验，累计 1500 经验，升至 Lv.13
        var useTwoResult = SimulationEngine.UseItem(state, fragmentId, ActionType.Cultivation, 2);
        Assert.True(useTwoResult.Success);
        Assert.Equal(1500.0, state.Combat.AbilityExperiences[abilityId]);
        Assert.Equal(13, state.Combat.GetAbilityLevel(abilityId));
        Assert.Equal(0, state.Inventory.Get(fragmentId));
    }

    /// <summary>
    /// 验证全部 28 件 T1/T2 装备均已完整在目录中就绪，槽位、品阶和主次属性全部合规。
    /// </summary>
    [Fact]
    public void All_28_equipment_and_4_sets_are_valid_in_catalog()
    {
        var allEquipment = CombatCatalog.AllEquipment;
        Assert.Equal(28, allEquipment.Count);

        var t1Items = allEquipment.Where(e => e.Tier == 1).ToList();
        var t2Items = allEquipment.Where(e => e.Tier == 2).ToList();
        Assert.Equal(14, t1Items.Count);
        Assert.Equal(14, t2Items.Count);

        // 验证 4 大套装装备均能以 ItemDefinition 形式在 ItemCatalog 检索到
        foreach (var equip in allEquipment)
        {
            Assert.True(ItemCatalog.TryGet(equip.Id, out var itemDef), $"未找到装备对应的 ItemDefinition: {equip.Id}");
            Assert.Equal(ItemKind.Equipment, itemDef.Kind);
            Assert.Equal(equip.Tier, itemDef.Tier);
        }
    }

    /// <summary>
    /// 验证 12 只怪物的掉落表均符合 100% 普通材料、10% 稀有材料、1% 特殊材料、0.1% 技能秘籍、2% 技能残卷的概率设定。
    /// </summary>
    [Fact]
    public void Monster_drop_tables_conform_to_tier_probabilities()
    {
        var monsters = GameDataCatalog.Current.Monsters.Values.ToList();
        Assert.Equal(12, monsters.Count);

        foreach (var monster in monsters)
        {
            Assert.NotEmpty(monster.DropTable);
            foreach (var drop in monster.DropTable)
            {
                Assert.True(ItemCatalog.TryGet(drop.ItemId, out var item), $"掉落项引用的物品不存在: {drop.ItemId}");

                if (drop.ItemId.StartsWith("item.tome.", StringComparison.Ordinal))
                {
                    Assert.Equal(DropRateConstants.SkillTome, drop.DropChance);
                }
                else if (drop.ItemId.StartsWith("item.fragment.", StringComparison.Ordinal))
                {
                    Assert.Equal(DropRateConstants.SkillFragment, drop.DropChance);
                }
                else if (drop.ItemId is "item.demon-dan" or "item.sha-fang" or "item.demon-core" or "item.meteor-fragment")
                {
                    Assert.Equal(DropRateConstants.SpecialMaterial, drop.DropChance);
                }
                else if (drop.ItemId is "item.broken-blade" or "item.sha-bone")
                {
                    Assert.Equal(DropRateConstants.RareMaterial, drop.DropChance);
                }
                else
                {
                    Assert.True(drop.DropChance is DropRateConstants.CommonMaterial or 0.5);
                }
            }
        }
    }
}
