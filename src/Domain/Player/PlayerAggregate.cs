using Domain.World;

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
    private int _realmLevel = 1;

    /// <summary>当前境界阶层（1: 练气期一层起步）</summary>
    public int RealmLevel
    {
        get => _realmLevel;
        set
        {
            if (_realmLevel == value) return;
            _realmLevel = value;
            RealmLevelVersion = checked(RealmLevelVersion + 1);
        }
    }

    /// <summary>境界阶层修订号：属性解析输入之一，写入即单调递增（同值写入不计）。</summary>
    internal long RealmLevelVersion { get; private set; }
    /// <summary>当前层修为（突破计量条，expToNext 封顶，突破清零）</summary>
    public double Experience { get; set; }
    /// <summary>底蕴：修为满层溢出沉淀池；未满层时按 1:1 兑换放大修为获取（单笔 ≤3× 增幅量），突破不清空。</summary>
    public double Foundation { get; set; }
    /// <summary>战斗经验：修为类收益按增幅前基础量等额累积的对抗池；只累积不消耗，突破不清空。</summary>
    public double CombatExperience { get; set; }
    /// <summary>道途累计获得的修为总量（单调递增，含已沉淀为底蕴的部分，不随突破清零）</summary>
    public double LifetimeExperience { get; set; }
    /// <summary>总计已完成的行动次数计数</summary>
    public double CompletedActions { get; set; }
    /// <summary>生平斩妖：普通妖兽累计击杀数</summary>
    public double NormalKills { get; set; }
    /// <summary>生平斩妖：精英妖兽累计击杀数</summary>
    public double EliteKills { get; set; }
    /// <summary>生平斩妖：首领妖兽累计击杀数</summary>
    public double BossKills { get; set; }
    /// <summary>生平败走：战斗落败累计次数</summary>
    public double TotalDefeats { get; set; }
    /// <summary>生平修炼时长累计（秒）</summary>
    public double CultivationSeconds { get; set; }
    /// <summary>道龄（已流逝寿元，秒）；随模拟时间自然累积，寿元上限由境界阶位配表决定。</summary>
    public double AgeSeconds { get; set; }

    /// <summary>连续奖励整秒量化的持久化进位余数，保证在线逐秒推进与离线快进的浮点结果位级一致。</summary>
    public double SimulationCarrySeconds { get; set; }

    /// <summary>
    /// 修为类收益入账量的纯计算（总纲 §7.11.1）：
    /// credited = min(3·amp, amp + foundation, max(0, cap - experience))。
    /// 满层（cap - experience ≤ 0）恒为 0；底蕴按 1:1 兑换补足 amp，单笔总入账封顶 3·amp。
    /// </summary>
    /// <param name="amplifiedAmount">玩家增幅倍率作用后的收益量。</param>
    /// <param name="cap">本层修为上限（expToNext）；无境界进度配置时传 <see cref="double.PositiveInfinity"/>。</param>
    /// <param name="experience">结算前本层已累积修为。</param>
    /// <param name="foundation">结算前底蕴池余量。</param>
    /// <returns>实际计入本层修为的量。</returns>
    public static double ComputeExperienceCredit(double amplifiedAmount, double cap, double experience, double foundation)
        => Math.Min(3.0 * amplifiedAmount,
            Math.Min(amplifiedAmount + foundation, Math.Max(0, cap - experience)));

    /// <summary>
    /// 修为类收益统一结算（总纲 §7.11.1，连续收益/单轮奖励/击杀奖励与冥想路径共用单点）：
    /// Experience += credited；Foundation += amp - credited（净变动，可为负）；CombatExperience += base。
    /// 返回实际计入本层修为的量，供战报账本按真实入账口径记录。
    /// </summary>
    /// <param name="baseAmount">增幅前基础量；等额累积进战斗经验，不受加成与瓶颈影响。</param>
    /// <param name="amplifiedAmount">玩家增幅倍率作用后的收益量。</param>
    /// <param name="cap">本层修为上限；无境界进度配置时传 <see cref="double.PositiveInfinity"/>。</param>
    /// <returns>实际计入本层修为的量（≥0）。</returns>
    public double CreditExperience(double baseAmount, double amplifiedAmount, double cap)
    {
        if (!double.IsFinite(baseAmount) || baseAmount < 0
            || !double.IsFinite(amplifiedAmount) || amplifiedAmount < 0
            || double.IsNaN(cap) || cap < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "修为收益入账参数无效。");
        var credited = ComputeExperienceCredit(amplifiedAmount, cap, Experience, Foundation);
        Experience += credited;
        Foundation += amplifiedAmount - credited;
        CombatExperience += baseAmount;
        LifetimeExperience += amplifiedAmount;
        if (!double.IsFinite(Experience) || !double.IsFinite(Foundation) || !double.IsFinite(CombatExperience) || !double.IsFinite(LifetimeExperience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "修为收益入账结果无效。");
        return credited;
    }

    /// <summary>分段回滚检查点专用：按备份文档就地恢复全部字段，并把境界修订号写回备份值。</summary>
    internal void RestoreSnapshot(PlayerProgressionState state, long realmLevelVersion)
    {
        _realmLevel = state.RealmLevel;
        RealmLevelVersion = realmLevelVersion;
        Experience = state.Experience;
        Foundation = state.Foundation;
        CombatExperience = state.CombatExperience;
        LifetimeExperience = state.LifetimeExperience;
        CompletedActions = state.CompletedActions;
        SimulationCarrySeconds = state.SimulationCarrySeconds;
        NormalKills = state.NormalKills;
        EliteKills = state.EliteKills;
        BossKills = state.BossKills;
        TotalDefeats = state.TotalDefeats;
        CultivationSeconds = state.CultivationSeconds;
        AgeSeconds = state.AgeSeconds;
    }
}

/// <summary>
/// 世界漫游与地图位置状态。
/// </summary>
public sealed class PlayerWorldState
{
    private string _nodeId = "node.yunxi-village";
    private Domain.World.BuildingEffectSet _nodeBuildingEffects = Domain.World.BuildingEffectSet.Empty;
    private string? _nodeBuildingNodeId;

    /// <summary>当前所在世界地图节点 ID</summary>
    public string NodeId
    {
        get => _nodeId;
        set
        {
            if (_nodeId == value) return;
            _nodeId = value;
            NoteAttributeInputMutation();
        }
    }
    /// <summary>当前正在通行的世界边 ID（未处于边上时为空）</summary>
    public string? EdgeId { get; set; }
    /// <summary>当前在边上的归一化行进进度 [0, 1]</summary>
    public double EdgeProgress { get; set; }
    /// <summary>节点内或边上插值坐标 X</summary>
    public double X { get; set; }
    /// <summary>节点内或边上插值坐标 Y</summary>
    public double Y { get; set; }
    /// <summary>迷雾探索与随机掉落种子</summary>
    public double ExplorationSeed { get; set; }
    /// <summary>
    /// 当前节点的公共建筑列表（共享注册表投影，由运行时按节拍刷新并持久化；
    /// 持久化副本供离线/冷挂机补算沿用最近已知状态）。
    /// </summary>
    public List<Domain.World.NodeBuildingState> NodeBuildings { get; set; } = [];
    /// <summary>
    /// 当前节点已建成建筑的汇总效果（运行时按节拍从共享注册表解析写入；
    /// 持久化副本供离线补算沿用最近已知状态）。
    /// </summary>
    public Domain.World.BuildingEffectSet NodeBuildingEffects
    {
        get => _nodeBuildingEffects;
        set
        {
            if (_nodeBuildingEffects == value) return;
            _nodeBuildingEffects = value;
            NoteAttributeInputMutation();
        }
    }
    /// <summary>
    /// 建筑投影所属节点 ID：投影与当前节点不一致时（如跨节点快进尚未刷新），
    /// 消费方必须通过 <see cref="EffectiveNodeBuildingEffects"/> 读取，保证效果不跨节点泄漏。
    /// </summary>
    public string? NodeBuildingNodeId
    {
        get => _nodeBuildingNodeId;
        set
        {
            if (_nodeBuildingNodeId == value) return;
            _nodeBuildingNodeId = value;
            NoteAttributeInputMutation();
        }
    }
    /// <summary>当前位置生效的节点建筑效果；投影属于其他节点或缺失时返回空集。</summary>
    public Domain.World.BuildingEffectSet EffectiveNodeBuildingEffects =>
        string.Equals(NodeBuildingNodeId, NodeId, StringComparison.Ordinal)
            ? NodeBuildingEffects
            : Domain.World.BuildingEffectSet.Empty;

    /// <summary>节点建筑效果相关输入的修订号（NodeId/NodeBuildingEffects/NodeBuildingNodeId 共享计数）。</summary>
    internal long AttributeInputVersion { get; private set; }
    private void NoteAttributeInputMutation() => AttributeInputVersion = checked(AttributeInputVersion + 1);

    /// <summary>分段回滚检查点专用：按备份文档就地恢复全部字段，并把属性输入修订号写回备份值。</summary>
    internal void RestoreSnapshot(PlayerWorldStateDocument state, long attributeInputVersion)
    {
        _nodeId = state.NodeId;
        EdgeId = state.EdgeId;
        EdgeProgress = state.EdgeProgress;
        X = state.X;
        Y = state.Y;
        ExplorationSeed = state.ExplorationSeed;
        NodeBuildings = state.NodeBuildings?.ToList() ?? [];
        _nodeBuildingEffects = state.NodeBuildingEffects ?? Domain.World.BuildingEffectSet.Empty;
        _nodeBuildingNodeId = state.NodeBuildingNodeId;
        AttributeInputVersion = attributeInputVersion;
    }
}

/// <summary>
/// 角色基础战斗属性（运行时由装备、功法与境界动态加成覆盖）。
/// </summary>
/// <summary>
/// 角色战斗资源分段：气血/灵力池与自然回复游标。战斗内外同一份状态，
/// 触发器与行动结算直接读写；落库时并入 Combat 分段文档，线上格式不变。
/// </summary>
public sealed class PlayerResourceState
{
    /// <summary>当前气血生命值</summary>
    public double Hitpoints { get; set; } = 100;
    /// <summary>最大气血上限</summary>
    public double MaxHitpoints { get; set; } = 100;
    /// <summary>当前灵力值</summary>
    public double Manapoints { get; set; } = 100;
    /// <summary>最大灵力上限</summary>
    public double MaxManapoints { get; set; } = 100;
    /// <summary>自然回复周期（每 5 秒结算一次）的累计进度余量。</summary>
    public double RegenCarrySeconds { get; set; }
}

public sealed class PlayerCombatState
{
    /// <summary>基础攻击威能</summary>
    public double AttackPower { get; set; } = 10;
    /// <summary>基础防御护甲</summary>
    public double Defense { get; set; } = 10;
    /// <summary>技能调度状态（冷却、吟唱与施放计数）。</summary>
    public PlayerCombatSkillState Skills { get; set; } = PlayerCombatSkillState.Empty;
}

/// <summary>
/// 角色身上当前附着的临时增益/减益状态集合。
/// </summary>
public sealed class PlayerBuffState
{
    /// <summary>活跃 Buff 列表，快进模拟时按过期时间顺序剔除；写入递增版本号供属性缓存失效检测。</summary>
    public VersionedList<ActiveBuff> Active { get; } = new();
}

/// <summary>
/// 单个附着 Buff 的持久化记录。
/// </summary>
/// <param name="BuffId">配表 Buff 唯一标识</param>
/// <param name="ExpiresAtUnixSeconds">过期时间戳（Unix 秒）</param>
/// <param name="Stacks">当前叠加层数</param>
public sealed record ActiveBuff(string BuffId, double ExpiresAtUnixSeconds, double Stacks = 1,
    string Name = "", string? SourceItemId = null, double HpRegen = 0, double MpRegen = 0,
    double AttackSpeedPoints = 0, double DamageAbsorptionPoints = 0,
    IReadOnlyDictionary<string, double>? StatRatios = null);

/// <summary>
/// 角色装备配置与已习得功法。
/// </summary>
public sealed partial class PlayerLoadoutState
{
    /// <summary>当前穿戴的非同质化独立装备实例 ID</summary>
    public List<InstanceId> EquippedInstances { get; } = [];
    /// <summary>当前穿戴的配表装备道具 ID；写入递增版本号供属性缓存失效检测。</summary>
    public VersionedList<ItemId> EquippedItemIds { get; } = new();
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
    /// <summary>开轮地点不符时是否自动插队赶路至入队锚点再执行（false 时走 Blocked 跳过语义）</summary>
    public bool AutoTravelEnabled { get; set; }
    /// <summary>各行动类型绑定的丹药/补给品自动使用槽位配置</summary>
    public List<AutoUseSlotState> AutoUseSlots { get; } = [];
    /// <summary>改名冷却截止时刻（Unix 秒）；0 表示未冷却。</summary>
    public double RenameCooldownUntilUnixSeconds { get; set; }
}

/// <summary>
/// 自动化使用快捷槽配置项。战斗域按 Group 分为药品栏（potion，仅药品类即时消耗品）
/// 与通用栏（general，任意战斗消耗品）两组，各 3 槽；其余行动域仅通用栏。
/// </summary>
public sealed record AutoUseSlotState(
    string ActionType,
    int SlotIndex,
    string? ItemId,
    double Quantity,
    bool Enabled,
    double RemainingDurationSeconds = 0,
    double CooldownUntilUnixSeconds = 0,
    string Group = "general")
{
    /// <summary>每组自动使用槽位数；与前端槽位网格（每组 3 槽）保持一致。</summary>
    public const int MaxSlotIndex = 2;
    /// <summary>通用栏：任意适用消耗品（符咒、阵法、特殊道具、药品等）。</summary>
    public const string SlotGroupGeneral = "general";
    /// <summary>药品栏：仅限药品类即时消耗品；仅战斗行动域有效。</summary>
    public const string SlotGroupPotion = "potion";
    /// <summary>战斗行动域槽位总数（药品栏 3 + 通用栏 3）。</summary>
    public const int CombatSlotCount = (MaxSlotIndex + 1) * 2;
}

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
/// <param name="PvpEnabled">斗法开关：仅对战斗遭遇行动生效；开启后参与斗法遭遇计数与撮合</param>
/// <param name="AnchorNodeId">入队锚点节点：入队时玩家驻留节点，自动移动（AutoTravelEnabled）开轮地点不符时的自动赶路目的地；旅途中入队为 null</param>
public sealed record ActionIntent(
    ActionId ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, double>? Parameters = null,
    bool PvpEnabled = false,
    string? AnchorNodeId = null)
{
}

/// <summary>
/// 排队中的行动实例，维护单次推进的耗时进度与剩余循环计数。
/// </summary>
public sealed class QueuedAction : Domain.Simulation.IPlayerActionState
{
    /// <summary>以意图创建排队行动；剩余重复次数初始化为 intent.RepeatCount。</summary>
    /// <param name="id">行动实例唯一标识。</param>
    /// <param name="intent">行动执行意图。</param>
    /// <param name="enqueuedAt">入队时间（统一转 UTC）。</param>
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
    /// <summary>战斗行动当前远征阶段；普通行动保持为空。</summary>
    public CombatExpeditionState? Expedition { get; internal set; }
    /// <summary>移动行动当前路径与路段行进状态；普通行动保持为空。</summary>
    public TravelActionState? Travel { get; internal set; }

    TravelActionState? Domain.Simulation.IPlayerActionState.Travel { get => Travel; set => Travel = value; }
    CombatExpeditionState? Domain.Simulation.IPlayerActionState.Expedition { get => Expedition; set => Expedition = value; }
    ActionQueueStatus Domain.Simulation.IPlayerActionState.Status { get => Status; set => Status = value; }
    double Domain.Simulation.IPlayerActionState.ProgressSeconds { get => ProgressSeconds; set => ProgressSeconds = value; }
    double Domain.Simulation.IPlayerActionState.RemainingRepeats { get => RemainingRepeats; set => RemainingRepeats = value; }
    double Domain.Simulation.IPlayerActionState.CompletedRepeats { get => CompletedRepeats; set => CompletedRepeats = value; }

    /// <summary>深拷贝当前行动状态副本</summary>
    public QueuedAction Clone() => new(Id, Intent, EnqueuedAt)
    {
        Status = Status,
        ProgressSeconds = ProgressSeconds,
        RemainingRepeats = RemainingRepeats,
        CompletedRepeats = CompletedRepeats,
        Expedition = Expedition,
        Travel = Travel
    };
}

/// <summary>
/// 在线玩家完整领域聚合。它是单写者上下文内的唯一可变状态，禁止在领域层持有数据库或网络对象。
/// </summary>
public sealed partial class PlayerAggregate
{
    /// <summary>行动队列容量上限。</summary>
    public const int MaxActionQueueLength = 256;
    private readonly List<QueuedAction> _actions;
    private readonly HashSet<PlayerSectionKind> _dirtySections = [];
    private readonly Dictionary<PlayerSectionKind, long> _dirtyMarks = [];
    private long _dirtyStamp;
    private readonly ReadOnlyCollection<QueuedAction> _actionsView;
    private readonly ReadOnlySet<PlayerSectionKind> _dirtySectionsView;
    private long _sectionVersion;
    private long _cachedAttributeRevision = -1;
    private GameDataCatalog? _cachedAttributeCatalog;
    private PlayerAttributes? _cachedAttributes;

    private PlayerAggregate(
        PlayerId playerId,
        AccountId accountId,
        string displayName,
        DateTimeOffset createdAt,
        string gameDataFingerprint,
        PlayerProgression? progression = null,
        PlayerWorldState? world = null,
        PlayerResourceState? resources = null,
        PlayerCombatState? combat = null,
        PlayerBuffState? buffs = null,
        PlayerLoadoutState? loadout = null,
        PlayerSettingsState? settings = null,
        PlayerDuelState? duel = null,
        List<QueuedAction>? actions = null)
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
        Progression = progression ?? new();
        World = world ?? new();
        Resources = resources ?? new();
        Combat = combat ?? new();
        Buffs = buffs ?? new();
        Loadout = loadout ?? new();
        Settings = settings ?? new();
        Duel = duel ?? new();
        _actions = actions ?? [];
        _actionsView = _actions.AsReadOnly();
        _dirtySectionsView = new ReadOnlySet<PlayerSectionKind>(_dirtySections);
        Duel.BindQueue(_actionsView);
    }

    /// <summary>玩家聚合唯一标识。</summary>
    public PlayerId PlayerId { get; }
    /// <summary>所属账号标识。</summary>
    public AccountId AccountId { get; }
    /// <summary>道号/角色显示名。</summary>
    public string DisplayName { get; private set; }
    /// <summary>角色创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>上次确定性模拟推进到的时刻（UTC）。</summary>
    public DateTimeOffset LastSimulatedAt { get; internal set; }
    /// <summary>当前在线状态。</summary>
    public PresenceState Presence { get; private set; } = PresenceState.Offline;
    /// <summary>聚合单调版本号，持久化层 CAS 乐观锁依据。</summary>
    public long StateVersion { get; internal set; }
    /// <summary>当前绑定的配表内容指纹。</summary>
    public string GameDataFingerprint { get; private set; }
    /// <summary>境界与修为分段。</summary>
    public PlayerProgression Progression { get; }
    /// <summary>世界漫游分段。</summary>
    public PlayerWorldState World { get; }
    /// <summary>战斗资源分段（气血/灵力池与回复游标）。</summary>
    public PlayerResourceState Resources { get; }
    /// <summary>战斗属性分段。</summary>
    public PlayerCombatState Combat { get; }
    /// <summary>Buff 增益分段。</summary>
    public PlayerBuffState Buffs { get; }
    /// <summary>装备与功法装配分段。</summary>
    public PlayerLoadoutState Loadout { get; }
    /// <summary>偏好设置分段。</summary>
    public PlayerSettingsState Settings { get; }
    /// <summary>行动队列只读视图（队首为当前执行项）。</summary>
    public IReadOnlyList<QueuedAction> Actions => _actionsView;
    /// <summary>待刷盘脏分段只读视图。</summary>
    public IReadOnlySet<PlayerSectionKind> DirtySections => _dirtySectionsView;

    /// <summary>
    /// 分段脏标记流水号视图：每次 MarkDirty/Touch 记录单调递增戳（重复标脏戳号继续前进），
    /// 供单写者锁内做「本次操作触及分段」的 before/after 差集（网络增量窄化用，
    /// 不替代 DirtySections 的持久化语义，也不随 ConsumeDirtySections 清空）。
    /// </summary>
    public IReadOnlyDictionary<PlayerSectionKind, long> DirtySectionMarks => _dirtyMarks;

    /// <summary>
    /// 属性解析输入修订号：由全部属性输入源的单调版本号合成
    /// （境界阶层 / 节点建筑效果三元组 / 穿戴装备 ID / 装备槽强化 / 六维经验 / 活跃 Buff）。
    /// 任一输入源写入都会令其严格增大，合成值相等当且仅当全部输入未变；
    /// 属性解析缓存以「同配表实例 + 本修订号未变」为命中条件，命中结果与全量重算逐位一致。
    /// </summary>
    internal long AttributeInputRevision =>
        Progression.RealmLevelVersion
        + World.AttributeInputVersion
        + Buffs.Active.Version
        + Loadout.EquippedItemIds.Version
        + Loadout.Equipment.Version
        + Loadout.CoreAttributesVersion;

    /// <summary>尝试命中属性解析缓存：同配表实例且属性输入修订号未变时复用上轮结果。</summary>
    internal bool TryGetCachedAttributes(GameDataCatalog catalog, out PlayerAttributes attributes)
    {
        if (_cachedAttributes is not null
            && _cachedAttributeRevision == AttributeInputRevision
            && ReferenceEquals(_cachedAttributeCatalog, catalog))
        {
            attributes = _cachedAttributes;
            return true;
        }
        attributes = null!;
        return false;
    }

    /// <summary>登记一次全量属性解析结果，连同当时的输入修订号与配表实例。</summary>
    internal void StoreCachedAttributes(GameDataCatalog catalog, PlayerAttributes attributes)
    {
        _cachedAttributes = attributes;
        _cachedAttributeCatalog = catalog;
        _cachedAttributeRevision = AttributeInputRevision;
    }

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
        aggregate.MarkDirty(PlayerSectionKind.Duel);
        return aggregate;
    }

    /// <summary>
    /// 视图工厂：包装既有分段实例与行动队列的可变命令门面。
    /// ECS 宿主命令路径使用——分段与队列直接使用组件持有的 POCO 引用，
    /// 聚合方法修改的就是组件本体，无需快照导出/重建/回灌往返；
    /// 脏标记与属性缓存仍为请求级私有状态，标量头字段经显式进出同步。
    /// </summary>
    internal static PlayerAggregate Wrap(
        PlayerId playerId,
        AccountId accountId,
        string displayName,
        DateTimeOffset createdAt,
        string gameDataFingerprint,
        long stateVersion,
        PresenceState presence,
        DateTimeOffset lastSimulatedAt,
        PlayerProgression progression,
        PlayerWorldState world,
        PlayerResourceState resources,
        PlayerCombatState combat,
        PlayerBuffState buffs,
        PlayerLoadoutState loadout,
        PlayerSettingsState settings,
        PlayerDuelState duel,
        List<QueuedAction> actions)
        => new(playerId, accountId, displayName, createdAt, gameDataFingerprint,
            progression, world, resources, combat, buffs, loadout, settings, duel, actions)
        {
            StateVersion = stateVersion,
            Presence = presence,
            LastSimulatedAt = lastSimulatedAt
        };

    /// <summary>
    /// 设置在线状态；状态变更时标记 Settings 分段为脏。
    /// </summary>
    public void SetPresence(PresenceState presence)
    {
        if (!Enum.IsDefined(presence))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
        if (Presence == presence) return;
        Presence = presence;
        // 每次离线会话重置离线账本：进入离线即清空上一会话的挂机统计，收益从本次离线起算。
        if (presence == PresenceState.Offline)
        {
            Duel.ResetOfflineReport(LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d);
            // OfflineReport 属 duel 分段持久化/下发内容：重置必须标脏 Duel，否则重置结果不落盘。
            MarkDirty(PlayerSectionKind.Duel);
        }
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

    /// <summary>道号允许字符：字母、数字、下划线与中文间隔号；长度 2~12。</summary>
    private static readonly System.Text.RegularExpressions.Regex DisplayNamePattern =
        new(@"^[\p{L}\p{N}_·]{2,12}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    /// <summary>改名冷却时长：24 小时。</summary>
    public static readonly TimeSpan RenameCooldown = TimeSpan.FromHours(24);

    /// <summary>
    /// 无副作用校验改名前置：同名视为合法空操作；否则校验字符规则与 24 小时冷却。
    /// 名称唯一性由宿主通过 identity.players.display_name_normalized 唯一索引保证。
    /// </summary>
    public void EnsureCanRename(string displayName, DateTimeOffset now)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal)) return;
        if (!DisplayNamePattern.IsMatch(normalized))
            throw new DomainException(DomainErrorCode.InvalidArgument, "道号须为 2-12 个字符，且仅含文字、数字、下划线或间隔号。");
        if (now.ToUnixTimeSeconds() < Settings.RenameCooldownUntilUnixSeconds)
            throw new DomainException(DomainErrorCode.Conflict, "改名冷却中，请稍后再试。");
    }

    /// <summary>
    /// 修改道号/角色显示名。校验 2~12 字符与无特殊字符规则，并施加 24 小时改名冷却。
    /// </summary>
    public void Rename(string displayName, DateTimeOffset now)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        EnsureCanRename(normalized, now);
        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal)) return;
        DisplayName = normalized;
        Settings.RenameCooldownUntilUnixSeconds = now.Add(RenameCooldown).ToUnixTimeSeconds();
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
        // 专用执行器（当前为战斗遭遇）不使用普通行动时长，而是由自身阶段时钟推进。
        // 只有普通计时行动的无限循环才必须拒绝零时长，避免模拟器无法消耗时间。
        if (intent.RepeatCount == -1
            && definition.DurationSeconds <= 0
            && definition.ExecutionKind != ActionExecutionKind.CombatEncounter)
            throw new DomainException(DomainErrorCode.InvalidArgument, "无限重复行动必须具有正时长，避免模拟无法推进。");
        var normalizedIntent = new ActionIntent(
            intent.ActionId,
            intent.RepeatCount,
            intent.TargetNodeId,
            intent.Parameters is null
                ? null
                : new ReadOnlyDictionary<string, double>(intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
            intent.PvpEnabled,
            // 锚点记录入队时驻留节点：自动移动开轮准入的唯一目的地真源，旅途中入队不登记。
            intent.AnchorNodeId ?? (World.EdgeId is null ? World.NodeId : null));
        var action = new QueuedAction(Guid.NewGuid(), normalizedIntent, now);
        _actions.Add(action);
        Touch(PlayerSectionKind.ActionQueue);
        return action;
    }

    /// <summary>
    /// 高优先级插入行动到队列首位（Index 0），专用于 world.travel 移动行动。
    /// 原队首正在执行的行动保留已有进度挂起（状态置为 Pending），待移动完成后自动继续。
    /// 若队列中已有旧的同类行动（如 world.travel），则先行移出替换。
    /// 模拟推进内部调用时传 incrementVersion: false 并自行 MarkDirty，保持「一次推进一次版本提交」口径。
    /// </summary>
    public QueuedAction EnqueuePriority(
        ActionIntent intent,
        DateTimeOffset now,
        ActionCatalog catalog,
        TravelActionState? travel = null,
        bool incrementVersion = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (intent is null || !intent.ActionId.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动意图无效。");
        if (!catalog.Contains(intent.ActionId))
            throw new DomainException(DomainErrorCode.NotFound, $"行动 {intent.ActionId} 不存在。");

        // 移除现存的同类行动（保持 world.travel 唯一）；被移除实例的统计账本随之归档。
        for (var i = _actions.Count - 1; i >= 0; i--)
        {
            if (_actions[i].Intent.ActionId == intent.ActionId)
            {
                ArchiveReportForAction(_actions[i].Id, now);
                _actions.RemoveAt(i);
            }
        }

        if (_actions.Count >= MaxActionQueueLength)
            throw new DomainException(DomainErrorCode.Conflict, $"行动队列最多只能包含 {MaxActionQueueLength} 项。");

        // 原首位行动若正在运行，挂起为 Pending，已有 ProgressSeconds 完整保留
        if (_actions.Count > 0 && _actions[0].Status == ActionQueueStatus.Running)
        {
            _actions[0].Status = ActionQueueStatus.Pending;
        }

        var normalizedIntent = new ActionIntent(
            intent.ActionId,
            1,
            intent.TargetNodeId,
            intent.Parameters is null
                ? null
                : new ReadOnlyDictionary<string, double>(intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
            intent.PvpEnabled,
            intent.AnchorNodeId);

        var action = new QueuedAction(Guid.NewGuid(), normalizedIntent, now)
        {
            Travel = travel,
            Status = ActionQueueStatus.Running
        };

        _actions.Insert(0, action);
        if (incrementVersion) Touch(PlayerSectionKind.ActionQueue);
        else MarkDirty(PlayerSectionKind.ActionQueue);
        return action;
    }

    /// <summary>
    /// 从队列中显式取消/移除指定行动实例。
    /// 若移除的是队首正在运行的行动，后续排队的挂起行动立即激活为运行中。
    /// </summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1)
    {
        var index = _actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0) return false;
        if (_actions[index].Expedition is not null && Resources.Hitpoints <= 0)
        {
            Resources.Hitpoints = 1;
            MarkDirty(PlayerSectionKind.Combat);
        }
        // 实例离开队列即归档其统计账本；未传精确时刻时按当前模拟时钟收尾。
        ArchiveReportForAction(actionInstanceId,
            endedAtUnixSeconds >= 0 ? endedAtUnixSeconds : LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d);
        _actions.RemoveAt(index);
        if (index == 0 && _actions.Count > 0 && _actions[0].Status == ActionQueueStatus.Pending)
        {
            _actions[0].Status = ActionQueueStatus.Running;
        }
        if (incrementVersion) Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>
    /// 调整队列中某项行动的执行次序（-1 向上移一位，1 向下移一位）。
    /// 调序后严格维护不变量：仅队首（Index 0）允许为运行中状态，其余项降为排队中。
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
        if (_actions.Count > 0)
        {
            if (_actions[0].Status == ActionQueueStatus.Pending) _actions[0].Status = ActionQueueStatus.Running;
            for (var i = 1; i < _actions.Count; i++)
            {
                if (_actions[i].Status == ActionQueueStatus.Running) _actions[i].Status = ActionQueueStatus.Pending;
            }
        }
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
    /// 切换自动移动开关。开启后开轮行动地点不符时自动插队赶路至入队锚点，替代 Blocked 跳过。
    /// </summary>
    public void SetAutoTravelEnabled(bool enabled)
    {
        if (Settings.AutoTravelEnabled == enabled) return;
        Settings.AutoTravelEnabled = enabled;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 完成或根据循环设置重新入队队首行动。
    /// 若启用全局循环且行动非一次性移动（world.travel），则重置其重复次数与耗时进度后追加至队尾；
    /// 否则直接移出队列。若队列仍有待执行项，激活新队首。
    /// </summary>
    public QueuedAction? CompleteOrRequeueHeadAction(Guid actionId, DateTimeOffset now)
    {
        var index = _actions.FindIndex(action => action.Id == actionId);
        if (index < 0) return null;
        var current = _actions[index];
        // 旧实例出队即归档账本；循环重入产生新实例，将另行建账。
        ArchiveReportForAction(current.Id, now);
        _actions.RemoveAt(index);

        var isLoopable = Settings.LoopEnabled && current.Intent.ActionId.Value != "world.travel";
        QueuedAction? requeued = null;
        if (isLoopable)
        {
            var repeats = current.Intent.RepeatCount > 0 ? current.Intent.RepeatCount : 1;
            requeued = new QueuedAction(Guid.NewGuid(), current.Intent, now)
            {
                RemainingRepeats = repeats,
                CompletedRepeats = 0,
                ProgressSeconds = 0,
                Status = _actions.Count == 0 ? ActionQueueStatus.Running : ActionQueueStatus.Pending,
                Travel = null,
                Expedition = null
            };
            _actions.Add(requeued);
        }

        if (_actions.Count > 0 && _actions[0].Status == ActionQueueStatus.Pending)
        {
            _actions[0].Status = ActionQueueStatus.Running;
        }

        Touch(PlayerSectionKind.ActionQueue);
        return isLoopable ? requeued : null;
    }

    /// <summary>
    /// 将指定行动实例轮转到队列末尾（用于队列无限循环调度或受阻行动换位）。
    /// 轮转后严格维护不变量：新的队首若为 Pending 则置为 Running，原行动若为 Running 则置为 Pending（Blocked 则保持 Blocked）。
    /// </summary>
    public bool RotateActionToTail(Guid actionInstanceId)
    {
        var index = _actions.FindIndex(action => action.Id == actionInstanceId);
        if (index < 0 || _actions.Count <= 1) return false;

        var action = _actions[index];
        _actions.RemoveAt(index);
        if (action.Status == ActionQueueStatus.Running)
        {
            action.Status = ActionQueueStatus.Pending;
        }
        _actions.Add(action);

        if (_actions[0].Status == ActionQueueStatus.Pending)
        {
            _actions[0].Status = ActionQueueStatus.Running;
        }

        Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>
    /// 轮转队首行动至队尾。当队首被阻断且队列有多项行动时轮换，以便后续未阻断行动得到调度。
    /// </summary>
    public bool RotateHeadAction()
    {
        if (_actions.Count <= 1) return false;
        return RotateActionToTail(_actions[0].Id);
    }

    /// <summary>
    /// 突破至下一境界阶层。要求当前修为触及配表瓶颈（Experience ≥ expToNext），
    /// 突破后重置当前阶层灵气与修为，推进 Progression 脏标记。
    /// </summary>
    public void Breakthrough(Domain.GameData.RealmProgressionCatalog? realm)
    {
        if (Progression.RealmLevel < 1)
            throw new DomainException(DomainErrorCode.InvariantViolation, "当前境界状态无效。");
        if (realm is not null)
        {
            var cap = realm.ExpCapFor(Progression.RealmLevel);
            if (cap <= 0)
                throw new DomainException(DomainErrorCode.Conflict, "已至最高境界，无可突破。");
            if (Progression.Experience < cap - 1e-9)
                throw new DomainException(DomainErrorCode.Conflict, "修为未满，尚未触及瓶颈。");
        }
        var realmBefore = Progression.RealmLevel;
        Progression.RealmLevel = checked(Progression.RealmLevel + 1);
        // 突破只重置本层计量：Experience 清零；Foundation 底蕴池与 CombatExperience 对抗池按设计保留不清空。
        Progression.Experience = 0;
        // 突破升级计入行动统计账本（realm 级 LevelUp，TargetId 恒为空串）。
        RecordStats(_actions.Count > 0 ? _actions[0].Id : (Guid?)null,
            LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d,
            report => report.LevelUps.Add(new PlayerReportLevelUp("realm", "", realmBefore, Progression.RealmLevel)));
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
        if (string.Equals(World.NodeId, normalized, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(World.EdgeId)
            && World.X == x && World.Y == y) return;
        World.NodeId = normalized;
        World.EdgeId = null;
        World.EdgeProgress = 0;
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
        Loadout.Equipment.Clear();
        Loadout.Equipment.AddRange(itemIds.Select(id => new PlayerEquipmentSlot(id.Value, 0)));
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>
    /// 配置特定行动类型（如炼丹、战斗、采集）绑定的消耗品自动使用快捷槽位。
    /// </summary>
    public void ConfigureAutoUse(string actionType, int slotIndex, string? itemId, double quantity, bool enabled,
        string? group = null)
    {
        actionType = actionType?.Trim() ?? string.Empty;
        itemId = itemId?.Trim();
        group = string.IsNullOrWhiteSpace(group) ? AutoUseSlotState.SlotGroupGeneral : group.Trim();
        if (string.IsNullOrWhiteSpace(actionType) || actionType.Length > 32 || slotIndex is < 0 || slotIndex > AutoUseSlotState.MaxSlotIndex
            || group is not (AutoUseSlotState.SlotGroupGeneral or AutoUseSlotState.SlotGroupPotion)
            || (group == AutoUseSlotState.SlotGroupPotion && !string.Equals(actionType, "combat", StringComparison.OrdinalIgnoreCase))
            || (itemId is not null && (itemId.Length == 0 || itemId.Length > 128))
            || !double.IsFinite(quantity) || quantity <= 0 || quantity > 1_000_000)
            throw new DomainException(DomainErrorCode.InvalidArgument, "自动使用配置无效。");
        var index = Settings.AutoUseSlots.FindIndex(slot => slot.ActionType == actionType
            && slot.SlotIndex == slotIndex && slot.Group == group);
        var normalizedEnabled = enabled && !string.IsNullOrWhiteSpace(itemId);
        var remainingDurationSeconds = index >= 0
            && normalizedEnabled
            && string.Equals(Settings.AutoUseSlots[index].ItemId, itemId, StringComparison.Ordinal)
                ? Settings.AutoUseSlots[index].RemainingDurationSeconds
                : 0d;
        var cooldown = index >= 0 ? Settings.AutoUseSlots[index].CooldownUntilUnixSeconds : 0;
        var value = new AutoUseSlotState(actionType, slotIndex, itemId, quantity, normalizedEnabled,
            remainingDurationSeconds, cooldown, group);
        if (index >= 0 && Settings.AutoUseSlots[index] == value) return;
        if (index >= 0) Settings.AutoUseSlots[index] = value;
        else Settings.AutoUseSlots.Add(value);
        Settings.AutoUseSlots.Sort((left, right) =>
        {
            var actionOrder = string.CompareOrdinal(left.ActionType, right.ActionType);
            if (actionOrder != 0) return actionOrder;
            var groupOrder = string.CompareOrdinal(left.Group, right.Group);
            return groupOrder != 0 ? groupOrder : left.SlotIndex.CompareTo(right.SlotIndex);
        });
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>将指定分段标记为脏，待下次刷盘时同步；同时推进该分段的标记流水号供触及差集使用。</summary>
    public void MarkDirty(PlayerSectionKind section)
    {
        _dirtySections.Add(section);
        _dirtyMarks[section] = checked(++_dirtyStamp);
    }

    /// <summary>命令完成后推进聚合版本并标记相应 section；持久化层以此做 CAS 乐观锁检测。</summary>
    public long Touch(params PlayerSectionKind[] sections)
    {
        if (sections is null || sections.Length == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "至少需要一个脏 section。");
        StateVersion = checked(StateVersion + 1);
        foreach (var section in sections) MarkDirty(section);
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

    /// <summary>模拟推进器专用：标记某分段受推进影响而发生变更</summary>
    internal void ApplySimulationMutation(PlayerSectionKind section) => MarkDirty(section);
}
