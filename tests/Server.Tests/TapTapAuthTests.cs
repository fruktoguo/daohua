using System.Net;
using System.Text;
using DaoHua.Server.Clean.TapTap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public void OAuthStateProtector_RoundtripTamperMismatchAndExpiryWorkAsExpected()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TapTap:StateSecret"] = "unit-test-taptap-oauth-state-secret-key"
            })
            .Build();
        var protector = new TapTapOAuthStateProtector(config);
        var issuedAt = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        var (state, cookieValue) = protector.CreateState(issuedAt);

        Assert.Equal(32, state.Length);
        Assert.True(protector.ValidateState(state, cookieValue, issuedAt.AddMinutes(5)));
        Assert.False(protector.ValidateState("wrong-state", cookieValue, issuedAt.AddMinutes(5)));
        Assert.False(protector.ValidateState(state, cookieValue, issuedAt.Add(TapTapOAuthStateProtector.StateLifetime).AddSeconds(1)));

        // 篡改签名最后一位必须拒绝
        var tamperedCookie = cookieValue[..^1] + (cookieValue[^1] == 'A' ? 'B' : 'A');
        Assert.False(protector.ValidateState(state, tamperedCookie, issuedAt.AddMinutes(5)));
    }

    [Fact]
    public async Task GetUserProfileAsync_VerifiesMacCredentialsAgainstTapTapResponse()
    {
        string? capturedAuth = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedAuth = request.Headers.Authorization?.ToString();
            var json = """
                {
                  "success": true,
                  "data": {
                    "openid": "tap_open_123",
                    "unionid": "tap_union_456",
                    "name": "太初道友",
                    "avatar": "https://img.tapimg.com/avatar.png"
                  }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var service = new TapTapAuthService(
            new HttpClient(handler),
            Options.Create(new TapTapOptions { ClientId = "demo_client_id", ClientSecret = "demo_secret" }),
            NullLogger<TapTapAuthService>.Instance);

        var profile = await service.GetUserProfileAsync(new TapTapAccessToken("kid_1", "mac", "mac_secret_1", "hmac-sha-1"));

        Assert.NotNull(profile);
        Assert.Equal("tap_open_123", profile!.OpenId);
        Assert.Equal("tap_union_456", profile.UnionId);
        Assert.Equal("太初道友", profile.Name);
        Assert.NotNull(capturedAuth);
        Assert.StartsWith("MAC id=\"kid_1\",", capturedAuth);
    }

    [Fact]
    public async Task GetUserProfileAsync_ReturnsNullWhenTapTapRejectsToken()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"success":false,"data":{"error":"invalid_token"}}""", Encoding.UTF8, "application/json")
        });

        var service = new TapTapAuthService(
            new HttpClient(handler),
            Options.Create(new TapTapOptions { ClientId = "demo_client_id", ClientSecret = "demo_secret" }),
            NullLogger<TapTapAuthService>.Instance);

        var profile = await service.GetUserProfileAsync(new TapTapAccessToken("forged_kid", "mac", "wrong_key", "hmac-sha-1"));

        Assert.Null(profile);
    }

    [Fact]
    public void TryResolveAllowedRedirectUri_EnforcesWhitelist()
    {
        var service = new TapTapAuthService(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new TapTapOptions
            {
                ClientId = "demo_client_id",
                ClientSecret = "demo_secret",
                RedirectUri = "https://daohua.yuohira.com/api/v1/auth/taptap/callback",
                AllowedRedirectUris = ["http://daohua.localhost/api/v1/auth/taptap/callback"]
            }),
            NullLogger<TapTapAuthService>.Instance);

        Assert.True(service.TryResolveAllowedRedirectUri(null, out var def));
        Assert.Equal("https://daohua.yuohira.com/api/v1/auth/taptap/callback", def);
        Assert.True(service.TryResolveAllowedRedirectUri("http://daohua.localhost/api/v1/auth/taptap/callback", out var whitelisted));
        Assert.Equal("http://daohua.localhost/api/v1/auth/taptap/callback", whitelisted);

        Assert.False(service.TryResolveAllowedRedirectUri("https://evil.example.com/callback", out _));
        Assert.False(service.TryResolveAllowedRedirectUri("javascript:alert(1)", out _));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
