using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>真实 ECS 玩家路径只消费会话投影，展示远征不能创建第二份战斗实体。</summary>
public sealed class EcsParticipationProjectionTests
{
    [Fact]
    public async Task ParticipationProjectionDoesNotCreateBattleOrSchedulePersonalAttacks()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "成员", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        var frozen = player.CaptureActionParticipantSnapshot(catalog) with
        {
            Stats = CombatantStats.Create(50_000, 50, 500, 1),
            InitialHitpoints = 50_000
        };
        var spec = new ActionParticipationSpec("session.ecs-wait", "encounter.qi-beast", player.World.NodeId,
            91, 0, baseline.ToUnixTimeSeconds(), [frozen]) { ActionId = new("combat.hunt-qi-beast") };
        player.StartActionParticipation(spec, baseline, catalog.Actions);
        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var enteredAt = baseline.AddSeconds(catalog.CombatEncounters[spec.EncounterId].SearchSeconds + 0.1);
        var projection = session.AdvanceTo(enteredAt).Single();
        Assert.Equal(CombatExpeditionPhase.Combat, projection.Expedition!.Phase);
        player.ApplyActionParticipationSettlement(projection, catalog);
        using var runtime = new EcsGameRuntime(catalog, baseline);
        var id = runtime.CreatePlayer(player);
        var before = runtime.ExportSnapshot(id).State;
        Assert.Null(runtime.NextAdvanceBoundary(id));
        await runtime.RunPlayerOnceAsync(id, enteredAt.AddSeconds(20));
        var after = runtime.ExportSnapshot(id);
        Assert.Equal(before.Combat.Hitpoints, after.State.Combat.Hitpoints);
        Assert.Equal(before.Combat.Manapoints, after.State.Combat.Manapoints);
        Assert.Equal(before.Actions[0].CompletedRepeats, after.State.Actions[0].CompletedRepeats);
        Assert.Equal(before.Actions[0].Expedition, after.State.Actions[0].Expedition);
        var entity = runtime.World.GetEntity(after.EntityId)!;
        Assert.Equal(0, entity.GetComponent<PlayerCombatComponent>()!.EncounterEntityId);
        Assert.Null(entity.GetComponent<PlayerCombatComponent>()!.Encounter);
    }

    /// <summary>冻结的近五秒回复余数不能把一小时回线切成数十万个人窗口。</summary>
    [Fact]
    public async Task WaitingMemberWithNearDueRegenAdvancesPersonalClockWithoutReplayingResources()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseline = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "回线成员", baseline, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        player.Resources.RegenCarrySeconds = 4.99;
        var frozen = player.CaptureActionParticipantSnapshot(catalog);
        var spec = new ActionParticipationSpec("session.regen-wait", "encounter.qi-beast", player.World.NodeId,
            91, 0, baseline.ToUnixTimeSeconds(), [frozen]) { ActionId = new("combat.hunt-qi-beast") };
        player.StartActionParticipation(spec, baseline, catalog.Actions);
        player.Duel.StagedItems.Add(new("item.refined-copper", 7,
            baseline.AddSeconds(20).ToUnixTimeMilliseconds() / 1000d - catalog.Duel.StagedSettleSeconds));
        using var runtime = new EcsGameRuntime(catalog, baseline);
        var id = runtime.CreatePlayer(player);
        var target = baseline.AddHours(1);
        await runtime.RunPlayerOnceAsync(id, target);
        var state = runtime.ExportSnapshot(id).State;
        Assert.Equal(target, state.LastSimulatedAt);
        Assert.Equal(4.99, state.Combat.RegenCarrySeconds);
        Assert.Equal(frozen.InitialHitpoints, state.Combat.Hitpoints);
        Assert.Equal(frozen.InitialManapoints, state.Combat.Manapoints);
        Assert.Equal(0, state.Actions[0].CompletedRepeats);
        Assert.Empty(state.Duel.StagedItems);
        var settled = Assert.Single(runtime.DrainSimulationResult(id)!.AssetDeltas);
        Assert.Equal("item.refined-copper", settled.Asset.ItemId.Value);
        Assert.Equal(7, settled.QuantityDelta);
    }
}
