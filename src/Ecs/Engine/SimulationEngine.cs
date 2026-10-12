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
    IReadOnlyDictionary<AssetKey, double> AssetDeltas,
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
    public const int MaxTransitionsPerAdvance = DaoHua.Ecs.Systems.ActionExecutionRules.MaxTransitionsPerAdvance;

    /// <summary>整秒量化的贴整容差：吸收 0.1 这类二进制无法精确表示的秒数在累加中的尾差。</summary>
    private const double SecondQuantizationEpsilon = 1e-9;


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
    internal sealed class LiveCombatBattle
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
        // 优先经驻留节点实体（MapNodeEntity.EncounterProfileComponent）取生态遭遇池；
        // 聚合/非实体路径回退到配表 WorldGraph——两者承载同一份静态投影，选择语义不变。
        var pool = player.NodeEncounterIds
            ?? (gameData.WorldGraph.TryGetNode(player.World.NodeId, out var node) ? node.EncounterIds : []);
        if (string.IsNullOrWhiteSpace(player.World.EdgeId) && pool.Count > 0)
        {
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

    /// <summary>无世界调用复用 ECS 搜寻阶段规则。</summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player, CombatEncounterDefinition encounter, CombatExpeditionPhase phase,
        GameDataCatalog gameData, double completedRepeats, long? seed = null, int waveNumber = 1)
        => Systems.CombatActionRules.CreateCombatSearchState(player, encounter, phase, gameData,
            completedRepeats, seed, waveNumber);

    /// <summary>无世界调用复用 ECS 搜寻阶段规则。</summary>
    public static CombatExpeditionState CreateCombatSearchState(
        IPlayerResourceContext player, IPlayerActionState action, CombatEncounterDefinition encounter,
        CombatExpeditionPhase phase, GameDataCatalog gameData, long? seed = null, int waveNumber = 1)
        => Systems.CombatActionRules.CreateCombatSearchState(player, action, encounter, phase, gameData, seed, waveNumber);

    /// <summary>无世界调用复用 ECS 战斗入场规则。</summary>
    public static CombatEntryPreparation PrepareCombatEntry(
        IPlayerResourceContext player, IPlayerActionState action, ActionDefinition definition,
        CombatEncounterDefinition encounter, CombatExpeditionState completedSearch,
        GameDataCatalog gameData, AssetWallet? assetBalances, DateTimeOffset entryAt)
        => Systems.CombatActionRules.PrepareCombatEntry(player, action, definition, encounter, completedSearch,
            gameData, assetBalances, entryAt);

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
        // 离线收益封顶（§14.2）：边界 = min(请求起点 + 48h 保险丝, 离线锚点 + 48h)；
        // 无锚点时只受保险丝约束。超出部分只推进模拟时钟与过期清理，不再产出行动收益。
        var fuse = from.Add(PlayerLifecycleRules.OfflineIdleLimit);
        var capAt = player.OfflineSinceAt is { } anchor
            && anchor.Add(PlayerLifecycleRules.OfflineIdleLimit) < fuse
                ? anchor.Add(PlayerLifecycleRules.OfflineIdleLimit) : fuse;
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
                // 暂存物品入账写入 Duel 账本与资产差量：不计 changed 会让入账已落库但无推送下发。
                changed |= stagedSettled.Length > 0;
                foreach (var settledItem in stagedSettled)
                {
                    var settledDelta = new AssetDelta(player.PlayerId, new AssetKey(new ItemId(settledItem.ItemId)), settledItem.Quantity, "simulation");
                    wallet.Apply(settledDelta);
                    deltas[settledDelta.Asset] = deltas.GetValueOrDefault(settledDelta.Asset) + settledDelta.QuantityDelta;
                    var settledItemId = new ItemId(settledItem.ItemId);
                    events.Add(new SimulationEvent(now.AddSeconds(step), "duel.staged.settled",
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
            changed |= ((IPlayerResourceContext)player).ExpireBuffs(target.ToUnixTimeMilliseconds() / 1000d);
            if (!player.Combat.Skills.IsEmpty)
            {
                // 超上限丢弃技能时钟会改变 combat 分段内容：必须标脏，保证增量推送与刷盘不漏。
                // 语义判定：反序列化还原的空技能态与 Empty 单例引用不等，!= Empty 会误标脏。
                player.Combat.Skills = PlayerCombatSkillState.Empty;
                player.MarkDirty(PlayerSectionKind.Combat);
            }
            player.LastSimulatedAt = target;
            changed = true;
            // offline.capped 只在跨越封顶边界的请求发一次：起点已越界的请求静默推进时钟。
            if (from <= capAt)
                events.Add(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        }
        if (changed) player.StateVersion = checked(player.StateVersion + 1);
        return new(player.PlayerId, from, target, (target - from).TotalSeconds, actions, repeats,
            deltas.Where(pair => pair.Value != 0).Select(pair => new AssetDelta(player.PlayerId, pair.Key, pair.Value, "simulation")).ToArray(),
            events.View, player.StateVersion, changed, events.Dropped);
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
        Dictionary<AssetKey, double>? deltas = null;
        SimulationEventTail? events = null;

        var blockedActions = new HashSet<Guid>();
        // 同一行动 ID 在转换循环中反复出现：按 actionId 缓存最近一次查表结果，跳过重复字典查找。
        ActionId lastActionId = default;
        ActionDefinition? lastDefinition = null;
        // 余量按浮点可表达性收尾：战斗时钟以秒级 double 推进，1e-9 以下的残差在 battleFrom + step
        // 回加时会被 ULP 吸收成零消耗，使循环永远不消耗时间而触顶转换上限。
        // 队列停泊开关：暂停时行动整体冻结，墙钟资源窗口仍由外层资源边界照常结算。
        while (remainingSeconds > 1e-9 && player.Actions.Count > 0)
        {
            var action = player.Actions[0];
            var nowUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
            // 参与会话结束是权威队列边界，必须先于暂停和阻塞判断处理，避免暂停永久阻挡散场清理。
            if (action.Participation is { } participation && double.IsFinite(participation.EndsAtUnixSeconds)
                && nowUnix + 1e-6 >= participation.EndsAtUnixSeconds)
            {
                player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: participation.EndsAtUnixSeconds);
                events ??= new SimulationEventTail();
                events.Add(new SimulationEvent(from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                    "action.participation.ended", "行动参与会话已结束。", action.Intent.ActionId));
                continue;
            }
            // 队伍战斗由会话计算容器推进；个人补算只走墙钟，消费投影前不得独立出手。
            if (action.Participation is not null) break;
            // 暂停只冻结仍然有效的行动，不能改变已到期参与会话的收尾优先级。
            if (player.Settings.QueuePaused) break;
            // 阻塞恢复复核：停泊队首先按濒死复苏口径归一气血，再重跑准入——
            // 阵亡不回血（§7.9.3）意味着 hp=0 的受阻战斗行动永远等不到回血，必须经复苏解除。
            // 恢复成功即转 Running 由正常分派接管；仍受阻才出队/轮转/停泊。
            if (action.Status == ActionQueueStatus.Blocked)
            {
                var blockedActionId = action.Intent.ActionId;
                if (lastDefinition is null || lastActionId != blockedActionId)
                {
                    lastDefinition = catalog.Get(blockedActionId);
                    lastActionId = blockedActionId;
                }
                if (Systems.ActionExecutionRules.AdvanceBlockedAction(player, action, lastDefinition,
                        gameData, assetBalances,
                        from.AddSeconds((target - from).TotalSeconds - remainingSeconds),
                        deltas, ref events))
                {
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
            var duration = TimedActionDuration(player, action, definition, gameData, assetBalances, deltas);
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
            CompleteRepeat(player, action, definition, autoUseMultiplier, gameData, assetBalances, from.AddSeconds((target - from).TotalSeconds - remainingSeconds), ref deltas, ref events, ref completedActions, ref completedRepeats);
        }

        // 道龄随模拟时间自然流逝；Progression 仅在发生实际状态转移时标脏，避免空队列每秒刷盘。
        player.Progression.AgeSeconds += (target - from).TotalSeconds;
        player.LastSimulatedAt = target;
        if (remainingSeconds < 0) throw new DomainException(DomainErrorCode.InvariantViolation, "模拟时间出现负余量。");
        var headActivated = false;
        if (player.Actions.Count > 0 && player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            player.Actions[0].Status = ActionQueueStatus.Running;
            headActivated = true;
            // transitions==0（零耗时推进或本轮未完成任何转移）时此处是队首状态唯一变更点，
            // 必须补标 ActionQueue，保证增量触及差集与刷盘不漏状态翻转。
            if (transitions == 0) player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        // 队首 Pending→Running 是可见状态翻转：暂停队列等零转移窗口同样推进版本与推送，
        // 空队列才只更新内存时钟（不递增版本不标脏），避免每秒产生无意义磁盘写入。
        if (transitions > 0 || headActivated)
        {
            player.StateVersion = checked(player.StateVersion + 1);
            if (transitions > 0)
            {
                player.MarkDirty(PlayerSectionKind.Progression);
                player.MarkDirty(PlayerSectionKind.ActionQueue);
            }
        }

        AssetDelta[] assetDeltas;
        if (deltas is null || deltas.Count == 0)
        {
            assetDeltas = [];
        }
        else
        {
            var validDeltas = new List<KeyValuePair<AssetKey, double>>(deltas.Count);
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
                validDeltas.Sort(static (a, b) =>
                {
                    var order = string.CompareOrdinal(a.Key.ItemId.Value, b.Key.ItemId.Value);
                    return order != 0 ? order : string.CompareOrdinal(a.Key.Variant, b.Key.Variant);
                });
                assetDeltas = new AssetDelta[validDeltas.Count];
                var playerId = player.PlayerId;
                for (var i = 0; i < validDeltas.Count; i++)
                {
                    var pair = validDeltas[i];
                    assetDeltas[i] = new AssetDelta(playerId, pair.Key, pair.Value, "simulation");
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
            transitions > 0 || headActivated,
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
        ref Dictionary<AssetKey, double>? deltas,
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
            // 参与会话面板开打即冻结：不做修订刷新，战外 Buff 视图同样取定格——
            // 会话期内的换装/增益漂移不进入共享战斗，否则各成员推演不逐位一致。
            battleState = session!.Battle;
            if (action.Participation?.Spec is { } liveSpec)
            {
                var selfMemberBuffs = liveSpec.Members[battleState.SelfIndex].ExternalBuffs;
                battleState.Player.ExternalBuffStacks = buffId
                    => Systems.CombatActionRules.SumFrozenBuffStacks(selfMemberBuffs, buffId);
            }
            else
            {
                if (gameData.BaseAttributes is null || session.AttributeRevision != player.AttributeInputRevision)
                {
                    battleState.Player.RefreshStats(PlayerAttributeResolver.Resolve(player, gameData).ToCombatant(player.Progression.CombatExperience));
                    session.AttributeRevision = player.AttributeInputRevision;
                }
                battleState.Player.ExternalBuffStacks = buffId => SumActiveBuffStacks(activeBuffs, buffId);
            }
        }
        else
        {
            battleState = RestoreCombatBattle(player, action, encounter, gameData);
            live?.Adopt(action, battleState, expedition, player.AttributeInputRevision);
        }
        var battleFrom = battleState.ElapsedSeconds;
        var combatResult = Systems.CombatEventAdvanceSystem.AdvanceTo(battleState, battleFrom + remainingSeconds);
        if (combatResult.Outcome != CombatOutcome.Running) live?.Release(action);
        CompleteCombatAdvanceCore(player, action, definition, encounter, gameData, battleState, combatResult,
            from, target, ref remainingSeconds, ref deltas, ref events, ref completedActions, ref completedRepeats,
            assetBalances);
    }

    /// <summary>无世界调用复用 ECS 战斗状态导出。</summary>
    public static CombatExpeditionState CaptureCombatState(CombatEncounterState battle, CombatExpeditionState template)
        => Systems.CombatActionRules.CaptureCombatState(battle, template);

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
        => DaoHua.Ecs.Systems.ActionExecutionRules.AdvanceTravelAction(player, action, definition, gameData, from, target, ref remainingSeconds, ref events, ref completedActions, ref completedRepeats);


    internal static long CreateSeed(string encounterId, double completedRepeats)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var character in encounterId) { hash ^= character; hash *= 1099511628211UL; }
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(completedRepeats);
            return (long)hash;
        }
    }



    /// <summary>
    /// 单轮行动耗时走满时的结算处理。
    /// 逐条结算配表声明的按轮收益（修为/灵力/气血/经验/物品/Buff）、扣除输入材料、扣减剩余次数，
    /// 若次数归零则移出队列；若为 -1（无限重复）则绝对保留队列项供下一轮循环。
    /// </summary>
    private static void CompleteRepeat(
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
        => DaoHua.Ecs.Systems.ActionExecutionRules.CompleteRepeat(player, action, definition, autoUseMultiplier, gameData, assets, target, ref deltas, ref events, ref completedActions, ref completedRepeats);






    /// <summary>战外 Buff 层数合计：注入战斗单位 ExternalBuffStacks 的求值体，无 LINQ 分配。</summary>
    internal static double SumActiveBuffStacks(VersionedList<ActiveBuff> actives, string? buffId)
        => DaoHua.Ecs.Systems.ActionExecutionRules.SumActiveBuffStacks(actives, buffId);

    /// <summary>行动队列全阻塞判定：与原 Actions.All 同序短路。</summary>
    internal static bool AllActionsBlocked(IReadOnlyList<IPlayerActionState> actions)
    {
        for (var i = 0; i < actions.Count; i++)
            if (actions[i].Status != ActionQueueStatus.Blocked) return false;
        return true;
    }




    /// <summary>
    /// 累计资产变动差额并执行有限性校验。
    /// </summary>
    internal static void AddDelta(Dictionary<AssetKey, double> deltas, AssetKey asset, double amount)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AddDelta(deltas, asset, amount);

    /// <summary>ItemId 便捷重载：基础键变体为空串。</summary>
    internal static void AddDelta(Dictionary<AssetKey, double> deltas, ItemId item, double amount)
        => DaoHua.Ecs.Systems.ActionExecutionRules.AddDelta(deltas, item, amount);
}
