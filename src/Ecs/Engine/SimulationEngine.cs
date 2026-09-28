using System.Text.Json;
using System.Collections.ObjectModel;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;

namespace DaoHua.Ecs;

/// <summary>
/// 战斗搜寻完成后进入本波战斗的共享准备结果。
/// 该原语只负责一次入场边界，返回 ECS 需要挂载的完整战斗状态、远征快照、
/// 资产增量和按原序生成的领域事件；调用方仍负责把状态写入自己的单写者实体或聚合。
/// </summary>
public sealed record CombatEntryPreparation(
    CombatEncounterState? Battle,
    CombatExpeditionState Expedition,
    IReadOnlyDictionary<ItemId, double> AssetDeltas,
    IReadOnlyList<SimulationEvent> Events,
    bool Blocked);

/// <summary>
/// 角色行为与挂机离线收益的唯一确定性快进入口引擎。
/// 核心设计原则：
/// 1. 离线挂机和在线逐秒推进的计算结果必须位级一致（Bitwise Deterministic）；
/// 2. 算法按“行动完成边界”跳步推进，杜绝按离线时间无脑逐秒生成历史事件；
/// 3. 行动队列中 RemainingRepeats == -1 为无限循环，每轮结算后绝不出队；
/// 4. 持续灵力奖励通过整秒量化余数（SimulationCarrySeconds）精确进位，经验仅在单轮完成时结算。
/// </summary>
public static partial class SimulationEngine
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
    /// 模拟事件有界尾窗：产出顺序与朴素 List.Add 完全一致，驻留条数封顶 2×TailCapacity；
    /// 每越过水位整段折叠最旧的 TailCapacity 条并计入 Dropped（摊销搬迁成本）。
    /// 长跨度离线补算（48h 战斗可产十万级事件）下事件内存恒有界，保留段恒为产出流的连续后缀。
    /// </summary>
    internal sealed class SimulationEventTail
    {
        /// <summary>尾窗保底保留条数：下游日志消费只取末尾 200 条，256 条留出余量。</summary>
        public const int TailCapacity = 256;
        /// <summary>触发整段裁剪的水位（2×容量），摊销 RemoveRange 搬迁成本。</summary>
        private const int TrimThreshold = TailCapacity * 2;

        private readonly List<SimulationEvent> _events = [];

        /// <summary>被折叠丢弃的事件总数（含并入子结果时带入的丢弃计数）。</summary>
        public long Dropped { get; private set; }
        /// <summary>当前驻留事件数。</summary>
        public int Count => _events.Count;
        /// <summary>驻留事件视图；调用方追加完成后直接作为结果列表，不再回写。</summary>
        public IReadOnlyList<SimulationEvent> View => _events;

        /// <summary>按原追加语义推入一条事件；越过水位时先整段折叠最旧的 TailCapacity 条。</summary>
        public void Add(SimulationEvent entry)
        {
            if (_events.Count == TrimThreshold)
            {
                _events.RemoveRange(0, TailCapacity);
                Dropped += TailCapacity;
            }
            _events.Add(entry);
        }

        /// <summary>并入子推进结果：其保留段逐条推入，其丢弃计数直接汇总（丢弃段均早于保留段）。</summary>
        public void Merge(SimulationResult inner)
        {
            Dropped += inner.DroppedEventCount;
            var innerEvents = inner.Events;
            for (var i = 0; i < innerEvents.Count; i++) Add(innerEvents[i]);
        }

        /// <summary>合并流中被尾窗裁剪、从未构造实体的事件数直接计入丢弃（不占用列表空间）。</summary>
        public void Fold(long count) => Dropped += count;
    }

    /// <summary>
    /// 外层推进循环内跨步复用的进行中战斗会话：同一行动的 Combat 相位在相邻外层步之间
    /// 直接续用 <see cref="CombatEncounterState"/> 内存态，不再每步对整场战斗做
    /// Restore（模板/单位/快照校验重建）与 ToExpeditionState（玩家+敌人快照数组）拆装。
    /// 会话仅在单次 AdvanceTo 调用内有效：期间只把标量镜像字段写回行动文档，
    /// 完整单位快照在 Flush（补算收尾、消耗品触发器读文档、会话被顶替）时统一回写，
    /// 持久化文档与逐位重建口径保持一致。
    /// </summary>
    private sealed class LiveCombatBattle
    {
        /// <summary>当前存活会话；同一时刻至多一场复用战斗，错位会话由守卫自动失配。</summary>
        public Entry? Current;

        /// <summary>一场进行中战斗的复用句柄。</summary>
        public sealed class Entry
        {
            /// <summary>战斗所属行动实例。</summary>
            public required QueuedAction Action { get; init; }
            /// <summary>内存态战斗上下文。</summary>
            public required CombatEncounterState Battle { get; init; }
            /// <summary>本场战斗所属波次号（匹配守卫的一部分）。</summary>
            public required int WaveNumber { get; init; }
            /// <summary>最近一次刷新玩家战斗面板时的属性输入修订号；未变则跳过本步面板刷新。</summary>
            public long AttributeRevision { get; set; }
        }

        /// <summary>
        /// 判定行动当前文档仍指向本会话战斗：同行动实例、同遭遇/种子/波次，
        /// 且文档战斗时钟与内存态一致（文档被外部替换或回写漂移时自动失配，回退全量装配）。
        /// </summary>
        private static bool Matches(Entry entry, QueuedAction action, CombatExpeditionState? expedition)
            => ReferenceEquals(entry.Action, action)
                && expedition is { Phase: CombatExpeditionPhase.Combat }
                && expedition.EncounterId == entry.Battle.EncounterId
                && expedition.Seed == entry.Battle.Seed
                && expedition.WaveNumber == entry.WaveNumber
                && expedition.BattleElapsedSeconds == entry.Battle.ElapsedSeconds;

        /// <summary>命中复用时把会话交给调用方；失配返回 false 由调用方走全量装配。</summary>
        public bool TryReuse(QueuedAction action, CombatExpeditionState? expedition, out Entry? entry)
        {
            var current = Current;
            if (current is not null && Matches(current, action, expedition))
            {
                entry = current;
                return true;
            }
            entry = null;
            return false;
        }

        /// <summary>
        /// 登记一场新建/新装配的战斗为复用会话；被顶替的旧会话先按其行动现文档全量回写，
        /// 防止离开队首的行动滞留仅标量同步过的文档逃逸进持久化快照。
        /// </summary>
        public void Adopt(QueuedAction action, CombatEncounterState battle, CombatExpeditionState expedition,
            long attributeRevision)
        {
            Flush();
            Current = new Entry
            {
                Action = action,
                Battle = battle,
                WaveNumber = expedition.WaveNumber,
                AttributeRevision = attributeRevision
            };
        }

        /// <summary>会话不再是当前复用对象时丢弃引用（战斗终局、相位离开等）。</summary>
        public void Release(QueuedAction action)
        {
            if (Current is { } current && ReferenceEquals(current.Action, action)) Current = null;
        }

        /// <summary>
        /// 把内存态战斗全量回写为持久化文档（含玩家/敌人完整快照）。仅当行动文档仍指向
        /// 本会话战斗时才覆盖；镜像气血/灵力以文档现值为准——脱战相位（自然回复、
        /// 即时消耗品）可能已在战斗步之外改写镜像，不能用水位内值覆盖回去。
        /// </summary>
        public void Flush()
        {
            var current = Current;
            if (current is null) return;
            var doc = current.Action.Expedition;
            if (!Matches(current, current.Action, doc)) return;
            var exported = CaptureCombatState(current.Battle, doc!);
            current.Action.Expedition = exported with
            {
                PlayerHitpoints = doc!.PlayerHitpoints,
                PlayerManapoints = doc.PlayerManapoints
            };
        }
    }

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
    /// <param name="player">角色聚合实例（就地推进）。</param>
    /// <param name="target">推进截止目标时刻。</param>
    /// <param name="gameData">全局配表。</param>
    /// <param name="assetBalances">玩家资产钱包快照（材料判定与记账依据）；为 null 时按无库存处理，有消耗的行动将被阻塞。</param>
    /// <param name="duel">斗法撮合选项；离线补算必须传 null 或不启用匹配以保持确定性。</param>
    /// <returns>包含资产变动与事件明细的模拟结算结果。</returns>
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

    /// <summary>
    /// 一次推进请求的状态版本提交窗口。ECS 战斗系统可按领域自然边界记录 Core
    /// 变更，最后一次性提交与 OOP 外层相同的版本增量。
    /// </summary>
    public sealed class StateVersionCommitSession
    {
        private bool _requestChanged;
        private bool _coreWindowChanged;
        private int _coreVersionCommits;
        private bool _committed;

        /// <summary>记录一个 Core 边界；到达自然边界时才产生一次 Core 版本提交。</summary>
        public void RecordCoreStep(bool changed, bool endsNaturalBoundary)
        {
            ObjectDisposedException.ThrowIf(_committed, this);
            _requestChanged |= changed;
            _coreWindowChanged |= changed;
            if (!endsNaturalBoundary) return;
            if (_coreWindowChanged) _coreVersionCommits = checked(_coreVersionCommits + 1);
            _coreWindowChanged = false;
        }

        /// <summary>记录请求外层发生可见变化；只在最终提交时增加一次外层版本。</summary>
        public void MarkRequestChanged(bool changed)
        {
            ObjectDisposedException.ThrowIf(_committed, this);
            _requestChanged |= changed;
        }

        /// <summary>待提交的 Core 次数，供宿主诊断，不允许由宿主自行改写版本。</summary>
        public int PendingCoreCommits => _coreVersionCommits;

        /// <summary>提交累计增量并标记完成；重复提交会快速失败。</summary>
        public long Commit(IPlayerResourceContext player)
        {
            ObjectDisposedException.ThrowIf(_committed, this);
            ArgumentNullException.ThrowIfNull(player);
            var delta = checked((long)_coreVersionCommits + (_requestChanged ? 1L : 0L));
            if (delta > 0) player.StateVersion = checked(player.StateVersion + delta);
            _committed = true;
            return delta;
        }
    }

    /// <summary>创建一次与 OOP AdvanceTo 对齐的版本提交窗口。</summary>
    public static StateVersionCommitSession BeginStateVersionCommitSession() => new();

    /// <summary>
    /// 为 ECS 战斗行动解析本轮应进入的遭遇。该方法只读取玩家世界位置、行动定义和配表，
    /// 不推进时间、不创建战斗实体，也不修改玩家聚合；ECS 可在自己的实体系统中使用同一
    /// 节点生态选择与确定性种子语义，而不必调用完整的 OOP 快进引擎。
    /// </summary>
    public static CombatEncounterDefinition ResolveCombatEncounterForAction(
        IPlayerResourceContext player,
        ActionDefinition definition,
        double completedRepeats,
        GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(gameData);
        if (!double.IsFinite(completedRepeats) || completedRepeats < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗行动完成轮次无效。");

        var encounterId = definition.CombatEncounterId;
        if (string.IsNullOrWhiteSpace(player.World.EdgeId)
            && gameData.WorldGraph.TryGetNode(player.World.NodeId, out var node)
            && node.EncounterIds.Count > 0)
        {
            var pool = node.EncounterIds;
            var index = pool.Count == 1
                ? 0
                : (int)((ulong)CreateSeed(definition.Id.Value, completedRepeats) % (ulong)pool.Count);
            encounterId = pool[index];
        }
        if (string.IsNullOrWhiteSpace(encounterId)
            || !gameData.CombatEncounters.TryGetValue(encounterId, out var encounter))
            throw new DomainException(DomainErrorCode.NotFound,
                $"战斗行动 {definition.Id} 在节点 {player.World.NodeId} 无可用遭遇。");
        return encounter;
    }

    /// <summary>按领域行动实例解析遭遇的便捷重载；不会推进行动或修改玩家状态。</summary>
    public static CombatEncounterDefinition ResolveCombatEncounter(
        PlayerAggregate player,
        QueuedAction action,
        ActionDefinition definition,
        GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ResolveCombatEncounterForAction(player, definition, action.CompletedRepeats, gameData);
    }

    /// <summary>
    /// 创建战斗行动的搜寻阶段快照。该原语只生成可持久化的阶段数据，供 ECS 的搜寻系统
    /// 作为实体组件状态写入；战斗实体创建、边界推进和奖励结算仍由各自 ECS 系统负责。
    /// </summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        double completedRepeats,
        long? seed = null,
        int waveNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(gameData);
        if (phase is not (CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻阶段无效。");
        if (!double.IsFinite(completedRepeats) || completedRepeats < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗行动完成轮次无效。");
        if (waveNumber < 1)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗波次无效。");

        // 首波使用遭遇固定搜寻读条；波间搜寻使用玩家当前寻敌速度修正。
        var delayFactor = player.ResolveAttributes(gameData).SearchDelayFactor;
        var duration = phase == CombatExpeditionPhase.InitialSearch
            ? encounter.SearchSeconds
            : encounter.RespawnSeconds * delayFactor;
        if (!double.IsFinite(duration) || duration <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻时长无效。");
        return new CombatExpeditionState(
            encounter.Id,
            phase,
            0,
            duration,
            seed ?? CreateSeed(encounter.Id, completedRepeats),
            0,
            player.Resources.Hitpoints,
            player.Resources.Manapoints,
            0,
            0,
            0,
            CombatOutcome.Running,
            waveNumber,
            null);
    }

    /// <summary>按领域行动实例创建搜寻阶段的便捷重载；不会推进行动或修改玩家状态。</summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player,
        IPlayerActionState action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog gameData,
        long? seed = null,
        int waveNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(action);
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber);
    }

    /// <summary>
    /// 把已完成的搜寻阶段转换为真实战斗入场状态。
    /// OOP 快进和 ECS CombatActionSystem 必须共用此原语，避免一侧只投影搜寻状态而跳过
    /// 波次选择、技能装配、战外 Buff、持续消耗品、战斗账本和入场日志。
    /// </summary>
    public static CombatEntryPreparation PrepareCombatEntry(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        CombatEncounterDefinition encounter,
        CombatExpeditionState completedSearch,
        GameDataCatalog gameData,
        AssetWallet? assetBalances,
        DateTimeOffset entryAt)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(completedSearch);
        ArgumentNullException.ThrowIfNull(gameData);
        if (completedSearch.Phase is not (CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search)
            || (!completedSearch.DuelSeekFailed
                && completedSearch.PhaseElapsedSeconds + 1e-9 < completedSearch.PhaseDurationSeconds))
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻尚未完成，不能进入战斗。");
        var wave = EncounterWaveSelector.Select(encounter, gameData.CombatMonsters,
            completedSearch.Seed, completedSearch.WaveNumber);
        var battle = CombatEncounterState.Create(
            encounter.Id,
            completedSearch.Seed,
            new CombatantTemplate(player.ResolveAttributes(gameData).ToCombatant(),
                SkillSlot.FromAbilitySlots(player.Loadout.AbilitySlots, gameData.Abilities)),
            wave.Select(monster => new CombatantTemplate(monster.Stats, monster.Skills)).ToArray(),
            InBattleRespawnSeconds(encounter));
        for (var i = 0; i < wave.Count; i++) battle.Enemies[i].DefinitionId = wave[i].Id;
        battle.Player.Hitpoints = Math.Clamp(player.Resources.Hitpoints, 0, player.Resources.MaxHitpoints);
        battle.Player.Manapoints = Math.Clamp(player.Resources.Manapoints, 0, player.Resources.MaxManapoints);
        battle.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(player.Buffs.Active, buffId);
        player.Combat.Skills.ApplyTo(battle.Player);

        Dictionary<ItemId, double>? deltas = null;
        SimulationEventTail? events = null;
        var waveNames = string.Join("、", wave.Select(monster => monster.Name));
        if (battle.Player.Hitpoints <= 0)
        {
            action.Status = ActionQueueStatus.Blocked;
            action.Expedition = completedSearch;
            AddCombatEventAt(ref events, entryAt, "combat.lost", $"气血衰竭，无法迎战：{waveNames}。", definition.Id);
            if (player.Settings.LoopEnabled && player.Actions.Count > 1 && !AllActionsBlocked(player.Actions))
                player.RotateActionToTail(action.Id);
            return new CombatEntryPreparation(null, completedSearch,
                deltas ?? new Dictionary<ItemId, double>(), events?.View ?? [], true);
        }

        var atUnix = entryAt.ToUnixTimeMilliseconds() / 1000d;
        RechargeCombatSustainedSlots(player, gameData, assetBalances, atUnix, action.Id, battle,
            ref deltas, ref events);
        InjectSustainedCombatBuffs(player, gameData, battle);
        DeterministicCombatEngine.DecideInitialActions(battle);
        var combatState = CaptureCombatState(battle, completedSearch with
        {
            Phase = CombatExpeditionPhase.Combat,
            PhaseElapsedSeconds = 0,
            PhaseDurationSeconds = 0,
            Outcome = CombatOutcome.Running,
            DuelSeekFailed = false
        });
        action.ProgressSeconds = 0;
        action.Expedition = combatState;
        player.RecordStats(action.Id, atUnix, report => report.Encounters += 1);
        AddCombatEventAt(ref events, entryAt, "combat.started", $"遭遇妖兽：{waveNames}。", definition.Id);
        return new CombatEntryPreparation(battle, combatState,
            deltas ?? new Dictionary<ItemId, double>(), events?.View ?? [], false);
    }

    /// <summary>
    /// 确保行动统计账本已为当前实例建立。ECS 的战斗准备系统在创建首个搜寻阶段时调用，
    /// 以保留 OOP 推进入口相同的 Duel.CurrentReport/ActiveReports 生命周期；方法只修改
    /// 实际玩家分段，不推进时间，也不生成战斗事件。
    /// </summary>
    public static void EnsureActionReportForAction(
        IPlayerResourceContext player,
        Guid actionInstanceId,
        ActionDefinition definition,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(definition);
        if (actionInstanceId == Guid.Empty)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识不能为空。");
        player.EnsureActionReport(actionInstanceId, definition.Id, definition.Name, now);
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
        var events = new SimulationEventTail();
        double actions = 0, repeats = 0;
        var changed = false;
        // 进行中战斗的跨步复用会话：仅本次调用内有效，结束时统一 Flush 回写完整快照。
        var live = new LiveCombatBattle();
        // 离线/挂起补算收益上限 48 小时：超出部分只推进模拟时钟与过期清理，不再产出行动收益。
        var capAt = from.AddHours(48);
        var computeTarget = target > capAt ? capAt : target;
        try
        {
        while (player.LastSimulatedAt < computeTarget)
        {
            using var windowMeasurement = RuntimeInstrumentation.MeasureCurrent("domain.logic.resource_window");
            var now = player.LastSimulatedAt;
            var resourceBoundary = PrepareResourceBoundaryCore(player, gameData, wallet,
                computeTarget, events, live, out var instantDeltas);
            changed |= resourceBoundary.Changed;
            foreach (var delta in instantDeltas)
            {
                wallet.Apply(delta);
                deltas[delta.Asset] = deltas.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
            }
            var step = resourceBoundary.StepSeconds;
            var result = AdvanceToCore(player, now.AddSeconds(step), gameData.Actions, gameData, wallet, duel, live);
            // 无暂存物品时整段跳过：SettleStagedItems 空表恒返回空，标脏只在真实结算时发生。
            if (player.Duel.StagedItems.Count > 0)
            {
                var stagedSettled = SettleResourceItems(player, now.AddSeconds(step).ToUnixTimeMilliseconds() / 1000d,
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
            }
            changed |= AdvanceIdleSkillResources(player, step);
            foreach (var delta in result.AssetDeltas)
            {
                wallet.Apply(delta);
                deltas[delta.Asset] = deltas.GetValueOrDefault(delta.Asset) + delta.QuantityDelta;
            }
            events.Merge(result);
            actions += result.CompletedActions;
            repeats += result.CompletedRepeats;
            changed |= result.Changed;
            changed |= CompleteResourceRegeneration(player, gameData, step);
        }
        }
        finally
        {
            // 复用会话收尾：把仍在进行的战斗完整快照回写行动文档，保证 ExportState/分段落盘
            // 与外部改动后下一次补算看到的远征状态与逐步拆装口径逐位一致；异常路径同样回写。
            live.Flush();
        }
        if (player.LastSimulatedAt < target)
        {
            // 跳过 48 小时上限外的尾段时间：清理过期增益并把时钟推进到真实目标，不结算收益。
            var expired = player.Buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= target.ToUnixTimeMilliseconds() / 1000d);
            if (expired > 0) { player.MarkDirty(PlayerSectionKind.Buffs); changed = true; }
            if (!player.Combat.Skills.IsEmpty)
            {
                // 超上限丢弃技能时钟会改变 combat 分段内容：必须标脏，保证增量推送与刷盘不漏。
                // 语义判定：反序列化还原的空技能态与 Empty 单例引用不等，!= Empty 会误标脏。
                player.Combat.Skills = PlayerCombatSkillState.Empty;
                player.MarkDirty(PlayerSectionKind.Combat);
            }
            player.LastSimulatedAt = target;
            changed = true;
            events.Add(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        }
        if (changed) player.StateVersion = checked(player.StateVersion + 1);
        return new(player.PlayerId, from, target, (target - from).TotalSeconds, actions, repeats,
            deltas.Where(pair => pair.Value != 0).Select(pair => new AssetDelta(player.PlayerId, pair.Key, pair.Value, "simulation")).ToArray(),
            events.View, player.StateVersion, changed, events.Dropped);
    }

    private static List<AssetDelta> ActivateInstantConsumables(IPlayerResourceContext player,
        GameDataCatalog catalog, AssetWallet wallet, DateTimeOffset at, SimulationEventTail events,
        LiveCombatBattle? live = null)
    {
        if (player.Actions.Count == 0 || player.Resources.Hitpoints <= 0) return [];
        // 无任何启用槽位时整段跳过：免每步 catalog.Actions.Get 与 MatchesActionType 固定税。
        var slots = player.Settings.AutoUseSlots;
        var anyEnabled = false;
        for (var i = 0; i < slots.Count; i++)
            if (slots[i].Enabled) { anyEnabled = true; break; }
        if (!anyEnabled) return [];
        var kind = catalog.Actions.Get(player.Actions[0].Intent.ActionId).Kind;
        List<AssetDelta>? deltas = null;
        var now = at.ToUnixTimeMilliseconds() / 1000d;
        // 触发求值上下文惰性构建：仅当存在带条件的消耗品时才装配战斗单位视图。
        CombatantState? evalCaster = null;
        CombatantState? evalTarget = null;
        List<CombatantState>? evalAllies = null;
        List<CombatantState>? evalEnemies = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null || !MatchesActionType(slot.ActionType, kind)
                || slot.CooldownUntilUnixSeconds > now) continue;
            var id = new ItemId(slot.ItemId);
            var item = catalog.Items[id];
            if (item.Consumable is not { IsContinuous: false } definition
                || !definition.UsableInActionKinds.Contains(kind)
                || !catalog.RawItems[id.Value].TryGetProperty("consumable", out var raw)) continue;
            // 消耗品使用条件与技能共用同一套触发判定模型；求值失败一律跳过本槽。
            if (definition.Triggers is { Count: > 0 } triggers)
            {
                if (evalCaster is null)
                {
                    // 触发器求值读取行动文档中的敌人快照：复用会话需先把内存态全量回写。
                    live?.Flush();
                    evalCaster = BuildConsumableEvalUnits(player, catalog, out evalTarget, out evalAllies, out evalEnemies);
                }
                if (!Domain.Combat.SkillTriggerEvaluator.MatchesAll(triggers, evalCaster, evalTarget,
                        evalAllies!, evalEnemies!)) continue;
            }
            // 已记账扣件汇总按原列表顺序逐条累加（与 LINQ Where+Sum 同序，位级一致）。
            var consumed = 0d;
            if (deltas is not null)
                for (var j = 0; j < deltas.Count; j++)
                    if (deltas[j].Asset.ItemId == id) consumed += deltas[j].QuantityDelta;
            if (wallet.Get(new AssetKey(id)) + consumed < 1) continue;
            double Read(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.GetDouble() : 0;
            var hp = Read(raw, "hitpointRestore");
            var mp = Read(raw, "manapointRestore");
            var cooldown = Read(raw, "cooldownSeconds");
            if (!raw.TryGetProperty("buffs", out var buffs) && hp == 0 && mp == 0) continue;
            if (cooldown <= 0) throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
            player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + hp);
            player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + mp);
            if (buffs.ValueKind == JsonValueKind.Array)
                foreach (var buff in buffs.EnumerateArray())
                {
                    var buffId = buff.GetProperty("buffId").GetString()!;
                    player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
                    player.Buffs.Active.Add(new(buffId, now + Read(buff, "durationSeconds"), 1,
                        buff.GetProperty("name").GetString()!, id.Value, Read(buff, "hpRegen"), Read(buff, "mpRegen"),
                        Read(buff, "attackSpeedPoints"), Read(buff, "damageAbsorptionPoints")));
                }
            player.Settings.AutoUseSlots[i] = slot with { CooldownUntilUnixSeconds = now + cooldown };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Buffs);
            player.MarkDirty(PlayerSectionKind.Combat);
            (deltas ??= []).Add(new AssetDelta(player.PlayerId, new AssetKey(id), -1, "auto-use"));
            // 即时消耗品扣件计入队首行动账本「失去」物品。
            player.RecordStats(player.Actions[0].Id, now, report => report.AddItemLost(id, 1));
            events.Add(new SimulationEvent(at, "auto-use.activated", $"自动使用 {item.Name}。", ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
        }
        return deltas ?? [];
    }

    /// <summary>
    /// 为消耗品触发判定装配求值单位视图：施放方为当前玩家快照；战斗进行中敌方取
    /// 当前波存活单位，「目标」对象按当前有效威胁最高者确定性解析（非随机抽取）。
    /// </summary>
    private static CombatantState BuildConsumableEvalUnits(IPlayerResourceContext player, GameDataCatalog catalog,
        out CombatantState? target, out List<CombatantState> allies, out List<CombatantState> enemies)
    {
        var caster = new CombatantState("player", player.ResolveAttributes(catalog).ToCombatant())
        {
            Hitpoints = player.Resources.Hitpoints,
            Manapoints = player.Resources.Manapoints
        };
        var activeBuffs = player.Buffs.Active;
        caster.ExternalBuffStacks = buffId => SumActiveBuffStacks(activeBuffs, buffId);
        allies = [caster];
        enemies = [];
        if (player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat, Enemies: { Count: > 0 } waveEnemies })
            for (var i = 0; i < waveEnemies.Count; i++)
            {
                var enemy = waveEnemies[i];
                if (!catalog.CombatMonsters.TryGetValue(enemy.EnemyId, out var monster)) continue;
                var unit = new CombatantState($"enemy:{i}", monster.Stats)
                {
                    Hitpoints = enemy.State.Hitpoints,
                    Manapoints = enemy.State.Manapoints,
                    Threat = enemy.State.Threat,
                    ThreatBonusPoints = enemy.State.ThreatBonusPoints
                };
                if (enemy.State.Buffs is { Count: > 0 } buffs) unit.Buffs.AddRange(buffs);
                enemies.Add(unit);
            }
        // 目标 = 存活敌中有效威胁最高者（MaxBy 语义：严格更大才替换，平局保留先见者）。
        CombatantState? best = null;
        for (var i = 0; i < enemies.Count; i++)
            if (enemies[i].IsAlive && (best is null || enemies[i].EffectiveThreat > best.EffectiveThreat))
                best = enemies[i];
        target = best;
        return caster;
    }

    private static SimulationResult AdvanceToCore(
        PlayerAggregate player,
        DateTimeOffset target,
        ActionCatalog catalog,
        GameDataCatalog? gameData,
        AssetWallet? assetBalances,
        DuelAdvanceOptions? duel = null,
        LiveCombatBattle? live = null)
    {
        using var dispatchMeasurement = RuntimeInstrumentation.MeasureCurrent("domain.logic.action_dispatch");
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
        SimulationEventTail? events = null;

        var blockedActions = new HashSet<Guid>();
        // 同一行动 ID 在转换循环中反复出现：按 actionId 缓存最近一次查表结果，跳过重复字典查找。
        ActionId lastActionId = default;
        ActionDefinition? lastDefinition = null;
        // 余量按浮点可表达性收尾：战斗时钟以秒级 double 推进，1e-9 以下的残差在 battleFrom + step
        // 回加时会被 ULP 吸收成零消耗，使循环永远不消耗时间而触顶转换上限。
        while (remainingSeconds > 1e-9 && player.Actions.Count > 0)
        {
            var action = player.Actions[0];
            // 阻塞项是明确的停泊状态。它不能被本次推进重新标记为 Running，
            // 否则战斗失败等不消费时间的结果会在队头空转直到转换上限。
            if (action.Status == ActionQueueStatus.Blocked)
            {
                if (!player.Settings.LoopEnabled)
                {
                    player.RemoveAction(action.Id, incrementVersion: false,
                        endedAtUnixSeconds: from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d);
                    transitions++;
                    continue;
                }
                if (player.Settings.LoopEnabled && player.Actions.Count > 1)
                {
                    if (AllActionsBlocked(player.Actions))
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
                throw new DomainException(DomainErrorCode.InvariantViolation,
                    $"单次模拟超过最大状态转换数（action={action.Intent.ActionId} phase={action.Expedition?.Phase} remaining={remainingSeconds:0.######}）。");

            var actionId = action.Intent.ActionId;
            if (lastDefinition is null || lastActionId != actionId)
            {
                lastDefinition = catalog.Get(actionId);
                lastActionId = actionId;
            }
            var definition = lastDefinition;
            var currentAt = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
            if (!TryStartActionCore(player, action, definition, gameData, assetBalances, currentAt, deltas, ref events))
                continue;
            var isActionStart = action.ProgressSeconds <= SecondQuantizationEpsilon;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                if (gameData is null)
                    throw new DomainException(DomainErrorCode.NotFound, $"行动 {definition.Id} 缺少战斗配表上下文。");
                AdvanceCombatAction(player, action, definition, gameData, assetBalances, duel, from, target,
                    ref remainingSeconds, ref deltas, ref events, ref completedActions, ref completedRepeats, live);
                continue;
            }
            if (definition.ExecutionKind == ActionExecutionKind.Travel)
            {
                AdvanceTravelAction(player, action, definition, gameData, from, target, ref remainingSeconds,
                    ref events, ref completedActions, ref completedRepeats);
                continue;
            }
            var duration = TimedActionDuration(player, definition);
            if (duration <= 0)
            {
                // 开发期容错自愈：Enqueue 拒绝“-1 且时长≤0”，但配表热更可能把存量行动时长改为 0，
                // 队头空转会耗尽转换上限并让玩家永久卡死。此处移出失效行动并留事件证据，继续推进后续行动。
                player.RemoveAction(action.Id, incrementVersion: false,
                    endedAtUnixSeconds: from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d);
                events ??= new SimulationEventTail();
                events.Add(new SimulationEvent(
                    from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                    "action.invalid-dropped",
                    $"行动 {definition.Name} 的时长配置无效（{duration:0.######} 秒），已自动移出队列。",
                    definition.Id));
                continue;
            }

            var actionAt = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
            var autoUseMultiplier = PrepareTimedActionCore(player, action, definition,
                isActionStart, gameData, assetBalances, actionAt, ref deltas, ref events);
            var needed = Math.Max(0, duration - action.ProgressSeconds);
            var step = Math.Min(remainingSeconds, needed);
            remainingSeconds -= step;
            AdvanceTimedAction(player, action, definition, autoUseMultiplier, gameData, step,
                from.AddSeconds((target - from).TotalSeconds - remainingSeconds));
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
            // transitions==0（零耗时推进或本轮未完成任何转移）时此处是队首状态唯一变更点，
            // 必须补标 ActionQueue，保证增量触及差集与刷盘不漏状态翻转。
            if (transitions == 0) player.MarkDirty(PlayerSectionKind.ActionQueue);
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
            events?.View ?? [],
            player.StateVersion,
            transitions > 0,
            events?.Dropped ?? 0);
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
        AssetWallet? assetBalances,
        DuelAdvanceOptions? duel,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats,
        LiveCombatBattle? live = null)
    {
        using var dispatchMeasurement = RuntimeInstrumentation.MeasureCurrent("domain.logic.combat_dispatch");
        if (action.Expedition?.Phase != CombatExpeditionPhase.Combat)
        {
            var prepared = AdvanceCombatPreparationCore(player, action, definition, gameData, assetBalances,
                duel, from, target, ref remainingSeconds, ref deltas, ref events);
            if (prepared is not null) live?.Adopt(action, prepared, action.Expedition!, player.AttributeInputRevision);
            return;
        }
        var expedition = action.Expedition;
        if (!gameData.CombatEncounters.TryGetValue(expedition.EncounterId, out var encounter))
            throw new DomainException(DomainErrorCode.NotFound, $"远征状态引用的遭遇 {expedition.EncounterId} 不存在。");
        if (expedition.Phase != CombatExpeditionPhase.Combat
            || expedition.Enemies is not { Count: > 0 } waveEnemies)
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗远征敌人状态无效。");
        // 敌人字典查找展开为显式循环：每场战斗一次、语义与原来的 Any 短路一致。
        for (var i = 0; i < waveEnemies.Count; i++)
            if (!gameData.CombatMonsters.ContainsKey(waveEnemies[i].EnemyId))
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗远征敌人状态无效。");

        var activeBuffs = player.Buffs.Active;
        CombatEncounterState battleState;
        if (live is not null && live.TryReuse(action, expedition, out var session))
        {
            // 同一场进行中的战斗跨步续用内存态：仅当属性输入修订号变化（Buff 到期、
            // 消耗品生效、六维经验等）时把最新结算面板换入存活单位，与每步 Restore 的
            // 模板刷新语义逐位一致；BaseAttributes 旁路目录不经修订号覆盖，逐次重算。
            battleState = session!.Battle;
            if (gameData.BaseAttributes is null || session.AttributeRevision != player.AttributeInputRevision)
            {
                battleState.Player.RefreshStats(PlayerAttributeResolver.Resolve(player, gameData).ToCombatant());
                session.AttributeRevision = player.AttributeInputRevision;
            }
            battleState.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(activeBuffs, buffId);
        }
        else
        {
            battleState = RestoreCombatBattle(player, action, encounter, gameData);
            live?.Adopt(action, battleState, expedition, player.AttributeInputRevision);
        }
        var battleFrom = battleState.ElapsedSeconds;
        var combatResult = DeterministicCombatEngine.AdvanceTo(battleState, battleFrom + remainingSeconds);
        if (combatResult.Outcome != CombatOutcome.Running) live?.Release(action);
        CompleteCombatAdvanceCore(player, action, definition, encounter, gameData, battleState, combatResult,
            from, target, ref remainingSeconds, ref deltas, ref events, ref completedActions, ref completedRepeats);
    }

    /// <summary>
    /// 斗法剧本回放推进：结果在撮合时已确定性锁定，本相位只按时间轴回放事件并结算归属。
    /// 战斗期间任何配置修改不影响剧本；玩家主动结束行动走 ApplyDuelForfeit 判负路径。
    /// </summary>
    private static void AdvanceDuelPlayback(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        CombatEncounterDefinition encounter,
        GameDataCatalog gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref Dictionary<ItemId, double>? deltas,
        ref SimulationEventTail? events)
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
        var viewerMp = expedition.PlayerManapoints;
        var opponentMp = playback.OpponentManapoints;
        // 回放游标前的事件已全部应用过；游标到 playbackTo 之间的事件逐条应用并写日志。
        for (var i = cursor; i < playback.Events.Count; i++)
        {
            var duelEvent = playback.Events[i];
            if (duelEvent.AtSeconds > playbackTo + 1e-9) break;
            cursor = i + 1;
            var targetIsViewer = duelEvent.TargetIsInitiator == playback.ViewerIsInitiator;
            var attackerIsViewer = duelEvent.AttackerIsInitiator == playback.ViewerIsInitiator;
            if (targetIsViewer) { viewerHp = duelEvent.TargetHitpointsAfter; viewerMp = duelEvent.TargetManapointsAfter; }
            else { opponentHp = duelEvent.TargetHitpointsAfter; opponentMp = duelEvent.TargetManapointsAfter; }
            if (attackerIsViewer) viewerMp = duelEvent.AttackerManapointsAfter;
            else opponentMp = duelEvent.AttackerManapointsAfter;
            var attacker = duelEvent.AttackerIsInitiator == playback.ViewerIsInitiator ? "你" : playback.OpponentName;
            var targetName = targetIsViewer ? "你" : playback.OpponentName;
            AddCombatEventAt(ref events, from.AddSeconds(elapsedBefore + Math.Max(0, duelEvent.AtSeconds - expedition.BattleElapsedSeconds)),
                "combat.attack", $"{attacker}{(duelEvent.Hit ? $"命中{targetName}，造成 {duelEvent.Damage:0.##} 点伤害" : $"攻击{targetName}未命中")}。", definition.Id);
        }
        var updated = expedition with
        {
            BattleElapsedSeconds = playbackTo,
            PlayerHitpoints = viewerHp,
            PlayerManapoints = viewerMp,
            Duel = playback with { EventCursor = cursor, OpponentHitpoints = opponentHp, OpponentManapoints = opponentMp }
        };
        action.Expedition = updated;
        player.Resources.Hitpoints = Math.Clamp(viewerHp, 0, player.Resources.MaxHitpoints);
        player.Resources.Manapoints = Math.Clamp(viewerMp, 0, player.Resources.MaxManapoints);
        player.MarkDirty(PlayerSectionKind.Combat);
        player.MarkDirty(PlayerSectionKind.ActionQueue);
        if (!playback.Resolved && playbackTo + 1e-9 < playback.DurationSeconds) return;

        // 回放结束（或已裁决标记）：结算斗法归属。
        var now = from.AddSeconds(elapsedBefore + Math.Max(0, playbackTo - expedition.BattleElapsedSeconds));
        var nowUnix = now.ToUnixTimeMilliseconds() / 1000d;
        var duelSettings = gameData.Duel;
        player.Duel.ActiveDuelId = null;
        switch (playback.ViewerOutcome)
        {
            case CombatOutcome.PlayerWon:
                foreach (var spoils in playback.OpponentStagedSnapshot)
                {
                    var spoilsId = new ItemId(spoils.ItemId);
                    AddDelta(deltas ??= new Dictionary<ItemId, double>(), spoilsId, spoils.Quantity);
                    player.RecordStats(action.Id, nowUnix, report => report.AddItem(spoilsId, spoils.Quantity));
                }
                player.RecordStats(action.Id, nowUnix, report => report.DuelWins += 1);
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.won",
                    playback.Forfeited
                        ? $"斗法获胜：{playback.OpponentName} 弃战离去，其暂存物品尽数落袋。"
                        : $"斗法获胜：击败 {playback.OpponentName}，缴获其暂存物品。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1);
                break;
            case CombatOutcome.EnemyWon:
                // 弃战路径（ApplyDuelForfeit）已即时记账暂存移除、冷却与败绩，回放落点不再重复结算。
                if (!playback.Forfeited)
                {
                    ActionReportBook.RemoveStagedCombatItems(player, playback.SelfStagedSnapshot, nowUnix);
                    player.Duel.CooldownUntilUnixSeconds = nowUnix + duelSettings.DefeatCooldownSeconds;
                    player.RecordStats(action.Id, nowUnix,
                        report => { report.DuelLosses += 1; report.Deaths += 1; });
                    player.Progression.TotalDefeats += 1;
                }
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.lost",
                    playback.Forfeited
                        ? $"斗法弃战：你已结束斗法行动，暂存物品被 {playback.OpponentName} 取走。"
                        : $"斗法落败：{playback.OpponentName} 取走了你的暂存物品，一小时内不再进入斗法。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1) with
                { Phase = CombatExpeditionPhase.Respawning, PhaseDurationSeconds = encounter.PlayerRespawnSeconds };
                player.Resources.Hitpoints = 0;
                break;
            default:
                AddCombatEvent(ref events, from, target, remainingSeconds, "duel.draw",
                    $"斗法平局：与 {playback.OpponentName} 战成平手。", definition.Id);
                action.Expedition = CreateSearchState(player, action, encounter, CombatExpeditionPhase.Search,
                    gameData, DeterministicSequence.NextSeed(expedition.Seed), waveNumber: 1);
                break;
        }
        player.MarkDirty(PlayerSectionKind.Duel);
        player.MarkDirty(PlayerSectionKind.Progression);
        action.ProgressSeconds = 0;
    }

    /// <summary>团灭分流到引擎参数：原地复活遭遇携带复活读条秒数，团灭即败遭遇传 0（引擎按 EnemyWon 终局处理）。</summary>
    private static double InBattleRespawnSeconds(CombatEncounterDefinition encounter)
        => encounter.OnPartyWipe == PartyWipeRule.Respawn ? encounter.PlayerRespawnSeconds : 0;

    private static CombatExpeditionState CreateSearchState(
        IPlayerResourceContext player,
        IPlayerActionState action,
        CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase,
        GameDataCatalog? gameData,
        long? seed = null,
        int waveNumber = 1)
    {
        if (gameData is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗搜寻配表不能为空。");
        return CreateCombatSearchState(player, encounter, phase, gameData,
            action.CompletedRepeats, seed, waveNumber);
    }

    public static CombatExpeditionState CaptureCombatState(CombatEncounterState battle, CombatExpeditionState template) => template with
    {
        BattleElapsedSeconds = battle.ElapsedSeconds,
        PlayerHitpoints = battle.Player.Hitpoints,
        PlayerManapoints = battle.Player.Manapoints,
        PlayerNextAttackAtSeconds = battle.Player.NextAttackAtSeconds,
        PlayerAttackCount = battle.Player.AttackCount,
        PlayerThreat = battle.Player.Threat,
        Outcome = battle.Outcome,
        // 倒地窗口的复活截止点随相位持久化；存活或战斗终局时回到 null。
        PlayerRespawnAtSeconds = double.IsFinite(battle.PlayerRespawnAtSeconds) ? battle.PlayerRespawnAtSeconds : null,
        // 玩家单位的完整战斗快照（吟唱槽位/逐槽冷却/施放计数/战内 Buff）随相位持久化，恢复后序列不漂移。
        PlayerState = CombatEncounterState.Snapshot(battle.Player),
        Enemies = battle.Enemies.Select(enemy => new ExpeditionEnemyState(
            enemy.DefinitionId ?? enemy.Id,
            CombatEncounterState.Snapshot(enemy),
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityText(enemy.Skills[enemy.PendingSkillIndex].Ability, "id") : null,
            enemy.PendingSkillIndex >= 0 && enemy.PendingSkillIndex < enemy.Skills.Count
                ? DeterministicCombatEngine.AbilityNumber(enemy.Skills[enemy.PendingSkillIndex].Ability, "castMilliseconds") / 1000d * enemy.Stats.CastTimeFactor
                    * PercentValue.Multiplier(enemy.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear)
                : enemy.Stats.AttackIntervalSeconds)).ToArray()
    };

    /// <summary>
    /// 击杀单只怪物的全部奖励：修为、底蕴、六维经验与掉落。
    /// 斗法开启时掉落爆率乘以配置加成、掉落物进入暂存区；所有收益计入当前战斗账本。
    /// </summary>
    private static void ApplyCombatRewards(
        IPlayerResourceContext player,
        CombatMonsterDefinition monster,
        long seed,
        bool pvpEnabled,
        double killAtUnixSeconds,
        GameDataCatalog gameData,
        Guid actionInstanceId,
        ref Dictionary<ItemId, double>? deltas)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.reward");
        if (monster.QiReward > 0)
        {
            player.Progression.Qi += monster.QiReward;
            player.Progression.LifetimeQi += monster.QiReward;
            player.RecordStats(actionInstanceId, killAtUnixSeconds, report => report.Qi += monster.QiReward);
        }
        if (monster.ExperienceReward > 0)
        {
            var cappedExpReward = CapExperience(player, monster.ExperienceReward, gameData);
            player.Progression.Experience += cappedExpReward;
            player.RecordStats(actionInstanceId, killAtUnixSeconds, report => report.Experience += cappedExpReward);
        }
        if (monster.RootExpReward > 0)
            DistributeRootExp(player, monster.RootExpReward, gameData, actionInstanceId, killAtUnixSeconds);
        var dropBonus = pvpEnabled ? PercentValue.Multiplier(gameData.Duel.DropChanceBonusPercent, PercentCurveMode.Linear) : 1;
        foreach (var drop in monster.DropTable)
        {
            if (!drop.ItemId.IsValid || drop.MinQuantity <= 0 || drop.MaxQuantity < drop.MinQuantity || drop.DropChance <= 0) continue;
            // 判定与数量抽样分属两条独立通道：判定 roll 来自主流递推，
            // 数量 roll 用固定盐位 XOR 出的侧流，彼此不共享抽样值。
            var roll = (unchecked((ulong)DeterministicSequence.NextSeed(seed)) % 1_000_000UL) / 1_000_000d;
            var quantityRoll = (unchecked((ulong)DeterministicSequence.NextSeed(seed ^ DropQuantityChannelSalt)) % 1_000_000UL) / 1_000_000d;
            seed = DeterministicSequence.NextSeed(seed);
            if (roll > Math.Min(1, drop.DropChance * dropBonus)) continue;
            // 数量为闭区间 [min, max] 整数均匀抽样：跨度 +1 保证最大数量可达。
            var quantity = Math.Floor(drop.MinQuantity + (drop.MaxQuantity - drop.MinQuantity + 1) * quantityRoll);
            quantity = Math.Clamp(quantity, drop.MinQuantity, drop.MaxQuantity);
            if (pvpEnabled)
            {
                // 斗法期间掉落进入暂存区：入账延迟与归属由暂存结算/斗法结算决定；StageItem 内部同步记账。
                ActionReportBook.StageCombatItem(player, drop.ItemId, quantity, killAtUnixSeconds);
            }
            else
            {
                AddDelta(deltas ??= new Dictionary<ItemId, double>(), drop.ItemId, quantity);
                player.RecordStats(actionInstanceId, killAtUnixSeconds, report => report.AddItem(drop.ItemId, quantity));
            }
        }
    }

    /// <summary>
    /// 六维经验分配：按已装备功法槽位的 rootAttribute 集合均分；
    /// 无功法或功法未声明归属维度时六维平均分配。
    /// </summary>
    private static void DistributeRootExp(IPlayerResourceContext player, double amount, GameDataCatalog gameData,
        Guid actionInstanceId, double atUnixSeconds)
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
            var expBefore = RootExpOf(core, attribute);
            var levelBefore = RootAttributeCurve.LevelFor(expBefore);
            var levelAfter = RootAttributeCurve.LevelFor(expBefore + share);
            core = attribute switch
            {
                CoreAspectKind.Stamina => core with { StaminaExp = core.StaminaExp + share },
                CoreAspectKind.Meridians => core with { MeridiansExp = core.MeridiansExp + share },
                CoreAspectKind.Attack => core with { AttackExp = core.AttackExp + share },
                CoreAspectKind.Bone => core with { BoneExp = core.BoneExp + share },
                CoreAspectKind.Agility => core with { AgilityExp = core.AgilityExp + share },
                _ => core with { ConsciousnessExp = core.ConsciousnessExp + share }
            };
            var aspect = attribute;
            player.RecordStats(actionInstanceId, atUnixSeconds, report =>
            {
                report.RootExp.Add(aspect, share);
                if (levelAfter > levelBefore)
                    report.LevelUps.Add(new PlayerReportLevelUp("root", aspect.ToString().ToLowerInvariant(), levelBefore, levelAfter));
            });
        }
        player.Loadout.CoreAttributes = core;
    }

    /// <summary>按根骨维度读取累计经验（六维升级判定用）。</summary>
    private static double RootExpOf(PlayerCoreAttributes core, CoreAspectKind attribute) => attribute switch
    {
        CoreAspectKind.Stamina => core.StaminaExp,
        CoreAspectKind.Meridians => core.MeridiansExp,
        CoreAspectKind.Attack => core.AttackExp,
        CoreAspectKind.Bone => core.BoneExp,
        CoreAspectKind.Agility => core.AgilityExp,
        _ => core.ConsciousnessExp
    };

    private static bool TryParseCoreAspect(string abilityId, GameDataCatalog catalog, out CoreAspectKind attribute)
    {
        attribute = default;
        if (!catalog.Abilities.TryGetValue(abilityId, out var ability)
            || !ability.TryGetProperty("rootAttribute", out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        return Enum.TryParse(property.GetString(), true, out attribute) && Enum.IsDefined(attribute);
    }

    private static void AddCombatEvent(ref SimulationEventTail? events, DateTimeOffset from, DateTimeOffset target, double remainingSeconds, string code, string message, ActionId actionId)
    {
        events ??= new SimulationEventTail();
        var elapsed = Math.Max(0, (target - from).TotalSeconds - remainingSeconds);
        AddCombatEventAt(ref events, from.AddSeconds(elapsed), code, message, actionId);
    }

    private static void AddCombatEventAt(ref SimulationEventTail? events, DateTimeOffset at, string code, string message, ActionId actionId)
    {
        events ??= new SimulationEventTail();
        events.Add(new SimulationEvent(at, code, message, actionId));
    }

    /// <summary>
    /// 纯确定性推进移动行动。逐边段平滑累积行进进度与坐标插值，并在抵达终点时完成结算出队与触发到达事件。
    /// </summary>
    private static void AdvanceTravelAction(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        var worldGraph = gameData?.WorldGraph;
        var travel = PrepareTravelActionCore(player, action, definition, gameData,
            from.AddSeconds((target - from).TotalSeconds - remainingSeconds), ref events);
        if (travel is null) return;

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
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        TravelActionState travel,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref SimulationEventTail? events,
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
        var arrivalUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
        // 抵达终点计一次完成轮次，随后实例出队归档账本。
        player.RecordStats(action.Id, arrivalUnix, report => report.Cycles += 1);
        player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: arrivalUnix);
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
        events ??= new SimulationEventTail();
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

    /// <summary>掉落数量抽样的侧通道盐位：与判定主流（种子递推）异或隔离，保证两条随机流互不串扰。</summary>
    private const long DropQuantityChannelSalt = unchecked((long)0x9E3779B97F4A7C15UL);

    /// <summary>
    /// 把任意切分的模拟秒数累进持久化进位余数（carry）并取整整秒。
    /// 1e-9 量级贴整后再向下取整，使“一次推进 N 秒”与“逐秒推进 N 次”产生完全相同的整秒序列，
    /// 连续奖励的浮点操作序列因此位级一致；余数保留到下次推进。
    /// </summary>
    private static long QuantizeElapsedSeconds(IPlayerActionContext player, double seconds)
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
    private static double CapExperience(IPlayerActionContext player, double gain, GameDataCatalog? gameData)
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
    private static void ApplyContinuousReward(IPlayerActionContext player, ActionDefinition definition, long wholeSeconds,
        double multiplier, GameDataCatalog? gameData, Guid actionInstanceId, double atUnixSeconds)
    {
        if (wholeSeconds <= 0) return;
        var expCap = gameData?.RealmProgression?.ExpCapFor(player.Progression.RealmLevel);
        var realmConfigured = gameData?.RealmProgression is not null;
        // 溢出预检查沿用“先校验后变更”语义：rate * 整秒数越界时不得污染任何进度数值。
        var seconds = (double)wholeSeconds;
        var combatTouched = false;
        var progressionTouched = false;
        // 账本按实际入账量记录：修为为每秒全额累加；经验记瓶颈截断后的真实增量。
        var qiRecorded = 0d;
        var experienceBefore = player.Progression.Experience;
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
                    qiRecorded += total;
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
        var experienceDelta = player.Progression.Experience - experienceBefore;
        if (qiRecorded != 0 || experienceDelta != 0)
            player.RecordStats(actionInstanceId, atUnixSeconds,
                report => { report.Qi += qiRecorded; report.Experience += experienceDelta; });
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
        // 账本按实际入账量记录：经验记瓶颈截断后的真实增量，物品产出计获得、输入材料计失去。
        var experienceDelta = nextExperience - player.Progression.Experience;
        player.RecordStats(action.Id, target.ToUnixTimeMilliseconds() / 1000d, report =>
        {
            report.Qi += qiGain;
            report.Experience += experienceDelta;
            report.Cycles += 1;
            if (itemRewards is not null)
                foreach (var (item, quantity) in itemRewards)
                    report.AddItem(item, quantity);
            foreach (var (item, quantity) in definition.Inputs)
                report.AddItemLost(item, quantity);
        });
        player.Progression.Qi += qiGain;
        player.Progression.LifetimeQi += qiGain;
        player.Progression.Experience = nextExperience;
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
                player.RemoveAction(action.Id, incrementVersion: false,
                    endedAtUnixSeconds: target.ToUnixTimeMilliseconds() / 1000d);
            }
        }
    }

    private static double PrepareSustainedAutoUse(
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
                        report => report.AddItemLost(item.Id, 1));
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

    private static void CompleteSustainedAutoUse(
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

    /// <summary>战斗域通用栏持续消耗品槽判定：仅 general 组可挂持续消耗品。</summary>
    /// <remarks>由调用方按索引扫描代替迭代器：战斗中逐帧调用路径不分配枚举器，
    /// 且写回槽位只替换当前索引，实时读与原 ToArray 快照语义等价。</remarks>
    private static bool TryGetCombatSustainedSlot(IPlayerResourceContext player, GameDataCatalog gameData, int index,
        out AutoUseSlotState slot, out ItemDefinition item, out ConsumableDefinition consumable)
    {
        slot = player.Settings.AutoUseSlots[index];
        item = null!;
        consumable = null!;
        if (!slot.Enabled || slot.ItemId is null || slot.Group != AutoUseSlotState.SlotGroupGeneral
            || !string.Equals(slot.ActionType, "combat", StringComparison.OrdinalIgnoreCase)) return false;
        var itemId = new ItemId(slot.ItemId);
        if (!gameData.Items.TryGetValue(itemId, out item!)
            || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0 } found
            || !found.UsableInActionKinds.Contains(ActionKind.Combat)) return false;
        consumable = found;
        return true;
    }

    /// <summary>遭遇入场时为空的通用栏持续消耗品槽补能：消耗 1 件物品充入整段时长，条件不满足则跳过。</summary>
    private static void RechargeCombatSustainedSlots(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet? assetBalances, double atUnix, Guid actionInstanceId, CombatEncounterState battle,
        ref Dictionary<ItemId, double>? deltas, ref SimulationEventTail? events)
    {
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out var consumable)) continue;
            if (slot.RemainingDurationSeconds > 0) continue;
            // 持续消耗品的入场补能同样走共享触发判定：条件不满足本场不启用，不扣件。
            if (consumable.Triggers is { Count: > 0 } triggers)
            {
                // 目标 = 存活敌中有效威胁最高者（MaxBy 语义：严格更大才替换，平局保留先见者）。
                CombatantState? triggerTarget = null;
                var battleEnemies = battle.Enemies;
                for (var i = 0; i < battleEnemies.Count; i++)
                    if (battleEnemies[i].IsAlive
                        && (triggerTarget is null || battleEnemies[i].EffectiveThreat > triggerTarget.EffectiveThreat))
                        triggerTarget = battleEnemies[i];
                if (!Domain.Combat.SkillTriggerEvaluator.MatchesAll(triggers, battle.Player,
                        triggerTarget, [battle.Player], battleEnemies)) continue;
            }
            var itemId = item.Id;
            var available = (assetBalances?.Get(new AssetKey(itemId)) ?? 0d)
                + (deltas?.GetValueOrDefault(itemId) ?? 0d);
            if (available < 1d) continue;
            AddDelta(deltas ??= new Dictionary<ItemId, double>(), itemId, -1d);
            player.RecordStats(actionInstanceId, atUnix, report => report.AddItemLost(itemId, 1));
            player.Settings.AutoUseSlots[index] = slot with { RemainingDurationSeconds = consumable.DurationSeconds };
            player.MarkDirty(PlayerSectionKind.Settings);
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(DateTimeOffset.FromUnixTimeMilliseconds((long)(atUnix * 1000)),
                "auto-use.activated", $"自动使用 {item.Name}，可生效 {consumable.DurationSeconds:0.######} 秒。",
                null, new Dictionary<ItemId, double> { [itemId] = -1d }));
        }
    }

    /// <summary>将持续消耗品的生效 Buff 注入本次遭遇：时长对齐槽位剩余时长，耗尽即自然过期。</summary>
    private static void InjectSustainedCombatBuffs(IPlayerResourceContext player, GameDataCatalog gameData,
        CombatEncounterState battle)
    {
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out _)) continue;
            if (slot.RemainingDurationSeconds <= 0
                || !gameData.RawItems.TryGetValue(item.Id.Value, out var rawItem)
                || !rawItem.TryGetProperty("consumable", out var raw)
                || !raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                battle.Player.Buffs.RemoveAll(current => current.BuffId == buffId);
                battle.Player.Buffs.Add(new CombatBuffState(buffId,
                    battle.ElapsedSeconds + slot.RemainingDurationSeconds, 1,
                    JsonRead.Number(buff, "hpRegen"), JsonRead.Number(buff, "mpRegen"),
                    JsonRead.Number(buff, "attackSpeedPoints"), JsonRead.Number(buff, "damageAbsorptionPoints"),
                    JsonRead.Number(buff, "threatPoints")));
            }
        }
    }

    /// <summary>战斗存活时钟扣减：仅对本场实际推进的战斗秒数生效。</summary>
    private static void DrainCombatSustainedSlots(IPlayerResourceContext player, GameDataCatalog gameData,
        double consumedBattleSeconds, DateTimeOffset at, ActionId actionId, ref SimulationEventTail? events)
    {
        if (consumedBattleSeconds <= 0) return;
        for (var index = 0; index < player.Settings.AutoUseSlots.Count; index++)
        {
            if (!TryGetCombatSustainedSlot(player, gameData, index, out var slot, out var item, out _)) continue;
            if (slot.RemainingDurationSeconds <= 0) continue;
            var remaining = Math.Max(0d, slot.RemainingDurationSeconds - consumedBattleSeconds);
            player.Settings.AutoUseSlots[index] = slot with { RemainingDurationSeconds = remaining };
            player.MarkDirty(PlayerSectionKind.Settings);
            if (remaining > 0) continue;
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(at, "auto-use.depleted", $"{item.Name} 的持续时间已耗尽。", actionId));
        }
    }

    /// <summary>波次敌人攻击计数求和：保持原 LINQ Sum 的逐项累加顺序（double 累加顺序参与位级确定性）。</summary>
    private static double SumEnemyAttackCounts(IReadOnlyList<ExpeditionEnemyState> waveEnemies)
    {
        var total = 0d;
        for (var i = 0; i < waveEnemies.Count; i++) total += waveEnemies[i].State.AttackCount;
        return total;
    }

    /// <summary>战外 Buff 层数合计：注入战斗单位 ExternalBuffStacks 的求值体，无 LINQ 分配。</summary>
    private static double SumActiveBuffStacks(VersionedList<ActiveBuff> actives, string? buffId)
    {
        var total = 0d;
        for (var i = 0; i < actives.Count; i++)
            if (buffId is null || actives[i].BuffId == buffId) total += actives[i].Stacks;
        return total;
    }

    /// <summary>行动队列全阻塞判定：与原 Actions.All 同序短路。</summary>
    private static bool AllActionsBlocked(IReadOnlyList<IPlayerActionState> actions)
    {
        for (var i = 0; i < actions.Count; i++)
            if (actions[i].Status != ActionQueueStatus.Blocked) return false;
        return true;
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
