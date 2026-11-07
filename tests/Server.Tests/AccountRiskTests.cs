using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>账号风控纯单元回归：确保信号只产生稳定 HMAC、策略边界不可被运维配置绕过。</summary>
public sealed class AccountRiskTests
{
    /// <summary>同一信号在不同输入顺序下产生相同摘要，原始设备值不会出现在摘要中。</summary>
    [Fact]
    public void HasherIsStableAndDoesNotExposeRawValue()
    {
        var hasher = new AccountRiskHasher("test-secret");
        var policy = AccountRiskPolicy.Default;
        var first = hasher.Hash(new AccountRiskSignals(
            DeviceConfigHash: "renderer-a",
            DeviceId: "device-a",
            IpAddress: "192.0.2.4",
            NetworkHash: "network-a",
            PersonKey: "person-a"), policy);
        var second = hasher.Hash(new AccountRiskSignals(
            DeviceConfigHash: "renderer-a",
            DeviceId: "device-a",
            IpAddress: "192.0.2.4",
            NetworkHash: "network-a",
            PersonKey: "person-a"), policy);

        Assert.Equal(first, second);
        Assert.All(first, item => Assert.DoesNotContain("renderer-a", item.Hash, StringComparison.OrdinalIgnoreCase));
        Assert.All(first, item => Assert.Equal(64, item.Hash.Length));
    }

    /// <summary>策略注册表拒绝越界账号上限，避免面板误配置成无限放行。</summary>
    [Fact]
    public void PolicyRegistryRejectsInvalidLimit()
    {
        var registry = new AccountRiskPolicyRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.Replace(AccountRiskPolicy.Default with { MaxAccountsPerDevice = 0 }));
        Assert.Throws<InvalidOperationException>(() => registry.Replace(AccountRiskPolicy.Default with { MaxAccountsPerPerson = 33 }));
        Assert.Throws<InvalidOperationException>(() => registry.Replace(AccountRiskPolicy.Default with
        {
            Modules = AccountRiskPolicy.Default.Modules.Select((module, index) => index == 0 ? null! : module).ToArray()
        }));
    }

    /// <summary>Android 客户端即使缺少浏览器指纹且上报了硬件配置，也绝不把粗粒度配置降级为 personKey。</summary>
    [Fact]
    public void AndroidEnvironmentNeverUsesDeviceConfigHashForPersonKey()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Linux; Android 14; 23117RK66C Build/UKQ1.230804.001; wv) AppleWebKit/537.36 TapTap/2.48.0";
        var sharedConfigHash = Convert.ToHexString(SHA256.HashData("mali-g77|360x800x24|cpu:8|android"u8)).ToLowerInvariant();
        var deviceId = Guid.NewGuid().ToString("D");

        var signals = collector.Collect(context, new(sharedConfigHash, deviceId, null, null), persistCookie: false);

        Assert.True(AccountRiskRequestContext.IsAndroid(context));
        Assert.Equal($"device:{signals.DeviceId}", signals.PersonKey);
        Assert.DoesNotContain(sharedConfigHash, signals.PersonKey, StringComparison.Ordinal);
    }

    /// <summary>IsAndroid 正确识别常见 Android 客户端 User-Agent，并排除 Windows/iOS/macOS 等桌面或苹果环境。</summary>
    [Theory]
    [InlineData("Mozilla/5.0 (Linux; U; Android 13; zh-cn; 2210132C Build/TKQ1.220829.002)", true)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.6723.107 Mobile Safari/537.36 TapTap/2.48.0", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", false)]
    public void IsAndroidIdentifiesAndroidUserAgents(string userAgent, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        Assert.Equal(expected, AccountRiskRequestContext.IsAndroid(context));
    }

    /// <summary>桌面端缺少指纹时，亦不得将粗粒度硬件配置直接提拔为自然人主体键。</summary>
    [Fact]
    public void DesktopEnvironmentDoesNotPromoteConfigHashToPersonKey()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
        var sharedConfigHash = Convert.ToHexString(SHA256.HashData("rtx-4080|1920x1080x24|cpu:16|windows"u8)).ToLowerInvariant();
        var deviceId = Guid.NewGuid().ToString("D");

        var signals = collector.Collect(context, new(sharedConfigHash, deviceId, null, null), persistCookie: false);

        Assert.False(AccountRiskRequestContext.IsAndroid(context));
        Assert.Equal($"device:{signals.DeviceId}", signals.PersonKey);
        Assert.DoesNotContain(sharedConfigHash, signals.PersonKey, StringComparison.Ordinal);
    }
}
