using System.Text.Json;
using Domain.Player;

namespace Domain.Combat;

/// <summary>
/// 斗法（PVP）全局调参：全部数值来自 config/game-data/duel.json（id = duel.settings），
/// 配表缺失时使用内建默认值，禁止在结算代码中散落魔法数。
/// </summary>
/// <param name="EncounterChancePerBattle">每次进入战斗时斗法遭遇计数 +1 的概率（默认 0.005 ≈ 每 2 小时一次）</param>
/// <param name="PowerBandRatio">实力匹配上下浮动范围（0.3 = ±30%）</param>
/// <param name="DefeatCooldownSeconds">斗法失败冷却秒数（3600 = 一小时）</param>
/// <param name="StagedSettleSeconds">暂存区物品入账延迟秒数（1200 = 20 分钟）</param>
/// <param name="StagedBucketSeconds">暂存批次量化桶长（300 = 5 分钟）：入账时刻向上取整到桶尾，同物品同桶合并</param>
/// <param name="DropChanceBonusPercent">斗法开启时物品爆率加成百分比点数（20 = +20% 乘区）</param>
/// <param name="MaxDuelSeconds">单场斗法剧本最长结算秒数，超时判平</param>
/// <param name="MaxBattleReports">战斗收益记录最大保存条数（10）</param>
public sealed record DuelSettings(
    double EncounterChancePerBattle = 0.005,
    double PowerBandRatio = 0.3,
    double DefeatCooldownSeconds = 3600,
    double StagedSettleSeconds = 1200,
    double StagedBucketSeconds = 300,
    double DropChanceBonusPercent = 20,
    double MaxDuelSeconds = 300,
    int MaxBattleReports = 10)
{
    /// <summary>配表未提供斗法设置时使用的内建默认。</summary>
    public static DuelSettings Default { get; } = new();

    /// <summary>从 JSON 对象解析斗法调参；字段缺失回退默认值。</summary>
    public static DuelSettings Parse(JsonElement element)
    {
        var settings = new DuelSettings(
            JsonRead.Number(element, "encounterChancePerBattle", 0.005),
            JsonRead.Number(element, "powerBandRatio", 0.3),
            JsonRead.Number(element, "defeatCooldownSeconds", 3600),
            JsonRead.Number(element, "stagedSettleSeconds", 1200),
            JsonRead.Number(element, "stagedBucketSeconds", 300),
            JsonRead.Number(element, "dropChanceBonusPercent", 20),
            JsonRead.Number(element, "maxDuelSeconds", 300),
            JsonRead.Integer(element, "maxBattleReports", 10));
        if (!double.IsFinite(settings.EncounterChancePerBattle) || settings.EncounterChancePerBattle is < 0 or > 1
            || !double.IsFinite(settings.PowerBandRatio) || settings.PowerBandRatio is < 0 or > 10
            || !double.IsFinite(settings.DefeatCooldownSeconds) || settings.DefeatCooldownSeconds < 0
            || !double.IsFinite(settings.StagedSettleSeconds) || settings.StagedSettleSeconds < 0
            || !double.IsFinite(settings.StagedBucketSeconds) || settings.StagedBucketSeconds <= 0
            || !double.IsFinite(settings.DropChanceBonusPercent) || settings.DropChanceBonusPercent < 0
            || !double.IsFinite(settings.MaxDuelSeconds) || settings.MaxDuelSeconds <= 0
            || settings.MaxBattleReports < 1 || settings.MaxBattleReports > 100)
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法全局配置无效。");
        return settings;
    }
}

/// <summary>斗法匹配开关：仅在线推进路径提供撮合能力；离线补算必须传 null/不匹配以保持确定性。</summary>
public sealed record DuelAdvanceOptions(bool MatchingEnabled);

/// <summary>
/// 斗法剧本单条事件（以发起方阵营与单位下标为权威坐标的结算帧，原生支持 1~5 vs 1~5 多方斗法）。
/// 回放时按 ViewerIsInitiator 与 SelfUnitIndex 做视角翻译，不区分挑战/被挑战。
/// </summary>
/// <param name="AtSeconds">事件发生的战斗时钟（秒）。</param>
/// <param name="AttackerIsInitiator">出手方是否属于斗法发起方阵营。</param>
/// <param name="TargetIsInitiator">受击方是否属于斗法发起方阵营。</param>
/// <param name="Damage">本次结算的伤害量（治疗事件为 0）。</param>
/// <param name="Hit">本次攻击是否命中。</param>
/// <param name="Critical">本次攻击是否暴击。</param>
/// <param name="TargetHitpointsAfter">受击方在事件结算后的剩余气血。</param>
/// <param name="AbilityId">施放的技能 ID；普通攻击为空。</param>
/// <param name="Healing">本次结算的治疗量（伤害事件为 0）。</param>
/// <param name="AttackerManapointsAfter">出手方在事件结算后的剩余灵力（manaCost 起手预扣、汲取与回复均已入账）。</param>
/// <param name="TargetManapointsAfter">受击方在事件结算后的剩余灵力。</param>
/// <param name="AbilityNameOverride">施放切态显示名覆写；为空时按 AbilityId 查真源名称。</param>
/// <param name="AttackerUnitIndex">出手方在其所属阵营名单中的下标（0..4；1v1 恒为 0）。</param>
/// <param name="TargetUnitIndex">受击方在其所属阵营名单中的下标（0..4；1v1 恒为 0）。</param>
public sealed record DuelScriptEvent(
    double AtSeconds,
    bool AttackerIsInitiator,
    bool TargetIsInitiator,
    double Damage,
    bool Hit,
    bool Critical,
    double TargetHitpointsAfter,
    string? AbilityId = null,
    double Healing = 0,
    double AttackerManapointsAfter = 0,
    double TargetManapointsAfter = 0,
    bool Break = false,
    bool Parried = false,
    string? AbilityNameOverride = null,
    int AttackerUnitIndex = 0,
    int TargetUnitIndex = 0)
{
    /// <summary>持续消耗品（阵盘）充能事件标记：回放扣件并刷新槽位持续时间，不下发前端动作表现。</summary>
    public const string SustainedActivateMarker = "@sustained-activate";
}

/// <summary>
/// 多方斗法回放中的单个参战玩家镜像（总纲 §7.10.4，原生支持 1~5 vs 1~5）：
/// 承载实时气血/灵力与撮合冻结的属性面板和装配快照，双方全员同构。
/// </summary>
public sealed record DuelPlaybackUnit(
    string PlayerId,
    string DisplayName,
    string Avatar,
    CombatantStats Stats,
    double Hitpoints,
    double Manapoints,
    IReadOnlyList<PlayerAbilitySlot>? Skills = null,
    IReadOnlyList<PlayerEquipmentSlot>? Equipment = null)
{
    /// <summary>校验单名斗法参战单位镜像不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(PlayerId) && PlayerId.Length <= 64
        && !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 64
        && !string.IsNullOrWhiteSpace(Avatar) && Avatar.Length <= 64
        && Stats is not null
        && double.IsFinite(Stats.MaxHitpoints) && Stats.MaxHitpoints > 0
        && double.IsFinite(Stats.MaxManapoints) && Stats.MaxManapoints >= 0
        && double.IsFinite(Hitpoints) && Hitpoints >= 0 && Hitpoints <= Stats.MaxHitpoints
        && double.IsFinite(Manapoints) && Manapoints >= 0 && Manapoints <= Stats.MaxManapoints
        && (Skills is null || (Skills.Count <= 16 && Skills.All(slot => slot is not null
            && (slot.AbilityId?.Length ?? 0) <= 128
            && double.IsFinite(slot.Level) && slot.Level >= 1)))
        && (Equipment is null || (Equipment.Count <= 24 && Equipment.All(item => item is not null
            && !string.IsNullOrWhiteSpace(item.ItemId) && item.ItemId.Length <= 128
            && double.IsFinite(item.EnhancementLevel) && item.EnhancementLevel >= 0)));

    /// <summary>由冻结斗法镜像构造初始满状态回放单位。</summary>
    public static DuelPlaybackUnit FromCombatant(DuelCombatant combatant) => new(
        combatant.PlayerId.Value.ToString("N"),
        combatant.DisplayName,
        combatant.Avatar,
        combatant.Stats,
        combatant.Stats.MaxHitpoints,
        combatant.Stats.MaxManapoints,
        combatant.Skills
            .Select(slot => new PlayerAbilitySlot(JsonRead.Text(slot.Ability, "id"), slot.Level))
            .ToArray(),
        combatant.Equipment);
}

/// <summary>
/// 斗法完整结算剧本：撮合时一次性推演完毕，双方（1~5 vs 1~5）各自回放同一份确定性结果。
/// InitiatorStats/DefenderStats 保留为单人或首员视角快捷投影；多方全员各自视角的统计与紧凑记录
/// 由 InitiatorMemberStats / DefenderMemberStats 与 InitiatorMemberRecords / DefenderMemberRecords 承载。
/// </summary>
public sealed record DuelScript(
    CombatOutcome Outcome,
    double DurationSeconds,
    IReadOnlyList<DuelScriptEvent> Events,
    IReadOnlyList<DuelStagedItem> InitiatorStagedItems,
    IReadOnlyList<DuelStagedItem> DefenderStagedItems,
    IReadOnlyList<CombatUnitStatDocument>? InitiatorStats = null,
    IReadOnlyList<CombatUnitStatDocument>? DefenderStats = null,
    DuelBattleRecordDocument? InitiatorRecord = null,
    DuelBattleRecordDocument? DefenderRecord = null,
    IReadOnlyList<IReadOnlyList<CombatUnitStatDocument>>? InitiatorMemberStats = null,
    IReadOnlyList<IReadOnlyList<CombatUnitStatDocument>>? DefenderMemberStats = null,
    IReadOnlyList<DuelBattleRecordDocument>? InitiatorMemberRecords = null,
    IReadOnlyList<DuelBattleRecordDocument>? DefenderMemberRecords = null);

/// <summary>
/// 单个玩家视角的斗法回放状态；随战斗远征分段持久化，断线重连按事件游标续播。
/// ViewerOutcome 已在挂载时翻译为「本视角」胜负（PlayerWon 即本方胜）；
/// SelfUnits / OpponentUnits 原生支持 1~5 vs 1~5 多方队伍斗法（单人时各含 1 项，缺省可向旧 1v1 存档透明回退）。
/// </summary>
/// <param name="DuelId">斗法场次唯一标识。</param>
/// <param name="OpponentPlayerId">对手首员玩家 ID。</param>
/// <param name="OpponentName">对手展示名（多人队伍显示队长等 N 人）。</param>
/// <param name="ViewerIsInitiator">观看方是否属于斗法发起方阵营。</param>
/// <param name="ViewerOutcome">本视角胜负（挂载时已翻译）。</param>
/// <param name="DurationSeconds">斗法剧本总时长（秒）。</param>
/// <param name="Events">确定性剧本事件轴（双方共享同一份）。</param>
/// <param name="SelfStagedSnapshot">本方暂存区快照（撮合时冻结）。</param>
/// <param name="OpponentStagedSnapshot">对手暂存区快照（撮合时冻结）。</param>
/// <param name="OpponentStats">对手首员镜像的完整战斗属性（撮合时冻结）。</param>
/// <param name="OpponentHitpoints">对手首员当前气血（回放起点）。</param>
/// <param name="OpponentManapoints">对手首员当前灵力（回放起点）。</param>
/// <param name="EventCursor">已回放事件游标；断线重连按它续播。</param>
/// <param name="Resolved">结算是否已入账（暂存与胜负已落账）。</param>
/// <param name="Forfeited">本方是否认输弃赛。</param>
/// <param name="ViewerStats">本方单位战斗统计（撮合时确定性锁定，挂载时并入队首行动账本）。</param>
/// <param name="OpponentSkills">对手首员镜像功法槽位（撮合时冻结）。</param>
/// <param name="OpponentEquipment">对手首员镜像已穿戴装备（撮合时冻结）。</param>
/// <param name="OpponentAvatar">对手首员形象标识。</param>
/// <param name="ViewerRecord">本视角紧凑斗法行动记录。</param>
/// <param name="SelfUnitIndex">本机玩家在己方参战名单中的下标（0..4；1v1 恒为 0）。</param>
/// <param name="SelfUnits">己方全体参战玩家镜像（1~5 人，含实时血蓝与冻结面板/装配）。</param>
/// <param name="OpponentUnits">对方全体参战玩家镜像（1~5 人，含实时血蓝与冻结面板/装配）。</param>
public sealed record DuelPlaybackState(
    string DuelId,
    string OpponentPlayerId,
    string OpponentName,
    bool ViewerIsInitiator,
    CombatOutcome ViewerOutcome,
    double DurationSeconds,
    IReadOnlyList<DuelScriptEvent> Events,
    IReadOnlyList<DuelStagedItem> SelfStagedSnapshot,
    IReadOnlyList<DuelStagedItem> OpponentStagedSnapshot,
    CombatantStats OpponentStats,
    double OpponentHitpoints,
    double OpponentManapoints,
    double EventCursor = 0,
    bool Resolved = false,
    bool Forfeited = false,
    IReadOnlyList<CombatUnitStatDocument>? ViewerStats = null,
    IReadOnlyList<PlayerAbilitySlot>? OpponentSkills = null,
    IReadOnlyList<PlayerEquipmentSlot>? OpponentEquipment = null,
    string OpponentAvatar = "player",
    DuelBattleRecordDocument? ViewerRecord = null,
    int SelfUnitIndex = 0,
    IReadOnlyList<DuelPlaybackUnit>? SelfUnits = null,
    IReadOnlyList<DuelPlaybackUnit>? OpponentUnits = null)
{
    /// <summary>单侧斗法队伍最大人数（对齐总纲 §7.10.4）。</summary>
    public const int MaxTeamUnits = 5;

    /// <summary>校验回放状态不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(DuelId) && DuelId.Length <= 64
        && !string.IsNullOrWhiteSpace(OpponentPlayerId) && OpponentPlayerId.Length <= 64
        && !string.IsNullOrWhiteSpace(OpponentName) && OpponentName.Length <= 64
        && Enum.IsDefined(ViewerOutcome)
        && double.IsFinite(DurationSeconds) && DurationSeconds > 0
        && Events is not null && Events.Count <= 200_000
        && SelfStagedSnapshot is not null && OpponentStagedSnapshot is not null
        && OpponentStats is not null
        && double.IsFinite(OpponentStats.MaxHitpoints) && OpponentStats.MaxHitpoints > 0
        && double.IsFinite(OpponentStats.MaxManapoints) && OpponentStats.MaxManapoints >= 0
        && double.IsFinite(OpponentHitpoints) && OpponentHitpoints >= 0
        && OpponentHitpoints <= OpponentStats.MaxHitpoints
        && double.IsFinite(OpponentManapoints) && OpponentManapoints >= 0
        && OpponentManapoints <= OpponentStats.MaxManapoints
        && double.IsFinite(EventCursor) && EventCursor >= 0 && EventCursor <= Events.Count
        && (ViewerStats is null || (ViewerStats.Count <= 8 && ViewerStats.All(unit => unit is not null
            && !string.IsNullOrWhiteSpace(unit.UnitKey) && unit.UnitKey.Length <= 128
            && Enum.IsDefined(unit.Kind) && (unit.DisplayName?.Length ?? 0) <= 128
            && new[] { unit.DamageDealtPhysical, unit.DamageDealtSpell,
                unit.DamageTakenPhysical, unit.DamageTakenSpell,
                unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
                unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue }
                .All(value => double.IsFinite(value) && value >= 0))))
        && (OpponentSkills is null || (OpponentSkills.Count <= 16 && OpponentSkills.All(slot => slot is not null
            && (slot.AbilityId?.Length ?? 0) <= 128
            && double.IsFinite(slot.Level) && slot.Level >= 1)))
        && (OpponentEquipment is null || (OpponentEquipment.Count <= 24 && OpponentEquipment.All(item => item is not null
            && !string.IsNullOrWhiteSpace(item.ItemId) && item.ItemId.Length <= 128
            && double.IsFinite(item.EnhancementLevel) && item.EnhancementLevel >= 0)))
        && (ViewerRecord is null || ViewerRecord.IsValid)
        && SelfUnitIndex >= 0 && SelfUnitIndex < MaxTeamUnits
        && (SelfUnits is null || (SelfUnits.Count is >= 1 and <= MaxTeamUnits
            && SelfUnitIndex < SelfUnits.Count
            && SelfUnits.All(unit => unit is not null && unit.IsValid)))
        && (OpponentUnits is null || (OpponentUnits.Count is >= 1 and <= MaxTeamUnits
            && OpponentUnits.All(unit => unit is not null && unit.IsValid)));

    /// <summary>把撮合剧本翻译为单人单视角回放载荷：委托多方工厂，自动填充 1v1 单位列表。</summary>
    public static DuelPlaybackState ForViewer(DuelScript script, string duelId,
        DuelCombatant initiator, DuelCombatant defender, bool viewerIsInitiator)
        => ForTeamViewer(script, duelId, [initiator], [defender], viewerIsInitiator, selfUnitIndex: 0);

    /// <summary>
    /// 把多方（1~5 vs 1~5）撮合剧本翻译为指定阵营指定成员视角的回放载荷：
    /// 胜负、暂存快照、双方单位阵列、统计簿与紧凑斗法记录均按观看成员视角对齐。
    /// </summary>
    public static DuelPlaybackState ForTeamViewer(
        DuelScript script,
        string duelId,
        IReadOnlyList<DuelCombatant> initiatorSide,
        IReadOnlyList<DuelCombatant> defenderSide,
        bool viewerIsInitiator,
        int selfUnitIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (initiatorSide is null || initiatorSide.Count is < 1 or > MaxTeamUnits)
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法发起方阵容无效。");
        if (defenderSide is null || defenderSide.Count is < 1 or > MaxTeamUnits)
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法应战方阵容无效。");
        var selfSide = viewerIsInitiator ? initiatorSide : defenderSide;
        var opponentSide = viewerIsInitiator ? defenderSide : initiatorSide;
        if (selfUnitIndex < 0 || selfUnitIndex >= selfSide.Count)
            throw new DomainException(DomainErrorCode.InvalidArgument, "斗法观看成员下标无效。");

        var leadOpponent = opponentSide[0];
        var opponentTitle = opponentSide.Count == 1
            ? leadOpponent.DisplayName
            : $"{leadOpponent.DisplayName}等{opponentSide.Count}人";
        var outcome = viewerIsInitiator ? script.Outcome : script.Outcome switch
        {
            CombatOutcome.PlayerWon => CombatOutcome.EnemyWon,
            CombatOutcome.EnemyWon => CombatOutcome.PlayerWon,
            var other => other
        };
        var memberStatsList = viewerIsInitiator ? script.InitiatorMemberStats : script.DefenderMemberStats;
        var viewerStats = memberStatsList is not null && selfUnitIndex < memberStatsList.Count
            ? memberStatsList[selfUnitIndex]
            : (viewerIsInitiator ? script.InitiatorStats : script.DefenderStats);
        var memberRecordsList = viewerIsInitiator ? script.InitiatorMemberRecords : script.DefenderMemberRecords;
        var rawRecord = memberRecordsList is not null && selfUnitIndex < memberRecordsList.Count
            ? memberRecordsList[selfUnitIndex]
            : (viewerIsInitiator ? script.InitiatorRecord : script.DefenderRecord);

        var selfUnits = selfSide.Select(DuelPlaybackUnit.FromCombatant).ToArray();
        var opponentUnits = opponentSide.Select(DuelPlaybackUnit.FromCombatant).ToArray();

        return new DuelPlaybackState(
            duelId,
            leadOpponent.PlayerId.Value.ToString("N"),
            opponentTitle,
            viewerIsInitiator,
            outcome,
            script.DurationSeconds,
            script.Events,
            viewerIsInitiator ? script.InitiatorStagedItems : script.DefenderStagedItems,
            viewerIsInitiator ? script.DefenderStagedItems : script.InitiatorStagedItems,
            leadOpponent.Stats,
            leadOpponent.Stats.MaxHitpoints,
            leadOpponent.Stats.MaxManapoints,
            ViewerStats: viewerStats,
            OpponentSkills: opponentUnits[0].Skills,
            OpponentEquipment: opponentUnits[0].Equipment,
            OpponentAvatar: leadOpponent.Avatar,
            ViewerRecord: rawRecord is null ? null : rawRecord with { DuelId = duelId },
            SelfUnitIndex: selfUnitIndex,
            SelfUnits: selfUnits,
            OpponentUnits: opponentUnits);
    }
}

/// <summary>斗法镜像冻结的战斗自动消耗品槽位快照（撮合推演专用运行时输入，不随剧本持久化）。</summary>
public sealed record DuelConsumableSlotSnapshot(
    int SlotIndex,
    string Group,
    string ItemId,
    double InitialCooldownRemainingSeconds,
    double InitialSustainedRemainingSeconds);

/// <summary>斗法剧本推演输入：一方开战瞬间的镜像快照（属性/技能/装备/暂存物/战斗消耗品在撮合时冻结）。</summary>
public sealed record DuelCombatant(
    PlayerId PlayerId,
    string DisplayName,
    double Power,
    CombatantStats Stats,
    IReadOnlyList<SkillSlot> Skills,
    IReadOnlyList<DuelStagedItem> StagedItems,
    IReadOnlyList<PlayerEquipmentSlot> Equipment,
    string Avatar = "player",
    IReadOnlyList<DuelConsumableSlotSnapshot>? ConsumableSlots = null,
    IReadOnlyDictionary<string, JsonElement[]>? ConsumableTriggers = null,
    IReadOnlyDictionary<string, double>? ItemBalances = null,
    IReadOnlyList<ActiveBuff>? ExternalBuffs = null,
    double SnapshotAtUnixSeconds = 0);
