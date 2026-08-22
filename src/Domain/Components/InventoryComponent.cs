// ==============================================================================
// 文件名称: InventoryComponent.cs
// 主要职责: 玩家背包与物品存储组件。
// 核心逻辑 / 定义内容:
//   1. 纯数据字典存储物品持有量，数值统一采用 double 精度。
//   2. 提供只读查询（Get, Has, ToFrozenSnapshot）。
//   3. 零业务逻辑：不自行判断扣除或产出，由过渡系统统一操作。
// ==============================================================================

namespace Domain.Components;

using System.Collections.Frozen;
using YuoCore.Ecs;

/// <summary>
/// 玩家背包与物品存储组件。
/// 严格遵循纯 ECS 规范：只存储数据并提供只读快照/查询，数值统一采用 double 精度。
/// </summary>
public sealed class InventoryComponent : YuoComponent
{
    /// <summary>物品持有字典（物品 ID -> 持有数量）。</summary>
    public Dictionary<string, double> Items { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取指定物品的当前持有数量。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <returns>持有数量，若不存在则返回 0.0。</returns>
    public double Get(string itemId) => Items.TryGetValue(itemId, out var amount) ? amount : 0.0;

    /// <summary>
    /// 检查指定物品是否持有足够数量。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <param name="amount">所需数量。</param>
    /// <returns>是否足额持有。</returns>
    public bool Has(string itemId, double amount) => Get(itemId) >= amount;

    /// <summary>
    /// 生成不可变只读字典快照（供只读推进上下文 ActionExecutionContext 使用）。
    /// </summary>
    public FrozenDictionary<string, double> ToFrozenSnapshot() => Items.ToFrozenDictionary(StringComparer.Ordinal);
}
