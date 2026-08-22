// ==============================================================================
// 文件名称: ItemDefinition.cs
// 主要职责: 定义修仙物品、材料、丹药消耗品与装备的静态只读配置原型。
// 核心逻辑 / 定义内容:
//   1. 描述物品基础属性、分类、品阶与交易性。
//   2. 描述消耗品特有使用规则、触发条件与附带 Buff 效果。
//   3. 描述装备槽位与基础/强化属性修饰。
//   4. 采用 sealed record 与只读不可变集合，不混入任何运行时状态。
// ==============================================================================

namespace Domain.Definitions;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Domain.Common;
using Domain.Models;

/// <summary>
/// 物品附带的 Buff 增益/减益效果静态只读配置原型。
/// </summary>
public sealed record BuffEffectDefinition
{
    /// <summary>Buff 唯一原型 ID。</summary>
    public required string BuffId { get; init; }

    /// <summary>Buff 显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>单次施加持续时间（秒）。</summary>
    public double DurationSeconds { get; init; }

    /// <summary>是否允许叠加层数。</summary>
    public bool Stackable { get; init; }

    /// <summary>最大可叠加层数上限。</summary>
    public int MaxStacks { get; init; } = 1;

    /// <summary>灵气吸纳倍率修正。</summary>
    public double QiGainMultiplier { get; init; } = 1.0;

    /// <summary>攻击等级加成比例。</summary>
    public double AttackLevelRatio { get; init; }

    /// <summary>伤害加成比例。</summary>
    public double DamageRatio { get; init; }

    /// <summary>攻击速度加成比例。</summary>
    public double AttackSpeedRatio { get; init; }

    /// <summary>探索采集产出倍率修正。</summary>
    public double GatheringOutputMultiplier { get; init; } = 1.0;

    /// <summary>炼丹产出倍率修正。</summary>
    public double AlchemyOutputMultiplier { get; init; } = 1.0;
}

/// <summary>
/// 消耗品特有属性与使用规则静态只读定义。
/// </summary>
public sealed record ConsumableDefinition
{
    /// <summary>允许在哪些动作类型执行期间使用（修炼/探索/炼丹/战斗）。</summary>
    public required FrozenSet<ActionType> UsableInActionTypes { get; init; }

    /// <summary>常规动作中的使用冷却时间（秒）。</summary>
    public double CooldownSeconds { get; init; }

    /// <summary>使用后额外调息/恢复时间（秒）。</summary>
    public double RecoverySeconds { get; init; }

    /// <summary>战斗中自动使用的默认血量百分比阈值（0.0 ~ 1.0）。</summary>
    public double TriggerHpRatio { get; init; } = 0.6;

    /// <summary>是否在战斗开始瞬间立即自动触发使用。</summary>
    public bool TriggerAtCombatStart { get; init; }

    /// <summary>直接回复生命值点数。</summary>
    public double HitpointRestore { get; init; }

    /// <summary>直接回复法力/灵力值点数。</summary>
    public double ManapointRestore { get; init; }

    /// <summary>使用后施加的 Buff 效果列表。</summary>
    public ImmutableArray<BuffEffectDefinition> Buffs { get; init; } = [];

    /// <summary>默认附带的战斗触发条件规则。</summary>
    public ImmutableArray<CombatTriggerDefinition> DefaultCombatTriggers { get; init; } = [];
}

/// <summary>
/// 装备基础与强化属性静态只读定义。
/// </summary>
public sealed record EquipmentDefinition
{
    /// <summary>装备唯一强类型标识 ID。</summary>
    public required ItemId Id { get; init; }

    /// <summary>装备名称。</summary>
    public required string Name { get; init; }

    /// <summary>装备穿戴槽位。</summary>
    public required EquipmentSlot Slot { get; init; }

    /// <summary>装备品阶层次（1 ~ N 阶）。</summary>
    public int Tier { get; init; } = 1;

    /// <summary>装备基础属性修饰。</summary>
    public EquipmentCombatStats Stats { get; init; } = new();

    /// <summary>每级强化的属性增量加成。</summary>
    public EquipmentCombatStats EnhancementBonus { get; init; } = new();
}

/// <summary>
/// 游戏中所有道具物品的通用静态只读原型定义。
/// </summary>
public sealed record ItemDefinition
{
    /// <summary>物品唯一强类型标识 ID。</summary>
    public required ItemId Id { get; init; }

    /// <summary>物品显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>物品详细描述与背景故事。</summary>
    public required string Description { get; init; }

    /// <summary>物品所属大类形态。</summary>
    public required ItemKind Kind { get; init; }

    /// <summary>物品细分业务分类。</summary>
    public ItemCategory Category { get; init; } = ItemCategory.General;

    /// <summary>物品品阶层次（1 ~ N 阶）。</summary>
    public int Tier { get; init; } = 1;

    /// <summary>系统商店基础售出单价（灵石）。</summary>
    public double SellPrice { get; init; }

    /// <summary>是否允许在市场中挂单交易。</summary>
    public bool IsTradable { get; init; } = true;

    /// <summary>若为消耗品，其专属消耗属性（否则为 null）。</summary>
    public ConsumableDefinition? Consumable { get; init; }

    /// <summary>若为装备，其专属装备属性（否则为 null）。</summary>
    public EquipmentDefinition? Equipment { get; init; }
}
