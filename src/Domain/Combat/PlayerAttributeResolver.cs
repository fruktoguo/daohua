using System.Text.Json;
using Domain.GameData;
using Domain.Player;

namespace Domain.Combat;

/// <summary>玩家综合战斗属性面板：由境界锚点、装备、Buff 与六维根骨经乘区结算导出。</summary>
public sealed record PlayerAttributes(
    double MaxHitpoints, double MaxManapoints, double PhysicalAttack, double SpellAttack,
    double Armor, double MagicResistance, double Accuracy, double Evasion,
    double Critical, double CriticalAvoidance, double BreakPower, double Parry,
    double HpRegen, double MpRegen, double AttackIntervalSeconds,
    double DamageAbsorptionRatio = 0,
    double CriticalMultiplier = 1.5,
    double SkillIntensity = 1,
    double CooldownFactor = 1,
    double CastTimeFactor = 1,
    double BasicAttackRatio = 1,
    double SearchDelayFactor = 1,
    double ThreatRatio = 0)
{
    /// <summary>金抗性点数（减免金属性伤害）。</summary>
    public double MetalResistance { get; init; }
    /// <summary>木抗性点数（减免木属性伤害）。</summary>
    public double WoodResistance { get; init; }
    /// <summary>水抗性点数（减免水属性伤害）。</summary>
    public double WaterResistance { get; init; }
    /// <summary>火抗性点数（减免火属性伤害）。</summary>
    public double FireResistance { get; init; }
    /// <summary>土抗性点数（减免土属性伤害）。</summary>
    public double EarthResistance { get; init; }
    /// <summary>抗性缩放倍率（等于当前境界阶位 Tier）。</summary>
    public double ResistanceScale { get; init; } = 1;
    /// <summary>反应效果参数包：装备特殊机制（汲取/反弹）汇总结果。</summary>
    public CombatEffectProfile Effects { get; init; } = CombatEffectProfile.Empty;
    /// <summary>折叠为战斗单位属性规格：普攻全流派统一走物理攻击力与物理伤害；术法攻击仅供技能结算。</summary>
    /// <returns>可进入战斗引擎的单位属性。</returns>
    public CombatantStats ToCombatant() => CombatantStats.Create(MaxHitpoints,
        PhysicalAttack, Armor,
        AttackIntervalSeconds, Accuracy, Evasion,
        CombatDamageKind.Physical) with
        {
            SpellAttack = SpellAttack,
            MaxManapoints = MaxManapoints,
            BasicAttackRatio = BasicAttackRatio,
            SkillIntensity = SkillIntensity,
            CastTimeFactor = CastTimeFactor,
            CooldownFactor = CooldownFactor,
            DamageAbsorptionPoints = DamageAbsorptionRatio * 100,
            ThreatRatio = ThreatRatio,
            MagicResistance = MagicResistance, Critical = Critical, CriticalResistance = CriticalAvoidance,
            CriticalMultiplier = CriticalMultiplier,
            BreakPower = BreakPower, ParryResolve = Parry, ResistanceScale = ResistanceScale,
            MetalResistance = MetalResistance, WoodResistance = WoodResistance, WaterResistance = WaterResistance,
            FireResistance = FireResistance, EarthResistance = EarthResistance,
            Effects = Effects
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

    /// <summary>从在线玩家聚合解析属性面板（含装备强化、Buff 与六维加成）。</summary>
    /// <param name="player">在线玩家聚合。</param>
    /// <param name="catalog">全局配表。</param>
    /// <returns>结算完成的属性面板。</returns>
    public static PlayerAttributes Resolve(PlayerAggregate player, GameDataCatalog catalog)
        => Resolve(player.Progression.RealmLevel, player.Loadout.EquippedItemIds, player.Loadout.Export(), player.Buffs.Active,
            new(player.Combat.Hitpoints, player.Combat.MaxHitpoints, player.Combat.Manapoints,
                player.Combat.MaxManapoints, player.Combat.AttackPower, player.Combat.Defense), catalog,
            player.World.EffectiveNodeBuildingEffects);

    private static PlayerAttributes Resolve(int level, IReadOnlyList<ItemId> equipment, PlayerLoadoutDocument loadout, IReadOnlyList<ActiveBuff> buffs,
        PlayerCombatStateDocument combat, GameDataCatalog catalog, Domain.World.BuildingEffectSet? nodeBuildingEffects = null)
    {
        if (catalog.BaseAttributes is null)
            return new(combat.MaxHitpoints, combat.MaxManapoints, combat.AttackPower, 0, combat.Defense, 0, 1, 0, 0, 0, 0, 0, 0, 0, 2);
        var a = catalog.BaseAttributes.At(level);
        var flat = new Dictionary<string, double>(StringComparer.Ordinal);
        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        var threatRatio = 0d;
        double hpDrain = 0, mpDrain = 0, reflect = 0;
        foreach (var id in equipment)
        {
            if (!catalog.RawEquipments.TryGetValue(id.Value, out var item))
                throw new DomainException(DomainErrorCode.NotFound, $"装备 {id} 不存在。");
            var enhancement = loadout.Equipment.FirstOrDefault(slot => slot.ItemId == id.Value)?.EnhancementLevel ?? 0;
            Add(item, "flatStats", flat, 1 + enhancement * 0.02);
            Add(item, "baseStatRatios", ratios, 1 + enhancement * 0.02);
            if (item.TryGetProperty("specialMechanics", out var mechanics) && mechanics.ValueKind == JsonValueKind.Object)
            {
                threatRatio += ReadMechanic(mechanics, "threatRatio");
                hpDrain += ReadMechanic(mechanics, "hpDrainRatio");
                mpDrain += ReadMechanic(mechanics, "mpDrainRatio");
                reflect += ReadMechanic(mechanics, "damageReflectRatio");
            }
        }
        var core = loadout.CoreAttributes;
        double Root(string key) => key switch
        {
            "maxHitpoints" => (core.StaminaLevel + core.DefenseLevel) * 0.01,
            "maxManapoints" => (core.IntelligenceLevel + core.DefenseLevel) * 0.01,
            "physicalArmor" or "hitpointRegen" => core.StaminaLevel * 0.01,
            "spellResistance" or "manapointRegen" => core.IntelligenceLevel * 0.01,
            "physicalAttack" or "criticalChance" or "break" => core.AttackLevel * 0.01,
            "evasion" or "criticalAvoidance" => core.SwordsmanshipLevel * 0.01,
            "parry" => (core.DefenseLevel + core.SwordsmanshipLevel) * 0.01,
            "spellAttack" => core.TalismanLevel * 0.02,
            "accuracy" => core.TalismanLevel * 0.01,
            _ => 0
        };
        var buffRatios = new Dictionary<string, double>(StringComparer.Ordinal);
        double speed = 0, absorption = 0;
        double hpRegenFlat = 0, mpRegenFlat = 0;
        // 节点公共建筑效果：气血自然回复固定值与额外减伤并入同名乘区。
        if (nodeBuildingEffects is not null)
        {
            hpRegenFlat += nodeBuildingEffects.CombatHitpointRegen;
            absorption += nodeBuildingEffects.CombatDamageReductionRatio;
        }
        foreach (var buff in buffs)
        {
            hpRegenFlat += buff.HpRegen * buff.Stacks;
            mpRegenFlat += buff.MpRegen * buff.Stacks;
            speed += buff.AttackSpeedRatio * buff.Stacks;
            absorption += buff.DamageAbsorptionRatio * buff.Stacks;
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
        var progress = Math.Pow((Math.Clamp(level, 1, 127) - 1) / 126d, 2);
        var tier = catalog.BaseAttributes.TierAt(level);
        var skillIntensity = 1 + 0.3 * (tier - 1);
        var cooldownFactor = 1 - 0.75 * progress;
        var castTimeFactor = 1 - 0.5 * progress;
        var physical = Stat("physicalAttack", a.Ad);
        var spell = Stat("spellAttack", a.Ap);
        // 普攻节奏全流派统一：基准 1.0s，同时吃行动速度与攻击速度成长；
        // 掌心雷等填充技的吟唱时长走 castMilliseconds ×(1-ATR) 另一条线，不占普攻间隔。
        var interval = castTimeFactor * castTimeFactor / (1 + Math.Max(0, speed));
        var basicRatio = 1 + progress;
        return new(Stat("maxHitpoints", a.Hp), Stat("maxManapoints", a.Mp), physical,
            spell, Stat("physicalArmor", a.Armor), Stat("spellResistance", a.Mr),
            Stat("accuracy", a.Acc), Stat("evasion", a.Eva), Stat("criticalChance", a.Crit),
            Stat("criticalAvoidance", a.Cavoid), Stat("break", a.BreakPt), Stat("parry", a.Parry),
            hpRegen, mpRegen, interval, Math.Clamp(absorption, 0, 1),
            CriticalMultiplier: 1.5 + 3.5 * progress,
            SkillIntensity: skillIntensity,
            CooldownFactor: cooldownFactor,
            CastTimeFactor: castTimeFactor,
            BasicAttackRatio: basicRatio,
            SearchDelayFactor: castTimeFactor,
            ThreatRatio: Math.Max(0, threatRatio))
        {
            MetalResistance = Stat("metalResistance", 0), WoodResistance = Stat("woodResistance", 0),
            WaterResistance = Stat("waterResistance", 0), FireResistance = Stat("fireResistance", 0),
            EarthResistance = Stat("earthResistance", 0), ResistanceScale = tier,
            Effects = new CombatEffectProfile(hpDrain, mpDrain, reflect)
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
        var c = player.Combat;
        if (c.MaxHitpoints != attributes.MaxHitpoints || c.MaxManapoints != attributes.MaxManapoints
            || c.AttackPower != attributes.PhysicalAttack || c.Defense != attributes.Armor)
            player.MarkDirty(PlayerSectionKind.Combat);
        c.MaxHitpoints = attributes.MaxHitpoints;
        c.MaxManapoints = attributes.MaxManapoints;
        c.AttackPower = attributes.PhysicalAttack;
        c.Defense = attributes.Armor;
        c.Hitpoints = Math.Min(c.Hitpoints, c.MaxHitpoints);
        c.Manapoints = Math.Min(c.Manapoints, c.MaxManapoints);
        return attributes;
    }
}
