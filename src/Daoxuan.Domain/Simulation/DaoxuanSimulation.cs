using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.Simulation;

/// <summary>
/// 行动队列的唯一结算入口。
/// 结算按“行动完成边界”推进，不按离线时长逐秒循环，服务重启后可以直接快进到目标时间。
/// </summary>
public static class DaoxuanSimulation
{
    private const int MaxTransitionsPerAdvance = 2_000_000;
    private const int EventLogLimit = 40;
    private const int PendingEventLimit = 4_096;
    private const int MinimumCombatSearchMilliseconds = 1_000;
    private const int MaximumCombatSearchMilliseconds = 60_000;

    /// <summary>
    /// 为新创建的玩家生成默认初始聚合状态，包括默认采集/炼丹/修炼队列。
    /// </summary>
    /// <param name="playerId">玩家唯一 GUID。</param>
    /// <param name="name">玩家道号/昵称。</param>
    /// <param name="now">创建时间戳。</param>
    /// <param name="account">所属账号标识。</param>
    /// <returns>初始化的玩家聚合根状态对象。</returns>
    public static DaoxuanPlayerState CreateDefaultState(
        Guid playerId,
        string? name = null,
        DateTimeOffset? now = null,
        string? account = null)
    {
        var timestamp = NormalizeTime(now ?? DateTimeOffset.UtcNow);
        var resolvedName = string.IsNullOrWhiteSpace(name) ? "无名修士" : name.Trim();
        var resolvedAccount = string.IsNullOrWhiteSpace(account) ? resolvedName : account.Trim();
        var state = new DaoxuanPlayerState
        {
            PlayerId = playerId,
            Name = resolvedName,
            Account = resolvedAccount,
            CreatedAt = timestamp,
            LastSimulatedAt = timestamp,
            PresenceMode = DaoxuanPresenceMode.Online
        };

        state.Queue.AutoUseSlots[DaoxuanActionType.Cultivation][0] = new DaoxuanAutoUseSlot
        {
            ItemId = DaoxuanItemIds.QiGatheringPill,
            Quantity = 1,
            Enabled = true
        };
        state.Queue.Entries.Add(CreateQueuedAction(DaoxuanActionIds.GatherHerbs));
        state.Queue.Entries.Add(CreateQueuedAction(DaoxuanActionIds.RefineQiPill));
        state.Queue.Entries.Add(CreateQueuedAction(DaoxuanActionIds.DrawQi));
        AddEvent(state, "行动队列已建立：采集 → 炼丹 → 修炼");
        return state;
    }

    /// <summary>
    /// 归一化校验玩家状态，修剪非法/过期数据，补齐缺失集合并标记脏段。
    /// </summary>
    /// <param name="state">待归一化的玩家状态聚合根。</param>
    /// <returns>如果有任何字段被修复或更改则返回 true，否则返回 false。</returns>
    public static bool NormalizeState(DaoxuanPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Queue ??= new DaoxuanActionQueueState();
        var queue = state.Queue;
        var queueChanged = false;
        var inventoryChanged = false;
        var combatChanged = false;
        var combatProgressChanged = false;
        if (state.Inventory is null)
        {
            state.Inventory = new DaoxuanInventoryState();
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
        state.World ??= new DaoxuanWorldState();
        var worldChanged = DaoxuanWorldCatalog.Normalize(state.World);
        queue.Entries ??= [];

        for (var index = queue.Entries.Count - 1; index >= 0; index--)
        {
            var entry = queue.Entries[index];
            if (!DaoxuanActionCatalog.TryGet(entry.ActionId, out var definition)
                || (definition.ExecutionKind == DaoxuanActionExecutionKind.Travel
                    && !IsValidTravelPlan(state.World, entry.TravelPlan)))
            {
                queue.Entries.RemoveAt(index);
                queueChanged = true;
                continue;
            }

            if (definition.ExecutionKind == DaoxuanActionExecutionKind.Travel)
            {
                entry.TotalRepeats = 1;
                entry.RemainingRepeats = 1;
                entry.DurationSeconds = entry.TravelPlan!.TotalDurationSeconds;
            }
            else if (definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter)
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
        foreach (var actionType in Enum.GetValues<DaoxuanActionType>())
        {
            if (!queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
            {
                queue.AutoUseSlots[actionType] = Enumerable
                    .Range(0, DaoxuanCombatLimits.AutoUseSlotCount)
                    .Select(_ => new DaoxuanAutoUseSlot())
                    .ToList();
                queueChanged = true;
                continue;
            }

            queueChanged |= NormalizeSlots(
                slots,
                DaoxuanCombatLimits.AutoUseSlotCount,
                static () => new DaoxuanAutoUseSlot());
        }

        state.Combat ??= new DaoxuanCombatLoadout();
        if (state.Visual is null)
        {
            state.Visual = new DaoxuanCharacterVisualDefinition
            {
                Renderer = "text",
                Source = state.Name[..1]
            };
            combatChanged = true;
        }
        if (state.CombatResources is null)
        {
            state.CombatResources = new DaoxuanCombatResourceState();
            combatChanged = true;
        }
        state.Combat.AbilitySlots ??= [];
        state.Combat.ConsumableSlots ??= [];
        state.Combat.Equipment ??= [];
        state.Combat.LearnedAbilityIds ??= [];
        if (!double.IsFinite(state.Combat.HpRegenPerTenSeconds)
            || state.Combat.HpRegenPerTenSeconds < 0)
        {
            state.Combat.HpRegenPerTenSeconds = 0.0;
            combatChanged = true;
        }
        if (!double.IsFinite(state.Combat.MpRegenPerTenSeconds)
            || state.Combat.MpRegenPerTenSeconds < 0)
        {
            state.Combat.MpRegenPerTenSeconds = 0.0;
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
        combatChanged |= NormalizeEquipment(state.Combat.Equipment);
        combatChanged |= NormalizeCombatEffectRatios(state.Combat);
        combatChanged |= NormalizeSlots(
            state.Combat.AbilitySlots,
            DaoxuanCombatLimits.AbilitySlotCount,
            static () => new DaoxuanCombatAbilitySlot());
        combatChanged |= NormalizeLearnedAbilities(state.Combat);
        combatChanged |= NormalizeSlots(
            state.Combat.ConsumableSlots,
            DaoxuanCombatLimits.ConsumableSlotCount,
            static () => new DaoxuanConsumableSlot());
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
                         .Where(pair => pair.Value < 0 || !DaoxuanCombatCatalog.TryGetEncounter(pair.Key, out _))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                state.CombatEncounterCompletions.Remove(encounterId);
                combatProgressChanged = true;
            }
        }

        var dirty = (queueChanged ? DaoxuanDirtySections.Queue : DaoxuanDirtySections.None)
            | (inventoryChanged ? DaoxuanDirtySections.Inventory : DaoxuanDirtySections.None)
            | (combatChanged ? DaoxuanDirtySections.Combat : DaoxuanDirtySections.None)
            | (combatProgressChanged ? DaoxuanDirtySections.Stats : DaoxuanDirtySections.None)
            | (worldChanged ? DaoxuanDirtySections.World : DaoxuanDirtySections.None);
        if (dirty != DaoxuanDirtySections.None) Touch(state, dirty);
        return dirty != DaoxuanDirtySections.None;
    }

    /// <summary>
    /// 向玩家行动队列尾部追加一个常规行动。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="actionId">行动原型 ID。</param>
    /// <param name="repeats">重复次数（-1 表示无限循环，或 1 ~ 1000）。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult Enqueue(
        DaoxuanPlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!DaoxuanActionCatalog.TryGet(actionId, out var definition))
        {
            return DaoxuanCommandResult.Fail("ACTION_NOT_FOUND", "行动不存在。");
        }

        if (definition.HiddenFromCatalog || definition.ExecutionKind == DaoxuanActionExecutionKind.Travel)
        {
            return DaoxuanCommandResult.Fail("ACTION_NOT_SELECTABLE", "该行动不能直接加入队列。");
        }

        if (!DaoxuanWorldCatalog.CanPerformAction(state.World, definition))
        {
            return DaoxuanCommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该行动。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return DaoxuanCommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        var action = CreateQueuedAction(actionId, repeats);
        state.Queue.Entries.Add(action);

        Touch(state, DaoxuanDirtySections.Queue | DaoxuanDirtySections.Events);
        AddEvent(state, $"已加入行动：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return DaoxuanCommandResult.Ok("行动已加入队列。");
    }

    /// <summary>
    /// 立即向队列头部插入并启动一次历练战斗行动。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="actionId">历练行动 ID。</param>
    /// <param name="repeats">重复次数。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult StartCombat(
        DaoxuanPlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!DaoxuanActionCatalog.TryGet(actionId, out var definition))
        {
            return DaoxuanCommandResult.Fail("ACTION_NOT_FOUND", "历练行动不存在。");
        }

        if (definition.ExecutionKind != DaoxuanActionExecutionKind.CombatEncounter)
        {
            return DaoxuanCommandResult.Fail("ACTION_NOT_COMBAT", "该行动不是历练战斗。");
        }

        if (!DaoxuanWorldCatalog.CanPerformAction(state.World, definition))
        {
            return DaoxuanCommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该历练。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return DaoxuanCommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            if (DaoxuanActionCatalog.TryGet(current.ActionId, out var currentDefinition)
                && currentDefinition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter)
            {
                state.Queue.Entries.RemoveAt(0);
            }
            else
            {
                current.Status = DaoxuanActionStatus.Queued;
            }
        }

        EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
        state.Queue.Entries.Insert(0, CreateQueuedAction(actionId, repeats));
        Touch(state, DaoxuanDirtySections.Queue | DaoxuanDirtySections.Combat | DaoxuanDirtySections.Events);
        AddEvent(state, $"已立即开始历练：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return DaoxuanCommandResult.Ok("历练已开始。");
    }

    /// <summary>
    /// 规划前往目标节点的大地图路线并将其置于队列首位开始赶路。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="destinationNodeId">目的地地点节点 ID。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult TravelTo(DaoxuanPlayerState state, string destinationNodeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(destinationNodeId)
            || !DaoxuanWorldCatalog.TryPlanTravel(state.World, destinationNodeId, out var plan))
        {
            return DaoxuanCommandResult.Fail("TRAVEL_ROUTE_NOT_FOUND", "无法从当前位置规划到目标地点的路线。");
        }

        var hadCombat = state.Queue.Entries.FirstOrDefault() is { } oldTop
            && DaoxuanActionCatalog.TryGet(oldTop.ActionId, out var oldTopDef)
            && (oldTopDef.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter || oldTop.CombatExpedition is not null);

        state.Queue.Entries.RemoveAll(entry => entry.TravelPlan is not null);
        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            current.Status = DaoxuanActionStatus.Queued;
        }

        var destination = DaoxuanWorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
        state.Queue.Entries.Insert(0, new DaoxuanQueuedAction
        {
            ActionId = DaoxuanActionIds.WorldTravel,
            TotalRepeats = 1,
            RemainingRepeats = 1,
            DurationSeconds = plan.TotalDurationSeconds,
            Status = DaoxuanActionStatus.Queued,
            TravelPlan = plan
        });
        var dirty = DaoxuanDirtySections.Queue | DaoxuanDirtySections.Events;
        if (hadCombat)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= DaoxuanDirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(
            state,
            $"已规划前往 {{{{location:{destination.Id}|{destination.Name}}}}}，共 {plan.Segments.Count} 段山路，预计 {plan.TotalDurationSeconds} 秒");
        return DaoxuanCommandResult.Ok($"已开始前往{destination.Name}。");
    }

    /// <summary>
    /// 从行动队列中移除指定唯一编号的行动项。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="entryId">队列项唯一 EntryId。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult Remove(DaoxuanPlayerState state, Guid entryId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        if (index < 0)
        {
            return DaoxuanCommandResult.Fail("ACTION_NOT_FOUND", "队列中没有找到该行动。");
        }

        var removed = state.Queue.Entries[index];
        var definition = DaoxuanActionCatalog.Get(removed.ActionId);
        var actionName = removed.TravelPlan is null
            ? definition.Name
            : $"赶往{DaoxuanWorldCatalog.GetNode(removed.TravelPlan.MapId, removed.TravelPlan.DestinationNodeId).Name}";
        state.Queue.Entries.RemoveAt(index);
        var dirty = DaoxuanDirtySections.Queue | DaoxuanDirtySections.Events;
        if (removed.CombatExpedition is not null || definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= DaoxuanDirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(state, $"已从队列移除行动：{actionName}");
        return DaoxuanCommandResult.Ok("行动已移除。");
    }

    /// <summary>
    /// 调整队列中某行动项的前后顺序（-1 表示前移，1 表示后移）。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="entryId">队列项 EntryId。</param>
    /// <param name="direction">移动方向（-1 或 1）。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult Move(DaoxuanPlayerState state, Guid entryId, int direction)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (direction is not (-1 or 1))
        {
            return DaoxuanCommandResult.Fail("INVALID_DIRECTION", "队列移动方向无效。");
        }

        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= state.Queue.Entries.Count)
        {
            return DaoxuanCommandResult.Fail("QUEUE_EDGE", "行动已经在队列边界。");
        }

        if (state.Queue.Entries[index].TravelPlan is not null
            || state.Queue.Entries[targetIndex].TravelPlan is not null)
        {
            return DaoxuanCommandResult.Fail("TRAVEL_PRIORITY", "赶路行动必须保持在队列首位。");
        }

        (state.Queue.Entries[index], state.Queue.Entries[targetIndex]) =
            (state.Queue.Entries[targetIndex], state.Queue.Entries[index]);
        Touch(state, DaoxuanDirtySections.Queue);
        return DaoxuanCommandResult.Ok("队列顺序已更新。");
    }

    /// <summary>
    /// 配置指定行动类型的消耗品自动使用槽位及触发条件。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="actionType">行动类型。</param>
    /// <param name="itemId">物品 ID（可为空表示清空）。</param>
    /// <param name="quantity">单次使用数量。</param>
    /// <param name="enabled">是否启用。</param>
    /// <param name="slotIndex">槽位下标（0 ~ 4）。</param>
    /// <param name="triggers">自定义触发条件列表。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult SetAutoUse(
        DaoxuanPlayerState state,
        DaoxuanActionType actionType,
        string? itemId,
        int quantity,
        bool enabled,
        int slotIndex = 0,
        IReadOnlyList<DaoxuanCombatTrigger>? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return DaoxuanCommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!Enum.IsDefined(actionType))
        {
            return DaoxuanCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        if (slotIndex < 0 || slotIndex >= DaoxuanCombatLimits.AutoUseSlotCount)
        {
            return DaoxuanCommandResult.Fail(
                "INVALID_SLOT_INDEX",
                $"自动使用槽位序号必须在 0 到 {DaoxuanCombatLimits.AutoUseSlotCount - 1} 之间。");
        }

        if (!ValidateTriggers(triggers))
        {
            return DaoxuanCommandResult.Fail("INVALID_TRIGGERS", "自动使用触发条件无效。");
        }

        if (!string.IsNullOrWhiteSpace(itemId)
            && !DaoxuanItemCatalog.CanUseInAction(itemId, actionType))
        {
            return DaoxuanCommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }

        if (!state.Queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
        {
            slots = [];
            state.Queue.AutoUseSlots[actionType] = slots;
        }

        while (slots.Count < DaoxuanCombatLimits.AutoUseSlotCount)
        {
            slots.Add(new DaoxuanAutoUseSlot());
        }

        var normalizedTriggers = triggers is { Count: > 0 } ? CloneTriggers(triggers) : null;
        var currentSlot = slots[slotIndex];
        if (currentSlot.ItemId == itemId
            && currentSlot.Quantity == quantity
            && currentSlot.Enabled == (enabled && !string.IsNullOrWhiteSpace(itemId))
            && TriggersEqual(currentSlot.Triggers, normalizedTriggers))
        {
            return DaoxuanCommandResult.Ok("自动使用槽无需更新。");
        }

        slots[slotIndex] = new DaoxuanAutoUseSlot
        {
            ItemId = itemId,
            Quantity = quantity,
            Enabled = enabled && !string.IsNullOrWhiteSpace(itemId),
            Triggers = normalizedTriggers
        };
        Touch(state, DaoxuanDirtySections.Queue);
        return DaoxuanCommandResult.Ok("自动使用槽已更新。");
    }

    /// <summary>
    /// 手动立即使用指定背包消耗品，获得对应 Buff 增益。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="itemId">消耗品 ID。</param>
    /// <param name="actionType">生效动作类型。</param>
    /// <param name="quantity">使用数量。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult UseItem(
        DaoxuanPlayerState state,
        string itemId,
        DaoxuanActionType actionType,
        int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return DaoxuanCommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!DaoxuanItemCatalog.TryGet(itemId, out var item)
            || item.Consumable is null
            || !item.Consumable.UsableInActionTypes.Contains(actionType))
        {
            return DaoxuanCommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }

        if (item.Consumable.Buffs.Count == 0
            && (item.Consumable.HitpointRestore > 0 || item.Consumable.ManapointRestore > 0))
        {
            return DaoxuanCommandResult.Fail("ITEM_REQUIRES_COMBAT", "该物品只能在历练战斗事件中自动使用。");
        }

        if (state.Inventory.Get(itemId) < quantity)
        {
            return DaoxuanCommandResult.Fail("INSUFFICIENT_ITEM", "物品数量不足。");
        }

        // 手动使用共享自动使用槽的 Buff 规则；战斗中的即时生命恢复由战斗事件处理器负责。
        state.Inventory.Add(itemId, -quantity);
        ApplyBuffEffects(state, item, quantity, state.LastSimulatedAt);
        Touch(state, DaoxuanDirtySections.Inventory | DaoxuanDirtySections.Buffs | DaoxuanDirtySections.Events);
        AddEvent(state, $"手动使用 {{{{item:{item.Id}|{item.Name}}}}} × {quantity}");
        return DaoxuanCommandResult.Ok("物品已使用。");
    }

    /// <summary>
    /// 更新玩家战斗配置方案（包括流派、伤害属性、技能槽位、消耗品槽位、装备列表及高级战斗词条加成）。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="style">战斗流派（体魄/神识/五行等）。</param>
    /// <param name="damageType">主修伤害属性（物理/真火/癸水/乙木等）。</param>
    /// <param name="abilitySlots">装配的技能槽位列表。</param>
    /// <param name="consumableSlots">装配的战斗消耗品槽位列表。</param>
    /// <param name="combatDropRate">战斗掉落率加成比例。</param>
    /// <param name="combatDropQuantity">战斗掉落数量加成比例。</param>
    /// <param name="combatRareFind">稀有掉落发现率加成比例。</param>
    /// <param name="tenacity">韧性点数。</param>
    /// <param name="searchSpeedRatio">寻怪速度加成比例。</param>
    /// <param name="equipment">穿戴装备列表。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult SetCombatLoadout(
        DaoxuanPlayerState state,
        DaoxuanCombatStyle? style,
        DaoxuanDamageType? damageType,
        IReadOnlyList<DaoxuanCombatAbilitySlot>? abilitySlots,
        IReadOnlyList<DaoxuanConsumableSlot>? consumableSlots,
        double? combatDropRate = null,
        double? combatDropQuantity = null,
        double? combatRareFind = null,
        double? tenacity = null,
        double? searchSpeedRatio = null,
        IReadOnlyList<DaoxuanEquippedItem>? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (style is not null && !Enum.IsDefined(style.Value))
        {
            return DaoxuanCommandResult.Fail("INVALID_COMBAT_STYLE", "战斗流派无效。");
        }

        if (damageType is not null && !Enum.IsDefined(damageType.Value))
        {
            return DaoxuanCommandResult.Fail("INVALID_DAMAGE_TYPE", "伤害类型无效。");
        }

        if (!ValidateCombatMultiplier(combatDropRate)
            || !ValidateCombatMultiplier(combatDropQuantity)
            || !ValidateCombatMultiplier(combatRareFind))
        {
            return DaoxuanCommandResult.Fail("INVALID_COMBAT_DROP_BONUS", "战斗掉落倍率必须在 -100% 到 100000% 之间。");
        }

        if (!ValidateTenacity(tenacity))
        {
            return DaoxuanCommandResult.Fail("INVALID_TENACITY", "韧性必须在 0 到 100000 之间。");
        }
        if (!ValidateSearchSpeedRatio(searchSpeedRatio))
        {
            return DaoxuanCommandResult.Fail("INVALID_SEARCH_SPEED", "寻怪速度必须在 -90% 到 100000% 之间。");
        }

        List<DaoxuanEquippedItem>? normalizedEquipment = null;
        if (equipment is not null)
        {
            if (!TryNormalizeEquipment(equipment, out normalizedEquipment, out var equipmentError))
            {
                return DaoxuanCommandResult.Fail("INVALID_EQUIPMENT", equipmentError);
            }
        }

        List<DaoxuanCombatAbilitySlot>? normalizedAbilities = null;
        if (abilitySlots is not null)
        {
            if (abilitySlots.Count > DaoxuanCombatLimits.AbilitySlotCount
                || abilitySlots.Any(slot => slot.Level is < 1 or > 100))
            {
                return DaoxuanCommandResult.Fail("INVALID_ABILITY_SLOTS", "功法槽数量或等级无效。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !DaoxuanCombatCatalog.TryGetAbility(slot.AbilityId, out _)))
            {
                return DaoxuanCommandResult.Fail("ABILITY_NOT_FOUND", "功法不存在。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !state.Combat.LearnedAbilityIds.Contains(slot.AbilityId, StringComparer.Ordinal)))
            {
                return DaoxuanCommandResult.Fail("ABILITY_NOT_LEARNED", "尚未习得该功法。");
            }

            if (abilitySlots.Any(slot => !ValidateTriggers(slot.Triggers)))
            {
                return DaoxuanCommandResult.Fail("INVALID_ABILITY_TRIGGERS", "功法触发条件无效。");
            }

            normalizedAbilities = abilitySlots
                .Select(slot => new DaoxuanCombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = CloneTriggers(slot.Triggers)
                })
                .Concat(Enumerable.Range(
                    abilitySlots.Count,
                    DaoxuanCombatLimits.AbilitySlotCount - abilitySlots.Count).Select(_ => new DaoxuanCombatAbilitySlot()))
                .ToList();
        }

        if (!TryValidateConsumableSlots(consumableSlots, out var error))
        {
            return DaoxuanCommandResult.Fail("INVALID_CONSUMABLE_SLOTS", error);
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
        Touch(state, DaoxuanDirtySections.Combat);
        return DaoxuanCommandResult.Ok("战斗配置已更新。");
    }

    /// <summary>
    /// 开启或关闭队列循环模式。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="enabled">是否开启循环。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult SetLoop(DaoxuanPlayerState state, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Queue.LoopEnabled == enabled)
        {
            return DaoxuanCommandResult.Ok(enabled ? "循环已经开启。" : "循环已经关闭。");
        }

        state.Queue.LoopEnabled = enabled;
        Touch(state, DaoxuanDirtySections.Queue);
        return DaoxuanCommandResult.Ok(enabled ? "循环已开启。" : "循环已关闭。");
    }

    /// <summary>
    /// 设置玩家在线/挂机状态（Online/BackgroundSimulated/FullyOffline），并在此之前追赶进度。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="mode">目标存在状态。</param>
    /// <param name="now">当前时间戳。</param>
    /// <returns>命令执行结果。</returns>
    public static DaoxuanCommandResult SetPresence(
        DaoxuanPlayerState state,
        DaoxuanPresenceMode mode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedNow = NormalizeTime(now);
        if (state.PresenceMode != DaoxuanPresenceMode.FullyOffline)
        {
            AdvanceTo(state, normalizedNow);
        }

        state.PresenceMode = mode;
        Touch(state, DaoxuanDirtySections.Presence);
        return DaoxuanCommandResult.Ok($"状态已切换为 {mode}。");
    }

    /// <summary>
    /// 执行破境突破操作。当修为达到当前大境界/层级上限且标记待突破时方可成功。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <returns>突破执行结果。</returns>
    public static DaoxuanCommandResult Breakthrough(DaoxuanPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var progress = DaoxuanCombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;
        var isCap = progress.ExpToNext > 0 && expToCap <= 0.0001 && state.Stats.Spirit > 0;
        if (!state.IsPendingBreakthrough && !isCap)
        {
            return DaoxuanCommandResult.Fail("CULTIVATION_NOT_CAPPED", "修为尚未圆满，无法突破。");
        }

        state.IsPendingBreakthrough = false;
        Touch(state, DaoxuanDirtySections.Stats | DaoxuanDirtySections.Events | DaoxuanDirtySections.Combat);
        AddEvent(state, "破境功成！洗髓伐经，道行更进一层。");
        return DaoxuanCommandResult.Ok("突破成功！");
    }

    /// <summary>
    /// 增加玩家修炼修为。
    /// 规则：
    /// 1. 当修为达到当前层满值且未突破时：
    ///    获得的全部修为转化为「底蕴」(FoundationExp)；
    /// 2. 当有底蕴时：获得修为享受 3 倍经验（基础 1x + 消耗底蕴提供 2x 加成，若底蕴不足则消耗全部底蕴提供等值加成）；
    /// 3. 若加上获得修为后触及当前层境界满值，则卡在满值并开启待突破标记，溢出部分自动注入底蕴。
    /// </summary>
    /// <param name="state">玩家状态。</param>
    /// <param name="baseSpiritGain">基础修为增量。</param>
    /// <returns>实际获得修为、转化为底蕴的修为、消耗掉的底蕴修为。</returns>
    public static (double actualSpiritGain, double foundationGain, double foundationConsumed) AddCultivationSpirit(
        DaoxuanPlayerState state,
        double baseSpiritGain)
    {
        if (baseSpiritGain <= 0) return (0, 0, 0);

        var progress = DaoxuanCombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;

        if (progress.ExpToNext <= 0.0 || progress.Level >= DaoxuanCombatConstants.MaxRealmLevel)
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

    /// <summary>
    /// 将玩家聚合状态以确定性边界方式快进推演至目标时间戳。
    /// 按行动完成边界、Buff 过期时间、战斗遭遇结算逐步迭代。
    /// </summary>
    /// <param name="state">待推进的玩家状态聚合根。</param>
    /// <param name="targetTime">目标对齐时间戳。</param>
    /// <returns>推进结算摘要结果（脏段、完成动作数、状态转移数、产生事件列表等）。</returns>
    public static DaoxuanSimulationResult AdvanceTo(
        DaoxuanPlayerState state,
        DateTimeOffset targetTime)
    {
        ArgumentNullException.ThrowIfNull(state);
        var target = NormalizeTime(targetTime);
        var cursor = NormalizeTime(state.LastSimulatedAt);
        if (target < cursor)
        {
            return new DaoxuanSimulationResult(DaoxuanDirtySections.None, 0, 0, []);
        }

        if (state.PresenceMode == DaoxuanPresenceMode.FullyOffline)
        {
            // 完全离线账号不参与行动结算；推进时间戳即可避免重新启用时追溯这段时间。
            state.LastSimulatedAt = target;
            return new DaoxuanSimulationResult(DaoxuanDirtySections.None, 0, 0, []);
        }

        var dirty = DaoxuanDirtySections.None;
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
                dirty |= DaoxuanDirtySections.Buffs;
            }

            if (state.Queue.Entries.Count == 0)
            {
                var available = GetAvailableMilliseconds(cursor, target);
                if (available > 0 && RegenerateResources(state, cursor, available))
                {
                    dirty |= DaoxuanDirtySections.Combat;
                }
                cursor = target;
                break;
            }

            var current = state.Queue.Entries[0];
            var definition = DaoxuanActionCatalog.Get(current.ActionId);
            current.DurationSeconds = GetDurationSeconds(current, definition);
            var wasBlocked = current.Status == DaoxuanActionStatus.Blocked;
            current.Status = DaoxuanActionStatus.Running;

            if (!CanStartAction(state, current, definition))
            {
                current.Status = DaoxuanActionStatus.Blocked;
                if (state.Queue.LoopEnabled)
                {
                    if (!skippedForMissingInputs.Add(current.EntryId))
                    {
                        // 全部行动都被跳过时队列空转：这段空闲时间同样享受生命和灵力自动回复。
                        var idleMilliseconds = GetAvailableMilliseconds(cursor, target);
                        if (idleMilliseconds > 0 && RegenerateResources(state, cursor, idleMilliseconds))
                        {
                            dirty |= DaoxuanDirtySections.Combat;
                        }
                        cursor = target;
                        break;
                    }

                    if (!wasBlocked)
                    {
                        state.Stats.BlockedActions++;
                        AddEvent(state, $"行动跳过：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足，已移至队尾");
                        dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.Stats | DaoxuanDirtySections.Events;
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
                    dirty |= DaoxuanDirtySections.Stats;
                }

                AddEvent(state, $"行动结束：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足");
                dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.Events;
                events.Add($"ended:{definition.Id}:missing-inputs");
                state.Queue.Entries.RemoveAt(0);
                transitions++;
                continue;
            }

            if (hasPendingQueueRotation)
            {
                dirty |= DaoxuanDirtySections.Queue;
                hasPendingQueueRotation = false;
            }

            if (definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter)
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
                    dirty |= DaoxuanDirtySections.QueueProgress;
                    if (current.TravelPlan is not null)
                    {
                        DaoxuanWorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                        dirty |= DaoxuanDirtySections.World;
                    }
                    if (RegenerateResources(state, cursor, progress))
                    {
                        dirty |= DaoxuanDirtySections.Combat;
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
                DaoxuanWorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                dirty |= DaoxuanDirtySections.World;
            }
            if (RegenerateResources(state, cursor, remainingMilliseconds))
            {
                dirty |= DaoxuanDirtySections.Combat;
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
            dirty |= DaoxuanDirtySections.Events;
            if (hasPendingQueueRotation)
            {
                dirty |= DaoxuanDirtySections.Queue;
            }
        }

        if (ExpireBuffs(state, target))
        {
            dirty |= DaoxuanDirtySections.Buffs;
        }

        if (dirty != DaoxuanDirtySections.None)
        {
            Touch(state, dirty);
        }

        state.LastSimulatedAt = target;
        return new DaoxuanSimulationResult(
            dirty,
            completedActions,
            transitions,
            events,
            combatEvents);
    }

    /// <summary>
    /// 计算该玩家下一次需要发生状态转换或事件推进的目标时间戳（用于定时器调度与离线快进预判）。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <returns>下次事件触发时间戳，若无任何挂起事件或处于完全离线则返回 null。</returns>
    public static DateTimeOffset? GetNextTransitionAt(DaoxuanPlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PresenceMode == DaoxuanPresenceMode.FullyOffline) return null;

        DateTimeOffset? next = state.Buffs
            .Where(buff => buff.ExpiresAt > state.LastSimulatedAt)
            .Select(buff => (DateTimeOffset?)NormalizeTime(buff.ExpiresAt))
            .OrderBy(value => value)
            .FirstOrDefault();
        if (state.Queue.Entries.Count == 0) return next;

        var current = state.Queue.Entries[0];
        if (!DaoxuanActionCatalog.TryGet(current.ActionId, out var definition))
        {
            return state.LastSimulatedAt;
        }

        DateTimeOffset? actionDeadline;
        if (current.Status != DaoxuanActionStatus.Running)
        {
            actionDeadline = state.LastSimulatedAt;
        }
        else if (definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter)
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
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition)
    {
        var expedition = current.CombatExpedition ?? CreateCombatExpedition(definition);
        if (expedition.Phase != DaoxuanCombatExpeditionPhase.Combat)
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
        var initialPlayer = DaoxuanCombatSimulator.CreatePlayerState(
            battleLoadout,
            battleBuffs,
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints,
            state.Stats.Spirit);
        var result = DaoxuanCombatSimulator.Run(
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
            playerSpirit: state.Stats.Spirit,
            initialAbilityCooldowns: expedition.InitialAbilityCooldowns,
            initialConsumableCooldowns: expedition.InitialConsumableCooldowns);
        if (result.OutcomeMilliseconds is not null)
        {
            return expedition.BattleStartedAt.AddMilliseconds(result.OutcomeMilliseconds.Value);
        }

        return result.NextEventMilliseconds is { } nextEvent
            ? expedition.BattleStartedAt.AddMilliseconds(nextEvent)
            : null;
    }

    private static void AdvanceCombatAction(
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition,
        ref DateTimeOffset cursor,
        DateTimeOffset target,
        ref DaoxuanDirtySections dirty,
        ref int completedActions,
        ref int transitions,
        ref int combatEvents)
    {
        var expedition = current.CombatExpedition ??= CreateCombatExpedition(definition);
        EnsureCombatResources(state, cursor);
        current.DurationSeconds = 0;

        while (cursor <= target && transitions < MaxTransitionsPerAdvance)
        {
            if (expedition.Phase is DaoxuanCombatExpeditionPhase.InitialSearch
                or DaoxuanCombatExpeditionPhase.Search)
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
                        dirty |= DaoxuanDirtySections.Combat;
                    }
                    cursor = target;
                    dirty |= DaoxuanDirtySections.QueueProgress;
                    return;
                }

                cursor = cursor.AddMilliseconds(remaining);
                expedition.PhaseElapsedMilliseconds = 0;
                current.ProgressSeconds = 0;
                transitions++;
                if (RegenerateResources(state, cursor, remaining))
                {
                    dirty |= DaoxuanDirtySections.Combat;
                }

                var startedEncounterId = BeginCombatBattle(state, current, definition, cursor);
                var startedEncounter = DaoxuanCombatCatalog.GetEncounter(startedEncounterId);
                current.DurationSeconds = 0;
                AddEvent(state, $"发现 {{{{encounter:{startedEncounter.Id}|{startedEncounter.Name}}}}}，战斗开始。", cursor);
                dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.QueueProgress | DaoxuanDirtySections.Events;
                continue;
            }

            var encounterId = ResolveCombatEncounterId(state, definition, expedition);
            var encounter = DaoxuanCombatCatalog.GetEncounter(encounterId);
            var availableCombatMilliseconds = GetAvailableMilliseconds(cursor, target);
            var requestedElapsed = checked(expedition.PhaseElapsedMilliseconds + availableCombatMilliseconds);
            var battleLoadout = expedition.BattleLoadout ?? state.Combat.Clone();
            var battleBuffs = expedition.BattleBuffs ?? [];
            var battleInventory = expedition.BattleInventory ?? new Dictionary<string, long>(StringComparer.Ordinal);
            var initialPlayer = DaoxuanCombatSimulator.CreatePlayerState(
                battleLoadout,
                battleBuffs,
                expedition.BattleStartedAt,
                expedition.InitialHitpoints,
                expedition.InitialManapoints,
                state.Stats.Spirit);
            var result = DaoxuanCombatSimulator.Run(
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
                playerSpirit: state.Stats.Spirit,
                initialAbilityCooldowns: expedition.InitialAbilityCooldowns,
                initialConsumableCooldowns: expedition.InitialConsumableCooldowns);

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
                UpdateCombatCooldowns(state, result, expedition.BattleStartedAt);
                dirty |= DaoxuanDirtySections.Combat;
            }

            if (result.OutcomeMilliseconds is not { } outcomeMilliseconds)
            {
                expedition.PhaseElapsedMilliseconds = requestedElapsed;
                current.ProgressSeconds = ToDisplaySeconds(requestedElapsed);
                current.DurationSeconds = 0;
                cursor = target;
                dirty |= DaoxuanDirtySections.QueueProgress;
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
            current.Status = DaoxuanActionStatus.Running;
            expedition.Phase = DaoxuanCombatExpeditionPhase.Search;
            expedition.PhaseDurationMilliseconds = CalculateCombatSearchMilliseconds(encounter, state.Combat);
            current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
            dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.QueueProgress | DaoxuanDirtySections.Stats;
        }
    }

    private static string BeginCombatBattle(
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition,
        DateTimeOffset startedAt)
    {
        var expedition = current.CombatExpedition ??= new DaoxuanCombatExpeditionState();
        var nextBattleSequence = checked(expedition.BattleSequence + 1);
        var encounterId = SelectCombatEncounterId(state, definition, nextBattleSequence);
        EnsureCombatResources(state, startedAt);
        var initial = DaoxuanCombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            startedAt,
            state.CombatResources.Hitpoints,
            state.CombatResources.Manapoints,
            state.Stats.Spirit);
        SetCombatResources(state, initial);
        expedition.Phase = DaoxuanCombatExpeditionPhase.Combat;
        expedition.PhaseDurationMilliseconds = 0;
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EncounterId = encounterId;
        expedition.BattleSequence = nextBattleSequence;
        expedition.CompletedEncountersBeforeBattle = state.CombatEncounterCompletions.GetValueOrDefault(encounterId);
        expedition.BattleStartedAt = startedAt;
        expedition.InitialHitpoints = initial.Hitpoints;
        expedition.InitialManapoints = initial.Manapoints;
        expedition.InitialAbilityCooldowns.Clear();
        foreach (var (abilityId, readyAt) in state.CombatResources.AbilityCooldowns)
        {
            var remaining = Math.Max(0, (long)Math.Ceiling((readyAt - startedAt).TotalMilliseconds));
            if (remaining > 0)
            {
                expedition.InitialAbilityCooldowns[abilityId] = remaining;
            }
        }
        expedition.InitialConsumableCooldowns = state.CombatResources.ConsumableSlotCooldowns
            .Select(readyAt => Math.Max(0, (long)Math.Ceiling((readyAt - startedAt).TotalMilliseconds)))
            .ToList();
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

    private static DaoxuanDirtySections ApplyIncrementalCombatConsumption(
        DaoxuanPlayerState state,
        DaoxuanCombatExpeditionState expedition,
        DaoxuanCombatRunResult result)
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

        return changed ? DaoxuanDirtySections.Inventory : DaoxuanDirtySections.None;
    }

    private static DaoxuanDirtySections EmitIncrementalCombatEvents(
        DaoxuanPlayerState state,
        DaoxuanCombatExpeditionState expedition,
        DaoxuanCombatRunResult result,
        DateTimeOffset occurredAt)
    {
        if (expedition.EmittedEventCount >= result.Events.Count) return DaoxuanDirtySections.None;
        foreach (var entry in result.Events.Skip(expedition.EmittedEventCount))
        {
            AddEvent(state, $"战斗：{entry}", occurredAt);
        }
        expedition.EmittedEventCount = result.Events.Count;
        return DaoxuanDirtySections.Events;
    }

    private static DaoxuanDirtySections ApplyIncrementalCombatSettlement(
        DaoxuanPlayerState state,
        DaoxuanCombatExpeditionState expedition,
        string encounterId,
        DaoxuanCombatRunResult result,
        DateTimeOffset settledAt,
        ref int combatEvents)
    {
        var dirty = DaoxuanDirtySections.None;
        var processedEventDelta = Math.Max(0, result.ProcessedEvents - expedition.AppliedProcessedEvents);
        if (processedEventDelta > 0)
        {
            combatEvents = checked(combatEvents + processedEventDelta);
            expedition.AppliedProcessedEvents = result.ProcessedEvents;
        }

        var spiritDelta = Math.Max(0, result.Spirit - expedition.AppliedSpirit);
        if (spiritDelta > 0)
        {
            state.Inventory.Add(DaoxuanItemIds.Spirit, spiritDelta);
            expedition.AppliedSpirit = result.Spirit;
            dirty |= DaoxuanDirtySections.Inventory;
        }

        dirty |= ApplyIncrementalDrops(state, expedition.AppliedDroppedItems, result.DroppedItems);
        dirty |= ApplyIncrementalDrops(state, expedition.AppliedRareDroppedItems, result.RareDroppedItems);

        foreach (var application in result.BuffApplications)
        {
            ApplyCombatBuff(state, application, settledAt);
            dirty |= DaoxuanDirtySections.Buffs;
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
            dirty |= DaoxuanDirtySections.Stats | DaoxuanDirtySections.Combat;
        }

        return dirty;
    }

    private static DaoxuanDirtySections ApplyIncrementalDrops(
        DaoxuanPlayerState state,
        IDictionary<string, long> appliedDrops,
        IReadOnlyDictionary<string, long>? currentDrops)
    {
        if (currentDrops is not { Count: > 0 }) return DaoxuanDirtySections.None;
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
        return changed ? DaoxuanDirtySections.Inventory : DaoxuanDirtySections.None;
    }

    private static void FinishCombatBatch(
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition,
        DateTimeOffset completedAt,
        ref DaoxuanDirtySections dirty)
    {
        AddEvent(state, $"行动完成：{{{{action:{definition.Id}|{definition.Name}}}}}", completedAt);
        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.DurationSeconds = 0;
            current.Status = DaoxuanActionStatus.Queued;
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
        dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.Stats | DaoxuanDirtySections.Events | DaoxuanDirtySections.Combat;
    }

    private static void EnsureAliveAfterLeavingCombat(DaoxuanPlayerState state, DateTimeOffset at)
    {
        EnsureCombatResources(state, at);
        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
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
        DaoxuanPlayerState state,
        DateTimeOffset at,
        double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0) return false;
        EnsureCombatResources(state, at);

        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
        var maxHp = attributes.MaxHitpoints;
        var maxMp = attributes.MaxManapoints;
        var hpRegenRatio = attributes.HpRegenPerTenSeconds;
        var mpRegenRatio = attributes.MpRegenPerTenSeconds;

        var changed = false;

        // 生命值自动回复：仅在玩家存活（Hitpoints > 0）时生效
        if (state.CombatResources.Hitpoints > 0)
        {
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
        }
        else
        {
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

    private static void UpdateCombatCooldowns(
        DaoxuanPlayerState state,
        DaoxuanCombatRunResult result,
        DateTimeOffset battleStartedAt)
    {
        EnsureCombatResources(state, state.LastSimulatedAt);
        var simulatedAt = battleStartedAt.AddMilliseconds(result.SimulatedMilliseconds);

        if (result.PlayerAbilityCooldowns is not null)
        {
            foreach (var (abilityId, remainingMs) in result.PlayerAbilityCooldowns)
            {
                if (remainingMs > 0)
                {
                    state.CombatResources.AbilityCooldowns[abilityId] = simulatedAt.AddMilliseconds(remainingMs);
                }
                else
                {
                    state.CombatResources.AbilityCooldowns.Remove(abilityId);
                }
            }
            foreach (var slot in state.Combat.AbilitySlots)
            {
                if (!string.IsNullOrWhiteSpace(slot.AbilityId)
                    && !result.PlayerAbilityCooldowns.ContainsKey(slot.AbilityId)
                    && state.CombatResources.AbilityCooldowns.TryGetValue(slot.AbilityId, out var readyAt)
                    && readyAt <= simulatedAt)
                {
                    state.CombatResources.AbilityCooldowns.Remove(slot.AbilityId);
                }
            }
        }

        if (result.PlayerConsumableCooldowns is not null)
        {
            while (state.CombatResources.ConsumableSlotCooldowns.Count < result.PlayerConsumableCooldowns.Count)
            {
                state.CombatResources.ConsumableSlotCooldowns.Add(DateTimeOffset.MinValue);
            }
            for (var i = 0; i < result.PlayerConsumableCooldowns.Count; i++)
            {
                var remainingMs = result.PlayerConsumableCooldowns[i];
                state.CombatResources.ConsumableSlotCooldowns[i] = remainingMs > 0
                    ? simulatedAt.AddMilliseconds(remainingMs)
                    : DateTimeOffset.MinValue;
            }
        }
    }

    private static void ResetBattleSnapshot(DaoxuanCombatExpeditionState expedition)
    {
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EncounterId = null;
        expedition.BattleStartedAt = default;
        expedition.InitialHitpoints = 0;
        expedition.InitialManapoints = 0;
        expedition.InitialAbilityCooldowns.Clear();
        expedition.InitialConsumableCooldowns.Clear();
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

    private static void EnsureCombatResources(DaoxuanPlayerState state, DateTimeOffset at)
    {
        state.CombatResources ??= new DaoxuanCombatResourceState();
        if (state.CombatResources.Initialized) return;
        SetCombatResources(state, DaoxuanCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, at, spirit: state.Stats.Spirit));
    }

    private static void SetCombatResources(DaoxuanPlayerState state, DaoxuanCombatPlayerState player)
    {
        state.CombatResources ??= new DaoxuanCombatResourceState();
        state.CombatResources.Initialized = true;
        state.CombatResources.Hitpoints = Math.Clamp(player.Hitpoints, 0, player.MaxHitpoints);
        state.CombatResources.Manapoints = Math.Clamp(player.Manapoints, 0, player.MaxManapoints);
    }

    private static DaoxuanBuffState CloneCombatBuff(DaoxuanBuffState buff) => new()
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
        DaoxuanCombatEncounterDefinition encounter,
        DaoxuanCombatLoadout loadout)
    {
        var baseMilliseconds = Math.Max(1, encounter.RespawnMilliseconds);
        var ratio = Math.Clamp(loadout.SearchSpeedRatio, -0.9, 1_000);
        return Math.Clamp(
            (int)Math.Ceiling(baseMilliseconds / (1 + ratio)),
            MinimumCombatSearchMilliseconds,
            MaximumCombatSearchMilliseconds);
    }

    private static DaoxuanCombatEncounterDefinition GetCombatEncounter(
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        DaoxuanCombatExpeditionState expedition)
    {
        return DaoxuanCombatCatalog.GetEncounter(ResolveCombatEncounterId(state, definition, expedition));
    }

    private static string ResolveCombatEncounterId(
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        DaoxuanCombatExpeditionState expedition)
    {
        if (!string.IsNullOrWhiteSpace(expedition.EncounterId)
            && DaoxuanCombatCatalog.TryGetEncounter(expedition.EncounterId, out _))
        {
            return expedition.EncounterId;
        }

        var battleSequence = Math.Max(1, expedition.BattleSequence);
        expedition.EncounterId = SelectCombatEncounterId(state, definition, battleSequence);
        return expedition.EncounterId;
    }

    private static string SelectCombatEncounterId(
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        long battleSequence)
    {
        if (state.World.NodeId is { Length: > 0 } nodeId)
        {
            var node = DaoxuanWorldCatalog.GetNode(state.World.MapId, nodeId);
            var candidates = node.EncounterIds
                .Where(id => DaoxuanCombatCatalog.TryGetEncounter(id, out _))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates[SelectStableEncounterIndex(state.PlayerId, nodeId, battleSequence, candidates.Length)];
            }
        }

        return definition.CombatEncounterId is { Length: > 0 } configured
            && DaoxuanCombatCatalog.TryGetEncounter(configured, out _)
                ? configured
                : DaoxuanCombatEncounterIds.QiBeast;
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
        DaoxuanPlayerState state,
        DaoxuanQueuedAction current,
        DaoxuanActionDefinition definition,
        ref DaoxuanDirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        if (current.TravelPlan is not null)
        {
            var destination = DaoxuanWorldCatalog.GetNode(
                current.TravelPlan.MapId,
                current.TravelPlan.DestinationNodeId);
            state.Stats.CompletedActions++;
            state.Queue.Entries.RemoveAt(0);
            dirty |= DaoxuanDirtySections.Queue
                | DaoxuanDirtySections.World
                | DaoxuanDirtySections.Stats
                | DaoxuanDirtySections.Events;
            AddEvent(state, $"已抵达 {{{{location:{destination.Id}|{destination.Name}}}}}");
            events.Add($"arrived:{destination.Id}");
            return;
        }

        foreach (var input in definition.Inputs)
        {
            state.Inventory.Add(input.Key, -input.Value);
        }

        if (definition.Type is DaoxuanActionType.Exploration or DaoxuanActionType.Alchemy)
        {
            var consumed = ConsumeAutoUseItems(state, definition.Type, state.LastSimulatedAt);
            if (consumed.Count > 0)
            {
                dirty |= DaoxuanDirtySections.Inventory | DaoxuanDirtySections.Buffs;
                foreach (var (itemId, quantity) in consumed)
                {
                    var itemName = DaoxuanItemCatalog.TryGet(itemId, out var usedItem)
                        ? usedItem.Name
                        : itemId;
                    AddEvent(state, $"{definition.Name}自动使用 {{{{item:{itemId}|{itemName}}}}} × {quantity}");
                }
            }
        }

        foreach (var output in definition.Outputs)
        {
            if (definition.Type == DaoxuanActionType.Cultivation && output.Key == DaoxuanItemIds.Qi)
            {
                continue;
            }

            var multiplier = definition.Type switch
            {
                DaoxuanActionType.Exploration => GetGatheringOutputMultiplier(state, state.LastSimulatedAt),
                DaoxuanActionType.Alchemy => GetAlchemyOutputMultiplier(state, state.LastSimulatedAt),
                _ => 1
            };
            var amount = multiplier == 1
                ? output.Value
                : Math.Max(1, (long)Math.Ceiling(output.Value * multiplier));
            state.Inventory.Add(output.Key, amount);
        }

        switch (definition.Type)
        {
            case DaoxuanActionType.Exploration:
                state.Stats.HerbsCollected += GetProducedAmount(
                    state,
                    definition,
                    DaoxuanItemIds.SpiritHerb,
                    GetGatheringOutputMultiplier(state, state.LastSimulatedAt));
                break;
            case DaoxuanActionType.Alchemy:
                state.Stats.PillsCrafted += GetProducedAmount(
                    state,
                    definition,
                    DaoxuanItemIds.QiGatheringPill,
                    GetAlchemyOutputMultiplier(state, state.LastSimulatedAt));
                break;
            case DaoxuanActionType.Cultivation:
                var consumedPills = ConsumeAutoUseItems(state, DaoxuanActionType.Cultivation, state.LastSimulatedAt);
                if (consumedPills.Count > 0)
                {
                    dirty |= DaoxuanDirtySections.Inventory | DaoxuanDirtySections.Buffs;
                    foreach (var (itemId, quantity) in consumedPills)
                    {
                        var itemName = DaoxuanItemCatalog.TryGet(itemId, out var usedItem)
                            ? usedItem.Name
                            : itemId;
                        AddEvent(state, $"修炼自动服用 {{{{item:{itemId}|{itemName}}}}} × {quantity}");
                    }
                }

                var multiplier = GetQiGainMultiplier(state, state.LastSimulatedAt);
                var baseQi = definition.Outputs.GetValueOrDefault(DaoxuanItemIds.Qi);
                var qiGain = (long)Math.Round(baseQi * multiplier, MidpointRounding.AwayFromZero);
                state.Inventory.Add(DaoxuanItemIds.Qi, qiGain);
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
                dirty |= DaoxuanDirtySections.Stats;
                break;
            case DaoxuanActionType.Combat:
                CompleteCombatAction(
                    state,
                    definition,
                    ref dirty,
                    events,
                    ref combatEvents);
                break;
        }

        state.Stats.CompletedActions++;
        dirty |= DaoxuanDirtySections.Queue | DaoxuanDirtySections.Inventory | DaoxuanDirtySections.Stats | DaoxuanDirtySections.Events;
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
            current.Status = DaoxuanActionStatus.Queued;
            return;
        }

        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.Status = DaoxuanActionStatus.Queued;
            state.Queue.Entries.RemoveAt(0);
            state.Queue.Entries.Add(current);
            return;
        }

        state.Queue.Entries.RemoveAt(0);
    }

    private static List<(string ItemId, int Quantity)> ConsumeAutoUseItems(
        DaoxuanPlayerState state,
        DaoxuanActionType actionType,
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
                || !DaoxuanItemCatalog.TryGet(slot.ItemId, out var item)
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
        DaoxuanPlayerState state,
        DaoxuanItemDefinition item,
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
                state.Buffs.Add(new DaoxuanBuffState
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
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        ref DaoxuanDirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        var encounterId = definition.CombatEncounterId ?? DaoxuanCombatEncounterIds.QiBeast;
        var encounter = DaoxuanCombatCatalog.GetEncounter(encounterId);
        var searchSeconds = Math.Clamp(
            (int)Math.Ceiling(Math.Max(0, encounter.SearchMilliseconds) / 1_000d),
            0,
            definition.DurationSeconds);
        var combatSeconds = Math.Max(0, definition.DurationSeconds - searchSeconds);
        var startedAt = state.LastSimulatedAt
            .AddSeconds(-definition.DurationSeconds)
            .AddSeconds(searchSeconds);
        var result = DaoxuanCombatSimulator.Run(
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
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        string encounterId,
        DaoxuanCombatRunResult result,
        DateTimeOffset completedAt,
        bool applyConsumedItems,
        ref DaoxuanDirtySections dirty,
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

        state.Inventory.Add(DaoxuanItemIds.Spirit, result.Spirit);
        if (result.DroppedItems is { Count: > 0 })
        {
            foreach (var drop in result.DroppedItems)
            {
                state.Inventory.Add(drop.Key, drop.Value);
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
        dirty |= DaoxuanDirtySections.Inventory
            | DaoxuanDirtySections.Buffs
            | DaoxuanDirtySections.Stats
            | DaoxuanDirtySections.Combat;
    }

    private static void ApplyCombatBuff(
        DaoxuanPlayerState state,
        DaoxuanCombatBuffApplication application,
        DateTimeOffset now)
    {
        var existing = state.Buffs.FirstOrDefault(buff => buff.BuffId == application.BuffId);
        if (existing is null)
        {
            state.Buffs.Add(new DaoxuanBuffState
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
        DaoxuanPlayerState state,
        DaoxuanQueuedAction queuedAction,
        DaoxuanActionDefinition definition)
    {
        if (definition.ExecutionKind == DaoxuanActionExecutionKind.Travel)
        {
            return IsValidTravelPlan(state.World, queuedAction.TravelPlan);
        }

        return DaoxuanWorldCatalog.CanPerformAction(state.World, definition)
            && definition.Inputs.All(input => state.Inventory.Get(input.Key) >= input.Value);
    }

    private static bool IsValidTravelPlan(DaoxuanWorldState world, DaoxuanTravelPlan? plan)
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
            _ = DaoxuanWorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
            var duration = 0;
            foreach (var segment in plan.Segments)
            {
                _ = DaoxuanWorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
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
        IReadOnlyList<DaoxuanConsumableSlot>? requested,
        out string error)
    {
        if (requested is null)
        {
            error = string.Empty;
            return true;
        }

        if (requested.Count > DaoxuanCombatLimits.ConsumableSlotCount
            || requested.Any(slot => slot.Quantity is < 1 or > 99))
        {
            error = "消耗品槽数量或使用数量无效。";
            return false;
        }

        if (requested.Any(slot => !string.IsNullOrWhiteSpace(slot.ItemId)
            && !DaoxuanItemCatalog.CanUseInAction(slot.ItemId, DaoxuanActionType.Combat)))
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

    private static List<DaoxuanConsumableSlot> CloneConsumableSlots(
        IReadOnlyList<DaoxuanConsumableSlot> slots)
    {
        return slots
            .Select(slot => new DaoxuanConsumableSlot
            {
                ItemId = slot.ItemId,
                Quantity = slot.Quantity,
                Enabled = slot.Enabled && !string.IsNullOrWhiteSpace(slot.ItemId),
                Triggers = CloneTriggers(slot.Triggers)
            })
            .Concat(Enumerable.Range(
                slots.Count,
                DaoxuanCombatLimits.ConsumableSlotCount - slots.Count).Select(_ => new DaoxuanConsumableSlot()))
            .ToList();
    }

    private static bool ValidateTriggers(IReadOnlyList<DaoxuanCombatTrigger>? triggers)
    {
        if (triggers is null) return true;
        if (triggers.Count > DaoxuanCombatLimits.TriggerCountPerSlot) return false;
        return triggers.All(trigger =>
            Enum.IsDefined(trigger.Dependency)
            && Enum.IsDefined(trigger.Condition)
            && Enum.IsDefined(trigger.Comparator)
            && double.IsFinite(trigger.Value)
            && (trigger.Condition != DaoxuanCombatTriggerCondition.BuffStacks
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
        IReadOnlyList<DaoxuanEquippedItem> source,
        out List<DaoxuanEquippedItem> normalized,
        out string error)
    {
        normalized = [];
        error = string.Empty;
        if (source.Count > DaoxuanCombatLimits.EquipmentSlotCount)
        {
            error = $"装备数量不能超过 {DaoxuanCombatLimits.EquipmentSlotCount} 件。";
            return false;
        }

        var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item is null
                || !DaoxuanCombatCatalog.TryGetEquipment(item.ItemId, out var definition)
                || item.EnhancementLevel is < 0 or > DaoxuanCombatLimits.MaxEnhancementLevel)
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

    private static bool NormalizeEquipment(List<DaoxuanEquippedItem> equipment)
    {
        if (TryNormalizeEquipment(equipment, out var normalized, out _)
            && normalized.Count == equipment.Count)
        {
            return false;
        }

        equipment.Clear();
        return true;
    }

    private static bool NormalizeCombatEffectRatios(DaoxuanCombatLoadout combat)
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

    private static List<DaoxuanCombatTrigger> CloneTriggers(
        IReadOnlyList<DaoxuanCombatTrigger>? triggers)
    {
        return triggers?.Select(trigger => trigger.Clone()).ToList() ?? [];
    }

    private static bool TriggersEqual(
        IReadOnlyList<DaoxuanCombatTrigger>? left,
        IReadOnlyList<DaoxuanCombatTrigger>? right)
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

    private static bool ExpireBuffs(DaoxuanPlayerState state, DateTimeOffset now)
    {
        var removed = state.Buffs.RemoveAll(buff => buff.ExpiresAt <= now);
        return removed > 0;
    }

    private static double GetQiGainMultiplier(DaoxuanPlayerState state, DateTimeOffset now)
    {
        var buffMultiplier = state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.QiGainMultiplier)
            .DefaultIfEmpty(1)
            .Max();
        return buffMultiplier * DaoxuanWorldCatalog.GetCultivationMultiplier(state.World);
    }

    private static double GetGatheringOutputMultiplier(DaoxuanPlayerState state, DateTimeOffset now)
    {
        return state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.GatheringOutputMultiplier)
            .Where(multiplier => multiplier > 0)
            .DefaultIfEmpty(1)
            .Max();
    }

    private static double GetAlchemyOutputMultiplier(DaoxuanPlayerState state, DateTimeOffset now)
    {
        return state.Buffs
            .Where(buff => buff.ExpiresAt > now)
            .Select(buff => buff.AlchemyOutputMultiplier)
            .Where(multiplier => multiplier > 0)
            .DefaultIfEmpty(1)
            .Max();
    }

    private static long GetProducedAmount(
        DaoxuanPlayerState state,
        DaoxuanActionDefinition definition,
        string itemId,
        double multiplier)
    {
        var baseAmount = definition.Outputs.GetValueOrDefault(itemId);
        return multiplier == 1
            ? baseAmount
            : Math.Max(1, (long)Math.Ceiling(baseAmount * multiplier));
    }

    private static DaoxuanQueuedAction CreateQueuedAction(string actionId, int repeats = 1)
    {
        var definition = DaoxuanActionCatalog.Get(actionId);
        return new DaoxuanQueuedAction
        {
            ActionId = actionId,
            TotalRepeats = repeats,
            RemainingRepeats = repeats,
            DurationSeconds = definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter
                ? 0
                : definition.DurationSeconds,
            CombatExpedition = definition.ExecutionKind == DaoxuanActionExecutionKind.CombatEncounter
                ? CreateCombatExpedition(definition)
                : null
        };
    }

    private static DaoxuanCombatExpeditionState CreateCombatExpedition(DaoxuanActionDefinition definition)
    {
        var encounterId = definition.CombatEncounterId ?? DaoxuanCombatEncounterIds.QiBeast;
        var encounter = DaoxuanCombatCatalog.GetEncounter(encounterId);
        return new DaoxuanCombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, encounter.SearchMilliseconds)
        };
    }

    private static bool NormalizeLearnedAbilities(DaoxuanCombatLoadout combat)
    {
        var learned = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var abilityId in combat.LearnedAbilityIds)
        {
            if (!string.IsNullOrWhiteSpace(abilityId)
                && DaoxuanCombatCatalog.TryGetAbility(abilityId, out _)
                && learned.Add(abilityId))
            {
                continue;
            }

            changed = true;
        }

        foreach (var slot in combat.AbilitySlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.AbilityId)
                && DaoxuanCombatCatalog.TryGetAbility(slot.AbilityId, out _)
                && learned.Add(slot.AbilityId))
            {
                changed = true;
            }
        }

        var normalized = learned.OrderBy(abilityId => abilityId, StringComparer.Ordinal).ToList();
        if (!changed && combat.LearnedAbilityIds.SequenceEqual(normalized, StringComparer.Ordinal)) return false;
        combat.LearnedAbilityIds = normalized;
        return true;
    }

    private static int GetDurationSeconds(
        DaoxuanQueuedAction queuedAction,
        DaoxuanActionDefinition definition)
    {
        return queuedAction.TravelPlan?.TotalDurationSeconds ?? definition.DurationSeconds;
    }

    private static void EnsureTotalRepeats(DaoxuanQueuedAction action)
    {
        if (action.TotalRepeats != 0) return;
        action.TotalRepeats = NormalizeRepeats(action.RemainingRepeats);
    }

    private static int GetConfiguredRepeats(DaoxuanQueuedAction action)
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

    private static bool SetTotalRepeats(DaoxuanQueuedAction action, int repeats)
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

    private static void Touch(DaoxuanPlayerState state, DaoxuanDirtySections sections)
    {
        state.Version++;
        state.DirtySections |= sections;
    }

    /// <summary>
    /// 向玩家状态中追加一条结构化领域日志事件，并在在线时同步入队挂起推送列表。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="message">事件模板文本（支持双大括号实体标签）。</param>
    /// <param name="occurredAt">事件发生时间（为空则默认使用上次推进时间戳）。</param>
    public static void AddEvent(
        DaoxuanPlayerState state,
        string message,
        DateTimeOffset? occurredAt = null)
    {
        var entry = new DaoxuanLogEvent
        {
            Sequence = checked(++state.EventSequence),
            OccurredAt = occurredAt ?? state.LastSimulatedAt,
            Template = message
        };

        state.StructuredEventLog.Add(entry);
        if (state.PresenceMode == DaoxuanPresenceMode.Online)
        {
            state.PendingEvents.Add(entry);
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

/// <summary>
/// 领域命令执行结果记录。
/// </summary>
/// <param name="Success">是否执行成功。</param>
/// <param name="Code">状态码（如 "OK", "ACTION_NOT_FOUND" 等）。</param>
/// <param name="Message">用户可见的中文提示信息。</param>
public sealed record DaoxuanCommandResult(bool Success, string Code, string Message)
{
    /// <summary>构建成功执行结果。</summary>
    public static DaoxuanCommandResult Ok(string message) => new(true, "OK", message);

    /// <summary>构建失败执行结果。</summary>
    public static DaoxuanCommandResult Fail(string code, string message) => new(false, code, message);
}

