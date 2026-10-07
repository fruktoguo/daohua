namespace Domain.Simulation;

/// <summary>单轮行动所需的可变队列字段；由聚合队列项或 ECS 行动组件直接实现。</summary>
public interface IPlayerActionState
{
    /// <summary>行动实例唯一标识；入队时生成，归档与账本按它对账。</summary>
    Guid Id { get; }
    /// <summary>行动执行意图（配表动作 ID、重复次数与目标参数）。</summary>
    ActionIntent Intent { get; }
    /// <summary>当前调度状态（Pending/Running/Blocked）。</summary>
    ActionQueueStatus Status { get; set; }
    /// <summary>当前单轮已推进的秒数；达到配表单轮时长后触发单次结算。</summary>
    double ProgressSeconds { get; set; }
    /// <summary>剩余重复次数；-1 代表无限循环，模拟推进单轮完成后不得自动出队。</summary>
    double RemainingRepeats { get; set; }
    /// <summary>已成功完成的轮次计数。</summary>
    double CompletedRepeats { get; set; }
    /// <summary>战斗行动当前远征状态（相位/波次/战斗时钟）；普通行动为 null。</summary>
    CombatExpeditionState? Expedition { get; set; }
    /// <summary>移动行动当前路径与路段行进状态；普通行动为 null。</summary>
    TravelActionState? Travel { get; set; }
    /// <summary>组队会话绑定（party.expedition 伪行动携带共享剧本规格与权威散场时刻）；非会话行动为 null。</summary>
    Domain.Player.PartySessionBinding? PartySession => null;
}

/// <summary>
/// 计时结算借用的领域状态视图，不拥有玩家、不复制快照、不调度时间。
/// 队列与账本操作由实际状态所有者实施，保持各运行时的单写者边界。
/// </summary>
public interface IPlayerActionContext
{
    /// <summary>成长分段：修为/底蕴/战斗经验/境界与道龄。</summary>
    PlayerProgression Progression { get; }
    /// <summary>世界漫游分段：驻留节点、旅途进度与探索种子。</summary>
    PlayerWorldState World { get; }
    /// <summary>战斗资源分段（气血/灵力池与回复游标）；战斗内外同一份可变状态。</summary>
    PlayerResourceState Resources { get; }
    /// <summary>基础战斗属性分段：装备/功法/境界加成前的基础值。</summary>
    PlayerCombatState Combat { get; }
    /// <summary>临时增益/减益分段。</summary>
    PlayerBuffState Buffs { get; }
    /// <summary>客户端偏好与挂机自动化配置分段。</summary>
    PlayerSettingsState Settings { get; }
    /// <summary>行动队列只读视图；结构变更须经队列规则原语，不在此处直接增删。</summary>
    IReadOnlyList<IPlayerActionState> Actions { get; }
    /// <summary>标记分段待持久化；模拟内推进由轮结算统一提交，避免脏流水膨胀。</summary>
    void MarkDirty(PlayerSectionKind section);
    /// <summary>确保指定行动实例的战报账本存在；单轮结算与归档前调用，缺失时按参数补建。</summary>
    void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now);
    /// <summary>把一批已结算增量写入行动账本；只搬运数值，不重算奖励。</summary>
    void RecordStats(Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta);
    /// <summary>移除指定行动实例并归档账本；实例不存在时返回 false 不产生副作用。</summary>
    /// <returns>是否实际移除。</returns>
    bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1);
    /// <summary>把指定行动实例轮转到队尾（手动轮换用）；实例不存在时返回 false。</summary>
    bool RotateActionToTail(Guid actionInstanceId);
    /// <summary>收益落账后的轮完成裁决；只有重复次数为 0 才循环或离队。</summary>
    PlayerActionRoundDisposition FinishActionRound(Guid actionInstanceId, bool loopable, double endedAtUnixSeconds);
    /// <summary>队首高优插队（自动移动/手动赶路/组队会话开打共用）：同类去重、挂起运行中队首、新行动立即 Running；模拟内传 incrementVersion: false。</summary>
    IPlayerActionState EnqueuePriority(ActionIntent intent, DateTimeOffset at, ActionCatalog catalog,
        TravelActionState? travel = null, bool incrementVersion = true,
        Domain.Player.PartySessionBinding? partySession = null);
}

/// <summary>资源自然窗口需要的直接分段引用；不要求任何聚合或序列化文档。</summary>
public interface IPlayerResourceContext : IPlayerActionContext
{
    /// <summary>玩家标识。</summary>
    PlayerId PlayerId { get; }
    /// <summary>装备配置与已习得功法分段。</summary>
    PlayerLoadoutState Loadout { get; }
    /// <summary>斗法分段：撮合状态、回放载荷与暂存区。</summary>
    PlayerDuelState Duel { get; }
    /// <summary>在线状态（Offline/Online/Suspended）。</summary>
    PresenceState Presence { get; }
    /// <summary>最近一次模拟推进到的墙钟时刻；离线补算与在线推进共用同一推进游标。</summary>
    DateTimeOffset LastSimulatedAt { get; set; }
    /// <summary>聚合状态版本号；每次对外可见变更递增，用于并发冲突检测。</summary>
    long StateVersion { get; set; }
    /// <summary>按当前装备/功法/Buff/境界合成面板属性；属性输入无变更时实现可返回缓存。</summary>
    PlayerAttributes ResolveAttributes(GameDataCatalog catalog);
    /// <summary>按资源边界时刻移除到期增益；状态所有者负责 Buff 分段的脏标记。</summary>
    bool ExpireBuffs(double atUnixSeconds);
    /// <summary>
    /// 当前驻留节点的遭遇池；ECS 路径经 MapNodeEntity.EncounterProfileComponent 提供，
    /// 聚合/非实体路径返回 null，由引擎回退到配表 WorldGraph 查询。旅途（在边上）为 null。
    /// </summary>
    IReadOnlyList<string>? NodeEncounterIds => null;
}
