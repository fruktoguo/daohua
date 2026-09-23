using System.Text.Json;
using Domain.Common;

namespace Domain.Combat;

/// <summary>
/// 技能触发条件的唯一求值器与校验权威：玩家远征、怪物技能槽与斗法镜像共用同一套
/// dependency/condition/comparator 词汇与求值语义，杜绝同一行为按单位身份分叉。
/// 运行时求值对非法输入一律返回 false 且绝不抛异常（校验期已拒绝，运行期安全兜底）；
/// 校验入口负责在装载/配表解析期拒绝词表外的输入。
/// </summary>
public static class SkillTriggerEvaluator
{
    /// <summary>全部触发条件同时满足才返回 true；任一不满足即短路。空表视为恒可用。</summary>
    /// <param name="triggers">触发条件 JSON 列表。</param>
    /// <param name="caster">施放方战斗单位。</param>
    /// <param name="primaryTarget">当前集火目标（target-enemy 依赖的解析对象，可空）。</param>
    /// <param name="allies">施放方全体友军。</param>
    /// <param name="opponents">敌方全体单位。</param>
    public static bool MatchesAll(IReadOnlyList<JsonElement> triggers, CombatantState caster,
        CombatantState? primaryTarget, IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents)
    {
        foreach (var trigger in triggers)
            if (!Matches(trigger, caster, primaryTarget, allies, opponents)) return false;
        return true;
    }

    /// <summary>校验功法/神通配表的可执行性：必须具备有效效果、正施放周期、合法伤害类别与目标类型。</summary>
    /// <param name="ability">功法配表 JSON；非法时抛出 InvalidArgument。</param>
    public static void ValidateAbility(JsonElement ability)
    {
        var healing = JsonRead.Text(ability, "effectType") == "heal";
        var ratio = JsonRead.Number(ability, healing ? "healingRatio" : "damageRatio");
        var flat = JsonRead.Number(ability, healing ? "flatHealing" : "flatDamage");
        var cast = JsonRead.Number(ability, "castMilliseconds");
        var cooldown = JsonRead.Number(ability, "cooldownMilliseconds");
        var target = JsonRead.Text(ability, "targetType");
        var supportive = target is "self" or "lowestHitpointsAlly" or "allAllies";
        JsonElement buffElement = default;
        var hasBuff = ability.ValueKind == JsonValueKind.Object
            && ability.TryGetProperty("buff", out buffElement) && buffElement.ValueKind == JsonValueKind.Object;
        var hasEffect = ratio + flat > 0 || healing
            || supportive && (JsonRead.Number(ability, "threatGain") > 0 || JsonRead.Number(ability, "threatPoints") > 0 || hasBuff);
        if (ratio < 0 || flat < 0 || !hasEffect || cast < 0 || cooldown < 0 || cast + cooldown <= 0
            || JsonRead.Number(ability, "manaCost") < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能未配置可执行效果或有效施放周期。");
        if (JsonRead.Text(ability, "category") is not ("physical" or "spell")
            || !Enum.TryParse<CombatElement>(JsonRead.Text(ability, "element") ?? "none", true, out _))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能伤害类别或五行属性无效。");
        // discipline 划分内功心法（internal）与神通术法（spell）；缺省视为术法。
        if (JsonRead.Text(ability, "discipline") is not (null or "internal" or "spell"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "功法类别（discipline）必须为 internal 或 spell。");
        if (healing ? target is not (null or "self" or "lowestHitpointsAlly" or "allAllies")
            : target is not (null or "targetEnemy" or "allEnemies" or "self" or "allAllies" or "lowestHitpointsAlly"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能目标没有可执行效果。");
        // 战内 Buff 必须携带正持续时长与有限效果量，否则授予即过期或语义不清。
        if (hasBuff)
        {
            var duration = JsonRead.Number(buffElement, "durationSeconds");
            JsonRead.Number(buffElement, "hpRegen"); JsonRead.Number(buffElement, "mpRegen");
            JsonRead.Number(buffElement, "attackSpeedPoints"); JsonRead.Number(buffElement, "damageAbsorptionPoints");
            JsonRead.Number(buffElement, "threatPoints"); JsonRead.Number(buffElement, "maxStacks");
            if (duration <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 必须配置正持续时长。");
        }
        if (ability.TryGetProperty("defaultTriggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array)
            foreach (var trigger in triggers.EnumerateArray()) ValidateTrigger(trigger);
    }

    /// <summary>单体对象依赖词表：自身 / 本轮锁定目标 / 友敌双方经成员选择解析出的单体。</summary>
    private static bool IsUnitDependency(string? dependency) => dependency is "self" or "target-enemy"
        or "ally-lowest-hp" or "ally-highest-hp" or "enemy-lowest-hp" or "enemy-highest-hp";

    /// <summary>集合对象依赖词表：友方/敌方全体存活单位构成的集合（群体聚合条件的作用域）。</summary>
    private static bool IsSetDependency(string? dependency) => dependency is "ally" or "enemy";

    /// <summary>集合对象可选条件词表：作用于集合的群体聚合值。</summary>
    private static bool IsSetCondition(string? condition) => condition is "alive-count" or "dead-count";

    /// <summary>单体数值条件词表。</summary>
    private static bool IsNumericUnitCondition(string? condition) => condition is "current-hp" or "missing-hp"
        or "current-hp-percentage" or "missing-hp-percentage" or "current-mana" or "missing-mana"
        or "current-mana-percentage" or "missing-mana-percentage" or "buff-stacks";

    /// <summary>Boolean 条件词表（活性语义，仅 is-active/is-inactive 比对）。</summary>
    private static bool IsBoolCondition(string? condition) => condition is "stun-status" or "blind-status" or "silence-status";

    /// <summary>
    /// 校验单条触发条件配表：对象-类型-比对三元组必须按作用域矩阵自洽——单体对象仅可用
    /// 单体状态条件，集合对象仅可用群体聚合条件；Boolean 条件只接受是否比对，
    /// 数值条件只接受大小等于比对。技能与消耗品共用同一词表。
    /// </summary>
    /// <param name="trigger">触发条件 JSON；非法时抛出 InvalidArgument。</param>
    public static void ValidateTrigger(JsonElement trigger)
    {
        var dependency = JsonRead.Text(trigger, "dependency");
        if (!IsUnitDependency(dependency) && !IsSetDependency(dependency))
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件对象无效。");
        var condition = JsonRead.Text(trigger, "condition");
        var conditionValid = IsSetDependency(dependency)
            ? IsSetCondition(condition)
            : IsNumericUnitCondition(condition) || IsBoolCondition(condition);
        if (!conditionValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件类型对所选对象不可用。");
        var comparator = JsonRead.Text(trigger, "comparator");
        var comparatorValid = IsBoolCondition(condition)
            ? comparator is "is-active" or "is-inactive"
            : comparator is "greater-than-equal" or "less-than-equal" or "greater-than" or "less-than" or "equal";
        if (!comparatorValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件比对方式与类型不匹配。");
        if (JsonRead.Number(trigger, "value") < 0
            || (condition == "buff-stacks" && string.IsNullOrWhiteSpace(JsonRead.Text(trigger, "buffId"))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件阈值无效。");
    }

    private static bool Matches(JsonElement trigger, CombatantState caster,
        CombatantState? primaryTarget, IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents)
    {
        if (trigger.ValueKind != JsonValueKind.Object) return false;
        var dependency = JsonRead.Text(trigger, "dependency") ?? "self";
        double value;
        if (IsSetDependency(dependency))
        {
            // 群体聚合条件：对集合求值，集合为空仍返回真实计数（0）。
            var set = dependency == "ally" ? allies : opponents;
            value = JsonRead.Text(trigger, "condition") switch
            {
                "alive-count" => set.Count(unit => unit.IsAlive),
                "dead-count" => set.Count(unit => !unit.IsAlive),
                _ => double.NaN
            };
        }
        else
        {
            var unit = ResolveDependency(dependency, caster, primaryTarget, allies, opponents);
            // 依赖无法解析（如敌方全灭后仍被求值）或词表外输入：运行时一律判定失败，不抛异常。
            if (unit is null) return false;
            value = ReadCondition(trigger, unit);
        }
        if (!double.IsFinite(value)) return false;
        var threshold = JsonRead.Number(trigger, "value");
        return JsonRead.Text(trigger, "comparator") switch
        {
            "greater-than-equal" => value >= threshold,
            "less-than-equal" => value <= threshold,
            "greater-than" => value > threshold,
            "less-than" => value < threshold,
            "equal" => value == threshold,
            // 状态/层数类条件的活性比较：value 为层数计数（当前恒 ≥0）。
            "is-active" => value > 0,
            "is-inactive" => value <= 0,
            _ => false
        };
    }

    /// <summary>
    /// 依赖按真实语义解析到具体战斗单位；依赖目标不存在（如 target-enemy 无集火对象）
    /// 或词表外依赖一律返回 null，由调用方判定为条件不满足。
    /// </summary>
    private static CombatantState? ResolveDependency(string dependency, CombatantState caster,
        CombatantState? primaryTarget, IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents)
        => dependency switch
        {
            "self" => caster,
            "target-enemy" => primaryTarget is { IsAlive: true } ? primaryTarget : null,
            "ally-lowest-hp" => allies.Where(unit => unit.IsAlive)
                .MinBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints),
            "ally-highest-hp" => allies.Where(unit => unit.IsAlive).MaxBy(unit => unit.Hitpoints),
            "enemy-lowest-hp" => opponents.Where(unit => unit.IsAlive)
                .MinBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints),
            "enemy-highest-hp" => opponents.Where(unit => unit.IsAlive).MaxBy(unit => unit.Hitpoints),
            _ => null
        };

    private static double ReadCondition(JsonElement trigger, CombatantState unit)
    {
        var maxHp = unit.Stats.MaxHitpoints;
        var maxMana = unit.Stats.MaxManapoints;
        return JsonRead.Text(trigger, "condition") switch
        {
            "current-hp" => unit.Hitpoints,
            "missing-hp" => maxHp - unit.Hitpoints,
            "current-hp-percentage" => unit.Hitpoints / maxHp * 100,
            "missing-hp-percentage" => (maxHp - unit.Hitpoints) / maxHp * 100,
            "current-mana" => unit.Manapoints,
            "missing-mana" => maxMana - unit.Manapoints,
            // 最大灵力为 0 的单位按满灵力口径处理（百分比 0 已消耗），杜绝 0/0 产生 NaN。
            "current-mana-percentage" => maxMana > 0 ? unit.Manapoints / maxMana * 100 : 0,
            "missing-mana-percentage" => maxMana > 0 ? (maxMana - unit.Manapoints) / maxMana * 100 : 0,
            // Buff 层数经单位注入的 Buff 视图求值（战内 Buff + 宿主注入的战外视图）。
            "buff-stacks" => unit.BuffStacks(JsonRead.Text(trigger, "buffId")),
            // 控制类状态条件为预留词表：当前战斗模型无眩晕/致盲/沉默实现，恒按 0 层处理。
            "stun-status" or "blind-status" or "silence-status" => 0d,
            _ => double.NaN
        };
    }
}
