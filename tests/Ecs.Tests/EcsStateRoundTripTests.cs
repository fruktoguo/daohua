using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// ECS 玩家实体导入/导出门禁：组件必须承载完整领域分段，不能只保留运行时用到的少数字段。
/// 此测试不启动 HTTP 或数据库，只验证 EcsGameRuntime 的真实实体装配边界。
/// </summary>
public sealed class EcsStateRoundTripTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlayerSectionsAndAssetsRoundTripWithoutFieldLoss()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(
            new PlayerId(Guid.Parse("10000000-0000-4000-8000-000000000001")),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000001")),
            "Ecs种子",
            SeedTime,
            catalog.Fingerprint);
        player.SetPresence(PresenceState.Offline, SeedTime);
        player.Progression.RealmLevel = 3;
        player.Progression.Experience = 12.5;
        player.Progression.Experience = 7.25;
        player.Progression.LifetimeExperience = 99.5;
        player.Progression.CompletedActions = 4;
        player.Progression.SimulationCarrySeconds = 0.75;
        player.Progression.NormalKills = 2;
        player.Progression.EliteKills = 1;
        player.Progression.TotalDefeats = 0.5;
        player.Progression.CultivationSeconds = 33;
        player.Progression.AgeSeconds = 66;
        player.World.NodeId = "node.yunxi-village";
        player.World.X = 12.5;
        player.World.Y = -4.25;
        player.World.ExplorationSeed = 88;
        player.World.EdgeId = "edge.yunxi-shuyu";
        player.World.EdgeProgress = 0.375;
        player.Resources.Hitpoints = 77;
        player.Resources.MaxHitpoints = 123;
        player.Resources.Manapoints = 55;
        player.Resources.MaxManapoints = 99;
        player.Combat.AttackPower = 31;
        player.Combat.Defense = 17;
        player.Resources.RegenCarrySeconds = 1.25;
        player.Buffs.Active.Add(new ActiveBuff("buff.seed", SeedTime.AddSeconds(42).ToUnixTimeSeconds(), 2,
            "种子增益", Effects: [new BuffEffect(BuffEffectChannel.AttackSpeedPoints, 35), new BuffEffect(BuffEffectChannel.DamageAbsorptionPoints, 4)]));
        player.ReplaceCombatSlots(
            [new PlayerAbilitySlot("ability.heavy-slash", 2)],
            [new PlayerEquipmentSlot("equipment.qingfeng-sword", 3)]);
        player.Settings.LoopEnabled = false;
        player.Settings.NotificationsEnabled = false;
        player.Settings.Locale = "zh-CN";
        player.Settings.AutoUseSlots.Add(new AutoUseSlotState("combat", 0, "item.healing-pill", 2, true,
            RemainingDurationSeconds: 17, CooldownUntilUnixSeconds: 19, Group: AutoUseSlotState.SlotGroupPotion));
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 3), SeedTime, catalog.Actions);

        var expected = player.ExportState();
        var balances = new[]
        {
            new AssetBalance(player.PlayerId, new AssetKey(new ItemId("item.beast-hide")), 4.5, 0),
            new AssetBalance(player.PlayerId, new AssetKey(new ItemId("item.beast-hide"), "quality=rare"), 1, 0)
        };

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(expected, balances);
        var actual = runtime.ExportSnapshot(player.PlayerId.Value);

        Assert.Equal(expected.PlayerId, actual.State.PlayerId);
        Assert.Equal(expected.AccountId, actual.State.AccountId);
        Assert.Equal(expected.DisplayName, actual.State.DisplayName);
        Assert.Equal(expected.CreatedAt, actual.State.CreatedAt);
        Assert.Equal(expected.LastSimulatedAt, actual.State.LastSimulatedAt);
        Assert.Equal(expected.Presence, actual.State.Presence);
        Assert.Equal(expected.StateVersion, actual.State.StateVersion);
        Assert.Equal(expected.GameDataFingerprint, actual.State.GameDataFingerprint);
        AssertJsonEqual(expected.Progression, actual.State.Progression, "Progression");
        AssertJsonEqual(expected.World, actual.State.World, "World");
        AssertJsonEqual(expected.Combat, actual.State.Combat, "Combat");
        AssertJsonEqual(expected.Buffs, actual.State.Buffs, "Buffs");
        AssertJsonEqual(expected.EquippedInstances, actual.State.EquippedInstances, "EquippedInstances");
        AssertJsonEqual(expected.LearnedAbilities, actual.State.LearnedAbilities, "LearnedAbilities");
        AssertJsonEqual(expected.Settings, actual.State.Settings, "Settings");
        AssertJsonEqual(expected.Actions, actual.State.Actions, "Actions");
        AssertJsonEqual(expected.Loadout, actual.State.Loadout, "Loadout");
        AssertJsonEqual(expected.Duel, actual.State.Duel, "Duel");
        Assert.Equal(expected.EquippedItemIds, actual.State.EquippedItemIds);
        var expectedAssets = balances.ToDictionary(item => item.Asset, item => item.Quantity);
        Assert.Equal(expectedAssets.Count, actual.Assets.Count);
        foreach (var (asset, quantity) in expectedAssets)
        {
            Assert.True(actual.Assets.TryGetValue(asset, out var actualQuantity), $"ECS 资产 {asset} 丢失。");
            Assert.Equal(quantity, actualQuantity);
        }
    }

    [Fact]
    public void CreatePlayer_WhenAggregateRestoreThrows_DoesNotLeakEntityAndRetrySucceeds()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(
            new PlayerId(Guid.Parse("10000000-0000-4000-8000-000000000199")),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000199")),
            "清理测试", SeedTime, catalog.Fingerprint);
        var validState = player.ExportState();
        var invalidState = validState with
        {
            Progression = validState.Progression with { RealmLevel = 0 }
        };

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var ex = Assert.Throws<DomainException>(() => runtime.CreatePlayer(invalidState));
        Assert.Contains("section", ex.Message);

        // 确认没有残留游离实体泄露进 World
        var entityId = YuoCore.Ecs.IDGenerate.GetID($"player:{validState.PlayerId.Value:N}");
        Assert.Null(runtime.World.GetEntity(entityId));

        // 重试使用合法状态创建应顺利成功，不得报实体ID重复
        var createdId = runtime.CreatePlayer(validState);
        Assert.Equal(validState.PlayerId.Value, createdId);
        Assert.NotNull(runtime.World.GetEntity(entityId));
    }

    private static void AssertJsonEqual<T>(T expected, T actual, string section)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        Assert.True(string.Equals(expectedJson, actualJson, StringComparison.Ordinal),
            $"ECS 分段 {section} 往返不一致。expected={expectedJson} actual={actualJson}");
    }
}
