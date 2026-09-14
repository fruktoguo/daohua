namespace Domain.Combat;

/// <summary>战斗伤害类型划分</summary>
public enum CombatDamageKind : byte
{
    /// <summary>肉身物理打击</summary>
    Physical = 0,
    /// <summary>玄门道法神通打击</summary>
    Spell = 1
}

/// <summary>攻击携带的五行属性；无属性攻击跳过五行减免。</summary>
public enum CombatElement : byte { None, Metal, Wood, Water, Fire, Earth }

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
    public double MagicResistance { get; init; }
    public double ResistanceScale { get; init; } = 1;
    public double PhysicalPenetrationPercent { get; init; }
    public double MagicPenetrationPercent { get; init; }
    public double Critical { get; init; }
    public double CriticalResistance { get; init; }
    public double CriticalMultiplier { get; init; } = 1.5;
    public double BreakPower { get; init; }
    public double ParryResolve { get; init; }
    public CombatElement Element { get; init; }
    public double MetalResistance { get; init; }
    public double WoodResistance { get; init; }
    public double WaterResistance { get; init; }
    public double FireResistance { get; init; }
    public double EarthResistance { get; init; }

    internal void Validate()
    {
        double[] nonnegative = [AttackPower, Defense, Accuracy, Evasion, MagicResistance,
            Critical, CriticalResistance, BreakPower, ParryResolve, MetalResistance,
            WoodResistance, WaterResistance, FireResistance, EarthResistance];
        if (nonnegative.Any(value => !double.IsFinite(value) || value < 0)
            || !double.IsFinite(MaxHitpoints) || MaxHitpoints <= 0
            || !double.IsFinite(AttackIntervalSeconds) || AttackIntervalSeconds <= 0
            || !double.IsFinite(ResistanceScale) || ResistanceScale <= 0
            || !double.IsFinite(CriticalMultiplier) || CriticalMultiplier < 1
            || !double.IsFinite(PhysicalPenetrationPercent) || PhysicalPenetrationPercent is < 0 or > 100
            || !double.IsFinite(MagicPenetrationPercent) || MagicPenetrationPercent is < 0 or > 100
            || !Enum.IsDefined(Element) || !Enum.IsDefined(DamageKind))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗属性无效。");
    }

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
        stats.Validate();
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

    /// <summary>从持久化标量恢复一场遭遇战。</summary>
    public static CombatEncounterState Restore(
        string encounterId,
        long seed,
        CombatantStats player,
        CombatantStats enemy,
        double elapsedSeconds,
        double playerHitpoints,
        double enemyHitpoints,
        double playerNextAttackAtSeconds,
        double enemyNextAttackAtSeconds,
        double playerAttackCount,
        double enemyAttackCount,
        CombatOutcome outcome,
        double sequence = 0)
    {
        var encounter = Start(encounterId, seed, player, enemy);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0
            || !double.IsFinite(playerHitpoints) || playerHitpoints < 0 || playerHitpoints > player.MaxHitpoints
            || !double.IsFinite(enemyHitpoints) || enemyHitpoints < 0 || enemyHitpoints > enemy.MaxHitpoints
            || !double.IsFinite(playerNextAttackAtSeconds) || playerNextAttackAtSeconds < 0
            || !double.IsFinite(enemyNextAttackAtSeconds) || enemyNextAttackAtSeconds < 0
            || !double.IsFinite(playerAttackCount) || playerAttackCount < 0
            || !double.IsFinite(enemyAttackCount) || enemyAttackCount < 0
            || !double.IsFinite(sequence) || sequence < 0
            || !Enum.IsDefined(outcome))
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗状态无效。");
        encounter.ElapsedSeconds = elapsedSeconds;
        encounter.Player.Hitpoints = playerHitpoints;
        encounter.Enemy.Hitpoints = enemyHitpoints;
        encounter.Player.NextAttackAtSeconds = playerNextAttackAtSeconds;
        encounter.Enemy.NextAttackAtSeconds = enemyNextAttackAtSeconds;
        encounter.Player.AttackCount = playerAttackCount;
        encounter.Enemy.AttackCount = enemyAttackCount;
        encounter.Outcome = outcome;
        encounter.Sequence = sequence;
        var expectedOutcome = (encounter.Player.IsAlive, encounter.Enemy.IsAlive) switch
        {
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
        if (outcome != expectedOutcome)
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗结果与生命状态不一致。");
        return encounter;
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
    double TargetHitpointsAfter)
{
    public bool Critical { get; init; }
    public bool Break { get; init; }
    public bool Parried { get; init; }
}

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

/// <summary>妖兽试炼远征阶段；状态会随行动队列一同持久化。</summary>
public enum CombatExpeditionPhase : byte
{
    /// <summary>首次搜寻遭遇目标</summary>
    InitialSearch = 0,
    /// <summary>击败上一场敌人后搜寻下一场遭遇</summary>
    Search = 1,
    /// <summary>与当前敌人交战</summary>
    Combat = 2,
    Respawning = 3
}

/// <summary>
/// 可持久化的妖兽试炼远征状态。
/// 只保存确定性恢复所需的标量，不保存 CombatEncounterState 引用。
/// </summary>
public sealed record CombatExpeditionState(
    string EncounterId,
    CombatExpeditionPhase Phase,
    double PhaseElapsedSeconds,
    double PhaseDurationSeconds,
    long Seed,
    string? EnemyId,
    double BattleElapsedSeconds,
    double PlayerHitpoints,
    double EnemyHitpoints,
    double PlayerNextAttackAtSeconds,
    double EnemyNextAttackAtSeconds,
    double PlayerAttackCount,
    double EnemyAttackCount,
    CombatOutcome Outcome,
    int WaveNumber = 1,
    IReadOnlyList<string>? WaveEnemyIds = null)
{
    /// <summary>校验远征状态是否满足持久化不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(EncounterId) && EncounterId.Length <= 128
        && Enum.IsDefined(Phase)
        && double.IsFinite(PhaseElapsedSeconds) && PhaseElapsedSeconds >= 0
        && double.IsFinite(PhaseDurationSeconds) && (Phase == CombatExpeditionPhase.Combat ? PhaseDurationSeconds >= 0 : PhaseDurationSeconds > 0)
        && (Phase == CombatExpeditionPhase.Combat || PhaseElapsedSeconds <= PhaseDurationSeconds + 1e-9)
        && double.IsFinite(BattleElapsedSeconds) && BattleElapsedSeconds >= 0
        && double.IsFinite(PlayerHitpoints) && PlayerHitpoints >= 0
        && double.IsFinite(EnemyHitpoints) && EnemyHitpoints >= 0
        && double.IsFinite(PlayerNextAttackAtSeconds) && PlayerNextAttackAtSeconds >= 0
        && double.IsFinite(EnemyNextAttackAtSeconds) && EnemyNextAttackAtSeconds >= 0
        && double.IsFinite(PlayerAttackCount) && PlayerAttackCount >= 0
        && double.IsFinite(EnemyAttackCount) && EnemyAttackCount >= 0
        && Enum.IsDefined(Outcome)
        && (EnemyId is null || (EnemyId.Length > 0 && EnemyId.Length <= 128))
        && WaveNumber >= 1
        && (WaveEnemyIds is null || (WaveEnemyIds.Count <= 4 && WaveEnemyIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128)))
        && (Phase is CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search or CombatExpeditionPhase.Respawning
            ? Outcome == CombatOutcome.Running && EnemyId is null
            : EnemyId is not null && PhaseElapsedSeconds <= 1e-9);
}

/// <summary>
/// 无外部随机副作用的确定性战斗推进引擎。
/// 相同的初始属性与时间推进必得完全一致的伤害和事件序列，供客户端预测回放与服务端权威核验。
/// </summary>
public static class DeterministicCombatEngine
{
    /// <summary>技能复用普攻对抗与减伤公式，独立施放计数不改变普通攻击随机流。</summary>
    public static CombatEvent ResolveAbilityAttack(CombatEncounterState encounter, CombatantStats stats, double castCount)
    {
        var caster = new CombatantState("player", stats) { Hitpoints = encounter.Player.Hitpoints, AttackCount = castCount };
        List<CombatEvent>? events = null;
        ResolveAttack(caster, encounter.Enemy, encounter.Seed, 2, encounter.ElapsedSeconds, ref events);
        UpdateOutcome(encounter);
        return events![0];
    }

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
                ResolveAttack(encounter.Player, encounter.Enemy, encounter.Seed, 0, encounter.ElapsedSeconds, ref events);
                encounter.Player.NextAttackAtSeconds = encounter.ElapsedSeconds + encounter.Player.Stats.AttackIntervalSeconds;
            }
            if (encounter.Enemy.IsAlive && encounter.Enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
            {
                ResolveAttack(encounter.Enemy, encounter.Player, encounter.Seed, 1, encounter.ElapsedSeconds, ref events);
                encounter.Enemy.NextAttackAtSeconds = encounter.ElapsedSeconds + encounter.Enemy.Stats.AttackIntervalSeconds;
            }
            encounter.Sequence += 1;
            UpdateOutcome(encounter);
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome, (IReadOnlyList<CombatEvent>?)events ?? [], encounter.Sequence);
    }

    /// <summary>按破拆招、闪避、暴击和两层抗性顺序结算，抽样仅依赖已持久化的种子与出手序号。</summary>
    private static void ResolveAttack(CombatantState attacker, CombatantState target, long seed, ulong side, double at, ref List<CombatEvent>? events)
    {
        if (!attacker.IsAlive || !target.IsAlive) return;
        var attack = attacker.Stats;
        var defense = target.Stats;
        var contestRoll = Roll(seed, side, attacker.AttackCount, 0);
        var broken = attack.BreakPower > defense.ParryResolve
            && contestRoll < attack.BreakPower / (attack.BreakPower + defense.ParryResolve + 100);
        var parried = defense.ParryResolve > attack.BreakPower
            && contestRoll < defense.ParryResolve / (defense.ParryResolve + attack.BreakPower + 100);
        var accuracy = attack.Accuracy * (broken ? 2 : 1);
        var evasion = defense.Evasion * (parried ? 2 : 1);
        var hit = Roll(seed, side, attacker.AttackCount, 1) >= evasion / (evasion + accuracy + 100);
        var criticalPoints = attack.Critical * (broken ? 2 : 1);
        var critical = hit && Roll(seed, side, attacker.AttackCount, 2)
            < criticalPoints / (criticalPoints + defense.CriticalResistance + 100);
        var resistance = attack.DamageKind == CombatDamageKind.Physical ? defense.Defense : defense.MagicResistance;
        var penetration = attack.DamageKind == CombatDamageKind.Physical ? attack.PhysicalPenetrationPercent : attack.MagicPenetrationPercent;
        resistance *= (parried ? 2 : 1) * (1 - penetration / 100);
        // 等价于 RawDamage² / (RawDamage + RES × K + 1)，先除后乘避免平方中间值溢出。
        var damage = hit ? attack.AttackPower * (attack.AttackPower / (attack.AttackPower + resistance * defense.ResistanceScale + 1)) : 0;
        var elementResistance = attack.Element switch
        {
            CombatElement.Metal => defense.MetalResistance,
            CombatElement.Wood => defense.WoodResistance,
            CombatElement.Water => defense.WaterResistance,
            CombatElement.Fire => defense.FireResistance,
            CombatElement.Earth => defense.EarthResistance,
            _ => 0
        };
        if (attack.Element != CombatElement.None)
            damage *= 1 - elementResistance / (elementResistance + 100 * defense.ResistanceScale);
        if (critical) damage *= attack.CriticalMultiplier;
        target.Hitpoints = Math.Max(0, target.Hitpoints - damage);
        attacker.AttackCount += 1;
        events ??= new List<CombatEvent>();
        events.Add(new CombatEvent(at, attacker.Id, target.Id, damage, hit, attack.DamageKind, target.Hitpoints)
        { Critical = critical, Break = broken, Parried = parried });
    }

    private static double Roll(long seed, ulong side, double attackCount, ulong channel)
    {
        // SplitMix64 混合固定计数器；各判定使用独立通道，切片与恢复不会改变随机流。
        unchecked
        {
            var value = (ulong)seed ^ ((ulong)BitConverter.DoubleToInt64Bits(attackCount) * 0x9E3779B97F4A7C15UL)
                ^ ((side + 1) * 0xD1B54A32D192ED03UL) ^ ((channel + 1) * 0x94D049BB133111EBUL);
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 11) * (1d / 9007199254740992d);
        }
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
