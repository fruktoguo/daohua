// ==============================================================================
// 文件名称: CultivationComponent.cs
// 主要职责: 玩家修为、境界与修炼状态组件。
// 核心逻辑 / 定义内容:
//   1. 纯数据保存灵气、修炼经验、周天计数、突破状态、挂机模式与生效 Buff 列表。
//   2. 保存单调递增的模拟时间戳 LastSimulatedAt。
// ==============================================================================

namespace Domain.Components;

using Domain.Models;
using YuoCore.Ecs;

/// <summary>
/// 玩家修为、境界与修炼状态组件。
/// </summary>
public sealed class CultivationComponent : YuoComponent
{
    /// <summary>当前灵气修为储备。</summary>
    public double Qi { get; set; }

    /// <summary>筑基/功法修炼经验值。</summary>
    public double FoundationExp { get; set; }

    /// <summary>累计打坐修炼大周天次数。</summary>
    public long CultivationCycles { get; set; }

    /// <summary>是否处于等待突破境界状态。</summary>
    public bool IsPendingBreakthrough { get; set; }

    /// <summary>上次完成模拟结算的时间戳（单调递增）。</summary>
    public DateTimeOffset LastSimulatedAt { get; set; }

    /// <summary>玩家当前的在线与挂机模式。</summary>
    public PresenceMode PresenceMode { get; set; } = PresenceMode.IdleOffline;

    /// <summary>当前生效中的 Buff 列表。</summary>
    public List<BuffState> Buffs { get; init; } = [];
}
