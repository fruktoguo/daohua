using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 行动队列的唯一结算入口。
/// 结算按“行动完成边界”推进，不按离线时长逐秒循环，服务重启后可以直接快进到目标时间。
/// </summary>
public static class XiuxianSimulation
{
    private const int MaxTransitionsPerAdvance = 2_000_000;
    private const int EventLogLimit = 40;
    private const int PendingEventLimit = 4_096;
    private const int MinimumCombatSearchMilliseconds = 1_000;
    private const int MaximumCombatSearchMilliseconds = 60_000;

    public static XiuxianPlayerState CreateDefaultState(
        Guid playerId,
        string? name = null,
        DateTimeOffset? now = null)
    {
        var timestamp = NormalizeTime(now ?? DateTimeOffset.UtcNow);
        var state = new XiuxianPlayerState
        {
            PlayerId = playerId,
            Name = string.IsNullOrWhiteSpace(name) ? "无名修士" : name.Trim(),
            LastSimulatedAt = timestamp,
            PresenceMode = XiuxianPresenceMode.Online
        };

        state.Queue.AutoUseSlots[XiuxianActionType.Cultivation][0] = new XiuxianAutoUseSlot
        {
            ItemId = XiuxianItemIds.QiGatheringPill,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.LearnedAbilityIds.Add(XiuxianCombatAbilityIds.FiveElementsFlare);
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;

        state.Queue.Entries.Add(CreateQueuedAction(XiuxianActionIds.GatherHerbs));
        state.Queue.Entries.Add(CreateQueuedAction(XiuxianActionIds.RefineQiPill));
        state.Queue.Entries.Add(CreateQueuedAction(XiuxianActionIds.DrawQi));
        AddEvent(state, "行动队列已建立：采集 → 炼丹 → 修炼");
        return state;
    }

    public static bool NormalizeState(XiuxianPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Queue ??= new XiuxianActionQueueState();
        var queue = state.Queue;
        var queueChanged = false;
        var inventoryChanged = false;
        var combatChanged = false;
        var combatProgressChanged = false;
        if (state.Inventory is null)
        {
            state.Inventory = new XiuxianInventoryState();
            inventoryChanged = true;
        }

        if (state.Inventory.Items is null)
        {
            state.Inventory.Items = new Dictionary<string, long>(StringComparer.Ordinal);
            inventoryChanged = true;
        }

        foreach (var itemId in state.Inventory.Items
                     .Where(pair => pair.Value == 0)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            state.Inventory.Items.Remove(itemId);
            inventoryChanged = true;
        }
        state.World ??= new XiuxianWorldState();
        var worldChanged = XiuxianWorldCatalog.Normalize(state.World);
        queue.Entries ??= [];
        queue.LoopTemplate ??= [];

        if (queue.LoopTemplate.Count > 0)
        {
            var missingPrefixCount = queue.LoopEnabled
                ? FindLegacyMissingPrefixCount(queue.Entries, queue.LoopTemplate)
                : -1;

            if (missingPrefixCount >= 0)
            {
                for (var index = 0; index < queue.Entries.Count; index++)
                {
                    queueChanged |= SetTotalRepeats(
                        queue.Entries[index],
                        GetConfiguredRepeats(queue.LoopTemplate[index + missingPrefixCount]));
                }

                for (var index = 0; index < missingPrefixCount; index++)
                {
                    var restored = queue.LoopTemplate[index].CloneForLoop();
                    restored.TotalRepeats = NormalizeRepeats(GetConfiguredRepeats(queue.LoopTemplate[index]));
                    restored.RemainingRepeats = restored.TotalRepeats;
                    queue.Entries.Add(restored);
                }
            }

            queue.LoopTemplate.Clear();
            queueChanged = true;
        }

        for (var index = queue.Entries.Count - 1; index >= 0; index--)
        {
            var entry = queue.Entries[index];
            if (!XiuxianActionCatalog.TryGet(entry.ActionId, out var definition)
                || (definition.ExecutionKind == XiuxianActionExecutionKind.Travel
                    && !IsValidTravelPlan(state.World, entry.TravelPlan)))
            {
                queue.Entries.RemoveAt(index);
                queueChanged = true;
                continue;
            }

            if (definition.ExecutionKind == XiuxianActionExecutionKind.Travel)
            {
                entry.TotalRepeats = 1;
                entry.RemainingRepeats = 1;
                entry.DurationSeconds = entry.TravelPlan!.TotalDurationSeconds;
            }
            else if (definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter)
            {
                entry.DurationSeconds = 0;
                if (entry.CombatExpedition is null)
                {
                    entry.CombatExpedition = CreateCombatExpedition(definition);
                    queueChanged = true;
                }
            }
            else
            {
                var durationMilliseconds = checked((long)Math.Max(0, entry.DurationSeconds) * 1_000);
                var normalizedProgress = entry.ProgressMilliseconds > 0
                    ? entry.ProgressMilliseconds
                    : checked((long)Math.Max(0, entry.ProgressSeconds) * 1_000);
                normalizedProgress = Math.Clamp(normalizedProgress, 0, durationMilliseconds);
                if (entry.ProgressMilliseconds != normalizedProgress)
                {
                    entry.ProgressMilliseconds = normalizedProgress;
                    queueChanged = true;
                }

                var displaySeconds = (int)Math.Floor(normalizedProgress / 1_000d);
                if (entry.ProgressSeconds != displaySeconds)
                {
                    entry.ProgressSeconds = displaySeconds;
                    queueChanged = true;
                }
            }

            queueChanged |= SetTotalRepeats(entry, GetConfiguredRepeats(entry));
        }

        queue.AutoUseSlots ??= [];
        foreach (var actionType in Enum.GetValues<XiuxianActionType>())
        {
            if (!queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
            {
                queue.AutoUseSlots[actionType] = Enumerable
                    .Range(0, XiuxianCombatLimits.AutoUseSlotCount)
                    .Select(_ => new XiuxianAutoUseSlot())
                    .ToList();
                queueChanged = true;
                continue;
            }

            queueChanged |= NormalizeSlots(
                slots,
                XiuxianCombatLimits.AutoUseSlotCount,
                static () => new XiuxianAutoUseSlot());
        }

        state.Combat ??= new XiuxianCombatLoadout();
        if (state.Visual is null)
        {
            state.Visual = new XiuxianCharacterVisualDefinition
            {
                Renderer = "text",
                Source = state.Name[..1]
            };
            combatChanged = true;
        }
        if (state.CombatResources is null)
        {
            state.CombatResources = new XiuxianCombatResourceState();
            combatChanged = true;
        }
        state.Combat.AbilitySlots ??= [];
        state.Combat.ConsumableSlots ??= [];
        state.Combat.FoodSlots ??= [];
        state.Combat.DrinkSlots ??= [];
        state.Combat.Equipment ??= [];
        state.Combat.LearnedAbilityIds ??= [];
        if (!double.IsFinite(state.Combat.HpRegenPerTenSeconds)
            || state.Combat.HpRegenPerTenSeconds < 0)
        {
            state.Combat.HpRegenPerTenSeconds = 0.01;
            combatChanged = true;
        }
        if (!double.IsFinite(state.Combat.MpRegenPerTenSeconds)
            || state.Combat.MpRegenPerTenSeconds < 0)
        {
            state.Combat.MpRegenPerTenSeconds = 0.01;
            combatChanged = true;
        }
        if (!ValidateCombatMultiplier(state.Combat.CombatDropRate))
        {
            state.Combat.CombatDropRate = 0;
            combatChanged = true;
        }
        if (!ValidateCombatMultiplier(state.Combat.CombatDropQuantity))
        {
            state.Combat.CombatDropQuantity = 0;
            combatChanged = true;
        }
        if (!ValidateCombatMultiplier(state.Combat.CombatRareFind))
        {
            state.Combat.CombatRareFind = 0;
            combatChanged = true;
        }
        if (!ValidateSearchSpeedRatio(state.Combat.SearchSpeedRatio))
        {
            state.Combat.SearchSpeedRatio = 0;
            combatChanged = true;
        }
        if (!ValidateTenacity(state.Combat.Tenacity))
        {
            state.Combat.Tenacity = 0;
            combatChanged = true;
        }
        if (state.Combat.StaminaLevel < 10) { state.Combat.StaminaLevel = 10; combatChanged = true; }
        if (state.Combat.IntelligenceLevel < 10) { state.Combat.IntelligenceLevel = 10; combatChanged = true; }
        if (state.Combat.AttackLevel < 10) { state.Combat.AttackLevel = 10; combatChanged = true; }
        if (state.Combat.DefenseLevel < 10) { state.Combat.DefenseLevel = 10; combatChanged = true; }
        if (state.Combat.SwordsmanshipLevel < 10) { state.Combat.SwordsmanshipLevel = 10; combatChanged = true; }
        if (state.Combat.TalismanLevel < 10) { state.Combat.TalismanLevel = 10; combatChanged = true; }
        if (state.Combat.FiveElementsLevel < 10) { state.Combat.FiveElementsLevel = 10; combatChanged = true; }
        combatChanged |= NormalizeEquipment(state.Combat.Equipment);
        combatChanged |= NormalizeCombatEffectRatios(state.Combat);
        combatChanged |= NormalizeSlots(
            state.Combat.AbilitySlots,
            XiuxianCombatLimits.AbilitySlotCount,
            static () => new XiuxianCombatAbilitySlot());
        combatChanged |= NormalizeLearnedAbilities(state.Combat);
        combatChanged |= MigrateLegacyConsumableSlots(state.Combat);
        combatChanged |= NormalizeSlots(
            state.Combat.ConsumableSlots,
            XiuxianCombatLimits.ConsumableSlotCount,
            static () => new XiuxianConsumableSlot());
        foreach (var slot in state.Combat.AbilitySlots)
        {
            if (slot.Triggers is not null) continue;
            slot.Triggers = [];
            combatChanged = true;
        }

        foreach (var slot in state.Combat.ConsumableSlots)
        {
            if (slot.Triggers is not null) continue;
            slot.Triggers = [];
            combatChanged = true;
        }

        if (state.CombatEncounterCompletions is null)
        {
            state.CombatEncounterCompletions = new Dictionary<string, long>(StringComparer.Ordinal);
            combatProgressChanged = true;
        }
        else
        {
            foreach (var encounterId in state.CombatEncounterCompletions
                         .Where(pair => pair.Value < 0 || !XiuxianCombatCatalog.TryGetEncounter(pair.Key, out _))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                state.CombatEncounterCompletions.Remove(encounterId);
                combatProgressChanged = true;
            }
        }

        var dirty = (queueChanged ? XiuxianDirtySections.Queue : XiuxianDirtySections.None)
            | (inventoryChanged ? XiuxianDirtySections.Inventory : XiuxianDirtySections.None)
            | (combatChanged ? XiuxianDirtySections.Combat : XiuxianDirtySections.None)
            | (combatProgressChanged ? XiuxianDirtySections.Stats : XiuxianDirtySections.None)
            | (worldChanged ? XiuxianDirtySections.World : XiuxianDirtySections.None);
        if (dirty != XiuxianDirtySections.None) Touch(state, dirty);
        return dirty != XiuxianDirtySections.None;
    }

    public static XiuxianCommandResult Enqueue(
        XiuxianPlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!XiuxianActionCatalog.TryGet(actionId, out var definition))
        {
            return XiuxianCommandResult.Fail("ACTION_NOT_FOUND", "行动不存在。");
        }

        if (definition.HiddenFromCatalog || definition.ExecutionKind == XiuxianActionExecutionKind.Travel)
        {
            return XiuxianCommandResult.Fail("ACTION_NOT_SELECTABLE", "该行动不能直接加入队列。");
        }

        if (!XiuxianWorldCatalog.CanPerformAction(state.World, definition))
        {
            return XiuxianCommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该行动。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return XiuxianCommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        var action = CreateQueuedAction(actionId, repeats);
        state.Queue.Entries.Add(action);

        Touch(state, XiuxianDirtySections.Queue | XiuxianDirtySections.Events);
        AddEvent(state, $"已加入行动：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return XiuxianCommandResult.Ok("行动已加入队列。");
    }

    public static XiuxianCommandResult StartCombat(
        XiuxianPlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!XiuxianActionCatalog.TryGet(actionId, out var definition))
        {
            return XiuxianCommandResult.Fail("ACTION_NOT_FOUND", "历练行动不存在。");
        }

        if (definition.ExecutionKind != XiuxianActionExecutionKind.CombatEncounter)
        {
            return XiuxianCommandResult.Fail("ACTION_NOT_COMBAT", "该行动不是历练战斗。");
        }

        if (!XiuxianWorldCatalog.CanPerformAction(state.World, definition))
        {
            return XiuxianCommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该历练。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return XiuxianCommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            if (XiuxianActionCatalog.TryGet(current.ActionId, out var currentDefinition)
                && currentDefinition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter)
            {
                state.Queue.Entries.RemoveAt(0);
            }
            else
            {
                current.Status = XiuxianActionStatus.Queued;
            }
        }

        EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
        state.Queue.Entries.Insert(0, CreateQueuedAction(actionId, repeats));
        Touch(state, XiuxianDirtySections.Queue | XiuxianDirtySections.Combat | XiuxianDirtySections.Events);
        AddEvent(state, $"已立即开始历练：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return XiuxianCommandResult.Ok("历练已开始。");
    }

    public static XiuxianCommandResult TravelTo(XiuxianPlayerState state, string destinationNodeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(destinationNodeId)
            || !XiuxianWorldCatalog.TryPlanTravel(state.World, destinationNodeId, out var plan))
        {
            return XiuxianCommandResult.Fail("TRAVEL_ROUTE_NOT_FOUND", "无法从当前位置规划到目标地点的路线。");
        }

        var hadCombat = state.Queue.Entries.FirstOrDefault() is { } oldTop
            && XiuxianActionCatalog.TryGet(oldTop.ActionId, out var oldTopDef)
            && (oldTopDef.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter || oldTop.CombatExpedition is not null);

        state.Queue.Entries.RemoveAll(entry => entry.TravelPlan is not null);
        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            current.Status = XiuxianActionStatus.Queued;
        }

        var destination = XiuxianWorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
        state.Queue.Entries.Insert(0, new XiuxianQueuedAction
        {
            ActionId = XiuxianActionIds.WorldTravel,
            TotalRepeats = 1,
            RemainingRepeats = 1,
            DurationSeconds = plan.TotalDurationSeconds,
            Status = XiuxianActionStatus.Queued,
            TravelPlan = plan
        });
        var dirty = XiuxianDirtySections.Queue | XiuxianDirtySections.Events;
        if (hadCombat)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= XiuxianDirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(
            state,
            $"已规划前往 {{{{location:{destination.Id}|{destination.Name}}}}}，共 {plan.Segments.Count} 段山路，预计 {plan.TotalDurationSeconds} 秒");
        return XiuxianCommandResult.Ok($"已开始前往{destination.Name}。");
    }

    public static XiuxianCommandResult Remove(XiuxianPlayerState state, Guid entryId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        if (index < 0)
        {
            return XiuxianCommandResult.Fail("ACTION_NOT_FOUND", "队列中没有找到该行动。");
        }

        var removed = state.Queue.Entries[index];
        var definition = XiuxianActionCatalog.Get(removed.ActionId);
        var actionName = removed.TravelPlan is null
            ? definition.Name
            : $"赶往{XiuxianWorldCatalog.GetNode(removed.TravelPlan.MapId, removed.TravelPlan.DestinationNodeId).Name}";
        state.Queue.Entries.RemoveAt(index);
        var dirty = XiuxianDirtySections.Queue | XiuxianDirtySections.Events;
        if (removed.CombatExpedition is not null || definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= XiuxianDirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(state, $"已从队列移除行动：{actionName}");
        return XiuxianCommandResult.Ok("行动已移除。");
    }

    public static XiuxianCommandResult Move(XiuxianPlayerState state, Guid entryId, int direction)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (direction is not (-1 or 1))
        {
            return XiuxianCommandResult.Fail("INVALID_DIRECTION", "队列移动方向无效。");
        }

        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= state.Queue.Entries.Count)
        {
            return XiuxianCommandResult.Fail("QUEUE_EDGE", "行动已经在队列边界。");
        }

        if (state.Queue.Entries[index].TravelPlan is not null
            || state.Queue.Entries[targetIndex].TravelPlan is not null)
        {
            return XiuxianCommandResult.Fail("TRAVEL_PRIORITY", "赶路行动必须保持在队列首位。");
        }

        (state.Queue.Entries[index], state.Queue.Entries[targetIndex]) =
            (state.Queue.Entries[targetIndex], state.Queue.Entries[index]);
        Touch(state, XiuxianDirtySections.Queue);
        return XiuxianCommandResult.Ok("队列顺序已更新。");
    }

    public static XiuxianCommandResult SetAutoUse(
        XiuxianPlayerState state,
        XiuxianActionType actionType,
        string? itemId,
        int quantity,
        bool enabled,
        int slotIndex = 0,
        IReadOnlyList<XiuxianCombatTrigger>? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return XiuxianCommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!Enum.IsDefined(actionType))
        {
            return XiuxianCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        if (slotIndex < 0 || slotIndex >= XiuxianCombatLimits.AutoUseSlotCount)
        {
            return XiuxianCommandResult.Fail(
                "INVALID_SLOT_INDEX",
                $"自动使用槽位序号必须在 0 到 {XiuxianCombatLimits.AutoUseSlotCount - 1} 之间。");
        }

        if (!ValidateTriggers(triggers))
        {
            return XiuxianCommandResult.Fail("INVALID_TRIGGERS", "自动使用触发条件无效。");
        }

        if (!string.IsNullOrWhiteSpace(itemId)
            && !XiuxianItemCatalog.CanUseInAction(itemId, actionType))
        {
            return XiuxianCommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }

        if (!state.Queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
        {
            slots = [];
            state.Queue.AutoUseSlots[actionType] = slots;
        }

        while (slots.Count < XiuxianCombatLimits.AutoUseSlotCount)
        {
            slots.Add(new XiuxianAutoUseSlot());
        }

        var normalizedTriggers = triggers is { Count: > 0 } ? CloneTriggers(triggers) : null;
        var currentSlot = slots[slotIndex];
        if (currentSlot.ItemId == itemId
            && currentSlot.Quantity == quantity
            && currentSlot.Enabled == (enabled && !string.IsNullOrWhiteSpace(itemId))
            && TriggersEqual(currentSlot.Triggers, normalizedTriggers))
        {
            return XiuxianCommandResult.Ok("自动使用槽无需更新。");
        }

        slots[slotIndex] = new XiuxianAutoUseSlot
        {
            ItemId = itemId,
            Quantity = quantity,
            Enabled = enabled && !string.IsNullOrWhiteSpace(itemId),
            Triggers = normalizedTriggers
        };
        Touch(state, XiuxianDirtySections.Queue);
        return XiuxianCommandResult.Ok("自动使用槽已更新。");
    }

    public static XiuxianCommandResult UseItem(
        XiuxianPlayerState state,
        string itemId,
        XiuxianActionType actionType,
        int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return XiuxianCommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!XiuxianItemCatalog.TryGet(itemId, out var item)
            || item.Consumable is null
            || !item.Consumable.UsableInActionTypes.Contains(actionType))
        {
            return XiuxianCommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }

        if (item.Consumable.Buffs.Count == 0
            && (item.Consumable.HitpointRestore > 0 || item.Consumable.ManapointRestore > 0))
        {
            return XiuxianCommandResult.Fail("ITEM_REQUIRES_COMBAT", "该物品只能在历练战斗事件中自动使用。");
        }

        if (state.Inventory.Get(itemId) < quantity)
        {
            return XiuxianCommandResult.Fail("INSUFFICIENT_ITEM", "物品数量不足。");
        }

        // 手动使用共享自动使用槽的 Buff 规则；战斗中的即时生命恢复由战斗事件处理器负责。
        state.Inventory.Add(itemId, -quantity);
        ApplyBuffEffects(state, item, quantity, state.LastSimulatedAt);
        Touch(state, XiuxianDirtySections.Inventory | XiuxianDirtySections.Buffs | XiuxianDirtySections.Events);
        AddEvent(state, $"手动使用 {{{{item:{item.Id}|{item.Name}}}}} × {quantity}");
        return XiuxianCommandResult.Ok("物品已使用。");
    }

    public static XiuxianCommandResult SetCombatLoadout(
        XiuxianPlayerState state,
        XiuxianCombatStyle? style,
        XiuxianDamageType? damageType,
        IReadOnlyList<XiuxianCombatAbilitySlot>? abilitySlots,
        IReadOnlyList<XiuxianConsumableSlot>? consumableSlots,
        double? combatDropRate = null,
        double? combatDropQuantity = null,
        double? combatRareFind = null,
        double? tenacity = null,
        double? searchSpeedRatio = null,
        IReadOnlyList<XiuxianEquippedItem>? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (style is not null && !Enum.IsDefined(style.Value))
        {
            return XiuxianCommandResult.Fail("INVALID_COMBAT_STYLE", "战斗流派无效。");
        }

        if (damageType is not null && !Enum.IsDefined(damageType.Value))
        {
            return XiuxianCommandResult.Fail("INVALID_DAMAGE_TYPE", "伤害类型无效。");
        }

        if (!ValidateCombatMultiplier(combatDropRate)
            || !ValidateCombatMultiplier(combatDropQuantity)
            || !ValidateCombatMultiplier(combatRareFind))
        {
            return XiuxianCommandResult.Fail("INVALID_COMBAT_DROP_BONUS", "战斗掉落倍率必须在 -100% 到 100000% 之间。");
        }

        if (!ValidateTenacity(tenacity))
        {
            return XiuxianCommandResult.Fail("INVALID_TENACITY", "韧性必须在 0 到 100000 之间。");
        }
        if (!ValidateSearchSpeedRatio(searchSpeedRatio))
        {
            return XiuxianCommandResult.Fail("INVALID_SEARCH_SPEED", "寻怪速度必须在 -90% 到 100000% 之间。");
        }

        List<XiuxianEquippedItem>? normalizedEquipment = null;
        if (equipment is not null)
        {
            if (!TryNormalizeEquipment(equipment, out normalizedEquipment, out var equipmentError))
            {
                return XiuxianCommandResult.Fail("INVALID_EQUIPMENT", equipmentError);
            }
        }

        List<XiuxianCombatAbilitySlot>? normalizedAbilities = null;
        if (abilitySlots is not null)
        {
            if (abilitySlots.Count > XiuxianCombatLimits.AbilitySlotCount
                || abilitySlots.Any(slot => slot.Level is < 1 or > 100))
            {
                return XiuxianCommandResult.Fail("INVALID_ABILITY_SLOTS", "功法槽数量或等级无效。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !XiuxianCombatCatalog.TryGetAbility(slot.AbilityId, out _)))
            {
                return XiuxianCommandResult.Fail("ABILITY_NOT_FOUND", "功法不存在。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !state.Combat.LearnedAbilityIds.Contains(slot.AbilityId, StringComparer.Ordinal)))
            {
                return XiuxianCommandResult.Fail("ABILITY_NOT_LEARNED", "尚未习得该功法。");
            }

            if (abilitySlots.Any(slot => !ValidateTriggers(slot.Triggers)))
            {
                return XiuxianCommandResult.Fail("INVALID_ABILITY_TRIGGERS", "功法触发条件无效。");
            }

            normalizedAbilities = abilitySlots
                .Select(slot => new XiuxianCombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = CloneTriggers(slot.Triggers)
                })
                .Concat(Enumerable.Range(
                    abilitySlots.Count,
                    XiuxianCombatLimits.AbilitySlotCount - abilitySlots.Count).Select(_ => new XiuxianCombatAbilitySlot()))
                .ToList();
        }

        if (!TryValidateConsumableSlots(consumableSlots, out var error))
        {
            return XiuxianCommandResult.Fail("INVALID_CONSUMABLE_SLOTS", error);
        }

        if (normalizedAbilities is not null)
        {
            state.Combat.AbilitySlots = normalizedAbilities;
        }

        if (consumableSlots is not null)
        {
            state.Combat.ConsumableSlots = CloneConsumableSlots(consumableSlots);
        }

        if (normalizedEquipment is not null)
        {
            state.Combat.Equipment = normalizedEquipment;
        }

        if (style is not null) state.Combat.Style = style.Value;
        if (damageType is not null) state.Combat.DamageType = damageType.Value;
        if (combatDropRate is not null) state.Combat.CombatDropRate = combatDropRate.Value;
        if (combatDropQuantity is not null) state.Combat.CombatDropQuantity = combatDropQuantity.Value;
        if (combatRareFind is not null) state.Combat.CombatRareFind = combatRareFind.Value;
        if (tenacity is not null) state.Combat.Tenacity = tenacity.Value;
        if (searchSpeedRatio is not null) state.Combat.SearchSpeedRatio = searchSpeedRatio.Value;
        Touch(state, XiuxianDirtySections.Combat);
        return XiuxianCommandResult.Ok("战斗配置已更新。");
    }

    public static XiuxianCommandResult SetLoop(XiuxianPlayerState state, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Queue.LoopEnabled == enabled)
        {
            return XiuxianCommandResult.Ok(enabled ? "循环已经开启。" : "循环已经关闭。");
        }

        state.Queue.LoopEnabled = enabled;
        Touch(state, XiuxianDirtySections.Queue);
        return XiuxianCommandResult.Ok(enabled ? "循环已开启。" : "循环已关闭。");
    }

    public static XiuxianCommandResult SetPresence(
        XiuxianPlayerState state,
        XiuxianPresenceMode mode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedNow = NormalizeTime(now);
        if (state.PresenceMode != XiuxianPresenceMode.FullyOffline)
        {
            AdvanceTo(state, normalizedNow);
        }

        state.PresenceMode = mode;
        Touch(state, XiuxianDirtySections.Presence);
        return XiuxianCommandResult.Ok($"状态已切换为 {mode}。");
    }

    public static XiuxianCommandResult Breakthrough(XiuxianPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var progress = XiuxianCombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;
        var isCap = progress.ExpToNext > 0 && expToCap <= 0.0001 && state.Stats.Spirit > 0;
        if (!state.IsPendingBreakthrough && !isCap)
        {
            return XiuxianCommandResult.Fail("CULTIVATION_NOT_CAPPED", "修为尚未圆满，无法突破。");
        }

        state.IsPendingBreakthrough = false;
        Touch(state, XiuxianDirtySections.Stats | XiuxianDirtySections.Events | XiuxianDirtySections.Combat);
        AddEvent(state, "破境功成！洗髓伐经，道行更进一层。");
        return XiuxianCommandResult.Ok("突破成功！");
    }

    /// <summary>
    /// 增加玩家修炼修为。
    /// 规则：
    /// 1. 当修为达到当前层满值且未突破时：
    ///    获得的全部修为转化为「底蕴」(FoundationExp)；
    /// 2. 当有底蕴时：获得修为享受 3 倍经验（基础 1x + 消耗底蕴提供 2x 加成，若底蕴不足则消耗全部底蕴提供等值加成）；
    /// 3. 若加上获得修为后触及当前层境界满值，则卡在满值并开启待突破标记，溢出部分自动注入底蕴。
    /// </summary>
    public static (double actualSpiritGain, double foundationGain, double foundationConsumed) AddCultivationSpirit(
        XiuxianPlayerState state,
        double baseSpiritGain)
    {
        if (baseSpiritGain <= 0) return (0, 0, 0);

        var progress = XiuxianCombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;

        if (progress.ExpToNext <= 0.0 || progress.Level >= XiuxianCombatConstants.MaxRealmLevel)
        {
            state.Stats.FoundationExp += baseSpiritGain;
            return (0, baseSpiritGain, 0);
        }

        if (state.IsPendingBreakthrough || expToCap <= 0.0001)
        {
            state.IsPendingBreakthrough = true;
            state.Stats.FoundationExp += baseSpiritGain;
            return (0, baseSpiritGain, 0);
        }

        // 当有底蕴时：获得修为享受三倍经验，但会扣除等值的底蕴
        // 比如获得 10 修为，底蕴 10，则消耗 10 底蕴获得 20 实际修为；底蕴 50，则消耗 20 底蕴获得 30 实际修为
        var maxBonus = baseSpiritGain * 2.0;
        var foundationConsumed = Math.Min(state.Stats.FoundationExp, maxBonus);
        state.Stats.FoundationExp = Math.Max(0, state.Stats.FoundationExp - foundationConsumed);

        var totalGain = baseSpiritGain + foundationConsumed;

        if (totalGain >= expToCap)
        {
            var actualGain = expToCap;
            state.Stats.Spirit += (long)Math.Round(actualGain);
            state.IsPendingBreakthrough = true;
            var overflow = totalGain - expToCap;
            if (overflow > 0)
            {
                state.Stats.FoundationExp += overflow;
            }
            return (actualGain, overflow, foundationConsumed);
        }
        else
        {
            state.Stats.Spirit += (long)Math.Round(totalGain);
            return (totalGain, 0, foundationConsumed);
        }
    }

    public static XiuxianSimulationResult AdvanceTo(
        XiuxianPlayerState state,
        DateTimeOffset targetTime)
    {
        ArgumentNullException.ThrowIfNull(state);
        var target = NormalizeTime(targetTime);
        var cursor = NormalizeTime(state.LastSimulatedAt);
        if (target < cursor)
        {
            return new XiuxianSimulationResult(XiuxianDirtySections.None, 0, 0, []);
        }

        if (state.PresenceMode == XiuxianPresenceMode.FullyOffline)
        {
            // 完全离线账号不参与行动结算；推进时间戳即可避免重新启用时追溯这段时间。
            state.LastSimulatedAt = target;
            return new XiuxianSimulationResult(XiuxianDirtySections.None, 0, 0, []);
        }

        var dirty = XiuxianDirtySections.None;
        var events = new List<string>();
        var completedActions = 0;
        var transitions = 0;
        var skippedForMissingInputs = new HashSet<Guid>();
        var hasPendingQueueRotation = false;
        var combatEvents = 0;

        while (cursor <= target && transitions < MaxTransitionsPerAdvance)
        {
            if (ExpireBuffs(state, cursor))
            {
                dirty |= XiuxianDirtySections.Buffs;
            }

            if (state.Queue.Entries.Count == 0)
            {
                var available = GetAvailableMilliseconds(cursor, target);
                if (available > 0 && RegenerateResources(state, cursor, available))
                {
                    dirty |= XiuxianDirtySections.Combat;
                }
                cursor = target;
                break;
            }

            var current = state.Queue.Entries[0];
            var definition = XiuxianActionCatalog.Get(current.ActionId);
            current.DurationSeconds = GetDurationSeconds(current, definition);
            var wasBlocked = current.Status == XiuxianActionStatus.Blocked;
            current.Status = XiuxianActionStatus.Running;

            if (!CanStartAction(state, current, definition))
            {
                current.Status = XiuxianActionStatus.Blocked;
                if (state.Queue.LoopEnabled)
                {
                    if (!skippedForMissingInputs.Add(current.EntryId))
                    {
                        // 全部行动都被跳过时队列空转：这段空闲时间同样享受生命和灵力自动回复。
                        var idleMilliseconds = GetAvailableMilliseconds(cursor, target);
                        if (idleMilliseconds > 0 && RegenerateResources(state, cursor, idleMilliseconds))
                        {
                            dirty |= XiuxianDirtySections.Combat;
                        }
                        cursor = target;
                        break;
                    }

                    if (!wasBlocked)
                    {
                        state.Stats.BlockedActions++;
                        AddEvent(state, $"行动跳过：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足，已移至队尾");
                        dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.Stats | XiuxianDirtySections.Events;
                        events.Add($"skipped:{definition.Id}");
                    }

                    state.Queue.Entries.RemoveAt(0);
                    state.Queue.Entries.Add(current);
                    hasPendingQueueRotation = true;
                    transitions++;
                    continue;
                }

                if (!wasBlocked)
                {
                    state.Stats.BlockedActions++;
                    dirty |= XiuxianDirtySections.Stats;
                }

                AddEvent(state, $"行动结束：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足");
                dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.Events;
                events.Add($"ended:{definition.Id}:missing-inputs");
                state.Queue.Entries.RemoveAt(0);
                transitions++;
                continue;
            }

            if (hasPendingQueueRotation)
            {
                dirty |= XiuxianDirtySections.Queue;
                hasPendingQueueRotation = false;
            }

            if (definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter)
            {
                AdvanceCombatAction(
                    state,
                    current,
                    definition,
                    ref cursor,
                    target,
                    ref dirty,
                    ref completedActions,
                    ref transitions,
                    ref combatEvents);
                skippedForMissingInputs.Clear();
                if (cursor >= target || transitions >= MaxTransitionsPerAdvance) break;
                continue;
            }

            var durationMilliseconds = checked((long)Math.Max(0, current.DurationSeconds) * 1_000);
            if (current.ProgressMilliseconds <= 0 && current.ProgressSeconds > 0)
            {
                current.ProgressMilliseconds = checked((long)current.ProgressSeconds * 1_000);
            }
            current.ProgressMilliseconds = Math.Clamp(current.ProgressMilliseconds, 0, durationMilliseconds);
            var remainingMilliseconds = Math.Max(1, durationMilliseconds - current.ProgressMilliseconds);
            var finishAt = cursor.AddMilliseconds(remainingMilliseconds);
            if (finishAt > target)
            {
                var progress = GetAvailableMilliseconds(cursor, target);
                if (progress > 0)
                {
                    current.ProgressMilliseconds = Math.Min(
                        durationMilliseconds,
                        checked(current.ProgressMilliseconds + progress));
                    current.ProgressSeconds = (int)Math.Floor(current.ProgressMilliseconds / 1_000d);
                    dirty |= XiuxianDirtySections.QueueProgress;
                    if (current.TravelPlan is not null)
                    {
                        XiuxianWorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                        dirty |= XiuxianDirtySections.World;
                    }
                    if (RegenerateResources(state, cursor, progress))
                    {
                        dirty |= XiuxianDirtySections.Combat;
                    }
                }

                cursor = target;
                break;
            }

            cursor = finishAt;
            state.LastSimulatedAt = cursor;
            current.ProgressMilliseconds = durationMilliseconds;
            current.ProgressSeconds = current.DurationSeconds;
            if (current.TravelPlan is not null)
            {
                XiuxianWorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                dirty |= XiuxianDirtySections.World;
            }
            if (RegenerateResources(state, cursor, remainingMilliseconds))
            {
                dirty |= XiuxianDirtySections.Combat;
            }
            CompleteAction(
                state,
                current,
                definition,
                ref dirty,
                events,
                ref combatEvents);
            completedActions++;
            transitions++;
            skippedForMissingInputs.Clear();
        }

        if (transitions >= MaxTransitionsPerAdvance)
        {
            AddEvent(state, "本次快进达到保护上限，剩余行动将在下一次推进继续结算");
            dirty |= XiuxianDirtySections.Events;
            if (hasPendingQueueRotation)
            {
                dirty |= XiuxianDirtySections.Queue;
            }
        }

        if (ExpireBuffs(state, target))
        {
            dirty |= XiuxianDirtySections.Buffs;
        }

        if (dirty != XiuxianDirtySections.None)
        {
            Touch(state, dirty);
        }

        state.LastSimulatedAt = target;
        return new XiuxianSimulationResult(
            dirty,
            completedActions,
            transitions,
            events,
            combatEvents);
    }

    public static DateTimeOffset? GetNextTransitionAt(XiuxianPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PresenceMode == XiuxianPresenceMode.FullyOffline) return null;

        DateTimeOffset? next = state.Buffs
            .Where(buff => buff.ExpiresAt > state.LastSimulatedAt)
            .Select(buff => (DateTimeOffset?)NormalizeTime(buff.ExpiresAt))
            .OrderBy(value => value)
            .FirstOrDefault();
        if (state.Queue.Entries.Count == 0) return next;

        var current = state.Queue.Entries[0];
        if (!XiuxianActionCatalog.TryGet(current.ActionId, out var definition))
        {
            return state.LastSimulatedAt;
        }

        DateTimeOffset? actionDeadline;
        if (current.Status != XiuxianActionStatus.Running)
        {
            actionDeadline = state.LastSimulatedAt;
        }
        else if (definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter)
        {
            actionDeadline = GetNextCombatTransitionAt(state, current, definition);
        }
        else
        {
            var durationMilliseconds = checked((long)Math.Max(0, GetDurationSeconds(current, definition)) * 1_000);
            var progressMilliseconds = current.ProgressMilliseconds > 0
                ? current.ProgressMilliseconds
                : checked((long)Math.Max(0, current.ProgressSeconds) * 1_000);
            actionDeadline = state.LastSimulatedAt.AddMilliseconds(
                Math.Max(1, durationMilliseconds - Math.Clamp(progressMilliseconds, 0, durationMilliseconds)));
        }

        return next is null || actionDeadline < next ? actionDeadline : next;
    }

    private static DateTimeOffset? GetNextCombatTransitionAt(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition)
    {
        var expedition = current.CombatExpedition ?? CreateCombatExpedition(definition);
        if (expedition.Phase != XiuxianCombatExpeditionPhase.Combat)
        {
            var remaining = Math.Max(
                1,
                expedition.PhaseDurationMilliseconds - expedition.PhaseElapsedMilliseconds);
            return state.LastSimulatedAt.AddMilliseconds(remaining);
        }

        var encounterId = ResolveCombatEncounterId(state, definition, expedition);
        var battleLoadout = expedition.BattleLoadout ?? state.Combat;
        var battleBuffs = expedition.BattleBuffs ?? state.Buffs;
        var battleInventory = expedition.BattleInventory ?? state.Inventory.Items;
        var initialPlayer = XiuxianCombatSimulator.CreatePlayerState(
            battleLoadout,
            battleBuffs,
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints,
            state.Stats.Spirit);
        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            battleLoadout,
            battleBuffs,
            battleInventory,
            encounterId,
            expedition.BattleStartedAt,
            TimeSpan.FromMilliseconds(Math.Max(0, expedition.PhaseElapsedMilliseconds)),
            expedition.CompletedEncountersBeforeBattle,
            initialPlayer,
            stopAfterFirstVictory: true,
            playerSpirit: state.Stats.Spirit);
        if (result.OutcomeMilliseconds is not null)
        {
            return expedition.BattleStartedAt.AddMilliseconds(result.OutcomeMilliseconds.Value);
        }

        return result.NextEventMilliseconds is { } nextEvent
            ? expedition.BattleStartedAt.AddMilliseconds(nextEvent)
            : null;
    }

    private static void AdvanceCombatAction(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition,
        ref DateTimeOffset cursor,
        DateTimeOffset target,
        ref XiuxianDirtySections dirty,
        ref int completedActions,
        ref int transitions,
        ref int combatEvents)
    {
        var expedition = current.CombatExpedition ??= CreateCombatExpedition(definition);
        EnsureCombatResources(state, cursor);
        current.DurationSeconds = 0;

        // 旧快照中的 Recovery 代表战败后回满资源。现行规则统一进入可衰减的重新搜索，
        // 只迁移阶段和耗时，不改动玩家已经保留的生命与灵力。
        if (expedition.Phase == XiuxianCombatExpeditionPhase.Recovery)
        {
            var encounter = GetCombatEncounter(state, definition, expedition);
            expedition.Phase = XiuxianCombatExpeditionPhase.Search;
            expedition.PhaseDurationMilliseconds = CalculateCombatSearchMilliseconds(encounter, state.Combat);
            expedition.PhaseElapsedMilliseconds = Math.Min(
                expedition.PhaseElapsedMilliseconds,
                expedition.PhaseDurationMilliseconds);
            current.ProgressSeconds = ToDisplaySeconds(expedition.PhaseElapsedMilliseconds);
            current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
            dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.QueueProgress;
        }

        if (state.CombatResources.Hitpoints <= 0
            && expedition.Phase is XiuxianCombatExpeditionPhase.InitialSearch or XiuxianCombatExpeditionPhase.Search)
        {
            var encounter = GetCombatEncounter(state, definition, expedition);
            expedition.Phase = XiuxianCombatExpeditionPhase.Death;
            expedition.PhaseElapsedMilliseconds = 0;
            expedition.PhaseDurationMilliseconds = Math.Max(1, encounter.PlayerRespawnMilliseconds);
            current.ProgressSeconds = 0;
            current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
            dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.QueueProgress | XiuxianDirtySections.Combat;
        }

        while (cursor <= target && transitions < MaxTransitionsPerAdvance)
        {
            if (expedition.Phase == XiuxianCombatExpeditionPhase.Death)
            {
                var duration = Math.Max(1, expedition.PhaseDurationMilliseconds);
                var available = GetAvailableMilliseconds(cursor, target);
                var remaining = Math.Max(0, duration - expedition.PhaseElapsedMilliseconds);
                if (available < remaining)
                {
                    expedition.PhaseElapsedMilliseconds = checked(expedition.PhaseElapsedMilliseconds + available);
                    current.ProgressSeconds = ToDisplaySeconds(expedition.PhaseElapsedMilliseconds);
                    current.DurationSeconds = ToDisplaySeconds(duration);
                    cursor = target;
                    dirty |= XiuxianDirtySections.QueueProgress;
                    return;
                }

                cursor = cursor.AddMilliseconds(remaining);
                expedition.PhaseElapsedMilliseconds = 0;
                current.ProgressSeconds = 0;
                transitions++;
                SetCombatResources(state, XiuxianCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, cursor, spirit: state.Stats.Spirit));
                AddEvent(state, "你已复活。", cursor);
                dirty |= XiuxianDirtySections.Combat | XiuxianDirtySections.Events;

                if (expedition.CompleteAfterRespawn)
                {
                    expedition.CompleteAfterRespawn = false;
                    FinishCombatBatch(state, current, definition, cursor, ref dirty);
                    return;
                }

                expedition.Phase = XiuxianCombatExpeditionPhase.Search;
                var legacyEncounter = GetCombatEncounter(state, definition, expedition);
                expedition.PhaseDurationMilliseconds = CalculateCombatSearchMilliseconds(legacyEncounter, state.Combat);
                current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
                current.Status = XiuxianActionStatus.Running;
                dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.QueueProgress;
                continue;
            }

            if (expedition.Phase is XiuxianCombatExpeditionPhase.InitialSearch
                or XiuxianCombatExpeditionPhase.Search)
            {
                var duration = Math.Max(1, expedition.PhaseDurationMilliseconds);
                var available = GetAvailableMilliseconds(cursor, target);
                var remaining = Math.Max(0, duration - expedition.PhaseElapsedMilliseconds);
                if (available < remaining)
                {
                    expedition.PhaseElapsedMilliseconds = checked(expedition.PhaseElapsedMilliseconds + available);
                    current.ProgressSeconds = ToDisplaySeconds(expedition.PhaseElapsedMilliseconds);
                    current.DurationSeconds = ToDisplaySeconds(duration);
                    if (RegenerateResources(state, cursor, available))
                    {
                        dirty |= XiuxianDirtySections.Combat;
                    }
                    cursor = target;
                    dirty |= XiuxianDirtySections.QueueProgress;
                    return;
                }

                cursor = cursor.AddMilliseconds(remaining);
                expedition.PhaseElapsedMilliseconds = 0;
                current.ProgressSeconds = 0;
                transitions++;
                if (RegenerateResources(state, cursor, remaining))
                {
                    dirty |= XiuxianDirtySections.Combat;
                }

                var startedEncounterId = BeginCombatBattle(state, current, definition, cursor);
                var startedEncounter = XiuxianCombatCatalog.GetEncounter(startedEncounterId);
                current.DurationSeconds = 0;
                AddEvent(state, $"发现 {{{{encounter:{startedEncounter.Id}|{startedEncounter.Name}}}}}，战斗开始。", cursor);
                dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.QueueProgress | XiuxianDirtySections.Events;
                continue;
            }

            var encounterId = ResolveCombatEncounterId(state, definition, expedition);
            var encounter = XiuxianCombatCatalog.GetEncounter(encounterId);
            var availableCombatMilliseconds = GetAvailableMilliseconds(cursor, target);
            var requestedElapsed = checked(expedition.PhaseElapsedMilliseconds + availableCombatMilliseconds);
            var battleLoadout = expedition.BattleLoadout ?? state.Combat.Clone();
            var battleBuffs = expedition.BattleBuffs ?? [];
            var battleInventory = expedition.BattleInventory ?? new Dictionary<string, long>(StringComparer.Ordinal);
            var initialPlayer = XiuxianCombatSimulator.CreatePlayerState(
                battleLoadout,
                battleBuffs,
                expedition.BattleStartedAt,
                expedition.InitialHitpoints,
                expedition.InitialManapoints,
                state.Stats.Spirit);
            var result = XiuxianCombatSimulator.Run(
                state.PlayerId,
                battleLoadout,
                battleBuffs,
                battleInventory,
                encounterId,
                expedition.BattleStartedAt,
                TimeSpan.FromMilliseconds(requestedElapsed),
                expedition.CompletedEncountersBeforeBattle,
                initialPlayer,
                stopAfterFirstVictory: true,
                playerSpirit: state.Stats.Spirit);

            dirty |= ApplyIncrementalCombatConsumption(state, expedition, result);
            dirty |= EmitIncrementalCombatEvents(state, expedition, result, cursor);
            dirty |= ApplyIncrementalCombatSettlement(
                state,
                expedition,
                encounterId,
                result,
                expedition.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds),
                ref combatEvents);
            if (result.FinalPlayer is { } currentPlayer)
            {
                SetCombatResources(state, currentPlayer);
                dirty |= XiuxianDirtySections.Combat;
            }

            if (result.OutcomeMilliseconds is not { } outcomeMilliseconds)
            {
                expedition.PhaseElapsedMilliseconds = requestedElapsed;
                current.ProgressSeconds = ToDisplaySeconds(requestedElapsed);
                current.DurationSeconds = 0;
                cursor = target;
                dirty |= XiuxianDirtySections.QueueProgress;
                return;
            }

            var consumedMilliseconds = Math.Max(0, outcomeMilliseconds - expedition.PhaseElapsedMilliseconds);
            cursor = cursor.AddMilliseconds(consumedMilliseconds);
            expedition.PhaseElapsedMilliseconds = outcomeMilliseconds;
            state.Stats.CompletedActions++;
            completedActions++;
            transitions++;

            if (!current.IsInfinite)
            {
                current.RemainingRepeats = Math.Max(0, current.RemainingRepeats - 1);
            }

            if (!current.IsInfinite && current.RemainingRepeats == 0)
            {
                FinishCombatBatch(state, current, definition, cursor, ref dirty);
                return;
            }

            ResetBattleSnapshot(expedition);
            current.ProgressSeconds = 0;
            current.Status = XiuxianActionStatus.Running;
            expedition.Phase = XiuxianCombatExpeditionPhase.Search;
            expedition.PhaseDurationMilliseconds = CalculateCombatSearchMilliseconds(encounter, state.Combat);
            current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
            dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.QueueProgress | XiuxianDirtySections.Stats;
        }
    }

    private static string BeginCombatBattle(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition,
        DateTimeOffset startedAt)
    {
        var expedition = current.CombatExpedition ??= new XiuxianCombatExpeditionState();
        var nextBattleSequence = checked(expedition.BattleSequence + 1);
        var encounterId = SelectCombatEncounterId(state, definition, nextBattleSequence);
        EnsureCombatResources(state, startedAt);
        var initial = XiuxianCombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            startedAt,
            state.CombatResources.Hitpoints,
            state.CombatResources.Manapoints,
            state.Stats.Spirit);
        SetCombatResources(state, initial);
        expedition.Phase = XiuxianCombatExpeditionPhase.Combat;
        expedition.PhaseDurationMilliseconds = 0;
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EncounterId = encounterId;
        expedition.BattleSequence = nextBattleSequence;
        expedition.CompletedEncountersBeforeBattle = state.CombatEncounterCompletions.GetValueOrDefault(encounterId);
        expedition.BattleStartedAt = startedAt;
        expedition.InitialHitpoints = initial.Hitpoints;
        expedition.InitialManapoints = initial.Manapoints;
        expedition.EmittedEventCount = 0;
        expedition.BattleLoadout = state.Combat.Clone();
        expedition.BattleBuffs = state.Buffs.Select(CloneCombatBuff).ToList();
        expedition.BattleInventory = new Dictionary<string, long>(state.Inventory.Items, StringComparer.Ordinal);
        expedition.AppliedConsumedItems.Clear();
        expedition.AppliedDroppedItems.Clear();
        expedition.AppliedRareDroppedItems.Clear();
        expedition.AppliedWins = 0;
        expedition.AppliedLosses = 0;
        expedition.AppliedEnemiesDefeated = 0;
        expedition.AppliedProcessedEvents = 0;
        expedition.AppliedDamageDealt = 0;
        expedition.AppliedDamageTaken = 0;
        expedition.AppliedExperience = 0;
        expedition.AppliedSpirit = 0;
        expedition.CompleteAfterRespawn = false;
        return encounterId;
    }

    private static XiuxianDirtySections ApplyIncrementalCombatConsumption(
        XiuxianPlayerState state,
        XiuxianCombatExpeditionState expedition,
        XiuxianCombatRunResult result)
    {
        var changed = false;
        foreach (var consumed in result.ConsumedItems)
        {
            var applied = expedition.AppliedConsumedItems.GetValueOrDefault(consumed.Key);
            var delta = Math.Max(0, consumed.Value - applied);
            if (delta <= 0) continue;
            state.Inventory.Add(consumed.Key, -delta);
            expedition.AppliedConsumedItems[consumed.Key] = consumed.Value;
            changed = true;
        }

        return changed ? XiuxianDirtySections.Inventory : XiuxianDirtySections.None;
    }

    private static XiuxianDirtySections EmitIncrementalCombatEvents(
        XiuxianPlayerState state,
        XiuxianCombatExpeditionState expedition,
        XiuxianCombatRunResult result,
        DateTimeOffset occurredAt)
    {
        if (expedition.EmittedEventCount >= result.Events.Count) return XiuxianDirtySections.None;
        foreach (var entry in result.Events.Skip(expedition.EmittedEventCount))
        {
            AddEvent(state, $"战斗：{entry}", occurredAt);
        }
        expedition.EmittedEventCount = result.Events.Count;
        return XiuxianDirtySections.Events;
    }

    private static XiuxianDirtySections ApplyIncrementalCombatSettlement(
        XiuxianPlayerState state,
        XiuxianCombatExpeditionState expedition,
        string encounterId,
        XiuxianCombatRunResult result,
        DateTimeOffset settledAt,
        ref int combatEvents)
    {
        var dirty = XiuxianDirtySections.None;
        var processedEventDelta = Math.Max(0, result.ProcessedEvents - expedition.AppliedProcessedEvents);
        if (processedEventDelta > 0)
        {
            combatEvents = checked(combatEvents + processedEventDelta);
            expedition.AppliedProcessedEvents = result.ProcessedEvents;
        }

        var spiritDelta = Math.Max(0, result.Spirit - expedition.AppliedSpirit);
        if (spiritDelta > 0)
        {
            state.Inventory.Add(XiuxianItemIds.Spirit, spiritDelta);
            expedition.AppliedSpirit = result.Spirit;
            dirty |= XiuxianDirtySections.Inventory;
        }

        dirty |= ApplyIncrementalDrops(state, expedition.AppliedDroppedItems, result.DroppedItems);
        dirty |= ApplyIncrementalDrops(state, expedition.AppliedRareDroppedItems, result.RareDroppedItems);

        foreach (var application in result.BuffApplications)
        {
            ApplyCombatBuff(state, application, settledAt);
            dirty |= XiuxianDirtySections.Buffs;
        }

        var winsDelta = Math.Max(0, result.Wins - expedition.AppliedWins);
        var lossesDelta = Math.Max(0, result.Losses - expedition.AppliedLosses);
        var enemiesDelta = Math.Max(0, result.EnemiesDefeated - expedition.AppliedEnemiesDefeated);
        var damageDealtDelta = Math.Max(0, result.DamageDealt - expedition.AppliedDamageDealt);
        var damageTakenDelta = Math.Max(0, result.DamageTaken - expedition.AppliedDamageTaken);
        var experienceDelta = Math.Max(0, result.Experience - expedition.AppliedExperience);
        if (winsDelta > 0
            || lossesDelta > 0
            || enemiesDelta > 0
            || damageDealtDelta > 0
            || damageTakenDelta > 0
            || experienceDelta > 0)
        {
            state.Stats.CombatWins = checked(state.Stats.CombatWins + winsDelta);
            state.Stats.CombatLosses = checked(state.Stats.CombatLosses + lossesDelta);
            state.Stats.CombatEnemiesDefeated = checked(state.Stats.CombatEnemiesDefeated + enemiesDelta);
            state.Stats.CombatDamageDealt = checked(state.Stats.CombatDamageDealt + damageDealtDelta);
            state.Stats.CombatDamageTaken = checked(state.Stats.CombatDamageTaken + damageTakenDelta);
            state.Stats.CombatExperience = checked(state.Stats.CombatExperience + experienceDelta);
            state.CombatEncounterCompletions[encounterId] = checked(
                state.CombatEncounterCompletions.GetValueOrDefault(encounterId) + winsDelta);
            expedition.AppliedWins = result.Wins;
            expedition.AppliedLosses = result.Losses;
            expedition.AppliedEnemiesDefeated = result.EnemiesDefeated;
            expedition.AppliedDamageDealt = result.DamageDealt;
            expedition.AppliedDamageTaken = result.DamageTaken;
            expedition.AppliedExperience = result.Experience;
            dirty |= XiuxianDirtySections.Stats | XiuxianDirtySections.Combat;
        }

        return dirty;
    }

    private static XiuxianDirtySections ApplyIncrementalDrops(
        XiuxianPlayerState state,
        IDictionary<string, long> appliedDrops,
        IReadOnlyDictionary<string, long>? currentDrops)
    {
        if (currentDrops is not { Count: > 0 }) return XiuxianDirtySections.None;
        var changed = false;
        foreach (var drop in currentDrops)
        {
            var applied = appliedDrops.TryGetValue(drop.Key, out var appliedQuantity)
                ? appliedQuantity
                : 0;
            var delta = Math.Max(0, drop.Value - applied);
            if (delta <= 0) continue;
            state.Inventory.Add(drop.Key, delta);
            appliedDrops[drop.Key] = drop.Value;
            changed = true;
        }
        return changed ? XiuxianDirtySections.Inventory : XiuxianDirtySections.None;
    }

    private static void FinishCombatBatch(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition,
        DateTimeOffset completedAt,
        ref XiuxianDirtySections dirty)
    {
        AddEvent(state, $"行动完成：{{{{action:{definition.Id}|{definition.Name}}}}}", completedAt);
        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.DurationSeconds = 0;
            current.Status = XiuxianActionStatus.Queued;
            current.CombatExpedition = CreateCombatExpedition(definition);
            state.Queue.Entries.RemoveAt(0);
            state.Queue.Entries.Add(current);
            EnsureCombatResources(state, completedAt);
        }
        else
        {
            state.Queue.Entries.RemoveAt(0);
            EnsureAliveAfterLeavingCombat(state, completedAt);
        }
        dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.Stats | XiuxianDirtySections.Events | XiuxianDirtySections.Combat;
    }

    private static void EnsureAliveAfterLeavingCombat(XiuxianPlayerState state, DateTimeOffset at)
    {
        EnsureCombatResources(state, at);
        var attributes = XiuxianCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
        if (state.CombatResources.Hitpoints <= 0)
        {
            state.CombatResources.Hitpoints = 1;
            state.CombatResources.HitpointRemainder = 0;
            state.CombatResources.Manapoints = Math.Clamp(state.CombatResources.Manapoints, 0, attributes.MaxManapoints);
            state.CombatResources.ManapointRemainder = 0;
            AddEvent(state, "你已脱离战斗，重伤复苏（生命值恢复至 1 点）。", at);
        }
        else
        {
            state.CombatResources.Hitpoints = Math.Clamp(state.CombatResources.Hitpoints, 1, attributes.MaxHitpoints);
            state.CombatResources.Manapoints = Math.Clamp(state.CombatResources.Manapoints, 0, attributes.MaxManapoints);
        }
    }

    private static bool RegenerateResources(
        XiuxianPlayerState state,
        DateTimeOffset at,
        double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0) return false;
        EnsureCombatResources(state, at);

        if (state.CombatResources.Hitpoints <= 0) return false;

        var attributes = XiuxianCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
        var maxHp = attributes.MaxHitpoints;
        var maxMp = attributes.MaxManapoints;
        var hpRegenRatio = attributes.HpRegenPerTenSeconds;
        var mpRegenRatio = attributes.MpRegenPerTenSeconds;

        var changed = false;

        if (state.CombatResources.Hitpoints < maxHp && hpRegenRatio > 0 && maxHp > 0)
        {
            var totalGain = maxHp * hpRegenRatio * (elapsedMilliseconds / 10_000.0) + state.CombatResources.HitpointRemainder;
            var hpGain = (long)Math.Floor(totalGain);
            state.CombatResources.HitpointRemainder = totalGain - hpGain;
            if (hpGain > 0)
            {
                state.CombatResources.Hitpoints = Math.Min(maxHp, state.CombatResources.Hitpoints + hpGain);
                if (state.CombatResources.Hitpoints >= maxHp)
                {
                    state.CombatResources.HitpointRemainder = 0;
                }
                changed = true;
            }
        }
        else if (state.CombatResources.Hitpoints >= maxHp)
        {
            if (state.CombatResources.Hitpoints > maxHp)
            {
                state.CombatResources.Hitpoints = maxHp;
                changed = true;
            }
            state.CombatResources.HitpointRemainder = 0;
        }

        if (state.CombatResources.Manapoints < maxMp && mpRegenRatio > 0 && maxMp > 0)
        {
            var totalGain = maxMp * mpRegenRatio * (elapsedMilliseconds / 10_000.0) + state.CombatResources.ManapointRemainder;
            var mpGain = (long)Math.Floor(totalGain);
            state.CombatResources.ManapointRemainder = totalGain - mpGain;
            if (mpGain > 0)
            {
                state.CombatResources.Manapoints = Math.Min(maxMp, state.CombatResources.Manapoints + mpGain);
                if (state.CombatResources.Manapoints >= maxMp)
                {
                    state.CombatResources.ManapointRemainder = 0;
                }
                changed = true;
            }
        }
        else if (state.CombatResources.Manapoints >= maxMp)
        {
            if (state.CombatResources.Manapoints > maxMp)
            {
                state.CombatResources.Manapoints = maxMp;
                changed = true;
            }
            state.CombatResources.ManapointRemainder = 0;
        }

        return changed;
    }

    private static void ResetBattleSnapshot(XiuxianCombatExpeditionState expedition)
    {
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EncounterId = null;
        expedition.BattleStartedAt = default;
        expedition.InitialHitpoints = 0;
        expedition.InitialManapoints = 0;
        expedition.EmittedEventCount = 0;
        expedition.BattleLoadout = null;
        expedition.BattleBuffs.Clear();
        expedition.BattleInventory.Clear();
        expedition.AppliedConsumedItems.Clear();
        expedition.AppliedDroppedItems.Clear();
        expedition.AppliedRareDroppedItems.Clear();
        expedition.AppliedWins = 0;
        expedition.AppliedLosses = 0;
        expedition.AppliedEnemiesDefeated = 0;
        expedition.AppliedProcessedEvents = 0;
        expedition.AppliedDamageDealt = 0;
        expedition.AppliedDamageTaken = 0;
        expedition.AppliedExperience = 0;
        expedition.AppliedSpirit = 0;
    }

    private static void EnsureCombatResources(XiuxianPlayerState state, DateTimeOffset at)
    {
        state.CombatResources ??= new XiuxianCombatResourceState();
        if (state.CombatResources.Initialized) return;
        SetCombatResources(state, XiuxianCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, at, spirit: state.Stats.Spirit));
    }

    private static void SetCombatResources(XiuxianPlayerState state, XiuxianCombatPlayerState player)
    {
        state.CombatResources ??= new XiuxianCombatResourceState();
        state.CombatResources.Initialized = true;
        state.CombatResources.Hitpoints = Math.Clamp(player.Hitpoints, 0, player.MaxHitpoints);
        state.CombatResources.Manapoints = Math.Clamp(player.Manapoints, 0, player.MaxManapoints);
    }

    private static XiuxianBuffState CloneCombatBuff(XiuxianBuffState buff) => new()
    {
        BuffId = buff.BuffId,
        Name = buff.Name,
        Stacks = buff.Stacks,
        QiGainMultiplier = buff.QiGainMultiplier,
        AttackLevelRatio = buff.AttackLevelRatio,
        DamageRatio = buff.DamageRatio,
        AttackSpeedRatio = buff.AttackSpeedRatio,
        GatheringOutputMultiplier = buff.GatheringOutputMultiplier,
        AlchemyOutputMultiplier = buff.AlchemyOutputMultiplier,
        SourceItemId = buff.SourceItemId,
        ExpiresAt = buff.ExpiresAt
    };

    private static int CalculateCombatSearchMilliseconds(
        XiuxianCombatEncounterDefinition encounter,
        XiuxianCombatLoadout loadout)
    {
        var baseMilliseconds = Math.Max(1, encounter.RespawnMilliseconds);
        var ratio = Math.Clamp(loadout.SearchSpeedRatio, -0.9, 1_000);
        return Math.Clamp(
            (int)Math.Ceiling(baseMilliseconds / (1 + ratio)),
            MinimumCombatSearchMilliseconds,
            MaximumCombatSearchMilliseconds);
    }

    private static XiuxianCombatEncounterDefinition GetCombatEncounter(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        XiuxianCombatExpeditionState expedition)
    {
        return XiuxianCombatCatalog.GetEncounter(ResolveCombatEncounterId(state, definition, expedition));
    }

    private static string ResolveCombatEncounterId(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        XiuxianCombatExpeditionState expedition)
    {
        if (!string.IsNullOrWhiteSpace(expedition.EncounterId)
            && XiuxianCombatCatalog.TryGetEncounter(expedition.EncounterId, out _))
        {
            return expedition.EncounterId;
        }

        var battleSequence = Math.Max(1, expedition.BattleSequence);
        expedition.EncounterId = SelectCombatEncounterId(state, definition, battleSequence);
        return expedition.EncounterId;
    }

    private static string SelectCombatEncounterId(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        long battleSequence)
    {
        if (state.World.NodeId is { Length: > 0 } nodeId)
        {
            var node = XiuxianWorldCatalog.GetNode(state.World.MapId, nodeId);
            var candidates = node.EncounterIds
                .Where(id => XiuxianCombatCatalog.TryGetEncounter(id, out _))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates[SelectStableEncounterIndex(state.PlayerId, nodeId, battleSequence, candidates.Length)];
            }
        }

        return definition.CombatEncounterId is { Length: > 0 } configured
            && XiuxianCombatCatalog.TryGetEncounter(configured, out _)
                ? configured
                : XiuxianCombatEncounterIds.QiBeast;
    }

    private static int SelectStableEncounterIndex(
        Guid playerId,
        string nodeId,
        long battleSequence,
        int count)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in playerId.ToByteArray())
        {
            hash = (hash ^ value) * prime;
        }
        foreach (var value in nodeId)
        {
            hash = (hash ^ value) * prime;
        }
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ (byte)(battleSequence >> shift)) * prime;
        }
        return (int)(hash % (uint)Math.Max(1, count));
    }

    private static long GetAvailableMilliseconds(DateTimeOffset cursor, DateTimeOffset target)
    {
        return Math.Max(0, (long)Math.Floor((target - cursor).TotalMilliseconds));
    }

    private static int ToDisplaySeconds(long milliseconds)
    {
        return milliseconds <= 0 ? 0 : (int)Math.Ceiling(milliseconds / 1_000d);
    }

    private static void CompleteAction(
        XiuxianPlayerState state,
        XiuxianQueuedAction current,
        XiuxianActionDefinition definition,
        ref XiuxianDirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        if (current.TravelPlan is not null)
        {
            var destination = XiuxianWorldCatalog.GetNode(
                current.TravelPlan.MapId,
                current.TravelPlan.DestinationNodeId);
            state.Stats.CompletedActions++;
            state.Queue.Entries.RemoveAt(0);
            dirty |= XiuxianDirtySections.Queue
                | XiuxianDirtySections.World
                | XiuxianDirtySections.Stats
                | XiuxianDirtySections.Events;
            AddEvent(state, $"已抵达 {{{{location:{destination.Id}|{destination.Name}}}}}");
            events.Add($"arrived:{destination.Id}");
            return;
        }

        foreach (var input in definition.Inputs)
        {
            state.Inventory.Add(input.Key, -input.Value);
        }

        if (definition.Type is XiuxianActionType.Exploration or XiuxianActionType.Alchemy)
        {
            var consumed = ConsumeAutoUseItems(state, definition.Type, state.LastSimulatedAt);
            if (consumed.Count > 0)
            {
                dirty |= XiuxianDirtySections.Inventory | XiuxianDirtySections.Buffs;
                foreach (var (itemId, quantity) in consumed)
                {
                    var itemName = XiuxianItemCatalog.TryGet(itemId, out var usedItem)
                        ? usedItem.Name
                        : itemId;
                    AddEvent(state, $"{definition.Name}自动使用 {{{{item:{itemId}|{itemName}}}}} × {quantity}");
                }
            }
        }

        foreach (var output in definition.Outputs)
        {
            if (definition.Type == XiuxianActionType.Cultivation && output.Key == XiuxianItemIds.Qi)
            {
                continue;
            }

            var multiplier = definition.Type switch
            {
                XiuxianActionType.Exploration => GetGatheringOutputMultiplier(state, state.LastSimulatedAt),
                XiuxianActionType.Alchemy => GetAlchemyOutputMultiplier(state, state.LastSimulatedAt),
                _ => 1
            };
            var amount = multiplier == 1
                ? output.Value
                : Math.Max(1, (long)Math.Ceiling(output.Value * multiplier));
            state.Inventory.Add(output.Key, amount);
        }

        switch (definition.Type)
        {
            case XiuxianActionType.Exploration:
                state.Stats.HerbsCollected += GetProducedAmount(
                    state,
                    definition,
                    XiuxianItemIds.SpiritHerb,
                    GetGatheringOutputMultiplier(state, state.LastSimulatedAt));
                break;
            case XiuxianActionType.Alchemy:
                state.Stats.PillsCrafted += GetProducedAmount(
                    state,
                    definition,
                    XiuxianItemIds.QiGatheringPill,
                    GetAlchemyOutputMultiplier(state, state.LastSimulatedAt));
                break;
            case XiuxianActionType.Cultivation:
                var consumedPills = ConsumeAutoUseItems(state, XiuxianActionType.Cultivation, state.LastSimulatedAt);
                if (consumedPills.Count > 0)
                {
                    dirty |= XiuxianDirtySections.Inventory | XiuxianDirtySections.Buffs;
                    foreach (var (itemId, quantity) in consumedPills)
                    {
                        var itemName = XiuxianItemCatalog.TryGet(itemId, out var usedItem)
                            ? usedItem.Name
                            : itemId;
                        AddEvent(state, $"修炼自动服用 {{{{item:{itemId}|{itemName}}}}} × {quantity}");
                    }
                }

                var multiplier = GetQiGainMultiplier(state, state.LastSimulatedAt);
                var baseQi = definition.Outputs.GetValueOrDefault(XiuxianItemIds.Qi);
                var qiGain = (long)Math.Round(baseQi * multiplier, MidpointRounding.AwayFromZero);
                state.Inventory.Add(XiuxianItemIds.Qi, qiGain);
                state.Stats.CultivationCycles++;
                state.Stats.Qi += qiGain;
                var (actualSpiritGain, foundationGain, foundationConsumed) = AddCultivationSpirit(state, 1);
                if (foundationGain > 0)
                {
                    AddEvent(state, $"当前境界已满，运转吐纳将修为转化为 {foundationGain:F0} 点道基底蕴");
                }
                else if (foundationConsumed > 0)
                {
                    AddEvent(state, $"底蕴化生，获 3 倍修为加速 (+{actualSpiritGain:F0} 修为，消耗 {foundationConsumed:F0} 点底蕴)");
                }
                dirty |= XiuxianDirtySections.Stats;
                break;
            case XiuxianActionType.Combat:
                CompleteCombatAction(
                    state,
                    definition,
                    ref dirty,
                    events,
                    ref combatEvents);
                break;
        }

        state.Stats.CompletedActions++;
        dirty |= XiuxianDirtySections.Queue | XiuxianDirtySections.Inventory | XiuxianDirtySections.Stats | XiuxianDirtySections.Events;
        AddEvent(state, $"行动完成：{{{{action:{definition.Id}|{definition.Name}}}}}");
        events.Add($"completed:{definition.Id}");

        EnsureTotalRepeats(current);
        if (current.IsInfinite || current.RemainingRepeats > 1)
        {
            if (!current.IsInfinite)
            {
                current.RemainingRepeats--;
            }

            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.Status = XiuxianActionStatus.Queued;
            return;
        }

        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.Status = XiuxianActionStatus.Queued;
            state.Queue.Entries.RemoveAt(0);
            state.Queue.Entries.Add(current);
            return;
        }

        state.Queue.Entries.RemoveAt(0);
    }

    private static List<(string ItemId, int Quantity)> ConsumeAutoUseItems(
        XiuxianPlayerState state,
        XiuxianActionType actionType,
        DateTimeOffset now)
    {
        var consumed = new List<(string ItemId, int Quantity)>();
        if (!state.Queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
        {
            return consumed;
        }

        // 每格独立结算：单格缺物或不可用不影响其他格。
        foreach (var slot in slots)
        {
            if (slot is null
                || !slot.Enabled
                || string.IsNullOrWhiteSpace(slot.ItemId)
                || !XiuxianItemCatalog.TryGet(slot.ItemId, out var item)
                || item.Consumable is null
                || !item.Consumable.UsableInActionTypes.Contains(actionType)
                || state.Inventory.Get(slot.ItemId) < slot.Quantity)
            {
                continue;
            }

            state.Inventory.Add(slot.ItemId, -slot.Quantity);
            ApplyBuffEffects(state, item, slot.Quantity, now);
            consumed.Add((slot.ItemId, slot.Quantity));
        }

        return consumed;
    }

    private static void ApplyBuffEffects(
        XiuxianPlayerState state,
        XiuxianItemDefinition item,
        int quantity,
        DateTimeOffset now)
    {
        if (item.Consumable is null) return;
        foreach (var effect in item.Consumable.Buffs)
        {
            var existing = state.Buffs.FirstOrDefault(buff => buff.BuffId == effect.BuffId);
            var stacks = existing is null
                ? quantity
                : effect.Stackable
                    ? Math.Min(Math.Max(1, effect.MaxStacks), existing.Stacks + quantity)
                    : 1;
            var multiplier = effect.QiGainMultiplier > 1
                ? 1 + stacks * (effect.QiGainMultiplier - 1)
                : effect.QiGainMultiplier;
            if (existing is null)
            {
                state.Buffs.Add(new XiuxianBuffState
                {
                    BuffId = effect.BuffId,
                    Name = effect.Name,
                    Stacks = stacks,
                    QiGainMultiplier = multiplier,
                    AttackLevelRatio = effect.AttackLevelRatio,
                    DamageRatio = effect.DamageRatio,
                    AttackSpeedRatio = effect.AttackSpeedRatio,
                    GatheringOutputMultiplier = effect.GatheringOutputMultiplier,
                    AlchemyOutputMultiplier = effect.AlchemyOutputMultiplier,
                    SourceItemId = item.Id,
                    ExpiresAt = now.AddSeconds(Math.Max(1, effect.DurationSeconds))
                });
            }
            else
            {
                existing.Stacks = stacks;
                existing.ExpiresAt = now.AddSeconds(Math.Max(1, effect.DurationSeconds));
                existing.QiGainMultiplier = multiplier;
                existing.AttackLevelRatio = effect.AttackLevelRatio;
                existing.DamageRatio = effect.DamageRatio;
                existing.AttackSpeedRatio = effect.AttackSpeedRatio;
                existing.GatheringOutputMultiplier = effect.GatheringOutputMultiplier;
                existing.AlchemyOutputMultiplier = effect.AlchemyOutputMultiplier;
                existing.SourceItemId = item.Id;
            }
        }
    }

    private static void CompleteCombatAction(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        ref XiuxianDirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        var encounterId = definition.CombatEncounterId ?? XiuxianCombatEncounterIds.QiBeast;
        var encounter = XiuxianCombatCatalog.GetEncounter(encounterId);
        var searchSeconds = Math.Clamp(
            (int)Math.Ceiling(Math.Max(0, encounter.SearchMilliseconds) / 1_000d),
            0,
            definition.DurationSeconds);
        var combatSeconds = Math.Max(0, definition.DurationSeconds - searchSeconds);
        var startedAt = state.LastSimulatedAt
            .AddSeconds(-definition.DurationSeconds)
            .AddSeconds(searchSeconds);
        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            encounterId,
            startedAt,
            TimeSpan.FromSeconds(combatSeconds),
            state.CombatEncounterCompletions.GetValueOrDefault(encounterId),
            playerSpirit: state.Stats.Spirit);
        ApplyCombatRunResult(
            state,
            definition,
            encounterId,
            result,
            state.LastSimulatedAt,
            applyConsumedItems: true,
            ref dirty,
            ref combatEvents);
        foreach (var entry in result.Events) events.Add($"战斗：{entry}");
    }

    private static void ApplyCombatRunResult(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        string encounterId,
        XiuxianCombatRunResult result,
        DateTimeOffset completedAt,
        bool applyConsumedItems,
        ref XiuxianDirtySections dirty,
        ref int combatEvents)
    {
        combatEvents = checked(combatEvents + result.ProcessedEvents);

        if (applyConsumedItems)
        {
            foreach (var consumed in result.ConsumedItems)
            {
                state.Inventory.Add(consumed.Key, -consumed.Value);
            }
        }

        state.Inventory.Add(XiuxianItemIds.Spirit, result.Spirit);
        if (result.DroppedItems is { Count: > 0 })
        {
            foreach (var drop in result.DroppedItems)
            {
                state.Inventory.Add(drop.Key, drop.Value);
            }
        }
        else if (result.DroppedQuantity > 0)
        {
            var fallbackEncounter = XiuxianCombatCatalog.GetEncounter(encounterId);
            if (!string.IsNullOrWhiteSpace(fallbackEncounter.DropItemId))
            {
                state.Inventory.Add(fallbackEncounter.DropItemId, result.DroppedQuantity);
            }
        }

        if (result.RareDroppedItems is { Count: > 0 })
        {
            foreach (var drop in result.RareDroppedItems)
            {
                state.Inventory.Add(drop.Key, drop.Value);
            }
        }

        foreach (var application in result.BuffApplications)
        {
            ApplyCombatBuff(state, application, completedAt);
        }

        state.Stats.CombatWins += result.Wins;
        state.Stats.CombatLosses += result.Losses;
        state.Stats.CombatExperience += result.Experience;
        state.Stats.CombatDamageDealt += result.DamageDealt;
        state.Stats.CombatDamageTaken += result.DamageTaken;
        state.Stats.CombatEnemiesDefeated += result.EnemiesDefeated;
        state.CombatEncounterCompletions[encounterId] = checked(
            state.CombatEncounterCompletions.GetValueOrDefault(encounterId) + result.Wins);
        dirty |= XiuxianDirtySections.Inventory
            | XiuxianDirtySections.Buffs
            | XiuxianDirtySections.Stats
            | XiuxianDirtySections.Combat;
    }

    private static void ApplyCombatBuff(
        XiuxianPlayerState state,
        XiuxianCombatBuffApplication application,
        DateTimeOffset now)
    {
        var existing = state.Buffs.FirstOrDefault(buff => buff.BuffId == application.BuffId);
        if (existing is null)
        {
            state.Buffs.Add(new XiuxianBuffState
            {
                BuffId = application.BuffId,
                Name = application.Name,
                Stacks = application.Stacks,
                QiGainMultiplier = application.QiGainMultiplier,
                AttackLevelRatio = application.AttackLevelRatio,
                DamageRatio = application.DamageRatio,
                AttackSpeedRatio = application.AttackSpeedRatio,
                SourceItemId = application.SourceItemId,
                ExpiresAt = now.AddMilliseconds(application.ExpiresAfterMilliseconds)
            });
            return;
        }

        existing.Stacks = application.Stacks;
        existing.QiGainMultiplier = application.QiGainMultiplier;
        existing.AttackLevelRatio = application.AttackLevelRatio;
        existing.DamageRatio = application.DamageRatio;
        existing.AttackSpeedRatio = application.AttackSpeedRatio;
        existing.SourceItemId = application.SourceItemId;
        existing.ExpiresAt = now.AddMilliseconds(application.ExpiresAfterMilliseconds);
    }

    private static bool CanStartAction(
        XiuxianPlayerState state,
        XiuxianQueuedAction queuedAction,
        XiuxianActionDefinition definition)
    {
        if (definition.ExecutionKind == XiuxianActionExecutionKind.Travel)
        {
            return IsValidTravelPlan(state.World, queuedAction.TravelPlan);
        }

        return XiuxianWorldCatalog.CanPerformAction(state.World, definition)
            && definition.Inputs.All(input => state.Inventory.Get(input.Key) >= input.Value);
    }

    private static bool IsValidTravelPlan(XiuxianWorldState world, XiuxianTravelPlan? plan)
    {
        if (plan is null
            || plan.TotalDurationSeconds <= 0
            || plan.Segments.Count == 0
            || !string.Equals(world.MapId, plan.MapId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            _ = XiuxianWorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
            var duration = 0;
            foreach (var segment in plan.Segments)
            {
                _ = XiuxianWorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
                if (segment.DurationSeconds <= 0
                    || !double.IsFinite(segment.StartProgress)
                    || !double.IsFinite(segment.EndProgress)
                    || segment.StartProgress is < 0 or > 1
                    || segment.EndProgress is < 0 or > 1)
                {
                    return false;
                }

                duration = checked(duration + segment.DurationSeconds);
            }

            return duration == plan.TotalDurationSeconds;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool TryValidateConsumableSlots(
        IReadOnlyList<XiuxianConsumableSlot>? requested,
        out string error)
    {
        if (requested is null)
        {
            error = string.Empty;
            return true;
        }

        if (requested.Count > XiuxianCombatLimits.ConsumableSlotCount
            || requested.Any(slot => slot.Quantity is < 1 or > 99))
        {
            error = "消耗品槽数量或使用数量无效。";
            return false;
        }

        if (requested.Any(slot => !string.IsNullOrWhiteSpace(slot.ItemId)
            && !XiuxianItemCatalog.CanUseInAction(slot.ItemId, XiuxianActionType.Combat)))
        {
            error = "存在不能用于战斗的物品。";
            return false;
        }

        if (requested.Any(slot => !ValidateTriggers(slot.Triggers)))
        {
            error = "消耗品触发条件无效。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static List<XiuxianConsumableSlot> CloneConsumableSlots(
        IReadOnlyList<XiuxianConsumableSlot> slots)
    {
        return slots
            .Select(slot => new XiuxianConsumableSlot
            {
                ItemId = slot.ItemId,
                Quantity = slot.Quantity,
                Enabled = slot.Enabled && !string.IsNullOrWhiteSpace(slot.ItemId),
                Triggers = CloneTriggers(slot.Triggers)
            })
            .Concat(Enumerable.Range(
                slots.Count,
                XiuxianCombatLimits.ConsumableSlotCount - slots.Count).Select(_ => new XiuxianConsumableSlot()))
            .ToList();
    }

    private static bool ValidateTriggers(IReadOnlyList<XiuxianCombatTrigger>? triggers)
    {
        if (triggers is null) return true;
        if (triggers.Count > XiuxianCombatLimits.TriggerCountPerSlot) return false;
        return triggers.All(trigger =>
            Enum.IsDefined(trigger.Dependency)
            && Enum.IsDefined(trigger.Condition)
            && Enum.IsDefined(trigger.Comparator)
            && double.IsFinite(trigger.Value)
            && (trigger.Condition != XiuxianCombatTriggerCondition.BuffStacks
                || !string.IsNullOrWhiteSpace(trigger.BuffId)));
    }

    private static bool ValidateCombatMultiplier(double? value)
    {
        return value is null
            || (double.IsFinite(value.Value) && value.Value >= -1 && value.Value <= 1_000);
    }

    private static bool ValidateTenacity(double? value)
    {
        return value is null
            || (double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 100_000);
    }

    private static bool TryNormalizeEquipment(
        IReadOnlyList<XiuxianEquippedItem> source,
        out List<XiuxianEquippedItem> normalized,
        out string error)
    {
        normalized = [];
        error = string.Empty;
        if (source.Count > XiuxianCombatLimits.EquipmentSlotCount)
        {
            error = $"装备数量不能超过 {XiuxianCombatLimits.EquipmentSlotCount} 件。";
            return false;
        }

        var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item is null
                || !XiuxianCombatCatalog.TryGetEquipment(item.ItemId, out var definition)
                || item.EnhancementLevel is < 0 or > XiuxianCombatLimits.MaxEnhancementLevel)
            {
                error = "装备 ID 或强化等级无效。";
                return false;
            }

            if (!occupiedSlots.Add(definition.Slot))
            {
                error = $"装备槽位 {definition.Slot} 不能重复占用。";
                return false;
            }

            normalized.Add(item.Clone());
        }

        if (occupiedSlots.Contains("two-hand") && occupiedSlots.Contains("main-hand"))
        {
            error = "双手法器不能与主手法器同时装备。";
            return false;
        }

        return true;
    }

    private static bool NormalizeEquipment(List<XiuxianEquippedItem> equipment)
    {
        if (TryNormalizeEquipment(equipment, out var normalized, out _)
            && normalized.Count == equipment.Count)
        {
            return false;
        }

        equipment.Clear();
        return true;
    }

    private static bool NormalizeCombatEffectRatios(XiuxianCombatLoadout combat)
    {
        var changed = false;
        changed |= AssignNormalizedRatio(combat.PierceChance, 1, value => combat.PierceChance = value);
        changed |= AssignNormalizedRatio(combat.PenetrationRatio, 1_000, value => combat.PenetrationRatio = value);
        changed |= AssignNormalizedRatio(combat.CurseRatio, 10, value => combat.CurseRatio = value);
        changed |= AssignNormalizedRatio(combat.FuryRatio, 10, value => combat.FuryRatio = value);
        changed |= AssignNormalizedRatio(combat.WeakenRatio, 10, value => combat.WeakenRatio = value);
        changed |= AssignNormalizedRatio(combat.LifeStealRatio, 10, value => combat.LifeStealRatio = value);
        changed |= AssignNormalizedRatio(combat.ArmorDamageRatio, 10, value => combat.ArmorDamageRatio = value);
        changed |= AssignNormalizedRatio(combat.HpDrainRatio, 10, value => combat.HpDrainRatio = value);
        return changed;
    }

    private static bool AssignNormalizedRatio(double value, double maximum, Action<double> assign)
    {
        if (double.IsFinite(value) && value >= 0 && value <= maximum) return false;
        assign(0);
        return true;
    }

    private static List<XiuxianCombatTrigger> CloneTriggers(
        IReadOnlyList<XiuxianCombatTrigger>? triggers)
    {
        return triggers?.Select(trigger => trigger.Clone()).ToList() ?? [];
    }

    private static bool TriggersEqual(
        IReadOnlyList<XiuxianCombatTrigger>? left,
        IReadOnlyList<XiuxianCombatTrigger>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        for (var index = 0; index < leftCount; index++)
        {
            var a = left![index];
            var b = right![index];
            if (a.Dependency != b.Dependency
                || a.Condition != b.Condition
                || a.Comparator != b.Comparator
                || a.Value != b.Value
                || !string.Equals(a.BuffId, b.BuffId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // 旧版快照的灵食/灵饮分组并入统一的消耗品槽：按先灵食后灵饮的顺序取有内容的前 3 格，其余丢弃。
    private static bool MigrateLegacyConsumableSlots(XiuxianCombatLoadout combat)
    {
        if (combat.FoodSlots.Count == 0 && combat.DrinkSlots.Count == 0) return false;
        var legacy = combat.FoodSlots
            .Concat(combat.DrinkSlots)
            .Where(slot => slot is not null
                && (!string.IsNullOrWhiteSpace(slot.ItemId) || slot.Triggers is { Count: > 0 }))
            .Take(XiuxianCombatLimits.ConsumableSlotCount)
            .ToList();
        if (legacy.Count > 0
            && combat.ConsumableSlots.All(slot => slot is null || string.IsNullOrWhiteSpace(slot.ItemId)))
        {
            combat.ConsumableSlots = legacy;
        }

        combat.FoodSlots.Clear();
        combat.DrinkSlots.Clear();
        return true;
    }

    private static bool NormalizeSlots<T>(List<T> slots, int expectedCount, Func<T> factory)
        where T : class
    {
        var changed = false;
        if (slots.Count > expectedCount)
        {
            slots.RemoveRange(expectedCount, slots.Count - expectedCount);
            changed = true;
        }

        while (slots.Count < expectedCount)
        {
            slots.Add(factory());
            changed = true;
        }

        for (var index = 0; index < slots.Count; index++)
        {
            if (slots[index] is not null) continue;
            slots[index] = factory();
            changed = true;
        }

        return changed;
    }

    private static bool ExpireBuffs(XiuxianPlayerState state, DateTimeOffset now)
    {
        var removed = state.Buffs.RemoveAll(buff => buff.ExpiresAt <= now);
        return removed > 0;
    }

    private static double GetQiGainMultiplier(XiuxianPlayerState state, DateTimeOffset now)
    {
        var buffMultiplier = state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.QiGainMultiplier)
            .DefaultIfEmpty(1)
            .Max();
        return buffMultiplier * XiuxianWorldCatalog.GetCultivationMultiplier(state.World);
    }

    private static double GetGatheringOutputMultiplier(XiuxianPlayerState state, DateTimeOffset now)
    {
        return state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.GatheringOutputMultiplier)
            .Where(multiplier => multiplier > 0)
            .DefaultIfEmpty(1)
            .Max();
    }

    private static double GetAlchemyOutputMultiplier(XiuxianPlayerState state, DateTimeOffset now)
    {
        return state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.AlchemyOutputMultiplier)
            .Where(multiplier => multiplier > 0)
            .DefaultIfEmpty(1)
            .Max();
    }

    private static long GetProducedAmount(
        XiuxianPlayerState state,
        XiuxianActionDefinition definition,
        string itemId,
        double multiplier)
    {
        var baseAmount = definition.Outputs.GetValueOrDefault(itemId);
        return multiplier == 1
            ? baseAmount
            : Math.Max(1, (long)Math.Ceiling(baseAmount * multiplier));
    }

    private static XiuxianQueuedAction CreateQueuedAction(string actionId, int repeats = 1)
    {
        var definition = XiuxianActionCatalog.Get(actionId);
        return new XiuxianQueuedAction
        {
            ActionId = actionId,
            TotalRepeats = repeats,
            RemainingRepeats = repeats,
            DurationSeconds = definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter
                ? 0
                : definition.DurationSeconds,
            CombatExpedition = definition.ExecutionKind == XiuxianActionExecutionKind.CombatEncounter
                ? CreateCombatExpedition(definition)
                : null
        };
    }

    private static XiuxianCombatExpeditionState CreateCombatExpedition(XiuxianActionDefinition definition)
    {
        var encounterId = definition.CombatEncounterId ?? XiuxianCombatEncounterIds.QiBeast;
        var encounter = XiuxianCombatCatalog.GetEncounter(encounterId);
        return new XiuxianCombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, encounter.SearchMilliseconds)
        };
    }

    private static bool NormalizeLearnedAbilities(XiuxianCombatLoadout combat)
    {
        var learned = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var abilityId in combat.LearnedAbilityIds)
        {
            if (!string.IsNullOrWhiteSpace(abilityId)
                && XiuxianCombatCatalog.TryGetAbility(abilityId, out _)
                && learned.Add(abilityId))
            {
                continue;
            }

            changed = true;
        }

        foreach (var slot in combat.AbilitySlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.AbilityId)
                && XiuxianCombatCatalog.TryGetAbility(slot.AbilityId, out _)
                && learned.Add(slot.AbilityId))
            {
                changed = true;
            }
        }

        if (learned.Count == 0)
        {
            learned.Add(XiuxianCombatAbilityIds.FiveElementsFlare);
            changed = true;
        }

        var normalized = learned.OrderBy(abilityId => abilityId, StringComparer.Ordinal).ToList();
        if (!changed && combat.LearnedAbilityIds.SequenceEqual(normalized, StringComparer.Ordinal)) return false;
        combat.LearnedAbilityIds = normalized;
        return true;
    }

    private static int GetDurationSeconds(
        XiuxianQueuedAction queuedAction,
        XiuxianActionDefinition definition)
    {
        return queuedAction.TravelPlan?.TotalDurationSeconds ?? definition.DurationSeconds;
    }

    private static void EnsureTotalRepeats(XiuxianQueuedAction action)
    {
        if (action.TotalRepeats != 0) return;
        action.TotalRepeats = NormalizeRepeats(action.RemainingRepeats);
    }

    private static int FindLegacyMissingPrefixCount(
        IReadOnlyList<XiuxianQueuedAction> entries,
        IReadOnlyList<XiuxianQueuedAction> template)
    {
        if (entries.Count > template.Count) return -1;
        var missingPrefixCount = template.Count - entries.Count;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!string.Equals(
                    entries[index].ActionId,
                    template[index + missingPrefixCount].ActionId,
                    StringComparison.Ordinal))
            {
                return -1;
            }
        }

        return missingPrefixCount;
    }

    private static int GetConfiguredRepeats(XiuxianQueuedAction action)
    {
        return action.TotalRepeats == 0 ? action.RemainingRepeats : action.TotalRepeats;
    }

    private static int NormalizeRepeats(int repeats)
    {
        return repeats < 0 ? -1 : Math.Max(1, repeats);
    }

    private static bool ValidateSearchSpeedRatio(double? value)
    {
        return value is null || (double.IsFinite(value.Value) && value.Value is >= -0.9 and <= 1_000);
    }

    private static bool SetTotalRepeats(XiuxianQueuedAction action, int repeats)
    {
        var normalized = NormalizeRepeats(repeats);
        if (action.TotalRepeats == normalized) return false;
        action.TotalRepeats = normalized;
        return true;
    }

    private static DateTimeOffset NormalizeTime(DateTimeOffset value)
    {
        return new DateTimeOffset(
            value.UtcDateTime.AddTicks(-(value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)),
            TimeSpan.Zero);
    }

    private static void Touch(XiuxianPlayerState state, XiuxianDirtySections sections)
    {
        state.Version++;
        state.DirtySections |= sections;
    }

    private static void AddEvent(
        XiuxianPlayerState state,
        string message,
        DateTimeOffset? occurredAt = null)
    {
        var entry = new XiuxianLogEvent
        {
            Sequence = checked(++state.EventSequence),
            OccurredAt = occurredAt ?? state.LastSimulatedAt,
            Template = message
        };

        state.EventLog.Add($"[{entry.OccurredAt:MM-dd HH:mm:ss}] {message}");
        state.StructuredEventLog.Add(entry);
        if (state.PresenceMode == XiuxianPresenceMode.Online)
        {
            state.PendingEvents.Add(entry);
        }
        if (state.EventLog.Count > EventLogLimit)
        {
            state.EventLog.RemoveRange(0, state.EventLog.Count - EventLogLimit);
        }

        if (state.StructuredEventLog.Count > EventLogLimit)
        {
            state.StructuredEventLog.RemoveRange(0, state.StructuredEventLog.Count - EventLogLimit);
        }

        if (state.PendingEvents.Count > PendingEventLimit)
        {
            state.PendingEvents.RemoveRange(0, state.PendingEvents.Count - PendingEventLimit);
        }
    }
}

public sealed record XiuxianCommandResult(bool Success, string Code, string Message)
{
    public static XiuxianCommandResult Ok(string message) => new(true, "OK", message);

    public static XiuxianCommandResult Fail(string code, string message) => new(false, code, message);
}
