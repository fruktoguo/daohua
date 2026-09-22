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
/// <param name="DropChanceBonusPercent">斗法开启时物品爆率加成百分比点数（20 = +20% 乘区）</param>
/// <param name="MaxDuelSeconds">单场斗法剧本最长结算秒数，超时判平</param>
/// <param name="MaxBattleReports">战斗收益记录最大保存条数（10）</param>
public sealed record DuelSettings(
    double EncounterChancePerBattle = 0.005,
    double PowerBandRatio = 0.3,
    double DefeatCooldownSeconds = 3600,
    double StagedSettleSeconds = 1200,
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
            JsonRead.Number(element, "dropChanceBonusPercent", 20),
            JsonRead.Number(element, "maxDuelSeconds", 300),
            JsonRead.Integer(element, "maxBattleReports", 10));
        if (!double.IsFinite(settings.EncounterChancePerBattle) || settings.EncounterChancePerBattle is < 0 or > 1
            || !double.IsFinite(settings.PowerBandRatio) || settings.PowerBandRatio is < 0 or > 10
            || !double.IsFinite(settings.DefeatCooldownSeconds) || settings.DefeatCooldownSeconds < 0
            || !double.IsFinite(settings.StagedSettleSeconds) || settings.StagedSettleSeconds < 0
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
    /// <summary>出手方在事件结算后的剩余灵力（manaCost 起手预扣、汲取与回复均已入账）。</summary>
    double AttackerManapointsAfter = 0,
    /// <summary>受击方在事件结算后的剩余灵力。</summary>
    double TargetManapointsAfter = 0);

/// <summary>斗法完整结算剧本：撮合时一次性推演完毕，双方各自回放同一份确定性结果。</summary>
public sealed record DuelScript(
    CombatOutcome Outcome,
    double DurationSeconds,
    IReadOnlyList<DuelScriptEvent> Events,
    IReadOnlyList<DuelStagedItem> InitiatorStagedItems,
    IReadOnlyList<DuelStagedItem> DefenderStagedItems);

/// <summary>
/// 单个玩家视角的斗法回放状态；随战斗远征分段持久化，断线重连按事件游标续播。
/// ViewerOutcome 已在挂载时翻译为「本视角」胜负（PlayerWon 即本玩家胜）。
/// </summary>
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
    /// <summary>对手镜像的完整战斗属性（撮合时冻结；气血/灵力上限与属性面板真值均取自这里）。</summary>
    CombatantStats OpponentStats,
    double OpponentHitpoints,
    double OpponentManapoints,
    double EventCursor = 0,
    bool Resolved = false,
    bool Forfeited = false)
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
        && double.IsFinite(EventCursor) && EventCursor >= 0 && EventCursor <= Events.Count;

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
            opponent.Stats.MaxManapoints);
    }
}

/// <summary>斗法剧本推演输入：一方开战瞬间的镜像快照（属性/技能/暂存物在撮合时冻结）。</summary>
public sealed record DuelCombatant(
    PlayerId PlayerId,
    string DisplayName,
    double Power,
    CombatantStats Stats,
    IReadOnlyList<SkillSlot> Skills,
    IReadOnlyList<DuelStagedItem> StagedItems);

/// <summary>
/// 斗法剧本推演器：撮合成功后把双方镜像一次性确定性推演至终结，
/// 产出双方共享的 DuelScript；战斗期间任何配置变更不影响结果。
/// </summary>
public static class DuelScriptEngine
{
    /// <summary>推演一场斗法；initiator 固定映射为剧本中的 player 视角。</summary>
    public static DuelScript Resolve(
        DuelCombatant initiator,
        DuelCombatant defender,
        long seed,
        DuelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(defender);
        ArgumentNullException.ThrowIfNull(settings);
        var encounter = CombatEncounterState.Start("duel", seed,
            new CombatantTemplate(initiator.Stats, initiator.Skills),
            [new CombatantTemplate(defender.Stats, defender.Skills)]);
        var result = DeterministicCombatEngine.AdvanceTo(encounter, settings.MaxDuelSeconds);
        var outcome = encounter.Outcome == CombatOutcome.Running ? CombatOutcome.Draw : encounter.Outcome;
        var events = new List<DuelScriptEvent>(result.Events.Count);
        // 双方灵力沿事件流滚动携带：主事件带结算后真值，衍生事件沿用最近已知值，
        // 回放侧无需领域推算即可逐帧还原双资源条。
        var initiatorMp = initiator.Stats.MaxManapoints;
        var defenderMp = defender.Stats.MaxManapoints;
        foreach (var combatEvent in result.Events)
        {
            var attackerIsInitiator = combatEvent.AttackerId == "player";
            var targetIsInitiator = combatEvent.TargetId == "player";
            if (combatEvent.AttackerManapointsAfter is { } attackerMp)
            {
                if (attackerIsInitiator) initiatorMp = attackerMp; else defenderMp = attackerMp;
            }
            if (combatEvent.TargetManapointsAfter is { } targetMp)
            {
                if (targetIsInitiator) initiatorMp = targetMp; else defenderMp = targetMp;
            }
            events.Add(new DuelScriptEvent(
                combatEvent.AtSeconds,
                attackerIsInitiator,
                targetIsInitiator,
                combatEvent.Damage,
                combatEvent.Hit,
                combatEvent.Critical,
                combatEvent.TargetHitpointsAfter,
                combatEvent.AbilityId,
                combatEvent.Healing,
                attackerIsInitiator ? initiatorMp : defenderMp,
                targetIsInitiator ? initiatorMp : defenderMp));
        }
        return new DuelScript(outcome, Math.Max(encounter.ElapsedSeconds, 1e-9), events,
            initiator.StagedItems, defender.StagedItems);
    }
}

