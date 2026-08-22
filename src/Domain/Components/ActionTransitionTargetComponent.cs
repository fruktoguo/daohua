// ==============================================================================
// 文件名称: ActionTransitionTargetComponent.cs
// 主要职责: 玩家动作队列单次推进目标时间戳瞬态输入组件。
// 核心逻辑 / 定义内容:
//   1. 纯数据保存 Server / 调度宿主显式注入的推进目标时间戳（TargetTime）。
//   2. 瞬态非持久化：由 ActionTransitionSystem 消费后立即移除，严禁持久化。
//   3. 消除隐式时间推进与 DateTimeOffset.UtcNow 依赖，确保时间注入确定性。
// ==============================================================================

namespace Domain.Components;

using YuoCore.Ecs;

/// <summary>
/// 玩家动作队列单次推进目标时间戳瞬态输入组件。
/// 由调度宿主在触发 IActionTransitionDue 调度前显式挂载，由 ActionTransitionSystem 消费后立即移除。
/// </summary>
public sealed class ActionTransitionTargetComponent : YuoComponent
{
    /// <summary>本次推进的目标时间戳（确定性时间注入）。</summary>
    public DateTimeOffset TargetTime { get; set; }
}
