using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Protocol.Simulator;

namespace Server;

/// <summary>
/// 战斗预演只读档案与环境快照映射：把权威玩家状态与节点建筑投影导出为纯白名单 DTO，
/// 不暴露或共享任何服务端可变状态集合。
/// </summary>
public static class SimulatorProfileCapture
{
    /// <summary>从权威玩家状态与普通资产余额捕获战斗白名单档案；不共享真实可变集合。</summary>
    public static SimulatorProfileDto Capture(
        PlayerAggregateState state,
        IEnumerable<SimulatorAssetDto> assets,
        GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(catalog);
        var combat = state.Combat;
        var loadout = state.Loadout;
        var progression = state.Progression;
        var linggen = progression.HeavenGateConfirmed;
        return new SimulatorProfileDto(
            SimulatorContractVersions.Profile,
            catalog.Fingerprint,
            state.PlayerId.Value.ToString("D"),
            state.DisplayName,
            state.Settings.Avatar,
            state.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d,
            progression.RealmLevel,
            progression.Experience,
            progression.Foundation,
            progression.CombatExperience,
            progression.LifetimeExperience,
            progression.AgeSeconds,
            progression.CompletedActions,
            progression.TraitIds?.ToArray(),
            progression.UnlockedTraits?.Select(trait => new SimulatorTraitProgressDto(trait.Id, trait.Level, trait.Exp)).ToArray(),
            linggen is { } confirmed ? new SimulatorLinggenDto(confirmed.Metal, confirmed.Wood, confirmed.Water, confirmed.Fire, confirmed.Earth) : null,
            progression.HeavenGateEntered,
            new SimulatorCoreAttributesDto(
                loadout.CoreAttributes.StaminaExp, loadout.CoreAttributes.MeridiansExp, loadout.CoreAttributes.AttackExp,
                loadout.CoreAttributes.BoneExp, loadout.CoreAttributes.AgilityExp, loadout.CoreAttributes.ConsciousnessExp),
            loadout.AbilitySlots.Select(slot => new SimulatorAbilitySlotDto(slot.AbilityId, slot.Level)).ToArray(),
            loadout.AbilityExperiences?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            loadout.Equipment.Select(slot => new SimulatorEquipmentSlotDto(slot.ItemId, slot.EnhancementLevel)).ToArray(),
            loadout.TechniqueExperiences?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            loadout.AbilityTriggers?.ToDictionary(pair => pair.Key, pair => pair.Value.Select(trigger => trigger.Clone()).ToArray(), StringComparer.Ordinal),
            loadout.ConsumableTriggers?.ToDictionary(pair => pair.Key, pair => pair.Value.Select(trigger => trigger.Clone()).ToArray(), StringComparer.Ordinal),
            combat.Hitpoints,
            combat.MaxHitpoints,
            combat.Manapoints,
            combat.MaxManapoints,
            combat.RegenCarrySeconds,
            ToDto(combat.Skills),
            combat.Buffs.Buffs.Select(ToDto).ToArray(),
            (state.Buffs ?? []).Select(ToDto).ToArray(),
            (state.Settings.AutoUseSlots ?? [])
                .Where(slot => slot.ActionType == AutoUseBar.Combat)
                .Select(ToDto).ToArray(),
            assets.ToArray(),
            (int)state.Settings.RespawnPolicy,
            state.Settings.RespawnTargetPlayerId?.ToString("D"));
    }

    public static SimulatorCombatSkillsDto ToDto(PlayerCombatSkillState state)
        => new(state.Cooldowns.Select(cooldown => new SimulatorSkillCooldownDto(cooldown.AbilityId, cooldown.RemainingSeconds)).ToArray(),
            state.CastingAbilityId, state.CastRemainingSeconds, state.CastCount);

    public static SimulatorCombatBuffCarryDto ToDto(CombatBuffCarry carry)
        => new(carry.BuffId, carry.RemainingSeconds, carry.Stacks, carry.Name, carry.MissingHitpointsPerStack,
            carry.Effects?.Select(ToDto).ToArray(), carry.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static SimulatorActiveBuffDto ToDto(ActiveBuff buff)
        => new(buff.BuffId, buff.ExpiresAtUnixSeconds, buff.Stacks, buff.Name, buff.SourceItemId,
            buff.Effects?.Select(ToDto).ToArray(), buff.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static SimulatorBuffEffectDto ToDto(BuffEffect effect)
        => new((int)effect.Channel, effect.Value);

    public static SimulatorAutoUseSlotDto ToDto(AutoUseSlotState slot)
        => new(slot.ActionType, slot.SlotIndex, slot.ItemId, slot.Quantity, slot.Enabled,
            slot.RemainingDurationSeconds, slot.CooldownUntilUnixSeconds, slot.Group);

    public static SimulatorBuildingEffectsDto ToDto(BuildingEffectSet effects)
        => new(effects.ExplorationSpeedRatio, effects.ExplorationOutputRatio, effects.AlchemySpeedRatio,
            effects.AlchemyOutputRatio, effects.CultivationOutputRatio, effects.CombatHitpointRegen,
            effects.CombatDamageReductionRatio, effects.MonsterWeakenRatio, effects.GatheringSpeedRatio,
            effects.MiningSpeedRatio, effects.ForgingSpeedRatio, effects.TalismanSpeedRatio,
            effects.FormationSpeedRatio, effects.EnhancementSpeedRatio, effects.CombatManapointRegen);

    public static SimulatorNodeBuildingDto ToDto(NodeBuildingState state)
        => new(state.BuildingId, state.Level, state.TargetLevel, state.StartedAtUnixSeconds,
            state.ContributedMaterials?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static SimulatorNodeBuildingUsageDto ToDto(NodeBuildingUsage usage)
        => new(usage.BuildingId, usage.ActiveUsers, usage.Capacity, usage.Efficiency);
}

/// <summary>
/// 模拟器 JSON 线协议：camelCase + 严格反序列化——数字必须为 JSON 数字字面量、
/// 属性名大小写敏感、构造参数与全部 DTO 属性必须出现（可为 null，不允许缺键静默补默认）。
/// </summary>
public static class SimulatorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static info =>
                {
                    if (info.Kind != JsonTypeInfoKind.Object
                        || !string.Equals(info.Type.Namespace, "Protocol.Simulator", StringComparison.Ordinal))
                        return;
                    foreach (var property in info.Properties)
                        property.IsRequired = true;
                }
            }
        }
    };
}
