using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Domain.World;

/// <summary>宗门组织的纯规则；驻地使用普通节点身份，建筑仍由节点建筑规则结算。</summary>
public static class ZongmenRules
{
    public const string TokenItemId = "item.zongmen-lingpai";
    public const string HallBuildingId = "building.zongmen-dadian";
    public const string ResidencePrefix = "node.zongmen.";
    public const double InitialCapacity = 20;
    public const double CapacityPerLevel = 5;
    public static readonly string[] RoleNames = ["宗主", "副宗主", "太上长老", "长老", "内门弟子", "外门弟子", "杂役"];

    /// <summary>名称按统一规范化口径裁决唯一性，避免宽窄字符和首尾空白绕过重名约束。</summary>
    public static string NormalizeName(string name)
    {
        var normalized = name.Trim().Normalize(NormalizationForm.FormKC);
        var count = new StringInfo(normalized).LengthInTextElements;
        if (count is < 2 or > 16 || normalized.Any(char.IsControl))
            throw new DomainException(DomainErrorCode.InvalidArgument, "宗门名称须为2至16字。");
        return normalized;
    }

    /// <summary>所属前缀必须为单个汉字，不承担宗门全名的唯一标识职责。</summary>
    public static string NormalizePrefix(string prefix)
    {
        var value = prefix.Trim().Normalize(NormalizationForm.FormKC);
        if (!Regex.IsMatch(value, @"\A[\p{IsCJKUnifiedIdeographs}]\z", RegexOptions.CultureInvariant))
            throw new DomainException(DomainErrorCode.InvalidArgument, "宗门前缀须为一个汉字。");
        return value;
    }

    /// <summary>个人地图与角色位置采用同一驻地偏移，避免驻地标记覆盖公共入口。</summary>
    public static (double X, double Y) ResidenceCoordinates(double anchorX, double anchorY)
        => (Math.Min(95, anchorX + 6), Math.Min(95, anchorY + 6));

    public static string ResidenceNodeId(Guid id) => ResidencePrefix + id.ToString("N");
    public static bool TryResidenceId(string? nodeId, out Guid id)
    {
        id = default;
        return nodeId is not null && nodeId.StartsWith(ResidencePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(nodeId[ResidencePrefix.Length..], "N", out id);
    }

    public static double MemberCapacity(int hallLevel) => InitialCapacity + CapacityPerLevel * Math.Max(0, hallLevel - 1);
    public static bool CanManage(int role) => role is >= 0 and <= 3;
    public static bool CanManageMember(int actorRole, int targetRole) => CanManage(actorRole) && actorRole < targetRole;
    public static string DisplayName(string name, string? prefix) => string.IsNullOrEmpty(prefix) ? name : $"[{prefix}] {name}";
}
