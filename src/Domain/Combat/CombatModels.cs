using System.Text.Json;

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
public enum CombatElement : byte
{
    /// <summary>无属性。</summary>
    None,
    /// <summary>金属性。</summary>
    Metal,
    /// <summary>木属性。</summary>
    Wood,
    /// <summary>水属性。</summary>
    Water,
    /// <summary>火属性。</summary>
    Fire,
    /// <summary>土属性。</summary>
    Earth
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
/// 战斗单位静态属性规格；玩家与怪物共用同一套同构属性池。
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
    /// <summary>术法攻击（术法技能结算共用；普攻永远是 100% 物理攻击力）。</summary>
    public double SpellAttack { get; init; }
    /// <summary>灵力上限；敌方技能耗蓝与玩家吟唱共用同构资源池。</summary>
    public double MaxManapoints { get; init; }
    /// <summary>普通攻击伤害倍率（全单位统一成长曲线 100%→200% AD）。</summary>
    public double BasicAttackRatio { get; init; } = 1;
    /// <summary>技能吟唱时长乘区（行动速度 ATR）：吟唱时间 = castMilliseconds × 本系数，与攻击速度无关。</summary>
    public double CastTimeFactor { get; init; } = 1;
    /// <summary>技能冷却乘区（冷却缩减 CDR）：冷却自吟唱完成时刻起算。</summary>
    public double CooldownFactor { get; init; } = 1;
    /// <summary>技能强度乘区（SkillIntensity = 1 + (Tier-1) × 0.30）。</summary>
    public double SkillIntensity { get; init; } = 1;
    /// <summary>免伤点数（Linear 百分比点，正点降低承受伤害）。</summary>
    public double DamageAbsorptionPoints { get; init; }
    /// <summary>仇恨威胁加成（小数比率，+2.0 表示威胁放大 200%）。</summary>
    public double ThreatRatio { get; init; }
    /// <summary>法术抗性点数（Spell 伤害的主抗性池）。</summary>
    public double MagicResistance { get; init; }
    /// <summary>抗性缩放倍率（等于境界阶位 Tier，放大全部抗性点数的效果）。</summary>
    public double ResistanceScale { get; init; } = 1;
    /// <summary>物理穿透百分比点数（0~100，按比例抵消目标物理抗性）。</summary>
    public double PhysicalPenetrationPercent { get; init; }
    /// <summary>法术穿透百分比点数（0~100，按比例抵消目标法术抗性）。</summary>
    public double MagicPenetrationPercent { get; init; }
    /// <summary>暴击点数（与目标暴击抗性对抗决定暴击概率）。</summary>
    public double Critical { get; init; }
    /// <summary>暴击抗性点数（降低被暴击概率）。</summary>
    public double CriticalResistance { get; init; }
    /// <summary>暴击伤害倍率（≥1，暴击时乘入最终伤害）。</summary>
    public double CriticalMultiplier { get; init; } = 1.5;
    /// <summary>破防威力点数（与招架结算对抗；胜出时削弱目标闪避与抗性）。</summary>
    public double BreakPower { get; init; }
    /// <summary>招架结算点数（与破防威力对抗；胜出时提升自身闪避与抗性）。</summary>
    public double ParryResolve { get; init; }
    /// <summary>攻击五行属性；None 时跳过五行抗性减免。</summary>
    public CombatElement Element { get; init; }
    /// <summary>金抗性点数（按抗性公式减免金属性伤害）。</summary>
    public double MetalResistance { get; init; }
    /// <summary>木抗性点数（按抗性公式减免木属性伤害）。</summary>
    public double WoodResistance { get; init; }
    /// <summary>水抗性点数（按抗性公式减免水属性伤害）。</summary>
    public double WaterResistance { get; init; }
    /// <summary>火抗性点数（按抗性公式减免火属性伤害）。</summary>
    public double FireResistance { get; init; }
    /// <summary>土抗性点数（按抗性公式减免土属性伤害）。</summary>
    public double EarthResistance { get; init; }
    /// <summary>反应效果参数包（汲取/反弹等衍生效果强度）；由钩子管线在攻击结算中读取。</summary>
    public CombatEffectProfile Effects { get; init; } = CombatEffectProfile.Empty;

    /// <summary>校验属性面板全部字段满足有限性与取值域不变量；非法时抛出 InvalidArgument。</summary>
    internal void Validate()
    {
        double[] nonnegative = [AttackPower, SpellAttack, MaxManapoints, Defense, Accuracy, Evasion, MagicResistance,
            Critical, CriticalResistance, BreakPower, ParryResolve, MetalResistance,
            WoodResistance, WaterResistance, FireResistance, EarthResistance];
        if (nonnegative.Any(value => !double.IsFinite(value) || value < 0)
            || !double.IsFinite(MaxHitpoints) || MaxHitpoints <= 0
            || !double.IsFinite(AttackIntervalSeconds) || AttackIntervalSeconds <= 0
            || !double.IsFinite(ResistanceScale) || ResistanceScale <= 0
            || !double.IsFinite(CriticalMultiplier) || CriticalMultiplier < 1
            || !double.IsFinite(BasicAttackRatio) || BasicAttackRatio <= 0
            || !double.IsFinite(CastTimeFactor) || CastTimeFactor <= 0
            || !double.IsFinite(CooldownFactor) || CooldownFactor <= 0
            || !double.IsFinite(SkillIntensity) || SkillIntensity <= 0
            || !double.IsFinite(DamageAbsorptionPoints) || !double.IsFinite(ThreatRatio) || ThreatRatio < 0
            || !double.IsFinite(PhysicalPenetrationPercent) || PhysicalPenetrationPercent is < 0 or > 100
            || !double.IsFinite(MagicPenetrationPercent) || MagicPenetrationPercent is < 0 or > 100
            || !Enum.IsDefined(Element) || !Enum.IsDefined(DamageKind))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗属性无效。");
        if (Effects is null) throw new DomainException(DomainErrorCode.InvalidArgument, "战斗反应效果参数无效。");
        Effects.Validate();
    }

    /// <summary>按核心必填属性创建战斗单位规格；扩展属性经 init 赋值后由 Validate 统一复核。</summary>
    /// <param name="maxHitpoints">气血上限（必须为正有限数）。</param>
    /// <param name="attackPower">攻击威能（非负）。</param>
    /// <param name="defense">防御护甲点数（非负）。</param>
    /// <param name="attackIntervalSeconds">普攻周期秒数（必须为正）。</param>
    /// <param name="accuracy">命中点数（非负）。</param>
    /// <param name="evasion">闪避点数（非负）。</param>
    /// <param name="damageKind">普攻伤害类型（物理/术法）。</param>
    /// <returns>通过基础校验的属性规格。</returns>
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
/// 战斗单位随战携带的技能槽：配表技能 JSON + 可用性门槛。
/// 单时间线出手语义：出手边界先结算待完成动作，再按槽位顺序挑选首个可用技能进入吟唱
/// （吟唱占用动作时长，仅吃行动速度 ATR），无可用技能时回落普通攻击；
/// 技能施放后按 cooldownMilliseconds 进入冷却循环，槽位顺序即优先级（0CD 填充技应放末尾）。
/// TriggerHpRatio 为怪物技能的气血门槛（当前气血比例 ≤ 阈值才可用）；
/// Triggers 非空时为斗法玩家镜像技能：改用配表触发条件判定替代气血门槛。
/// </summary>
public sealed record MonsterSkillSlot(JsonElement Ability, double Level = 1, double TriggerHpRatio = 1,
    IReadOnlyList<JsonElement>? Triggers = null);

/// <summary>战斗单位生成模板：静态属性 + 可选技能槽。</summary>
public sealed record CombatantTemplate(CombatantStats Stats, IReadOnlyList<MonsterSkillSlot>? Skills = null);

/// <summary>
/// 战斗交锋中的单位动态运行时状态（维护当前气血、灵力、威胁与下一次出手时刻）。
/// </summary>
public sealed class CombatantState
{
    /// <summary>以静态属性创建战斗单位运行时状态；气血灵力初始为满，首次出手动作由开战决策决定。</summary>
    /// <param name="id">战斗单位标识（player 或 enemy:{index}，≤128 字符）。</param>
    /// <param name="stats">静态属性面板。</param>
    /// <param name="skills">随战技能槽（可空）。</param>
    public CombatantState(string id, CombatantStats stats, IReadOnlyList<MonsterSkillSlot>? skills = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new DomainException(DomainErrorCode.InvalidArgument, "战斗单位标识无效。");
        Id = id;
        Stats = stats ?? throw new ArgumentNullException(nameof(stats));
        stats.Validate();
        Hitpoints = stats.MaxHitpoints;
        Manapoints = stats.MaxManapoints;
        // 普攻引导：各单位首次出手需先完成一个完整攻击周期的蓄势，而非开战瞬发。
        NextAttackAtSeconds = stats.AttackIntervalSeconds;
        Skills = skills ?? [];
    }

    /// <summary>战斗者标识（player 或 enemy:{index}）</summary>
    public string Id { get; }
    /// <summary>配表怪物定义 ID（玩家为 null）。</summary>
    public string? DefinitionId { get; internal set; }
    /// <summary>战斗静态属性面板</summary>
    public CombatantStats Stats { get; }
    /// <summary>随战技能槽（敌方单位使用）。</summary>
    public IReadOnlyList<MonsterSkillSlot> Skills { get; }
    /// <summary>当前剩余气血值</summary>
    public double Hitpoints { get; internal set; }
    /// <summary>当前剩余灵力值</summary>
    public double Manapoints { get; internal set; }
    /// <summary>下一次普通攻击或出手的预定战斗时间（秒）</summary>
    public double NextAttackAtSeconds { get; internal set; }
    /// <summary>累计出手攻击次数</summary>
    public double AttackCount { get; internal set; }
    /// <summary>累积威胁值：本场对敌方全体造成的总伤害。</summary>
    public double Threat { get; internal set; }
    /// <summary>嘲讽/仇恨加成（小数比率）；有效威胁 = Threat × (1 + 加成)。</summary>
    public double ThreatRatioBonus { get; internal set; }
    /// <summary>战内临时免伤点数（如破阵子类全体增益）。</summary>
    public double BonusAbsorptionPoints { get; internal set; }
    /// <summary>正在吟唱的技能槽下标；-1 表示下一次出手为普通攻击。吟唱占用整条动作时间线。</summary>
    public int PendingSkillIndex { get; internal set; } = -1;
    /// <summary>逐槽技能冷却就绪时刻（所有带技能单位共用）；冷却自吟唱完成时刻起算。</summary>
    public double[]? SkillCooldownUntil { get; internal set; }
    /// <summary>累计完成施放的技能次数，作为技能出手的确定性抽样独立计数器。</summary>
    public double SkillCastCount { get; internal set; }
    /// <summary>气血大于容差即视作存活</summary>
    public bool IsAlive => Hitpoints > 1e-12;
    /// <summary>参与目标抽取的有效威胁值。</summary>
    public double EffectiveThreat => Threat * (1 + Math.Max(0, Stats.ThreatRatio + ThreatRatioBonus));
}

/// <summary>战斗单位可持久化的运行时快照。</summary>
public sealed record CombatantSnapshot(
    double Hitpoints,
    double Manapoints,
    double NextAttackAtSeconds,
    double AttackCount,
    double Threat,
    int PendingSkillIndex = -1,
    double ThreatRatioBonus = 0,
    double BonusAbsorptionPoints = 0,
    IReadOnlyList<double>? SkillCooldownUntil = null,
    double SkillCastCount = 0);

/// <summary>
/// 单场战斗遭遇战状态聚合：一名玩家对阵同波全体敌人。
/// </summary>
public sealed class CombatEncounterState
{
    private CombatEncounterState(string encounterId, long seed, CombatantState player, IReadOnlyList<CombatantState> enemies)
    {
        EncounterId = encounterId;
        Seed = seed;
        Player = player;
        Enemies = enemies;
    }

    /// <summary>遭遇战唯一标识</summary>
    public string EncounterId { get; }
    /// <summary>战斗伪随机数种子（用于命中/暴击等确定性复现）</summary>
    public long Seed { get; }
    /// <summary>当前战斗已推进的总时间（秒）</summary>
    public double ElapsedSeconds { get; internal set; }
    /// <summary>玩家方战斗单位</summary>
    public CombatantState Player { get; }
    /// <summary>同波全体敌方单位（含已阵亡者，按下标稳定排序）。</summary>
    public IReadOnlyList<CombatantState> Enemies { get; }
    /// <summary>当前对决胜负裁决</summary>
    public CombatOutcome Outcome { get; internal set; } = CombatOutcome.Running;
    /// <summary>状态转换序号（递增帧号）</summary>
    public double Sequence { get; internal set; }
    /// <summary>玩家当前集火目标：存活敌人中有效威胁最高者。</summary>
    public CombatantState? PrimaryEnemy
    {
        get
        {
            CombatantState? primary = null;
            foreach (var enemy in Enemies)
                if (enemy.IsAlive && (primary is null || enemy.EffectiveThreat > primary.EffectiveThreat))
                    primary = enemy;
            return primary;
        }
    }

    /// <summary>开启一场玩家对单敌的遭遇战。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy)
        => Start(encounterId, seed, player, [new CombatantTemplate(enemy)]);

    /// <summary>开启一场多敌方单位的战斗遭遇。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, IReadOnlyList<CombatantTemplate> enemies)
        => Start(encounterId, seed, new CombatantTemplate(player), enemies);

    /// <summary>开启一场玩家方携带技能模板的遭遇（斗法剧本双方镜像同构）。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantTemplate player, IReadOnlyList<CombatantTemplate> enemies)
    {
        if (string.IsNullOrWhiteSpace(encounterId) || encounterId.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇标识无效。");
        ArgumentNullException.ThrowIfNull(player);
        if (player.Stats is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇玩家模板无效。");
        if (enemies is null || enemies.Count == 0 || enemies.Any(template => template?.Stats is null))
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇敌方阵容无效。");
        var states = new CombatantState[enemies.Count];
        for (var i = 0; i < enemies.Count; i++)
            states[i] = new CombatantState($"enemy:{i}", enemies[i].Stats, enemies[i].Skills);
        var playerState = new CombatantState("player", player.Stats, player.Skills);
        var encounter = new CombatEncounterState(encounterId, seed, playerState, states);
        // 开战即决策各单位首次出手：可放技能直接进入吟唱，否则按普攻周期蓄势。
        DeterministicCombatEngine.DecideInitialActions(encounter);
        return encounter;
    }

    /// <summary>从持久化快照恢复一场多敌遭遇战。</summary>
    public static CombatEncounterState Restore(
        string encounterId,
        long seed,
        CombatantStats playerStats,
        CombatantSnapshot player,
        IReadOnlyList<CombatantTemplate> enemies,
        IReadOnlyList<CombatantSnapshot> enemySnapshots,
        double elapsedSeconds,
        CombatOutcome outcome,
        double sequence = 0)
    {
        var encounter = Start(encounterId, seed, playerStats, enemies);
        if (enemySnapshots is null || enemySnapshots.Count != enemies.Count
            || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0
            || !double.IsFinite(sequence) || sequence < 0
            || !Enum.IsDefined(outcome))
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗状态无效。");
        ApplySnapshot(encounter.Player, player, playerStats.MaxHitpoints, playerStats.MaxManapoints);
        for (var i = 0; i < enemies.Count; i++)
            ApplySnapshot(encounter.Enemies[i], enemySnapshots[i], enemies[i].Stats.MaxHitpoints, enemies[i].Stats.MaxManapoints);
        encounter.ElapsedSeconds = elapsedSeconds;
        encounter.Outcome = outcome;
        encounter.Sequence = sequence;
        var expectedOutcome = (encounter.Player.IsAlive, encounter.Enemies.Any(enemy => enemy.IsAlive)) switch
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

    /// <summary>导出指定单位的可持久化快照。</summary>
    public static CombatantSnapshot Snapshot(CombatantState state)
        => new(state.Hitpoints, state.Manapoints, state.NextAttackAtSeconds, state.AttackCount,
            state.Threat, state.PendingSkillIndex, state.ThreatRatioBonus, state.BonusAbsorptionPoints,
            state.SkillCooldownUntil, state.SkillCastCount);

    private static void ApplySnapshot(CombatantState state, CombatantSnapshot snapshot, double maxHitpoints, double maxManapoints)
    {
        if (!double.IsFinite(snapshot.Hitpoints) || snapshot.Hitpoints < 0 || snapshot.Hitpoints > maxHitpoints
            || !double.IsFinite(snapshot.Manapoints) || snapshot.Manapoints < 0 || snapshot.Manapoints > maxManapoints
            || !double.IsFinite(snapshot.NextAttackAtSeconds) || snapshot.NextAttackAtSeconds < 0
            || !double.IsFinite(snapshot.AttackCount) || snapshot.AttackCount < 0
            || !double.IsFinite(snapshot.Threat) || snapshot.Threat < 0
            || !double.IsFinite(snapshot.ThreatRatioBonus) || snapshot.ThreatRatioBonus < 0
            || !double.IsFinite(snapshot.BonusAbsorptionPoints)
            || snapshot.PendingSkillIndex < -1
            || !double.IsFinite(snapshot.SkillCastCount) || snapshot.SkillCastCount < 0
            || (snapshot.SkillCooldownUntil is { } cooldowns
                && (cooldowns.Count != state.Skills.Count || cooldowns.Any(value => !double.IsFinite(value) || value < 0))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗单位状态无效。");
        state.Hitpoints = snapshot.Hitpoints;
        state.Manapoints = snapshot.Manapoints;
        state.NextAttackAtSeconds = snapshot.NextAttackAtSeconds;
        state.AttackCount = snapshot.AttackCount;
        state.Threat = snapshot.Threat;
        state.ThreatRatioBonus = snapshot.ThreatRatioBonus;
        state.BonusAbsorptionPoints = snapshot.BonusAbsorptionPoints;
        // 配表技能槽数量可能随版本变化：越界的吟唱槽位回退为普攻出手。
        state.PendingSkillIndex = snapshot.PendingSkillIndex >= 0 && snapshot.PendingSkillIndex < state.Skills.Count
            ? snapshot.PendingSkillIndex : -1;
        state.SkillCooldownUntil = snapshot.SkillCooldownUntil?.ToArray();
        state.SkillCastCount = snapshot.SkillCastCount;
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
    /// <summary>本次攻击是否暴击。</summary>
    public bool Critical { get; init; }
    /// <summary>本次攻击是否触发破防（攻方破拆胜出）。</summary>
    public bool Break { get; init; }
    /// <summary>本次攻击是否被招架（守方招架胜出）。</summary>
    public bool Parried { get; init; }
    /// <summary>施放事件的技能 ID；普通攻击为空。</summary>
    public string? AbilityId { get; init; }
    /// <summary>施放事件的治疗量（伤害事件为 0）。</summary>
    public double Healing { get; init; }
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
    /// <summary>重生中。</summary>
    Respawning = 3,
    /// <summary>斗法匹配等待中：由运行时撮合挂载对手，期间不消耗战斗时间。</summary>
    DuelPending = 4,
    /// <summary>斗法剧本回放中：战斗结果已确定性锁定，按时间轴逐事件回放。</summary>
    Duel = 5
}

/// <summary>远征波次中单个敌人的可持久化战斗快照。</summary>
public sealed record ExpeditionEnemyState(
    string EnemyId,
    double Hitpoints,
    double Manapoints,
    double NextAttackAtSeconds,
    double AttackCount,
    double Threat,
    int PendingSkillIndex = -1,
    double ThreatRatioBonus = 0,
    double BonusAbsorptionPoints = 0,
    IReadOnlyList<double>? SkillCooldownUntil = null,
    /// <summary>正在吟唱的技能 ID（展示用；出手决策以 PendingSkillIndex 为准）。</summary>
    string? PendingAbilityId = null,
    /// <summary>当前动作总时长秒数（吟唱时长或普攻间隔，展示用）。</summary>
    double PendingActionDurationSeconds = 0,
    /// <summary>累计完成施放次数（确定性抽样计数，恢复一致性必需）。</summary>
    double SkillCastCount = 0);

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
    double BattleElapsedSeconds,
    double PlayerHitpoints,
    double PlayerManapoints,
    double PlayerNextAttackAtSeconds,
    double PlayerAttackCount,
    double PlayerThreat,
    CombatOutcome Outcome,
    int WaveNumber = 1,
    IReadOnlyList<ExpeditionEnemyState>? Enemies = null)
{
    /// <summary>斗法回放载荷；Phase == Duel 时必须非空，其余相位必须为 null。</summary>
    public DuelPlaybackState? Duel { get; init; }
    /// <summary>本波入场前斗法撮合已失败标记：避免匹配失败后下一场战斗反复进入等待。</summary>
    public bool DuelSeekFailed { get; init; }

    /// <summary>校验远征状态是否满足持久化不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(EncounterId) && EncounterId.Length <= 128
        && Enum.IsDefined(Phase)
        && double.IsFinite(PhaseElapsedSeconds) && PhaseElapsedSeconds >= 0
        && double.IsFinite(PhaseDurationSeconds) && (Phase is CombatExpeditionPhase.Combat or CombatExpeditionPhase.DuelPending or CombatExpeditionPhase.Duel ? PhaseDurationSeconds >= 0 : PhaseDurationSeconds > 0)
        && (Phase is CombatExpeditionPhase.Combat or CombatExpeditionPhase.DuelPending or CombatExpeditionPhase.Duel || PhaseElapsedSeconds <= PhaseDurationSeconds + 1e-9)
        && double.IsFinite(BattleElapsedSeconds) && BattleElapsedSeconds >= 0
        && double.IsFinite(PlayerHitpoints) && PlayerHitpoints >= 0
        && double.IsFinite(PlayerManapoints) && PlayerManapoints >= 0
        && double.IsFinite(PlayerNextAttackAtSeconds) && PlayerNextAttackAtSeconds >= 0
        && double.IsFinite(PlayerAttackCount) && PlayerAttackCount >= 0
        && double.IsFinite(PlayerThreat) && PlayerThreat >= 0
        && Enum.IsDefined(Outcome)
        && WaveNumber >= 1
        && (Enemies is null || (Enemies.Count <= 8 && Enemies.All(enemy => enemy is not null
            && !string.IsNullOrWhiteSpace(enemy.EnemyId) && enemy.EnemyId.Length <= 128
            && double.IsFinite(enemy.Hitpoints) && enemy.Hitpoints >= 0
            && double.IsFinite(enemy.Manapoints) && enemy.Manapoints >= 0
            && double.IsFinite(enemy.NextAttackAtSeconds) && enemy.NextAttackAtSeconds >= 0
            && double.IsFinite(enemy.AttackCount) && enemy.AttackCount >= 0
            && double.IsFinite(enemy.Threat) && enemy.Threat >= 0
            && enemy.PendingSkillIndex >= -1
            && double.IsFinite(enemy.PendingActionDurationSeconds) && enemy.PendingActionDurationSeconds >= 0
            && double.IsFinite(enemy.SkillCastCount) && enemy.SkillCastCount >= 0
            && (enemy.SkillCooldownUntil is null || enemy.SkillCooldownUntil.All(value => double.IsFinite(value) && value >= 0)))))
        && Phase switch
        {
            CombatExpeditionPhase.DuelPending => Outcome == CombatOutcome.Running
                && (Enemies is null || Enemies.Count == 0) && Duel is null,
            CombatExpeditionPhase.Duel => Outcome == CombatOutcome.Running
                && Duel is { } duel && duel.IsValid
                && (Enemies is null || Enemies.Count == 0) && PhaseElapsedSeconds <= 1e-9,
            CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search or CombatExpeditionPhase.Respawning
                => Outcome == CombatOutcome.Running && (Enemies is null || Enemies.Count == 0) && Duel is null,
            _ => Enemies is { Count: > 0 } && PhaseElapsedSeconds <= 1e-9 && Duel is null
        };
}

/// <summary>
/// 无外部随机副作用的确定性战斗推进引擎。
/// 相同的初始属性与时间推进必得完全一致的伤害和事件序列，供客户端预测回放与服务端权威核验。
/// </summary>
public static class DeterministicCombatEngine
{
    /// <summary>单次推进最多产出的战斗事件上限，防止攻速异常引发死循环</summary>
    public const int MaxEventsPerAdvance = 100_000;

    /// <summary>
    /// 将战斗状态按事件驱动推进至目标时刻：只跨过双方出手时刻，不产生等间隔时间片。
    /// </summary>
    /// <param name="encounter">遭遇战状态（就地推进）。</param>
    /// <param name="targetSeconds">目标战斗时钟秒数（不得早于当前已推进时间）。</param>
    /// <returns>本段推进的战斗帧：事件明细、终局裁决与序列号。</returns>
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
            var next = encounter.Player.IsAlive ? encounter.Player.NextAttackAtSeconds : double.PositiveInfinity;
            foreach (var enemy in encounter.Enemies)
                if (enemy.IsAlive && enemy.NextAttackAtSeconds < next) next = enemy.NextAttackAtSeconds;
            if (!double.IsFinite(next)) throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
            if (next > targetSeconds)
            {
                encounter.ElapsedSeconds = targetSeconds;
                break;
            }
            encounter.ElapsedSeconds = Math.Max(encounter.ElapsedSeconds, next);
            if (encounter.Player.IsAlive && encounter.Player.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                PlayerAct(encounter, ref events);
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                    EnemyAct(encounter, enemy, i, ref events);
            }
            encounter.Sequence += 1;
            UpdateOutcome(encounter);
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome, (IReadOnlyList<CombatEvent>?)events ?? [], encounter.Sequence);
    }

    /// <summary>按存活敌人有效威胁权重抽取玩家攻击目标；全零威胁退化为等概率。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号（与其他判定隔离的抽样流）。</param>
    /// <returns>被选中的存活敌人；无存活敌人返回 null。</returns>
    public static CombatantState? SelectEnemyTarget(CombatEncounterState encounter, double attackerCount, ulong channel)
    {
        var alive = encounter.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0) return null;
        if (alive.Length == 1) return alive[0];
        var roll = Roll(encounter.Seed, 0, attackerCount, channel);
        var total = alive.Sum(enemy => enemy.EffectiveThreat);
        if (total <= 1e-12) return alive[(int)Math.Min(alive.Length - 1, (long)(roll * alive.Length))];
        var ticket = roll * total;
        foreach (var enemy in alive)
        {
            if (ticket < enemy.EffectiveThreat) return enemy;
            ticket -= enemy.EffectiveThreat;
        }
        return alive[^1];
    }

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="maxTargets">最多抽取目标数。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号；多目标逐位递增以隔离抽样流。</param>
    /// <returns>按抽取顺序排列的不重复存活敌人。</returns>
    public static IReadOnlyList<CombatantState> SelectEnemyTargets(CombatEncounterState encounter, int maxTargets, double attackerCount, ulong channel)
    {
        var alive = encounter.Enemies.Where(enemy => enemy.IsAlive).ToList();
        var result = new List<CombatantState>();
        while (alive.Count > 0 && result.Count < maxTargets)
        {
            var roll = Roll(encounter.Seed, 0, attackerCount, channel + (ulong)result.Count);
            var total = alive.Sum(enemy => enemy.EffectiveThreat);
            CombatantState chosen;
            if (total <= 1e-12)
            {
                chosen = alive[(int)Math.Min(alive.Count - 1, (long)(roll * alive.Count))];
            }
            else
            {
                var ticket = roll * total;
                chosen = alive[^1];
                foreach (var enemy in alive)
                {
                    if (ticket < enemy.EffectiveThreat) { chosen = enemy; break; }
                    ticket -= enemy.EffectiveThreat;
                }
            }
            alive.Remove(chosen);
            result.Add(chosen);
        }
        return result;
    }

    /// <summary>技能复用普攻对抗与减伤公式；施放方威胁按实际伤害累积。</summary>
    /// <param name="encounter">遭遇战状态（玩家视为施放方）。</param>
    /// <param name="target">受击敌方单位。</param>
    /// <param name="stats">技能折算后的攻击面板。</param>
    /// <param name="castCount">累计施放计数（确定性抽样的计数维度）。</param>
    /// <param name="abilityId">技能标识（随事件与钩子上下文透传）。</param>
    /// <returns>本次技能命中判定与伤害明细事件。</returns>
    public static CombatEvent ResolveAbilityAttack(CombatEncounterState encounter, CombatantState target, CombatantStats stats, double castCount, string? abilityId = null)
    {
        List<CombatEvent>? events = null;
        ResolveAttack(encounter, encounter.Player, target, 2, encounter.ElapsedSeconds, ref events, stats, castCount, abilityId);
        UpdateOutcome(encounter);
        return events![0];
    }

    /// <summary>开战即决策各单位的首次出手动作：可放技能直接进入吟唱，否则按普攻周期蓄势。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
    {
        for (var i = 0; i < encounter.Enemies.Count; i++)
            DecideNextAction(encounter, encounter.Enemies[i], encounter.Enemies, [encounter.Player], encounter.Player);
        DecideNextAction(encounter, encounter.Player, PlayerAllies(encounter), encounter.Enemies,
            SelectEnemyTarget(encounter, 0, 3));
    }

    /// <summary>玩家方出手边界：先结算待完成动作（普攻或吟唱完成的技能），再决策下一动作。</summary>
    private static void PlayerAct(CombatEncounterState encounter, ref List<CombatEvent>? events)
    {
        var player = encounter.Player;
        var allies = PlayerAllies(encounter);
        ResolvePending(encounter, player, allies,
            SelectEnemyTarget(encounter, player.AttackCount, 3), 0, ref events);
        DecideNextAction(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3));
    }

    private static IReadOnlyList<CombatantState> PlayerAllies(CombatEncounterState encounter)
        => [encounter.Player];

    /// <summary>敌方出手边界：与玩家同构，先结算待完成动作再决策下一动作。</summary>
    private static void EnemyAct(CombatEncounterState encounter, CombatantState enemy, int index, ref List<CombatEvent>? events)
    {
        ResolvePending(encounter, enemy, encounter.Enemies, encounter.Player, (ulong)(1 + index), ref events);
        DecideNextAction(encounter, enemy, encounter.Enemies, [encounter.Player], encounter.Player);
    }

    /// <summary>
    /// 结算出手边界上已决定的动作：吟唱中的技能在此刻结算（目标在结算时重新抽取），
    /// 否则结算普通攻击。
    /// </summary>
    private static void ResolvePending(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, CombatantState? target, ulong side, ref List<CombatEvent>? events)
    {
        var pending = unit.PendingSkillIndex;
        unit.PendingSkillIndex = -1;
        if (pending >= 0 && pending < unit.Skills.Count)
        {
            CastSkill(encounter, unit, allies, target ?? unit, unit.Skills[pending], side, ref events);
            unit.SkillCastCount += 1;
            return;
        }
        if (target is not null)
            ResolveAttack(encounter, unit, target, side, encounter.ElapsedSeconds, ref events);
    }

    /// <summary>
    /// 出手决策：按槽位顺序挑选首个可用技能进入吟唱——可用性门槛（怪物气血比例 / 镜像触发条件）、
    /// 冷却就绪与灵力消耗在起手时判定并预扣；吟唱占用整条动作时间线，时长 = castMilliseconds × 行动速度乘区，
    /// 冷却自吟唱完成时刻起算。无可用技能时下一次出手回落为普通攻击。
    /// </summary>
    private static void DecideNextAction(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents, CombatantState? target)
    {
        var skills = unit.Skills;
        if (skills.Count > 0 && unit.Stats.MaxHitpoints > 0)
        {
            var cooldowns = unit.SkillCooldownUntil ??= new double[skills.Count];
            for (var i = 0; i < skills.Count; i++)
            {
                var slot = skills[i];
                if (cooldowns[i] > encounter.ElapsedSeconds + 1e-12) continue;
                var available = slot.Triggers is { } triggers
                    ? target is not null && DuelTriggerEvaluator.MatchesAll(triggers, unit, target, allies, opponents)
                    : unit.Hitpoints / unit.Stats.MaxHitpoints <= slot.TriggerHpRatio;
                if (!available) continue;
                var manaCost = AbilityNumber(slot.Ability, "manaCost");
                if (unit.Manapoints < manaCost) continue;
                unit.Manapoints -= manaCost;
                var castSeconds = Math.Max(1e-9,
                    AbilityNumber(slot.Ability, "castMilliseconds") / 1000d * unit.Stats.CastTimeFactor);
                unit.PendingSkillIndex = i;
                unit.NextAttackAtSeconds = encounter.ElapsedSeconds + castSeconds;
                cooldowns[i] = unit.NextAttackAtSeconds
                    + AbilityNumber(slot.Ability, "cooldownMilliseconds") / 1000d * unit.Stats.CooldownFactor;
                return;
            }
        }
        unit.PendingSkillIndex = -1;
        unit.NextAttackAtSeconds = encounter.ElapsedSeconds + unit.Stats.AttackIntervalSeconds;
    }

    /// <summary>技能施放：伤害/治疗/嘲讽/群体增益统一复用同构结算管线；怪物与斗法镜像共用。
    /// 灵力消耗与冷却已在起手决策时预扣，此处只负责完成结算。</summary>
    private static void CastSkill(CombatEncounterState encounter, CombatantState caster,
        IReadOnlyList<CombatantState> allies, CombatantState target, MonsterSkillSlot slot,
        ulong side, ref List<CombatEvent>? events)
    {
        var ability = slot.Ability;
        var spell = AbilityText(ability, "category") == "spell";
        var attackBase = spell ? caster.Stats.SpellAttack : caster.Stats.AttackPower;
        var id = AbilityText(ability, "id") ?? string.Empty;
        var ratioBonus = 1 + (slot.Level - 1) * 0.006;
        var flatBonus = 1 + (slot.Level - 1) * 0.01;
        if (AbilityText(ability, "effectType") == "heal")
        {
            var ally = allies.Where(unit => unit.IsAlive).OrderBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints).First();
            var healing = Math.Min(ally.Stats.MaxHitpoints - ally.Hitpoints,
                attackBase * caster.Stats.SkillIntensity * AbilityNumber(ability, "healingRatio") * ratioBonus
                    + AbilityNumber(ability, "flatHealing") * flatBonus);
            ally.Hitpoints += healing;
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, ally.Id, 0, true,
                spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, ally.Hitpoints) { AbilityId = id, Healing = healing });
            return;
        }
        if (AbilityText(ability, "effectType") == "buff" && ability.TryGetProperty("buff", out var buff))
        {
            var absorption = buff.TryGetProperty("damageAbsorptionRatio", out var absorb) && absorb.TryGetDouble(out var absorbPoints) ? absorbPoints * 100 : 0;
            var threatBonus = buff.TryGetProperty("threatRatio", out var threat) && threat.TryGetDouble(out var threatBuff) ? threatBuff : 0;
            foreach (var ally in allies.Where(unit => unit.IsAlive))
            {
                ally.BonusAbsorptionPoints += absorption;
                ally.ThreatRatioBonus += threatBonus;
            }
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, caster.Id, 0, true, CombatDamageKind.Spell, caster.Hitpoints) { AbilityId = id });
            return;
        }
        var threatGain = AbilityNumber(ability, "threatGain");
        var threatRatio = AbilityNumber(ability, "threatRatio");
        if (threatGain > 0 || threatRatio > 0 || AbilityText(ability, "targetType") == "self")
        {
            caster.Threat += threatGain;
            caster.ThreatRatioBonus += threatRatio;
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, caster.Id, 0, true, CombatDamageKind.Physical, caster.Hitpoints) { AbilityId = id });
            return;
        }
        var stats = caster.Stats with
        {
            AttackPower = attackBase * caster.Stats.SkillIntensity * AbilityNumber(ability, "damageRatio") * ratioBonus
                + AbilityNumber(ability, "flatDamage") * flatBonus,
            BasicAttackRatio = 1,
            DamageKind = spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
            Element = AbilityElement(ability)
        };
        // 技能出手以施放方累计完成次数为抽样计数，保证确定性随机流不依赖槽位语义。
        var count = caster.SkillCastCount + 1;
        ResolveAttack(encounter, caster, target, side, encounter.ElapsedSeconds, ref events, stats, count, id);
    }

    /// <summary>按破拆招、闪避、暴击和两层抗性顺序结算，抽样仅依赖已持久化的种子与出手序号。
    /// 判定前、伤害落账前、落账后三个点位依次驱动钩子管线；钩子衍生伤害直接落账不再回流。</summary>
    private static void ResolveAttack(CombatEncounterState encounter, CombatantState attacker, CombatantState target,
        ulong side, double at, ref List<CombatEvent>? events,
        CombatantStats? statsOverride = null, double? countOverride = null, string? abilityId = null)
    {
        if (!attacker.IsAlive || !target.IsAlive) return;
        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = attacker,
            Target = target,
            AttackStats = statsOverride ?? attacker.Stats,
            AbilityId = abilityId,
            Side = side,
            Count = countOverride ?? attacker.AttackCount,
            AtSeconds = at
        };
        CombatHookPipeline.MutateResolve(context);
        var attack = context.AttackStats;
        var defense = target.Stats;
        var contestRoll = Roll(encounter.Seed, side, context.Count, 0);
        var broken = attack.BreakPower > defense.ParryResolve
            && contestRoll < attack.BreakPower / (attack.BreakPower + defense.ParryResolve + 100);
        var parried = defense.ParryResolve > attack.BreakPower
            && contestRoll < defense.ParryResolve / (defense.ParryResolve + attack.BreakPower + 100);
        var accuracy = attack.Accuracy * (broken ? 2 : 1);
        var evasion = defense.Evasion * (parried ? 2 : 1);
        var hit = Roll(encounter.Seed, side, context.Count, 1) >= evasion / (evasion + accuracy + 100);
        var criticalPoints = attack.Critical * (broken ? 2 : 1);
        var critical = hit && Roll(encounter.Seed, side, context.Count, 2)
            < criticalPoints / (criticalPoints + defense.CriticalResistance + 100);
        var resistance = attack.DamageKind == CombatDamageKind.Physical ? defense.Defense : defense.MagicResistance;
        var penetration = attack.DamageKind == CombatDamageKind.Physical ? attack.PhysicalPenetrationPercent : attack.MagicPenetrationPercent;
        resistance *= (parried ? 2 : 1) * (1 - penetration / 100);
        var rawDamage = attack.AttackPower * attack.BasicAttackRatio;
        // 等价于 RawDamage² / (RawDamage + RES × K + 1)，先除后乘避免平方中间值溢出。
        var damage = hit ? rawDamage * (rawDamage / (rawDamage + resistance * defense.ResistanceScale + 1)) : 0;
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
        // 免伤为负向 Linear 点数：+x 点 → 承受伤害 ×100/(100+x)，负点反向增伤。
        damage *= PercentValue.Multiplier(-(defense.DamageAbsorptionPoints + target.BonusAbsorptionPoints), PercentCurveMode.Linear);
        context.Hit = hit;
        context.Critical = critical;
        context.Broken = broken;
        context.Parried = parried;
        context.Damage = damage;
        CombatHookPipeline.MutateDamage(context);
        context.Damage = Math.Max(0, context.Damage);
        target.Hitpoints = Math.Max(0, target.Hitpoints - context.Damage);
        attacker.AttackCount += 1;
        attacker.Threat += context.Damage;
        events ??= new List<CombatEvent>();
        events.Add(new CombatEvent(at, attacker.Id, target.Id, context.Damage, context.Hit, attack.DamageKind, target.Hitpoints)
        { Critical = context.Critical, Break = context.Broken, Parried = context.Parried, AbilityId = abilityId });
        CombatHookPipeline.ApplyAftermath(context);
        if (context.DerivedEvents is { } derived) events.AddRange(derived);
    }

    internal static string? AbilityText(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static double AbilityNumber(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.TryGetDouble(out var number) ? number : 0;

    private static CombatElement AbilityElement(JsonElement ability)
        => Enum.TryParse<CombatElement>(AbilityText(ability, "element") ?? "none", true, out var element) ? element : CombatElement.None;

    internal static double Roll(long seed, ulong side, double attackCount, ulong channel)
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
        encounter.Outcome = (encounter.Player.IsAlive, encounter.Enemies.Any(enemy => enemy.IsAlive)) switch
        {
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }
}
