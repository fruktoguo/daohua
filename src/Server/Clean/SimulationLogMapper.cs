using System.Globalization;
using Domain.GameData;
using Domain.Simulation;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 领域模拟事件到战报日志传输 DTO 的映射器。
/// 核心契约：
/// 1. 采用结构化标记模板语法：`{{kind:id|label|extra}}`（如 `{{action:id|名称}}`，`{{item:id|道具名|数量}}`）；
/// 2. 服务端只产出语义标记模板，前端统一负责按类型高亮着色、道具悬浮卡片展示和数量格式化；
/// 3. 所有产出物名称根据游戏配表（GameDataCatalog）即时解析，保持数据源唯一。
/// </summary>
public static class SimulationLogMapper
{
    /// <summary>
    /// 将确定性推进产生的领域模拟事件集合转换为带单调递增 Sequence 的战报日志 DTO，并写入环形缓冲区。
    /// 当事件数量超过 bufferCapacity (默认 200) 时，仅格式化并保留最新的 bufferCapacity 条日志，消除超量事件的 GC 尖峰。
    /// </summary>
    public static IReadOnlyList<LogEventDto> ToLogEvents(IReadOnlyList<SimulationEvent> events, GameDataCatalog gameData, PlayerLogBuffer buffer, int bufferCapacity = 200)
    {
        if (events.Count == 0) return [];
        var startIndex = Math.Max(0, events.Count - bufferCapacity);
        var result = new List<LogEventDto>();
        for (var i = startIndex; i < events.Count; i++)
        {
            var simulationEvent = events[i];
            var template = ToTemplate(simulationEvent, gameData);
            var timestamp = simulationEvent.At.ToUnixTimeMilliseconds();

            if (result.Count > 0 && result[^1].Template == template)
            {
                var last = result[^1];
                result[^1] = last with
                {
                    Sequence = buffer.NextSequence(),
                    TimestampUnixMilliseconds = Math.Max(last.TimestampUnixMilliseconds, timestamp),
                    Count = last.Count + 1
                };
            }
            else
            {
                var logEvent = new LogEventDto(
                    buffer.NextSequence(),
                    timestamp,
                    template,
                    1);
                result.Add(logEvent);
            }
        }

        for (var i = 0; i < result.Count; i++)
        {
            buffer.Append(result[i]);
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
