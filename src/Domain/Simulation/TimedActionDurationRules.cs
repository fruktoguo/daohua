using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.World;

namespace Domain.Simulation;

/// <summary>
/// 计时行动耗时计算纯领域规则：强化按目标等级取 5+t 动态时长，其余取配表静态时长；
/// 节点建筑速度乘区独立除法，技艺等级速度乘区（每级 +5%）与已装备技艺工具
/// techniqueBonus.speedRatio 并入同一除数（duration /= 1 + 0.05×L + Σ工具加成），
/// 保持同点加算语义与确定性。
/// </summary>
public static class TimedActionDurationRules
{
    /// <summary>纯参数内核：计算加速后的单轮实际耗时（秒）。</summary>
    public static double CalculateCore(
        double baseDuration,
        ActionKind kind,
        string? technique,
        BuildingEffectSet? buildingEffects,
        IReadOnlyDictionary<string, double>? techniqueExperiences,
        IReadOnlyList<PlayerEquipmentSlot>? equipment,
        GameDataCatalog? gameData,
        int? enhancementTargetLevel = null,
        LinggenValues linggen = default)
    {
        var duration = baseDuration;
        if (kind == ActionKind.Enhancement && enhancementTargetLevel is not null)
        {
            var target = enhancementTargetLevel.Value;
            duration = target >= 1 && target <= EnhancementRules.MaxLevel
                ? EnhancementRules.DurationSeconds(target)
                : 0;
        }

        var buildingSpeed = buildingEffects?.SpeedRatioFor(kind, technique) ?? 0;
        if (buildingSpeed > 0) duration /= 1 + buildingSpeed;

        if (technique is not null && TechniqueCatalog.IsKnown(technique))
        {
            var exp = techniqueExperiences is not null && techniqueExperiences.TryGetValue(technique, out var storedExp)
                ? storedExp : 0;
            var level = TechniqueCatalog.LevelFor(exp, gameData?.RealmProgression);
            duration /= 1 + TechniqueCatalog.SpeedBonusRatio(level)
                + TechniqueCatalog.EquippedToolSpeedRatio(equipment, technique, gameData)
                + HeavenGateRules.ResolveTechniqueSpeedRatio(linggen, technique);
        }

        return duration;
    }

    /// <summary>根据运行时玩家资源上下文计算单轮实际耗时（秒）。</summary>
    public static double Calculate(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        AssetWallet? assets,
        IReadOnlyDictionary<AssetKey, double>? deltas = null)
    {
        int? targetLevel = null;
        if (definition.Kind == ActionKind.Enhancement)
        {
            var holdings = EnhancementEffectiveBalances(assets, deltas);
            var targetCap = EnhancementRules.MaxLevel;
            int? preferredLevel = null;
            if (action.Intent.Parameters is not null)
            {
                if (action.Intent.Parameters.TryGetValue(EnhancementRules.TargetLevelParameter, out var targetParam)
                    && double.IsFinite(targetParam) && targetParam > 0)
                    targetCap = Math.Min(EnhancementRules.MaxLevel, (int)Math.Floor(targetParam));
                if (action.Intent.Parameters.TryGetValue(EnhancementRules.InitialLevelParameter, out var initialParam)
                    && double.IsFinite(initialParam) && initialParam >= 0)
                    preferredLevel = (int)Math.Floor(initialParam);
            }
            var current = action.EnhancementCurrentLevel
                ?? (action.Intent.TargetItemId is { } targetText
                    ? EnhancementRules.CurrentLevel(player.Loadout, new ItemId(targetText), holdings, targetCap, preferredLevel)
                    : -1);
            targetLevel = current >= 0 ? current + 1 : null;
        }

        return CalculateCore(
            definition.DurationSeconds,
            definition.Kind,
            definition.Technique,
            player.World.EffectiveNodeBuildingEffects,
            player.Loadout.TechniqueExperiences,
            player.Loadout.Equipment,
            gameData,
            targetLevel,
            player.Progression.HeavenGate.EffectiveValues);
    }

    /// <summary>根据导出的玩家状态快照计算单轮实际耗时（秒，供协议序列化与快照投影使用）。</summary>
    public static double Calculate(
        PlayerAggregateState state,
        QueuedActionState action,
        ActionDefinition definition,
        GameDataCatalog? gameData)
    {
        int? targetLevel = null;
        if (definition.Kind == ActionKind.Enhancement)
        {
            var current = action.EnhancementCurrentLevel
                ?? (action.Intent.Parameters is not null && action.Intent.Parameters.TryGetValue(EnhancementRules.InitialLevelParameter, out var initialParam) && double.IsFinite(initialParam)
                    ? (int)Math.Floor(initialParam)
                    : 0);
            targetLevel = current + 1;
        }

        return CalculateCore(
            definition.DurationSeconds,
            definition.Kind,
            definition.Technique,
            state.World.EffectiveNodeBuildingEffects,
            state.Loadout.TechniqueExperiences,
            state.Loadout.Equipment,
            gameData,
            targetLevel,
            state.Progression.HeavenGateEntered ? state.Progression.HeavenGateConfirmed ?? LinggenValues.Empty : LinggenValues.Empty);
    }

    private static Dictionary<AssetKey, double> EnhancementEffectiveBalances(
        AssetWallet? assets,
        IReadOnlyDictionary<AssetKey, double>? deltas)
    {
        var merged = new Dictionary<AssetKey, double>();
        if (assets is not null)
            foreach (var balance in assets.Balances)
                merged[balance.Asset] = balance.Quantity;
        if (deltas is not null)
            foreach (var (asset, quantity) in deltas)
                merged[asset] = merged.GetValueOrDefault(asset) + quantity;
        return merged;
    }
}
