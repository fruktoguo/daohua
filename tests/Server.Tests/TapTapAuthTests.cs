using DaoHua.Server.Clean.TapTap;
using Xunit;

namespace Server.Tests;

public sealed class TapTapAuthTests
{
    /// <summary>
    /// TapTap 官方文档中给出的权威签算校验样例：
    /// kid: demo_kid
    /// mac_key: demo_mac_key
    /// timestamp: 1618221750
    /// nonce: adssd
    /// method: GET
    /// uri: /account/basic-info/v1?client_id=demo_client_id
    /// host: open.tapapis.cn
    /// port: 443
    /// 期望输出：Authorization: MAC id="demo_kid",ts="1618221750",nonce="adssd",mac="oYWjWxKzPBbeK2xKY7+ADJKAlmE="
    /// </summary>
    [Fact]
    public void ComputeMacAuthorizationMatchesOfficialSample()
    {
        var authHeader = TapTapAuthService.ComputeMacAuthorization(
            kid: "demo_kid",
            macKey: "demo_mac_key",
            timestamp: "1618221750",
            nonce: "adssd",
            method: "GET",
            uri: "/account/basic-info/v1?client_id=demo_client_id",
            host: "open.tapapis.cn",
            port: "443");

        Assert.Equal("MAC id=\"demo_kid\",ts=\"1618221750\",nonce=\"adssd\",mac=\"oYWjWxKzPBbeK2xKY7+ADJKAlmE=\"", authHeader);
    }
}
