using Domain.Combat;
using Domain.Common;
using Domain.GameData;

namespace Domain.Player;

/// <summary>秘法页即时物品操作的确定性规则。</summary>
public static class SecretArtRules
{
    public const string LiquefyActionId = "secret_art.liquefy";
    public const string DisassembleActionId = "secret_art.disassemble";
    public const string ConvertActionId = "secret_art.convert";

    public static bool IsSecretArtAction(string actionId) =>
        actionId is LiquefyActionId or DisassembleActionId or ConvertActionId;

    public const double LiquefactionMultiplier = 3;
    public const double DisassemblyReturnRatio = 0.70;
    public const double FragmentTomeChance = 0.05;
    /// <summary>化灵单轮基础耗时基数（秒）：实际 = 基数 + 每级耗时 × (物品等级 − 1)。</summary>
    public const double LiquefyBaseDurationSeconds = 5;
    /// <summary>化灵目标物品等级每提升 1 级的额外耗时（秒）。</summary>
    public const double LiquefyDurationPerLevelSeconds = 0.3;
    /// <summary>秘法技艺等级每低于物品等级 1 级的化灵成功率衰减点数。</summary>
    public const double LiquefySuccessPenaltyPerLevel = 0.05;
    /// <summary>化灵成功率下限。</summary>
    public const double LiquefyMinSuccessChance = 0.2;
    /// <summary>化灵判定独立通道：与强化判定（0xE0A1）、区间产出判定（0x11A7）及战斗随机流完全隔离。</summary>
    private const ulong LiquefyRollChannel = 0xE0A2;
    private const int MaxRecipeDepth = 4;

    /// <summary>化灵单轮基础耗时（秒）：5 + 0.3 × (物品等级 − 1)，物品等级按配表显式 level 计（不含强化 +N）。</summary>
    public static double LiquefyDurationSeconds(int itemLevel)
        => LiquefyBaseDurationSeconds + LiquefyDurationPerLevelSeconds * Math.Max(0, itemLevel - 1);

    /// <summary>
    /// 化灵成功率：秘法技艺等级不低于物品等级时恒 100%；每低 1 级 −5%，下限 20%。
    /// </summary>
    public static double LiquefySuccessChance(double techniqueLevel, int itemLevel)
    {
        var deficit = Math.Max(0, itemLevel - (int)Math.Floor(Math.Max(0, techniqueLevel)));
        return Math.Clamp(1.0 - LiquefySuccessPenaltyPerLevel * deficit, LiquefyMinSuccessChance, 1.0);
    }

    /// <summary>
    /// 化灵当轮确定性判定：种子取行动实例 Id 前 8 字节，计数维度用已完成轮次，
    /// 独立通道保证与强化判定、区间产出判定及战斗随机流互不影响；同一实例同一轮次必得同一结果。
    /// </summary>
    public static bool LiquefySucceeds(Guid actionInstanceId, double completedRepeats, double successChance)
    {
        if (!double.IsFinite(successChance) || successChance < 0 || successChance > 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, "化灵成功率无效。");
        var seed = BitConverter.ToInt64(actionInstanceId.ToByteArray(), 0);
        var roll = DeterministicRoll.Next(seed, 0, completedRepeats, LiquefyRollChannel);
        return roll < successChance;
    }

    /// <summary>只接受配表中的功法残卷，并解析同一后缀的完整秘籍。</summary>
    internal static bool TryResolveFragmentTome(GameDataCatalog? gameData, string fragmentItemId, out ItemId tomeId)
    {
        const string prefix = "item.fragment.";
        tomeId = default;
        if (gameData is null || fragmentItemId.Length <= prefix.Length
            || !fragmentItemId.StartsWith(prefix, StringComparison.Ordinal)
            || !gameData.Items.TryGetValue(new ItemId(fragmentItemId), out var fragment)
            || fragment.SubCategory != "manualFragment")
            return false;
        var candidate = new ItemId("item.tome." + fragmentItemId[prefix.Length..]);
        if (!gameData.Items.TryGetValue(candidate, out var tome) || tome.SubCategory != "manualTome")
            return false;
        tomeId = candidate;
        return true;
    }

    /// <summary>残卷推演失败时的保底灵石奖励：按残卷自身系统回收价的 2 倍动态计算。</summary>
    public static double ResolveFragmentFailureReward(GameDataCatalog gameData, string fragmentItemId)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        return ResolveRecycleUnitPrice(gameData, fragmentItemId, 0) * 2.0;
    }

    /// <summary>解析普通物品或装备当前变体的系统商店回收单价；不可回收时返回 0。</summary>
    public static double ResolveRecycleUnitPrice(GameDataCatalog gameData, string itemId, int enhancementLevel)
    {
        if (gameData.RawEquipments.ContainsKey(itemId))
        {
            return EnhancementRules.ComputeEquipmentSellPrice(gameData, itemId, enhancementLevel) ?? 0;
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

    /// <summary>计算秘法单轮行动产出的技艺经验与神识经验（神识经验维持原基准倍率，不随技艺经验全局翻倍）。</summary>
    public static (double TechExp, double ConsciousnessExp) ComputeYieldExperience(GameDataCatalog? gameData, string itemId, double durationSeconds)
    {
        var itemLevel = gameData is null ? 1 : CraftingRecipeRules.ResolveItemLevel(itemId, gameData);
        var techExp = TechniqueCatalog.ComputeTechniqueExperience(gameData, itemLevel, durationSeconds);
        var baseExp = itemLevel <= 1 ? 10.0 : (gameData?.RealmProgression?.BaseExperienceForLevel(itemLevel) ?? 10.0);
        var consciousnessExp = Math.Max(1.0, Math.Round(baseExp * (durationSeconds / 10.0))) * 2.0;
        return (techExp, consciousnessExp);
    }
}
