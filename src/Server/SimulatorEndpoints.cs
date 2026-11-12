using System.Security.Claims;
using Domain.Player;
using Domain.World;
using Protocol.Simulator;
using Server.Clean;
using Server.Clean.Social;

namespace Server;

/// <summary>
/// 战斗预演只读端点：本人/同队档案捕获与全量节点环境快照。父组已挂 RequireAuthorization。
/// </summary>
public static class SimulatorEndpoints
{
    /// <summary>在已鉴权玩家路由组上映射两条模拟器只读端点。</summary>
    public static void Map(RouteGroupBuilder players)
    {
        players.MapGet("/{playerId:guid}/simulator-profile", async (Guid playerId, Guid? targetPlayerId,
            ClaimsPrincipal user, HttpContext context,
            IPlayerRuntime runtime, IPlayerStateRepository sections, IAssetBalanceRepository assetBalances,
            ISocialRepository social, Domain.GameData.GameDataCatalog gameData, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsPlayer(user, playerId)) return Results.Forbid();
            var targetId = targetPlayerId ?? playerId;
            if (targetId != playerId)
            {
                var partyId = await social.GetPartyIdAsync(playerId, ct);
                var party = partyId is { } id ? await social.GetPartyAsync(id, ct) : null;
                if (party is null || !party.Members.Any(member => member.PlayerId == targetId))
                    return Results.Forbid();
            }
            var loaded = runtime.TryGetLoaded(targetId);
            var state = loaded?.State;
            if (state is null)
            {
                var bundle = await sections.LoadAsync(targetId, ct);
                if (bundle is null) return Results.NotFound();
                state = bundle.Head.StateVersion > 0 || bundle.Sections.Count > 0
                    ? Domain.Persistence.SectionCodec.DecodeAggregate(bundle)
                    : PlayerAggregate.Create(new PlayerId(targetId), new AccountId(targetId),
                        bundle.Identity.DisplayName, bundle.Identity.CreatedAt, gameData.Fingerprint).ExportState();
            }
            var balances = loaded?.Assets ?? await assetBalances.LoadBalancesAsync(targetId, ct);
            return Results.Ok(DaoHua.Simulator.SimulatorProfileMapper.Capture(state,
                balances.Select(balance => new SimulatorAssetDto(balance.AssetId, balance.Variant, balance.Quantity)),
                gameData));
        });
        players.MapGet("/{playerId:guid}/simulator-environment", async (Guid playerId,
            ClaimsPrincipal user, HttpContext context,
            NodeBuildingRegistry nodeBuildings, Domain.GameData.GameDataCatalog gameData,
            PostgresZongmenRepository zongmen, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsPlayer(user, playerId)) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
            var own = await zongmen.FindForPlayerAsync(playerId, ct);
            var index = await nodeBuildings.AllBuildingsAsync(now, ct);
            // 环境节点集合 = 静态公共节点 ∪ 本人宗门动态驻地；其他玩家驻地一律过滤。
            var nodeIds = gameData.WorldGraph.Nodes
                .Where(entry => !ZongmenRules.TryResidenceId(entry.Id, out _) || entry.Id == own?.NodeId)
                .Select(entry => entry.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (own is { NodeId: { } ownNodeId } && !nodeIds.Contains(ownNodeId, StringComparer.Ordinal))
                nodeIds.Add(ownNodeId);
            var nodes = new List<SimulatorEnvironmentNodeDto>();
            foreach (var nodeId in nodeIds)
            {
                var snapshot = index.TryGetValue(nodeId, out var found) ? found : new NodeBuildingSnapshot(0, []);
                var counts = await nodeBuildings.UsageForAsync(nodeId, now, ct);
                var effects = snapshot.Buildings.Count == 0
                    ? BuildingEffectSet.Empty
                    : NodeBuildingRules.AggregateEffects(snapshot.Buildings, gameData.Buildings, nowUnixSeconds, counts);
                nodes.Add(new SimulatorEnvironmentNodeDto(
                    nodeId,
                    snapshot.Buildings.Select(DaoHua.Simulator.SimulatorProfileMapper.ToDto).ToArray(),
                    DaoHua.Simulator.SimulatorProfileMapper.ToDto(effects),
                    nodeBuildings.UsagesFor(snapshot.Buildings, counts, nowUnixSeconds)
                        .Select(DaoHua.Simulator.SimulatorProfileMapper.ToDto).ToArray()));
            }
            return Results.Ok(new SimulatorEnvironmentDto(
                SimulatorContractVersions.Environment, gameData.Fingerprint, nowUnixSeconds, nodes));
        });
    }

    private static bool IsPlayer(ClaimsPrincipal principal, Guid playerId) =>
        Guid.TryParse(principal.FindFirstValue("player_id"), out var current) && current == playerId;
}
