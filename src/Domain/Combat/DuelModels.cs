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
/// 斗法剧本单条事件（以发起方为 player 视角的权威结算帧）。
/// 回放时按 ViewerIsInitiator 做视角翻译，不区分挑战/被挑战。
/// </summary>
/// <param name="AtSeconds">事件发生的战斗时钟（秒）。</param>
/// <param name="AttackerIsInitiator">出手方是否为斗法发起方。</param>
/// <param name="TargetIsInitiator">受击方是否为斗法发起方。</param>
/// <param name="Damage">本次结算的伤害量（治疗事件为 0）。</param>
/// <param name="Hit">本次攻击是否命中。</param>
/// <param name="Critical">本次攻击是否暴击。</param>
/// <param name="TargetHitpointsAfter">受击方在事件结算后的剩余气血。</param>
/// <param name="AbilityId">施放的技能 ID；普通攻击为空。</param>
/// <param name="Healing">本次结算的治疗量（伤害事件为 0）。</param>
/// <param name="AttackerManapointsAfter">出手方在事件结算后的剩余灵力（manaCost 起手预扣、汲取与回复均已入账）。</param>
/// <param name="TargetManapointsAfter">受击方在事件结算后的剩余灵力。</param>
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
    double TargetManapointsAfter = 0);

/// <summary>
/// 斗法完整结算剧本：撮合时一次性推演完毕，双方各自回放同一份确定性结果。
/// InitiatorStats/DefenderStats 为推演时按本机视角累计的战斗单位统计（unitKey=player），
/// 仅携带本方一行——对方队伍数据不进入任何一方的行动账本。
/// </summary>
public sealed record DuelScript(
    CombatOutcome Outcome,
    double DurationSeconds,
    IReadOnlyList<DuelScriptEvent> Events,
    IReadOnlyList<DuelStagedItem> InitiatorStagedItems,
    IReadOnlyList<DuelStagedItem> DefenderStagedItems,
    IReadOnlyList<CombatUnitStatDocument>? InitiatorStats = null,
    IReadOnlyList<CombatUnitStatDocument>? DefenderStats = null);

/// <summary>
/// 单个玩家视角的斗法回放状态；随战斗远征分段持久化，断线重连按事件游标续播。
/// ViewerOutcome 已在挂载时翻译为「本视角」胜负（PlayerWon 即本玩家胜）。
/// </summary>
/// <param name="DuelId">斗法场次唯一标识。</param>
/// <param name="OpponentPlayerId">对手玩家 ID。</param>
/// <param name="OpponentName">对手展示名。</param>
/// <param name="ViewerIsInitiator">观看方是否为斗法发起方。</param>
/// <param name="ViewerOutcome">本视角胜负（挂载时已翻译）。</param>
/// <param name="DurationSeconds">斗法剧本总时长（秒）。</param>
/// <param name="Events">确定性剧本事件轴（双方共享同一份）。</param>
/// <param name="SelfStagedSnapshot">本方暂存区快照（撮合时冻结）。</param>
/// <param name="OpponentStagedSnapshot">对手暂存区快照（撮合时冻结）。</param>
/// <param name="OpponentStats">对手镜像的完整战斗属性（撮合时冻结；气血/灵力上限与属性面板真值均取自这里）。</param>
/// <param name="OpponentHitpoints">对手当前气血（回放起点）。</param>
/// <param name="OpponentManapoints">对手当前灵力（回放起点）。</param>
/// <param name="EventCursor">已回放事件游标；断线重连按它续播。</param>
/// <param name="Resolved">结算是否已入账（暂存与胜负已落账）。</param>
/// <param name="Forfeited">本方是否认输弃赛。</param>
/// <param name="ViewerStats">本方单位战斗统计（撮合时确定性锁定，挂载时并入队首行动账本）。</param>
/// <param name="OpponentSkills">对手镜像功法槽位（撮合时冻结；详情弹窗「装配」Tab 按需投影的唯一数据源）。</param>
/// <param name="OpponentEquipment">对手镜像已穿戴装备（撮合时冻结；与功法槽同为装配展示数据，不参与回放结算）。</param>
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
    IReadOnlyList<PlayerEquipmentSlot>? OpponentEquipment = null)
{
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
                unit.HealingRegen, unit.HealingOther }
                .All(value => double.IsFinite(value) && value >= 0))))
        && (OpponentSkills is null || (OpponentSkills.Count <= 16 && OpponentSkills.All(slot => slot is not null
            && (slot.AbilityId?.Length ?? 0) <= 128
            && double.IsFinite(slot.Level) && slot.Level >= 1)))
        && (OpponentEquipment is null || (OpponentEquipment.Count <= 24 && OpponentEquipment.All(item => item is not null
            && !string.IsNullOrWhiteSpace(item.ItemId) && item.ItemId.Length <= 128
            && double.IsFinite(item.EnhancementLevel) && item.EnhancementLevel >= 0)));

    /// <summary>把撮合剧本翻译为单视角回放载荷：胜负与双方暂存快照按观看方翻转。</summary>
    public static DuelPlaybackState ForViewer(DuelScript script, string duelId,
        DuelCombatant initiator, DuelCombatant defender, bool viewerIsInitiator)
    {
        var opponent = viewerIsInitiator ? defender : initiator;
        var outcome = viewerIsInitiator ? script.Outcome : script.Outcome switch
        {
            CombatOutcome.PlayerWon => CombatOutcome.EnemyWon,
            CombatOutcome.EnemyWon => CombatOutcome.PlayerWon,
            var other => other
        };
        return new DuelPlaybackState(
            duelId,
            opponent.PlayerId.Value.ToString("N"),
            opponent.DisplayName,
            viewerIsInitiator,
            outcome,
            script.DurationSeconds,
            script.Events,
            viewerIsInitiator ? script.InitiatorStagedItems : script.DefenderStagedItems,
            viewerIsInitiator ? script.DefenderStagedItems : script.InitiatorStagedItems,
            opponent.Stats,
            opponent.Stats.MaxHitpoints,
            opponent.Stats.MaxManapoints,
            ViewerStats: viewerIsInitiator ? script.InitiatorStats : script.DefenderStats,
            OpponentSkills: opponent.Skills
                .Select(slot => new PlayerAbilitySlot(JsonRead.Text(slot.Ability, "id"), slot.Level))
                .ToArray(),
            OpponentEquipment: opponent.Equipment);
    }
}

/// <summary>斗法剧本推演输入：一方开战瞬间的镜像快照（属性/技能/装备/暂存物在撮合时冻结）。</summary>
public sealed record DuelCombatant(
    PlayerId PlayerId,
    string DisplayName,
    double Power,
    CombatantStats Stats,
    IReadOnlyList<SkillSlot> Skills,
    IReadOnlyList<DuelStagedItem> StagedItems,
    IReadOnlyList<PlayerEquipmentSlot> Equipment);

