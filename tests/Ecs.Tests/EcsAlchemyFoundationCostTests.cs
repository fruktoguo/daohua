using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 炼丹底蕴成本通道（逆命丹）回归套件：配表 <c>foundationCostRatio</c> 与 Timed 行动的
/// 准入校验、单轮结算共用 <see cref="HeavenGateRules.ResolveFoundationCost"/> 同一口径。
/// 只验证真实不变量——底蕴不足被阻断、底蕴充足按 25% 扣减、比例 0 时行为完全不变。
/// </summary>
public sealed class EcsAlchemyFoundationCostTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private const string ActionIdValue = "alchemy.refine-reverse-fate-pill";
    private const string PillItemId = "item.reverse-fate-pill";

    /// <summary>
    /// 真实配表链路：alchemy.json 的逆命丹配方必须解析出 25% 底蕴成本，
    /// 且其余炼丹行动一律为 0（未声明即不启用通道）。
    /// </summary>
    [Fact]
    public void EmbeddedReverseFatePillRecipeDeclaresFoundationCostAndOthersDoNot()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.Equal(HeavenGateRules.ReverseFatePillFoundationRatio,
            catalog.Actions.Get(new ActionId(ActionIdValue)).FoundationCostRatio);
        Assert.Equal(0, catalog.Actions.Get(new ActionId("alchemy.refine-qi-pill")).FoundationCostRatio);
    }

    /// <summary>
    /// 底蕴不足：队首逆命丹炼制行动被阻断，不产丹、不推进轮次，底蕴余额保持原值（不被扣成负数）。
    /// </summary>
    [Fact]
    public async Task InsufficientFoundationBlocksTheRecipeWithoutSideEffects()
    {
        var catalog = CreateCatalog(HeavenGateRules.ReverseFatePillFoundationRatio);
        const double cost = 250; // expToNext 1000 × 25%
        var player = CreatePlayer(catalog);
        player.Progression.Foundation = cost - 1;
        player.Enqueue(new(new ActionId(ActionIdValue), 1), From, catalog.Actions);

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        var target = From.AddSeconds(10);
        await runtime.RunPlayerOnceAsync(id, target);

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Equal(cost - 1, snapshot.State.Progression.Foundation);
        Assert.Equal(0, snapshot.State.Progression.CompletedActions);
        Assert.Equal(0, snapshot.State.Actions[0].CompletedRepeats);
        Assert.Equal(ActionQueueStatus.Blocked, snapshot.State.Actions[0].Status);
        Assert.Equal(0, snapshot.Assets.GetValueOrDefault(new AssetKey(new ItemId(PillItemId))));
        Assert.Contains(runtime.DrainSimulationEvents(id), entry => entry.Code == "action.blocked");
    }

    /// <summary>
    /// 底蕴充足：单轮结算按「当前升级所需修为 × 25%」扣除底蕴并产出 1 颗逆命丹，
    /// 扣除量与领域解析逐位一致。
    /// </summary>
    [Fact]
    public async Task SufficientFoundationDeductsTwentyFivePercentExactly()
    {
        var catalog = CreateCatalog(HeavenGateRules.ReverseFatePillFoundationRatio);
        const double cost = 250;
        var player = CreatePlayer(catalog);
        player.Progression.Foundation = cost * 4;
        player.Enqueue(new(new ActionId(ActionIdValue), 1), From, catalog.Actions);

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        await runtime.RunPlayerOnceAsync(id, From.AddSeconds(10));

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Empty(snapshot.State.Actions);
        Assert.Equal(1, snapshot.State.Progression.CompletedActions);
        Assert.Equal(cost * 4 - cost, snapshot.State.Progression.Foundation, 6);
        Assert.Equal(1, snapshot.Assets.GetValueOrDefault(new AssetKey(new ItemId(PillItemId))));
    }

    /// <summary>
    /// 比例 0：行为完全不变——底蕴为 0 也照常炼制并产出，不产生任何底蕴扣减。
    /// 与同一配表的启用通道配方对照，只有比例差异决定是否阻断。
    /// </summary>
    [Fact]
    public async Task ZeroRatioRecipeLeavesFoundationUntouched()
    {
        var freeCatalog = CreateCatalog(0);
        Assert.Equal(0, freeCatalog.Actions.Get(new ActionId(ActionIdValue)).FoundationCostRatio);
        var player = CreatePlayer(freeCatalog);
        player.Progression.Foundation = 0;
        player.Enqueue(new(new ActionId(ActionIdValue), 1), From, freeCatalog.Actions);
        // 对照种子必须在对局开始前取样：跑完后行动已出队，无法再复现同一队列。
        var seed = player.ExportState();

        using var runtime = new EcsGameRuntime(freeCatalog, From);
        var id = runtime.CreatePlayer(seed);
        await runtime.RunPlayerOnceAsync(id, From.AddSeconds(10));

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Empty(snapshot.State.Actions);
        Assert.Equal(1, snapshot.State.Progression.CompletedActions);
        Assert.Equal(0, snapshot.State.Progression.Foundation);
        Assert.Equal(1, snapshot.Assets.GetValueOrDefault(new AssetKey(new ItemId(PillItemId))));

        // 对照：同一配方只把比例改为 25%，同样底蕴为 0 时被阻断。
        var paidCatalog = CreateCatalog(HeavenGateRules.ReverseFatePillFoundationRatio);
        using var paidRuntime = new EcsGameRuntime(paidCatalog, From);
        var paidId = paidRuntime.CreatePlayer(seed);
        await paidRuntime.RunPlayerOnceAsync(paidId, From.AddSeconds(10));
        var paid = paidRuntime.ExportSnapshot(paidId);
        Assert.Equal(0, paid.State.Progression.Foundation);
        Assert.Equal(0, paid.State.Progression.CompletedActions);
        Assert.Equal(ActionQueueStatus.Blocked, paid.State.Actions[0].Status);
    }

    /// <summary>
    /// 零底蕴成本行动（如引气入体、普通炼丹）即便遇到历史快照下溢带负底蕴（如 -3.55e-15），
    /// 准入与单轮完成结算也恒放行，绝对不报「底蕴不足」阻断，且结算后底蕴自动自愈归零。
    /// </summary>
    [Fact]
    public async Task ZeroRatioActionWithUnderflowFoundationStartsAndCompletesSuccessfully()
    {
        var freeCatalog = CreateCatalog(0);
        var player = CreatePlayer(freeCatalog);
        player.Enqueue(new(new ActionId(ActionIdValue), 1), From, freeCatalog.Actions);
        var seed = player.ExportState();
        // 模拟极端脏输入：Progression 持久化快照带着 -3.55e-15
        var dirtyState = seed with
        {
            Progression = seed.Progression with { Foundation = -3.552713678800501e-15 }
        };

        using var runtime = new EcsGameRuntime(freeCatalog, From);
        var id = runtime.CreatePlayer(dirtyState);
        await runtime.RunPlayerOnceAsync(id, From.AddSeconds(10));

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Empty(snapshot.State.Actions);
        Assert.Equal(1, snapshot.State.Progression.CompletedActions);
        Assert.Equal(0, snapshot.State.Progression.Foundation);
        Assert.Equal(1, snapshot.Assets.GetValueOrDefault(new AssetKey(new ItemId(PillItemId))));
        Assert.DoesNotContain(runtime.DrainSimulationEvents(id), entry => entry.Code == "action.blocked");
    }

    /// <summary>解析器口径：非数值/越界配置按 0 回落，缺省字段亦为 0。</summary>
    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("0.05", 0.05)]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData("\"0.25\"", 0)]
    [InlineData("-0.2", 0)]
    [InlineData("2", 0)]
    public void ParserAcceptsOnlyUnitIntervalRatios(string raw, double expected)
    {
        var header = System.Text.Encoding.UTF8.GetBytes("""
        {
          "contentVersion": "test",
          "realmProgression": { "expToNext": [1000, 1000], "lifespanYearsByTier": [80] }
        }
        """);
        var body = System.Text.Encoding.UTF8.GetBytes($$"""
        [
          { "id": "item.test-material", "name": "料", "description": "" },
          {
            "id": "alchemy.test-cost",
            "name": "测试丹",
            "type": "alchemy",
            "durationSeconds": 10,
            "foundationCostRatio": {{raw}},
            "inputs": {},
            "yields": [{ "kind": "item", "per": "repeat", "id": "item.test-material", "amount": 1 }]
          }
        ]
        """);
        var catalog = GameDataCatalog.Parse([header, body]);
        Assert.Equal(expected, catalog.Actions.Get(new ActionId("alchemy.test-cost")).FoundationCostRatio, 9);
    }

    /// <summary>
    /// 单配方测试目录：expToNext[1] = 1000，故 25% 底蕴成本的绝对值恒为 250；
    /// 炼丹配方无材料输入、只产 1 颗逆命丹，隔离出底蕴通道本身的行为。
    /// </summary>
    private static GameDataCatalog CreateCatalog(double foundationCostRatio)
    {
        var actions = new ActionCatalog([
            new(new ActionId(ActionIdValue), "逆命丹", ActionKind.Alchemy, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, new ItemId(PillItemId))],
                Technique: "alchemy",
                FoundationCostRatio: foundationCostRatio)
        ]);
        return new GameDataCatalog("test", "test", actions,
            items: new Dictionary<ItemId, ItemDefinition>
            {
                [new ItemId(PillItemId)] = new(new ItemId(PillItemId), "逆命丹", "", IsTradable: false)
            },
            realmProgression: new RealmProgressionCatalog([1000, 1000], [80]));
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "炼丹底蕴", From, catalog.Fingerprint);
        // 种子按「已断线存档」播种：装载规范化对持久化 Online 一律转离线。
        player.SetPresence(PresenceState.Offline, From);
        return player;
    }
}
