using Domain.Simulation;

namespace Domain.Player;

/// <summary>
/// 已完成结算的统计增量；只搬运实际入账量，不计算奖励、不修改资产或调度状态。
/// 批次集合仅在同步记账期间借用，账本按源集合顺序累加，避免改变浮点与条目顺序。
/// </summary>
public readonly struct PlayerStatsDelta
{
    /// <summary>修为本层入账量（经 CreditExperience 结算后的实际入账值，非基础收益）。</summary>
    public double Experience { get; init; }
    /// <summary>底蕴入账量（修为满层后沉淀，突破不清空）。</summary>
    public double Foundation { get; init; }
    /// <summary>战斗经验入账量（按增幅前基础量等额入账）。</summary>
    public double CombatExperience { get; init; }
    /// <summary>
    /// 内功修为增幅后的原始份额：只供会话投递在成员单写者内重算瓶颈与底蕴——
    /// 容器清零已结算的 <see cref="Experience"/>/<see cref="Foundation"/>，消费端按本值入账；
    /// 0 表示未提供（按 <see cref="CombatExperience"/> 计）。本字段不是已结算入账量，不进战报账本。
    /// </summary>
    public double CultivationExperience { get; init; }
    /// <summary>完成轮次数（计时轮/战斗波次/抵达终点均计 1）。</summary>
    public double Cycles { get; init; }
    /// <summary>战斗遭遇场次。</summary>
    public double Encounters { get; init; }
    /// <summary>妖兽（普通）击杀数。</summary>
    public double NormalKills { get; init; }
    /// <summary>精英击杀数。</summary>
    public double EliteKills { get; init; }
    /// <summary>妖王（头目）击杀数。</summary>
    public double BossKills { get; init; }
    /// <summary>讨伐失败与斗法败北合计次数。</summary>
    public double Deaths { get; init; }
    /// <summary>斗法胜场数。</summary>
    public double DuelWins { get; init; }
    /// <summary>斗法败北场数。</summary>
    public double DuelLosses { get; init; }
    /// <summary>本次入账的六维根骨维度；null 表示本批无根骨经验。</summary>
    public CoreAspectKind? RootAspect { get; init; }
    /// <summary>六维根骨经验入账量（伴随 RootAspect 携带）。</summary>
    public double RootExperience { get; init; }
    /// <summary>本次入账的功法 ID；null 表示本批无功法经验。</summary>
    public string? AbilityId { get; init; }
    /// <summary>功法参悟经验点数（伴随 AbilityId 携带）。</summary>
    public double AbilityExperience { get; init; }
    /// <summary>本次入账的技艺 ID；null 表示本批无技艺经验。</summary>
    public string? TechniqueId { get; init; }
    /// <summary>技艺经验点数（伴随 TechniqueId 携带）。</summary>
    public double TechniqueExperience { get; init; }
    /// <summary>本次触发的升级事件（境界/根骨/功法）；无升级为 null。</summary>
    public PlayerReportLevelUp? LevelUp { get; init; }
    /// <summary>获得物品的单一明细；批量增减走 ItemGains/ItemLosses。</summary>
    public ItemId? GainedItem { get; init; }
    /// <summary>单一获得明细的数量（伴随 GainedItem 携带）。</summary>
    public double GainedQuantity { get; init; }
    /// <summary>损失物品的单一明细；批量增减走 ItemGains/ItemLosses。</summary>
    public ItemId? LostItem { get; init; }
    /// <summary>单一损失明细的数量（伴随 LostItem 携带）。</summary>
    public double LostQuantity { get; init; }
    /// <summary>批量物品获得明细；与单一明细互斥使用，账本按源集合顺序累加。</summary>
    public IReadOnlyDictionary<ItemId, double>? ItemGains { get; init; }
    /// <summary>批量物品损失明细；与单一明细互斥使用，账本按源集合顺序累加。</summary>
    public IReadOnlyDictionary<ItemId, double>? ItemLosses { get; init; }
    /// <summary>斗法暂存区损失明细（押存物品按批次入对手账）。</summary>
    public IReadOnlyList<DuelStagedItem>? StagedItemLosses { get; init; }
    /// <summary>斗法缴获物品明细（对手暂存区物品）。</summary>
    public IReadOnlyList<DuelStagedItem>? DuelItemGains { get; init; }

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
        if (TechniqueId is { } techniqueId) report.AddTechniqueExp(techniqueId, TechniqueExperience);
        if (LevelUp is { } levelUp) report.LevelUps.Add(levelUp);
        if (GainedItem is { } gainedItem) report.AddItem(gainedItem, GainedQuantity);
        if (LostItem is { } lostItem) report.AddItemLost(lostItem, LostQuantity);
        if (ItemGains is { } gains)
            foreach (var (item, quantity) in gains) report.AddItem(item, quantity);
        if (ItemLosses is { } losses)
            foreach (var (item, quantity) in losses) report.AddItemLost(item, quantity);
        if (StagedItemLosses is { } stagedLosses)
        {
            foreach (var item in stagedLosses)
            {
                var itemId = new ItemId(item.ItemId);
                report.AddItemLost(itemId, item.Quantity);
                report.AddDuelItemLost(itemId, item.Quantity);
            }
        }
        if (DuelItemGains is { } duelGains)
        {
            foreach (var item in duelGains)
            {
                var itemId = new ItemId(item.ItemId);
                report.AddItem(itemId, item.Quantity);
                report.AddDuelItemGained(itemId, item.Quantity);
            }
        }
    }
}

/// <summary>运行时统计派发边界；无实体聚合直接记账，ECS 命令门面与推进上下文同步派发 tag。</summary>
public interface IPlayerStatsSink
{
    /// <summary>把一批已结算增量写入玩家战报账本；时间参数只用于账本归日，不触发任何结算。</summary>
    /// <param name="player">玩家状态视图（用于定位账本与脏标记）。</param>
    /// <param name="actionInstanceId">归属行动实例；null 表示非行动来源（如斗法结算）。</param>
    /// <param name="atUnixSeconds">入账时刻（Unix 秒），决定账本归日。</param>
    /// <param name="delta">已结算的统计增量。</param>
    void Record(IPlayerResourceContext player, Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta);
}
