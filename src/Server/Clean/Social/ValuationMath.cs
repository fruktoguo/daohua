using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Domain.GameData;
using Domain.Market;
using Domain.Player;

namespace Server.Clean.Social;

/// <summary>
/// 身家估值纯计算辅助（单一计算真源）：
/// 供 <see cref="PartyValuationService"/>（组队/名片）与全服排行榜整点物化共用，确保全游戏身家口径完全一致。
/// </summary>
public static class ValuationMath
{
    /// <summary>功法 ID 前缀；去掉它即得到秘籍/残卷物品 ID 后缀。</summary>
    private const string AbilityIdPrefix = "ability.";
    /// <summary>残卷物品 ID 前缀（秘籍缺配表时的回单价目）。</summary>
    private const string FragmentPrefix = "item.fragment.";
    /// <summary>单本残卷研读注入的标准经验点数（EcsPlayerRuntimeAdapter 对齐）。</summary>
    private const double ExpPerFragment = 500d;

    /// <summary>装备回收定价参数簿：白板价 V0 与强化保护单价 P 双字典（构造期一次性物化，杜绝并发写竞争）。</summary>
    public sealed record EquipmentSellPriceBook(
        FrozenDictionary<string, double> BaseSellPrices,
        FrozenDictionary<string, double> ProtectionUnitPrices);

    /// <summary>一次性构建全量装备回收定价参数簿（白板价与保护单价同趟解算），消除运行期并发写入。</summary>
    public static EquipmentSellPriceBook BuildSellPriceBook(GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var costCache = new Dictionary<string, double>(StringComparer.Ordinal);
        var basePrices = new Dictionary<string, double>(catalog.RawEquipments.Count, StringComparer.Ordinal);
        var protectionPrices = new Dictionary<string, double>(catalog.RawEquipments.Count, StringComparer.Ordinal);
        foreach (var equipmentId in catalog.RawEquipments.Keys)
        {
            if (EnhancementRules.ComputeEquipmentBaseSellPrice(catalog, equipmentId, costCache) is { } price && price > 0)
            {
                basePrices[equipmentId] = price;
                protectionPrices[equipmentId] = EnhancementRules.ResolveProtectionUnitPrice(catalog, equipmentId, price, costCache);
            }
        }
        return new EquipmentSellPriceBook(
            basePrices.ToFrozenDictionary(StringComparer.Ordinal),
            protectionPrices.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>按口径合计战斗装备、功法与技艺工具价值；非有限结果归零。</summary>
    public static ValuationResult Compute(
        PlayerLoadoutDocument loadout,
        GameDataCatalog catalog,
        EquipmentSellPriceBook sellPriceBook,
        MarketRuntime? market)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sellPriceBook);

        var equipmentTotal = 0d;
        var toolsTotal = 0d;
        foreach (var slot in loadout.Equipment)
        {
            var price = EquipmentUnitPrice(slot.ItemId, slot.EnhancementLevel, catalog, sellPriceBook, market);
            if (IsTool(slot.ItemId, catalog))
                toolsTotal += price;
            else
                equipmentTotal += price;
        }

        var abilitiesTotal = 0d;
        if (loadout.AbilityExperiences is not null)
        {
            foreach (var (abilityId, exp) in loadout.AbilityExperiences)
            {
                if (!double.IsFinite(exp) || exp < 0) continue;
                var suffix = abilityId.StartsWith(AbilityIdPrefix, StringComparison.Ordinal)
                    ? abilityId[AbilityIdPrefix.Length..]
                    : abilityId;
                var fragmentPrice = FragmentUnitPrice(suffix, catalog, market);
                if (fragmentPrice <= 0) continue;

                // 功法按总经验折算残卷数量（每 500 经验 = 1 本残卷），已学功法底线按 1 本残卷计
                var fragmentCount = Math.Max(1.0, exp / ExpPerFragment);
                abilitiesTotal += fragmentCount * fragmentPrice;
            }
        }

        return new ValuationResult(
            double.IsFinite(equipmentTotal) ? equipmentTotal : 0,
            double.IsFinite(abilitiesTotal) ? abilitiesTotal : 0,
            double.IsFinite(toolsTotal) ? toolsTotal : 0);
    }

    private static bool IsTool(string itemIdText, GameDataCatalog catalog)
    {
        if (catalog.RawEquipments.TryGetValue(itemIdText, out var element)
            && element.TryGetProperty("slot", out var slotProp)
            && slotProp.ValueKind == JsonValueKind.String)
        {
            return slotProp.GetString()?.StartsWith("tool-", StringComparison.Ordinal) == true;
        }
        return false;
    }

    private static double EquipmentUnitPrice(
        string itemIdText,
        double enhancementLevel,
        GameDataCatalog catalog,
        EquipmentSellPriceBook sellPriceBook,
        MarketRuntime? market)
    {
        if (!catalog.RawEquipments.ContainsKey(itemIdText)) return 0;
        var levelInt = (int)Math.Floor(enhancementLevel);
        var variant = levelInt > 0
            ? string.Create(CultureInfo.InvariantCulture, $"+{levelInt:0}")
            : string.Empty;
        var instrument = new MarketInstrument(new ItemId(itemIdText), variant);
        var refPrice = market?.TryGetReferencePrice(instrument);
        if (refPrice is > 0) return refPrice.Value;

        if (sellPriceBook.BaseSellPrices.TryGetValue(itemIdText, out var basePrice) && basePrice > 0)
        {
            var protectionPrice = sellPriceBook.ProtectionUnitPrices.GetValueOrDefault(itemIdText, basePrice);
            return EnhancementRules.CalculateEquipmentSellPrice(basePrice, protectionPrice, levelInt);
        }
        return 0;
    }

    private static double FragmentUnitPrice(string suffix, GameDataCatalog catalog, MarketRuntime? market)
    {
        var fragmentId = new ItemId(FragmentPrefix + suffix);
        if (market?.TryGetReferencePrice(new MarketInstrument(fragmentId)) is { } fragRef && fragRef > 0)
            return fragRef;
        if (catalog.Items.TryGetValue(fragmentId, out var fragDef))
            return Math.Max(0, fragDef.SellPrice);
        return 0;
    }
}
