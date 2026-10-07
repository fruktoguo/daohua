using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 真实战斗 ECS parity 门禁：同一配表、种子、玩家装配技能和战外 Buff，
/// OOP 确定性引擎与 ECS 实体战斗必须产生相同的边界、技能/冷却、Buff、事件和怪物状态。
/// 该测试禁止用「完整 AdvanceTo 后再投影」伪造阶段系统的正确性。
/// </summary>
public sealed class EcsCombatParityTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid AccountIdValue = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private const string EncounterId = "encounter.twilight-pass";
    private const string ActionId = "combat.hunt-qi-beast";
    private const long Seed = 0x1234_5678_9ABC_DEF0;

    [Fact]
    public async Task EquippedSkillBuffAndCombatEventsMatchDeterministicOop()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var ability = catalog.Abilities["ability.heavy-slash"];
        var player = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "Ecs战斗", SeedTime, catalog.Fingerprint);
        player.ReplaceCombatSlots(
            [new PlayerAbilitySlot("ability.heavy-slash", 1)],
            [new PlayerEquipmentSlot("equipment.qingfeng-sword", 2)]);
        var resolved = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(player.Progression.CombatExperience);
        player.Resources.MaxHitpoints = resolved.MaxHitpoints;
        player.Resources.Hitpoints = resolved.MaxHitpoints;
        player.Resources.MaxManapoints = resolved.MaxManapoints;
        player.Resources.Manapoints = resolved.MaxManapoints;
        player.Buffs.Active.Add(new ActiveBuff("buff.test-haste", SeedTime.AddSeconds(2).ToUnixTimeSeconds(),
            1, "疾风", AttackSpeedPoints: 500));
        var state = player.ExportState();

        var encounterDefinition = catalog.CombatEncounters[EncounterId];
        var monsters = EncounterWaveSelector.Select(encounterDefinition, catalog.CombatMonsters, Seed, 1).ToArray();
        Assert.NotEmpty(monsters);
        var expected = DeterministicCombatEngine.Start(EncounterId, Seed,
            new CombatantTemplate(resolved, [new SkillSlot(ability, 1)]),
            monsters.Select(monster => new CombatantTemplate(monster.Stats, monster.Skills)).ToArray(),
            encounterDefinition.PlayerRespawnSeconds);
        expected.Player.Buffs.Add(new CombatBuffState("buff.test-haste", 2, 1, AttackSpeedPoints: 500));
        const double targetSeconds = 1.5;
        var expectedResult = DeterministicCombatEngine.AdvanceTo(expected, targetSeconds);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(state);
        runtime.EnqueueAction(PlayerIdValue, new ActionId(ActionId), -1);
        runtime.CreateEncounter(PlayerIdValue, EncounterId, Seed);
        await runtime.RunPlayerOnceAsync(PlayerIdValue, SeedTime.AddSeconds(targetSeconds));

        var encounterEntity = runtime.World.Entities.Values
            .Select(entity => (Entity: entity, Encounter: entity.GetComponent<CombatEncounterComponent>()))
            .Single(pair => pair.Encounter?.EncounterId == EncounterId);
        var actual = encounterEntity.Encounter!;

        Assert.Equal(expected.Outcome, actual.State.Outcome);
        Assert.Equal(expected.ElapsedSeconds, actual.State.ElapsedSeconds, 12);
        Assert.Equal(expected.Sequence, actual.State.Sequence);
        AssertCombatantEqual(expected.Player, actual.State.Player, "player");
        Assert.Equal(expected.Enemies.Count, actual.State.Enemies.Count);
        for (var i = 0; i < expected.Enemies.Count; i++)
            AssertCombatantEqual(expected.Enemies[i], actual.State.Enemies[i], $"enemy:{i}");

        // 事件必须保留同一时间线全部边界；只保留最后边界会丢失技能、普攻和冷却证据。
        var actualEvents = encounterEntity.Entity.GetComponent<CombatLogComponent>()?.Events ?? [];
        Assert.Equal(expectedResult.Events, actualEvents);
        Assert.Equal(expected.Player.Buffs.Count, actual.State.Player.Buffs.Count);
        Assert.Contains(actual.State.Player.Buffs, buff => buff.BuffId == "buff.test-haste");
        AssertQueueStillInfinite(runtime.ExportSnapshot(PlayerIdValue).State, new ActionId(ActionId));

        // 怪物 Entity 必须保留完整掉落配置，后续 RewardSystem 只能读取该实体配置。
        for (var i = 0; i < actual.EnemyEntityIds.Count; i++)
        {
            var monsterEntity = runtime.World.GetEntity(actual.EnemyEntityIds[i]);
            var profile = monsterEntity?.GetComponent<DropProfileComponent>();
            Assert.NotNull(profile);
            Assert.Equal(JsonSerializer.Serialize(monsters[i].DropTable), JsonSerializer.Serialize(profile!.Drops));
        }
    }

    [Fact]
    public async Task CombatActionSnapshotMatchesPublicOopSimulationBoundary()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var seedPlayer = PlayerAggregate.Create(new PlayerId(Guid.Parse("10000000-0000-4000-8000-000000000004")),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000004")), "Ecs行动", SeedTime, catalog.Fingerprint);
        seedPlayer.Enqueue(new ActionIntent(new ActionId(ActionId), 1), SeedTime, catalog.Actions);
        var seedState = seedPlayer.ExportState();
        var expectedPlayer = PlayerAggregate.Restore(seedState);
        SimulationEngine.AdvanceTo(expectedPlayer, SeedTime.AddSeconds(2), catalog);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seedState);
        await runtime.RunPlayerOnceAsync(seedPlayer.PlayerId.Value, SeedTime.AddSeconds(2));
        var actual = runtime.ExportSnapshot(seedPlayer.PlayerId.Value).State;

        Assert.Equal(expectedPlayer.Actions.Count, actual.Actions.Count);
        Assert.Equal(expectedPlayer.Actions[0].Expedition?.Phase, actual.Actions[0].Expedition?.Phase);
        Assert.Equal(JsonSerializer.Serialize(expectedPlayer.ExportState()), JsonSerializer.Serialize(actual));
    }

    private static void AssertCombatantEqual(CombatantState expected, CombatantState actual, string label)
    {
        var expectedSnapshot = CombatEncounterState.Snapshot(expected);
        var actualSnapshot = CombatEncounterState.Snapshot(actual);
        var expectedJson = JsonSerializer.Serialize(expectedSnapshot);
        var actualJson = JsonSerializer.Serialize(actualSnapshot);
        Assert.True(string.Equals(expectedJson, actualJson, StringComparison.Ordinal),
            $"战斗单位 {label} 状态不一致。expected={expectedJson} actual={actualJson}");
    }

    private static void AssertQueueStillInfinite(PlayerAggregateState state, ActionId actionId)
    {
        var action = Assert.Single(state.Actions, item => item.Intent.ActionId == actionId);
        Assert.Equal(-1, action.RemainingRepeats);
    }
}
