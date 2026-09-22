using System.Text.Json;

namespace Domain.Common;

/// <summary>
/// JSON 配表字段读取的唯一实现：对象形状防御、缺省回退与数值有限性校验。
/// 配表/装配/快照所有读取入口统一走本助手，杜绝各处复制导致的校验口径漂移。
/// </summary>
internal static class JsonRead
{
    /// <summary>读取字符串字段；非对象或字段非字符串时返回 null。</summary>
    public static string? Text(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    /// <summary>读取字符串字段并携带缺省值。</summary>
    public static string Text(JsonElement value, string key, string fallback)
        => Text(value, key) ?? fallback;

    /// <summary>读取数值字段；缺失或不可解析回退默认值，结果必须有限（含默认值）。</summary>
    public static double Number(JsonElement value, string key, double fallback = 0)
    {
        var number = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            && property.TryGetDouble(out var parsed) ? parsed : fallback;
        if (!double.IsFinite(number))
            throw new DomainException(DomainErrorCode.InvalidArgument, $"字段 {key} 必须为有限数值。");
        return number;
    }

    /// <summary>读取整数字段；缺失或不可解析回退默认值。</summary>
    public static int Integer(JsonElement value, string key, int fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            && property.TryGetInt32(out var parsed) ? parsed : fallback;

    /// <summary>读取布尔字段；缺失或类型不符回退默认值。</summary>
    public static bool Boolean(JsonElement value, string key, bool fallback = false)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            }
            : fallback;

    /// <summary>读取数组字段；非数组返回空数组。</summary>
    public static JsonElement[] Array(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToArray() : [];

    /// <summary>读取字符串数组字段；剔除非字符串与空项。</summary>
    public static string[] StringArray(JsonElement value, string key)
        => Array(value, key).Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
}
