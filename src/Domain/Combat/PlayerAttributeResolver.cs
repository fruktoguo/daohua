using System.Text.Json;
using Domain.GameData;
using Domain.Player;

namespace Domain.Combat;

/// <summary>玩家属性 的领域定义。</summary>
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
    double ThreatRatio = 0,
    bool SpellStyle = false)
{
    /// <summary>获取或设置 金抗性 的值。</summary>
    public double MetalResistance { get; init; }
    /// <summary>获取或设置 木抗性 的值。</summary>
    public double WoodResistance { get; init; }
    /// <summary>获取或设置 水抗性 的值。</summary>
    public double WaterResistance { get; init; }
    /// <summary>获取或设置 火抗性 的值。</summary>
    public double FireResistance { get; init; }
    /// <summary>获取或设置 土抗性 的值。</summary>
    public double EarthResistance { get; init; }
    /// <summary>获取或设置 抗性倍率 的值。</summary>
    public double ResistanceScale { get; init; } = 1;
    /// <summary>
    /// 执行 转换为战斗者 操作。
    /// </summary>
    /// <returns>返回处理结果。</returns>
    public CombatantStats ToCombatant() => CombatantStats.Create(MaxHitpoints,
        SpellStyle ? SpellAttack : PhysicalAttack, Armor,
        AttackIntervalSeconds, Accuracy, Evasion,
        SpellStyle ? CombatDamageKind.Spell : CombatDamageKind.Physical) with
        {
            SpellAttack = SpellAttack,
            MaxManapoints = MaxManapoints,
            BasicAttackRatio = BasicAttackRatio,
            SkillIntensity = SkillIntensity,
            DamageAbsorptionPoints = DamageAbsorptionRatio * 100,
            ThreatRatio = ThreatRatio,
            MagicResistance = MagicResistance, Critical = Critical, CriticalResistance = CriticalAvoidance,
            CriticalMultiplier = CriticalMultiplier,
            BreakPower = BreakPower, ParryResolve = Parry, ResistanceScale = ResistanceScale,
            MetalResistance = MetalResistance, WoodResistance = WoodResistance, WaterResistance = WaterResistance,
            FireResistance = FireResistance, EarthResistance = EarthResistance
        };
}

/// <summary>战斗和面板共用属性计算；各项回复只读取对应配置列。</summary>
public static class PlayerAttributeResolver
{
    /// <summary>
    /// 执行 结算 操作。
    /// </summary>
    /// <param name="state">state 参数。</param>
    /// <param name="catalog">catalog 参数。</param>
    /// <returns>返回处理结果。</returns>
    public static PlayerAttributes Resolve(PlayerAggregateState state, GameDataCatalog catalog)
        => Resolve(state.Progression.RealmLevel, state.EquippedItemIds ?? [], state.Loadout, state.Buffs, state.Combat, catalog);

    /// <summary>
    /// 执行 结算 操作。
    /// </summary>
    /// <param name="player">player 参数。</param>
    /// <param name="catalog">catalog 参数。</param>
    /// <returns>返回处理结果。</returns>
    public static PlayerAttributes Resolve(PlayerAggregate player, GameDataCatalog catalog)
        => Resolve(player.Progression.RealmLevel, player.Loadout.EquippedItemIds, player.Loadout.Export(), player.Buffs.Active,
            new(player.Combat.Hitpoints, player.Combat.MaxHitpoints, player.Combat.Manapoints,
                player.Combat.MaxManapoints, player.Combat.AttackPower, player.Combat.Defense), catalog);

    private static PlayerAttributes Resolve(int level, IReadOnlyList<ItemId> equipment, PlayerLoadoutDocument loadout, IReadOnlyList<ActiveBuff> buffs,
        PlayerCombatStateDocument combat, GameDataCatalog catalog)
    {
        if (catalog.BaseAttributes is null)
            return new(combat.MaxHitpoints, combat.MaxManapoints, combat.AttackPower, 0, combat.Defense, 0, 1, 0, 0, 0, 0, 0, 0, 0, 2);
        var a = catalog.BaseAttributes.At(level);
        var flat = new Dictionary<string, double>(StringComparer.Ordinal);
        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        var threatRatio = 0d;
        foreach (var id in equipment)
        {
            if (!catalog.RawEquipments.TryGetValue(id.Value, out var item))
                throw new DomainException(DomainErrorCode.NotFound, $"装备 {id} 不存在。");
            var enhancement = loadout.Equipment.FirstOrDefault(slot => slot.ItemId == id.Value)?.EnhancementLevel ?? 0;
            Add(item, "flatStats", flat, 1 + enhancement * 0.02);
            Add(item, "baseStatRatios", ratios, 1 + enhancement * 0.02);
            if (item.TryGetProperty("specialMechanics", out var mechanics) && mechanics.ValueKind == JsonValueKind.Object)
                threatRatio += ReadMechanic(mechanics, "threatRatio");
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
        var spellStyle = spell > physical;
        // 普攻节奏：物理系 1.0s 基准 ×(1-ATR)×(1-专属攻速加成)；术法系灵爆 2.0s 基准 ×(1-ATR)。
        var baseInterval = spellStyle ? 2 : 1;
        var interval = baseInterval * castTimeFactor * (spellStyle ? 1 : castTimeFactor) / (1 + Math.Max(0, speed));
        var basicRatio = spellStyle ? 0.6 * skillIntensity : 1 + progress;
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
            ThreatRatio: Math.Max(0, threatRatio),
            SpellStyle: spellStyle)
        {
            MetalResistance = Stat("metalResistance", 0), WoodResistance = Stat("woodResistance", 0),
            WaterResistance = Stat("waterResistance", 0), FireResistance = Stat("fireResistance", 0),
            EarthResistance = Stat("earthResistance", 0), ResistanceScale = tier
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

    /// <summary>
    /// 执行 Apply 操作。
    /// </summary>
    /// <param name="player">player 参数。</param>
    /// <param name="catalog">catalog 参数。</param>
    /// <returns>返回处理结果。</returns>
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
