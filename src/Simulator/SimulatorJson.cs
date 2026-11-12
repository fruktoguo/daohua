using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DaoHua.Simulator;

/// <summary>
/// 模拟器 JSON 线协议：camelCase + 严格反序列化——数字必须为 JSON 数字字面量、
/// 属性名大小写敏感、构造参数与全部 DTO 属性必须出现（可为 null，不允许缺键静默补默认），
/// 杜绝前端缺字段/字符串数字混进本地推演。required 修饰只作用于 Protocol.Simulator 的对象 DTO，
/// 不影响 JsonElement 规则、字典与领域对象。
/// </summary>
public static class SimulatorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static info =>
                {
                    if (info.Kind != JsonTypeInfoKind.Object
                        || !string.Equals(info.Type.Namespace, "Protocol.Simulator", StringComparison.Ordinal))
                        return;
                    foreach (var property in info.Properties)
                        property.IsRequired = true;
                }
            }
        }
    };
}
