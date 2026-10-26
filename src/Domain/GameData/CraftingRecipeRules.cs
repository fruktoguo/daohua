namespace Domain.GameData;

using System;
using System.Text.Json;
using Domain.Common;

/// <summary>
/// 制造配方 T 级换算与图鉴解锁等级规则。
/// 对应总纲 §23 万象图鉴与解锁机制：
/// T1 配方默认 1 级解锁；T2 配方需 18 级（凡俗 [天] 叩天门）解锁；
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
        return ResolveRequiredRealmLevel(action, catalog.RawItems, catalog.RawEquipments);
    }

    /// <summary>
    /// 解析行动所需解锁境界等级。
    /// 配表显式声明的 <c>level</c> 优先级最高（行动本身即承载关卡，如 T1 采集点可配置 18 级门槛）；
    /// 缺省时依据产出物（Yields）与消耗材料（Inputs）中包含物品/装备的最大 Tier 裁决。
    /// 该优先级与装配期经验结算（<c>GameDataCatalog.ResolveActionCatalog</c>）及前端
    /// <c>resolveActionLevel</c> 完全一致，禁止两处口径分叉。
    /// </summary>
    public static int ResolveRequiredRealmLevel(ActionDefinition action,
        IReadOnlyDictionary<string, JsonElement>? rawItems,
        IReadOnlyDictionary<string, JsonElement>? rawEquipments)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 0. 配表显式等级优先（行动自身即关卡定义）
        if (action.Level is { } explicitLevel) return explicitLevel;

        // 1. 优先按产出物（Yields）的 Tier 裁决（与前端 resolveActionLevel 一致）
        if (action.Yields is { Count: > 0 })
        {
            foreach (var yield in action.Yields)
            {
                if (yield.Kind != ActionYieldKind.Item || yield.Item is null) continue;
                var tier = ResolveItemOrEquipmentTier(yield.Item.Value.Value, rawItems, rawEquipments);
                if (tier > 0) return ConvertTierToLevel(tier);
            }
        }

        // 2. 缺省时回退按输入材料的最大 Tier 裁决
        var maxTier = 1;
        if (action.Inputs is { Count: > 0 })
        {
            foreach (var (itemId, _) in action.Inputs)
            {
                var tier = ResolveItemOrEquipmentTier(itemId.Value, rawItems, rawEquipments);
                if (tier > maxTier) maxTier = tier;
            }
        }

        return ConvertTierToLevel(maxTier);
    }

    /// <summary>
    /// 判断行动/配方是否已解锁：
    /// 1. 所需等级 requiredLevel <= 1 默认解锁；
    /// 2. 玩家角色境界达到 requiredLevel；
    /// 3. 若行动关联技艺（technique），且对应技艺等级达到 requiredLevel。
    /// 境界与技艺等级通常为「或」关系（满足任一即放行）；
    /// 但若行动声明了底蕴成本（FoundationCostRatio > 0，如逆命丹），底蕴成本按境界升级所需修为折算，
    /// 为杜绝低境界利用低底蕴批量炼丹套利，必须角色境界实打实达到 requiredLevel，不享受技艺跨境界放行。
    /// </summary>
    public static bool IsActionUnlocked(
        ActionDefinition action,
        int realmLevel,
        double techniqueLevel,
        GameDataCatalog catalog,
        out int requiredLevel)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(catalog);
        requiredLevel = ResolveRequiredRealmLevel(action, catalog);
        if (requiredLevel <= 1) return true;
        if (realmLevel >= requiredLevel) return true;
        if (action.FoundationCostRatio > 0) return false;
        if (!string.IsNullOrEmpty(action.Technique) && techniqueLevel >= requiredLevel) return true;
        return false;
    }
    /// <summary>
    /// 单个物品/装备 ID 的等阶代表等级（tier → 境界等级换算，未命中按 T1）。
    /// 强化等动态裁决以目标装备本值作为「行动等级」基准（总纲 §20.1.4）。
    /// </summary>
    public static int ResolveItemLevel(string id, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ResolveItemLevel(id, catalog.RawItems, catalog.RawEquipments);
    }

    /// <summary>单个物品/装备 ID 的等阶代表等级（tier → 境界等级换算，未命中按 T1）。</summary>
    public static int ResolveItemLevel(string id,
        IReadOnlyDictionary<string, JsonElement>? rawItems,
        IReadOnlyDictionary<string, JsonElement>? rawEquipments)
        => ConvertTierToLevel(ResolveItemOrEquipmentTier(id, rawItems, rawEquipments));

    public static int ResolveItemOrEquipmentTier(string id, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ResolveItemOrEquipmentTier(id, catalog.RawItems, catalog.RawEquipments);
    }

    public static int ResolveItemOrEquipmentTier(string id,
        IReadOnlyDictionary<string, JsonElement>? rawItems,
        IReadOnlyDictionary<string, JsonElement>? rawEquipments)
    {
        if (string.IsNullOrWhiteSpace(id)) return 1;

        if (rawEquipments is not null
            && rawEquipments.TryGetValue(id, out var eqElement)
            && eqElement.TryGetProperty("tier", out var eqTier)
            && eqTier.TryGetInt32(out var eqTierVal)
            && eqTierVal > 0)
        {
            return eqTierVal;
        }

        if (rawItems is not null
            && rawItems.TryGetValue(id, out var itemElement)
            && itemElement.TryGetProperty("tier", out var itemTier)
            && itemTier.TryGetInt32(out var itemTierVal)
            && itemTierVal > 0)
        {
            return itemTierVal;
        }

        return 1;
    }
}
