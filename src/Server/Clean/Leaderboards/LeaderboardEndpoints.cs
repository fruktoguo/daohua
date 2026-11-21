using System.Security.Claims;
using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Leaderboards;

/// <summary>
/// 全服排行榜只读 HTTP 端点映射：
/// GET /api/v1/leaderboards（总览目录）与 GET /api/v1/leaderboards/{boardId}（单榜 Top 50 + 本人名次）。
/// 统一要求已鉴权 Cookie 会话，支持强弱 ETag 304 协商与私有缓存防串号隔离。
/// </summary>
public static class LeaderboardEndpoints
{
    /// <summary>将排行榜只读 HTTP 路由注册到 ASP.NET Core 端点管线。</summary>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/leaderboards").RequireAuthorization().RequireRateLimiting("command");

        group.MapGet("/", (HttpContext context, LeaderboardRuntime runtime) =>
        {
            var result = runtime.TryGetIndex(context.Request.Headers.IfNoneMatch);
            if (result is null)
                return NotReadyResponse(context);

            return WritePayload(context, result.Value);
        });

        group.MapGet("/{boardId}", (string boardId, ClaimsPrincipal user, HttpContext context, LeaderboardRuntime runtime) =>
        {
            if (!LeaderboardAssembler.IsKnownBoard(boardId))
            {
                return Results.NotFound(new
                {
                    status = 404,
                    title = "LEADERBOARD_NOT_FOUND",
                    detail = "指定的全服排行榜不存在。"
                });
            }

            Guid? playerId = Guid.TryParse(user.FindFirstValue("player_id"), out var parsed) ? parsed : null;
            var result = runtime.TryGetBoard(boardId, playerId, context.Request.Headers.IfNoneMatch);
            if (result is null)
                return NotReadyResponse(context);

            return WritePayload(context, result.Value);
        });
    }

    private static IResult NotReadyResponse(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "5";
        return Results.Json(
            new
            {
                status = 503,
                title = "LEADERBOARD_NOT_READY",
                detail = "天榜正在凝练本期整点名录，请稍候片刻重试。"
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult WritePayload(HttpContext context, LeaderboardHttpPayload payload)
    {
        context.Response.Headers.ETag = payload.ETag;
        context.Response.Headers.CacheControl = "private, max-age=60";
        context.Response.Headers.Vary = "Cookie";

        if (payload.NotModified)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Bytes(payload.JsonUtf8, "application/json; charset=utf-8");
    }
}
