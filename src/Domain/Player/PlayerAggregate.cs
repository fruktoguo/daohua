namespace Domain.Player;

public enum PresenceState : byte
{
    Offline = 0,
    Online = 1,
    Suspended = 2
}

public sealed class PlayerProgression
{
    public int RealmLevel { get; set; } = 1;
    public double Qi { get; set; }
    public double Experience { get; set; }
    public double LifetimeQi { get; set; }
    public double CompletedActions { get; set; }

    /// <summary>连续奖励整秒量化的持久化进位余数，保证在线逐秒推进与离线快进的浮点结果位级一致。</summary>
    public double SimulationCarrySeconds { get; set; }
}

public sealed class PlayerWorldState
{
    public string NodeId { get; set; } = "node.yunxi-village";
    public double X { get; set; }
    public double Y { get; set; }
    public double ExplorationSeed { get; set; }
}

public sealed class PlayerCombatState
{
    public double Hitpoints { get; set; } = 100;
    public double MaxHitpoints { get; set; } = 100;
    public double Manapoints { get; set; } = 100;
    public double MaxManapoints { get; set; } = 100;
    public double AttackPower { get; set; } = 10;
    public double Defense { get; set; } = 10;
}

public sealed class PlayerBuffState
{
    public List<ActiveBuff> Active { get; } = [];
}

public sealed record ActiveBuff(string BuffId, double ExpiresAtUnixSeconds, double Stacks = 1);

public sealed class PlayerLoadoutState
{
    public List<InstanceId> EquippedInstances { get; } = [];
    public List<ItemId> EquippedItemIds { get; } = [];
    public List<AbilityId> LearnedAbilities { get; } = [];
}

public sealed class PlayerSettingsState
{
    public string Locale { get; set; } = "zh-CN";
    public bool NotificationsEnabled { get; set; } = true;
    public bool LoopEnabled { get; set; } = true;
    public List<AutoUseSlotState> AutoUseSlots { get; } = [];
}

public sealed record AutoUseSlotState(
    string ActionType,
    int SlotIndex,
    string? ItemId,
    double Quantity,
    bool Enabled);

public enum ActionQueueStatus : byte
{
    Pending = 0,
    Running = 1,
    Blocked = 2
}

public sealed record ActionIntent(
    ActionId ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, double>? Parameters = null)
{
}

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

    public Guid Id { get; }
    public ActionIntent Intent { get; }
    public DateTimeOffset EnqueuedAt { get; }
    public ActionQueueStatus Status { get; internal set; } = ActionQueueStatus.Pending;
    public double ProgressSeconds { get; internal set; }
    public double RemainingRepeats { get; internal set; }
    public double CompletedRepeats { get; internal set; }

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

    public void SetPresence(PresenceState presence)
    {
        if (!Enum.IsDefined(presence))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
        if (Presence == presence) return;
        Presence = presence;
        Touch(PlayerSectionKind.Settings);
    }

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 64)
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家名称无效。");
        var normalized = displayName.Trim();
        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal)) return;
        DisplayName = normalized;
        Touch(PlayerSectionKind.Settings);
    }

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

    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true)
    {
        var index = _actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0) return false;
        _actions.RemoveAt(index);
        if (incrementVersion) Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

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

    public void SetLoopEnabled(bool enabled)
    {
        if (Settings.LoopEnabled == enabled) return;
        Settings.LoopEnabled = enabled;
        Touch(PlayerSectionKind.Settings);
    }

    public void Breakthrough()
    {
        if (Progression.RealmLevel < 1)
            throw new DomainException(DomainErrorCode.InvariantViolation, "当前境界状态无效。");
        Progression.RealmLevel = checked(Progression.RealmLevel + 1);
        Progression.Qi = 0;
        Progression.Experience = 0;
        Touch(PlayerSectionKind.Progression);
    }

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

    public void MarkDirty(PlayerSectionKind section) => _dirtySections.Add(section);

    /// <summary>命令完成后推进聚合版本并标记相应 section；持久化层以此做 CAS。</summary>
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

    public IReadOnlyList<PlayerSectionKind> ConsumeDirtySections()
    {
        var values = _dirtySections.OrderBy(value => value).ToArray();
        _dirtySections.Clear();
        return values;
    }

    public long NextSectionVersion() => checked(++_sectionVersion);

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

    internal void ApplySimulationMutation(PlayerSectionKind section) => MarkDirty(section);
}
