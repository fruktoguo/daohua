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
        static double Read(JsonElement obj, string key, double fallback) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.TryGetDouble(out var number)
                ? number : fallback;
        static int ReadInt(JsonElement obj, string key, int fallback) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
                ? number : fallback;
        var settings = new DuelSettings(
            Read(element, "encounterChancePerBattle", 0.005),
            Read(element, "powerBandRatio", 0.3),
            Read(element, "defeatCooldownSeconds", 3600),
            Read(element, "stagedSettleSeconds", 1200),
            Read(element, "dropChanceBonusPercent", 20),
            Read(element, "maxDuelSeconds", 300),
            ReadInt(element, "maxBattleReports", 10));
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
    double Healing = 0);

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
    double OpponentMaxHitpoints,
    double OpponentHitpoints,
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
        && double.IsFinite(OpponentMaxHitpoints) && OpponentMaxHitpoints > 0
        && double.IsFinite(OpponentHitpoints) && OpponentHitpoints >= 0
        && OpponentHitpoints <= OpponentMaxHitpoints
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
            opponent.Stats.MaxHitpoints,
            opponent.Stats.MaxHitpoints);
    }
}

/// <summary>斗法剧本推演输入：一方开战瞬间的镜像快照（属性/技能/暂存物在撮合时冻结）。</summary>
public sealed record DuelCombatant(
    PlayerId PlayerId,
    string DisplayName,
    double Power,
    CombatantStats Stats,
    IReadOnlyList<MonsterSkillSlot> Skills,
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
        foreach (var combatEvent in result.Events)
            events.Add(new DuelScriptEvent(
                combatEvent.AtSeconds,
                combatEvent.AttackerId == "player",
                combatEvent.TargetId == "player",
                combatEvent.Damage,
                combatEvent.Hit,
                combatEvent.Critical,
                combatEvent.TargetHitpointsAfter,
                combatEvent.AbilityId,
                combatEvent.Healing));
        return new DuelScript(outcome, Math.Max(encounter.ElapsedSeconds, 1e-9), events,
            initiator.StagedItems, defender.StagedItems);
    }
}

/// <summary>斗法镜像技能触发条件求值：与玩家功法同一套触发 JSON，作用于战斗单位状态。</summary>
internal static class DuelTriggerEvaluator
{
    public static bool MatchesAll(IReadOnlyList<JsonElement> triggers, CombatantState caster,
        CombatantState? primaryTarget, IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents)
    {
        foreach (var trigger in triggers)
            if (!Matches(trigger, caster, primaryTarget, allies, opponents)) return false;
        return true;
    }

    private static bool Matches(JsonElement trigger, CombatantState caster,
        CombatantState? primaryTarget, IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents)
    {
        var dependency = Text(trigger, "dependency") ?? "self";
        var unit = dependency switch
        {
            "self" => caster,
            "target-enemy" => primaryTarget ?? caster,
            "ally-lowest-hp" => allies.Where(unit => unit.IsAlive).OrderBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints).FirstOrDefault() ?? caster,
            "ally-highest-hp" => allies.Where(unit => unit.IsAlive).OrderByDescending(unit => unit.Hitpoints).FirstOrDefault() ?? caster,
            "enemy-lowest-hp" => opponents.Where(unit => unit.IsAlive).OrderBy(unit => unit.Hitpoints / unit.Stats.MaxHitpoints).FirstOrDefault() ?? primaryTarget ?? caster,
            "enemy-highest-hp" => opponents.Where(unit => unit.IsAlive).OrderByDescending(unit => unit.Hitpoints).FirstOrDefault() ?? primaryTarget ?? caster,
            _ => caster
        };
        var maxHp = Math.Max(1e-9, unit.Stats.MaxHitpoints);
        var maxMana = Math.Max(1e-9, unit.Stats.MaxManapoints);
        var value = Text(trigger, "condition") switch
        {
            "current-hp" => unit.Hitpoints,
            "missing-hp" => maxHp - unit.Hitpoints,
            "current-hp-percentage" => unit.Hitpoints / maxHp * 100,
            "missing-hp-percentage" => (maxHp - unit.Hitpoints) / maxHp * 100,
            "current-mana" => unit.Manapoints,
            "missing-mana" => maxMana - unit.Manapoints,
            "current-mana-percentage" => unit.Manapoints / maxMana * 100,
            "missing-mana-percentage" => (maxMana - unit.Manapoints) / maxMana * 100,
            // 斗法镜像不携带 Buff 状态，buff-stacks 恒为 0。
            "buff-stacks" => 0d,
            _ => 0d
        };
        var threshold = trigger.TryGetProperty("value", out var thresholdProperty) && thresholdProperty.TryGetDouble(out var parsed)
            ? parsed : 0;
        return Text(trigger, "comparator") switch
        {
            "greater-than-equal" => value >= threshold,
            "less-than-equal" => value <= threshold,
            "greater-than" => value > threshold,
            "less-than" => value < threshold,
            _ => false
        };
    }

    private static string? Text(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
