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

    /// <summary>预置遭遇计数并挂上勾选斗法的讨伐行动；推进一次后即处于匹配等待相位。</summary>
    private static async Task SeedSeekerAsync(EcsPlayerRuntimeAdapter runtime, Guid playerId)
    {
        await runtime.LoadAsync(playerId);
        await runtime.MutateOwnedAsync(playerId, aggregate =>
        {
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
