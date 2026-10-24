using Domain.Simulation;

namespace Domain.Player;

/// <summary>会话生成的个人账本增量；保留发生时刻，消费延迟不能改变每日及离线归账。</summary>
public sealed record ActionParticipationTimedStats(double AtUnixSeconds, PlayerStatsDelta Delta);

/// <summary>
/// 唯一会话推演生成的成员投影。资产、经验与账本为增量，战斗资源及展示态为终值；
/// 不包含个人设置、实时装备或整个玩家聚合，消费不能覆盖玩家期间的其他操作。
/// </summary>
public sealed record ActionParticipationMemberSettlement(
    string SessionId,
    long Sequence,
    PlayerId PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    double Hitpoints,
    double Manapoints,
    double RegenCarrySeconds,
    PlayerCombatSkillState Skills,
    PlayerCombatBuffState Buffs,
    CombatExpeditionState? Expedition,
    double ProgressSeconds,
    double CompletedRepeats,
    IReadOnlyList<ActionParticipationTimedStats> Stats,
    IReadOnlyList<AssetDelta> AssetDeltas,
    IReadOnlyList<SimulationEvent> Events,
    IReadOnlyList<CombatUnitStatDocument>? CombatStats,
    long DroppedEventCount = 0,
    double? EndsAtUnixSeconds = null,
    IReadOnlyList<DuelStagedItem>? StagedItems = null,
    IReadOnlyList<SessionConsumableSlot>? ConsumableSlots = null);

/// <summary>
/// 队伍会话的恢复检查点：冻结输入与唯一远征态共同恢复战斗，成员不持有可执行副本。
/// 自然回复余数按成员保存，不以队长周期覆盖个人进度。
/// 个人结算 inbox 由宿主持久化，与本检查点在同一事务中提交。
/// </summary>
public sealed record ActionParticipationSessionDocument(
    ActionParticipationSpec Spec,
    string GameDataFingerprint,
    double AdvancedAtUnixSeconds,
    long Sequence,
    CombatExpeditionState? Expedition,
    PlayerCombatSkillState Skills,
    PlayerCombatBuffState Buffs,
    IReadOnlyList<double> MemberRegenCarrySeconds,
    double CompletedRepeats,
    IReadOnlyList<CombatUnitStatDocument> CombatStats,
    double? EndsAtUnixSeconds = null,
    IReadOnlyList<SessionConsumableSlot>? ConsumableLedger = null,
    ActionParticipationInventoryMode InventoryMode = ActionParticipationInventoryMode.Online,
    double ModeSinceUnixSeconds = 0,
    double NextConsumableEvaluationAtUnixSeconds = 0,
    double OfflineBatchBoundaryUnixSeconds = 0,
    IReadOnlyList<SessionConsumableBalance>? OfflineReserves = null);

/// <summary>会话库存来源；在线只用工作钱包，离线只用检查点储备。</summary>
public enum ActionParticipationInventoryMode
{
    /// <summary>至少一名成员在线，按真实可用钱包结算。</summary>
    Online,
    /// <summary>全员离线，批内只消耗已提交储备。</summary>
    Offline
}

/// <summary>独立离线储备余额，按成员、物品与变体唯一；不与槽位数量绑定。</summary>
public sealed record SessionConsumableBalance(int MemberIndex, string ItemId, string Variant, double Quantity)
{
    /// <summary>持久化余额不变量。</summary>
    public bool IsValid => MemberIndex >= 0 && !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && Variant is not null && Variant.Length <= 128 && double.IsFinite(Quantity) && Quantity >= 0;
}

/// <summary>会话消耗槽的独立时钟；库存不在槽行中保存，散场时续写成员同配置槽。</summary>
public sealed record SessionConsumableSlot(
    int MemberIndex,
    string ItemId,
    string Group,
    int SlotIndex,
    double CooldownUntilUnixSeconds = 0,
    double SustainedExpiresAtUnixSeconds = 0)
{
    /// <summary>持久化不变量。</summary>
    public bool IsValid => MemberIndex >= 0
        && !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && (Group == AutoUseSlotState.SlotGroupGeneral || Group == AutoUseSlotState.SlotGroupPotion)
        && SlotIndex >= 0
        && double.IsFinite(CooldownUntilUnixSeconds) && CooldownUntilUnixSeconds >= 0
        && double.IsFinite(SustainedExpiresAtUnixSeconds) && SustainedExpiresAtUnixSeconds >= 0;
}

/// <summary>配表指纹不一致时的安全收尾结果：不读取新配表、不产生收益，只关闭旧检查点。</summary>
public sealed record ActionParticipationSessionFinalization(
    ActionParticipationSessionDocument Document,
    IReadOnlyList<ActionParticipationMemberSettlement> Settlements,
    IReadOnlyList<AssetDelta> AssetDeltas);
