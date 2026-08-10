using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.Simulation;

public static class XiuxianActionCatalog
{
    public static IReadOnlyCollection<XiuxianActionDefinition> All =>
        XiuxianGameData.Current.Actions.Values.Where(definition => !definition.HiddenFromCatalog).ToArray();

    public static IReadOnlyCollection<XiuxianActionDefinition> AllIncludingHidden =>
        XiuxianGameData.Current.Actions.Values.ToArray();

    public static XiuxianActionDefinition Get(string actionId)
    {
        return XiuxianGameData.Current.Actions.TryGetValue(actionId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知行动: {actionId}");
    }

    public static bool TryGet(string actionId, out XiuxianActionDefinition definition)
    {
        return XiuxianGameData.Current.Actions.TryGetValue(actionId, out definition!);
    }
}

