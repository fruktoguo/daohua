using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Protocol.Simulator;

namespace DaoHua.Simulator;

/// <summary>
/// 战斗预演档案 ⇄ 领域聚合状态的白名单映射。服务端只调用 <see cref="Capture"/>，
/// 模拟器内部用 <see cref="RestoreToState"/> 把档案在目标节点建筑投影下还原为一次新讨伐的输入快照。
/// </summary>
public static class SimulatorProfileMapper
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

    /// <summary>
    /// 把档案在目标节点建筑投影下还原为推演输入聚合状态：原地点投影被完全替换为目标快照，
    /// 行动队列/账号/斗法历史不进入还原面。
    /// </summary>
    /// <param name="memberIndex">成员序号；档案无源玩家 ID 时用于派生确定性占位 ID。</param>
    public static PlayerAggregateState RestoreToState(
        SimulatorProfileDto profile,
        int memberIndex,
        string targetNodeId,
        SimulatorEnvironmentNodeDto environment,
        GameDataCatalog catalog,
        double startAtUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(catalog);
        var playerGuid = SimulatorInputValidator.NormalizeMemberId(profile, memberIndex);
        if (profile.RespawnPolicy is < 0 or > 2)
            throw new DomainException(DomainErrorCode.InvalidArgument, "预演档案复活策略无效。");
        var respawnTarget = Guid.Empty;
        if (profile.RespawnTargetPlayerId is { } rawTarget
            && (!Guid.TryParse(rawTarget, out respawnTarget) || respawnTarget == Guid.Empty))
            throw new DomainException(DomainErrorCode.InvalidArgument, "预演档案复活目标无效。");
        var playerId = new PlayerId(playerGuid);
        var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(startAtUnixSeconds * 1000d));
        var world = new PlayerWorldStateDocument(
            targetNodeId,
            0,
            0,
            0,
            null,
            0,
            environment.Buildings.Select(ToDomain).ToArray(),
            ToDomain(environment.Effects),
            targetNodeId,
            environment.Usages.Select(ToDomain).ToArray());
        var combat = new PlayerCombatStateDocument(
            profile.Hitpoints,
            profile.MaxHitpoints,
            profile.Manapoints,
            profile.MaxManapoints,
            0,
            0,
            profile.RegenCarrySeconds)
        {
            Skills = ToDomain(profile.CombatSkills),
            Buffs = new PlayerCombatBuffState(profile.CombatBuffCarry.Select(ToDomain).ToArray())
        };
        var progression = new PlayerProgressionState(
            profile.RealmLevel,
            profile.Experience,
            profile.CompletedActions,
            0,
            0,
            0,
            0,
            0,
            0,
            profile.AgeSeconds,
            profile.Foundation,
            profile.CombatExperience,
            profile.LifetimeExperience,
            profile.TraitIds?.ToArray(),
            null,
            profile.UnlockedTraits?.Select(trait => new TraitProgressState(trait.Id, trait.Level, trait.Exp)).ToArray(),
            null,
            profile.ConfirmedLinggen is { } dto ? new LinggenValues(dto.Metal, dto.Wood, dto.Water, dto.Fire, dto.Earth) : null,
            profile.HeavenGateEntered);
        var abilitySlots = profile.AbilitySlots.Select(slot => new PlayerAbilitySlot(slot.AbilityId, slot.Level)).ToArray();
        var equipment = profile.Equipment.Select(slot => new PlayerEquipmentSlot(slot.ItemId, slot.EnhancementLevel)).ToArray();
        var learned = new List<AbilityId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in abilitySlots)
            if (slot.AbilityId is { } abilityId && seen.Add(abilityId)) learned.Add(new AbilityId(abilityId));
        if (profile.AbilityExperiences is not null)
            foreach (var abilityId in profile.AbilityExperiences.Keys)
                if (seen.Add(abilityId)) learned.Add(new AbilityId(abilityId));
        var loadout = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(
                profile.CoreAttributes.StaminaExp, profile.CoreAttributes.MeridiansExp, profile.CoreAttributes.AttackExp,
                profile.CoreAttributes.BoneExp, profile.CoreAttributes.AgilityExp, profile.CoreAttributes.ConsciousnessExp),
            abilitySlots,
            equipment,
            profile.AbilityExperiences?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            profile.TechniqueExperiences?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            profile.AbilityTriggers?.ToDictionary(pair => pair.Key, pair => pair.Value.Select(trigger => trigger.Clone()).ToArray(), StringComparer.Ordinal),
            profile.ConsumableTriggers?.ToDictionary(pair => pair.Key, pair => pair.Value.Select(trigger => trigger.Clone()).ToArray(), StringComparer.Ordinal));
        var settings = new PlayerSettingsStateDocument(
            "zh-CN",
            false,
            false,
            false,
            profile.CombatAutoUseSlots.Select(slot => ToDomain(slot, profile.CapturedAtUnixSeconds, startAtUnixSeconds)).ToArray(),
            0,
            false,
            0,
            0,
            string.IsNullOrWhiteSpace(profile.Avatar) ? "player" : profile.Avatar,
            null,
            (CombatRespawnPolicy)profile.RespawnPolicy,
            respawnTarget != Guid.Empty ? respawnTarget : null);
        var restored = new PlayerAggregateState(
            playerId,
            new AccountId(playerGuid),
            string.IsNullOrWhiteSpace(profile.DisplayName) ? playerGuid.ToString("N")[..8] : profile.DisplayName,
            startAt,
            startAt,
            PresenceState.Offline,
            null,
            1,
            catalog.Fingerprint,
            progression,
            world,
            combat,
            profile.Buffs.Select(buff => ToDomain(buff, profile.CapturedAtUnixSeconds, startAtUnixSeconds)).ToArray(),
            [],
            learned,
            settings,
            [],
            loadout,
            new PlayerDuelStateDocument(0, 0, null, [], null, []),
            equipment.Select(slot => new ItemId(slot.ItemId)).ToArray());
        // 空行动队列副本执行真实资源边界准备态：清理过期增益、同步库存耐久增益、应用资源属性并夹取资源；
        // ActivateInstantConsumables 因 Actions 为空立即返回，不耗药、不推进时钟。
        var player = PlayerAggregate.Restore(restored);
        SimulationEngine.PrepareResourceBoundary(player, catalog, BuildWallet(playerId, profile), startAt, new ActionMutationBatch());
        return player.ExportState();
    }

    /// <summary>按档案普通钱包构建领域钱包（全部余额交给引擎自行过滤消耗品）。</summary>
    public static AssetWallet BuildWallet(PlayerId playerId, SimulatorProfileDto profile)
    {
        var wallet = new AssetWallet(playerId);
        wallet.Restore(profile.Assets.Select(asset =>
            new AssetBalance(playerId, new AssetKey(new ItemId(asset.ItemId), asset.Variant ?? string.Empty), asset.Quantity, 0)));
        return wallet;
    }

    /// <summary>按档案普通钱包生成会话工作钱包余额行（逐成员独立）。</summary>
    public static IReadOnlyList<AssetBalance> BuildWorkingWallet(PlayerId playerId, SimulatorProfileDto profile)
        => profile.Assets.Select(asset =>
            new AssetBalance(playerId, new AssetKey(new ItemId(asset.ItemId), asset.Variant ?? string.Empty), asset.Quantity, 0)).ToArray();

    public static SimulatorCombatSkillsDto ToDto(PlayerCombatSkillState state)
        => new(state.Cooldowns.Select(cooldown => new SimulatorSkillCooldownDto(cooldown.AbilityId, cooldown.RemainingSeconds)).ToArray(),
            state.CastingAbilityId, state.CastRemainingSeconds, state.CastCount);

    public static PlayerCombatSkillState ToDomain(SimulatorCombatSkillsDto dto)
        => new(dto.Cooldowns.Select(cooldown => new CombatSkillCooldown(cooldown.AbilityId, cooldown.RemainingSeconds)).ToArray(),
            dto.CastingAbilityId, dto.CastRemainingSeconds, dto.CastCount);

    public static SimulatorCombatBuffCarryDto ToDto(CombatBuffCarry carry)
        => new(carry.BuffId, carry.RemainingSeconds, carry.Stacks, carry.Name, carry.MissingHitpointsPerStack,
            carry.Effects?.Select(ToDto).ToArray(), carry.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static CombatBuffCarry ToDomain(SimulatorCombatBuffCarryDto dto)
        => new(dto.BuffId, dto.RemainingSeconds, dto.Stacks, dto.Name, dto.MissingHitpointsPerStack,
            dto.Effects?.Select(ToDomain).ToArray(), dto.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static SimulatorActiveBuffDto ToDto(ActiveBuff buff)
        => new(buff.BuffId, buff.ExpiresAtUnixSeconds, buff.Stacks, buff.Name, buff.SourceItemId,
            buff.Effects?.Select(ToDto).ToArray(), buff.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static ActiveBuff ToDomain(SimulatorActiveBuffDto dto)
        => ToDomain(dto, dto.ExpiresAtUnixSeconds, dto.ExpiresAtUnixSeconds);

    public static ActiveBuff ToDomain(SimulatorActiveBuffDto dto, double sourceAtUnixSeconds, double targetAtUnixSeconds)
    {
        var remaining = dto.ExpiresAtUnixSeconds - sourceAtUnixSeconds;
        return new ActiveBuff(dto.BuffId, targetAtUnixSeconds + Math.Max(0, remaining), dto.Stacks, dto.Name,
            dto.SourceItemId,
            dto.Effects?.Select(ToDomain).ToArray(),
            dto.StatRatios?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public static SimulatorBuffEffectDto ToDto(BuffEffect effect)
        => new((int)effect.Channel, effect.Value);

    public static BuffEffect ToDomain(SimulatorBuffEffectDto dto)
        => new((BuffEffectChannel)dto.Channel, dto.Value);

    public static SimulatorAutoUseSlotDto ToDto(AutoUseSlotState slot)
        => new(slot.ActionType, slot.SlotIndex, slot.ItemId, slot.Quantity, slot.Enabled,
            slot.RemainingDurationSeconds, slot.CooldownUntilUnixSeconds, slot.Group);

    public static AutoUseSlotState ToDomain(SimulatorAutoUseSlotDto dto)
        => ToDomain(dto, dto.CooldownUntilUnixSeconds, dto.CooldownUntilUnixSeconds);

    public static AutoUseSlotState ToDomain(SimulatorAutoUseSlotDto dto, double sourceAtUnixSeconds, double targetAtUnixSeconds)
    {
        var remainingCooldown = dto.CooldownUntilUnixSeconds - sourceAtUnixSeconds;
        return new AutoUseSlotState(dto.ActionType, dto.SlotIndex, dto.ItemId, dto.Quantity, dto.Enabled,
            dto.RemainingDurationSeconds, targetAtUnixSeconds + Math.Max(0, remainingCooldown), dto.Group);
    }

    public static SimulatorBuildingEffectsDto ToDto(BuildingEffectSet effects)
        => new(effects.ExplorationSpeedRatio, effects.ExplorationOutputRatio, effects.AlchemySpeedRatio,
            effects.AlchemyOutputRatio, effects.CultivationOutputRatio, effects.CombatHitpointRegen,
            effects.CombatDamageReductionRatio, effects.MonsterWeakenRatio, effects.GatheringSpeedRatio,
            effects.MiningSpeedRatio, effects.ForgingSpeedRatio, effects.TalismanSpeedRatio,
            effects.FormationSpeedRatio, effects.EnhancementSpeedRatio, effects.CombatManapointRegen);

    public static BuildingEffectSet ToDomain(SimulatorBuildingEffectsDto dto)
        => new(dto.ExplorationSpeedRatio, dto.ExplorationOutputRatio, dto.AlchemySpeedRatio,
            dto.AlchemyOutputRatio, dto.CultivationOutputRatio, dto.CombatHitpointRegen,
            dto.CombatDamageReductionRatio, dto.MonsterWeakenRatio, dto.GatheringSpeedRatio,
            dto.MiningSpeedRatio, dto.ForgingSpeedRatio, dto.TalismanSpeedRatio,
            dto.FormationSpeedRatio, dto.EnhancementSpeedRatio, dto.CombatManapointRegen);

    public static SimulatorNodeBuildingDto ToDto(NodeBuildingState state)
        => new(state.BuildingId, state.Level, state.TargetLevel, state.StartedAtUnixSeconds,
            state.ContributedMaterials?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static NodeBuildingState ToDomain(SimulatorNodeBuildingDto dto)
        => new(dto.BuildingId, dto.Level, dto.TargetLevel, dto.StartedAtUnixSeconds,
            dto.ContributedMaterials ?? new Dictionary<string, double>());

    public static SimulatorNodeBuildingUsageDto ToDto(NodeBuildingUsage usage)
        => new(usage.BuildingId, usage.ActiveUsers, usage.Capacity, usage.Efficiency);

    public static NodeBuildingUsage ToDomain(SimulatorNodeBuildingUsageDto dto)
        => new(dto.BuildingId, dto.ActiveUsers, dto.Capacity, dto.Efficiency);

    public static SimulatorCombatUnitStatDto ToDto(CombatUnitStatDocument document)
        => new(document.UnitKey, (int)document.Kind, document.DisplayName,
            document.DamageDealtPhysical, document.DamageDealtSpell,
            document.DamageTakenPhysical, document.DamageTakenSpell,
            document.DamageMitigatedPhysical, document.DamageMitigatedSpell,
            document.HealingRegen, document.HealingOther, document.DamageDealtTrue, document.DamageTakenTrue);

    public static SimulatorEventDto ToDto(SimulationEvent simulationEvent)
        => new(simulationEvent.At.ToUnixTimeMilliseconds() / 1000d,
            simulationEvent.Code, simulationEvent.Message,
            simulationEvent.ActionId?.ToString(),
            simulationEvent.ItemDeltas?.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal));

    public static SimulatorAssetDto ToDto(AssetDelta delta)
        => new(delta.Asset.ItemId.Value, delta.Asset.Variant, delta.QuantityDelta);
}
