using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Server.Clean.Ops;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 本地运维面板回归门禁：配置校验与路径规范化、一次性令牌生成与保密、
/// 日志环形缓冲边界与过滤、私网来源判定（面板写操作仅允许回环与私有网段）。
/// </summary>
public sealed class OpsPanelTests
{
    private static OpsOptions Options(params (string Key, string Value)[] entries) =>
        OpsOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build());

    /// <summary>未启用时返回禁用默认值，且不因非法取值阻断启动。</summary>
    [Fact]
    public void Disabled_ReturnsDefaults_WithoutStrictValidation()
    {
        var options = Options(("Ops:Enabled", "false"), ("Ops:LogBufferCapacity", "1"));
        Assert.False(options.Enabled);
        Assert.Equal("/ops", options.Path);
        Assert.Equal(string.Empty, options.Token);
    }

    /// <summary>启用时绑定并规范化路径、缓冲容量与令牌。</summary>
    [Fact]
    public void Enabled_BindsAndNormalizesValues()
    {
        var options = Options(
            ("Ops:Enabled", "true"),
            ("Ops:Path", "ops/"),
            ("Ops:LogBufferCapacity", "800"),
            ("Ops:Token", "local-token"));
        Assert.True(options.Enabled);
        Assert.Equal("/ops", options.Path);
        Assert.Equal(800, options.LogBufferCapacity);
        Assert.Equal("local-token", options.Token);
    }

    /// <summary>未配置令牌时进程内生成一次性令牌，且不随 ToString 泄露。</summary>
    [Fact]
    public void Enabled_WithoutToken_GeneratesOneTimeToken()
    {
        var options = Options(("Ops:Enabled", "true"));
        Assert.True(options.Enabled);
        Assert.Equal(32, options.Token.Length);
        Assert.DoesNotContain(options.Token, options.ToString());
    }

    /// <summary>启用时越界容量快速失败。</summary>
    [Fact]
    public void Enabled_InvalidCapacity_Throws()
    {
        var entries = new List<(string, string)> { ("Ops:Enabled", "true"), ("Ops:LogBufferCapacity", "10") };
        Assert.Throws<InvalidOperationException>(() => Options([.. entries]));
    }

    /// <summary>启用时超长令牌快速失败。</summary>
    [Fact]
    public void Enabled_TooLongToken_Throws()
    {
        var entries = new List<(string, string)> { ("Ops:Enabled", "true"), ("Ops:Token", new string('a', 513)) };
        Assert.Throws<InvalidOperationException>(() => Options([.. entries]));
    }

    /// <summary>日志缓冲按容量截断，并保留最新条目。</summary>
    [Fact]
    public void LogBuffer_BoundsCapacity_AndKeepsNewest()
    {
        using var buffer = new OpsLogBuffer(3);
        var logger = buffer.CreateLogger("test.category");
        for (var i = 1; i <= 5; i++) logger.LogInformation("message-{Index}", i);

        var entries = buffer.Snapshot(LogLevel.Information, 100);
        Assert.Equal(3, entries.Count);
        Assert.Equal("message-5", entries[0].Message);
        Assert.Equal("message-3", entries[^1].Message);
        Assert.Equal("test.category", entries[0].Category);
    }

    /// <summary>日志缓冲按最低级别过滤并保留异常全文。</summary>
    [Fact]
    public void LogBuffer_FiltersByLevel_AndCapturesException()
    {
        using var buffer = new OpsLogBuffer(50);
        var logger = buffer.CreateLogger("test.level");
        logger.LogInformation("info");
        logger.LogWarning("warn");
        logger.LogError(new InvalidOperationException("boom"), "error");

        var warnings = buffer.Snapshot(LogLevel.Warning, 100);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, entry => Assert.True(entry.Level is "Warning" or "Error"));
        Assert.Contains(warnings, entry => entry.Exception is not null && entry.Exception.Contains("boom"));
        Assert.Single(buffer.Snapshot(LogLevel.Error, 100));
    }

    /// <summary>日志缓冲支持类别子串与正文/异常关键词过滤（大小写不敏感）。</summary>
    [Fact]
    public void LogBuffer_FiltersByCategoryAndQuery()
    {
        using var buffer = new OpsLogBuffer(50);
        var market = buffer.CreateLogger("Server.Clean.MarketRuntime");
        var player = buffer.CreateLogger("Server.Clean.PlayerRuntime");
        market.LogInformation("订单撮合完成");
        player.LogWarning("玩家加载失败");
        player.LogError(new InvalidOperationException("boom-marker"), "异常");

        Assert.Single(buffer.Snapshot(LogLevel.Information, 100, "marketruntime"));
        Assert.Single(buffer.Snapshot(LogLevel.Information, 100, null, "撮合"));
        Assert.Single(buffer.Snapshot(LogLevel.Information, 100, null, "BOOM-MARKER"));
        Assert.Empty(buffer.Snapshot(LogLevel.Information, 100, "Missing"));
    }

    /// <summary>释放后不再写入。</summary>
    [Fact]
    public void LogBuffer_AfterDispose_IgnoresWrites()
    {
        var buffer = new OpsLogBuffer(10);
        var logger = buffer.CreateLogger("test.dispose");
        logger.LogInformation("before");
        buffer.Dispose();
        logger.LogInformation("after");
        Assert.Single(buffer.Snapshot(LogLevel.Information, 100));
    }

    /// <summary>回环、RFC1918、链路本地与 IPv6 唯一本地地址视为可访问来源。</summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.82", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Network_ClassifiesPrivateAndPublic(string address, bool expected)
    {
        Assert.Equal(expected, OpsNetwork.IsPrivateOrLoopback(IPAddress.Parse(address)));
    }

    /// <summary>未知来源（null）一律拒绝。</summary>
    [Fact]
    public void Network_NullAddress_IsRejected() => Assert.False(OpsNetwork.IsPrivateOrLoopback(null));
}
