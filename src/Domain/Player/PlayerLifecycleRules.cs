namespace Domain.Player;

/// <summary>
/// 玩家三态生命周期规则常量：离线挂机与彻底离线的共同边界。
/// 规则常量不可配置——离线收益上限与生命周期判定必须全服一致。
/// </summary>
public static class PlayerLifecycleRules
{
    /// <summary>离线收益上限：自离线锚点起最多结算 48 小时挂机收益；同时也是彻底离线判定边界。</summary>
    public static readonly TimeSpan OfflineIdleLimit = TimeSpan.FromHours(48);
}
