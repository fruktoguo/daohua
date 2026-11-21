using System.Security.Claims;
using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Checkin;

/// <summary>
/// 每日签到 REST API 路由映射（/api/v1/checkin/*）：
/// 统一要求已鉴权 Cookie 会话，严格从 <see cref="ClaimsPrincipal"/> 解析 player_id。
/// </summary>
public static class CheckinEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/checkin").RequireAuthorization().RequireRateLimiting("command");

        group.MapGet("/", async (ClaimsPrincipal user, CheckinRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.GetStatusAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (CheckinOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/claim", async (ClaimsPrincipal user, CheckinRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.ClaimAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (CheckinOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("command");
    }

    private static bool TryGetPlayerId(ClaimsPrincipal user, out Guid playerId)
        => Guid.TryParse(user.FindFirstValue("player_id"), out playerId) && playerId != Guid.Empty;

    private static IResult MapFailure(CheckinOperationException ex)
    {
        var (status, title) = ex.Failure switch
        {
            CheckinOperationFailure.BotRestricted => (StatusCodes.Status403Forbidden, "CHECKIN_BOT_RESTRICTED"),
            _ => (StatusCodes.Status404NotFound, "PLAYER_NOT_FOUND")
        };
        return Results.Problem(title: title, detail: ex.Message, statusCode: status);
    }
}
