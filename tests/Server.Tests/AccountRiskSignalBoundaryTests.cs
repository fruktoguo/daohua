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

    /// <summary>客户端探测或请求头中命中无头/自动化特征时，风险信号正确标记 IsHeadless 并哈希为 Headless 信号。</summary>
    [Fact]
    public void HeadlessEnvironmentIsIdentifiedByClientProbeOrHeaders()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var hasher = new AccountRiskHasher("test-headless");

        // 1. 正常桌面 Chrome
        var normalContext = new DefaultHttpContext();
        normalContext.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        var normalSignals = collector.Collect(normalContext, new(null, Guid.NewGuid().ToString("D"), null, null, Automated: false));
        Assert.False(normalSignals.IsHeadless);
        Assert.DoesNotContain(hasher.Hash(normalSignals, AccountRiskPolicy.Default), s => s.Kind == AccountRiskSignalKind.Headless);

        // 2. 客户端自报自动化环境
        var automatedSignals = collector.Collect(normalContext, new(null, Guid.NewGuid().ToString("D"), null, null, Automated: true));
        Assert.True(automatedSignals.IsHeadless);
        Assert.Contains(hasher.Hash(automatedSignals, AccountRiskPolicy.Default), s => s.Kind == AccountRiskSignalKind.Headless);

        // 3. User-Agent 包含 HeadlessChrome
        var headlessContext = new DefaultHttpContext();
        headlessContext.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36";
        var headlessSignals = collector.Collect(headlessContext, new(null, Guid.NewGuid().ToString("D"), null, null));
        Assert.True(headlessSignals.IsHeadless);
        Assert.Contains(hasher.Hash(headlessSignals, AccountRiskPolicy.Default), s => s.Kind == AccountRiskSignalKind.Headless);

        // 4. Client Hints (Sec-CH-UA) 包含 HeadlessChrome
        var hintsContext = new DefaultHttpContext();
        hintsContext.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
        hintsContext.Request.Headers["Sec-CH-UA"] = "\"HeadlessChrome\";v=\"120\", \"Chromium\";v=\"120\"";
        var hintsSignals = collector.Collect(hintsContext, new(null, Guid.NewGuid().ToString("D"), null, null));
        Assert.True(hintsSignals.IsHeadless);

        // 5. 缺少 User-Agent 的裸脚本请求
        var emptyContext = new DefaultHttpContext();
        var bareSignals = collector.Collect(emptyContext, new(null, Guid.NewGuid().ToString("D"), null, null));
        Assert.True(bareSignals.IsHeadless);
    }

    /// <summary>同一物理设备的指纹在隐身/无痕窗口（每次无 Cookie）下仍推导出相同 PersonKey，确保主体限制无法被无痕窗口绕过。</summary>
    [Fact]
    public void FingerprintAnchorsStablePersonKeyAcrossIncognitoWindows()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var stableFingerprint = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

        // 模拟第 1 个无痕窗口：无 Cookie，上报临时随机 DeviceId + 稳定硬件指纹
        var window1 = new DefaultHttpContext();
        window1.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0";
        var signals1 = collector.Collect(window1, new(null, Guid.NewGuid().ToString("D"), null, stableFingerprint));

        // 模拟第 2 个无痕窗口：完全丢弃 Cookie，生成新的临时 DeviceId，但硬件指纹相同
        var window2 = new DefaultHttpContext();
        window2.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0";
        var signals2 = collector.Collect(window2, new(null, Guid.NewGuid().ToString("D"), null, stableFingerprint));

        // 两者临时 DeviceId 不同，但 PersonKey 均稳定锚定在硬件指纹上
        Assert.NotEqual(signals1.DeviceId, signals2.DeviceId);
        Assert.Equal($"fp:{stableFingerprint}", signals1.PersonKey);
        Assert.Equal($"fp:{stableFingerprint}", signals2.PersonKey);
        Assert.Equal(signals1.PersonKey, signals2.PersonKey);
    }

    /// <summary>验证加固后的风控模块权重与频次门槛，确保符合防批量产号要求。</summary>
    [Fact]
    public void HardenedPolicyModuleWeightsReflectStricterAntiAbuse()
    {
        var policy = AccountRiskPolicy.Default;
        var ip = policy.Modules.Single(m => m.Id == "ipAddress");
        var fingerprint = policy.Modules.Single(m => m.Id == "browserFingerprint");
        var velocity = policy.Modules.Single(m => m.Id == "registrationVelocity");

        Assert.Equal(40, ip.Weight);
        Assert.Equal(40, fingerprint.Weight);
        Assert.Equal(80, velocity.Weight);
        Assert.Equal(3, velocity.Threshold); // 1 小时内注册 >= 3 次即加 80 分秒拒
        Assert.Equal(80, policy.RiskThreshold);
    }
}
