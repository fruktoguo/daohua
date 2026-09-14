using System.Text.Json;

namespace Domain.Player;

/// <summary>玩家Core属性 的领域定义。</summary>
public sealed record PlayerCoreAttributes(
    double StaminaLevel = 10, double IntelligenceLevel = 10, double AttackLevel = 10,
    double DefenseLevel = 10, double SwordsmanshipLevel = 10, double TalismanLevel = 10);

/// <summary>玩家功法/神通槽位 的领域定义。</summary>
public sealed record PlayerAbilitySlot(string? AbilityId, double Level, IReadOnlyList<JsonElement> Triggers);
/// <summary>玩家Equipment槽位 的领域定义。</summary>
public sealed record PlayerEquipmentSlot(string ItemId, double EnhancementLevel);
/// <summary>玩家装备配置文档 的领域定义。</summary>
public sealed record PlayerLoadoutDocument(
    PlayerCoreAttributes CoreAttributes,
    IReadOnlyList<PlayerAbilitySlot> AbilitySlots,
    IReadOnlyList<PlayerEquipmentSlot> Equipment);

/// <summary>玩家装备配置状态 的领域定义。</summary>
public sealed partial class PlayerLoadoutState
{
    /// <summary>获取或设置 Core属性 的值。</summary>
    public PlayerCoreAttributes CoreAttributes { get; internal set; } = new();
    public List<PlayerAbilitySlot> AbilitySlots { get; } = [];
    public List<PlayerEquipmentSlot> Equipment { get; } = [];

    /// <summary>
    /// 执行 Export 操作。
    /// </summary>
    /// <returns>返回处理结果。</returns>
    internal PlayerLoadoutDocument Export() => new(CoreAttributes,
        AbilitySlots.Select(slot => slot with { Triggers = slot.Triggers.Select(trigger => trigger.Clone()).ToArray() }).ToArray(),
        Equipment.ToArray());

    /// <summary>
    /// 执行 恢复 操作。
    /// </summary>
    /// <param name="document">document 参数。</param>
    internal void Restore(PlayerLoadoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var core = document.CoreAttributes;
        if (core is null || document.AbilitySlots is null || document.Equipment is null
            || new[] { core.StaminaLevel, core.IntelligenceLevel, core.AttackLevel, core.DefenseLevel, core.SwordsmanshipLevel, core.TalismanLevel }
                .Any(value => !double.IsFinite(value) || value < 0)
            || document.AbilitySlots.Any(slot => slot is null || !double.IsFinite(slot.Level) || slot.Level < 1 || slot.Triggers is null
                || slot.Triggers.Any(trigger => trigger.ValueKind != JsonValueKind.Object))
            || document.Equipment.Any(item => item is null || string.IsNullOrWhiteSpace(item.ItemId)
                || !double.IsFinite(item.EnhancementLevel) || item.EnhancementLevel < 0))
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配快照无效。");
        CoreAttributes = core;
        AbilitySlots.Clear();
        AbilitySlots.AddRange(document.AbilitySlots.Select(slot => slot with { Triggers = slot.Triggers.Select(trigger => trigger.Clone()).ToArray() }));
        Equipment.Clear();
        Equipment.AddRange(document.Equipment);
    }
}

/// <summary>玩家聚合 的领域定义。</summary>
public sealed partial class PlayerAggregate
{
    /// <summary>
    /// 执行 习得功法/神通 操作。
    /// </summary>
    /// <param name="abilityId">abilityId 参数。</param>
    public void LearnAbility(AbilityId abilityId)
    {
        if (!abilityId.IsValid || Loadout.LearnedAbilities.Contains(abilityId))
            throw new DomainException(DomainErrorCode.InvalidArgument, "功法无效或已习得。");
        Loadout.LearnedAbilities.Add(abilityId);
        if (Loadout.AbilitySlots.Count == 0)
            Loadout.AbilitySlots.AddRange(Enumerable.Range(0, 4).Select(_ => new PlayerAbilitySlot(null, 1, [])));
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
