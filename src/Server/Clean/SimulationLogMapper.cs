using System.Globalization;
using Domain.GameData;
using Domain.Simulation;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 把领域模拟事件映射为战报日志。模板使用前端引用语法 {{kind:id|label|extra}}，
/// 服务端只产出模板文本，由前端负责解析渲染。
/// </summary>
public static class SimulationLogMapper
{
    public static IReadOnlyList<LogEventDto> ToLogEvents(IReadOnlyList<SimulationEvent> events, GameDataCatalog gameData, PlayerLogBuffer buffer)
    {
        if (events.Count == 0) return [];
        var result = new List<LogEventDto>(events.Count);
        foreach (var simulationEvent in events)
        {
            var logEvent = new LogEventDto(
                buffer.NextSequence(),
                simulationEvent.At.ToUnixTimeMilliseconds(),
                ToTemplate(simulationEvent, gameData));
            buffer.Append(logEvent);
            result.Add(logEvent);
        }
        return result;
    }

    private static string ToTemplate(SimulationEvent simulationEvent, GameDataCatalog gameData) => simulationEvent.Code switch
    {
        "action.completed" => BuildActionCompleted(simulationEvent, gameData),
        _ => simulationEvent.Message
    };

    private static string BuildActionCompleted(SimulationEvent simulationEvent, GameDataCatalog gameData)
    {
        var builder = new StringBuilder("完成行动 {{action:")
            .Append(simulationEvent.ActionId?.Value ?? string.Empty)
            .Append('|')
            .Append(simulationEvent.Message)
            .Append("}}");
        if (simulationEvent.ItemDeltas is not { Count: > 0 } deltas) return builder.ToString();
        builder.Append("，获得 ");

        var items = new KeyValuePair<ItemId, double>[deltas.Count];
        var idx = 0;
        foreach (var kv in deltas) items[idx++] = kv;
        if (items.Length > 1)
        {
            Array.Sort(items, static (a, b) => string.CompareOrdinal(a.Key.Value, b.Key.Value));
        }

        var first = true;
        for (var i = 0; i < items.Length; i++)
        {
            var pair = items[i];
            if (!first) builder.Append('、');
            builder.Append("{{item:")
                .Append(pair.Key.Value)
                .Append('|')
                .Append(ItemName(gameData, pair.Key))
                .Append('|')
                .AppendFormat(CultureInfo.InvariantCulture, "{0:0.######}", pair.Value)
                .Append("}}");
            first = false;
        }
        return builder.ToString();
    }

    private static string ItemName(GameDataCatalog gameData, ItemId itemId) =>
        gameData.RawItems.TryGetValue(itemId.Value, out var item)
        && item.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? itemId.Value
            : itemId.Value;
}
