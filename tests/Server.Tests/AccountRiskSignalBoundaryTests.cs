using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 账号风控的纯内存边界：摘要归一化、请求上下文采集与反向代理还原。
/// 这些用例全部使用 <see cref="EphemeralDataProtectionProvider"/> 与 <see cref="DefaultHttpContext"/>，
/// 不触任何持久化，因此不进 PostgresIntegration 集合，可在 --fast 下执行。
/// </summary>
public sealed class AccountRiskSignalBoundaryTests
{
    /// <summary>同一 IPv6 的展开与压缩写法不会拆成两份风险信号。</summary>
    [Fact]
    public void HasherNormalizesEquivalentIpv6Representations()
    {
        var hasher = new AccountRiskHasher("account-risk-ip-boundary");
        var expanded = hasher.Hash(new AccountRiskSignals(IpAddress: "2001:0DB8:0000:0000:0000:0000:0000:0047"), AccountRiskPolicy.Default);
        var compressed = hasher.Hash(new AccountRiskSignals(IpAddress: "2001:db8::47"), AccountRiskPolicy.Default);

        Assert.Equal(expanded, compressed);
    }

    /// <summary>可信请求边界将 IPv4-mapped 来源与普通 IPv4 归为同一 IP 和网段。</summary>
    [Fact]
    public void RequestContextNormalizesMappedIpv4Source()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var plain = new DefaultHttpContext();
        plain.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.47");
        var mapped = new DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.47");

        var plainSignals = collector.Collect(plain, null, persistCookie: false);
        var mappedSignals = collector.Collect(mapped, null, persistCookie: false);

        Assert.Equal(plainSignals.IpAddress, mappedSignals.IpAddress);
        Assert.Equal(plainSignals.NetworkHash, mappedSignals.NetworkHash);
    }

    /// <summary>空客户端对象不能得到随机设备额度；已验证的设备 Cookie 仍能恢复真实锚点。</summary>
    [Fact]
    public void EmptyClientCannotInventDeviceAndProtectedCookieRestoresAnchor()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var empty = new DefaultHttpContext();
        var missing = collector.Collect(empty, new(null, null, null, null));
        Assert.Null(missing.DeviceId);
        Assert.Null(missing.PersonKey);
        Assert.False(empty.Response.Headers.ContainsKey("Set-Cookie"));

        var original = new DefaultHttpContext();
        var id = Guid.NewGuid().ToString("D");
        var first = collector.Collect(original, new(null, id, null, null));
        Assert.NotNull(first.DeviceId);
        Assert.NotEqual(id, first.DeviceId);
        var restored = new DefaultHttpContext();
        restored.Request.Headers.Cookie = original.Response.Headers.SetCookie.ToString().Split(';')[0];
        var actual = collector.Collect(restored, new(null, Guid.NewGuid().ToString("D"), null, null), false);
        Assert.Equal(first.DeviceId, actual.DeviceId);
        Assert.Equal($"device:{first.DeviceId}", actual.PersonKey);
    }

    /// <summary>反向代理场景下，清空 KnownNetworks/KnownProxies 的 ForwardedHeadersMiddleware 能够正确将 Docker 网桥网关后的真实客户端 IP 还原并采集为准确网段。</summary>
    [Fact]
    public async Task ForwardedHeadersExtractsRealClientIpThroughBridgeGateway()
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var middleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.19.0.1"); // Docker 网桥网关
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.195";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse("203.0.113.195"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);

        var dataProtection = new EphemeralDataProtectionProvider();
        var collector = new AccountRiskRequestContext(dataProtection);
        var signals = collector.Collect(context, new AccountRiskClientSignals(null, Guid.NewGuid().ToString("D"), null, null), persistCookie: false);

        Assert.Equal("203.0.113.195", signals.IpAddress);
        Assert.Equal("203.0.113.0/24", signals.NetworkHash);
    }
}
