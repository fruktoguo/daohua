namespace Server.Clean.AntiMultiOpen;

/// <summary>
/// 防多开子系统统一配置。三条防线各自独立开关，可单独启用或停用：
/// 1. TabGuard：前端同浏览器多标签页互斥（开关经 HelloAck 下发给客户端）；
/// 2. SingleSessionKick：同一账号后登录连接顶替并踢出旧连接（关闭时退回「拒绝新连接」语义）；
/// 3. DeviceQuota：同一设备指纹允许同时在线的账号数上限。
/// </summary>
/// <param name="TabGuardEnabled">是否启用前端多标签页互斥守卫</param>
/// <param name="SingleSessionKickEnabled">是否启用单账号互踢；false 时第二个连接被拒绝</param>
/// <param name="DeviceQuotaEnabled">是否启用设备指纹并发账号配额</param>
/// <param name="DeviceQuotaMaxAccounts">同一设备指纹允许同时在线的最大账号数</param>
public sealed record AntiMultiOpenOptions(
    bool TabGuardEnabled,
    bool SingleSessionKickEnabled,
    bool DeviceQuotaEnabled,
    int DeviceQuotaMaxAccounts)
{
    /// <summary>默认策略：标签守卫与互踢启用，设备配额默认关闭（指纹为软对抗手段，按需开启）</summary>
    public static AntiMultiOpenOptions Default { get; } = new(
        TabGuardEnabled: true,
        SingleSessionKickEnabled: true,
        DeviceQuotaEnabled: false,
        DeviceQuotaMaxAccounts: 2);

    /// <summary>
    /// 从 IConfiguration 绑定并校验防多开配置；非法取值在启动阶段快速失败。
    /// </summary>
    public static AntiMultiOpenOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var tabGuard = configuration.GetValue("AntiMultiOpen:TabGuardEnabled", Default.TabGuardEnabled);
        var kick = configuration.GetValue("AntiMultiOpen:SingleSessionKickEnabled", Default.SingleSessionKickEnabled);
        var quotaEnabled = configuration.GetValue("AntiMultiOpen:DeviceQuotaEnabled", Default.DeviceQuotaEnabled);
        var quotaMax = configuration.GetValue("AntiMultiOpen:DeviceQuotaMaxAccounts", Default.DeviceQuotaMaxAccounts);
        if (quotaMax is < 1 or > 64) throw new InvalidOperationException("AntiMultiOpen:DeviceQuotaMaxAccounts 必须在 1..64。");
        return new AntiMultiOpenOptions(tabGuard, kick, quotaEnabled, quotaMax);
    }
}
