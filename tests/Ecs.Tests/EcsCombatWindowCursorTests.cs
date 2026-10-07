using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 战斗段提前终局的外层游标回归：跨窗口存活的战斗在新窗口内提前胜利时，
/// 会话游标停在胜利时刻而承诺边界停在窗口末；外层游标必须跟随会话真实游标，
/// 否则下一波搜寻的合法边界会被误判为「ECS 自然边界时钟发生倒退」。
/// </summary>
public sealed class EcsCombatWindowCursorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(50)]
    public async Task CombatWaveWinInsideWindowKeepsOuterCursorMonotonic(double enemyHitpoints)
    {
        var catalog = CreateCatalog(enemyHitpoints, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "游标回归", Start, catalog.Fingerprint);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Start, catalog.Actions);

        using var runtime = new EcsGameRuntime(catalog, Start);
        var id = runtime.CreatePlayer(player.ExportState());
        var exception = await Record.ExceptionAsync(() => runtime.RunPlayerOnceAsync(id, Start.AddSeconds(12)).AsTask());
        Assert.Null(exception);

        // 场景真实性门禁：12 秒内必须完成至少一波讨伐，证明战斗确实跨窗口并提前终局，
        // 而不是构造恰好空转通过。
        var state = runtime.ExportSnapshot(id).State;
        Assert.True(state.Actions[0].CompletedRepeats >= 1,
            $"战斗未在窗口内提前终局：completedRepeats={state.Actions[0].CompletedRepeats}");
    }

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower, double searchSeconds = 1)
    {
        var actions = new ActionCatalog([
            new ActionDefinition(
                new ActionId("combat.test"),
                "讨伐测试",
                ActionKind.Combat,
                0,
                new Dictionary<ItemId, double>(),
                [],
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.test",
            "测试妖兽",
            1,
            1,
            CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2),
            [],
            []);
        var encounter = new CombatEncounterDefinition(
            "encounter.test",
            "测试遭遇",
            1,
            1,
            1,
            searchSeconds,
            1,
            180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)],
            [],
            [],
            0);
        return new GameDataCatalog(
            "test-1",
            "test-fingerprint",
            actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
    }
}
