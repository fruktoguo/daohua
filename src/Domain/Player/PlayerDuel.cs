using System.Text.Json;
using Domain.Combat;
using Domain.GameData;

namespace Domain.Player;

/// <summary>
/// 暂存区单条物品批次：斗法期间战斗掉落先入暂存，按 stagedBucketSeconds 档位桶合并，
/// AcquiredAtUnixSeconds 为桶尾时刻（结算时刻 = 桶尾 + stagedSettleSeconds），到期或斗法结算后归属才确定。
/// </summary>
public sealed record DuelStagedItem(string ItemId, double Quantity, double AcquiredAtUnixSeconds)
{
    /// <summary>校验暂存记录不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && double.IsFinite(Quantity) && Quantity > 0
        && double.IsFinite(AcquiredAtUnixSeconds) && AcquiredAtUnixSeconds >= 0;
}

/// <summary>战报中的物品收益条目。</summary>
public sealed record PlayerReportItem(string ItemId, double Quantity);

/// <summary>战报中的功法参悟经验条目。</summary>
public sealed record PlayerReportAbilityExp(string AbilityId, double Experience);

/// <summary>战报中的技艺修行经验条目。</summary>
public sealed record PlayerReportTechniqueExp(string TechniqueId, double Experience);

/// <summary>
/// 战报中的等级提升记录。
/// Kind ∈ realm/root/ability/technique；root 的 TargetId 用 CoreAspectKind 枚举名小写（如 stamina），
/// ability 用功法 ID，technique 用技艺 ID，realm 用空串。
/// </summary>
public sealed record PlayerReportLevelUp(string Kind, string TargetId, double FromLevel, double ToLevel);

/// <summary>六维经验增量容器；战报与收益分配共用。</summary>
public sealed class PlayerRootExpValues
{
    /// <summary>体魄经验增量</summary>
    public double Stamina { get; set; }
    /// <summary>经脉经验增量</summary>
    public double Meridians { get; set; }
    /// <summary>力道经验增量</summary>
    public double Attack { get; set; }
    /// <summary>根骨经验增量</summary>
    public double Bone { get; set; }
    /// <summary>身法经验增量</summary>
    public double Agility { get; set; }
    /// <summary>神识经验增量</summary>
    public double Consciousness { get; set; }
    /// <summary>六维经验合计</summary>
    public double Total => Stamina + Meridians + Attack + Bone + Agility + Consciousness;
    /// <summary>清空六维经验累计（每日账本 rollover 用）。</summary>
    public void Clear()
    {
        Stamina = 0; Meridians = 0; Attack = 0;
        Bone = 0; Agility = 0; Consciousness = 0;
    }

    /// <summary>按根骨维度累加经验。</summary>
    public void Add(CoreAspectKind attribute, double amount)
    {
        switch (attribute)
        {
            case CoreAspectKind.Stamina: Stamina += amount; break;
            case CoreAspectKind.Meridians: Meridians += amount; break;
            case CoreAspectKind.Attack: Attack += amount; break;
            case CoreAspectKind.Bone: Bone += amount; break;
            case CoreAspectKind.Agility: Agility += amount; break;
            default: Consciousness += amount; break;
        }
    }
    /// <summary>拷贝一份独立副本。</summary>
    public PlayerRootExpValues Clone() => new()
    {
        Stamina = Stamina, Meridians = Meridians, Attack = Attack,
        Bone = Bone, Agility = Agility, Consciousness = Consciousness
    };
}

/// <summary>
/// 行动统计账本：一个行动实例从建账到出队归档期间的全部收益与计数。
/// 同一结构同时承担三类账本——行动实例账本、离线挂机账本与每日统计账本。
/// </summary>
public sealed class PlayerBattleReport
{
    /// <summary>报告标识（行动实例账本为实例 Id 的 N 格式串；单例账本固定为 offline/daily）</summary>
    public string ReportId { get; set; } = "";
    /// <summary>绑定的行动实例 Id；离线/每日账本为 Guid.Empty</summary>
    public Guid ActionInstanceId { get; set; }
    /// <summary>行动配表 ID；离线/每日账本为空串</summary>
    public string ActionId { get; set; } = "";
    /// <summary>行动名称快照；离线/每日账本为空串</summary>
    public string ActionName { get; set; } = "";
    /// <summary>统计开始时间（Unix 秒）</summary>
    public double StartedAtUnixSeconds { get; set; }
    /// <summary>统计结束时间（Unix 秒）</summary>
    public double EndedAtUnixSeconds { get; set; }
    /// <summary>修为收益（瓶颈截断与底蕴兑换放大后的本层实际入账量）</summary>
    public double Experience { get; set; }
    /// <summary>底蕴净变动（满层溢出沉淀为正、兑换放大消耗为负）</summary>
    public double Foundation { get; set; }
    /// <summary>战斗经验收益（修为类收益按增幅前基础量等额记账，不受加成）</summary>
    public double CombatExperience { get; set; }
    /// <summary>六维经验收益</summary>
    public PlayerRootExpValues RootExp { get; } = new();
    /// <summary>功法参悟经验收益（按功法 ID 聚合）</summary>
    public List<PlayerReportAbilityExp> AbilityExp { get; } = [];
    /// <summary>技艺修行经验收益（按技艺 ID 聚合）</summary>
    public List<PlayerReportTechniqueExp> TechniqueExp { get; } = [];
    /// <summary>等级提升记录（境界/六维/功法）</summary>
    public List<PlayerReportLevelUp> LevelUps { get; } = [];
    /// <summary>物品收益（含暂存区与缴获）</summary>
    public List<PlayerReportItem> Items { get; } = [];
    /// <summary>物品失去（行动输入消耗、自动消耗品、斗法暂存被缴）</summary>
    public List<PlayerReportItem> ItemsLost { get; } = [];
    /// <summary>已完成行动轮次</summary>
    public double Cycles { get; set; }
    /// <summary>战斗遭遇场次</summary>
    public double Encounters { get; set; }
    /// <summary>普通妖兽击杀数</summary>
    public double NormalKills { get; set; }
    /// <summary>精英妖兽击杀数</summary>
    public double EliteKills { get; set; }
    /// <summary>首领妖兽击杀数</summary>
    public double BossKills { get; set; }
    /// <summary>战斗落败/死亡次数</summary>
    public double Deaths { get; set; }
    /// <summary>本周期斗法胜场</summary>
    public double DuelWins { get; set; }
    /// <summary>本周期斗法败场（含主动结束判负）</summary>
    public double DuelLosses { get; set; }
    /// <summary>UTC+8 自然日序号（仅每日账本使用，-1 表示尚未归属任何自然日）</summary>
    public long DayIndex { get; set; } = -1;

    /// <summary>是否存在任何可记录内容；空账本归档时丢弃。</summary>
    public bool HasContent => Experience > 0 || Foundation != 0 || CombatExperience > 0
        || RootExp.Total > 0
        || AbilityExp.Count > 0 || TechniqueExp.Count > 0 || LevelUps.Count > 0
        || Items.Count > 0 || ItemsLost.Count > 0
        || Cycles > 0 || Encounters > 0
        || NormalKills > 0 || EliteKills > 0 || BossKills > 0 || Deaths > 0
        || DuelWins > 0 || DuelLosses > 0;

    /// <summary>
    /// 导出脏标记：账本字段只允许经 RecordStats/归档/重置/建账等聚合记账路径写入，
    /// 写入路径负责置位本标记；斗法分段导出凭此仅重建被写账本的文档，干净账本直接复用。
    /// </summary>
    internal bool ExportDirty { get; private set; } = true;
    /// <summary>上次导出的文档；ExportDirty 为 false 时可按位级一致语义直接复用。</summary>
    internal PlayerBattleReportDocument? ExportedDocument { get; private set; }
    /// <summary>记账路径写入后调用：标记导出文档过期。</summary>
    internal void MarkExportDirty() => ExportDirty = true;
    /// <summary>登记导出文档并清除脏标记。</summary>
    internal PlayerBattleReportDocument CacheExported(PlayerBattleReportDocument document)
    {
        ExportedDocument = document;
        ExportDirty = false;
        return document;
    }

    /// <summary>合并记录一件物品收益。</summary>
    public void AddItem(ItemId itemId, double quantity)
    {
        var index = Items.FindIndex(entry => entry.ItemId == itemId.Value);
        if (index >= 0) Items[index] = Items[index] with { Quantity = Items[index].Quantity + quantity };
        else Items.Add(new(itemId.Value, quantity));
    }

    /// <summary>合并记录一件物品失去。</summary>
    public void AddItemLost(ItemId itemId, double quantity)
    {
        var index = ItemsLost.FindIndex(entry => entry.ItemId == itemId.Value);
        if (index >= 0) ItemsLost[index] = ItemsLost[index] with { Quantity = ItemsLost[index].Quantity + quantity };
        else ItemsLost.Add(new(itemId.Value, quantity));
    }

    /// <summary>合并记录功法参悟经验。</summary>
    public void AddAbilityExp(string abilityId, double experience)
    {
        var index = AbilityExp.FindIndex(entry => entry.AbilityId == abilityId);
        if (index >= 0) AbilityExp[index] = AbilityExp[index] with { Experience = AbilityExp[index].Experience + experience };
        else AbilityExp.Add(new(abilityId, experience));
    }

    /// <summary>合并记录技艺修行经验。</summary>
    public void AddTechniqueExp(string techniqueId, double experience)
    {
        var index = TechniqueExp.FindIndex(entry => entry.TechniqueId == techniqueId);
        if (index >= 0) TechniqueExp[index] = TechniqueExp[index] with { Experience = TechniqueExp[index].Experience + experience };
        else TechniqueExp.Add(new(techniqueId, experience));
    }

    /// <summary>清空全部累计字段（每日账本跨日 rollover 用）；ReportId/行动标识保留。</summary>
    public void ResetBucket(long dayIndex, double atUnixSeconds)
    {
        Experience = 0;
        Foundation = 0;
        CombatExperience = 0;
        RootExp.Clear();
        AbilityExp.Clear();
        TechniqueExp.Clear();
        LevelUps.Clear();
        Items.Clear();
        ItemsLost.Clear();
        Cycles = 0;
        Encounters = 0;
        NormalKills = 0;
        EliteKills = 0;
        BossKills = 0;
        Deaths = 0;
        DuelWins = 0;
        DuelLosses = 0;
        DayIndex = dayIndex;
        StartedAtUnixSeconds = atUnixSeconds;
        EndedAtUnixSeconds = atUnixSeconds;
    }
}

/// <summary>
/// 斗法玩法分段状态：遭遇计数、失败冷却、进行中斗法、暂存区与行动统计三账本
/// （行动实例账本、离线挂机账本、每日统计账本）及归档历史。
/// </summary>
public sealed class PlayerDuelState
{
    /// <summary>所属聚合的行动队列视图；供 CurrentReport 定位队首行动账本。</summary>
    private IReadOnlyList<QueuedAction>? _queue;
    private double _encounterCharges;
    private double _cooldownUntilUnixSeconds;
    private string? _activeDuelId;
    private PlayerBattleReport _offlineReport = new() { ReportId = "offline" };
    private PlayerBattleReport _dailyReport = new() { ReportId = "daily" };

    /// <summary>斗法遭遇计数；大于 0 时下次进入战斗尝试撮合。</summary>
    public double EncounterCharges
    {
        get => _encounterCharges;
        set
        {
            if (_encounterCharges == value) return;
            _encounterCharges = value;
            NoteStructureMutation();
        }
    }
    /// <summary>失败冷却截止时间（Unix 秒）；冷却内不匹配也不被匹配。</summary>
    public double CooldownUntilUnixSeconds
    {
        get => _cooldownUntilUnixSeconds;
        set
        {
            if (_cooldownUntilUnixSeconds == value) return;
            _cooldownUntilUnixSeconds = value;
            NoteStructureMutation();
        }
    }
    /// <summary>进行中斗法标识；非空时暂存区冻结入账。</summary>
    public string? ActiveDuelId
    {
        get => _activeDuelId;
        set
        {
            if (_activeDuelId == value) return;
            _activeDuelId = value;
            NoteStructureMutation();
        }
    }
    /// <summary>暂存区物品批（按结算时刻升序、同物品同桶合并）；写入递增版本号供导出缓存失效检测。</summary>
    public VersionedList<DuelStagedItem> StagedItems { get; } = new();
    /// <summary>按行动实例 Id 各持一本的收益账本；实例出队时归档进 Reports；写入递增版本号。</summary>
    public VersionedDictionary<Guid, PlayerBattleReport> ActionReports { get; } = new();
    /// <summary>离线挂机统计账本：Presence 转入 Offline 时重置，离线补算期间全部收益入账。</summary>
    public PlayerBattleReport OfflineReport
    {
        get => _offlineReport;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_offlineReport, value)) return;
            _offlineReport = value;
            NoteStructureMutation();
        }
    }
    /// <summary>每日统计账本：按 UTC+8 自然日（DayIndex）自动 rollover。</summary>
    public PlayerBattleReport DailyReport
    {
        get => _dailyReport;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_dailyReport, value)) return;
            _dailyReport = value;
            NoteStructureMutation();
        }
    }
    /// <summary>最近战报归档（最新在前，最多 MaxBattleReports 条）；写入递增版本号。</summary>
    public VersionedList<PlayerBattleReport> Reports { get; } = new();

    /// <summary>标量与账本对象替换的结构修订号（遭遇计数/冷却/进行中斗法/单例账本换引用）。</summary>
    internal long StructureVersion { get; private set; }
    private void NoteStructureMutation() => StructureVersion = checked(StructureVersion + 1);

    /// <summary>
    /// 斗法文档导出修订号：结构修订号 + 各集合版本号合成。
    /// 账本字段内容写入不计入本修订号（由 PlayerBattleReport.ExportDirty 单独追踪）。
    /// </summary>
    internal long ExportRevision => StructureVersion + StagedItems.Version + Reports.Version + ActionReports.Version;

    /// <summary>分段回滚检查点专用：把结构与各集合修订号写回备份值；调用方须先把内容恢复到与版本一致的状态。</summary>
    internal void RestoreRevisionCounters(long structureVersion, long stagedItemsVersion, long reportsVersion, long actionReportsVersion)
    {
        StructureVersion = structureVersion;
        StagedItems.RestoreVersion(stagedItemsVersion);
        Reports.RestoreVersion(reportsVersion);
        ActionReports.RestoreVersion(actionReportsVersion);
    }

    /// <summary>队首行动的收益账本（便捷访问）；队首无账本或空队列时为 null。</summary>
    public PlayerBattleReport? CurrentReport
        => _queue is { Count: > 0 } queue ? ReportFor(queue[0].Id) : null;

    /// <summary>绑定所属聚合的行动队列视图；由聚合构造时一次性挂接。</summary>
    internal void BindQueue(IReadOnlyList<QueuedAction> queue) => _queue = queue;

    /// <summary>查找指定行动实例的收益账本；不存在返回 null。</summary>
    public PlayerBattleReport? ReportFor(Guid actionInstanceId)
        => ActionReports.TryGetValue(actionInstanceId, out var report) ? report : null;

    /// <summary>获取或新建指定行动实例的收益账本；新建时 StartedAt=EndedAt=入账时刻。</summary>
    public PlayerBattleReport GetOrCreateReport(Guid instanceId, string actionId, string actionName, double atUnixSeconds)
    {
        if (ActionReports.TryGetValue(instanceId, out var existing)) return existing;
        var report = new PlayerBattleReport
        {
            ReportId = instanceId.ToString("N"),
            ActionInstanceId = instanceId,
            ActionId = actionId,
            ActionName = actionName,
            StartedAtUnixSeconds = atUnixSeconds,
            EndedAtUnixSeconds = atUnixSeconds
        };
        ActionReports[instanceId] = report;
        return report;
    }

    /// <summary>离线账本重置：进入离线会话时清空并重新起算。</summary>
    public void ResetOfflineReport(double atUnixSeconds)
    {
        OfflineReport.ResetBucket(0, atUnixSeconds);
        OfflineReport.MarkExportDirty();
    }
}

/// <summary>斗法分段的可持久化文档形态。</summary>
public sealed record PlayerDuelStateDocument(
    double EncounterCharges,
    double CooldownUntilUnixSeconds,
    string? ActiveDuelId,
    IReadOnlyList<DuelStagedItem> StagedItems,
    PlayerBattleReportDocument? CurrentReport,
    IReadOnlyList<PlayerBattleReportDocument> Reports,
    IReadOnlyList<PlayerBattleReportDocument>? ActiveReports = null,
    PlayerBattleReportDocument? OfflineReport = null,
    PlayerBattleReportDocument? DailyReport = null);

/// <summary>战报的可持久化文档形态（不含内部 ActionInstanceId，由 ReportId 承载）。</summary>
public sealed record PlayerBattleReportDocument(
    string ReportId,
    string ActionId,
    string ActionName,
    double StartedAtUnixSeconds,
    double EndedAtUnixSeconds,
    double Experience,
    PlayerRootExpDocument RootExp,
    IReadOnlyList<PlayerReportItem> Items,
    double DuelWins,
    double DuelLosses,
    IReadOnlyList<PlayerReportItem>? ItemsLost = null,
    IReadOnlyList<PlayerReportAbilityExp>? AbilityExp = null,
    IReadOnlyList<PlayerReportTechniqueExp>? TechniqueExp = null,
    IReadOnlyList<PlayerReportLevelUp>? LevelUps = null,
    double Cycles = 0,
    double Encounters = 0,
    double NormalKills = 0,
    double EliteKills = 0,
    double BossKills = 0,
    double Deaths = 0,
    long DayIndex = -1,
    double Foundation = 0,
    double CombatExperience = 0);

/// <summary>六维经验文档形态。</summary>
public sealed record PlayerRootExpDocument(
    double Stamina, double Meridians, double Attack,
    double Bone, double Agility, double Consciousness);

/// <summary>玩家聚合的斗法玩法分部：匹配预约、回放挂载、暂存区与战报结算。</summary>
public sealed partial class PlayerAggregate
{
    /// <summary>斗法玩法分段状态（遭遇计数、失败冷却、暂存区与战报）。</summary>
    public PlayerDuelState Duel { get; }

    /// <summary>当前斗法实力值：六维等级总和（后续可替换为更复杂口径）。</summary>
    public double DuelPower => Loadout.CoreAttributes.TotalLevel;

    /// <summary>当前战斗行动的斗法开关（仅队首战斗行动生效）。</summary>
    public bool IsDuelSeeking => _actions.Count > 0 && _actions[0].Intent.PvpEnabled
        && _actions[0].Expedition is not null
        && Duel.CooldownUntilUnixSeconds <= LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d
        && Duel.ActiveDuelId is null;

    /// <summary>
    /// 队首行动是否处于斗法匹配等待相位（撮合运行时据此发起匹配）。
    /// </summary>
    public bool IsDuelPending => _actions.Count > 0
        && _actions[0].Expedition?.Phase == CombatExpeditionPhase.DuelPending;

    /// <summary>
    /// 斗法入场检查：当前是否满足「可被匹配/发起匹配」的全部前置条件。
    /// 要求队首为勾选斗法的战斗远征、遭遇计数大于 0、不在冷却且无进行中斗法。
    /// </summary>
    public bool CanJoinDuel(double nowUnixSeconds)
        => _actions.Count > 0
            && _actions[0].Intent.PvpEnabled
            && _actions[0].Expedition is { } expedition
            && expedition.Phase is not CombatExpeditionPhase.Duel
            && Duel.EncounterCharges > 0
            && Duel.CooldownUntilUnixSeconds <= nowUnixSeconds
            && Duel.ActiveDuelId is null;

    /// <summary>
    /// 预约一场斗法：校验资格并锁定 ActiveDuelId、消耗一点遭遇计数。
    /// 由撮合运行时在单写者上下文调用；失败返回 false 且不产生任何状态变更。
    /// </summary>
    public bool TryReserveDuel(Guid duelId, double nowUnixSeconds)
    {
        if (!CanJoinDuel(nowUnixSeconds)) return false;
        Duel.EncounterCharges -= 1;
        Duel.ActiveDuelId = duelId.ToString("N");
        MarkDirty(PlayerSectionKind.Duel);
        return true;
    }

    /// <summary>解除斗法预约（撮合中断回滚）：归还计数并清空进行中标。</summary>
    public void ReleaseDuelReservation(Guid duelId)
    {
        if (Duel.ActiveDuelId != duelId.ToString("N")) return;
        Duel.ActiveDuelId = null;
        Duel.EncounterCharges += 1;
        MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>
    /// 挂载斗法回放：发起方从 DuelPending、应战方从任意战斗远征相位进入斗法。
    /// 双方气血灵力重置为满，当前波次怪物被斗法打断（应战方搜寻进度保留到赛后）。
    /// </summary>
    public void AttachDuel(DuelPlaybackState playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        if (!playback.IsValid || _actions.Count == 0 || _actions[0].Expedition is not { } expedition
            || !_actions[0].Intent.PvpEnabled)
            throw new DomainException(DomainErrorCode.InvariantViolation, "斗法挂载时远征状态无效。");
        if (Duel.ActiveDuelId != playback.DuelId)
            throw new DomainException(DomainErrorCode.InvariantViolation, "斗法挂载与预约标识不一致。");
        var resources = Resources;
        resources.Hitpoints = resources.MaxHitpoints;
        resources.Manapoints = resources.MaxManapoints;
        Combat.Skills = PlayerCombatSkillState.Empty;
        _actions[0].Expedition = expedition with
        {
            Phase = CombatExpeditionPhase.Duel,
            PhaseElapsedSeconds = 0,
            PhaseDurationSeconds = 0,
            BattleElapsedSeconds = 0,
            PlayerHitpoints = Resources.Hitpoints,
            PlayerManapoints = Resources.Manapoints,
            PlayerNextAttackAtSeconds = 0,
            PlayerAttackCount = 0,
            PlayerThreat = 0,
            Enemies = null,
            Duel = playback,
            DuelSeekFailed = false
        };
        _actions[0].ProgressSeconds = 0;
        MarkDirty(PlayerSectionKind.ActionQueue);
        MarkDirty(PlayerSectionKind.Combat);
    }

    /// <summary>
    /// 撤销斗法挂接：对手侧挂载失败时的撮合回滚——退回已扣计数、清空进行中标，
    /// 并把队首远征恢复为「搜寻已完成」的正常波次入口（本波不再撮合）。
    /// </summary>
    public void DetachDuel(Guid duelId)
    {
        if (Duel.ActiveDuelId != duelId.ToString("N")) return;
        Duel.ActiveDuelId = null;
        Duel.EncounterCharges += 1;
        if (_actions.Count > 0 && _actions[0].Expedition is { Phase: CombatExpeditionPhase.Duel, Duel: { } playback } expedition
            && playback.DuelId == duelId.ToString("N"))
        {
            _actions[0].Expedition = expedition with
            {
                Duel = null,
                DuelSeekFailed = true,
                Phase = CombatExpeditionPhase.Search,
                PhaseElapsedSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
                PhaseDurationSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9)
            };
        }
        MarkDirty(PlayerSectionKind.Duel);
        MarkDirty(PlayerSectionKind.ActionQueue);
    }

    /// <summary>
    /// 取消斗法匹配等待：恢复为「搜寻已完成」的正常波次入口，并标记本波不再撮合。
    /// </summary>
    public void AbortDuelSeek()
    {
        if (_actions.Count == 0 || _actions[0].Expedition is not { Phase: CombatExpeditionPhase.DuelPending } expedition)
            return;
        _actions[0].Expedition = expedition with
        {
            Phase = CombatExpeditionPhase.Search,
            PhaseElapsedSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
            PhaseDurationSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
            DuelSeekFailed = true
        };
        MarkDirty(PlayerSectionKind.ActionQueue);
    }

    /// <summary>
    /// 玩家主动结束斗法行动：立即判负——清空暂存区快照物品、进入失败冷却、归档败绩，
    /// 并把本地回放锁定为已裁决败北（行动即使仅被挪下队首也不再按剧本翻盘）。
    /// 由行动移除/调序/移动路径在单写者上下文调用；返回斗法与对手 Id 供运行时通知对方判胜。
    /// </summary>
    public (Guid DuelId, Guid PeerId)? ApplyDuelForfeit(DateTimeOffset now, DuelSettings settings)
    {
        var expedition = _actions.Count > 0 ? _actions[0].Expedition : null;
        var duel = expedition?.Duel;
        if (duel is null || duel.Resolved || Duel.ActiveDuelId != duel.DuelId
            || !Guid.TryParse(duel.DuelId, out var duelId))
            return null;
        var nowUnix = now.ToUnixTimeMilliseconds() / 1000d;
        RemoveStagedItems(duel.SelfStagedSnapshot, nowUnix);
        Duel.ActiveDuelId = null;
        Duel.CooldownUntilUnixSeconds = nowUnix + settings.DefeatCooldownSeconds;
        RecordStats(_actions.Count > 0 ? _actions[0].Id : (Guid?)null, nowUnix,
            new PlayerStatsDelta { DuelLosses = 1, Deaths = 1 });
        Progression.TotalDefeats += 1;
        _actions[0].Expedition = expedition! with
        {
            Duel = duel with { Resolved = true, Forfeited = true, ViewerOutcome = CombatOutcome.EnemyWon }
        };
        MarkDirty(PlayerSectionKind.Duel);
        MarkDirty(PlayerSectionKind.Progression);
        MarkDirty(PlayerSectionKind.ActionQueue);
        return (duelId, Guid.TryParse(duel.OpponentPlayerId, out var peer) ? peer : Guid.Empty);
    }

    /// <summary>收到对手弃战通知：本地回放直接标记为已裁决胜利，下次推进结算缴获。</summary>
    public bool ApplyPeerForfeit(Guid duelId)
    {
        if (Duel.ActiveDuelId != duelId.ToString("N")) return false;
        var expedition = _actions.Count > 0 ? _actions[0].Expedition : null;
        if (expedition?.Duel is not { } duel || duel.Resolved) return false;
        _actions[0].Expedition = expedition with
        {
            Duel = duel with { Resolved = true, Forfeited = true, ViewerOutcome = CombatOutcome.PlayerWon }
        };
        // Touch 推进 StateVersion：对手弃战判胜由 RouteDuelForfeitAsync 主动推送，
        // 仅标脏不推版本会得到同版本快照，被客户端版本门禁丢弃后判胜永不显示。
        Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>斗法期间获得物品：进入暂存区而非直接入账；入账时刻按当前调参桶长量化。</summary>
    public void StageItem(ItemId itemId, double quantity, double acquiredAtUnixSeconds)
        => ActionReportBook.StageCombatItem(this, itemId, quantity, acquiredAtUnixSeconds, DuelSettingsTuning.StagedBucketSeconds);

    /// <summary>按快照清单移除暂存物品（斗法落败被对方缴获），被缴物品计入「失去」。</summary>
    internal void RemoveStagedItems(IReadOnlyList<DuelStagedItem> snapshot, double atUnixSeconds)
        => ActionReportBook.RemoveStagedCombatItems(this, snapshot, atUnixSeconds);

    /// <summary>
    /// 行动统计记账枢纽：同一份已结算增量依次作用于
    /// ①指定行动实例账本（存在才记，不隐式建账）②离线账本（仅 Presence==Offline 时）
    /// ③每日账本（先按 UTC+8 自然日 rollover 再入账）。任一账本被写入即标记 Duel 脏。
    /// </summary>
    internal void RecordStats(Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta)
    {
        if (_statsSink is { } sink) sink.Record(this, actionInstanceId, atUnixSeconds, delta);
        else ActionReportBook.RecordActionStats(this, actionInstanceId, atUnixSeconds, delta);
    }

    /// <summary>队首行动开始推进时按实例建账（已存在则续用）；仅新建时标脏。</summary>
    internal void EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => ActionReportBook.EnsureActionReport(this, instanceId, actionId, actionName, now);

    /// <summary>行动实例出队时归档其账本；空账本直接丢弃，非空账本插入 Reports 头部并裁剪至上限。</summary>
    internal void ArchiveReportForAction(Guid instanceId, double endedAtUnixSeconds)
        => ActionReportBook.ArchiveActionReport(this, instanceId, endedAtUnixSeconds, DuelSettingsTuning.MaxBattleReports);

    /// <summary>行动实例出队时归档其账本（DateTimeOffset 便捷重载）。</summary>
    internal void ArchiveReportForAction(Guid instanceId, DateTimeOffset endedAt)
        => ArchiveReportForAction(instanceId, endedAt.ToUnixTimeMilliseconds() / 1000d);

    /// <summary>
    /// 构建斗法镜像快照：开战瞬间冻结属性面板、功法技能槽与暂存物品清单。
    /// 镜像技能使用槽位配置的触发条件（缺省回退配表 defaultTriggers），可重复施放。
    /// </summary>
    public DuelCombatant BuildDuelMirror(GameDataCatalog gameData)
    {
        // 战斗经验随冻结面板进入镜像：撮合后回放期不再二次解析（总纲 §7.7.6）。
        var stats = PlayerAttributeResolver.Resolve(this, gameData).ToCombatant(Progression.CombatExperience);
        // 斗法镜像与玩家/怪物共用同一套通用技能槽：触发条件与装配口径完全一致（按功法 ID 挂接）。
        var skills = SkillSlot.FromAbilitySlots(Loadout.AbilitySlots, gameData.Abilities, Loadout.AbilityTriggers);
        return new DuelCombatant(PlayerId, DisplayName, DuelPower, stats, skills, Duel.StagedItems.ToArray());
    }

    /// <summary>斗法调参当前生效值；由模拟推进在配表上下文中刷新。</summary>
    internal DuelSettings DuelSettingsTuning { get; set; } = DuelSettings.Default;
}
