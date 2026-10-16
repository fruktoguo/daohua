using System.Text.Json;

namespace Domain.Player;

/// <summary>六维根骨维度标识；功法配表 rootAttribute 字段按此枚举分配六维经验。</summary>
public enum CoreAspectKind : byte
{
    /// <summary>体魄</summary>
    Stamina = 0,
    /// <summary>经脉</summary>
    Meridians = 1,
    /// <summary>力道</summary>
    Attack = 2,
    /// <summary>根骨</summary>
    Bone = 3,
    /// <summary>身法</summary>
    Agility = 4,
    /// <summary>神识</summary>
    Consciousness = 5
}

/// <summary>
/// 六维经验→等级曲线：等级（点数）由 <see cref="Domain.GameData.RealmProgressionCatalog"/> 派生的
/// 3×境界级数点成本表导出（总纲 §3.2.2，一级境界对应三点、相邻修为条线性插值），0 级起算；
/// 与前端 ROOT_CUMULATIVE_EXP 同源。配表缺失时按 0 级计。
/// </summary>
public static class RootAttributeCurve
{
    /// <summary>经验换算六维等级（0~3×境界表长度点；0 经验即 0 级，无基础等级）。</summary>
    public static double LevelFor(double exp, Domain.GameData.RealmProgressionCatalog? catalog)
        => catalog?.RootLevelFor(exp) ?? 0;
    /// <summary>达到指定等级所需的累计经验（查表）。</summary>
    public static double ExpForLevel(double level, Domain.GameData.RealmProgressionCatalog? catalog)
        => catalog?.RootCumulativeExpFor((int)Math.Max(0, Math.Floor(level))) ?? 0;
    /// <summary>level → level+1 点所需经验；已满点返回 0。</summary>
    public static double ExpToNextFor(double level, Domain.GameData.RealmProgressionCatalog? catalog)
        => catalog?.RootExpToNextFor((int)Math.Max(0, Math.Floor(level))) ?? 0;
}

/// <summary>六维等级视图：由经验经境界修为表派生点成本表结算，不持久化。</summary>
public readonly record struct RootAttributeLevels(
    double Stamina, double Meridians, double Attack, double Bone, double Agility, double Consciousness)
{
    /// <summary>六维等级总和，斗法实力匹配的当前口径。</summary>
    public double Total => Stamina + Meridians + Attack + Bone + Agility + Consciousness;
}

/// <summary>
/// 玩家六维根骨：持久化经验值，等级由 <see cref="Levels"/> 经点成本表导出。
/// 初始 0 级（0 经验）：六维加成从零开始，不提供任何出生白送属性。
/// </summary>
public sealed record PlayerCoreAttributes(
    double StaminaExp = 0,
    double MeridiansExp = 0,
    double AttackExp = 0,
    double BoneExp = 0,
    double AgilityExp = 0,
    double ConsciousnessExp = 0)
{
    /// <summary>经验结算为六维等级视图；配表缺失时六维全 0 级。</summary>
    public RootAttributeLevels Levels(Domain.GameData.RealmProgressionCatalog? catalog) => new(
        RootAttributeCurve.LevelFor(StaminaExp, catalog),
        RootAttributeCurve.LevelFor(MeridiansExp, catalog),
        RootAttributeCurve.LevelFor(AttackExp, catalog),
        RootAttributeCurve.LevelFor(BoneExp, catalog),
        RootAttributeCurve.LevelFor(AgilityExp, catalog),
        RootAttributeCurve.LevelFor(ConsciousnessExp, catalog));

    /// <summary>按维度读取累计经验（升级判定用）。</summary>
    public double ExpFor(CoreAspectKind aspect) => aspect switch
    {
        CoreAspectKind.Stamina => StaminaExp,
        CoreAspectKind.Meridians => MeridiansExp,
        CoreAspectKind.Attack => AttackExp,
        CoreAspectKind.Bone => BoneExp,
        CoreAspectKind.Agility => AgilityExp,
        _ => ConsciousnessExp
    };

    /// <summary>返回指定维度累加 amount 后的新记录（值语义不可变更新）。</summary>
    public PlayerCoreAttributes WithAddedExp(CoreAspectKind aspect, double amount) => aspect switch
    {
        CoreAspectKind.Stamina => this with { StaminaExp = StaminaExp + amount },
        CoreAspectKind.Meridians => this with { MeridiansExp = MeridiansExp + amount },
        CoreAspectKind.Attack => this with { AttackExp = AttackExp + amount },
        CoreAspectKind.Bone => this with { BoneExp = BoneExp + amount },
        CoreAspectKind.Agility => this with { AgilityExp = AgilityExp + amount },
        _ => this with { ConsciousnessExp = ConsciousnessExp + amount }
    };
}

/// <summary>技能经验→等级曲线：独立立方曲线 floor(cbrt(exp/100))+1，保持 1 基（习得即 1 级），技能书/残卷按点数注入经验。</summary>
public static class AbilityExpCurve
{
    /// <summary>经验换算技能等级（1~127 级）。</summary>
    public static double LevelFor(double exp)
        => !double.IsFinite(exp) || exp <= 0 ? 1 : Math.Min(127, Math.Floor(Math.Cbrt(exp / 100d)) + 1);
}

/// <summary>功法/神通装配槽位：技能 ID（可空）与参悟等级；自定义触发条件按功法 ID 挂在 AbilityTriggers，不随槽位走。</summary>
public sealed record PlayerAbilitySlot(string? AbilityId, double Level);
/// <summary>装备槽位记录：配表装备 ID 与强化等级。</summary>
public sealed record PlayerEquipmentSlot(string ItemId, double EnhancementLevel);
/// <summary>装配分段的可持久化文档形态。</summary>
public sealed record PlayerLoadoutDocument(
    PlayerCoreAttributes CoreAttributes,
    IReadOnlyList<PlayerAbilitySlot> AbilitySlots,
    IReadOnlyList<PlayerEquipmentSlot> Equipment,
    IReadOnlyDictionary<string, double>? AbilityExperiences = null,
    IReadOnlyDictionary<string, double>? TechniqueExperiences = null,
    IReadOnlyDictionary<string, JsonElement[]>? AbilityTriggers = null);

/// <summary>装配分段运行时状态（六维根骨、功法槽位、装备与技能经验）。</summary>
public sealed partial class PlayerLoadoutState
{
    private PlayerCoreAttributes _coreAttributes = new();
    private PlayerLoadoutDocument? _exportedDocument;
    private long _exportedRevision = -1;

    /// <summary>六维根骨经验；等级由境界修为表派生点成本表导出；写入递增版本号（属性解析与装配导出共用输入）。</summary>
    public PlayerCoreAttributes CoreAttributes
    {
        get => _coreAttributes;
        internal set
        {
            if (_coreAttributes == value) return;
            _coreAttributes = value;
            CoreAttributesVersion = checked(CoreAttributesVersion + 1);
        }
    }

    /// <summary>六维经验修订号：属性解析与装配导出的共同输入，写入即单调递增。</summary>
    internal long CoreAttributesVersion { get; private set; }

    /// <summary>功法装配槽位（至多 4 槽，槽位可空）；写入递增版本号供导出缓存失效检测。</summary>
    public VersionedList<PlayerAbilitySlot> AbilitySlots { get; } = new();
    /// <summary>已穿戴装备槽位列表；写入递增版本号（属性解析与装配导出共用输入）。</summary>
    public VersionedList<PlayerEquipmentSlot> Equipment { get; } = new();
    /// <summary>已习得技能的累计经验，键为 ability ID；等级由 AbilityExpCurve 导出；写入递增版本号。</summary>
    public VersionedDictionary<AbilityId, double> AbilityExperiences { get; } = new();
    /// <summary>七技艺的累计经验，键为 TechniqueId；等级经境界修为曲线逐段推进导出；写入递增版本号。</summary>
    public VersionedDictionary<string, double> TechniqueExperiences { get; } = new();
    /// <summary>功法自定义触发条件，键为 ability ID 字符串；跟随功法而非装配槽位，写入递增版本号。</summary>
    public VersionedDictionary<string, JsonElement[]> AbilityTriggers { get; } = new();

    /// <summary>装配文档导出修订号：槽位/装备/技能经验/技艺经验/触发条件/六维任一写入严格增大。</summary>
    internal long ExportRevision =>
        AbilitySlots.Version + Equipment.Version + AbilityExperiences.Version + TechniqueExperiences.Version
        + AbilityTriggers.Version + CoreAttributesVersion;

    /// <summary>导出可持久化装配文档；触发条件 JSON 做防御性深拷贝。
    /// 修订号未变时直接复用上轮文档（位级一致），仅在装配变更时才重新克隆触发器与字典。</summary>
    /// <returns>装配分段快照文档。</returns>
    public PlayerLoadoutDocument Export()
    {
        if (_exportedDocument is { } cached && _exportedRevision == ExportRevision) return cached;
        var document = new PlayerLoadoutDocument(CoreAttributes,
            AbilitySlots.ToArray(),
            Equipment.ToArray(),
            AbilityExperiences.Count == 0 ? null : AbilityExperiences.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
            TechniqueExperiences.Count == 0 ? null : TechniqueExperiences.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            AbilityTriggers.Count == 0 ? null : AbilityTriggers.ToDictionary(
                pair => pair.Key, pair => pair.Value.Select(trigger => trigger.Clone()).ToArray(), StringComparer.Ordinal));
        _exportedDocument = document;
        _exportedRevision = ExportRevision;
        return document;
    }

    /// <summary>从持久化文档恢复装配状态；结构与数值不满足不变量时抛出 InvalidArgument。</summary>
    /// <param name="document">装配快照文档。</param>
    internal void Restore(PlayerLoadoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var core = document.CoreAttributes;
        if (core is null || document.AbilitySlots is null || document.Equipment is null
            || new[] { core.StaminaExp, core.MeridiansExp, core.AttackExp, core.BoneExp, core.AgilityExp, core.ConsciousnessExp }
                .Any(value => !double.IsFinite(value) || value < 0)
            || document.AbilitySlots.Any(slot => slot is null || !double.IsFinite(slot.Level) || slot.Level < 1)
            || document.Equipment.Any(item => item is null || string.IsNullOrWhiteSpace(item.ItemId)
                || !double.IsFinite(item.EnhancementLevel) || item.EnhancementLevel < 0)
            || (document.AbilityExperiences?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || !double.IsFinite(pair.Value) || pair.Value < 0) ?? false)
            || (document.TechniqueExperiences?.Any(pair => !Domain.GameData.TechniqueCatalog.IsKnown(pair.Key)
                || !double.IsFinite(pair.Value) || pair.Value < 0) ?? false)
            || (document.AbilityTriggers?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || pair.Value.Any(trigger => trigger.ValueKind != JsonValueKind.Object)) ?? false))
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配快照无效。");
        CoreAttributes = core;
        AbilitySlots.Clear();
        AbilitySlots.AddRange(document.AbilitySlots);
        Equipment.Clear();
        Equipment.AddRange(document.Equipment);
        // 功法参悟经验与技艺经验同语义：只在文档显式携带时覆盖；
        // 槽位装配替换（ReplaceCombatSlots）构造的局部文档不抹除既有累计。
        if (document.AbilityExperiences is not null)
        {
            AbilityExperiences.Clear();
            foreach (var pair in document.AbilityExperiences)
                AbilityExperiences[new AbilityId(pair.Key)] = pair.Value;
        }
        // 技艺经验只在文档显式携带时覆盖：槽位装配替换（ReplaceCombatSlots）构造的局部文档不抹除既有技艺累计。
        if (document.TechniqueExperiences is not null)
        {
            TechniqueExperiences.Clear();
            foreach (var pair in document.TechniqueExperiences)
                TechniqueExperiences[pair.Key] = pair.Value;
        }
        // 触发条件是全量配置项而非累计值：无论文档是否显式携带都覆盖（缺省即清空）。
        AbilityTriggers.Clear();
        if (document.AbilityTriggers is not null)
        {
            foreach (var pair in document.AbilityTriggers)
                AbilityTriggers[pair.Key] = pair.Value.Select(trigger => trigger.Clone()).ToArray();
        }
    }

    /// <summary>分段回滚检查点专用：把装配各输入源修订号写回备份值；调用方须先把内容恢复到与版本一致的状态。</summary>
    internal void RestoreRevisionCounters(long coreAttributesVersion, long abilitySlotsVersion, long equipmentVersion,
        long abilityExperiencesVersion, long techniqueExperiencesVersion = 0, long abilityTriggersVersion = 0)
    {
        CoreAttributesVersion = coreAttributesVersion;
        AbilitySlots.RestoreVersion(abilitySlotsVersion);
        Equipment.RestoreVersion(equipmentVersion);
        AbilityExperiences.RestoreVersion(abilityExperiencesVersion);
        TechniqueExperiences.RestoreVersion(techniqueExperiencesVersion);
        AbilityTriggers.RestoreVersion(abilityTriggersVersion);
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
            Loadout.AbilitySlots.AddRange(Enumerable.Range(0, 4).Select(_ => new PlayerAbilitySlot(null, 1)));
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
        if (levelAfter > levelBefore)
        {
            for (var i = 0; i < Loadout.AbilitySlots.Count; i++)
            {
                var slot = Loadout.AbilitySlots[i];
                if (slot.AbilityId == abilityId.Value && slot.Level < levelAfter)
                    Loadout.AbilitySlots[i] = new PlayerAbilitySlot(slot.AbilityId, levelAfter);
            }
        }
        // 功法经验与参悟升级计入行动统计账本（ability 级 LevelUp，TargetId 为功法 ID）。
        var headId = _actions.Count > 0 ? _actions[0].Id : (Guid?)null;
        var abilityIdValue = abilityId.Value;
        RecordStats(headId, LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d, new PlayerStatsDelta
        {
            AbilityId = abilityIdValue,
            AbilityExperience = experience,
            LevelUp = levelAfter > levelBefore
                ? new PlayerReportLevelUp("ability", abilityIdValue, levelBefore, levelAfter) : null
        });
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>技艺当前等级：由累计经验经境界修为曲线逐段推进导出；未接触按 0 级计。</summary>
    public double TechniqueLevelFor(string techniqueId, Domain.GameData.GameDataCatalog? gameData)
        => Domain.GameData.TechniqueCatalog.LevelFor(
            Loadout.TechniqueExperiences.TryGetValue(techniqueId, out var exp) ? exp : 0,
            gameData?.RealmProgression);

    /// <summary>
    /// 授予技艺累计经验并同步等级跃迁账本（kind "technique"）；
    /// 经验量为当次行动的基础耗时（强化行动按当次实际基础耗时），与既有收益通道同一记账口径。
    /// </summary>
    /// <param name="techniqueId">技艺标识（七技艺之一）。</param>
    /// <param name="experience">本批次入账的累计经验量。</param>
    /// <param name="gameData">配表（提供技艺经验曲线所复用的境界修为段表；缺失时经验照记但等级停在 0）。</param>
    public void GrantTechniqueExperience(string techniqueId, double experience, Domain.GameData.GameDataCatalog? gameData)
    {
        if (!Domain.GameData.TechniqueCatalog.IsKnown(techniqueId))
            throw new DomainException(DomainErrorCode.InvalidArgument, "技艺标识无效。");
        if (!double.IsFinite(experience) || experience <= 0 || experience > 1_000_000)
            throw new DomainException(DomainErrorCode.InvalidArgument, "技艺经验无效。");
        var progression = gameData?.RealmProgression;
        var previousExp = Loadout.TechniqueExperiences.TryGetValue(techniqueId, out var stored) ? stored : 0;
        var nextExp = previousExp + experience;
        Loadout.TechniqueExperiences[techniqueId] = nextExp;
        var levelBefore = Domain.GameData.TechniqueCatalog.LevelFor(previousExp, progression);
        var levelAfter = Domain.GameData.TechniqueCatalog.LevelFor(nextExp, progression);
        // 技艺经验与升段计入行动统计账本（technique 级 LevelUp，TargetId 为技艺 ID）。
        var headId = _actions.Count > 0 ? _actions[0].Id : (Guid?)null;
        RecordStats(headId, LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d, new PlayerStatsDelta
        {
            TechniqueId = techniqueId,
            TechniqueExperience = experience,
            LevelUp = levelAfter > levelBefore
                ? new PlayerReportLevelUp("technique", techniqueId, levelBefore, levelAfter) : null
        });
        Touch(PlayerSectionKind.Loadout);
    }

    /// <summary>装配不授予功法或属性成长，只修改已授权的槽位配置与功法触发条件。</summary>
    /// <param name="abilities">权威功法槽位列表（至多 4 槽）。</param>
    /// <param name="equipment">权威装备槽位列表。</param>
    /// <param name="abilityTriggers">功法触发条件全量 map（键为已习得功法 ID）；null 表示清空全部自定义条件。</param>
    public void ReplaceCombatSlots(IReadOnlyList<PlayerAbilitySlot> abilities, IReadOnlyList<PlayerEquipmentSlot> equipment,
        IReadOnlyDictionary<string, JsonElement[]>? abilityTriggers = null)
    {
        Loadout.Restore(new(Loadout.CoreAttributes, abilities, equipment, AbilityTriggers: abilityTriggers));
        Loadout.EquippedItemIds.Clear();
        Loadout.EquippedItemIds.AddRange(equipment.Select(item => new ItemId(item.ItemId)));
        Touch(PlayerSectionKind.Loadout);
        Touch(PlayerSectionKind.Combat);
    }
}
