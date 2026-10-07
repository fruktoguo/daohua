using Domain.Combat;

namespace Domain.Player;

/// <summary>
/// 组队讨伐成员冻结快照（总纲 §21.13）：会话创建时由成员面板结算冻结，
/// 作为全部在场成员共享剧本的确定性输入之一；每名成员各自的模拟按同一规格逐位重放。
/// </summary>
/// <param name="PlayerId">成员玩家 ID（文本形态便于快照直存）。</param>
/// <param name="DisplayName">成员显示名：战报标签与掉落归属文案使用。</param>
/// <param name="RealmLevel">境界等级：等级惩罚（MWI malus）与面板冻结的基准。</param>
/// <param name="DropBonusPoints">掉落增幅点数：共享掉率合计与个人归属权重。</param>
/// <param name="SearchSpeedPoints">寻敌速度点数（行动速度通道）：全队寻敌取最大值（探路最快者带路）。</param>
/// <param name="InitialHitpoints">入场气血：会话创建时的当前值（冻结）。</param>
/// <param name="InitialManapoints">入场灵力：会话创建时的当前值（冻结）。</param>
/// <param name="Stats">冻结战斗面板（境界+装备+Buff+六维结算结果）。</param>
/// <param name="Skills">冻结功法槽（装配技能与触发条件）。</param>
/// <param name="ExternalBuffs">战外持久 Buff 层数冻结视图（buffId → 层数）。</param>
/// <param name="CombatSkills">跨战技能状态（冷却/槽位）冻结值。</param>
/// <param name="SustainedBuffs">开打定格的激活持续消耗品 Buff（剩余时长冻结）；
/// 会话期间不做槽位补能/扣减——其他成员的钱包不可见，定格画面是唯一公共输入。</param>
public sealed partial record PartyMemberSnapshot(
    string PlayerId,
    string DisplayName,
    int RealmLevel,
    double DropBonusPoints,
    double SearchSpeedPoints,
    double InitialHitpoints,
    double InitialManapoints,
    CombatantStats Stats,
    IReadOnlyList<SkillSlot> Skills,
    IReadOnlyDictionary<string, double>? ExternalBuffs = null,
    PlayerCombatSkillState? CombatSkills = null,
    IReadOnlyList<PartySustainedBuff>? SustainedBuffs = null)
{
    /// <summary>成员快照持久化不变量校验。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(PlayerId) && PlayerId.Length <= 64
        && !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 64
        && RealmLevel >= 0
        && double.IsFinite(DropBonusPoints)
        && double.IsFinite(SearchSpeedPoints) && SearchSpeedPoints >= 0
        && double.IsFinite(InitialHitpoints) && InitialHitpoints >= 0
        && double.IsFinite(InitialManapoints) && InitialManapoints >= 0
        && Stats is not null
        && Skills is not null && Skills.Count <= 16 && Skills.All(slot => slot is not null)
        && (ExternalBuffs is null || ExternalBuffs.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && double.IsFinite(pair.Value) && pair.Value >= 0))
        && (SustainedBuffs is null || SustainedBuffs.All(buff => buff is not null && buff.IsValid));
}

/// <summary>开打定格的持续消耗品 Buff：成员快照内以剩余时长承载，入场时 ExpiresAt = 战斗时钟 + 定格剩余。</summary>
/// <param name="BuffId">Buff 标识。</param>
/// <param name="RemainingSeconds">定格时刻的剩余生效秒数。</param>
/// <param name="Stacks">层数（与战内 Buff 同口径）。</param>
public sealed record PartySustainedBuff(
    string BuffId,
    double RemainingSeconds,
    double Stacks = 1,
    double HpRegen = 0,
    double MpRegen = 0,
    double AttackSpeedPoints = 0,
    double DamageAbsorptionPoints = 0,
    double ThreatPoints = 0,
    double HpRegenPostRatio = 0,
    double MpRegenPostRatio = 0,
    double DamageBonusPoints = 0)
{
    /// <summary>持久化不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(BuffId) && BuffId.Length <= 128
        && double.IsFinite(RemainingSeconds) && RemainingSeconds > 0
        && double.IsFinite(Stacks) && Stacks > 0
        && double.IsFinite(HpRegen) && double.IsFinite(MpRegen)
        && double.IsFinite(AttackSpeedPoints) && double.IsFinite(DamageAbsorptionPoints)
        && double.IsFinite(ThreatPoints) && double.IsFinite(HpRegenPostRatio)
        && double.IsFinite(MpRegenPostRatio) && double.IsFinite(DamageBonusPoints);
}

/// <summary>
/// 组队讨伐会话规格：队伍共战的完整确定性输入，随会话行动持久化。
/// 全部成员的模拟消费同一份规格推演同一场战斗；阵容在会话期间不可变，任何成员变动 = 散场重建。
/// </summary>
/// <param name="SessionId">会话唯一标识（社交层裁决产生，Guid 文本）。</param>
/// <param name="EncounterId">目标遭遇 ID：会话创建时按队伍目标节点遭遇池钉死，会话内不走节点池重锚。</param>
/// <param name="NodeId">队伍目标节点：诊断与重锚豁免记录。</param>
/// <param name="Seed">会话随机种子：波次选择、战斗抽样与掉落/归属掷点的公共源。</param>
/// <param name="SearchSpeedPoints">全队寻敌速度点数（成员最大值）：波间搜寻时长 ÷(1+点数/100)，对全员一致。</param>
/// <param name="StartedAtUnixSeconds">会话开打墙钟（Unix 秒）。</param>
/// <param name="Members">在场成员冻结快照，顺序即盟友阵列顺序（本机成员的下标为 SelfIndex）。</param>
public sealed record PartyExpeditionSpec(
    string SessionId,
    string EncounterId,
    string NodeId,
    long Seed,
    double SearchSpeedPoints,
    double StartedAtUnixSeconds,
    IReadOnlyList<PartyMemberSnapshot> Members)
{
    /// <summary>会话规格持久化不变量校验。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 64
        && !string.IsNullOrWhiteSpace(EncounterId) && EncounterId.Length <= 128
        && !string.IsNullOrWhiteSpace(NodeId) && NodeId.Length <= 128
        && double.IsFinite(SearchSpeedPoints) && SearchSpeedPoints >= 0
        && double.IsFinite(StartedAtUnixSeconds) && StartedAtUnixSeconds >= 0
        && Members is { Count: >= 2 and <= 5 } && Members.All(member => member is not null && member.IsValid);

    /// <summary>本机成员在盟友阵列中的下标；未找到返回 -1（规格不合法时不应进入会话）。</summary>
    public int SelfIndexOf(PlayerId playerId)
    {
        var text = playerId.Value.ToString("D");
        for (var i = 0; i < Members.Count; i++)
            if (string.Equals(Members[i].PlayerId, text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// 等级惩罚比率（MWI Level Malus）：成员境界低于队内最高 20% 以内不受罚，
    /// 超出后每超 1% 罚 3%，封顶 90%。返回 [0, 0.9]。
    /// </summary>
    public double LevelMalusFor(int memberIndex)
    {
        var highest = 0;
        for (var i = 0; i < Members.Count; i++) highest = Math.Max(highest, Members[i].RealmLevel);
        var level = Members[memberIndex].RealmLevel;
        if (highest <= 0 || level <= 0) return 0;
        var ratio = (double)highest / level;
        return Math.Min(0.9, Math.Max(0, 3 * (ratio - 1.2)));
    }

    /// <summary>全队掉落增幅点数合计（共享掉率乘区：1 + Σ点数/100）。</summary>
    public double TotalDropBonusPoints
    {
        get
        {
            var total = 0d;
            for (var i = 0; i < Members.Count; i++) total += Members[i].DropBonusPoints;
            return total;
        }
    }

    /// <summary>单件掉落的归属权重：个人掉落增幅 ×(1-等级惩罚)，全零时退化为均等权重。</summary>
    public double AssignmentWeightFor(int memberIndex)
    {
        var weight = Math.Max(0, 1 + Members[memberIndex].DropBonusPoints / 100d) * (1 - LevelMalusFor(memberIndex));
        return weight <= 0 ? 0 : weight;
    }
}

/// <summary>
/// 队首会话行动携带的绑定：共享剧本规格 + 权威散场时刻（总纲 §21.13）。
/// 推进到 EndsAtUnixSeconds 自然收尾离队；+∞ 表示会话进行中。非会话行动为 null。
/// </summary>
/// <param name="Spec">会话规格（含全部成员冻结面板）。</param>
/// <param name="EndsAtUnixSeconds">权威散场墙钟（Unix 秒）；+∞ 表示进行中。</param>
public sealed record PartySessionBinding(PartyExpeditionSpec Spec, double EndsAtUnixSeconds = double.PositiveInfinity)
{
    /// <summary>持久化不变量：规格合法且散场时刻有限或 +∞。</summary>
    public bool IsValid => Spec is not null && Spec.IsValid
        && (double.IsPositiveInfinity(EndsAtUnixSeconds)
            || (double.IsFinite(EndsAtUnixSeconds) && EndsAtUnixSeconds >= 0));
}
