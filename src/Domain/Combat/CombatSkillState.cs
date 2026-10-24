using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Common;
using Domain.GameData;
using Domain.Player;

namespace Domain.Combat;

/// <summary>
/// 战斗单位随战携带的技能槽：配表技能 JSON + 槽位等级 + 触发条件列表。
/// 玩家（装配槽位）、怪物（配表技能槽）与斗法镜像共用同一类型——不存在按身份分叉的技能模型。
/// 单时间线出手语义：出手边界先结算待完成动作，再按槽位顺序挑选首个可用技能进入吟唱
/// （吟唱占用动作时长，仅吃行动速度 ATR），无可用技能时回落普通攻击；
/// 技能施放后按 cooldownMilliseconds 进入冷却循环，槽位顺序即优先级（0CD 填充技应放末尾）。
/// 触发条件解析顺序：玩家按功法 ID 配置的自定义条件 → 配表 defaultTriggers → 空表（恒可用）；
/// 怪物配表的 triggerHpRatio 在解析期折叠为等价触发器（self current-hp-percentage ≤ 阈值）。
/// </summary>
public sealed record SkillSlot(JsonElement Ability, double Level = 1, IReadOnlyList<JsonElement>? Triggers = null)
{
    /// <summary>
    /// 槽位触发条件：显式传入时按传入值（空表 = 恒可用）；传 null 时折叠为配表 defaultTriggers，
    /// 保证任何构造路径的求值口径一致。
    /// </summary>
    public IReadOnlyList<JsonElement> Triggers { get; init; } = Triggers ?? DefaultTriggers(Ability);

    private static JsonElement[] DefaultTriggers(JsonElement ability)
    {
        if (ability.ValueKind == JsonValueKind.Object
            && ability.TryGetProperty("defaultTriggers", out var defaults)
            && defaults.ValueKind == JsonValueKind.Array)
            return defaults.EnumerateArray().Select(trigger => trigger.Clone()).ToArray();

        return InferDefaultTriggers(ability);
    }

    /// <summary>
    /// 根据技能特性自动推导出厂默认使用条件：
    /// 1. 治疗技能：友方最低生命百分比 < 75%
    /// 2. Buff技能：自身/目标不存在该Buff (层数 == 0)
    /// 3. 伤害/通用技能：目标当前生命 > 1
    /// </summary>
    internal static JsonElement[] InferDefaultTriggers(JsonElement ability)
    {
        if (ability.ValueKind != JsonValueKind.Object) return [];
        var effectType = JsonRead.Text(ability, "effectType");
        if (string.Equals(effectType, "heal", StringComparison.OrdinalIgnoreCase))
        {
            return [JsonDocument.Parse("""
                {"dependency":"allies","condition":"lowest-hp-percentage","comparator":"less-than","value":75.0}
                """).RootElement.Clone()];
        }

        // 纯 Buff 技能
        string? buffId = null;
        if (ability.TryGetProperty("buff", out var buff) && buff.ValueKind == JsonValueKind.Object)
            buffId = JsonRead.Text(buff, "buffId");
        else if (ability.TryGetProperty("buffs", out var buffs) && buffs.ValueKind == JsonValueKind.Array && buffs.GetArrayLength() > 0)
            buffId = JsonRead.Text(buffs[0], "buffId");

        var damageRatio = JsonRead.Number(ability, "damageRatio");
        var flatDamage = JsonRead.Number(ability, "flatDamage");
        if (!string.IsNullOrEmpty(buffId) && damageRatio <= 0 && flatDamage <= 0)
        {
            var targetType = JsonRead.Text(ability, "targetType");
            var dep = targetType is "targetEnemy" or "allEnemies" ? "target-enemy" : "self";
            return [JsonDocument.Parse($$"""
                {"dependency":"{{dep}}","condition":"buff-stacks","comparator":"equal","value":0.0,"buffId":"{{buffId}}"}
                """).RootElement.Clone()];
        }

        // 默认伤害技能：目标生命 > 1
        return [JsonDocument.Parse("""
            {"dependency":"target-enemy","condition":"current-hp","comparator":"greater-than","value":1.0}
            """).RootElement.Clone()];
    }

    /// <summary>由玩家装配槽位构建通用技能槽：触发条件键缺失时回退配表 defaultTriggers。</summary>
    /// <param name="slots">玩家装配槽位列表（空槽跳过）。</param>
    /// <param name="abilities">全局功法配表。</param>
    /// <param name="abilityTriggers">按功法 ID 挂接的自定义触发条件 map。
    /// 键存在即覆盖（含空数组 = 玩家显式选择无条件恒施放）；键缺失才回退配表默认值。</param>
    /// <returns>按装配顺序排列的通用技能槽。</returns>
    internal static List<SkillSlot> FromAbilitySlots(IReadOnlyList<PlayerAbilitySlot> slots,
        IReadOnlyDictionary<string, JsonElement> abilities,
        IReadOnlyDictionary<string, JsonElement[]>? abilityTriggers = null)
    {
        var skills = new List<SkillSlot>();
        foreach (var slot in slots)
        {
            if (slot.AbilityId is null || !abilities.TryGetValue(slot.AbilityId, out var ability)) continue;
            // 内功心法为常驻被动心法，不进入战斗施放序列（总纲 §5.3.1）。
            if (JsonRead.Text(ability, "discipline") == "internal") continue;
            var triggers = abilityTriggers is not null && abilityTriggers.TryGetValue(slot.AbilityId, out var custom)
                ? custom.Select(trigger => trigger.Clone()).ToArray()
                : null;
            skills.Add(new SkillSlot(ability.Clone(), slot.Level, triggers));
        }
        return skills;
    }
}

/// <summary>
/// 消耗品触发条件的唯一解析入口：玩家按物品 ID 的自定义配置覆盖配表出厂默认。
/// 三态语义与功法一致——键存在即覆盖（空数组 = 玩家显式选择无条件恒可用），
/// 键缺失才回退配表 <c>consumable.triggers</c>。所有消费点（单人即时/持续、组队会话）必须经此取条件，
/// 禁止直接读 <c>ConsumableDefinition.Triggers</c>，否则玩家配置对不同消费路径表现不一致。
/// </summary>
public static class ConsumableTriggerResolver
{
    /// <summary>解析指定物品实际生效的触发条件列表；空表表示无条件恒可用。</summary>
    /// <param name="itemId">物品 ID 字符串。</param>
    /// <param name="consumable">配表消耗品定义（携带出厂默认条件）。</param>
    /// <param name="playerTriggers">玩家自定义触发条件 map（键为物品 ID），可空。</param>
    public static IReadOnlyList<JsonElement> Resolve(string itemId, ConsumableDefinition consumable,
        IReadOnlyDictionary<string, JsonElement[]>? playerTriggers)
    {
        if (playerTriggers is not null && playerTriggers.TryGetValue(itemId, out var custom)) return custom;
        return consumable.Triggers ?? [];
    }
}

public sealed record CombatSkillCooldown(string AbilityId, double RemainingSeconds);

/// <summary>
/// 玩家技能的跨战持久化状态：冷却列表、当前吟唱技能与累计施放计数。
/// 战内该状态由战斗单位的通用槽位时间线承载（SkillCooldownUntil/PendingSkillIndex/SkillCastCount），
/// 本记录只在战斗边界（开战/战后/脱战）与聚合快照层与通用形态互相折算。
/// </summary>
public sealed record PlayerCombatSkillState(
    IReadOnlyList<CombatSkillCooldown> Cooldowns, string? CastingAbilityId,
    double CastRemainingSeconds, double CastCount)
{
    /// <summary>空技能状态单例（无冷却、无吟唱、计数为零）。</summary>
    public static PlayerCombatSkillState Empty { get; } = new([], null, 0, 0);

    /// <summary>
    /// 语义空判定：无冷却、无吟唱、无施放计数。
    /// 反序列化/推进产生的空内容实例与 Empty 单例在记录相等性下不相等（列表引用比较），
    /// 需要「是否为空」语义时必须用本属性而非 != Empty。
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => Cooldowns is null
        || (Cooldowns.Count == 0 && CastingAbilityId is null && CastRemainingSeconds == 0 && CastCount == 0);

    /// <summary>校验状态不变量：冷却项非空且技能 ID 唯一、吟唱字段自洽、计数非负有限。</summary>
    public bool IsValid
    {
        get
        {
            if (Cooldowns is null) return false;
            // 单趟校验 + Ordinal 唯一性集合：等价于原 All(...) && Distinct(Ordinal).Count() == Count。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Cooldowns.Count; i++)
            {
                var value = Cooldowns[i];
                if (string.IsNullOrWhiteSpace(value.AbilityId)
                    || !double.IsFinite(value.RemainingSeconds) || value.RemainingSeconds <= 0
                    || !seen.Add(value.AbilityId)) return false;
            }
            return double.IsFinite(CastRemainingSeconds) && CastRemainingSeconds >= 0
                && (CastingAbilityId is null ? CastRemainingSeconds == 0 : !string.IsNullOrWhiteSpace(CastingAbilityId))
                && double.IsFinite(CastCount) && CastCount >= 0;
        }
    }

    /// <summary>距离下一个脱战结算边界（最早冷却到期）的秒数；无待结算项时返回 +∞（计算属性不参与持久化）。</summary>
    [JsonIgnore]
    public double NextBoundarySeconds
    {
        get
        {
            if (CastingAbilityId is not null) return CastRemainingSeconds;
            var next = double.PositiveInfinity;
            for (var i = 0; i < Cooldowns.Count; i++)
                if (Cooldowns[i].RemainingSeconds < next) next = Cooldowns[i].RemainingSeconds;
            return next;
        }
    }

    /// <summary>非战斗时间也扣减冷却，离开战斗取消吟唱；已支付的灵力不退还。</summary>
    public PlayerCombatSkillState AdvanceIdle(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能推进时间无效。");
        // 过滤+扣减单趟完成：保留剩余时间大于 seconds+1e-9 的项，按原列表顺序。
        var cooldowns = new List<CombatSkillCooldown>(Cooldowns.Count);
        for (var i = 0; i < Cooldowns.Count; i++)
        {
            var value = Cooldowns[i];
            if (value.RemainingSeconds > seconds + 1e-9)
                cooldowns.Add(value with { RemainingSeconds = value.RemainingSeconds - seconds });
        }
        return this with
        {
            Cooldowns = cooldowns,
            CastingAbilityId = null,
            CastRemainingSeconds = 0
        };
    }

    /// <summary>
    /// 开战时把跨战冷却/吟唱映射到战斗单位的通用槽位时间线（战斗时钟起点为 0）。
    /// 冷却条目按技能 ID 对位槽位下标；装配变更后不存在的技能冷却自然丢弃。
    /// </summary>
    public void ApplyTo(CombatantState unit)
    {
        if (Cooldowns.Count == 0 && CastingAbilityId is null && CastCount == 0) return;
        var untils = unit.SkillCooldownUntil ??= new double[unit.Skills.Count];
        for (var i = 0; i < unit.Skills.Count; i++)
        {
            var id = JsonRead.Text(unit.Skills[i].Ability, "id");
            if (id is null) continue;
            // 冷却条目数极小，线性扫描代替 FirstOrDefault 委托：取首个同名条目（Ordinal 语义与 ??= 0 一致）。
            var remaining = 0d;
            for (var j = 0; j < Cooldowns.Count; j++)
                if (string.Equals(Cooldowns[j].AbilityId, id, StringComparison.Ordinal))
                {
                    remaining = Cooldowns[j].RemainingSeconds;
                    break;
                }
            if (remaining > 0) untils[i] = remaining;
        }
        unit.SkillCastCount = CastCount;
        if (CastingAbilityId is not null)
            for (var i = 0; i < unit.Skills.Count; i++)
                if (JsonRead.Text(unit.Skills[i].Ability, "id") == CastingAbilityId)
                {
                    unit.PendingSkillIndex = i;
                    unit.NextAttackAtSeconds = Math.Max(1e-9, CastRemainingSeconds);
                    break;
                }
    }

    /// <summary>战后把战斗单位的通用槽位状态折算回跨战持久化形态。</summary>
    /// <param name="unit">战斗单位（玩家方）。</param>
    /// <param name="elapsedSeconds">当前战斗时钟秒数（冷却折算基准）。</param>
    public static PlayerCombatSkillState FromCombatant(CombatantState unit, double elapsedSeconds)
    {
        var cooldowns = new List<CombatSkillCooldown>();
        if (unit.SkillCooldownUntil is { } untils)
            for (var i = 0; i < untils.Length && i < unit.Skills.Count; i++)
                if (untils[i] > elapsedSeconds + 1e-9
                    && JsonRead.Text(unit.Skills[i].Ability, "id") is { } abilityId)
                    cooldowns.Add(new(abilityId, untils[i] - elapsedSeconds));
        string? castingId = null;
        var castRemaining = 0d;
        if (unit.PendingSkillIndex >= 0 && unit.PendingSkillIndex < unit.Skills.Count)
        {
            castingId = JsonRead.Text(unit.Skills[unit.PendingSkillIndex].Ability, "id");
            castRemaining = castingId is null ? 0 : Math.Max(0, unit.NextAttackAtSeconds - elapsedSeconds);
        }
        return new(cooldowns, castingId, castRemaining, unit.SkillCastCount);
    }
}

/// <summary>
/// 战内 Buff 的跨波携带条目：效果载荷与 <see cref="CombatBuffState"/> 共享同一通道形态（总纲 §7.9.5.3），
/// 折算与重注入只换到期时刻、不复制效果字段——战内 Buff 只随「遭遇战存活时钟」折算，
/// 波间搜寻/复活等脱战相位时钟冻结（与持续消耗品扣减同模型，总纲 §6.3）。
/// </summary>
/// <param name="BuffId">Buff 标识。</param>
/// <param name="RemainingSeconds">定格时刻的剩余生效秒数（战斗时钟口径）。</param>
/// <param name="Stacks">层数。</param>
/// <param name="Name">展示名。</param>
/// <param name="MissingHitpointsPerStack">已损气血派生层数阈值（0=固定层数）。</param>
/// <param name="Effects">数值效果通道载荷。</param>
/// <param name="StatRatios">属性百分比修饰（战外折算消费）。</param>
public sealed record CombatBuffCarry(
    string BuffId,
    double RemainingSeconds,
    double Stacks = 1,
    string Name = "",
    double MissingHitpointsPerStack = 0,
    IReadOnlyList<BuffEffect>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null)
{
    /// <summary>持久化不变量：剩余秒数必须为正有限值（含 MaxValue 永驻哨兵）；层数允许为 0（已损气血派生的惰性态）。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(BuffId) && BuffId.Length <= 128
        && double.IsFinite(RemainingSeconds) && RemainingSeconds > 0
        && double.IsFinite(Stacks) && Stacks >= 0
        && double.IsFinite(MissingHitpointsPerStack)
        && (Effects is null || Effects.All(effect => effect is not null
            && Enum.IsDefined(effect.Channel) && double.IsFinite(effect.Value)))
        && (StatRatios is null || StatRatios.Values.All(double.IsFinite))
        && Name is not null && Name.Length <= 64;
}

/// <summary>
/// 玩家战内 Buff 的跨波携带态：战斗行动存续期间全部战内 Buff 随波次清空折算携带、
/// 下一波入场按剩余秒数重注入；只在整场战斗行动结束（行动移除/散场/重锚）时清空。
/// 标记 <c>resetOnWave</c> 的 Buff（战外 Buff/消耗品槽/特质等由外部真源每波重注入者）
/// 不进入携带——由注入方各自重建，避免携带副本逃逸其真实生命周期。
/// </summary>
public sealed record PlayerCombatBuffState(IReadOnlyList<CombatBuffCarry> Buffs)
{
    /// <summary>空 Buff 状态单例。</summary>
    public static PlayerCombatBuffState Empty { get; } = new([]);

    /// <summary>语义空判定：无携带条目。</summary>
    [JsonIgnore]
    public bool IsEmpty => Buffs is null || Buffs.Count == 0;

    /// <summary>校验状态不变量：条目非空合法、BuffId 唯一。</summary>
    public bool IsValid
    {
        get
        {
            if (Buffs is null) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Buffs.Count; i++)
                if (Buffs[i] is null || !Buffs[i].IsValid || !seen.Add(Buffs[i].BuffId)) return false;
            return true;
        }
    }

    /// <summary>携带条目合计威胁增幅点数（与 <see cref="CombatantState.SumBuffEffect"/> 同式，供详情面板折入）。</summary>
    public double SumThreatPoints()
    {
        var total = 0d;
        if (Buffs is null) return total;
        for (var i = 0; i < Buffs.Count; i++)
        {
            var effects = Buffs[i].Effects;
            if (effects is null) continue;
            for (var j = 0; j < effects.Count; j++)
                if (effects[j].Channel == BuffEffectChannel.ThreatPoints)
                    total += effects[j].Value * Buffs[i].Stacks;
        }
        return total;
    }

    /// <summary>最早到期剩余秒数；空态返回 +∞（计算属性不参与持久化）。</summary>
    [JsonIgnore]
    public double NextBoundarySeconds
    {
        get
        {
            var next = double.PositiveInfinity;
            if (Buffs is not null)
                for (var i = 0; i < Buffs.Count; i++)
                    if (Buffs[i].RemainingSeconds < next) next = Buffs[i].RemainingSeconds;
            return next;
        }
    }

    /// <summary>开战时把携带 Buff 重注入战斗单位：到期时刻 = 当前战斗时钟 + 定格剩余。</summary>
    /// <param name="unit">目标战斗单位。</param>
    /// <param name="battleElapsedSeconds">新战斗的当前时钟秒数（新开场恒为 0）。</param>
    public void ApplyTo(CombatantState unit, double battleElapsedSeconds)
    {
        if (Buffs is null) return;
        for (var i = 0; i < Buffs.Count; i++)
        {
            var carry = Buffs[i];
            var expiresAt = battleElapsedSeconds + carry.RemainingSeconds;
            // 效果载荷引用直传：携带与战内共享同一通道形态，只换到期时刻（同 BuffId 覆盖）。
            unit.SetBuff(new CombatBuffState(carry.BuffId,
                double.IsFinite(expiresAt) ? expiresAt : double.MaxValue,
                carry.Stacks, carry.Name,
                MissingHitpointsPerStack: carry.MissingHitpointsPerStack,
                Effects: carry.Effects, StatRatios: carry.StatRatios));
        }
        // 携带态只定格了授予瞬间的层数；已损气血派生的 Buff 必须按入场时的实际血线重新对齐，
        // 否则波间回复/受伤后重注入会沿用上一波旧层数（罡锋即此类）。
        unit.SyncDerivedStacks();
    }

    /// <summary>波次清空（或战斗切片收尾）时把单位战内 Buff 折算回携带形态。</summary>
    /// <param name="unit">战斗单位（玩家方或盟友）。</param>
    /// <param name="elapsedSeconds">当前战斗时钟秒数（剩余折算基准）。</param>
    /// <returns>携带态；无可携带条目时为语义空。</returns>
    public static PlayerCombatBuffState FromCombatant(CombatantState unit, double elapsedSeconds)
    {
        List<CombatBuffCarry>? carries = null;
        for (var i = 0; i < unit.Buffs.Count; i++)
        {
            var buff = unit.Buffs[i];
            if (buff.ResetOnWave) continue;
            var remaining = buff.ExpiresAtSeconds - elapsedSeconds;
            if (!(remaining > 1e-9)) continue;
            carries ??= new List<CombatBuffCarry>(unit.Buffs.Count - i);
            carries.Add(new CombatBuffCarry(buff.BuffId, remaining, buff.Stacks, buff.Name,
                buff.MissingHitpointsPerStack, buff.Effects, buff.StatRatios));
        }
        return new(carries ?? (IReadOnlyList<CombatBuffCarry>)[]);
    }
}
