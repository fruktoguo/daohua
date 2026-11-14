using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

public sealed class ChatImageUploadRegistryTests
{
    private readonly ChatImageUploadRegistry _registry;
    private readonly string _secret = "test-secret-key-123456";

    public ChatImageUploadRegistryTests()
    {
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["Chat:ImageSecret"] = _secret,
            ["Chat:ImageUploadEndpoint"] = "https://img.yuohira.com/upload",
            ["Chat:ImageBaseUrl"] = "https://img.yuohira.com"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
        _registry = new ChatImageUploadRegistry(configuration);
    }

    [Fact]
    public void TryAcquireUploadTicket_FirstUpload_ReturnsTicketAndRemainingQuota()
    {
        var accountId = Guid.NewGuid();
        var sha256 = new string('a', 64);

        var success = _registry.TryAcquireUploadTicket(accountId, sha256, "png", out var result, out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.False(result.Cached);
        Assert.NotNull(result.Ticket);
        Assert.Equal(19, result.RemainingToday);
        Assert.Contains(sha256[..16], result.Url);
    }

    [Fact]
    public void TryAcquireUploadTicket_SameHashDuplicate_ReturnsCachedUrlWithoutConsumingQuota()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var sha256 = new string('b', 64);

        // 账号 A 首次上传并用服务端签发的 Ticket 完成 Confirm 登记
        var successA = _registry.TryAcquireUploadTicket(accountA, sha256, "gif", out var resultA, out var errA);
        Assert.True(successA);
        Assert.False(resultA.Cached);
        Assert.Equal(19, resultA.RemainingToday);
        Assert.NotNull(resultA.Ticket);
        var confirmOk = _registry.RegisterUploadedUrl(accountA, sha256, resultA.Url, resultA.Ticket!, out var confirmErr);
        Assert.True(confirmOk);
        Assert.Null(confirmErr);

        // 账号 B 上传相同哈希的图片（全服去重复用）
        var successB = _registry.TryAcquireUploadTicket(accountB, sha256, "gif", out var resultB, out var errB);
        Assert.True(successB);
        Assert.Null(errB);
        Assert.True(resultB.Cached);
        Assert.Equal(resultA.Url, resultB.Url); // 复用完全相同的直链
        Assert.Null(resultB.Ticket); // 命中缓存无需再向 Worker 上传
        Assert.Equal(20, resultB.RemainingToday); // 账号 B 未消耗额度（剩余仍为 20）
    }

    [Fact]
    public void TryAcquireUploadTicket_Exceeds20PerDay_RejectsWithLimitMessage()
    {
        var accountId = Guid.NewGuid();

        // 消耗完 20 张配额
        for (var i = 0; i < 20; i++)
        {
            var hash = $"{i:D4}".PadLeft(64, '0');
            var ok = _registry.TryAcquireUploadTicket(accountId, hash, "png", out var r, out var err);
            Assert.True(ok);
            Assert.False(r.Cached);
            Assert.Equal(19 - i, r.RemainingToday);
        }

        // 第 21 张必须拦截
        var hash21 = "9999".PadLeft(64, '0');
        var success21 = _registry.TryAcquireUploadTicket(accountId, hash21, "png", out var r21, out var err21);
        Assert.False(success21);
        Assert.NotNull(err21);
        Assert.Contains("20", err21);
    }

    [Fact]
    public void TryAcquireUploadTicket_TicketHmacSignatureIsValid()
    {
        var accountId = Guid.NewGuid();
        var sha256 = "c0ffee".PadRight(64, '0');

        var success = _registry.TryAcquireUploadTicket(accountId, sha256, "webp", out var result, out _);
        Assert.True(success);
        Assert.NotNull(result.Ticket);

        var parts = result.Ticket.Split('.');
        Assert.Equal(2, parts.Length);

        var payloadB64 = parts[0];
        var sigB64 = parts[1];

        // 校验签名
        var expectedSig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(payloadB64));
        var expectedSigB64 = Convert.ToBase64String(expectedSig).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        Assert.Equal(expectedSigB64, sigB64);

        // 校验载荷内容
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(payloadB64)));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        Assert.Equal(accountId.ToString("N"), root.GetProperty("acc").GetString());
        Assert.Equal(sha256, root.GetProperty("hash").GetString());
        Assert.Equal("webp", root.GetProperty("ext").GetString());
        Assert.True(root.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ProductionEnvironment_RequiresStrongConfiguredImageSecret()
    {
        var prodEnv = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var emptyConfig = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new ChatImageUploadRegistry(emptyConfig, prodEnv));

        var weakConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:ImageSecret"] = "short-weak-secret" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ChatImageUploadRegistry(weakConfig, prodEnv));

        var strongConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:ImageSecret"] = "0123456789abcdef0123456789abcdef" })
            .Build();
        var registry = new ChatImageUploadRegistry(strongConfig, prodEnv);
        var ok = registry.TryAcquireUploadTicket(Guid.NewGuid(), new string('e', 64), "png", out var result, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(result.Ticket);
    }

    [Fact]
    public void NonProductionEnvironment_GeneratesEphemeralSecretWhenUnconfigured()
    {
        var devEnv = new FakeHostEnvironment { EnvironmentName = Environments.Development };
        var emptyConfig = new ConfigurationBuilder().Build();

        var registry = new ChatImageUploadRegistry(emptyConfig, devEnv);
        var ok = registry.TryAcquireUploadTicket(Guid.NewGuid(), new string('f', 64), "webp", out var result, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(result.Ticket);
    }

    [Fact]
    public void RegisterUploadedUrl_EnforcesTicketSignatureOwnershipHashExpiryAndSingleUse()
    {
        var now = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        var registry = CreateControlledRegistry(() => now);
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var hash = new string('1', 64);

        Assert.True(registry.TryAcquireUploadTicket(owner, hash, "webp", out var ticketResult, out _));
        var ticket = ticketResult.Ticket!;

        // 1. 跨账号冒名提交必须被拒
        Assert.False(registry.RegisterUploadedUrl(attacker, hash, ticketResult.Url, ticket, out var crossAccErr));
        Assert.NotNull(crossAccErr);

        // 2. 篡改 Ticket 签名必须被拒（含有效字节篡改、末尾非规范填充位异变与 '=' 延展）
        var dotIdx = ticket.IndexOf('.');
        var sigFlippedChar = ticket[dotIdx + 1] == 'A' ? 'B' : 'A';
        var sigFlippedTicket = ticket[..(dotIdx + 1)] + sigFlippedChar + ticket[(dotIdx + 2)..];
        Assert.False(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, sigFlippedTicket, out _));

        var tamperedTicket = ticket[..^1] + (ticket[^1] == 'A' ? 'B' : 'A');
        Assert.False(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, tamperedTicket, out _));

        const string base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var tailIdx = base64UrlAlphabet.IndexOf(ticket[^1]);
        var paddingFlippedTicket = ticket[..^1] + base64UrlAlphabet[tailIdx ^ 1];
        Assert.False(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, paddingFlippedTicket, out _));
        Assert.False(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, ticket + "=", out _));

        // 3. 绑定哈希不一致必须被拒
        Assert.False(registry.RegisterUploadedUrl(owner, new string('2', 64), ticketResult.Url, ticket, out _));

        // 4. 首次合法核销必须成功
        Assert.True(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, ticket, out var okErr));
        Assert.Null(okErr);

        // 5. 同一 Ticket 重复确认必须被拒（一次性核销）
        Assert.False(registry.RegisterUploadedUrl(owner, hash, ticketResult.Url, ticket, out var replayErr));
        Assert.NotNull(replayErr);

        // 6. 过期 Ticket 必须被拒
        var hashExpired = new string('3', 64);
        Assert.True(registry.TryAcquireUploadTicket(owner, hashExpired, "png", out var expiredTicket, out _));
        now = now.AddMinutes(10);
        Assert.False(registry.RegisterUploadedUrl(owner, hashExpired, expiredTicket.Url, expiredTicket.Ticket!, out var expiredErr));
        Assert.NotNull(expiredErr);
    }

    [Theory]
    [InlineData("http://img.yuohira.com/{h16}.png")]
    [InlineData("https://1.2.3.4/test.png")]
    [InlineData("https://user:pass@beeimg.cn/test.png")]
    [InlineData("https://evil.example.com/test.png")]
    [InlineData("https://img.yuohira.com/otherpath.png")]
    [InlineData("https://img.yuohira.com/{h16}.png?probe=1")]
    [InlineData("https://beeimg.cn/not-an-image.html")]
    public void RegisterUploadedUrl_RejectsUntrustedSchemesHostsAndSpoofedPaths(string rawPattern)
    {
        var registry = CreateControlledRegistry(() => new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var accountId = Guid.NewGuid();
        var hash = new string('4', 64);
        Assert.True(registry.TryAcquireUploadTicket(accountId, hash, "png", out var ticket, out _));

        var candidateUrl = rawPattern.Replace("{h16}", hash[..16], StringComparison.Ordinal);
        Assert.False(registry.RegisterUploadedUrl(accountId, hash, candidateUrl, ticket.Ticket!, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void RegisterUploadedUrl_AllowsWhitelistedHostAndEnforcesBoundedCacheAndDailyCleanup()
    {
        var now = new DateTimeOffset(2026, 11, 14, 23, 55, 0, TimeSpan.Zero);
        var registry = CreateControlledRegistry(() => now, maxCachedHashes: 3);

        var accounts = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var hash = $"{i:D4}".PadLeft(64, '5');
            Assert.True(registry.TryAcquireUploadTicket(accounts[i], hash, "png", out var res, out _));
            var url = i == 0 ? $"https://i.beeimg.cn/{hash[..16]}.png" : res.Url;
            Assert.True(registry.RegisterUploadedUrl(accounts[i], hash, url, res.Ticket!, out var err));
            Assert.Null(err);
        }

        // 缓存上限 3 条：最老的第 0 条被逐出，第 1~3 条保留
        Assert.Equal(3, registry.CachedHashCount);
        Assert.True(registry.TryAcquireUploadTicket(Guid.NewGuid(), $"{0:D4}".PadLeft(64, '5'), "png", out var evictedRes, out _));
        Assert.False(evictedRes.Cached);
        Assert.True(registry.TryAcquireUploadTicket(Guid.NewGuid(), $"{3:D4}".PadLeft(64, '5'), "png", out var retainedRes, out _));
        Assert.True(retainedRes.Cached);

        // 跨 UTC 零点后再次请求应清理前一日过期计数
        now = now.AddMinutes(10);
        Assert.True(registry.TryAcquireUploadTicket(Guid.NewGuid(), new string('7', 64), "webp", out _, out _));
        Assert.Equal(1, registry.DailyCountEntryCount);
    }

    private ChatImageUploadRegistry CreateControlledRegistry(Func<DateTimeOffset> utcNow, int maxCachedHashes = ChatImageUploadRegistry.MaxCachedHashes)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ImageSecret"] = _secret,
                ["Chat:ImageUploadEndpoint"] = "https://img.yuohira.com/upload",
                ["Chat:ImageBaseUrl"] = "https://img.yuohira.com"
            })
            .Build();
        return new ChatImageUploadRegistry(config, new FakeHostEnvironment(), utcNow, maxCachedHashes);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Server.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string PadBase64(string b64)
    {
        var s = b64.Replace("-", "+").Replace("_", "/");
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
