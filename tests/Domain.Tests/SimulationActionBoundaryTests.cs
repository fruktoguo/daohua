using System.Text.Json;
using Domain.GameData;
using Domain.Economy;
using Domain.Common;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Domain.Tests;

/// <summary>行动自然边界入口门禁：共享资源外层必须与普通 AdvanceTo 保持逐位一致。</summary>
public sealed class SimulationActionBoundaryTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TimedBoundaryKeepsBuffRegenAndSnapshotSemantics()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var whole = CreatePlayer(catalog, "cultivation.draw-qi");
        var stepped = PlayerAggregate.Restore(whole.ExportState());
        var target = SeedTime.AddSeconds(3);

        var expected = SimulationEngine.AdvanceTo(whole, target, catalog);
        var actual = AdvanceTimedPrimitives(stepped, target, catalog, null, [3]);

        AssertStateEqual(whole, stepped);
        Assert.Equal(JsonSerializer.Serialize(expected.Events), JsonSerializer.Serialize(actual.Events));
        Assert.Equal(JsonSerializer.Serialize(expected.AssetDeltas), JsonSerializer.Serialize(actual.AssetDeltas));
    }

    [Fact]
    public void CombatSearchBoundaryKeepsSharedResourceAndSnapshotSemantics()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var whole = CreatePlayer(catalog, "combat.hunt-qi-beast");
        var stepped = PlayerAggregate.Restore(whole.ExportState());
        var target = SeedTime.AddSeconds(2);

        var expected = SimulationEngine.AdvanceTo(whole, target, catalog);
        IPlayerResourceContext view = stepped;
        var wallet = new AssetWallet(stepped.PlayerId);
        var request = new ActionMutationBatch();
        var core = new ActionMutationBatch();
        var versions = SimulationEngine.BeginStateVersionCommitSession();
        var plan = SimulationEngine.PrepareResourceBoundary(view, catalog, wallet, target, request);
        var action = stepped.Actions[0];
        var definition = catalog.Actions.Get(action.Intent.ActionId);
        Assert.True(SimulationEngine.TryStartAction(view, action, definition, catalog, wallet, SeedTime, core));
        var encounter = SimulationEngine.ResolveCombatEncounter(stepped, action, definition, catalog);
        action.Expedition = SimulationEngine.CreateCombatSearchState(stepped, encounter,
            Domain.Combat.CombatExpeditionPhase.InitialSearch, catalog, action.CompletedRepeats);
        core.AddEvent(new(SeedTime, "combat.search.started", $"开始搜寻：{encounter.Name}。", definition.Id));
        SimulationEngine.AdvanceCombatSearchPhase(action, plan.StepSeconds);
        view.Progression.AgeSeconds += (target - SeedTime).TotalSeconds;
        view.LastSimulatedAt = target;
        view.MarkDirty(PlayerSectionKind.Progression);
        view.MarkDirty(PlayerSectionKind.ActionQueue);
        versions.RecordCoreStep(true, true);
        var resourceChanged = SimulationEngine.CompleteResourceBoundary(
            view, catalog, wallet, SeedTime, plan.StepSeconds, core, request);
        versions.MarkRequestChanged(plan.Changed || resourceChanged);
        versions.Commit(view);
        var actual = new SimulationResult(view.PlayerId, SeedTime, target, (target - SeedTime).TotalSeconds,
            request.CompletedActions, request.CompletedRepeats, request.ToAssetDeltas(view.PlayerId, false),
            request.Events, view.StateVersion, true, request.DroppedEventCount);

        AssertStateEqual(whole, stepped);
        Assert.Equal(JsonSerializer.Serialize(expected.Events), JsonSerializer.Serialize(actual.Events));
        Assert.Equal(JsonSerializer.Serialize(expected.AssetDeltas), JsonSerializer.Serialize(actual.AssetDeltas));
    }

    [Fact]
    public void TimedPrimitivesPreserveWholeRequestVersionAcrossNaturalBoundaries()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var whole = CreatePlayer(catalog, "explore.gather-ningxue-grass");
        var segmented = PlayerAggregate.Restore(whole.ExportState());
        var target = SeedTime.AddSeconds(17);

        SimulationEngine.AdvanceTo(whole, target, catalog);
        AdvanceTimedPrimitives(segmented, target, catalog, null, [5, 8, 13, 16, 17]);

        AssertStateEqual(whole, segmented);
    }

    [Fact]
    public void TimedPrimitivesPreserveMaterialBlockingAndWalletAcrossSplitWindow()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var whole = CreatePlayer(catalog, "alchemy.refine-qi-pill");
        var segmented = PlayerAggregate.Restore(whole.ExportState());
        var material = new AssetKey(new ItemId("item.ningxue-grass"));
        var balances = new[] { new AssetBalance(whole.PlayerId, material, 9, 0) };
        var wholeWallet = new AssetWallet(whole.PlayerId);
        wholeWallet.Restore(balances);
        var segmentedWallet = new AssetWallet(segmented.PlayerId);
        segmentedWallet.Restore(balances);
        var target = SeedTime.AddSeconds(37);

        var expected = SimulationEngine.AdvanceTo(whole, target, catalog, wholeWallet);
        foreach (var delta in expected.AssetDeltas) wholeWallet.Apply(delta);
        AdvanceTimedPrimitives(segmented, target, catalog, segmentedWallet, [5, 12, 17, 24, 25, 36, 37]);

        AssertStateEqual(whole, segmented);
        Assert.Equal(JsonSerializer.Serialize(wholeWallet.Balances.OrderBy(x => x.Asset.ToString())),
            JsonSerializer.Serialize(segmentedWallet.Balances.OrderBy(x => x.Asset.ToString())));
    }

    [Fact]
    public void StateVersionCommitSessionAggregatesCoreBoundariesAndOneRequestCommit()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog, "explore.gather-ningxue-grass");
        var before = player.StateVersion;
        var session = SimulationEngine.BeginStateVersionCommitSession();

        session.RecordCoreStep(changed: true, endsNaturalBoundary: true);
        session.RecordCoreStep(changed: true, endsNaturalBoundary: false);
        session.RecordCoreStep(changed: true, endsNaturalBoundary: true);
        session.MarkRequestChanged(changed: true);

        Assert.Equal(2, session.PendingCoreCommits);
        Assert.Equal(3, session.Commit(player));
        Assert.Equal(before + 3, player.StateVersion);
    }

    // 模拟组件系统各自调用原语：只接收分段接口，窗口内不导出或重建聚合。
    private static SimulationResult AdvanceTimedPrimitives(IPlayerResourceContext player, DateTimeOffset target,
        GameDataCatalog catalog, AssetWallet? assets, int[] cuts)
    {
        var from = player.LastSimulatedAt;
        var wallet = new AssetWallet(player.PlayerId);
        if (assets is not null) wallet.Restore(assets.Balances);
        var request = new ActionMutationBatch();
        var versions = SimulationEngine.BeginStateVersionCommitSession();
        var cutIndex = 0;
        while (player.LastSimulatedAt < target)
        {
            var windowStart = player.LastSimulatedAt;
            var plan = SimulationEngine.PrepareResourceBoundary(player, catalog, wallet, target, request);
            var windowEnd = windowStart.AddSeconds(plan.StepSeconds);
            var remaining = plan.StepSeconds;
            var core = new ActionMutationBatch();
            var transitions = 0;
            while (remaining > 1e-9 && player.Actions.Count > 0)
            {
                var action = player.Actions[0];
                var at = windowStart.AddSeconds(plan.StepSeconds - remaining);
                if (action.Status == ActionQueueStatus.Blocked)
                {
                    if (!SimulationEngine.AdvanceBlockedAction(player, action, at)) break;
                    transitions++;
                    continue;
                }
                transitions++;
                var definition = catalog.Actions.Get(action.Intent.ActionId);
                if (!SimulationEngine.TryStartAction(player, action, definition, catalog, wallet, at, core)) continue;
                var duration = SimulationEngine.TimedActionDuration(player, action, definition, catalog, wallet, core.ItemDeltas);
                var multiplier = SimulationEngine.PrepareTimedAction(player, action, definition, catalog, wallet, at, core);
                while (cutIndex < cuts.Length && from.AddSeconds(cuts[cutIndex]) <= at) cutIndex++;
                var available = cutIndex < cuts.Length ? Math.Min(remaining, (from.AddSeconds(cuts[cutIndex]) - at).TotalSeconds) : remaining;
                var step = Math.Min(available, Math.Max(0, duration - action.ProgressSeconds));
                remaining -= step;
                at = windowStart.AddSeconds(plan.StepSeconds - remaining);
                SimulationEngine.AdvanceTimedAction(player, action, definition, multiplier, catalog, step, at);
                if (action.ProgressSeconds + 1e-9 >= duration)
                    SimulationEngine.CompleteTimedAction(player, action, definition, multiplier, catalog, wallet, at, core);
            }
            player.Progression.AgeSeconds += (windowEnd - windowStart).TotalSeconds;
            player.LastSimulatedAt = windowEnd;
            if (player.Actions.Count > 0 && player.Actions[0].Status == ActionQueueStatus.Pending)
            {
                player.Actions[0].Status = ActionQueueStatus.Running;
                if (transitions == 0) player.MarkDirty(PlayerSectionKind.ActionQueue);
            }
            if (transitions > 0)
            {
                player.MarkDirty(PlayerSectionKind.Progression);
                player.MarkDirty(PlayerSectionKind.ActionQueue);
            }
            versions.RecordCoreStep(transitions > 0, true);
            var changed = SimulationEngine.CompleteResourceBoundary(player, catalog, wallet, windowStart, plan.StepSeconds, core, request);
            versions.MarkRequestChanged(plan.Changed || changed);
        }
        if (assets is not null)
            foreach (var delta in request.ToAssetDeltas(player.PlayerId, sortByItem: false)) assets.Apply(delta);
        versions.Commit(player);
        return new(player.PlayerId, from, target, (target - from).TotalSeconds,
            request.CompletedActions, request.CompletedRepeats, request.ToAssetDeltas(player.PlayerId, false),
            request.Events, player.StateVersion, versions.PendingCoreCommits > 0, request.DroppedEventCount);
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog, string actionId)
    {
        var player = PlayerAggregate.Create(
            new PlayerId(Guid.Parse("10000000-0000-4000-8000-000000000003")),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000003")),
            "边界测试",
            SeedTime,
            catalog.Fingerprint);
        player.Buffs.Active.Add(new ActiveBuff("buff.boundary", SeedTime.AddSeconds(2).ToUnixTimeSeconds(),
            1, "边界回复", HpRegen: 2, AttackSpeedPoints: 10));
        player.Enqueue(new ActionIntent(new ActionId(actionId), -1), SeedTime, catalog.Actions);
        return player;
    }

    private static void AssertStateEqual(PlayerAggregate expected, PlayerAggregate actual)
    {
        var expectedJson = JsonSerializer.Serialize(expected.ExportState());
        var actualJson = JsonSerializer.Serialize(actual.ExportState());
        Assert.Equal(expectedJson, actualJson);
    }
}
