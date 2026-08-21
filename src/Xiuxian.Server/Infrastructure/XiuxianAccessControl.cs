using System.Security.Cryptography;
using System.Text;

namespace Xiuxian.Server.Infrastructure;

/// <summary>
/// 玩家级接口的可选共享访问令牌校验。
/// 安全假设：当前客户端仅以本地 UUID 标识玩家，playerId 即权限；本令牌用于公网部署时
/// 阻止任意访客枚举或接管他人 playerId，属于部署者持有的共享口令，不是账号级鉴权。
/// 开放注册或涉及真实资产前，仍须补齐完整的身份认证、授权与审计。
/// </summary>
public sealed class XiuxianAccessControl
{
    private readonly byte[]? _expectedToken;

    public XiuxianAccessControl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredToken = configuration["Server:AccessToken"];
        Enabled = !string.IsNullOrWhiteSpace(configuredToken);
        // 启用时缓存 UTF8 字节，避免每次请求重复编码；未启用时保持 null。
        _expectedToken = Enabled ? Encoding.UTF8.GetBytes(configuredToken) : null;
    }

    /// <summary>是否启用鉴权：配置值非空白即视为启用。</summary>
    public bool Enabled { get; }

    /// <summary>
    /// 校验调用方提供的令牌。未启用时直接放行以保持向后兼容；
    /// 启用时长度不等直接失败，等长时使用恒定时间比较，
    /// 防止攻击者通过响应耗时逐字节猜测令牌（时序侧信道）。
    /// </summary>
    public bool IsValid(string? provided)
    {
        if (!Enabled) return true;
        if (string.IsNullOrEmpty(provided)) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return providedBytes.Length == _expectedToken!.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, _expectedToken);
    }
}
