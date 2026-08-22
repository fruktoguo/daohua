// ==============================================================================
// 文件名称: AccessControl.cs
// 主要职责: 提供服务端 API 与 WebSocket 连接的共享访问控制与令牌防时序攻击校验。
// 核心逻辑 / 定义内容:
//   1. 从应用配置读取共享访问口令 (Server:AccessToken) 并缓存 UTF-8 字节。
//   2. 使用 CryptographicOperations.FixedTimeEquals 执行恒定时间比较，防止时序侧信道攻击。
//   3. 支持开发环境未配置时自动放行。
// ==============================================================================

using System.Security.Cryptography;
using System.Text;

namespace Server.Infrastructure;

/// <summary>
/// 玩家级接口共享访问控制 (AccessControl)。
/// <para>
/// 职责与安全说明：
/// 1. 当前客户端使用本地 UUID 标识玩家，未配置商业鉴权前使用共享口令 (Server:AccessToken) 保护 API 与 WebSocket。
/// 2. 未配置令牌时默认放行（便于本地开发）；配置后强制要求调用方提供正确 Token。
/// 3. 使用 <see cref="CryptographicOperations.FixedTimeEquals"/> 恒定时间比较，防止时序侧信道攻击 (Timing Attack)。
/// </para>
/// </summary>
public sealed class AccessControl
{
    private readonly byte[]? _expectedToken;

    /// <summary>
    /// 从应用配置加载 AccessToken 并预编码为 UTF8 字节。
    /// </summary>
    public AccessControl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredToken = configuration["Server:AccessToken"];
        Enabled = !string.IsNullOrWhiteSpace(configuredToken);
        // 启用时缓存 UTF8 字节，避免每次请求重复编码；未启用时保持 null。
        _expectedToken = Enabled ? Encoding.UTF8.GetBytes(configuredToken!) : null;
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
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(provided.Length);
        if (maxByteCount <= 256)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var written = Encoding.UTF8.GetBytes(provided.AsSpan(), buffer);
            var providedBytes = buffer[..written];
            return providedBytes.Length == _expectedToken!.Length
                && CryptographicOperations.FixedTimeEquals(providedBytes, _expectedToken);
        }
        else
        {
            var providedBytes = Encoding.UTF8.GetBytes(provided);
            return providedBytes.Length == _expectedToken!.Length
                && CryptographicOperations.FixedTimeEquals(providedBytes, _expectedToken);
        }
    }
}
