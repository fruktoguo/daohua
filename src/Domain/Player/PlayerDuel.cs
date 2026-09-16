using System.Text.Json;
using Domain.Combat;
using Domain.GameData;

namespace Domain.Player;

/// <summary>暂存区单条物品记录：斗法期间战斗掉落先入暂存，到期或结算后归属才确定。</summary>
public sealed record DuelStagedItem(string ItemId, double Quantity, double AcquiredAtUnixSeconds)
{
    /// <summary>校验暂存记录不变量。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && ItemId.Length <= 128
        && double.IsFinite(Quantity) && Quantity > 0
        && double.IsFinite(AcquiredAtUnixSeconds) && AcquiredAtUnixSeconds >= 0;
}

/// <summary>战报中的物品收益条目。</summary>
public sealed record PlayerReportItem(string ItemId, double Quantity);

/// <summary>六维经验增量容器；战报与收益分配共用。</summary>
public sealed class PlayerRootExpValues
{
    /// <summary>体魄经验增量</summary>
    public double Stamina { get; set; }
    /// <summary>悟性经验增量</summary>
    public double Intelligence { get; set; }
    /// <summary>力道经验增量</summary>
    public double Attack { get; set; }
    /// <summary>防御经验增量</summary>
    public double Defense { get; set; }
    /// <summary>剑术经验增量</summary>
    public double Swordsmanship { get; set; }
    /// <summary>符术经验增量</summary>
    public double Talisman { get; set; }
    /// <summary>六维经验合计</summary>
    public double Total => Stamina + Intelligence + Attack + Defense + Swordsmanship + Talisman;
    /// <summary>按根骨维度累加经验。</summary>
    public void Add(CoreAspectKind attribute, double amount)
    {
        switch (attribute)
        {
            case CoreAspectKind.Stamina: Stamina += amount; break;
            case CoreAspectKind.Intelligence: Intelligence += amount; break;
            case CoreAspectKind.Attack: Attack += amount; break;
            case CoreAspectKind.Defense: Defense += amount; break;
            case CoreAspectKind.Swordsmanship: Swordsmanship += amount; break;
            default: Talisman += amount; break;
        }
    }
    /// <summary>拷贝一份独立副本。</summary>
    public PlayerRootExpValues Clone() => new()
    {
        Stamina = Stamina, Intelligence = Intelligence, Attack = Attack,
        Defense = Defense, Swordsmanship = Swordsmanship, Talisman = Talisman
    };
}

/// <summary>
/// 单次战斗行动收益账本：一次入队行动从开始到移出队列期间的全部收益。
/// 新行动开始时重置；归档后保存最近 10 次。
/// </summary>
public sealed class PlayerBattleReport
{
    /// <summary>报告标识（绑定行动实例 Id 的 N 格式串）</summary>
    public string ReportId { get; set; } = "";
    /// <summary>绑定的行动实例 Id</summary>
    public Guid ActionInstanceId { get; set; }
    /// <summary>行动配表 ID</summary>
    public string ActionId { get; set; } = "";
    /// <summary>行动名称快照</summary>
    public string ActionName { get; set; } = "";
    /// <summary>统计开始时间（Unix 秒）</summary>
    public double StartedAtUnixSeconds { get; set; }
    /// <summary>统计结束时间（Unix 秒）</summary>
    public double EndedAtUnixSeconds { get; set; }
    /// <summary>修为收益</summary>
    public double Qi { get; set; }
    /// <summary>底蕴收益（修为满溢转化部分单独统计）</summary>
    public double Experience { get; set; }
    /// <summary>六维经验收益</summary>
    public PlayerRootExpValues RootExp { get; } = new();
    /// <summary>物品收益（含暂存区与缴获）</summary>
    public List<PlayerReportItem> Items { get; } = [];
    /// <summary>本周期斗法胜场</summary>
    public double DuelWins { get; set; }
    /// <summary>本周期斗法败场（含主动结束判负）</summary>
    public double DuelLosses { get; set; }

    /// <summary>是否存在任何可记录内容；空账本归档时丢弃。</summary>
    public bool HasContent => Qi > 0 || Experience > 0 || RootExp.Total > 0
        || Items.Count > 0 || DuelWins > 0 || DuelLosses > 0;

    /// <summary>合并记录一件物品收益。</summary>
    public void AddItem(ItemId itemId, double quantity)
    {
        var index = Items.FindIndex(entry => entry.ItemId == itemId.Value);
        if (index >= 0) Items[index] = Items[index] with { Quantity = Items[index].Quantity + quantity };
        else Items.Add(new(itemId.Value, quantity));
    }
}

/// <summary>
/// 斗法玩法分段状态：遭遇计数、失败冷却、进行中斗法、暂存区与战斗收益记录。
/// </summary>
public sealed class PlayerDuelState
{
    /// <summary>斗法遭遇计数；大于 0 时下次进入战斗尝试撮合。</summary>
    public double EncounterCharges { get; set; }
    /// <summary>失败冷却截止时间（Unix 秒）；冷却内不匹配也不被匹配。</summary>
    public double CooldownUntilUnixSeconds { get; set; }
    /// <summary>进行中斗法标识；非空时暂存区冻结入账。</summary>
    public string? ActiveDuelId { get; set; }
    /// <summary>暂存区物品（先入先出结算）。</summary>
    public List<DuelStagedItem> StagedItems { get; } = [];
    /// <summary>当前进行中的收益账本；新战斗行动开始时重建。</summary>
    public PlayerBattleReport? CurrentReport { get; set; }
    /// <summary>最近战报归档（最新在前，最多 MaxBattleReports 条）。</summary>
    public List<PlayerBattleReport> Reports { get; } = [];
}

/// <summary>斗法分段的可持久化文档形态。</summary>
public sealed record PlayerDuelStateDocument(
    double EncounterCharges,
    double CooldownUntilUnixSeconds,
    string? ActiveDuelId,
    IReadOnlyList<DuelStagedItem> StagedItems,
    PlayerBattleReportDocument? CurrentReport,
    IReadOnlyList<PlayerBattleReportDocument> Reports);

/// <summary>战报的可持久化文档形态（不含内部 ActionInstanceId，由 ReportId 承载）。</summary>
public sealed record PlayerBattleReportDocument(
    string ReportId,
    string ActionId,
    string ActionName,
    double StartedAtUnixSeconds,
    double EndedAtUnixSeconds,
    double Qi,
    double Experience,
    PlayerRootExpDocument RootExp,
    IReadOnlyList<PlayerReportItem> Items,
    double DuelWins,
    double DuelLosses);

/// <summary>六维经验文档形态。</summary>
public sealed record PlayerRootExpDocument(
    double Stamina, double Intelligence, double Attack,
    double Defense, double Swordsmanship, double Talisman);

public sealed partial class PlayerAggregate
{
    /// <summary>获取或设置 斗法分段状态 的值。</summary>
    public PlayerDuelState Duel { get; } = new();

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
        var attributes = Combat;
        attributes.Hitpoints = attributes.MaxHitpoints;
        attributes.Manapoints = attributes.MaxManapoints;
        Combat.Skills = PlayerCombatSkillState.Empty;
        _actions[0].Expedition = expedition with
        {
            Phase = CombatExpeditionPhase.Duel,
            PhaseElapsedSeconds = 0,
            PhaseDurationSeconds = 0,
            BattleElapsedSeconds = 0,
            PlayerHitpoints = Combat.Hitpoints,
            PlayerManapoints = Combat.Manapoints,
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
        RemoveStagedItems(duel.SelfStagedSnapshot);
        Duel.ActiveDuelId = null;
        Duel.CooldownUntilUnixSeconds = nowUnix + settings.DefeatCooldownSeconds;
        Duel.CurrentReport?.DuelLosses += 1;
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
        MarkDirty(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>斗法期间获得物品：进入暂存区而非直接入账。</summary>
    public void StageItem(ItemId itemId, double quantity, double acquiredAtUnixSeconds)
    {
        if (!itemId.IsValid || !double.IsFinite(quantity) || quantity <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "暂存物品无效。");
        Duel.StagedItems.Add(new(itemId.Value, quantity, acquiredAtUnixSeconds));
        Duel.CurrentReport?.AddItem(itemId, quantity);
        MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>
    /// 结算到期暂存物品：到达入账时间且不在斗法冻结期时返回待入账列表并从暂存区移除。
    /// </summary>
    public IReadOnlyList<DuelStagedItem> SettleStagedItems(double nowUnixSeconds, double settleSeconds)
    {
        if (Duel.ActiveDuelId is not null || Duel.StagedItems.Count == 0) return [];
        var settled = Duel.StagedItems.Where(item => item.AcquiredAtUnixSeconds + settleSeconds <= nowUnixSeconds).ToArray();
        if (settled.Length == 0) return [];
        foreach (var item in settled) Duel.StagedItems.Remove(item);
        MarkDirty(PlayerSectionKind.Duel);
        return settled;
    }

    /// <summary>按快照清单移除暂存物品（斗法落败被对方缴获）。</summary>
    internal void RemoveStagedItems(IReadOnlyList<DuelStagedItem> snapshot)
    {
        if (snapshot.Count == 0) return;
        foreach (var item in snapshot)
        {
            var index = Duel.StagedItems.IndexOf(item);
            if (index >= 0) Duel.StagedItems.RemoveAt(index);
        }
        MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>新战斗行动会话开始时重建收益账本；旧账本非空则归档（最多保留 10 条）。</summary>
    internal void BeginBattleReport(Guid actionInstanceId, ActionId actionId, string actionName, DateTimeOffset now)
    {
        if (Duel.CurrentReport?.ActionInstanceId == actionInstanceId) return;
        ArchiveCurrentReport(now);
        Duel.CurrentReport = new PlayerBattleReport
        {
            ReportId = actionInstanceId.ToString("N"),
            ActionInstanceId = actionInstanceId,
            ActionId = actionId.Value,
            ActionName = actionName,
            StartedAtUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d,
            EndedAtUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d
        };
        MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>归档当前收益账本；仅当账本与队首行动解绑或行动结束时调用。</summary>
    internal void ArchiveCurrentReport(DateTimeOffset endedAt)
    {
        var report = Duel.CurrentReport;
        if (report is null) return;
        Duel.CurrentReport = null;
        report.EndedAtUnixSeconds = endedAt.ToUnixTimeMilliseconds() / 1000d;
        if (!report.HasContent) { MarkDirty(PlayerSectionKind.Duel); return; }
        Duel.Reports.Insert(0, report);
        while (Duel.Reports.Count > DuelSettingsTuning.MaxBattleReports)
            Duel.Reports.RemoveAt(Duel.Reports.Count - 1);
        MarkDirty(PlayerSectionKind.Duel);
    }

    /// <summary>
    /// 构建斗法镜像快照：开战瞬间冻结属性面板、功法技能槽与暂存物品清单。
    /// 镜像技能使用槽位配置的触发条件（缺省回退配表 defaultTriggers），可重复施放。
    /// </summary>
    public DuelCombatant BuildDuelMirror(GameDataCatalog gameData)
    {
        var stats = PlayerAttributeResolver.Resolve(this, gameData).ToCombatant();
        var skills = new List<MonsterSkillSlot>();
        foreach (var slot in Loadout.AbilitySlots)
        {
            if (slot.AbilityId is null || !gameData.Abilities.TryGetValue(slot.AbilityId, out var ability)) continue;
            IReadOnlyList<JsonElement> triggers = slot.Triggers.Count > 0
                ? slot.Triggers
                : ability.TryGetProperty("defaultTriggers", out var defaults) && defaults.ValueKind == JsonValueKind.Array
                    ? defaults.EnumerateArray().ToArray()
                    : [];
            skills.Add(new MonsterSkillSlot(ability, slot.Level, 1, triggers));
        }
        return new DuelCombatant(PlayerId, DisplayName, DuelPower, stats, skills, Duel.StagedItems.ToArray());
    }

    /// <summary>斗法调参当前生效值；由模拟推进在配表上下文中刷新。</summary>
    internal DuelSettings DuelSettingsTuning { get; set; } = DuelSettings.Default;
}
