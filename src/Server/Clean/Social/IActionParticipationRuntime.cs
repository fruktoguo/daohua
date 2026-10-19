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

    /// <summary>会话资产已确定提交后的缓存失效提示；订阅者不得等待玩家写锁。</summary>
    event Action<Guid>? WalletChanged;

    /// <summary>存在感变更后按最新在线名册结清旧模式并切换库存来源。</summary>
    ValueTask SynchronizePresenceAsync(Guid playerId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>首次回线在加载快照前结清离线阶段并退回储备；租约覆盖存在感登记，失败释放时重新仲裁。</summary>
    ValueTask<IAsyncDisposable> PrepareConnectionAsync(Guid playerId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>读取唯一检查点中的成员离线储备，不改变批算时钟。</summary>
    ValueTask<IReadOnlyList<ParticipationOfflineReserve>> LoadOfflineReservesAsync(Guid playerId,
        CancellationToken cancellationToken = default);

    /// <summary>交付冻结槽位并把开场真实实耗、初始检查点与收件箱原子提交；重复交付不重演。</summary>
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

/// <summary>角色观察卡使用的离线储备只读投影；不属于可售钱包。</summary>
public sealed record ParticipationOfflineReserve(string ItemId, string Variant, double Quantity);
