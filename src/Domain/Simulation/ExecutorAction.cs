// ==============================================================================
// 文件名称: ExecutorAction.cs
// 主要职责: 动作执行推进的确定性静态执行器，纯函数根据输入上下文计算输出增量。
// 核心逻辑 / 定义内容:
//   1. 接收只读不可变的 ActionExecutionContext，不持有 Entity、World 或外部对象。
//   2. 确定性计算输入材料消耗、产出物品、属性/修为增量及结构化事件。
//   3. 计算推进后的阶段、进度毫秒数与下次过渡截止时间提示（NextTransitionHint）。
// ==============================================================================

namespace Domain.Simulation;

using Domain.Common;
using Domain.Constants;
using Domain.Models;

/// <summary>
/// 动作执行推进的确定性静态执行器。
/// 严格遵循纯函数/无副作用契约：只根据只读 ActionExecutionContext 计算并返回 ActionAdvanceResult。
/// </summary>
public static class ExecutorAction
{
    /// <summary>
    /// 根据只读执行上下文计算动作单步推进的确定性增量结果。
    /// </summary>
    /// <param name="context">只读动作执行上下文。</param>
    /// <returns>推进结果增量契约对象。</returns>
    public static ActionAdvanceResult Advance(ActionExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var definition = context.Action;
        var cursor = context.Cursor;
        var target = context.Target;

        if (target < cursor)
        {
            return ActionAdvanceResult.Unchanged(cursor);
        }

        // 1. 检查世界节点可用性
        if (definition.AvailableNodeIds.Count > 0 &&
            context.CurrentNodeId != null &&
            !definition.AvailableNodeIds.Contains(context.CurrentNodeId))
        {
            return ActionAdvanceResult.Block(cursor, ActionBlockReasons.InvalidLocation);
        }

        // 2. 检查输入材料消耗
        if (definition.Inputs.Count > 0)
        {
            foreach (var (itemId, requiredAmount) in definition.Inputs)
            {
                var held = context.AvailableInventory.GetValueOrDefault(itemId, 0.0);
                if (held < requiredAmount)
                {
                    return ActionAdvanceResult.Block(cursor, ActionBlockReasons.MissingInputs);
                }
            }
        }

        // 3. 计算单次耗时与完成时间戳
        var durationMs = (long)Math.Max(1000, definition.DurationSeconds * 1000.0);
        var remainingMs = Math.Max(1, durationMs - context.ProgressMilliseconds);
        var finishAt = cursor.AddMilliseconds(remainingMs);

        if (finishAt <= target)
        {
            // 本次动作周期已到达完成边界
            var playerKey = context.PlayerId != Guid.Empty ? context.PlayerId.ToString("D") : context.EntityId.ToString();
            var result = new ActionAdvanceResult
            {
                IdempotencyKey = $"{playerKey}:{context.EntryId}:{context.ExecutionRevision}:{finishAt.ToUnixTimeMilliseconds()}",
                ConsumedUntil = finishAt,
                Completed = true,
                RemainingRepeatsDelta = -1,
                NextProgressMilliseconds = 0,
                NextTransitionHint = finishAt
            };

            // 扣减输入物品
            foreach (var (itemId, amount) in definition.Inputs)
            {
                result.Delta.AddItemDelta(itemId, -amount);
            }

            // 产出输出物品
            foreach (var (itemId, amount) in definition.Outputs)
            {
                result.Delta.AddItemDelta(itemId, amount);
            }

            // 根据类型增加属性与收益
            switch (definition.Type)
            {
                case ActionType.Cultivation:
                    result.Delta.Stats.QiDelta += 10.0;
                    result.Delta.Stats.CultivationCyclesDelta += 1;
                    break;
                case ActionType.Exploration:
                    result.Delta.Stats.HerbsCollectedDelta += 1;
                    break;
                case ActionType.Alchemy:
                    result.Delta.Stats.PillsCraftedDelta += 1;
                    break;
            }

            result.Delta.Stats.CompletedActionsDelta += 1;

            result.Events.Add(new ActionEventDelta
            {
                IdempotencyKey = result.IdempotencyKey,
                OccurredAt = finishAt,
                Template = $"完成行动：{definition.Name}",
                ActionType = definition.Type,
                Category = definition.Type.ToString().ToLowerInvariant()
            });

            return result;
        }
        else
        {
            // 目标时间不足以完成，推进部分进度毫秒数
            var elapsedMs = (long)(target - cursor).TotalMilliseconds;
            var nextProgress = Math.Min(durationMs, context.ProgressMilliseconds + elapsedMs);
            return new ActionAdvanceResult
            {
                ConsumedUntil = target,
                Completed = false,
                NextProgressMilliseconds = nextProgress,
                NextTransitionHint = finishAt
            };
        }
    }
}
