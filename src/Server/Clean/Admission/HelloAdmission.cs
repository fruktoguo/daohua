// ==============================================================================
// 文件名称: HelloAdmission.cs
// 主要职责: 会话准入中性扩展点——Hello 握手时的在线会话所有权裁决接口、默认开放语义
//           实现与构建期注入模块的启动装配发现。防多开等私有实现经此接入而不出现在开源仓库。
// ==============================================================================

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Clean.Admission;

/// <summary>会话准入裁决结果：主仓库只承载中性字段，拒绝/踢线的错误码、文案与关闭码由具体实现提供。</summary>
/// <param name="Accepted">是否准入</param>
/// <param name="KickedSession">准入成功且顶替了同账号旧会话时的旧会话实例</param>
/// <param name="KickNoticeErrorCode">顶替旧会话时随通知帧下发的错误码</param>
/// <param name="KickNoticeMessage">顶替旧会话时随通知帧下发的提示文案</param>
/// <param name="KickCloseCode">顶替旧会话时用于关闭旧连接的 WebSocket 关闭码</param>
/// <param name="ErrorCode">拒绝时的协议错误码</param>
/// <param name="Message">拒绝时可展示给用户的提示</param>
/// <param name="CloseCode">拒绝时应使用的 WebSocket 关闭码</param>
public sealed record HelloAdmissionVerdict(
    bool Accepted,
    WebSocketSession? KickedSession,
    string? KickNoticeErrorCode,
    string? KickNoticeMessage,
    int? KickCloseCode,
    string? ErrorCode,
    string? Message,
    int? CloseCode)
{
    /// <summary>构造普通准入成功结果（无顶替）</summary>
    public static HelloAdmissionVerdict Accept() => new(true, null, null, null, null, null, null, null);

    /// <summary>构造拒绝结果</summary>
    public static HelloAdmissionVerdict Reject(string errorCode, string message, int closeCode) =>
        new(false, null, null, null, null, errorCode, message, closeCode);
}

/// <summary>
/// 会话准入钩子：Hello 握手时统一裁决在线会话所有权（单会话互斥/互踢/设备配额等由具体实现定义）。
/// 默认实现为开放语义；启用了私有准入模块的构建由模块启动器覆盖注册。
/// </summary>
public interface IHelloAdmissionHook
{
    /// <summary>对完成身份认证的会话执行准入裁决；仅在 Hello 处理流程中调用一次。</summary>
    /// <param name="session">待准入的会话</param>
    /// <param name="deviceKey">客户端 Hello 上报的设备键（设备 ID 优先，可为 null）</param>
    /// <param name="moduleDeclaration">客户端 Hello 上报的模块声明（开源构建恒为 null）</param>
    /// <param name="policy">本次握手风险观测所用策略快照，防止准入途中切换开关</param>
    HelloAdmissionVerdict Admit(WebSocketSession session, string? deviceKey, string? moduleDeclaration, AccountAbuse.AccountRiskPolicy policy);

    /// <summary>会话生命周期结束时释放准入占用的登记位。</summary>
    /// <returns>该会话仍是注册表中的当前会话时返回 true，调用方据此决定是否卸载玩家 Actor</returns>
    bool Release(WebSocketSession session);

    /// <summary>当前策略快照下是否向客户端下发标签页互斥开关（false 表示 HelloAck 不携带该配置）。</summary>
    bool TabGuardEnabled(AccountAbuse.AccountRiskPolicy policy);
}

/// <summary>
/// 默认开放语义：无条件放行并登记首个会话（供广播与运维可见）；同账号后续连接静默共存，不拒绝不顶替。
/// </summary>
/// <param name="sessions">在线会话注册表</param>
public sealed class OpenHelloAdmissionHook(SessionRegistry sessions) : IHelloAdmissionHook
{
    public HelloAdmissionVerdict Admit(WebSocketSession session, string? deviceKey, string? moduleDeclaration, AccountAbuse.AccountRiskPolicy policy)
    {
        // 互斥登记失败（已有在线会话）时静默共存：开放语义不拒绝任何连接，广播与运维可见性归属首个会话。
        sessions.TryAdd(session);
        return HelloAdmissionVerdict.Accept();
    }

    public bool Release(WebSocketSession session) => sessions.Remove(session.PlayerId, session);

    public bool TabGuardEnabled(AccountAbuse.AccountRiskPolicy policy) => false;
}

/// <summary>扩展模块装配入口：构建期注入本程序集的实现类由宿主启动时发现并调用，注册各自服务。</summary>
public interface IHostModuleStartup
{
    /// <summary>在宿主默认注册之后调用；实现应以覆盖语义替换默认服务注册。</summary>
    void ConfigureServices(IServiceCollection services);
}

/// <summary>扫描程序集中实现了 <see cref="IHostModuleStartup"/> 的模块装配入口。</summary>
public static class HostModuleDiscovery
{
    /// <summary>枚举指定程序集内的全部模块启动器（开源构建中不存在实现类型，返回空序列）。</summary>
    public static IEnumerable<IHostModuleStartup> EnumerateStartups(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
            if (typeof(IHostModuleStartup).IsAssignableFrom(type) && type is { IsInterface: false, IsAbstract: false })
                yield return (IHostModuleStartup)Activator.CreateInstance(type)!;
    }
}
