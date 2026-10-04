namespace Server.Clean.AntiMultiOpen;

/// <summary>
/// 从账号检测动态策略派生的单次准入快照。
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
    int DeviceQuotaMaxAccounts);
