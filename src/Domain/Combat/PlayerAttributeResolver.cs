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
    double CriticalDamagePoints = CombatGrowthCurve.CriticalDamageBasePoints,
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

    // ── 固定伤害增减点数（总纲第七篇 §7.7.1 第六层：末位直加直减）──
    /// <summary>固定伤害提升。</summary>
    public double FixedDamageBonus { get; init; }
    /// <summary>固定伤害减免。</summary>
    public double FixedDamageReduction { get; init; }
    /// <summary>固定物理伤害提升。</summary>
    public double FixedPhysicalDamageBonus { get; init; }
    /// <summary>固定物理伤害减免。</summary>
    public double FixedPhysicalDamageReduction { get; init; }
    /// <summary>固定法术伤害提升。</summary>
    public double FixedSpellDamageBonus { get; init; }
    /// <summary>固定法术伤害减免。</summary>
    public double FixedSpellDamageReduction { get; init; }

    // ── 特殊属性点数 ──
    /// <summary>攻击速度点数（NonLinear：仅缩短普攻读条）。</summary>
    public double AttackSpeedPoints { get; init; }
    /// <summary>行动速度点数（NonLinear：缩短全部读条）。</summary>
    public double ActionSpeedPoints { get; init; }
    /// <summary>冷却缩减点数（NonLinear：缩短全部冷却）。</summary>
    public double CooldownReductionPoints { get; init; }
    /// <summary>掉落增幅点数（Linear：队战共享掉率合计与单件归属权重；100 点 = 100%，总纲 §21.13）。</summary>
    public double DropBonusPoints { get; init; }

    /// <summary>抗性缩放倍率（等于当前等阶 Tier）。</summary>
    public double ResistanceScale { get; init; } = 1;
    /// <summary>反应效果参数包：装备特殊机制（汲取/反弹）汇总结果（点数）。</summary>
    public CombatEffectProfile Effects { get; init; } = CombatEffectProfile.Empty;
    /// <summary>已解锁特质 ID 集合（trait.*，总纲 §7.12）：条件触发型特质的判定载体，随面板下发。</summary>
    public IReadOnlyList<string>? Traits { get; init; }
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
            DamageAbsorptionPoints = DamageAbsorptionPoints,
            ThreatBonusPoints = ThreatBonusPoints,
            MagicResistance = MagicResistance, Critical = Critical, CriticalResistance = CriticalAvoidance,
            CriticalDamagePoints = CriticalDamagePoints,
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
            FixedDamageBonus = FixedDamageBonus,
            FixedDamageReduction = FixedDamageReduction,
            FixedPhysicalDamageBonus = FixedPhysicalDamageBonus,
            FixedPhysicalDamageReduction = FixedPhysicalDamageReduction,
            FixedSpellDamageBonus = FixedSpellDamageBonus,
            FixedSpellDamageReduction = FixedSpellDamageReduction,
            AttackSpeedPoints = AttackSpeedPoints,
            ActionSpeedPoints = ActionSpeedPoints,
            CooldownReductionPoints = CooldownReductionPoints,
            Effects = Effects,
            Traits = Traits,
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
                : null, state.Progression.TraitIds);

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
            player.World.EffectiveNodeBuildingEffects, player.Progression.TraitIds);
        if (catalog.BaseAttributes is not null)
            player.StoreCachedAttributes(catalog, resolved);
        return resolved;
    }

    /// <summary>直接解析组件持有的领域分段，供非聚合运行时共用同一属性公式。</summary>
    public static PlayerAttributes Resolve(PlayerProgression progression, PlayerWorldState world,
        PlayerResourceState resources, PlayerCombatState combat, PlayerBuffState buffs, PlayerLoadoutState loadout, GameDataCatalog catalog)
        => Resolve(progression.RealmLevel, loadout.EquippedItemIds, loadout.Export(), buffs.Active,
            new(resources.Hitpoints, resources.MaxHitpoints, resources.Manapoints, resources.MaxManapoints,
                combat.AttackPower, combat.Defense), catalog, world.EffectiveNodeBuildingEffects, progression.TraitIds);

    /// <summary>同一组直接分段的属性输入修订号，允许状态所有者复用已解析面板。</summary>
    public static long InputRevision(PlayerProgression progression, PlayerWorldState world,
        PlayerBuffState buffs, PlayerLoadoutState loadout)
        => progression.RealmLevelVersion + world.AttributeInputVersion + buffs.Active.Version
            + loadout.EquippedItemIds.Version + loadout.Equipment.Version + loadout.CoreAttributesVersion
            + progression.TraitIds.Version + loadout.AbilitySlots.Version;

    private static PlayerAttributes Resolve(int level, IReadOnlyList<ItemId> equipment, PlayerLoadoutDocument loadout, IReadOnlyList<ActiveBuff> buffs,
        PlayerCombatStateDocument combat, GameDataCatalog catalog, Domain.World.BuildingEffectSet? nodeBuildingEffects = null,
        IReadOnlyList<string>? traitIds = null)
    {
        var traits = traitIds is { Count: > 0 } ? traitIds.ToArray() : null;
        var threatOnTaken = CombatTraitRules.ThreatOnTakenPoints(traits);
        if (catalog.BaseAttributes is null)
            return new(combat.MaxHitpoints, combat.MaxManapoints, combat.AttackPower, 0, combat.Defense, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1)
            {
                Traits = traits,
                Effects = new CombatEffectProfile(ThreatOnTakenPoints: threatOnTaken)
            };
        var a = catalog.BaseAttributes.At(level);
        var flat = new Dictionary<string, double>(StringComparer.Ordinal);
        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        var traitRatios = new Dictionary<string, double>(StringComparer.Ordinal);
        // 点数通道汇总：装备 flatStats 中 *Points 键（随强化缩放，总纲 §4）+ Buff/特质点数通道（100 点 = 100%）。
        var points = new Dictionary<string, double>(StringComparer.Ordinal);
        double P(string key) => points.GetValueOrDefault(key);
        void AddPoint(string key, double value) { if (value != 0) points[key] = P(key) + value; }
        CombatTraitRules.AccumulateModifiers(traits, traitRatios, points);
        foreach (var id in equipment)
        {
            // 配表历史删项或未知装备优雅跳过，不提供加成但拒绝阻断玩家加载与模拟
            if (!catalog.RawEquipments.TryGetValue(id.Value, out var item))
                continue;
            var enhancement = loadout.Equipment.FirstOrDefault(slot => slot.ItemId == id.Value)?.EnhancementLevel ?? 0;
            // 强化总倍率 = 1.05^等级（总纲 §4.2 指数成长，+20 ≈ 2.653×）
            var enhancementScale = Math.Pow(1.05, enhancement);
            // flatStats 以 Points 结尾的键为百分比点数通道（增幅/冷却/治疗等），与其余固定值同受强化缩放。
            if (item.TryGetProperty("flatStats", out var flatProps) && flatProps.ValueKind == JsonValueKind.Object)
                foreach (var stat in flatProps.EnumerateObject())
                {
                    var number = stat.Value.GetDouble();
                    if (!double.IsFinite(number)) throw new DomainException(DomainErrorCode.InvalidArgument, "装备属性必须为有限数值。");
                    var target = stat.Name.EndsWith("Points", StringComparison.Ordinal) ? points : flat;
                    target[stat.Name] = target.GetValueOrDefault(stat.Name) + number * enhancementScale;
                }
            Add(item, "baseStatRatios", ratios, enhancementScale);
        }
        // 内功常驻面板被动（总纲 §5.3.1）：装配在 0 号内功位的心法 passive 点数与固定值生效。
        if (loadout.AbilitySlots is { Count: > 0 }
            && loadout.AbilitySlots[0].AbilityId is { } internalId
            && catalog.Abilities.TryGetValue(internalId, out var internalAbility)
            && JsonRead.Text(internalAbility, "discipline") == "internal"
            && internalAbility.TryGetProperty("passive", out var passiveProps)
            && passiveProps.ValueKind == JsonValueKind.Object)
        {
            foreach (var stat in passiveProps.EnumerateObject())
            {
                var number = stat.Value.GetDouble();
                if (!double.IsFinite(number)) continue;
                var target = stat.Name.EndsWith("Points", StringComparison.Ordinal) ? points : flat;
                target[stat.Name] = target.GetValueOrDefault(stat.Name) + number;
            }
        }
        var core = loadout.CoreAttributes;
        var root = core.Levels(catalog.RealmProgression);
        // 六维每点同时提供固定值与百分比（总纲 §3.2.2/§7.5）：固定值并入基础池，百分比为独立乘区；
        // 体魄/根骨每点 +12 生命、经脉/根骨每点 +7 灵力、神识术攻 +2、体魄回血与经脉回蓝每点 +0.5，其余映射属性每点 +1。
        double RootFlat(string key) => key switch
        {
            "maxHitpoints" => (root.Stamina + root.Bone) * 12,
            "maxManapoints" => (root.Meridians + root.Bone) * 7,
            "physicalArmor" => root.Stamina,
            "hitpointRegen" => root.Stamina * 0.5,
            "spellResistance" => root.Meridians,
            "manapointRegen" => root.Meridians * 0.5,
            "physicalAttack" or "criticalChance" or "break" => root.Attack,
            "evasion" or "criticalAvoidance" => root.Agility,
            "parry" => root.Bone + root.Agility,
            "spellAttack" => root.Consciousness * 2,
            "accuracy" => root.Consciousness,
            _ => 0
        };
        double RootRatio(string key) => key switch
        {
            "maxHitpoints" => (root.Stamina + root.Bone) * 0.01,
            "maxManapoints" => (root.Meridians + root.Bone) * 0.01,
            "physicalArmor" or "hitpointRegen" => root.Stamina * 0.01,
            "spellResistance" or "manapointRegen" => root.Meridians * 0.01,
            "physicalAttack" or "criticalChance" or "break" => root.Attack * 0.01,
            "evasion" or "criticalAvoidance" => root.Agility * 0.01,
            "parry" => (root.Bone + root.Agility) * 0.01,
            "spellAttack" => root.Consciousness * 0.02,
            "accuracy" => root.Consciousness * 0.01,
            _ => 0
        };
        var buffRatios = new Dictionary<string, double>(StringComparer.Ordinal);
        double hpRegenFlat = 0, mpRegenFlat = 0;
        double hpRegenPost = 0, mpRegenPost = 0;
        // 节点公共建筑效果：气血与灵力自然回复固定值与额外减伤并入同名乘区（比率键 ×100 折点）。
        if (nodeBuildingEffects is not null)
        {
            hpRegenFlat += nodeBuildingEffects.CombatHitpointRegen;
            mpRegenFlat += nodeBuildingEffects.CombatManapointRegen;
            AddPoint("damageAbsorptionPoints", nodeBuildingEffects.CombatDamageReductionRatio * 100);
        }
        foreach (var buff in buffs)
        {
            hpRegenFlat += buff.HpRegen * buff.Stacks;
            mpRegenFlat += buff.MpRegen * buff.Stacks;
            hpRegenPost += buff.HpRegenPostRatio * buff.Stacks;
            mpRegenPost += buff.MpRegenPostRatio * buff.Stacks;
            AddPoint("attackSpeedPoints", buff.AttackSpeedPoints * buff.Stacks);
            AddPoint("damageAbsorptionPoints", buff.DamageAbsorptionPoints * buff.Stacks);
            if (buff.StatRatios is not null)
                foreach (var pair in buff.StatRatios)
                    buffRatios[pair.Key] = buffRatios.GetValueOrDefault(pair.Key) + pair.Value * buff.Stacks;
        }
        // 属性管线：(基础锚点+六维固定值) ×(1+六维%) +装备固定值 → ×(1+装备%) → ×(1+Buff%) → ×(1+特质%)，负向按比例软着陆。
        double Stat(string key, double value) =>
            ((value + RootFlat(key)) * (1 + RootRatio(key)) + flat.GetValueOrDefault(key))
            * SoftRatio(ratios.GetValueOrDefault(key))
            * SoftRatio(buffRatios.GetValueOrDefault(key))
            * SoftRatio(traitRatios.GetValueOrDefault(key));
        // 回复收口公式（总纲 §20.3）：(面板回复 + Σ固定加成) × (1 + Σ后段比率)。
        double hpRegen = (Stat("hitpointRegen", a.HpRegen) + hpRegenFlat) * (1 + hpRegenPost);
        double mpRegen = (Stat("manapointRegen", a.MpRegen) + mpRegenFlat) * (1 + mpRegenPost);
        // 玩家特殊属性为获取型——境界不保送成长点数（装备/内功/特质/Buff 等渠道见上方汇总），
        // 仅保留暴击伤害通道基础 50 点（总纲 §7.7.5）；等阶仅作动态抗性系数 ResistanceScale。
        var tier = catalog.BaseAttributes.TierAt(level);
        AddPoint("criticalDamageBonusPoints", CombatGrowthCurve.CriticalDamageBasePoints);
        var physical = Stat("physicalAttack", a.Ad);
        var spell = Stat("spellAttack", a.Ap);
        // 普攻基准读条恒为 1.0s：速度点数由引擎在出手边界按 NonLinear 双除数结算，不在此预折。
        const double interval = 1;
        return new(Stat("maxHitpoints", a.Hp), Stat("maxManapoints", a.Mp), physical,
            spell, Stat("physicalArmor", a.Armor), Stat("spellResistance", a.Mr),
            Stat("accuracy", a.Acc), Stat("evasion", a.Eva), Stat("criticalChance", a.Crit),
            Stat("criticalAvoidance", a.Cavoid), Stat("break", a.BreakPt), Stat("parry", a.Parry),
            hpRegen, mpRegen, interval, Math.Max(0, P("damageAbsorptionPoints")),
            CriticalDamagePoints: P("criticalDamageBonusPoints"),
            ThreatBonusPoints: P("threatBonusPoints"))
        {
            PhysicalDamageBonusPoints = P("physicalDamageBonusPoints"),
            SpellDamageBonusPoints = P("spellDamageBonusPoints"),
            TotalDamageBonusPoints = P("totalDamageBonusPoints"),
            BasicAttackBonusPoints = P("basicAttackBonusPoints"),
            SkillBonusPoints = P("skillBonusPoints"),
            PhysicalDamageReductionPoints = P("physicalDamageReductionPoints"),
            SpellDamageReductionPoints = P("spellDamageReductionPoints"),
            MetalDamageBonusPoints = P("metalDamageBonusPoints"),
            WoodDamageBonusPoints = P("woodDamageBonusPoints"),
            WaterDamageBonusPoints = P("waterDamageBonusPoints"),
            FireDamageBonusPoints = P("fireDamageBonusPoints"),
            EarthDamageBonusPoints = P("earthDamageBonusPoints"),
            MetalDamageReductionPoints = P("metalDamageReductionPoints"),
            WoodDamageReductionPoints = P("woodDamageReductionPoints"),
            WaterDamageReductionPoints = P("waterDamageReductionPoints"),
            FireDamageReductionPoints = P("fireDamageReductionPoints"),
            EarthDamageReductionPoints = P("earthDamageReductionPoints"),
            FixedDamageBonus = flat.GetValueOrDefault("fixedDamageBonus") + P("fixedDamageBonus"),
            FixedDamageReduction = flat.GetValueOrDefault("fixedDamageReduction") + P("fixedDamageReduction"),
            FixedPhysicalDamageBonus = flat.GetValueOrDefault("fixedPhysicalDamageBonus") + P("fixedPhysicalDamageBonus"),
            FixedPhysicalDamageReduction = flat.GetValueOrDefault("fixedPhysicalDamageReduction") + P("fixedPhysicalDamageReduction"),
            FixedSpellDamageBonus = flat.GetValueOrDefault("fixedSpellDamageBonus") + P("fixedSpellDamageBonus"),
            FixedSpellDamageReduction = flat.GetValueOrDefault("fixedSpellDamageReduction") + P("fixedSpellDamageReduction"),
            AttackSpeedPoints = P("attackSpeedPoints"),
            ActionSpeedPoints = P("actionSpeedPoints"),
            CooldownReductionPoints = P("cooldownReductionPoints"),
            DropBonusPoints = P("dropBonusPoints"),
            ResistanceScale = tier,
            Traits = traits,
            Effects = new CombatEffectProfile(P("hpDrainPoints"), P("mpDrainPoints"), P("damageReflectPoints"), P("healingBonusPoints"), P("threatOnTakenPoints"))
        };
    }

    /// <summary>百分比比率按双模式 Linear 结算：正值 1+x，负值 1/(1+|x|) 软着陆。</summary>
    private static double SoftRatio(double ratio)
        => ratio >= 0 ? 1 + ratio : 1 / (1 - ratio);

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
