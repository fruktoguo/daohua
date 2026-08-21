using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.Simulation;

/// <summary>
/// 物品规则的唯一来源。行动、战斗和协议只读取版本化游戏数据快照。
/// </summary>
public static class DaoxuanItemCatalog
{
    public static IReadOnlyCollection<DaoxuanItemDefinition> All => DaoxuanGameData.Current.Items.Values.ToArray();

    public static DaoxuanItemDefinition Get(string itemId)
    {
        return DaoxuanGameData.Current.Items.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知物品: {itemId}");
    }

    public static bool TryGet(string? itemId, out DaoxuanItemDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            definition = null!;
            return false;
        }

        return DaoxuanGameData.Current.Items.TryGetValue(itemId, out definition!);
    }

    public static bool CanUseInAction(string? itemId, DaoxuanActionType actionType)
    {
        return TryGet(itemId, out var definition)
            && definition.Consumable?.UsableInActionTypes.Contains(actionType) == true;
    }
}

