namespace Domain.GameData;

using System;
using System.Text.Json;
using Domain.Common;

/// <summary>
/// 制造配方 T 级换算与图鉴解锁等级规则。
/// 对应总纲 §23 万象图鉴与解锁机制：
/// T1 配方默认 1 级解锁；T2 配方需 18 级（凡俗 [天] 叩仙门）解锁；
/// 等级公式与前端 convertTierToLevel 完全对齐：tier <= 1 ? 1 : 18 + (tier - 2) * 12。
/// </summary>
public static class CraftingRecipeRules
{
    /// <summary>
    /// 将配方/物品等阶（Tier）换算为所需角色境界等级。
    /// Tier 1 -> 1
    /// Tier 2 -> 18
    /// Tier 3 -> 30
    /// </summary>
    public static int ConvertTierToLevel(int tier)
    {
        if (tier <= 1) return 1;
        return 18 + (tier - 2) * 12;
    }

    /// <summary>
    /// 解析制造行动所需解锁境界等级。
    /// 依据产出物（Yields）与消耗材料（Inputs）中包含物品/装备的最大 Tier 裁决。
    /// </summary>
    public static int ResolveRequiredRealmLevel(ActionDefinition action, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(catalog);

        // 1. 优先按产出物（Yields）的 Tier 裁决（与前端 resolveActionLevel 一致）
        if (action.Yields is { Count: > 0 })
        {
            foreach (var yield in action.Yields)
            {
                if (yield.Kind != ActionYieldKind.Item || yield.Item is null) continue;
                var tier = ResolveItemOrEquipmentTier(yield.Item.Value.Value, catalog);
                if (tier > 0) return ConvertTierToLevel(tier);
            }
        }

        // 2. 缺省时回退按输入材料的最大 Tier 裁决
        var maxTier = 1;
        if (action.Inputs is { Count: > 0 })
        {
            foreach (var (itemId, _) in action.Inputs)
            {
                var tier = ResolveItemOrEquipmentTier(itemId.Value, catalog);
                if (tier > maxTier) maxTier = tier;
            }
        }

        return ConvertTierToLevel(maxTier);
    }

    private static int ResolveItemOrEquipmentTier(string id, GameDataCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(id)) return 1;

        if (catalog.RawEquipments.TryGetValue(id, out var eqElement)
            && eqElement.TryGetProperty("tier", out var eqTier)
            && eqTier.TryGetInt32(out var eqTierVal)
            && eqTierVal > 0)
        {
            return eqTierVal;
        }

        if (catalog.RawItems.TryGetValue(id, out var itemElement)
            && itemElement.TryGetProperty("tier", out var itemTier)
            && itemTier.TryGetInt32(out var itemTierVal)
            && itemTierVal > 0)
        {
            return itemTierVal;
        }

        return 1;
    }
}
