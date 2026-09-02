namespace Domain.Combat;

/// <summary>战斗伤害类型划分</summary>
public enum CombatDamageKind : byte
{
    /// <summary>肉身物理打击</summary>
    Physical = 0,
    /// <summary>玄门道法神通打击</summary>
    Spell = 1
}

/// <summary>战斗最终胜负裁决状态</summary>
public enum CombatOutcome : byte
{
    /// <summary>战斗仍在持续交锋中</summary>
    Running = 0,
    /// <summary>玩家击败敌人获胜</summary>
    PlayerWon = 1,
    /// <summary>敌人击溃玩家获胜</summary>
    EnemyWon = 2,
    /// <summary>双方同归于尽或超时平局</summary>
    Draw = 3
}

/// <summary>
/// 战斗单位静态属性规格。
/// 所有属性计算统一使用 double 精度。
/// </summary>
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

/// <summary>
/// 战斗交锋中的单位动态运行时状态（维护当前气血与下一次出手时刻）。
/// </summary>
public sealed class CombatantState
{
    public CombatantState(string id, CombatantStats stats)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new DomainException(DomainErrorCode.InvalidArgument, "战斗单位标识无效。");
        Id = id;
        Stats = stats ?? throw new ArgumentNullException(nameof(stats));
        Hitpoints = stats.MaxHitpoints;
    }

    /// <summary>战斗者标识（如 player 或 enemy）</summary>
    public string Id { get; }
    /// <summary>战斗静态属性面板</summary>
    public CombatantStats Stats { get; }
    /// <summary>当前剩余气血值</summary>
    public double Hitpoints { get; internal set; }
    /// <summary>下一次普通攻击或出手的预定战斗时间（秒）</summary>
    public double NextAttackAtSeconds { get; internal set; }
    /// <summary>累计出手攻击次数</summary>
    public double AttackCount { get; internal set; }
    /// <summary>气血大于容差即视作存活</summary>
    public bool IsAlive => Hitpoints > 1e-12;
}

/// <summary>
/// 单场战斗遭遇战状态聚合。
/// </summary>
public sealed class CombatEncounterState
{
    private CombatEncounterState(string encounterId, long seed, CombatantState player, CombatantState enemy)
    {
        EncounterId = encounterId;
        Seed = seed;
        Player = player;
        Enemy = enemy;
    }

    /// <summary>遭遇战唯一标识</summary>
    public string EncounterId { get; }
    /// <summary>战斗伪随机数种子（用于命中/暴击等确定性复现）</summary>
    public long Seed { get; }
    /// <summary>当前战斗已推进的总时间（秒）</summary>
    public double ElapsedSeconds { get; internal set; }
    /// <summary>玩家方战斗单位</summary>
    public CombatantState Player { get; }
    /// <summary>敌方对峙单位</summary>
    public CombatantState Enemy { get; }
    /// <summary>当前对决胜负裁决</summary>
    public CombatOutcome Outcome { get; internal set; } = CombatOutcome.Running;
    /// <summary>状态转换序号（递增帧号）</summary>
    public double Sequence { get; internal set; }

    /// <summary>
    /// 初始化开启一场新的战斗遭遇
    /// </summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy)
    {
        if (string.IsNullOrWhiteSpace(encounterId) || encounterId.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇标识无效。");
        if (player is null || enemy is null) throw new ArgumentNullException(player is null ? nameof(player) : nameof(enemy));
        return new CombatEncounterState(encounterId, seed, new CombatantState("player", player), new CombatantState("enemy", enemy));
    }
}

/// <summary>
/// 战斗单次攻击/受创事件明细。
/// </summary>
public sealed record CombatEvent(
    double AtSeconds,
    string AttackerId,
    string TargetId,
    double Damage,
    bool Hit,
    CombatDamageKind DamageKind,
    double TargetHitpointsAfter);

/// <summary>
/// 战斗步进切片结算结果帧。
/// </summary>
public sealed record CombatAdvanceResult(
    string EncounterId,
    double FromSeconds,
    double ToSeconds,
    CombatOutcome Outcome,
    IReadOnlyList<CombatEvent> Events,
    double Sequence);

/// <summary>
/// 无外部随机副作用的确定性战斗推进引擎。
/// 相同的初始属性与时间推进必得完全一致的伤害和事件序列，供客户端预测回放与服务端权威核验。
/// </summary>
public static class DeterministicCombatEngine
{
    /// <summary>单次推进最多产出的战斗事件上限，防止攻速异常引发死循环</summary>
    public const int MaxEventsPerAdvance = 100_000;

    /// <summary>
    /// 将战斗状态推进至指定的 targetSeconds 目标时刻。
    /// </summary>
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

    /// <summary>
    /// 结算单次普攻动作。
    /// 命中对抗：Accuracy >= Evasion 时命中。
    /// 减伤公式：标准双曲减伤 100 / (100 + Defense)。
    /// </summary>
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

    /// <summary>
    /// 根据交战双方存活状态更新战局结果。
    /// </summary>
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

