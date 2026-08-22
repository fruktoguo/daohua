// ==============================================================================
// 文件名称: CharacterStatResolver.cs
// 主要职责: 通用角色与怪物数值解析器，负责根据品阶（Tier）、等级、生命层次（Rank）与属性权重推导确定性的 14 项基础属性。
// 核心逻辑 / 定义内容:
//   1. 根据生命层次（Boss、Leader、Elite、Minion 等）提供基础全属性与生命放大乘区
//   2. 结合品阶基准期望值（GetTierExpectedAttributes）与传入权重字典计算并补齐 14 项属性
//   3. 保证数值推导的严格确定性，支持缺省权重下的默认属性派生
// ==============================================================================
using Domain.Models;

namespace Domain.GameData;


/// <summary>
/// 通用角色数值解析器。
/// 负责根据等级、阶位、生命层次（Rank）与权重，将 14 项基础属性映射为确定性数值。
/// 所有属性权重缺省默认为 1.0，哪怕传入空权重字典也能推导出完整的基准属性。
/// </summary>
public static class CharacterStatResolver
{
    /// <summary>
    /// 根据生命层次类型获取默认的全属性乘区与生命乘区。
    /// </summary>
    /// <param name="rank">生命层次枚举。</param>
    /// <returns>(全属性倍率, 生命倍率)</returns>
    public static (double StatMultiplier, double HpMultiplier) GetRankMultipliers(UnitRank rank) =>
        rank switch
        {
            UnitRank.Boss => (1.50, 20.00),
            UnitRank.Leader => (1.35, 10.00),
            UnitRank.Elite => (1.20, 5.00),
            UnitRank.Summon or UnitRank.Pet => (1.00, 1.00),
            UnitRank.Normal or UnitRank.PlayerMirror or UnitRank.Npc => (1.00, 1.00),
            _ => (0.80, 3.00) // 默认 minion 小怪
        };

    /// <summary>
    /// 兼容重载：从字符串解析 Rank 并获取乘区。
    /// </summary>
    public static (double StatMultiplier, double HpMultiplier) GetRankMultipliers(string? rank) =>
        TryParseRank(rank, out var parsed) ? GetRankMultipliers(parsed) : (0.80, 3.00);

    public static bool TryParseRank(string? rank, out UnitRank parsed)
    {
        if (string.IsNullOrWhiteSpace(rank))
        {
            parsed = UnitRank.Minion;
            return false;
        }

        var span = rank.AsSpan().Trim();
        if (span.Equals("boss", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Boss; return true; }
        if (span.Equals("leader", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Leader; return true; }
        if (span.Equals("elite", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Elite; return true; }
        if (span.Equals("summon", StringComparison.OrdinalIgnoreCase) || span.Equals("minion_summon", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Summon; return true; }
        if (span.Equals("pet", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Pet; return true; }
        if (span.Equals("player_mirror", StringComparison.OrdinalIgnoreCase) || span.Equals("playermirror", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.PlayerMirror; return true; }
        if (span.Equals("npc", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Npc; return true; }
        if (span.Equals("normal", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Normal; return true; }
        if (span.Equals("minion", StringComparison.OrdinalIgnoreCase)) { parsed = UnitRank.Minion; return true; }

        parsed = UnitRank.Minion;
        return false;
    }

    /// <summary>
    /// 执行通用角色数值解析。
    /// </summary>
    /// <param name="request">解析请求配置。</param>
    /// <returns>解析后的 14 项基础属性集合。</returns>
    public static ResolvedCharacterStats Resolve(CharacterStatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var level = Math.Max(1, request.Level);
        var tier = request.Tier > 0 ? request.Tier : Math.Clamp((level - 1) / 12 + 1, 1, 12);
        var expected = DataBudgetResolver.GetTierExpectedAttributes(tier);

        var (defaultStatMult, defaultHpMult) = GetRankMultipliers(request.Rank);
        var statMultiplier = request.StatMultiplier ?? defaultStatMult;
        var hpMultiplier = request.HpMultiplier ?? defaultHpMult;

        var weights = request.Weights;
        double GetW(string[] keys, double defaultWeight = 1.0)
        {
            if (weights is null || weights.Count == 0) return defaultWeight;
            foreach (var key in keys)
            {
                if (weights.TryGetValue(key, out var val)) return val;
            }
            return defaultWeight;
        }

        // 1. 生命与灵力 (权重缺省默认为 1.0)
        var hpWeight = GetW(["hp", "maxHitpoints", "hitpoints"], 1.0);
        var mpWeight = GetW(["mp", "maxManapoints", "manapoints"], 1.0);
        var rawHp = expected.MaxHitpoints * hpWeight * statMultiplier * hpMultiplier;
        var rawMp = expected.MaxManapoints * mpWeight * statMultiplier;

        long maxHitpoints = request.MaxHitpoints ?? (request.Floor
            ? (long)Math.Max(1, Math.Floor(rawHp))
            : (long)Math.Max(1, Math.Round(rawHp)));

        long maxManapoints = request.MaxManapoints ?? (request.Floor
            ? (long)Math.Max(0, Math.Floor(rawMp))
            : (long)Math.Max(0, Math.Round(rawMp)));

        // 2. 攻击力 (根据流派决定物攻/法攻，权重缺省默认为 1.0)
        var isMagic = request.Style is CombatStyle.FiveElements or CombatStyle.Talisman;
        var baseAttackExpected = isMagic ? expected.MagicAttack : expected.PhysicalAttack;
        var attackWeight = GetW(isMagic ? ["ap", "magicAttack", "attack"] : ["ad", "physicalAttack", "maxDamage", "attack"], 1.0);
        var rawAtk = baseAttackExpected * attackWeight * statMultiplier;

        long maxDamage = request.MaxDamage ?? (request.Floor
            ? (long)Math.Max(1, Math.Floor(rawAtk))
            : (long)Math.Max(1, Math.Round(rawAtk)));

        var defAtkWeight = GetW(["defensiveMaxDamage", "defensiveAttack", "defensivePhysicalAttack"], attackWeight * 0.7);
        var rawDefAtk = baseAttackExpected * defAtkWeight * statMultiplier;

        long defensiveMaxDamage = request.DefensiveMaxDamage ?? (request.Floor
            ? (long)Math.Max(0, Math.Floor(rawDefAtk))
            : (long)Math.Max(0, Math.Round(rawDefAtk)));

        // 3. 双抗 (物理护甲与术法抗性，权重缺省默认为 1.0)
        var armorWeight = GetW(["armor", "defense"], 1.0);
        var rawArmor = expected.Armor * armorWeight * statMultiplier;
        double armor = request.Armor ?? (request.Floor ? Math.Floor(rawArmor) : Math.Round(rawArmor, 2));

        var baseMRes = expected.MagicResistance > 0 ? expected.MagicResistance : expected.Armor;
        var waterResWeight = GetW(["waterResistance", "mres", "magicResistance"], 1.0);
        var natureResWeight = GetW(["natureResistance", "mres", "magicResistance"], 1.0);
        var fireResWeight = GetW(["fireResistance", "mres", "magicResistance"], 1.0);

        var rawWaterRes = baseMRes * waterResWeight * statMultiplier;
        var rawNatureRes = baseMRes * natureResWeight * statMultiplier;
        var rawFireRes = baseMRes * fireResWeight * statMultiplier;

        double waterResistance = request.WaterResistance ?? (request.Floor ? Math.Floor(rawWaterRes) : Math.Round(rawWaterRes, 2));
        double natureResistance = request.NatureResistance ?? (request.Floor ? Math.Floor(rawNatureRes) : Math.Round(rawNatureRes, 2));
        double fireResistance = request.FireResistance ?? (request.Floor ? Math.Floor(rawFireRes) : Math.Round(rawFireRes, 2));

        // 4. 命中与闪避 (权重缺省默认为 1.0)
        var hitWeight = GetW(["hit", "accuracyRating", "accuracy"], 1.0);
        var evaWeight = GetW(["eva", "evasionRating", "evasion"], 1.0);
        var rawAccuracy = expected.AccuracyRating * hitWeight * statMultiplier;
        var rawEvasion = expected.EvasionRating * evaWeight * statMultiplier;

        double accuracyRating = request.AccuracyRating ?? (request.Floor ? Math.Floor(rawAccuracy) : Math.Round(rawAccuracy, 2));
        double evasionRating = request.EvasionRating ?? (request.Floor ? Math.Floor(rawEvasion) : Math.Round(rawEvasion, 2));

        // 5. 暴击与免爆 (权重缺省默认为 1.0)
        var critWeight = GetW(["crit", "critical", "criticalRating"], 1.0);
        var critAvoidWeight = GetW(["critavoid", "criticalAvoidance", "cavoid"], 1.0);
        var rawCrit = expected.CriticalRating * critWeight * statMultiplier;
        var rawCritAvoid = expected.CriticalAvoidance * critAvoidWeight * statMultiplier;

        double criticalRating = request.CriticalRating ?? (request.Floor ? Math.Floor(rawCrit) : Math.Round(rawCrit, 2));
        double criticalAvoidance = request.CriticalAvoidance ?? (request.Floor ? Math.Floor(rawCritAvoid) : Math.Round(rawCritAvoid, 2));

        // 6. 破招与拆招 (权重缺省默认为 1.0)
        var breakWeight = GetW(["break", "break_p", "breakRating"], 1.0);
        var parryWeight = GetW(["parry", "parryRating"], 1.0);
        var rawBreak = expected.BreakRating * breakWeight * statMultiplier;
        var rawParry = expected.ParryRating * parryWeight * statMultiplier;

        double breakRating = request.BreakRating ?? (request.Floor ? Math.Floor(rawBreak) : Math.Round(rawBreak, 2));
        double parryRating = request.ParryRating ?? (request.Floor ? Math.Floor(rawParry) : Math.Round(rawParry, 2));

        // 7. 5 秒固定回复量 (基准为 2% 上限保底 1 点，权重缺省默认为 1.0)
        var hpRegenWeight = GetW(["hpregen", "regen", "hp_regen"], 1.0);
        var mpRegenWeight = GetW(["mpregen", "regen", "mp_regen"], 1.0);

        var rawHpRegen = Math.Max(1.0, maxHitpoints * 0.02 * hpRegenWeight);
        var rawMpRegen = maxManapoints > 0 ? Math.Max(1.0, maxManapoints * 0.02 * mpRegenWeight) : 0.0;

        double hpRegen = request.HpRegen ?? (request.Floor ? Math.Floor(rawHpRegen) : Math.Round(rawHpRegen, 2));
        double mpRegen = request.MpRegen ?? (request.Floor ? Math.Floor(rawMpRegen) : Math.Round(rawMpRegen, 2));

        return new ResolvedCharacterStats(
            MaxHitpoints: maxHitpoints,
            MaxManapoints: maxManapoints,
            MaxDamage: maxDamage,
            DefensiveMaxDamage: defensiveMaxDamage,
            Armor: armor,
            WaterResistance: waterResistance,
            NatureResistance: natureResistance,
            FireResistance: fireResistance,
            AccuracyRating: accuracyRating,
            EvasionRating: evasionRating,
            CriticalRating: criticalRating,
            CriticalAvoidance: criticalAvoidance,
            BreakRating: breakRating,
            ParryRating: parryRating,
            HpRegen: hpRegen,
            MpRegen: mpRegen,
            Tier: tier,
            Level: level,
            StatMultiplier: statMultiplier,
            HpMultiplier: hpMultiplier);
    }

    /// <summary>
    /// 便捷解析重载：通过基础参数直接推导角色 14 项属性。
    /// </summary>
    public static ResolvedCharacterStats Resolve(
        int level,
        UnitRank rank = UnitRank.Minion,
        CombatStyle style = CombatStyle.Body,
        DamageType damageType = DamageType.Physical,
        IReadOnlyDictionary<string, double>? weights = null,
        bool floor = true,
        int tier = 0) =>
        Resolve(new CharacterStatRequest
        {
            Level = level,
            Tier = tier,
            Rank = rank,
            Style = style,
            DamageType = damageType,
            Weights = weights,
            Floor = floor
        });

    /// <summary>
    /// 便捷解析重载（字符串 rank 兼容）：通过基础参数直接推导角色 14 项属性。
    /// </summary>
    public static ResolvedCharacterStats Resolve(
        int level,
        string? rank,
        CombatStyle style = CombatStyle.Body,
        DamageType damageType = DamageType.Physical,
        IReadOnlyDictionary<string, double>? weights = null,
        bool floor = true,
        int tier = 0) =>
        Resolve(level, TryParseRank(rank, out var parsed) ? parsed : UnitRank.Minion, style, damageType, weights, floor, tier);
}
