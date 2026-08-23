// ==============================================================================
// 文件名称: ActionTransitionSystem.cs
// 主要职责: 玩家动作队列唯一过渡与生命周期流转 ECS 系统。
// 核心逻辑 / 定义内容:
//   1. 继承 YuoSystem<ActionQueueComponent> 并实现 IActionTransitionDue 标签。
//   2. 作为唯一的状态写入协调者：读取各薄组件只读数据，调用纯执行契约，原子应用 ActionAdvanceResult。
//   3. 唯一管理队列条目出队、循环克隆、阶段流转与重置，严禁组件自身出队或结算。
//   4. 零 PlayerState 双写、零兼容 facade、零第二套推进器。
// ==============================================================================

namespace Domain.Systems;

using System.Collections.Frozen;
using Domain.Common;
using Domain.Components;
using Domain.Constants;
using Domain.Models;
using Domain.Simulation;
using YuoCore.Ecs;

/// <summary>
/// 玩家动作队列唯一过渡与生命周期流转 ECS 系统。
/// 严格遵循架构规约：
/// 1. 组件只保存数据和只读派生属性；
/// 2. 队列推进、出队、扣减重复次数与循环克隆由本系统唯一处理；
/// 3. 执行器与纯计算只产生 ActionAdvanceResult，由本系统原子应用到各个薄组件。
/// </summary>
public sealed class ActionTransitionSystem : YuoSystem<ActionQueueComponent>, IActionTransitionDue
{
    /// <summary>
    /// 当世界调度标签 IActionTransitionDue 触发时，对实体的当前动作执行一次过渡结算。
    /// </summary>
    /// <param name="queue">实体的动作队列组件。</param>
    public override void Run(ActionQueueComponent queue)
    {
        var entity = queue.Entity;
        if (entity.IsDisposed || queue.IsEmpty)
        {
            return;
        }

        if (!entity.TryGetComponent<ActionTransitionTargetComponent>(out var targetComponent))
        {
            throw new InvalidOperationException(
                "通过 IActionTransitionDue 触发 ActionTransitionSystem 时必须显式注入 ActionTransitionTargetComponent 目标时间戳，禁止在缺失目标时隐式推进。");
        }

        var targetTime = targetComponent.TargetTime;
        entity.RemoveComponent<ActionTransitionTargetComponent>();

        AdvanceCurrentAction(entity, targetTime);
    }

    /// <summary>
    /// 将纯推进计算生成的 ActionAdvanceResult 原子提交到实体上的各个薄组件。
    /// </summary>
    /// <param name="entity">目标玩家实体。</param>
    /// <param name="result">执行器计算生成的推进结果增量。</param>
    /// <returns>已应用的结果对象。</returns>
    public static ActionAdvanceResult ApplyAdvanceResult(YuoEntity entity, ActionAdvanceResult result)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(result);

        entity.TryGetComponent<SyncDirtyComponent>(out var dirty);

        // 1. 原子更新背包物品
        if (result.Delta.ItemDeltas.Count > 0 && entity.TryGetComponent<InventoryComponent>(out var inventory))
        {
            foreach (var (itemId, amount) in result.Delta.ItemDeltas)
            {
                var nextAmount = inventory.Get(itemId) + amount;
                if (nextAmount <= 0)
                {
                    inventory.Items.Remove(itemId);
                }
                else
                {
                    inventory.Items[itemId] = nextAmount;
                }
            }

            dirty?.MarkDirty(DirtySections.Inventory);
        }

        // 2. 原子更新玩家属性、修为与统计数据
        if (result.Delta.Stats.HasChanges)
        {
            if (entity.TryGetComponent<PlayerStatsComponent>(out var stats))
            {
                stats.Spirit += result.Delta.Stats.SpiritDelta;
                stats.HerbsCollected += result.Delta.Stats.HerbsCollectedDelta;
                stats.PillsCrafted += result.Delta.Stats.PillsCraftedDelta;
                stats.CultivationCycles += result.Delta.Stats.CultivationCyclesDelta;
                stats.CompletedActions += result.Delta.Stats.CompletedActionsDelta;
                stats.BlockedActions += result.Delta.Stats.BlockedActionsDelta;
                stats.CombatWins += result.Delta.Stats.CombatWinsDelta;
                stats.CombatLosses += result.Delta.Stats.CombatLossesDelta;
                stats.CombatExperience += result.Delta.Stats.CombatExperienceDelta;
                stats.CombatDamageDealt += result.Delta.Stats.CombatDamageDealtDelta;
                stats.CombatDamageTaken += result.Delta.Stats.CombatDamageTakenDelta;
                stats.CombatEnemiesDefeated += result.Delta.Stats.CombatEnemiesDefeatedDelta;
                stats.CombatPlayersDefeated += result.Delta.Stats.CombatPlayersDefeatedDelta;
                dirty?.MarkDirty(DirtySections.Stats);
            }

            if (entity.TryGetComponent<CultivationComponent>(out var cult))
            {
                cult.Qi += result.Delta.Stats.QiDelta;
                cult.FoundationExp += result.Delta.Stats.FoundationExpDelta;
                cult.CultivationCycles += (long)result.Delta.Stats.CultivationCyclesDelta;
                dirty?.MarkDirty(DirtySections.Stats);
            }
        }

        // 3. 原子更新战斗资源（生命、法力、冷却）
        if (result.Delta.CombatResources.HasChanges && entity.TryGetComponent<CombatResourcesComponent>(out var combatResources))
        {
            combatResources.Hitpoints += result.Delta.CombatResources.HitpointsDelta;
            combatResources.Manapoints += result.Delta.CombatResources.ManapointsDelta;
            if (result.Delta.CombatResources.HitpointRemainder.HasValue)
            {
                combatResources.HitpointRemainder = result.Delta.CombatResources.HitpointRemainder.Value;
            }

            if (result.Delta.CombatResources.ManapointRemainder.HasValue)
            {
                combatResources.ManapointRemainder = result.Delta.CombatResources.ManapointRemainder.Value;
            }

            if (result.Delta.CombatResources.AbilityCooldownUpdates is { Count: > 0 } abilityUpdates)
            {
                foreach (var (abilityId, expireAt) in abilityUpdates)
                {
                    combatResources.AbilityCooldowns[abilityId] = expireAt;
                }
            }

            if (result.Delta.CombatResources.ConsumableCooldownUpdates is { Count: > 0 } slotUpdates)
            {
                foreach (var (slotIndex, expireAt) in slotUpdates)
                {
                    combatResources.ConsumableSlotCooldowns[slotIndex] = expireAt;
                }
            }

            dirty?.MarkDirty(DirtySections.Combat);
        }

        // 4. 原子更新 Buff 列表
        if ((result.Delta.AddedBuffs is { Count: > 0 } || result.Delta.RemovedBuffIds is { Count: > 0 }) &&
            entity.TryGetComponent<CultivationComponent>(out var cultivation))
        {
            if (result.Delta.RemovedBuffIds is { Count: > 0 } removedIds)
            {
                cultivation.Buffs.RemoveAll(buff => removedIds.Contains(buff.BuffId));
            }

            if (result.Delta.AddedBuffs is { Count: > 0 } addedBuffs)
            {
                foreach (var newBuff in addedBuffs)
                {
                    var existing = cultivation.Buffs.FirstOrDefault(b => b.BuffId == newBuff.BuffId);
                    if (existing != null)
                    {
                        existing.Stacks = newBuff.Stacks;
                        existing.ExpiresAt = newBuff.ExpiresAt;
                    }
                    else
                    {
                        cultivation.Buffs.Add(newBuff);
                    }
                }
            }

            dirty?.MarkDirty(DirtySections.Buffs);
        }

        // 5. 原子记录结构化事件
        if (result.Events.Count > 0 && dirty != null)
        {
            foreach (var evt in result.Events)
            {
                var sequence = ++dirty.EventSequence;
                var logEvent = new LogEvent
                {
                    Sequence = sequence,
                    OccurredAt = evt.OccurredAt,
                    Template = evt.Template
                };
                dirty.PendingEvents.Add(logEvent);
                dirty.StructuredEventLog.Add(logEvent);
            }

            dirty.MarkDirty(DirtySections.Events);
        }

        // 6. 原子更新世界大地图移动增量
        if (result.Delta.World != null)
        {
            if (entity.TryGetComponent<ActionQueueComponent>(out var queueComp) && queueComp.Current?.Travel is { } travel)
            {
                if (result.Delta.World.TargetMapId != null)
                {
                    travel.MapId = result.Delta.World.TargetMapId;
                }

                if (result.Delta.World.TargetNodeId != null)
                {
                    travel.DestinationNodeId = result.Delta.World.TargetNodeId;
                }
            }

            dirty?.MarkDirty(DirtySections.World);
        }

        // 7. 唯一管理动作队列状态机与生命周期
        if (entity.TryGetComponent<ActionQueueComponent>(out var queue) && queue.Current is { } current)
        {
            current.ExecutionRevision++;

            if (result.Blocked)
            {
                current.Status = ActionStatus.Blocked;
                current.BlockReasonCode = result.BlockReasonCode;
                dirty?.MarkDirty(DirtySections.Queue);
            }
            else if (result.Completed)
            {
                if (!current.IsInfinite)
                {
                    current.RemainingRepeats += result.RemainingRepeatsDelta;
                }

                current.ProgressMilliseconds = 0;

                if (!current.IsInfinite && current.RemainingRepeats <= 0)
                {
                    // 队列条目全部完成：出队
                    queue.Entries.RemoveAt(0);
                    if (queue.LoopEnabled)
                    {
                        queue.Entries.Add(current.CloneForLoop());
                    }

                    if (queue.Current != null)
                    {
                        queue.Current.Status = ActionStatus.Running;
                    }
                }
                else
                {
                    // 本次完成但仍有剩余次数
                    current.Status = ActionStatus.Running;
                }

                queue.Revision++;
                dirty?.MarkDirty(DirtySections.Queue);
            }
            else
            {
                // 推进中（未完成）
                current.Status = ActionStatus.Running;
                current.ProgressMilliseconds = result.NextProgressMilliseconds;
                if (result.NextPhase.HasValue && current.Combat != null)
                {
                    current.Combat.Phase = result.NextPhase.Value;
                }

                dirty?.MarkDirty(DirtySections.Queue);
            }
        }

        // 8. 更新模拟时间与版本号
        if (entity.TryGetComponent<CultivationComponent>(out var cultComp))
        {
            if (result.ConsumedUntil > cultComp.LastSimulatedAt)
            {
                cultComp.LastSimulatedAt = result.ConsumedUntil;
            }
        }

        if (dirty != null)
        {
            dirty.Version++;
        }

        return result;
    }

    /// <summary>
    /// 对实体当前的动作执行单步推进计算，并原子提交结果。
    /// </summary>
    /// <param name="entity">目标玩家实体。</param>
    /// <param name="targetTime">推进目标时间戳。</param>
    /// <returns>推进结算结果。</returns>
    public static ActionAdvanceResult AdvanceCurrentAction(YuoEntity entity, DateTimeOffset targetTime)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TryGetComponent<ActionQueueComponent>(out var queue) || queue.IsEmpty)
        {
            return ActionAdvanceResult.Unchanged(targetTime);
        }

        var current = queue.Current!;
        entity.TryGetComponent<CultivationComponent>(out var cultivation);
        entity.TryGetComponent<InventoryComponent>(out var inventory);

        var cursor = cultivation?.LastSimulatedAt ?? targetTime;
        if (targetTime < cursor)
        {
            return ActionAdvanceResult.Unchanged(cursor);
        }

        // 检索动作配置原型
        if (!ActionCatalog.TryGet(current.ActionId, out var definition))
        {
            var invalidDefResult = ActionAdvanceResult.Block(cursor, ActionBlockReasons.InvalidDefinition);
            return ApplyAdvanceResult(entity, invalidDefResult);
        }

        var context = new ActionExecutionContext
        {
            EntityId = entity.ID,
            EntryId = current.EntryId,
            ExecutionRevision = current.ExecutionRevision,
            Action = definition,
            Cursor = cursor,
            Target = targetTime,
            ProgressMilliseconds = current.ProgressMilliseconds,
            AvailableInventory = inventory?.ToFrozenSnapshot() ?? FrozenDictionary<string, double>.Empty,
            CurrentMapId = current.Travel?.MapId,
            CurrentNodeId = current.Travel?.DestinationNodeId
        };

        var advanceResult = ExecutorAction.Advance(context);
        return ApplyAdvanceResult(entity, advanceResult);
    }

    /// <summary>
    /// 对实体执行多步离散事件推进直到到达 targetTime 或达到步数预算上限。
    /// 确保时间单调递增，cap 上限时保留已消费游标并返回可续算状态。
    /// </summary>
    /// <param name="entity">目标玩家实体。</param>
    /// <param name="targetTime">推进目标时间戳。</param>
    /// <param name="maxTransitions">单次推进允许的最大转换步数。</param>
    /// <returns>综合模拟结果。</returns>
    public static SimulationResult AdvanceEntityTo(
        YuoEntity entity,
        DateTimeOffset targetTime,
        int maxTransitions = SimulationConstants.MaxTransitionsPerAdvance,
        TimeSpan? maxOfflineDuration = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TryGetComponent<ActionQueueComponent>(out var queue))
        {
            return new SimulationResult(DirtySections.None, 0, 0, []);
        }

        entity.TryGetComponent<CultivationComponent>(out var cultivation);
        var cursor = cultivation?.LastSimulatedAt ?? targetTime;
        if (targetTime < cursor)
        {
            return new SimulationResult(DirtySections.None, 0, 0, []);
        }

        if (cultivation?.PresenceMode == PresenceMode.FullyOffline)
        {
            cultivation.LastSimulatedAt = targetTime;
            return new SimulationResult(DirtySections.None, 0, 0, []);
        }

        // 施加最大离线时间跨度截断，防止无限长时间补算
        var effectiveMaxOffline = maxOfflineDuration ?? SimulationConstants.DefaultMaxOfflineDuration;
        var rawDuration = targetTime - cursor;
        var duration = TimeSpan.FromMilliseconds(Math.Min(rawDuration.TotalMilliseconds, effectiveMaxOffline.TotalMilliseconds));
        targetTime = cursor + duration;

        var completedActions = 0;
        var transitions = 0;
        var events = new List<string>();
        var blockedEntries = new HashSet<Guid>();

        while (cursor <= targetTime && transitions < maxTransitions)
        {
            if (queue.IsEmpty)
            {
                if (cultivation != null)
                {
                    cultivation.LastSimulatedAt = targetTime;
                }

                break;
            }

            var current = queue.Current!;
            var stepResult = AdvanceCurrentAction(entity, targetTime);
            transitions++;

            if (stepResult.Blocked)
            {
                if (!blockedEntries.Add(current.EntryId))
                {
                    // 队列中所有任务均受阻：停止循环并将时间推至 targetTime
                    if (cultivation != null)
                    {
                        cultivation.LastSimulatedAt = targetTime;
                    }

                    break;
                }

                if (queue.LoopEnabled && queue.Count > 1)
                {
                    // 轮换至下一个任务
                    queue.Entries.RemoveAt(0);
                    queue.Entries.Add(current);
                }
                else
                {
                    // 未开启循环或单项受阻：停泊
                    if (cultivation != null)
                    {
                        cultivation.LastSimulatedAt = targetTime;
                    }

                    break;
                }
            }
            else
            {
                blockedEntries.Clear();
                if (stepResult.Completed)
                {
                    completedActions++;
                    events.Add($"completed:{current.ActionId}");
                }
            }

            cursor = cultivation?.LastSimulatedAt ?? stepResult.ConsumedUntil;

            if (stepResult.ConsumedUntil >= targetTime)
            {
                break;
            }
        }

        var capped = transitions >= maxTransitions && cursor < targetTime;
        var reachedTarget = !capped;

        entity.TryGetComponent<SyncDirtyComponent>(out var dirty);
        var dirtySections = dirty?.DirtySections ?? DirtySections.None;

        return new SimulationResult(
            dirtySections,
            completedActions,
            transitions,
            events,
            0,
            reachedTarget,
            capped);
    }
}
