using System.Text.Json;
using Domain.GameData;
using Domain.Player;

namespace Domain.Market;

/// <summary>
/// 坊市市场参考价（referencePrice）量次复合平滑模型调参（config/game-data/market.json，id = market.settings）。
/// 核心模型：单笔冲击当量 ΔW = TradeBaseWeight + Q；单笔权重 α = min(MaxSingleTradeWeight, ΔW / (Capacity * GlobalMultiplier))。
/// </summary>
/// <param name="TradeBaseWeight">单笔成交基础底数 Q_base（默认 15）：控制交易频次与成交数量的相对权重；当 Q_base=15 时严格满足「3次各10个 = 1次60个」</param>
/// <param name="GlobalMultiplier">全局容量调速倍率（默认 1.0）：>1.0 更保守慢速收敛，<1.0 更灵敏快速收敛</param>
/// <param name="MaxSingleTradeWeight">单笔交易最大权重保护 α_max（默认 0.5）：杜绝单笔极端大单瞬时击穿大盘均价</param>
/// <param name="EnhancedEquipmentCapacity">强化装备（+N，N>0）目标收敛容量（默认 48，Q=1 时 ΔW=16，对应 3 次成交收敛）</param>
/// <param name="EquipmentCapacity">基础白板装备（+0）目标收敛容量（默认 96，Q=1 时 ΔW=16，对应 6 次成交收敛）</param>
/// <param name="ConsumableCapacity">丹药与消耗品目标收敛容量（默认 240，对应约 12~15 次交易收敛）</param>
/// <param name="ManualCapacity">功法秘籍与残卷目标收敛容量（默认 240，对应约 15 次交易收敛）</param>
/// <param name="MaterialCapacity">基础与精炼材料目标收敛容量（默认 1000，大容量抗噪防刷单操纵）</param>
/// <param name="DefaultCapacity">未明确分类物品的兜底目标收敛容量（默认 300）</param>
public sealed record MarketReferencePriceSettings(
    double TradeBaseWeight = 15.0,
    double GlobalMultiplier = 1.0,
    double MaxSingleTradeWeight = 0.5,
    double EnhancedEquipmentCapacity = 48.0,
    double EquipmentCapacity = 96.0,
    double ConsumableCapacity = 240.0,
    double ManualCapacity = 240.0,
    double MaterialCapacity = 1000.0,
    double DefaultCapacity = 300.0)
{
    /// <summary>配表缺失时的内置默认配置。</summary>
    public static MarketReferencePriceSettings Default { get; } = new();

    /// <summary>从 JSON 对象解析市场参考价设置；字段缺失时自动回退默认值。</summary>
    public static MarketReferencePriceSettings Parse(JsonElement element)
    {
        var targetElement = element.TryGetProperty("referencePrice", out var refProp) && refProp.ValueKind == JsonValueKind.Object
            ? refProp
            : element;

        var capacitiesElement = targetElement.TryGetProperty("targetCapacities", out var capProp) && capProp.ValueKind == JsonValueKind.Object
            ? capProp
            : targetElement;

        var settings = new MarketReferencePriceSettings(
            TradeBaseWeight: JsonRead.Number(targetElement, "tradeBaseWeight", 15.0),
            GlobalMultiplier: JsonRead.Number(targetElement, "globalMultiplier", 1.0),
            MaxSingleTradeWeight: JsonRead.Number(targetElement, "maxSingleTradeWeight", 0.5),
            EnhancedEquipmentCapacity: JsonRead.Number(capacitiesElement, "enhancedEquipment", 48.0),
            EquipmentCapacity: JsonRead.Number(capacitiesElement, "equipment", 96.0),
            ConsumableCapacity: JsonRead.Number(capacitiesElement, "consumable", 240.0),
            ManualCapacity: JsonRead.Number(capacitiesElement, "manual", 240.0),
            MaterialCapacity: JsonRead.Number(capacitiesElement, "material", 1000.0),
            DefaultCapacity: JsonRead.Number(capacitiesElement, "default", 300.0));

        if (!double.IsFinite(settings.TradeBaseWeight) || settings.TradeBaseWeight < 0
            || !double.IsFinite(settings.GlobalMultiplier) || settings.GlobalMultiplier <= 0
            || !double.IsFinite(settings.MaxSingleTradeWeight) || settings.MaxSingleTradeWeight is <= 0 or > 1.0
            || !double.IsFinite(settings.EnhancedEquipmentCapacity) || settings.EnhancedEquipmentCapacity <= 0
            || !double.IsFinite(settings.EquipmentCapacity) || settings.EquipmentCapacity <= 0
            || !double.IsFinite(settings.ConsumableCapacity) || settings.ConsumableCapacity <= 0
            || !double.IsFinite(settings.ManualCapacity) || settings.ManualCapacity <= 0
            || !double.IsFinite(settings.MaterialCapacity) || settings.MaterialCapacity <= 0
            || !double.IsFinite(settings.DefaultCapacity) || settings.DefaultCapacity <= 0)
        {
            throw new DomainException(DomainErrorCode.InvalidArgument, "市场参考价调参配置无效。");
        }

        return settings;
    }
}

/// <summary>
/// 坊市物品市场参考价格（referencePrice）量次复合加权平滑规则引擎。
/// 将单笔交易行为（频次）与流转体量（数量）有机结合，并按标的品类提供自适应收敛特性。
/// </summary>
public static class ReferencePriceRules
{
    /// <summary>品类标识常量</summary>
    public const string CategoryEnhancedEquipment = "enhancedEquipment";
    public const string CategoryEquipment = "equipment";
    public const string CategoryConsumable = "consumable";
    public const string CategoryManual = "manual";
    public const string CategoryMaterial = "material";
    public const string CategoryDefault = "default";

    /// <summary>
    /// 解算标的物品所属的平滑品类。
    /// 优先级：强化装备（+N, N>0）> 基础装备 > 消耗品 > 秘籍残卷 > 材料 > 兜底。
    /// </summary>
    public static string ResolveCategory(string assetId, string? variant, GameDataCatalog? gameData)
    {
        var level = EnhancementRules.ParseLevel(variant);
        var isEnhanced = level is > 0;

        var isEquipment = (gameData is not null && gameData.RawEquipments.ContainsKey(assetId))
            || assetId.StartsWith("equipment.", StringComparison.Ordinal);

        if (isEnhanced || (isEquipment && level is > 0))
        {
            return CategoryEnhancedEquipment;
        }

        if (isEquipment)
        {
            return CategoryEquipment;
        }

        if (gameData is not null && gameData.Items.TryGetValue(new ItemId(assetId), out var item))
        {
            if (string.Equals(item.Category, "consumable", StringComparison.OrdinalIgnoreCase))
                return CategoryConsumable;
            if (string.Equals(item.Category, "manual", StringComparison.OrdinalIgnoreCase)
                || assetId.StartsWith("item.tome.", StringComparison.Ordinal)
                || assetId.StartsWith("item.fragment.", StringComparison.Ordinal))
                return CategoryManual;
            if (string.Equals(item.Category, "material", StringComparison.OrdinalIgnoreCase))
                return CategoryMaterial;
        }

        // 无配表时的纯标识降级解析（单元测试或最小上下文）
        if (assetId.StartsWith("item.tome.", StringComparison.Ordinal) || assetId.StartsWith("item.fragment.", StringComparison.Ordinal))
            return CategoryManual;
        if (assetId.StartsWith("item.pill-", StringComparison.Ordinal) || assetId.Contains("pill"))
            return CategoryConsumable;
        if (assetId.StartsWith("item.", StringComparison.Ordinal))
            return CategoryMaterial;

        return CategoryDefault;
    }

    /// <summary>
    /// 获取指定品类的基准目标收敛容量。
    /// </summary>
    public static double GetTargetCapacity(MarketReferencePriceSettings settings, string category)
    {
        return category switch
        {
            CategoryEnhancedEquipment => settings.EnhancedEquipmentCapacity,
            CategoryEquipment => settings.EquipmentCapacity,
            CategoryConsumable => settings.ConsumableCapacity,
            CategoryManual => settings.ManualCapacity,
            CategoryMaterial => settings.MaterialCapacity,
            _ => settings.DefaultCapacity
        };
    }

    /// <summary>
    /// 计算指定单笔交易对市场参考价的迭代权重系数 α。
    /// </summary>
    /// <param name="assetId">标的道具 ID。</param>
    /// <param name="variant">变体标识（强化等级如 "+10"）。</param>
    /// <param name="quantity">成交数量。</param>
    /// <param name="gameData">配表目录（可选）。</param>
    /// <param name="settings">平滑配置（可选，缺省取默认值）。</param>
    /// <returns>平滑权重 α ∈ (0, α_max]。</returns>
    public static double CalculateTradeWeight(
        string assetId,
        string? variant,
        double quantity,
        GameDataCatalog? gameData,
        MarketReferencePriceSettings? settings = null)
    {
        var config = settings ?? gameData?.MarketReferencePrice ?? MarketReferencePriceSettings.Default;
        var category = ResolveCategory(assetId, variant, gameData);
        var baseCapacity = GetTargetCapacity(config, category);

        var validQuantity = double.IsFinite(quantity) && quantity > 0 ? quantity : 1.0;
        var impactEnergy = config.TradeBaseWeight + validQuantity;
        var targetCapacity = baseCapacity * config.GlobalMultiplier;

        if (!double.IsFinite(targetCapacity) || targetCapacity <= 0)
        {
            targetCapacity = 300.0;
        }

        var rawWeight = impactEnergy / targetCapacity;
        var clampedWeight = Math.Clamp(rawWeight, 0.001, config.MaxSingleTradeWeight);
        return clampedWeight;
    }

    /// <summary>
    /// 根据既有均价、最新成交价与单笔计算出的权重 α 计算下一代参考价格。
    /// </summary>
    /// <param name="currentReferencePrice">当前参考价格；无历史记录传入 null 或非有限数。</param>
    /// <param name="fillPrice">最新成交单价。</param>
    /// <param name="weight">单笔成交权重 α。</param>
    public static double CalculateNextReferencePrice(double? currentReferencePrice, double fillPrice, double weight)
    {
        if (!double.IsFinite(fillPrice) || fillPrice <= 0)
        {
            return currentReferencePrice is { } cur && double.IsFinite(cur) && cur > 0 ? cur : 0;
        }

        if (currentReferencePrice is not { } oldPrice || !double.IsFinite(oldPrice) || oldPrice <= 0)
        {
            return fillPrice;
        }

        var validWeight = Math.Clamp(weight, 0.0, 1.0);
        return oldPrice * (1.0 - validWeight) + fillPrice * validWeight;
    }

    /// <summary>
    /// 一体化计算下一代参考价格。
    /// </summary>
    public static double CalculateNextReferencePrice(
        double? currentReferencePrice,
        double fillPrice,
        string assetId,
        string? variant,
        double quantity,
        GameDataCatalog? gameData,
        MarketReferencePriceSettings? settings = null)
    {
        var weight = CalculateTradeWeight(assetId, variant, quantity, gameData, settings);
        return CalculateNextReferencePrice(currentReferencePrice, fillPrice, weight);
    }
}
