using Domain.Common;
using Domain.GameData;

namespace Domain.Player;

/// <summary>秘法页即时物品操作的确定性规则。</summary>
public static class SecretArtRules
{
    public const double LiquefactionMultiplier = 3;
    public const double DisassemblyReturnRatio = 0.70;
    public const double FragmentTomeChance = 0.05;
    public const double FragmentSpiritStoneReward = 100;
    private const int MaxRecipeDepth = 4;

    /// <summary>解析普通物品或装备当前变体的系统商店回收单价；不可回收时返回 0。</summary>
    public static double ResolveRecycleUnitPrice(GameDataCatalog gameData, string itemId, int enhancementLevel)
    {
        if (gameData.RawEquipments.ContainsKey(itemId))
        {
            var basePrice = EnhancementRules.ComputeEquipmentBaseSellPrice(gameData, itemId);
            return basePrice is > 0
                ? EnhancementRules.CalculateEquipmentSellPrice(basePrice.Value, enhancementLevel)
                : 0;
        }

        return gameData.Items.TryGetValue(new ItemId(itemId), out var item) && item.SellPrice > 0
            ? item.SellPrice
            : 0;
    }

    /// <summary>
    /// 展开锻造配方并汇总为基础物品原料，再按每种原料 70% 向下取整。
    /// 装备作为配方投入时递归拆开，避免把中间装备当作返还材料。
    /// </summary>
    public static IReadOnlyDictionary<ItemId, double>? ComputeDisassemblyReturns(GameDataCatalog gameData, string equipmentId)
    {
        var requirements = ComputeBaseMaterials(gameData, equipmentId, 0);
        if (requirements is null) return null;

        var returns = new Dictionary<ItemId, double>();
        foreach (var (itemId, amount) in requirements)
        {
            var returned = Math.Floor(amount * DisassemblyReturnRatio);
            if (returned > 0) returns.Add(itemId, returned);
        }
        return returns;
    }

    /// <summary>根据单次随机值决定残卷转化产物；roll ∈ [0,1)。</summary>
    public static bool FragmentProducesTome(double roll)
    {
        if (!double.IsFinite(roll) || roll < 0 || roll >= 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, "残卷转化概率值无效。");
        return roll < FragmentTomeChance;
    }

    private static Dictionary<ItemId, double>? ComputeBaseMaterials(GameDataCatalog gameData, string equipmentId, int depth)
    {
        if (depth > MaxRecipeDepth)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"装备配方递归超过深度上限（疑似配表成环）：{equipmentId}。");

        var recipe = gameData.Actions.All.FirstOrDefault(action => action.Technique == "forging" && action.Yields.Any(yield =>
            yield.Kind == ActionYieldKind.Item && yield.Item is { } product
            && string.Equals(product.Value, equipmentId, StringComparison.Ordinal)));
        if (recipe is null) return null;

        var outputAmount = recipe.Yields
            .Where(yield => yield.Kind == ActionYieldKind.Item && yield.Item is { } product
                && string.Equals(product.Value, equipmentId, StringComparison.Ordinal))
            .Sum(yield => yield.Amount);
        if (!double.IsFinite(outputAmount) || outputAmount <= 0) return null;

        var materials = new Dictionary<ItemId, double>();
        foreach (var (inputId, inputAmount) in recipe.Inputs)
        {
            var perOutput = inputAmount / outputAmount;
            if (inputId.Value.StartsWith("equipment.", StringComparison.Ordinal))
            {
                var nested = ComputeBaseMaterials(gameData, inputId.Value, depth + 1);
                if (nested is null) return null;
                foreach (var (materialId, materialAmount) in nested)
                    materials[materialId] = materials.GetValueOrDefault(materialId) + materialAmount * perOutput;
            }
            else
            {
                if (!gameData.Items.ContainsKey(inputId)) return null;
                materials[inputId] = materials.GetValueOrDefault(inputId) + perOutput;
            }
        }

        return materials;
    }
}
