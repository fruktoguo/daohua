using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Components;

/// <summary>
/// 玩家身份组件：承载跨分段共享的聚合头字段（标识、版本号、在线状态与脏标记）。
/// 网络/持久化边界只通过此组件的标识识别实体；分段脏集合跨请求保留，由宿主成功入 durable queue 后确认。
/// 实体在登录恢复（CreatePlayer/RestorePlayer）时创建、登出时销毁；头字段由宿主命令通道回写，
/// 行动系统只读标识、写版本号与脏标记。
/// </summary>
public sealed class PlayerIdentityComponent : YuoComponent
{
    /// <summary>玩家唯一标识；ECS 实体查找与快照归属的主键。</summary>
    public Guid PlayerId { get; set; }
    /// <summary>所属账号 ID（Guid 的 "N" 连续格式文本）；账号与角色的归属边界。</summary>
    public string AccountId { get; set; } = "";
    /// <summary>角色展示名；改名命令经聚合冷却校验后由宿主命令通道回写。</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>聚合状态版本号：每次业务变更 +1，供乐观并发与推送变更判定。</summary>
    public long StateVersion { get; set; } = 1;
    /// <summary>存档对应的配表内容指纹（GameDataCatalog.Fingerprint）；用于检测存档与配表漂移。</summary>
    public string GameDataFingerprint { get; set; } = "";
    /// <summary>角色创建时刻（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>在线状态；转入 Offline 时由宿主重置离线账本并标脏 Duel 分段。</summary>
    public PresenceState Presence { get; set; } = PresenceState.Online;
    /// <summary>组件持久化脏集合跨请求保留，由宿主成功入 durable queue 后确认。</summary>
    public HashSet<PlayerSectionKind> DirtySections { get; } = [];
    /// <summary>分段脏流水号：每次标脏单调递增（已脏分段再次触及也递增），
    /// 与 PlayerAggregateState.DirtySectionMarks 同语义，供命令/推送差集窄化判定。</summary>
    public Dictionary<PlayerSectionKind, long> DirtyMarks { get; } = [];
    /// <summary>标记分段待持久化：加入脏集合并递增该分段脏流水号。</summary>
    public void MarkDirty(PlayerSectionKind section)
    {
        DirtySections.Add(section);
        DirtyMarks[section] = DirtyMarks.TryGetValue(section, out var mark) ? mark + 1 : 1;
    }
}

/// <summary>
/// 玩家成长组件：保存行动系统实际修改的成长数值，而不是缓存聚合对象。
/// Progression 分段由 ECS 单写者直接持有，行动系统经 IPlayerResourceContext 就地写入，
/// 快照导出/恢复由 EcsWorldShard 承担；下方转发属性读写直达分段，便于系统免解引用取值。
/// </summary>
public sealed class PlayerProgressionComponent : YuoComponent
{
    /// <summary>完整领域成长分段由 ECS 单写者直接持有，保留统计、余数与击杀计数。</summary>
    public PlayerProgression Progression { get; set; } = new();
    /// <summary>小境界等级（Lv1~127，每级一个小境界名号）；写入递增境界修订号供属性缓存失效。</summary>
    public int RealmLevel { get => Progression.RealmLevel; set => Progression.RealmLevel = value; }
    /// <summary>本层修为（突破计量条，满层截断、突破清零）。</summary>
    public double Experience { get => Progression.Experience; set => Progression.Experience = value; }
    /// <summary>已完成行动轮次累计（每次单轮结算 +1）。</summary>
    public double CompletedActions { get => Progression.CompletedActions; set => Progression.CompletedActions = value; }
    /// <summary>道龄（已流逝寿元，秒）；随模拟时间自然累积，寿元上限由等阶配表决定。</summary>
    public double AgeSeconds { get => Progression.AgeSeconds; set => Progression.AgeSeconds = value; }
}

/// <summary>
/// 玩家世界位置组件：节点/边坐标、探索种子与节点建筑投影。
/// 节点引用对应长期存在的 MapNodeEntity；旅行系统写坐标与边进度，
/// EcsWorldShard.SyncNodeMembership 在位置变更后统一维护节点人口与驻留实体引用。
/// </summary>
public sealed class PlayerWorldComponent : YuoComponent
{
    /// <summary>完整世界分段，含边上移动与建筑投影字段。</summary>
    public PlayerWorldState World { get; set; } = new();
    /// <summary>当前所在世界节点 ID；处于边中段时仍为出发节点，以 EdgeId 非空判定离站。</summary>
    public string NodeId { get => World.NodeId; set => World.NodeId = value; }
    /// <summary>节点内或边上插值坐标 X（旅行系统按边进度写入）。</summary>
    public double X { get => World.X; set => World.X = value; }
    /// <summary>节点内或边上插值坐标 Y（旅行系统按边进度写入）。</summary>
    public double Y { get => World.Y; set => World.Y = value; }
    /// <summary>迷雾探索与随机掉落的确定性种子；随探索行动推进演化。</summary>
    public double ExplorationSeed { get => World.ExplorationSeed; set => World.ExplorationSeed = value; }
    /// <summary>最近一次节点公共建筑投影版本；仅用于跳过重复读投影，不进入玩家存档。</summary>
    public long NodeBuildingProjectionVersion { get; set; } = long.MinValue;
    /// <summary>
    /// 当前驻留节点的实体引用（对应长期存在的 MapNodeEntity）；纯运行时索引，不进入快照。
    /// 由 EcsWorldShard.SyncNodeMembership 在创建/恢复/命令/移动结算后统一维护。
    /// </summary>
    public long CurrentNodeEntityId { get; set; }
}

/// <summary>
/// 玩家战斗资源组件：气血/灵力池与自然回复游标，战斗内外同一份状态。
/// 触发器系统（受击回血/反伤等）直接读写本组件；落库时并入 Combat 分段文档。
/// </summary>
public sealed class PlayerResourcesComponent : YuoComponent
{
    /// <summary>完整资源分段，包含回复余数。</summary>
    public PlayerResourceState Resources { get; set; } = new();
    /// <summary>当前气血；0 即阵亡，战斗内外同一份状态。</summary>
    public double Hitpoints { get => Resources.Hitpoints; set => Resources.Hitpoints = value; }
    /// <summary>气血上限；属性解析后由资源边界同步回写。</summary>
    public double MaxHitpoints { get => Resources.MaxHitpoints; set => Resources.MaxHitpoints = value; }
    /// <summary>当前灵力（技能消耗池）。</summary>
    public double Manapoints { get => Resources.Manapoints; set => Resources.Manapoints = value; }
    /// <summary>灵力上限；属性解析后由资源边界同步回写。</summary>
    public double MaxManapoints { get => Resources.MaxManapoints; set => Resources.MaxManapoints = value; }
}

/// <summary>
/// 玩家战斗组件：战斗分段基值与 ECS 侧战斗会话引用。
/// Combat 分段保存加成前基础值（面板真值经 PlayerAttributeResolver 合成，不在此缓存）；
/// Encounter 系列字段由战斗管线在遭遇装配/窗口收尾时写入，随遭遇实体销毁清空。
/// </summary>
public sealed class PlayerCombatComponent : YuoComponent
{
    /// <summary>完整战斗分段，包含技能冷却与吟唱。</summary>
    public PlayerCombatState Combat { get; set; } = new();
    /// <summary>基础攻击威能（加成前存档值；面板真值经属性解析器合成）。</summary>
    public double AttackPower { get => Combat.AttackPower; set => Combat.AttackPower = value; }
    /// <summary>基础防御护甲（加成前存档值；面板真值经属性解析器合成）。</summary>
    public double Defense { get => Combat.Defense; set => Combat.Defense = value; }
    /// <summary>进行中战斗的领域状态；与遭遇实体 CombatEncounterComponent.State 共享同一引用，无战斗时为 null。</summary>
    public CombatEncounterState? Encounter { get; set; }
    /// <summary>当前遭遇实体 ID；0 表示无进行中战斗。</summary>
    public long EncounterEntityId { get; set; }
    /// <summary>本场战斗的确定性随机种子（命中/暴击等可复现）。</summary>
    public long EncounterSeed { get; set; }
    /// <summary>最近一次战斗窗口收尾时的终局裁决；Running 表示战斗仍在进行。</summary>
    public CombatOutcome LastOutcome { get; set; } = CombatOutcome.Running;
    /// <summary>最近一次战斗窗口产生的事件条数（与战报/日志口径对齐）。</summary>
    public int LastEventCount { get; set; }
}

/// <summary>
/// 玩家装配组件：装备/功法分段与技能槽真源。
/// 装备解析仍由 Domain 属性解析器负责，ECS 不复制公式；分段由宿主命令通道（聚合命令）写入，
/// 遭遇装配时读取 AbilitySlots 生成随战技能槽。
/// </summary>
public sealed class PlayerLoadoutComponent : YuoComponent
{
    /// <summary>完整装备/功法分段，保留六维、槽位、装备强化与技能经验。</summary>
    public PlayerLoadoutState Loadout { get; set; } = new();
    /// <summary>装备道具 ID 镜像（string 形式）；当前 ECS 运行时未维护，真值以 Loadout.EquippedItemIds 为准。</summary>
    public List<string> EquippedItemIds { get; } = [];
    /// <summary>已习得功法 ID 镜像（string 形式）；当前 ECS 运行时未维护，真值以 Loadout.LearnedAbilities 为准。</summary>
    public List<string> LearnedAbilityIds { get; } = [];
}

/// <summary>
/// 解析后战斗面板缓存组件（不落库）：输入修订号与四路分段引用任一变化即重算。
/// 挂在玩家实体上供触发器/行动系统直接读面板，无需重建解析上下文。
/// </summary>
public sealed class PlayerStatsComponent : YuoComponent
{
    /// <summary>上次解析产出的完整属性面板；null 表示尚未解析或被替换输入失效。</summary>
    public PlayerAttributes? Attributes { get; set; }
    /// <summary>缓存对应的配表实例；换配表即失效。</summary>
    public GameDataCatalog? Catalog { get; set; }
    /// <summary>缓存对应的属性输入修订号。</summary>
    public long InputRevision { get; set; } = -1;
    /// <summary>修订号命中还要求输入分段对象引用未更换；随分段替换自动失效。</summary>
    public PlayerProgression? ProgressionInput { get; set; }
    /// <summary>缓存命中校验用的世界分段输入引用；与上次解析传入对象不同即重算。</summary>
    public PlayerWorldState? WorldInput { get; set; }
    /// <summary>缓存命中校验用的 Buff 分段输入引用；与上次解析传入对象不同即重算。</summary>
    public PlayerBuffState? BuffsInput { get; set; }
    /// <summary>缓存命中校验用的装配分段输入引用；与上次解析传入对象不同即重算。</summary>
    public PlayerLoadoutState? LoadoutInput { get; set; }
}

/// <summary>
/// 玩家持久 Buff 组件：战外增益/减益分段，到期由 PlayerBuffExpirySystem 按资源边界同步裁决。
/// 分段在登录恢复时创建、随实体销毁；战斗边界把 Active 注入战内 CombatBuffState，
/// 属性解析按 StatRatios 合成面板（与战内 Buff 不重复折叠）。
/// </summary>
public sealed class PlayerBuffComponent : YuoComponent
{
    /// <summary>完整战外 Buff 分段，保留版本号与所有 StatRatios。</summary>
    public PlayerBuffState Buffs { get; set; } = new();
    /// <summary>战外持久 Buff 列表视图；写入递增分段版本号，战斗边界据此注入战内 Buff。</summary>
    public Domain.Common.VersionedList<ActiveBuff> Active => Buffs.Active;
}

/// <summary>
/// 粗粒度行动队列组件：玩家行动的调度真源，队首优先推进。
/// RemainingRepeats=-1 保持无限循环（单轮完成后严禁自动出队）；推进游标由行动系统与
/// EcsWorldShard 在恢复/命令回写时维护，是快照 LastSimulatedAt 的载体。
/// </summary>
public sealed class PlayerActionQueueComponent : YuoComponent
{
    /// <summary>排队中的行动实例；队首为当前推进目标，出队/轮转经行动队列变更通道执行。</summary>
    public List<QueuedAction> Actions { get; } = [];
    /// <summary>推进游标：上次模拟推进到达的时刻，快照导出为聚合 LastSimulatedAt。</summary>
    public DateTimeOffset LastAdvancedAt { get; set; }
    /// <summary>LastAdvancedAt 的聚合语义别名（IPlayerResourceContext.LastSimulatedAt 的落点）。</summary>
    public DateTimeOffset LastSimulatedAt { get => LastAdvancedAt; set => LastAdvancedAt = value; }
}

/// <summary>
/// 玩家资产余额镜像组件：按资产键镜像外部钱包余额，供行动准入与快照导出免查宿主。
/// 不进玩家聚合——资产真源仍是 AssetWallet 与 economy.* 账本；请求提交边界由管线从外部钱包整表同步。
/// </summary>
public sealed class PlayerInventoryComponent : YuoComponent
{
    /// <summary>资产键保留变体后缀，避免装备词缀或品质在 ECS 往返中被折叠。</summary>
    public Dictionary<AssetKey, double> Items { get; } = [];
}

/// <summary>
/// 玩家经济关联组件（预留槽位）：按 ECS 化架构规约挂接市场关联数据。
/// 当前运行时无系统读写本组件，市场与资产结算仍走宿主侧 AssetWallet 与 economy.* 账本。
/// </summary>
public sealed class PlayerEconomyComponent : YuoComponent
{
    /// <summary>灵石余额（预留字段；当前无写入方，真源在宿主侧钱包）。</summary>
    public double SpiritStones { get; set; }
    /// <summary>资产版本号（预留字段；当前无写入方）。</summary>
    public long AssetVersion { get; set; }
}

/// <summary>
/// 玩家偏好设置组件：语言、通知、循环模式与自动使用槽位等低频分段。
/// 由宿主命令通道写入；行动系统在推进时读取循环/自动赶路/停泊开关决定队列行为。
/// </summary>
public sealed class PlayerSettingsComponent : YuoComponent
{
    /// <summary>完整设置分段（Locale/LoopEnabled/AutoTravelEnabled/QueuePaused/AutoUseSlots 等）。</summary>
    public PlayerSettingsState Settings { get; set; } = new();
}

/// <summary>
/// 玩家斗法分段组件：遭遇计数、失败冷却、暂存区与行动/离线/每日三账本。
/// 账本由记账系统（ActionStats/OfflineStats/DailyStats）与队列归档系统写入；
/// 文档导出/恢复只发生在网络或落盘快照边界，行动边界不调用。
/// </summary>
public sealed class PlayerDuelComponent : YuoComponent
{
    /// <summary>完整斗法分段状态。</summary>
    public PlayerDuelState Duel { get; set; } = new();

    /// <summary>
    /// 分段文档导出/恢复边界：导出时按队首行动实例投影 CurrentReport，
    /// 恢复时就地重建账本集合（CurrentReport 为冗余导出字段，不参与恢复）。
    /// </summary>
    public PlayerDuelStateDocument Document
    {
        get
        {
            var actions = Entity.GetComponent<PlayerActionQueueComponent>()!.Actions;
            return PlayerDuelStateCodec.Export(Duel, actions.Count == 0 ? null : actions[0].Id);
        }
        set => PlayerDuelStateCodec.Restore(Duel, value);
    }
}


/// <summary>
/// 怪物与玩家共享的战斗单位组件：把领域战斗单位状态挂接到真实 ECS 实体。
/// State 由遭遇装配时注入，与 CombatEncounterComponent.State 内的单位引用相同，
/// 战斗引擎经领域原语就地修改；随遭遇销毁或实体回收而失效。
/// </summary>
public sealed class CombatantComponent : YuoComponent
{
    /// <summary>遭遇与单位组件共同持有的唯一战斗状态；装配完成后即可参与相位或触发器。</summary>
    public CombatantState State { get; set; } = null!;
    /// <summary>战斗单位标识（player 或 enemy:{index}）。</summary>
    public string CombatantId => State.Id;
    /// <summary>战斗静态属性面板视图（随战斗跨步 RefreshStats 换入最新结算结果）。</summary>
    public CombatantStats Stats => State.Stats;
    /// <summary>装配时标记该战斗单位是否为玩家侧单位；由遭遇装配写入。</summary>
    public bool IsPlayer { get; set; }
}

/// <summary>
/// 怪物配表身份组件：记录怪物定义与展示名，供掉落结算与战报回查配表。
/// 随怪物实体在遭遇装配/生态刷怪时创建，随遭遇销毁或实体回收而销毁。
/// </summary>
public sealed class MonsterIdentityComponent : YuoComponent
{
    /// <summary>怪物配表定义 ID（CombatMonsters 键）。</summary>
    public string MonsterDefinitionId { get; set; } = "";
    /// <summary>怪物展示名（配表缺省时回退定义 ID）。</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// 技能调度组件：CombatantState 技能字段的实体视图（玩家与怪物共用）。
/// 冷却、吟唱与施放计数由战斗引擎按边界推进写入；战斗状态随遭遇销毁，组件随实体回收。
/// </summary>
public sealed class SkillStateComponent : YuoComponent
{
    private CombatantState State => Entity.GetComponent<CombatantComponent>()!.State;
    /// <summary>随战技能槽（与 CombatantState.Skills 同一列表）。</summary>
    public IReadOnlyList<SkillSlot> Skills => State.Skills;
    /// <summary>逐槽冷却就绪时刻（战斗时钟秒）；自吟唱完成时刻起算，null 表示无冷却记录。</summary>
    public double[]? CooldownUntil { get => State.SkillCooldownUntil; set => State.SkillCooldownUntil = value; }
    /// <summary>正在吟唱的技能槽下标；-1 表示下一次出手为普通攻击。</summary>
    public int PendingSkillIndex { get => State.PendingSkillIndex; set => State.PendingSkillIndex = value; }
    /// <summary>累计完成施放的技能次数；作为技能出手的确定性抽样独立计数器。</summary>
    public double CastCount { get => State.SkillCastCount; set => State.SkillCastCount = value; }
}

/// <summary>
/// 战内 Buff 组件：CombatantState.Buffs 的实体视图（战斗时钟过期，随战斗快照持久化）。
/// 由战斗引擎在边界内授予/叠层/剔除；战外持久 Buff 走 PlayerBuffComponent，两者不互通。
/// </summary>
public sealed class BuffComponent : YuoComponent
{
    /// <summary>战内 Buff 为单位状态的直接视图，授予、叠层和到期不会形成平行列表。</summary>
    public List<CombatBuffState> Active => Entity.GetComponent<CombatantComponent>()!.State.Buffs;
}

/// <summary>
/// 掉落表组件：战斗单位阵亡后的掉落候选（配表 DropTable 引用）。
/// 随怪物实体在遭遇装配时创建；战斗结算按确定性随机通道逐条判定掉落。
/// </summary>
public sealed class DropProfileComponent : YuoComponent
{
    /// <summary>掉落条目列表（物品、掉率与数量区间）。</summary>
    public IReadOnlyList<CombatDropDefinition> Drops { get; set; } = [];
}

/// <summary>
/// 地图节点组件：世界拓扑中一个节点的静态描述，随节点实体长期存在（与分片同生命周期）。
/// 创建时由配表 WorldGraph 一次性写入，运行期只读；玩家与怪物经 NodeId 关联到此实体。
/// </summary>
public sealed class MapNodeComponent : YuoComponent
{
    /// <summary>节点配表 ID（WorldGraph 键）。</summary>
    public string NodeId { get; set; } = "";
    /// <summary>节点展示名。</summary>
    public string Name { get; set; } = "";
    /// <summary>节点在地图上的坐标 X。</summary>
    public double X { get; set; }
    /// <summary>节点在地图上的坐标 Y。</summary>
    public double Y { get; set; }
    /// <summary>相邻节点 ID 列表（由世界边推导，按序去重）。</summary>
    public IReadOnlyList<string> Neighbors { get; set; } = [];
}

/// <summary>
/// 节点公共建筑组件：承载共享注册表向 ECS 实体的只读投影。
/// 节点实体随分片长期存在；宿主按节拍刷新投影（只标脏 World 分段，不递增玩家业务版本）。
/// </summary>
public sealed class NodeBuildingComponent : YuoComponent
{
    /// <summary>本节点全部已建成建筑按等级线性叠加后的汇总效果（产出比率/加速比率/战斗回复与减伤）。</summary>
    public BuildingEffectSet Effects { get; set; } = BuildingEffectSet.Empty;
    /// <summary>节点建筑运行时状态镜像；权威状态仍在 NodeBuildingRegistry，组件只承载投影供实体查询。</summary>
    public IReadOnlyList<Domain.World.NodeBuildingState> Buildings { get; set; } = [];
}

/// <summary>
/// 节点遭遇池组件：该节点可触发的遭遇定义 ID 列表（配表一次性写入，长期只读）。
/// 行动系统优先经驻留节点实体读取遭遇池，旅途中（无驻留节点实体）回落配表查询。
/// </summary>
public sealed class EncounterProfileComponent : YuoComponent
{
    /// <summary>遭遇配表 ID 列表（CombatEncounters 键）。</summary>
    public IReadOnlyList<string> EncounterIds { get; set; } = [];
}

/// <summary>
/// 节点人口组件：驻留玩家与活跃怪物的运行时计数，不进任何快照。
/// 玩家集合由 EcsWorldShard.SyncNodeMembership 在位置变更后同步维护；
/// 怪物计数在生态刷怪挂接父节点时递增。
/// </summary>
public sealed class NodePopulationComponent : YuoComponent
{
    /// <summary>当前驻留本节点的玩家实体 ID；由玩家位置变更时同步维护，不进快照。</summary>
    public HashSet<long> PresentPlayerEntityIds { get; } = [];
    /// <summary>本节点活跃怪物计数（生态刷怪时递增）。</summary>
    public int ActiveMonsterCount { get; set; }
    /// <summary>节点容量上限（默认 100；当前 ECS 运行时内无消费方，供生态容量扩展预留）。</summary>
    public int Capacity { get; set; } = 100;
}

/// <summary>
/// 战斗遭遇实体组件：一场进行中战斗的宿主，持有遭遇与全部单位共享的唯一战斗状态。
/// 由 EcsWorldShard 在遭遇装配时创建、终局/取消/回滚时销毁；战斗相位系统按自然边界推进 State，
/// 玩家与怪物的 CombatantComponent.State 与其中的单位引用相同，领域原语就地修改真实 ECS 单位。
/// </summary>
public sealed class CombatEncounterComponent : YuoComponent
{
    /// <summary>遭遇配表 ID（CombatEncounters 键）。</summary>
    public string EncounterId { get; set; } = "";
    /// <summary>参战玩家实体 ID。</summary>
    public long PlayerEntityId { get; set; }
    /// <summary>敌方单位实体 ID 列表（与 State.Enemies 按下标一一对应）。</summary>
    public IReadOnlyList<long> EnemyEntityIds { get; set; } = [];
    /// <summary>
    /// 非本机盟友单位实体 ID 列表（参与会话挂接的纯战斗实体，与 State.PlayerAllies 中非本机下标对应）；
    /// 单人恒为空列表。盟友实体只承载触发器派发的实体句柄，不映射真实玩家实体。
    /// </summary>
    public IReadOnlyList<long> AllyEntityIds { get; set; } = [];
    /// <summary>唯一战斗状态聚合（单玩家对阵同波全体敌人）。</summary>
    public CombatEncounterState State { get; set; } = null!;
    /// <summary>本场战斗的确定性随机种子（命中/暴击等可复现）。</summary>
    public long Seed { get; set; }
    /// <summary>本场战斗相对时钟的起点；用于把宿主 DateTimeOffset 的 100ns 量化与领域 double 边界对齐。</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>玩家当前集火目标的实体 ID（CombatTargetSystem 按有效威胁最高者维护）；0 表示无存活目标。</summary>
    public long PrimaryTargetEntityId { get; set; }
    /// <summary>装配时的属性输入修订号快照；战斗中配表锚点变化时管线比对刷新，保证面板与配表一致。</summary>
    public long AttributeRevision { get; set; }
    /// <summary>最近一次边界推进产生的战斗事件；CombatEventSystem 按原序追加进日志后清空。</summary>
    public List<CombatEvent> LastEvents { get; } = [];
    /// <summary>AdvanceBoundary 推进期间的相位工作上下文：相位 System 经 RunSystem&lt;tag&gt; 派发后从此读取；请求边界间为 null。</summary>
    public DaoHua.Ecs.Combat.CombatPhaseContext? PhaseContext { get; set; }
}

/// <summary>
/// 战斗日志组件：遭遇实体上的战斗事件流水（按领域事件原序追加，不重算随机数）。
/// 随遭遇实体创建与销毁；供战报/前端回放消费。
/// </summary>
public sealed class CombatLogComponent : YuoComponent
{
    /// <summary>本场战斗累计的攻击/受创事件明细。</summary>
    public List<CombatEvent> Events { get; } = [];
}

/// <summary>
/// 世界级时钟与只读配表目录：挂在 YuoWorld.Main，系统按固定边界推进。
/// 由 EcsWorldShard 在每次请求推进前经 Fiber mailbox 写入，系统只读；
/// 战斗/行动系统据此判定本帧是否推进、是否聚焦当前玩家。
/// </summary>
public sealed class EcsClockComponent : YuoComponent
{
    /// <summary>本分片的只读配表目录（GameDataCatalog）。</summary>
    public GameDataCatalog Catalog { get; set; } = GameDataCatalog.BuiltIn;
    /// <summary>当前世界时钟（本帧已推进到的时刻）。</summary>
    public DateTimeOffset Current { get; set; }
    /// <summary>本帧与上一自然边界的间隔秒数（准备阶段为 0；当前仅诊断写入，无读取方）。</summary>
    public double DeltaSeconds { get; set; }
    /// <summary>准备阶段标记：true 时系统只做边界准备，不推进真实结算与战斗单位。</summary>
    public bool PreparationOnly { get; set; }
    /// <summary>本帧推进边界（Tick 的目标时刻）。</summary>
    public DateTimeOffset Target { get; set; }
    /// <summary>当前请求的硬上界；战斗事件不得因 DateTime 量化越过该 double 目标。</summary>
    public DateTimeOffset RequestTarget { get; set; }
    /// <summary>当前请求聚焦的玩家 ID；非空时其他玩家的行动/战斗系统跳过本帧。</summary>
    public Guid? ActivePlayerId { get; set; }
    /// <summary>请求提交阶段只消费 Fiber mailbox，不再次运行行动系统。</summary>
    public bool SkipWorldUpdate { get; set; }
    /// <summary>待跳过世界更新的实体计数；逐实体递减归零后解除 SkipWorldUpdate。</summary>
    public int SkipWorldUpdateEntities { get; set; }
    /// <summary>由 Fiber 内的行动阶段调用；宿主线程不得直接改玩家组件。</summary>
    public Func<YuoEntity, CombatEncounterState, DateTimeOffset, CombatEncounterComponent>? AttachCombatBattle { get; set; }
    /// <summary>战斗终局/取消时由行动管线回调，销毁该玩家的全部遭遇实体。</summary>
    public Action<YuoEntity>? ReleaseCombatBattle { get; set; }
}
