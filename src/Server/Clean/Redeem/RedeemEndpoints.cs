using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Protocol;

namespace Server.Clean.Redeem;

/// <summary>
/// 玩家兑换码 REST API 路由映射（POST /api/v1/redeem）：
/// 强制要求已鉴权 Cookie 会话与独立 "redeem" 防穷举限流，严格从 <see cref="ClaimsPrincipal"/> 解析 player_id。
/// </summary>
public static class RedeemEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/redeem").RequireAuthorization();

        group.MapPost("/", async (
            RedeemCodeRequestDto? request,
            ClaimsPrincipal user,
            RedeemRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId))
                return Results.Unauthorized();

            try
            {
                var result = await runtime.RedeemAsync(playerId, request?.Code, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (RedeemOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("redeem");
    }

    private static bool TryGetPlayerId(ClaimsPrincipal user, out Guid playerId)
        => Guid.TryParse(user.FindFirstValue("player_id"), out playerId) && playerId != Guid.Empty;

    private static IResult MapFailure(RedeemOperationException ex)
    {
        var (status, title) = ex.Failure switch
        {
            RedeemOperationFailure.InvalidCode => (StatusCodes.Status400BadRequest, "REDEEM_INVALID_CODE"),
            RedeemOperationFailure.InvalidArgument => (StatusCodes.Status400BadRequest, "REDEEM_INVALID_ARGUMENT"),
            RedeemOperationFailure.PlayerNotFound => (StatusCodes.Status404NotFound, "PLAYER_NOT_FOUND"),
            RedeemOperationFailure.BotRestricted => (StatusCodes.Status403Forbidden, "REDEEM_BOT_RESTRICTED"),
            RedeemOperationFailure.NotFound => (StatusCodes.Status404NotFound, "REDEEM_NOT_FOUND"),
            RedeemOperationFailure.Disabled => (StatusCodes.Status409Conflict, "REDEEM_DISABLED"),
            RedeemOperationFailure.Expired => (StatusCodes.Status409Conflict, "REDEEM_EXPIRED"),
            RedeemOperationFailure.Exhausted => (StatusCodes.Status409Conflict, "REDEEM_EXHAUSTED"),
            RedeemOperationFailure.CodeAlreadyUsed => (StatusCodes.Status409Conflict, "REDEEM_CODE_USED"),
            RedeemOperationFailure.AlreadyClaimedBatch => (StatusCodes.Status409Conflict, "REDEEM_ALREADY_CLAIMED"),
            RedeemOperationFailure.CodeConflict => (StatusCodes.Status409Conflict, "REDEEM_CODE_CONFLICT"),
            _ => (StatusCodes.Status400BadRequest, "REDEEM_ERROR")
        };
        return Results.Problem(title: title, detail: ex.Message, statusCode: status);
    }
}
