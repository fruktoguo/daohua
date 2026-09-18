using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Server.Clean.Ops;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 本地运维面板回归门禁：配置校验与路径规范化、日志环形缓冲边界与级别过滤、
/// 私网来源判定（面板仅允许回环与私有网段访问）。
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
        var options = Options(("Ops:Enabled", "false"), ("Ops:Port", "0"));
        Assert.False(options.Enabled);
        Assert.Equal(OpsOptions.DefaultPort, options.Port);
        Assert.Equal("/ops", options.Path);
    }

    /// <summary>启用时绑定并规范化端口、监听地址、路径与缓冲容量。</summary>
    [Fact]
    public void Enabled_BindsAndNormalizesValues()
    {
        var options = Options(
            ("Ops:Enabled", "true"),
            ("Ops:Port", "5200"),
            ("Ops:ListenAddress", "0.0.0.0"),
            ("Ops:Path", "ops/"),
            ("Ops:LogBufferCapacity", "800"));
        Assert.True(options.Enabled);
        Assert.Equal(5200, options.Port);
        Assert.Equal("0.0.0.0", options.ListenAddress);
        Assert.Equal("/ops", options.Path);
        Assert.Equal(800, options.LogBufferCapacity);
        Assert.Equal("http://0.0.0.0:5200", options.Url);
    }

    /// <summary>启用时非法端口、非法监听地址、越界容量均快速失败。</summary>
    [Theory]
    [InlineData("Ops:Port", "70000")]
    [InlineData("Ops:ListenAddress", "not-an-ip")]
    [InlineData("Ops:LogBufferCapacity", "10")]
    public void Enabled_InvalidValue_Throws(string key, string value)
    {
        var entries = new List<(string, string)> { ("Ops:Enabled", "true"), (key, value) };
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
