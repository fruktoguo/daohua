// ==============================================================================
// 文件名称: ItemModels.cs
// 主要职责: 定义道具、材料、消耗品、装备及其附带 Buff/词条等物品系统的核心领域模型与静态原型定义。
// 核心逻辑 / 定义内容:
//   1. 物品类型、Buff 增益效果及消耗品规则定义（ItemKind, BuffEffectDefinition, ConsumableDefinition）
//   2. 装备槽位、属性与强化加成模型定义（EquipmentDefinition, EquippedSlots 等）
//   3. 物品静态原型定义与背包/实例数据结构（ItemDefinition, InventoryItem 等）
// ==============================================================================
namespace Domain.Models;

/// <summary>
/// 物品基础形态与大类分类枚举。
/// </summary>
public enum ItemKind
{
    /// <summary>基础材料（采集物、炼丹药材等）。</summary>
    Material = 0,
    /// <summary>可消耗使用的丹药、灵符或饮品。</summary>
    Consumable = 1,
    /// <summary>流通货币（如灵石）。</summary>
    Currency = 2,
    /// <summary>修为/功法资源（如灵气）。</summary>
    Resource = 3,
    /// <summary>可穿戴强化的武器法宝装备。</summary>
    Equipment = 4
}

/// <summary>
/// 物品附带的 Buff 增益/减益效果配置原型。
/// </summary>
public sealed class BuffEffectDefinition
{
    /// <summary>Buff 唯一原型 ID。</summary>
    public required string BuffId { get; init; }
    /// <summary>Buff 显示名称。</summary>
    public required string Name { get; init; }
    /// <summary>单次施加持续时间（秒）。</summary>
    public int DurationSeconds { get; init; }
    /// <summary>是否允许叠加层数。</summary>
    public bool Stackable { get; init; }
    /// <summary>最大可叠加层数上限。</summary>
    public int MaxStacks { get; init; } = 1;
    /// <summary>灵气吸纳倍率修正。</summary>
    public double QiGainMultiplier { get; init; } = 1;
    /// <summary>攻击等级加成比例。</summary>
    public double AttackLevelRatio { get; init; }
    /// <summary>伤害加成比例。</summary>
    public double DamageRatio { get; init; }
    /// <summary>攻击速度加成比例。</summary>
    public double AttackSpeedRatio { get; init; }
    /// <summary>探索采集产出倍率修正。</summary>
    public double GatheringOutputMultiplier { get; init; } = 1;
    /// <summary>炼丹产出倍率修正。</summary>
    public double AlchemyOutputMultiplier { get; init; } = 1;
}

/// <summary>
/// 消耗品特有属性与使用规则定义。
/// </summary>
public sealed class ConsumableDefinition
{
    /// <summary>允许在哪些动作类型执行期间使用（修炼/探索/炼丹/战斗）。</summary>
    public required IReadOnlySet<ActionType> UsableInActionTypes { get; init; }
    /// <summary>单件物品提供的有效动作持续时间（秒，银河奶牛式按动作耗时扣除）。</summary>
    public double DurationSeconds { get; init; }
    /// <summary>是否为银河奶牛式持续消耗品。</summary>
    public bool IsContinuous { get; init; }
    /// <summary>修炼效果加成倍率（如 1.30 表示增加 30% 修炼收益）。</summary>
    public double CultivationMultiplier { get; init; } = 1.0;
    /// <summary>采集探索产出倍率修正。</summary>
    public double GatheringMultiplier { get; init; } = 1.0;
    /// <summary>炼丹产出倍率修正。</summary>
    public double AlchemyMultiplier { get; init; } = 1.0;
    /// <summary>常规动作中的使用冷却时间（秒）。</summary>
    public double CooldownSeconds { get; init; }
    /// <summary>使用后额外调息/恢复时间（秒）。</summary>
    public int RecoverySeconds { get; init; }
    /// <summary>战斗中自动使用的默认血量百分比阈值。</summary>
    public double TriggerHpRatio { get; init; } = 0.6;
    /// <summary>是否在战斗开始瞬间立即自动触发使用。</summary>
    public bool TriggerAtCombatStart { get; init; }
    /// <summary>直接回复生命值点数。</summary>
    public long HitpointRestore { get; init; }
    /// <summary>直接回复法力值点数。</summary>
    public long ManapointRestore { get; init; }
    /// <summary>使用后施加的 Buff 效果列表。</summary>
    public IReadOnlyList<BuffEffectDefinition> Buffs { get; init; } = [];
    /// <summary>默认附带的战斗触发条件规则。</summary>
    public IReadOnlyList<CombatTrigger> DefaultCombatTriggers { get; init; } = [];
}

/// <summary>
/// 游戏中所有道具物品的通用静态原型定义。
/// </summary>
public sealed class ItemDefinition
{
    /// <summary>物品唯一标识 ID。</summary>
    public required string Id { get; init; }
    /// <summary>物品显示名称。</summary>
    public required string Name { get; init; }
    /// <summary>物品详细描述与背景故事。</summary>
    public required string Description { get; init; }
    /// <summary>物品所属大类形态。</summary>
    public required ItemKind Kind { get; init; }
    /// <summary>物品细分分类。</summary>
    public ItemCategory Category { get; init; } = ItemCategory.General;
    /// <summary>物品品阶层次（1 ~ N 阶）。</summary>
    public int Tier { get; init; } = 1;
    /// <summary>系统商店基础售出单价（灵石）。</summary>
    public long SellPrice { get; init; }
    /// <summary>是否允许在市场中挂单交易。</summary>
    public bool IsTradable { get; init; } = true;
    /// <summary>若为消耗品，其专属消耗属性（否则为 null）。</summary>
    public ConsumableDefinition? Consumable { get; init; }
}

/// <summary>
/// 玩家配置的快捷战斗消耗品槽位模型。
/// </summary>
public sealed class ConsumableSlot
{
    /// <summary>槽位装配的物品 ID。</summary>
    public string? ItemId { get; set; }
    /// <summary>单次自动使用消耗的数量。</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>是否启用该槽位的自动触发。</summary>
    public bool Enabled { get; set; }
    /// <summary>该槽位判定的触发器规则列表。</summary>
    public List<CombatTrigger> Triggers { get; set; } = [];
}

/// <summary>
/// 玩家配置的战斗主动技能装备槽位模型。
/// </summary>
public sealed class CombatAbilitySlot
{
    /// <summary>装配的技能 ID。</summary>
    public string? AbilityId { get; set; }
    /// <summary>技能装配等级。</summary>
    public int Level { get; set; } = 1;
    /// <summary>施法触发器规则列表。</summary>
    public List<CombatTrigger> Triggers { get; set; } = [];
}

/// <summary>
/// 玩家完整战斗方案装配聚合（包含流派偏好、六大修仙四维加点、装备穿戴、技能槽与消耗品槽）。
/// </summary>
public sealed class CombatLoadout
{
    /// <summary>气血/体魄修炼等级（影响生命上限与防御）。</summary>
    public int StaminaLevel { get; set; } = 10;
    /// <summary>神识/悟性修炼等级（影响法力上限与命中）。</summary>
    public int IntelligenceLevel { get; set; } = 10;
    /// <summary>攻击/力量修炼等级（影响伤害输出）。</summary>
    public int AttackLevel { get; set; } = 10;
    /// <summary>剑道/身法修炼等级（影响攻速与闪避）。</summary>
    public int SwordsmanshipLevel { get; set; } = 10;
    /// <summary>护体/防御修炼等级（影响减伤与护甲）。</summary>
    public int DefenseLevel { get; set; } = 10;
    /// <summary>符箓/阵法修炼等级（影响抗性与法术威力）。</summary>
    public int TalismanLevel { get; set; } = 10;
    /// <summary>每 10 秒生命回复量。</summary>
    public double HpRegenPerTenSeconds { get; set; } = 0.0;
    /// <summary>每 10 秒法力回复量。</summary>
    public double MpRegenPerTenSeconds { get; set; } = 0.0;
    /// <summary>韧性点数（控制命中率使用 100 / (100 + 韧性) 修正）。</summary>
    public double Tenacity { get; set; }
    /// <summary>当前穿戴的装备列表（装备 ID 与强化等级）。</summary>
    public List<EquippedItem> Equipment { get; set; } = [];
    /// <summary>防御穿透比例。</summary>
    public double PenetrationRatio { get; set; }
    /// <summary>普攻吸血比例。</summary>
    public double LifeStealRatio { get; set; }
    /// <summary>护甲加成伤害比例。</summary>
    public double ArmorDamageRatio { get; set; }
    /// <summary>伤害吸血比例。</summary>
    public double HpDrainRatio { get; set; }
    /// <summary>暴击概率。</summary>
    public double CriticalChance { get; set; }
    /// <summary>暴击伤害加成倍率。</summary>
    public double CriticalDamageRatio { get; set; }
    /// <summary>抗暴/暴击抵抗率。</summary>
    public double CriticalAvoidance { get; set; }
    /// <summary>伤害吸收减免比例。</summary>
    public double DamageAbsorptionRatio { get; set; }
    /// <summary>破招等级分。</summary>
    public double BreakRating { get; set; }
    /// <summary>格挡/招架等级分。</summary>
    public double ParryRating { get; set; }
    /// <summary>技能强度修正系数。</summary>
    public double SkillIntensity { get; set; } = 1.0;
    /// <summary>动态抗性常数 K。</summary>
    public double DynamicResistanceFactorK { get; set; } = 1.0;
    /// <summary>动作减速比例。</summary>
    public double ActionSpeedReductionRatio { get; set; }
    /// <summary>冷却缩减比例。</summary>
    public double CooldownReductionRatio { get; set; }
    /// <summary>掉落率加成（0 表示基础值，0.2 表示提升 20%）。</summary>
    public double CombatDropRate { get; set; }
    /// <summary>掉落数量倍率加成。</summary>
    public double CombatDropQuantity { get; set; }
    /// <summary>稀有掉落发现率加成。</summary>
    public double CombatRareFind { get; set; }
    /// <summary>重新搜寻敌人速度加成（耗时按 1 / (1 + ratio) 衰减）。</summary>
    public double SearchSpeedRatio { get; set; }
    /// <summary>玩家当前已领悟/学会的所有技能 ID 列表。</summary>
    public List<string> LearnedAbilityIds { get; set; } = [];
    /// <summary>玩家当前已领悟功法的经验值字典（AbilityId -> 累计经验值）。</summary>
    public Dictionary<string, double> AbilityExperiences { get; set; } = new(StringComparer.Ordinal);
    /// <summary>战斗与心法功法装备槽位（默认 5 格：首格内功 + 4 格术法，支持后续动态扩展）。</summary>
    public List<CombatAbilitySlot> AbilitySlots { get; set; } =
    [new(), new(), new(), new(), new()];
    /// <summary>3 个固定战斗消耗品快捷槽位。</summary>
    public List<ConsumableSlot> ConsumableSlots { get; set; } =
    [new(), new(), new()];

    /// <summary>深克隆战斗装配方案对象。</summary>
    public CombatLoadout Clone()
    {
        return new CombatLoadout
        {
            StaminaLevel = StaminaLevel,
            IntelligenceLevel = IntelligenceLevel,
            AttackLevel = AttackLevel,
            SwordsmanshipLevel = SwordsmanshipLevel,
            DefenseLevel = DefenseLevel,
            TalismanLevel = TalismanLevel,
            HpRegenPerTenSeconds = HpRegenPerTenSeconds,
            MpRegenPerTenSeconds = MpRegenPerTenSeconds,
            Tenacity = Tenacity,
            Equipment = Equipment?.Select(item => item.Clone()).ToList() ?? [],
            PenetrationRatio = PenetrationRatio,
            LifeStealRatio = LifeStealRatio,
            ArmorDamageRatio = ArmorDamageRatio,
            HpDrainRatio = HpDrainRatio,
            CriticalChance = CriticalChance,
            CriticalDamageRatio = CriticalDamageRatio,
            CriticalAvoidance = CriticalAvoidance,
            DamageAbsorptionRatio = DamageAbsorptionRatio,
            BreakRating = BreakRating,
            ParryRating = ParryRating,
            SkillIntensity = SkillIntensity,
            DynamicResistanceFactorK = DynamicResistanceFactorK,
            ActionSpeedReductionRatio = ActionSpeedReductionRatio,
            CooldownReductionRatio = CooldownReductionRatio,
            CombatDropRate = CombatDropRate,
            CombatDropQuantity = CombatDropQuantity,
            CombatRareFind = CombatRareFind,
            SearchSpeedRatio = SearchSpeedRatio,
            LearnedAbilityIds = LearnedAbilityIds?.ToList() ?? [],
            AbilityExperiences = AbilityExperiences?.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
            AbilitySlots = AbilitySlots.Select(slot => new CombatAbilitySlot
            {
                AbilityId = slot.AbilityId,
                Level = slot.Level,
                Triggers = slot.Triggers?.Select(trigger => trigger.Clone()).ToList() ?? []
            }).ToList(),
            ConsumableSlots = ConsumableSlots.Select(CloneConsumableSlot).ToList()
        };
    }

    /// <summary>
    /// 获取指定技能的当前等级（基于累计经验计算，若未领悟返回 1）。
    /// </summary>
    public int GetAbilityLevel(string abilityId)
    {
        if (string.IsNullOrWhiteSpace(abilityId)) return 1;
        if (AbilityExperiences != null && AbilityExperiences.TryGetValue(abilityId, out var exp))
        {
            return Domain.Constants.ExperienceConstants.GetLevelFromExp(exp);
        }
        return 1;
    }

    private static ConsumableSlot CloneConsumableSlot(ConsumableSlot slot)
    {
        return new ConsumableSlot
        {
            ItemId = slot.ItemId,
            Quantity = slot.Quantity,
            Enabled = slot.Enabled,
            Triggers = slot.Triggers?.Select(trigger => trigger.Clone()).ToList() ?? []
        };
    }
}

