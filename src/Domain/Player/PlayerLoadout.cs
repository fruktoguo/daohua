using System.Text.Json;

namespace Domain.Player;

/// <summary>六维根骨维度标识；功法配表 rootAttribute 字段按此枚举分配六维经验。</summary>
public enum CoreAspectKind : byte
{
    /// <summary>体魄</summary>
    Stamina = 0,
    /// <summary>悟性</summary>
    Intelligence = 1,
    /// <summary>力道</summary>
    Attack = 2,
    /// <summary>防御</summary>
    Defense = 3,
    /// <summary>剑术</summary>
    Swordsmanship = 4,
    /// <summary>符术</summary>
    Talisman = 5
}

/// <summary>六维经验→等级曲线：Level = floor(cbrt(exp/100)) + 1，与前端 getCumulativeExpForLevel 反函数一致。</summary>
public static class RootAttributeCurve
{
    /// <summary>初始 10 级对应的经验锚点：100×(10-1)³。</summary>
    public const double BaseExp = 72_900;
    /// <summary>经验换算等级（1~127 级）。</summary>
    public static double LevelFor(double exp)
        => !double.IsFinite(exp) || exp <= 0 ? 1 : Math.Min(127, Math.Floor(Math.Cbrt(exp / 100d)) + 1);
    /// <summary>达到指定等级所需的累计经验。</summary>
    public static double ExpForLevel(double level)
        => 100d * Math.Pow(Math.Max(1, level) - 1, 3);
}

/// <summary>
/// 玩家六维根骨：持久化经验值，等级由经验曲线导出。
/// 初始经验 72900 对应 10 级，与历史默认等级保持数值等价。
/// </summary>
public sealed record PlayerCoreAttributes(
    double StaminaExp = RootAttributeCurve.BaseExp,
    double IntelligenceExp = RootAttributeCurve.BaseExp,
    double AttackExp = RootAttributeCurve.BaseExp,
    double DefenseExp = RootAttributeCurve.BaseExp,
    double SwordsmanshipExp = RootAttributeCurve.BaseExp,
    double TalismanExp = RootAttributeCurve.BaseExp)
{
    /// <summary>体魄等级（由经验导出）</summary>
    public double StaminaLevel => RootAttributeCurve.LevelFor(StaminaExp);
    /// <summary>悟性等级（由经验导出）</summary>
    public double IntelligenceLevel => RootAttributeCurve.LevelFor(IntelligenceExp);
    /// <summary>力道等级（由经验导出）</summary>
    public double AttackLevel => RootAttributeCurve.LevelFor(AttackExp);
    /// <summary>防御等级（由经验导出）</summary>
    public double DefenseLevel => RootAttributeCurve.LevelFor(DefenseExp);
    /// <summary>剑术等级（由经验导出）</summary>
    public double SwordsmanshipLevel => RootAttributeCurve.LevelFor(SwordsmanshipExp);
    /// <summary>符术等级（由经验导出）</summary>
    public double TalismanLevel => RootAttributeCurve.LevelFor(TalismanExp);
    /// <summary>六维等级总和，斗法实力匹配的当前口径。</summary>
    public double TotalLevel => StaminaLevel + IntelligenceLevel + AttackLevel + DefenseLevel + SwordsmanshipLevel + TalismanLevel;
}

/// <summary>技能经验→等级曲线：与六维共用立方曲线，技能书/残卷按点数注入经验。</summary>
public static class AbilityExpCurve
{
    /// <summary>经验换算技能等级（1~127 级）。</summary>
    public static double LevelFor(double exp) => RootAttributeCurve.LevelFor(exp);
}

/// <summary>功法/神通装配槽位：技能 ID（可空）、参悟等级与自定义触发条件。</summary>
public sealed record PlayerAbilitySlot(string? AbilityId, double Level, IReadOnlyList<JsonElement> Triggers);
/// <summary>装备槽位记录：配表装备 ID 与强化等级。</summary>
public sealed record PlayerEquipmentSlot(string ItemId, double EnhancementLevel);
/// <summary>装配分段的可持久化文档形态。</summary>
public sealed record PlayerLoadoutDocument(
    PlayerCoreAttributes CoreAttributes,
    IReadOnlyList<PlayerAbilitySlot> AbilitySlots,
    IReadOnlyList<PlayerEquipmentSlot> Equipment,
    IReadOnlyDictionary<string, double>? AbilityExperiences = null);

/// <summary>装配分段运行时状态（六维根骨、功法槽位、装备与技能经验）。</summary>
public sealed partial class PlayerLoadoutState
{
    /// <summary>六维根骨经验；等级由立方曲线导出。</summary>
    public PlayerCoreAttributes CoreAttributes { get; internal set; } = new();
    /// <summary>功法装配槽位（至多 4 槽，槽位可空）。</summary>
    public List<PlayerAbilitySlot> AbilitySlots { get; } = [];
    /// <summary>已穿戴装备槽位列表。</summary>
    public List<PlayerEquipmentSlot> Equipment { get; } = [];
    /// <summary>已习得技能的累计经验，键为 ability ID；等级由 AbilityExpCurve 导出。</summary>
    public Dictionary<AbilityId, double> AbilityExperiences { get; } = [];

    /// <summary>导出可持久化装配文档；触发条件 JSON 做防御性深拷贝。</summary>
    /// <returns>装配分段快照文档。</returns>
    internal PlayerLoadoutDocument Export() => new(CoreAttributes,
        AbilitySlots.Select(slot => slot with { Triggers = slot.Triggers.Select(trigger => trigger.Clone()).ToArray() }).ToArray(),
        Equipment.ToArray(),
        AbilityExperiences.Count == 0 ? null : AbilityExperiences.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal));

    /// <summary>从持久化文档恢复装配状态；结构与数值不满足不变量时抛出 InvalidArgument。</summary>
    /// <param name="document">装配快照文档。</param>
    internal void Restore(PlayerLoadoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var core = document.CoreAttributes;
        if (core is null || document.AbilitySlots is null || document.Equipment is null
            || new[] { core.StaminaExp, core.IntelligenceExp, core.AttackExp, core.DefenseExp, core.SwordsmanshipExp, core.TalismanExp }
                .Any(value => !double.IsFinite(value) || value < 0)
            || document.AbilitySlots.Any(slot => slot is null || !double.IsFinite(slot.Level) || slot.Level < 1 || slot.Triggers is null
                || slot.Triggers.Any(trigger => trigger.ValueKind != JsonValueKind.Object))
            || document.Equipment.Any(item => item is null || string.IsNullOrWhiteSpace(item.ItemId)
                || !double.IsFinite(item.EnhancementLevel) || item.EnhancementLevel < 0)
            || (document.AbilityExperiences?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || !double.IsFinite(pair.Value) || pair.Value < 0) ?? false))
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配快照无效。");
        CoreAttributes = core;
        AbilitySlots.Clear();
        AbilitySlots.AddRange(document.AbilitySlots.Select(slot => slot with { Triggers = slot.Triggers.Select(trigger => trigger.Clone()).ToArray() }));
        Equipment.Clear();
        Equipment.AddRange(document.Equipment);
        AbilityExperiences.Clear();
        if (document.AbilityExperiences is not null)
            foreach (var pair in document.AbilityExperiences)
                AbilityExperiences[new AbilityId(pair.Key)] = pair.Value;
    }
}

/// <summary>玩家聚合的功法习得与装配分部。</summary>
public sealed partial class PlayerAggregate
{
    /// <summary>习得功法并登记到装配；首次习得时自动初始化 4 个空装配槽位。</summary>
    /// <param name="abilityId">待习得的功法/神通 ID。</param>
    public void LearnAbility(AbilityId abilityId)
    {
        if (!abilityId.IsValid || Loadout.LearnedAbilities.Contains(abilityId))
            throw new DomainException(DomainErrorCode.InvalidArgument, "功法无效或已习得。");
        Loadout.LearnedAbilities.Add(abilityId);
        if (Loadout.AbilitySlots.Count == 0)
            Loadout.AbilitySlots.AddRange(Enumerable.Range(0, 4).Select(_ => new PlayerAbilitySlot(null, 1, [])));
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>已习得技能的参悟等级，由技能经验经立方曲线导出；未习得按 1 级计。</summary>
    public double AbilityLevelFor(AbilityId abilityId)
        => Loadout.AbilityExperiences.TryGetValue(abilityId, out var exp) ? AbilityExpCurve.LevelFor(exp) : 1;

    /// <summary>
    /// 为已习得技能注入参悟经验；秘籍复读与残卷研读的点数由宿主按配表决定。
    /// </summary>
    public void GrantAbilityExperience(AbilityId abilityId, double experience)
    {
        if (!abilityId.IsValid || !Loadout.LearnedAbilities.Contains(abilityId))
            throw new DomainException(DomainErrorCode.InvalidArgument, "尚未习得该功法，无法参悟。");
        if (!double.IsFinite(experience) || experience <= 0 || experience > 1_000_000)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技能经验无效。");
        var previousExp = Loadout.AbilityExperiences.TryGetValue(abilityId, out var stored) ? stored : 0;
        var nextExp = previousExp + experience;
        Loadout.AbilityExperiences[abilityId] = nextExp;
        var levelBefore = AbilityExpCurve.LevelFor(previousExp);
        var levelAfter = AbilityExpCurve.LevelFor(nextExp);
        // 功法经验与参悟升级计入行动统计账本（ability 级 LevelUp，TargetId 为功法 ID）。
        var headId = _actions.Count > 0 ? _actions[0].Id : (Guid?)null;
        var abilityIdValue = abilityId.Value;
        RecordStats(headId, LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d, report =>
        {
            report.AddAbilityExp(abilityIdValue, experience);
            if (levelAfter > levelBefore)
                report.LevelUps.Add(new PlayerReportLevelUp("ability", abilityIdValue, levelBefore, levelAfter));
        });
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>装配不授予功法或属性成长，只修改已授权的槽位配置。</summary>
    public void ReplaceCombatSlots(IReadOnlyList<PlayerAbilitySlot> abilities, IReadOnlyList<PlayerEquipmentSlot> equipment)
    {
        Loadout.Restore(new(Loadout.CoreAttributes, abilities, equipment));
        Loadout.EquippedItemIds.Clear();
        Loadout.EquippedItemIds.AddRange(equipment.Select(item => new ItemId(item.ItemId)));
        Touch(PlayerSectionKind.Loadout);
        Touch(PlayerSectionKind.Combat);
    }
}
