namespace Domain.Simulation;

/// <summary>单轮行动所需的可变队列字段；由聚合队列项或 ECS 行动组件直接实现。</summary>
public interface IPlayerActionState
{
    Guid Id { get; }
    ActionIntent Intent { get; }
    ActionQueueStatus Status { get; set; }
    double ProgressSeconds { get; set; }
    double RemainingRepeats { get; set; }
    double CompletedRepeats { get; set; }
    CombatExpeditionState? Expedition { get; set; }
    TravelActionState? Travel { get; set; }
}

/// <summary>
/// 计时结算借用的领域状态视图，不拥有玩家、不复制快照、不调度时间。
/// 队列与账本操作由实际状态所有者实施，保持各运行时的单写者边界。
/// </summary>
public interface IPlayerActionContext
{
    PlayerProgression Progression { get; }
    PlayerWorldState World { get; }
    /// <summary>战斗资源分段（气血/灵力池与回复游标）；战斗内外同一份可变状态。</summary>
    PlayerResourceState Resources { get; }
    PlayerCombatState Combat { get; }
    PlayerBuffState Buffs { get; }
    PlayerSettingsState Settings { get; }
    IReadOnlyList<IPlayerActionState> Actions { get; }
    void MarkDirty(PlayerSectionKind section);
    void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now);
    void RecordStats(Guid? actionInstanceId, double atUnixSeconds, Action<PlayerBattleReport> apply);
    bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1);
    bool RotateActionToTail(Guid actionInstanceId);
    /// <summary>队首高优插队（自动移动/手动赶路共用）：同类去重、挂起运行中队首、新行动立即 Running；模拟内传 incrementVersion: false。</summary>
    IPlayerActionState EnqueuePriority(ActionIntent intent, DateTimeOffset at, ActionCatalog catalog,
        TravelActionState? travel = null, bool incrementVersion = true);
}

/// <summary>资源自然窗口需要的直接分段引用；不要求任何聚合或序列化文档。</summary>
public interface IPlayerResourceContext : IPlayerActionContext
{
    PlayerId PlayerId { get; }
    PlayerLoadoutState Loadout { get; }
    PlayerDuelState Duel { get; }
    PresenceState Presence { get; }
    DateTimeOffset LastSimulatedAt { get; set; }
    long StateVersion { get; set; }
    PlayerAttributes ResolveAttributes(GameDataCatalog catalog);
    /// <summary>
    /// 当前驻留节点的遭遇池；ECS 路径经 MapNodeEntity.EncounterProfileComponent 提供，
    /// 聚合/非实体路径返回 null，由引擎回退到配表 WorldGraph 查询。旅途（在边上）为 null。
    /// </summary>
    IReadOnlyList<string>? NodeEncounterIds => null;
}
