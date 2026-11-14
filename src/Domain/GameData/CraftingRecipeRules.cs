namespace Domain.GameData;

using System;
using System.Text.Json;
using Domain.Common;

/// <summary>
/// 制造配方解锁与物品/装备自身等级解析规则。
/// 所有物品、装备与行动均直接读取配表显式声明的 <c>level</c>（缺省为 1 级），不再由等阶 <c>tier</c> 换算等级。
/// 对应总纲 §23 万象图鉴与解锁机制：
/// - 制造/采集解锁：优先取行动自身 <c>level</c>，缺省时直读产出物 <c>level</c> 或输入材料最大 <c>level</c>（最终缺省 1）；
/// - 强化经验/神识基准：直读目标装备或物品自身 <c>level</c>（缺省 1）。
/// </summary>
public static class CraftingRecipeRules
{
    /// <summary>
    /// 解析制造行动所需解锁境界等级。
    /// 缺省显式 <c>level</c> 时：直读首个物品/装备产出物自身 <c>level</c>（再缺省则取输入材料最大 <c>level</c>，最终缺省 1）。
    /// </summary>
    public static int ResolveRequiredRealmLevel(ActionDefinition action, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(catalog);
        return ResolveRequiredRealmLevel(action, catalog.RawItems, catalog.RawEquipments);
    }

    /// <summary>
    /// 解析行动所需解锁境界等级。
    /// 配表显式声明的 <c>level</c> 优先级最高（如 starter 精炼行动显式声明 1 级）；
    /// 缺省时直读首个物品/装备产出物（<c>Yields</c>）自身 <c>level</c>，无产出物时回退输入材料（<c>Inputs</c>）的最大 <c>level</c>，最终缺省 1。
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

        // 1. 优先按产出物（Yields）自身显式 level 裁决（装备或物品同口径直读）
        if (action.Yields is { Count: > 0 })
        {
            foreach (var yield in action.Yields)
            {
                if (yield.Kind != ActionYieldKind.Item || yield.Item is null) continue;
                return ResolveItemLevel(yield.Item.Value.Value, rawItems, rawEquipments);
            }
        }

        // 2. 缺省时回退按输入材料的最大显式 level 裁决
        var maxLevel = 1;
        if (action.Inputs is { Count: > 0 })
        {
            foreach (var (itemId, _) in action.Inputs)
            {
                var level = ResolveItemLevel(itemId.Value, rawItems, rawEquipments);
                if (level > maxLevel) maxLevel = level;
            }
        }

        return maxLevel;
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
    /// 单个物品/装备 ID 的代表等级（直读显式 level，缺省按 1 级）。
    /// 强化等动态裁决以目标装备自身 level 作为「行动等级」基准（总纲 §20.1.4）。
    /// </summary>
    public static int ResolveItemLevel(string id, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ResolveItemLevel(id, catalog.RawItems, catalog.RawEquipments);
    }

    /// <summary>
    /// 解析单个装备穿戴所需的境界等级（直读配表显式 level，缺省按 1 级）。
    /// </summary>
    public static int ResolveEquipmentRequiredLevel(string id, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ResolveEquipmentRequiredLevel(id, catalog.RawEquipments);
    }

    /// <summary>
    /// 解析单个装备穿戴所需的境界等级（直读配表显式 level，缺省按 1 级）。
    /// </summary>
    public static int ResolveEquipmentRequiredLevel(string id, IReadOnlyDictionary<string, JsonElement>? rawEquipments)
    {
        if (string.IsNullOrWhiteSpace(id) || rawEquipments is null) return 1;
        if (rawEquipments.TryGetValue(id, out var element)
            && element.TryGetProperty("level", out var lvl)
            && lvl.TryGetInt32(out var l)
            && l > 0)
        {
            return l;
        }
        return 1;
    }

    /// <summary>单个物品/装备 ID 的代表等级（直读显式 level，装备先于物品，缺省按 1 级）。</summary>
    public static int ResolveItemLevel(string id,
        IReadOnlyDictionary<string, JsonElement>? rawItems,
        IReadOnlyDictionary<string, JsonElement>? rawEquipments)
    {
        if (string.IsNullOrWhiteSpace(id)) return 1;

        if (rawEquipments is not null
            && rawEquipments.TryGetValue(id, out var eqElement)
            && eqElement.TryGetProperty("level", out var eqLvl)
            && eqLvl.TryGetInt32(out var eqLevel)
            && eqLevel > 0)
        {
            return eqLevel;
        }

        if (rawItems is not null
            && rawItems.TryGetValue(id, out var itemElement)
            && itemElement.TryGetProperty("level", out var itemLvl)
            && itemLvl.TryGetInt32(out var itemLevel)
            && itemLevel > 0)
        {
            return itemLevel;
        }

        return 1;
    }
}
