// ==============================================================================
// 文件名称: StatKeys.cs
// 主要职责: 集中定义修仙世界全域通用属性与词缀修饰器键名（StatKeys）常量。
// 核心逻辑 / 定义内容:
//   1. 基础体征与攻防属性键定义（生命、灵力、物攻、法攻、护甲、抗性等）。
//   2. 战斗机制与副属性键定义（命中、闪避、暴击、攻速、韧性、吸血等）。
//   3. 修仙日常与生活技艺产出倍率键定义（修为吸纳、采集产出、炼丹产出、移动速度等）。
// ==============================================================================

namespace Domain.Constants;

/// <summary>
/// 全域通用属性与词缀修饰器键名常量池。
/// </summary>
public static class StatKeys
{
    // =========================================================================
    // 1. 修仙日常与生活生产
    // =========================================================================

    /// <summary>灵气修为吸纳倍率（基准 1.0）。</summary>
    public const string QiGain = "qi_gain";

    /// <summary>灵草采集产出倍率（基准 1.0）。</summary>
    public const string GatheringOutput = "gathering_output";

    /// <summary>炼丹炼器产出倍率（基准 1.0）。</summary>
    public const string AlchemyOutput = "alchemy_output";

    /// <summary>大世界移动与赶路速度倍率（基准 1.0）。</summary>
    public const string MovementSpeed = "move_speed";

    // =========================================================================
    // 2. 基础攻防与体征属性
    // =========================================================================

    /// <summary>最大生命值加成比例。</summary>
    public const string MaxHitpointsRatio = "hp_max_ratio";

    /// <summary>最大法力值加成比例。</summary>
    public const string MaxManapointsRatio = "mp_max_ratio";

    /// <summary>攻击等级加成比例。</summary>
    public const string AttackLevelRatio = "atk_level_ratio";

    /// <summary>全伤害加成比例。</summary>
    public const string DamageRatio = "damage_ratio";

    /// <summary>物理攻击加成比例。</summary>
    public const string PhysicalDamageRatio = "phys_damage_ratio";

    /// <summary>术法攻击加成比例。</summary>
    public const string SpellDamageRatio = "spell_damage_ratio";

    /// <summary>护甲物理防御加成比例。</summary>
    public const string ArmorRatio = "armor_ratio";

    /// <summary>术法抗性加成比例。</summary>
    public const string MagicResistanceRatio = "magic_res_ratio";

    // =========================================================================
    // 3. 战斗副属性与高级机制
    // =========================================================================

    /// <summary>攻击速度加成比例。</summary>
    public const string AttackSpeedRatio = "atk_speed_ratio";

    /// <summary>命中精准加成比例。</summary>
    public const string AccuracyRatio = "accuracy_ratio";

    /// <summary>身法闪避加成比例。</summary>
    public const string EvasionRatio = "evasion_ratio";

    /// <summary>暴击概率加成（0.0 ~ 1.0）。</summary>
    public const string CriticalChance = "crit_chance";

    /// <summary>暴击伤害倍率加成。</summary>
    public const string CriticalDamage = "crit_damage";

    /// <summary>韧性点数加成。</summary>
    public const string Tenacity = "tenacity";

    /// <summary>生命自然回复速度加成比例。</summary>
    public const string HpRegenRatio = "hp_regen_ratio";

    /// <summary>灵力自然回复速度加成比例。</summary>
    public const string MpRegenRatio = "mp_regen_ratio";

    /// <summary>伤害减免比例 (0.0 ~ 1.0)。</summary>
    public const string DamageReductionRatio = "damage_reduction";

    /// <summary>吸血转化比例 (0.0 ~ 1.0)。</summary>
    public const string LifeStealRatio = "lifesteal_ratio";
}
