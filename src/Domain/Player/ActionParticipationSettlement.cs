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
    double? EndsAtUnixSeconds = null);

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
    IReadOnlyList<SessionConsumableSlot>? ConsumableLedger = null);

/// <summary>
/// 会话消耗品托管台账行：随检查点持久化，按成员槽位记录剩余托管配额与时钟（墙钟口径）。
/// 剩余配额只减不增；散场把未耗尽配额作为资产差量退回成员钱包。
/// </summary>
/// <param name="MemberIndex">成员下标（与 <see cref="ActionParticipationSpec.Members"/> 同序）。</param>
/// <param name="ItemId">托管物品 ID。</param>
/// <param name="Group">槽位组（general/potion）。</param>
/// <param name="SlotIndex">组内槽位下标。</param>
/// <param name="RemainingQuantity">托管剩余数量；durable 恒为锁定的 1 件，不消耗。</param>
/// <param name="CooldownUntilUnixSeconds">冷却钟（Unix 秒）：即时消耗品触发冷却、持续槽耗竭后冷却共用。</param>
/// <param name="SustainedExpiresAtUnixSeconds">持续消耗品生效到期墙钟（Unix 秒）；0 表示未生效。</param>
public sealed record SessionConsumableSlot(
    int MemberIndex,
    string ItemId,
    string Group,
    int SlotIndex,
    double RemainingQuantity,
    double CooldownUntilUnixSeconds = 0,
    double SustainedExpiresAtUnixSeconds = 0)
{
    /// <summary>持久化不变量。</summary>
    public bool IsValid => MemberIndex >= 0
        && !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && (Group == AutoUseSlotState.SlotGroupGeneral || Group == AutoUseSlotState.SlotGroupPotion)
        && SlotIndex >= 0
        && double.IsFinite(RemainingQuantity) && RemainingQuantity >= 0
        && double.IsFinite(CooldownUntilUnixSeconds) && CooldownUntilUnixSeconds >= 0
        && double.IsFinite(SustainedExpiresAtUnixSeconds) && SustainedExpiresAtUnixSeconds >= 0;
}

/// <summary>配表指纹不一致时的安全收尾结果：不读取新配表、不产生收益，只关闭旧检查点。</summary>
public sealed record ActionParticipationSessionFinalization(
    ActionParticipationSessionDocument Document,
    IReadOnlyList<ActionParticipationMemberSettlement> Settlements);
