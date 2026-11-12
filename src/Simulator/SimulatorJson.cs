using System.Text.Json;

namespace DaoHua.Simulator;

/// <summary>模拟器 JSON 线协议：camelCase + Web 默认（双精度原样透传、大小写不敏感）。</summary>
public static class SimulatorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
