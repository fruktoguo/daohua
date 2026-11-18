using Protocol;

namespace Server.Clean.Redeem;

/// <summary>兑换码业务异常分类（映射为明确的 HTTP 状态码与契约错误码）。</summary>
public enum RedeemOperationFailure
{
    InvalidCode = 0,
    InvalidArgument = 1,
    PlayerNotFound = 2,
    BotRestricted = 3,
    NotFound = 4,
    Disabled = 5,
    Expired = 6,
    Exhausted = 7,
    CodeAlreadyUsed = 8,
    AlreadyClaimedBatch = 9,
    CodeConflict = 10
}

/// <summary>兑换码业务异常。</summary>
public sealed class RedeemOperationException(RedeemOperationFailure failure, string message) : Exception(message)
{
    public RedeemOperationFailure Failure { get; } = failure;
}

/// <summary>运营后台创建通用口令码请求。</summary>
public sealed record OpsCreateUniversalRedeemRequest(
    string Code,
    string Title,
    IReadOnlyList<RedeemRewardDto>? Rewards,
    int? MaxClaims,
    int? ExpireDays,
    bool NeverExpire = false);

/// <summary>运营后台批量生成单次兑换码请求。</summary>
public sealed record OpsCreateSingleUseRedeemRequest(
    int Count,
    string? Prefix,
    string Title,
    IReadOnlyList<RedeemRewardDto>? Rewards,
    int? ExpireDays,
    bool NeverExpire = false);

/// <summary>运营后台停用或恢复兑换码批次请求。</summary>
public sealed record OpsSetRedeemBatchDisabledRequest(
    long BatchId,
    bool Disabled);

/// <summary>运营后台查看的兑换码批次摘要记录。</summary>
public sealed record OpsRedeemBatchDto(
    long BatchId,
    string Title,
    string Mode,
    string? UniversalCode,
    IReadOnlyList<RedeemRewardDto> Rewards,
    int? MaxClaims,
    int ClaimedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? DisabledAt,
    bool IsActive);

/// <summary>运营后台查看/导出的单条兑换码明细。</summary>
public sealed record OpsRedeemCodeItemDto(
    string Code,
    Guid? RedeemedBy,
    string? RedeemedByName,
    DateTimeOffset? RedeemedAt);

/// <summary>运营后台创建兑换码批次响应（单次码模式下返回全部新生成的兑换码供复制/导出）。</summary>
public sealed record OpsCreateRedeemBatchResponse(
    OpsRedeemBatchDto Batch,
    IReadOnlyList<string> Codes);
