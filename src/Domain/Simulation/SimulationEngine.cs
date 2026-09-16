using Domain.World;
using System.Text.Json;

namespace Domain.Simulation;

/// <summary>
/// 快进模拟推进过程中触发的关键业务事件（如单轮行动完成、掉落产生、失效行动自动移除等）。
/// </summary>
/// <param name="At">事件发生时间（UTC）</param>
/// <param name="Code">事件分类代码</param>
/// <param name="Message">事件日志文本</param>
/// <param name="ActionId">触发事件的行动定义 ID</param>
/// <param name="ItemDeltas">产出或消耗的物品变动明细</param>
public sealed record SimulationEvent(
    DateTimeOffset At,
    string Code,
    string Message,
    ActionId? ActionId = null,
    IReadOnlyDictionary<ItemId, double>? ItemDeltas = null);

/// <summary>
/// 模拟引擎单次推进的完整结算结果。
/// 包含时间跨度、推进轮次、物品资产流水变动以及是否引起聚合状态演进。
/// </summary>
public sealed record SimulationResult(
    PlayerId PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    double ElapsedSeconds,
    double CompletedActions,
    double CompletedRepeats,
    IReadOnlyList<AssetDelta> AssetDeltas,
    IReadOnlyList<SimulationEvent> Events,
    long StateVersion,
    bool Changed);

/// <summary>
/// 角色行为与挂机离线收益的唯一确定性快进入口引擎。
/// 核心设计原则：
/// 1. 离线挂机和在线逐秒推进的计算结果必须位级一致（Bitwise Deterministic）；
/// 2. 算法按“行动完成边界”跳步推进，杜绝按离线时间无脑逐秒生成历史事件；
/// 3. 行动队列中 RemainingRepeats == -1 为无限循环，每轮结算后绝不出队；
/// 4. 持续灵力奖励通过整秒量化余数（SimulationCarrySeconds）精确进位，经验仅在单轮完成时结算。
/// </summary>
public static class SimulationEngine
{
    /// <summary>当前模拟模式版本</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>单次快进计算的最大状态转移步数上限，防止异常输入或配置死循环卡死主服务</summary>
    public const int MaxTransitionsPerAdvance = 100_000;

    /// <summary>整秒量化的贴整容差：吸收 0.1 这类二进制无法精确表示的秒数在累加中的尾差。</summary>
    private const double SecondQuantizationEpsilon = 1e-9;

    /// <summary>单次连续奖励可施加的整秒上限：正常推进远低于该值，越界说明配表或时钟已破坏，快速失败避免巨量循环。</summary>
    private const double MaxWholeSecondsPerApply = 1_000_000_000;

    /// <summary>
    /// 将玩家聚合状态从 LastSimulatedAt 确定性推进至目标时间 target。
    /// </summary>
    /// <param name="player">角色聚合实例（在此方法中直接就地更新）</param>
    /// <param name="target">推进截止目标时刻（必须不早于 player.LastSimulatedAt）</param>
    /// <param name="catalog">生效的动作配表目录</param>
    /// <returns>包含资产变动与事件明细的模拟结算结果</returns>
    public static SimulationResult AdvanceTo(
        PlayerAggregate player,
        DateTimeOffset target,
        ActionCatalog? catalog = null)
        => AdvanceToCore(player, target, catalog ?? ActionCatalog.Default, null, null);

    /// <summary>使用完整游戏配表推进行动，包含战斗遭遇、持续消耗品与怪物属性。</summary>
    public static SimulationResult AdvanceTo(
        PlayerAggregate player,
        DateTimeOffset target,
        GameDataCatalog gameData,
        AssetWallet? assetBalances = null,
        DuelAdvanceOptions? duel = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (assetBalances is not null && assetBalances.PlayerId != player.PlayerId)
            throw new DomainException(DomainErrorCode.InvalidArgument, "资产余额与模拟玩家不一致。");
        return AdvanceWithResources(player, target, gameData, assetBalances, duel);
    }

    private static SimulationResult AdvanceWithResources(PlayerAggregate player, DateTimeOffset target,
        GameDataCatalog gameData, AssetWallet? assets, DuelAdvanceOptions? duel)
    {
        var from = player.LastSimulatedAt;
        if (target < from) throw new DomainException(DomainErrorCode.InvalidArgument, "目标时间不能倒退。");
        player.DuelSettingsTuning = gameData.Duel;
        var wallet = new AssetWallet(player.PlayerId);
        if (assets is not null) wallet.Restore(assets.Balances);
        var deltas = new Dictionary<AssetKey, double>();
        var events = new List<SimulationEvent>();
        double actions = 0, repeats = 0;
        var changed = false;
        // 离线/挂起补算收益上限 48 小时：超出部分只推进模拟时钟与过期清理，不再产出行动收益。
        var computeTarget = target - from > TimeSpan.FromHours(48) ? from.AddHours(48) : target;
        while (player.LastSimulatedAt < computeTarget)
        {
            var now = player.LastSimulatedAt;
            var expired = player.Buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= now.ToUnixTimeMilliseconds() / 1000d);
            if (expired > 0) { player.MarkDirty(PlayerSectionKind.Buffs); changed = true; }
            var attributes = PlayerAttributeResolver.Apply(player, gameData);
            var instantDeltas = ActivateInstantConsumables(player, gameData, wallet, now, events);
            foreach (var delta in instantDeltas)
            {
                wallet.Apply(delta);
                deltas[delta.Asset] = deltas.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
            }
            var untilRegen = 5 - player.Combat.RegenCarrySeconds;
            var step = Math.Min((computeTarget - now).TotalSeconds, untilRegen);
            if (player.Buffs.Active.Count > 0)
                step = Math.Min(step, player.Buffs.Active.Min(buff => buff.ExpiresAtUnixSeconds) - now.ToUnixTimeMilliseconds() / 1000d);
            foreach (var slot in player.Settings.AutoUseSlots)
            {
                var untilCooldown = slot.CooldownUntilUnixSeconds - now.ToUnixTimeMilliseconds() / 1000d;
                if (slot.Enabled && untilCooldown > 1e-9) step = Math.Min(step, untilCooldown);
            }
            var result = AdvanceToCore(player, now.AddSeconds(step), gameData.Actions, gameData, wallet, duel);
            var stagedSettled = player.SettleStagedItems(now.AddSeconds(step).ToUnixTimeMilliseconds() / 1000d,
                gameData.Duel.StagedSettleSeconds);
            foreach (var settledItem in stagedSettled)
            {
                var settledDelta = new AssetDelta(player.PlayerId, new AssetKey(new ItemId(settledItem.ItemId)), settledItem.Quantity, "simulation");
                wallet.Apply(settledDelta);
                deltas[settledDelta.Asset] = deltas.GetValueOrDefault(settledDelta.Asset) + settledDelta.QuantityDelta;
                var settledItemId = new ItemId(settledItem.ItemId);
                events.Add(new SimulationEvent(now.AddSeconds(step), "duel.staged-settled",
                    $"暂存物品入账：{gameData.Items.GetValueOrDefault(settledItemId)?.Name ?? settledItem.ItemId} ×{settledItem.Quantity:0.##}。", null,
                    new Dictionary<ItemId, double> { [settledItemId] = settledItem.Quantity }));
            }
            if (player.Actions.Count == 0 || player.Actions[0].Expedition?.Phase != CombatExpeditionPhase.Combat)
            {
                var skills = player.Combat.Skills;
                // 空闲技能推进只在确有冷却/吟唱待结算时改写状态，避免空推进把聚合标脏。
                if (skills.Cooldowns.Count > 0 || skills.CastingAbilityId is not null)
                {
                    player.Combat.Skills = CombatSkillEngine.AdvanceIdle(skills, step);
                    player.MarkDirty(PlayerSectionKind.Combat);
                    changed = true;
                }
            }
            foreach (var delta in result.AssetDeltas)
            {
                wallet.Apply(delta);
                deltas[delta.Asset] = deltas.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
            }
            events.AddRange(result.Events);
            actions += result.CompletedActions;
            repeats += result.CompletedRepeats;
            changed |= result.Changed;
            // 回复余量只是结算相位，不构成可见变化；仅当回复实际改变生命/灵力时才标脏，
            // 否则空闲满状态玩家会每秒推进都 bump StateVersion 并向外推送无意义增量。
            player.Combat.RegenCarrySeconds += step;
            if (player.Combat.RegenCarrySeconds >= 5 - 1e-9)
            {
                player.Combat.RegenCarrySeconds = 0;
                attributes = PlayerAttributeResolver.Apply(player, gameData);
                var hitpoints = player.Combat.Hitpoints;
                var manapoints = player.Combat.Manapoints;
                if (player.Combat.Hitpoints > 0)
                    player.Combat.Hitpoints = Math.Min(attributes.MaxHitpoints, player.Combat.Hitpoints + attributes.HpRegen);
                player.Combat.Manapoints = Math.Min(attributes.MaxManapoints, player.Combat.Manapoints + attributes.MpRegen);
                // 远征 PlayerHitpoints 是权威气血的展示镜像：搜寻/复活等非交战相位同样需要
                // 跟上自然回复，否则前端在脱战期间显示冻结的旧血量，下次开战才跳变。
                if (player.Actions.Count > 0 && player.Actions[0].Expedition is { } expedition
                    && (expedition.PlayerHitpoints != player.Combat.Hitpoints
                        || expedition.PlayerManapoints != player.Combat.Manapoints))
                    player.Actions[0].Expedition = expedition with
                    {
                        PlayerHitpoints = player.Combat.Hitpoints,
                        PlayerManapoints = player.Combat.Manapoints
                    };
                if (player.Combat.Hitpoints != hitpoints || player.Combat.Manapoints != manapoints)
                {
                    player.MarkDirty(PlayerSectionKind.Combat);
                    changed = true;
                }
            }
        }
        if (player.LastSimulatedAt < target)
        {
            // 跳过 48 小时上限外的尾段时间：清理过期增益并把时钟推进到真实目标，不结算收益。
            var expired = player.Buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= target.ToUnixTimeMilliseconds() / 1000d);
            if (expired > 0) { player.MarkDirty(PlayerSectionKind.Buffs); changed = true; }
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            player.LastSimulatedAt = target;
            changed = true;
            events.Add(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        }
        if (changed) player.StateVersion = checked(player.StateVersion + 1);
        return new(player.PlayerId, from, target, (target - from).TotalSeconds, actions, repeats,
            deltas.Where(pair => pair.Value != 0).Select(pair => new AssetDelta(player.PlayerId, pair.Key, pair.Value, "simulation")).ToArray(),
            events, player.StateVersion, changed);
    }

    private static List<AssetDelta> ActivateInstantConsumables(PlayerAggregate player,
        GameDataCatalog catalog, AssetWallet wallet, DateTimeOffset at, List<SimulationEvent> events)
    {
        if (player.Actions.Count == 0 || player.Combat.Hitpoints <= 0) return [];
        var kind = catalog.Actions.Get(player.Actions[0].Intent.ActionId).Kind;
        var deltas = new List<AssetDelta>();
        var now = at.ToUnixTimeMilliseconds() / 1000d;
        for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
        {
            var slot = player.Settings.AutoUseSlots[i];
            if (!slot.Enabled || slot.ItemId is null || !MatchesActionType(slot.ActionType, kind)
                || slot.CooldownUntilUnixSeconds > now) continue;
            var id = new ItemId(slot.ItemId);
            var item = catalog.Items[id];
            if (item.Consumable is not { IsContinuous: false } definition
                || !definition.UsableInActionKinds.Contains(kind)
                || !catalog.RawItems[id.Value].TryGetProperty("consumable", out var raw)) continue;
            var consumed = deltas.Where(delta => delta.Asset.ItemId == id).Sum(delta => delta.QuantityDelta);
            if (wallet.Get(new AssetKey(id)) + consumed < 1) continue;
            double Read(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.GetDouble() : 0;
            var hp = Read(raw, "hitpointRestore");
            var mp = Read(raw, "manapointRestore");
            var cooldown = Read(raw, "cooldownSeconds");
            if (!raw.TryGetProperty("buffs", out var buffs) && hp == 0 && mp == 0) continue;
            if (cooldown <= 0) throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
            player.Combat.Hitpoints = Math.Min(player.Combat.MaxHitpoints, player.Combat.Hitpoints + hp);
            player.Combat.Manapoints = Math.Min(player.Combat.MaxManapoints, player.Combat.Manapoints + mp);
            if (buffs.ValueKind == JsonValueKind.Array)
                foreach (var buff in buffs.EnumerateArray())
                {
                    var buffId = buff.GetProperty("buffId").GetString()!;
                    player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
                    player.Buffs.Active.Add(new(buffId, now + Read(buff, "durationSeconds"), 1,
                        buff.GetProperty("name").GetString()!, id.Value, Read(buff, "hpRegen"), Read(buff, "mpRegen"),
                        Read(buff, "attackSpeedRatio"), Read(buff, "damageAbsorptionRatio")));
                }
            player.Settings.AutoUseSlots[i] = slot with { CooldownUntilUnixSeconds = now + cooldown };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Buffs);
            player.MarkDirty(PlayerSectionKind.Combat);
            deltas.Add(new(player.PlayerId, new AssetKey(id), -1, "auto-use"));
            events.Add(new(at, "auto-use.activated", $"自动使用 {item.Name}。", ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
        }
        return deltas;
    }

    private static SimulationResult AdvanceToCore(
        PlayerAggregate player,
        DateTimeOffset target,
        ActionCatalog catalog,
        GameDataCatalog? gameData,
        AssetWallet? assetBalances,
        DuelAdvanceOptions? duel = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        target = target.ToUniversalTime();
        if (target < player.LastSimulatedAt)
            throw new DomainException(DomainErrorCode.InvalidArgument, "目标时间不能早于玩家上次模拟时间。");

        var from = player.LastSimulatedAt;
        var remainingSeconds = (target - from).TotalSeconds;
        var completedActions = 0d;
        var completedRepeats = 0d;
        var transitions = 0;
        Dictionary<ItemId, double>? deltas = null;
        List<SimulationEvent>? events = null;

        var blockedActions = new HashSet<Guid>();
        while (remainingSeconds > 0 && player.Actions.Count > 0)
        {
            var action = player.Actions[0];
            // 收益账本与队首行动绑定：队首切换（完成/移除/调序/插队）时旧账本立即归档。
            if (player.Duel.CurrentReport is { } boundReport && boundReport.ActionInstanceId != action.Id)
                player.ArchiveCurrentReport(from.AddSeconds((target - from).TotalSeconds - remainingSeconds));
            // 阻塞项是明确的停泊状态。它不能被本次推进重新标记为 Running，
            // 否则战斗失败等不消费时间的结果会在队头空转直到转换上限。
            if (action.Status == ActionQueueStatus.Blocked)
            {
                if (!player.Settings.LoopEnabled)
                {
                    player.RemoveAction(action.Id, incrementVersion: false);
                    transitions++;
                    continue;
                }
                if (player.Settings.LoopEnabled && player.Actions.Count > 1)
                {
                    if (player.Actions.All(a => a.Status == ActionQueueStatus.Blocked))
                    {
                        break;
                    }
                    player.RotateActionToTail(action.Id);
                    if (++transitions > MaxTransitionsPerAdvance)
                        throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");
                    continue;
                }
                break;
            }
            if (++transitions > MaxTransitionsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");

            var definition = catalog.Get(action.Intent.ActionId);
            var availableHere = definition.AllowedNodeIds is null || definition.AllowedNodeIds.Count == 0
                || (player.World.EdgeId is null && definition.AllowedNodeIds.Contains(player.World.NodeId));
            var hasInputs = definition.Inputs.All(input =>
                (assetBalances?.Get(new AssetKey(input.Key)) ?? (gameData is null ? double.PositiveInfinity : 0))
                + (deltas?.GetValueOrDefault(input.Key) ?? 0) >= input.Value);
            if (!availableHere || !hasInputs)
            {
                action.Status = ActionQueueStatus.Blocked;
                events ??= [];
                events.Add(new(from.AddSeconds((target - from).TotalSeconds - remainingSeconds), "action.blocked",
                    availableHere ? "材料不足，行动受阻。" : "当前位置不允许执行该行动。", definition.Id));
                continue;
            }
            // 行动周期起点的唯一判据是行动自身进度归零：此时本轮尚未结算任何整秒收益，
            // 持续消耗品在这一点扣件才能让本轮整段收益都吃到倍率；若已结算过整秒则等下一轮。
            // 严禁再叠加 SimulationCarrySeconds 之类的全局浮点状态：该进位余数只记录亚秒残差，
            // 在整秒序列上保持不变且没有任何重置点，一旦因离线补算变成非零就会让本判定恒为 false，
            // 导致“动作开始扣件”永不触发、持续消耗品永久失效。
            var isActionStart = action.ProgressSeconds <= SecondQuantizationEpsilon;
            action.Status = ActionQueueStatus.Running;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                if (gameData is null)
                    throw new DomainException(DomainErrorCode.NotFound, $"行动 {definition.Id} 缺少战斗配表上下文。");
                AdvanceCombatAction(player, action, definition, gameData, duel, from, target, ref remainingSeconds,
                    ref deltas, ref events, ref completedActions, ref completedRepeats);
                continue;
            }
            if (definition.ExecutionKind == ActionExecutionKind.Travel)
            {
                AdvanceTravelAction(player, action, definition, gameData, from, target, ref remainingSeconds,
                    ref events, ref completedActions, ref completedRepeats);
                continue;
            }
            var duration = definition.DurationSeconds;
            if (duration <= 0)
            {
                // 开发期容错自愈：Enqueue 拒绝“-1 且时长≤0”，但配表热更可能把存量行动时长改为 0，
                // 队头空转会耗尽转换上限并让玩家永久卡死。此处移出失效行动并留事件证据，继续推进后续行动。
                player.RemoveAction(action.Id, incrementVersion: false);
                events ??= new List<SimulationEvent>();
                events.Add(new SimulationEvent(
                    from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                    "action.invalid-dropped",
                    $"行动 {definition.Name} 的时长配置无效（{duration:0.######} 秒），已自动移出队列。",
                    definition.Id));
                continue;
            }

            var actionAt = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
            var autoUseMultiplier = PrepareSustainedAutoUse(
                player,
                definition,
                isActionStart,
                gameData,
                assetBalances,
                actionAt,
                ref deltas,
                ref events);
            if (definition.Kind == ActionKind.Cultivation && gameData is not null)
                autoUseMultiplier *= CultivationMultiplier(gameData, player.World.NodeId);
            var needed = Math.Max(0, duration - action.ProgressSeconds);
            var step = Math.Min(remainingSeconds, needed);
            remainingSeconds -= step;
            var wholeSeconds = QuantizeElapsedSeconds(player, step);
            action.ProgressSeconds += wholeSeconds;
            // 生平修炼时长只统计修炼类行动的实际推进秒数。
            if (definition.Kind == ActionKind.Cultivation && wholeSeconds > 0)
                player.Progression.CultivationSeconds += wholeSeconds;
            ApplyContinuousReward(player, definition, wholeSeconds, autoUseMultiplier, gameData);
            if (step > 0)
            {
                player.MarkDirty(definition.Kind switch
                {
                    ActionKind.Cultivation => PlayerSectionKind.Progression,
                    ActionKind.Exploration => PlayerSectionKind.World,
                    ActionKind.Combat => PlayerSectionKind.Combat,
                    _ => PlayerSectionKind.ActionQueue
                });
            }
            if (action.ProgressSeconds + 1e-9 < duration) break;
            CompleteRepeat(player, action, definition, autoUseMultiplier, gameData, from.AddSeconds((target - from).TotalSeconds - remainingSeconds), ref deltas, ref events, ref completedActions, ref completedRepeats);
        }

        // 道龄随模拟时间自然流逝；Progression 仅在发生实际状态转移时标脏，避免空队列每秒刷盘。
        player.Progression.AgeSeconds += (target - from).TotalSeconds;
        player.LastSimulatedAt = target;
        if (remainingSeconds < 0) throw new DomainException(DomainErrorCode.InvariantViolation, "模拟时间出现负余量。");
        if (player.Actions.Count > 0 && player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            player.Actions[0].Status = ActionQueueStatus.Running;
        }
        // 空队列只更新内存中的时钟，不递增状态版本，也不标记 section，避免每秒产生无意义的磁盘写入。
        if (transitions > 0)
        {
            player.StateVersion = checked(player.StateVersion + 1);
            player.MarkDirty(PlayerSectionKind.Progression);
            player.MarkDirty(PlayerSectionKind.ActionQueue);
        }

        AssetDelta[] assetDeltas;
        if (deltas is null || deltas.Count == 0)
        {
            assetDeltas = [];
        }
        else
        {
            var validDeltas = new List<KeyValuePair<ItemId, double>>(deltas.Count);
            foreach (var pair in deltas)
            {
                if (Math.Abs(pair.Value) > 1e-12)
                {
                    validDeltas.Add(pair);
                }
            }

            if (validDeltas.Count == 0)
            {
                assetDeltas = [];
            }
            else
            {
                validDeltas.Sort(static (a, b) => string.CompareOrdinal(a.Key.Value, b.Key.Value));
                assetDeltas = new AssetDelta[validDeltas.Count];
                var playerId = player.PlayerId;
                for (var i = 0; i < validDeltas.Count; i++)
                {
                    var pair = validDeltas[i];
                    assetDeltas[i] = new AssetDelta(playerId, new AssetKey(pair.Key), pair.Value, "simulation");
                }
            }
        }

        return new SimulationResult(
            player.PlayerId,
            from,
            target,
            (target - from).TotalSeconds,
            completedActions,
            completedRepeats,
            assetDeltas,
            (IReadOnlyList<SimulationEvent>?)events ?? [],
            player.StateVersion,
            transitions > 0);
    }

    /// <summary>
    /// 推进一个战斗遭遇行动。每次调用至少消耗搜寻或战斗时间；阶段切换后由外层循环继续推进，
    /// 从而避免 durationSeconds=0 的战斗行动进入普通计时器并被自愈删除。
    /// </summary>
    private static void AdvanceCombatAction(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        GameDataCatalog gameData,
        DuelAdvanceOptions? duel,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref List<SimulationEvent>? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        CombatEncounterDefinition encounter;
        var expedition = action.Expedition;
        if (expedition is null)
        {
            encounter = ResolveCombatEncounter(player, action, definition, gameData);
            // 新战斗行动会话开始：重建本次行动收益账本。
            player.BeginBattleReport(action.Id, definition.Id, definition.Name,
                from.AddSeconds((target - from).TotalSeconds - remainingSeconds));
            expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.InitialSearch, gameData);
            action.Expedition = expedition;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.search.started", $"开始搜寻：{encounter.Name}。", definition.Id);
        }
        else if (!gameData.CombatEncounters.TryGetValue(expedition.EncounterId, out encounter!))
        {
            throw new DomainException(DomainErrorCode.NotFound, $"远征状态引用的遭遇 {expedition.EncounterId} 不存在。");
        }

        if (!expedition.IsValid)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"行动 {definition.Id} 的战斗远征状态无效。");

        if (expedition.Phase == CombatExpeditionPhase.Duel)
        {
            AdvanceDuelPlayback(player, action, definition, encounter, gameData, from, target,
                ref remainingSeconds, ref deltas, ref events);
            return;
        }

        if (expedition.Phase == CombatExpeditionPhase.DuelPending)
        {
            if (duel?.MatchingEnabled == true)
            {
                // 匹配等待不消耗战斗时间：整段剩余时间记为等待，由撮合运行时在下一拍完成挂载。
                action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + remainingSeconds };
                action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
                remainingSeconds = 0;
                return;
            }
            // 离线补算等无撮合路径：保留遭遇计数，回退为正常波次入口。
            expedition = expedition with
            {
                Phase = CombatExpeditionPhase.Search,
                PhaseElapsedSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
                PhaseDurationSeconds = Math.Max(expedition.PhaseElapsedSeconds, 1e-9),
                DuelSeekFailed = true
            };
            action.Expedition = expedition;
        }

        var phaseRemaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
        if (expedition.Phase == CombatExpeditionPhase.Respawning)
        {
            var step = Math.Min(remainingSeconds, phaseRemaining);
            remainingSeconds -= step;
            action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + step };
            action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
            if (action.ProgressSeconds + 1e-9 < expedition.PhaseDurationSeconds) return;
            player.Combat.Hitpoints = player.Combat.MaxHitpoints;
            player.Combat.Manapoints = player.Combat.MaxManapoints;
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            for (var i = 0; i < player.Settings.AutoUseSlots.Count; i++)
                player.Settings.AutoUseSlots[i] = player.Settings.AutoUseSlots[i] with { CooldownUntilUnixSeconds = 0 };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Combat);
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData, NextSeed(expedition.Seed));
            action.ProgressSeconds = 0;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.respawned", "复活完成，气血与灵力恢复。", definition.Id);
            return;
        }
        if (expedition.Phase is CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search)
        {
            var step = Math.Min(remainingSeconds, phaseRemaining);
            // DuelSeekFailed 的搜寻态是撮合失败回退出的「已完成搜寻」入口，phaseRemaining 为 0 属正常。
            if (step <= 0 && !expedition.DuelSeekFailed)
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗搜寻阶段没有可推进的时间。");
            remainingSeconds -= step;
            var updated = expedition with
            {
                PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + step
            };
            action.ProgressSeconds = updated.PhaseElapsedSeconds;
            action.Expedition = updated;
            if (updated.PhaseElapsedSeconds + 1e-9 < updated.PhaseDurationSeconds) return;

            var seed = updated.Seed;
            // 斗法入场判定：勾选斗法且不在失败冷却中时，每次进入战斗先判定遭遇计数。
            var duelSettings = gameData.Duel;
            if (action.Intent.PvpEnabled)
            {
                var entryAtUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
                if (player.Duel.CooldownUntilUnixSeconds <= entryAtUnix)
                {
                    var chargesBefore = player.Duel.EncounterCharges;
                    var chargeRoll = (unchecked((ulong)NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
                    if (chargeRoll < duelSettings.EncounterChancePerBattle)
                    {
                        player.Duel.EncounterCharges += 1;
                        player.MarkDirty(PlayerSectionKind.Duel);
                        AddCombatEvent(ref events, from, target, remainingSeconds, "duel.charge", "心血来潮：斗法遭遇感应 +1。", definition.Id);
                    }
                    // 计数大于 0 时进入匹配等待（以判定前计数为准，本波新增的计数留给下次入场）。
                    if (chargesBefore > 0 && !updated.DuelSeekFailed && duel?.MatchingEnabled == true)
                    {
                        action.Expedition = updated with
                        {
                            Phase = CombatExpeditionPhase.DuelPending,
                            PhaseElapsedSeconds = 0,
                            PhaseDurationSeconds = 0,
                            DuelSeekFailed = false
                        };
                        action.ProgressSeconds = 0;
                        AddCombatEvent(ref events, from, target, remainingSeconds, "duel.seeking", "斗法感应触发，正在匹配旗鼓相当的对手……", definition.Id);
                        return;
                    }
                }
            }

            // 按遭遇预算与波次生成确定性的本波怪物阵容，同波全体敌人同场交战。
            var wave = EncounterWaveSelector.Select(encounter, gameData.CombatMonsters, seed, updated.WaveNumber);
            var battle = CombatEncounterState.Start(
                encounter.Id,
                seed,
                PlayerAttributeResolver.Resolve(player, gameData).ToCombatant(),
                wave.Select(monster => new CombatantTemplate(monster.Stats, monster.Skills)).ToArray());
            for (var i = 0; i < wave.Count; i++)
                battle.Enemies[i].DefinitionId = wave[i].Id;
            battle.Player.Hitpoints = Math.Clamp(player.Combat.Hitpoints, 0, player.Combat.MaxHitpoints);
            battle.Player.Manapoints = Math.Clamp(player.Combat.Manapoints, 0, player.Combat.MaxManapoints);
            var waveNames = string.Join("、", wave.Select(monster => monster.Name));
            if (battle.Player.Hitpoints <= 0)
            {
                action.Status = ActionQueueStatus.Blocked;
                AddCombatEvent(ref events, from, target, remainingSeconds, "combat.lost", $"气血衰竭，无法迎战：{waveNames}。", definition.Id);
                if (player.Settings.LoopEnabled && player.Actions.Count > 1 && !player.Actions.All(a => a.Status == ActionQueueStatus.Blocked))
                {
                    player.RotateActionToTail(action.Id);
                }
                return;
            }
            var combatState = ToExpeditionState(battle, updated with
            {
                Phase = CombatExpeditionPhase.Combat,
                PhaseElapsedSeconds = 0,
                PhaseDurationSeconds = 0,
                Outcome = CombatOutcome.Running,
                DuelSeekFailed = false
            });
            action.ProgressSeconds = 0;
            action.Expedition = combatState;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.started", $"遭遇妖兽：{waveNames}。", definition.Id);
            return;
        }

        if (expedition.Phase != CombatExpeditionPhase.Combat || expedition.Enemies is not { Count: > 0 } waveEnemies
            || waveEnemies.Any(enemy => !gameData.CombatMonsters.ContainsKey(enemy.EnemyId)))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗远征敌人状态无效。");

        var templates = waveEnemies.Select(enemy =>
        {
            var monster = gameData.CombatMonsters[enemy.EnemyId];
            return new CombatantTemplate(monster.Stats, monster.Skills);
        }).ToArray();
        var battleState = CombatEncounterState.Restore(
            encounter.Id,
            expedition.Seed,
            PlayerAttributeResolver.Resolve(player, gameData).ToCombatant(),
            new CombatantSnapshot(expedition.PlayerHitpoints, expedition.PlayerManapoints,
                expedition.PlayerNextAttackAtSeconds, expedition.PlayerAttackCount, expedition.PlayerThreat),
            templates,
            waveEnemies.Select(enemy => new CombatantSnapshot(enemy.Hitpoints, enemy.Manapoints,
                enemy.NextAttackAtSeconds, enemy.AttackCount, enemy.Threat,
                enemy.UsedSkillMask, enemy.ThreatRatioBonus, enemy.BonusAbsorptionPoints)).ToArray(),
            expedition.BattleElapsedSeconds,
            expedition.Outcome,
            expedition.PlayerAttackCount + waveEnemies.Sum(enemy => enemy.AttackCount));
        for (var i = 0; i < waveEnemies.Count; i++)
            battleState.Enemies[i].DefinitionId = waveEnemies[i].EnemyId;
        var battleFrom = battleState.ElapsedSeconds;
        var actionElapsedBeforeCombat = (target - from).TotalSeconds - remainingSeconds;
        var attributes = PlayerAttributeResolver.Resolve(player, gameData);
        var skillStart = CombatSkillEngine.Advance(player, gameData, battleState, attributes, player.Combat.Skills, 0);
        player.Combat.Skills = skillStart.State;
        battleState.Player.Manapoints = player.Combat.Manapoints;
        var battleTo = battleFrom + Math.Min(remainingSeconds, CombatSkillEngine.NextBoundarySeconds(player.Combat.Skills));
        var combatResult = DeterministicCombatEngine.AdvanceTo(battleState, battleTo);
        var consumed = Math.Max(0, battleState.ElapsedSeconds - battleFrom);
        var skillResult = CombatSkillEngine.Advance(player, gameData, battleState, attributes, player.Combat.Skills, consumed);
        player.Combat.Skills = skillResult.State;
        foreach (var skillEvent in skillStart.Events.Concat(skillResult.Events))
            AddCombatEventAt(ref events, from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, skillEvent.AtSeconds - battleFrom)),
                "combat.skill", $"施放 {skillEvent.AbilityId}，伤害 {skillEvent.Damage:0.##}，治疗 {skillEvent.Healing:0.##}。", definition.Id);
        combatResult = combatResult with { Outcome = battleState.Outcome };
        remainingSeconds -= consumed;
        player.Combat.Hitpoints = battleState.Player.Hitpoints;
        // 战斗中气血是独立持久化分段；仅标记行动队列会导致重连后回退到旧气血。
        player.MarkDirty(PlayerSectionKind.Combat);
        var updatedBattle = ToExpeditionState(battleState, expedition with
        {
            PhaseElapsedSeconds = Math.Min(expedition.PhaseDurationSeconds, expedition.PhaseElapsedSeconds + consumed),
            Outcome = combatResult.Outcome
        });
        action.Expedition = updatedBattle;
        action.ProgressSeconds = updatedBattle.PhaseElapsedSeconds;
        string NameOf(string combatantId) => combatantId == "player" ? "你"
            : combatantId.StartsWith("enemy:", StringComparison.Ordinal)
                && int.TryParse(combatantId[6..], out var enemyIndex) && enemyIndex >= 0 && enemyIndex < waveEnemies.Count
                ? gameData.CombatMonsters[waveEnemies[enemyIndex].EnemyId].Name
                : combatantId;
        foreach (var combatEvent in combatResult.Events)
        {
            var attacker = NameOf(combatEvent.AttackerId);
            var targetName = NameOf(combatEvent.TargetId);
            var eventAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom));
            AddCombatEventAt(ref events, eventAt,
                "combat.attack", $"{attacker}{(combatEvent.Hit ? $"命中{targetName}，造成 {combatEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
        }
        if (combatResult.Outcome == CombatOutcome.Running) return;
        if (combatResult.Outcome != CombatOutcome.PlayerWon)
        {
            // 讨伐失败：计入生平败走，波次重置回第 1 波。
            player.Progression.TotalDefeats += 1;
            player.MarkDirty(PlayerSectionKind.Progression);
            action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, gameData, updatedBattle.Seed,
                waveNumber: 1) with
            { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
            action.ProgressSeconds = 0;
            var waveNames = string.Join("、", waveEnemies.Select(enemy => gameData.CombatMonsters[enemy.EnemyId].Name));
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.lost", $"讨伐失败：{waveNames}。", definition.Id);
            return;
        }

        // 波次清空才结算掉落与完成一次重复；逐只发放击杀奖励并计入生平斩妖。
        var pvpEnabled = action.Intent.PvpEnabled;
        var killAtUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
        var killSeed = updatedBattle.Seed;
        foreach (var enemy in waveEnemies)
        {
            var monster = gameData.CombatMonsters[enemy.EnemyId];
            ApplyCombatRewards(player, monster, killSeed, pvpEnabled, killAtUnix, gameData, ref deltas);
            killSeed = NextSeed(killSeed);
            switch (monster.Rank)
            {
                case CombatMonsterRank.Elite: player.Progression.EliteKills += 1; break;
                case CombatMonsterRank.Boss: player.Progression.BossKills += 1; break;
                default: player.Progression.NormalKills += 1; break;
            }
        }
        player.MarkDirty(PlayerSectionKind.Loadout);
        player.MarkDirty(PlayerSectionKind.Progression);
        AddCombatEvent(ref events, from, target, remainingSeconds, "combat.won",
            $"讨伐成功：{string.Join("、", waveEnemies.Select(enemy => gameData.CombatMonsters[enemy.EnemyId].Name))}。", definition.Id);

        completedActions += 1;
        completedRepeats += 1;
        action.CompletedRepeats += 1;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
        if (action.RemainingRepeats == 0)
        {
            if (player.Settings.LoopEnabled)
            {
                action.RemainingRepeats = action.Intent.RepeatCount;
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, NextSeed(updatedBattle.Seed), waveNumber: 1);
                action.ProgressSeconds = 0;
                if (player.Actions.Count > 1)
                {
                    player.RotateActionToTail(action.Id);
                }
                return;
            }
            player.RemoveAction(action.Id, incrementVersion: false);
            return;
        }
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
            gameData, NextSeed(updatedBattle.Seed), updatedBattle.WaveNumber + 1);
        action.ProgressSeconds = 0;
    }

    /// <summary>
    /// 斗法剧本回放推进：结果在撮合时已确定性锁定，本相位只按时间轴回放事件并结算归属。
    /// 战斗期间任何配置修改不影响剧本；玩家主动结束行动走 ApplyDuelForfeit 判负路径。
    /// </summary>
    private static void AdvanceDuelPlayback(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        CombatEncounterDefinition encounter,
        GameDataCatalog gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref List<SimulationEvent>? events)
    {
        var expedition = action.Expedition!;
        var playback = expedition.Duel ?? throw new DomainException(DomainErrorCode.InvariantViolation, "斗法回放载荷缺失。");
        var elapsedBefore = (target - from).TotalSeconds - remainingSeconds;
        var step = playback.Resolved ? 0 : Math.Min(remainingSeconds, Math.Max(0, playback.DurationSeconds - expedition.BattleElapsedSeconds));
        remainingSeconds -= step;
        var playbackTo = Math.Min(expedition.BattleElapsedSeconds + step, playback.DurationSeconds);
        var cursor = (int)playback.EventCursor;
        var viewerHp = expedition.PlayerHitpoints;
        var opponentHp = playback.OpponentHitpoints;
        // 回放游标前的事件已全部应用过；游标到 playbackTo 之间的事件逐条应用并写日志。
        for (var i = cursor; i < playback.Events.Count; i++)
        {
            var duelEvent = playback.Events[i];
            if (duelEvent.AtSeconds > playbackTo + 1e-9) break;
            cursor = i + 1;
            var targetIsViewer = duelEvent.TargetIsInitiator == playback.ViewerIsInitiator;
            if (targetIsViewer) viewerHp = duelEvent.TargetHitpointsAfter;
            else opponentHp = duelEvent.TargetHitpointsAfter;
            var attacker = duelEvent.AttackerIsInitiator == playback.ViewerIsInitiator ? "你" : playback.OpponentName;
            var targetName = targetIsViewer ? "你" : playback.OpponentName;
            AddCombatEventAt(ref events, from.AddSeconds(elapsedBefore + Math.Max(0, duelEvent.AtSeconds - expedition.BattleElapsedSeconds)),
                "combat.attack", $"{attacker}{(duelEvent.Hit ? $"命中{targetName}，造成 {duelEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
        }
        var updated = expedition with
        {
            BattleElapsedSeconds = playbackTo,
            PlayerHitpoints = viewerHp,
            Duel = playback with { EventCursor = cursor, OpponentHitpoints = opponentHp }
        };
        action.Expedition = updated;
        player.Combat.Hitpoints = Math.Clamp(viewerHp, 0, player.Combat.MaxHitpoints);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (!playback.Resolved && playbackTo + 1e-9 < playback.DurationSeconds) return;

        // 回放结束（或已裁决标记）：结算斗法归属。
        var now = from.AddSeconds(elapsedBefore + Math.Max(0, playbackTo - expedition.BattleElapsedSeconds));
        var nowUnix = now.ToUnixTimeMilliseconds() / 1000d;
        var duelSettings = gameData.Duel;
        var report = player.Duel.CurrentReport;
        player.Duel.ActiveDuelId = null;
        switch (playback.ViewerOutcome)
        {
            case CombatOutcome.PlayerWon:
                foreach (var spoils in playback.OpponentStagedSnapshot)
                {
                    var spoilsId = new ItemId(spoils.ItemId);
                    AddDelta(deltas ??= new Dictionary<ItemId, double>(), spoilsId, spoils.Quantity);
                    report?.AddItem(spoilsId, spoils.Quantity);
                }
                if (report is not null) report.DuelWins += 1;
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.won",
                    playback.Forfeited
                        ? $"斗法获胜：{playback.OpponentName} 弃战离去，其暂存物品尽数落袋。"
                        : $"斗法获胜：击败 {playback.OpponentName}，缴获其暂存物品。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, NextSeed(expedition.Seed), waveNumber: 1);
                break;
            case CombatOutcome.EnemyWon:
                // 弃战路径（ApplyDuelForfeit）已即时记账暂存移除、冷却与败绩，回放落点不再重复结算。
                if (!playback.Forfeited)
                {
                    player.RemoveStagedItems(playback.SelfStagedSnapshot);
                    player.Duel.CooldownUntilUnixSeconds = nowUnix + duelSettings.DefeatCooldownSeconds;
                    if (report is not null) report.DuelLosses += 1;
                    player.Progression.TotalDefeats += 1;
                }
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.lost",
                    playback.Forfeited
                        ? $"斗法弃战：你已结束斗法行动，暂存物品被 {playback.OpponentName} 取走。"
                        : $"斗法落败：{playback.OpponentName} 取走了你的暂存物品，一小时内不再进入斗法。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, NextSeed(expedition.Seed), waveNumber: 1) with
                { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
                player.Combat.Hitpoints = 0;
                break;
            default:
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.draw",
                    $"斗法平局：与 {playback.OpponentName} 战成平手。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, NextSeed(expedition.Seed), waveNumber: 1);
                break;
        }
        player.MarkDirty(PlayerSectionKind.Duel);
        player.MarkDirty(PlayerSectionKind.Progression);
        action.ProgressSeconds = 0;
    }

    /// <summary>
    /// 解析战斗行动在当前节点实际应战的遭遇：玩家位于已登记生态遭遇的节点时，
    /// 按确定性种子从节点 encounterIds 中抽取；节点无生态或行进中则回退行动钉死的 combatEncounterId。
    /// </summary>
    private static CombatEncounterDefinition ResolveCombatEncounter(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        GameDataCatalog gameData)
    {
        var encounterId = definition.CombatEncounterId;
        if (string.IsNullOrWhiteSpace(player.World.EdgeId)
            && gameData.WorldGraph.TryGetNode(player.World.NodeId, out var node)
            && node.EncounterIds.Count > 0)
        {
            var pool = node.EncounterIds;
            var index = pool.Count == 1 ? 0 : (int)((ulong)CreateSeed(definition.Id.Value, action.CompletedRepeats) % (ulong)pool.Count);
            encounterId = pool[index];
        }
        if (string.IsNullOrWhiteSpace(encounterId)
            || !gameData.CombatEncounters.TryGetValue(encounterId, out var encounter))
            throw new DomainException(DomainErrorCode.NotFound, $"战斗行动 {definition.Id} 在节点 {player.World.NodeId} 无可用遭遇。");
        return encounter;
    }

    private static CombatExpeditionState CreateSearchState(
        PlayerAggregate player,
        QueuedAction action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog? gameData,
        long? seed = null,
        int waveNumber = 1)
    {
        // 搜寻等待时间随境界成长：Wait(Lv) = 5.0 - 2.5·x²，作用于遭遇基础搜寻秒数的乘区。
        var delayFactor = gameData is not null ? PlayerAttributeResolver.Resolve(player, gameData).SearchDelayFactor : 1;
        var duration = (phase == CombatExpeditionPhase.InitialSearch ? encounter.SearchSeconds : encounter.RespawnSeconds) * delayFactor;
        return new(
            encounter.Id,
            phase,
            0,
            duration,
            seed ?? CreateSeed(encounter.Id, action.CompletedRepeats),
            0,
            player.Combat.Hitpoints,
            player.Combat.Manapoints,
            0,
            0,
            0,
            CombatOutcome.Running,
            waveNumber,
            null);
    }

    private static CombatExpeditionState ToExpeditionState(CombatEncounterState battle, CombatExpeditionState template) => template with
    {
        BattleElapsedSeconds = battle.ElapsedSeconds,
        PlayerHitpoints = battle.Player.Hitpoints,
        PlayerManapoints = battle.Player.Manapoints,
        PlayerNextAttackAtSeconds = battle.Player.NextAttackAtSeconds,
        PlayerAttackCount = battle.Player.AttackCount,
        PlayerThreat = battle.Player.Threat,
        Outcome = battle.Outcome,
        Enemies = battle.Enemies.Select(enemy => new ExpeditionEnemyState(
            enemy.DefinitionId ?? enemy.Id, enemy.Hitpoints, enemy.Manapoints,
            enemy.NextAttackAtSeconds, enemy.AttackCount, enemy.Threat,
            enemy.UsedSkillMask, enemy.ThreatRatioBonus, enemy.BonusAbsorptionPoints)).ToArray()
    };

    /// <summary>
    /// 击杀单只怪物的全部奖励：修为、底蕴、六维经验与掉落。
    /// 斗法开启时掉落爆率乘以配置加成、掉落物进入暂存区；所有收益计入当前战斗账本。
    /// </summary>
    private static void ApplyCombatRewards(
        PlayerAggregate player,
        CombatMonsterDefinition monster,
        long seed,
        bool pvpEnabled,
        double killAtUnixSeconds,
        GameDataCatalog gameData,
        ref Dictionary<ItemId, double>? deltas)
    {
        var report = player.Duel.CurrentReport;
        if (monster.QiReward > 0)
        {
            player.Progression.Qi += monster.QiReward;
            player.Progression.LifetimeQi += monster.QiReward;
            if (report is not null) report.Qi += monster.QiReward;
        }
        if (monster.ExperienceReward > 0)
        {
            var cappedExpReward = CapExperience(player, monster.ExperienceReward, gameData);
            player.Progression.Experience += cappedExpReward;
            if (report is not null) report.Experience += cappedExpReward;
        }
        if (monster.RootExpReward > 0)
            DistributeRootExp(player, monster.RootExpReward, gameData, report);
        var dropBonus = pvpEnabled ? PercentValue.Multiplier(gameData.Duel.DropChanceBonusPercent, PercentCurveMode.Linear) : 1;
        foreach (var drop in monster.DropTable)
        {
            if (!drop.ItemId.IsValid || drop.MinQuantity <= 0 || drop.MaxQuantity < drop.MinQuantity || drop.DropChance <= 0) continue;
            var roll = (unchecked((ulong)NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
            seed = NextSeed(seed);
            if (roll > Math.Min(1, drop.DropChance * dropBonus)) continue;
            var quantity = Math.Floor(drop.MinQuantity + (drop.MaxQuantity - drop.MinQuantity) * roll);
            quantity = Math.Max(drop.MinQuantity, quantity);
            if (pvpEnabled)
            {
                // 斗法期间掉落进入暂存区：入账延迟与归属由暂存结算/斗法结算决定。
                player.StageItem(drop.ItemId, quantity, killAtUnixSeconds);
            }
            else
            {
                AddDelta(deltas ??= new Dictionary<ItemId, double>(), drop.ItemId, quantity);
                report?.AddItem(drop.ItemId, quantity);
            }
        }
    }

    /// <summary>
    /// 六维经验分配：按已装备功法槽位的 rootAttribute 集合均分；
    /// 无功法或功法未声明归属维度时六维平均分配。
    /// </summary>
    private static void DistributeRootExp(PlayerAggregate player, double amount, GameDataCatalog gameData, PlayerBattleReport? report)
    {
        var targets = new HashSet<CoreAspectKind>();
        var anySlot = false;
        foreach (var slot in player.Loadout.AbilitySlots)
        {
            if (slot.AbilityId is null) continue;
            anySlot = true;
            if (TryParseCoreAspect(slot.AbilityId, gameData, out var attribute))
                targets.Add(attribute);
            else
            {
                // 功法未声明归属维度视为全修：覆盖全部六维。
                targets.UnionWith(Enum.GetValues<CoreAspectKind>());
            }
        }
        if (!anySlot || targets.Count == 0)
            targets.UnionWith(Enum.GetValues<CoreAspectKind>());
        var share = amount / targets.Count;
        var core = player.Loadout.CoreAttributes;
        foreach (var attribute in targets)
        {
            core = attribute switch
            {
                CoreAspectKind.Stamina => core with { StaminaExp = core.StaminaExp + share },
                CoreAspectKind.Intelligence => core with { IntelligenceExp = core.IntelligenceExp + share },
                CoreAspectKind.Attack => core with { AttackExp = core.AttackExp + share },
                CoreAspectKind.Defense => core with { DefenseExp = core.DefenseExp + share },
                CoreAspectKind.Swordsmanship => core with { SwordsmanshipExp = core.SwordsmanshipExp + share },
                _ => core with { TalismanExp = core.TalismanExp + share }
            };
            report?.RootExp.Add(attribute, share);
        }
        player.Loadout.CoreAttributes = core;
    }

    private static bool TryParseCoreAspect(string abilityId, GameDataCatalog catalog, out CoreAspectKind attribute)
    {
        attribute = default;
        if (!catalog.Abilities.TryGetValue(abilityId, out var ability)
            || !ability.TryGetProperty("rootAttribute", out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        return Enum.TryParse(property.GetString(), true, out attribute) && Enum.IsDefined(attribute);
    }

    private static void AddCombatEvent(ref List<SimulationEvent>? events, DateTimeOffset from, DateTimeOffset target, double remainingSeconds, string code, string message, ActionId actionId)
    {
        events ??= new List<SimulationEvent>();
        var elapsed = Math.Max(0, (target - from).TotalSeconds - remainingSeconds);
        AddCombatEventAt(ref events, from.AddSeconds(elapsed), code, message, actionId);
    }

    private static void AddCombatEventAt(ref List<SimulationEvent>? events, DateTimeOffset at, string code, string message, ActionId actionId)
    {
        events ??= new List<SimulationEvent>();
        events.Add(new SimulationEvent(at, code, message, actionId));
    }

    /// <summary>
    /// 纯确定性推进移动行动。逐边段平滑累积行进进度与坐标插值，并在抵达终点时完成结算出队与触发到达事件。
    /// </summary>
    private static void AdvanceTravelAction(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref List<SimulationEvent>? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        var worldGraph = gameData?.WorldGraph;
        var travel = action.Travel;

        // 如果 travel 状态为空，基于当前玩家位置和意图目标节点进行自愈规划
        if (travel is null)
        {
            var targetNodeId = action.Intent.TargetNodeId ?? player.World.NodeId;
            if (worldGraph is not null && worldGraph.ContainsNode(targetNodeId))
            {
                try
                {
                    var plan = worldGraph.PlanFromPosition(
                        player.World.EdgeId,
                        player.World.EdgeProgress,
                        player.World.NodeId,
                        targetNodeId);
                    travel = new TravelActionState(
                        targetNodeId,
                        plan.TotalTravelSeconds,
                        plan.EdgeIds,
                        plan.Segments);
                    action.Travel = travel;
                }
                catch (DomainException)
                {
                    player.RemoveAction(action.Id, incrementVersion: false);
                    events ??= new List<SimulationEvent>();
                    events.Add(new SimulationEvent(
                        from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                        "action.invalid-dropped",
                        $"移动行动 {definition.Name} 无法到达目标地点，已自动移出队列。",
                        definition.Id));
                    return;
                }
            }
            else
            {
                player.RemoveAction(action.Id, incrementVersion: false);
                events ??= new List<SimulationEvent>();
                events.Add(new SimulationEvent(
                    from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                    "action.invalid-dropped",
                    $"移动行动 {definition.Name} 目标节点无效，已自动移出队列。",
                    definition.Id));
                return;
            }
        }

        if (travel.TotalDurationSeconds <= 0 || travel.Segments.Count == 0)
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, worldGraph);
            return;
        }

        var travelRemaining = Math.Max(0, travel.TotalDurationSeconds - travel.ProgressSeconds);
        var step = Math.Min(remainingSeconds, travelRemaining);
        if (step <= 0)
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, worldGraph);
            return;
        }

        remainingSeconds -= step;

        var segIndex = travel.CurrentSegmentIndex;
        var segElapsed = travel.SegmentElapsedSeconds;
        var progressSec = travel.ProgressSeconds;
        var toAdvance = step;

        while (toAdvance > 0 && segIndex < travel.Segments.Count)
        {
            var seg = travel.Segments[segIndex];
            var segRemaining = Math.Max(0, seg.DurationSeconds - segElapsed);
            var segStep = Math.Min(toAdvance, segRemaining);
            segElapsed += segStep;
            progressSec += segStep;
            toAdvance -= segStep;

            if (segElapsed + 1e-9 >= seg.DurationSeconds)
            {
                segIndex++;
                segElapsed = 0;
            }
        }

        action.ProgressSeconds = progressSec;
        action.Travel = travel with
        {
            CurrentSegmentIndex = segIndex,
            SegmentElapsedSeconds = segElapsed,
            ProgressSeconds = progressSec
        };

        if (segIndex < travel.Segments.Count)
        {
            var currentSeg = travel.Segments[segIndex];
            var segRatio = currentSeg.DurationSeconds > 0
                ? Math.Clamp(segElapsed / currentSeg.DurationSeconds, 0, 1)
                : 1d;
            var edgeProg = currentSeg.StartProgress + (currentSeg.EndProgress - currentSeg.StartProgress) * segRatio;
            player.World.EdgeId = currentSeg.EdgeId;
            player.World.EdgeProgress = Math.Clamp(edgeProg, 0, 1);

            if (worldGraph is not null && worldGraph.TryGetEdge(currentSeg.EdgeId, out var edgeObj))
            {
                if (worldGraph.TryGetNode(edgeObj.FromNodeId, out var fromNode)
                    && worldGraph.TryGetNode(edgeObj.ToNodeId, out var toNode))
                {
                    player.World.X = fromNode.X + (toNode.X - fromNode.X) * player.World.EdgeProgress;
                    player.World.Y = fromNode.Y + (toNode.Y - fromNode.Y) * player.World.EdgeProgress;
                }
            }

            player.MarkDirty(PlayerSectionKind.World);
            player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        else
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, worldGraph);
        }
    }

    private static void FinishTravel(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        TravelActionState travel,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref List<SimulationEvent>? events,
        ref double completedActions,
        ref double completedRepeats,
        WorldGraph? worldGraph)
    {
        player.World.NodeId = travel.TargetNodeId;
        player.World.EdgeId = null;
        player.World.EdgeProgress = 0;

        var nodeName = travel.TargetNodeId;
        if (worldGraph is not null && worldGraph.TryGetNode(travel.TargetNodeId, out var targetNode))
        {
            player.World.X = targetNode.X;
            player.World.Y = targetNode.Y;
            nodeName = targetNode.Name;
        }

        player.MarkDirty(PlayerSectionKind.World);
        player.RemoveAction(action.Id, incrementVersion: false);
        if (player.Actions.Count > 0 && player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            player.Actions[0].Status = ActionQueueStatus.Running;
        }
        player.MarkDirty(PlayerSectionKind.ActionQueue);

        completedActions += 1;
        completedRepeats += 1;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);

        var arrivalTime = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
        events ??= new List<SimulationEvent>();
        events.Add(new SimulationEvent(
            arrivalTime,
            "world.arrived",
            $"已抵达地点：{nodeName}。",
            definition.Id));
    }

    private static long CreateSeed(string encounterId, double completedRepeats)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var character in encounterId) { hash ^= character; hash *= 1099511628211UL; }
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(completedRepeats);
            return (long)hash;
        }
    }

    private static long NextSeed(long seed)
    {
        unchecked
        {
            var value = (ulong)seed;
            value ^= value << 13;
            value ^= value >> 7;
            value ^= value << 17;
            return (long)value;
        }
    }

    /// <summary>
    /// 把任意切分的模拟秒数累进持久化进位余数（carry）并取整整秒。
    /// 1e-9 量级贴整后再向下取整，使“一次推进 N 秒”与“逐秒推进 N 次”产生完全相同的整秒序列，
    /// 连续奖励的浮点操作序列因此位级一致；余数保留到下次推进。
    /// </summary>
    private static long QuantizeElapsedSeconds(PlayerAggregate player, double seconds)
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
    /// 修为经验入账截断：触及本层瓶颈（expToNext）后锁定增长待突破，满级（cap≤0）恒锁定。
    /// </summary>
    private static double CapExperience(PlayerAggregate player, double gain, GameDataCatalog? gameData)
    {
        var realm = gameData?.RealmProgression;
        if (realm is null) return gain;
        var cap = realm.ExpCapFor(player.Progression.RealmLevel);
        if (cap <= 0) return 0;
        return Math.Max(0, Math.Min(cap - player.Progression.Experience, gain));
    }

    /// <summary>
    /// 结算行动声明的按秒收益（per=second 的修为/灵力/气血/经验等持续通道）。
    /// 逐整秒循环累加，严格确保大跨度离线快进与客户端逐秒在线推进的 IEEE 754 浮点累加序列完全一致；
    /// 灵力与气血按聚合内已刷新的属性上限逐秒截断。
    /// </summary>
    private static void ApplyContinuousReward(PlayerAggregate player, ActionDefinition definition, long wholeSeconds, double multiplier, GameDataCatalog? gameData)
    {
        if (wholeSeconds <= 0) return;
        var expCap = gameData?.RealmProgression?.ExpCapFor(player.Progression.RealmLevel);
        var realmConfigured = gameData?.RealmProgression is not null;
        // 溢出预检查沿用“先校验后变更”语义：rate * 整秒数越界时不得污染任何进度数值。
        var seconds = (double)wholeSeconds;
        var combatTouched = false;
        var progressionTouched = false;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerSecond) continue;
            var rate = yield.Amount * multiplier;
            var total = rate * seconds;
            if (!double.IsFinite(rate) || !double.IsFinite(total))
                throw new DomainException(DomainErrorCode.InvariantViolation, "行动收益超出数值范围。");
            switch (yield.Kind)
            {
                case ActionYieldKind.Qi:
                    if (!double.IsFinite(player.Progression.Qi + total) || !double.IsFinite(player.Progression.LifetimeQi + total))
                        throw new DomainException(DomainErrorCode.InvariantViolation, "修为收益超出数值范围。");
                    // 逐整秒累加，与在线逐秒推进保持完全相同的浮点操作序列。
                    for (var i = 0; i < wholeSeconds; i++)
                    {
                        player.Progression.Qi += rate;
                        player.Progression.LifetimeQi += rate;
                    }
                    progressionTouched = true;
                    break;
                case ActionYieldKind.Experience:
                    if (!double.IsFinite(player.Progression.Experience + total))
                        throw new DomainException(DomainErrorCode.InvariantViolation, "经验收益超出数值范围。");
                    // 修为触及本层瓶颈后锁定增长，待突破后方可继续累积；满级（cap<=0）恒锁定。
                    for (var i = 0; i < wholeSeconds; i++)
                        player.Progression.Experience = realmConfigured
                            ? Math.Min(expCap ?? 0, player.Progression.Experience + rate)
                            : player.Progression.Experience + rate;
                    progressionTouched = true;
                    break;
                case ActionYieldKind.Mana:
                    // 满值时跳过逐秒循环，避免大额离线补算在已饱和资源上空转。
                    if (player.Combat.Manapoints >= player.Combat.MaxManapoints) break;
                    for (var i = 0; i < wholeSeconds && player.Combat.Manapoints < player.Combat.MaxManapoints; i++)
                        player.Combat.Manapoints = Math.Min(player.Combat.MaxManapoints, player.Combat.Manapoints + rate);
                    combatTouched = true;
                    break;
                case ActionYieldKind.Hitpoints:
                    if (player.Combat.Hitpoints >= player.Combat.MaxHitpoints) break;
                    for (var i = 0; i < wholeSeconds && player.Combat.Hitpoints < player.Combat.MaxHitpoints; i++)
                        player.Combat.Hitpoints = Math.Min(player.Combat.MaxHitpoints, player.Combat.Hitpoints + rate);
                    combatTouched = true;
                    break;
            }
        }
        if (combatTouched) player.MarkDirty(PlayerSectionKind.Combat);
        if (progressionTouched) player.MarkDirty(PlayerSectionKind.Progression);
        if (!double.IsFinite(player.Progression.Qi) || !double.IsFinite(player.Progression.LifetimeQi)
            || !double.IsFinite(player.Progression.Experience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "模拟后的玩家进度数值无效。");
    }

    /// <summary>
    /// 单轮行动耗时走满时的结算处理。
    /// 逐条结算配表声明的按轮收益（修为/灵力/气血/经验/物品/Buff）、扣除输入材料、扣减剩余次数，
    /// 若次数归零则移出队列；若为 -1（无限重复）则绝对保留队列项供下一轮循环。
    /// </summary>
    private static void CompleteRepeat(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        double autoUseMultiplier,
        GameDataCatalog? gameData,
        DateTimeOffset target,
        ref Dictionary<ItemId, double>? deltas,
        ref List<SimulationEvent>? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        // 先汇总数值型收益并完成有限性校验，再统一落账，沿用“先校验后变更”语义。
        double qiGain = 0, experienceGain = 0, manaGain = 0, hitpointGain = 0;
        foreach (var yield in definition.Yields)
        {
            if (yield.Period != ActionYieldPeriod.PerRepeat) continue;
            var amount = yield.Amount * autoUseMultiplier;
            switch (yield.Kind)
            {
                case ActionYieldKind.Qi: qiGain += amount; break;
                case ActionYieldKind.Experience: experienceGain += amount; break;
                case ActionYieldKind.Mana: manaGain += amount; break;
                case ActionYieldKind.Hitpoints: hitpointGain += amount; break;
            }
        }
        var nextExperience = player.Progression.Experience + CapExperience(player, experienceGain, gameData);
        if (!double.IsFinite(qiGain) || !double.IsFinite(experienceGain) || !double.IsFinite(manaGain) || !double.IsFinite(hitpointGain)
            || !double.IsFinite(player.Progression.Qi + qiGain) || !double.IsFinite(player.Progression.LifetimeQi + qiGain)
            || !double.IsFinite(nextExperience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动收益超出数值范围。");
        action.ProgressSeconds = 0;
        action.CompletedRepeats += 1;
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
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
                        grant.Name, null, grant.HpRegen, grant.MpRegen, grant.AttackSpeedRatio, grant.DamageAbsorptionRatio,
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
        player.Progression.Qi += qiGain;
        player.Progression.LifetimeQi += qiGain;
        player.Progression.Experience = nextExperience;
        if (manaGain != 0)
            player.Combat.Manapoints = Math.Min(player.Combat.MaxManapoints, player.Combat.Manapoints + manaGain);
        if (hitpointGain != 0)
            player.Combat.Hitpoints = Math.Min(player.Combat.MaxHitpoints, player.Combat.Hitpoints + hitpointGain);
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (manaGain != 0 || hitpointGain != 0) player.MarkDirty(PlayerSectionKind.Combat);
        if (buffTouched) player.MarkDirty(PlayerSectionKind.Buffs);
        events ??= new List<SimulationEvent>();
        events.Add(new SimulationEvent(
            target,
            "action.completed",
            definition.Name,
            definition.Id,
            itemRewards));
        // -1 表示无限行动：每轮完成后保留队列项并继续下一轮；仅在严格递减至 0 时出队或循环重入。
        if (action.RemainingRepeats == 0)
        {
            if (player.Settings.LoopEnabled && definition.ExecutionKind != ActionExecutionKind.Travel)
            {
                action.RemainingRepeats = action.Intent.RepeatCount;
                action.ProgressSeconds = 0;
                if (player.Actions.Count > 1)
                {
                    player.RotateActionToTail(action.Id);
                }
            }
            else
            {
                player.RemoveAction(action.Id, incrementVersion: false);
            }
        }
    }

    private static double PrepareSustainedAutoUse(
        PlayerAggregate player,
        ActionDefinition definition,
        bool isActionStart,
        GameDataCatalog? gameData,
        AssetWallet? assetBalances,
        DateTimeOffset at,
        ref Dictionary<ItemId, double>? deltas,
        ref List<SimulationEvent>? events)
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
                    slot = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
                    player.Settings.AutoUseSlots[i] = slot;
                    player.MarkDirty(PlayerSectionKind.Settings);
                    events ??= new List<SimulationEvent>();
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

    private static void CompleteSustainedAutoUse(
        PlayerAggregate player,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        DateTimeOffset at,
        ref List<SimulationEvent>? events)
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
            events ??= new List<SimulationEvent>();
            events.Add(new SimulationEvent(
                at,
                "auto-use.depleted",
                $"{item.Name} 的持续时间已耗尽。",
                definition.Id));
        }
    }

    private static bool TryGetSustainedConsumable(
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

    private static bool MatchesActionType(string actionType, ActionKind kind) => kind switch
    {
        ActionKind.Cultivation => string.Equals(actionType, "cultivation", StringComparison.OrdinalIgnoreCase),
        ActionKind.Exploration => string.Equals(actionType, "exploration", StringComparison.OrdinalIgnoreCase),
        ActionKind.Alchemy => string.Equals(actionType, "alchemy", StringComparison.OrdinalIgnoreCase),
        ActionKind.Combat => string.Equals(actionType, "combat", StringComparison.OrdinalIgnoreCase),
        ActionKind.Travel => string.Equals(actionType, "travel", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static double CultivationMultiplier(GameDataCatalog catalog, string nodeId)
    {
        foreach (var map in catalog.Maps)
            foreach (var node in map.GetProperty("nodes").EnumerateArray())
                if (node.GetProperty("id").GetString() == nodeId)
                    return node.TryGetProperty("cultivationMultiplier", out var multiplier) ? multiplier.GetDouble() : 1;
        return 1;
    }

    private static double ScaleOutput(double quantity, double multiplier)
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
    private static void AddDelta(Dictionary<ItemId, double> deltas, ItemId item, double amount)
    {
        if (!item.IsValid || !double.IsFinite(amount))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源增量无效。");
        var next = deltas.TryGetValue(item, out var current) ? current + amount : amount;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "行动资源累计增量超出数值范围。");
        deltas[item] = next;
    }
}
