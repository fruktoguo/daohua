using System.Text.Json;

namespace Domain.Player;

public sealed record PlayerCoreAttributes(
    double StaminaLevel = 10, double IntelligenceLevel = 10, double AttackLevel = 10,
    double DefenseLevel = 10, double SwordsmanshipLevel = 10, double TalismanLevel = 10);

public sealed record PlayerAbilitySlot(string? AbilityId, double Level, IReadOnlyList<JsonElement> Triggers);
public sealed record PlayerEquipmentSlot(string ItemId, double EnhancementLevel);
public sealed record PlayerLoadoutDocument(
    PlayerCoreAttributes CoreAttributes,
    IReadOnlyList<PlayerAbilitySlot> AbilitySlots,
    IReadOnlyList<PlayerEquipmentSlot> Equipment);

public sealed partial class PlayerLoadoutState
{
    public PlayerCoreAttributes CoreAttributes { get; internal set; } = new();
    public List<PlayerAbilitySlot> AbilitySlots { get; } = [];
    public List<PlayerEquipmentSlot> Equipment { get; } = [];

    internal PlayerLoadoutDocument Export() => new(CoreAttributes,
        AbilitySlots.Select(slot => slot with { Triggers = slot.Triggers.Select(trigger => trigger.Clone()).ToArray() }).ToArray(),
        Equipment.ToArray());

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

public sealed partial class PlayerAggregate
{
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
