using Domain.Simulation;

namespace Domain.Player;

/// <summary>
/// 已完成结算的统计增量；只搬运实际入账量，不计算奖励、不修改资产或调度状态。
/// 批次集合仅在同步记账期间借用，账本按源集合顺序累加，避免改变浮点与条目顺序。
/// </summary>
public readonly struct PlayerStatsDelta
{
    public double Experience { get; init; }
    public double Foundation { get; init; }
    public double CombatExperience { get; init; }
    public double Cycles { get; init; }
    public double Encounters { get; init; }
    public double NormalKills { get; init; }
    public double EliteKills { get; init; }
    public double BossKills { get; init; }
    public double Deaths { get; init; }
    public double DuelWins { get; init; }
    public double DuelLosses { get; init; }
    public CoreAspectKind? RootAspect { get; init; }
    public double RootExperience { get; init; }
    public string? AbilityId { get; init; }
    public double AbilityExperience { get; init; }
    public PlayerReportLevelUp? LevelUp { get; init; }
    public ItemId? GainedItem { get; init; }
    public double GainedQuantity { get; init; }
    public ItemId? LostItem { get; init; }
    public double LostQuantity { get; init; }
    public IReadOnlyDictionary<ItemId, double>? ItemGains { get; init; }
    public IReadOnlyDictionary<ItemId, double>? ItemLosses { get; init; }
    public IReadOnlyList<DuelStagedItem>? StagedItemLosses { get; init; }

    /// <summary>账本字段的纯增量累加；日期、归属与导出脏标由账本所有者处理。</summary>
    public void ApplyTo(PlayerBattleReport report)
    {
        if (Experience != 0) report.Experience += Experience;
        if (Foundation != 0) report.Foundation += Foundation;
        if (CombatExperience != 0) report.CombatExperience += CombatExperience;
        if (Cycles != 0) report.Cycles += Cycles;
        if (Encounters != 0) report.Encounters += Encounters;
        // 波次统计原本逐只加 1；保留每类计数的加法序列，不能先求和再一次加入大额历史计数。
        for (double i = 0; i < NormalKills; i++) report.NormalKills += 1;
        for (double i = 0; i < EliteKills; i++) report.EliteKills += 1;
        for (double i = 0; i < BossKills; i++) report.BossKills += 1;
        if (Deaths != 0) report.Deaths += Deaths;
        if (DuelWins != 0) report.DuelWins += DuelWins;
        if (DuelLosses != 0) report.DuelLosses += DuelLosses;
        if (RootAspect is { } aspect) report.RootExp.Add(aspect, RootExperience);
        if (AbilityId is { } abilityId) report.AddAbilityExp(abilityId, AbilityExperience);
        if (LevelUp is { } levelUp) report.LevelUps.Add(levelUp);
        if (GainedItem is { } gainedItem) report.AddItem(gainedItem, GainedQuantity);
        if (LostItem is { } lostItem) report.AddItemLost(lostItem, LostQuantity);
        if (ItemGains is { } gains)
            foreach (var (item, quantity) in gains) report.AddItem(item, quantity);
        if (ItemLosses is { } losses)
            foreach (var (item, quantity) in losses) report.AddItemLost(item, quantity);
        if (StagedItemLosses is { } stagedLosses)
            foreach (var item in stagedLosses) report.AddItemLost(new ItemId(item.ItemId), item.Quantity);
    }
}

/// <summary>运行时统计派发边界；无实体聚合直接记账，ECS 命令门面与推进上下文同步派发 tag。</summary>
public interface IPlayerStatsSink
{
    void Record(IPlayerResourceContext player, Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta);
}
