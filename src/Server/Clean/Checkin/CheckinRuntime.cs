using Protocol;

namespace Server.Clean.Checkin;

/// <summary>
/// 每日签到核心运行时：
/// - 查询当前签到摘要/状态；
/// - 领取后调用 <see cref="IPlayerRuntime.ReloadWalletAndNotifyAsync"/> 热刷新在线角色 ECS 内存钱包并推送增量快照。
/// </summary>
public sealed class CheckinRuntime(
    PostgresCheckinRepository repository,
    IPlayerRuntime? playerRuntime = null,
    IPlayerSimulationClock? clock = null)
{
    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;

    /// <summary>获取 WS 握手用的轻量签到摘要。</summary>
    public ValueTask<CheckinSummaryDto> GetSummaryAsync(
        Guid playerId,
        bool? knownIsBot = null,
        CancellationToken ct = default)
        => repository.GetSummaryAsync(playerId, Now, knownIsBot, ct);

    /// <summary>查询完整每日签到状态。</summary>
    public ValueTask<CheckinStatusDto> GetStatusAsync(Guid playerId, CancellationToken ct = default)
        => repository.GetStatusAsync(playerId, Now, ct);

    /// <summary>领取今日签到奖励，成功后热刷新在线玩家内存钱包。</summary>
    public async ValueTask<CheckinClaimResponseDto> ClaimAsync(Guid playerId, CancellationToken ct = default)
    {
        var result = await repository.ClaimAsync(playerId, Now, unitRandom: null, ct).ConfigureAwait(false);
        if (result.Claimed && playerRuntime is not null)
            await playerRuntime.ReloadWalletAndNotifyAsync(playerId, ct).ConfigureAwait(false);
        return result;
    }
}
