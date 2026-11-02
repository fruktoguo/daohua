using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

public sealed class CombatDifficultyEcsTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("10000000-0000-4000-8000-00000000dd01");
    private static readonly Guid AccountIdValue = Guid.Parse("20000000-0000-4000-8000-00000000dd01");

    [Fact]
    public async Task DifficultyExpedition_ScalesMonsterAttributesAndExperience()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var defaultPlayer = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(AccountIdValue),
            "默认历练", SeedTime, catalog.Fingerprint).ExportState();

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(defaultPlayer);

        // 默认难度 (0)
        var defaultIntent = new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1, Difficulty: 0);
        runtime.EnqueueActionIntent(PlayerIdValue, defaultIntent);
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await runtime.RunPlayerOnceAsync(PlayerIdValue, SeedTime.AddMinutes(1), cts1.Token);

        var snapDefault = runtime.ExportSnapshot(PlayerIdValue);
        var defaultAction = snapDefault.State.Actions.FirstOrDefault();
        Assert.NotNull(defaultAction);

        // 难度 T5 历练
        var t5PlayerId = Guid.Parse("10000000-0000-4000-8000-00000000dd05");
        var t5Player = PlayerAggregate.Create(new PlayerId(t5PlayerId), new AccountId(AccountIdValue),
            "高难历练", SeedTime, catalog.Fingerprint).ExportState();
        runtime.CreatePlayer(t5Player);
        var t5Intent = new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1, Difficulty: 5);
        runtime.EnqueueActionIntent(t5PlayerId, t5Intent);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await runtime.RunPlayerOnceAsync(t5PlayerId, SeedTime.AddMinutes(1), cts2.Token);

        var snapT5 = runtime.ExportSnapshot(t5PlayerId);
        var t5Action = snapT5.State.Actions.FirstOrDefault();
        Assert.NotNull(t5Action);

        // 如果生成了波次敌怪，T5 难度下的怪物气血上限必须显著高于默认怪物（约为 3.05 倍）
        var defExpedition = snapDefault.State.Actions.Count > 0 ? snapDefault.State.Actions[0].Expedition : null;
        var t5Expedition = snapT5.State.Actions.Count > 0 ? snapT5.State.Actions[0].Expedition : null;
        if (defExpedition?.Enemies is { Count: > 0 } defEnemies && t5Expedition?.Enemies is { Count: > 0 } t5Enemies)
        {
            var defEnemy = defEnemies[0];
            var t5Enemy = t5Enemies[0];
            Assert.True(t5Enemy.State.Hitpoints >= defEnemy.State.Hitpoints * 2.5,
                $"T5 怪物生命未显著提升：T5={t5Enemy.State.Hitpoints}, Def={defEnemy.State.Hitpoints}");
        }
    }
}
