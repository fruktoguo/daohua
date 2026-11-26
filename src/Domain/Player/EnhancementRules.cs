using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Simulation;

namespace Domain.Player;

/// <summary>
/// 装备强化单次结算规则（总纲 §20.4）：目标等级、成本、成功率、确定性判定与结果折算。
/// 本类只承载无状态公式；钱包合并视图、资产变体迁移与队列裁决由行动执行层驱动。
/// </summary>
public static class EnhancementRules
{
    /// <summary>装备强化行动 ID。</summary>
    public const string ActionId = "enhance.equipment";
    /// <summary>强化等级上限；目标等级 t 超出后行动阻塞。</summary>
    public const int MaxLevel = 20;
    /// <summary>高阶强化成功率封顶起算目标等级（t ≥ 11，即 +10→+11 及以上受 45% 硬上限约束）。</summary>
    public const int HighTierSuccessCapLevel = 11;
    /// <summary>高阶强化（t ≥ 11）单次成功率硬上限（45%），防止跨过 50% 对称点引发期望轮次坍塌。</summary>
    public const double HighTierMaxSuccessRate = 0.45;
    /// <summary>秘法等级超出目标等级（L &gt; t）时每超出 1 级的线性乘算提升比率（0.5%）。</summary>
    public const double OverlevelSuccessRatioPerLevel = 0.005;
    /// <summary>单次强化基础耗时基数：实际耗时 = BaseDurationSeconds + 目标等级。</summary>
    public const double BaseDurationSeconds = 5;
    /// <summary>保护材料意图槽键：AuxItemId 指定每轮消耗的保护材料 ID（装备配表 enhanceProtectionItems 列表内任一材料，或未配置时同装备无强化基础件）。</summary>
    /// <summary>强化灵石货币 ID；每次强化消耗 t×10。</summary>
    public const string SpiritStoneItemId = "currency.spirit-stone";
    /// <summary>目标等级意图参数键：当前强化等级达到该值后行动提前完成出队。</summary>
    public const string TargetLevelParameter = "targetLevel";
    /// <summary>起始强化等级意图参数键：多变体共存时指定强化的起始等级。</summary>
    public const string InitialLevelParameter = "initialLevel";
    /// <summary>起护等级意图参数键：当前等级达到该阈值时才开始消耗材料进行掉级保护（失败仅回落一级），最低为 2。</summary>
    public const string ProtectFromLevelParameter = "protectLevel";

    /// <summary>强化判定独立通道：与战斗命中/暴击/掉落通道完全隔离，不扰战斗随机流。</summary>
    private const ulong RollChannel = 0xE0A1;

    // 目标等级 1..20 的基础成功率查表：+1 为 50%，+2..+3 为 45%，+4..+6 为 40%，+7..+10 为 35%，+11..+20 统一为 30%。
    private static readonly double[] SuccessTable =
    [
        0.50, 0.45, 0.45, 0.40, 0.40, 0.40, 0.35, 0.35, 0.35, 0.35,
        0.30, 0.30, 0.30, 0.30, 0.30, 0.30, 0.30, 0.30, 0.30, 0.30
    ];

    /// <summary>强化单轮结算判定结果。</summary>
    public enum Outcome : byte
    {
        /// <summary>强化成功：资产变体升至目标等级。</summary>
        Success = 0,
        /// <summary>强化失败且保护生效：较当前等级回落一级至 max(0, t-2)，下限 +0。</summary>
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

    /// <summary>强化等级对应的资产变体名：0 级返回空串（基础键），N 级映射 "+N"。</summary>
    public static string VariantForLevel(int level)
        => level <= 0 ? string.Empty : "+" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>强化等级对应的资产键：0 级回到基础键，N 级映射 "+N" 变体。</summary>
    public static AssetKey KeyFor(ItemId itemId, int level)
        => new(itemId, VariantForLevel(level));

    /// <summary>
    /// 目标装备当前强化等级（兼容无上限入参重载）：默认 targetCap=MaxLevel 且无首选等级。
    /// </summary>
    public static int CurrentLevel(PlayerLoadoutState loadout, ItemId itemId,
        IReadOnlyDictionary<AssetKey, double> holdings)
        => CurrentLevel(loadout, itemId, holdings, MaxLevel, null);

    /// <summary>统计已穿戴装备槽（含战斗装备与 tool-* 技艺工具）中指定物品与强化等级的件数。</summary>
    public static int CountEquipped(PlayerLoadoutState loadout, string itemId, int enhancementLevel)
        => CountEquipped(loadout.Equipment, itemId, enhancementLevel);

    /// <summary>统计装配快照中已穿戴装备槽（含战斗装备与 tool-* 技艺工具）的指定物品与强化等级件数。</summary>
    public static int CountEquipped(PlayerLoadoutDocument loadout, string itemId, int enhancementLevel)
        => CountEquipped(loadout.Equipment, itemId, enhancementLevel);

    private static int CountEquipped(IReadOnlyList<PlayerEquipmentSlot> equipment, string itemId, int enhancementLevel)
    {
        var count = 0;
        for (var i = 0; i < equipment.Count; i++)
        {
            var slot = equipment[i];
            if (string.Equals(slot.ItemId, itemId, StringComparison.Ordinal)
                && (int)Math.Floor(slot.EnhancementLevel) == enhancementLevel)
                count++;
        }
        return count;
    }

    /// <summary>解析一条强化队列行动当前锁定的装备强化等级（已追踪变体优先，否则回退意图 initialLevel，缺省 0）。</summary>
    public static int ResolveActionEnhancementLevel(IPlayerActionState action)
        => ResolveEnhancementLevel(action.Intent, action.EnhancementCurrentLevel);

    private static int ResolveEnhancementLevel(ActionIntent intent, int? currentLevel)
    {
        var level = currentLevel
            ?? (intent.Parameters is not null
                && intent.Parameters.TryGetValue(InitialLevelParameter, out var initialParam)
                && double.IsFinite(initialParam)
                ? (int)Math.Floor(initialParam)
                : 0);
        return Math.Clamp(level, 0, MaxLevel);
    }

    /// <summary>统计行动队列中正被强化行动锁定的指定物品与强化等级件数（可排除当前正在结算的行动自身）。</summary>
    public static int CountEnhanceLocked(IEnumerable<IPlayerActionState>? actions, string itemId, int enhancementLevel,
        Guid? excludeActionId = null)
    {
        if (actions is null) return 0;
        var count = 0;
        foreach (var action in actions)
        {
            if (excludeActionId is { } excluded && action.Id == excluded)
            {
                if (action.Intent.ActionId.Value == ActionId) break;
                continue;
            }
            if (MatchesEnhanceLock(action.Intent, action.EnhancementCurrentLevel, itemId, enhancementLevel))
                count++;
        }
        return count;
    }

    /// <summary>统计行动快照列表中正被强化行动锁定的指定物品与强化等级件数（可排除指定行动）。</summary>
    public static int CountEnhanceLocked(IEnumerable<QueuedActionState>? actions, string itemId, int enhancementLevel,
        Guid? excludeActionId = null)
    {
        if (actions is null) return 0;
        var count = 0;
        foreach (var action in actions)
        {
            if (excludeActionId is { } excluded && action.Id == excluded)
            {
                if (action.Intent.ActionId.Value == ActionId) break;
                continue;
            }
            if (MatchesEnhanceLock(action.Intent, action.EnhancementCurrentLevel, itemId, enhancementLevel))
                count++;
        }
        return count;
    }

    private static bool MatchesEnhanceLock(ActionIntent intent, int? currentLevel, string itemId, int enhancementLevel)
        => intent.ActionId.Value == ActionId
            && string.Equals(intent.TargetItemId, itemId, StringComparison.Ordinal)
            && ResolveEnhancementLevel(intent, currentLevel) == enhancementLevel;

    /// <summary>统计指定物品与强化等级的不可动用总件数（已穿戴件数 + 其他强化队列锁定件数）。</summary>
    public static int CountOccupied(PlayerLoadoutState loadout, IEnumerable<IPlayerActionState>? actions,
        string itemId, int enhancementLevel, Guid? excludeActionId = null)
        => CountEquipped(loadout, itemId, enhancementLevel)
            + CountEnhanceLocked(actions, itemId, enhancementLevel, excludeActionId);

    /// <summary>
    /// 目标装备当前强化等级：仅取背包内未穿戴且未被其他强化队列锁定的可用件；
    /// 若指定了 preferredLevel 且持存可用，优先使用 preferredLevel；
    /// 装备中、挂售中、其他强化队列锁定中均不可作为强化目标；背包内无可用件时返回 -1。
    /// </summary>
    public static int CurrentLevel(PlayerLoadoutState loadout, ItemId itemId,
        IReadOnlyDictionary<AssetKey, double> holdings, int targetCap, int? preferredLevel = null,
        IReadOnlyList<IPlayerActionState>? actions = null, Guid? excludeActionId = null)
    {
        var level = -1;
        Span<int> occupiedCounts = stackalloc int[MaxLevel + 1];
        occupiedCounts.Clear();
        for (var i = 0; i < loadout.Equipment.Count; i++)
        {
            var slot = loadout.Equipment[i];
            if (string.Equals(slot.ItemId, itemId.Value, StringComparison.Ordinal))
            {
                var lvl = (int)Math.Floor(slot.EnhancementLevel);
                if (lvl >= 0 && lvl <= MaxLevel)
                    occupiedCounts[lvl]++;
            }
        }
        if (actions is not null)
        {
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (excludeActionId is { } excluded && action.Id == excluded)
                {
                    if (action.Intent.ActionId.Value == ActionId) break;
                    continue;
                }
                if (action.Intent.ActionId.Value == ActionId
                    && string.Equals(action.Intent.TargetItemId, itemId.Value, StringComparison.Ordinal))
                {
                    var lvl = ResolveActionEnhancementLevel(action);
                    occupiedCounts[lvl]++;
                }
            }
        }

        if (preferredLevel is { } pref && pref >= 0 && pref < targetCap && pref <= MaxLevel)
        {
            var prefKey = KeyFor(itemId, pref);
            var prefAvailable = holdings.GetValueOrDefault(prefKey) - occupiedCounts[pref];
            if (prefAvailable >= 1)
                return pref;
        }

        foreach (var (asset, quantity) in holdings)
        {
            if (asset.ItemId != itemId || quantity < 1 || ParseLevel(asset.Variant) is not { } parsed) continue;
            if (parsed < 0 || parsed >= targetCap || parsed > MaxLevel) continue;
            var available = quantity - occupiedCounts[parsed];
            if (available < 1) continue;
            if (parsed > level) level = parsed;
        }
        return level;
    }

    /// <summary>当次强化（目标等级 t）基础耗时秒数：5 + t；技艺速度加成由行动层另乘。</summary>
    public static double DurationSeconds(int targetLevel) => BaseDurationSeconds + targetLevel;

    /// <summary>当次强化灵石消耗：t × 10。</summary>
    public static double SpiritStoneCostFor(int targetLevel) => targetLevel * 10d;

    /// <summary>基础成功率：t≤20 查表，超出兜底 0.30，结果 clamp [0.01, 0.5]。</summary>
    public static double BaseSuccessRate(int targetLevel)
    {
        var rate = targetLevel >= 1 && targetLevel <= SuccessTable.Length
            ? SuccessTable[targetLevel - 1]
            : 0.30;
        return Math.Clamp(rate, 0.01, 0.5);
    }

    /// <summary>
    /// 技艺、辅助消耗品与强化工具修正后成功率（总纲 §20.4.2 / §20.7.2）：
    /// L≥t 仅对超出目标等级部分线性乘算 ×(1+0.005×(L-t))，L&lt;t 乘性衰减 ×0.9^(t-L)；
    /// 叠加消耗品与已穿戴强化工具固定加成后分段封顶：t≤10 clamp [0, 0.99]，t≥11 clamp [0, 0.45]。
    /// </summary>
    public static double SuccessRate(int targetLevel, double techniqueLevel, double consumableBonus = 0)
    {
        var rate = BaseSuccessRate(targetLevel);
        rate = techniqueLevel >= targetLevel
            ? rate * (1.0 + OverlevelSuccessRatioPerLevel * (techniqueLevel - targetLevel))
            : rate * Math.Pow(0.9, targetLevel - techniqueLevel);
        rate += consumableBonus;
        var maxRate = targetLevel >= HighTierSuccessCapLevel ? HighTierMaxSuccessRate : 0.99;
        return Math.Clamp(rate, 0, maxRate);
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
        double techniqueLevel, out Check check, out string reason, int? currentTrackedLevel = null,
        double consumableBonus = 0, IReadOnlyList<IPlayerActionState>? actions = null, Guid? excludeActionId = null)
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
        // 意图目标等级：缺省按上限处理；当前等级已达标时行动提前完成出队。
        var targetCap = MaxLevel;
        if (intent.Parameters is not null
            && intent.Parameters.TryGetValue(TargetLevelParameter, out var targetParameter)
            && double.IsFinite(targetParameter) && targetParameter > 0)
            targetCap = Math.Min(MaxLevel, (int)Math.Floor(targetParameter));

        int current;
        if (currentTrackedLevel is { } tracked)
        {
            if (tracked >= targetCap)
            {
                check = Check.TargetReached;
                reason = string.Empty;
                return null;
            }
            if (tracked >= MaxLevel)
            {
                reason = "装备已达强化上限。";
                return null;
            }
            var heldKey = KeyFor(itemId, tracked);
            var occupiedCount = CountOccupied(loadout, actions, itemId.Value, tracked, excludeActionId);
            if (holdings.GetValueOrDefault(heldKey) - occupiedCount < 1)
            {
                reason = "未持有可用目标装备，强化受阻。";
                return null;
            }
            current = tracked;
        }
        else
        {
            int? preferredLevel = null;
            if (intent.Parameters is not null
                && intent.Parameters.TryGetValue(InitialLevelParameter, out var initialParameter)
                && double.IsFinite(initialParameter) && initialParameter >= 0)
                preferredLevel = (int)Math.Floor(initialParameter);

            current = CurrentLevel(loadout, itemId, holdings, targetCap, preferredLevel, actions, excludeActionId);
            if (current < 0)
            {
                var maxHeld = CurrentLevel(loadout, itemId, holdings, MaxLevel + 1, null, actions, excludeActionId);
                if (maxHeld >= targetCap)
                {
                    check = Check.TargetReached;
                    reason = string.Empty;
                    return null;
                }
                if (maxHeld >= MaxLevel)
                {
                    reason = "装备已达强化上限。";
                    return null;
                }
                reason = "未持有可用目标装备，强化受阻。";
                return null;
            }
        }
        var target = current + 1;
        var spiritStoneCost = SpiritStoneCostFor(target);
        if (holdings.GetValueOrDefault(new AssetKey(new ItemId(SpiritStoneItemId))) < spiritStoneCost)
            return null;
        var protectLevel = 0;
        if (intent.Parameters is not null
            && intent.Parameters.TryGetValue(ProtectFromLevelParameter, out var protectParameter)
            && double.IsFinite(protectParameter))
            protectLevel = Math.Max(2, (int)Math.Floor(protectParameter));

        ItemId? effectiveProtectionItemId = null;
        if (protectionItemId is { } protectionId && current >= protectLevel)
        {
            var protectionKey = new AssetKey(protectionId);
            // 保护材料仅限背包内未穿戴且未被其他强化队列锁定的 0 级件
            var occupiedProtection = CountOccupied(loadout, actions, protectionId.Value, 0, excludeActionId);
            var availableProtection = holdings.GetValueOrDefault(protectionKey) - occupiedProtection;
            // 保护材料恒为无强化基础件；当保护材料即目标装备且当前等级为 0 时，
            // 本体与代护件共用同一资产键，需 ≥2（1 本体 + 1 代护）。
            var required = protectionKey == new AssetKey(itemId) && current == 0 ? 2 : 1;
            if (availableProtection < required)
            {
                reason = "强化保护材料不足，行动受阻。";
                return null;
            }
            effectiveProtectionItemId = protectionId;
        }
        check = Check.Ready;
        reason = string.Empty;
        var toolSuccessBonus = TechniqueCatalog.EquippedToolEnhancementSuccessBonus(loadout, gameData);
        return new Preparation(itemId, current, target, spiritStoneCost,
            effectiveProtectionItemId, SuccessRate(target, techniqueLevel, consumableBonus + toolSuccessBonus));
    }

    /// <summary>
    /// 获取玩家强化自动栏（AutoUseBar.Enhancement）中当前生效的持续消耗品所提供的强化成功率加成（如淬灵粉 +0.03、固灵砂 +0.05）。
    /// 对声明非零 <see cref="ConsumableUniqueEffect"/> 的同组消耗品仅取最高等级一项，不重复累加。
    /// </summary>
    public static double EffectiveEnhancementSuccessBonus(
        IReadOnlyList<AutoUseSlotState>? slots,
        GameDataCatalog? gameData)
    {
        if (slots is null || slots.Count == 0 || gameData is null) return 0;
        var bonus = 0d;
        Dictionary<ConsumableUniqueEffect, ((int Level, int Tier, double Bonus) Rank, double Bonus)>? uniqueWinners = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null || slot.RemainingDurationSeconds <= 0) continue;
            if (!string.Equals(slot.ActionType, Domain.GameData.AutoUseBar.Enhancement, StringComparison.OrdinalIgnoreCase)) continue;
            if (!gameData.Items.TryGetValue(new ItemId(slot.ItemId), out var item) || item.Consumable is null) continue;
            if (item.Consumable.EnhancementSuccessBonus <= 0) continue;
            if (item.Consumable.UniqueEffect != ConsumableUniqueEffect.None)
            {
                uniqueWinners ??= [];
                var rank = (item.Level, item.Tier, item.Consumable.EnhancementSuccessBonus);
                if (!uniqueWinners.TryGetValue(item.Consumable.UniqueEffect, out var current)
                    || rank.CompareTo(current.Rank) > 0)
                    uniqueWinners[item.Consumable.UniqueEffect] = (rank, item.Consumable.EnhancementSuccessBonus);
            }
            else
            {
                bonus += item.Consumable.EnhancementSuccessBonus;
            }
        }
        if (uniqueWinners is not null)
        {
            foreach (var winner in uniqueWinners.Values)
                bonus += winner.Bonus;
        }
        return bonus;
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
        Outcome.FailProtected => Math.Max(0, prep.CurrentLevel - 1),
        _ => 0
    };

    /// <summary>
    /// 装备强化各等级受保折旧常数表（总纲 §4.6.2，alpha = 0.50）：
    /// Beta: 累计期望保护消耗件数加成系数（无量纲，须乘单件保护单价 P 折算灵石）；
    /// Fee: 累计期望手续费残值返还（灵石）。
    /// </summary>
    private static readonly (double Beta, double Fee)[] EnhancementDepreciationTable =
    [
        (0.000, 0),     // +0
        (0.500, 10),    // +1
        (1.111, 32),    // +2
        (1.722, 66),    // +3
        (2.472, 116),   // +4
        (3.222, 178),   // +5
        (3.972, 253),   // +6
        (4.901, 353),   // +7
        (5.829, 467),   // +8
        (6.758, 596),   // +9
        (7.687, 739),   // +10
        (8.853, 922),   // +11
        (10.020, 1122), // +12
        (11.187, 1339), // +13
        (12.353, 1572), // +14
        (13.520, 1822), // +15
        (14.687, 2089), // +16
        (15.853, 2372), // +17
        (17.020, 2672), // +18
        (18.187, 2989), // +19
        (19.353, 3322), // +20
    ];

    /// <summary>
    /// 计算指定强化等级的装备商店回收单价（总纲 §4.6.2 50% 额外期望造价折旧闭式查表）：
    /// SellPrice(+N) = round(V0 + beta_N * P + F_N)；
    /// P 为单件保护材料回收单价（见 <see cref="ResolveProtectionUnitPrice"/>），非法入参按 V0 兜底；
    /// N=0 时恒等于 V0；N 越界按 [0, MaxLevel] 截断。
    /// </summary>
    public static double CalculateEquipmentSellPrice(double baseSellPrice, double protectionUnitPrice, int enhancementLevel)
    {
        if (baseSellPrice <= 0 || !double.IsFinite(baseSellPrice)) return 0;
        var level = Math.Clamp(enhancementLevel, 0, MaxLevel);
        var (beta, fee) = EnhancementDepreciationTable[level];
        var unitProtection = protectionUnitPrice > 0 && double.IsFinite(protectionUnitPrice)
            ? protectionUnitPrice
            : baseSellPrice;
        return Math.Round(baseSellPrice + beta * unitProtection + fee);
    }

    /// <summary>
    /// 单件强化保护材料的系统回收单价 P（总纲 §4.6.2）：
    /// 装备配表 enhanceProtectionItems 非空时取列表内可回收材料的最低回收单价
    /// （item.* 按 sellPrice、equipment.* 按其白板回收价递归解算，理性玩家恒选最廉代护）；
    /// 未配置或全部不可定价时按白板价 V0 兜底（同装备无强化基础件代护）。
    /// </summary>
    public static double ResolveProtectionUnitPrice(GameDataCatalog gameData, string equipmentId,
        double baseSellPrice, IDictionary<string, double>? costCache = null)
    {
        if (baseSellPrice <= 0 || !double.IsFinite(baseSellPrice)) return 0;
        if (!gameData.RawEquipments.TryGetValue(equipmentId, out var equipment)) return baseSellPrice;
        var allowed = ReadProtectionItems(equipment);
        if (allowed is null) return baseSellPrice;
        var min = double.PositiveInfinity;
        foreach (var id in allowed)
        {
            double? price = id.StartsWith("equipment.", StringComparison.Ordinal)
                ? ComputeEquipmentBaseSellPrice(gameData, id, costCache)
                : gameData.Items.TryGetValue(new ItemId(id), out var item) && item.SellPrice > 0
                    ? item.SellPrice
                    : null;
            if (price is { } p && p < min) min = p;
        }
        return double.IsFinite(min) ? min : baseSellPrice;
    }

    /// <summary>
    /// 一步到位解算装备指定强化等级的商店回收单价：白板价 V0 与保护单价 P 一并推导；
    /// 配方不存在或材料无回收价时返回 null（不可回收口径）。
    /// </summary>
    public static double? ComputeEquipmentSellPrice(GameDataCatalog gameData, string equipmentId,
        int enhancementLevel, IDictionary<string, double>? costCache = null)
    {
        if (ComputeEquipmentBaseSellPrice(gameData, equipmentId, costCache) is not { } basePrice || basePrice <= 0)
            return null;
        var protectionPrice = ResolveProtectionUnitPrice(gameData, equipmentId, basePrice, costCache);
        return CalculateEquipmentSellPrice(basePrice, protectionPrice, enhancementLevel);
    }

    /// <summary>
    /// 装备锻造材料成本价：定位 yields 产出该装备的锻造行动，inputs 中 item.* 按回收价×数量求和、
    /// equipment.* 投入递归展开（T2 配方含 T1 装备投入）；配表成环安全保护上限为 4 层。
    /// </summary>
    public static double? ComputeEquipmentMaterialCost(GameDataCatalog gameData, string equipmentId,
        IDictionary<string, double>? cache = null, int depth = 0)
    {
        const int MaxRecipeDepth = 4;
        if (depth > MaxRecipeDepth)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"装备配方递归超过深度上限（疑似配表成环）：{equipmentId}。");
        if (cache is not null)
        {
            lock (cache)
            {
                if (cache.TryGetValue(equipmentId, out var cached)) return cached;
            }
        }
        var recipe = gameData.Actions.All.FirstOrDefault(action =>
            action.Yields.Any(yield => yield.Item is { } product && string.Equals(product.Value, equipmentId, StringComparison.Ordinal)));
        if (recipe is null) return null;
        double cost = 0;
        foreach (var (input, quantity) in recipe.Inputs)
        {
            if (input.Value.StartsWith("equipment.", StringComparison.Ordinal))
            {
                if (ComputeEquipmentMaterialCost(gameData, input.Value, cache, depth + 1) is not { } nested) return null;
                cost += nested * quantity;
            }
            else if (gameData.Items.TryGetValue(input, out var material) && material.SellPrice > 0)
            {
                cost += material.SellPrice * quantity;
            }
            else
            {
                return null;
            }
        }
        if (cache is not null)
        {
            lock (cache)
            {
                cache[equipmentId] = cost;
            }
        }
        return cost;
    }

    /// <summary>
    /// 装备白板商店基础回收售价 V0（总纲 §4.6.1 制造翻倍铁律：原料总成本 * 2.0）。
    /// 若配方不存在或材料无回收价，返回 null。
    /// </summary>
    public static double? ComputeEquipmentBaseSellPrice(GameDataCatalog gameData, string equipmentId,
        IDictionary<string, double>? costCache = null)
    {
        if (ComputeEquipmentMaterialCost(gameData, equipmentId, costCache, 0) is { } cost)
            return cost * 2.0;
        return null;
    }
}
