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

    /// <summary>受阻队首先复核准入（含濒死复苏归一气血）；仍受阻才执行一次出队或轮转。
    /// true 表示本轮已处理（恢复/移除/轮转），false 表示条件未满足保持停泊。</summary>
    public static bool AdvanceBlockedAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => AdvanceBlockedAction(player, action, definition, gameData, assets, at,
            mutations.Deltas, ref mutations.EventTail);

    internal static bool AdvanceBlockedAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        // 濒死复苏：阵亡不回血口径下 hp=0 的受阻战斗行动永远等不到回血，按脱战复苏 1 HP
        // 归一后复核准入；复苏或条件补足即恢复 Running，由正常分派接管，不再永久停泊。
        if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter && player.Resources.Hitpoints <= 0)
        {
            player.Resources.Hitpoints = 1;
            player.MarkDirty(PlayerSectionKind.Combat);
        }
        if (TryStartActionCore(player, action, definition, gameData, assets, at, deltas, ref events))
            return true;
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
    public static bool TryStartAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => TryStartActionCore(player, action, definition, gameData, assets, at,
            mutations.Deltas, ref mutations.EventTail);

    internal static bool TryStartActionCore(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.start");
        var availableHere = (definition.AllowedNodeIds is null || definition.AllowedNodeIds.Count == 0
            || (player.World.EdgeId is null && definition.AllowedNodeIds.Contains(player.World.NodeId)))
            && (definition.ExcludedNodeIds is not { Count: > 0 } excludedNodes
                || player.World.EdgeId is not null
                || !excludedNodes.Contains(player.World.NodeId));
        var hasInputs = true;
        foreach (var input in definition.Inputs)
            if ((assets?.Get(new AssetKey(input.Key)) ?? (gameData is null ? double.PositiveInfinity : 0))
                + (deltas?.GetValueOrDefault(new AssetKey(input.Key)) ?? 0) < input.Value)
            {
                hasInputs = false;
                break;
            }
        // 讨伐行动不走「地点不符」口径：地点校验失败按条件不足阻断；其余行动维持原文案。
        var blockedReason = availableHere
            ? "材料不足，行动受阻。"
            : definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                ? "条件不足，行动受阻。"
                : "当前位置不允许执行该行动。";
        if (availableHere && hasInputs && definition.Kind == ActionKind.Enhancement)
        {
            // 强化准入：目标装备/等级上限/意图目标/材料/护符一次结算；已达标则整行动提前完成出队。
            var holdings = EnhancementRulesEffectiveBalances(assets, deltas);
            var techniqueLevel = TechniqueCatalog.LevelFor(
                player.Loadout.TechniqueExperiences.TryGetValue(TechniqueCatalog.Enhancement, out var storedExp) ? storedExp : 0,
                gameData?.RealmProgression);
            if (EnhancementRules.Resolve(player.Loadout, action.Intent, holdings, gameData, techniqueLevel,
                    out var check, out var reason) is null)
            {
                if (check == EnhancementRules.Check.TargetReached)
                {
                    player.EnsureActionReport(action.Id, definition.Id, definition.Name, at);
                    events ??= new();
                    events.Add(new SimulationEvent(at, "action.completed", definition.Name, definition.Id));
                    player.RemoveAction(action.Id, incrementVersion: false,
                        endedAtUnixSeconds: at.ToUnixTimeMilliseconds() / 1000d);
                    return false;
                }
                availableHere = true;
                hasInputs = false;
                blockedReason = reason;
            }
        }
        if (!availableHere || !hasInputs)
        {
            // 地点不符且自动移动开启：插队赶路至入队锚点替代 Blocked；任一前置不满足回落原语义。
            // 讨伐行动除外——讨伐跟随玩家移动，当前地点不可执行时直接条件不足阻断，不赶回锚点。
            if (!availableHere && definition.ExecutionKind != ActionExecutionKind.CombatEncounter
                && TryEnqueueAutoTravel(player, action, definition, gameData, at, ref events))
                return false;
            // 已是停泊状态的行动每轮复核准入：仍不满足时静默维持，不重复刷阻塞事件。
            if (action.Status != ActionQueueStatus.Blocked)
            {
                action.Status = ActionQueueStatus.Blocked;
                events ??= new();
                events.Add(new SimulationEvent(at, "action.blocked", blockedReason, definition.Id));
            }
            return false;
        }
        // 讨伐跟随驻留地点：准入通过但远征遭遇不属于此地时，就地按新地点重锚续跑。
        if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter && gameData is not null)
            CombatActionRules.ReanchorExpeditionIfMoved(player, action, definition, gameData);
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
        // 锚点准入口径与开轮门禁同构：白名单须在列；黑名单须不含锚点（未配约束时全图可执行）。
        var anchorUnavailable = definition.AllowedNodeIds is { Count: > 0 } allowedNodes
            ? anchor is null || !allowedNodes.Contains(anchor)
            : definition.ExcludedNodeIds is { } excludedNodes && anchor is not null && excludedNodes.Contains(anchor);
        if (!player.Settings.AutoTravelEnabled
            || gameData?.WorldGraph is not { } worldGraph
            || string.IsNullOrWhiteSpace(anchor)
            || anchorUnavailable
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

    /// <summary>
    /// 计时行动的时长结算：强化按当次目标等级取 5+t 动态时长，其余取配表静态时长；
    /// 节点建筑速度乘区独立除法，技艺等级速度乘区（每级 +5%）与已装备技艺工具
    /// techniqueBonus.speedRatio 并入同一除数（duration /= 1 + 0.05×L + Σ工具加成），
    /// 保持同点加算语义与确定性。
    /// </summary>
    public static double TimedActionDuration(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData,
        AssetWallet? assets, IReadOnlyDictionary<AssetKey, double>? deltas)
    {
        var duration = definition.DurationSeconds;
        if (definition.Kind == ActionKind.Enhancement)
        {
            var current = action.Intent.TargetItemId is { } targetText
                ? EnhancementRules.CurrentLevel(player.Loadout, new Domain.Common.ItemId(targetText),
                    EnhancementRulesEffectiveBalances(assets, deltas))
                : -1;
            duration = current >= 0 && current < EnhancementRules.MaxLevel
                ? EnhancementRules.DurationSeconds(current + 1)
                : 0;
        }
        var buildingSpeed = player.World.EffectiveNodeBuildingEffects.SpeedRatioFor(definition.Kind, definition.Technique);
        if (buildingSpeed > 0) duration /= 1 + buildingSpeed;
        if (definition.Technique is { } technique && TechniqueCatalog.IsKnown(technique))
        {
            var level = TechniqueCatalog.LevelFor(
                player.Loadout.TechniqueExperiences.TryGetValue(technique, out var exp) ? exp : 0,
                gameData?.RealmProgression);
            // 技艺等级加成与已装备工具加成并入同一除数；level≤0 时 SpeedBonusRatio 为 0，
            // 无加成时除数为 1 语义不变。
            duration /= 1 + TechniqueCatalog.SpeedBonusRatio(level)
                + TechniqueCatalog.EquippedToolSpeedRatio(player.Loadout, technique, gameData);
        }
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
        DateTimeOffset at, ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
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
    public static void AdvanceTimedAction(IPlayerResourceContext player, IPlayerActionState action,
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
    public static void CompleteTimedAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, double multiplier, GameDataCatalog? gameData, AssetWallet? assets,
        DateTimeOffset at, ActionMutationBatch mutations)
        => CompleteRepeat(player, action, definition, multiplier, gameData, assets, at,
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
    internal static void ApplyContinuousReward(IPlayerResourceContext player, ActionDefinition definition, long wholeSeconds,
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
        // 底蕴记净变动（满层沉淀为正、兑换消耗为负）。
        var experienceCredited = 0d;
        var foundationDelta = 0d;
        var rootExpGains = new Dictionary<CoreAspectKind, double>();
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
                    // 未入账增幅沉淀为底蕴；满级（cap≤0）全额沉淀。打坐修行等非战斗行动不增加战斗经验。
                    if (!double.IsFinite(player.Progression.Experience + 3 * total)
                        || !double.IsFinite(player.Progression.Foundation + total))
                        throw new DomainException(DomainErrorCode.InvariantViolation, "经验收益超出数值范围。");
                    for (var i = 0; i < wholeSeconds; i++)
                        experienceCredited += player.Progression.CreditExperience(rate, expCap);
                    foundationDelta += total - experienceCredited;
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
                case ActionYieldKind.RootExp:
                    // 六维经验为定额口径：不吃持续消耗品/灵脉/建筑产出倍率（总纲 §20.1.4），
                    // 逐整秒聚合保证大跨度离线快进与逐秒在线推进的浮点累加序列一致。
                    if (yield.Aspect is { } aspect)
                        for (var i = 0; i < wholeSeconds; i++)
                            rootExpGains[aspect] = rootExpGains.GetValueOrDefault(aspect) + yield.Amount;
                    break;
            }
        }
        foreach (var (aspect, rootExp) in rootExpGains)
            GrantRootExperience(player, aspect, rootExp, gameData, actionInstanceId, atUnixSeconds);
        if (combatTouched) player.MarkDirty(PlayerSectionKind.Combat);
        if (progressionTouched) player.MarkDirty(PlayerSectionKind.Progression);
        if (experienceCredited != 0 || foundationDelta != 0)
            player.RecordStats(actionInstanceId, atUnixSeconds,
                new PlayerStatsDelta
                {
                    Experience = experienceCredited,
                    Foundation = foundationDelta
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
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        double autoUseMultiplier,
        GameDataCatalog? gameData,
        AssetWallet? assets,
        DateTimeOffset target,
        ref Dictionary<AssetKey, double>? deltas,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.action.yield");
        if (definition.Kind == ActionKind.Enhancement)
        {
            CompleteEnhancementRepeat(player, action, definition, gameData, assets, target,
                ref deltas, ref events, ref completedActions, ref completedRepeats);
            return;
        }
        // 先汇总数值型收益并完成有限性校验，再统一落账，沿用“先校验后变更”语义。
        // experienceGain 记增幅后总量（底蕴结算输入）。
        double experienceGain = 0, manaGain = 0, hitpointGain = 0;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerRepeat) continue;
            var amount = yield.Amount * autoUseMultiplier;
            switch (yield.Kind)
            {
                case ActionYieldKind.Experience: experienceGain += amount; break;
                case ActionYieldKind.Mana: manaGain += amount; break;
                case ActionYieldKind.Hitpoints: hitpointGain += amount; break;
            }
        }
        // 本层修为上限：无境界进度配置时取 +∞（不封顶也不沉淀）；封顶 0 表示满级恒锁定。
        var expCap = gameData?.RealmProgression?.ExpCapFor(player.Progression.RealmLevel) ?? double.PositiveInfinity;
        if (!double.IsFinite(experienceGain) || !double.IsFinite(manaGain) || !double.IsFinite(hitpointGain)
            || !double.IsFinite(player.Progression.Experience + 3 * experienceGain)
            || !double.IsFinite(player.Progression.Foundation + experienceGain))
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
                    deltas ??= new Dictionary<AssetKey, double>();
                    var scaled = ScaleOutput(yield.Amount, autoUseMultiplier);
                    AddDelta(deltas, new AssetKey(yield.Item!.Value), scaled);
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
            deltas ??= new Dictionary<AssetKey, double>();
            AddDelta(deltas, new AssetKey(item), -quantity);
        }
        CompleteSustainedAutoUse(player, definition, gameData, target, ref events);
        // 修为类收益统一结算（总纲 §7.11.1）：瓶颈截断 + 底蕴 1:1 兑换放大（≤3×amp）+
        // 增幅溢量沉淀为底蕴；满级（cap≤0）全额沉淀。非战斗行动不增加战斗经验。
        var experienceDelta = player.Progression.CreditExperience(experienceGain, expCap);
        // 账本按实际入账量记录：修为记瓶颈截断与底蕴兑换放大后的真实增量，底蕴记净变动；物品产出计获得、输入材料计失去。
        player.RecordStats(action.Id, target.ToUnixTimeMilliseconds() / 1000d, new PlayerStatsDelta
        {
            Experience = experienceDelta,
            Foundation = experienceGain - experienceDelta,
            Cycles = 1,
            ItemGains = itemRewards,
            ItemLosses = definition.Inputs
        });
        // 技艺经验按当轮基准经验入账（总纲 §20.1.2 / §20.1.4）：等级跃迁写入同一份 LevelUp 账本。
        if (definition.Technique is { } technique && TechniqueCatalog.IsKnown(technique))
        {
            var actionLevel = definition.Level ?? 1;
            var baseExp = actionLevel <= 1 ? 10.0 : (gameData?.RealmProgression?.BaseExperienceForLevel(actionLevel) ?? 10.0);
            var techExp = Math.Max(1.0, Math.Round(baseExp * (definition.DurationSeconds / 10.0)));
            GrantTechniqueExperience(player, action.Id, technique, techExp, gameData,
                target.ToUnixTimeMilliseconds() / 1000d);
        }
        // 六维经验按配表定向定额入账（总纲 §20.1.4）：不吃 autoUseMultiplier 收益倍率。
        foreach (var yield in definition.Yields)
            if (yield is { Kind: ActionYieldKind.RootExp, Period: ActionYieldPeriod.PerRepeat, Aspect: { } aspect })
                GrantRootExperience(player, aspect, yield.Amount, gameData, action.Id,
                    target.ToUnixTimeMilliseconds() / 1000d);
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
        // 引导里程碑推进的行动完成当轮即离队：∞ 循环与 LoopEnabled 轮转都会驻留队首，
        // 不截断则阻塞后续引导行动（采集 ∞ 占位导致炼丹永远排不上）。
        var tutorialAdvanced = TutorialRules.OnActionRoundCompleted(player, definition.Id, target, ref deltas, ref events);
        if (tutorialAdvanced) action.RemainingRepeats = 0;
        player.FinishActionRound(action.Id,
            !tutorialAdvanced && definition.ExecutionKind != ActionExecutionKind.Travel,
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
        ref Dictionary<AssetKey, double>? deltas,
        ref SimulationEventTail? events)
    {
        if (gameData is null || player.Settings.AutoUseSlots.Count == 0) return 1d;
        var multiplier = 1d;
        for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
        {
            var slot = player.Settings.AutoUseSlots[i];
            if (!TryGetSustainedConsumable(slot, Domain.GameData.AutoUseBar.For(definition), definition.Kind,
                gameData, out var item, out var consumable)) continue;

            if (slot.RemainingDurationSeconds > consumable.DurationSeconds)
            {
                slot = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
                player.Settings.AutoUseSlots[i] = slot;
                player.MarkDirty(PlayerSectionKind.Settings);
            }
            // 持续消耗品冷却自耗竭时刻起算（CompleteSustainedAutoUse 写入）：冷却未走完不在行动起点补能。
            if (slot.CooldownUntilUnixSeconds > at.ToUnixTimeMilliseconds() / 1000d) continue;
            if (isActionStart && slot.RemainingDurationSeconds <= 0)
            {
                var available = assetBalances?.Get(new AssetKey(item.Id)) ?? 0d;
                if (deltas is not null && deltas.TryGetValue(new AssetKey(item.Id), out var pendingDelta)) available += pendingDelta;
                if (available + 1e-12 >= 1d)
                {
                    AddDelta(deltas ??= new Dictionary<AssetKey, double>(), new AssetKey(item.Id), -1d);
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
            if (!TryGetSustainedConsumable(slot, Domain.GameData.AutoUseBar.For(definition), definition.Kind,
                gameData, out var item, out var consumable)
                || slot.RemainingDurationSeconds <= 0)
                continue;
            var remaining = Math.Max(0d, slot.RemainingDurationSeconds - definition.DurationSeconds);
            var updated = slot with { RemainingDurationSeconds = remaining };
            if (remaining <= 0 && consumable.CooldownSeconds > 0)
            {
                // 持续消耗品冷却自耗竭时刻起算：耗竭点在本回合内旧剩余秒数处。
                var atUnix = at.ToUnixTimeMilliseconds() / 1000d;
                updated = updated with
                {
                    CooldownUntilUnixSeconds = atUnix - (definition.DurationSeconds - slot.RemainingDurationSeconds)
                        + consumable.CooldownSeconds
                };
            }
            player.Settings.AutoUseSlots[i] = updated;
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

    /// <summary>槽位是否对运行中行动产出持续消耗品：
    /// 同栏域 + 物品声明该栏域 + 对该行动大类确有加成（倍率 &gt; 1，无效场景不充能不扣时长）。</summary>
    internal static bool TryGetSustainedConsumable(
        AutoUseSlotState slot,
        string? actionBarId,
        ActionKind actionKind,
        GameDataCatalog gameData,
        out ItemDefinition item,
        out ConsumableDefinition consumable)
    {
        item = null!;
        consumable = null!;
        if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.ItemId) || actionBarId is null
            || !string.Equals(slot.ActionType, actionBarId, StringComparison.OrdinalIgnoreCase)) return false;
        var itemId = new ItemId(slot.ItemId);
        if (!itemId.IsValid || !gameData.Items.TryGetValue(itemId, out item!)) return false;
        consumable = item.Consumable!;
        // durable 持续品（阵盘）不走时长扣件路径：由资源边界的 ActiveBuff 注入/清除承载。
        return consumable is { IsContinuous: true, DurationSeconds: > 0, Durable: false }
            && consumable.UsableInBars.Contains(actionBarId)
            && consumable.GetMultiplier(actionKind) > 1d;
    }

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
    internal static void AddDelta(Dictionary<AssetKey, double> deltas, AssetKey asset, double amount)
    {
        if (!asset.ItemId.IsValid || asset.Variant is null || asset.Variant.Length > 128 || !double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源增量无效。");
        var next = deltas.TryGetValue(asset, out var current) ? current + amount : amount;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源累计增量超出数值范围。");
        deltas[asset] = next;
    }

    /// <summary>ItemId 便捷重载：基础键变体为空串。</summary>
    internal static void AddDelta(Dictionary<AssetKey, double> deltas, ItemId item, double amount)
        => AddDelta(deltas, new AssetKey(item), amount);

    /// <summary>合并钱包余额与窗口暂存增量的有效余额视图：强化等级/材料判定共用同一口径。</summary>
    internal static IReadOnlyDictionary<AssetKey, double> EnhancementRulesEffectiveBalances(
        AssetWallet? assets, IReadOnlyDictionary<AssetKey, double>? deltas)
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

    /// <summary>
    /// 六维经验单维定额入账（总纲 §20.1.4）：累加经验、经点成本表判定等级跃迁（kind "root"）、
    /// 记 RootExp 账本并标脏 Loadout。击杀全归属入账与行动 rootexp 收益共用本口径；
    /// amount 为最终定额，调用方负责折算，本处不再乘任何收益倍率。
    /// </summary>
    internal static void GrantRootExperience(IPlayerResourceContext player, CoreAspectKind aspect,
        double amount, GameDataCatalog? gameData, Guid actionInstanceId, double atUnixSeconds)
    {
        if (amount <= 0) return;
        if (!double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "六维经验收益超出数值范围。");
        var core = player.Loadout.CoreAttributes;
        var expBefore = core.ExpFor(aspect);
        var levelBefore = RootAttributeCurve.LevelFor(expBefore, gameData?.RealmProgression);
        var levelAfter = RootAttributeCurve.LevelFor(expBefore + amount, gameData?.RealmProgression);
        player.Loadout.CoreAttributes = core.WithAddedExp(aspect, amount);
        player.RecordStats(actionInstanceId, atUnixSeconds, new PlayerStatsDelta
        {
            RootAspect = aspect,
            RootExperience = amount,
            LevelUp = levelAfter > levelBefore
                ? new PlayerReportLevelUp("root", aspect.ToString().ToLowerInvariant(), levelBefore, levelAfter) : null
        });
        player.MarkDirty(PlayerSectionKind.Loadout);
    }

    /// <summary>
    /// 授予技艺累计经验并按境界修为曲线折算等级跃迁（kind "technique"）；
    /// 与 <see cref="PlayerAggregate.GrantTechniqueExperience"/> 同一入账口径，面向行动逐轮结算。
    /// </summary>
    internal static void GrantTechniqueExperience(IPlayerResourceContext player, Guid actionId,
        string techniqueId, double experience, GameDataCatalog? gameData, double atUnixSeconds)
    {
        var previousExp = player.Loadout.TechniqueExperiences.TryGetValue(techniqueId, out var stored) ? stored : 0;
        var nextExp = previousExp + experience;
        player.Loadout.TechniqueExperiences[techniqueId] = nextExp;
        var levelBefore = TechniqueCatalog.LevelFor(previousExp, gameData?.RealmProgression);
        var levelAfter = TechniqueCatalog.LevelFor(nextExp, gameData?.RealmProgression);
        player.RecordStats(actionId, atUnixSeconds, new PlayerStatsDelta
        {
            TechniqueId = techniqueId,
            TechniqueExperience = experience,
            LevelUp = levelAfter > levelBefore
                ? new PlayerReportLevelUp("technique", techniqueId, levelBefore, levelAfter) : null
        });
        player.MarkDirty(PlayerSectionKind.Loadout);
    }

    /// <summary>
    /// 强化行动单轮结算（总纲 §20.4.2）：开轮已冻结时长，本轮重新解析当前等级与成本，
    /// 判定后完成材料/护符扣减与 +N 资产变体迁移，已装备槽位同步等级；
    /// 不走静态 inputs/yields——收益/消耗全部由强化规则动态结算。
    /// </summary>
    private static void CompleteEnhancementRepeat(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        AssetWallet? assets,
        DateTimeOffset target,
        ref Dictionary<AssetKey, double>? deltas,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        var holdings = EnhancementRulesEffectiveBalances(assets, deltas);
        var techniqueLevel = TechniqueCatalog.LevelFor(
            player.Loadout.TechniqueExperiences.TryGetValue(TechniqueCatalog.Enhancement, out var storedExp) ? storedExp : 0,
            gameData?.RealmProgression);
        var atUnix = target.ToUnixTimeMilliseconds() / 1000d;
        var prep = EnhancementRules.Resolve(player.Loadout, action.Intent, holdings, gameData, techniqueLevel,
            out var check, out var reason);
        if (prep is null)
        {
            // 轮间状态漂移（材料被消耗/装备被移除/目标已达成）：按开轮准入同语义回退为阻塞或提前完成，
            // 本轮耗时已消耗但不产生强化结算，与材料不足阻塞口径一致。
            if (check == EnhancementRules.Check.TargetReached)
            {
                action.ProgressSeconds = 0;
                events ??= new SimulationEventTail();
                events.Add(new SimulationEvent(target, "action.completed", definition.Name, definition.Id));
                player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: atUnix);
                return;
            }
            action.Status = ActionQueueStatus.Blocked;
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(target, "action.blocked", reason, definition.Id));
            return;
        }
        var outcome = EnhancementRules.Roll(action.Id, action.CompletedRepeats, prep);
        var resultLevel = EnhancementRules.ResultLevel(prep, outcome);
        var fromKey = EnhancementRules.KeyFor(prep.ItemId, prep.CurrentLevel);
        var toKey = EnhancementRules.KeyFor(prep.ItemId, resultLevel);
        action.ProgressSeconds = 0;
        PlayerActionQueueRules.RecordCompletedRound(action);
        completedActions += 1;
        completedRepeats += 1;
        deltas ??= new Dictionary<AssetKey, double>();
        // 材料与保护材料扣减先行，再做装备变体迁移：账本顺序与静态 inputs 扣减一致。
        AddDelta(deltas, new AssetKey(new ItemId(EnhancementRules.SpiritStoneItemId)), -prep.SpiritStoneCost);
        var itemLosses = new Dictionary<ItemId, double>
        {
            [new ItemId(EnhancementRules.SpiritStoneItemId)] = prep.SpiritStoneCost
        };
        // 保护材料恒按无强化基础键扣 1：配表列表内材料或同装备裸件均走 AssetKey(裸变体)。
        if (prep.ProtectionItemId is { } protectionId)
        {
            AddDelta(deltas, new AssetKey(protectionId), -1d);
            itemLosses[protectionId] = 1;
        }
        AddDelta(deltas, fromKey, -1d);
        AddDelta(deltas, toKey, 1d);
        // 强化技艺经验按目标装备等级基准与当次实际基础耗时（5+t）入账（总纲 §20.1.2 / §20.1.4）。
        var itemLevel = gameData is null ? 1 : CraftingRecipeRules.ResolveItemLevel(prep.ItemId.Value, gameData);
        var duration = EnhancementRules.DurationSeconds(prep.TargetLevel);
        var enhancementBaseExp = itemLevel <= 1 ? 10.0 : (gameData?.RealmProgression?.BaseExperienceForLevel(itemLevel) ?? 10.0);
        var enhancementTechExp = Math.Max(1.0, Math.Round(enhancementBaseExp * (duration / 10.0)));
        GrantTechniqueExperience(player, action.Id, TechniqueCatalog.Enhancement,
            enhancementTechExp, gameData, atUnix);
        // 强化六维·神识按目标装备等阶换算等级与当次基础耗时动态折算（总纲 §20.1.4）：定额不吃倍率。
        if (gameData?.RealmProgression is { } progression)
            GrantRootExperience(player, CoreAspectKind.Consciousness,
                Math.Round(progression.BaseExperienceForLevel(
                    CraftingRecipeRules.ResolveItemLevel(prep.ItemId.Value, gameData))
                    * EnhancementRules.DurationSeconds(prep.TargetLevel) / 10d),
                gameData, action.Id, atUnix);
        player.RecordStats(action.Id, atUnix, new PlayerStatsDelta
        {
            Cycles = 1,
            ItemLosses = itemLosses
        });
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        events ??= new SimulationEventTail();
        events.Add(new SimulationEvent(target,
            outcome == EnhancementRules.Outcome.Success ? "enhance.succeeded" : "enhance.failed",
            outcome == EnhancementRules.Outcome.Success
                ? $"强化成功：{prep.ItemId} 提升至 +{resultLevel}。"
                : $"强化失败：{prep.ItemId} 回落至 +{resultLevel}。",
            definition.Id));
        player.FinishActionRound(action.Id, definition.ExecutionKind != ActionExecutionKind.Travel, atUnix);
    }
}
