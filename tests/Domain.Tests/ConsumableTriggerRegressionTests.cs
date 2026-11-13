using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using DaoHua.Ecs;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 单人即时消耗品（雷击符/丹药）条件求值回归门禁：
/// 1. 怪物阶层条件（target-enemy is-boss/is-elite 与 enemies has-boss/has-elite）真实生效；
/// 2. 非零难度缩放下敌方生命/灵力百分比与伤害判定使用缩放后面板；
/// 3. 战内即时丹药同时落入 ActiveBuff 与战内 Buff 时 buff-stacks 严格计为 1 层且过期归零。
/// </summary>
public sealed class ConsumableTriggerRegressionTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (AssetKey Asset, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, balance.Asset, balance.Quantity, 0)));
        return wallet;
    }

    private static (GameDataCatalog Catalog, ItemId StrikeId, ItemId PillId) BuildCatalog(
        CombatMonsterRank enemyRank,
        double enemyHitpoints = 100_000)
    {
        var strike = new ItemId("item.test-leiji");
        var pill = new ItemId("item.test-zhixue");
        var strikeJson = """
            {"id":"item.test-leiji","name":"测试雷击符","kind":"consumable","category":"talisman",
             "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,
             "strike":{"manaCostRatio":0.05,"manaDamageRatio":0.2,"spellAttackRatio":2.0,"element":"wood"}}}
            """;
        var pillJson = """
            {"id":"item.test-zhixue","name":"测试止血散","kind":"consumable","category":"consumable","subCategory":"pill",
             "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,
             "buffs":[{"buffId":"buff.zhixue","name":"止血散·回春","durationSeconds":30,"hpRegen":10,"damageAbsorptionPoints":5}]}}
            """;
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [strike] = new(strike, "测试雷击符", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Strike: new ConsumableStrike(0.05, 0.2, 2.0, CombatElement.Wood),
                    CooldownSeconds: 60),
                "talisman"),
            [pill] = new(pill, "测试止血散", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    CooldownSeconds: 60),
                "consumable", SubCategory: "pill"),
        };
        var rawItems = new Dictionary<string, JsonElement>
        {
            [strike.Value] = Json(strikeJson),
            [pill.Value] = Json(pillJson),
        };
        var monsters = new Dictionary<string, CombatMonsterDefinition>
        {
            ["enemy.primary"] = new("enemy.primary", "主敌", 1, 1, enemyRank,
                CombatantStats.Create(enemyHitpoints, 0, 0, 2), [], [])
        };
        var spawns = new List<CombatSpawnDefinition> { new("enemy.primary", 1, 1) };
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, 1, 1, 180,
            spawns, [], [], 0);
        var catalog = new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: monsters,
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
        return (catalog, strike, pill);
    }

    [Theory]
    [InlineData(CombatMonsterRank.Boss, """{"dependency":"target-enemy","condition":"is-boss","comparator":"is-active"}""", true)]
    [InlineData(CombatMonsterRank.Minion, """{"dependency":"target-enemy","condition":"is-boss","comparator":"is-active"}""", false)]
    [InlineData(CombatMonsterRank.Elite, """{"dependency":"target-enemy","condition":"is-elite","comparator":"is-active"}""", true)]
    [InlineData(CombatMonsterRank.Minion, """{"dependency":"target-enemy","condition":"is-elite","comparator":"is-active"}""", false)]
    [InlineData(CombatMonsterRank.Boss, """{"dependency":"enemies","condition":"has-boss","comparator":"is-active"}""", true)]
    [InlineData(CombatMonsterRank.Minion, """{"dependency":"enemies","condition":"has-boss","comparator":"is-active"}""", false)]
    [InlineData(CombatMonsterRank.Elite, """{"dependency":"enemies","condition":"has-elite","comparator":"is-active"}""", true)]
    [InlineData(CombatMonsterRank.Minion, """{"dependency":"enemies","condition":"has-elite","comparator":"is-active"}""", false)]
    public void StrikeTalisman_EvaluatesMonsterRankConditionsAccurately(
        CombatMonsterRank enemyRank,
        string triggerJson,
        bool expectedActivated)
    {
        var (catalog, strike, _) = BuildCatalog(enemyRank);
        var player = CreatePlayer("符修");
        player.Loadout.ConsumableTriggers[strike.Value] = [Json(triggerJson)];
        var wallet = CreateWallet(player, (new AssetKey(strike), 1));
        player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(expectedActivated, result.Events.Any(e => e.Code == "auto-use.activated"));
        Assert.Equal(expectedActivated ? 0 : 1, wallet.Get(new AssetKey(strike)));
    }

    [Fact]
    public void StrikeTalisman_UsesScaledMonsterStatsUnderDifficultyForPercentageTriggers()
    {
        var (catalog, strike, _) = BuildCatalog(CombatMonsterRank.Minion);
        // 难度 +2 会放大敌人实际生命值：若求值单位忘记做难度缩放，Hitpoints / unscaledMaxHitpoints > 100%，
        // 将错误导致「current-hp-percentage <= 100」与「missing-hp-percentage >= 0」在满血时均为 false。
        var hpTrigger = Json("""{"dependency":"target-enemy","condition":"current-hp-percentage","comparator":"less-than-equal","value":100}""");
        var missingTrigger = Json("""{"dependency":"target-enemy","condition":"missing-hp-percentage","comparator":"greater-than-equal","value":0}""");
        var totalTrigger = Json("""{"dependency":"enemies","condition":"total-hp-percentage","comparator":"less-than-equal","value":100}""");
        var player = CreatePlayer("难度测试");
        player.Loadout.ConsumableTriggers[strike.Value] = [hpTrigger, missingTrigger, totalTrigger];
        var wallet = CreateWallet(player, (new AssetKey(strike), 1));
        player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, Difficulty: 2), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        Assert.Equal(0, wallet.Get(new AssetKey(strike)));
    }

    [Fact]
    public void InstantBuffPill_CountsSingleBuffStackInCombatAndExpiresCleanly()
    {
        var (catalog, strike, pill) = BuildCatalog(CombatMonsterRank.Boss, enemyHitpoints: 500_000);
        var player = CreatePlayer("丹符双修");
        // 槽 0：止血散（仅在无 buff.zhixue 时使用）；槽 1：雷击符（严格要求 buff.zhixue 层数 == 1）
        // 若存在双计 Bug，吃药后 buff-stacks 变成 2，雷击符永远无法触发。
        player.Loadout.ConsumableTriggers[pill.Value] = [
            Json("""{"dependency":"self","condition":"buff-stacks","buffId":"buff.zhixue","comparator":"equal","value":0}""")
        ];
        player.Loadout.ConsumableTriggers[strike.Value] = [
            Json("""{"dependency":"self","condition":"buff-stacks","buffId":"buff.zhixue","comparator":"equal","value":1}""")
        ];
        var wallet = CreateWallet(player, (new AssetKey(pill), 2), (new AssetKey(strike), 2));
        player.ConfigureAutoUse("combat", 0, pill.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 先推进 2 秒，进入战斗后止血散与雷击符应各触发 1 次（且仅 1 次）。
        var first = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog, wallet);
        first.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(1, wallet.Get(new AssetKey(pill)));
        Assert.Equal(1, wallet.Get(new AssetKey(strike)));
        Assert.Single(player.Buffs.Active, b => b.BuffId == "buff.zhixue");

        // 再推进到第 40 秒：buff 30 秒持续期结束且战外 ActiveBuff 已到期。
        var second = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), catalog, wallet);
        second.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.DoesNotContain(player.Buffs.Active, b => b.BuffId == "buff.zhixue");
    }
}
