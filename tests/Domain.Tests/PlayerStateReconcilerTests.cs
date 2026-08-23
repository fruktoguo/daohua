// ==============================================================================
// 文件名称: PlayerStateReconcilerTests.cs
// 主要职责: 验证 PlayerStateReconciler 在配表发生变动时对失效物品、装备、技能、动作队列及地图的自愈清洗与合法资产保留。
// ==============================================================================

using Domain.GameData;
using Domain.Models;
using Domain.Simulation;
using Xunit;

namespace Domain.Tests;

public sealed class PlayerStateReconcilerTests
{
    [Fact]
    public void Reconcile_RemovesInvalidItemsAndPreservesValidItems()
    {
        var catalog = GameDataCatalog.Current;
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "自愈测试修士",
            Version = 1
        };

        // 添加一个合法物品和一个不存在的失效物品
        var validItemId = catalog.Items.Keys.First();
        var invalidItemId = "item_deleted_ancient_dust_999";

        state.Inventory.Items[validItemId] = 50;
        state.Inventory.Items[invalidItemId] = 10;

        var report = PlayerStateReconciler.Reconcile(state, catalog, "old_fingerprint");

        Assert.True(report.HasChanges);
        Assert.True(report.FingerprintMismatched);
        Assert.Equal(1, report.RemovedInventoryItems);
        Assert.True(state.Inventory.Items.ContainsKey(validItemId));
        Assert.Equal(50, state.Inventory.Items[validItemId]);
        Assert.False(state.Inventory.Items.ContainsKey(invalidItemId));
    }

    [Fact]
    public void Reconcile_RemovesInvalidEquipmentAndPreservesValidEquipment()
    {
        var catalog = GameDataCatalog.Current;
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "装备测试修士"
        };

        var validEquipId = catalog.Equipment.Keys.First();
        var invalidEquipId = "equip_removed_shadow_blade_888";

        state.Combat.Equipment.Add(new EquippedItem { ItemId = validEquipId, EnhancementLevel = 5 });
        state.Combat.Equipment.Add(new EquippedItem { ItemId = invalidEquipId, EnhancementLevel = 1 });

        var report = PlayerStateReconciler.Reconcile(state, catalog, "old_fingerprint");

        Assert.Equal(1, report.RemovedEquipment);
        Assert.Single(state.Combat.Equipment);
        Assert.Equal(validEquipId, state.Combat.Equipment[0].ItemId);
    }

    [Fact]
    public void Reconcile_RemovesInvalidAbilitiesAndResetsSlots()
    {
        var catalog = GameDataCatalog.Current;
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "技能测试修士"
        };

        var validAbilityId = catalog.Abilities.Keys.First();
        var invalidAbilityId = "ability_deleted_divine_wrath_777";

        state.Combat.LearnedAbilityIds.Add(validAbilityId);
        state.Combat.LearnedAbilityIds.Add(invalidAbilityId);

        state.Combat.AbilitySlots[0].AbilityId = validAbilityId;
        state.Combat.AbilitySlots[1].AbilityId = invalidAbilityId;

        var report = PlayerStateReconciler.Reconcile(state, catalog, "old_fingerprint");

        Assert.Equal(1, report.RemovedLearnedAbilities);
        Assert.Equal(1, report.ResetAbilitySlots);
        Assert.Single(state.Combat.LearnedAbilityIds);
        Assert.Equal(validAbilityId, state.Combat.LearnedAbilityIds[0]);
        Assert.Equal(validAbilityId, state.Combat.AbilitySlots[0].AbilityId);
        Assert.Null(state.Combat.AbilitySlots[1].AbilityId);
    }

    [Fact]
    public void Reconcile_RemovesInvalidQueueActionsAndPreservesValidQueue()
    {
        var catalog = GameDataCatalog.Current;
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "队列测试修士"
        };

        var validActionId = catalog.Actions.Keys.First();
        var invalidActionId = "action_removed_secret_explore_666";

        state.Queue.Entries.Add(new QueuedAction { ActionId = validActionId, RemainingRepeats = 3 });
        state.Queue.Entries.Add(new QueuedAction { ActionId = invalidActionId, RemainingRepeats = 1 });

        var report = PlayerStateReconciler.Reconcile(state, catalog, "old_fingerprint");

        Assert.Equal(1, report.RemovedQueueActions);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(validActionId, state.Queue.Entries[0].ActionId);
    }

    [Fact]
    public void Reconcile_ResetsInvalidWorldNodeToFallback()
    {
        var catalog = GameDataCatalog.Current;
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "地图测试修士",
            World = new WorldState
            {
                MapId = "map_deleted_void_realm_000",
                NodeId = "node_deleted_void_altar_000"
            }
        };

        var report = PlayerStateReconciler.Reconcile(state, catalog, "old_fingerprint");

        Assert.True(report.ResetWorldNode);
        Assert.Contains(state.World.MapId, catalog.Maps.Keys);
        Assert.NotNull(state.World.NodeId);
        Assert.Contains(catalog.Maps[state.World.MapId].Nodes, n => n.Id == state.World.NodeId);
    }
}
