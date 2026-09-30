using System.Text.Json;
using Domain.GameData;
using Domain.Player;

namespace Domain.Combat;

/// <summary>
/// 玩家综合战斗属性面板：由境界锚点、装备、Buff 与六维根骨经乘区结算导出。
/// 百分比修饰属性一律以点数存储（100 点 = 100%，总纲第七篇 §7.1.2/§7.7.5）。
/// </summary>
public sealed record PlayerAttributes(
    double MaxHitpoints, double MaxManapoints, double PhysicalAttack, double SpellAttack,
    double Armor, double MagicResistance, double Accuracy, double Evasion,
    double Critical, double CriticalAvoidance, double BreakPower, double Parry,
    double HpRegen, double MpRegen, double AttackIntervalSeconds,
    double DamageAbsorptionPoints = 0,
    double CriticalMultiplier = 1.5,
    double SkillIntensity = 1,
    double CooldownFactor = 1,
    double CastTimeFactor = 1,
    double BasicAttackRatio = 1,
    double SearchDelayFactor = 1,
    double ThreatBonusPoints = 0)
{
    // ── 增减伤点数（Linear 增伤 / NonLinear 减伤）──
    /// <summary>物理增伤点数。</summary>
    public double PhysicalDamageBonusPoints { get; init; }
    /// <summary>法术增伤点数。</summary>
    public double SpellDamageBonusPoints { get; init; }
    /// <summary>金系增伤点数。</summary>
    public double MetalDamageBonusPoints { get; init; }
    /// <summary>木系增伤点数。</summary>
    public double WoodDamageBonusPoints { get; init; }
    /// <summary>水系增伤点数。</summary>
    public double WaterDamageBonusPoints { get; init; }
    /// <summary>火系增伤点数。</summary>
    public double FireDamageBonusPoints { get; init; }
    /// <summary>土系增伤点数。</summary>
    public double EarthDamageBonusPoints { get; init; }
    /// <summary>总增伤点数。</summary>
    public double TotalDamageBonusPoints { get; init; }
    /// <summary>普攻增幅点数。</summary>
    public double BasicAttackBonusPoints { get; init; }
    /// <summary>技能增幅点数。</summary>
    public double SkillBonusPoints { get; init; }
    /// <summary>物理减伤点数。</summary>
    public double PhysicalDamageReductionPoints { get; init; }
    /// <summary>法术减伤点数。</summary>
    public double SpellDamageReductionPoints { get; init; }
    /// <summary>金系减伤点数。</summary>
    public double MetalDamageReductionPoints { get; init; }
    /// <summary>木系减伤点数。</summary>
    public double WoodDamageReductionPoints { get; init; }
    /// <summary>水系减伤点数。</summary>
    public double WaterDamageReductionPoints { get; init; }
    /// <summary>火系减伤点数。</summary>
    public double FireDamageReductionPoints { get; init; }
    /// <summary>土系减伤点数。</summary>
    public double EarthDamageReductionPoints { get; init; }

    // ── 特殊属性点数 ──
    /// <summary>攻击速度点数（NonLinear：仅缩短普攻读条）。</summary>
    public double AttackSpeedPoints { get; init; }
    /// <summary>行动速度点数（NonLinear：缩短全部读条）。</summary>
    public double ActionSpeedPoints { get; init; }
    /// <summary>冷却缩减点数（NonLinear：缩短全部冷却）。</summary>
    public double CooldownReductionPoints { get; init; }

    /// <summary>抗性缩放倍率（等于当前境界阶位 Tier）。</summary>
    public double ResistanceScale { get; init; } = 1;
    /// <summary>反应效果参数包：装备特殊机制（汲取/反弹）汇总结果（点数）。</summary>
    public CombatEffectProfile Effects { get; init; } = CombatEffectProfile.Empty;
    /// <summary>折叠为战斗单位属性规格：普攻全流派统一走物理攻击力与物理伤害；术法攻击仅供技能结算。</summary>
    /// <param name="combatExperience">战斗经验对抗池取值（总纲 §7.7.6）：由调用方从 Progression.CombatExperience 注入；
    /// 属性面板不承载该字段——它不随装备/Buff/境界等属性输入修订号变化，缓存命中时也必须取调用方现值。</param>
    /// <returns>可进入战斗引擎的单位属性。</returns>
    public CombatantStats ToCombatant(double combatExperience) => CombatantStats.Create(MaxHitpoints,
        PhysicalAttack, Armor,
        AttackIntervalSeconds, Accuracy, Evasion,
        CombatDamageKind.Physical) with
        {
            SpellAttack = SpellAttack,
            MaxManapoints = MaxManapoints,
            HpRegen = HpRegen,
            MpRegen = MpRegen,
            BasicAttackRatio = BasicAttackRatio,
            SkillIntensity = SkillIntensity,
            CastTimeFactor = CastTimeFactor,
            CooldownFactor = CooldownFactor,
            DamageAbsorptionPoints = DamageAbsorptionPoints,
            ThreatBonusPoints = ThreatBonusPoints,
            MagicResistance = MagicResistance, Critical = Critical, CriticalResistance = CriticalAvoidance,
            CriticalMultiplier = CriticalMultiplier,
            BreakPower = BreakPower, ParryResolve = Parry, ResistanceScale = ResistanceScale,
            PhysicalDamageBonusPoints = PhysicalDamageBonusPoints,
            SpellDamageBonusPoints = SpellDamageBonusPoints,
            MetalDamageBonusPoints = MetalDamageBonusPoints,
            WoodDamageBonusPoints = WoodDamageBonusPoints,
            WaterDamageBonusPoints = WaterDamageBonusPoints,
            FireDamageBonusPoints = FireDamageBonusPoints,
            EarthDamageBonusPoints = EarthDamageBonusPoints,
            TotalDamageBonusPoints = TotalDamageBonusPoints,
            BasicAttackBonusPoints = BasicAttackBonusPoints,
            SkillBonusPoints = SkillBonusPoints,
            PhysicalDamageReductionPoints = PhysicalDamageReductionPoints,
            SpellDamageReductionPoints = SpellDamageReductionPoints,
            MetalDamageReductionPoints = MetalDamageReductionPoints,
            WoodDamageReductionPoints = WoodDamageReductionPoints,
            WaterDamageReductionPoints = WaterDamageReductionPoints,
            FireDamageReductionPoints = FireDamageReductionPoints,
            EarthDamageReductionPoints = EarthDamageReductionPoints,
            AttackSpeedPoints = AttackSpeedPoints,
            ActionSpeedPoints = ActionSpeedPoints,
            CooldownReductionPoints = CooldownReductionPoints,
            Effects = Effects,
            CombatExperience = combatExperience
        };
}

/// <summary>战斗和面板共用属性计算；各项回复只读取对应配置列。</summary>
public static class PlayerAttributeResolver
{
    /// <summary>从持久化快照文档解析玩家属性面板。</summary>
    /// <param name="state">玩家聚合快照。</param>
    /// <param name="catalog">全局配表（境界锚点、装备与功法来源）。</param>
    /// <returns>结算完成的属性面板。</returns>
    public static PlayerAttributes Resolve(PlayerAggregateState state, GameDataCatalog catalog)
        => Resolve(state.Progression.RealmLevel, state.EquippedItemIds ?? [], state.Loadout, state.Buffs, state.Combat, catalog,
            string.Equals(state.World.NodeBuildingNodeId, state.World.NodeId, StringComparison.Ordinal)
                ? state.World.NodeBuildingEffects
                : null);

    /// <summary>从在线玩家聚合解析属性面板（含装备强化、Buff 与六维加成）。
    /// 带「属性输入修订号」缓存：同配表实例且全部输入源版本号未变时直接复用上轮结果；
    /// 命中结果与全量重算逐位一致。catalog.BaseAttributes 缺失时面板由 Combat 分段兜底，
    /// 该输入不经修订号覆盖，此路径旁路缓存逐次重算。</summary>
    /// <param name="player">在线玩家聚合。</param>
    /// <param name="catalog">全局配表。</param>
    /// <returns>结算完成的属性面板。</returns>
    public static PlayerAttributes Resolve(PlayerAggregate player, GameDataCatalog catalog)
    {
        if (catalog.BaseAttributes is not null && player.TryGetCachedAttributes(catalog, out var cached))
            return cached;
        var resolved = Resolve(player.Progression.RealmLevel, player.Loadout.EquippedItemIds, player.Loadout.Export(), player.Buffs.Active,
            new(player.Resources.Hitpoints, player.Resources.MaxHitpoints, player.Resources.Manapoints,
                player.Resources.MaxManapoints, player.Combat.AttackPower, player.Combat.Defense), catalog,
            player.World.EffectiveNodeBuildingEffects);
        if (catalog.BaseAttributes is not null)
            player.StoreCachedAttributes(catalog, resolved);
        return resolved;
    }

    /// <summary>直接解析组件持有的领域分段，供非聚合运行时共用同一属性公式。</summary>
    public static PlayerAttributes Resolve(PlayerProgression progression, PlayerWorldState world,
        PlayerResourceState resources, PlayerCombatState combat, PlayerBuffState buffs, PlayerLoadoutState loadout, GameDataCatalog catalog)
        => Resolve(progression.RealmLevel, loadout.EquippedItemIds, loadout.Export(), buffs.Active,
            new(resources.Hitpoints, resources.MaxHitpoints, resources.Manapoints, resources.MaxManapoints,
                combat.AttackPower, combat.Defense), catalog, world.EffectiveNodeBuildingEffects);

    /// <summary>同一组直接分段的属性输入修订号，允许状态所有者复用已解析面板。</summary>
    public static long InputRevision(PlayerProgression progression, PlayerWorldState world,
        PlayerBuffState buffs, PlayerLoadoutState loadout)
        => progression.RealmLevelVersion + world.AttributeInputVersion + buffs.Active.Version
            + loadout.EquippedItemIds.Version + loadout.Equipment.Version + loadout.CoreAttributesVersion;

    private static PlayerAttributes Resolve(int level, IReadOnlyList<ItemId> equipment, PlayerLoadoutDocument loadout, IReadOnlyList<ActiveBuff> buffs,
        PlayerCombatStateDocument combat, GameDataCatalog catalog, Domain.World.BuildingEffectSet? nodeBuildingEffects = null)
    {
        if (catalog.BaseAttributes is null)
            return new(combat.MaxHitpoints, combat.MaxManapoints, combat.AttackPower, 0, combat.Defense, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1);
        var a = catalog.BaseAttributes.At(level);
        var flat = new Dictionary<string, double>(StringComparer.Ordinal);
        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        // 点数通道汇总：装备 specialMechanics 全量点数键 + Buff 点数通道（100 点 = 100%）。
        double threat = 0, hpDrain = 0, mpDrain = 0, reflect = 0, healingBonus = 0;
        double attackSpeed = 0, actionSpeed = 0, cooldownReduction = 0;
        double physicalBonus = 0, spellBonus = 0, totalBonus = 0, basicAttackBonus = 0, skillBonus = 0;
        double physicalReduction = 0, spellReduction = 0;
        double metalBonus = 0, woodBonus = 0, waterBonus = 0, fireBonus = 0, earthBonus = 0;
        double metalReduction = 0, woodReduction = 0, waterReduction = 0, fireReduction = 0, earthReduction = 0;
        foreach (var id in equipment)
        {
            if (!catalog.RawEquipments.TryGetValue(id.Value, out var item))
                throw new DomainException(DomainErrorCode.NotFound, $"装备 {id} 不存在。");
            var enhancement = loadout.Equipment.FirstOrDefault(slot => slot.ItemId == id.Value)?.EnhancementLevel ?? 0;
            Add(item, "flatStats", flat, 1 + enhancement * 0.02);
            Add(item, "baseStatRatios", ratios, 1 + enhancement * 0.02);
            if (item.TryGetProperty("specialMechanics", out var mechanics) && mechanics.ValueKind == JsonValueKind.Object)
            {
                threat += ReadMechanic(mechanics, "threatBonusPoints");
                hpDrain += ReadMechanic(mechanics, "hpDrainPoints");
                mpDrain += ReadMechanic(mechanics, "mpDrainPoints");
                reflect += ReadMechanic(mechanics, "damageReflectPoints");
                healingBonus += ReadMechanic(mechanics, "healingBonusPoints");
                attackSpeed += ReadMechanic(mechanics, "attackSpeedPoints");
                actionSpeed += ReadMechanic(mechanics, "actionSpeedPoints");
                cooldownReduction += ReadMechanic(mechanics, "cooldownReductionPoints");
                physicalBonus += ReadMechanic(mechanics, "physicalDamageBonusPoints");
                spellBonus += ReadMechanic(mechanics, "spellDamageBonusPoints");
                totalBonus += ReadMechanic(mechanics, "totalDamageBonusPoints");
                basicAttackBonus += ReadMechanic(mechanics, "basicAttackBonusPoints");
                skillBonus += ReadMechanic(mechanics, "skillBonusPoints");
                physicalReduction += ReadMechanic(mechanics, "physicalDamageReductionPoints");
                spellReduction += ReadMechanic(mechanics, "spellDamageReductionPoints");
                metalBonus += ReadMechanic(mechanics, "metalDamageBonusPoints");
                woodBonus += ReadMechanic(mechanics, "woodDamageBonusPoints");
                waterBonus += ReadMechanic(mechanics, "waterDamageBonusPoints");
                fireBonus += ReadMechanic(mechanics, "fireDamageBonusPoints");
                earthBonus += ReadMechanic(mechanics, "earthDamageBonusPoints");
                metalReduction += ReadMechanic(mechanics, "metalDamageReductionPoints");
                woodReduction += ReadMechanic(mechanics, "woodDamageReductionPoints");
                waterReduction += ReadMechanic(mechanics, "waterDamageReductionPoints");
                fireReduction += ReadMechanic(mechanics, "fireDamageReductionPoints");
                earthReduction += ReadMechanic(mechanics, "earthDamageReductionPoints");
            }
        }
        var core = loadout.CoreAttributes;
        double Root(string key) => key switch
        {
            "maxHitpoints" => (core.StaminaLevel + core.BoneLevel) * 0.01,
            "maxManapoints" => (core.MeridiansLevel + core.BoneLevel) * 0.01,
            "physicalArmor" or "hitpointRegen" => core.StaminaLevel * 0.01,
            "spellResistance" or "manapointRegen" => core.MeridiansLevel * 0.01,
            "physicalAttack" or "criticalChance" or "break" => core.AttackLevel * 0.01,
            "evasion" or "criticalAvoidance" => core.AgilityLevel * 0.01,
            "parry" => (core.BoneLevel + core.AgilityLevel) * 0.01,
            "spellAttack" => core.ConsciousnessLevel * 0.02,
            "accuracy" => core.ConsciousnessLevel * 0.01,
            _ => 0
        };
        var buffRatios = new Dictionary<string, double>(StringComparer.Ordinal);
        double absorption = 0;
        double hpRegenFlat = 0, mpRegenFlat = 0;
        // 节点公共建筑效果：气血自然回复固定值与额外减伤并入同名乘区（比率键 ×100 折点）。
        if (nodeBuildingEffects is not null)
        {
            hpRegenFlat += nodeBuildingEffects.CombatHitpointRegen;
            absorption += nodeBuildingEffects.CombatDamageReductionRatio * 100;
        }
        foreach (var buff in buffs)
        {
            hpRegenFlat += buff.HpRegen * buff.Stacks;
            mpRegenFlat += buff.MpRegen * buff.Stacks;
            attackSpeed += buff.AttackSpeedPoints * buff.Stacks;
            absorption += buff.DamageAbsorptionPoints * buff.Stacks;
            if (buff.StatRatios is not null)
                foreach (var pair in buff.StatRatios)
                    buffRatios[pair.Key] = buffRatios.GetValueOrDefault(pair.Key) + pair.Value * buff.Stacks;
        }
        // 四乘区：基础锚点 ×(1+六维%) +装备固定值 → ×(1+装备%) → ×(1+Buff%)，负向按比例软着陆。
        double Stat(string key, double value) =>
            (value * (1 + Root(key)) + flat.GetValueOrDefault(key))
            * SoftRatio(ratios.GetValueOrDefault(key))
            * SoftRatio(buffRatios.GetValueOrDefault(key));
        double hpRegen = Stat("hitpointRegen", a.HpRegen) + hpRegenFlat;
        double mpRegen = Stat("manapointRegen", a.MpRegen) + mpRegenFlat;
        // 成长乘区与怪物同构：统一走 CombatGrowthCurve 单点实现。
        var progress = CombatGrowthCurve.Progress(level);
        var tier = catalog.BaseAttributes.TierAt(level);
        var skillIntensity = CombatGrowthCurve.SkillIntensity(tier);
        var cooldownFactor = CombatGrowthCurve.CooldownFactor(level);
        var castTimeFactor = CombatGrowthCurve.CastTimeFactor(level);
        var physical = Stat("physicalAttack", a.Ad);
        var spell = Stat("spellAttack", a.Ap);
        // 普攻基准读条 = 基准 1.0s × 成长曲线平方（行动速度+攻速双成长通道）；
        // 攻击速度/行动速度点数由引擎在出手边界按 NonLinear 除数结算，不在此预折。
        var interval = castTimeFactor * castTimeFactor;
        var basicRatio = CombatGrowthCurve.BasicAttackRatio(level);
        return new(Stat("maxHitpoints", a.Hp), Stat("maxManapoints", a.Mp), physical,
            spell, Stat("physicalArmor", a.Armor), Stat("spellResistance", a.Mr),
            Stat("accuracy", a.Acc), Stat("evasion", a.Eva), Stat("criticalChance", a.Crit),
            Stat("criticalAvoidance", a.Cavoid), Stat("break", a.BreakPt), Stat("parry", a.Parry),
            hpRegen, mpRegen, interval, Math.Max(0, absorption),
            CriticalMultiplier: CombatGrowthCurve.CriticalMultiplier(level),
            SkillIntensity: skillIntensity,
            CooldownFactor: cooldownFactor,
            CastTimeFactor: castTimeFactor,
            BasicAttackRatio: basicRatio,
            SearchDelayFactor: castTimeFactor,
            ThreatBonusPoints: threat)
        {
            PhysicalDamageBonusPoints = physicalBonus,
            SpellDamageBonusPoints = spellBonus,
            TotalDamageBonusPoints = totalBonus,
            BasicAttackBonusPoints = basicAttackBonus,
            SkillBonusPoints = skillBonus,
            PhysicalDamageReductionPoints = physicalReduction,
            SpellDamageReductionPoints = spellReduction,
            MetalDamageBonusPoints = metalBonus,
            WoodDamageBonusPoints = woodBonus,
            WaterDamageBonusPoints = waterBonus,
            FireDamageBonusPoints = fireBonus,
            EarthDamageBonusPoints = earthBonus,
            MetalDamageReductionPoints = metalReduction,
            WoodDamageReductionPoints = woodReduction,
            WaterDamageReductionPoints = waterReduction,
            FireDamageReductionPoints = fireReduction,
            EarthDamageReductionPoints = earthReduction,
            AttackSpeedPoints = attackSpeed,
            ActionSpeedPoints = actionSpeed,
            CooldownReductionPoints = cooldownReduction,
            ResistanceScale = tier,
            Effects = new CombatEffectProfile(hpDrain, mpDrain, reflect, healingBonus)
        };
    }

    /// <summary>百分比比率按双模式 Linear 结算：正值 1+x，负值 1/(1+|x|) 软着陆。</summary>
    private static double SoftRatio(double ratio)
        => ratio >= 0 ? 1 + ratio : 1 / (1 - ratio);

    private static double ReadMechanic(JsonElement mechanics, string key)
    {
        var number = mechanics.TryGetProperty(key, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0;
        if (!double.IsFinite(number)) throw new DomainException(DomainErrorCode.InvalidArgument, "装备特殊机制数值必须有限。");
        return number;
    }

    private static void Add(JsonElement item, string key, Dictionary<string, double> target, double scale)
    {
        if (!item.TryGetProperty(key, out var values)) return;
        foreach (var value in values.EnumerateObject())
        {
            var number = value.Value.GetDouble();
            if (!double.IsFinite(number)) throw new DomainException(DomainErrorCode.InvalidArgument, "装备属性必须为有限数值。");
            target[value.Name] = target.GetValueOrDefault(value.Name) + number * scale;
        }
    }

    /// <summary>重新解析属性并把生命/灵力/攻防面板写回聚合战斗分段；上限变化时夹取当前值，变更时标记 Combat 分段脏。</summary>
    /// <param name="player">在线玩家聚合（Combat 分段就地更新）。</param>
    /// <param name="catalog">全局配表。</param>
    /// <returns>结算完成的属性面板。</returns>
    public static PlayerAttributes Apply(PlayerAggregate player, GameDataCatalog catalog)
    {
        var attributes = Resolve(player, catalog);
        var r = player.Resources;
        var c = player.Combat;
        if (r.MaxHitpoints != attributes.MaxHitpoints || r.MaxManapoints != attributes.MaxManapoints
            || c.AttackPower != attributes.PhysicalAttack || c.Defense != attributes.Armor)
            player.MarkDirty(PlayerSectionKind.Combat);
        r.MaxHitpoints = attributes.MaxHitpoints;
        r.MaxManapoints = attributes.MaxManapoints;
        c.AttackPower = attributes.PhysicalAttack;
        c.Defense = attributes.Armor;
        r.Hitpoints = Math.Min(r.Hitpoints, r.MaxHitpoints);
        r.Manapoints = Math.Min(r.Manapoints, r.MaxManapoints);
        return attributes;
    }
}
