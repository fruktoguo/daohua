// ==============================================================================
// 文件名称: SyncDirtyComponent.cs
// 主要职责: 状态同步与持久化脏标记追踪组件。
// 核心逻辑 / 定义内容:
//   1. 追踪实体状态变动（DirtySections）、持久化版本号（Version）与事件序号（EventSequence）。
//   2. 暂存未同步的结构化事件列表与历史事件日志。
//   3. 提供只读与标记方法（MarkDirty, ClearDirty, IsDirty）。
// ==============================================================================

namespace Domain.Components;

using Domain.Models;
using YuoCore.Ecs;

/// <summary>
/// 状态同步与持久化脏标记追踪组件。
/// </summary>
public sealed class SyncDirtyComponent : YuoComponent
{
    /// <summary>当前累积的脏标记节掩码。</summary>
    public DirtySections DirtySections { get; set; } = DirtySections.None;

    /// <summary>玩家状态快照单调递增版本号。</summary>
    public long Version { get; set; }

    /// <summary>结构化事件单调递增序列号计数器。</summary>
    public long EventSequence { get; set; }

    /// <summary>本轮模拟产生且尚未同步给网络或持久化的暂存日志列表。</summary>
    public List<LogEvent> PendingEvents { get; init; } = [];

    /// <summary>已持久化的历史结构化事件日志列表。</summary>
    public List<LogEvent> StructuredEventLog { get; init; } = [];

    /// <summary>标记指定节为脏。</summary>
    public void MarkDirty(DirtySections section) => DirtySections |= section;

    /// <summary>清除所有脏标记。</summary>
    public void ClearDirty() => DirtySections = DirtySections.None;

    /// <summary>检查指定节是否已脏。</summary>
    public bool IsDirty(DirtySections section) => (DirtySections & section) != 0;
}
