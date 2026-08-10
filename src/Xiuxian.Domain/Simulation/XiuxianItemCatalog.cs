using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 物品规则的唯一来源。行动、战斗和协议只读取版本化游戏数据快照。
/// </summary>
public static class XiuxianItemCatalog
{
    public static IReadOnlyCollection<XiuxianItemDefinition> All => XiuxianGameData.Current.Items.Values.ToArray();

    public static XiuxianItemDefinition Get(string itemId)
    {
        return XiuxianGameData.Current.Items.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知物品: {itemId}");
    }

    public static bool TryGet(string? itemId, out XiuxianItemDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            definition = null!;
            return false;
        }

        return XiuxianGameData.Current.Items.TryGetValue(itemId, out definition!);
    }

    public static bool CanUseInAction(string? itemId, XiuxianActionType actionType)
    {
        return TryGet(itemId, out var definition)
            && definition.Consumable?.UsableInActionTypes.Contains(actionType) == true;
    }
}

