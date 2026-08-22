// ==============================================================================
// 文件名称: ActionAdvanceContracts.cs
// 主要职责: 定义确定性执行器（Executor*）推进计算的输入上下文与输出结果增量纯领域合约。
// 核心逻辑 / 定义内容:
//   1. 阻塞原因稳定代码常量定义（ActionBlockReasons）。
//   2. 幂等结构化事件增量与玩家属性/战斗资源增量（ActionEventDelta, ActionPlayerStatDelta, ActionCombatResourceDelta, ActionWorldDelta）。
//   3. 动作推进原子变更增量（ActionDelta）与推进结果契约（ActionAdvanceResult）。
//   4. 确定性执行器只读输入上下文契约（ActionExecutionContext）。
// ==============================================================================

using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Domain.Common;
using Domain.Constants;
using Domain.Definitions;
using Domain.Models;
using ActionDefinition = Domain.Definitions.ActionDefinition;

namespace Domain.Simulation;

/// <summary>
/// 动作推进受阻的标准原因错误码常量。
/// </summary>
public static class ActionBlockReasons
{
    /// <summary>缺少所需输入材料或前置消耗品。</summary>
    public const string MissingInputs = "MissingInputs";

    /// <summary>当前所处世界地图节点不允许执行该动作。</summary>
    public const string InvalidLocation = "InvalidLocation";

    /// <summary>动作配置原型不存在或配置参数无效。</summary>
    public const string InvalidDefinition = "InvalidDefinition";

    /// <summary>玩家状态不可用（如正在突破、死亡等）。</summary>
    public const string PlayerStateUnavailable = "PlayerStateUnavailable";

    /// <summary>动作被主动取消或打断。</summary>
    public const string ActionCancelled = "ActionCancelled";

    /// <summary>前置条件不满足。</summary>
    public const string ConditionNotMet = "ConditionNotMet";
}

/// <summary>
/// 结构化事件增量，携带稳定幂等键，供状态机原子提交到日志与网络事件流中。
/// </summary>
public sealed class ActionEventDelta
{
    /// <summary>事件全局稳定幂等键，防止重复生成或重复投递。</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>事件发生的时间戳。</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>事件文本模板或内容。</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>关联的动作分类。</summary>
    public ActionType ActionType { get; set; }

    /// <summary>事件分类或来源标签（如 craft, loot, combat, blocked 等）。</summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// 玩家属性、修为与统计数据的原子增量。
/// 数值计算统一采用 double 精度。
/// </summary>
public sealed class ActionPlayerStatDelta
{
    /// <summary>灵气修为增量。</summary>
    public double QiDelta { get; set; }

    /// <summary>灵石增量。</summary>
    public double SpiritDelta { get; set; }

    /// <summary>筑基/功法修炼经验增量。</summary>
    public double FoundationExpDelta { get; set; }

    /// <summary>采集灵草次数增量。</summary>
    public double HerbsCollectedDelta { get; set; }

    /// <summary>炼制丹药次数增量。</summary>
    public double PillsCraftedDelta { get; set; }

    /// <summary>打坐修炼大周天次数增量。</summary>
    public double CultivationCyclesDelta { get; set; }

    /// <summary>累计完成动作总次数增量。</summary>
    public double CompletedActionsDelta { get; set; }

    /// <summary>累计受阻动作次数增量。</summary>
    public double BlockedActionsDelta { get; set; }

    /// <summary>战斗胜利总场次增量。</summary>
    public double CombatWinsDelta { get; set; }

    /// <summary>战斗失败总场次增量。</summary>
    public double CombatLossesDelta { get; set; }

    /// <summary>累计战斗经验值增量。</summary>
    public double CombatExperienceDelta { get; set; }

    /// <summary>战斗造成伤害增量。</summary>
    public double CombatDamageDealtDelta { get; set; }

    /// <summary>战斗承受伤害增量。</summary>
    public double CombatDamageTakenDelta { get; set; }

    /// <summary>击败敌怪总数增量。</summary>
    public double CombatEnemiesDefeatedDelta { get; set; }

    /// <summary>切磋击败玩家总数增量。</summary>
    public double CombatPlayersDefeatedDelta { get; set; }

    /// <summary>检查是否有任何非零增量。</summary>
    [JsonIgnore]
    public bool HasChanges =>
        QiDelta != 0 || SpiritDelta != 0 || FoundationExpDelta != 0 ||
        HerbsCollectedDelta != 0 || PillsCraftedDelta != 0 || CultivationCyclesDelta != 0 ||
        CompletedActionsDelta != 0 || BlockedActionsDelta != 0 ||
        CombatWinsDelta != 0 || CombatLossesDelta != 0 || CombatExperienceDelta != 0 ||
        CombatDamageDealtDelta != 0 || CombatDamageTakenDelta != 0 ||
        CombatEnemiesDefeatedDelta != 0 || CombatPlayersDefeatedDelta != 0;
}

/// <summary>
/// 战斗资源（生命、法力、冷却）的原子增量与变更。
/// 数值计算统一采用 double 精度。
/// </summary>
public sealed class ActionCombatResourceDelta
{
    /// <summary>生命值绝对增量（正数回血，负数扣血）。</summary>
    public double HitpointsDelta { get; set; }

    /// <summary>法力值绝对增量（正数回蓝，负数扣蓝）。</summary>
    public double ManapointsDelta { get; set; }

    /// <summary>生命回复小数累加余量（若有更新）。</summary>
    public double? HitpointRemainder { get; set; }

    /// <summary>法力回复小数累加余量（若有更新）。</summary>
    public double? ManapointRemainder { get; set; }

    /// <summary>技能绝对冷却到期时间更新字典（技能 ID -> 到期时间戳）。</summary>
    public Dictionary<string, DateTimeOffset>? AbilityCooldownUpdates { get; set; }

    /// <summary>消耗品快捷槽位绝对冷却到期时间更新字典（槽位索引 -> 到期时间戳）。</summary>
    public Dictionary<int, DateTimeOffset>? ConsumableCooldownUpdates { get; set; }

    /// <summary>检查是否有任何资源变动。</summary>
    [JsonIgnore]
    public bool HasChanges =>
        HitpointsDelta != 0 || ManapointsDelta != 0 ||
        HitpointRemainder.HasValue || ManapointRemainder.HasValue ||
        (AbilityCooldownUpdates is { Count: > 0 }) ||
        (ConsumableCooldownUpdates is { Count: > 0 });
}

/// <summary>
/// 世界大地图位置与移动的原子增量。
/// </summary>
public sealed class ActionWorldDelta
{
    /// <summary>世界地图 ID 变更（若跨地图移动）。</summary>
    public string? TargetMapId { get; set; }

    /// <summary>停驻地图节点 ID 变更。</summary>
    public string? TargetNodeId { get; set; }

    /// <summary>当前途经边 ID 变更。</summary>
    public string? EdgeId { get; set; }

    /// <summary>当前边移动进度比例（0.0 ~ 1.0）。</summary>
    public double? EdgeProgress { get; set; }
}

/// <summary>
/// 动作执行推进生成的综合状态增量载荷。
/// 在所属单 Fiber 内由 ActionTransitionSystem 原子提交，不持有任何 Entity 或外部引用。
/// </summary>
public sealed class ActionDelta
{
    /// <summary>物品增量字典（物品 ID -> 数量增量，正数表示产出/获得，负数表示消耗/扣减）。</summary>
    public Dictionary<string, double> ItemDeltas { get; set; } = new(StringComparer.Ordinal);

    /// <summary>玩家属性与统计数据增量。</summary>
    public ActionPlayerStatDelta Stats { get; set; } = new();

    /// <summary>战斗资源（生命、法力与冷却）增量。</summary>
    public ActionCombatResourceDelta CombatResources { get; set; } = new();

    /// <summary>世界地图移动与位置变更（若有）。</summary>
    public ActionWorldDelta? World { get; set; }

    /// <summary>新施加或叠层的 Buff 列表（若有）。</summary>
    public List<BuffState>? AddedBuffs { get; set; }

    /// <summary>到期或被驱散的 Buff ID 列表（若有）。</summary>
    public List<string>? RemovedBuffIds { get; set; }

    /// <summary>
    /// 累加物品增量（数量变动为 0 时自动移除）。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <param name="amount">变动数量（正数增加，负数减少）。</param>
    public void AddItemDelta(string itemId, double amount)
    {
        if (amount == 0) return;
        if (ItemDeltas.TryGetValue(itemId, out var current))
        {
            var next = current + amount;
            if (next == 0)
            {
                ItemDeltas.Remove(itemId);
            }
            else
            {
                ItemDeltas[itemId] = next;
            }
        }
        else
        {
            ItemDeltas[itemId] = amount;
        }
    }
}

/// <summary>
/// 单次推进计算的核心结果契约。
/// 确定性执行器（Executor*）根据 ActionExecutionContext 计算并返回本结果，
/// 随后由唯一的 ActionTransitionSystem 进行原子应用。
/// </summary>
public sealed class ActionAdvanceResult
{
    /// <summary>全局幂等键，标识本次推进结果的唯一逻辑版本。</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>本次推进实际消费到的时间边界戳（单调递增）。</summary>
    public DateTimeOffset ConsumedUntil { get; set; }

    /// <summary>是否完成了一次重复周期或整个队列条目。</summary>
    public bool Completed { get; set; }

    /// <summary>本批次扣减的剩余重复次数（例如 -1，0 表示未扣减）。</summary>
    public int RemainingRepeatsDelta { get; set; }

    /// <summary>是否因条件不足进入阻塞状态。</summary>
    public bool Blocked { get; set; }

    /// <summary>阻塞原因稳定代码（参考 ActionBlockReasons）。</summary>
    public string? BlockReasonCode { get; set; }

    /// <summary>推进后的下一阶段枚举（针对战斗历练或分阶段任务）。</summary>
    public CombatExpeditionPhase? NextPhase { get; set; }

    /// <summary>推进后当前阶段或单次动作累计推进的毫秒数。</summary>
    public long NextProgressMilliseconds { get; set; }

    /// <summary>需要原子提交的物品、属性、资源与世界状态增量。</summary>
    public ActionDelta Delta { get; set; } = new();

    /// <summary>本次推进产生且带有稳定幂等键的结构化事件日志列表。</summary>
    public List<ActionEventDelta> Events { get; set; } = [];

    /// <summary>供调度器唤醒使用的下一次截止时间提示（为 null 表示无活跃调度或处于阻塞/排队态）。</summary>
    public DateTimeOffset? NextTransitionHint { get; set; }

    /// <summary>
    /// 便捷工厂方法：创建一个无操作（如游标未动或无任何变更）推进结果。
    /// </summary>
    public static ActionAdvanceResult Unchanged(DateTimeOffset cursor) => new()
    {
        ConsumedUntil = cursor,
        Completed = false,
        Blocked = false
    };

    /// <summary>
    /// 便捷工厂方法：创建一个受阻推进结果。
    /// </summary>
    public static ActionAdvanceResult Block(
        DateTimeOffset cursor,
        string reasonCode,
        string? idempotencyKey = null) => new()
    {
        IdempotencyKey = idempotencyKey ?? string.Empty,
        ConsumedUntil = cursor,
        Completed = false,
        Blocked = true,
        BlockReasonCode = reasonCode
    };
}

/// <summary>
/// 确定性执行器（Executor*）的输入上下文契约。
/// 只包含只读纯数据快照，不持有任何 Entity/World/数据库/网络对象。
/// </summary>
public sealed class ActionExecutionContext
{
    /// <summary>玩家唯一 GUID。</summary>
    public Guid PlayerId { get; init; }

    /// <summary>队列项唯一 EntryId。</summary>
    public Guid EntryId { get; init; }

    /// <summary>执行修订版本号（ExecutionRevision）。</summary>
    public long ExecutionRevision { get; init; }

    /// <summary>当前动作静态原型定义。</summary>
    public required ActionDefinition Action { get; init; }

    /// <summary>当前模拟游标时间戳。</summary>
    public DateTimeOffset Cursor { get; init; }

    /// <summary>本次推进目标时间戳。</summary>
    public DateTimeOffset Target { get; init; }

    /// <summary>当前动作已累计推进的毫秒数。</summary>
    public long ProgressMilliseconds { get; init; }

    /// <summary>当前阶段（若为分阶段任务）。</summary>
    public CombatExpeditionPhase CurrentPhase { get; init; } = CombatExpeditionPhase.InitialSearch;

    /// <summary>本次模拟允许的流转转换步数预算。</summary>
    public int TransitionBudget { get; init; } = SimulationConstants.MaxTransitionsPerAdvance;

    /// <summary>可用物品及持有量只读快照。</summary>
    public FrozenDictionary<string, double> AvailableInventory { get; init; } =
        FrozenDictionary<string, double>.Empty;

    /// <summary>当前所处的世界地图 ID（若有）。</summary>
    public string? CurrentMapId { get; init; }

    /// <summary>当前停驻的世界地图节点 ID（若有，供校验可执行区域）。</summary>
    public string? CurrentNodeId { get; init; }
}
