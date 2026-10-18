using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 生产斗法撮合门禁：AdvanceAllAsync 推进窗口内的匹配循环——
/// 等待者配对挂载同一剧本、无候选静默回退、离线补算路径不撮合。
/// </summary>
public sealed class DuelMatchingTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 两名勾选斗法且持有遭遇计数的玩家在同一推进窗口配对：
    /// 双方 ActiveDuelId 相同、队首相位进入 Duel、计数各消耗一点。
    /// </summary>
    [Fact]
    public async Task AdvanceAll_PairsTwoPendingSeekersIntoSharedDuel()
    {
        var (seeker, defender) = (Guid.NewGuid(), Guid.NewGuid());
        var clock = new ManualClock(Baseline);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeSections(new HashSet<Guid> { seeker, defender }, Baseline), new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded(), simulationClock: clock, duelAdvance: new DuelAdvanceOptions(true));
        await SeedSeekerAsync(runtime, seeker);
        await SeedSeekerAsync(runtime, defender);

        clock.AdvanceTo(Baseline.AddSeconds(25));
        await runtime.AdvanceAllAsync(clock.UtcNow);

        var seekerState = runtime.TryGetLoaded(seeker)!.State;
        var defenderState = runtime.TryGetLoaded(defender)!.State;
        var duelId = seekerState.Duel.ActiveDuelId;
        Assert.NotNull(duelId);
        Assert.Equal(duelId, defenderState.Duel.ActiveDuelId);
        Assert.Equal(CombatExpeditionPhase.Duel, seekerState.Actions[0].Expedition!.Phase);
        Assert.Equal(CombatExpeditionPhase.Duel, defenderState.Actions[0].Expedition!.Phase);
        Assert.NotNull(seekerState.Actions[0].Expedition!.Duel);
        Assert.NotNull(defenderState.Actions[0].Expedition!.Duel);
        Assert.Equal(0, seekerState.Duel.EncounterCharges);
        Assert.Equal(0, defenderState.Duel.EncounterCharges);
    }

    /// <summary>
    /// 无候选对手时静默回退：退出等待相位、遭遇计数保留、不产生进行中斗法；
    /// 下一次推进按 DuelSeekFailed 标记进入正常怪物波次。
    /// </summary>
    [Fact]
    public async Task AdvanceAll_LoneSeekerFallsBackToNormalWave()
    {
        var seeker = Guid.NewGuid();
        var clock = new ManualClock(Baseline);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeSections(new HashSet<Guid> { seeker }, Baseline), new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded(), simulationClock: clock, duelAdvance: new DuelAdvanceOptions(true));
        await SeedSeekerAsync(runtime, seeker);

        clock.AdvanceTo(Baseline.AddSeconds(25));
        await runtime.AdvanceAllAsync(clock.UtcNow);

        var state = runtime.TryGetLoaded(seeker)!.State;
        var expedition = state.Actions[0].Expedition!;
        Assert.NotEqual(CombatExpeditionPhase.DuelPending, expedition.Phase);
        Assert.True(expedition.DuelSeekFailed);
        Assert.Null(state.Duel.ActiveDuelId);
        Assert.Equal(1, state.Duel.EncounterCharges);

        clock.AdvanceTo(clock.UtcNow.AddSeconds(25));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Equal(CombatExpeditionPhase.Combat, runtime.TryGetLoaded(seeker)!.State.Actions[0].Expedition!.Phase);
    }

    /// <summary>
    /// 离线/无撮合环境：遭遇计数保留但不进等待相位、不产出斗法——与 ComputeNode 补算同口径。
    /// </summary>
    [Fact]
    public async Task AdvanceAll_WithoutMatchingNeverPairs()
    {
        var (seeker, defender) = (Guid.NewGuid(), Guid.NewGuid());
        var clock = new ManualClock(Baseline);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeSections(new HashSet<Guid> { seeker, defender }, Baseline), new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded(), simulationClock: clock);
        await SeedSeekerAsync(runtime, seeker);
        await SeedSeekerAsync(runtime, defender);

        clock.AdvanceTo(Baseline.AddSeconds(25));
        await runtime.AdvanceAllAsync(clock.UtcNow);

        Assert.Null(runtime.TryGetLoaded(seeker)!.State.Duel.ActiveDuelId);
        Assert.Null(runtime.TryGetLoaded(defender)!.State.Duel.ActiveDuelId);
        Assert.Equal(1, runtime.TryGetLoaded(seeker)!.State.Duel.EncounterCharges);
    }

    /// <summary>
    /// 严格同节点约束（总纲 §7.10.1）：处于不同节点的两名寻求者即使战力匹配，也绝对不可撮合，
    /// 推进后各自退出等待相位并回退。
    /// </summary>
    [Fact]
    public async Task AdvanceAll_SeekersAtDifferentNodesNeverPair()
    {
        var (seeker, defender) = (Guid.NewGuid(), Guid.NewGuid());
        var clock = new ManualClock(Baseline);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeSections(new HashSet<Guid> { seeker, defender }, Baseline), new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded(), simulationClock: clock, duelAdvance: new DuelAdvanceOptions(true));
        // node.yunxi-village 与 node.cloud-gate 均为支持 combat.hunt-qi-beast 的真实有效节点
        await SeedSeekerAsync(runtime, seeker, nodeId: "node.yunxi-village");
        await SeedSeekerAsync(runtime, defender, nodeId: "node.cloud-gate");

        clock.AdvanceTo(Baseline.AddSeconds(25));
        await runtime.AdvanceAllAsync(clock.UtcNow);

        var seekerState = runtime.TryGetLoaded(seeker)!.State;
        var defenderState = runtime.TryGetLoaded(defender)!.State;
        Assert.Null(seekerState.Duel.ActiveDuelId);
        Assert.Null(defenderState.Duel.ActiveDuelId);
        Assert.NotEqual(CombatExpeditionPhase.Duel, seekerState.Actions[0].Expedition!.Phase);
        Assert.NotEqual(CombatExpeditionPhase.Duel, defenderState.Actions[0].Expedition!.Phase);
        Assert.True(seekerState.Actions[0].Expedition!.DuelSeekFailed);
        Assert.True(defenderState.Actions[0].Expedition!.DuelSeekFailed);
        Assert.Equal(1, seekerState.Duel.EncounterCharges);
        Assert.Equal(1, defenderState.Duel.EncounterCharges);
    }

    /// <summary>
    /// 节点斗法队伍数统计（CountNodeDuelCombatTeams）：
    /// 仅统计当前节点开启斗法并且处于战斗的队伍，单人各计 1 队，同一组队会话多成员去重计 1 队。
    /// </summary>
    [Fact]
    public async Task CountNodeDuelCombatTeams_AccuratelyCountsPvpCombatTeamsAndDeduplicatesSessions()
    {
        var (soloPvpAlpha, soloNonPvpAlpha, soloPvpBeta, party1Alpha, party2Alpha) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var allIds = new HashSet<Guid> { soloPvpAlpha, soloNonPvpAlpha, soloPvpBeta, party1Alpha, party2Alpha };
        var clock = new ManualClock(Baseline);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeSections(allIds, Baseline), new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded(), simulationClock: clock);

        var actions = GameDataCatalog.LoadEmbedded().Actions;

        // 1. soloPvpAlpha: node.yunxi-village，开启斗法
        await runtime.LoadAsync(soloPvpAlpha);
        await runtime.MutateOwnedAsync(soloPvpAlpha, a =>
        {
            a.World.NodeId = "node.yunxi-village";
            a.World.EdgeId = null;
            a.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true), a.LastSimulatedAt, actions);
        });

        // 2. soloNonPvpAlpha: node.yunxi-village，未开启斗法
        await runtime.LoadAsync(soloNonPvpAlpha);
        await runtime.MutateOwnedAsync(soloNonPvpAlpha, a =>
        {
            a.World.NodeId = "node.yunxi-village";
            a.World.EdgeId = null;
            a.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: false), a.LastSimulatedAt, actions);
        });

        // 3. soloPvpBeta: node.cloud-gate，开启斗法
        await runtime.LoadAsync(soloPvpBeta);
        await runtime.MutateOwnedAsync(soloPvpBeta, a =>
        {
            a.World.NodeId = "node.cloud-gate";
            a.World.EdgeId = null;
            a.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true), a.LastSimulatedAt, actions);
        });

        // 4 & 5. party1Alpha, party2Alpha: node.yunxi-village，开启斗法，属于同一个组队讨伐会话
        var sessionId = "party.session.sample";
        var catalog = GameDataCatalog.LoadEmbedded();
        var frozen = new List<ActionParticipantSnapshot>();
        foreach (var memberId in new[] { party1Alpha, party2Alpha })
        {
            await runtime.LoadAsync(memberId);
            await runtime.MutateOwnedAsync(memberId, a =>
            {
                a.World.NodeId = "node.yunxi-village";
                a.World.EdgeId = null;
                frozen.Add(a.CaptureActionParticipantSnapshot(catalog));
            });
        }
        var spec = new ActionParticipationSpec(sessionId, "encounter.qi-beast", "node.yunxi-village", 97, 0,
            Baseline.ToUnixTimeMilliseconds() / 1000d, frozen)
        {
            ActionId = new("combat.hunt-qi-beast"),
            PvpEnabled = true
        };
        foreach (var memberId in new[] { party1Alpha, party2Alpha })
        {
            await runtime.MutateOwnedAsync(memberId, a =>
            {
                a.StartActionParticipation(spec, a.LastSimulatedAt, actions);
            });
        }

        // 推进一次使得战斗行动进入 Running 状态并初始化 Expedition
        clock.AdvanceTo(Baseline.AddSeconds(5));
        await runtime.AdvanceAllAsync(clock.UtcNow);

        // 验证统计
        Assert.Equal(2, runtime.CountNodeDuelCombatTeams("node.yunxi-village")); // soloPvpAlpha (1) + party (1)
        Assert.Equal(1, runtime.CountNodeDuelCombatTeams("node.cloud-gate"));     // soloPvpBeta (1)
        Assert.Equal(0, runtime.CountNodeDuelCombatTeams("node.herb-valley"));
        Assert.Equal(0, runtime.CountNodeDuelCombatTeams(null));
        Assert.Equal(0, runtime.CountNodeDuelCombatTeams(string.Empty));
    }

    /// <summary>预置遭遇计数并挂上勾选斗法的讨伐行动；推进一次后即处于匹配等待相位。</summary>
    private static async Task SeedSeekerAsync(EcsPlayerRuntimeAdapter runtime, Guid playerId, string? nodeId = null)
    {
        await runtime.LoadAsync(playerId);
        await runtime.MutateOwnedAsync(playerId, aggregate =>
        {
            if (!string.IsNullOrEmpty(nodeId))
            {
                aggregate.World.NodeId = nodeId;
                aggregate.World.EdgeId = null;
            }
            aggregate.Duel.EncounterCharges = 1;
            aggregate.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true),
                aggregate.LastSimulatedAt, GameDataCatalog.LoadEmbedded().Actions);
        });
    }

    /// <summary>内存假分段仓库：按注册玩家各给一份全新空包。</summary>
    private sealed class FakeSections(IReadOnlySet<Guid> playerIds, DateTimeOffset createdAt) : IPlayerStateRepository
    {
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PlayerStateBundle?>(playerIds.Contains(id)
                ? new PlayerStateBundle(
                    new PlayerIdentity(id, id, $"道友{id:N}"[..8], createdAt),
                    new StoredHead(id, 0, 1, createdAt, string.Empty, string.Empty, createdAt),
                    new Dictionary<string, StoredSection>(), [])
                : null);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }

    private sealed class ManualClock(DateTimeOffset at) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; private set; } = at;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
    }
}
