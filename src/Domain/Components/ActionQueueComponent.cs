// ==============================================================================
// 文件名称: ActionQueueComponent.cs
// 主要职责: 玩家动作队列组件，承载动作队列条目列表与循环设置。
// 核心逻辑 / 定义内容:
//   1. 纯数据保存当前动作条目列表、循环开关与队列版本号。
//   2. 提供只读派生属性（Current, IsEmpty, Count）。
//   3. 零业务逻辑：不包含出队、结算、调用执行器或循环逻辑。
// ==============================================================================

namespace Domain.Components;

using YuoCore.Ecs;

/// <summary>
/// 玩家动作队列组件。
/// 严格遵循纯 ECS 规范：只保存数据与只读派生属性，绝不包含出队、结算、调用执行器或循环逻辑。
/// </summary>
public sealed class ActionQueueComponent : YuoComponent
{
    /// <summary>当前队列中的动作执行条目列表。</summary>
    public List<QueueEntryState> Entries { get; init; } = [];

    /// <summary>队列是否开启循环播放。</summary>
    public bool LoopEnabled { get; set; } = true;

    /// <summary>队列结构变更修订版本号。</summary>
    public long Revision { get; set; }

    /// <summary>当前正在执行或队首待执行的动作条目。</summary>
    public QueueEntryState? Current => Entries.Count == 0 ? null : Entries[0];

    /// <summary>队列是否为空。</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>队列中动作条目总数。</summary>
    public int Count => Entries.Count;
}
