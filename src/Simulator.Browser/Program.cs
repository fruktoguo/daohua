using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DaoHua.Simulator;

namespace DaoHua.Simulator.Browser;

/// <summary>
/// 浏览器 WASM 战斗预演入口：全部 JSON 字符串进出，领域/输入异常直接传到 JS 侧，
/// 由 Worker 包装为结构化错误；前端据实展示，绝无回退计算。
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class SimulatorEntry
{
    [JSExport]
    public static string CatalogFingerprint()
        => SimulatorRunner.CatalogFingerprintJson();

    [JSExport]
    public static string CreateDefaultProfile()
        => SimulatorRunner.CreateDefaultProfileJson();

    [JSExport]
    public static string ResolveProfiles(string requestJson)
        => SimulatorRunner.ResolveProfilesJson(requestJson);

    [JSExport]
    public static string Run(string requestJson)
        => SimulatorRunner.RunJson(requestJson);

    public static void Main()
    {
    }
}
