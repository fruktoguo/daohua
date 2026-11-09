using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

        // 账号 A 首次上传
        var successA = _registry.TryAcquireUploadTicket(accountA, sha256, "gif", out var resultA, out var errA);
        Assert.True(successA);
        Assert.False(resultA.Cached);
        Assert.Equal(19, resultA.RemainingToday);
        _registry.RegisterUploadedUrl(sha256, resultA.Url);

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

    private static string PadBase64(string b64)
    {
        var s = b64.Replace("-", "+").Replace("_", "/");
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
