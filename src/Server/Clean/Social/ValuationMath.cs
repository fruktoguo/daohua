using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Domain.Combat;
using Domain.GameData;
using Domain.Market;
using Domain.Player;

namespace Server.Clean.Social;

/// <summary>
/// 道行与身家纯计算辅助（单一计算真源）：
/// - <see cref="ComputeDaohang"/>：当前启用的「道行」口径（境界×2 + 根基×1.5 + 穿戴战斗装备等级×(1+强化×5%) + 携带功法等级 + 携带特质消耗×(1+特质等级×5%)，工具不计）；
/// - <see cref="Compute"/>：原有基于坊市/回收价的「身家」估值实现（保留不删除，暂不启用）。
/// 供 <see cref="PartyValuationService"/>（组队/名片/附近）与全服排行榜整点物化共用，确保全游戏口径完全一致。
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

    /// <summary>
    /// 计算角色当前「道行」（保留至多 2 位小数以消除浮点尾差）：
    /// 道行 = (境界等级 × 2 + 根基 × 1.5)
    ///      + Σ 已穿戴非工具战斗装备等级 × (1 + 强化等级 × 5%)
    ///      + Σ 已携带非空功法槽位等级（仅统计 ability.*，不含符法天命符箓）
    ///      + Σ 已携带特质消耗点数 × (1 + 对应特质等级 × 5%)。
    /// 技艺工具不计入道行。
    /// </summary>
    public static ValuationResult ComputeDaohang(
        int fallbackRealmLevel,
        PlayerProgressionState? progression,
        PlayerLoadoutDocument? loadout,
        GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var realmLevel = Math.Max(1, progression?.RealmLevel ?? fallbackRealmLevel);
        var genjiPoints = Math.Max(0, progression?.GenjiPoints ?? 0);
        var realmAndGenji = RoundDaohang(realmLevel * 2.0 + genjiPoints * 1.5);

        var equipmentTotal = 0d;
        if (loadout?.Equipment is { Count: > 0 } equipment)
        {
            foreach (var slot in equipment)
            {
                if (slot is null || string.IsNullOrWhiteSpace(slot.ItemId)) continue;
                if (!catalog.RawEquipments.ContainsKey(slot.ItemId) || IsTool(slot.ItemId, catalog))
                    continue;
                var itemLevel = CraftingRecipeRules.ResolveEquipmentRequiredLevel(slot.ItemId, catalog.RawEquipments);
                var enhLevel = double.IsFinite(slot.EnhancementLevel) && slot.EnhancementLevel > 0
                    ? Math.Floor(slot.EnhancementLevel)
                    : 0d;
                equipmentTotal += itemLevel * (1.0 + enhLevel * 0.05);
            }
        }
        equipmentTotal = RoundDaohang(equipmentTotal);

        var abilitiesTotal = 0d;
        if (loadout?.AbilitySlots is { Count: > 0 } abilitySlots)
        {
            var countedAbilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in abilitySlots)
            {
                if (slot is null || string.IsNullOrWhiteSpace(slot.AbilityId)) continue;
                if (!catalog.Abilities.ContainsKey(slot.AbilityId) || !countedAbilities.Add(slot.AbilityId))
                    continue;
                var level = double.IsFinite(slot.Level) && slot.Level >= 1 ? slot.Level : 1d;
                if (loadout.AbilityExperiences is not null
                    && loadout.AbilityExperiences.TryGetValue(slot.AbilityId, out var exp)
                    && double.IsFinite(exp) && exp > 0)
                {
                    level = AbilityExpCurve.LevelFor(exp);
                }
                abilitiesTotal += Math.Max(1d, Math.Floor(level));
            }
        }
        abilitiesTotal = RoundDaohang(abilitiesTotal);

        var traitsTotal = 0d;
        if (progression?.TraitIds is { Count: > 0 } traitIds)
        {
            Dictionary<string, int>? traitLevelById = null;
            if (progression.UnlockedTraits is { Count: > 0 } unlocked)
            {
                traitLevelById = new Dictionary<string, int>(unlocked.Count, StringComparer.Ordinal);
                foreach (var entry in unlocked)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Id))
                        traitLevelById[entry.Id] = Math.Max(1, entry.Level);
                }
            }

            var countedTraits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var traitId in traitIds)
            {
                if (string.IsNullOrWhiteSpace(traitId) || !countedTraits.Add(traitId)) continue;
                if (!CombatTraitRules.AllTraits.TryGetValue(traitId, out var def)) continue;
                var traitLevel = traitLevelById is not null && traitLevelById.TryGetValue(traitId, out var lv)
                    ? Math.Max(1, lv)
                    : 1;
                traitsTotal += def.Cost * (1.0 + traitLevel * 0.05);
            }
        }
        traitsTotal = RoundDaohang(traitsTotal);

        return new ValuationResult(
            Equipment: equipmentTotal,
            Abilities: abilitiesTotal,
            Tools: 0d,
            RealmAndGenji: realmAndGenji,
            Traits: traitsTotal);
    }

    internal static double RoundDaohang(double value)
        => double.IsFinite(value) && value > 0
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : 0d;

    /// <summary>【保留未用】按原身家口径合计战斗装备、功法与技艺工具市场/回收价值；非有限结果归零。</summary>
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
