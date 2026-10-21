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
    /// <summary>单条技能/消耗品的触发条件条数上限：服务端装载期、命令期与前端编辑器共用此单一常量。</summary>
    public const int MaxTriggersPerList = 4;

    /// <summary>数值等值比较容差：气血/灵力百分比由除法的浮点结果参与比较，
    /// 精确 `==` 在连续量上永不成立，故 equal 一律按本容差判定。前端模拟器必须同值对齐。</summary>
    public const double EqualTolerance = 1e-6;

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
        // discipline 划分内功心法（internal）与神通术法（spell）；缺省视为术法。
        var discipline = JsonRead.Text(ability, "discipline") ?? "spell";
        if (discipline is not ("internal" or "spell"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "功法类别（discipline）必须为 internal 或 spell。");

        if (discipline == "internal")
        {
            // 内功心法为常驻心法基座（总纲 §5.3）：不参与施放管线，不配置施放/CD/蓝耗/触发器。
            if (ability.TryGetProperty("rootExpEfficiencyPerLevel", out var effProp)
                && (!effProp.TryGetDouble(out var eff) || eff < 0))
                throw new DomainException(DomainErrorCode.InvalidArgument, "内功参悟效率必须为非负数。");
            if (ability.TryGetProperty("rootExpMultipliers", out var multsProp))
            {
                if (multsProp.ValueKind != JsonValueKind.Object)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "内功六维乘率必须为对象。");
                foreach (var prop in multsProp.EnumerateObject())
                {
                    if (!Enum.TryParse<CoreAspectKind>(prop.Name, true, out _))
                        throw new DomainException(DomainErrorCode.InvalidArgument, $"内功六维乘率维度无效: {prop.Name}");
                    if (!prop.Value.TryGetDouble(out var mult) || mult < 0)
                        throw new DomainException(DomainErrorCode.InvalidArgument, $"内功维度 {prop.Name} 乘率必须为非负数。");
                }
            }
            if (ability.TryGetProperty("cultivationExpBonusPoints", out var cultProp)
                && (!cultProp.TryGetDouble(out var cultBonus) || !double.IsFinite(cultBonus)))
                throw new DomainException(DomainErrorCode.InvalidArgument, "内功修为增减点必须为有限数值。");
            if (ability.TryGetProperty("passiveEfficiencyPerLevel", out var passEffProp)
                && (!passEffProp.TryGetDouble(out var passEff) || passEff < 0 || !double.IsFinite(passEff)))
                throw new DomainException(DomainErrorCode.InvalidArgument, "内功被动面板成长效率必须为非负有限数。");
            if (ability.TryGetProperty("passive", out var passiveProp))
            {
                if (passiveProp.ValueKind != JsonValueKind.Object)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "内功常驻被动必须为对象。");
                foreach (var prop in passiveProp.EnumerateObject())
                {
                    if (!prop.Value.TryGetDouble(out var val) || !double.IsFinite(val))
                        throw new DomainException(DomainErrorCode.InvalidArgument, $"内功常驻属性 {prop.Name} 必须为有限数值。");
                }
            }
            return;
        }

        var healing = JsonRead.Text(ability, "effectType") == "heal";
        var ratio = JsonRead.Number(ability, healing ? "healingRatio" : "damageRatio");
        var flat = JsonRead.Number(ability, healing ? "flatHealing" : "flatDamage");
        var cast = JsonRead.Number(ability, "castMilliseconds");
        var cooldown = JsonRead.Number(ability, "cooldownMilliseconds");
        var target = JsonRead.Text(ability, "targetType");
        var supportive = target is "self" or "lowestHitpointsAlly" or "allAllies";
        // Buff 规格：buffs[] 多 Buff 优先，回落单 buff 对象。
        var buffElements = new List<JsonElement>();
        if (ability.ValueKind == JsonValueKind.Object
            && ability.TryGetProperty("buffs", out var buffsArray) && buffsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in buffsArray.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "技能 buffs 列表元素必须为对象。");
                buffElements.Add(entry);
            }
        }
        else if (ability.ValueKind == JsonValueKind.Object
            && ability.TryGetProperty("buff", out var buffElement) && buffElement.ValueKind == JsonValueKind.Object)
        {
            buffElements.Add(buffElement);
        }
        var hasBuff = buffElements.Count > 0;
        var hasEffect = ratio + flat > 0 || healing
            || supportive && (JsonRead.Number(ability, "threatGain") > 0 || JsonRead.Number(ability, "threatPoints") > 0 || hasBuff);
        if (ratio < 0 || flat < 0 || !hasEffect || cast < 0 || cooldown < 0 || cast + cooldown <= 0
            || JsonRead.Number(ability, "manaCost") < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能未配置可执行效果或有效施放周期。");
        if (JsonRead.Text(ability, "category") is not ("physical" or "spell")
            || !Enum.TryParse<CombatElement>(JsonRead.Text(ability, "element") ?? "none", true, out _))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能伤害类别或五行属性无效。");
        if (healing ? target is not (null or "self" or "lowestHitpointsAlly" or "allAllies")
            : target is not (null or "targetEnemy" or "allEnemies" or "self" or "allAllies" or "lowestHitpointsAlly"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能目标没有可执行效果。");
        // 战内 Buff 必须携带正持续时长与有限效果量，否则授予即过期或语义不清。
        foreach (var spec in buffElements) ValidateAbilityBuff(spec);
        if (ability.TryGetProperty("defaultTriggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array)
            foreach (var trigger in triggers.EnumerateArray()) ValidateTrigger(trigger);
    }

    /// <summary>
    /// 校验单条技能 Buff 规格：时长、数值有限性与叠层/层数派生词表。
    /// buffs[] 与单 buff 共用本校验，杜绝多 Buff 形态绕过既有约束。
    /// </summary>
    private static void ValidateAbilityBuff(JsonElement buffElement)
    {
        var duration = JsonRead.Number(buffElement, "durationSeconds");
        JsonRead.Number(buffElement, "hpRegen"); JsonRead.Number(buffElement, "mpRegen");
        JsonRead.Number(buffElement, "attackSpeedPoints"); JsonRead.Number(buffElement, "damageAbsorptionPoints");
        JsonRead.Number(buffElement, "threatPoints"); JsonRead.Number(buffElement, "maxStacks");
        JsonRead.Number(buffElement, "damageBonusPoints");
        JsonRead.Number(buffElement, "threatPerSecondMaxHpPoints");
        JsonRead.Number(buffElement, "attackPower"); JsonRead.Number(buffElement, "accuracy");
        JsonRead.Number(buffElement, "critical"); JsonRead.Number(buffElement, "resistancePoints");
        JsonRead.Number(buffElement, "hpRegenPerSecond");
        var missingPerStack = JsonRead.Number(buffElement, "missingHitpointsPerStack");
        if (missingPerStack < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 的 missingHitpointsPerStack 不能为负数。");
        var resistancePerStack = JsonRead.Number(buffElement, "resistancePerStack");
        if (resistancePerStack < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 的 resistancePerStack 不能为负数。");
        var spellAttackPercent = JsonRead.Number(buffElement, "spellAttackStackPercent");
        if (spellAttackPercent < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 的 spellAttackStackPercent 不能为负数。");
        var stackPolicy = JsonRead.Text(buffElement, "stackPolicy");
        if (stackPolicy is not (null or "capped" or "perCaster" or "maxKeep"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 的 stackPolicy 无效。");
        // resetOnWave 只接受布尔字面量：true = 该 Buff 不进入跨波携带（波次清空即消散）。
        if (buffElement.TryGetProperty("resetOnWave", out var resetFlag)
            && resetFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 的 resetOnWave 必须为布尔值。");
        // 层数由已损气血派生的 Buff 满血时为 0 层，属合法惰性态。
        if (duration <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能 Buff 必须配置正持续时长。");
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
    /// <param name="knownBuffIds">可选 Buff 词表：非 null 时 buff-stacks 的 buffId 必须命中。
    /// 装载期与命令期传当前配表派生集合；传 null 只校验 buffId 非空。</param>
    public static void ValidateTrigger(JsonElement trigger, IReadOnlySet<string>? knownBuffIds = null)
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
        if (JsonRead.Number(trigger, "value") < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件阈值无效。");
        if (condition != "buff-stacks") return;
        var buffId = JsonRead.Text(trigger, "buffId");
        if (string.IsNullOrWhiteSpace(buffId))
            throw new DomainException(DomainErrorCode.InvalidArgument, "状态层数条件必须指定具体状态。");
        // 配表中不存在的 Buff 层数恒为 0，条件永远不成立——属静默死配置，装载/命令期一律拒绝。
        if (knownBuffIds is not null && !knownBuffIds.Contains(buffId))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"触发条件引用了不存在的状态：{buffId}。");
    }

    /// <summary>校验触发条件列表：条数不超过 <see cref="MaxTriggersPerList"/>，逐条过 <see cref="ValidateTrigger"/>。
    /// 空表合法（显式无条件语义）。</summary>
    /// <param name="triggers">触发条件 JSON 数组。</param>
    /// <param name="knownBuffIds">可选 Buff 词表，语义同 <see cref="ValidateTrigger"/>。</param>
    public static void ValidateTriggerList(JsonElement triggers, IReadOnlySet<string>? knownBuffIds = null)
    {
        if (triggers.ValueKind != JsonValueKind.Array)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件必须是数组。");
        if (triggers.GetArrayLength() > MaxTriggersPerList)
            throw new DomainException(DomainErrorCode.InvalidArgument,
                $"触发条件最多 {MaxTriggersPerList} 条。");
        foreach (var trigger in triggers.EnumerateArray()) ValidateTrigger(trigger, knownBuffIds);
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
            var condition = JsonRead.Text(trigger, "condition");
            if (condition is "alive-count" or "dead-count")
            {
                var aliveWanted = condition == "alive-count";
                var count = 0;
                for (var i = 0; i < set.Count; i++)
                    if (set[i].IsAlive == aliveWanted) count++;
                value = count;
            }
            else
            {
                value = double.NaN;
            }
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
            "equal" => Math.Abs(value - threshold) <= EqualTolerance,
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
            "ally-lowest-hp" => MinAliveBy(allies, static unit => unit.Hitpoints / unit.Stats.MaxHitpoints),
            "ally-highest-hp" => MaxAliveBy(allies, static unit => unit.Hitpoints),
            "enemy-lowest-hp" => MinAliveBy(opponents, static unit => unit.Hitpoints / unit.Stats.MaxHitpoints),
            "enemy-highest-hp" => MaxAliveBy(opponents, static unit => unit.Hitpoints),
            _ => null
        };

    /// <summary>存活单位中 key 最小者：复刻 MinBy 语义——按列表序扫描、严格小于才替换（平局保留先见者），
    /// 空集返回 null；double.CompareTo 保证 NaN 键的比较序与原 Comparer&lt;double&gt;.Default 一致。</summary>
    private static CombatantState? MinAliveBy(IReadOnlyList<CombatantState> units, Func<CombatantState, double> key)
    {
        CombatantState? best = null;
        var bestKey = 0d;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (!unit.IsAlive) continue;
            var k = key(unit);
            if (best is null || k.CompareTo(bestKey) < 0) { best = unit; bestKey = k; }
        }
        return best;
    }

    /// <summary>存活单位中 key 最大者：复刻 MaxBy 语义——严格大于才替换（平局保留先见者），空集返回 null。</summary>
    private static CombatantState? MaxAliveBy(IReadOnlyList<CombatantState> units, Func<CombatantState, double> key)
    {
        CombatantState? best = null;
        var bestKey = 0d;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (!unit.IsAlive) continue;
            var k = key(unit);
            if (best is null || k.CompareTo(bestKey) > 0) { best = unit; bestKey = k; }
        }
        return best;
    }

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
