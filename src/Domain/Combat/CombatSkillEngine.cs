using System.Text.Json;

namespace Domain.Combat;

/// <summary>战斗技能冷却 的领域定义。</summary>
public sealed record CombatSkillCooldown(string AbilityId, double RemainingSeconds);
/// <summary>玩家战斗技能状态 的领域定义。</summary>
public sealed record PlayerCombatSkillState(
    IReadOnlyList<CombatSkillCooldown> Cooldowns, string? CastingAbilityId,
    double CastRemainingSeconds, double CastCount)
{
    /// <summary>获取或设置 为空 的值。</summary>
    public static PlayerCombatSkillState Empty { get; } = new([], null, 0, 0);

    /// <summary>获取或设置 是否有效 的值。</summary>
    public bool IsValid => Cooldowns is not null && Cooldowns.All(value => !string.IsNullOrWhiteSpace(value.AbilityId)
        && double.IsFinite(value.RemainingSeconds) && value.RemainingSeconds > 0)
        && Cooldowns.Select(value => value.AbilityId).Distinct(StringComparer.Ordinal).Count() == Cooldowns.Count
        && double.IsFinite(CastRemainingSeconds) && CastRemainingSeconds >= 0
        && (CastingAbilityId is null ? CastRemainingSeconds == 0 : !string.IsNullOrWhiteSpace(CastingAbilityId))
        && double.IsFinite(CastCount) && CastCount >= 0;
}

/// <summary>战斗技能事件 的领域定义。</summary>
public sealed record CombatSkillEvent(string AbilityId, double AtSeconds, double Damage, double Healing, bool Hit, bool Critical);
/// <summary>战斗技能推进结果 的领域定义。</summary>
public sealed record CombatSkillAdvanceResult(PlayerCombatSkillState State, IReadOnlyList<CombatSkillEvent> Events);

/// <summary>单敌战斗技能调度；宿主在普通攻击、技能完成、回复等最近边界推进，禁止跨过尚未处理的技能边界。</summary>
public static class CombatSkillEngine
{
    /// <summary>
    /// 执行 下一次Boundary秒 操作。
    /// </summary>
    /// <param name="state">state 参数。</param>
    /// <returns>返回处理结果。</returns>
    public static double NextBoundarySeconds(PlayerCombatSkillState state)
        => state.CastingAbilityId is not null ? state.CastRemainingSeconds
            : state.Cooldowns.Select(value => value.RemainingSeconds).DefaultIfEmpty(double.PositiveInfinity).Min();

    /// <summary>
    /// 执行 校验功法/神通 操作。
    /// </summary>
    /// <param name="ability">ability 参数。</param>
    public static void ValidateAbility(JsonElement ability)
    {
        var healing = Text(ability, "effectType") == "heal";
        var ratio = Number(ability, healing ? "healingRatio" : "damageRatio");
        var flat = Number(ability, healing ? "flatHealing" : "flatDamage");
        var cast = Number(ability, "castMilliseconds");
        var cooldown = Number(ability, "cooldownMilliseconds");
        if (ratio < 0 || flat < 0 || ratio + flat <= 0 || cast < 0 || cooldown < 0 || cast + cooldown <= 0
            || Number(ability, "manaCostRatio") < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能未配置可执行效果或有效施放周期。");
        if (Text(ability, "category") is not ("physical" or "spell")
            || !Enum.TryParse<CombatElement>(Text(ability, "element") ?? "none", true, out _))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能伤害类别或五行属性无效。");
        var target = Text(ability, "targetType");
        if (healing ? target is not (null or "self" or "lowestHitpointsAlly" or "allAllies")
            : target is not (null or "targetEnemy" or "allEnemies"))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能目标没有可执行效果。");
        if (ability.TryGetProperty("defaultTriggers", out var triggers))
            foreach (var trigger in triggers.EnumerateArray()) ValidateTrigger(trigger);
    }

    /// <summary>
    /// 执行 校验Trigger 操作。
    /// </summary>
    /// <param name="trigger">trigger 参数。</param>
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

    /// <summary>
    /// 执行 推进 操作。
    /// </summary>
    /// <param name="player">player 参数。</param>
    /// <param name="catalog">catalog 参数。</param>
    /// <param name="encounter">encounter 参数。</param>
    /// <param name="attributes">attributes 参数。</param>
    /// <param name="state">state 参数。</param>
    /// <param name="seconds">seconds 参数。</param>
    /// <returns>返回处理结果。</returns>
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
                var mana = Number(ability, "manaCostRatio") * 10;
                if (player.Combat.Manapoints < mana) continue;
                var triggers = slot.Triggers.Count == 0
                    ? ability.GetProperty("defaultTriggers").EnumerateArray().ToArray() : slot.Triggers;
                if (!triggers.All(trigger => Matches(trigger, player, encounter))) continue;
                player.Combat.Manapoints -= mana;
                player.MarkDirty(PlayerSectionKind.Combat);
                state = state with { CastingAbilityId = slot.AbilityId, CastRemainingSeconds = Number(ability, "castMilliseconds") / 1000 };
                if (state.CastRemainingSeconds == 0)
                    state = Complete(player, catalog, encounter, attributes, state, events);
                break;
            }
        }
        return new(state, events);
    }

    private static PlayerCombatSkillState Complete(PlayerAggregate player, GameDataCatalog catalog,
        CombatEncounterState encounter, PlayerAttributes attributes, PlayerCombatSkillState state, List<CombatSkillEvent> events)
    {
        var id = state.CastingAbilityId!;
        var ability = catalog.Abilities[id];
        var spell = Text(ability, "category") == "spell";
        var attack = spell ? attributes.SpellAttack : attributes.PhysicalAttack;
        if (Text(ability, "effectType") == "heal")
        {
            var healing = Math.Min(encounter.Player.Stats.MaxHitpoints - encounter.Player.Hitpoints,
                attack * Number(ability, "healingRatio") + Number(ability, "flatHealing"));
            encounter.Player.Hitpoints += healing;
            player.Combat.Hitpoints = encounter.Player.Hitpoints;
            events.Add(new(id, encounter.ElapsedSeconds, 0, healing, true, false));
        }
        else
        {
            var stats = attributes.ToCombatant() with
            {
                AttackPower = attack * Number(ability, "damageRatio") + Number(ability, "flatDamage"),
                DamageKind = spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
                Element = Enum.Parse<CombatElement>(Text(ability, "element") ?? "none", true)
            };
            var hit = DeterministicCombatEngine.ResolveAbilityAttack(encounter, stats, state.CastCount);
            events.Add(new(id, encounter.ElapsedSeconds, hit.Damage, 0, hit.Hit, hit.Critical));
        }
        player.MarkDirty(PlayerSectionKind.Combat);
        var cooldowns = state.Cooldowns.Where(value => value.AbilityId != id).ToList();
        var cooldown = Number(ability, "cooldownMilliseconds") / 1000;
        if (cooldown > 0) cooldowns.Add(new(id, cooldown));
        return new(cooldowns, null, 0, state.CastCount + 1);
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
        var unit = self ? encounter.Player : encounter.Enemy;
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
