using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;

namespace Domain.Player;

/// <summary>
/// 装备强化单次结算规则（总纲 §20.4）：目标等级、成本、成功率、确定性判定与结果折算。
/// 本类只承载无状态公式；钱包合并视图、资产变体迁移与队列裁决由行动执行层驱动。
/// </summary>
public static class EnhancementRules
{
    /// <summary>强化等级上限；目标等级 t 超出后行动阻塞。</summary>
    public const int MaxLevel = 20;
    /// <summary>单次强化基础耗时基数：实际耗时 = BaseDurationSeconds + 目标等级。</summary>
    public const double BaseDurationSeconds = 5;
    /// <summary>强化精华物品 ID；每次强化消耗 max(1, floor(t/2))。</summary>
    public const string EssenceItemId = "item.enhance-essence";
    /// <summary>保护材料意图槽键：AuxItemId 指定每轮消耗的保护材料 ID（装备配表 enhanceProtectionItems 列表内任一材料，或未配置时同装备无强化基础件）。</summary>
    /// <summary>强化灵石货币 ID；每次强化消耗 t×10。</summary>
    public const string SpiritStoneItemId = "currency.spirit-stone";
    /// <summary>目标等级意图参数键：当前强化等级达到该值后行动提前完成出队。</summary>
    public const string TargetLevelParameter = "targetLevel";

    /// <summary>强化判定独立通道：与战斗命中/暴击/掉落通道完全隔离，不扰战斗随机流。</summary>
    private const ulong RollChannel = 0xE0A1;

    // 目标等级 1..10 的基础成功率查表；11+ 走 0.3×0.95^(t-11) 指数衰减。
    private static readonly double[] SuccessTable = [0.5, 0.45, 0.45, 0.4, 0.4, 0.4, 0.35, 0.35, 0.35, 0.35];

    /// <summary>强化单轮结算判定结果。</summary>
    public enum Outcome : byte
    {
        /// <summary>强化成功：资产变体升至目标等级。</summary>
        Success = 0,
        /// <summary>强化失败且携带保护材料：回落至 max(0, t-1)（即保持当前等级）。</summary>
        FailProtected = 1,
        /// <summary>强化失败且无护符：强化等级归零回到基础键。</summary>
        FailReset = 2,
    }

    /// <summary>强化行动准入裁决。</summary>
    public enum Check : byte
    {
        /// <summary>本轮可以开跑。</summary>
        Ready = 0,
        /// <summary>当前等级已达到意图目标：行动提前完成出队。</summary>
        TargetReached = 1,
        /// <summary>前置条件不满足：行动阻塞（余额不足/物品不存在）。</summary>
        Blocked = 2,
    }

    /// <summary>一次强化轮次的准入快照：目标、成本、成功率在开轮时冻结，整轮内不变。</summary>
    public sealed record Preparation(
        ItemId ItemId,
        int CurrentLevel,
        int TargetLevel,
        double EssenceCost,
        double SpiritStoneCost,
        ItemId? ProtectionItemId,
        double SuccessRate);

    /// <summary>解析强化 +N 资产变体：基础键返回 0，非强化变体（无法解析或负级）返回 null。</summary>
    public static int? ParseLevel(string? variant)
    {
        if (variant is null || variant.Length == 0) return 0;
        if (variant[0] != '+') return null;
        return int.TryParse(variant.AsSpan(1), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var level) && level >= 0 ? level : null;
    }

    /// <summary>强化等级对应的资产键：0 级回到基础键，N 级映射 "+N" 变体。</summary>
    public static AssetKey KeyFor(ItemId itemId, int level)
        => level <= 0 ? new AssetKey(itemId) : new AssetKey(itemId, "+" + level.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// 目标装备当前强化等级：已装备槽位记录为权威（与余额键一致由宿主保证），
    /// 其次扫描有效余额视图中的 +N 变体取最高级；未持有目标装备返回 -1。
    /// </summary>
    public static int CurrentLevel(PlayerLoadoutState loadout, ItemId itemId,
        IReadOnlyDictionary<AssetKey, double> holdings)
    {
        var level = -1;
        for (var i = 0; i < loadout.Equipment.Count; i++)
            if (string.Equals(loadout.Equipment[i].ItemId, itemId.Value, StringComparison.Ordinal)
                && loadout.Equipment[i].EnhancementLevel > level)
                level = (int)loadout.Equipment[i].EnhancementLevel;
        foreach (var (asset, quantity) in holdings)
        {
            if (asset.ItemId != itemId || quantity < 1 || ParseLevel(asset.Variant) is not { } parsed) continue;
            if (parsed > level) level = parsed;
        }
        return level;
    }

    /// <summary>当次强化（目标等级 t）基础耗时秒数：5 + t；技艺速度加成由行动层另乘。</summary>
    public static double DurationSeconds(int targetLevel) => BaseDurationSeconds + targetLevel;

    /// <summary>当次强化精华消耗：max(1, floor(t/2))。</summary>
    public static double EssenceCostFor(int targetLevel) => Math.Max(1, Math.Floor(targetLevel / 2d));

    /// <summary>当次强化灵石消耗：t × 10。</summary>
    public static double SpiritStoneCostFor(int targetLevel) => targetLevel * 10d;

    /// <summary>基础成功率：t≤10 查表，t≥11 按 0.3×0.95^(t-11)，结果 clamp [0.01, 0.5]。</summary>
    public static double BaseSuccessRate(int targetLevel)
    {
        var rate = targetLevel <= SuccessTable.Length
            ? SuccessTable[targetLevel - 1]
            : 0.3 * Math.Pow(0.95, targetLevel - 11);
        return Math.Clamp(rate, 0.01, 0.5);
    }

    /// <summary>技艺修正后成功率：L≥t 加性 +0.01×L，L&lt;t 乘性 ×0.9^(t-L)，终值 clamp [0, 0.99]。</summary>
    public static double SuccessRate(int targetLevel, double techniqueLevel)
    {
        var rate = BaseSuccessRate(targetLevel);
        rate = techniqueLevel >= targetLevel
            ? rate + 0.01 * techniqueLevel
            : rate * Math.Pow(0.9, targetLevel - techniqueLevel);
        return Math.Clamp(rate, 0, 0.99);
    }

    /// <summary>
    /// 强化行动开轮准入结算：目标装备、当前等级、意图目标、材料、护符与成功率一次冻结。
    /// 返回 null 表示不可进入强化裁决（check 给出 Blocked 或 TargetReached）。
    /// </summary>
    /// <param name="loadout">装配分段（装备槽等级与技艺经验来源）。</param>
    /// <param name="intent">行动意图（目标/辅助物品与参数来源）。</param>
    /// <param name="holdings">钱包与本轮暂存增量合并后的有效余额视图。</param>
    /// <param name="gameData">配表（装备存在性判定需要）。</param>
    /// <param name="techniqueLevel">强化技艺当前等级。</param>
    /// <param name="check">准入裁决。</param>
    /// <param name="reason">非 Ready 时的事件文案。</param>
    public static Preparation? Resolve(PlayerLoadoutState loadout, ActionIntent intent,
        IReadOnlyDictionary<AssetKey, double> holdings, GameDataCatalog? gameData,
        double techniqueLevel, out Check check, out string reason)
    {
        check = Check.Blocked;
        reason = "强化材料不足，行动受阻。";
        if (intent.TargetItemId is not { } targetText)
        {
            reason = "强化目标装备无效。";
            return null;
        }
        var itemId = new ItemId(targetText);
        if (gameData is null || !itemId.IsValid || !gameData.RawEquipments.TryGetValue(targetText, out var equipment))
        {
            reason = "强化目标装备无效。";
            return null;
        }
        // 保护材料裁决：装备配表 enhanceProtectionItems 非空时取其列表内任一材料；
        // 未配置时允许以同装备无强化基础件（AssetKey 裸变体）代护。
        ItemId? protectionItemId = null;
        if (intent.AuxItemId is { } auxText)
        {
            var auxId = new ItemId(auxText);
            if (!auxId.IsValid)
            {
                reason = "强化保护材料无效。";
                return null;
            }
            var allowed = ReadProtectionItems(equipment);
            var valid = allowed is { Length: > 0 }
                ? allowed.Contains(auxText)
                : string.Equals(auxText, targetText, StringComparison.Ordinal);
            if (!valid)
            {
                reason = "强化保护材料无效。";
                return null;
            }
            protectionItemId = auxId;
        }
        var current = CurrentLevel(loadout, itemId, holdings);
        if (current < 0 || current >= MaxLevel)
        {
            reason = current < 0 ? "未持有目标装备，强化受阻。" : "装备已达强化上限。";
            return null;
        }
        // 意图目标等级：缺省按上限处理；当前等级已达标时行动提前完成出队。
        var targetCap = MaxLevel;
        if (intent.Parameters is not null
            && intent.Parameters.TryGetValue(TargetLevelParameter, out var targetParameter)
            && double.IsFinite(targetParameter) && targetParameter > 0)
            targetCap = Math.Min(MaxLevel, (int)Math.Floor(targetParameter));
        if (current >= targetCap)
        {
            check = Check.TargetReached;
            reason = string.Empty;
            return null;
        }
        var target = current + 1;
        var essenceCost = EssenceCostFor(target);
        var spiritStoneCost = SpiritStoneCostFor(target);
        if (holdings.GetValueOrDefault(new AssetKey(new ItemId(EssenceItemId))) < essenceCost
            || holdings.GetValueOrDefault(new AssetKey(new ItemId(SpiritStoneItemId))) < spiritStoneCost)
            return null;
        if (protectionItemId is { } protectionId)
        {
            var protectionKey = new AssetKey(protectionId);
            // 保护材料恒为无强化基础件；当保护材料即目标装备且当前等级为 0 时，
            // 本体与代护件共用同一资产键，需 ≥2（1 本体 + 1 代护）。
            var required = protectionKey == new AssetKey(itemId) && current == 0 ? 2 : 1;
            if (holdings.GetValueOrDefault(protectionKey) < required)
            {
                reason = "强化保护材料不足，行动受阻。";
                return null;
            }
        }
        check = Check.Ready;
        reason = string.Empty;
        return new Preparation(itemId, current, target, essenceCost, spiritStoneCost,
            protectionItemId, SuccessRate(target, techniqueLevel));
    }

    /// <summary>
    /// 当轮确定性判定：种子取行动实例 Id 前 8 字节，计数维度用已完成轮次，
    /// 独立通道保证与战斗随机流互不影响；同一实例同一轮次必得同一结果。
    /// </summary>
    public static Outcome Roll(Guid actionInstanceId, double completedRepeats, Preparation prep)
    {
        var seed = BitConverter.ToInt64(actionInstanceId.ToByteArray(), 0);
        var roll = DeterministicRoll.Next(seed, 0, completedRepeats, RollChannel);
        if (roll < prep.SuccessRate) return Outcome.Success;
        return prep.ProtectionItemId is not null ? Outcome.FailProtected : Outcome.FailReset;
    }

    /// <summary>装备配表 enhanceProtectionItems 数组读出；缺省/非数组/空串条目剔除，返回 null 表示未配置。</summary>
    private static string[]? ReadProtectionItems(System.Text.Json.JsonElement equipment)
    {
        if (!equipment.TryGetProperty("enhanceProtectionItems", out var items)
            || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        var list = items.EnumerateArray()
            .Where(entry => entry.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(entry => entry.GetString()!)
            .Where(text => text.Length > 0)
            .ToArray();
        return list.Length > 0 ? list : null;
    }

    /// <summary>判定结果对应的迁移后强化等级。</summary>
    public static int ResultLevel(Preparation prep, Outcome outcome) => outcome switch
    {
        Outcome.Success => prep.TargetLevel,
        Outcome.FailProtected => Math.Max(0, prep.TargetLevel - 1),
        _ => 0
    };
}
