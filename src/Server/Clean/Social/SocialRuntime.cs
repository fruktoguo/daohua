using System.Text.Json;
using Domain.GameData;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 好友与队伍业务运行时（单例）。
/// 设计要点：
/// 1. 好友与队伍不属于玩家聚合状态，因此不进入玩家级单写者门禁，避免跨玩家互相阻塞；
/// 2. 所有状态变更落 PostgreSQL 后，向受影响玩家推送完整 <see cref="SocialStateDto"/>；
/// 3. 在线存在感变化（上线/下线/换节点）会触发相关玩家的社交状态刷新（好友在线状态与附近修士列表）；
/// 4. 队伍档案/匹配/大厅是招募标签层（docs/design/总纲/21）：身家裁决与并发加队由服务端权威完成，
///    加队满员判定在 <see cref="ISocialRepository.TryJoinPartyAsync"/> 的行锁事务内收敛（open_slots 口径）；
/// 5. 组队讨伐（总纲 §21.13）：每次队伍状态变化后经 <see cref="ArbitratePartySessionAsync"/> 仲裁——
///    满槽+全员就绪+全员位于目标节点自动开打；任一条件失效或解散即写终态并逐成员收尾队首绑定。
///    会话规格（冻结面板/遭遇/种子）随 spec jsonb 持久化；成员行动入队走玩家运行时单写者边界——
///    未装载成员的冻结面板直接解码持久化真源、不强拉装载与冷补算，其参与行动挂载与终局收尾
///    顺延到下次上线的装载对账；独立权威检查点与成员收件箱负责崩溃恢复与离线收益交付。
/// </summary>
public sealed class SocialRuntime
{
    /// <summary>队伍人数上限（含队长）。</summary>
    public const int MaxPartyMembers = 5;
    /// <summary>组队讨伐开启槽位下限（总纲 §21.13，与领域规格 2~5 一致）。</summary>
    public const int MinOpenSlots = 2;
    /// <summary>组队大厅各类列表的最大返回条数。</summary>
    public const int LobbyListLimit = 50;

    /// <summary>实例内推送的最大并行构建数：限制并发占用的数据库连接数。</summary>
    private const int MaxNotifyParallelism = 4;
    /// <summary>队伍名称长度上限（字符）。</summary>
    private const int MaxPartyNameLength = 32;

    private readonly ISocialRepository _repository;
    private readonly OnlinePresenceRegistry _presence;
    private readonly PartyValuationService _valuation;
    private readonly ChatRuntime _chat;
    private readonly ILogger<SocialRuntime> _logger;
    /// <summary>配表已知世界节点 ID 集合：party profile 的 nodeId 合法性判定。</summary>
    private readonly HashSet<string> _nodeIds;
    /// <summary>游戏配表：会话开战时的遭遇池钉死与成员面板冻结采集。</summary>
    private readonly GameDataCatalog _gameData;
    /// <summary>玩家运行时（组队讨伐成员变更通道）；单测装配可为 null 时仲裁退化跳过。</summary>
    private readonly IPlayerRuntime? _players;
    /// <summary>模拟时钟：会话开打/散场时刻与玩家推进同时间轴（确定性基准模式对齐）。</summary>
    private readonly IPlayerSimulationClock _clock;
    private readonly IActionParticipationRuntime? _participation;
    /// <summary>玩家持久化分段仓储：未装载成员的冻结面板直接从持久化真源解码，不触发装载与冷补算。</summary>
    private readonly IPlayerStateRepository? _sections;
    /// <summary>资产余额只读边界：定格时刻可托管库存的权威来源（托管实扣随会话事务原子提交）。</summary>
    private readonly IAssetBalanceRepository? _assetBalances;
    /// <summary>节点公共建筑注册表：开打定格时读取目标节点封魔阵类怪物削弱比并冻结进会话规格。</summary>
    private readonly NodeBuildingRegistry? _nodeBuildings;
    /// <summary>组队讨伐的每队伍裁决门：开打/散场仲裁串行化，跨实例竞态由 DB 唯一索引收敛。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _partyGates = new();
    /// <summary>实例级通知并发门：跨并发存在感触发共享 4 个实际通知槽位。</summary>
    private readonly SemaphoreSlim _notifyGate = new(MaxNotifyParallelism, MaxNotifyParallelism);
    /// <summary>常驻成员簿：有人在线（含宽限期）的活跃会话里保持装载的离线成员（总纲18 §14.6）。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _residentMembers = new();
    /// <summary>常驻对账串行门：与装载/卸载、成员消费及断线回调互斥，防并发竞态。</summary>
    private readonly SemaphoreSlim _residentGate = new(1, 1);
    private readonly object _pendingNotifyLock = new();
    private readonly Dictionary<Guid, PendingNotification> _pendingNotifications = [];

    private sealed class PendingNotification(Delegate[] subscribers)
    {
        public Delegate[] Subscribers = subscribers;
        public bool NeedsRefresh = true;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>构造并接管在线存在感变化订阅。</summary>
    /// <param name="repository">好友/队伍持久化边界。</param>
    /// <param name="presence">在线存在感注册表。</param>
    /// <param name="valuation">身家估值服务。</param>
    /// <param name="chat">传音运行时（party.share 复用频道作用域与限频）。</param>
    /// <param name="catalog">游戏配表（节点合法性校验、世界遭遇池与成员面板采集）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="players">玩家运行时适配器（组队讨伐开打/散场经单写者边界改成员行动）；为 null 时仲裁退化不动作。</param>
    /// <param name="simulationClock">模拟时钟；缺省按环境装配（与玩家运行时同口径）。</param>
    /// <param name="sections">玩家持久化分段仓储（离线成员冻结面板数据源）；为 null 时未装载成员无法定格。</param>
    /// <param name="nodeBuildings">节点公共建筑注册表（目标节点怪物削弱比数据源）；为 null 时会话恒无削弱。</param>
    public SocialRuntime(
        ISocialRepository repository,
        OnlinePresenceRegistry presence,
        PartyValuationService valuation,
        ChatRuntime chat,
        GameDataCatalog catalog,
        ILogger<SocialRuntime> logger,
        IPlayerRuntime? players = null,
        IPlayerSimulationClock? simulationClock = null,
        IActionParticipationRuntime? participation = null,
        IPlayerStateRepository? sections = null,
        IAssetBalanceRepository? assetBalances = null,
        NodeBuildingRegistry? nodeBuildings = null)
    {
        _repository = repository;
        _presence = presence;
        _valuation = valuation;
        _chat = chat;
        _logger = logger;
        _gameData = catalog;
        _players = players;
        _clock = simulationClock ?? PlayerSimulationClock.FromEnvironment();
        _participation = participation;
        _sections = sections;
        _assetBalances = assetBalances;
        _nodeBuildings = nodeBuildings;
        _nodeIds = catalog.WorldGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        _presence.PresenceChanged += OnPresenceChangedAsync;
        _presence.OnlineChanged += OnOnlineChangedAsync;
        _presence.NodeChanged += OnNodeChangedAsync;
    }

    /// <summary>某玩家社交状态发生变化；订阅者按玩家过滤后下发。</summary>
    public event Func<Guid, ValueTask>? StateChanged;

    /// <summary>读取玩家当前队伍 ID（供聊天频道作用域解析）。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>队伍 ID 或 null。</returns>
    public ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        _repository.GetPartyIdAsync(playerId, cancellationToken);

    /// <summary>读取队伍与成员（供队伍频道广播收件人解析）。</summary>
    /// <param name="partyId">队伍 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>队伍记录或 null。</returns>
    public ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default) =>
        _repository.GetPartyAsync(partyId, cancellationToken);

    /// <summary>构建玩家当前完整社交状态视图。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="requestId">回执关联的社交指令请求 ID；主动推送传 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>社交状态载荷。</returns>
    public async ValueTask<SocialStateDto> BuildStateAsync(Guid playerId, string? requestId, CancellationToken cancellationToken = default)
    {
        // 持久化视图打包成单连接一次往返（NpgsqlBatch），替换原先 6 次串行查询；
        // 本方法无副作用且只依赖 playerId，可由握手/扇出等多调用方安全并行调用。
        var snapshot = await _repository.LoadStateSnapshotAsync(playerId, cancellationToken);
        var party = snapshot.Party;
        var invitation = snapshot.Invitation;

        // 队伍成员身家走展示缓存口径（≤30s），推送风暴下摊薄估值开销。
        IReadOnlyDictionary<Guid, ValuationResult> valuations = new Dictionary<Guid, ValuationResult>();
        var levelMap = new Dictionary<Guid, int>();
        var malusMap = new Dictionary<Guid, double>();
        if (party is { Members.Count: > 0 })
        {
            valuations = await _valuation.ComputeDisplayAsync(
                [.. party.Members.Select(member => member.PlayerId)], cancellationToken);
            if (snapshot.PartySession is { } activeSession)
            {
                try
                {
                    var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(activeSession.SpecJson, PersistenceJson.Options);
                    if (spec is not null)
                    {
                        for (var i = 0; i < spec.Members.Count; i++)
                        {
                            if (Guid.TryParse(spec.Members[i].PlayerId, out var mid))
                            {
                                levelMap[mid] = spec.Members[i].RealmLevel;
                                malusMap[mid] = spec.LevelMalusFor(i);
                            }
                        }
                    }
                }
                catch
                {
                    // 容错降级
                }
            }

            var highestLevel = levelMap.Values.Count > 0 ? levelMap.Values.Max() : 1;
            foreach (var member in party.Members)
            {
                if (!levelMap.ContainsKey(member.PlayerId))
                {
                    var lvl = _players?.TryGetLoaded(member.PlayerId)?.State.Progression.RealmLevel ?? 1;
                    levelMap[member.PlayerId] = lvl;
                    highestLevel = Math.Max(highestLevel, lvl);
                }
            }
            foreach (var member in party.Members)
            {
                if (!malusMap.ContainsKey(member.PlayerId))
                {
                    malusMap[member.PlayerId] = ActionParticipationSpec.CalculateLevelMalus(levelMap[member.PlayerId], highestLevel);
                }
            }
        }
        var leaderName = party?.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName;

        return new SocialStateDto(
            requestId,
            [.. snapshot.Friends.Select(friend => new FriendDto(friend.FriendId.ToString("D"), friend.DisplayName, _presence.IsOnline(friend.FriendId)))],
            [.. snapshot.IncomingRequests.Select(ToRequestDto)],
            [.. snapshot.OutgoingRequests.Select(ToRequestDto)],
            party is null
                ? null
                : new PartyDto(
                    party.PartyId.ToString("D"),
                    party.LeaderId.ToString("D"),
                    DisplayName(party.Profile, leaderName),
                    party.Profile.ActivityKind,
                    party.Profile.NodeId,
                    party.Profile.MinValuation,
                    party.Profile.IsPublic,
                    party.Profile.PvpEnabled,
                    [.. party.Members.Select(member => new PartyMemberDto(
                        member.PlayerId.ToString("D"), member.DisplayName,
                        _presence.IsOnline(member.PlayerId),
                        valuations.TryGetValue(member.PlayerId, out var value) ? value.Total : 0,
                        member.Ready,
                        levelMap.GetValueOrDefault(member.PlayerId, 1),
                        malusMap.GetValueOrDefault(member.PlayerId, 0),
                        _presence.GetAvatar(member.PlayerId),
                        valuations.TryGetValue(member.PlayerId, out var v) ? v.ToDto() : null))],
                    party.OpenSlots,
                    snapshot.PartySession is { } session
                        ? new PartySessionDto(session.SessionId, session.EncounterId, session.NodeId,
                            [.. session.MemberIds.Select(id => id.ToString("D"))],
                            session.StartedAt.ToUnixTimeMilliseconds())
                        : null),
            invitation is null
                ? null
                : new PartyInvitationDto(invitation.PartyId.ToString("D"), invitation.InviterId.ToString("D"), invitation.InviterName, invitation.PartyName),
            await BuildNearbyPlayersAsync(playerId, cancellationToken),
            snapshot.Seeker is null
                ? null
                : new PartySeekerDto(
                    snapshot.Seeker.ActivityKind, snapshot.Seeker.NodeId, snapshot.Seeker.MinValuation,
                    snapshot.Seeker.CreatedAt.ToUnixTimeMilliseconds()));
    }
    private async ValueTask<IReadOnlyList<NearbyPlayerDto>> BuildNearbyPlayersAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var nearby = _presence.SnapshotAtNode(_presence.GetNodeId(playerId));
        if (nearby.Count == 0) return nearby;
        var nearbyGuids = nearby.Select(p => Guid.Parse(p.PlayerId)).ToArray();
        var nearbyValuations = await _valuation.ComputeDisplayAsync(nearbyGuids, cancellationToken);
        return [.. nearby.Select(p =>
        {
            var res = nearbyValuations.GetValueOrDefault(Guid.Parse(p.PlayerId));
            return p with { Valuation = res.Total, ValuationBreakdown = res.ToDto() };
        })];
    }


    /// <summary>
    /// 构建组队大厅视图：公开队伍与匹配中散人，按筛选条件过滤。
    /// minValuation 语义：队伍按成员最低身家、散人按本人身家过滤。
    /// </summary>
    /// <param name="playerId">请求玩家 ID。</param>
    /// <param name="request">大厅筛选请求。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>大厅载荷（含请求者本人身家）。</returns>
    public async ValueTask<PartyLobbyDto> BuildLobbyAsync(Guid playerId, PartyLobbyRequestDto request, CancellationToken cancellationToken = default)
    {
        // 非法筛选收敛为「不限」，大厅是只读视图不做拒绝语义。
        var activity = PartyActivityKinds.IsSupported(request.ActivityKind) ? request.ActivityKind! : PartyActivityKinds.Any;
        var nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? string.Empty : request.NodeId.Trim();
        var minValuation = request.MinValuation is { } floor && double.IsFinite(floor) ? Math.Max(0, floor) : 0;

        var parties = await _repository.ListPublicPartiesAsync(activity, nodeId, LobbyListLimit, cancellationToken);
        var seekers = await _repository.ListPartySeekersAsync(activity, nodeId, LobbyListLimit, cancellationToken);

        var subjectIds = new HashSet<Guid> { playerId };
        foreach (var party in parties)
            foreach (var member in party.Members)
                subjectIds.Add(member.PlayerId);
        foreach (var seeker in seekers)
            subjectIds.Add(seeker.PlayerId);
        var valuations = await _valuation.ComputeDisplayAsync(subjectIds, cancellationToken);

        var entries = new List<PartyLobbyEntryDto>(parties.Count);
        foreach (var party in parties)
        {
            if (party.Members.Count == 0) continue;
            var memberDtos = new List<PartyLobbyMemberDto>(party.Members.Count);
            var minMemberValuation = double.MaxValue;
            foreach (var member in party.Members)
            {
                var value = valuations.TryGetValue(member.PlayerId, out var found) ? found : default;
                if (value.Total < minMemberValuation) minMemberValuation = value.Total;
                memberDtos.Add(new PartyLobbyMemberDto(
                    member.PlayerId.ToString("D"), member.DisplayName,
                    _presence.IsOnline(member.PlayerId), value.Total, value.ToDto()));
            }
            if (minMemberValuation < minValuation) continue;
            entries.Add(new PartyLobbyEntryDto(
                party.PartyId.ToString("D"),
                DisplayName(party.Profile, party.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName),
                party.Profile.ActivityKind, party.Profile.NodeId,
                party.LeaderId.ToString("D"),
                party.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName ?? string.Empty,
                party.Profile.MinValuation,
                party.CreatedAt.ToUnixTimeMilliseconds(),
                memberDtos));
        }

        var seekerDtos = new List<PartyLobbySeekerDto>(seekers.Count);
        foreach (var seeker in seekers)
        {
            var value = valuations.TryGetValue(seeker.PlayerId, out var found) ? found : default;
            if (value.Total < minValuation) continue;
            seekerDtos.Add(new PartyLobbySeekerDto(
                seeker.PlayerId.ToString("D"), seeker.DisplayName,
                _presence.IsOnline(seeker.PlayerId), value.Total,
                seeker.ActivityKind, seeker.NodeId, seeker.MinValuation,
                seeker.CreatedAt.ToUnixTimeMilliseconds(),
                value.ToDto()));
        }

        return new PartyLobbyDto(
            request.RequestId,
            valuations.TryGetValue(playerId, out var mine) ? mine.Total : 0,
            entries,
            seekerDtos,
            valuations.TryGetValue(playerId, out var myV) ? myV.ToDto() : null);
    }

    /// <summary>
    /// 构建队伍成员角色信息卡（总纲 §21.14）：一次按需查询应答，不随社交状态常驻下发。
    /// 授权边界：仅可查看本人与同队成员；其余返回 <c>error="forbidden"</c>，身份不存在返回 <c>error="not-found"</c>。
    /// 数据源：已装载玩家读运行时内存态，未装载玩家读持久化分段真源（与身家估值同一展示口径——
    /// 不投影未消费会话结算）；六维等级经境界修为表服务端派生，与 loadout 分段投影同形。
    /// </summary>
    /// <param name="playerId">请求玩家 ID。</param>
    /// <param name="request">角色信息卡请求（已校验的合法 playerId）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>角色信息卡载荷。</returns>
    public async ValueTask<PlayerCardDto> BuildPlayerCardAsync(Guid playerId, PlayerCardRequestDto request, CancellationToken cancellationToken = default)
    {
        var targetId = Guid.Parse(request.PlayerId!);

        // 授权判定：本人、同队成员或同地点附近修士（同节点在线）
        if (targetId != playerId)
        {
            var isSameParty = false;
            var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
            if (partyId is { } id)
            {
                var party = await _repository.GetPartyAsync(id, cancellationToken);
                isSameParty = party is not null && party.Members.Any(member => member.PlayerId == targetId);
            }

            var viewerNode = _presence.GetNodeId(playerId);
            var targetNode = _presence.GetNodeId(targetId);
            var isNearby = viewerNode is not null && targetNode is not null && viewerNode == targetNode;

            if (!isSameParty && !isNearby)
                return new PlayerCardDto(request.RequestId, targetId.ToString("D"), Error: "forbidden");
        }
        var identity = await _repository.GetPlayerIdentityAsync(targetId, cancellationToken);
        if (identity is null)
            return new PlayerCardDto(request.RequestId, targetId.ToString("D"), Error: "not-found");

        // 已装载读运行时内存态；未装载读持久化真源（注册后尚未首次落盘回落默认聚合，账号/道号/注册时刻取 bundle 身份）。
        var state = _players?.TryGetLoaded(targetId)?.State;
        if (state is null)
        {
            var bundle = _sections is null ? null : await _sections.LoadAsync(targetId, cancellationToken);
            if (bundle is null)
                return new PlayerCardDto(request.RequestId, targetId.ToString("D"), Error: "not-found");
            state = bundle.Head.StateVersion > 0 || bundle.Sections.Count > 0
                ? SectionCodec.DecodeAggregate(bundle)
                : PlayerAggregate.Create(new PlayerId(targetId), new AccountId(bundle.Identity.AccountId),
                    bundle.Identity.DisplayName, bundle.Identity.CreatedAt, _gameData.Fingerprint).ExportState();
        }

        var core = state.Loadout.CoreAttributes;
        var levels = core.Levels(_gameData.RealmProgression);
        var valuation = await _valuation.ComputeBreakdownAsync(targetId, cancellationToken);
        var availableBalances = _assetBalances is null
            ? [] : await _assetBalances.LoadBalancesAsync(targetId, cancellationToken);
        var offlineReserves = _participation is null
            ? [] : await _participation.LoadOfflineReservesAsync(targetId, cancellationToken);
        var availableByItem = availableBalances.GroupBy(balance => balance.AssetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(balance => balance.Quantity), StringComparer.Ordinal);
        var reservesByItem = offlineReserves.GroupBy(reserve => reserve.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(reserve => reserve.Quantity), StringComparer.Ordinal);
        var combatSlots = (state.Settings.AutoUseSlots ?? [])
            .Where(slot => slot.ActionType == AutoUseBar.Combat)
            .Select(slot => new PlayerCardAutoUseSlotDto(slot.SlotIndex, slot.ItemId,
                slot.ItemId is { } itemId ? availableByItem.GetValueOrDefault(itemId) : 0,
                slot.Enabled, slot.Group,
                slot.ItemId is { } reserveItemId ? reservesByItem.GetValueOrDefault(reserveItemId) : 0))
            .ToArray();

        var playerAttributes = PlayerAttributeResolver.Resolve(state, _gameData);
        var attributes = ProtocolSnapshotMapper.ToAttributesDto(playerAttributes);
        var techniqueLevels = TechniqueCatalog.All.ToDictionary(
            tech => tech,
            tech => (int)TechniqueCatalog.LevelFor(
                state.Loadout.TechniqueExperiences is { } techExps && techExps.TryGetValue(tech, out var exp) ? exp : 0,
                _gameData.RealmProgression),
            StringComparer.Ordinal);
        return new PlayerCardDto(
            request.RequestId,
            targetId.ToString("D"),
            DisplayName: identity.DisplayName,
            Avatar: state.Settings.Avatar,
            PlayerSeq: identity.PlayerSeq,
            Online: _presence.IsOnline(targetId),
            RealmLevel: state.Progression.RealmLevel,
            Experience: state.Progression.Experience,
            ExperienceToNext: _gameData.RealmProgression?.ExpCapFor(state.Progression.RealmLevel) ?? 0,
            CombatExperience: state.Progression.CombatExperience,
            Valuation: valuation.Total,
            CoreAttributes: new PlayerCardCoreDto(
                core.StaminaExp, core.MeridiansExp, core.AttackExp,
                core.BoneExp, core.AgilityExp, core.ConsciousnessExp,
                levels.Stamina, levels.Meridians, levels.Attack,
                levels.Bone, levels.Agility, levels.Consciousness,
                levels.Total),
            AbilitySlots: [.. state.Loadout.AbilitySlots.Select(slot => new PlayerCardAbilitySlotDto(slot.AbilityId, slot.Level))],
            LearnedAbilityIds: [.. state.LearnedAbilities.Select(ability => ability.Value)],
            AbilityExperiences: state.Loadout.AbilityExperiences,
            Equipment: [.. state.Loadout.Equipment.Select(item => new PlayerCardEquipmentDto(item.ItemId, item.EnhancementLevel))],
            AutoUseSlots: combatSlots,
            Attributes: attributes,
            TechniqueLevels: techniqueLevels,
            ValuationBreakdown: valuation.ToDto());
    }

    /// <summary>执行一条社交指令。</summary>
    /// <param name="playerId">发起玩家 ID。</param>
    /// <param name="action">社交指令载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>执行结局；失败时携带原因码。</returns>
    public async ValueTask<SocialOutcome> ApplyAsync(Guid playerId, SocialActionDto action, CancellationToken cancellationToken = default)
    {
        if (!SocialActions.IsSupported(action.Action)) return new SocialOutcome(SocialFailure.InvalidAction);
        var now = DateTimeOffset.UtcNow;
        switch (action.Action)
        {
            case SocialActions.FriendRequest:
            {
                var targetId = await ResolveTargetAsync(action, cancellationToken);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                if (await _repository.AreFriendsAsync(playerId, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.AlreadyFriends);
                if (!await _repository.TryCreateFriendRequestAsync(Guid.NewGuid(), playerId, targetId.Value, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestExists);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendAccept:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryAcceptFriendRequestAsync(targetId.Value, playerId, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestNotFound);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendReject:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryRejectFriendRequestAsync(targetId.Value, playerId, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestNotFound);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendRemove:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryRemoveFriendAsync(playerId, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotFriends);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyCreate:
            {
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                if (!TryBuildProfile(action.Party, PartyProfileRecord.Default, full: true, out var profile))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                // 自建队伍任队长；匹配登记与旧邀请随入队一并清除（创建路径无成员冲突）。
                await _repository.TryClearPartySeekerAsync(playerId, cancellationToken);
                await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                var partyId = await _repository.CreatePartyAsync(playerId, profile, now, cancellationToken);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyInvite:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                // 未组队时以默认档案自建队伍并任队长：好友列表的「邀请入队」不经大厅表单也可直达。
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken)
                    ?? await _repository.CreatePartyAsync(playerId, PartyProfileRecord.Default, now, cancellationToken);
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (party.Members.Count >= party.OpenSlots) return new SocialOutcome(SocialFailure.PartyFull);
                if (await _repository.GetPartyIdAsync(targetId.Value, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.TargetAlreadyInParty);
                await _repository.UpsertPartyInvitationAsync(partyId, targetId.Value, playerId, now, cancellationToken);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyAccept:
            {
                var invitation = await _repository.GetPartyInvitationAsync(playerId, cancellationToken);
                if (invitation is null) return new SocialOutcome(SocialFailure.InvitationNotFound);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                {
                    await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                }
                var party = await _repository.GetPartyAsync(invitation.PartyId, cancellationToken);
                if (party is null)
                {
                    await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                }
                if (party.Profile.MinValuation > 0
                    && await _valuation.ComputeAsync(playerId, cancellationToken) < party.Profile.MinValuation)
                    return new SocialOutcome(SocialFailure.ValuationTooLow);
                var join = await _repository.TryJoinPartyAsync(invitation.PartyId, playerId, now, cancellationToken);
                if (join != PartyJoinResult.Joined)
                {
                    if (join == PartyJoinResult.NotFound) await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(join switch
                    {
                        PartyJoinResult.NotFound => SocialFailure.PartyNotFound,
                        PartyJoinResult.Full => SocialFailure.PartyFull,
                        _ => SocialFailure.AlreadyInParty
                    });
                }
                // 成员加入是讨伐仲裁触发点（总纲 §21.13）：阵容变化先裁决再广播。
                await ArbitratePartySessionAsync(invitation.PartyId, "member-joined", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), playerId, invitation.InviterId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyDecline:
            {
                if (!await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.InvitationNotFound);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyJoin:
            {
                if (!Guid.TryParse(action.PartyId, out var partyId) || partyId == Guid.Empty)
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.PartyNotFound);
                // 公开队伍大厅加入与不公开队伍链接点击共用此校验：满员与身家门槛服务端说了算。
                if (party.Profile.MinValuation > 0
                    && await _valuation.ComputeAsync(playerId, cancellationToken) < party.Profile.MinValuation)
                    return new SocialOutcome(SocialFailure.ValuationTooLow);
                var join = await _repository.TryJoinPartyAsync(partyId, playerId, now, cancellationToken);
                if (join != PartyJoinResult.Joined)
                {
                    return new SocialOutcome(join switch
                    {
                        PartyJoinResult.NotFound => SocialFailure.PartyNotFound,
                        PartyJoinResult.Full => SocialFailure.PartyFull,
                        _ => SocialFailure.AlreadyInParty
                    });
                }
                // 入队即归属变化：本人复活策略回到缺省（换队、被邀入队后都不得沿用上一队的设定）。
                await ResetRespawnPoliciesAsync([playerId], playerId, cancellationToken);
                // 成员加入是讨伐仲裁触发点（总纲 §21.13）：阵容变化先裁决再广播。
                await ArbitratePartySessionAsync(partyId, "member-joined", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyUpdate:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (!TryBuildProfile(action.Party, party.Profile, full: false, out var profile))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                if (!await _repository.TryUpdatePartyProfileAsync(partyId.Value, profile, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                // 目标节点/目标类型变化是讨伐仲裁触发点：会话成立性随新档案重评。
                await ArbitratePartySessionAsync(partyId.Value, "profile-changed", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyKick:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (!await _repository.TryKickPartyMemberAsync(partyId.Value, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.TargetNotFound);
                // 归属变化重置复活策略：被踢者本人回到缺省，其余曾指名他的成员同样重置。
                await ResetRespawnPoliciesAsync(
                    [.. party.Members.Select(member => member.PlayerId), targetId.Value], targetId.Value, cancellationToken);
                // 踢出使阵容收缩：活跃会话条件必然失效，先仲裁散场再广播。
                await ArbitratePartySessionAsync(partyId.Value, "member-kicked", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyDisband:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                // 先对活跃讨伐会话做权威散场（spec 名册含当前成员），再删 parties 行——
                // 顺序不可颠倒：行删除后 spec 名册不可考，成员队首绑定只能等装载对账兜底。
                await ArbitratePartySessionAsync(partyId.Value, "party-disbanded", cancellationToken, forceDissolve: true);
                // 成员/邀请随 ON DELETE CASCADE 清除，聊天卡片链接置空失效。
                var former = await _repository.DisbandPartyAsync(partyId.Value, cancellationToken);
                if (former is null) return new SocialOutcome(SocialFailure.PartyNotFound);
                // 解散同样改变归属：全部原成员回到缺省策略（每人各自重置自己）。
                foreach (var member in former)
                    await ResetRespawnPoliciesAsync([member.PlayerId], member.PlayerId, cancellationToken);
                await NotifyAsync([.. former.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyMatchSet:
            {
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                if (!TryBuildSeekerCriteria(action.Party, out var activity, out var nodeId, out var minValuation))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                await _repository.UpsertPartySeekerAsync(playerId, activity, nodeId, minValuation, now, cancellationToken);
                // 自动并入：公开且未满员、目标匹配、双向身家门槛满足的队伍；多命中取人多者、再取创建最早者。
                var myValuation = await _valuation.ComputeAsync(playerId, cancellationToken);
                var candidates = await _repository.ListPublicPartiesAsync(activity, nodeId, LobbyListLimit, cancellationToken);
                PartyRecord? best = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.Members.Count >= candidate.OpenSlots) continue;
                    if (candidate.Profile.MinValuation > myValuation) continue;
                    if (minValuation > 0)
                    {
                        // 我方申报的队友门槛：每名在队成员身家都要满足（实时口径）。
                        var memberIds = candidate.Members.Select(member => member.PlayerId).ToArray();
                        var meetsAll = true;
                        foreach (var memberId in memberIds)
                        {
                            if (await _valuation.ComputeAsync(memberId, cancellationToken) < minValuation)
                            {
                                meetsAll = false;
                                break;
                            }
                        }
                        if (!meetsAll) continue;
                    }
                    if (best is null
                        || candidate.Members.Count > best.Members.Count
                        || (candidate.Members.Count == best.Members.Count && candidate.CreatedAt < best.CreatedAt))
                    {
                        best = candidate;
                    }
                }
                if (best is not null)
                {
                    var join = await _repository.TryJoinPartyAsync(best.PartyId, playerId, now, cancellationToken);
                    if (join == PartyJoinResult.Joined)
                    {
                        // 匹配并入改变阵容：并入后仲裁一次（新成员未就绪时只会散场评估）。
                        await ArbitratePartySessionAsync(best.PartyId, "member-joined", cancellationToken);
                        await NotifyAsync([.. best.Members.Select(member => member.PlayerId), playerId], playerId);
                        return SocialOutcome.Ok;
                    }
                    // 并入竞争失败（满员/他队截胡）：登记已写入，保持匹配中。
                    if (join == PartyJoinResult.AlreadyInParty) return new SocialOutcome(SocialFailure.AlreadyInParty);
                }
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyMatchClear:
            {
                if (!await _repository.TryClearPartySeekerAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.SeekingNotFound);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyShare:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                var senderName = party.Members.FirstOrDefault(member => member.PlayerId == playerId)?.DisplayName ?? string.Empty;
                var share = await _chat.SendPartyShareAsync(
                    playerId, senderName, action.Channel, ResolveTargetId(action), party, cancellationToken);
                if (share.Status != ChatSendStatus.Ok)
                {
                    return new SocialOutcome(share.Status == ChatSendStatus.RateLimited
                        ? SocialFailure.RateLimited
                        : SocialFailure.ChannelUnavailable);
                }
                // 队伍卡片发言不改变社交状态，但仍回执一帧供前端确认。
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyLeave:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (!await _repository.TryRemovePartyMemberAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotInParty);
                var affected = new List<Guid> { playerId };
                if (party is not null)
                {
                    affected.AddRange(party.Members.Select(member => member.PlayerId));
                    // 队长离开时把队长位交给剩余成员中最早加入者（members 已按加入时间排序）。
                    if (party.LeaderId == playerId)
                    {
                        var next = party.Members.FirstOrDefault(member => member.PlayerId != playerId);
                        if (next is not null)
                            await _repository.TryTransferPartyLeaderAsync(partyId.Value, next.PlayerId, cancellationToken);
                    }
                }
                await _repository.DeletePartyIfEmptyAsync(partyId.Value, cancellationToken);
                // 归属变化重置复活策略：离队者本人回到缺省，其余曾指名他的成员同样重置。
                if (party is not null)
                    await ResetRespawnPoliciesAsync(party.Members.Select(member => member.PlayerId), playerId, cancellationToken);
                // 离队使活跃会话的冻结名册失效：先仲裁散场，再广播新旧成员。
                await ArbitratePartySessionAsync(partyId.Value, "member-left", cancellationToken);
                await NotifyAsync(affected, playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyReady or SocialActions.PartyUnready:
            {
                // 就绪标记是成员本人的会话意愿开关（总纲 §21.13）；重复置位幂等。
                var ready = action.Action == SocialActions.PartyReady;
                if (!await _repository.TrySetPartyMemberReadyAsync(playerId, ready, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotInParty);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is { } ownedPartyId)
                {
                    await ArbitratePartySessionAsync(ownedPartyId, ready ? "member-ready" : "member-unready", cancellationToken);
                    if (await _repository.GetPartyAsync(ownedPartyId, cancellationToken) is { } refreshed)
                        await NotifyAsync([.. refreshed.Members.Select(member => member.PlayerId)], playerId);
                }
                return SocialOutcome.Ok;
            }
            case SocialActions.PartySlots:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                // 开启槽位 2~5 且不得低于当前队员数（收窄不逐人，只收紧后续加入与开战判定）。
                if (action.Party?.OpenSlots is not { } openSlots
                    || openSlots < MinOpenSlots || openSlots > MaxPartyMembers
                    || openSlots < party.Members.Count)
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                if (!await _repository.TryUpdatePartyOpenSlotsAsync(partyId.Value, openSlots, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                // 槽位收窄使「满槽」失效直接散场；放开后若仍满槽也可能满足开战——统一重裁。
                await ArbitratePartySessionAsync(partyId.Value, "open-slots-changed", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            default:
                return new SocialOutcome(SocialFailure.InvalidAction);
        }
    }

    /// <summary>主动向单个玩家推送最新社交状态（握手完成、节点变化等场景）。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask PushAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        NotifyAsync([playerId]);

    /// <summary>首帧前结清离线储备，直到真实存在感登记后才释放短期在线标记。</summary>
    public ValueTask<IAsyncDisposable> PrepareConnectionAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        (_participation ?? throw new InvalidOperationException("未装配行动参与权威宿主。"))
            .PrepareConnectionAsync(playerId, _clock.UtcNow, cancellationToken);

    private ValueTask OnOnlineChangedAsync(Guid playerId) => _participation is null
        ? ValueTask.CompletedTask : _participation.SynchronizePresenceAsync(playerId, _clock.UtcNow, CancellationToken.None);

    private async ValueTask OnPresenceChangedAsync(IReadOnlyList<Guid> affected)
    {
        try
        {
            await NotifyAsync(affected);
        }
        catch (Exception exception)
        {
            // 存在感推送失败不能影响上线/下线主流程：仅记录，由下一次状态变更或重连补偿。
            _logger.LogError(exception, "在线存在感变化推送社交状态失败。");
        }
    }

    /// <summary>
    /// 档案补丁归一化：full=true（创建）时未提供字段回落默认值，full=false（更新）时保留基线字段。
    /// 队伍名允许空串（展示层回落「{队长道号}的小队」），非空名称去首尾空白后限长 32。
    /// </summary>
    private bool TryBuildProfile(PartyProfileDto? patch, PartyProfileRecord baseline, bool full, out PartyProfileRecord result)
    {
        result = baseline;
        var name = baseline.Name;
        if (patch?.Name is { } rawName)
        {
            name = rawName.Trim();
            if (name.Length > MaxPartyNameLength) return false;
        }
        else if (full) name = string.Empty;

        var activity = baseline.ActivityKind;
        if (patch?.ActivityKind is { } rawActivity)
        {
            activity = rawActivity.Trim();
            if (!PartyActivityKinds.IsSupported(activity)) return false;
        }
        else if (full) activity = PartyActivityKinds.Any;

        var nodeId = baseline.NodeId;
        if (patch?.NodeId is { } rawNode)
        {
            nodeId = rawNode.Trim();
            if (nodeId.Length > 0 && !_nodeIds.Contains(nodeId)) return false;
        }
        else if (full) nodeId = string.Empty;

        var minValuation = baseline.MinValuation;
        if (patch?.MinValuation is { } rawMin)
        {
            if (!double.IsFinite(rawMin) || rawMin < 0) return false;
            minValuation = rawMin;
        }
        else if (full) minValuation = 0;

        var isPublic = baseline.IsPublic;
        if (patch?.IsPublic is { } rawPublic) isPublic = rawPublic;
        else if (full) isPublic = false;

        // 斗法开关（总纲 §7.10.4）：仅讨伐目标可置位；目标改离讨伐自动归位，
        // 非讨伐档案上显式置 true 视为非法档案快速失败。
        var pvpEnabled = baseline.PvpEnabled;
        if (patch?.PvpEnabled is { } rawPvp) pvpEnabled = rawPvp;
        else if (full) pvpEnabled = false;
        if (activity != PartyActivityKinds.Combat)
        {
            if (patch?.PvpEnabled == true) return false;
            pvpEnabled = false;
        }

        result = new PartyProfileRecord(name, activity, nodeId, minValuation, isPublic, pvpEnabled);
        return true;
    }

    /// <summary>匹配登记条件解析：只取 activityKind/nodeId/minValuation 三个招募条件字段。</summary>
    private bool TryBuildSeekerCriteria(PartyProfileDto? patch, out string activity, out string nodeId, out double minValuation)
    {
        activity = PartyActivityKinds.Any;
        nodeId = string.Empty;
        minValuation = 0;
        if (patch?.ActivityKind is { } rawActivity)
        {
            activity = rawActivity.Trim();
            if (!PartyActivityKinds.IsSupported(activity)) return false;
        }
        if (patch?.NodeId is { } rawNode)
        {
            nodeId = rawNode.Trim();
            if (nodeId.Length > 0 && !_nodeIds.Contains(nodeId)) return false;
        }
        if (patch?.MinValuation is { } rawMin)
        {
            if (!double.IsFinite(rawMin) || rawMin < 0) return false;
            minValuation = rawMin;
        }
        return true;
    }

    /// <summary>队伍显示名：档案名称为空时回落「{队长道号}的小队」。</summary>
    private static string DisplayName(PartyProfileRecord profile, string? leaderName) =>
        profile.Name.Length > 0 ? profile.Name : $"{leaderName ?? string.Empty}的小队";

    /// <summary>
    /// 向受影响玩家推送最新社交状态。发起指令的玩家由 WebSocket 端点用带 requestId 的回执帧单独回复，
    /// 因此这里排除该玩家，避免同一状态被推送两次。
    /// </summary>
    private async ValueTask NotifyAsync(IReadOnlyCollection<Guid> playerIds, Guid? exclude = null)
    {
        var handler = StateChanged;
        if (handler is null || playerIds.Count == 0) return;
        // 固定本次订阅快照；直接调用多播异步委托只能得到最后一个订阅的 ValueTask。
        var subscribers = handler.GetInvocationList();
        // 收件人去重；离线玩家没有会话，构建状态只会白跑数据库查询。
        var recipients = new List<Guid>(playerIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var playerId in playerIds)
        {
            if (playerId == exclude || !seen.Add(playerId)) continue;
            if (!_presence.IsOnline(playerId)) continue;
            recipients.Add(playerId);
        }
        if (recipients.Count == 0) return;
        if (recipients.Count == 1)
        {
            await NotifyOneAsync(recipients[0]);
            return;
        }
        // 不同收件人并行；完整状态与存在感投影、单人推送与多次扇出都共享实例上限 4。
        var pending = new List<Task>(recipients.Count);
        foreach (var playerId in recipients)
            pending.Add(NotifyOneAsync(playerId));
        await Task.WhenAll(pending);

        Task NotifyOneAsync(Guid playerId)
        {
            PendingNotification notification;
            lock (_pendingNotifyLock)
            {
                if (_pendingNotifications.TryGetValue(playerId, out notification!))
                {
                    notification.NeedsRefresh = true;
                    notification.Subscribers = subscribers;
                    return notification.Completion.Task;
                }
                notification = new(subscribers);
                _pendingNotifications.Add(playerId, notification);
            }
            _ = DispatchNotificationAsync(playerId, notification);
            return notification.Completion.Task;
        }
    }

    private async Task DispatchNotificationAsync(Guid playerId, PendingNotification notification)
    {
        try
        {
            while (true)
            {
                await _notifyGate.WaitAsync();
                try
                {
                    Delegate[] subscribers;
                    lock (_pendingNotifyLock)
                    {
                        notification.NeedsRefresh = false;
                        subscribers = notification.Subscribers;
                    }
                    foreach (Func<Guid, ValueTask> subscriber in subscribers) await subscriber(playerId);
                }
                finally { _notifyGate.Release(); }
                lock (_pendingNotifyLock)
                {
                    if (notification.NeedsRefresh) continue;
                    _pendingNotifications.Remove(playerId);
                    notification.Completion.SetResult();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_pendingNotifyLock) _pendingNotifications.Remove(playerId);
            notification.Completion.SetException(exception);
        }
    }

    private async ValueTask<Guid?> ResolveTargetAsync(SocialActionDto action, CancellationToken cancellationToken)
    {
        var byId = ResolveTargetId(action);
        if (byId is not null) return byId;
        if (string.IsNullOrWhiteSpace(action.TargetName)) return null;
        return await _repository.FindPlayerByNameAsync(action.TargetName, cancellationToken);
    }

    private static Guid? ResolveTargetId(SocialActionDto action) =>
        Guid.TryParse(action.TargetPlayerId, out var targetId) && targetId != Guid.Empty ? targetId : null;

    // ============================================================================
    // 组队讨伐仲裁（总纲 §21.13）：冻结全员面板后交付唯一权威会话，成员只消费自己的结算，
    // 任一成立条件失效（离队/踢出/取消就绪/槽位变化/节点变化/解散/离线逐出）即散场重建。
    // ============================================================================

    /// <summary>成员节点变化触发讨伐仲裁（Unregister 不触发：离线保留席位，逐出走 48h 扫描）。</summary>
    private async ValueTask OnNodeChangedAsync(Guid playerId)
    {
        try
        {
            if (await _repository.GetPartyIdAsync(playerId, CancellationToken.None) is { } partyId)
                await ArbitratePartySessionAsync(partyId, "member-node-changed", CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 仲裁失败只影响本次触发：就绪/节点等后续变更会再次进入仲裁，会话行是唯一收敛点。
            _logger.LogError(exception, "节点变化触发组队讨伐仲裁失败（玩家 {PlayerId}）。", playerId);
        }
    }

    /// <summary>
    /// 队伍讨伐会话仲裁入口：每次队伍状态变化后裁决一次。进程内经每队伍门串行；
    /// 跨实例并发由 ux_party_sessions_active 部分唯一索引与 ended_at IS NULL 条件更新收敛。
    /// </summary>
    /// <param name="partyId">待裁决队伍。</param>
    /// <param name="reason">仲裁原因标签（散场时落入 end_reason）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="forceDissolve">true 时忽略成立条件强制散场（解散队伍等终局路径）。</param>
    private async ValueTask ArbitratePartySessionAsync(Guid partyId, string reason, CancellationToken cancellationToken, bool forceDissolve = false)
    {
        if (_players is null) return; // 无玩家运行时的装配（社交单测）：仲裁退化，社交状态照常落库。
        var gate = _partyGates.GetOrAdd(partyId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ArbitratePartySessionOwnedAsync(partyId, reason, forceDissolve, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>裁决体（必须持有队伍门后调用）：条件齐备开打，条件失效散场，二者互斥且幂等。</summary>
    private async ValueTask ArbitratePartySessionOwnedAsync(Guid partyId, string reason, bool forceDissolve, CancellationToken cancellationToken)
    {
        var party = await _repository.GetPartyAsync(partyId, cancellationToken);
        var session = await _repository.GetActivePartySessionAsync(partyId, cancellationToken);
        if (party is null)
        {
            // 队伍已删但残留活跃会话（解散竞态漏网）：按名册散场收尾。
            if (session is not null)
                await DissolveSessionAsync(session, reason, cancellationToken);
            return;
        }
        if (session is not null)
        {
            // 会话存续要求名册冻结：成员变动/目标节点漂移/斗法开关漂移/满槽就绪失任一条件即散场。
            var stillTarget = await ResolveReadyTargetNodeAsync(party, cancellationToken);
            var rosterIntact = session.MemberIds.Count == party.Members.Count
                && party.Members.All(member => session.MemberIds.Contains(member.PlayerId));
            if (forceDissolve
                || stillTarget is null
                || !rosterIntact
                || !string.Equals(stillTarget, session.NodeId, StringComparison.Ordinal)
                || session.PvpEnabled != party.Profile.PvpEnabled)
                await DissolveSessionAsync(session, reason, cancellationToken);
            return;
        }
        if (!forceDissolve
            && await ResolveReadyTargetNodeAsync(party, cancellationToken) is { } targetNode)
            await StartSessionAsync(party, targetNode, cancellationToken);
    }

    /// <summary>
    /// 开战条件齐备判定：成员数=开启槽位（2~5）、全员就绪、全员位于目标节点。
    /// 目标节点：档案指定优先，空串回落队长所在节点；任一成员不在目标节点即不成立。
    /// 返回成立时的目标节点 ID，不成立返回 null。
    /// </summary>
    private async ValueTask<string?> ResolveReadyTargetNodeAsync(PartyRecord party, CancellationToken cancellationToken)
    {
        if (party.Members.Count < MinOpenSlots || party.Members.Count != party.OpenSlots) return null;
        if (party.Members.Any(member => !member.Ready)) return null;
        var nodeMap = await _repository.GetPlayerNodeMapAsync(
            [.. party.Members.Select(member => member.PlayerId)], cancellationToken);
        var target = party.Profile.NodeId.Length > 0
            ? party.Profile.NodeId
            : ResolveMemberNode(party.LeaderId, nodeMap);
        if (string.IsNullOrEmpty(target)) return null;
        foreach (var member in party.Members)
            if (!string.Equals(ResolveMemberNode(member.PlayerId, nodeMap), target, StringComparison.Ordinal))
                return null;
        return target;
    }

    /// <summary>成员节点口径：已装载实体以内存态为准（快照比投影新）；未装载成员走持久化投影（迁地途中为缺席）。</summary>
    private string? ResolveMemberNode(Guid memberId, IReadOnlyDictionary<Guid, string> projections)
    {
        if (_players?.TryGetLoaded(memberId) is { } live)
            return live.State.World.EdgeId is null ? live.State.World.NodeId : null;
        return projections.TryGetValue(memberId, out var nodeId) ? nodeId : null;
    }

    /// <summary>
    /// 开打：冻结全员面板 → 钉死遭遇 → 写会话行占活跃位（并发仲裁收敛点）→ 逐成员入队 session 行动。
    /// 行先行、行动后投递：投递失败的成员由各自装载对账与看门狗兜底补发。
    /// </summary>
    private async ValueTask StartSessionAsync(PartyRecord party, string targetNode, CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        if (!_gameData.WorldGraph.TryGetNode(targetNode, out var node) || node.EncounterIds.Count == 0)
        {
            _logger.LogWarning("组队讨伐开战放弃：目标节点 {NodeId} 不在世界图或无遭遇池（队伍 {PartyId}）。", targetNode, party.PartyId);
            return;
        }
        if (party.Profile.ActivityKind is not ("any" or "combat"))
        {
            _logger.LogInformation("行动参与开战跳过：队伍目标类型 {ActivityKind} 不是讨伐（队伍 {PartyId}）。", party.Profile.ActivityKind, party.PartyId);
            return;
        }
        var action = _gameData.Actions.All
            .Where(definition => definition.Kind == ActionKind.Combat
                && definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (definition.AllowedNodeIds is null || definition.AllowedNodeIds.Contains(targetNode))
                && (definition.ExcludedNodeIds is null || !definition.ExcludedNodeIds.Contains(targetNode)))
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (action is null)
        {
            _logger.LogWarning("行动参与开战放弃：目标节点 {NodeId} 没有可用讨伐行动（队伍 {PartyId}）。", targetNode, party.PartyId);
            return;
        }
        var seed = Random.Shared.NextInt64(long.MaxValue);
        var encounterId = node.EncounterIds[(int)(seed % node.EncounterIds.Count)];
        var sessionId = $"action:{Guid.NewGuid():N}";

        // 逐成员冻结面板：已装载成员经所有权通道采集最新内存态并强制落盘；
        // 未装载成员直接解码持久化真源（durable 队列叠加已落库段），不做全量装载与冷补算。
        // 离线成员的冻结面板以其最后提交状态为准；任一成员失败整局放弃，不开半个会话。
        var snapshots = new ActionParticipantSnapshot?[party.Members.Count];
        for (var i = 0; i < party.Members.Count; i++)
        {
            var member = party.Members[i];
            try
            {
                if (_players?.TryGetLoaded(member.PlayerId) is not null)
                {
                    await ApplySessionMutationAsync(member.PlayerId,
                        aggregate => snapshots[i] = aggregate.CaptureActionParticipantSnapshot(_gameData),
                        cancellationToken);
                    // 恢复从 durable 队列读取驻留真源；冻结后先提交成员状态，再交付会话检查点。
                    await _players!.FlushAsync(member.PlayerId, force: true, cancellationToken);
                }
                else
                {
                    snapshots[i] = await CapturePersistedSnapshotAsync(member.PlayerId, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "组队讨伐开战放弃：成员 {PlayerId} 冻结面板采集异常（队伍 {PartyId}）。", member.PlayerId, party.PartyId);
                return;
            }
            if (snapshots[i] is null)
            {
                _logger.LogError("组队讨伐开战放弃：成员 {PlayerId} 冻结面板为空（队伍 {PartyId}）。", member.PlayerId, party.PartyId);
                return;
            }
        }

        // 冻结采集完成后再定统一开场边界，避免成员装载时钟晚于会话开始。
        startedAt = _clock.UtcNow;
        // 封魔阵类节点削弱随开打定格冻结进会话规格（总纲 §9.4）：各成员共享同一公共输入。
        var monsterWeakenRatio = _nodeBuildings is null ? 0
            : (await _nodeBuildings.EffectsForAsync(targetNode, startedAt, cancellationToken)).MonsterWeakenRatio;
        var spec = new ActionParticipationSpec(
            sessionId, encounterId, targetNode, seed,
            snapshots.Max(member => member!.SearchSpeedPoints),
            startedAt.ToUnixTimeMilliseconds() / 1000d,
            [.. snapshots.Select(member => member!)])
        {
            ActionId = action.Id,
            PvpEnabled = party.Profile.PvpEnabled,
            MonsterWeakenRatio = monsterWeakenRatio
        };
        if (!spec.IsValid)
        {
            _logger.LogError("组队讨伐开战放弃：会话规格不合法（队伍 {PartyId}，会话 {SessionId}）。", party.PartyId, sessionId);
            return;
        }
        var participation = _participation ?? throw new InvalidOperationException("未装配行动参与权威宿主。");
        // 开场实际扣件、初始检查点与成员收件箱由会话宿主同事务提交，个人状态不预扣库存。
        await participation.CreateAsync(party.PartyId, spec, cancellationToken);

        var undelivered = 0;
        foreach (var member in party.Members)
        {
            // 未装载成员不强行装载投递：其下次上线的装载对账会按活跃会话行补发挂载。
            if (_players?.TryGetLoaded(member.PlayerId) is null) continue;
            try
            {
                // 玩家变更通道先按持久化真源建立活动引用或消费终局；不重复执行旧开场规格。
                await ApplySessionMutationAsync(member.PlayerId, null, cancellationToken);
            }
            catch (Exception exception)
            {
                undelivered++;
                _logger.LogError(exception, "组队讨伐行动投递失败：成员 {PlayerId}（会话 {SessionId}）。", member.PlayerId, sessionId);
            }
        }
        if (undelivered > 0)
            _logger.LogWarning("组队讨伐会话 {SessionId} 有 {Count} 名成员行动未投递；其装载对账将按活跃会话行补发。", sessionId, undelivered);
        // 开打后常驻对账：名册有人在线时离线成员即入常驻簿（§14.6）。
        await ReconcileResidentMembersAsync(cancellationToken);
        await NotifyAsync([.. party.Members.Select(member => member.PlayerId)]);
        _logger.LogInformation("组队讨伐会话 {SessionId} 开打：队伍 {PartyId}，遭遇 {EncounterId}，成员 {Count} 人。",
            sessionId, party.PartyId, encounterId, party.Members.Count);
    }

    /// 散场：权威会话推进到结束边界并投递末批，随后成员在自身所有权内消费并恢复个人行动。
    /// 三态语义（总纲18 §14.6）：未装载的离线成员同样强拉消费终局结算后卸载，
    /// 保证其持久化状态与个人冷算资格即时恢复，不能从旧状态开下一场。
    /// </summary>
    private async ValueTask DissolveSessionAsync(PartySessionRecord session, string reason, CancellationToken cancellationToken)
    {
        var endedAt = _clock.UtcNow;
        await (_participation ?? throw new InvalidOperationException("未装配行动参与权威宿主。"))
            .EndAsync(session.SessionId, endedAt, reason, cancellationToken);
        foreach (var memberId in session.MemberIds)
        {
            try
            {
                await ApplySessionMutationAsync(memberId, null, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "组队讨伐散场收尾失败：成员 {PlayerId}（会话 {SessionId}）。", memberId, session.SessionId);
            }
        }
        await ReconcileResidentMembersAsync(cancellationToken);
        await NotifyAsync([.. session.MemberIds]);
        _logger.LogInformation("组队讨伐会话 {SessionId} 散场：{Reason}。", session.SessionId, reason);
    }


    /// <summary>
    /// 未装载成员的冻结面板采集：直接解码持久化真源（durable 队列叠加已落库段），
    /// 与玩家装载口径一致——注册后尚未首次落盘的成员回落默认聚合；成员不存在返回 null。
    /// 收件箱仍压着未消费会话结算时（散场收尾不强制装载），持久化头是消费前旧态，
    /// 先按装载对账同口径在临时聚合上重放结算投影，冻结消费后的有效面板——
    /// 「离线成员不能从旧状态开下一场」（总纲 §21.13），如散场复活回血必须生效。
    /// </summary>
    private async ValueTask<ActionParticipantSnapshot?> CapturePersistedSnapshotAsync(Guid memberId, CancellationToken cancellationToken)
    {
        if (_sections is null) return null;
        var bundle = await _sections.LoadAsync(memberId, cancellationToken);
        if (bundle is null) return null;
        var state = bundle.Head.StateVersion > 0 || bundle.Sections.Count > 0
            ? SectionCodec.DecodeAggregate(bundle)
            : PlayerAggregate.Create(new PlayerId(memberId), new AccountId(bundle.Identity.AccountId),
                bundle.Identity.DisplayName, bundle.Identity.CreatedAt, _gameData.Fingerprint).ExportState();
        state = await ProjectPendingSettlementsAsync(memberId, state, cancellationToken);
        return ActionParticipantSnapshot.FromState(state, _gameData);
    }

    /// <summary>
    /// 未消费会话结算的只读终局投影：把装载对账的消费语义（会话绑定补齐→逐条结算→
    /// 终局收尾）在临时聚合上重放一遍，得到成员上线消费后才会呈现的有效状态。
    /// 只读投影不消费收件箱、不写分段，成员上线对账仍是唯一入账口径；
    /// 结算损坏或绑定断裂沿用消费路径的失败语义，显式抛出由开战采集兜底。
    /// </summary>
    private async ValueTask<PlayerAggregateState> ProjectPendingSettlementsAsync(Guid memberId,
        PlayerAggregateState state, CancellationToken cancellationToken)
    {
        if (_participation is null) return state;
        var pending = await _participation.LoadPendingAsync(memberId, cancellationToken);
        if (pending.Count == 0) return state;
        var sessions = new Dictionary<string, PartySessionRecord>(StringComparer.Ordinal);
        foreach (var sessionId in pending.Select(item => item.SessionId).Distinct())
            sessions[sessionId] = await _repository.GetPartySessionAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException("会话结算缺少权威会话记录。");
        var aggregate = PlayerAggregate.Restore(state);
        // 同刻换场也必须先消费旧会话末批；组内序号才是个人收益消费顺序。
        foreach (var group in pending.GroupBy(item => item.SessionId).OrderBy(group => sessions[group.Key].StartedAt))
        {
            var row = sessions[group.Key];
            var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(row.SpecJson, PersistenceJson.Options)
                ?? throw new InvalidOperationException("会话冻结输入为空。");
            foreach (var item in group.OrderBy(item => item.Sequence))
            {
                var settlement = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(item.PayloadJson, PersistenceJson.Options)
                    ?? throw new InvalidOperationException("会话个人结算载荷为空。");
                if (settlement.PlayerId.Value != memberId || settlement.SessionId != item.SessionId || settlement.Sequence != item.Sequence)
                    throw new InvalidOperationException("会话收件箱与个人结算身份不一致。");
                if (aggregate.Actions.Count == 0 || aggregate.Actions[0].Participation?.SessionId != item.SessionId)
                {
                    if (aggregate.Actions.Any(action => action.Participation is not null))
                        throw new DomainException(DomainErrorCode.InvariantViolation, "前一会话结算尚未收尾。");
                    aggregate.StartActionParticipation(spec, row.StartedAt, _gameData.Actions,
                        row.EndedAt?.ToUnixTimeMilliseconds() / 1000d ?? double.PositiveInfinity,
                        appliedSequence: item.Sequence - 1);
                }
                aggregate.ApplyActionParticipationSettlement(settlement, _gameData);
                if (settlement.EndsAtUnixSeconds is { } endsAt)
                {
                    aggregate.EndActionParticipation(item.SessionId, endsAt);
                    aggregate.RemoveAction(aggregate.Actions[0].Id, endedAtUnixSeconds: endsAt);
                }
            }
        }
        return aggregate.ExportState();
    }

    /// <summary>
    /// 队伍归属变化后重置复活策略（总纲 §7.9.4.1）。策略虽然是玩家自己的设置，但它依赖
    /// 「当前与谁同队」这一前提，归属一变就必须回到缺省的「直接复活」，否则玩家会按不再成立的意图倒地等待：
    /// 1. 离队/换队/被踢/超时逐出的玩家本人；
    /// 2. 把「指定队友存活」指名到该离队者的其余成员。
    /// 经既有单写者通道逐人处理（在线离线统一，离线成员装载后即卸并强制落盘）。
    /// </summary>
    /// <param name="members">受影响玩家（含发起离队者本人）。</param>
    /// <param name="departedPlayerId">离队玩家标识：本人重置全部策略，其余成员仅重置指名到他的策略。</param>
    private async ValueTask ResetRespawnPoliciesAsync(IEnumerable<Guid> members, Guid departedPlayerId, CancellationToken cancellationToken)
    {
        if (_players is null) return;
        foreach (var memberId in members.Distinct())
        {
            await ApplySessionMutationAsync(memberId,
                aggregate => {
                    if (memberId == departedPlayerId) aggregate.ResetRespawnPolicy();
                    else aggregate.ResetRespawnPolicyIfTarget(departedPlayerId);
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// 会话成员对账通道：在线/离线成员统一经玩家运行时门内装载、结算消费与可选变更；
    /// 本会话强拉的离线实体变更后即卸并强制落盘，不留驻内存、不落「在线」假状态。
    /// </summary>
    private async ValueTask ApplySessionMutationAsync(Guid memberId, Action<PlayerAggregate>? mutate, CancellationToken cancellationToken)
    {
        if (_players is null) return;
        var wasLoaded = _players.TryGetLoaded(memberId) is not null;
        // 装载不再改变在线状态（§14.1）：离线成员被强拉后仍保持离线锚点，只做业务变更。
        await _players.MutateOwnedAsync(memberId, aggregate => mutate?.Invoke(aggregate), cancellationToken);
        // 仅卸载本会话强拉的实体：真在线玩家、常驻成员与既有驻留实体不动（变更后复核注册表收窄竞态）。
        if (!wasLoaded && !_presence.IsOnline(memberId) && !_residentMembers.ContainsKey(memberId))
            await _players.RemoveAsync(memberId, flush: true, offlineSince: null, cancellationToken);
    }

    /// <summary>
    /// 48h 离线成员逐出（看门狗调用）：逐队在队伍门内完成队长移交/空队清理/讨伐仲裁，
    /// 再向剩余成员与被逐者广播（被逐者离线时被推送层过滤，其上线对账兜底）。
    /// </summary>
    public async ValueTask EvictOfflinePartyMembersAsync(TimeSpan offlineLimit, CancellationToken cancellationToken = default)
    {
        var evicted = await _repository.DeleteInactivePartyMembersAsync(_clock.UtcNow - offlineLimit, cancellationToken);
        if (evicted.Count == 0) return;
        foreach (var group in evicted.GroupBy(item => item.PartyId))
        {
            var partyId = group.Key;
            var gate = _partyGates.GetOrAdd(partyId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) continue;
                // 被逐出者是队长时移交最早剩余成员（与离队路径同规则）。
                if (group.Any(item => item.PlayerId == party.LeaderId))
                {
                    var next = party.Members.FirstOrDefault(member => group.All(item => item.PlayerId != member.PlayerId));
                    if (next is not null)
                        await _repository.TryTransferPartyLeaderAsync(partyId, next.PlayerId, cancellationToken);
                }
                await _repository.DeletePartyIfEmptyAsync(partyId, cancellationToken);
                // 归属变化重置复活策略：逐人处理（被逐者本人回到缺省，其余曾指名他们的成员同样重置）。
                foreach (var evictedMember in group)
                    await ResetRespawnPoliciesAsync(
                        [.. party.Members.Select(member => member.PlayerId), evictedMember.PlayerId],
                        evictedMember.PlayerId, cancellationToken);
                await ArbitratePartySessionOwnedAsync(partyId, "member-offline-timeout", forceDissolve: false, cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), .. group.Select(item => item.PlayerId)]);
                _logger.LogInformation("48h 离线逐出完成：队伍 {PartyId} 移除 {Count} 人。", partyId, group.Count());
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 周期先按组织条件裁决会话，再让已装载成员进入自己的所有权通道消费结算并补齐引用。
    /// 不从成员旧战斗态自行散场；漏投、取消准备与成员关系落库后崩溃均由权威会话收敛。
    /// </summary>
    public async ValueTask ReconcileLoadedSessionBindingsAsync(CancellationToken cancellationToken = default)
    {
        if (_players is null) return;
        var loaded = (await _players.GetLoadedSnapshotsAsync(cancellationToken))
            .Where(snapshot => snapshot.State.Actions.Any(action => action.Participation is not null)
                || _participation?.RequiresReconciliation(snapshot.PlayerId) == true).ToArray();
        var parties = new HashSet<Guid>();
        foreach (var snapshot in loaded)
        {
            if (await _repository.GetPartyIdAsync(snapshot.PlayerId, cancellationToken) is { } partyId)
                parties.Add(partyId);
            if (snapshot.State.Actions.Count > 0 && snapshot.State.Actions[0].Participation is { } binding)
            {
                var session = await _repository.GetPartySessionAsync(binding.SessionId, cancellationToken)
                    ?? throw new InvalidOperationException("参与引用缺少权威会话记录。");
                parties.Add(session.PartyId);
            }
        }
        foreach (var partyId in parties)
            await ArbitratePartySessionAsync(partyId, "organization-reconciled", cancellationToken);
        foreach (var snapshot in loaded)
            // 空变更：保留门内结算对账调用以触发收件箱消费，在线状态由连接生命周期驱动不再在此对账。
            await _players.MutateOwnedAsync(snapshot.PlayerId, _ => { }, cancellationToken);
    }

    /// <summary>
    /// 常驻成员对账（总纲18 §14.6）：名册中至少一人在线（含宽限期）的活跃会话，
    /// 其离线成员保持装载常驻并实时消费结算；不再属于该集合的常驻成员卸载归还。
    /// 开打/散场/看门狗/宽限到期后各对账一次；全程在常驻门内串行，与装载卸载竞态互斥。
    /// </summary>
    public async ValueTask ReconcileResidentMembersAsync(CancellationToken cancellationToken = default)
    {
        if (_players is null) return;
        await _residentGate.WaitAsync(cancellationToken);
        try
        {
            var sessions = await _repository.ListActivePartySessionsAsync(cancellationToken);
            // 期望常驻：名册中至少一人在线的会话里所有不在线成员。
            var expected = new HashSet<Guid>();
            foreach (var session in sessions)
            {
                if (!session.MemberIds.Any(_presence.IsOnline)) continue;
                foreach (var memberId in session.MemberIds)
                    if (!_presence.IsOnline(memberId)) expected.Add(memberId);
            }
            // 期望但未装载：强拉常驻——装载对账自身完成会话绑定与收件箱消费，不改变在线状态。
            foreach (var memberId in expected)
            {
                if (_players.TryGetLoaded(memberId) is not null)
                {
                    _residentMembers.TryAdd(memberId, 0);
                    continue;
                }
                try
                {
                    await _players.LoadAsync(memberId, cancellationToken);
                    _residentMembers[memberId] = 0;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "会话常驻成员装载失败：成员 {PlayerId}，下轮对账重试。", memberId);
                }
            }
            // 簿中不再期望：已在线者仅摘簿（实体移交连接生命周期/宽限接管），仍离线者落盘卸载后摘簿。
            foreach (var memberId in _residentMembers.Keys.ToArray())
            {
                if (expected.Contains(memberId)) continue;
                if (_presence.IsOnline(memberId))
                {
                    _residentMembers.TryRemove(memberId, out _);
                    continue;
                }
                try
                {
                    await _players.RemoveAsync(memberId, flush: true, offlineSince: null, cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "会话常驻成员卸载失败：成员 {PlayerId}，保留簿内下轮重试。", memberId);
                    continue;
                }
                _residentMembers.TryRemove(memberId, out _);
            }
        }
        finally
        {
            _residentGate.Release();
        }
    }

    /// <summary>
    /// 全员离线会话批算（总纲18 §14.6）：权威宿主按批算间隔推进到当前，
    /// 推进后逐成员经所有权通道强拉消费结算再卸载；单成员失败逐个记录不阻塞。
    /// </summary>
    public async ValueTask AdvanceOfflineSessionsAsync(TimeSpan batchInterval, CancellationToken cancellationToken = default)
    {
        if (_players is null || _participation is null) return;
        var members = await _participation.AdvanceOfflineDueAsync(_clock.UtcNow, batchInterval, cancellationToken);
        foreach (var memberId in members)
        {
            if (_players.TryGetLoaded(memberId) is not null) continue;
            try
            {
                await ApplySessionMutationAsync(memberId, null, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "全员离线会话成员结算消费失败：成员 {PlayerId}。", memberId);
            }
        }
        await NotifyAsync(members);
    }

    private static FriendRequestDto ToRequestDto(FriendRequestView view) =>
        new(view.RequestId.ToString("D"), view.PlayerId.ToString("D"), view.DisplayName, view.CreatedAt.ToUnixTimeMilliseconds());
}
