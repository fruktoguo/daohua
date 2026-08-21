using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.Simulation;

public static class DaoxuanActionCatalog
{
    public static IReadOnlyCollection<DaoxuanActionDefinition> All =>
        DaoxuanGameData.Current.Actions.Values.Where(definition => !definition.HiddenFromCatalog).ToArray();

    public static IReadOnlyCollection<DaoxuanActionDefinition> AllIncludingHidden =>
        DaoxuanGameData.Current.Actions.Values.ToArray();

    public static DaoxuanActionDefinition Get(string actionId)
    {
        return DaoxuanGameData.Current.Actions.TryGetValue(actionId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知行动: {actionId}");
    }

    public static bool TryGet(string actionId, out DaoxuanActionDefinition definition)
    {
        return DaoxuanGameData.Current.Actions.TryGetValue(actionId, out definition!);
    }
}

