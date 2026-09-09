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
        AssetWallet? assetBalances = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (assetBalances is not null && assetBalances.PlayerId != player.PlayerId)
            throw new DomainException(DomainErrorCode.InvalidArgument, "资产余额与模拟玩家不一致。");
        return AdvanceToCore(player, target, gameData.Actions, gameData, assetBalances);
    }

    private static SimulationResult AdvanceToCore(
        PlayerAggregate player,
        DateTimeOffset target,
        ActionCatalog catalog,
        GameDataCatalog? gameData,
        AssetWallet? assetBalances)
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

        while (remainingSeconds > 0 && player.Actions.Count > 0)
        {
            var action = player.Actions[0];
            // 阻塞项是明确的停泊状态。它不能被本次推进重新标记为 Running，
            // 否则战斗失败等不消费时间的结果会在队头空转直到转换上限。
            if (action.Status == ActionQueueStatus.Blocked)
                break;
            if (++transitions > MaxTransitionsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");

            var definition = catalog.Get(action.Intent.ActionId);
            var isActionStart = action.ProgressSeconds <= SecondQuantizationEpsilon
                && player.Progression.SimulationCarrySeconds <= SecondQuantizationEpsilon;
            action.Status = ActionQueueStatus.Running;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                if (gameData is null)
                    throw new DomainException(DomainErrorCode.NotFound, $"行动 {definition.Id} 缺少战斗配表上下文。");
                AdvanceCombatAction(player, action, definition, gameData, from, target, ref remainingSeconds,
                    ref deltas, ref events, ref completedActions, ref completedRepeats);
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
            var needed = Math.Max(0, duration - action.ProgressSeconds);
            var step = Math.Min(remainingSeconds, needed);
            remainingSeconds -= step;
            var wholeSeconds = QuantizeElapsedSeconds(player, step);
            action.ProgressSeconds += wholeSeconds;
            ApplyContinuousReward(player, definition, wholeSeconds, autoUseMultiplier);
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

        player.LastSimulatedAt = target;
        if (remainingSeconds < 0) throw new DomainException(DomainErrorCode.InvariantViolation, "模拟时间出现负余量。");
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
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref List<SimulationEvent>? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        if (string.IsNullOrWhiteSpace(definition.CombatEncounterId)
            || !gameData.CombatEncounters.TryGetValue(definition.CombatEncounterId, out var encounter))
            throw new DomainException(DomainErrorCode.NotFound, $"战斗行动 {definition.Id} 关联的遭遇不存在。");

        var expedition = action.Expedition;
        if (expedition is null)
        {
            expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.InitialSearch);
            action.Expedition = expedition;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.search.started", $"开始搜寻：{encounter.Name}。", definition.Id);
        }

        if (!expedition.IsValid)
            throw new DomainException(DomainErrorCode.InvariantViolation, $"行动 {definition.Id} 的战斗远征状态无效。");

        var phaseRemaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
        if (expedition.Phase is CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search)
        {
            var step = Math.Min(remainingSeconds, phaseRemaining);
            if (step <= 0)
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
            if (!TrySelectMonster(encounter, gameData.CombatMonsters, seed, out var monster))
                throw new DomainException(DomainErrorCode.NotFound, $"遭遇 {encounter.Id} 没有可用敌人。");
            var battle = CombatEncounterState.Start(
                encounter.Id,
                seed,
                CombatantStats.Create(player.Combat.MaxHitpoints, player.Combat.AttackPower, player.Combat.Defense, 2),
                monster.Stats);
            battle.Player.Hitpoints = Math.Clamp(player.Combat.Hitpoints, 0, player.Combat.MaxHitpoints);
            var combatState = ToExpeditionState(battle, updated with
            {
                Phase = CombatExpeditionPhase.Combat,
                PhaseElapsedSeconds = 0,
                PhaseDurationSeconds = 0,
                EnemyId = monster.Id,
                EnemyHitpoints = monster.Stats.MaxHitpoints,
                Outcome = CombatOutcome.Running
            });
            action.ProgressSeconds = 0;
            action.Expedition = combatState;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.started", $"遭遇妖兽：{monster.Name}。", definition.Id);
            return;
        }

        if (expedition.Phase != CombatExpeditionPhase.Combat || string.IsNullOrWhiteSpace(expedition.EnemyId)
            || !gameData.CombatMonsters.TryGetValue(expedition.EnemyId, out var currentMonster))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗远征敌人状态无效。");

        var battleState = CombatEncounterState.Restore(
            encounter.Id,
            expedition.Seed,
            CombatantStats.Create(player.Combat.MaxHitpoints, player.Combat.AttackPower, player.Combat.Defense, 2),
            currentMonster.Stats,
            expedition.BattleElapsedSeconds,
            expedition.PlayerHitpoints,
            expedition.EnemyHitpoints,
            expedition.PlayerNextAttackAtSeconds,
            expedition.EnemyNextAttackAtSeconds,
            expedition.PlayerAttackCount,
            expedition.EnemyAttackCount,
            expedition.Outcome,
            expedition.PlayerAttackCount + expedition.EnemyAttackCount);
        var battleFrom = battleState.ElapsedSeconds;
        var actionElapsedBeforeCombat = (target - from).TotalSeconds - remainingSeconds;
        var battleTo = battleFrom + remainingSeconds;
        var combatResult = DeterministicCombatEngine.AdvanceTo(battleState, battleTo);
        var consumed = Math.Max(0, battleState.ElapsedSeconds - battleFrom);
        remainingSeconds -= consumed;
        player.Combat.Hitpoints = battleState.Player.Hitpoints;
        // 战斗中气血是独立持久化分段；仅标记行动队列会导致重连后回退到旧气血。
        player.MarkDirty(PlayerSectionKind.Combat);
        var updatedBattle = ToExpeditionState(battleState, expedition with
        {
            PhaseElapsedSeconds = Math.Min(expedition.PhaseDurationSeconds, expedition.PhaseElapsedSeconds + consumed),
            BattleElapsedSeconds = battleState.ElapsedSeconds,
            PlayerHitpoints = battleState.Player.Hitpoints,
            EnemyHitpoints = battleState.Enemy.Hitpoints,
            PlayerNextAttackAtSeconds = battleState.Player.NextAttackAtSeconds,
            EnemyNextAttackAtSeconds = battleState.Enemy.NextAttackAtSeconds,
            PlayerAttackCount = battleState.Player.AttackCount,
            EnemyAttackCount = battleState.Enemy.AttackCount,
            Outcome = combatResult.Outcome
        });
        action.Expedition = updatedBattle;
        action.ProgressSeconds = updatedBattle.PhaseElapsedSeconds;
        foreach (var combatEvent in combatResult.Events)
        {
            var attacker = combatEvent.AttackerId == "player" ? "你" : currentMonster.Name;
            var targetName = combatEvent.TargetId == "player" ? "你" : currentMonster.Name;
            var eventAt = from.AddSeconds(actionElapsedBeforeCombat + Math.Max(0, combatEvent.AtSeconds - battleFrom));
            AddCombatEventAt(ref events, eventAt,
                "combat.attack", $"{attacker}{(combatEvent.Hit ? $"命中{targetName}，造成 {combatEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
        }
        if (combatResult.Outcome == CombatOutcome.Running) return;
        if (combatResult.Outcome != CombatOutcome.PlayerWon)
        {
            action.Status = ActionQueueStatus.Blocked;
            AddCombatEvent(ref events, from, target, remainingSeconds, "combat.lost", $"讨伐失败：{currentMonster.Name}。", definition.Id);
            return;
        }

        completedActions += 1;
        completedRepeats += 1;
        action.CompletedRepeats += 1;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        ApplyCombatRewards(player, currentMonster, updatedBattle.Seed, ref deltas);
        AddCombatEvent(ref events, from, target, remainingSeconds, "combat.won", $"讨伐成功：{currentMonster.Name}。", definition.Id);
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
        if (action.RemainingRepeats == 0)
        {
            player.RemoveAction(action.Id, incrementVersion: false);
            return;
        }
        action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search, NextSeed(updatedBattle.Seed));
        action.ProgressSeconds = 0;
    }

    private static CombatExpeditionState CreateSearchState(
        PlayerAggregate player,
        QueuedAction action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        long? seed = null) => new(
        encounter.Id,
        phase,
        0,
        phase == CombatExpeditionPhase.InitialSearch ? encounter.SearchSeconds : encounter.RespawnSeconds,
        seed ?? CreateSeed(encounter.Id, action.CompletedRepeats),
        null,
        0,
        player.Combat.Hitpoints,
        0,
        0,
        0,
        0,
        0,
        CombatOutcome.Running);

    private static CombatExpeditionState ToExpeditionState(CombatEncounterState battle, CombatExpeditionState template) => template with
    {
        BattleElapsedSeconds = battle.ElapsedSeconds,
        PlayerHitpoints = battle.Player.Hitpoints,
        EnemyHitpoints = battle.Enemy.Hitpoints,
        PlayerNextAttackAtSeconds = battle.Player.NextAttackAtSeconds,
        EnemyNextAttackAtSeconds = battle.Enemy.NextAttackAtSeconds,
        PlayerAttackCount = battle.Player.AttackCount,
        EnemyAttackCount = battle.Enemy.AttackCount,
        Outcome = battle.Outcome
    };

    private static bool TrySelectMonster(
        CombatEncounterDefinition encounter,
        IReadOnlyDictionary<string, CombatMonsterDefinition> monsters,
        long seed,
        out CombatMonsterDefinition monster)
    {
        var candidates = encounter.RandomSpawns.Where(spawn => monsters.ContainsKey(spawn.EnemyId) && spawn.Weight > 0).ToArray();
        if (candidates.Length == 0) { monster = null!; return false; }
        var total = candidates.Sum(spawn => spawn.Weight);
        var ticket = (unchecked((ulong)seed) % 1_000_000UL) / 1_000_000d * total;
        foreach (var candidate in candidates)
        {
            if (ticket < candidate.Weight) { monster = monsters[candidate.EnemyId]; return true; }
            ticket -= candidate.Weight;
        }
        monster = monsters[candidates[^1].EnemyId];
        return true;
    }

    private static void ApplyCombatRewards(PlayerAggregate player, CombatMonsterDefinition monster, long seed, ref Dictionary<ItemId, double>? deltas)
    {
        if (monster.DropTable.Count == 0) return;
        foreach (var drop in monster.DropTable)
        {
            if (!drop.ItemId.IsValid || drop.MinQuantity <= 0 || drop.MaxQuantity < drop.MinQuantity || drop.DropChance <= 0) continue;
            var roll = (unchecked((ulong)NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
            seed = NextSeed(seed);
            if (roll > drop.DropChance) continue;
            var quantity = Math.Floor(drop.MinQuantity + (drop.MaxQuantity - drop.MinQuantity) * roll);
            AddDelta(deltas ??= new Dictionary<ItemId, double>(), drop.ItemId, Math.Max(drop.MinQuantity, quantity));
        }
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
    /// 结算行动持续产生的被动收益（当前仅包括修炼产生的灵力）。
    /// 逐整秒循环累加，严格确保大跨度离线快进与客户端逐秒在线推进的 IEEE 754 浮点累加序列完全一致。
    /// </summary>
    private static void ApplyContinuousReward(PlayerAggregate player, ActionDefinition definition, long wholeSeconds, double multiplier)
    {
        if (wholeSeconds <= 0) return;
        // 溢出预检查沿用“先校验后变更”语义：rate * 整秒数越界时不得污染任何进度数值。
        var seconds = (double)wholeSeconds;
        if (definition.QiPerSecond > 0)
        {
            var rate = definition.QiPerSecond * multiplier;
            var total = rate * seconds;
            if (!double.IsFinite(rate) || !double.IsFinite(total) || !double.IsFinite(player.Progression.Qi + total) || !double.IsFinite(player.Progression.LifetimeQi + total))
                throw new DomainException(DomainErrorCode.InvariantViolation, "气运奖励超出数值范围。");
            // 逐整秒累加，与在线逐秒推进保持完全相同的浮点操作序列。
            for (var i = 0; i < wholeSeconds; i++)
            {
                player.Progression.Qi += rate;
                player.Progression.LifetimeQi += rate;
            }
        }
        if (!double.IsFinite(player.Progression.Qi) || !double.IsFinite(player.Progression.LifetimeQi))
            throw new DomainException(DomainErrorCode.InvariantViolation, "模拟后的玩家进度数值无效。");
    }

    /// <summary>
    /// 单轮行动耗时走满时的结算处理。
    /// 发放单轮经验、扣除输入材料、发放输出产物、扣减剩余次数，若次数归零则移出队列；
    /// 若为 -1（无限重复）则绝对保留队列项供下一轮循环。
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
        var experienceGain = definition.ExperiencePerRepeat * autoUseMultiplier;
        var nextExperience = player.Progression.Experience + experienceGain;
        if (!double.IsFinite(experienceGain) || !double.IsFinite(nextExperience))
            throw new DomainException(DomainErrorCode.InvariantViolation, "经验奖励超出数值范围。");
        action.ProgressSeconds = 0;
        action.CompletedRepeats += 1;
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
        completedActions += 1;
        completedRepeats += 1;
        if (definition.Outputs.Count > 0 || definition.Inputs.Count > 0)
        {
            deltas ??= new Dictionary<ItemId, double>();
            foreach (var (item, quantity) in definition.Outputs)
                AddDelta(deltas, item, ScaleOutput(quantity, autoUseMultiplier));
            foreach (var (item, quantity) in definition.Inputs) AddDelta(deltas, item, -quantity);
        }
        CompleteSustainedAutoUse(player, definition, gameData, target, ref events);
        player.Progression.Experience = nextExperience;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        events ??= new List<SimulationEvent>();
        events.Add(new SimulationEvent(
            target,
            "action.completed",
            definition.Name,
            definition.Id,
            definition.Outputs));
        // -1 表示无限行动：每轮完成后保留队列项并继续下一轮；仅在严格递减至 0 时出队。
        if (action.RemainingRepeats == 0)
        {
            player.RemoveAction(action.Id, incrementVersion: false);
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
