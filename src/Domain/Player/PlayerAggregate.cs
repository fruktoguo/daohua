using Domain.Combat;
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
    /// <summary>已解锁特质 ID 集合（trait.*，总纲 §7.12）：常驻特殊效果的获得标记；写入递增版本号供属性缓存失效检测。</summary>
    public VersionedList<string> TraitIds { get; } = new();
    /// <summary>已解锁特质养成记录（trait.* -> 等级与经验，总纲 §7.12.4）：抽到特质即解锁，重复获赠经验升级；写入递增版本号。</summary>
    public VersionedDictionary<string, Domain.Combat.TraitProgressState> UnlockedTraits { get; } = new(StringComparer.Ordinal);
    /// <summary>已击败妖兽 ID 集合（enemy.*，总纲 §23）：万象图鉴妖兽志解锁记录；写入递增版本号。</summary>
    public VersionedList<string> DefeatedMonsterIds { get; } = new();
    /// <summary>服务端暂存的装备分级强化履历记录（按 ActionId 索引）；供离线挂机暂存、客户端上线拉取与双向回执确认清理。</summary>
    public VersionedDictionary<Guid, ActionEnhancementRecord> StagedEnhancements { get; } = new();

    /// <summary>
    /// 记录单次强化尝试结果：按行动实例与目标等级累加 Attempts 与 Successes。
    /// </summary>
    public bool RecordEnhancementAttempt(Guid actionId, string itemId, int targetLevel, bool success, double atUnixSeconds)
    {
        if (actionId == Guid.Empty || string.IsNullOrWhiteSpace(itemId) || targetLevel < 1 || targetLevel > EnhancementRules.MaxLevel)
            return false;

        if (!StagedEnhancements.TryGetValue(actionId, out var record))
        {
            record = new ActionEnhancementRecord
            {
                ActionId = actionId,
                ItemId = itemId,
                IsCompleted = false,
                UpdatedAtUnixSeconds = atUnixSeconds
            };
            StagedEnhancements[actionId] = record;
        }

        var current = record.Levels.TryGetValue(targetLevel, out var stat) ? stat : new EnhancementLevelStat(0, 0);
        record.Levels[targetLevel] = new EnhancementLevelStat(current.Attempts + 1, current.Successes + (success ? 1 : 0));
        record.UpdatedAtUnixSeconds = atUnixSeconds;
        return true;
    }

    /// <summary>
    /// 将指定行动实例的强化记录标记为已完成（行动出队或达成目标）。
    /// </summary>
    public bool MarkEnhancementCompleted(Guid actionId, double atUnixSeconds)
    {
        if (actionId == Guid.Empty) return false;
        if (StagedEnhancements.TryGetValue(actionId, out var record) && !record.IsCompleted)
        {
            record.IsCompleted = true;
            record.UpdatedAtUnixSeconds = atUnixSeconds;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 响应客户端确认回执：彻底移除所有已标记为 IsCompleted 的暂存记录；
    /// 正在进行中（IsCompleted == false）的记录坚决保留，直到行动彻底完成并再次被确认。
    /// </summary>
    public int RemoveCompletedEnhancementRecords(IEnumerable<Guid> actionIds)
    {
        if (actionIds is null) return 0;
        var removed = 0;
        foreach (var id in actionIds)
        {
            if (StagedEnhancements.TryGetValue(id, out var record) && record.IsCompleted)
            {
                if (StagedEnhancements.Remove(id))
                    removed++;
            }
        }
        return removed;
    }
    /// <summary>当前层修为（突破计量条，expToNext 封顶，突破清零）</summary>
    public double Experience { get; set; }
    private double _foundation;
    /// <summary>底蕴：修为满层溢出沉淀池；未满层时按 1:1 兑换放大修为获取（单笔 ≤3× 增幅量），突破不清空。</summary>
    public double Foundation
    {
        get => _foundation;
        set => _foundation = double.IsFinite(value) && value < 1e-9 ? 0 : value;
    }
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
    /// <summary>道龄（已流逝寿元，秒）；随模拟时间自然累积，寿元上限由等阶配表决定。</summary>
    public double AgeSeconds { get; set; }

    /// <summary>开天门与五行灵根状态：叩天门满修为后必须先开天门取得灵根才能继续突破。</summary>
    public PlayerHeavenGateState HeavenGate { get; } = new();

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
            Math.Min(amplifiedAmount + Math.Max(0.0, foundation), Math.Max(0, cap - experience)));

    /// <summary>
    /// 修为类收益统一结算（总纲 §7.11.1）：
    /// Experience += credited；Foundation += amp - credited（净变动，可为负）；
    /// 仅战斗讨伐/击杀传入 combatExperience（打坐修炼等日常修行恒为 0）；
    /// LifetimeExperience += amplifiedAmount。
    /// 返回实际计入本层修为的量，供战报账本按真实入账口径记录。
    /// </summary>
    /// <param name="amplifiedAmount">玩家增幅倍率作用后的收益量。</param>
    /// <param name="cap">本层修为上限；无境界进度配置时传 <see cref="double.PositiveInfinity"/>。</param>
    /// <param name="combatExperience">战斗经验入账量；非战斗场景（如打坐修行）恒为 0，不受加成与瓶颈影响。</param>
    /// <returns>实际计入本层修为的量（≥0）。</returns>
    public double CreditExperience(double amplifiedAmount, double cap, double combatExperience = 0)
    {
        if (!double.IsFinite(amplifiedAmount) || amplifiedAmount < 0
            || !double.IsFinite(combatExperience) || combatExperience < 0
            || double.IsNaN(cap) || cap < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "修为收益入账参数无效。");
        var credited = ComputeExperienceCredit(amplifiedAmount, cap, Experience, Foundation);
        Experience += credited;
        Foundation = Math.Max(0.0, Foundation + amplifiedAmount - credited);
        CombatExperience += combatExperience;
        LifetimeExperience += amplifiedAmount;
        if (!double.IsFinite(Experience) || !double.IsFinite(Foundation) || !double.IsFinite(CombatExperience) || !double.IsFinite(LifetimeExperience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "修为收益入账结果无效。");
        return credited;
    }

    /// <summary>
    /// 设置已激活特质集合（总纲 §7.12）：严格校验消耗点数不超过当前境界特质容量。
    /// </summary>
    public void SetTraits(IEnumerable<string>? traitIds)
    {
        if (traitIds is null)
        {
            TraitIds.Clear();
            return;
        }
        var list = traitIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in list)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                throw new DomainException(DomainErrorCode.InvalidArgument, "特质 ID 无效。");
            if (UnlockedTraits.Count > 0 && !UnlockedTraits.ContainsKey(id))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"特质 {id} 尚未觉醒解锁。");
        }
        var totalCost = Domain.Combat.CombatTraitRules.TotalCost(list);
        var capacity = Domain.Combat.CombatTraitRules.CalculateCapacity(RealmLevel);
        if (totalCost > capacity)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"特质点数超出上限：当前境界容量 {capacity} 点，所选特质需 {totalCost} 点。");

        TraitIds.Clear();
        TraitIds.AddRange(list);
    }

    /// <summary>
    /// 使用觉醒石觉醒特质（总纲 §7.12.4）：抽取一项特质，未拥有初入 Lv 1，已拥有累积经验并判定升级。
    /// </summary>
    public (string TraitId, int Level, int Exp, bool LeveledUp, bool IsNew) AwakenTrait(Random random, string? targetTraitId = null)
    {
        ArgumentNullException.ThrowIfNull(random);
        var traitId = targetTraitId ?? Domain.Combat.CombatTraitRules.PickRandomTrait(random);
        if (!Domain.Combat.CombatTraitRules.AllTraits.ContainsKey(traitId))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"未知特质 {traitId}。");

        if (UnlockedTraits.TryGetValue(traitId, out var existing))
        {
            var (newLevel, newExp, leveledUp) = Domain.Combat.CombatTraitRules.AddExp(existing.Level, existing.Exp, 1);
            UnlockedTraits[traitId] = new Domain.Combat.TraitProgressState(traitId, newLevel, newExp);
            return (traitId, newLevel, newExp, leveledUp, IsNew: false);
        }
        else
        {
            UnlockedTraits[traitId] = new Domain.Combat.TraitProgressState(traitId, 1, 0);
            return (traitId, 1, 0, LeveledUp: false, IsNew: true);
        }
    }

    /// <summary>分段回滚检查点专用：按备份文档就地恢复全部字段，并把境界修订号写回备份值。</summary>
    internal void RestoreSnapshot(PlayerProgressionState state, long realmLevelVersion, long traitIdsVersion, long defeatedMonsterIdsVersion, long unlockedTraitsVersion, long stagedEnhancementsVersion)
    {
        _realmLevel = state.RealmLevel;
        RealmLevelVersion = realmLevelVersion;
        TraitIds.Clear();
        if (state.TraitIds is { Count: > 0 } traits) TraitIds.AddRange(traits);
        TraitIds.RestoreVersion(traitIdsVersion);
        DefeatedMonsterIds.Clear();
        if (state.DefeatedMonsterIds is { Count: > 0 } defeated) DefeatedMonsterIds.AddRange(defeated);
        DefeatedMonsterIds.RestoreVersion(defeatedMonsterIdsVersion);
        UnlockedTraits.Clear();
        if (state.UnlockedTraits is { Count: > 0 } unlocked)
        {
            foreach (var t in unlocked) UnlockedTraits[t.Id] = t;
        }
        UnlockedTraits.RestoreVersion(unlockedTraitsVersion);
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
        HeavenGate.Apply(state.HeavenGateRolled, state.HeavenGateConfirmed, state.HeavenGateEntered, state.HeavenGateRerollCount);
        StagedEnhancements.Clear();
        if (state.StagedEnhancements is { Count: > 0 } staged)
        {
            foreach (var r in staged)
                StagedEnhancements[r.ActionId] = r.Clone();
        }
        StagedEnhancements.RestoreVersion(stagedEnhancementsVersion);
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
    /// 各节点建筑拥挤使用快照（与 NodeBuildings 同节拍投影并持久化；
    /// 供建筑 tab 展示「使用中/承载」与超载衰减）。
    /// </summary>
    public List<Domain.World.NodeBuildingUsage> NodeBuildingUsages { get; set; } = [];
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
        NodeBuildingUsages = state.NodeBuildingUsages?.ToList() ?? [];
        AttributeInputVersion = attributeInputVersion;
    }
}

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

/// <summary>
/// 角色基础战斗属性分段（运行时由装备、功法与境界动态加成覆盖）。
/// 存档保存的是加成前的基础值；面板真值经属性解析器合成，不在此处缓存。
/// </summary>
public sealed class PlayerCombatState
{
    /// <summary>基础攻击威能</summary>
    public double AttackPower { get; set; } = 10;
    /// <summary>基础防御护甲</summary>
    public double Defense { get; set; } = 10;
    /// <summary>技能调度状态（冷却、吟唱与施放计数）。</summary>
    public PlayerCombatSkillState Skills { get; set; } = PlayerCombatSkillState.Empty;
    /// <summary>战内 Buff 跨波携带态：整场战斗行动存续期间随波次清空折算携带，行动结束统一移除。</summary>
    public PlayerCombatBuffState Buffs { get; set; } = PlayerCombatBuffState.Empty;
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
/// 单个附着 Buff 的持久化记录（战外持久 Buff）。数值效果以通道载荷承载（总纲 §7.9.5.3），
/// 与战内 CombatBuffState 共享同一效果形态，开战注入与面板折算不复制字段。
/// </summary>
/// <param name="BuffId">配表 Buff 唯一标识</param>
/// <param name="ExpiresAtUnixSeconds">过期时间戳（Unix 秒）；durable 阵盘类为 +∞（战内折 double.MaxValue 永驻）。</param>
/// <param name="Stacks">当前叠加层数</param>
/// <param name="Name">展示名（配表缺省时回退 BuffId）。</param>
/// <param name="SourceItemId">授予该 Buff 的物品 ID（消耗品来源追溯用）。</param>
/// <param name="Effects">数值效果通道载荷（每层基础值）。</param>
/// <param name="StatRatios">按属性名到比率百分点的修饰字典（如 attackPower: 10 = +10%）。</param>
public sealed record ActiveBuff(string BuffId, double ExpiresAtUnixSeconds, double Stacks = 1,
    string Name = "", string? SourceItemId = null,
    IReadOnlyList<BuffEffect>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null)
{
    /// <summary>读指定通道的每层基础值（Instance 型为总值）；未携带该通道返回 0。</summary>
    public double EffectValue(BuffEffectChannel channel)
    {
        if (Effects is null) return 0;
        for (var i = 0; i < Effects.Count; i++)
            if (Effects[i].Channel == channel) return Effects[i].Value;
        return 0;
    }
}

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
    /// <summary>行动队列执行完毕后是否整体循环轮循（新建角色默认关闭，避免误导新手）</summary>
    public bool LoopEnabled { get; set; }
    /// <summary>开轮地点不符时是否自动插队赶路至入队锚点再执行（false 时走 Blocked 跳过语义）</summary>
    public bool AutoTravelEnabled { get; set; }
    /// <summary>队列停泊开关：暂停时行动队列整体冻结不消费墙钟，队列结构与进度保留，恢复后继续</summary>
    public bool QueuePaused { get; set; }
    /// <summary>各行动类型绑定的丹药/补给品自动使用槽位配置</summary>
    public List<AutoUseSlotState> AutoUseSlots { get; } = [];
    /// <summary>改名冷却截止时刻（Unix 秒）；0 表示未冷却。</summary>
    public double RenameCooldownUntilUnixSeconds { get; set; }
    /// <summary>新手引导里程碑下标（见 <see cref="TutorialSteps"/>；服务端权威推进，达到终态后不再回退）</summary>
    public int TutorialStep { get; set; }
    /// <summary>新手引导奖励游标（已发放至第几里程碑）：推进仅当 nextStep > 游标才入账，
    /// 跳过时补发游标+1..Done 的剩余奖励；重学（restart）只重置 TutorialStep、游标不动。</summary>
    public int TutorialRewardsStep { get; set; }
    /// <summary>默认女剑修形象标识。</summary>
    public const string DefaultFemaleAvatar = "player";
    /// <summary>默认男剑修形象标识。</summary>
    public const string DefaultMaleAvatar = "player-male";
    /// <summary>本命形象外观标识（默认 player，可选 player-male）。</summary>
    public string Avatar { get; set; } = DefaultFemaleAvatar;
    /// <summary>已解锁的形象标识集合（默认仅开放女剑修与男剑修）。</summary>
    public HashSet<string> UnlockedAvatars { get; } = [DefaultFemaleAvatar, DefaultMaleAvatar];
    /// <summary>
    /// 组队讨伐的复活时机策略（总纲 §7.9.4.1）：本人在会话内倒地时的起身时机，由本人设定。
    /// 单人战斗恒按直接复活处理，此设置只在参与会话中生效。
    /// </summary>
    public CombatRespawnPolicy RespawnPolicy { get; set; } = CombatRespawnPolicy.Immediate;
    /// <summary>
    /// 「指定队友存活」的观察对象玩家 ID；null 表示未指定（该策略退化为直接复活）。
    /// 目标不在队伍中时同样按直接复活处理，不改写玩家设置。
    /// </summary>
    public Guid? RespawnTargetPlayerId { get; set; }

    /// <summary>检查形象是否已解锁（女剑修与男剑修恒为已解锁）。</summary>
    public bool IsAvatarUnlocked(string? avatar)
    {
        var normalized = PlayerAggregate.NormalizeAvatar(avatar);
        return normalized is DefaultFemaleAvatar or DefaultMaleAvatar || UnlockedAvatars.Contains(normalized);
    }

    /// <summary>解锁指定形象标识。</summary>
    public bool UnlockAvatar(string? avatar)
    {
        var normalized = PlayerAggregate.NormalizeAvatar(avatar);
        return UnlockedAvatars.Add(normalized);
    }
}

/// <summary>
/// 新手引导里程碑常量表（总纲 §22.2）。步骤单调递增：0 采集、1 炼丹、2 炼器、
/// 3 前往云巅草庐、4 修炼、5 返回云溪村、6 讨伐、7 完成/跳过终态。
/// </summary>
public static class TutorialSteps
{
    /// <summary>采集凝气草一轮</summary>
    public const int Gather = 0;
    /// <summary>炼制引气丹一轮</summary>
    public const int Alchemy = 1;
    /// <summary>锻造青锋剑一轮</summary>
    public const int Forge = 2;
    /// <summary>旅行到达云巅草庐</summary>
    public const int TravelToPeak = 3;
    /// <summary>引气入体一轮</summary>
    public const int Cultivate = 4;
    /// <summary>旅行返回云溪村</summary>
    public const int TravelToVillage = 5;
    /// <summary>讨伐行动完成一轮</summary>
    public const int Combat = 6;
    /// <summary>引导终态（完成或跳过均收敛到此值）</summary>
    public const int Done = 7;
}

/// <summary>
/// 自动化使用快捷槽配置项。ActionType 承载物品栏域标识（<see cref="Domain.GameData.AutoUseBar"/>）：
/// 历练栏（修炼/历练类行动共用，6 槽）与制造技艺栏、战斗栏（药品栏 potion 仅药品类即时消耗品，
/// 通用栏 general 任意战斗消耗品，各 3 槽）。
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
    /// <summary>通用栏：任意适用消耗品（符咒、阵法、特殊道具、药品等）。</summary>
    public const string SlotGroupGeneral = "general";
    /// <summary>药品栏：仅限药品类即时消耗品；仅战斗物品栏域有效。</summary>
    public const string SlotGroupPotion = "potion";
    /// <summary>战斗物品栏域槽位总数（药品栏 3 + 通用栏 3）。</summary>
    public const int CombatSlotCount = Domain.GameData.AutoUseBar.DefaultSlotCount * 2;
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
/// <param name="TargetItemId">目标物品 ID：强化行动指定要强化的装备（决定强化等级与动态时长）。</param>
/// <param name="AuxItemId">辅助物品 ID：强化行动的保护材料（须在装备配表 enhanceProtectionItems 列表内，或同装备无强化基础件代护）。</param>
public sealed record ActionIntent(
    ActionId ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, double>? Parameters = null,
    bool PvpEnabled = false,
    string? AnchorNodeId = null,
    string? TargetItemId = null,
    string? AuxItemId = null)
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
    public ActionIntent Intent { get; internal set; }
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
    /// <summary>行动参与绑定：共享剧本规格与权威散场时刻。</summary>
    public ActionParticipationBinding? Participation { get; internal set; }
    /// <summary>强化行动当前强化的物品变体等级（单件锁定追踪）</summary>
    public int? EnhancementCurrentLevel { get; internal set; }

    TravelActionState? Domain.Simulation.IPlayerActionState.Travel { get => Travel; set => Travel = value; }
    CombatExpeditionState? Domain.Simulation.IPlayerActionState.Expedition { get => Expedition; set => Expedition = value; }
    ActionParticipationBinding? Domain.Simulation.IPlayerActionState.Participation => Participation;
    ActionQueueStatus Domain.Simulation.IPlayerActionState.Status { get => Status; set => Status = value; }
    double Domain.Simulation.IPlayerActionState.ProgressSeconds { get => ProgressSeconds; set => ProgressSeconds = value; }
    double Domain.Simulation.IPlayerActionState.RemainingRepeats { get => RemainingRepeats; set => RemainingRepeats = value; }
    double Domain.Simulation.IPlayerActionState.CompletedRepeats { get => CompletedRepeats; set => CompletedRepeats = value; }
    int? Domain.Simulation.IPlayerActionState.EnhancementCurrentLevel { get => EnhancementCurrentLevel; set => EnhancementCurrentLevel = value; }

    /// <summary>深拷贝当前行动状态副本</summary>
    public QueuedAction Clone() => new(Id, Intent, EnqueuedAt)
    {
        Status = Status,
        ProgressSeconds = ProgressSeconds,
        RemainingRepeats = RemainingRepeats,
        CompletedRepeats = CompletedRepeats,
        Expedition = Expedition,
        Travel = Travel,
        Participation = Participation,
        EnhancementCurrentLevel = EnhancementCurrentLevel,
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
    private IPlayerStatsSink? _statsSink;
    private IPlayerActionQueueMutationSink? _actionQueueSink;
    private PlayerActionQueueMutationContext? _actionQueueWork;

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
    /// <summary>离线锚点：最近一次由在线转为非在线状态的时刻；在线时为 null。</summary>
    public DateTimeOffset? OfflineSinceAt { get; private set; }
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
    /// （境界阶层 / 节点建筑效果三元组 / 穿戴装备 ID / 装备槽强化 / 六维经验 / 活跃 Buff / 五行灵根）。
    /// 任一输入源写入都会令其严格增大，合成值相等当且仅当全部输入未变；
    /// 属性解析缓存以「同配表实例 + 本修订号未变」为命中条件，命中结果与全量重算逐位一致。
    /// </summary>
    internal long AttributeInputRevision =>
        Progression.RealmLevelVersion
        + World.AttributeInputVersion
        + Buffs.Active.Version
        + Loadout.EquippedItemIds.Version
        + Loadout.Equipment.Version
        + Loadout.CoreAttributesVersion
        + Progression.TraitIds.Version
        + Progression.UnlockedTraits.Version
        + Loadout.AbilitySlots.Version
        // 开天门 / 逆天改命改写生效灵根：五行增伤减伤点数随之变化，必须令面板缓存失效。
        + Progression.HeavenGate.Version;

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
        DateTimeOffset? offlineSinceAt,
        DateTimeOffset lastSimulatedAt,
        PlayerProgression progression,
        PlayerWorldState world,
        PlayerResourceState resources,
        PlayerCombatState combat,
        PlayerBuffState buffs,
        PlayerLoadoutState loadout,
        PlayerSettingsState settings,
        PlayerDuelState duel,
        List<QueuedAction> actions,
        IPlayerStatsSink statsSink,
        IPlayerActionQueueMutationSink actionQueueSink)
        => new(playerId, accountId, displayName, createdAt, gameDataFingerprint,
            progression, world, resources, combat, buffs, loadout, settings, duel, actions)
        {
            StateVersion = stateVersion,
            Presence = presence,
            OfflineSinceAt = offlineSinceAt,
            LastSimulatedAt = lastSimulatedAt,
            _statsSink = statsSink,
            _actionQueueSink = actionQueueSink
        };

    /// <summary>
    /// 设置在线状态并维护离线锚点：转入在线清空锚点，由在线转为非在线以 at 记锚，
    /// 非在线状态之间切换保留原锚点；状态变更时标记 Settings 分段为脏。
    /// </summary>
    public void SetPresence(PresenceState presence, DateTimeOffset at)
    {
        if (!Enum.IsDefined(presence))
            throw new DomainException(DomainErrorCode.InvalidArgument, "玩家在线状态无效。");
        if (Presence == presence) return;
        var wasOnline = Presence == PresenceState.Online;
        Presence = presence;
        if (presence == PresenceState.Online) OfflineSinceAt = null;
        else if (wasOnline) OfflineSinceAt = at;
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
    /// 客户端回执确认后清理已完成的强化暂存记录，并标记 Progression 分段为脏。
    /// </summary>
    public int RemoveCompletedEnhancementRecords(IEnumerable<Guid> actionIds)
    {
        var count = Progression.RemoveCompletedEnhancementRecords(actionIds);
        if (count > 0)
        {
            MarkDirty(PlayerSectionKind.Progression);
        }
        return count;
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
    /// 更改本命形象外观标识。
    /// </summary>
    public void SetAvatar(string? avatar)
    {
        var normalized = NormalizeAvatar(avatar);
        if (!Settings.IsAvatarUnlocked(normalized))
            throw new DomainException(DomainErrorCode.InvalidTransition, "形象尚未解锁，无法佩戴。");
        if (string.Equals(Settings.Avatar, normalized, StringComparison.Ordinal)) return;
        Settings.Avatar = normalized;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 解锁指定形象并标记偏好设置分段为脏。
    /// </summary>
    public bool UnlockAvatar(string? avatar)
    {
        var normalized = NormalizeAvatar(avatar);
        if (Settings.UnlockAvatar(normalized))
        {
            Touch(PlayerSectionKind.Settings);
            return true;
        }
        return false;
    }

    /// <summary>规整化本命形象外观标识。</summary>
    public static string NormalizeAvatar(string? avatar)
    {
        if (string.IsNullOrWhiteSpace(avatar)) return "player";
        var trimmed = avatar.Trim().ToLowerInvariant();
        if (trimmed is "default" or "female" or "player-female") return "player";
        if (trimmed is "male") return "player-male";
        if (trimmed.Length <= 64 && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return trimmed;
        return "player";
    }

    /// <summary>
    /// 向行动队列追加一项行动意图。
    /// 业务约束：
    /// 1. RepeatCount 必须为正整数或 -1（无限重复）；
    /// 2. 无限重复行动的时长必须大于 0，防止单步零耗时导致模拟死循环；
    /// 3. 行动必须存在于配表目录中。
    /// </summary>
    public QueuedAction Enqueue(ActionIntent intent, DateTimeOffset now, ActionCatalog catalog)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.Enqueue,
            Intent: intent, At: now, Catalog: catalog)).Action!;

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
        bool incrementVersion = true,
        ActionParticipationBinding? participation = null)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.EnqueuePriority,
            Intent: intent, At: now, Catalog: catalog, Travel: travel, IncrementVersion: incrementVersion,
            Participation: participation)).Action!;

    /// <summary>
    /// 从队列中显式取消/移除指定行动实例。
    /// 若移除的是队首正在运行的行动，后续排队的挂起行动立即激活为运行中。
    /// </summary>
    public bool RemoveAction(Guid actionInstanceId, bool incrementVersion = true, double endedAtUnixSeconds = -1)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.Remove, actionInstanceId,
            IncrementVersion: incrementVersion, EndedAtUnixSeconds: endedAtUnixSeconds)).Changed;

    /// <summary>
    /// 调整队列中某项行动的执行次序（-1 向上移一位，1 向下移一位）。
    /// 调序后严格维护不变量：仅队首（Index 0）允许为运行中状态，其余项降为排队中。
    /// </summary>
    public bool MoveAction(Guid actionInstanceId, int direction)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.Move, actionInstanceId,
            Direction: direction)).Changed;

    /// <summary>
    /// 一键清空行动队列：全部实例经统一变更管线归档行动账本，队首远征死亡时恢复最低气血，
    /// 单次版本提交；斗法弃战判负由命令路径在调用前另行结算。
    /// </summary>
    public bool ClearActions()
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.Clear)).Changed;

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
    /// 设置组队讨伐的复活时机策略（总纲 §7.9.4.1）。非「指定队友存活」分支一律清空观察对象，
    /// 避免残留引用在日后切回该分支时指向已离队的旧目标。
    /// </summary>
    /// <param name="policy">复活时机策略。</param>
    /// <param name="targetPlayerId">指定队友存活策略的观察对象；其余分支忽略。</param>
    public void SetRespawnPolicy(CombatRespawnPolicy policy, Guid? targetPlayerId = null)
    {
        var target = policy == CombatRespawnPolicy.TargetAllyAlive ? targetPlayerId : null;
        if (target == Guid.Empty) target = null;
        if (Settings.RespawnPolicy == policy && Settings.RespawnTargetPlayerId == target) return;
        Settings.RespawnPolicy = policy;
        Settings.RespawnTargetPlayerId = target;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 复活时机策略的重置（总纲 §7.9.4.1）：把策略收敛回缺省的「直接复活」并清空观察对象。
    /// 该设置虽然是玩家自己的数据，但在两种队伍归属变化下必须重置——因为策略依赖的
    /// 「与谁同队」这一前提已经改变，沿用旧选择会让玩家按不再成立的意图倒地等待：
    /// 1. 本人离队/换队（含被踢、超时逐出、解散）；
    /// 2. 被指名的观察对象离队。
    /// 仅在当前策略非缺省时置脏，避免无意义的设置分段写回。
    /// </summary>
    public void ResetRespawnPolicy()
    {
        if (Settings.RespawnPolicy == CombatRespawnPolicy.Immediate && Settings.RespawnTargetPlayerId is null) return;
        Settings.RespawnPolicy = CombatRespawnPolicy.Immediate;
        Settings.RespawnTargetPlayerId = null;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 指定队友存活策略下的观察对象离队：策略重置为直接复活（§7.9.4.1）。
    /// 仅当本人策略正是「指定队友存活」且指名到该玩家时才生效——其他策略与无关成员离队一律不受影响。
    /// </summary>
    /// <param name="departedPlayerId">已离队的玩家标识。</param>
    public void ResetRespawnPolicyIfTarget(Guid departedPlayerId)
    {
        if (Settings.RespawnPolicy != CombatRespawnPolicy.TargetAllyAlive) return;
        if (Settings.RespawnTargetPlayerId != departedPlayerId) return;
        ResetRespawnPolicy();
    }

    /// <summary>
    /// 跳过新手引导：收敛到终态并把奖励游标推进到 Done——剩余未领赠送/终奖的补发
    /// 由 ECS 层 TutorialRules 在命令事务内统一结算；已达终态时幂等空转。
    /// </summary>
    public void SkipTutorial()
    {
        if (Settings.TutorialStep >= TutorialSteps.Done) return;
        Settings.TutorialStep = TutorialSteps.Done;
        Settings.TutorialRewardsStep = TutorialSteps.Done;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 重新学习引导：里程碑重置为起点，奖励游标保持不动——重走可再完成全部交互步骤，
    /// 已领过的奖励不再重复发放（由 TutorialRules.Advance 的游标判定兜底）。
    /// </summary>
    public void RestartTutorial()
    {
        Settings.TutorialStep = TutorialSteps.Gather;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 切换队列停泊开关。暂停时行动队列整体冻结（计时/相位/旅行/撮合不消费墙钟），
    /// 墙钟资源窗口照常结算；恢复后按原进度继续。
    /// </summary>
    public void SetQueuePaused(bool paused)
    {
        if (Settings.QueuePaused == paused) return;
        Settings.QueuePaused = paused;
        Touch(PlayerSectionKind.Settings);
    }

    /// <summary>
    /// 将指定行动实例轮转到队列末尾（用于队列无限循环调度或受阻行动换位）。
    /// 轮转后严格维护不变量：新的队首若为 Pending 则置为 Running，原行动若为 Running 则置为 Pending（Blocked 则保持 Blocked）。
    /// </summary>
    public bool RotateActionToTail(Guid actionInstanceId)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.Rotate, actionInstanceId)).Changed;

    /// <summary>已结算轮次后的队列裁决；负数重复不离队，循环保留原实例账本。</summary>
    public PlayerActionRoundDisposition FinishActionRound(Guid actionInstanceId, bool loopable, double endedAtUnixSeconds)
        => ApplyActionQueueMutation(new(PlayerActionQueueMutationKind.FinishRound, actionInstanceId,
            IncrementVersion: false, EndedAtUnixSeconds: endedAtUnixSeconds, Loopable: loopable)).RoundDisposition;

    private PlayerActionQueueMutationResult ApplyActionQueueMutation(in PlayerActionQueueMutation mutation)
        => _actionQueueSink is { } sink
            ? sink.Dispatch(this, _actions, mutation)
            : PlayerActionQueueRules.DispatchInline(_actionQueueWork ??= new(), this, _actions, mutation,
                DuelSettingsTuning.MaxBattleReports);

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
    /// 若传入 actions 行动目录，将自动升级队列中低于当前境界最高可用档位的修炼行动。
    /// </summary>
    public void Breakthrough(Domain.GameData.RealmProgressionCatalog? realm, ActionCatalog? actions = null)
    {
        if (Progression.RealmLevel < 1)
            throw new DomainException(DomainErrorCode.InvariantViolation, "当前境界状态无效。");
        if (realm is not null)
        {
            // 灵根破镜乘区（总纲 §7.7.5.1）：入天门后每多一条有效灵根，本层修为瓶颈 +50%，
            // 未入天门恒 1.0——突破前置本来就要求先开天门，此处对叩天门层与后续层统一按同一口径放大。
            var linggenMultiplier = HeavenGateRules.ResolveCultivationMultiplier(
                Progression.HeavenGate.EffectiveValues, Progression.HeavenGate.Entered);
            var cap = realm.ExpCapFor(Progression.RealmLevel) * linggenMultiplier;
            if (cap <= 0)
                throw new DomainException(DomainErrorCode.Conflict, "已至最高境界，无可突破。");
            if (Progression.Experience < cap - 1e-9)
                throw new DomainException(DomainErrorCode.Conflict, "修为未满，尚未触及瓶颈。");
            // 叩天门满修为后必须先开天门取得灵根：未入天门时阻断突破，
            // 保证灵根是进入练气期的必经前置而非可跳过流程。
            if (HeavenGateRules.IsHeavenGateRealm(Progression.RealmLevel) && !Progression.HeavenGate.Entered)
                throw new DomainException(DomainErrorCode.Conflict, "需先叩开天门取得灵根，方可继续突破。");
        }
        var realmBefore = Progression.RealmLevel;
        Progression.RealmLevel = checked(Progression.RealmLevel + 1);
        // 突破只重置本层计量：Experience 清零；Foundation 底蕴池与 CombatExperience 对抗池按设计保留不清空。
        Progression.Experience = 0;
        // 突破升级计入行动统计账本（realm 级 LevelUp，TargetId 恒为空串）。
        RecordStats(_actions.Count > 0 ? _actions[0].Id : (Guid?)null,
            LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d,
            new PlayerStatsDelta { LevelUp = new PlayerReportLevelUp("realm", "", realmBefore, Progression.RealmLevel) });
        if (actions is not null)
        {
            TryUpgradeCultivationActions(actions);
        }
        Touch(PlayerSectionKind.Progression);
    }

    /// <summary>
    /// 检测当前行动队列中的修炼类型行动，若当前境界已解锁更高阶的修炼行动，则平滑升级至最高可用等级。
    /// </summary>
    public bool TryUpgradeCultivationActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (_actions.Count == 0) return false;
        var highestCultivation = ResolveHighestAvailableCultivation(catalog, Progression.RealmLevel);
        if (highestCultivation is null) return false;

        var changed = false;
        for (var i = 0; i < _actions.Count; i++)
        {
            var action = _actions[i];
            if (!catalog.TryGet(action.Intent.ActionId, out var currentDef) || currentDef.Kind != ActionKind.Cultivation)
                continue;
            var currentLevel = currentDef.Level ?? 1;
            var targetLevel = highestCultivation.Level ?? 1;
            if (targetLevel > currentLevel && action.Intent.ActionId != highestCultivation.Id)
            {
                action.Intent = action.Intent with { ActionId = highestCultivation.Id };
                ActionReportBook.EnsureActionReport(this, action.Id, highestCultivation.Id, highestCultivation.Name, LastSimulatedAt);
                changed = true;
            }
        }
        if (changed)
        {
            Touch(PlayerSectionKind.ActionQueue);
        }
        return changed;
    }

    /// <summary>
    /// 根据境界等级在行动目录中查找可用的最高阶修炼行动。
    /// </summary>
    public static ActionDefinition? ResolveHighestAvailableCultivation(ActionCatalog catalog, int realmLevel)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ActionDefinition? best = null;
        var bestLevel = -1;
        foreach (var def in catalog.All)
        {
            if (def.Kind != ActionKind.Cultivation) continue;
            var reqLevel = def.Level ?? 1;
            if (realmLevel >= reqLevel && reqLevel > bestLevel)
            {
                best = def;
                bestLevel = reqLevel;
            }
        }
        return best;
    }

    /// <summary>
    /// 解析修炼行动的灵石注灵消耗系数（一级凡俗 1x，二级炼气 2x）。
    /// </summary>
    public static int ResolveCultivationSpiritStoneScale(ActionDefinition? definition)
        => (definition?.Level is { } lvl && lvl >= 18) ? 2 : 1;

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
        actionType = actionType?.Trim().ToLowerInvariant() ?? string.Empty;
        itemId = itemId?.Trim();
        group = string.IsNullOrWhiteSpace(group) ? AutoUseSlotState.SlotGroupGeneral : group.Trim();
        if (!Domain.GameData.AutoUseBar.IsKnown(actionType)
            || slotIndex is < 0 || slotIndex >= Domain.GameData.AutoUseBar.SlotCountFor(actionType)
            || group is not (AutoUseSlotState.SlotGroupGeneral or AutoUseSlotState.SlotGroupPotion)
            || (group == AutoUseSlotState.SlotGroupPotion && actionType != Domain.GameData.AutoUseBar.Combat)
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
