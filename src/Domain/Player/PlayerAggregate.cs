namespace Domain.Player;

/// <summary>
/// 角色在线状态。
/// </summary>
public enum PresenceState : byte
{
    /// <summary>离线，交由离线模拟引擎按挂机规则处理</summary>
    Offline = 0,
    /// <summary>在线，由单 Fiber 本地运行时高频心跳与推进</summary>
    Online = 1,
    /// <summary>挂起或被系统临时封禁</summary>
    Suspended = 2
}

/// <summary>
/// 修仙境界与修为累积进度状态。
/// 所有数值均使用 double 精度以满足确定性进位。
/// </summary>
public sealed class PlayerProgression
{
    /// <summary>当前境界阶层（1: 练气期一层起步）</summary>
    public int RealmLevel { get; set; } = 1;
    /// <summary>当前阶层已累积灵气（突破时重置）</summary>
    public double Qi { get; set; }
    /// <summary>道法感悟与实战经验</summary>
    public double Experience { get; set; }
    /// <summary>道途中累计获得的灵气总量（单调递增，不随突破清零）</summary>
    public double LifetimeQi { get; set; }
    /// <summary>总计已完成的行动次数计数</summary>
    public double CompletedActions { get; set; }

    /// <summary>连续奖励整秒量化的持久化进位余数，保证在线逐秒推进与离线快进的浮点结果位级一致。</summary>
    public double SimulationCarrySeconds { get; set; }
}

/// <summary>
/// 世界漫游与地图位置状态。
/// </summary>
public sealed class PlayerWorldState
{
    /// <summary>当前所在世界地图节点 ID</summary>
    public string NodeId { get; set; } = "node.yunxi-village";
    /// <summary>节点内局部坐标 X</summary>
    public double X { get; set; }
    /// <summary>节点内局部坐标 Y</summary>
    public double Y { get; set; }
    /// <summary>迷雾探索与随机掉落种子</summary>
    public double ExplorationSeed { get; set; }
}

/// <summary>
/// 角色基础战斗属性（运行时由装备、功法与境界动态加成覆盖）。
/// </summary>
public sealed class PlayerCombatState
{
    /// <summary>当前气血生命值</summary>
    public double Hitpoints { get; set; } = 100;
    /// <summary>最大气血上限</summary>
    public double MaxHitpoints { get; set; } = 100;
    /// <summary>当前灵力值</summary>
    public double Manapoints { get; set; } = 100;
    /// <summary>最大灵力上限</summary>
    public double MaxManapoints { get; set; } = 100;
    /// <summary>基础攻击威能</summary>
    public double AttackPower { get; set; } = 10;
    /// <summary>基础防御护甲</summary>
    public double Defense { get; set; } = 10;
}

/// <summary>
/// 角色身上当前附着的临时增益/减益状态集合。
/// </summary>
public sealed class PlayerBuffState
{
    /// <summary>活跃 Buff 列表，快进模拟时按过期时间顺序剔除</summary>
    public List<ActiveBuff> Active { get; } = [];
}

/// <summary>
/// 单个附着 Buff 的持久化记录。
/// </summary>
/// <param name="BuffId">配表 Buff 唯一标识</param>
/// <param name="ExpiresAtUnixSeconds">过期时间戳（Unix 秒）</param>
/// <param name="Stacks">当前叠加层数</param>
public sealed record ActiveBuff(string BuffId, double ExpiresAtUnixSeconds, double Stacks = 1);

/// <summary>
/// 角色装备配置与已习得功法。
/// </summary>
public sealed class PlayerLoadoutState
{
    /// <summary>当前穿戴的非同质化独立装备实例 ID</summary>
    public List<InstanceId> EquippedInstances { get; } = [];
    /// <summary>当前穿戴的配表装备道具 ID</summary>
    public List<ItemId> EquippedItemIds { get; } = [];
    /// <summary>已掌握并激活在槽位上的功法神通 ID</summary>
    public List<AbilityId> LearnedAbilities { get; } = [];
}

/// <summary>
/// 客户端偏好与辅助挂机自动化配置。
/// </summary>
public sealed class PlayerSettingsState
{
    /// <summary>客户端界面语言（如 zh-CN）</summary>
    public string Locale { get; set; } = "zh-CN";
    /// <summary>是否开启系统通知与推送</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>行动队列执行完毕后是否整体循环轮循</summary>
    public bool LoopEnabled { get; set; } = true;
    /// <summary>各行动类型绑定的丹药/补给品自动使用槽位配置</summary>
    public List<AutoUseSlotState> AutoUseSlots { get; } = [];
}

/// <summary>
/// 自动化使用快捷槽配置项。
/// </summary>
public sealed record AutoUseSlotState(
    string ActionType,
    int SlotIndex,
    string? ItemId,
    double Quantity,
    bool Enabled);

/// <summary>
/// 行动项在队列中的调度状态。
/// </summary>
public enum ActionQueueStatus : byte
{
    /// <summary>排队等待执行</summary>
    Pending = 0,
    /// <summary>当前正在推进执行</summary>
    Running = 1,
    /// <summary>因消耗品不足或前置条件缺失被阻塞挂起</summary>
    Blocked = 2
}

/// <summary>
/// 行动执行意图描述（入队参数契约）。
/// </summary>
/// <param name="ActionId">配表动作标识</param>
/// <param name="RepeatCount">计划执行次数；特殊值 -1 代表无限循环直至手动取消</param>
/// <param name="TargetNodeId">指定世界节点（如跨地图移动或定向采集）</param>
/// <param name="Parameters">动态参数字典（如配方 ID、目标词条等）</param>
public sealed record ActionIntent(
    ActionId ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, double>? Parameters = null)
{
}

/// <summary>
/// 排队中的行动实例，维护单次推进的耗时进度与剩余循环计数。
/// </summary>
public sealed class QueuedAction
{
    public QueuedAction(Guid id, ActionIntent intent, DateTimeOffset enqueuedAt)
    {
        if (id == Guid.Empty) throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识不能为空。");
        Id = id;
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        EnqueuedAt = enqueuedAt.ToUniversalTime();
        RemainingRepeats = intent.RepeatCount;
    }

    /// <summary>队列项唯一实例标识</summary>
    public Guid Id { get; }
    /// <summary>行动配置意图</summary>
    public ActionIntent Intent { get; }
    /// <summary>入队时间（UTC）</summary>
    public DateTimeOffset EnqueuedAt { get; }
    /// <summary>当前调度状态</summary>
    public ActionQueueStatus Status { get; internal set; } = ActionQueueStatus.Pending;
    /// <summary>当前单轮行动已推进的秒数（达到配表总时长后触发单次结算）</summary>
    public double ProgressSeconds { get; internal set; }
    /// <summary>剩余重复次数；为 -1 时代表无限循环，绝对不得自动出队</summary>
    public double RemainingRepeats { get; internal set; }
    /// <summary>已成功完成的轮次计数</summary>
    public double CompletedRepeats { get; internal set; }

    /// <summary>深拷贝当前行动状态副本</summary>
    public QueuedAction Clone() => new(Id, Intent, EnqueuedAt)
    {
        Status = Status,
        ProgressSeconds = ProgressSeconds,
        RemainingRepeats = RemainingRepeats,
        CompletedRepeats = CompletedRepeats
    };
}

/// <summary>
/// 在线玩家完整领域聚合。它是单写者上下文内的唯一可变状态，禁止在领域层持有数据库或网络对象。
/// </summary>
public sealed partial class PlayerAggregate
{
    public const int MaxActionQueueLength = 256;
    private readonly List<QueuedAction> _actions = [];
    private readonly HashSet<PlayerSectionKind> _dirtySections = [];
    private readonly ReadOnlyCollection<QueuedAction> _actionsView;
    private readonly ReadOnlySet<PlayerSectionKind> _dirtySectionsView;
    private long _sectionVersion;

    private PlayerAggregate(
        PlayerId playerId,
        AccountId accountId,
        string displayName,
        DateTimeOffset createdAt,
        string gameDataFingerprint)
    {
        if (playerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家标识不能为空。");
        if (accountId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "账号标识不能为空。");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 64)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家名称无效。");
        if (string.IsNullOrWhiteSpace(gameDataFingerprint))
            throw new DomainException(DomainErrorCode.InvalidArgument, "游戏数据指纹不能为空。");

        PlayerId = playerId;
        AccountId = accountId;
        DisplayName = displayName.Trim();
        CreatedAt = createdAt.ToUniversalTime();
        LastSimulatedAt = CreatedAt;
        GameDataFingerprint = gameDataFingerprint;
        _actionsView = _actions.AsReadOnly();
        _dirtySectionsView = new ReadOnlySet<PlayerSectionKind>(_dirtySections);
    }

    public PlayerId PlayerId { get; }
    public AccountId AccountId { get; }
    public string DisplayName { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastSimulatedAt { get; internal set; }
    public PresenceState Presence { get; private set; } = PresenceState.Offline;
    public long StateVersion { get; internal set; }
    public string GameDataFingerprint { get; private set; }
    public PlayerProgression Progression { get; } = new();
    public PlayerWorldState World { get; } = new();
    public PlayerCombatState Combat { get; } = new();
    public PlayerBuffState Buffs { get; } = new();
    public PlayerLoadoutState Loadout { get; } = new();
    public PlayerSettingsState Settings { get; } = new();
    public IReadOnlyList<QueuedAction> Actions => _actionsView;
    public IReadOnlySet<PlayerSectionKind> DirtySections => _dirtySectionsView;

    /// <summary>
    /// 工厂方法：新建修仙者角色聚合实例，并默认将所有分段标记为脏以待初次落库。
    /// </summary>
    public static PlayerAggregate Create(
        PlayerId playerId,
        AccountId accountId,
        string displayName,
        DateTimeOffset now,
        string gameDataFingerprint)
    {
        var aggregate = new PlayerAggregate(playerId, accountId, displayName, now, gameDataFingerprint)
        {
            Presence = PresenceState.Online,
            StateVersion = 1
        };
        aggregate.MarkDirty(PlayerSectionKind.Progression);
        aggregate.MarkDirty(PlayerSectionKind.World);
        aggregate.MarkDirty(PlayerSectionKind.Combat);
        aggregate.MarkDirty(PlayerSectionKind.Buffs);
        aggregate.MarkDirty(PlayerSectionKind.Loadout);
        aggregate.MarkDirty(PlayerSectionKind.Settings);
        aggregate.MarkDirty(PlayerSectionKind.ActionQueue);
        return aggregate;
    }

    /// <summary>
    /// 设置在线状态；状态变更时标记 Settings 分段为脏。
    /// </summary>
    public void SetPresence(PresenceState presence)
    {
        if (!Enum.IsDefined(presence))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
        if (Presence == presence) return;
        Presence = presence;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 将玩家状态绑定到当前游戏数据版本。配表指纹不是存档 schema；变化时仅推进状态版本，
    /// 并借 Settings 分段携带新的头元数据写回持久化层。
    /// </summary>
    public bool RebindGameDataFingerprint(string gameDataFingerprint)
    {
        if (string.IsNullOrWhiteSpace(gameDataFingerprint))
            throw new DomainException(DomainErrorCode.InvalidArgument, "游戏数据指纹不能为空。");
        if (string.Equals(GameDataFingerprint, gameDataFingerprint, StringComparison.Ordinal)) return false;
        GameDataFingerprint = gameDataFingerprint;
        Touch(PlayerSectionKind.Settings);
        return true;
    }

    /// <summary>
    /// 修改道号/角色显示名。
    /// </summary>
    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 64)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家名称无效。");
        var normalized = displayName.Trim();
        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal)) return;
        DisplayName = normalized;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 向行动队列追加一项行动意图。
    /// 业务约束：
    /// 1. RepeatCount 必须为正整数或 -1（无限重复）；
    /// 2. 无限重复行动的时长必须大于 0，防止单步零耗时导致模拟死循环；
    /// 3. 行动必须存在于配表目录中。
    /// </summary>
    public QueuedAction Enqueue(ActionIntent intent, DateTimeOffset now, ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // RepeatCount 必须为整数（-1 除外）：非整数递减后会落入负数区间，"== 0" 出队条件永不成立，形成事实上的无限行动。
        if (intent is null || !intent.ActionId.IsValid || !double.IsFinite(intent.RepeatCount)
            || (intent.RepeatCount != -1 && (intent.RepeatCount < 1 || intent.RepeatCount > 1_000_000
                || intent.RepeatCount != Math.Floor(intent.RepeatCount))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动意图无效。");
        if (_actions.Count >= MaxActionQueueLength)
            throw new DomainException(DomainErrorCode.Conflict, $"行动队列最多只能包含 {MaxActionQueueLength} 项。");
        if (intent.TargetNodeId is not null && (intent.TargetNodeId.Length == 0 || intent.TargetNodeId.Length > 128))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动目标节点无效。");
        if (intent.Parameters is not null && intent.Parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || !double.IsFinite(pair.Value)))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动参数无效。");
        if (!catalog.Contains(intent.ActionId))
            throw new DomainException(DomainErrorCode.NotFound, $"行动 {intent.ActionId} 不存在。");
        var definition = catalog.Get(intent.ActionId);
        if (intent.RepeatCount == -1 && definition.DurationSeconds <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "无限重复行动必须具有正时长，避免模拟无法推进。");
        var normalizedIntent = new ActionIntent(
            intent.ActionId,
            intent.RepeatCount,
            intent.TargetNodeId,
            intent.Parameters is null
                ? null
                : new ReadOnlyDictionary<string, double>(intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        var action = new QueuedAction(Guid.NewGuid(), normalizedIntent, now);
        _actions.Add(action);
        Touch(PlayerSectionKind.ActionQueue);
        return action;
    }

    /// <summary>
    /// 从队列中显式取消/移除指定行动实例。
    /// </summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true)
    {
        var index = _actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0) return false;
        _actions.RemoveAt(index);
        if (incrementVersion) Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>
    /// 调整队列中某项行动的执行次序（-1 向上移一位，1 向下移一位）。
    /// </summary>
    public bool MoveAction(Guid actionInstanceId, int direction)
    {
        if (direction is not (-1 or 1))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动移动方向必须为 -1 或 1。");
        var index = _actions.FindIndex(action => action.Id == actionInstanceId);
        var target = index + direction;
        if (index < 0) return false;
        if (target < 0 || target >= _actions.Count) return false;
        (_actions[index], _actions[target]) = (_actions[target], _actions[index]);
        Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>
    /// 切换行动队列全局无限循环状态。开启后队列最后一项完成后重新回到头部轮询。
    /// </summary>
    public void SetLoopEnabled(bool enabled)
    {
        if (Settings.LoopEnabled == enabled) return;
        Settings.LoopEnabled = enabled;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 突破至下一境界阶层。重置当前阶层灵气与经验，推进 Progression 脏标记。
    /// </summary>
    public void Breakthrough()
    {
        if (Progression.RealmLevel < 1)
            throw new DomainException(DomainErrorCode.InvariantViolation, "当前境界状态无效。");
        Progression.RealmLevel = checked(Progression.RealmLevel + 1);
        Progression.Qi = 0;
        Progression.Experience = 0;
        Touch(PlayerSectionKind.Progression);
    }

    /// <summary>
    /// 传送到或移动至指定世界地图节点及坐标。
    /// </summary>
    public void TravelTo(string nodeId, double x = 0, double y = 0)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 128 || !double.IsFinite(x) || !double.IsFinite(y))
            throw new DomainException(DomainErrorCode.InvalidArgument, "目标世界节点无效。");
        var normalized = nodeId.Trim();
        if (string.Equals(World.NodeId, normalized, StringComparison.Ordinal) && World.X == x && World.Y == y) return;
        World.NodeId = normalized;
        World.X = x;
        World.Y = y;
        Touch(PlayerSectionKind.World);
    }

    /// <summary>
    /// 全量替换当前生效的战斗装备与习得功法。
    /// </summary>
    public void ReplaceLoadout(
        IEnumerable<InstanceId> equippedInstances,
        IEnumerable<AbilityId> learnedAbilities,
        IEnumerable<ItemId>? equippedItemIds = null)
    {
        ArgumentNullException.ThrowIfNull(equippedInstances);
        ArgumentNullException.ThrowIfNull(learnedAbilities);
        var equipped = equippedInstances.Distinct().ToArray();
        var learned = learnedAbilities.Distinct().ToArray();
        var itemIds = (equippedItemIds ?? []).Distinct().ToArray();
        if (equipped.Any(item => item.IsEmpty) || learned.Any(ability => !ability.IsValid) || itemIds.Any(item => !item.IsValid))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗装配标识无效。");
        if (Loadout.EquippedInstances.SequenceEqual(equipped)
            && Loadout.LearnedAbilities.SequenceEqual(learned)
            && Loadout.EquippedItemIds.SequenceEqual(itemIds)) return;
        Loadout.EquippedInstances.Clear();
        Loadout.EquippedInstances.AddRange(equipped);
        Loadout.LearnedAbilities.Clear();
        Loadout.LearnedAbilities.AddRange(learned);
        Loadout.EquippedItemIds.Clear();
        Loadout.EquippedItemIds.AddRange(itemIds);
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>
    /// 配置特定行动类型（如炼丹、战斗、采集）绑定的消耗品自动使用快捷槽位。
    /// </summary>
    public void ConfigureAutoUse(string actionType, int slotIndex, string? itemId, double quantity, bool enabled)
    {
        actionType = actionType?.Trim() ?? string.Empty;
        itemId = itemId?.Trim();
        if (string.IsNullOrWhiteSpace(actionType) || actionType.Length > 32 || slotIndex is < 0 or > 15
            || (itemId is not null && (itemId.Length == 0 || itemId.Length > 128))
            || !double.IsFinite(quantity) || quantity <= 0 || quantity > 1_000_000)
            throw new DomainException(DomainErrorCode.InvalidArgument, "自动使用配置无效。");
        var index = Settings.AutoUseSlots.FindIndex(slot => slot.ActionType == actionType && slot.SlotIndex == slotIndex);
        var value = new AutoUseSlotState(actionType, slotIndex, itemId, quantity, enabled && !string.IsNullOrWhiteSpace(itemId));
        if (index >= 0 && Settings.AutoUseSlots[index] == value) return;
        if (index >= 0) Settings.AutoUseSlots[index] = value;
        else Settings.AutoUseSlots.Add(value);
        Settings.AutoUseSlots.Sort((left, right) =>
        {
            var actionOrder = string.CompareOrdinal(left.ActionType, right.ActionType);
            return actionOrder != 0 ? actionOrder : left.SlotIndex.CompareTo(right.SlotIndex);
        });
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>将指定分段标记为脏，待下次刷盘时同步</summary>
    public void MarkDirty(PlayerSectionKind section) => _dirtySections.Add(section);

    /// <summary>命令完成后推进聚合版本并标记相应 section；持久化层以此做 CAS 乐观锁检测。</summary>
    public long Touch(params PlayerSectionKind[] sections)
    {
        if (sections is null || sections.Length == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "至少需要一个脏 section。");
        StateVersion = checked(StateVersion + 1);
        foreach (var section in sections) _dirtySections.Add(section);
        return StateVersion;
    }

    /// <summary>从持久化头恢复版本游标；仅允许单调不减。</summary>
    public void ApplyPersistedVersion(long stateVersion, long sectionVersion = 0)
    {
        if (stateVersion < StateVersion || sectionVersion < _sectionVersion)
            throw new DomainException(DomainErrorCode.Conflict, "持久化版本不能回退。");
        StateVersion = stateVersion;
        _sectionVersion = sectionVersion;
    }

    /// <summary>
    /// 提取并清空当前已标记的所有脏分段列表（零闭包、就地排序后返回）。
    /// </summary>
    public IReadOnlyList<PlayerSectionKind> ConsumeDirtySections()
    {
        if (_dirtySections.Count == 0)
        {
            return [];
        }

        var values = new PlayerSectionKind[_dirtySections.Count];
        _dirtySections.CopyTo(values);
        Array.Sort(values);
        _dirtySections.Clear();
        return values;
    }

    /// <summary>获取并自增下一个分段单调版本号</summary>
    public long NextSectionVersion() => checked(++_sectionVersion);

    /// <summary>
    /// 构造当前聚合的持久化头元数据
    /// </summary>
    public PlayerStateHead CreateHead(string checksum, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(checksum))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家头校验值不能为空。");
        return new PlayerStateHead(
            PlayerId,
            StateVersion,
            PlayerStateHead.CurrentSchemaVersion,
            LastSimulatedAt,
            GameDataFingerprint,
            checksum,
            now.ToUniversalTime());
    }

    /// <summary>模拟推进器专用：标记某分段受推进影响而发生变更</summary>
    internal void ApplySimulationMutation(PlayerSectionKind section) => MarkDirty(section);
}
