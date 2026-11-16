using Microsoft.AspNetCore.Routing;
using Server.Clean.Social;

namespace Server.Clean.Ops;

/// <summary>
/// 运维面板邮件运营端点（/ops/api/mails/*、/ops/api/actions/mail-*）。
/// 统一经 context.RequestServices 可选解析 MailRuntime，确保未注册邮件服务的最小宿主测试仍可构建面板路由。
/// </summary>
public static class OpsMailActions
{
    internal static void MapOpsMailActions(this IEndpointRouteBuilder group)
    {
        group.MapGet("/api/mails/broadcasts", async (HttpContext context, CancellationToken ct) =>
        {
            var mail = context.RequestServices.GetService<MailRuntime>();
            if (mail is null)
                return Results.NotFound(new { code = "MAIL_UNAVAILABLE", message = "当前进程角色未启用邮件服务。" });
            var items = await mail.ListOpsBroadcastsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { items });
        });

        group.MapPost("/api/actions/mail-send-player", async (
            HttpContext context,
            OpsSendPlayerMailRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var mail = context.RequestServices.GetService<MailRuntime>();
            if (mail is null)
                return Results.NotFound(new { code = "MAIL_UNAVAILABLE", message = "当前进程角色未启用邮件服务。" });
            try
            {
                var outcome = await mail.SendPlayerMailAsync(request, CancellationToken.None).ConfigureAwait(false);
                OpsPanel.Audit(loggerFactory, context, "mail.send-player", outcome.PlayerId);
                return Results.Ok(new
                {
                    action = "mail-send-player",
                    playerId = outcome.PlayerId,
                    mailId = outcome.MailId,
                    summary = outcome.Summary
                });
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/api/actions/mail-send-broadcast", async (
            HttpContext context,
            OpsSendBroadcastMailRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var mail = context.RequestServices.GetService<MailRuntime>();
            if (mail is null)
                return Results.NotFound(new { code = "MAIL_UNAVAILABLE", message = "当前进程角色未启用邮件服务。" });
            try
            {
                var created = await mail.SendBroadcastMailAsync(request, CancellationToken.None).ConfigureAwait(false);
                OpsPanel.Audit(loggerFactory, context, "mail.send-broadcast", null);
                return Results.Ok(new
                {
                    action = "mail-send-broadcast",
                    broadcastId = created.BroadcastId,
                    title = created.Title,
                    onlyExistingPlayers = created.OnlyExistingPlayers,
                    createdAt = created.CreatedAt,
                    expiresAt = created.ExpiresAt
                });
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/api/actions/mail-revoke-broadcast", async (
            HttpContext context,
            OpsRevokeBroadcastMailRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var mail = context.RequestServices.GetService<MailRuntime>();
            if (mail is null)
                return Results.NotFound(new { code = "MAIL_UNAVAILABLE", message = "当前进程角色未启用邮件服务。" });
            try
            {
                var revoked = await mail.RevokeBroadcastMailAsync(request.BroadcastId, CancellationToken.None).ConfigureAwait(false);
                if (!revoked)
                    return Results.NotFound(new { code = "BROADCAST_NOT_FOUND", message = "公告不存在或已撤回。" });
                OpsPanel.Audit(loggerFactory, context, "mail.revoke-broadcast", null);
                return Results.Ok(new { action = "mail-revoke-broadcast", broadcastId = request.BroadcastId, revoked = true });
            }
            catch (MailOperationException ex)
            {
                return MapFailure(ex);
            }
        });
    }

    private static IResult MapFailure(MailOperationException ex)
    {
        var status = ex.Failure switch
        {
            MailOperationFailure.NotFound => StatusCodes.Status404NotFound,
            MailOperationFailure.HasUnclaimedAttachments => StatusCodes.Status409Conflict,
            MailOperationFailure.BotRestricted => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = ex.Failure.ToString().ToUpperInvariant(), message = ex.Message }, statusCode: status);
    }
}
