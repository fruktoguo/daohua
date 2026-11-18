using Microsoft.AspNetCore.Routing;
using Server.Clean.Redeem;

namespace Server.Clean.Ops;

/// <summary>
/// 运维面板兑换码管理端点（/ops/api/redeem/*、/ops/api/actions/redeem-*）。
/// 统一经 context.RequestServices 可选解析 RedeemRuntime，保证最小宿主测试未装配兑换服务时仍可正常映射面板路由。
/// </summary>
public static class OpsRedeemActions
{
    internal static void MapOpsRedeemActions(this IEndpointRouteBuilder group)
    {
        group.MapGet("/api/redeem/batches", async (HttpContext context, CancellationToken ct) =>
        {
            var redeem = context.RequestServices.GetService<RedeemRuntime>();
            if (redeem is null)
                return Results.NotFound(new { code = "REDEEM_UNAVAILABLE", message = "当前进程角色未启用兑换码服务。" });
            var items = await redeem.ListOpsBatchesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { items });
        });

        group.MapGet("/api/redeem/batches/{batchId:long}/codes", async (
            long batchId,
            string? status,
            int? limit,
            HttpContext context,
            CancellationToken ct) =>
        {
            var redeem = context.RequestServices.GetService<RedeemRuntime>();
            if (redeem is null)
                return Results.NotFound(new { code = "REDEEM_UNAVAILABLE", message = "当前进程角色未启用兑换码服务。" });
            try
            {
                var items = await redeem.ListOpsBatchCodesAsync(batchId, status, limit ?? 500, ct).ConfigureAwait(false);
                return Results.Ok(new { batchId, items });
            }
            catch (RedeemOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/api/actions/redeem-create-universal", async (
            HttpContext context,
            OpsCreateUniversalRedeemRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var redeem = context.RequestServices.GetService<RedeemRuntime>();
            if (redeem is null)
                return Results.NotFound(new { code = "REDEEM_UNAVAILABLE", message = "当前进程角色未启用兑换码服务。" });
            try
            {
                var created = await redeem.CreateUniversalAsync(request, CancellationToken.None).ConfigureAwait(false);
                OpsPanel.Audit(loggerFactory, context, $"redeem.create-universal:{created.Batch.BatchId}", null);
                return Results.Ok(created);
            }
            catch (RedeemOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/api/actions/redeem-create-single-use", async (
            HttpContext context,
            OpsCreateSingleUseRedeemRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var redeem = context.RequestServices.GetService<RedeemRuntime>();
            if (redeem is null)
                return Results.NotFound(new { code = "REDEEM_UNAVAILABLE", message = "当前进程角色未启用兑换码服务。" });
            try
            {
                var created = await redeem.CreateSingleUseAsync(request, CancellationToken.None).ConfigureAwait(false);
                OpsPanel.Audit(loggerFactory, context, $"redeem.create-single-use:{created.Batch.BatchId}", null);
                return Results.Ok(created);
            }
            catch (RedeemOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/api/actions/redeem-set-disabled", async (
            HttpContext context,
            OpsSetRedeemBatchDisabledRequest request,
            OpsOptions opts,
            ILoggerFactory loggerFactory) =>
        {
            if (OpsPanel.RejectWriteRequest(context, opts) is { } rejected) return rejected;
            var redeem = context.RequestServices.GetService<RedeemRuntime>();
            if (redeem is null)
                return Results.NotFound(new { code = "REDEEM_UNAVAILABLE", message = "当前进程角色未启用兑换码服务。" });
            try
            {
                var updated = await redeem.SetBatchDisabledAsync(request.BatchId, request.Disabled, CancellationToken.None).ConfigureAwait(false);
                if (!updated)
                    return Results.NotFound(new { code = "BATCH_NOT_FOUND", message = "兑换码批次不存在。" });
                OpsPanel.Audit(loggerFactory, context, request.Disabled ? $"redeem.disable:{request.BatchId}" : $"redeem.enable:{request.BatchId}", null);
                return Results.Ok(new
                {
                    action = "redeem-set-disabled",
                    batchId = request.BatchId,
                    disabled = request.Disabled
                });
            }
            catch (RedeemOperationException ex)
            {
                return MapFailure(ex);
            }
        });
    }

    private static IResult MapFailure(RedeemOperationException ex)
    {
        var status = ex.Failure switch
        {
            RedeemOperationFailure.NotFound => StatusCodes.Status404NotFound,
            RedeemOperationFailure.CodeConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = ex.Failure.ToString().ToUpperInvariant(), message = ex.Message }, statusCode: status);
    }
}
