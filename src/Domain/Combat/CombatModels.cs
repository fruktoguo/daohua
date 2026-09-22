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
    /// <summary>每 5 秒结算点的气血回复量（战斗内按 5 秒网格确定性结算，与脱战回复同口径）。</summary>
    public double HpRegen { get; init; }
    /// <summary>每 5 秒结算点的灵力回复量。</summary>
    public double MpRegen { get; init; }
    /// <summary>反应效果参数包（汲取/反弹等衍生效果强度）；由钩子管线在攻击结算中读取。</summary>
    public CombatEffectProfile Effects { get; init; } = CombatEffectProfile.Empty;

    /// <summary>校验属性面板全部字段满足有限性与取值域不变量；非法时抛出 InvalidArgument。</summary>
    internal void Validate()
    {
        double[] nonnegative = [AttackPower, SpellAttack, MaxManapoints, Defense, Accuracy, Evasion, MagicResistance,
            Critical, CriticalResistance, BreakPower, ParryResolve, MetalResistance,
            WoodResistance, WaterResistance, FireResistance, EarthResistance, HpRegen, MpRegen];
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
/// 战内 Buff：随战斗授予的临时效果，过期时刻以战斗时钟（ElapsedSeconds）为准。
/// 随战斗快照持久化，恢复后事件序列不漂移；战外持久 Buff 仍走 <see cref="Domain.Player.ActiveBuff"/>。
/// </summary>
/// <param name="BuffId">Buff 标识；同 ID 重复授予时覆盖（先移除旧条目）。</param>
/// <param name="ExpiresAtSeconds">过期时刻（战斗时钟秒）。</param>
/// <param name="Stacks">层数（结算按层数线性放大效果量）。</param>
/// <param name="HpRegen">每 5 秒结算点的气血回复量。</param>
/// <param name="MpRegen">每 5 秒结算点的灵力回复量。</param>
/// <param name="AttackSpeedRatio">攻速加成比率：普攻间隔 ÷(1+合计比率)。</param>
/// <param name="DamageAbsorptionRatio">免伤比率：合计 ×100 折为免伤点数（与面板免伤同乘区）。</param>
/// <param name="ThreatRatio">仇恨加成比率：叠加进有效威胁乘区。</param>
public sealed record CombatBuffState(
    string BuffId, double ExpiresAtSeconds, double Stacks = 1,
    double HpRegen = 0, double MpRegen = 0, double AttackSpeedRatio = 0,
    double DamageAbsorptionRatio = 0, double ThreatRatio = 0)
{
    /// <summary>校验 Buff 字段满足持久化不变量。</summary>
    internal bool IsValid => !string.IsNullOrWhiteSpace(BuffId) && BuffId.Length <= 128
        && double.IsFinite(ExpiresAtSeconds) && ExpiresAtSeconds >= 0
        && double.IsFinite(Stacks) && Stacks > 0
        && double.IsFinite(HpRegen) && double.IsFinite(MpRegen)
        && double.IsFinite(AttackSpeedRatio) && double.IsFinite(DamageAbsorptionRatio)
        && double.IsFinite(ThreatRatio);
}

/// <summary>战斗单位生成模板：静态属性 + 可选技能槽。</summary>
public sealed record CombatantTemplate(CombatantStats Stats, IReadOnlyList<SkillSlot>? Skills = null);

/// <summary>
/// 战斗交锋中的单位动态运行时状态（维护当前气血、灵力、威胁与下一次出手时刻）。
/// </summary>
public sealed class CombatantState
{
    /// <summary>以静态属性创建战斗单位运行时状态；气血灵力初始为满，首次出手动作由开战决策决定。</summary>
    /// <param name="id">战斗单位标识（player 或 enemy:{index}，≤128 字符）。</param>
    /// <param name="stats">静态属性面板。</param>
    /// <param name="skills">随战技能槽（可空）。</param>
    public CombatantState(string id, CombatantStats stats, IReadOnlyList<SkillSlot>? skills = null)
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
    /// <summary>随战技能槽（玩家/怪物/斗法镜像共用同构槽位模型）。</summary>
    public IReadOnlyList<SkillSlot> Skills { get; }
    /// <summary>战内 Buff 列表（战斗时钟过期，随快照持久化）。</summary>
    public List<CombatBuffState> Buffs { get; } = [];
    /// <summary>战外 Buff 层数视图：由宿主注入（如玩家持久 Buff）；怪物与斗法镜像不注入，恒为 0。</summary>
    internal Func<string?, double>? ExternalBuffStacks { get; set; }
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
    /// <summary>参与目标抽取的有效威胁值：Threat ×(1 + 面板仇恨 + 嘲讽修正 + 战内 Buff 仇恨)。</summary>
    public double EffectiveThreat => Threat * (1 + Math.Max(0, Stats.ThreatRatio + ThreatRatioBonus + BuffThreatRatio));
    /// <summary>战内 Buff 合计攻速加成比率。</summary>
    internal double BuffAttackSpeedRatio => Buffs.Sum(buff => buff.AttackSpeedRatio * buff.Stacks);
    /// <summary>战内 Buff 合计免伤点数（比率 ×100 折点，与面板免伤同乘区）。</summary>
    internal double BuffAbsorptionPoints => Buffs.Sum(buff => buff.DamageAbsorptionRatio * buff.Stacks) * 100;
    /// <summary>战内 Buff 合计仇恨加成比率。</summary>
    internal double BuffThreatRatio => Buffs.Sum(buff => buff.ThreatRatio * buff.Stacks);

    /// <summary>Buff 层数求值：战内 Buff 层数 + 宿主注入的战外视图；buffId 为 null 时合计全部层数。</summary>
    internal double BuffStacks(string? buffId)
        => (buffId is null ? Buffs.Sum(buff => buff.Stacks)
                : Buffs.Where(buff => buff.BuffId == buffId).Sum(buff => buff.Stacks))
            + (ExternalBuffStacks?.Invoke(buffId) ?? 0);
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
    double SkillCastCount = 0,
    IReadOnlyList<CombatBuffState>? Buffs = null)
{
    /// <summary>快照字段的有限性/取值域校验（不含上限比对——上限依赖施法时面板）。</summary>
    internal bool IsValid => double.IsFinite(Hitpoints) && Hitpoints >= 0
        && double.IsFinite(Manapoints) && Manapoints >= 0
        && double.IsFinite(NextAttackAtSeconds) && NextAttackAtSeconds >= 0
        && double.IsFinite(AttackCount) && AttackCount >= 0
        && double.IsFinite(Threat) && Threat >= 0
        && double.IsFinite(ThreatRatioBonus) && ThreatRatioBonus >= 0
        && double.IsFinite(BonusAbsorptionPoints)
        && PendingSkillIndex >= -1
        && double.IsFinite(SkillCastCount) && SkillCastCount >= 0
        && (SkillCooldownUntil is null || SkillCooldownUntil.All(value => double.IsFinite(value) && value >= 0))
        && (Buffs is null || Buffs.All(buff => buff is not null && buff.IsValid));
}

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
        => Start(encounterId, seed, new CombatantTemplate(player), [new CombatantTemplate(enemy)]);

    /// <summary>开启一场多敌方单位的战斗遭遇。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, IReadOnlyList<CombatantTemplate> enemies)
        => Start(encounterId, seed, new CombatantTemplate(player), enemies);

    /// <summary>开启一场玩家方携带技能模板的遭遇（斗法剧本双方镜像同构）。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantTemplate player, IReadOnlyList<CombatantTemplate> enemies)
    {
        var encounter = Create(encounterId, seed, player, enemies);
        // 开战即决策各单位首次出手：可放技能直接进入吟唱，否则按普攻周期蓄势。
        DeterministicCombatEngine.DecideInitialActions(encounter);
        return encounter;
    }

    /// <summary>
    /// 创建遭遇但不决策首次出手：供宿主先注入跨战状态（灵力/冷却/战外 Buff 视图）再显式开局，
    /// 保证触发条件求值与灵力预扣都基于真实入场状态。
    /// </summary>
    internal static CombatEncounterState Create(string encounterId, long seed, CombatantTemplate player, IReadOnlyList<CombatantTemplate> enemies)
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
        return new CombatEncounterState(encounterId, seed, playerState, states);
    }

    /// <summary>从持久化快照恢复一场多敌遭遇战。</summary>
    public static CombatEncounterState Restore(
        string encounterId,
        long seed,
        CombatantTemplate player,
        CombatantSnapshot playerSnapshot,
        IReadOnlyList<CombatantTemplate> enemies,
        IReadOnlyList<CombatantSnapshot> enemySnapshots,
        double elapsedSeconds,
        CombatOutcome outcome,
        double sequence = 0)
    {
        // 恢复路径跳过开局决策：全部出手状态由快照逐字段回填，避免重复预扣灵力。
        var encounter = Create(encounterId, seed, player, enemies);
        if (enemySnapshots is null || enemySnapshots.Count != enemies.Count
            || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0
            || !double.IsFinite(sequence) || sequence < 0
            || !Enum.IsDefined(outcome))
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗状态无效。");
        ApplySnapshot(encounter.Player, playerSnapshot, player.Stats.MaxHitpoints, player.Stats.MaxManapoints);
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
            state.SkillCooldownUntil, state.SkillCastCount,
            state.Buffs.Count == 0 ? null : state.Buffs.ToArray());

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
                && (cooldowns.Count != state.Skills.Count || cooldowns.Any(value => !double.IsFinite(value) || value < 0)))
            || (snapshot.Buffs is { } buffs && buffs.Any(buff => buff is null || !buff.IsValid)))
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
        // 战内 Buff 随快照恢复：过期时刻是战斗时钟值，恢复后按原时刻到期，事件序列不漂移。
        state.Buffs.Clear();
        if (snapshot.Buffs is not null) state.Buffs.AddRange(snapshot.Buffs);
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
    /// <summary>事件结算完成时刻出手方的剩余灵力；衍生事件（反伤等）不携带则为 null。</summary>
    public double? AttackerManapointsAfter { get; init; }
    /// <summary>事件结算完成时刻受击方的剩余灵力；衍生事件不携带则为 null。</summary>
    public double? TargetManapointsAfter { get; init; }
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
    double SkillCastCount = 0,
    /// <summary>战内 Buff 列表（战斗时钟过期；恢复一致性必需）。</summary>
    IReadOnlyList<CombatBuffState>? Buffs = null);

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
    /// <summary>
    /// 玩家单位的完整战斗快照：吟唱槽位、逐槽冷却、施放计数与战内 Buff 的恢复依据；
    /// 上方的 Player* 标量字段是面向协议展示的镜像，Combat 相位必须携带完整快照。
    /// </summary>
    public CombatantSnapshot? PlayerState { get; init; }

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
            && (enemy.SkillCooldownUntil is null || enemy.SkillCooldownUntil.All(value => double.IsFinite(value) && value >= 0))
            && (enemy.Buffs is null || enemy.Buffs.All(buff => buff is not null && buff.IsValid)))))
        && (PlayerState is null || PlayerState.IsValid)
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
                && PlayerState is { }
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
            // 战内 Buff 的到期/周期回复同样是确定性时间线边界，必须与出手边界一起纳入推进。
            var effectAt = NextBuffBoundary(encounter);
            if (effectAt < next) next = effectAt;
            if (!double.IsFinite(next)) throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
            if (next > targetSeconds)
            {
                encounter.ElapsedSeconds = targetSeconds;
                break;
            }
            encounter.ElapsedSeconds = Math.Max(encounter.ElapsedSeconds, next);
            // 同刻到期先于出手结算：到达边界的 Buff 先失效/回复再进入动作结算。
            if (effectAt <= encounter.ElapsedSeconds + 1e-12) ApplyBuffTick(encounter, effectAt);
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
        => SelectTarget(encounter.Enemies, encounter.Seed, 0, attackerCount, channel);

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="maxTargets">最多抽取目标数。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号；多目标逐位递增以隔离抽样流。</param>
    /// <returns>按抽取顺序排列的不重复存活敌人。</returns>
    public static IReadOnlyList<CombatantState> SelectEnemyTargets(CombatEncounterState encounter, int maxTargets, double attackerCount, ulong channel)
        => SelectTargets(encounter.Enemies, maxTargets, encounter.Seed, 0, attackerCount, channel);

    /// <summary>按有效威胁权重从任意存活目标列表抽取单个目标；全零威胁退化为等概率。</summary>
    internal static CombatantState? SelectTarget(IReadOnlyList<CombatantState> opponents, long seed,
        ulong side, double count, ulong channel)
    {
        var alive = opponents.Where(unit => unit.IsAlive).ToArray();
        if (alive.Length == 0) return null;
        if (alive.Length == 1) return alive[0];
        var roll = Roll(seed, side, count, channel);
        var total = alive.Sum(unit => unit.EffectiveThreat);
        if (total <= 1e-12) return alive[(int)Math.Min(alive.Length - 1, (long)(roll * alive.Length))];
        var ticket = roll * total;
        foreach (var unit in alive)
        {
            if (ticket < unit.EffectiveThreat) return unit;
            ticket -= unit.EffectiveThreat;
        }
        return alive[^1];
    }

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    internal static IReadOnlyList<CombatantState> SelectTargets(IReadOnlyList<CombatantState> opponents, int maxTargets,
        long seed, ulong side, double count, ulong channel)
    {
        var alive = opponents.Where(unit => unit.IsAlive).ToList();
        var result = new List<CombatantState>();
        while (alive.Count > 0 && result.Count < maxTargets)
        {
            var roll = Roll(seed, side, count, channel + (ulong)result.Count);
            var total = alive.Sum(unit => unit.EffectiveThreat);
            CombatantState chosen;
            if (total <= 1e-12)
            {
                chosen = alive[(int)Math.Min(alive.Count - 1, (long)(roll * alive.Count))];
            }
            else
            {
                var ticket = roll * total;
                chosen = alive[^1];
                foreach (var unit in alive)
                {
                    if (ticket < unit.EffectiveThreat) { chosen = unit; break; }
                    ticket -= unit.EffectiveThreat;
                }
            }
            alive.Remove(chosen);
            result.Add(chosen);
        }
        return result;
    }

    /// <summary>技能出手的随机流侧位偏移：技能判定使用 side+偏移的独立流，与普攻（side 0/1+i）完全隔离。</summary>
    private const ulong SkillSideOffset = 0x1000;

    /// <summary>战内 Buff 周期回复的结算网格秒数。</summary>
    private const double BuffTickSeconds = 5;

    /// <summary>开战即决策各单位的首次出手动作：可放技能直接进入吟唱，否则按普攻周期蓄势。
    /// 已恢复吟唱中的单位（PendingSkillIndex ≥ 0）跳过决策，保留跨战延续的施放。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
    {
        for (var i = 0; i < encounter.Enemies.Count; i++)
            if (encounter.Enemies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.Enemies[i], encounter.Enemies, [encounter.Player], encounter.Player);
        if (encounter.Player.PendingSkillIndex < 0)
            DecideNextAction(encounter, encounter.Player, PlayerAllies(encounter), encounter.Enemies,
                SelectEnemyTarget(encounter, 0, 3));
    }

    /// <summary>玩家方出手边界：先结算待完成动作（普攻或吟唱完成的技能），再决策下一动作。</summary>
    private static void PlayerAct(CombatEncounterState encounter, ref List<CombatEvent>? events)
    {
        var player = encounter.Player;
        var allies = PlayerAllies(encounter);
        ResolvePending(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3), 0, ref events);
        DecideNextAction(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3));
    }

    private static IReadOnlyList<CombatantState> PlayerAllies(CombatEncounterState encounter)
        => [encounter.Player];

    /// <summary>敌方出手边界：与玩家同构，先结算待完成动作再决策下一动作。</summary>
    private static void EnemyAct(CombatEncounterState encounter, CombatantState enemy, int index, ref List<CombatEvent>? events)
    {
        ResolvePending(encounter, enemy, encounter.Enemies, [encounter.Player], encounter.Player, (ulong)(1 + index), ref events);
        DecideNextAction(encounter, enemy, encounter.Enemies, [encounter.Player], encounter.Player);
    }

    /// <summary>
    /// 结算出手边界上已决定的动作：吟唱中的技能在此刻结算（目标在结算时按威胁重新抽取），
    /// 否则结算普通攻击。技能走 side+SkillSideOffset 的独立随机流，与普攻流完全隔离。
    /// </summary>
    private static void ResolvePending(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents,
        CombatantState? target, ulong side, ref List<CombatEvent>? events)
    {
        var pending = unit.PendingSkillIndex;
        unit.PendingSkillIndex = -1;
        if (pending >= 0 && pending < unit.Skills.Count)
        {
            CastSkill(encounter, unit, allies, opponents, target, unit.Skills[pending], side + SkillSideOffset, ref events);
            unit.SkillCastCount += 1;
            return;
        }
        if (target is not null)
            ResolveAttack(encounter, unit, target, side, encounter.ElapsedSeconds, ref events);
    }

    /// <summary>
    /// 出手决策：按槽位顺序挑选首个可用技能进入吟唱——触发条件（统一求值器）、冷却就绪与
    /// 灵力消耗在起手时判定并预扣；吟唱占用整条动作时间线，时长 = castMilliseconds × 行动速度乘区，
    /// 冷却自吟唱完成时刻起算。无可用技能时下一次出手回落为普通攻击，普攻间隔吃战内攻速 Buff。
    /// </summary>
    private static void DecideNextAction(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents, CombatantState? target)
    {
        var skills = unit.Skills;
        if (skills.Count > 0)
        {
            var cooldowns = unit.SkillCooldownUntil ??= new double[skills.Count];
            for (var i = 0; i < skills.Count; i++)
            {
                var slot = skills[i];
                if (cooldowns[i] > encounter.ElapsedSeconds + 1e-12) continue;
                // 触发条件为唯一可用性门槛：空表恒可用；求值失败（依赖缺失/词表外输入）跳过本槽。
                if (slot.Triggers is { Count: > 0 } triggers
                    && !SkillTriggerEvaluator.MatchesAll(triggers, unit, target, allies, opponents))
                    continue;
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
        unit.NextAttackAtSeconds = encounter.ElapsedSeconds
            + unit.Stats.AttackIntervalSeconds / Math.Max(0.1, 1 + unit.BuffAttackSpeedRatio);
    }

    /// <summary>最近的战内效果边界：全体单位 Buff 的最早到期时刻；存在回复类 Buff 时叠加 5 秒结算网格。</summary>
    private static double NextBuffBoundary(CombatEncounterState encounter)
    {
        var next = double.PositiveInfinity;
        var regenPending = false;
        foreach (var unit in encounter.Enemies.Prepend(encounter.Player))
        {
            if (unit.Stats.HpRegen != 0 || unit.Stats.MpRegen != 0) regenPending = true;
            foreach (var buff in unit.Buffs)
            {
                if (buff.ExpiresAtSeconds < next) next = buff.ExpiresAtSeconds;
                if (buff.HpRegen != 0 || buff.MpRegen != 0) regenPending = true;
            }
        }
        if (regenPending)
            next = Math.Min(next, (Math.Floor(encounter.ElapsedSeconds / BuffTickSeconds) + 1) * BuffTickSeconds);
        return next;
    }

    /// <summary>战内效果边界结算：先剔除到期 Buff，再按 5 秒网格对存活单位结算周期回复。</summary>
    private static void ApplyBuffTick(CombatEncounterState encounter, double at)
    {
        foreach (var unit in encounter.Enemies.Prepend(encounter.Player))
            unit.Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        if (Math.Abs(at / BuffTickSeconds - Math.Round(at / BuffTickSeconds)) > 1e-9) return;
        foreach (var unit in encounter.Enemies.Prepend(encounter.Player))
        {
            if (!unit.IsAlive) continue;
            // 面板回复与战内 Buff 回复同口径：均为每 5 秒结算点一次。
            double hp = unit.Stats.HpRegen, mp = unit.Stats.MpRegen;
            foreach (var buff in unit.Buffs)
            {
                hp += buff.HpRegen * buff.Stacks;
                mp += buff.MpRegen * buff.Stacks;
            }
            if (hp != 0) unit.Hitpoints = Math.Clamp(unit.Hitpoints + hp, 0, unit.Stats.MaxHitpoints);
            if (mp != 0) unit.Manapoints = Math.Clamp(unit.Manapoints + mp, 0, unit.Stats.MaxManapoints);
        }
    }

    /// <summary>
    /// 技能施放统一结算：伤害/治疗/支援（威胁修正与战内 Buff）走同一条管线，玩家、怪物与斗法镜像完全同构。
    /// 灵力消耗与冷却已在起手决策时预扣，此处只负责完成结算。
    /// 技能类别判定：heal 效果或正治疗量为治疗；伤害为零且目标为友方/自身为支援技；其余为伤害技。
    /// </summary>
    private static void CastSkill(CombatEncounterState encounter, CombatantState caster,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents,
        CombatantState? target, SkillSlot slot, ulong side, ref List<CombatEvent>? events)
    {
        var ability = slot.Ability;
        var spell = AbilityText(ability, "category") == "spell";
        var attackBase = spell ? caster.Stats.SpellAttack : caster.Stats.AttackPower;
        var id = AbilityText(ability, "id") ?? string.Empty;
        var ratioBonus = CombatGrowthCurve.AbilityRatioGrowth(slot.Level);
        var flatBonus = CombatGrowthCurve.AbilityFlatGrowth(slot.Level);
        var targetType = AbilityText(ability, "targetType");
        // 威胁类字段是施放方修正，与技能效果类型无关：任何施放都可携带仇恨增益。
        caster.Threat += AbilityNumber(ability, "threatGain");
        caster.ThreatRatioBonus += AbilityNumber(ability, "threatRatio");
        var healOutput = attackBase * caster.Stats.SkillIntensity * AbilityNumber(ability, "healingRatio") * ratioBonus
            + AbilityNumber(ability, "flatHealing") * flatBonus;
        if (AbilityText(ability, "effectType") == "heal" || healOutput > 0)
        {
            // 治疗目标范围按 targetType 解析；治疗增益（healingBonusRatio）对一切施放单位通用。
            var healTargets = targetType switch
            {
                "self" => (IReadOnlyList<CombatantState>)[caster],
                "allAllies" => allies.Where(unit => unit.IsAlive).ToArray(),
                _ => allies.Where(unit => unit.IsAlive)
                    .OrderBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints).Take(1).ToArray()
            };
            var output = healOutput * (1 + caster.Stats.Effects.HealingBonusRatio);
            foreach (var ally in healTargets)
            {
                var healed = Math.Min(ally.Stats.MaxHitpoints - ally.Hitpoints, Math.Max(0, output));
                ally.Hitpoints += healed;
                events ??= new List<CombatEvent>();
                events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, ally.Id, 0, true,
                    spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, ally.Hitpoints)
                {
                    AbilityId = id, Healing = healed,
                    AttackerManapointsAfter = caster.Manapoints,
                    TargetManapointsAfter = ally.Manapoints
                });
            }
            return;
        }
        var hasDamage = AbilityNumber(ability, "damageRatio") + AbilityNumber(ability, "flatDamage") > 0;
        if (targetType == "self" || !hasDamage && targetType is not ("targetEnemy" or "allEnemies"))
        {
            // 支援技：以自身/友方为目标的零伤害技能；战内 Buff 授予友方并按战斗时钟到期。
            if (ability.TryGetProperty("buff", out var buff) && buff.ValueKind == JsonValueKind.Object)
            {
                var buffId = AbilityText(buff, "buffId") ?? $"ability-buff:{id}";
                var grant = new CombatBuffState(buffId,
                    encounter.ElapsedSeconds + Math.Max(0, AbilityNumber(buff, "durationSeconds")), 1,
                    AbilityNumber(buff, "hpRegen"), AbilityNumber(buff, "mpRegen"),
                    AbilityNumber(buff, "attackSpeedRatio"), AbilityNumber(buff, "damageAbsorptionRatio"),
                    AbilityNumber(buff, "threatRatio"));
                // maxStacks 缺省为 1：同 ID 重复授予刷新时长并叠层至上限。
                var stackCap = Math.Max(1, AbilityNumber(buff, "maxStacks"));
                var scope = targetType switch
                {
                    "lowestHitpointsAlly" => allies.Where(unit => unit.IsAlive)
                        .OrderBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints).Take(1).ToArray(),
                    "allAllies" => allies.Where(unit => unit.IsAlive).ToArray(),
                    _ => (IReadOnlyList<CombatantState>)[caster]
                };
                foreach (var ally in scope)
                {
                    var index = ally.Buffs.FindIndex(current => current.BuffId == buffId);
                    if (index < 0) ally.Buffs.Add(grant);
                    else ally.Buffs[index] = ally.Buffs[index] with
                    {
                        ExpiresAtSeconds = grant.ExpiresAtSeconds,
                        Stacks = Math.Min(stackCap, ally.Buffs[index].Stacks + 1)
                    };
                }
            }
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, caster.Id, 0, true,
                spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, caster.Hitpoints)
            {
                AbilityId = id,
                AttackerManapointsAfter = caster.Manapoints,
                TargetManapointsAfter = caster.Manapoints
            });
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
        var maxTargets = ability.TryGetProperty("maxTargets", out var maxTargetsProperty)
            && maxTargetsProperty.TryGetInt32(out var parsed) ? parsed : 0;
        if (targetType == "allEnemies")
        {
            // AoE 对一切施放单位通用：按威胁顺序不可重复抽取至多 maxTargets 个敌方目标。
            foreach (var picked in SelectTargets(opponents, maxTargets > 0 ? maxTargets : int.MaxValue,
                encounter.Seed, side, count, 17))
                ResolveAttack(encounter, caster, picked, side, encounter.ElapsedSeconds, ref events, stats, count, id);
            return;
        }
        var single = target is { IsAlive: true } ? target : SelectTarget(opponents, encounter.Seed, side, count, 16);
        if (single is not null)
            ResolveAttack(encounter, caster, single, side, encounter.ElapsedSeconds, ref events, stats, count, id);
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
        resistance *= parried ? 2 : 1;
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
        // 免伤为负向 Linear 点数：+x 点 → 承受伤害 ×100/(100+x)，负点反向增伤；
        // 战内 Buff 免伤（BuffAbsorptionPoints）与面板/临时免伤在同一乘区合并。
        damage *= PercentValue.Multiplier(
            -(defense.DamageAbsorptionPoints + target.BonusAbsorptionPoints + target.BuffAbsorptionPoints),
            PercentCurveMode.Linear);
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
        // 主事件先行占位，Aftermath（吸血/吸蓝/反伤）结算完成后回填双方灵力真值；
        // 衍生事件追加在主事件之后，保持战斗日志时序不变。
        var mainEventIndex = events.Count;
        events.Add(new CombatEvent(at, attacker.Id, target.Id, context.Damage, context.Hit, attack.DamageKind, target.Hitpoints)
        { Critical = context.Critical, Break = context.Broken, Parried = context.Parried, AbilityId = abilityId });
        CombatHookPipeline.ApplyAftermath(context);
        events[mainEventIndex] = events[mainEventIndex] with
        {
            AttackerManapointsAfter = attacker.Manapoints,
            TargetManapointsAfter = target.Manapoints
        };
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
