using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 道友攻略板块与角色名片配装付费查阅 REST API 路由映射（/api/v1/guides/* 与 /api/v1/players/{targetPlayerId}/unlock-loadout）：
/// 统一要求已鉴权 Cookie 会话，严格从会话 Claims 提取当前操作玩家身份。
/// </summary>
public static class GuideEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/guides")
            .RequireAuthorization()
            .RequireRateLimiting("command");

        group.MapGet("/", async (
            string? sort,
            bool? mine,
            int? limit,
            int? offset,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                var result = await runtime.ListAsync(
                    playerId,
                    sort,
                    mine == true,
                    limit ?? 50,
                    offset ?? 0,
                    ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapGet("/{postId:guid}", async (
            Guid postId,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.GetDetailAsync(playerId, postId, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/", async (
            CreateGuidePostRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.CreateAsync(playerId, request, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPut("/{postId:guid}", async (
            Guid postId,
            UpdateGuidePostRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.UpdateAsync(playerId, postId, request, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapDelete("/{postId:guid}", async (
            Guid postId,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                await runtime.DeleteAsync(playerId, postId, ct).ConfigureAwait(false);
                return Results.Ok(new { deleted = true, postId = postId.ToString("D") });
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/{postId:guid}/like", async (
            Guid postId,
            SetGuideLikeRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.SetLikeAsync(playerId, postId, request.Liked, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/{postId:guid}/unlock", async (
            Guid postId,
            UnlockGuidePostRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.UnlockAsync(playerId, postId, request, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/{postId:guid}/tip", async (
            Guid postId,
            TipGuidePostRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.TipAsync(playerId, postId, request, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        endpoints.MapPost("/api/v1/players/{targetPlayerId:guid}/unlock-loadout", async (
            Guid targetPlayerId,
            UnlockPlayerLoadoutRequest request,
            ClaimsPrincipal user,
            GuideRuntime runtime,
            CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var viewerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.UnlockPlayerLoadoutAsync(viewerId, targetPlayerId, request, ct).ConfigureAwait(false));
            }
            catch (GuideOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireAuthorization().RequireRateLimiting("command");
    }

    private static bool TryGetPlayerId(ClaimsPrincipal user, out Guid playerId)
        => Guid.TryParse(user.FindFirstValue("player_id"), out playerId) && playerId != Guid.Empty;

    private static IResult MapFailure(GuideOperationException ex)
    {
        var (status, code) = ex.Failure switch
        {
            GuideOperationFailure.NotFound => (StatusCodes.Status404NotFound, "GUIDE_NOT_FOUND"),
            GuideOperationFailure.Forbidden => (StatusCodes.Status403Forbidden, "GUIDE_FORBIDDEN"),
            GuideOperationFailure.BotRestricted => (StatusCodes.Status403Forbidden, "GUIDE_BOT_RESTRICTED"),
            GuideOperationFailure.NotRanked => (StatusCodes.Status403Forbidden, "GUIDE_NOT_RANKED"),
            GuideOperationFailure.DailyQuotaExceeded => (StatusCodes.Status429TooManyRequests, "GUIDE_DAILY_QUOTA_EXCEEDED"),
            GuideOperationFailure.InsufficientSpiritStones => (StatusCodes.Status409Conflict, "INSUFFICIENT_SPIRIT_STONES"),
            GuideOperationFailure.PriceChanged => (StatusCodes.Status409Conflict, "PRICE_CHANGED"),
            GuideOperationFailure.Conflict => (StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT"),
            _ => (StatusCodes.Status400BadRequest, "GUIDE_INVALID_ARGUMENT")
        };
        return Results.Json(new { code, message = ex.Message }, statusCode: status);
    }
}
