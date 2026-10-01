using System.Text.Json;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using SimulationEventTail = DaoHua.Ecs.SimulationEngine.SimulationEventTail;

namespace DaoHua.Ecs.Systems;

/// <summary>计时行动的 System 所属规则：准入、用品补能、整秒推进和单轮结算共用一套实现。</summary>
internal static partial class ActionExecutionRules
{
    private const double SecondQuantizationEpsilon = 1e-9;
    private const double MaxWholeSecondsPerApply = 1_000_000_000;

    /// <summary>受阻队首只执行一次出队或轮转；false 表示当前窗口剩余时间保持停泊。</summary>
    public static bool AdvanceBlockedAction(IPlayerActionContext player, IPlayerActionState action, DateTimeOffset at)
    {
        if (!player.Settings.LoopEnabled)
        {
            player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: at.ToUnixTimeMilliseconds() / 1000d);
            return true;
        }
        if (player.Actions.Count <= 1) return false;
        for (var i = 0; i < player.Actions.Count; i++)
            if (player.Actions[i].Status != ActionQueueStatus.Blocked)
            {
                player.RotateActionToTail(action.Id);
                return true;
            }
        return false;
    }

    /// <summary>按窗口工作钱包与尚未提交的材料增量检查地点和库存，再建账并激活队首。</summary>
    public static bool TryStartAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => TryStartActionCore(player, action, definition, gameData, assets, at,
            mutations.Deltas, ref mutations.EventTail);

    internal static bool TryStartActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.start");
        var availableHere = definition.AllowedNodeIds is null || definition.AllowedNodeIds.Count == 0
            || (player.World.EdgeId is null && definition.AllowedNodeIds.Contains(player.World.NodeId));
        var hasInputs = true;
        foreach (var input in definition.Inputs)
            if ((assets?.Get(new AssetKey(input.Key)) ?? (gameData is null ? double.PositiveInfinity : 0))
                + (deltas?.GetValueOrDefault(input.Key) ?? 0) < input.Value)
            {
                hasInputs = false;
                break;
            }
        if (!availableHere || !hasInputs)
        {
            // 地点不符且自动移动开启：插队赶路至入队锚点替代 Blocked；任一前置不满足回落原语义。
            if (!availableHere && TryEnqueueAutoTravel(player, action, definition, gameData, at, ref events))
                return false;
            action.Status = ActionQueueStatus.Blocked;
            events ??= new();
            events.Add(new SimulationEvent(at, "action.blocked",
                availableHere ? "材料不足，行动受阻。" : "当前位置不允许执行该行动。", definition.Id));
            return false;
        }
        player.EnsureActionReport(action.Id, definition.Id, definition.Name, at);
        action.Status = ActionQueueStatus.Running;
        return true;
    }

    /// <summary>
    /// 自动移动准入修正：开轮地点校验失败时，按入队锚点节点插队一条 world.travel 至队首，
    /// 抵达锚点后原行动按挂起恢复语义续跑。仅在 AutoTravelEnabled 开启、锚点仍在允许节点集、
    /// 世界图可达时生效；否则返回 false 由调用方走原 Blocked 跳过路径。
    /// </summary>
    internal static bool TryEnqueueAutoTravel(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, DateTimeOffset at,
        ref SimulationEventTail? events)
    {
        var anchor = action.Intent.AnchorNodeId;
        var travelId = new ActionId("world.travel");
        if (!player.Settings.AutoTravelEnabled
            || gameData?.WorldGraph is not { } worldGraph
            || string.IsNullOrWhiteSpace(anchor)
            || definition.AllowedNodeIds is not { } allowedNodes
            || !allowedNodes.Contains(anchor)
            || !worldGraph.TryGetNode(anchor, out var anchorNode)
            || !gameData.Actions.Contains(travelId))
            return false;
        TravelPlan plan;
        try
        {
            plan = worldGraph.PlanFromPosition(player.World.EdgeId, player.World.EdgeProgress,
                player.World.NodeId, anchor);
        }
        catch (DomainException)
        {
            return false;
        }
        // 与 world.travel 命令同一条插队通道；模拟内不额外推版本，仅标脏 ActionQueue。
        player.EnqueuePriority(new ActionIntent(travelId, 1, anchor), at,
            gameData.Actions,
            new TravelActionState(anchor, plan.TotalTravelSeconds, plan.EdgeIds, plan.Segments),
            incrementVersion: false);
        events ??= new();
        events.Add(new SimulationEvent(at, "action.auto-travel",
            $"当前位置无法执行，自动前往 {anchorNode.Name}。", definition.Id));
        return true;
    }

    /// <summary>计时行动的建筑加速只修正本轮时长，不改变按轮扣除的持续用品时长。</summary>
    public static double TimedActionDuration(IPlayerActionContext player, ActionDefinition definition)
    {
        var duration = definition.DurationSeconds;
        var buildingSpeed = player.World.EffectiveNodeBuildingEffects.SpeedRatioFor(definition.Kind);
        if (buildingSpeed > 0) duration /= 1 + buildingSpeed;
        return duration;
    }

    /// <summary>本轮开始前补充持续用品并解析收益倍率；调用方已完成地点和材料准入。</summary>
    public static double PrepareTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => PrepareTimedActionCore(player, action, definition,
            action.ProgressSeconds <= SecondQuantizationEpsilon, gameData, assets, at,
            ref mutations.Deltas, ref mutations.EventTail);

    internal static double PrepareTimedActionCore(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, bool isActionStart, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.prepare");
        var multiplier = PrepareSustainedAutoUse(player, definition, isActionStart, gameData,
            assets, at, action.Id, ref deltas, ref events);
        if (definition.Kind == ActionKind.Cultivation && gameData is not null)
            multiplier *= CultivationMultiplier(gameData, player.World.NodeId);
        multiplier *= 1 + player.World.EffectiveNodeBuildingEffects.OutputRatioFor(definition.Kind);
        return multiplier;
    }

    /// <summary>推进已由时钟裁剪的本轮片段，按原整秒序列写入进度与连续收益。</summary>
    public static void AdvanceTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        double stepSeconds, DateTimeOffset at)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.advance");
        var wholeSeconds = QuantizeElapsedSeconds(player, stepSeconds);
        action.ProgressSeconds += wholeSeconds;
        if (definition.Kind == ActionKind.Cultivation && wholeSeconds > 0)
            player.Progression.CultivationSeconds += wholeSeconds;
        ApplyContinuousReward(player, definition, wholeSeconds, multiplier, gameData,
            action.Id, at.ToUnixTimeMilliseconds() / 1000d);
        if (stepSeconds > 0)
            player.MarkDirty(definition.Kind switch
            {
                ActionKind.Cultivation => PlayerSectionKind.Progression,
                ActionKind.Exploration => PlayerSectionKind.World,
                ActionKind.Combat => PlayerSectionKind.Combat,
                _ => PlayerSectionKind.ActionQueue
            });
    }

    /// <summary>单轮完成结算：保留原物品、材料、用品、统计和队列修改的先后顺序。</summary>
    public static void CompleteTimedAction(IPlayerActionContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData,
        DateTimeOffset at, ActionMutationBatch mutations)
        => CompleteRepeat(player, action, definition, multiplier, gameData, at,
            ref mutations.Deltas, ref mutations.EventTail, ref mutations.ActionCount, ref mutations.RepeatCount);

    /// <summary>
    /// 把任意切分的模拟秒数累进持久化进位余数（carry）并取整整秒。
    /// 1e-9 量级贴整后再向下取整，使“一次推进 N 秒”与“逐秒推进 N 次”产生完全相同的整秒序列，
    /// 连续奖励的浮点操作序列因此位级一致；余数保留到下次推进。
    /// </summary>
    internal static long QuantizeElapsedSeconds(IPlayerActionContext player, double seconds)
    {
        if (seconds <= 0) return 0;
        var carry = player.Progression.SimulationCarrySeconds + seconds;
        var nearest = Math.Round(carry);
        if (Math.Abs(carry - nearest) <= SecondQuantizationEpsilon) carry = nearest;
        var floorSeconds = Math.Floor(carry);
        player.Progression.SimulationCarrySeconds = carry - floorSeconds;
        if (floorSeconds > MaxWholeSecondsPerApply)
            throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟整秒数超出可推进范围。");
        return (long)floorSeconds;
    }

    /// <summary>
    /// 结算行动声明的按秒收益（per=second 的修为/灵力/气血/经验等持续通道）。
    /// 逐整秒循环累加，严格确保大跨度离线快进与客户端逐秒在线推进的 IEEE 754 浮点累加序列完全一致；
    /// 灵力与气血按聚合内已刷新的属性上限逐秒截断。
    /// </summary>
    internal static void ApplyContinuousReward(IPlayerActionContext player, ActionDefinition definition, long wholeSeconds,
        double multiplier, GameDataCatalog? gameData, Guid actionInstanceId, double atUnixSeconds)
    {
        if (wholeSeconds <= 0) return;
        // 本层修为上限：无境界进度配置时取 +∞（不封顶也不沉淀）；封顶 0 表示满级恒锁定。
        var expCap = gameData?.RealmProgression?.ExpCapFor(player.Progression.RealmLevel) ?? double.PositiveInfinity;
        // 溢出预检查沿用“先校验后变更”语义：rate * 整秒数越界时不得污染任何进度数值。
        var seconds = (double)wholeSeconds;
        var combatTouched = false;
        var progressionTouched = false;
        // 账本按实际入账量记录：修为记瓶颈截断与底蕴兑换放大后的真实增量，
        // 底蕴记净变动（满层沉淀为正、兑换消耗为负），战斗经验按增幅前基础量等额记账。
        var experienceCredited = 0d;
        var foundationDelta = 0d;
        var combatExperienceGained = 0d;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerSecond) continue;
            var rate = yield.Amount * multiplier;
            var total = rate * seconds;
            if (!double.IsFinite(rate) || !double.IsFinite(total))
                throw new DomainException(DomainErrorCode.InvariantViolation, "行动收益超出数值范围。");
            switch (yield.Kind)
            {
                case ActionYieldKind.Experience:
                    // 修为类收益逐秒走统一结算：瓶颈截断 + 底蕴 1:1 兑换放大（单笔 ≤3×rate）+
                    // 未入账增幅沉淀为底蕴 + 战斗经验按每秒基础量入账；满级（cap≤0）全额沉淀。
                    if (!double.IsFinite(player.Progression.Experience + 3 * total)
                        || !double.IsFinite(player.Progression.Foundation + total)
                        || !double.IsFinite(player.Progression.CombatExperience + yield.Amount * seconds))
                        throw new DomainException(DomainErrorCode.InvariantViolation, "经验收益超出数值范围。");
                    for (var i = 0; i < wholeSeconds; i++)
                        experienceCredited += player.Progression.CreditExperience(yield.Amount, rate, expCap);
                    foundationDelta += total - experienceCredited;
                    combatExperienceGained += yield.Amount * seconds;
                    progressionTouched = true;
                    break;
                case ActionYieldKind.Mana:
                    // 满值时跳过逐秒循环，避免大额离线补算在已饱和资源上空转。
                    if (player.Resources.Manapoints >= player.Resources.MaxManapoints) break;
                    for (var i = 0; i < wholeSeconds && player.Resources.Manapoints < player.Resources.MaxManapoints; i++)
                        player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + rate);
                    combatTouched = true;
                    break;
                case ActionYieldKind.Hitpoints:
                    if (player.Resources.Hitpoints >= player.Resources.MaxHitpoints) break;
                    for (var i = 0; i < wholeSeconds && player.Resources.Hitpoints < player.Resources.MaxHitpoints; i++)
                        player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + rate);
                    combatTouched = true;
                    break;
            }
        }
        if (combatTouched) player.MarkDirty(PlayerSectionKind.Combat);
        if (progressionTouched) player.MarkDirty(PlayerSectionKind.Progression);
        if (experienceCredited != 0 || foundationDelta != 0 || combatExperienceGained != 0)
            player.RecordStats(actionInstanceId, atUnixSeconds,
                new PlayerStatsDelta
                {
                    Experience = experienceCredited,
                    Foundation = foundationDelta,
                    CombatExperience = combatExperienceGained
                });
        if (!double.IsFinite(player.Progression.Experience) || !double.IsFinite(player.Progression.Foundation)
            || !double.IsFinite(player.Progression.CombatExperience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "模拟后的玩家进度数值无效。");
    }

    /// <summary>
    /// 单轮行动耗时走满时的结算处理。
    /// 逐条结算配表声明的按轮收益（修为/灵力/气血/经验/物品/Buff）、扣除输入材料、扣减剩余次数，
    /// 若次数归零则移出队列；若为 -1（无限重复）则绝对保留队列项供下一轮循环。
    /// </summary>
    internal static void CompleteRepeat(
        IPlayerActionContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        double autoUseMultiplier,
        GameDataCatalog? gameData,
        DateTimeOffset target,
        ref Dictionary<ItemId, double>? deltas,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.yield");
        // 先汇总数值型收益并完成有限性校验，再统一落账，沿用“先校验后变更”语义。
        // experienceBase 记增幅前基础量（战斗经验记账源），experienceGain 记增幅后总量（底蕴结算输入）。
        double experienceGain = 0, manaGain = 0, hitpointGain = 0, experienceBase = 0;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerRepeat) continue;
            var amount = yield.Amount * autoUseMultiplier;
            switch (yield.Kind)
            {
                case ActionYieldKind.Experience: experienceBase += yield.Amount; experienceGain += amount; break;
                case ActionYieldKind.Mana: manaGain += amount; break;
                case ActionYieldKind.Hitpoints: hitpointGain += amount; break;
            }
        }
        // 本层修为上限：无境界进度配置时取 +∞（不封顶也不沉淀）；封顶 0 表示满级恒锁定。
        var expCap = gameData?.RealmProgression?.ExpCapFor(player.Progression.RealmLevel) ?? double.PositiveInfinity;
        if (!double.IsFinite(experienceGain) || !double.IsFinite(experienceBase) || !double.IsFinite(manaGain) || !double.IsFinite(hitpointGain)
            || !double.IsFinite(player.Progression.Experience + 3 * experienceGain)
            || !double.IsFinite(player.Progression.Foundation + experienceGain)
            || !double.IsFinite(player.Progression.CombatExperience + experienceBase))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动收益超出数值范围。");
        action.ProgressSeconds = 0;
        PlayerActionQueueRules.RecordCompletedRound(action);
        completedActions += 1;
        completedRepeats += 1;
        Dictionary<ItemId, double>? itemRewards = null;
        var buffTouched = false;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerRepeat) continue;
            switch (yield.Kind)
            {
                case ActionYieldKind.Item:
                    deltas ??= new Dictionary<ItemId, double>();
                    var scaled = ScaleOutput(yield.Amount, autoUseMultiplier);
                    AddDelta(deltas, yield.Item!.Value, scaled);
                    (itemRewards ??= new Dictionary<ItemId, double>())[yield.Item.Value] =
                        itemRewards.GetValueOrDefault(yield.Item.Value) + scaled;
                    break;
                case ActionYieldKind.Buff:
                    var grant = yield.Buff!;
                    player.Buffs.Active.RemoveAll(buff => buff.BuffId == grant.BuffId);
                    player.Buffs.Active.Add(new ActiveBuff(grant.BuffId,
                        target.ToUnixTimeMilliseconds() / 1000d + grant.DurationSeconds, yield.Amount,
                        grant.Name, null, grant.HpRegen, grant.MpRegen, grant.AttackSpeedPoints, grant.DamageAbsorptionPoints,
                        grant.StatRatios));
                    buffTouched = true;
                    break;
            }
        }
        foreach (var (item, quantity) in definition.Inputs)
        {
            deltas ??= new Dictionary<ItemId, double>();
            AddDelta(deltas, item, -quantity);
        }
        CompleteSustainedAutoUse(player, definition, gameData, target, ref events);
        // 修为类收益统一结算（总纲 §7.11.1）：瓶颈截断 + 底蕴 1:1 兑换放大（≤3×amp）+
        // 增幅溢量沉淀为底蕴 + 战斗经验按增幅前基础量入账；满级（cap≤0）全额沉淀。
        var experienceDelta = player.Progression.CreditExperience(experienceBase, experienceGain, expCap);
        // 账本按实际入账量记录：修为记瓶颈截断与底蕴兑换放大后的真实增量，底蕴记净变动，
        // 战斗经验按基础量记账；物品产出计获得、输入材料计失去。
        player.RecordStats(action.Id, target.ToUnixTimeMilliseconds() / 1000d, new PlayerStatsDelta
        {
            Experience = experienceDelta,
            Foundation = experienceGain - experienceDelta,
            CombatExperience = experienceBase,
            Cycles = 1,
            ItemGains = itemRewards,
            ItemLosses = definition.Inputs
        });
        if (manaGain != 0)
            player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + manaGain);
        if (hitpointGain != 0)
            player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + hitpointGain);
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (manaGain != 0 || hitpointGain != 0) player.MarkDirty(PlayerSectionKind.Combat);
        if (buffTouched) player.MarkDirty(PlayerSectionKind.Buffs);
        events ??= new SimulationEventTail();
        events.Add(new SimulationEvent(
            target,
            "action.completed",
            definition.Name,
            definition.Id,
            itemRewards));
        player.FinishActionRound(action.Id, definition.ExecutionKind != ActionExecutionKind.Travel,
            target.ToUnixTimeMilliseconds() / 1000d);
    }

    internal static double PrepareSustainedAutoUse(
        IPlayerActionContext player,
        ActionDefinition definition,
        bool isActionStart,
        GameDataCatalog? gameData,
        AssetWallet? assetBalances,
        DateTimeOffset at,
        Guid actionInstanceId,
        ref Dictionary<ItemId, double>? deltas,
        ref SimulationEventTail? events)
    {
        if (gameData is null || player.Settings.AutoUseSlots.Count == 0) return 1d;
        var multiplier = 1d;
        for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
        {
            var slot = player.Settings.AutoUseSlots[i];
            if (!TryGetSustainedConsumable(slot, definition.Kind, gameData, out var item, out var consumable)) continue;

            if (slot.RemainingDurationSeconds > consumable.DurationSeconds)
            {
                slot = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
                player.Settings.AutoUseSlots[i] = slot;
                player.MarkDirty(PlayerSectionKind.Settings);
            }
            if (isActionStart && slot.RemainingDurationSeconds <= 0)
            {
                var available = assetBalances?.Get(new AssetKey(item.Id)) ?? 0d;
                if (deltas is not null && deltas.TryGetValue(item.Id, out var pendingDelta)) available += pendingDelta;
                if (available + 1e-12 >= 1d)
                {
                    AddDelta(deltas ??= new Dictionary<ItemId, double>(), item.Id, -1d);
                    // 自动消耗品扣件计入「失去」物品。
                    player.RecordStats(actionInstanceId, at.ToUnixTimeMilliseconds() / 1000d,
                        new PlayerStatsDelta { LostItem = item.Id, LostQuantity = 1 });
                    slot = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
                    player.Settings.AutoUseSlots[i] = slot;
                    player.MarkDirty(PlayerSectionKind.Settings);
                    events ??= new SimulationEventTail();
                    events.Add(new SimulationEvent(
                        at,
                        "auto-use.activated",
                        $"自动使用 {item.Name}，可生效 {consumable.DurationSeconds:0.######} 秒。",
                        definition.Id,
                        new Dictionary<ItemId, double> { [item.Id] = -1d }));
                }
            }
            if (slot.RemainingDurationSeconds <= 0) continue;
            var itemMultiplier = consumable.GetMultiplier(definition.Kind);
            multiplier *= itemMultiplier;
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                throw new DomainException(DomainErrorCode.InvariantViolation, "持续消耗品倍率无效。");
        }
        return multiplier;
    }

    internal static void CompleteSustainedAutoUse(
        IPlayerActionContext player,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        DateTimeOffset at,
        ref SimulationEventTail? events)
    {
        if (gameData is null || player.Settings.AutoUseSlots.Count == 0) return;
        for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
        {
            var slot = player.Settings.AutoUseSlots[i];
            if (!TryGetSustainedConsumable(slot, definition.Kind, gameData, out var item, out _)
                || slot.RemainingDurationSeconds <= 0)
                continue;
            var remaining = Math.Max(0d, slot.RemainingDurationSeconds - definition.DurationSeconds);
            player.Settings.AutoUseSlots[i] = slot with { RemainingDurationSeconds = remaining };
            player.MarkDirty(PlayerSectionKind.Settings);
            if (remaining > 0) continue;
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(
                at,
                "auto-use.depleted",
                $"{item.Name} 的持续时间已耗尽。",
                definition.Id));
        }
    }

    internal static bool TryGetSustainedConsumable(
        AutoUseSlotState slot,
        ActionKind actionKind,
        GameDataCatalog gameData,
        out ItemDefinition item,
        out ConsumableDefinition consumable)
    {
        item = null!;
        consumable = null!;
        if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.ItemId) || !MatchesActionType(slot.ActionType, actionKind)) return false;
        var itemId = new ItemId(slot.ItemId);
        if (!itemId.IsValid || !gameData.Items.TryGetValue(itemId, out item!)) return false;
        consumable = item.Consumable!;
        return consumable is { IsContinuous: true, DurationSeconds: > 0 }
            && consumable.UsableInActionKinds.Contains(actionKind);
    }

    internal static bool MatchesActionType(string actionType, ActionKind kind) => kind switch
    {
        ActionKind.Cultivation => string.Equals(actionType, "cultivation", StringComparison.OrdinalIgnoreCase),
        ActionKind.Exploration => string.Equals(actionType, "exploration", StringComparison.OrdinalIgnoreCase),
        ActionKind.Alchemy => string.Equals(actionType, "alchemy", StringComparison.OrdinalIgnoreCase),
        ActionKind.Combat => string.Equals(actionType, "combat", StringComparison.OrdinalIgnoreCase),
        ActionKind.Travel => string.Equals(actionType, "travel", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static double CultivationMultiplier(GameDataCatalog catalog, string nodeId)
    {
        foreach (var map in catalog.Maps)
            foreach (var node in map.GetProperty("nodes").EnumerateArray())
                if (node.GetProperty("id").GetString() == nodeId)
                    return node.TryGetProperty("cultivationMultiplier", out var multiplier) ? multiplier.GetDouble() : 1;
        return 1;
    }

    internal static double ScaleOutput(double quantity, double multiplier)
    {
        if (multiplier == 1d) return quantity;
        var scaled = Math.Ceiling(quantity * multiplier);
        if (!double.IsFinite(scaled))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动产出倍率结算超出数值范围。");
        return Math.Max(1d, scaled);
    }

    /// <summary>
    /// 累计资产变动差额并执行有限性校验。
    /// </summary>
    internal static void AddDelta(Dictionary<ItemId, double> deltas, ItemId item, double amount)
    {
        if (!item.IsValid || !double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源增量无效。");
        var next = deltas.TryGetValue(item, out var current) ? current + amount : amount;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源累计增量超出数值范围。");
        deltas[item] = next;
    }
}
