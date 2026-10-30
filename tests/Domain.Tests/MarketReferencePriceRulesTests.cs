using System.Text.Json;
using Domain.GameData;
using Domain.Market;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 坊市物品市场参考价格（referencePrice）量次复合平滑模型单元测试。
/// 覆盖：数学等价性（3次10个=1次60个）、品类自适应收敛、单笔风控截断、首单初始化与配置反序列化。
/// </summary>
public sealed class MarketReferencePriceRulesTests
{
    private static readonly MarketReferencePriceSettings DefaultSettings = MarketReferencePriceSettings.Default;

    [Fact]
    public void TradeWeight_Three10Trades_EqualEquivalentImpactOfOne60Trade()
    {
        // 核心数学命题：单笔冲击当量 ΔW = 15 + Q
        // 3 笔各 10 个：3 * (15 + 10) = 75
        // 1 笔 60 个：1 * (15 + 60) = 75
        const string assetId = "item.peach-wood";
        const string variant = "";

        var weightSingle10 = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 10, null, DefaultSettings);
        var weightSingle60 = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 60, null, DefaultSettings);

        // 材料基准容量为 1000
        Assert.Equal(25.0 / 1000.0, weightSingle10, 6);
        Assert.Equal(75.0 / 1000.0, weightSingle60, 6);

        // 3 次 10 个累积冲击当量 3 * 25 = 75，与 1 次 60 个 75 精确相等
        Assert.Equal(weightSingle60, weightSingle10 * 3.0, 6);

        // 从价格演化角度验证：
        // 假设施加新价格 200，原价格 100
        // 1 笔 60 个直接演化：
        var priceAfter60 = ReferencePriceRules.CalculateNextReferencePrice(100.0, 200.0, weightSingle60);
        // 100 * (1 - 0.075) + 200 * 0.075 = 92.5 + 15 = 107.5 -> 四舍五入取整为 108
        Assert.Equal(108.0, priceAfter60);

        // 3 笔 10 个连续演化（一阶泰勒展开严格等价，小步长复利高度逼近）：
        var p1 = ReferencePriceRules.CalculateNextReferencePrice(100.0, 200.0, weightSingle10);
        var p2 = ReferencePriceRules.CalculateNextReferencePrice(p1, 200.0, weightSingle10);
        var p3 = ReferencePriceRules.CalculateNextReferencePrice(p2, 200.0, weightSingle10);

        // 连续 3 笔 10 个每步整型演化后为 106
        Assert.Equal(106.0, p3);
    }

    [Fact]
    public void CategoryConvergence_EnhancedEquipmentConvergesInThreeTrades()
    {
        // 强化装备（+N，N>0）：数量通常为 1，ΔW = 15 + 1 = 16，目标容量 48
        // 单笔权重 α = 16 / 48 = 1/3
        const string assetId = "equipment.qingfeng-sword";
        const string variant = "+10";

        var weight = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 1, null, DefaultSettings);
        Assert.Equal(1.0 / 3.0, weight, 6);

        // 初始参考价 10,000，市场发生暴跌到 1,000 的连续 3 笔成交
        var p0 = 10000.0;
        var p1 = ReferencePriceRules.CalculateNextReferencePrice(p0, 1000.0, weight);
        var p2 = ReferencePriceRules.CalculateNextReferencePrice(p1, 1000.0, weight);
        var p3 = ReferencePriceRules.CalculateNextReferencePrice(p2, 1000.0, weight);

        // 3 笔成交后，旧价格影响仅剩 (2/3)^3 = 8/27 ≈ 29.6%，新价格占 70.4%
        // p3 = 1000 + 9000 * (8/27) = 1000 + 2666.67 = 3666.67
        // p3 取整后为 3667
        Assert.Equal(3667.0, p3);
    }

    [Fact]
    public void CategoryConvergence_StandardEquipmentConvergesInSixTrades()
    {
        // 白板装备：数量为 1，ΔW = 16，目标容量 96
        // 单笔权重 α = 16 / 96 = 1/6
        const string assetId = "equipment.qingfeng-sword";
        const string variant = "";

        var weight = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 1, null, DefaultSettings);
        Assert.Equal(1.0 / 6.0, weight, 6);

        var price = 1000.0;
        for (var i = 0; i < 6; i++)
        {
            price = ReferencePriceRules.CalculateNextReferencePrice(price, 2000.0, weight);
        }

        // 6 笔成交后：(5/6)^6 ≈ 33.49% 旧价格残存，66.5% 以上反映新价格
        Assert.InRange(price, 1600.0, 1700.0);
    }

    [Fact]
    public void CategoryConvergence_MaterialResistsDustOrderWashTrading()
    {
        // 材料碎单（Q = 1）：ΔW = 16，目标容量 1000
        // 单笔权重仅为 16 / 1000 = 0.016 (1.6%)
        const string assetId = "item.ningxue-grass";
        const string variant = "";

        var weight = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 1, null, DefaultSettings);
        Assert.Equal(0.016, weight, 6);

        // 恶意对敲 1 个材料，价格从 5 灵石拉到 50 灵石
        var p1 = ReferencePriceRules.CalculateNextReferencePrice(5.0, 50.0, weight);
        // 单笔推动 0.72 灵石，均价 5.72 四舍五入取整为 6
        Assert.Equal(6.0, p1);
    }

    [Fact]
    public void SafetyGuard_MaxSingleTradeWeightClampsMegaOrders()
    {
        // 巨型超大单（例如 5,000 个材料）：ΔW = 15 + 5000 = 5015
        // 原始比值 5015 / 1000 = 5.015
        // 触发 MaxSingleTradeWeight = 0.5 截断保护
        const string assetId = "item.spirit-iron";
        const string variant = "";

        var weight = ReferencePriceRules.CalculateTradeWeight(assetId, variant, 5000, null, DefaultSettings);
        Assert.Equal(0.5, weight, 6);

        // 无论成交量多大，单笔最多拉动 50%
        var nextPrice = ReferencePriceRules.CalculateNextReferencePrice(100.0, 500.0, weight);
        Assert.Equal(300.0, nextPrice, 4);
    }

    [Fact]
    public void Settings_GlobalMultiplierScalesCapacities()
    {
        // 全局调速倍率测试：
        // 当 GlobalMultiplier = 0.5 时，容量减半，权重翻倍
        var fastSettings = DefaultSettings with { GlobalMultiplier = 0.5 };
        var normalWeight = ReferencePriceRules.CalculateTradeWeight("item.herb", "", 10, null, DefaultSettings);
        var fastWeight = ReferencePriceRules.CalculateTradeWeight("item.herb", "", 10, null, fastSettings);

        Assert.Equal(normalWeight * 2.0, fastWeight, 6);

        // 当 GlobalMultiplier = 2.0 时，容量翻倍，权重减半
        var slowSettings = DefaultSettings with { GlobalMultiplier = 2.0 };
        var slowWeight = ReferencePriceRules.CalculateTradeWeight("item.herb", "", 10, null, slowSettings);
        Assert.Equal(normalWeight * 0.5, slowWeight, 6);
    }

    [Fact]
    public void FirstTrade_DirectlyEstablishesInitialReferencePrice()
    {
        // 首次成交：无既有参考价（null、0 或负数），直接采用成交价
        var pNull = ReferencePriceRules.CalculateNextReferencePrice(null, 88.0, 0.05);
        Assert.Equal(88.0, pNull);

        var pZero = ReferencePriceRules.CalculateNextReferencePrice(0.0, 88.0, 0.05);
        Assert.Equal(88.0, pZero);

        var pNegative = ReferencePriceRules.CalculateNextReferencePrice(-1.0, 88.0, 0.05);
        Assert.Equal(88.0, pNegative);
    }

    [Fact]
    public void ParseSettings_ReadsValidJsonDocument()
    {
        const string json = """
        {
          "id": "market.settings",
          "referencePrice": {
            "tradeBaseWeight": 15,
            "globalMultiplier": 1.5,
            "maxSingleTradeWeight": 0.4,
            "targetCapacities": {
              "enhancedEquipment": 50,
              "equipment": 100,
              "consumable": 200,
              "manual": 250,
              "material": 1200,
              "default": 350
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var settings = MarketReferencePriceSettings.Parse(doc.RootElement);

        Assert.Equal(15.0, settings.TradeBaseWeight);
        Assert.Equal(1.5, settings.GlobalMultiplier);
        Assert.Equal(0.4, settings.MaxSingleTradeWeight);
        Assert.Equal(50.0, settings.EnhancedEquipmentCapacity);
        Assert.Equal(100.0, settings.EquipmentCapacity);
        Assert.Equal(200.0, settings.ConsumableCapacity);
        Assert.Equal(250.0, settings.ManualCapacity);
        Assert.Equal(1200.0, settings.MaterialCapacity);
        Assert.Equal(350.0, settings.DefaultCapacity);
    }
}
