// ==============================================================================
// 文件名称: ActionDefinition.cs
// 主要职责: 定义修仙动作静态只读配置原型（ActionDefinition）。
// 核心逻辑 / 定义内容:
//   1. 描述打坐、探索、炼丹、战斗及移动等行为的基础消耗、产出与执行规则。
//   2. 采用 sealed record 与只读不可变集合，不混入任何运行时状态。
// ==============================================================================

namespace Domain.Definitions;

using System.Collections.Frozen;
using Domain.Common;
using Domain.Models;

/// <summary>
/// 动作静态原型只读定义，描述打坐、探索、炼丹、战斗及移动等行为的基础消耗、产出与执行规则。
/// 全生命周期只读不可变，不混入运行时状态。
/// </summary>
public sealed record ActionDefinition
{
    /// <summary>动作唯一强类型标识符。</summary>
    public required ActionId Id { get; init; }

    /// <summary>动作显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>动作背景与功能描述。</summary>
    public required string Description { get; init; }

    /// <summary>动作所属基本类型（修炼/探索/炼丹/战斗）。</summary>
    public required ActionType Type { get; init; }

    /// <summary>推进执行模式（计时模式/战斗遭遇/大地图旅行）。</summary>
    public ActionExecutionKind ExecutionKind { get; init; } = ActionExecutionKind.Timed;

    /// <summary>单次执行标准耗时（秒）。</summary>
    public double DurationSeconds { get; init; }

    /// <summary>关联的战斗遭遇 ID（仅当为战斗动作时有效）。</summary>
    public EncounterId? CombatEncounterId { get; init; }

    /// <summary>允许执行该动作的世界地图节点 ID 集合（为空表示全域可用）。</summary>
    public FrozenSet<string> AvailableNodeIds { get; init; } = FrozenSet<string>.Empty;

    /// <summary>是否在常规动作目录中隐藏（如特殊位移动作）。</summary>
    public bool HiddenFromCatalog { get; init; }

    /// <summary>单次执行所需消耗的物品及数量字典。</summary>
    public FrozenDictionary<ItemId, double> Inputs { get; init; } = FrozenDictionary<ItemId, double>.Empty;

    /// <summary>单次执行成功产出的物品及数量字典。</summary>
    public FrozenDictionary<ItemId, double> Outputs { get; init; } = FrozenDictionary<ItemId, double>.Empty;
}
