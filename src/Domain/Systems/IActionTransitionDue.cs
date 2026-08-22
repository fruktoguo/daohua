// ==============================================================================
// 文件名称: IActionTransitionDue.cs
// 主要职责: 动作到期流转系统标签接口。
// 核心逻辑 / 定义内容:
//   1. 继承 YuoCore.Ecs.ISystemTag。
//   2. 调度器唤醒或到期触发时，统一调用 YuoWorld.RunSystem<IActionTransitionDue>(entity)。
// ==============================================================================

namespace Domain.Systems;

using YuoCore.Ecs;

/// <summary>
/// 动作到期流转事件标签。
/// 当调度器唤醒、命令显式触发或时间推进到达边界时，驱动挂载了此系统的实体执行队列过渡。
/// </summary>
public interface IActionTransitionDue : ISystemTag
{
}
