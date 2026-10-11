namespace Server.Clean.Social;

/// <summary>
/// 组队讨伐会话宿主边界：会话唯一推进与持久化独立于成员玩家所有权，
/// 成员运行时只读取并原子消费个人结算，不把玩家聚合交给会话宿主。
/// </summary>
public interface IActionParticipationRuntime
{
    /// <summary>最近一次在线推进的最早会话可见边界；全员离线会话不参与唤醒。</summary>
    DateTimeOffset? NextAdvanceBoundaryUtc { get; }

    /// <summary>已发现的活跃或待消费成员提示；首次装载仍须查询持久化真源。</summary>
    bool RequiresReconciliation(Guid playerId);

    /// <summary>交付冻结开场输入并持久化初始权威检查点；重复交付不覆盖已有推进。</summary>
    ValueTask CreateAsync(Guid partyId, ActionParticipationSpec spec, CancellationToken cancellationToken = default);

    /// <summary>只推进至少一名成员在线的活跃会话；全员离线时不执行战斗推演。</summary>
    ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default);

    /// <summary>
    /// 全员离线会话的离线挂机批算（总纲18 §14.6）：名册无在线成员的会话按批算间隔推进到目标，
    /// 推进不得越过名册离线成员最早的离线边界（越界以 member-offline-timeout 收尾）。
    /// 返回推进过的会话名册成员 ID（去重），由调用方逐成员强拉消费结算。
    /// </summary>
    ValueTask<IReadOnlyList<Guid>> AdvanceOfflineDueAsync(DateTimeOffset now, TimeSpan batchInterval,
        CancellationToken cancellationToken = default);

    /// <summary>成员装载或命令前推进其活跃会话，再读取待消费结算；首次回线承担冷算。</summary>
    ValueTask<IReadOnlyList<ParticipationInboxItem>> AdvanceForMemberAsync(Guid playerId,
        DateTimeOffset target, CancellationToken cancellationToken = default);

    /// <summary>在会话门内读取个人待消费批次与同序号检查点，临时补齐展示投影；不再次推进战斗。</summary>
    ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId,
        CancellationToken cancellationToken = default);

    /// <summary>推进至散场边界，并将最终个人结算与散场检查点原子持久化。</summary>
    ValueTask EndAsync(string sessionId, DateTimeOffset at, string reason, CancellationToken cancellationToken = default);
}
