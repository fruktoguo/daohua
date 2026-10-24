using System.Text.Json.Serialization;
using Domain.Combat;

namespace Domain.Player;

/// <summary>
/// 行动参与成员冻结快照：会话创建时由成员面板结算冻结，
/// 作为唯一队伍会话的确定性输入；成员只消费该会话的个人结算投影。
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
/// <param name="CombatBuffs">战内 Buff 跨波携带态冻结值（剩余秒数口径）；与 CombatSkills 同帧定格。</param>
/// <param name="SustainedBuffs">开打定格的激活持续消耗品 Buff；会话内由独立槽时钟续写剩余时长。</param>
/// <param name="Equipment">开打定格的已穿戴装备清单（物品 ID + 强化等级）；仅用于详情弹窗
/// 装配展示，不参与共享剧本结算——与冻结面板同源采集，定格口径与其他成员可见信息一致。</param>
/// <param name="RegenCarrySeconds">开场自然回复周期的个人进度余数。</param>
/// <param name="ConsumableSlots">开打冻结的非耐久战斗消耗品槽配置与时钟；库存由会话工作钱包或离线储备提供。</param>
/// <param name="InternalArt">开打定格的 0 号内功位心法：内功不进施放序列，冻结技能槽里没有它，
/// 但六维乘率与修为增减点必须在会话内按成员逐人结算，故单列一份冻结输入。</param>
/// <param name="RespawnPolicy">成员的复活时机策略冻结值（总纲 §7.9.4.1）：倒地后是否立即起身由本人设定裁决。</param>
/// <param name="RespawnTargetPlayerId">指定队友存活策略的观察对象玩家 ID（成员序内的 PlayerId 文本）；
/// 仅当策略为该分支且目标在册时有效，未指定/目标离册时退化为直接复活。</param>
public sealed partial record ActionParticipantSnapshot(
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
    PlayerCombatBuffState? CombatBuffs = null,
    IReadOnlyList<ActionSustainedBuff>? SustainedBuffs = null,
    IReadOnlyList<PlayerEquipmentSlot>? Equipment = null,
    double RegenCarrySeconds = 0,
    IReadOnlyList<ActionConsumableSlot>? ConsumableSlots = null,
    string Avatar = "player",
    InternalArtSnapshot? InternalArt = null,
    CombatRespawnPolicy RespawnPolicy = CombatRespawnPolicy.Immediate,
    string? RespawnTargetPlayerId = null)
{
    /// <summary>成员快照持久化不变量校验。</summary>
    public bool IsValid => Guid.TryParse(PlayerId, out var id) && id != Guid.Empty
        && !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 64
        && RealmLevel >= 0
        && double.IsFinite(DropBonusPoints)
        && double.IsFinite(SearchSpeedPoints) && SearchSpeedPoints >= 0
        && double.IsFinite(InitialHitpoints) && InitialHitpoints >= 0
        && double.IsFinite(InitialManapoints) && InitialManapoints >= 0
        && double.IsFinite(RegenCarrySeconds) && RegenCarrySeconds >= 0 && RegenCarrySeconds < 5
        && Stats is not null
        && Skills is not null && Skills.Count <= 16 && Skills.All(slot => slot is not null)
        && (ExternalBuffs is null || ExternalBuffs.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && double.IsFinite(pair.Value) && pair.Value >= 0))
        && (CombatSkills is null || CombatSkills.IsValid)
        && (CombatBuffs is null || CombatBuffs.IsValid)
        && (SustainedBuffs is null || SustainedBuffs.All(buff => buff is not null && buff.IsValid))
        && (Equipment is null || (Equipment.Count <= 24 && Equipment.All(item => item is not null
            && !string.IsNullOrWhiteSpace(item.ItemId) && item.ItemId.Length <= 128
            && double.IsFinite(item.EnhancementLevel) && item.EnhancementLevel >= 0)))
        && (ConsumableSlots is null || (ConsumableSlots.Count <= AutoUseSlotState.CombatSlotCount
            && ConsumableSlots.All(slot => slot is not null && slot.IsValid)
            && ConsumableSlots.Select(slot => (slot.Group, slot.SlotIndex)).Distinct().Count() == ConsumableSlots.Count))
        && (InternalArt is null || InternalArt.IsValid)
        && Enum.IsDefined(RespawnPolicy)
        && (RespawnTargetPlayerId is null
            || (Guid.TryParse(RespawnTargetPlayerId, out var targetId) && targetId != Guid.Empty));
}

/// <summary>
/// 开打定格的内功心法（0 号内功位）：只服务会话内的个人结算——六维经验按
/// <c>rootExpMultipliers × 参悟效率</c> 重分配、修为收益乘 <c>cultivationExpBonusPoints</c>；
/// 不参与共享战斗推演，也不进入成员技能槽。
/// </summary>
/// <param name="AbilityId">内功功法 ID（配表 <c>discipline=internal</c>）。</param>
/// <param name="Level">开打定格的参悟等级，与面板解析同口径（槽位等级与经验派生等级取高者）。</param>
public sealed record InternalArtSnapshot(string AbilityId, double Level)
{
    /// <summary>冻结输入不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(AbilityId) && AbilityId.Length <= 128
        && double.IsFinite(Level) && Level >= 1;
}

/// <summary>开打定格的持续消耗品 Buff：成员快照内以剩余时长承载，入场时 ExpiresAt = 战斗时钟 + 定格剩余。</summary>
/// <param name="BuffId">Buff 标识。</param>
/// <param name="RemainingSeconds">定格时刻的剩余生效秒数。</param>
/// <param name="Stacks">层数（与战内 Buff 同口径）。</param>
/// <param name="Name">展示名。</param>
/// <param name="TeamWide">全队生效（阵法类消耗品）：注入己方全部在场单位而非仅使用者；
/// 多名成员同 buffId 阵法时按成员序后者覆盖（与战内 Buff RemoveAll+Add 同口径）。</param>
/// <param name="Effects">数值效果通道载荷（与战内 CombatBuffState 共享形态）。</param>
/// <param name="StatRatios">属性百分比修饰（战外折算消费）。</param>
public sealed record ActionSustainedBuff(
    string BuffId,
    double RemainingSeconds,
    double Stacks = 1,
    string Name = "",
    bool TeamWide = false,
    IReadOnlyList<BuffEffect>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null)
{
    /// <summary>持久化不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(BuffId) && BuffId.Length <= 128
        && double.IsFinite(RemainingSeconds) && RemainingSeconds > 0
        && double.IsFinite(Stacks) && Stacks > 0
        && (Effects is null || Effects.All(effect => effect is not null
            && Enum.IsDefined(effect.Channel) && double.IsFinite(effect.Value)))
        && (StatRatios is null || StatRatios.Values.All(double.IsFinite));
}

/// <summary>
/// 开打冻结的战斗消耗品槽位，仅保存配置与时钟，不冻结库存数量。
/// 相同成员的同物品多槽共用独立余额，槽位只决定判定顺序与冷却。
/// </summary>
/// <param name="ItemId">消耗品 ID。</param>
/// <param name="ActionType">栏域标识，战斗消耗品恒为 combat。</param>
/// <param name="Group">槽位组（general/potion）。</param>
/// <param name="SlotIndex">组内槽位下标。</param>
/// <param name="CooldownUntilUnixSeconds">冻结的即时消耗品冷却钟（Unix 秒）。</param>
/// <param name="SustainedRemainingSeconds">冻结的已充能持续时长（秒）；0 表示未生效。</param>
public sealed record ActionConsumableSlot(
    string ItemId,
    string ActionType,
    string Group,
    int SlotIndex,
    double CooldownUntilUnixSeconds = 0,
    double SustainedRemainingSeconds = 0)
{
    /// <summary>持久化不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && !string.IsNullOrWhiteSpace(ActionType) && ActionType.Length <= 32
        && (Group == AutoUseSlotState.SlotGroupGeneral || Group == AutoUseSlotState.SlotGroupPotion)
        && SlotIndex >= 0
        && double.IsFinite(CooldownUntilUnixSeconds) && CooldownUntilUnixSeconds >= 0
        && double.IsFinite(SustainedRemainingSeconds) && SustainedRemainingSeconds >= 0;
}

/// <summary>
/// 行动参与会话冻结快照：会话创建时由参与成员面板结算冻结，
/// 作为唯一队伍会话的确定性输入；成员不再独立推演该战斗。
/// </summary>
/// <param name="SessionId">会话唯一标识（组织层裁决产生，Guid 文本）。</param>
/// <param name="EncounterId">目标遭遇 ID：会话创建时按参与目标节点遭遇池钉死，会话内不走节点池重锚。</param>
/// <param name="NodeId">参与目标节点：诊断与重锚豁免记录。</param>
/// <param name="Seed">会话随机种子：波次选择、战斗抽样与掉落/归属掷点的公共源。</param>
/// <param name="SearchSpeedPoints">参与者寻敌速度点数（行动速度通道）：波间搜寻时长 ÷(1+点数/100)，对全员一致。</param>
/// <param name="StartedAtUnixSeconds">会话开打墙钟（Unix 秒）。</param>
/// <param name="Members">在场成员冻结快照，顺序即盟友阵列顺序（本机成员的下标为 SelfIndex）。</param>
public sealed record ActionParticipationSpec(
    string SessionId,
    string EncounterId,
    string NodeId,
    long Seed,
    double SearchSpeedPoints,
    double StartedAtUnixSeconds,
    IReadOnlyList<ActionParticipantSnapshot> Members)
{
    /// <summary>参与绑定对应的真实行动 ID。</summary>
    public ActionId ActionId { get; init; }

    /// <summary>
    /// 队伍斗法开关（总纲 §7.10.4）：会话创建时从队伍档案冻结——勾选斗法的会话整队
    /// 作为一个匹配单位进入斗法撮合；档案值与会话冻结值漂移即散场重建。
    /// </summary>
    public bool PvpEnabled { get; init; }

    /// <summary>
    /// 目标节点封魔阵类怪物削弱比率（开打定格冻结）：会话内怪物模板生成按 ×(1−ratio) 折减
    /// 气血上限与双攻双防，各成员推演逐位一致；缺省解码为 0（无削弱）。
    /// </summary>
    public double MonsterWeakenRatio { get; init; }

    /// <summary>会话规格持久化不变量。</summary>
    public bool IsValid => ActionId.IsValid
        && !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 64
        && !string.IsNullOrWhiteSpace(EncounterId) && EncounterId.Length <= 128
        && !string.IsNullOrWhiteSpace(NodeId) && NodeId.Length <= 128
        && double.IsFinite(SearchSpeedPoints) && SearchSpeedPoints >= 0
        && double.IsFinite(MonsterWeakenRatio) && MonsterWeakenRatio >= 0
        && double.IsFinite(StartedAtUnixSeconds) && StartedAtUnixSeconds >= 0
        && Members is { Count: >= 1 and <= 5 } && Members.All(member => member is not null && member.IsValid)
        && Members.Select(member => Guid.Parse(member.PlayerId)).Distinct().Count() == Members.Count;

    /// <summary>本机成员在盟友阵列中的下标；未找到返回 -1（规格不合法时不应进入会话）。</summary>
    public int SelfIndexOf(PlayerId playerId)
    {
        var text = playerId.Value.ToString("D");
        for (var i = 0; i < Members.Count; i++)
            if (string.Equals(Members[i].PlayerId, text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// 等级惩罚比率（MWI Level Malus）：成员境界低于队内最高 20%（level × 0.2）或低于 12 级（取更高/更宽容者）以内不受罚；
    /// 超出免罚门槛后每超 1% 罚 3%，封顶 90%。返回 [0, 0.9]。
    /// </summary>
    public static double CalculateLevelMalus(int memberLevel, int highestLevel)
    {
        if (highestLevel <= 0 || memberLevel <= 0) return 0;
        var gap = highestLevel - memberLevel;
        var exemptGap = Math.Max(memberLevel * 0.2, 12.0);
        if (gap <= exemptGap) return 0;
        return Math.Min(0.9, 3 * ((gap - exemptGap) / memberLevel));
    }

    /// <summary>
    /// 等级惩罚比率（MWI Level Malus）：成员境界低于队内最高 20% 或低于 12 级（取更高/更宽容者）以内不受罚，
    /// 超出后每超 1% 罚 3%，封顶 90%。返回 [0, 0.9]。
    /// </summary>
    public double LevelMalusFor(int memberIndex)
    {
        var highest = 0;
        for (var i = 0; i < Members.Count; i++) highest = Math.Max(highest, Members[i].RealmLevel);
        return CalculateLevelMalus(Members[memberIndex].RealmLevel, highest);
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

/// <summary>成员持久化会话引用；冻结输入集中存放在唯一会话检查点。</summary>
public sealed record ActionParticipationReference(string SessionId);

/// <summary>成员队首只持有会话引用、个人消费游标及权威散场时刻。</summary>
public sealed record ActionParticipationBinding
{
    /// <summary>引用信封；沿用参与载荷的 spec 槽位，内容只含会话标识。</summary>
    [JsonPropertyName("spec")]
    public ActionParticipationReference Reference { get; init; }
    /// <summary>唯一会话标识；持久化恢复后由宿主按此引用装配展示规格。</summary>
    [JsonIgnore]
    public string SessionId => Reference.SessionId;
    /// <summary>运行时只读展示输入；不进入成员存档，也不能授权成员计算战斗。</summary>
    [JsonIgnore]
    public ActionParticipationSpec? Spec { get; init; }
    /// <summary>权威散场墙钟；+∞ 表示进行中。</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double EndsAtUnixSeconds { get; init; }
    /// <summary>已应用的个人结算序号，与个人变更原子持久化。</summary>
    public long AppliedSequence { get; init; }

    /// <summary>持久化只恢复会话引用；冻结输入始终由唯一会话检查点供给。</summary>
    [JsonConstructor]
    public ActionParticipationBinding(ActionParticipationReference reference, double endsAtUnixSeconds = double.PositiveInfinity,
        long appliedSequence = 0)
    {
        Reference = reference;
        EndsAtUnixSeconds = endsAtUnixSeconds;
        AppliedSequence = appliedSequence;
    }

    /// <summary>以会话标识创建成员引用。</summary>
    public ActionParticipationBinding(string sessionId, double endsAtUnixSeconds = double.PositiveInfinity,
        long appliedSequence = 0) : this(new ActionParticipationReference(sessionId), endsAtUnixSeconds, appliedSequence) { }

    /// <summary>开场时附带当前会话展示规格；导出成员载荷时仍只序列化引用。</summary>
    public ActionParticipationBinding(ActionParticipationSpec spec, double endsAtUnixSeconds = double.PositiveInfinity,
        long appliedSequence = 0) : this(spec.SessionId, endsAtUnixSeconds, appliedSequence) => Spec = spec;

    /// <summary>持久化不变量：引用及消费游标合法，散场时刻有限或 +∞。</summary>
    public bool IsValid => Reference is not null && !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 64
        && AppliedSequence >= 0
        && (double.IsPositiveInfinity(EndsAtUnixSeconds)
            || (double.IsFinite(EndsAtUnixSeconds) && EndsAtUnixSeconds >= 0));
}
