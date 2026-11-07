using Domain.Common;
using Domain.World;
using Server.Clean;

namespace Server;

public sealed partial class EcsPlayerRuntimeAdapter
{
    /// <summary>动态驻地不加入全局路网；入口只接受当前成员从挂靠点进入。</summary>
    private async ValueTask<(Guid DuelId, Guid PeerId)?> EnterZongmenResidenceAsync(Guid playerId, string nodeId, CancellationToken ct)
    {
        var own = zongmen is null ? null : await zongmen.FindForPlayerAsync(playerId, ct);
        if (own is null || own.Disbanded || own.NodeId != nodeId)
            throw new DomainException(DomainErrorCode.InvalidArgument, "只能进入本宗门驻地。");
        var before = ToSnapshot(ecs.ExportSnapshot(playerId));
        if (before.State.World.NodeId == nodeId) return null;
        if (before.State.World.EdgeId is not null || before.State.World.NodeId != own.AnchorNodeId)
            throw new DomainException(DomainErrorCode.InvalidArgument, "请先前往宗门挂靠地点。");
        if (before.State.Actions.Any(action => action.Participation is not null))
            throw new DomainException(DomainErrorCode.Conflict, "请先结束组队行动。");
        if (_simulationClock.UtcNow > before.LastSimulatedAt) await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, ct);
        var node = gameData.WorldGraph.GetNode(own.AnchorNodeId);
        var position = ZongmenRules.ResidenceCoordinates(node.X, node.Y);
        (Guid DuelId, Guid PeerId)? forfeit = null;
        ecs.MutatePlayer(playerId, aggregate =>
        {
            var staleTravel = aggregate.Actions.FirstOrDefault(action => action.Intent.ActionId.Value == "world.travel");
            if (staleTravel is not null) aggregate.RemoveAction(staleTravel.Id);
            forfeit = aggregate.ApplyDuelForfeit(_simulationClock.UtcNow, gameData.Duel);
            aggregate.TravelTo(nodeId, position.X, position.Y);
        });
        return forfeit;
    }

    private async ValueTask ExitZongmenResidenceAsync(Guid playerId, CancellationToken ct)
    {
        var before = ToSnapshot(ecs.ExportSnapshot(playerId));
        if (!ZongmenRules.TryResidenceId(before.State.World.NodeId, out var id)) return;
        var residence = await zongmen!.FindAsync(id, ct)
            ?? throw new InvalidOperationException("驻地缺少宗门权威行。");
        if (_simulationClock.UtcNow > before.LastSimulatedAt) await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, ct);
        RelocateFromResidence(playerId, residence.AnchorNodeId);
    }

    private void RelocateFromResidence(Guid playerId, string anchorNodeId)
    {
        var node = gameData.WorldGraph.GetNode(anchorNodeId);
        ecs.MutatePlayer(playerId, aggregate => aggregate.TravelTo(anchorNodeId, node.X, node.Y));
        TrackDirty(playerId);
    }

    /// <summary>离宗、踢出和解散先结算到资格撤销时刻，再退出驻地，防止离线继续享用旧建筑。</summary>
    private async ValueTask ReconcileZongmenResidenceAsync(Guid playerId, DateTimeOffset target, CancellationToken ct)
    {
        if (zongmen is null) return;
        var before = ToSnapshot(ecs.ExportSnapshot(playerId));
        if (!ZongmenRules.TryResidenceId(before.State.World.NodeId, out var id)) return;
        var residence = await zongmen.ResidenceAccessAsync(playerId, id, before.LastSimulatedAt, ct);
        var departure = residence.Departure;
        if (departure is not null || residence.Disbanded || !residence.IsMember)
        {
            var until = departure is null || departure.At > target ? target : departure.At;
            var dirty = ecs.GetDirtySections(playerId);
            if (until > before.LastSimulatedAt) await ecs.RunPlayerOnceAsync(playerId, until, ct);
            RelocateFromResidence(playerId, departure?.AnchorNodeId ?? residence.AnchorNodeId);
            await CommitSimulationAsync(playerId, before, ToSnapshot(ecs.ExportSnapshot(playerId)), null, dirty, ct);
            _loaded[playerId] = ToSnapshot(ecs.ExportSnapshot(playerId));
        }
        else
        {
            var anchor = gameData.WorldGraph.GetNode(residence.AnchorNodeId);
            var position = ZongmenRules.ResidenceCoordinates(anchor.X, anchor.Y);
            if (before.State.World.X != position.X || before.State.World.Y != position.Y)
                ecs.MutatePlayer(playerId, aggregate => aggregate.TravelTo(before.State.World.NodeId, position.X, position.Y));
        }
    }
}
