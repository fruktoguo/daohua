// ==============================================================================
// 文件名称: CombatResourcesComponent.cs
// 主要职责: 玩家战斗资源（生命、法力/灵力、技能与消耗品冷却）组件。
// 核心逻辑 / 定义内容:
//   1. 纯数据保存生命、法力、小数余量与冷却字典，数值统一采用 double 精度。
//   2. 提供只读派生属性（IsDead）。
// ==============================================================================

namespace Domain.Components;

using YuoCore.Ecs;

/// <summary>
/// 玩家战斗资源（生命、法力/灵力、冷却）组件。
/// </summary>
public sealed class CombatResourcesComponent : YuoComponent
{
    /// <summary>是否已完成初次战斗资源初始化。</summary>
    public bool Initialized { get; set; }

    /// <summary>当前持久化生命值。</summary>
    public double Hitpoints { get; set; }

    /// <summary>当前持久化法力/灵力值。</summary>
    public double Manapoints { get; set; }

    /// <summary>生命回复小数累加余量（保证高精度结算）。</summary>
    public double HitpointRemainder { get; set; }

    /// <summary>法力回复小数累加余量。</summary>
    public double ManapointRemainder { get; set; }

    /// <summary>技能绝对冷却到期时间字典（技能 ID -> 到期时间戳）。</summary>
    public Dictionary<string, DateTimeOffset> AbilityCooldowns { get; init; } = new(StringComparer.Ordinal);

    /// <summary>消耗品快捷槽位绝对冷却到期时间字典（槽位索引 -> 到期时间戳）。</summary>
    public Dictionary<int, DateTimeOffset> ConsumableSlotCooldowns { get; init; } = [];

    /// <summary>是否处于阵亡/死亡状态。</summary>
    public bool IsDead => Initialized && Hitpoints <= 0;
}
