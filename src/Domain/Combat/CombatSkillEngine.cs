using System.Text.Json;

namespace Domain.Combat;

/// <summary>单个技能的冷却条目：技能 ID 与剩余冷却秒数。</summary>
public sealed record CombatSkillCooldown(string AbilityId, double RemainingSeconds);
/// <summary>技能调度的可持久化状态：冷却列表、当前吟唱技能与累计施放计数。</summary>
public sealed record PlayerCombatSkillState(
    IReadOnlyList<CombatSkillCooldown> Cooldowns, string? CastingAbilityId,
    double CastRemainingSeconds, double CastCount)
{
    /// <summary>空技能状态单例（无冷却、无吟唱、计数为零）。</summary>
    public static PlayerCombatSkillState Empty { get; } = new([], null, 0, 0);

    /// <summary>校验状态不变量：冷却项非空且技能 ID 唯一、吟唱字段自洽、计数非负有限。</summary>
    public bool IsValid => Cooldowns is not null && Cooldowns.All(value => !string.IsNullOrWhiteSpace(value.AbilityId)
        && double.IsFinite(value.RemainingSeconds) && value.RemainingSeconds > 0)
        && Cooldowns.Select(value => value.AbilityId).Distinct(StringComparer.Ordinal).Count() == Cooldowns.Count
        && double.IsFinite(CastRemainingSeconds) && CastRemainingSeconds >= 0
        && (CastingAbilityId is null ? CastRemainingSeconds == 0 : !string.IsNullOrWhiteSpace(CastingAbilityId))
        && double.IsFinite(CastCount) && CastCount >= 0;
}

/// <summary>技能施放结算事件：技能 ID、时刻、伤害/治疗量与命中暴击标记。</summary>
public sealed record CombatSkillEvent(string AbilityId, double AtSeconds, double Damage, double Healing, bool Hit, bool Critical,
    string? TargetId = null);
/// <summary>技能推进结果：更新后的调度状态与本段产生的事件。</summary>
public sealed record CombatSkillAdvanceResult(PlayerCombatSkillState State, IReadOnlyList<CombatSkillEvent> Events);

/// <summary>多敌战斗技能调度；宿主在普通攻击、技能完成、回复等最近边界推进，禁止跨过尚未处理的技能边界。</summary>
public static class CombatSkillEngine
{
    /// <summary>距离下一个必须结算的技能边界（吟唱完成或最早冷却到期）的秒数。</summary>
    /// <param name="state">当前技能调度状态。</param>
    /// <returns>到最近结算边界的秒数；无待结算项时返回 +∞。</returns>
    public static double NextBoundarySeconds(PlayerCombatSkillState state)
        => state.CastingAbilityId is not null ? state.CastRemainingSeconds
            : state.Cooldowns.Select(value => value.RemainingSeconds).DefaultIfEmpty(double.PositiveInfinity).Min();

    /// <summary>校验功法/神通配表的可执行性：必须具备有效效果、正施放周期、合法伤害类别与目标类型。</summary>
    /// <param name="ability">功法配表 JSON；非法时抛出 InvalidArgument。</param>
    public static void ValidateAbility(JsonElement ability)
    {
        var healing = Text(ability, "effectType") == "heal";
        var ratio = Number(ability, healing ? "healingRatio" : "damageRatio");
        var flat = Number(ability, healing ? "flatHealing" : "flatDamage");
        var cast = Number(ability, "castMilliseconds");
        var cooldown = Number(ability, "cooldownMilliseconds");
        var target = Text(ability, "targetType");
        var supportive = target is "self" or "lowestHitpointsAlly" or "allAllies";
        var hasEffect = ratio + flat > 0 || healing || supportive && (Number(ability, "threatGain") > 0
            || Number(ability, "threatRatio") > 0 || ability.TryGetProperty("buff", out _));
        if (ratio < 0 || flat < 0 || !hasEffect || cast < 0 || cooldown < 0 || cast + cooldown <= 0
            || Number(ability, "manaCost") < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能未配置可执行效果或有效施放周期。");
        if (Text(ability, "category") is not ("physical" or "spell")
            || !Enum.TryParse<CombatElement>(Text(ability, "element") ?? "none", true, out _))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能伤害类别或五行属性无效。");
        // discipline 划分内功心法（internal）与神通术法（spell）；缺省视为术法。
        if (Text(ability, "discipline") is not (null or "internal" or "spell"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "功法类别（discipline）必须为 internal 或 spell。");
        if (healing ? target is not (null or "self" or "lowestHitpointsAlly" or "allAllies")
            : target is not (null or "targetEnemy" or "allEnemies" or "self" or "allAllies"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能目标没有可执行效果。");
        if (ability.TryGetProperty("defaultTriggers", out var triggers))
            foreach (var trigger in triggers.EnumerateArray()) ValidateTrigger(trigger);
    }

    /// <summary>校验单条技能触发条件配表：依赖目标、条件字段、比较符与阈值必须合法自洽。</summary>
    /// <param name="trigger">触发条件 JSON；非法时抛出 InvalidArgument。</param>
    public static void ValidateTrigger(JsonElement trigger)
    {
        var dependency = Text(trigger, "dependency");
        if (dependency is not ("self" or "target-enemy" or "ally-lowest-hp" or "ally-highest-hp" or "enemy-lowest-hp" or "enemy-highest-hp"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能触发目标无效。");
        var condition = Text(trigger, "condition");
        var self = IsSelf(dependency);
        if (condition is not ("current-hp" or "missing-hp"
            or "current-hp-percentage" or "missing-hp-percentage" or "current-mana" or "missing-mana"
            or "current-mana-percentage" or "missing-mana-percentage" or "buff-stacks")
            || (!self && condition is ("current-mana" or "missing-mana" or "current-mana-percentage" or "missing-mana-percentage" or "buff-stacks")))
            throw new DomainException(DomainErrorCode.InvalidArgument, "当前战斗没有该触发条件对应的状态。");
        if (Text(trigger, "comparator") is not ("greater-than-equal" or "less-than-equal" or "greater-than" or "less-than"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能触发比较方式无效。");
        if (Number(trigger, "value") < 0 || (condition == "buff-stacks" && string.IsNullOrWhiteSpace(Text(trigger, "buffId"))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能触发阈值无效。");
    }

    /// <summary>非战斗时间也扣减冷却，离开战斗取消吟唱；已支付的灵力不退还。</summary>
    public static PlayerCombatSkillState AdvanceIdle(PlayerCombatSkillState state, double seconds)
        => Tick(state, seconds) with { CastingAbilityId = null, CastRemainingSeconds = 0 };

    /// <summary>推进技能调度：先结算已完成吟唱，再按装配槽位与触发条件尝试自动施放。</summary>
    /// <param name="player">施放方聚合（灵力、Buff 等就地变更）。</param>
    /// <param name="catalog">全局配表（技能定义来源）。</param>
    /// <param name="encounter">当前遭遇战（技能作用对象）。</param>
    /// <param name="attributes">当前属性面板（技能数值折算依据）。</param>
    /// <param name="state">推进前技能调度状态。</param>
    /// <param name="seconds">本次推进秒数；不得超过 NextBoundarySeconds，跨边界按违约快速失败。</param>
    /// <returns>更新后的技能状态与本段技能事件。</returns>
    public static CombatSkillAdvanceResult Advance(PlayerAggregate player, GameDataCatalog catalog,
        CombatEncounterState encounter, PlayerAttributes attributes, PlayerCombatSkillState state, double seconds)
    {
        if (seconds > NextBoundarySeconds(state) + 1e-9)
            throw new DomainException(DomainErrorCode.InvariantViolation, "技能调度跨过未结算边界。");
        state = Tick(state, seconds);
        if (encounter.Outcome != CombatOutcome.Running)
            return new(state with { CastingAbilityId = null, CastRemainingSeconds = 0 }, []);
        List<CombatSkillEvent> events = [];
        if (state.CastingAbilityId is not null && state.CastRemainingSeconds <= 1e-9)
            state = Complete(player, catalog, encounter, attributes, state, events);
        if (state.CastingAbilityId is null && encounter.Outcome == CombatOutcome.Running)
        {
            foreach (var slot in player.Loadout.AbilitySlots)
            {
                if (slot.AbilityId is null || state.Cooldowns.Any(value => value.AbilityId == slot.AbilityId)) continue;
                var ability = catalog.Abilities[slot.AbilityId];
                var mana = Number(ability, "manaCost");
                if (player.Combat.Manapoints < mana) continue;
                var triggers = slot.Triggers.Count == 0
                    ? ability.GetProperty("defaultTriggers").EnumerateArray().ToArray() : slot.Triggers;
                if (!triggers.All(trigger => Matches(trigger, player, encounter))) continue;
                player.Combat.Manapoints -= mana;
                player.MarkDirty(PlayerSectionKind.Combat);
                // 行动速度成长（ATR）按乘区缩短吟唱时间。
                var castSeconds = Number(ability, "castMilliseconds") / 1000 * attributes.CastTimeFactor;
                state = state with { CastingAbilityId = slot.AbilityId, CastRemainingSeconds = castSeconds };
                if (state.CastRemainingSeconds <= 1e-9)
                    state = Complete(player, catalog, encounter, attributes, state, events, slot.Level);
                break;
            }
        }
        return new(state, events);
    }

    private static PlayerCombatSkillState Complete(PlayerAggregate player, GameDataCatalog catalog,
        CombatEncounterState encounter, PlayerAttributes attributes, PlayerCombatSkillState state, List<CombatSkillEvent> events,
        double? slotLevel = null)
    {
        var id = state.CastingAbilityId!;
        var ability = catalog.Abilities[id];
        var level = slotLevel ?? player.Loadout.AbilitySlots.FirstOrDefault(slot => slot.AbilityId == id)?.Level
            ?? player.AbilityLevelFor(new Domain.Common.AbilityId(id));
        // 等级成长：伤害/治疗倍率每级 +0.6%，固定数值每级 +1.0%。
        var ratioBonus = 1 + (level - 1) * 0.006;
        var flatBonus = 1 + (level - 1) * 0.01;
        var spell = Text(ability, "category") == "spell";
        var attack = spell ? attributes.SpellAttack : attributes.PhysicalAttack;
        var target = Text(ability, "targetType");
        if (Text(ability, "effectType") == "heal")
        {
            var healing = Math.Min(encounter.Player.Stats.MaxHitpoints - encounter.Player.Hitpoints,
                attack * attributes.SkillIntensity * Number(ability, "healingRatio") * ratioBonus
                + Number(ability, "flatHealing") * flatBonus);
            encounter.Player.Hitpoints += healing;
            player.Combat.Hitpoints = encounter.Player.Hitpoints;
            events.Add(new(id, encounter.ElapsedSeconds, 0, healing, true, false, "player"));
        }
        else if (target is "self" or "allAllies"
            && Number(ability, "damageRatio") + Number(ability, "flatDamage") <= 0)
        {
            ApplySupport(player, encounter, ability, id, events);
        }
        else
        {
            var stats = attributes.ToCombatant() with
            {
                AttackPower = attack * attributes.SkillIntensity * Number(ability, "damageRatio") * ratioBonus
                    + Number(ability, "flatDamage") * flatBonus,
                BasicAttackRatio = 1,
                DamageKind = spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
                Element = Enum.Parse<CombatElement>(Text(ability, "element") ?? "none", true)
            };
            IReadOnlyList<CombatantState> targets;
            var maxTargets = ability.TryGetProperty("maxTargets", out var maxTargetsProperty)
                && maxTargetsProperty.TryGetInt32(out var parsed) ? parsed : 0;
            if (target == "allEnemies")
                targets = maxTargets > 0
                    ? DeterministicCombatEngine.SelectEnemyTargets(encounter, maxTargets, state.CastCount, 8)
                    : encounter.Enemies.Where(enemy => enemy.IsAlive).ToArray();
            else
            {
                var picked = DeterministicCombatEngine.SelectEnemyTarget(encounter, state.CastCount, 4);
                targets = picked is null ? [] : [picked];
            }
            foreach (var enemy in targets)
            {
                var hit = DeterministicCombatEngine.ResolveAbilityAttack(encounter, enemy, stats, state.CastCount, id);
                events.Add(new(id, encounter.ElapsedSeconds, hit.Damage, 0, hit.Hit, hit.Critical, enemy.Id));
            }
        }
        player.MarkDirty(PlayerSectionKind.Combat);
        var cooldowns = state.Cooldowns.Where(value => value.AbilityId != id).ToList();
        // 冷却缩减（CDR）按乘区缩短技能 CD。
        var cooldown = Number(ability, "cooldownMilliseconds") / 1000 * attributes.CooldownFactor;
        if (cooldown > 0) cooldowns.Add(new(id, cooldown));
        return new(cooldowns, null, 0, state.CastCount + 1);
    }

    /// <summary>支援类技能（嘲讽/全体增益）：不造成伤害，按配表授予威胁或战内 Buff。</summary>
    private static void ApplySupport(PlayerAggregate player, CombatEncounterState encounter, JsonElement ability, string id, List<CombatSkillEvent> events)
    {
        var threatGain = Number(ability, "threatGain");
        var threatRatio = Number(ability, "threatRatio");
        var applied = false;
        if (threatGain > 0) { encounter.Player.Threat += threatGain; applied = true; }
        if (threatRatio > 0) { encounter.Player.ThreatRatioBonus += threatRatio; applied = true; }
        if (ability.TryGetProperty("buff", out var buff) && buff.ValueKind == JsonValueKind.Object)
        {
            var buffId = Text(buff, "buffId") ?? $"ability-buff:{id}";
            var duration = Number(buff, "durationSeconds");
            var at = player.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;
            player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
            player.Buffs.Active.Add(new(buffId, at + Math.Max(1, duration), 1,
                Text(buff, "name") ?? id, null, Number(buff, "hpRegen"), Number(buff, "mpRegen"),
                Number(buff, "attackSpeedRatio"), Number(buff, "damageAbsorptionRatio")));
            player.MarkDirty(PlayerSectionKind.Buffs);
            applied = true;
        }
        if (!applied)
            throw new DomainException(DomainErrorCode.InvalidArgument, "支援技能未配置可执行效果。");
        events.Add(new(id, encounter.ElapsedSeconds, 0, 0, true, false, "player"));
    }

    private static PlayerCombatSkillState Tick(PlayerCombatSkillState state, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能推进时间无效。");
        return state with
        {
            Cooldowns = state.Cooldowns.Where(value => value.RemainingSeconds > seconds + 1e-9)
                .Select(value => value with { RemainingSeconds = value.RemainingSeconds - seconds }).ToArray(),
            CastRemainingSeconds = Math.Max(0, state.CastRemainingSeconds - seconds)
        };
    }

    private static bool Matches(JsonElement trigger, PlayerAggregate player, CombatEncounterState encounter)
    {
        var self = IsSelf(Text(trigger, "dependency"));
        var enemy = encounter.PrimaryEnemy;
        var unit = self || enemy is null ? encounter.Player : enemy;
        var hp = unit.Hitpoints;
        var maxHp = unit.Stats.MaxHitpoints;
        var mana = player.Combat.Manapoints;
        var maxMana = player.Combat.MaxManapoints;
        var value = Text(trigger, "condition") switch
        {
            "current-hp" => hp,
            "missing-hp" => maxHp - hp,
            "current-hp-percentage" => hp / maxHp * 100,
            "missing-hp-percentage" => (maxHp - hp) / maxHp * 100,
            "current-mana" => mana,
            "missing-mana" => maxMana - mana,
            "current-mana-percentage" => mana / maxMana * 100,
            "missing-mana-percentage" => (maxMana - mana) / maxMana * 100,
            "buff-stacks" => player.Buffs.Active.Where(buff => buff.BuffId == Text(trigger, "buffId")).Sum(buff => buff.Stacks),
            _ => throw new DomainException(DomainErrorCode.InvariantViolation, "装配包含不支持的技能触发条件。")
        };
        var threshold = Number(trigger, "value");
        return Text(trigger, "comparator") switch
        {
            "greater-than-equal" => value >= threshold,
            "less-than-equal" => value <= threshold,
            "greater-than" => value > threshold,
            "less-than" => value < threshold,
            _ => throw new DomainException(DomainErrorCode.InvariantViolation, "装配包含不支持的技能比较方式。")
        };
    }

    private static bool IsSelf(string? dependency) => dependency is "self" or "ally-lowest-hp" or "ally-highest-hp";
    private static string? Text(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;
    private static double Number(JsonElement value, string key)
    {
        var number = value.TryGetProperty(key, out var property) ? property.GetDouble() : 0;
        if (!double.IsFinite(number)) throw new DomainException(DomainErrorCode.InvalidArgument, "技能配表数值必须有限。");
        return number;
    }
}
