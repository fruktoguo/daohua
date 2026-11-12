using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Protocol.Simulator;

namespace DaoHua.Simulator;

/// <summary>
/// 预演输入共享校验：<see cref="SimulatorRunner.ResolveProfiles"/> 与 <see cref="SimulatorRunner.Run"/> 统一执行。
/// 只加严预演契约边界的 fail-fast 语义，不改变 Domain 对旧存档解码的宽容规则。
/// </summary>
internal static class SimulatorInputValidator
{
    /// <summary>成员身份归一化：null = 自定义角色按固定下标派生；其余必须解析为非空 Guid。</summary>
    public static Guid NormalizeMemberId(SimulatorProfileDto profile, int memberIndex)
    {
        if (profile.SourcePlayerId is null)
            return new Guid($"51530000-0000-0000-0000-{memberIndex + 1:D12}");
        if (!Guid.TryParse(profile.SourcePlayerId, out var parsed) || parsed == Guid.Empty)
            throw new DomainException(DomainErrorCode.InvalidArgument, "预演档案源玩家标识无效。");
        return parsed;
    }

    /// <summary>复活目标成员关系校验：策略 2 必须指向阵容中另一名成员。</summary>
    public static void ValidateRespawnTargets(IReadOnlyList<SimulatorProfileDto> members, IReadOnlyList<Guid> memberIds)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var profile = members[i];
            if (profile.RespawnPolicy is < 0 or > 2)
                throw new DomainException(DomainErrorCode.InvalidArgument, "预演档案复活策略无效。");
            var target = Guid.Empty;
            var hasTarget = false;
            if (profile.RespawnTargetPlayerId is { } raw)
            {
                if (!Guid.TryParse(raw, out target) || target == Guid.Empty)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "预演档案复活目标无效。");
                hasTarget = true;
            }
            if (profile.RespawnPolicy != 2) continue;
            if (!hasTarget)
                throw new DomainException(DomainErrorCode.InvalidArgument, "指定队友存活复活策略必须指定复活目标。");
            var targetIndex = -1;
            for (var j = 0; j < memberIds.Count; j++)
                if (memberIds[j] == target) { targetIndex = j; break; }
            if (targetIndex < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "复活目标不在当前推演阵容中。");
            if (targetIndex == i)
                throw new DomainException(DomainErrorCode.InvalidArgument, "复活目标必须是阵容中的另一名成员。");
        }
    }

    /// <summary>校验整份环境快照的全部节点条目，而非仅目标节点。</summary>
    public static void ValidateEnvironment(SimulatorEnvironmentDto environment, GameDataCatalog gameData)
    {
        if (environment.Nodes is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "环境快照节点列表缺失。");
        if (!double.IsFinite(environment.CapturedAtUnixSeconds))
            throw new DomainException(DomainErrorCode.InvalidArgument, "环境快照捕获时间无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in environment.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.NodeId) || !seen.Add(node.NodeId))
                throw new DomainException(DomainErrorCode.InvalidArgument, "环境快照节点标识为空或重复。");
            var isStaticNode = gameData.WorldGraph.TryGetNode(node.NodeId, out _);
            var isResidence = ZongmenRules.TryResidenceId(node.NodeId, out _);
            if (!isStaticNode && !isResidence)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点不存在：{node.NodeId}。");
            if (node.Buildings is null || node.Effects is null || node.Usages is null)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {node.NodeId} 条目缺字段。");
            foreach (var building in node.Buildings)
            {
                if (building is null || string.IsNullOrWhiteSpace(building.BuildingId)
                    || !gameData.Buildings.TryGetValue(building.BuildingId, out _))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {node.NodeId} 引用了不存在的建筑。");
                if (building.Level < 0 || building.TargetLevel < 0
                    || !double.IsFinite(building.StartedAtUnixSeconds))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {node.NodeId} 建筑状态无效。");
                if (building.ContributedMaterials is { } materials
                    && materials.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value) || pair.Value < 0))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {node.NodeId} 建筑投入材料无效。");
            }
            foreach (var usage in node.Usages)
            {
                if (usage is null || string.IsNullOrWhiteSpace(usage.BuildingId)
                    || !gameData.Buildings.TryGetValue(usage.BuildingId, out _)
                    || !double.IsFinite(usage.ActiveUsers) || usage.ActiveUsers < 0
                    || usage.Capacity < 0
                    || !double.IsFinite(usage.Efficiency) || usage.Efficiency < 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {node.NodeId} 建筑拥挤条目无效。");
            }
            ValidateEffects(node.Effects, node.NodeId);
        }
    }

    private static void ValidateEffects(SimulatorBuildingEffectsDto effects, string nodeId)
    {
        if (!double.IsFinite(effects.ExplorationSpeedRatio) || !double.IsFinite(effects.ExplorationOutputRatio)
            || !double.IsFinite(effects.AlchemySpeedRatio) || !double.IsFinite(effects.AlchemyOutputRatio)
            || !double.IsFinite(effects.CultivationOutputRatio) || !double.IsFinite(effects.CombatHitpointRegen)
            || !double.IsFinite(effects.CombatDamageReductionRatio) || !double.IsFinite(effects.MonsterWeakenRatio)
            || !double.IsFinite(effects.GatheringSpeedRatio) || !double.IsFinite(effects.MiningSpeedRatio)
            || !double.IsFinite(effects.ForgingSpeedRatio) || !double.IsFinite(effects.TalismanSpeedRatio)
            || !double.IsFinite(effects.FormationSpeedRatio) || !double.IsFinite(effects.EnhancementSpeedRatio)
            || !double.IsFinite(effects.CombatManapointRegen))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"环境快照节点 {nodeId} 建筑效果存在非法数值。");
    }

    /// <summary>校验单份角色档案的全部非空必需段与集合；装备/功法/特质/消耗品引用必须存在。</summary>
    public static void ValidateMember(SimulatorProfileDto profile, int memberIndex, GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!double.IsFinite(profile.CapturedAtUnixSeconds)
            || !double.IsFinite(profile.Hitpoints) || profile.Hitpoints < 0
            || !double.IsFinite(profile.MaxHitpoints) || profile.MaxHitpoints <= 0
            || !double.IsFinite(profile.Manapoints) || profile.Manapoints < 0
            || !double.IsFinite(profile.MaxManapoints) || profile.MaxManapoints < 0
            || !double.IsFinite(profile.RegenCarrySeconds) || profile.RegenCarrySeconds < 0
            || !double.IsFinite(profile.Experience) || profile.Experience < 0
            || !double.IsFinite(profile.Foundation) || profile.Foundation < 0
            || !double.IsFinite(profile.CombatExperience) || profile.CombatExperience < 0
            || !double.IsFinite(profile.LifetimeExperience) || profile.LifetimeExperience < 0
            || !double.IsFinite(profile.AgeSeconds) || profile.AgeSeconds < 0
            || !double.IsFinite(profile.CompletedActions) || profile.CompletedActions < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员基础数值无效。");
        if (profile.RealmLevel < 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员境界无效。");
        if (profile.CoreAttributes is null || profile.AbilitySlots is null || profile.Equipment is null
            || profile.CombatSkills is null || profile.CombatBuffCarry is null || profile.Buffs is null
            || profile.CombatAutoUseSlots is null || profile.Assets is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员缺必需段。");
        ValidateEquipment(profile, memberIndex, gameData);
        var learned = ValidateAbilities(profile, memberIndex, gameData);
        ValidateTraits(profile, memberIndex);
        ValidateTriggers(profile, memberIndex, learned, gameData);
        ValidateAssets(profile, memberIndex, gameData);
        ValidateAutoUseSlots(profile, memberIndex, gameData);
        ValidateBuffs(profile, memberIndex);
    }

    private static void ValidateEquipment(SimulatorProfileDto profile, int memberIndex, GameDataCatalog gameData)
    {
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in profile.Equipment)
        {
            if (slot is null || string.IsNullOrWhiteSpace(slot.ItemId))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员装备条目无效。");
            if (!double.IsFinite(slot.EnhancementLevel) || slot.EnhancementLevel < 0
                || slot.EnhancementLevel > EnhancementRules.MaxLevel || slot.EnhancementLevel != Math.Floor(slot.EnhancementLevel))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员装备强化等级无效。");
            if (!gameData.RawEquipments.TryGetValue(slot.ItemId, out var definition))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员装备了不存在的装备：{slot.ItemId}。");
            var requiredLevel = CraftingRecipeRules.ResolveEquipmentRequiredLevel(slot.ItemId, gameData.RawEquipments);
            if (profile.RealmLevel < requiredLevel)
                throw new DomainException(DomainErrorCode.InvalidArgument,
                    $"预演档案第 {memberIndex + 1} 名成员境界不足，无法穿戴装备 {slot.ItemId}（需境界 Lv.{requiredLevel}）。");
            var slotId = definition.GetProperty("slot").GetString()!;
            if (!slots.Add(slotId) || (slots.Contains("two-hand") && (slots.Contains("weapon") || slots.Contains("off-hand"))))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员装备槽位冲突。");
        }
    }

    /// <summary>功法槽与经验集合校验；返回已习得功法集合（槽位 ∪ 经验键），供触发条件键校验复用。</summary>
    private static HashSet<string> ValidateAbilities(SimulatorProfileDto profile, int memberIndex, GameDataCatalog gameData)
    {
        if (profile.AbilitySlots.Count > 16)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法槽位过多。");
        var learned = new HashSet<string>(StringComparer.Ordinal);
        var slotted = new HashSet<string>(StringComparer.Ordinal);
        for (var slotIndex = 0; slotIndex < profile.AbilitySlots.Count; slotIndex++)
        {
            var slot = profile.AbilitySlots[slotIndex];
            if (slot is null)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法槽位无效。");
            if (!double.IsFinite(slot.Level) || slot.Level < 1 || slot.Level != Math.Floor(slot.Level))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法等级无效。");
            if (slot.AbilityId is null) continue;
            if (!gameData.Abilities.TryGetValue(slot.AbilityId, out var ability) || !slotted.Add(slot.AbilityId))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法不存在或重复装配。");
            SkillTriggerEvaluator.ValidateAbility(ability);
            var discipline = ability.TryGetProperty("discipline", out var disciplineElement)
                && disciplineElement.ValueKind == JsonValueKind.String
                ? disciplineElement.GetString() : "spell";
            if ((slotIndex == 0) != (discipline == "internal"))
                throw new DomainException(DomainErrorCode.InvalidArgument,
                    slotIndex == 0
                        ? $"预演档案第 {memberIndex + 1} 名成员内功位只能装配心法内功。"
                        : $"预演档案第 {memberIndex + 1} 名成员术法位只能装配神通术法。");
            var learnedLevel = profile.AbilityExperiences is not null
                && profile.AbilityExperiences.TryGetValue(slot.AbilityId, out var exp)
                ? AbilityExpCurve.LevelFor(exp) : 1;
            if (slot.Level > learnedLevel)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员装配等级超过已参悟等级。");
            learned.Add(slot.AbilityId);
        }
        if (profile.AbilityExperiences is not null)
            foreach (var pair in profile.AbilityExperiences)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || !gameData.Abilities.ContainsKey(pair.Key)
                    || !double.IsFinite(pair.Value) || pair.Value < 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法经验条目无效。");
                learned.Add(pair.Key);
            }
        if (profile.TechniqueExperiences is not null)
            foreach (var pair in profile.TechniqueExperiences)
                if (string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value) || pair.Value < 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员技艺经验条目无效。");
        return learned;
    }

    private static void ValidateTraits(SimulatorProfileDto profile, int memberIndex)
    {
        var equipped = profile.TraitIds ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in equipped)
            if (string.IsNullOrWhiteSpace(id) || !CombatTraitRules.AllTraits.ContainsKey(id) || !seen.Add(id))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员特质不存在或重复。");
        if (CombatTraitRules.TotalCost(equipped) > CombatTraitRules.CalculateCapacity(profile.RealmLevel))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员特质容量超限。");
        if (profile.UnlockedTraits is not null)
            foreach (var trait in profile.UnlockedTraits)
            {
                if (trait is null || string.IsNullOrWhiteSpace(trait.Id) || !CombatTraitRules.AllTraits.ContainsKey(trait.Id))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员觉醒特质不存在。");
                if (trait.Level is < 1 or > 20 || !double.IsFinite(trait.Exp) || trait.Exp < 0)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员特质等级无效。");
            }
    }

    private static void ValidateTriggers(
        SimulatorProfileDto profile, int memberIndex, HashSet<string> learned, GameDataCatalog gameData)
    {
        if (profile.AbilityTriggers is not null)
            foreach (var entry in profile.AbilityTriggers)
            {
                if (!learned.Contains(entry.Key))
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员功法未习得，无法配置触发条件。");
                ValidateTriggerList(entry.Value, memberIndex, gameData);
            }
        if (profile.ConsumableTriggers is not null)
            foreach (var entry in profile.ConsumableTriggers)
            {
                if (!gameData.Items.TryGetValue(new ItemId(entry.Key), out var item)
                    || item.Category != ItemDefinition.CategoryConsumable)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员消耗品触发条件键无效。");
                ValidateTriggerList(entry.Value, memberIndex, gameData);
            }
    }

    private static void ValidateTriggerList(JsonElement[]? rules, int memberIndex, GameDataCatalog gameData)
    {
        if (rules is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员触发条件载荷无效。");
        try
        {
            var element = JsonSerializer.SerializeToElement(rules, SimulatorJson.Options);
            SkillTriggerEvaluator.ValidateTriggerList(element, gameData.TriggerBuffIds);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员触发条件载荷无效。");
        }
    }

    private static void ValidateAssets(SimulatorProfileDto profile, int memberIndex, GameDataCatalog gameData)
    {
        var seen = new HashSet<(string ItemId, string Variant)>();
        foreach (var asset in profile.Assets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.ItemId)
                || !gameData.Items.ContainsKey(new ItemId(asset.ItemId)))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员资产引用了不存在的物品。");
            if (!double.IsFinite(asset.Quantity) || asset.Quantity < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员资产数量无效。");
            if (!seen.Add((asset.ItemId, asset.Variant ?? string.Empty)))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员资产存在重复条目。");
        }
    }

    private static void ValidateAutoUseSlots(SimulatorProfileDto profile, int memberIndex, GameDataCatalog gameData)
    {
        var seen = new HashSet<(string Group, int Index)>();
        var itemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stackable = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in profile.CombatAutoUseSlots)
        {
            if (slot is null || !string.Equals(slot.ActionType, AutoUseBar.Combat, StringComparison.Ordinal)
                || slot.Group is not (AutoUseSlotState.SlotGroupGeneral or AutoUseSlotState.SlotGroupPotion)
                || slot.SlotIndex is < 0 || slot.SlotIndex >= AutoUseBar.SlotCountFor(AutoUseBar.Combat)
                || !seen.Add((slot.Group, slot.SlotIndex)))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员战斗消耗品槽位无效。");
            if (!double.IsFinite(slot.Quantity) || slot.Quantity <= 0
                || !double.IsFinite(slot.RemainingDurationSeconds) || slot.RemainingDurationSeconds < 0
                || !double.IsFinite(slot.CooldownUntilUnixSeconds) || slot.CooldownUntilUnixSeconds < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员战斗消耗品槽数量或时钟无效。");
            if (slot.ItemId is null)
            {
                if (slot.Enabled)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员空消耗品槽不能启用。");
                continue;
            }
            if (!gameData.Items.TryGetValue(new ItemId(slot.ItemId), out var item)
                || item.Category != ItemDefinition.CategoryConsumable
                || item.Consumable is null || !item.Consumable.UsableInBars.Contains(AutoUseBar.Combat))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员消耗品槽物品不适用于战斗栏。");
            if (slot.Group == AutoUseSlotState.SlotGroupPotion
                && (item.DisplaySubCategory != ConsumableDefinition.CategoryPill || item.Consumable.IsContinuous))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员药品栏只能装配药品类即时消耗品。");
            itemCounts[slot.ItemId] = itemCounts.GetValueOrDefault(slot.ItemId) + 1;
            stackable[slot.ItemId] = item.Consumable.StackableSlotCount;
        }
        foreach (var pair in itemCounts)
            if (pair.Value > Math.Max(1, stackable[pair.Key]))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员消耗品槽同物品槽数超限。");
    }

    private static void ValidateBuffs(SimulatorProfileDto profile, int memberIndex)
    {
        foreach (var buff in profile.Buffs)
        {
            if (buff is null || string.IsNullOrWhiteSpace(buff.BuffId)
                || !double.IsFinite(buff.ExpiresAtUnixSeconds)
                || !double.IsFinite(buff.Stacks) || buff.Stacks <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员战外增益条目无效。");
        }
        foreach (var carry in profile.CombatBuffCarry)
        {
            if (carry is null || string.IsNullOrWhiteSpace(carry.BuffId)
                || !double.IsFinite(carry.RemainingSeconds) || carry.RemainingSeconds < 0
                || !double.IsFinite(carry.Stacks) || carry.Stacks <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员战内增益条目无效。");
        }
        if (profile.CombatSkills.Cooldowns is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员技能冷却列表缺失。");
        foreach (var cooldown in profile.CombatSkills.Cooldowns)
            if (cooldown is null || string.IsNullOrWhiteSpace(cooldown.AbilityId)
                || !double.IsFinite(cooldown.RemainingSeconds) || cooldown.RemainingSeconds < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员技能冷却条目无效。");
        if (!double.IsFinite(profile.CombatSkills.CastRemainingSeconds) || profile.CombatSkills.CastRemainingSeconds < 0
            || !double.IsFinite(profile.CombatSkills.CastCount) || profile.CombatSkills.CastCount < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"预演档案第 {memberIndex + 1} 名成员吟唱状态无效。");
    }
}
