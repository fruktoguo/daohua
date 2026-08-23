// ==============================================================================
// 文件名称: DefinitionTests.cs
// 主要职责: 验证强类型 ID（Domain.Common）与只读定义载体（Domain.Definitions）的构造与值相等性契约。
// 核心逻辑 / 定义内容:
//   1. 验证 PlayerId、ItemId、AbilityId、ActionId、EncounterId 强类型值对象的值语义与有效性检查。
//   2. 验证 ActionDefinition、ItemDefinition、CombatAbilityDefinition、CombatEncounterDefinition 静态配置记录的只读构造与值相等性。
// ==============================================================================

namespace Domain.Tests;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Domain.Common;
using Domain.Models;
using ActionDefinition = Domain.Definitions.ActionDefinition;
using ItemDefinition = Domain.Definitions.ItemDefinition;
using CombatAbilityDefinition = Domain.Definitions.CombatAbilityDefinition;
using CombatantDefinition = Domain.Definitions.CombatantDefinition;
using CombatEncounterDefinition = Domain.Definitions.CombatEncounterDefinition;
using ConsumableDefinition = Domain.Definitions.ConsumableDefinition;
using CharacterVisualDefinition = Domain.Definitions.CharacterVisualDefinition;
using CombatTriggerDefinition = Domain.Definitions.CombatTriggerDefinition;

/// <summary>
/// 强类型标识符与只读配置定义载体测试用例。
/// </summary>
public sealed class DefinitionTests
{
    /// <summary>
    /// 验证强类型标识符（PlayerId、ItemId、AbilityId、ActionId、EncounterId）的值相等性、非空性与字符串表示。
    /// </summary>
    [Fact]
    public void Strongly_typed_identifiers_support_value_equality_and_validation()
    {
        var rawGuid = Guid.NewGuid();
        var p1 = new PlayerId(rawGuid);
        var p2 = new PlayerId(rawGuid);
        var p3 = PlayerId.New();

        Assert.Equal(p1, p2);
        Assert.NotEqual(p1, p3);
        Assert.False(p1.IsEmpty);
        Assert.True(PlayerId.Empty.IsEmpty);
        Assert.Equal(rawGuid.ToString(), p1.ToString());

        var itemA1 = new ItemId("item.herb.spirit-herb");
        var itemA2 = new ItemId("item.herb.spirit-herb");
        var itemB = new ItemId("item.pill.qi-gathering");

        Assert.Equal(itemA1, itemA2);
        Assert.NotEqual(itemA1, itemB);
        Assert.True(itemA1.IsValid);
        Assert.False(ItemId.Empty.IsValid);
        Assert.Equal("item.herb.spirit-herb", itemA1.ToString());

        var ability1 = new AbilityId("ability.sword.slash");
        var ability2 = new AbilityId("ability.sword.slash");
        Assert.Equal(ability1, ability2);
        Assert.True(ability1.IsValid);

        var action1 = new ActionId("action.cultivation.draw-qi");
        var action2 = new ActionId("action.cultivation.draw-qi");
        Assert.Equal(action1, action2);
        Assert.True(action1.IsValid);

        var encounter1 = new EncounterId("encounter.qi-beast");
        var encounter2 = new EncounterId("encounter.qi-beast");
        Assert.Equal(encounter1, encounter2);
        Assert.True(encounter1.IsValid);
    }

    /// <summary>
    /// 验证 ActionDefinition 与 ItemDefinition 只读配置载体的声明式构造、不可变集合与 with 表达式。
    /// </summary>
    [Fact]
    public void Action_and_item_definitions_support_immutable_construction_and_record_equality()
    {
        var herbId = new ItemId("item.herb.lingcao");
        var actionId = new ActionId("explore.gather-herbs");

        var actionDef = new ActionDefinition
        {
            Id = actionId,
            Name = "采集灵草",
            Description = "于百草谷寻觅成熟灵草",
            Type = ActionType.Exploration,
            ExecutionKind = ActionExecutionKind.Timed,
            DurationSeconds = 10.0,
            Outputs = new Dictionary<ItemId, double> { [herbId] = 5.0 }.ToFrozenDictionary()
        };

        Assert.Equal(actionId, actionDef.Id);
        Assert.Equal(10.0, actionDef.DurationSeconds);
        Assert.Equal(5.0, actionDef.Outputs[herbId]);
        Assert.IsAssignableFrom<FrozenDictionary<ItemId, double>>(actionDef.Outputs);
        Assert.Empty(actionDef.AvailableNodeIds);

        var itemDef = new ItemDefinition
        {
            Id = herbId,
            Name = "灵草",
            Description = "吸收天地灵气生长的基础药材",
            Kind = ItemKind.Material,
            Category = ItemCategory.Herb,
            Tier = 1,
            SellPrice = 2.0,
            IsTradable = true,
            Consumable = new ConsumableDefinition
            {
                UsableInActionTypes = new[] { ActionType.Exploration, ActionType.Cultivation }.ToFrozenSet()
            }
        };

        var modifiedItem = itemDef with { SellPrice = 3.0 };

        Assert.Equal(herbId, itemDef.Id);
        Assert.Equal(2.0, itemDef.SellPrice);
        Assert.Equal(3.0, modifiedItem.SellPrice);
        Assert.NotEqual(itemDef, modifiedItem);
        Assert.NotNull(itemDef.Consumable);
        Assert.Contains(ActionType.Exploration, (IReadOnlySet<ActionType>)itemDef.Consumable.UsableInActionTypes);
        Assert.IsAssignableFrom<FrozenSet<ActionType>>(itemDef.Consumable.UsableInActionTypes);
    }

    /// <summary>
    /// 验证战斗技能与遭遇关卡定义载体的强类型绑定与真正不可变集合。
    /// </summary>
    [Fact]
    public void Combat_ability_and_encounter_definitions_support_strongly_typed_bindings()
    {
        var abilityId = new AbilityId("ability.five-elements.fireball");
        var encounterId = new EncounterId("encounter.qi-beast-den");

        var ability = new CombatAbilityDefinition
        {
            Id = abilityId,
            Name = "烈火弹",
            Style = CombatStyle.FiveElements,
            Category = DamageCategory.Spell,
            Element = Element.Fire,
            CooldownMilliseconds = 4000.0,
            CastMilliseconds = 1000.0,
            ManaCost = 15.0,
            DamageRatio = 1.8,
            FlatDamage = 30.0
        };

        var monster = new CombatantDefinition
        {
            Id = "monster.qi-wolf",
            Name = "灵纹幼狼",
            MaxHitpoints = 200.0,
            MaxManapoints = 50.0,
            Armor = 10.0,
            Abilities = [ability]
        };

        var encounter = new CombatEncounterDefinition
        {
            Id = encounterId,
            Name = "灵兽巢穴",
            Enemy = monster,
            SearchMilliseconds = 15000.0,
            RespawnMilliseconds = 5000.0,
            ExperiencePerWin = 50.0,
            SpiritMinPerWin = 10.0,
            SpiritMaxPerWin = 20.0
        };

        Assert.Equal(abilityId, ability.Id);
        Assert.Equal(1.8, ability.DamageRatio);
        Assert.Equal(encounterId, encounter.Id);
        Assert.Equal("monster.qi-wolf", encounter.Enemy.Id);
        Assert.Single(encounter.Enemy.Abilities);
        Assert.IsType<ImmutableArray<CombatAbilityDefinition>>(encounter.Enemy.Abilities);
    }

    /// <summary>
    /// 验证嵌套配置定义（CombatTriggerDefinition、CharacterVisualDefinition）为只读不可变 sealed record，
    /// 且配置载体类型严格解耦于 Domain.Models 运行时可变类。
    /// </summary>
    [Fact]
    public void Nested_definitions_are_immutable_records_and_decoupled_from_runtime_models()
    {
        // 1. 验证 CombatTriggerDefinition 值语义与不可变 with
        var trigger1 = new CombatTriggerDefinition
        {
            Dependency = CombatTriggerDependency.TargetEnemy,
            Condition = CombatTriggerCondition.CurrentHitpointsPercentage,
            Comparator = CombatTriggerComparator.LessThanOrEqual,
            Value = 0.3
        };
        var trigger2 = trigger1 with { Value = 0.5 };

        Assert.Equal(0.3, trigger1.Value);
        Assert.Equal(0.5, trigger2.Value);
        Assert.NotEqual(trigger1, trigger2);
        Assert.Equal(trigger1, trigger2 with { Value = 0.3 });

        // 2. 验证 CharacterVisualDefinition 值语义与不可变 with
        var visual1 = new CharacterVisualDefinition
        {
            Renderer = VisualRendererKind.Glyph,
            Source = "wolf",
            Poster = "poster.png"
        };
        var visual2 = visual1 with { Source = "tiger" };

        Assert.Equal("wolf", visual1.Source);
        Assert.Equal("tiger", visual2.Source);
        Assert.NotEqual(visual1, visual2);
        Assert.Equal(visual1, visual2 with { Source = "wolf" });

        // 3. 验证 CombatAbilityDefinition 与 ConsumableDefinition 集合与属性强类型绑定于不可变定义，未混入运行时类
        var abilityProperty = typeof(CombatAbilityDefinition).GetProperty(nameof(CombatAbilityDefinition.DefaultTriggers));
        Assert.NotNull(abilityProperty);
        Assert.Equal(typeof(ImmutableArray<CombatTriggerDefinition>), abilityProperty.PropertyType);

        var consumableProperty = typeof(ConsumableDefinition).GetProperty(nameof(ConsumableDefinition.DefaultCombatTriggers));
        Assert.NotNull(consumableProperty);
        Assert.Equal(typeof(ImmutableArray<CombatTriggerDefinition>), consumableProperty.PropertyType);

        var visualProperty = typeof(CombatantDefinition).GetProperty(nameof(CombatantDefinition.Visual));
        Assert.NotNull(visualProperty);
        Assert.Equal(typeof(CharacterVisualDefinition), visualProperty.PropertyType);
        Assert.NotEqual(typeof(Domain.Models.CharacterVisualDefinition), visualProperty.PropertyType);
    }
}
