namespace Protocol;

/// <summary>宗门成员三态存在感标识常量，由服务端按权威最大离线挂机时长裁定下发。</summary>
public static class ZongmenMemberPresenceStates
{
    /// <summary>当前实时在线。</summary>
    public const string Online = "online";
    /// <summary>已断线但仍在最大离线挂机时长内（挂机中）。</summary>
    public const string Idle = "idle";
    /// <summary>挂机已超时（彻底离线）。</summary>
    public const string Offline = "offline";
}

/// <summary>宗门成员公开资料；职位顺序为宗主至杂役，0 为宗主。</summary>
/// <param name="Online">当前是否实时在线。</param>
/// <param name="PresenceStatus">三态存在感：<c>online</c>（在线）、<c>idle</c>（挂机中）、<c>offline</c>（挂机超时已离线）。</param>
/// <param name="OfflineDurationSeconds">已离线时长（秒）；仅在挂机超时（<c>offline</c>）时下发，自实际断线时刻起算。</param>
public sealed record ZongmenMemberDto(
    string PlayerId,
    string DisplayName,
    int Role,
    bool Online,
    string PresenceStatus = ZongmenMemberPresenceStates.Offline,
    long? OfflineDurationSeconds = null);
/// <summary>宗门申请或邀请，由有权处理该记录的角色读取。</summary>
public sealed record ZongmenRequestDto(string ZongmenId, string ZongmenName, string PlayerId, string DisplayName, string Kind);
/// <summary>宗门名录摘要，携带挂靠地点以支持就近浏览与拜帖递交，不下发其他宗门的私有驻地身份或内部坐标。</summary>
public sealed record ZongmenSummaryDto(string ZongmenId, string Name, string Prefix, double MemberCount, double Capacity, string AnchorNodeId);
/// <summary>自身宗门资料与唯一私有节点的地图挂靠信息。</summary>
public sealed record ZongmenDto(string ZongmenId, string Name, string Prefix, string LeaderId, int MyRole,
    string AnchorNodeId, string NodeId, int HallLevel, double Capacity,
    IReadOnlyList<ZongmenMemberDto> Members, IReadOnlyList<ZongmenRequestDto> Requests);
/// <summary>宗门界面读取结果；自身资料、可加入组织名录、收到的邀请与本人待批拜帖分域提供。</summary>
public sealed record ZongmenStateDto(ZongmenDto? Zongmen, IReadOnlyList<ZongmenSummaryDto> Directory,
    IReadOnlyList<ZongmenRequestDto> Invitations, IReadOnlyList<ZongmenRequestDto> Applications);
