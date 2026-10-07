using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 讨伐迁地 ECS 边界门禁：讨伐行动在旧地点战斗中插队赶路，
/// 抵达新节点后旧地点战斗实体必须释放，远征按新节点遭遇池重锚续跑。
/// </summary>
public sealed class EcsCombatRelocationTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private const string HuntAction = "combat.hunt-qi-beast";

    [Fact]
    public async Task TravelDuringCombatReleasesStaleBattleAndReanchorsExpedition()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "讨伐迁地", From, catalog.Fingerprint);
        player.World.NodeId = "node.peach-forest";
        var resolved = PlayerAttributeResolver.Resolve(player, catalog)
            .ToCombatant(player.Progression.CombatExperience);
        player.Resources.MaxHitpoints = resolved.MaxHitpoints;
        player.Resources.Hitpoints = resolved.MaxHitpoints;
        player.Enqueue(new ActionIntent(new ActionId(HuntAction), -1), From, catalog.Actions);

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());

        // 桃林搜寻并进战：战斗实体挂接本地遭遇 encounter.sunset-ridge。
        await runtime.RunPlayerOnceAsync(id, From.AddSeconds(30));
        var inBattle = runtime.ExportSnapshot(id).State;
        Assert.Equal("encounter.sunset-ridge", inBattle.Actions[0].Expedition!.EncounterId);
        Assert.Equal(CombatExpeditionPhase.Combat, inBattle.Actions[0].Expedition!.Phase);
        var playerEntity = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!;
        var staleBattleId = playerEntity.GetComponent<PlayerCombatComponent>()!.EncounterEntityId;
        Assert.NotEqual(0, staleBattleId);
        Assert.Equal("encounter.sunset-ridge",
            runtime.World.GetEntity(staleBattleId)!.GetComponent<CombatEncounterComponent>()!.EncounterId);

        // 插队赶路到青云山门（遭遇池 encounter.twilight-pass）。
        var at = inBattle.LastSimulatedAt;
        var plan = catalog.WorldGraph.PlanFromPosition(inBattle.World.EdgeId, inBattle.World.EdgeProgress,
            inBattle.World.NodeId, "node.cloud-gate");
        runtime.MutatePlayer(id, aggregate => aggregate.EnqueuePriority(
            new ActionIntent(new ActionId("world.travel"), 1, "node.cloud-gate"),
            at, catalog.Actions, new TravelActionState("node.cloud-gate", plan.TotalTravelSeconds,
                plan.EdgeIds, plan.Segments)));
        var arrival = at.AddSeconds(plan.TotalTravelSeconds);
        await runtime.RunPlayerOnceAsync(id, arrival.AddSeconds(1));

        var moved = runtime.ExportSnapshot(id).State;
        Assert.Equal("node.cloud-gate", moved.World.NodeId);
        Assert.Null(runtime.World.GetEntity(staleBattleId));
        var resumed = Assert.Single(moved.Actions, a => a.Intent.ActionId.Value == HuntAction);
        Assert.Equal(ActionQueueStatus.Running, resumed.Status);
        Assert.Equal("encounter.twilight-pass", resumed.Expedition!.EncounterId);
    }
}
