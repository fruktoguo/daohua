using System.Security.Claims;
using Domain.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Server.Clean;

namespace Server;

/// <summary>
/// 当前行动内紧凑 PVP 斗法记录按需读取端点（WebSocket 快照剥离明细后的补充通道）。
/// </summary>
public static class DuelRecordEndpoints
{
    /// <summary>在 <c>/api/v1/players</c> 鉴权路由组上挂载 <c>GET /{playerId}/duel-records</c>。</summary>
    public static void Map(RouteGroupBuilder players)
    {
        players.MapGet("/{playerId:guid}/duel-records", async (
            Guid playerId,
            ClaimsPrincipal user,
            IPlayerRuntime runtime,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("player_id"), out var current) || current != playerId)
                return Results.Forbid();

            var snapshot = await runtime.LoadAsync(playerId, ct);
            var state = snapshot.State;
            var headAction = state.Actions.Count > 0 ? state.Actions[0] : null;
            var currentReport = state.Duel.CurrentReport;
            var records = new List<DuelBattleRecordDocument>(DuelBattleRecordLimits.MaxRecordsPerAction);
            string? liveDuelId = null;

            if (headAction?.Expedition is { Phase: CombatExpeditionPhase.Duel, Duel: { Resolved: false, ViewerRecord: { } liveRecord } duel } exp)
            {
                liveDuelId = duel.DuelId;
                var nowUnix = snapshot.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;
                records.Add(liveRecord.ForViewerSlice(CombatOutcome.Running, false, nowUnix, exp.BattleElapsedSeconds));
            }

            if (currentReport?.DuelRecords is { Count: > 0 } saved)
            {
                for (var i = 0; i < saved.Count && records.Count < DuelBattleRecordLimits.MaxRecordsPerAction; i++)
                {
                    if (liveDuelId is not null && string.Equals(saved[i].DuelId, liveDuelId, StringComparison.Ordinal))
                        continue;
                    records.Add(saved[i]);
                }
            }

            return Results.Ok(new
            {
                actionId = currentReport?.ActionId ?? headAction?.Intent.ActionId.Value ?? string.Empty,
                records
            });
        }).RequireRateLimiting("command");
    }
}
