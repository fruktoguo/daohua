using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;

namespace DaoHua.Ecs;

/// <summary>
/// 无网络、无存储的确定性冷补算执行器。
/// 调用方负责冻结并校验持久化输入；本类型只把完整领域快照装载到独立 ECS 世界、推进一次并导出脏分段与原始资产增量。
/// </summary>
public sealed class DeterministicCatchUpEngine(GameDataCatalog gameData) : IDisposable
{
    // 不接管在线宿主默认世界与进程级异常开关；节点与 verifier 都只在自己的世界执行。
    private readonly EcsWorldShard _runtime = CreateRuntime(gameData);

    private static EcsWorldShard CreateRuntime(GameDataCatalog catalog)
    {
        InlineCombatTriggerRegistry.EnsureInitialized();
        return new EcsWorldShard(catalog, fiberName: "daohua-catch-up", setWorldAsDefault: false, ownCloseSystemTry: false);
    }
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前执行器使用的配表。</summary>
    public GameDataCatalog GameData { get; } = gameData ?? throw new ArgumentNullException(nameof(gameData));

    /// <summary>
    /// 在独立 ECS 世界内推进一个玩家。输入 bundle 与余额必须来自同一冻结快照；stamp 只影响持久化记录时间戳，
    /// 不参与领域结算，因此验证端可使用固定值比较 section 字节。
    /// </summary>
    public async ValueTask<DeterministicCatchUpResult> AdvanceAsync(
        PlayerStateBundle bundle,
        IReadOnlyCollection<AssetBalance>? balances,
        DateTimeOffset target,
        DateTimeOffset stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (target < bundle.Head.LastSimulatedAt)
            throw new ArgumentException("补算目标时刻不能早于源模拟时刻。", nameof(target));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = SectionCodec.DecodeAggregate(bundle);
            var sourceVersion = state.StateVersion;
            _runtime.CreatePlayer(state, balances);
            try
            {
                _runtime.MutatePlayer(bundle.Identity.PlayerId, aggregate =>
                    aggregate.RebindGameDataFingerprint(GameData.Fingerprint));
                await _runtime.RunPlayerOnceAsync(bundle.Identity.PlayerId, target, cancellationToken).ConfigureAwait(false);
                var snapshot = _runtime.ExportSnapshot(bundle.Identity.PlayerId);
                var simulation = _runtime.DrainSimulationResult(bundle.Identity.PlayerId);
                var dirty = _runtime.GetDirtySections(bundle.Identity.PlayerId);
                var writes = SectionCodec.EncodeAggregate(snapshot.State, dirty, stamp);
                return new DeterministicCatchUpResult(
                    sourceVersion,
                    simulation?.StateVersion ?? snapshot.State.StateVersion,
                    snapshot.State.LastSimulatedAt,
                    writes,
                    simulation?.AssetDeltas ?? [],
                    (long)(simulation?.CompletedActions ?? 0));
            }
            finally
            {
                _runtime.RemovePlayer(bundle.Identity.PlayerId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _gate.Dispose();
    }
}

/// <summary>确定性补算的领域结果；不携带网络协议、数据库连接或宿主对象。</summary>
public sealed record DeterministicCatchUpResult(
    long SourceStateVersion,
    long TargetStateVersion,
    DateTimeOffset LastSimulatedAt,
    IReadOnlyList<SectionWrite> Sections,
    IReadOnlyList<AssetDelta> AssetDeltas,
    long CompletedActions);
