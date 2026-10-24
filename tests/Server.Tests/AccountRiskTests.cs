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
}
