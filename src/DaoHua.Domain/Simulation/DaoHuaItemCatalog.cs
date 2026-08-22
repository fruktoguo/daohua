// ==============================================================================
// 文件名称: DaoHuaItemCatalog.cs
// 主要职责: 物品静态目录检索类，提供对游戏中所有物品定义的查询与消耗品可用性校验。
// 核心逻辑 / 定义内容:
//   1. 全量物品配置检索与按 ID 获取物品原型（Get / TryGet）。
//   2. 校验物品是否允许在特定动作类型（修炼/探索/炼丹/战斗）中消耗（CanUseInAction）。
// ==============================================================================

using DaoHua.Domain.GameData;
using DaoHua.Domain.Models;


namespace DaoHua.Domain.Simulation;

/// <summary>
/// 物品静态目录检索类，提供对游戏中所有物品定义的查询与消耗品可用性校验。
/// </summary>
public static class DaoHuaItemCatalog
{
    /// <summary>获取全部物品定义列表。</summary>
    public static IReadOnlyCollection<DaoHuaItemDefinition> All => DaoHuaGameData.Current.Items.Values.ToArray();

    /// <summary>
    /// 根据物品 ID 获取物品静态原型定义。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <returns>物品原型对象。</returns>
    /// <exception cref="KeyNotFoundException">当物品 ID 不存在时抛出。</exception>
    public static DaoHuaItemDefinition Get(string itemId)
    {
        return DaoHuaGameData.Current.Items.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知物品: {itemId}");
    }

    /// <summary>
    /// 尝试根据物品 ID 检索物品定义原型。
    /// </summary>
    /// <param name="itemId">物品唯一 ID。</param>
    /// <param name="definition">输出获取到的物品定义原型。</param>
    /// <returns>找到返回 true，否则返回 false。</returns>
    public static bool TryGet(string? itemId, out DaoHuaItemDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            definition = null!;
            return false;
        }

        return DaoHuaGameData.Current.Items.TryGetValue(itemId, out definition!);
    }

    /// <summary>
    /// 校验指定物品是否允许在特定动作类型执行过程中使用。
    /// </summary>
    /// <param name="itemId">物品 ID。</param>
    /// <param name="actionType">动作类型（修炼/探索/炼丹/战斗）。</param>
    /// <returns>允许使用返回 true，否则返回 false。</returns>
    public static bool CanUseInAction(string? itemId, DaoHuaActionType actionType)
    {
        return TryGet(itemId, out var definition)
            && definition.Consumable?.UsableInActionTypes.Contains(actionType) == true;
    }
}


