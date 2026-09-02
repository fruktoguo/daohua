namespace Domain.Combat;

public enum CombatDamageKind : byte
{
    Physical = 0,
    Spell = 1
}

public enum CombatOutcome : byte
{
    Running = 0,
    PlayerWon = 1,
    EnemyWon = 2,
    Draw = 3
}

public sealed record CombatantStats(
    double MaxHitpoints,
    double AttackPower,
    double Defense,
    double AttackIntervalSeconds,
    double Accuracy = 1,
    double Evasion = 0,
    CombatDamageKind DamageKind = CombatDamageKind.Physical)
{
    public static CombatantStats Create(
        double maxHitpoints,
        double attackPower,
        double defense,
        double attackIntervalSeconds,
        double accuracy = 1,
        double evasion = 0,
        CombatDamageKind damageKind = CombatDamageKind.Physical)
    {
        if (!double.IsFinite(maxHitpoints) || maxHitpoints <= 0 || !double.IsFinite(attackPower) || attackPower < 0 ||
            !double.IsFinite(defense) || defense < 0 || !double.IsFinite(attackIntervalSeconds) || attackIntervalSeconds <= 0 ||
            !double.IsFinite(accuracy) || accuracy < 0 || !double.IsFinite(evasion) || evasion < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗属性无效。");
        return new CombatantStats(maxHitpoints, attackPower, defense, attackIntervalSeconds, accuracy, evasion, damageKind);
    }
}

public sealed class CombatantState
{
    public CombatantState(string id, CombatantStats stats)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new DomainException(DomainErrorCode.InvalidArgument, "战斗单位标识无效。");
        Id = id;
        Stats = stats ?? throw new ArgumentNullException(nameof(stats));
        Hitpoints = stats.MaxHitpoints;
    }

    public string Id { get; }
    public CombatantStats Stats { get; }
    public double Hitpoints { get; internal set; }
    public double NextAttackAtSeconds { get; internal set; }
    public double AttackCount { get; internal set; }
    public bool IsAlive => Hitpoints > 1e-12;
}

public sealed class CombatEncounterState
{
    private CombatEncounterState(string encounterId, long seed, CombatantState player, CombatantState enemy)
    {
        EncounterId = encounterId;
        Seed = seed;
        Player = player;
        Enemy = enemy;
    }

    public string EncounterId { get; }
    public long Seed { get; }
    public double ElapsedSeconds { get; internal set; }
    public CombatantState Player { get; }
    public CombatantState Enemy { get; }
    public CombatOutcome Outcome { get; internal set; } = CombatOutcome.Running;
    public double Sequence { get; internal set; }

    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy)
    {
        if (string.IsNullOrWhiteSpace(encounterId) || encounterId.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇标识无效。");
        if (player is null || enemy is null) throw new ArgumentNullException(player is null ? nameof(player) : nameof(enemy));
        return new CombatEncounterState(encounterId, seed, new CombatantState("player", player), new CombatantState("enemy", enemy));
    }
}

public sealed record CombatEvent(
    double AtSeconds,
    string AttackerId,
    string TargetId,
    double Damage,
    bool Hit,
    CombatDamageKind DamageKind,
    double TargetHitpointsAfter);

public sealed record CombatAdvanceResult(
    string EncounterId,
    double FromSeconds,
    double ToSeconds,
    CombatOutcome Outcome,
    IReadOnlyList<CombatEvent> Events,
    double Sequence);

/// <summary>无随机全局状态的确定性战斗推进器；同一输入必得同一事件序列。</summary>
public static class DeterministicCombatEngine
{
    public const int MaxEventsPerAdvance = 100_000;

    public static CombatAdvanceResult AdvanceTo(CombatEncounterState encounter, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        if (!double.IsFinite(targetSeconds) || targetSeconds < encounter.ElapsedSeconds)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗目标时间无效。");
        var from = encounter.ElapsedSeconds;
        List<CombatEvent>? events = null;
        var eventCount = 0;
        while (encounter.Outcome == CombatOutcome.Running && encounter.ElapsedSeconds < targetSeconds - 1e-12)
        {
            if (++eventCount > MaxEventsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗推进超过最大事件数。");
            var next = Math.Min(encounter.Player.NextAttackAtSeconds, encounter.Enemy.NextAttackAtSeconds);
            if (!double.IsFinite(next)) throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
            if (next > targetSeconds)
            {
                encounter.ElapsedSeconds = targetSeconds;
                break;
            }
            encounter.ElapsedSeconds = Math.Max(encounter.ElapsedSeconds, next);
            if (encounter.Player.IsAlive && encounter.Player.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
            {
                ResolveAttack(encounter.Player, encounter.Enemy, encounter.ElapsedSeconds, ref events);
                encounter.Player.NextAttackAtSeconds = encounter.ElapsedSeconds + encounter.Player.Stats.AttackIntervalSeconds;
            }
            if (encounter.Enemy.IsAlive && encounter.Enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
            {
                ResolveAttack(encounter.Enemy, encounter.Player, encounter.ElapsedSeconds, ref events);
                encounter.Enemy.NextAttackAtSeconds = encounter.ElapsedSeconds + encounter.Enemy.Stats.AttackIntervalSeconds;
            }
            encounter.Sequence += 1;
            UpdateOutcome(encounter);
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome, (IReadOnlyList<CombatEvent>?)events ?? [], encounter.Sequence);
    }

    private static void ResolveAttack(CombatantState attacker, CombatantState target, double at, ref List<CombatEvent>? events)
    {
        if (!attacker.IsAlive || !target.IsAlive) return;
        var hit = attacker.Stats.Accuracy + 1e-12 >= target.Stats.Evasion;
        var mitigation = 100d / (100d + target.Stats.Defense);
        var damage = hit ? Math.Max(0, attacker.Stats.AttackPower * mitigation) : 0;
        target.Hitpoints = Math.Max(0, target.Hitpoints - damage);
        attacker.AttackCount += 1;
        events ??= new List<CombatEvent>();
        events.Add(new CombatEvent(at, attacker.Id, target.Id, damage, hit, attacker.Stats.DamageKind, target.Hitpoints));
    }

    private static void UpdateOutcome(CombatEncounterState encounter)
    {
        encounter.Outcome = (encounter.Player.IsAlive, encounter.Enemy.IsAlive) switch
        {
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }
}
