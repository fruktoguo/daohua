// ==============================================================================
// 文件名称: ActionCatalog.cs
// 主要职责: 游戏动作原型静态目录检索类，提供对全部可执行动作配置的快速访问。
// 核心逻辑 / 定义内容:
//   1. 提供全量公开动作（All）与包含隐藏动作（AllIncludingHidden）的集合检索。
//   2. 根据动作 ID 获取动作定义原型（Get / TryGet）。
// ==============================================================================

using Domain.GameData;
using Domain.Models;


namespace Domain.Simulation;

/// <summary>
/// 游戏动作原型静态目录检索类，提供对全部可执行动作配置的快速访问。
/// </summary>
public static class ActionCatalog
{
    /// <summary>
    /// 获取目录中所有对玩家公开可见的常规动作列表（排除隐藏动作）。
    /// </summary>
    public static IReadOnlyCollection<ActionDefinition> All =>
        GameDataCatalog.Current.Actions.Values.Where(definition => !definition.HiddenFromCatalog).ToArray();

    /// <summary>
    /// 获取包括系统隐藏动作在内的全部动作定义列表。
    /// </summary>
    public static IReadOnlyCollection<ActionDefinition> AllIncludingHidden =>
        GameDataCatalog.Current.Actions.Values.ToArray();

    /// <summary>
    /// 根据动作 ID 获取对应的动作定义原型。
    /// </summary>
    /// <param name="actionId">动作唯一 ID。</param>
    /// <returns>动作定义原型对象。</returns>
    /// <exception cref="KeyNotFoundException">当指定的 actionId 不存在时抛出。</exception>
    public static ActionDefinition Get(string actionId)
    {
        return GameDataCatalog.Current.Actions.TryGetValue(actionId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知行动: {actionId}");
    }

    /// <summary>
    /// 尝试根据动作 ID 检索动作定义原型。
    /// </summary>
    /// <param name="actionId">动作唯一 ID。</param>
    /// <param name="definition">输出获取到的动作定义原型。</param>
    /// <returns>找到返回 true，否则返回 false。</returns>
    public static bool TryGet(string actionId, out ActionDefinition definition)
    {
        return GameDataCatalog.Current.Actions.TryGetValue(actionId, out definition!);
    }
}


