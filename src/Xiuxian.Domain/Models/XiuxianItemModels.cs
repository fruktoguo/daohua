namespace Xiuxian.Domain.Models;

public enum XiuxianItemKind
{
    Material = 0,
    Consumable = 1,
    Currency = 2,
    Resource = 3,
    Equipment = 4
}

public sealed class XiuxianBuffEffectDefinition
{
    public required string BuffId { get; init; }
    public required string Name { get; init; }
    public int DurationSeconds { get; init; }
    public bool Stackable { get; init; }
    public int MaxStacks { get; init; } = 1;
    public double QiGainMultiplier { get; init; } = 1;
    public double AttackLevelRatio { get; init; }
    public double DamageRatio { get; init; }
    public double AttackSpeedRatio { get; init; }
    public double GatheringOutputMultiplier { get; init; } = 1;
    public double AlchemyOutputMultiplier { get; init; } = 1;
}

public sealed class XiuxianConsumableDefinition
{
    public required IReadOnlySet<XiuxianActionType> UsableInActionTypes { get; init; }
    public int CooldownSeconds { get; init; }
    public int RecoverySeconds { get; init; }
    public double TriggerHpRatio { get; init; } = 0.6;
    public bool TriggerAtCombatStart { get; init; }
    public long HitpointRestore { get; init; }
    public long ManapointRestore { get; init; }
    public IReadOnlyList<XiuxianBuffEffectDefinition> Buffs { get; init; } = [];
    public IReadOnlyList<XiuxianCombatTrigger> DefaultCombatTriggers { get; init; } = [];
}

public sealed class XiuxianItemDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required XiuxianItemKind Kind { get; init; }
    public required string Category { get; init; }
    public int Tier { get; init; } = 1;
    public long SellPrice { get; init; }
    public bool IsTradable { get; init; } = true;
    public XiuxianConsumableDefinition? Consumable { get; init; }
}

public sealed class XiuxianConsumableSlot
{
    public string? ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Enabled { get; set; }
    public List<XiuxianCombatTrigger> Triggers { get; set; } = [];
}

public sealed class XiuxianCombatAbilitySlot
{
    public string? AbilityId { get; set; }
    public int Level { get; set; } = 1;
    public List<XiuxianCombatTrigger> Triggers { get; set; } = [];
}

public sealed class XiuxianCombatLoadout
{
    public XiuxianCombatStyle Style { get; set; } = XiuxianCombatStyle.Sword;
    public XiuxianDamageType DamageType { get; set; } = XiuxianDamageType.Physical;
    public int StaminaLevel { get; set; } = 10;
    public int IntelligenceLevel { get; set; } = 10;
    public int AttackLevel { get; set; } = 10;
    public int SwordsmanshipLevel { get; set; } = 10;
    public int DefenseLevel { get; set; } = 10;
    public int TalismanLevel { get; set; } = 10;
    public int FiveElementsLevel { get; set; } = 10;
    public double HpRegenPerTenSeconds { get; set; } = 0.0;
    public double MpRegenPerTenSeconds { get; set; } = 0.0;
    // 韧性按百分比点数计算；控制命中率使用 100 / (100 + 韧性) 修正。
    public double Tenacity { get; set; }
    // 装备本身只保存目录 ID 与强化等级，战斗属性由 XiuxianCombatCatalog 聚合。
    public List<XiuxianEquippedItem> Equipment { get; set; } = [];
    // 以下字段用于承载公开装备/功法的战斗词缀；默认值保持旧快照行为不变。
    public double PierceChance { get; set; }
    public double PenetrationRatio { get; set; }
    public double CurseRatio { get; set; }
    public double FuryRatio { get; set; }
    public double WeakenRatio { get; set; }
    public double LifeStealRatio { get; set; }
    public double ArmorDamageRatio { get; set; }
    public double HpDrainRatio { get; set; }
    public double CriticalChance { get; set; }
    public double CriticalDamageRatio { get; set; }
    public double CriticalAvoidance { get; set; }
    public double DamageAbsorptionRatio { get; set; }
    public double BreakRating { get; set; }
    public double ParryRating { get; set; }
    public double SkillIntensity { get; set; } = 1.0;
    public double DynamicResistanceFactorK { get; set; } = 1.0;
    public double ActionSpeedReductionRatio { get; set; }
    public double CooldownReductionRatio { get; set; }
    // 采用公开战斗模拟器的“百分比增量”语义：0 表示目录基础值，0.2 表示提高 20%。
    public double CombatDropRate { get; set; }
    public double CombatDropQuantity { get; set; }
    public double CombatRareFind { get; set; }
    // 重新搜寻速度使用百分比增量语义：1 表示速度提高 100%，耗时按 1 / (1 + ratio) 衰减。
    public double SearchSpeedRatio { get; set; }
    // 技能目录只描述可存在的技能；玩家实际可配置范围由已学技能列表决定。
    public List<string> LearnedAbilityIds { get; set; } = [];
    public List<XiuxianCombatAbilitySlot> AbilitySlots { get; set; } =
    [new(), new(), new(), new()];
    public List<XiuxianConsumableSlot> ConsumableSlots { get; set; } =
    [new(), new(), new()];
    // 仅用于读取旧版快照；归一化时按先灵食后灵饮并入 ConsumableSlots 后清空。
    public List<XiuxianConsumableSlot> FoodSlots { get; set; } = [];
    public List<XiuxianConsumableSlot> DrinkSlots { get; set; } = [];

    public XiuxianCombatLoadout Clone()
    {
        return new XiuxianCombatLoadout
        {
            Style = Style,
            DamageType = DamageType,
            StaminaLevel = StaminaLevel,
            IntelligenceLevel = IntelligenceLevel,
            AttackLevel = AttackLevel,
            SwordsmanshipLevel = SwordsmanshipLevel,
            DefenseLevel = DefenseLevel,
            TalismanLevel = TalismanLevel,
            FiveElementsLevel = FiveElementsLevel,
            HpRegenPerTenSeconds = HpRegenPerTenSeconds,
            MpRegenPerTenSeconds = MpRegenPerTenSeconds,
            Tenacity = Tenacity,
            Equipment = Equipment?.Select(item => item.Clone()).ToList() ?? [],
            PierceChance = PierceChance,
            PenetrationRatio = PenetrationRatio,
            CurseRatio = CurseRatio,
            FuryRatio = FuryRatio,
            WeakenRatio = WeakenRatio,
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
            AbilitySlots = AbilitySlots.Select(slot => new XiuxianCombatAbilitySlot
            {
                AbilityId = slot.AbilityId,
                Level = slot.Level,
                Triggers = slot.Triggers?.Select(trigger => trigger.Clone()).ToList() ?? []
            }).ToList(),
            ConsumableSlots = ConsumableSlots.Select(CloneConsumableSlot).ToList(),
            FoodSlots = FoodSlots.Select(CloneConsumableSlot).ToList(),
            DrinkSlots = DrinkSlots.Select(CloneConsumableSlot).ToList()
        };
    }

    private static XiuxianConsumableSlot CloneConsumableSlot(XiuxianConsumableSlot slot)
    {
        return new XiuxianConsumableSlot
        {
            ItemId = slot.ItemId,
            Quantity = slot.Quantity,
            Enabled = slot.Enabled,
            Triggers = slot.Triggers?.Select(trigger => trigger.Clone()).ToList() ?? []
        };
    }
}
