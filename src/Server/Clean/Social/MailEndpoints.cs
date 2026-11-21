using System.Security.Claims;
using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Social;

/// <summary>
/// 双轨邮件系统 REST API 路由映射（/api/v1/mails/*）：
/// 统一要求已鉴权 Cookie 会话，严禁依赖前端传参替代 player_id。
/// </summary>
public static class MailEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/mails").RequireAuthorization().RequireRateLimiting("command");

        group.MapGet("/", async (ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.ListMailsAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapGet("/{mailKey}", async (string mailKey, ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.GetDetailAndMarkReadAsync(playerId, mailKey, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/{mailKey}/read", async (string mailKey, ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.MarkReadAsync(playerId, mailKey, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/{mailKey}/claim", async (string mailKey, ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.ClaimSingleAsync(playerId, mailKey, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("command");

        group.MapPost("/claim-all", async (ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.ClaimAllAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("command");

        group.MapDelete("/{mailKey}", async (string mailKey, ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.DeleteSingleAsync(playerId, mailKey, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/delete-read", async (ClaimsPrincipal user, MailRuntime runtime, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await runtime.DeleteAllReadAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });
    }

    private static bool TryGetPlayerId(ClaimsPrincipal user, out Guid playerId)
        => Guid.TryParse(user.FindFirstValue("player_id"), out playerId) && playerId != Guid.Empty;

    private static IResult MapFailure(MailOperationException ex)
    {
        var (status, title) = ex.Failure switch
        {
            MailOperationFailure.NotFound => (StatusCodes.Status404NotFound, "MAIL_NOT_FOUND"),
            MailOperationFailure.HasUnclaimedAttachments => (StatusCodes.Status409Conflict, "MAIL_HAS_UNCLAIMED_ATTACHMENTS"),
            MailOperationFailure.BotRestricted => (StatusCodes.Status403Forbidden, "MAIL_BOT_RESTRICTED"),
            _ => (StatusCodes.Status400BadRequest, "MAIL_INVALID_ARGUMENT")
        };
        return Results.Json(new { status, title, detail = ex.Message }, statusCode: status);
    }
}
