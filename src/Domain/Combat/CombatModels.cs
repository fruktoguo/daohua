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
    /// <summary>攻击五行属性；None 时跳过五行增减伤匹配。</summary>
    public CombatElement Element { get; init; }

    // ── 增减伤点数（总纲第七篇 §7.7.5：增伤 Linear / 减伤 NonLinear，100 点 = 100%）──
    /// <summary>物理增伤点数（Linear：作用于物理大类伤害的攻方乘区）。</summary>
    public double PhysicalDamageBonusPoints { get; init; }
    /// <summary>法术增伤点数（Linear：作用于术法大类伤害的攻方乘区）。</summary>
    public double SpellDamageBonusPoints { get; init; }
    /// <summary>金系增伤点数（攻击携带金元素时生效）。</summary>
    public double MetalDamageBonusPoints { get; init; }
    /// <summary>木系增伤点数。</summary>
    public double WoodDamageBonusPoints { get; init; }
    /// <summary>水系增伤点数。</summary>
    public double WaterDamageBonusPoints { get; init; }
    /// <summary>火系增伤点数。</summary>
    public double FireDamageBonusPoints { get; init; }
    /// <summary>土系增伤点数。</summary>
    public double EarthDamageBonusPoints { get; init; }
    /// <summary>总增伤点数（Linear：任意伤害通用攻方乘区）。</summary>
    public double TotalDamageBonusPoints { get; init; }
    /// <summary>普攻增幅点数（Linear：普攻与带「普通攻击」tag 技能的增伤修饰）。</summary>
    public double BasicAttackBonusPoints { get; init; }
    /// <summary>技能增幅点数（Linear：不带「普通攻击」tag 技能的增伤修饰）。</summary>
    public double SkillBonusPoints { get; init; }
    /// <summary>物理减伤点数（NonLinear：守方承受物理伤害的收敛减伤）。</summary>
    public double PhysicalDamageReductionPoints { get; init; }
    /// <summary>法术减伤点数。</summary>
    public double SpellDamageReductionPoints { get; init; }
    /// <summary>金系减伤点数（承受金元素攻击时生效）。</summary>
    public double MetalDamageReductionPoints { get; init; }
    /// <summary>木系减伤点数。</summary>
    public double WoodDamageReductionPoints { get; init; }
    /// <summary>水系减伤点数。</summary>
    public double WaterDamageReductionPoints { get; init; }
    /// <summary>火系减伤点数。</summary>
    public double FireDamageReductionPoints { get; init; }
    /// <summary>土系减伤点数。</summary>
    public double EarthDamageReductionPoints { get; init; }
    /// <summary>总减伤/免伤点数（NonLinear：末位收口，负点反向增伤）。</summary>
    public double DamageAbsorptionPoints { get; init; }

    // ── 特殊属性点数（总纲第七篇 §7.7.5 特殊属性清单）──
    /// <summary>攻击速度点数（NonLinear：仅缩短普攻读条时间）。</summary>
    public double AttackSpeedPoints { get; init; }
    /// <summary>行动速度点数（NonLinear：缩短全部读条时间；普攻上与攻速乘算双重除数）。</summary>
    public double ActionSpeedPoints { get; init; }
    /// <summary>冷却缩减点数（NonLinear：缩短全部冷却时间）。</summary>
    public double CooldownReductionPoints { get; init; }
    /// <summary>威胁增幅点数（Linear：有效威胁 = 累计威胁 ×(1+点数/100)）。</summary>
    public double ThreatBonusPoints { get; init; }
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
            Critical, CriticalResistance, BreakPower, ParryResolve, HpRegen, MpRegen];
        double[] finite = [DamageAbsorptionPoints, ThreatBonusPoints,
            PhysicalDamageBonusPoints, SpellDamageBonusPoints, TotalDamageBonusPoints,
            BasicAttackBonusPoints, SkillBonusPoints,
            PhysicalDamageReductionPoints, SpellDamageReductionPoints,
            MetalDamageBonusPoints, WoodDamageBonusPoints, WaterDamageBonusPoints,
            FireDamageBonusPoints, EarthDamageBonusPoints,
            MetalDamageReductionPoints, WoodDamageReductionPoints, WaterDamageReductionPoints,
            FireDamageReductionPoints, EarthDamageReductionPoints,
            AttackSpeedPoints, ActionSpeedPoints, CooldownReductionPoints];
        if (nonnegative.Any(value => !double.IsFinite(value) || value < 0)
            || finite.Any(value => !double.IsFinite(value))
            || !double.IsFinite(MaxHitpoints) || MaxHitpoints <= 0
            || !double.IsFinite(AttackIntervalSeconds) || AttackIntervalSeconds <= 0
            || !double.IsFinite(ResistanceScale) || ResistanceScale <= 0
            || !double.IsFinite(CriticalMultiplier) || CriticalMultiplier < 1
            || !double.IsFinite(BasicAttackRatio) || BasicAttackRatio <= 0
            || !double.IsFinite(CastTimeFactor) || CastTimeFactor <= 0
            || !double.IsFinite(CooldownFactor) || CooldownFactor <= 0
            || !double.IsFinite(SkillIntensity) || SkillIntensity <= 0
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
/// <param name="AttackSpeedPoints">攻击速度点数：并入普攻读条攻速除数通道。</param>
/// <param name="DamageAbsorptionPoints">免伤点数：与面板免伤同乘区合并（NonLinear 收口）。</param>
/// <param name="ThreatPoints">威胁增幅点数：叠加进有效威胁乘区。</param>
public sealed record CombatBuffState(
    string BuffId, double ExpiresAtSeconds, double Stacks = 1,
    double HpRegen = 0, double MpRegen = 0, double AttackSpeedPoints = 0,
    double DamageAbsorptionPoints = 0, double ThreatPoints = 0)
{
    /// <summary>校验 Buff 字段满足持久化不变量。</summary>
    internal bool IsValid => !string.IsNullOrWhiteSpace(BuffId) && BuffId.Length <= 128
        && double.IsFinite(ExpiresAtSeconds) && ExpiresAtSeconds >= 0
        && double.IsFinite(Stacks) && Stacks > 0
        && double.IsFinite(HpRegen) && double.IsFinite(MpRegen)
        && double.IsFinite(AttackSpeedPoints) && double.IsFinite(DamageAbsorptionPoints)
        && double.IsFinite(ThreatPoints);
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
    /// <summary>
    /// ECS 宿主挂接的单位实体标识；-1 表示未绑定实体。
    /// 运行时字段，不进入快照——Attach/恢复挂接时由宿主回填，纯领域路径恒为 -1。
    /// </summary>
    public long EntityId { get; set; } = -1;
    /// <summary>配表怪物定义 ID（玩家为 null）。</summary>
    public string? DefinitionId { get; set; }
    /// <summary>战斗静态属性面板；进行中战斗跨步复用时由 <see cref="RefreshStats"/> 换入最新结算结果。</summary>
    public CombatantStats Stats { get; private set; }
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
    /// <summary>嘲讽/威胁增幅加成点数；有效威胁 = Threat ×(1 + 合计增幅点数/100)。</summary>
    public double ThreatBonusPoints { get; internal set; }
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
    /// <summary>参与目标抽取的有效威胁值：Threat ×(1 + (面板威胁增幅 + 嘲讽修正 + 战内 Buff 威胁)/100)。</summary>
    public double EffectiveThreat => Threat * Math.Max(0, 1 + (Stats.ThreatBonusPoints + ThreatBonusPoints + BuffThreatPoints) / 100);
    /// <summary>战内 Buff 合计攻击速度点数。</summary>
    internal double BuffAttackSpeedPoints
    {
        get
        {
            var total = 0d;
            for (var i = 0; i < Buffs.Count; i++) total += Buffs[i].AttackSpeedPoints * Buffs[i].Stacks;
            return total;
        }
    }
    /// <summary>战内 Buff 合计免伤点数（与面板免伤同乘区）。</summary>
    internal double BuffAbsorptionPoints
    {
        get
        {
            var total = 0d;
            for (var i = 0; i < Buffs.Count; i++) total += Buffs[i].DamageAbsorptionPoints * Buffs[i].Stacks;
            return total;
        }
    }
    /// <summary>战内 Buff 合计威胁增幅点数。</summary>
    internal double BuffThreatPoints
    {
        get
        {
            var total = 0d;
            for (var i = 0; i < Buffs.Count; i++) total += Buffs[i].ThreatPoints * Buffs[i].Stacks;
            return total;
        }
    }

    /// <summary>
    /// 换入重新结算的战斗属性面板：供进行中战斗在外层推进步间跨步复用时同步最新属性输入
    /// （战外 Buff 增减/到期、六维经验等），沿用恢复快照口径的越界校验——
    /// 当前气血/灵力超出新上限即拒绝，与 <see cref="CombatEncounterState.Restore"/> 逐位等价。
    /// </summary>
    public void RefreshStats(CombatantStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        stats.Validate();
        // 与 ApplySnapshot 的气血/灵力口径逐位一致：非负有限且不超出新面板上限。
        if (!double.IsFinite(Hitpoints) || Hitpoints < 0 || Hitpoints > stats.MaxHitpoints
            || !double.IsFinite(Manapoints) || Manapoints < 0 || Manapoints > stats.MaxManapoints)
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗单位状态无效。");
        Stats = stats;
    }

    /// <summary>Buff 层数求值：战内 Buff 层数 + 宿主注入的战外视图；buffId 为 null 时合计全部层数。</summary>
    internal double BuffStacks(string? buffId)
    {
        // 按列表原序累加，与原 LINQ Where+Sum 的累加序位级一致。
        var total = 0d;
        for (var i = 0; i < Buffs.Count; i++)
            if (buffId is null || Buffs[i].BuffId == buffId) total += Buffs[i].Stacks;
        return total + (ExternalBuffStacks?.Invoke(buffId) ?? 0);
    }
}

/// <summary>战斗单位可持久化的运行时快照。</summary>
public sealed record CombatantSnapshot(
    double Hitpoints,
    double Manapoints,
    double NextAttackAtSeconds,
    double AttackCount,
    double Threat,
    int PendingSkillIndex = -1,
    double ThreatBonusPoints = 0,
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
        && double.IsFinite(ThreatBonusPoints) && ThreatBonusPoints >= 0
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
    private CombatEncounterState(string encounterId, long seed, CombatantState player, IReadOnlyList<CombatantState> enemies,
        double playerRespawnSeconds)
    {
        EncounterId = encounterId;
        Seed = seed;
        Player = player;
        Enemies = enemies;
        PlayerRespawnSeconds = playerRespawnSeconds;
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
    /// <summary>
    /// 团灭分流：原地复活读条秒数（战斗时钟口径）。&gt; 0 时玩家阵亡不终局，进入躺尸窗口；
    /// 0 表示团灭即败（斗法/副本类遭遇），玩家方全灭立即 EnemyWon 终局。
    /// </summary>
    public double PlayerRespawnSeconds { get; }
    /// <summary>
    /// 原地复活截止点（战斗时钟秒）：玩家阵亡瞬间置为 ElapsedSeconds + PlayerRespawnSeconds，
    /// 起身后回到 +∞。倒地窗口内双方不出手，回复/冷却/到期照常推进。
    /// </summary>
    public double PlayerRespawnAtSeconds { get; internal set; } = double.PositiveInfinity;
    /// <summary>倒地窗口判定：玩家已阵亡且存在待触发复活点（仅原地复活模式可达）。</summary>
    public bool IsPlayerDowned => !Player.IsAlive && double.IsFinite(PlayerRespawnAtSeconds);
    /// <summary>
    /// 段内待派发触发器队列：伤害落账等结算点按序入队，战斗边界在同一序列点统一消费。
    /// 运行时字段，不进入快照——纯领域路径由引擎内联数学消费，ECS 宿主由 RunSystem 派发消费。
    /// </summary>
    public List<PendingCombatTrigger> PendingTriggers { get; } = [];
    /// <summary>
    /// 触发器派发口（运行时）：ECS 宿主挂接实体派发器，null 时引擎走内联数学等价路径。
    /// 不进入快照；恢复战斗由宿主在挂接时重新装配。
    /// </summary>
    internal ICombatTriggerSink? TriggerSink { get; set; }
    /// <summary>玩家方友方列表的会话缓存：玩家独自成军恒定 [Player]，出手边界每帧复用免分配。</summary>
    private CombatantState[]? _playerAllies;
    internal IReadOnlyList<CombatantState> PlayerAllies => _playerAllies ??= [Player];
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

    /// <summary>
    /// 创建遭遇但不决策首次出手：供宿主先注入跨战状态（灵力/冷却与战外 Buff 视图）再显式开局，
    /// 保证触发条件求值与灵力预扣都基于真实入场状态。
    /// 开局动作决策由战斗引擎侧 <c>DeterministicCombatEngine.Start</c> / <c>DecideInitialActions</c> 完成。
    /// </summary>
    public static CombatEncounterState Create(string encounterId, long seed, CombatantTemplate player,
        IReadOnlyList<CombatantTemplate> enemies, double playerRespawnSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(encounterId) || encounterId.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇标识无效。");
        ArgumentNullException.ThrowIfNull(player);
        if (player.Stats is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇玩家模板无效。");
        if (enemies is null || enemies.Count == 0 || enemies.Any(template => template?.Stats is null))
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇敌方阵容无效。");
        if (!double.IsFinite(playerRespawnSeconds) || playerRespawnSeconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇原地复活时长无效。");
        var states = new CombatantState[enemies.Count];
        for (var i = 0; i < enemies.Count; i++)
            states[i] = new CombatantState($"enemy:{i}", enemies[i].Stats, enemies[i].Skills);
        var playerState = new CombatantState("player", player.Stats, player.Skills);
        return new CombatEncounterState(encounterId, seed, playerState, states, playerRespawnSeconds);
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
        double sequence = 0,
        double playerRespawnSeconds = 0,
        double playerRespawnAtSeconds = double.PositiveInfinity)
    {
        // 恢复路径跳过开局决策：全部出手状态由快照逐字段回填，避免重复预扣灵力。
        var encounter = Create(encounterId, seed, player, enemies, playerRespawnSeconds);
        if (enemySnapshots is null || enemySnapshots.Count != enemies.Count
            || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0
            || !double.IsFinite(sequence) || sequence < 0
            || !Enum.IsDefined(outcome)
            || (double.IsFinite(playerRespawnAtSeconds) && playerRespawnAtSeconds < elapsedSeconds))
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化战斗状态无效。");
        ApplySnapshot(encounter.Player, playerSnapshot, player.Stats.MaxHitpoints, player.Stats.MaxManapoints);
        for (var i = 0; i < enemies.Count; i++)
            ApplySnapshot(encounter.Enemies[i], enemySnapshots[i], enemies[i].Stats.MaxHitpoints, enemies[i].Stats.MaxManapoints);
        encounter.ElapsedSeconds = elapsedSeconds;
        encounter.Outcome = outcome;
        encounter.Sequence = sequence;
        encounter.PlayerRespawnAtSeconds = playerRespawnAtSeconds;
        // 复活截止点只在倒地窗口内有意义：存活玩家携带该字段属于损坏快照。
        if (double.IsFinite(playerRespawnAtSeconds) && encounter.Player.IsAlive)
            throw new DomainException(DomainErrorCode.InvalidArgument, "持久化复活截止点与生命状态不一致。");
        var expectedOutcome = (encounter.Player.IsAlive, encounter.Enemies.Any(static e => e.IsAlive)) switch
        {
            // 倒地等待原地复活不是终局：存活敌人或已被跳死全灭都保持 Running，起身后统一裁定。
            (false, _) when double.IsFinite(playerRespawnAtSeconds) => CombatOutcome.Running,
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
            state.Threat, state.PendingSkillIndex, state.ThreatBonusPoints, state.BonusAbsorptionPoints,
            state.SkillCooldownUntil, state.SkillCastCount,
            state.Buffs.Count == 0 ? null : state.Buffs.ToArray());

    private static void ApplySnapshot(CombatantState state, CombatantSnapshot snapshot, double maxHitpoints, double maxManapoints)
    {
        if (!double.IsFinite(snapshot.Hitpoints) || snapshot.Hitpoints < 0 || snapshot.Hitpoints > maxHitpoints
            || !double.IsFinite(snapshot.Manapoints) || snapshot.Manapoints < 0 || snapshot.Manapoints > maxManapoints
            || !double.IsFinite(snapshot.NextAttackAtSeconds) || snapshot.NextAttackAtSeconds < 0
            || !double.IsFinite(snapshot.AttackCount) || snapshot.AttackCount < 0
            || !double.IsFinite(snapshot.Threat) || snapshot.Threat < 0
            || !double.IsFinite(snapshot.ThreatBonusPoints) || snapshot.ThreatBonusPoints < 0
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
        state.ThreatBonusPoints = snapshot.ThreatBonusPoints;
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
/// <param name="PlayerDeathTimestamps">本段内玩家阵亡的战斗时钟时刻（仅原地复活模式可多次出现）。</param>
/// <param name="PlayerRespawnTimestamps">本段内玩家原地起身的战斗时钟时刻。</param>
public sealed record CombatAdvanceResult(
    string EncounterId,
    double FromSeconds,
    double ToSeconds,
    CombatOutcome Outcome,
    IReadOnlyList<CombatEvent> Events,
    double Sequence,
    IReadOnlyList<double>? PlayerDeathTimestamps = null,
    IReadOnlyList<double>? PlayerRespawnTimestamps = null);

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

/// <summary>远征波次中单个敌人的可持久化战斗快照：与玩家共用同一 <see cref="CombatantSnapshot"/> 形态。</summary>
public sealed record ExpeditionEnemyState(
    string EnemyId,
    CombatantSnapshot State,
    /// <summary>正在吟唱的技能 ID（展示用；出手决策以 State.PendingSkillIndex 为准）。</summary>
    string? PendingAbilityId = null,
    /// <summary>当前动作总时长秒数（吟唱时长或普攻间隔，展示用）。</summary>
    double PendingActionDurationSeconds = 0);

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
    /// <summary>
    /// 原地复活截止点（战斗时钟秒）：仅原地复活遭遇的倒地窗口内非空，
    /// 其余相位与存活状态必须为 null。倒地窗口内 Enemies 保持原波次阵容不拆场。
    /// </summary>
    public double? PlayerRespawnAtSeconds { get; init; }

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
            && enemy.State is not null && enemy.State.IsValid
            && double.IsFinite(enemy.PendingActionDurationSeconds) && enemy.PendingActionDurationSeconds >= 0)))
        && (PlayerState is null || PlayerState.IsValid)
        // 复活截止点只存在于倒地窗口：必须落在当前战斗时钟之后；非 Combat 相位不得携带。
        && (PlayerRespawnAtSeconds is null || (double.IsFinite(PlayerRespawnAtSeconds.Value)
            && PlayerRespawnAtSeconds.Value > BattleElapsedSeconds - 1e-9 && Phase == CombatExpeditionPhase.Combat))
        && Phase switch
        {
            CombatExpeditionPhase.DuelPending => Outcome == CombatOutcome.Running
                && (Enemies is null || Enemies.Count == 0) && Duel is null,
            CombatExpeditionPhase.Duel => Outcome == CombatOutcome.Running
                && Duel is { } duel && duel.IsValid
                && (Enemies is null || Enemies.Count == 0) && PhaseElapsedSeconds <= 1e-9,
            CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search or CombatExpeditionPhase.Respawning
                => Outcome == CombatOutcome.Running && (Enemies is null || Enemies.Count == 0) && Duel is null,
            // Combat 相位允许玩家气血为 0：等价于倒地窗口存在（复活截止点非空），团灭即败遭遇不会留下该形态。
            _ => Enemies is { Count: > 0 } && PhaseElapsedSeconds <= 1e-9 && Duel is null
                && PlayerState is { }
                && (PlayerHitpoints > 1e-9 || PlayerRespawnAtSeconds is not null)
        };
}
