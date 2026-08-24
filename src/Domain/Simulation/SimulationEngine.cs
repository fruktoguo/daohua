// ==============================================================================
// 文件名称: SimulationEngine.cs
// 主要职责: 行动队列与修仙世界模拟的唯一确定性推进与结算引擎。
// 核心逻辑 / 定义内容:
//   1. 玩家初始聚合状态生成（CreateDefaultState）与状态合法性归一化（NormalizeState）。
//   2. 按行动完成边界推进模拟时间（Advance），处理修炼、采集、探索、打怪、突破等行动状态机。
//   3. 离线挂机确定性快进、自动消耗品使用、精力/寿元/修为结算与事件日志记录。
// ==============================================================================

using Domain.Constants;
using Domain.GameData;
using Domain.Models;

namespace Domain.Simulation;

/// <summary>
/// 行动队列的唯一结算入口。
/// 结算按“行动完成边界”推进，不按离线时长逐秒循环，服务重启后可以直接快进到目标时间。
/// </summary>
public static class SimulationEngine
{
    private const int MaxTransitionsPerAdvance = Domain.Constants.SimulationConstants.MaxTransitionsPerAdvance;
    private const int EventLogLimit = Domain.Constants.SimulationConstants.EventLogLimit;
    private const int PendingEventLimit = Domain.Constants.SimulationConstants.PendingEventLimit;
    private const int MinimumCombatSearchMilliseconds = Domain.Constants.SimulationConstants.MinimumCombatSearchMilliseconds;
    private const int MaximumCombatSearchMilliseconds = Domain.Constants.SimulationConstants.MaximumCombatSearchMilliseconds;

    /// <summary>
    /// 为新创建的玩家生成默认初始聚合状态，包括默认采集/炼丹/修炼队列。
    /// </summary>
    /// <param name="playerId">玩家唯一 GUID。</param>
    /// <param name="name">玩家道号/昵称。</param>
    /// <param name="now">创建时间戳。</param>
    /// <param name="account">所属账号标识。</param>
    /// <returns>初始化的玩家聚合根状态对象。</returns>
    public static PlayerState CreateDefaultState(
        Guid playerId,
        string? name = null,
        DateTimeOffset? now = null,
        string? account = null)
    {
        var timestamp = NormalizeTime(now ?? DateTimeOffset.UtcNow);
        var resolvedName = string.IsNullOrWhiteSpace(name) ? "无名修士" : name.Trim();
        var resolvedAccount = string.IsNullOrWhiteSpace(account) ? resolvedName : account.Trim();
        var state = new PlayerState
        {
            PlayerId = playerId,
            Name = resolvedName,
            Account = resolvedAccount,
            CreatedAt = timestamp,
            LastSimulatedAt = timestamp,
            PresenceMode = PresenceMode.Online
        };

        EnsureCombatResources(state, timestamp);
        AddEvent(state, "仙缘初辟，踏入道途。");
        return state;
    }

    /// <summary>
    /// 归一化校验玩家状态，修剪非法/过期数据，补齐缺失集合并标记脏段。
    /// </summary>
    /// <param name="state">待归一化的玩家状态聚合根。</param>
    /// <returns>如果有任何字段被修复或更改则返回 true，否则返回 false。</returns>
    public static bool NormalizeState(PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Queue ??= new ActionQueueState();
        var queue = state.Queue;
        var queueChanged = false;
        var inventoryChanged = false;
        var combatChanged = false;
        var combatProgressChanged = false;
        if (state.Inventory is null)
        {
            state.Inventory = new InventoryState();
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
        state.World ??= new WorldState();
        var worldChanged = WorldCatalog.Normalize(state.World);
        queue.Entries ??= [];

        for (var index = queue.Entries.Count - 1; index >= 0; index--)
        {
            var entry = queue.Entries[index];
            if (!ActionCatalog.TryGet(entry.ActionId, out var definition)
                || (definition.ExecutionKind == ActionExecutionKind.Travel
                    && !IsValidTravelPlan(state.World, entry.TravelPlan)))
            {
                queue.Entries.RemoveAt(index);
                queueChanged = true;
                continue;
            }

            if (definition.ExecutionKind == ActionExecutionKind.Travel)
            {
                entry.TotalRepeats = 1;
                entry.RemainingRepeats = 1;
                entry.DurationSeconds = entry.TravelPlan!.TotalDurationSeconds;
            }
            else if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
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
        foreach (var actionType in Enum.GetValues<ActionType>())
        {
            if (!queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
            {
                queue.AutoUseSlots[actionType] = Enumerable
                    .Range(0, CombatLimits.AutoUseSlotCount)
                    .Select(_ => new AutoUseSlot())
                    .ToList();
                queueChanged = true;
                continue;
            }

            queueChanged |= NormalizeSlots(
                slots,
                CombatLimits.AutoUseSlotCount,
                static () => new AutoUseSlot());
        }

        state.Combat ??= new CombatLoadout();
        if (state.Visual is null)
        {
            state.Visual = new CharacterVisualDefinition
            {
                Renderer = VisualRendererKind.Text,
                Source = state.Name[..1]
            };
            combatChanged = true;
        }
        if (state.CombatResources is null)
        {
            state.CombatResources = new CombatResourceState();
            combatChanged = true;
        }
        state.Combat.AbilitySlots ??= [];
        state.Combat.ConsumableSlots ??= [];
        state.Combat.Equipment ??= [];
        state.Combat.LearnedAbilityIds ??= [];
        state.Combat.AbilityExperiences ??= new(StringComparer.Ordinal);
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
            CombatLimits.AbilitySlotCount,
            static () => new CombatAbilitySlot());
        combatChanged |= NormalizeLearnedAbilities(state.Combat);
        combatChanged |= NormalizeSlots(
            state.Combat.ConsumableSlots,
            CombatLimits.ConsumableSlotCount,
            static () => new ConsumableSlot());
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

        if (state.CombatResources is null)
        {
            state.CombatResources = new CombatResourceState();
            combatChanged = true;
        }
        else
        {
            state.CombatResources.AbilityCooldowns ??= new(StringComparer.Ordinal);
            state.CombatResources.ConsumableSlotCooldowns ??= [];
        }

        if (state.CombatEncounterCompletions is null)
        {
            state.CombatEncounterCompletions = new Dictionary<string, long>(StringComparer.Ordinal);
            combatProgressChanged = true;
        }
        else
        {
            foreach (var encounterId in state.CombatEncounterCompletions
                         .Where(pair => pair.Value < 0 || !CombatCatalog.TryGetEncounter(pair.Key, out _))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                state.CombatEncounterCompletions.Remove(encounterId);
                combatProgressChanged = true;
            }
        }

        var dirty = (queueChanged ? DirtySections.Queue : DirtySections.None)
            | (inventoryChanged ? DirtySections.Inventory : DirtySections.None)
            | (combatChanged ? DirtySections.Combat : DirtySections.None)
            | (combatProgressChanged ? DirtySections.Stats : DirtySections.None)
            | (worldChanged ? DirtySections.World : DirtySections.None);
        if (dirty != DirtySections.None) Touch(state, dirty);
        return dirty != DirtySections.None;
    }

    /// <summary>
    /// 向玩家行动队列尾部追加一个常规行动。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="actionId">行动原型 ID。</param>
    /// <param name="repeats">重复次数（-1 表示无限循环，或 1 ~ 1000）。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Enqueue(
        PlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ActionCatalog.TryGet(actionId, out var definition))
        {
            return CommandResult.Fail("ACTION_NOT_FOUND", "行动不存在。");
        }

        if (definition.HiddenFromCatalog || definition.ExecutionKind == ActionExecutionKind.Travel)
        {
            return CommandResult.Fail("ACTION_NOT_SELECTABLE", "该行动不能直接加入队列。");
        }

        if (!WorldCatalog.CanPerformAction(state.World, definition))
        {
            return CommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该行动。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return CommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        var action = CreateQueuedAction(actionId, repeats);
        state.Queue.Entries.Add(action);

        Touch(state, DirtySections.Queue | DirtySections.Events);
        AddEvent(state, $"已加入行动：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return CommandResult.Ok("行动已加入队列。");
    }

    /// <summary>
    /// 立即向队列头部插入并启动一次历练战斗行动。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="actionId">历练行动 ID。</param>
    /// <param name="repeats">重复次数。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult StartCombat(
        PlayerState state,
        string actionId,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ActionCatalog.TryGet(actionId, out var definition))
        {
            return CommandResult.Fail("ACTION_NOT_FOUND", "历练行动不存在。");
        }

        if (definition.ExecutionKind != ActionExecutionKind.CombatEncounter)
        {
            return CommandResult.Fail("ACTION_NOT_COMBAT", "该行动不是历练战斗。");
        }

        if (!WorldCatalog.CanPerformAction(state.World, definition))
        {
            return CommandResult.Fail("ACTION_UNAVAILABLE_HERE", "当前位置不能进行该历练。");
        }

        if (repeats != -1 && (repeats < 1 || repeats > 1000))
        {
            return CommandResult.Fail("INVALID_REPEATS", "重复次数必须为无限或 1 到 1000 之间。");
        }

        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            if (ActionCatalog.TryGet(current.ActionId, out var currentDefinition)
                && currentDefinition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                state.Queue.Entries.RemoveAt(0);
            }
            else
            {
                current.Status = ActionStatus.Queued;
            }
        }

        EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
        state.Queue.Entries.Insert(0, CreateQueuedAction(actionId, repeats));
        Touch(state, DirtySections.Queue | DirtySections.Combat | DirtySections.Events);
        AddEvent(state, $"已立即开始历练：{{{{action:{definition.Id}|{definition.Name}}}}} × {(repeats == -1 ? "∞" : repeats)}");
        return CommandResult.Ok("历练已开始。");
    }

    /// <summary>
    /// 规划前往目标节点的大地图路线并将其置于队列首位开始赶路。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="destinationNodeId">目的地地点节点 ID。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult TravelTo(PlayerState state, string destinationNodeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(destinationNodeId)
            || !WorldCatalog.TryPlanTravel(state.World, destinationNodeId, out var plan))
        {
            return CommandResult.Fail("TRAVEL_ROUTE_NOT_FOUND", "无法从当前位置规划到目标地点的路线。");
        }

        var hadCombat = state.Queue.Entries.FirstOrDefault() is { } oldTop
            && ActionCatalog.TryGet(oldTop.ActionId, out var oldTopDef)
            && (oldTopDef.ExecutionKind == ActionExecutionKind.CombatEncounter || oldTop.CombatExpedition is not null);

        state.Queue.Entries.RemoveAll(entry => entry.TravelPlan is not null);
        if (state.Queue.Entries.FirstOrDefault() is { } current)
        {
            current.Status = ActionStatus.Queued;
        }

        var destination = WorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
        state.Queue.Entries.Insert(0, new QueuedAction
        {
            ActionId = ActionIds.WorldTravel,
            TotalRepeats = 1,
            RemainingRepeats = 1,
            DurationSeconds = plan.TotalDurationSeconds,
            Status = ActionStatus.Queued,
            TravelPlan = plan
        });
        var dirty = DirtySections.Queue | DirtySections.Events;
        if (hadCombat)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= DirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(
            state,
            $"已规划前往 {{{{location:{destination.Id}|{destination.Name}}}}}，共 {plan.Segments.Count} 段山路，预计 {plan.TotalDurationSeconds} 秒");
        return CommandResult.Ok($"已开始前往{destination.Name}。");
    }

    /// <summary>
    /// 从行动队列中移除指定唯一编号的行动项。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="entryId">队列项唯一 EntryId。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Remove(PlayerState state, Guid entryId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        if (index < 0)
        {
            return CommandResult.Fail("ACTION_NOT_FOUND", "队列中没有找到该行动。");
        }

        var removed = state.Queue.Entries[index];
        var definition = ActionCatalog.Get(removed.ActionId);
        var actionName = removed.TravelPlan is null
            ? definition.Name
            : $"赶往{WorldCatalog.GetNode(removed.TravelPlan.MapId, removed.TravelPlan.DestinationNodeId).Name}";
        state.Queue.Entries.RemoveAt(index);
        var dirty = DirtySections.Queue | DirtySections.Events;
        if (removed.CombatExpedition is not null || definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
        {
            EnsureAliveAfterLeavingCombat(state, state.LastSimulatedAt);
            dirty |= DirtySections.Combat;
        }
        Touch(state, dirty);
        AddEvent(state, $"已从队列移除行动：{actionName}");
        return CommandResult.Ok("行动已移除。");
    }

    /// <summary>
    /// 调整队列中某行动项的前后顺序（-1 表示前移，1 表示后移）。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="entryId">队列项 EntryId。</param>
    /// <param name="direction">移动方向（-1 或 1）。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Move(PlayerState state, Guid entryId, int direction)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (direction is not (-1 or 1))
        {
            return CommandResult.Fail("INVALID_DIRECTION", "队列移动方向无效。");
        }

        var index = state.Queue.Entries.FindIndex(action => action.EntryId == entryId);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= state.Queue.Entries.Count)
        {
            return CommandResult.Fail("QUEUE_EDGE", "行动已经在队列边界。");
        }

        if (state.Queue.Entries[index].TravelPlan is not null
            || state.Queue.Entries[targetIndex].TravelPlan is not null)
        {
            return CommandResult.Fail("TRAVEL_PRIORITY", "赶路行动必须保持在队列首位。");
        }

        (state.Queue.Entries[index], state.Queue.Entries[targetIndex]) =
            (state.Queue.Entries[targetIndex], state.Queue.Entries[index]);
        Touch(state, DirtySections.Queue);
        return CommandResult.Ok("队列顺序已更新。");
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
    public static CommandResult SetAutoUse(
        PlayerState state,
        ActionType actionType,
        string? itemId,
        int quantity,
        bool enabled,
        int slotIndex = 0,
        IReadOnlyList<CombatTrigger>? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return CommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!Enum.IsDefined(actionType))
        {
            return CommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        if (slotIndex < 0 || slotIndex >= CombatLimits.AutoUseSlotCount)
        {
            return CommandResult.Fail(
                "INVALID_SLOT_INDEX",
                $"自动使用槽位序号必须在 0 到 {CombatLimits.AutoUseSlotCount - 1} 之间。");
        }

        if (!ValidateTriggers(triggers))
        {
            return CommandResult.Fail("INVALID_TRIGGERS", "自动使用触发条件无效。");
        }

        if (!string.IsNullOrWhiteSpace(itemId)
            && !ItemCatalog.CanUseInAction(itemId, actionType))
        {
            return CommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }

        if (!state.Queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
        {
            slots = [];
            state.Queue.AutoUseSlots[actionType] = slots;
        }

        while (slots.Count < CombatLimits.AutoUseSlotCount)
        {
            slots.Add(new AutoUseSlot());
        }

        var normalizedTriggers = triggers is { Count: > 0 } ? CloneTriggers(triggers) : null;
        var currentSlot = slots[slotIndex];
        if (currentSlot.ItemId == itemId
            && currentSlot.Quantity == quantity
            && currentSlot.Enabled == (enabled && !string.IsNullOrWhiteSpace(itemId))
            && TriggersEqual(currentSlot.Triggers, normalizedTriggers))
        {
            return CommandResult.Ok("自动使用槽无需更新。");
        }

        // 银河奶牛式规则：若更换了槽位物品或卸下，已开启使用的消耗品剩余时间直接作废清零（已扣除物品不退回）
        var remaining = currentSlot.ItemId == itemId ? currentSlot.RemainingDurationSeconds : 0.0;
        if (currentSlot.ItemId != itemId && currentSlot.RemainingDurationSeconds > 0 && !string.IsNullOrWhiteSpace(currentSlot.ItemId))
        {
            var oldName = ItemCatalog.TryGet(currentSlot.ItemId, out var oldItem) ? oldItem.Name : currentSlot.ItemId;
            AddEvent(state, $"切换自动使用槽，已开启的 {{{{item:{currentSlot.ItemId}|{oldName}}}}} 剩余时间作废");
        }

        slots[slotIndex] = new AutoUseSlot
        {
            ItemId = itemId,
            Quantity = quantity,
            Enabled = enabled && !string.IsNullOrWhiteSpace(itemId),
            RemainingDurationSeconds = remaining,
            Triggers = normalizedTriggers
        };
        Touch(state, DirtySections.Queue | DirtySections.Events);
        return CommandResult.Ok("自动使用槽已更新。");
    }

    /// <summary>
    /// 手动立即使用指定背包消耗品，获得对应 Buff 增益。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="itemId">消耗品 ID。</param>
    /// <param name="actionType">生效动作类型。</param>
    /// <param name="quantity">使用数量。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult UseItem(
        PlayerState state,
        string itemId,
        ActionType actionType,
        int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (quantity is < 1 or > 99)
        {
            return CommandResult.Fail("INVALID_QUANTITY", "使用数量必须在 1 到 99 之间。");
        }

        if (!ItemCatalog.TryGet(itemId, out var item)
            || item.Consumable is null
            || !item.Consumable.UsableInActionTypes.Contains(actionType))
        {
            return CommandResult.Fail("ITEM_NOT_SUPPORTED", "该物品不能用于当前行动类型。");
        }
        if (item.Category == ItemCategory.Manual
            || item.Id.StartsWith("item.tome.", StringComparison.Ordinal)
            || item.Id.StartsWith("item.fragment.", StringComparison.Ordinal))
        {
            state.Combat.LearnedAbilityIds ??= [];
            state.Combat.AbilityExperiences ??= new(StringComparer.Ordinal);

            if (item.Id.StartsWith("item.tome.", StringComparison.Ordinal))
            {
                if (state.Inventory.Get(itemId) < 1)
                {
                    return CommandResult.Fail("INSUFFICIENT_ITEM", "功法秘籍数量不足。");
                }

                var abilityId = "ability." + item.Id["item.tome.".Length..];
                if (!CombatCatalog.TryGetAbility(abilityId, out var ability))
                {
                    return CommandResult.Fail("ABILITY_NOT_FOUND", "未找到对应的功法定义。");
                }

                if (!state.Combat.LearnedAbilityIds.Contains(abilityId, StringComparer.Ordinal))
                {
                    state.Inventory.Add(itemId, -1);
                    state.Combat.LearnedAbilityIds.Add(abilityId);
                    state.Combat.AbilityExperiences[abilityId] = SkillTomeConstants.FirstLearnExp;
                    Touch(state, DirtySections.Inventory | DirtySections.Combat | DirtySections.Events);
                    AddEvent(state, $"领悟并学会功法神通 {{{{ability:{ability.Id}|{ability.Name}}}}}！");
                    return CommandResult.Ok($"已成功学会功法【{ability.Name}】！");
                }
                else
                {
                    state.Inventory.Add(itemId, -1);
                    var newExp = state.Combat.AbilityExperiences.GetValueOrDefault(abilityId) + SkillTomeConstants.SubsequentTomeExp;
                    state.Combat.AbilityExperiences[abilityId] = newExp;
                    var (lvl, _, _, _) = ExperienceConstants.GetProgress(newExp);
                    Touch(state, DirtySections.Inventory | DirtySections.Combat | DirtySections.Events);
                    AddEvent(state, $"研读功法秘籍，{{{{ability:{ability.Id}|{ability.Name}}}}} 获得 {SkillTomeConstants.SubsequentTomeExp:F0} 点功法经验！当前等级：Lv.{lvl}");
                    return CommandResult.Ok($"研读成功，【{ability.Name}】获得 10000 点经验（当前等级：Lv.{lvl}）！");
                }
            }

            if (item.Id.StartsWith("item.fragment.", StringComparison.Ordinal))
            {
                var abilityId = "ability." + item.Id["item.fragment.".Length..];
                if (!CombatCatalog.TryGetAbility(abilityId, out var ability))
                {
                    return CommandResult.Fail("ABILITY_NOT_FOUND", "未找到对应的功法定义。");
                }

                if (!state.Combat.LearnedAbilityIds.Contains(abilityId, StringComparer.Ordinal))
                {
                    return CommandResult.Fail("ABILITY_NOT_LEARNED", $"尚未掌握【{ability.Name}】完整秘籍，无法研读残卷。");
                }

                if (state.Inventory.Get(itemId) < quantity)
                {
                    return CommandResult.Fail("INSUFFICIENT_ITEM", "残卷数量不足。");
                }

                state.Inventory.Add(itemId, -quantity);
                var expGain = SkillTomeConstants.FragmentExp * quantity;
                var newExp = state.Combat.AbilityExperiences.GetValueOrDefault(abilityId) + expGain;
                state.Combat.AbilityExperiences[abilityId] = newExp;
                var (lvl, _, _, _) = ExperienceConstants.GetProgress(newExp);
                Touch(state, DirtySections.Inventory | DirtySections.Combat | DirtySections.Events);
                AddEvent(state, $"研读残卷，{{{{ability:{ability.Id}|{ability.Name}}}}} 获得 {expGain:F0} 点功法经验！当前等级：Lv.{lvl}");
                return CommandResult.Ok($"研读成功，【{ability.Name}】获得 {expGain:F0} 点经验（当前等级：Lv.{lvl}）！");
            }
        }

        if (item.Consumable.DurationSeconds > 0 || item.Consumable.IsContinuous)
        {
            return CommandResult.Fail("ITEM_REQUIRES_SLOT", "持续性物品请装配至对应行动分类的自动使用槽位中生效。");
        }

        if (item.Consumable.Buffs.Count == 0
            && (item.Consumable.HitpointRestore > 0 || item.Consumable.ManapointRestore > 0))
        {
            return CommandResult.Fail("ITEM_REQUIRES_COMBAT", "该物品只能在历练战斗事件中自动使用。");
        }

        if (state.Inventory.Get(itemId) < quantity)
        {
            return CommandResult.Fail("INSUFFICIENT_ITEM", "物品数量不足。");
        }

        // 手动使用共享自动使用槽的 Buff 规则；战斗中的即时生命恢复由战斗事件处理器负责。
        state.Inventory.Add(itemId, -quantity);
        ApplyBuffEffects(state, item, quantity, state.LastSimulatedAt);
        Touch(state, DirtySections.Inventory | DirtySections.Buffs | DirtySections.Events);
        AddEvent(state, $"手动使用 {{{{item:{item.Id}|{item.Name}}}}} × {quantity}");
        return CommandResult.Ok("物品已使用。");
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
    public static CommandResult SetCombatLoadout(
        PlayerState state,
        IReadOnlyList<CombatAbilitySlot>? abilitySlots,
        IReadOnlyList<ConsumableSlot>? consumableSlots,
        double? combatDropRate = null,
        double? combatDropQuantity = null,
        double? combatRareFind = null,
        double? tenacity = null,
        double? searchSpeedRatio = null,
        IReadOnlyList<EquippedItem>? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ValidateCombatMultiplier(combatDropRate)
            || !ValidateCombatMultiplier(combatDropQuantity)
            || !ValidateCombatMultiplier(combatRareFind))
        {
            return CommandResult.Fail("INVALID_COMBAT_DROP_BONUS", "战斗掉落倍率必须在 -100% 到 100000% 之间。");
        }

        if (!ValidateTenacity(tenacity))
        {
            return CommandResult.Fail("INVALID_TENACITY", "韧性必须在 0 到 100000 之间。");
        }
        if (!ValidateSearchSpeedRatio(searchSpeedRatio))
        {
            return CommandResult.Fail("INVALID_SEARCH_SPEED", "寻怪速度必须在 -90% 到 100000% 之间。");
        }

        List<EquippedItem>? normalizedEquipment = null;
        if (equipment is not null)
        {
            if (!TryNormalizeEquipment(equipment, out normalizedEquipment, out var equipmentError))
            {
                return CommandResult.Fail("INVALID_EQUIPMENT", equipmentError);
            }
        }

        List<CombatAbilitySlot>? normalizedAbilities = null;
        if (abilitySlots is not null)
        {
            if (abilitySlots.Count > CombatLimits.AbilitySlotCount
                || abilitySlots.Any(slot => slot.Level is < 1 or > 100))
            {
                return CommandResult.Fail("INVALID_ABILITY_SLOTS", "功法槽数量或等级无效。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !CombatCatalog.TryGetAbility(slot.AbilityId, out _)))
            {
                return CommandResult.Fail("ABILITY_NOT_FOUND", "功法不存在。");
            }

            if (abilitySlots.Any(slot => !string.IsNullOrWhiteSpace(slot.AbilityId)
                && !state.Combat.LearnedAbilityIds.Contains(slot.AbilityId, StringComparer.Ordinal)))
            {
                return CommandResult.Fail("ABILITY_NOT_LEARNED", "尚未习得该功法。");
            }

            if (abilitySlots.Any(slot => !ValidateTriggers(slot.Triggers)))
            {
                return CommandResult.Fail("INVALID_ABILITY_TRIGGERS", "功法触发条件无效。");
            }

            normalizedAbilities = abilitySlots
                .Select(slot => new CombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = CloneTriggers(slot.Triggers)
                })
                .Concat(Enumerable.Range(
                    abilitySlots.Count,
                    CombatLimits.AbilitySlotCount - abilitySlots.Count).Select(_ => new CombatAbilitySlot()))
                .ToList();
        }

        if (!TryValidateConsumableSlots(consumableSlots, out var error))
        {
            return CommandResult.Fail("INVALID_CONSUMABLE_SLOTS", error);
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

        if (combatDropRate is not null) state.Combat.CombatDropRate = combatDropRate.Value;
        if (combatDropQuantity is not null) state.Combat.CombatDropQuantity = combatDropQuantity.Value;
        if (combatRareFind is not null) state.Combat.CombatRareFind = combatRareFind.Value;
        if (tenacity is not null) state.Combat.Tenacity = tenacity.Value;
        if (searchSpeedRatio is not null) state.Combat.SearchSpeedRatio = searchSpeedRatio.Value;
        Touch(state, DirtySections.Combat);
        return CommandResult.Ok("战斗配置已更新。");
    }

    /// <summary>
    /// 开启或关闭队列循环模式。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="enabled">是否开启循环。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult SetLoop(PlayerState state, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Queue.LoopEnabled == enabled)
        {
            return CommandResult.Ok(enabled ? "循环已经开启。" : "循环已经关闭。");
        }

        state.Queue.LoopEnabled = enabled;
        Touch(state, DirtySections.Queue);
        return CommandResult.Ok(enabled ? "循环已开启。" : "循环已关闭。");
    }

    /// <summary>
    /// 设置玩家在线/挂机状态（Online/BackgroundSimulated/FullyOffline），并在此之前追赶进度。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <param name="mode">目标存在状态。</param>
    /// <param name="now">当前时间戳。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult SetPresence(
        PlayerState state,
        PresenceMode mode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedNow = NormalizeTime(now);
        if (state.PresenceMode != PresenceMode.FullyOffline)
        {
            AdvanceTo(state, normalizedNow);
        }

        state.PresenceMode = mode;
        Touch(state, DirtySections.Presence);
        return CommandResult.Ok($"状态已切换为 {mode}。");
    }

    /// <summary>
    /// 执行破境突破操作。当修为达到当前大境界/层级上限且标记待突破时方可成功。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <returns>突破执行结果。</returns>
    public static CommandResult Breakthrough(PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var progress = CombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;
        var isCap = progress.ExpToNext > 0 && expToCap <= 0.0001 && state.Stats.Spirit > 0;
        if (!state.IsPendingBreakthrough && !isCap)
        {
            return CommandResult.Fail("CULTIVATION_NOT_CAPPED", "修为尚未圆满，无法突破。");
        }

        state.IsPendingBreakthrough = false;
        Touch(state, DirtySections.Stats | DirtySections.Events | DirtySections.Combat);
        AddEvent(state, "破境功成！洗髓伐经，道行更进一层。");
        return CommandResult.Ok("突破成功！");
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
        PlayerState state,
        double baseSpiritGain)
    {
        if (baseSpiritGain <= 0) return (0, 0, 0);

        var progress = CombatConstants.CalculateRealmProgress(state.Stats.Spirit);
        var expToCap = progress.ExpToNext - progress.CurrentLayerExp;

        if (progress.ExpToNext <= 0.0 || progress.Level >= CombatConstants.MaxRealmLevel)
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
    /// <param name="maxTransitions">单次推演允许的最大动作/状态流转次数上限。</param>
    /// <returns>推进结算摘要结果（脏段、完成动作数、状态转移数、产生事件列表等）。</returns>
    public static SimulationResult AdvanceTo(
        PlayerState state,
        DateTimeOffset targetTime,
        int maxTransitions = SimulationConstants.MaxTransitionsPerAdvance,
        TimeSpan? maxOfflineDuration = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var target = NormalizeTime(targetTime);
        var cursor = NormalizeTime(state.LastSimulatedAt);
        if (target < cursor)
        {
            return new SimulationResult(DirtySections.None, 0, 0, []);
        }

        if (state.PresenceMode == PresenceMode.FullyOffline)
        {
            // 完全离线账号不参与行动结算；推进时间戳即可避免重新启用时追溯这段时间。
            state.LastSimulatedAt = target;
            return new SimulationResult(DirtySections.None, 0, 0, []);
        }

        // 施加最大离线时间跨度截断，防止无限长时间补算
        var effectiveMaxOffline = maxOfflineDuration ?? SimulationConstants.DefaultMaxOfflineDuration;
        var rawDuration = target - cursor;
        var duration = TimeSpan.FromMilliseconds(Math.Min(rawDuration.TotalMilliseconds, effectiveMaxOffline.TotalMilliseconds));
        target = cursor + duration;

        var dirty = DirtySections.None;
        var events = new List<string>();
        var completedActions = 0;
        var transitions = 0;
        var skippedForMissingInputs = new HashSet<Guid>();
        var hasPendingQueueRotation = false;
        var combatEvents = 0;

        while (cursor <= target && transitions < maxTransitions)
        {
            if (ExpireBuffs(state, cursor))
            {
                dirty |= DirtySections.Buffs;
            }

            if (state.Queue.Entries.Count == 0)
            {
                var available = GetAvailableMilliseconds(cursor, target);
                if (available > 0 && RegenerateResources(state, cursor, available))
                {
                    dirty |= DirtySections.Combat;
                }
                cursor = target;
                break;
            }

            var current = state.Queue.Entries[0];
            var definition = ActionCatalog.Get(current.ActionId);
            current.DurationSeconds = GetDurationSeconds(current, definition);
            var wasBlocked = current.Status == ActionStatus.Blocked;
            current.Status = ActionStatus.Running;

            if (!CanStartAction(state, current, definition))
            {
                current.Status = ActionStatus.Blocked;
                if (state.Queue.LoopEnabled)
                {
                    if (!skippedForMissingInputs.Add(current.EntryId))
                    {
                        // 全部行动都被跳过时队列空转：这段空闲时间同样享受生命和灵力自动回复。
                        var idleMilliseconds = GetAvailableMilliseconds(cursor, target);
                        if (idleMilliseconds > 0 && RegenerateResources(state, cursor, idleMilliseconds))
                        {
                            dirty |= DirtySections.Combat;
                        }
                        cursor = target;
                        break;
                    }

                    if (!wasBlocked)
                    {
                        state.Stats.BlockedActions++;
                        AddEvent(state, $"行动跳过：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足，已移至队尾");
                        dirty |= DirtySections.Queue | DirtySections.Stats | DirtySections.Events;
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
                    dirty |= DirtySections.Stats;
                }

                AddEvent(state, $"行动结束：{{{{action:{definition.Id}|{definition.Name}}}}}条件不满足");
                dirty |= DirtySections.Queue | DirtySections.Events;
                events.Add($"ended:{definition.Id}:missing-inputs");
                state.Queue.Entries.RemoveAt(0);
                transitions++;
                continue;
            }

            if (hasPendingQueueRotation)
            {
                dirty |= DirtySections.Queue;
                hasPendingQueueRotation = false;
            }

            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
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
                    ref combatEvents,
                    maxTransitions);
                skippedForMissingInputs.Clear();
                if (cursor >= target || transitions >= maxTransitions) break;
                continue;
            }

            // 单次任务最终耗时保底 0.1 秒（100ms），杜绝 1ms 碎片推进空转风暴，同时保持内部事件边界推进
            var durationMilliseconds = checked((long)Math.Max(100, Math.Max(0.1, current.DurationSeconds) * 1_000));
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
                    dirty |= DirtySections.QueueProgress;
                    if (current.TravelPlan is not null)
                    {
                        WorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                        dirty |= DirtySections.World;
                    }
                    if (RegenerateResources(state, cursor, progress))
                    {
                        dirty |= DirtySections.Combat;
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
                WorldCatalog.ApplyTravelProgress(state.World, current.TravelPlan, current.ProgressSeconds);
                dirty |= DirtySections.World;
            }
            if (RegenerateResources(state, cursor, remainingMilliseconds))
            {
                dirty |= DirtySections.Combat;
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

        var isCapped = transitions >= maxTransitions && cursor < target;
        if (isCapped)
        {
            AddEvent(state, "本次快进达到保护上限，剩余行动将在下一次推进继续结算");
            dirty |= DirtySections.Events;
            if (hasPendingQueueRotation)
            {
                dirty |= DirtySections.Queue;
            }
        }

        if (ExpireBuffs(state, cursor))
        {
            dirty |= DirtySections.Buffs;
        }

        if (dirty != DirtySections.None)
        {
            Touch(state, dirty);
        }

        state.LastSimulatedAt = cursor;
        return new SimulationResult(
            dirty,
            completedActions,
            transitions,
            events,
            combatEvents,
            ReachedTarget: !isCapped,
            Capped: isCapped);
    }

    /// <summary>
    /// 计算该玩家下一次需要发生状态转换或事件推进的目标时间戳（用于定时器调度与离线快进预判）。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <returns>下次事件触发时间戳，若无任何挂起事件或处于完全离线则返回 null。</returns>
    public static DateTimeOffset? GetNextTransitionAt(PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PresenceMode == PresenceMode.FullyOffline) return null;

        DateTimeOffset? next = state.Buffs
            .Where(buff => buff.ExpiresAt > state.LastSimulatedAt)
            .Select(buff => (DateTimeOffset?)NormalizeTime(buff.ExpiresAt))
            .OrderBy(value => value)
            .FirstOrDefault();
        if (state.Queue.Entries.Count == 0) return next;

        var current = state.Queue.Entries[0];
        if (!ActionCatalog.TryGet(current.ActionId, out var definition))
        {
            return state.LastSimulatedAt;
        }

        DateTimeOffset? actionDeadline;
        if (current.Status != ActionStatus.Running)
        {
            actionDeadline = state.LastSimulatedAt;
        }
        else if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
        {
            actionDeadline = GetNextCombatTransitionAt(state, current, definition);
        }
        else
        {
            var durationMilliseconds = checked((long)Math.Max(100, Math.Max(0.1, GetDurationSeconds(current, definition)) * 1_000));
            var progressMilliseconds = current.ProgressMilliseconds > 0
                ? current.ProgressMilliseconds
                : checked((long)Math.Max(0, current.ProgressSeconds) * 1_000);
            actionDeadline = state.LastSimulatedAt.AddMilliseconds(
                Math.Max(1, durationMilliseconds - Math.Clamp(progressMilliseconds, 0, durationMilliseconds)));
        }

        return next is null || actionDeadline < next ? actionDeadline : next;
    }

    private static DateTimeOffset? GetNextCombatTransitionAt(
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition)
    {
        var expedition = current.CombatExpedition ?? CreateCombatExpedition(definition);
        if (expedition.Phase != CombatExpeditionPhase.Combat)
        {
            var remaining = Math.Max(
                1,
                expedition.PhaseDurationMilliseconds - expedition.PhaseElapsedMilliseconds);
            return state.LastSimulatedAt.AddMilliseconds(remaining);
        }

        var snapshot = expedition.BattleSnapshot
            ?? throw new InvalidOperationException("历练处于战斗阶段但缺少 BattleSnapshot 战斗入场快照。");
        var result = CombatSimulator.Run(
            state.PlayerId,
            snapshot,
            TimeSpan.FromMilliseconds(Math.Max(0, expedition.PhaseElapsedMilliseconds)),
            playerSpirit: state.Stats.Spirit,
            stopAfterFirstVictory: true);
        if (result.OutcomeMilliseconds is not null)
        {
            return snapshot.BattleStartedAt.AddMilliseconds(result.OutcomeMilliseconds.Value);
        }

        return result.NextEventMilliseconds is { } nextEvent
            ? snapshot.BattleStartedAt.AddMilliseconds(nextEvent)
            : null;
    }

    private static void AdvanceCombatAction(
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition,
        ref DateTimeOffset cursor,
        DateTimeOffset target,
        ref DirtySections dirty,
        ref int completedActions,
        ref int transitions,
        ref int combatEvents,
        int maxTransitions = SimulationConstants.MaxTransitionsPerAdvance)
    {
        var expedition = current.CombatExpedition ??= CreateCombatExpedition(definition);
        EnsureCombatResources(state, cursor);
        current.DurationSeconds = 0;

        while (cursor <= target && transitions < maxTransitions)
        {
            if (expedition.Phase is CombatExpeditionPhase.InitialSearch
                or CombatExpeditionPhase.Search)
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
                        dirty |= DirtySections.Combat;
                    }
                    cursor = target;
                    dirty |= DirtySections.QueueProgress;
                    return;
                }

                cursor = cursor.AddMilliseconds(remaining);
                state.LastSimulatedAt = cursor;
                expedition.PhaseElapsedMilliseconds = 0;
                current.ProgressSeconds = 0;
                transitions++;
                if (RegenerateResources(state, cursor, remaining))
                {
                    dirty |= DirtySections.Combat;
                }

                var startedEncounterId = BeginCombatBattle(state, current, definition, cursor);
                var startedEncounter = CombatCatalog.GetEncounter(startedEncounterId);
                current.DurationSeconds = 0;
                AddEvent(state, $"发现 {{{{encounter:{startedEncounter.Id}|{startedEncounter.Name}}}}}，战斗开始。", cursor);
                dirty |= DirtySections.Queue | DirtySections.QueueProgress | DirtySections.Events;
                continue;
            }

            var snapshot = expedition.BattleSnapshot
                ?? throw new InvalidOperationException("历练处于战斗阶段但缺少 BattleSnapshot 战斗入场快照。");
            var encounter = CombatCatalog.GetEncounter(snapshot.EncounterId);
            var availableCombatMilliseconds = GetAvailableMilliseconds(cursor, target);
            var requestedElapsed = checked(expedition.PhaseElapsedMilliseconds + availableCombatMilliseconds);

            var result = CombatSimulator.Run(
                state.PlayerId,
                snapshot,
                TimeSpan.FromMilliseconds(requestedElapsed),
                playerSpirit: state.Stats.Spirit,
                stopAfterFirstVictory: true);

            dirty |= ApplyIncrementalCombatConsumption(state, expedition, result);
            dirty |= EmitIncrementalCombatEvents(state, expedition, result, cursor);
            dirty |= ApplyIncrementalCombatSettlement(
                state,
                expedition,
                snapshot.EncounterId,
                result,
                snapshot.BattleStartedAt.AddMilliseconds(result.SimulatedMilliseconds),
                ref combatEvents);
            if (result.FinalPlayer is { } currentPlayer)
            {
                SetCombatResources(state, currentPlayer);
                UpdateCombatCooldowns(state, result, snapshot.BattleStartedAt);
                dirty |= DirtySections.Combat;
            }

            if (result.OutcomeMilliseconds is not { } outcomeMilliseconds)
            {
                expedition.PhaseElapsedMilliseconds = requestedElapsed;
                current.ProgressSeconds = ToDisplaySeconds(requestedElapsed);
                current.DurationSeconds = 0;
                cursor = target;
                state.LastSimulatedAt = cursor;
                dirty |= DirtySections.QueueProgress;
                return;
            }

            var consumedMilliseconds = Math.Max(0, outcomeMilliseconds - expedition.PhaseElapsedMilliseconds);
            cursor = cursor.AddMilliseconds(consumedMilliseconds);
            state.LastSimulatedAt = cursor;
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
            current.Status = ActionStatus.Running;
            expedition.Phase = CombatExpeditionPhase.Search;
            expedition.PhaseDurationMilliseconds = CalculateCombatSearchMilliseconds(encounter, state.Combat);
            current.DurationSeconds = ToDisplaySeconds(expedition.PhaseDurationMilliseconds);
            dirty |= DirtySections.Queue | DirtySections.QueueProgress | DirtySections.Stats;
        }
    }

    private static string BeginCombatBattle(
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition,
        DateTimeOffset startedAt)
    {
        var expedition = current.CombatExpedition ??= new CombatExpeditionState();
        var nextBattleSequence = checked(expedition.BattleSequence + 1);
        var encounterId = SelectCombatEncounterId(state, definition, nextBattleSequence);
        EnsureCombatResources(state, startedAt);
        var initial = CombatSimulator.CreatePlayerState(
            state.Combat,
            state.Buffs,
            startedAt,
            state.CombatResources.Hitpoints,
            state.CombatResources.Manapoints,
            state.Stats.Spirit);
        SetCombatResources(state, initial);

        var initialAbilityCooldowns = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (abilityId, readyAt) in state.CombatResources.AbilityCooldowns)
        {
            var remaining = Math.Max(0, (long)Math.Ceiling((readyAt - startedAt).TotalMilliseconds));
            if (remaining > 0)
            {
                initialAbilityCooldowns[abilityId] = remaining;
            }
        }
        var initialConsumableCooldowns = state.CombatResources.ConsumableSlotCooldowns
            .Select(readyAt => Math.Max(0, (long)Math.Ceiling((readyAt - startedAt).TotalMilliseconds)))
            .ToList();

        var snapshot = new BattleSnapshot
        {
            EncounterId = encounterId,
            BattleSequence = nextBattleSequence,
            CompletedEncountersBeforeBattle = state.CombatEncounterCompletions.GetValueOrDefault(encounterId),
            BattleStartedAt = startedAt,
            InitialHitpoints = initial.Hitpoints,
            InitialManapoints = initial.Manapoints,
            InitialAbilityCooldowns = initialAbilityCooldowns,
            InitialConsumableCooldowns = initialConsumableCooldowns,
            BattleLoadout = state.Combat.Clone(),
            BattleBuffs = state.Buffs.Select(CloneCombatBuff).ToList(),
            BattleInventory = new Dictionary<string, long>(state.Inventory.Items, StringComparer.Ordinal),
            ConfigFingerprint = GameDataCatalog.Current.Fingerprint
        };

        expedition.BattleSnapshot = snapshot;
        expedition.BattleSequence = nextBattleSequence;
        expedition.Phase = CombatExpeditionPhase.Combat;
        expedition.PhaseDurationMilliseconds = 0;
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EmittedEventCount = 0;
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

    private static DirtySections ApplyIncrementalCombatConsumption(
        PlayerState state,
        CombatExpeditionState expedition,
        CombatRunResult result)
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

        return changed ? DirtySections.Inventory : DirtySections.None;
    }

    private static DirtySections EmitIncrementalCombatEvents(
        PlayerState state,
        CombatExpeditionState expedition,
        CombatRunResult result,
        DateTimeOffset occurredAt)
    {
        if (expedition.EmittedEventCount >= result.Events.Count) return DirtySections.None;
        foreach (var entry in result.Events.Skip(expedition.EmittedEventCount))
        {
            AddEvent(state, $"战斗：{entry}", occurredAt);
        }
        expedition.EmittedEventCount = result.Events.Count;
        return DirtySections.Events;
    }

    private static DirtySections ApplyIncrementalCombatSettlement(
        PlayerState state,
        CombatExpeditionState expedition,
        string encounterId,
        CombatRunResult result,
        DateTimeOffset settledAt,
        ref int combatEvents)
    {
        var dirty = DirtySections.None;
        var processedEventDelta = Math.Max(0, result.ProcessedEvents - expedition.AppliedProcessedEvents);
        if (processedEventDelta > 0)
        {
            combatEvents = checked(combatEvents + processedEventDelta);
            expedition.AppliedProcessedEvents = result.ProcessedEvents;
        }

        var spiritDelta = Math.Max(0, result.Spirit - expedition.AppliedSpirit);
        if (spiritDelta > 0)
        {
            state.Inventory.Add(ItemIds.Spirit, spiritDelta);
            expedition.AppliedSpirit = result.Spirit;
            dirty |= DirtySections.Inventory;
        }

        dirty |= ApplyIncrementalDrops(state, expedition.AppliedDroppedItems, result.DroppedItems);
        dirty |= ApplyIncrementalDrops(state, expedition.AppliedRareDroppedItems, result.RareDroppedItems);

        foreach (var application in result.BuffApplications)
        {
            ApplyCombatBuff(state, application, settledAt);
            dirty |= DirtySections.Buffs;
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
            dirty |= DirtySections.Stats | DirtySections.Combat;
        }

        return dirty;
    }

    private static DirtySections ApplyIncrementalDrops(
        PlayerState state,
        IDictionary<string, long> appliedDrops,
        IReadOnlyDictionary<string, long>? currentDrops)
    {
        if (currentDrops is not { Count: > 0 }) return DirtySections.None;
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
        return changed ? DirtySections.Inventory : DirtySections.None;
    }

    private static void FinishCombatBatch(
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition,
        DateTimeOffset completedAt,
        ref DirtySections dirty)
    {
        AddEvent(state, $"行动完成：{{{{action:{definition.Id}|{definition.Name}}}}}", completedAt);
        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.DurationSeconds = 0;
            current.Status = ActionStatus.Queued;
            current.CombatExpedition = CreateCombatExpedition(definition);
            state.Queue.Entries.RemoveAt(0);
            state.Queue.Entries.Add(current);
            EnsureAliveAfterLeavingCombat(state, completedAt);
        }
        else
        {
            state.Queue.Entries.RemoveAt(0);
            EnsureAliveAfterLeavingCombat(state, completedAt);
        }
        dirty |= DirtySections.Queue | DirtySections.Stats | DirtySections.Events | DirtySections.Combat;
    }

    private static void EnsureAliveAfterLeavingCombat(PlayerState state, DateTimeOffset at)
    {
        EnsureCombatResources(state, at);
        var attributes = CombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
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
        PlayerState state,
        DateTimeOffset at,
        double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0) return false;
        EnsureCombatResources(state, at);

        var attributes = CombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, at, state.Stats.Spirit);
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
        PlayerState state,
        CombatRunResult result,
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

    private static void ResetBattleSnapshot(CombatExpeditionState expedition)
    {
        expedition.BattleSnapshot = null;
        expedition.PhaseElapsedMilliseconds = 0;
        expedition.EmittedEventCount = 0;
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

    private static void EnsureCombatResources(PlayerState state, DateTimeOffset at)
    {
        state.CombatResources ??= new CombatResourceState();
        if (state.CombatResources.Initialized) return;
        SetCombatResources(state, CombatSimulator.CreatePlayerState(state.Combat, state.Buffs, at, spirit: state.Stats.Spirit));
    }

    private static void SetCombatResources(PlayerState state, CombatPlayerState player)
    {
        state.CombatResources ??= new CombatResourceState();
        state.CombatResources.Initialized = true;
        state.CombatResources.Hitpoints = Math.Clamp(player.Hitpoints, 0, player.MaxHitpoints);
        state.CombatResources.Manapoints = Math.Clamp(player.Manapoints, 0, player.MaxManapoints);
    }

    private static BuffState CloneCombatBuff(BuffState buff) => buff.Clone();

    private static int CalculateCombatSearchMilliseconds(
        CombatEncounterDefinition encounter,
        CombatLoadout loadout)
    {
        var baseMilliseconds = Math.Max(1, encounter.RespawnMilliseconds);
        var ratio = Math.Clamp(loadout.SearchSpeedRatio, -0.9, 1_000);
        return Math.Clamp(
            (int)Math.Ceiling(baseMilliseconds / (1 + ratio)),
            MinimumCombatSearchMilliseconds,
            MaximumCombatSearchMilliseconds);
    }

    private static CombatEncounterDefinition GetCombatEncounter(
        PlayerState state,
        ActionDefinition definition,
        CombatExpeditionState expedition)
    {
        return CombatCatalog.GetEncounter(ResolveCombatEncounterId(state, definition, expedition));
    }

    private static string ResolveCombatEncounterId(
        PlayerState state,
        ActionDefinition definition,
        CombatExpeditionState expedition)
    {
        if (expedition.BattleSnapshot is { EncounterId: { Length: > 0 } snapshotEncounterId }
            && CombatCatalog.TryGetEncounter(snapshotEncounterId, out _))
        {
            return snapshotEncounterId;
        }

        var battleSequence = Math.Max(1, expedition.BattleSequence);
        return SelectCombatEncounterId(state, definition, battleSequence);
    }

    private static string SelectCombatEncounterId(
        PlayerState state,
        ActionDefinition definition,
        long battleSequence)
    {
        if (state.World.NodeId is { Length: > 0 } nodeId)
        {
            var node = WorldCatalog.GetNode(state.World.MapId, nodeId);
            var candidates = node.EncounterIds
                .Where(id => CombatCatalog.TryGetEncounter(id, out _))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates[SelectStableEncounterIndex(state.PlayerId, nodeId, battleSequence, candidates.Length)];
            }
        }

        return definition.CombatEncounterId is { Length: > 0 } configured
            && CombatCatalog.TryGetEncounter(configured, out _)
                ? configured
                : CombatEncounterIds.QiBeast;
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
        PlayerState state,
        QueuedAction current,
        ActionDefinition definition,
        ref DirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        if (current.TravelPlan is not null)
        {
            var destination = WorldCatalog.GetNode(
                current.TravelPlan.MapId,
                current.TravelPlan.DestinationNodeId);
            state.Stats.CompletedActions++;
            state.Queue.Entries.RemoveAt(0);
            dirty |= DirtySections.Queue
                | DirtySections.World
                | DirtySections.Stats
                | DirtySections.Events;
            AddEvent(state, $"已抵达 {{{{location:{destination.Id}|{destination.Name}}}}}");
            events.Add($"arrived:{destination.Id}");
            return;
        }

        foreach (var input in definition.Inputs)
        {
            state.Inventory.Add(input.Key, -input.Value);
        }

        var actionDuration = definition.DurationSeconds > 0 ? definition.DurationSeconds : 1.0;
        var autoUseMultiplier = ProcessAutoUseItemsForAction(state, definition.Type, actionDuration, state.LastSimulatedAt, ref dirty);

        foreach (var output in definition.Outputs)
        {
            if (definition.Type == ActionType.Cultivation && output.Key == ItemIds.Qi)
            {
                continue;
            }

            var multiplier = definition.Type switch
            {
                ActionType.Exploration => GetGatheringOutputMultiplier(state, state.LastSimulatedAt) * autoUseMultiplier,
                ActionType.Alchemy => GetAlchemyOutputMultiplier(state, state.LastSimulatedAt) * autoUseMultiplier,
                _ => 1.0
            };
            var amount = Math.Abs(multiplier - 1.0) < 1e-9
                ? output.Value
                : Math.Max(1, (long)Math.Ceiling(output.Value * multiplier));
            state.Inventory.Add(output.Key, amount);
        }

        switch (definition.Type)
        {
            case ActionType.Exploration:
                state.Stats.HerbsCollected += GetProducedAmount(
                    state,
                    definition,
                    ItemIds.SpiritHerb,
                    GetGatheringOutputMultiplier(state, state.LastSimulatedAt) * autoUseMultiplier);
                break;
            case ActionType.Alchemy:
                state.Stats.PillsCrafted += GetProducedAmount(
                    state,
                    definition,
                    ItemIds.QiGatheringPill,
                    GetAlchemyOutputMultiplier(state, state.LastSimulatedAt) * autoUseMultiplier);
                break;
            case ActionType.Cultivation:
                var buffMultiplier = GetQiGainMultiplier(state, state.LastSimulatedAt);
                var totalCultivationMultiplier = buffMultiplier * autoUseMultiplier;

                // 引气入体效果：获得 10 修为，1 灵力（受持续消耗品引气丹等加成）
                var baseExp = 10.0;
                var baseQi = definition.Outputs.GetValueOrDefault(ItemIds.Qi, 1L);
                if (baseQi <= 0) baseQi = 1;

                var finalExp = baseExp * totalCultivationMultiplier;
                var finalQi = (long)Math.Round(baseQi * totalCultivationMultiplier, MidpointRounding.AwayFromZero);
                if (finalQi < 1 && totalCultivationMultiplier > 0) finalQi = 1;

                state.Inventory.Add(ItemIds.Qi, finalQi);
                state.Stats.CultivationCycles++;
                state.Stats.Qi += finalQi;
                var (actualSpiritGain, foundationGain, foundationConsumed) = AddCultivationSpirit(state, finalExp);
                if (foundationGain > 0)
                {
                    AddEvent(state, $"当前境界已满，运转吐纳将修为转化为 {foundationGain:F0} 点道基底蕴");
                }
                else if (foundationConsumed > 0)
                {
                    AddEvent(state, $"底蕴化生，获 3 倍修为加速 (+{actualSpiritGain:F0} 修为，消耗 {foundationConsumed:F0} 点底蕴)");
                }
                dirty |= DirtySections.Stats;
                break;
            case ActionType.Combat:
                CompleteCombatAction(
                    state,
                    definition,
                    ref dirty,
                    events,
                    ref combatEvents);
                break;
        }

        state.Stats.CompletedActions++;
        dirty |= DirtySections.Queue | DirtySections.Inventory | DirtySections.Stats | DirtySections.Events;
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
            current.Status = ActionStatus.Queued;
            return;
        }

        if (state.Queue.LoopEnabled)
        {
            current.RemainingRepeats = current.TotalRepeats;
            current.ProgressSeconds = 0;
            current.ProgressMilliseconds = 0;
            current.Status = ActionStatus.Queued;
            state.Queue.Entries.RemoveAt(0);
            state.Queue.Entries.Add(current);
            return;
        }

        state.Queue.Entries.RemoveAt(0);
    }

    private static double ProcessAutoUseItemsForAction(
        PlayerState state,
        ActionType actionType,
        double actionDurationSeconds,
        DateTimeOffset now,
        ref DirtySections dirty)
    {
        var multiplier = 1.0;
        if (!state.Queue.AutoUseSlots.TryGetValue(actionType, out var slots) || slots is null)
        {
            return multiplier;
        }

        foreach (var slot in slots)
        {
            if (slot is null
                || !slot.Enabled
                || string.IsNullOrWhiteSpace(slot.ItemId)
                || !ItemCatalog.TryGet(slot.ItemId, out var item)
                || item.Consumable is null
                || !item.Consumable.UsableInActionTypes.Contains(actionType))
            {
                continue;
            }

            var cons = item.Consumable;
            if (cons.DurationSeconds > 0 || cons.IsContinuous)
            {
                // 银河奶牛式持续消耗品
                // 1. 若剩余时间不足 0，且背包有货，扣除 1 个并充能完整持续时间
                if (slot.RemainingDurationSeconds <= 0.0)
                {
                    if (state.Inventory.Get(slot.ItemId) >= 1)
                    {
                        state.Inventory.Add(slot.ItemId, -1);
                        slot.RemainingDurationSeconds = cons.DurationSeconds;
                        dirty |= DirtySections.Inventory | DirtySections.Queue;
                        AddEvent(state, $"使用 {{{{item:{item.Id}|{item.Name}}}}}，持续 {cons.DurationSeconds:F0} 秒");
                    }
                }

                // 2. 若槽位处于有效状态（RemainingDurationSeconds > 0）
                if (slot.RemainingDurationSeconds > 0.0)
                {
                    var itemMult = actionType switch
                    {
                        ActionType.Cultivation => cons.CultivationMultiplier,
                        ActionType.Exploration => cons.GatheringMultiplier,
                        ActionType.Alchemy => cons.AlchemyMultiplier,
                        _ => 1.0
                    };
                    if (itemMult > 0)
                    {
                        multiplier *= itemMult;
                    }

                    // 3. 动作完成，扣除本次动作实际耗时
                    slot.RemainingDurationSeconds -= actionDurationSeconds;
                    dirty |= DirtySections.Queue;

                    // 4. 若透支/耗尽，直接归零（不足部分免单）
                    if (slot.RemainingDurationSeconds <= 0.0)
                    {
                        slot.RemainingDurationSeconds = 0.0;
                        AddEvent(state, $"{{{{item:{item.Id}|{item.Name}}}}} 持续时间已耗尽");
                    }
                }
            }
            else
            {
                // 传统即时/Buff型消耗品
                if (state.Inventory.Get(slot.ItemId) >= slot.Quantity)
                {
                    state.Inventory.Add(slot.ItemId, -slot.Quantity);
                    ApplyBuffEffects(state, item, slot.Quantity, now);
                    dirty |= DirtySections.Inventory | DirtySections.Buffs;
                    AddEvent(state, $"自动使用 {{{{item:{item.Id}|{item.Name}}}}} × {slot.Quantity}");
                }
            }
        }

        return multiplier;
    }

    private static List<(string ItemId, int Quantity)> ConsumeAutoUseItems(
        PlayerState state,
        ActionType actionType,
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
                || !ItemCatalog.TryGet(slot.ItemId, out var item)
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
        PlayerState state,
        ItemDefinition item,
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
                state.Buffs.Add(new BuffState
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
        PlayerState state,
        ActionDefinition definition,
        ref DirtySections dirty,
        ICollection<string> events,
        ref int combatEvents)
    {
        var encounterId = definition.CombatEncounterId ?? CombatEncounterIds.QiBeast;
        var encounter = CombatCatalog.GetEncounter(encounterId);
        var searchSeconds = Math.Clamp(
            (int)Math.Ceiling(Math.Max(0, encounter.SearchMilliseconds) / 1_000d),
            0,
            definition.DurationSeconds);
        var combatSeconds = Math.Max(0, definition.DurationSeconds - searchSeconds);
        var startedAt = state.LastSimulatedAt
            .AddSeconds(-definition.DurationSeconds)
            .AddSeconds(searchSeconds);
        var result = CombatSimulator.Run(
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
        PlayerState state,
        ActionDefinition definition,
        string encounterId,
        CombatRunResult result,
        DateTimeOffset completedAt,
        bool applyConsumedItems,
        ref DirtySections dirty,
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

        state.Inventory.Add(ItemIds.Spirit, result.Spirit);
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
        dirty |= DirtySections.Inventory
            | DirtySections.Buffs
            | DirtySections.Stats
            | DirtySections.Combat;
    }

    private static void ApplyCombatBuff(
        PlayerState state,
        CombatBuffApplication application,
        DateTimeOffset now)
    {
        var existing = state.Buffs.FirstOrDefault(buff => buff.BuffId == application.BuffId);
        if (existing is null)
        {
            var buff = new BuffState
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
            };
            if (application.Modifiers is { Count: > 0 } mods)
            {
                foreach (var (k, v) in mods)
                {
                    buff.SetModifier(k, v);
                }
            }
            state.Buffs.Add(buff);
            return;
        }

        existing.Stacks = application.Stacks;
        existing.QiGainMultiplier = application.QiGainMultiplier;
        existing.AttackLevelRatio = application.AttackLevelRatio;
        existing.DamageRatio = application.DamageRatio;
        existing.AttackSpeedRatio = application.AttackSpeedRatio;
        existing.SourceItemId = application.SourceItemId;
        existing.ExpiresAt = now.AddMilliseconds(application.ExpiresAfterMilliseconds);
        if (application.Modifiers is { Count: > 0 } updateMods)
        {
            foreach (var (k, v) in updateMods)
            {
                existing.SetModifier(k, v);
            }
        }
    }

    private static bool CanStartAction(
        PlayerState state,
        QueuedAction queuedAction,
        ActionDefinition definition)
    {
        if (definition.ExecutionKind == ActionExecutionKind.Travel)
        {
            return IsValidTravelPlan(state.World, queuedAction.TravelPlan);
        }

        return WorldCatalog.CanPerformAction(state.World, definition)
            && definition.Inputs.All(input => state.Inventory.Get(input.Key) >= input.Value);
    }

    private static bool IsValidTravelPlan(WorldState world, TravelPlan? plan)
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
            _ = WorldCatalog.GetNode(plan.MapId, plan.DestinationNodeId);
            var duration = 0;
            foreach (var segment in plan.Segments)
            {
                _ = WorldCatalog.GetEdge(plan.MapId, segment.EdgeId);
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
        IReadOnlyList<ConsumableSlot>? requested,
        out string error)
    {
        if (requested is null)
        {
            error = string.Empty;
            return true;
        }

        if (requested.Count > CombatLimits.ConsumableSlotCount
            || requested.Any(slot => slot.Quantity is < 1 or > 99))
        {
            error = "消耗品槽数量或使用数量无效。";
            return false;
        }

        if (requested.Any(slot => !string.IsNullOrWhiteSpace(slot.ItemId)
            && !ItemCatalog.CanUseInAction(slot.ItemId, ActionType.Combat)))
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

    private static List<ConsumableSlot> CloneConsumableSlots(
        IReadOnlyList<ConsumableSlot> slots)
    {
        return slots
            .Select(slot => new ConsumableSlot
            {
                ItemId = slot.ItemId,
                Quantity = slot.Quantity,
                Enabled = slot.Enabled && !string.IsNullOrWhiteSpace(slot.ItemId),
                Triggers = CloneTriggers(slot.Triggers)
            })
            .Concat(Enumerable.Range(
                slots.Count,
                CombatLimits.ConsumableSlotCount - slots.Count).Select(_ => new ConsumableSlot()))
            .ToList();
    }

    private static bool ValidateTriggers(IReadOnlyList<CombatTrigger>? triggers)
    {
        if (triggers is null) return true;
        if (triggers.Count > CombatLimits.TriggerCountPerSlot) return false;
        return triggers.All(trigger =>
            Enum.IsDefined(trigger.Dependency)
            && Enum.IsDefined(trigger.Condition)
            && Enum.IsDefined(trigger.Comparator)
            && double.IsFinite(trigger.Value)
            && (trigger.Condition != CombatTriggerCondition.BuffStacks
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
        IReadOnlyList<EquippedItem> source,
        out List<EquippedItem> normalized,
        out string error)
    {
        normalized = [];
        error = string.Empty;
        if (source.Count > CombatLimits.EquipmentSlotCount)
        {
            error = $"装备数量不能超过 {CombatLimits.EquipmentSlotCount} 件。";
            return false;
        }

        var occupiedSlots = EquipmentSlotMask.None;
        foreach (var item in source)
        {
            if (item is null
                || !CombatCatalog.TryGetEquipment(item.ItemId, out var definition)
                || item.EnhancementLevel is < 0 or > CombatLimits.MaxEnhancementLevel)
            {
                error = "装备 ID 或强化等级无效。";
                return false;
            }

            var mask = (EquipmentSlotMask)(1 << (int)definition.Slot);
            if ((occupiedSlots & mask) != 0)
            {
                error = $"装备槽位 {definition.Slot} 不能重复占用。";
                return false;
            }

            occupiedSlots |= mask;
            normalized.Add(item.Clone());
        }

        if ((occupiedSlots & EquipmentSlotMask.TwoHand) != 0 && (occupiedSlots & EquipmentSlotMask.Weapon) != 0)
        {
            error = "双手法器不能与主手法器同时装备。";
            return false;
        }

        return true;
    }

    private static bool NormalizeEquipment(List<EquippedItem> equipment)
    {
        if (TryNormalizeEquipment(equipment, out var normalized, out _)
            && normalized.Count == equipment.Count)
        {
            return false;
        }

        equipment.Clear();
        return true;
    }

    private static bool NormalizeCombatEffectRatios(CombatLoadout combat)
    {
        var changed = false;
        changed |= AssignNormalizedRatio(combat.PenetrationRatio, 1_000, value => combat.PenetrationRatio = value);
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

    private static List<CombatTrigger> CloneTriggers(
        IReadOnlyList<CombatTrigger>? triggers)
    {
        return triggers?.Select(trigger => trigger.Clone()).ToList() ?? [];
    }

    private static bool TriggersEqual(
        IReadOnlyList<CombatTrigger>? left,
        IReadOnlyList<CombatTrigger>? right)
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

    private static bool ExpireBuffs(PlayerState state, DateTimeOffset now)
    {
        var removed = state.Buffs.RemoveAll(buff => buff.ExpiresAt <= now);
        return removed > 0;
    }

    private static double GetQiGainMultiplier(PlayerState state, DateTimeOffset now)
    {
        var buffMultiplier = BuffEvaluator.GetMaxMultiplier(state.Buffs, now, Domain.Constants.StatKeys.QiGain, 1.0);
        return buffMultiplier * WorldCatalog.GetCultivationMultiplier(state.World);
    }

    private static double GetGatheringOutputMultiplier(PlayerState state, DateTimeOffset now)
    {
        return BuffEvaluator.GetMaxMultiplier(state.Buffs, now, Domain.Constants.StatKeys.GatheringOutput, 1.0);
    }

    private static double GetAlchemyOutputMultiplier(PlayerState state, DateTimeOffset now)
    {
        return BuffEvaluator.GetMaxMultiplier(state.Buffs, now, Domain.Constants.StatKeys.AlchemyOutput, 1.0);
    }

    /// <summary>
    /// 获取当前生效中的大世界移动速度倍率（受神行符、疾风丹等 StatKeys.MovementSpeed 增益加成）。
    /// </summary>
    public static double GetTravelSpeedMultiplier(PlayerState state, DateTimeOffset now)
    {
        return BuffEvaluator.GetMaxMultiplier(state.Buffs, now, Domain.Constants.StatKeys.MovementSpeed, 1.0);
    }
    private static long GetProducedAmount(
        PlayerState state,
        ActionDefinition definition,
        string itemId,
        double multiplier)
    {
        var baseAmount = definition.Outputs.GetValueOrDefault(itemId);
        return multiplier == 1
            ? baseAmount
            : Math.Max(1, (long)Math.Ceiling(baseAmount * multiplier));
    }

    private static QueuedAction CreateQueuedAction(string actionId, int repeats = 1)
    {
        var definition = ActionCatalog.Get(actionId);
        return new QueuedAction
        {
            ActionId = actionId,
            TotalRepeats = repeats,
            RemainingRepeats = repeats,
            DurationSeconds = definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                ? 0
                : definition.DurationSeconds,
            CombatExpedition = definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                ? CreateCombatExpedition(definition)
                : null
        };
    }

    private static CombatExpeditionState CreateCombatExpedition(ActionDefinition definition)
    {
        var encounterId = definition.CombatEncounterId ?? CombatEncounterIds.QiBeast;
        var encounter = CombatCatalog.GetEncounter(encounterId);
        return new CombatExpeditionState
        {
            PhaseDurationMilliseconds = Math.Max(1, encounter.SearchMilliseconds)
        };
    }

    private static bool NormalizeLearnedAbilities(CombatLoadout combat)
    {
        var learned = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var abilityId in combat.LearnedAbilityIds)
        {
            if (!string.IsNullOrWhiteSpace(abilityId)
                && CombatCatalog.TryGetAbility(abilityId, out _)
                && learned.Add(abilityId))
            {
                continue;
            }

            changed = true;
        }

        foreach (var slot in combat.AbilitySlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.AbilityId)
                && CombatCatalog.TryGetAbility(slot.AbilityId, out _)
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
        QueuedAction queuedAction,
        ActionDefinition definition)
    {
        return queuedAction.TravelPlan?.TotalDurationSeconds ?? definition.DurationSeconds;
    }

    private static void EnsureTotalRepeats(QueuedAction action)
    {
        if (action.TotalRepeats != 0) return;
        action.TotalRepeats = NormalizeRepeats(action.RemainingRepeats);
    }

    private static int GetConfiguredRepeats(QueuedAction action)
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

    private static bool SetTotalRepeats(QueuedAction action, int repeats)
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

    private static void Touch(PlayerState state, DirtySections sections)
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
        PlayerState state,
        string message,
        DateTimeOffset? occurredAt = null)
    {
        var entry = new LogEvent
        {
            Sequence = checked(++state.EventSequence),
            OccurredAt = occurredAt ?? state.LastSimulatedAt,
            Template = message
        };

        state.StructuredEventLog.Add(entry);
        if (state.PresenceMode == PresenceMode.Online)
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
public sealed record CommandResult(bool Success, string Code, string Message)
{
    /// <summary>构建成功执行结果。</summary>
    public static CommandResult Ok(string message) => new(true, "OK", message);

    /// <summary>构建失败执行结果。</summary>
    public static CommandResult Fail(string code, string message) => new(false, code, message);
}

