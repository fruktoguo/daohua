using System.Text.Json;

namespace Domain.Combat;

/// <summary>Buff 数值效果的叠层方式：PerStack 值×层数线性放大；Instance 值即总值（状态型比率不随层数放大）。</summary>
public enum BuffEffectStackMode : byte
{
    /// <summary>按层数线性放大（层数承载数值的通道）。</summary>
    PerStack = 0,
    /// <summary>值即总值，不随层数放大（如双抗破防比率——比率是状态本身）。</summary>
    Instance = 1
}

/// <summary>Buff 效果的周期结算网格分组：驱动战斗自然边界调度（None 表示无周期结算点）。</summary>
public enum BuffEffectTickGroup : byte
{
    /// <summary>无周期结算（面板折叠/读条/乘区类通道）。</summary>
    None = 0,
    /// <summary>5 秒回复网格（hpRegen/mpRegen 与后段乘区）。</summary>
    Regen5s = 1,
    /// <summary>1 秒网格（每秒回血与每秒威胁成长）。</summary>
    Tick1s = 2
}

/// <summary>战外（脱战）面板折算目标：ActiveBuff 通道在属性解析中的落点；None 表示战外不折算（战内专属通道）。</summary>
public enum BuffOutOfCombatPanel : byte
{
    None = 0,
    HpRegenFlat = 1,
    MpRegenFlat = 2,
    HpRegenPost = 3,
    MpRegenPost = 4,
    AttackSpeedPoints = 5,
    DamageAbsorptionPoints = 6
}

/// <summary>
/// Buff 数值效果通道（总纲 §7.9.5.3）：每种通道对应一种效果语义与固定结算接入点。
/// 通道顺序即注册表索引顺序；新增通道必须同步登记 <see cref="BuffEffectRegistry.Channels"/>。
/// </summary>
public enum BuffEffectChannel : byte
{
    /// <summary>每 5 秒结算点气血回复量（与面板回复同网格）。</summary>
    HpRegen = 0,
    /// <summary>每 5 秒结算点灵力回复量。</summary>
    MpRegen = 1,
    /// <summary>气血回复后段乘区比率：固定回复合并后再乘 (1+Σ)。</summary>
    HpRegenPostRatio = 2,
    /// <summary>灵力回复后段乘区比率。</summary>
    MpRegenPostRatio = 3,
    /// <summary>每 1 秒结算点气血回复量（独立于面板 5 秒通道，不折入面板值）。</summary>
    HpRegenPerSecond = 4,
    /// <summary>每秒威胁成长点数：每个 1 秒结算点增加最大气血×点数/100 的基础威胁。</summary>
    ThreatPerSecondMaxHpPoints = 5,
    /// <summary>攻击速度点数：并入普攻读条攻速除数通道（NonLinear）。</summary>
    AttackSpeedPoints = 6,
    /// <summary>每层物理攻击力加成（EffectiveStats 攻方折叠）。</summary>
    AttackPower = 7,
    /// <summary>每层命中点数加成。</summary>
    Accuracy = 8,
    /// <summary>每层暴击点数加成。</summary>
    Critical = 9,
    /// <summary>总增伤点数：与面板总增伤同乘区（Linear）合并。</summary>
    DamageBonusPoints = 10,
    /// <summary>免伤点数：与面板免伤同乘区（NonLinear 收口）合并。</summary>
    DamageAbsorptionPoints = 11,
    /// <summary>双抗点数（Instance 型）：正增负破防，逐实例累加后统一乘算，不随层数放大、不吃等级成长。</summary>
    ResistancePoints = 12,
    /// <summary>威胁增幅点数：叠加进有效威胁乘区（§7.8.1）。</summary>
    ThreatPoints = 13,
    /// <summary>每层闪避点数加成。</summary>
    Evasion = 14,
    /// <summary>闪避后段乘区比率。</summary>
    EvasionPostRatio = 15
}

/// <summary>Buff 效果载荷条目：通道 + 每层基础值（Instance 型通道为总值）。</summary>
/// <param name="Channel">效果通道。</param>
/// <param name="Value">每层基础值（Instance 型为总值）；已折入授予期等级成长。</param>
public sealed record BuffEffect(BuffEffectChannel Channel, double Value);

/// <summary>效果通道注册表条目：通道的全部声明式元数据。</summary>
/// <param name="Channel">通道枚举值。</param>
/// <param name="ConfigKey">配表 JSON 键名（与协议字符串通道名同名；存量键保持不变，配表零改动）。</param>
/// <param name="StackMode">叠层方式。</param>
/// <param name="GrowsWithLevel">授予时是否乘等级成长（唯一例外：双抗破防比率不吃成长）。</param>
/// <param name="TickGroup">周期结算网格分组（驱动自然边界调度）。</param>
/// <param name="OutOfCombatPanel">战外面板折算目标。</param>
public sealed record BuffEffectChannelInfo(
    BuffEffectChannel Channel,
    string ConfigKey,
    BuffEffectStackMode StackMode,
    bool GrowsWithLevel,
    BuffEffectTickGroup TickGroup,
    BuffOutOfCombatPanel OutOfCombatPanel);

/// <summary>
/// Buff 效果通道注册表（单点真源，总纲 §7.9.5.3）：新增效果通道 = 枚举值 + 本表一行 + 对应结算点读一行聚合值，
/// 授予路径与词表校验经 <see cref="BuffEffectParser"/> 自动覆盖。
/// DataTool 的 <c>AbilityBuffKeys</c>/<c>ConsumableBuffKeys</c> 白名单为独立进程手工镜像，新增通道须同步登记。
/// </summary>
public static class BuffEffectRegistry
{
    /// <summary>全部通道声明（数组下标即通道枚举值，热路径 O(1) 索引）。</summary>
    public static readonly IReadOnlyList<BuffEffectChannelInfo> Channels =
    [
        new(BuffEffectChannel.HpRegen, "hpRegen",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Regen5s, BuffOutOfCombatPanel.HpRegenFlat),
        new(BuffEffectChannel.MpRegen, "mpRegen",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Regen5s, BuffOutOfCombatPanel.MpRegenFlat),
        new(BuffEffectChannel.HpRegenPostRatio, "hpRegenPostRatio",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Regen5s, BuffOutOfCombatPanel.HpRegenPost),
        new(BuffEffectChannel.MpRegenPostRatio, "mpRegenPostRatio",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Regen5s, BuffOutOfCombatPanel.MpRegenPost),
        new(BuffEffectChannel.HpRegenPerSecond, "hpRegenPerSecond",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Tick1s, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.ThreatPerSecondMaxHpPoints, "threatPerSecondMaxHpPoints",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.Tick1s, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.AttackSpeedPoints, "attackSpeedPoints",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.AttackSpeedPoints),
        new(BuffEffectChannel.AttackPower, "attackPower",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.Accuracy, "accuracy",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.Critical, "critical",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.DamageBonusPoints, "damageBonusPoints",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.DamageAbsorptionPoints, "damageAbsorptionPoints",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.DamageAbsorptionPoints),
        new(BuffEffectChannel.ResistancePoints, "resistancePoints",
            BuffEffectStackMode.Instance, false, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.ThreatPoints, "threatPoints",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.Evasion, "evasion",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
        new(BuffEffectChannel.EvasionPostRatio, "evasionPostRatio",
            BuffEffectStackMode.PerStack, true, BuffEffectTickGroup.None, BuffOutOfCombatPanel.None),
    ];

    private static readonly BuffEffectChannelInfo[] ChannelIndex = BuildChannelIndex();
    private static readonly Dictionary<string, BuffEffectChannelInfo> ConfigKeyMap = BuildConfigKeyMap();

    /// <summary>通道总数（聚合数组长度）。</summary>
    public static int ChannelCount => ChannelIndex.Length;

    /// <summary>按通道枚举取声明（数组索引，O(1)）。</summary>
    public static BuffEffectChannelInfo Info(BuffEffectChannel channel) => ChannelIndex[(int)channel];

    /// <summary>协议字符串通道名（与配表键同名，稳定不变）。</summary>
    public static string ChannelName(BuffEffectChannel channel) => ChannelIndex[(int)channel].ConfigKey;

    /// <summary>按配表 JSON 键查通道；未命中返回 false（生命周期键等非效果键走这里）。</summary>
    public static bool TryGetByConfigKey(string configKey, out BuffEffectChannelInfo info)
        => ConfigKeyMap.TryGetValue(configKey, out info!);

    private static BuffEffectChannelInfo[] BuildChannelIndex()
    {
        var index = new BuffEffectChannelInfo[Channels.Count];
        foreach (var info in Channels)
        {
            if ((int)info.Channel >= index.Length || index[(int)info.Channel] is not null)
                throw new DomainException(DomainErrorCode.InvariantViolation, $"效果通道注册表枚举值冲突：{info.Channel}。");
            index[(int)info.Channel] = info;
        }
        for (var i = 0; i < index.Length; i++)
            if (index[i] is null)
                throw new DomainException(DomainErrorCode.InvariantViolation, $"效果通道注册表枚举值 {i} 缺少登记。");
        return index;
    }

    private static Dictionary<string, BuffEffectChannelInfo> BuildConfigKeyMap()
    {
        var map = new Dictionary<string, BuffEffectChannelInfo>(Channels.Count, StringComparer.Ordinal);
        foreach (var info in Channels)
            if (!map.TryAdd(info.ConfigKey, info))
                throw new DomainException(DomainErrorCode.InvariantViolation, $"效果通道配表键重复：{info.ConfigKey}。");
        return map;
    }
}

/// <summary>
/// 配表 Buff 效果解析器：按注册表 ConfigKey 匹配 buff JSON 对象键，产出通道载荷。
/// 消耗品、功法、行动产出等全部授予路径共用；非数字键值跳过（词表校验在装载期把门）。
/// </summary>
public static class BuffEffectParser
{
    /// <summary>
    /// 解析 buff JSON 对象中的全部效果通道键。
    /// </summary>
    /// <param name="buff">buff 规格对象（consumable.buffs[] 条目、功法 buff/buffs[] 条目或行动产出 modifiers）。</param>
    /// <param name="effectGrowth">授予期成长乘区（等级成长 × 技能增幅；Instance 型与不吃成长通道自动豁免）。</param>
    /// <returns>通道载荷；无任何效果键时为 null（免分配）。</returns>
    public static List<BuffEffect>? Parse(JsonElement buff, double effectGrowth)
    {
        List<BuffEffect>? effects = null;
        foreach (var property in buff.EnumerateObject())
        {
            if (!BuffEffectRegistry.TryGetByConfigKey(property.Name, out var info)) continue;
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value)) continue;
            var scaled = info.GrowsWithLevel ? value * effectGrowth : value;
            (effects ??= []).Add(new BuffEffect(info.Channel, scaled));
        }
        return effects;
    }
}
