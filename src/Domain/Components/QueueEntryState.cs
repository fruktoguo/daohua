// ==============================================================================
// 文件名称: QueueEntryState.cs
// 主要职责: 玩家动作队列项运行态数据模型，用于 ECS 组件及快照。
// 核心逻辑 / 定义内容:
//   1. 纯数据承载动作实例 ID、原型关联、执行类型、状态、重复次数及进度毫秒数。
//   2. 包含旅行/战斗专属运行态。
//   3. 零业务逻辑，不自发出队或结算。
// ==============================================================================

namespace Domain.Components;

using System.Text.Json.Serialization;
using Domain.Common;
using Domain.Models;

/// <summary>
/// 队列实例运行态数据模型。
/// </summary>
public sealed class QueueEntryState
{
    /// <summary>队列项唯一实例 GUID，生命周期内不可复用。</summary>
    public Guid EntryId { get; set; } = Guid.NewGuid();

    /// <summary>指向只读动作原型 ID。</summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>推进执行模式（计时模式/战斗遭遇/大地图旅行）。</summary>
    public ActionExecutionKind Kind { get; set; } = ActionExecutionKind.Timed;

    /// <summary>当前队列项状态（排队/执行中/受阻）。</summary>
    public ActionStatus Status { get; set; } = ActionStatus.Queued;

    /// <summary>初始配置的总重复执行次数。</summary>
    public int TotalRepeats { get; set; }

    /// <summary>当前剩余执行次数（小于 0 表示无限循环）。</summary>
    public int RemainingRepeats { get; set; } = 1;

    /// <summary>每次运行或阶段过渡递增的执行修订版本号。</summary>
    public long ExecutionRevision { get; set; }

    /// <summary>当前动作累计推进的毫秒数。</summary>
    public long ProgressMilliseconds { get; set; }

    /// <summary>受阻稳定错误码（参考 ActionBlockReasons）。</summary>
    public string? BlockReasonCode { get; set; }

    /// <summary>旅行移动方案与路径（若为位移动作）。</summary>
    public TravelPlan? Travel { get; set; }

    /// <summary>历练/战斗运行态（若为战斗动作）。</summary>
    public CombatExpeditionState? Combat { get; set; }

    /// <summary>是否为无限次重复执行动作。</summary>
    [JsonIgnore]
    public bool IsInfinite => RemainingRepeats < 0;

    /// <summary>
    /// 为下一次队列循环创建重置了 EntryId、进度与状态的副本。
    /// 由过渡系统在结算旋转队列时调用，组件本身不自动出队或旋转。
    /// </summary>
    public QueueEntryState CloneForLoop()
    {
        var repeats = TotalRepeats > 0 ? TotalRepeats : 1;
        return new QueueEntryState
        {
            EntryId = Guid.NewGuid(),
            ActionId = ActionId,
            Kind = Kind,
            Status = ActionStatus.Queued,
            TotalRepeats = repeats,
            RemainingRepeats = repeats,
            ExecutionRevision = 0,
            ProgressMilliseconds = 0,
            BlockReasonCode = null,
            Travel = Travel?.Clone(),
            Combat = null
        };
    }
}
