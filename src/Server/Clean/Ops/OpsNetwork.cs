using System.Net;
using System.Net.Sockets;

namespace Server.Clean.Ops;

/// <summary>
/// 运维面板网络来源判定。
/// 面板只允许回环与私有网段访问，公网地址一律拒绝，避免误暴露到外网。
/// </summary>
public static class OpsNetwork
{
    /// <summary>判断地址是否属于回环或 RFC1918 / 唯一本地地址等私有网段。</summary>
    /// <param name="address">对端地址；为 null 时视为不可信。</param>
    /// <returns>回环或私有网段返回 true。</returns>
    public static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 => (bytes[0] & 0xFE) == 0xFC
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80),
            _ => false
        };
    }
}
