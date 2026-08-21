using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daoxuan.Domain.Models;

/// <summary>
/// 兼容旧版快照的 autoUseSlots 字段：旧版每种行动只有单个槽位对象，新版是固定 3 格的数组。
/// 读取时两种形态都接受（旧单槽归入第 0 格，归一化时补满空槽），写出时一律使用数组。
/// </summary>
public sealed class DaoxuanAutoUseSlotsJsonConverter
    : JsonConverter<Dictionary<DaoxuanActionType, List<DaoxuanAutoUseSlot>>>
{
    public override Dictionary<DaoxuanActionType, List<DaoxuanAutoUseSlot>> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("autoUseSlots 必须是对象。");
        }

        var result = new Dictionary<DaoxuanActionType, List<DaoxuanAutoUseSlot>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("autoUseSlots 结构无效。");
            }

            if (!Enum.TryParse<DaoxuanActionType>(reader.GetString(), ignoreCase: true, out var actionType))
            {
                reader.Skip();
                continue;
            }

            reader.Read();
            result[actionType] = reader.TokenType switch
            {
                JsonTokenType.StartArray =>
                    JsonSerializer.Deserialize<List<DaoxuanAutoUseSlot>>(ref reader, options) ?? [],
                // 旧版快照：值是单个槽位对象。
                JsonTokenType.StartObject =>
                    JsonSerializer.Deserialize<DaoxuanAutoUseSlot>(ref reader, options) is { } slot ? [slot] : [],
                JsonTokenType.Null => [],
                _ => throw new JsonException("autoUseSlots 槽位必须是数组或对象。")
            };
        }

        throw new JsonException("autoUseSlots 结构无效。");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<DaoxuanActionType, List<DaoxuanAutoUseSlot>> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var pair in value)
        {
            writer.WritePropertyName(pair.Key.ToString());
            JsonSerializer.Serialize(writer, pair.Value, options);
        }

        writer.WriteEndObject();
    }
}
