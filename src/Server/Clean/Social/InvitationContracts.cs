namespace Server.Clean.Social;

/// <summary>
/// 邀请系统常量：奖励数值以 TASK-20261005-05 需求为准（与 daojie-yusheng 参考实现不同）。
/// 里程碑按境界等级判定：练气期=19、筑基期=31（见 expected-attributes.json allRealmNodes 大境界节点）。
/// </summary>
public static class InvitationRewards
{
    /// <summary>绑定功德资产 ID（isTradable=false，仅系统产出）。</summary>
    public const string BoundMeritItemId = "item.gongde-bound";
    /// <summary>灵石资产 ID。</summary>
    public const string SpiritStoneItemId = "currency.spirit-stone";
    /// <summary>注册奖励：被邀请者得绑定功德。</summary>
    public const double SignupInviteeMerit = 1000;
    /// <summary>注册奖励：被邀请者得灵石。</summary>
    public const double SignupInviteeSpiritStones = 10000;
    /// <summary>注册奖励：邀请者得绑定功德。</summary>
    public const double SignupInviterMerit = 1000;
    /// <summary>练气期里程碑境界等级。</summary>
    public const int QiRealmLevel = 19;
    /// <summary>练气期里程碑双方各得绑定功德。</summary>
    public const double QiMilestoneMerit = 1000;
    /// <summary>筑基期里程碑境界等级。</summary>
    public const int FoundationRealmLevel = 31;
    /// <summary>筑基期里程碑双方各得绑定功德。</summary>
    public const double FoundationMilestoneMerit = 2000;

    /// <summary>里程碑键：注册结成。</summary>
    public const string MilestoneSignup = "signup";
    /// <summary>里程碑键：练气期。</summary>
    public const string MilestoneQi = "qi";
    /// <summary>里程碑键：筑基期。</summary>
    public const string MilestoneFoundation = "foundation";

    /// <summary>里程碑键 → 双方各入账绑定功德数量（signup 仅邀请者侧使用）。</summary>
    public static double MeritFor(string milestone) => milestone switch
    {
        MilestoneQi => QiMilestoneMerit,
        MilestoneFoundation => FoundationMilestoneMerit,
        _ => SignupInviterMerit,
    };

    /// <summary>里程碑键 → 境界中文名（练气期/筑基期/注册结成）。</summary>
    public static string RealmLabel(string milestone) => milestone switch
    {
        MilestoneQi => "练气期",
        MilestoneFoundation => "筑基期",
        _ => string.Empty,
    };

    /// <summary>邀请者侧入账日志模板（{{item}} 引用标记见 gameLogService.parseLogTemplate）。</summary>
    public static string InviterRewardLogTemplate(string milestone, string inviteeName) => milestone switch
    {
        MilestoneSignup => $"你的邀请码结成新同道「{inviteeName}」，获得 {{{{item:{BoundMeritItemId}|功德·绑定|{MeritFor(milestone):0}}}}}。",
        _ => $"你邀请的同道「{inviteeName}」突破至{RealmLabel(milestone)}，获得 {{{{item:{BoundMeritItemId}|功德·绑定|{MeritFor(milestone):0}}}}}。",
    };

    /// <summary>被邀请者侧里程碑入账日志模板。</summary>
    public static string InviteeRewardLogTemplate(string milestone) =>
        $"受邀里程碑·{RealmLabel(milestone)}达成，与邀请者各得 {{{{item:{BoundMeritItemId}|功德·绑定|{MeritFor(milestone):0}}}}}。";
}

/// <summary>被邀请人一行进度（邀请者视角）。</summary>
/// <param name="DisplayName">被邀请人道号。</param>
/// <param name="HighestRealmLevel">历史最高境界等级。</param>
/// <param name="QiRewarded">练气期里程碑奖励是否已发放。</param>
/// <param name="FoundationRewarded">筑基期里程碑奖励是否已发放。</param>
/// <param name="CreatedAt">邀请关系建立时间。</param>
public sealed record InvitationInviteeRow(
    string DisplayName,
    int HighestRealmLevel,
    bool QiRewarded,
    bool FoundationRewarded,
    DateTimeOffset CreatedAt);

/// <summary>邀请总览（GET /api/v1/invitation 响应载体的服务端形态）。</summary>
/// <param name="InviteCode">本人专属邀请码。</param>
/// <param name="Invitees">我邀请的玩家列表（按建立时间倒序）。</param>
/// <param name="InvitedByName">邀请我注册的道号；无邀请人时为 null。</param>
public sealed record InvitationOverview(
    string InviteCode,
    IReadOnlyList<InvitationInviteeRow> Invitees,
    string? InvitedByName);

/// <summary>一次里程碑的实际入账结果（供通知与日志构造）。</summary>
/// <param name="Milestone">里程碑键：qi=练气期，foundation=筑基期。</param>
/// <param name="InviterId">邀请者玩家标识。</param>
/// <param name="Merit">双方各自入账的绑定功德数量。</param>
public sealed record InvitationMilestoneGrant(string Milestone, Guid InviterId, double Merit);

/// <summary>邀请关系与里程碑奖励的持久化边界（social.player_invitations + identity.players.invite_code）。</summary>
public interface IInvitationRepository
{
    /// <summary>读取本人邀请总览；玩家不存在时返回 null。</summary>
    ValueTask<InvitationOverview?> GetOverviewAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 突破成功后按当前境界等级发放到期里程碑奖励：Serializable 事务内对邀请行 FOR UPDATE，
    /// 逐里程碑走幂等经济事务入账并翻转领取标记，同事务推进 highest_realm_level；
    /// 重复调用由领取标记 + 幂等键双保险去重，返回本次实际发放的里程碑列表。
    /// </summary>
    ValueTask<IReadOnlyList<InvitationMilestoneGrant>> GrantMilestonesAsync(
        Guid inviteeId, int realmLevel, CancellationToken cancellationToken = default);
}
