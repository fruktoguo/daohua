namespace Protocol;

/// <summary>宗门成员公开资料；职位顺序为宗主至杂役，0 为宗主。</summary>
public sealed record ZongmenMemberDto(string PlayerId, string DisplayName, int Role, bool Online);
/// <summary>宗门申请或邀请，由有权处理该记录的角色读取。</summary>
public sealed record ZongmenRequestDto(string ZongmenId, string ZongmenName, string PlayerId, string DisplayName, string Kind);
/// <summary>宗门名录摘要，不下发其他宗门的驻地身份或坐标。</summary>
public sealed record ZongmenSummaryDto(string ZongmenId, string Name, string Prefix, double MemberCount, double Capacity);
/// <summary>自身宗门资料与唯一私有节点的地图挂靠信息。</summary>
public sealed record ZongmenDto(string ZongmenId, string Name, string Prefix, string LeaderId, int MyRole,
    string AnchorNodeId, string NodeId, int HallLevel, double Capacity,
    IReadOnlyList<ZongmenMemberDto> Members, IReadOnlyList<ZongmenRequestDto> Requests);
/// <summary>宗门界面读取结果；自身资料、可加入组织名录与收到的邀请分域提供。</summary>
public sealed record ZongmenStateDto(ZongmenDto? Zongmen, IReadOnlyList<ZongmenSummaryDto> Directory,
    IReadOnlyList<ZongmenRequestDto> Invitations);
