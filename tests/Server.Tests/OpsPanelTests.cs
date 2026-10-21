using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Server.Clean.Ops;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 本地运维面板回归门禁：配置校验与路径规范化、一次性令牌生成与保密、
/// 日志环形缓冲边界与过滤。
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

    /// <summary>未配置路径与密码时进程内生成随机路径（`/ops-&lt;16位hex&gt;`）与一次性密码，且密码不随 ToString 泄露。</summary>
    [Fact]
    public void Enabled_WithoutPathAndToken_GeneratesRandomPathAndToken()
    {
        var options = Options(("Ops:Enabled", "true"));
        Assert.True(options.Enabled);
        Assert.StartsWith("/ops-", options.Path, StringComparison.Ordinal);
        Assert.Equal(21, options.Path.Length);
        Assert.Equal(32, options.Token.Length);
        Assert.DoesNotContain(options.Token, options.ToString());
    }

    /// <summary>启用时三类越界配置（非法路径前缀 / 越界缓冲容量 / 超长令牌）都必须快速失败，不得挂到玩家路由或用弱口令启动。</summary>
    [Theory]
    [InlineData("Ops:Path", "/admin")]          // 只允许 /ops 前缀，避免占用玩家路由
    [InlineData("Ops:LogBufferCapacity", "10")] // 低于下限会让日志缓冲失去环形语义
    [InlineData("Ops:Token", "LONG_TOKEN")]     // 超过 512 字符直接拒绝
    public void Enabled_InvalidConfiguration_Throws(string key, string value)
    {
        var effective = key == "Ops:Token" ? new string('a', 513) : value;
        var entries = new List<(string, string)> { ("Ops:Enabled", "true"), (key, effective) };
        Assert.Throws<InvalidOperationException>(() => Options([.. entries]));
    }

    /// <summary>路径白名单判定：生产环境仅放行当前激活随机路径，本地开发额外放行 `/ops` 别名，其余 `/ops*` 一律拦截。</summary>
    [Fact]
    public void BlockedOpsPath_AllowsOnlyActivePathAndDevAlias()
    {
        var active = new OpsOptions(true, "/ops-a1b2c3d4e5f60718", 500, "secret");

        Assert.False(OpsPanel.IsBlockedOpsPath("/", active, isDevelopment: false));
        Assert.False(OpsPanel.IsBlockedOpsPath("/api/v1/meta", active, isDevelopment: false));
        Assert.False(OpsPanel.IsBlockedOpsPath("/ops-a1b2c3d4e5f60718", active, isDevelopment: false));
        Assert.False(OpsPanel.IsBlockedOpsPath("/ops-a1b2c3d4e5f60718/api/overview", active, isDevelopment: false));

        Assert.True(OpsPanel.IsBlockedOpsPath("/ops", active, isDevelopment: false));
        Assert.True(OpsPanel.IsBlockedOpsPath("/ops/api/overview", active, isDevelopment: false));
        Assert.True(OpsPanel.IsBlockedOpsPath("/ops-other", active, isDevelopment: false));

        Assert.False(OpsPanel.IsBlockedOpsPath("/ops", active, isDevelopment: true));
        Assert.False(OpsPanel.IsBlockedOpsPath("/ops/api/overview", active, isDevelopment: true));
        Assert.True(OpsPanel.IsBlockedOpsPath("/ops-other", active, isDevelopment: true));

        Assert.True(OpsPanel.IsBlockedOpsPath("/ops-a1b2c3d4e5f60718", OpsOptions.Disabled, isDevelopment: false));
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
}
