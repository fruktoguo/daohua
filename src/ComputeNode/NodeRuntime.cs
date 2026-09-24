using System.Runtime.InteropServices;

namespace DaoHua.ComputeNode;

/// <summary>
/// 节点运行时指纹：进程架构 + 运行时描述。
/// 仅作诊断用途——主服在节点接入日志与注册视图中记录该值，
/// 用于定位 arm64/x64 或运行时版本差异导致的确定性漂移（如浮点尾数、JIT 代码生成差异）。
/// </summary>
public static class NodeRuntime
{
    /// <summary>形如 "Arm64/.NET 10.0.1" 的指纹串；进程内不变。</summary>
    public static readonly string Fingerprint =
        $"{RuntimeInformation.ProcessArchitecture}/{RuntimeInformation.FrameworkDescription}";
}
