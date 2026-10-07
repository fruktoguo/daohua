using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Common;
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
        => ability.ValueKind == JsonValueKind.Object
            && ability.TryGetProperty("defaultTriggers", out var defaults)
            && defaults.ValueKind == JsonValueKind.Array
            ? defaults.EnumerateArray().Select(trigger => trigger.Clone()).ToArray()
            : [];

    /// <summary>由玩家装配槽位构建通用技能槽：触发条件缺省回退配表 defaultTriggers。</summary>
    /// <param name="slots">玩家装配槽位列表（空槽跳过）。</param>
    /// <param name="abilities">全局功法配表。</param>
    /// <param name="abilityTriggers">按功法 ID 挂接的自定义触发条件 map；键命中且非空时覆盖配表默认值。</param>
    /// <returns>按装配顺序排列的通用技能槽。</returns>
    internal static List<SkillSlot> FromAbilitySlots(IReadOnlyList<PlayerAbilitySlot> slots,
        IReadOnlyDictionary<string, JsonElement> abilities,
        IReadOnlyDictionary<string, JsonElement[]>? abilityTriggers = null)
    {
        var skills = new List<SkillSlot>();
        foreach (var slot in slots)
        {
            if (slot.AbilityId is null || !abilities.TryGetValue(slot.AbilityId, out var ability)) continue;
            var triggers = abilityTriggers is not null
                && abilityTriggers.TryGetValue(slot.AbilityId, out var custom)
                && custom.Length > 0
                ? custom.Select(trigger => trigger.Clone()).ToArray()
                : null;
            skills.Add(new SkillSlot(ability.Clone(), slot.Level, triggers));
        }
        return skills;
    }
}

/// <summary>单个技能的冷却条目：技能 ID 与剩余冷却秒数。</summary>
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
