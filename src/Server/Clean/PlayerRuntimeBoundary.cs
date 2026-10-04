using Domain.Persistence;
using Domain.Simulation;
using Protocol;
using System.Globalization;

namespace Server.Clean;

/// <summary>
/// 玩家业务模拟时钟。默认绑定系统 UTC；基准实验可通过 DAOHUA_BENCH_SIMULATION_TARGET
/// 固定推进截止时间。租约、会话安全超时和数据库时间戳不使用此时钟。
/// </summary>
public interface IPlayerSimulationClock
{
    /// <summary>当前业务模拟时刻（UTC）；确定性模式下只随 <see cref="AdvanceTo"/> 前进。</summary>
    DateTimeOffset UtcNow { get; }
    /// <summary>是否为基准实验的确定性时钟：true 时模拟时刻与真实墙钟脱钩，必须显式推进。</summary>
    bool IsDeterministic { get; }
    /// <summary>把确定性模拟时钟推进到目标时刻；禁止倒退，非确定性模式调用抛异常。</summary>
    void AdvanceTo(DateTimeOffset target);
}

/// <summary>
/// <see cref="IPlayerSimulationClock"/> 的双模实现：非确定性模式直读系统 UTC；
/// 确定性模式把目标时刻保存在原子字段中，由基准端点显式推进，供在线/离线补算严格对齐。
/// </summary>
public sealed class PlayerSimulationClock : IPlayerSimulationClock
{
    private long _utcTicks;

    private PlayerSimulationClock(DateTimeOffset utcNow, bool isDeterministic)
    {
        _utcTicks = utcNow.UtcTicks;
        IsDeterministic = isDeterministic;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => IsDeterministic
        ? new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero)
        : DateTimeOffset.UtcNow;
    /// <inheritdoc />
    public bool IsDeterministic { get; }

    /// <inheritdoc />
    public void AdvanceTo(DateTimeOffset target)
    {
        if (!IsDeterministic) throw new InvalidOperationException("当前服务没有启用基准模拟时钟。");
        var ticks = target.ToUniversalTime().UtcTicks;
        while (true)
        {
            var current = Interlocked.Read(ref _utcTicks);
            if (ticks < current) throw new InvalidOperationException("基准模拟时钟不能倒退。");
            if (Interlocked.CompareExchange(ref _utcTicks, ticks, current) == current) return;
        }
    }

    /// <summary>
    /// 按环境变量装配：DAOHUA_BENCH_SIMULATION_TARGET 存在则创建固定起点的确定性时钟，
    /// 否则回退系统 UTC；值非 ISO-8601 UTC 时 fail-fast 抛异常。
    /// </summary>
    public static PlayerSimulationClock FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("DAOHUA_BENCH_SIMULATION_TARGET");
        if (string.IsNullOrWhiteSpace(value)) return new(DateTimeOffset.UtcNow, false);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            throw new InvalidOperationException("DAOHUA_BENCH_SIMULATION_TARGET 必须是 ISO-8601 UTC 时间。");
        return new(parsed.ToUniversalTime(), true);
    }
}

/// <summary>
/// 玩家运行时宿主边界。HTTP、WebSocket 和运维只依赖这组消息型操作；
/// 当前唯一实现是 ECS 世界适配器（历史 OOP 聚合单写者实现已移除）。
/// </summary>
public interface IPlayerRuntime : IAsyncDisposable
{
    /// <summary>当前驻留内存的已加载玩家实体数。</summary>
    int LoadedCount { get; }
    /// <summary>玩家快照推进后向订阅者（WebSocket 推送）回调；始终在玩家写锁外触发。</summary>
    event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
    /// <summary>一批战报日志产生后向订阅者回调；同样在玩家写锁外触发。</summary>
    event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
    /// <summary>装载玩家到内存：读取持久化状态并补算离线欠债；玩家不存在抛异常。</summary>
    ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default);
    /// <summary>读取已加载玩家的最新快照；未加载返回 null（不触发装载，供估值等只读旁路）。</summary>
    PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId);
    /// <summary>
    /// 执行一条玩家领域命令：单写者串行入口，含幂等回执、乐观版本校验与持久化提交。
    /// 失败不抛异常而以 <see cref="PlayerCommandResult"/> 错误码返回（领域违例、所有权丢失等）。
    /// </summary>
    ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default);
    /// <summary>把该玩家脏分段写入 durable 刷盘队列；force 跳过分段脏龄去抖立即写。</summary>
    ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default);
    /// <summary>并发刷新全部已加载玩家的脏分段；任一失败聚合抛出，不静默。</summary>
    ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default);
    /// <summary>统一推进全部已加载玩家模拟到 target（UTC）并逐玩家提交持久化与推送。</summary>
    ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default);
    /// <summary>下一次需要全量推进的最早到期时刻（UTC）；null 表示无待结算行动，后台可空转。</summary>
    DateTimeOffset? NextAdvanceBoundaryUtc { get; }
    /// <summary>卸载玩家实体并清缓存；flush 为 true 时先转离线并强制落盘，不丢未提交状态。</summary>
    ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default);
    /// <summary>玩家战报回放缓冲的当前快照；仅进程内存，供断线重连补齐日志。</summary>
    IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId);
    /// <summary>全部已加载玩家快照的稳定排序副本；供批量推进度量与运维旁路读取。</summary>
    ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default);
}

/// <summary>跨宿主共用的运行时快照载荷。</summary>
public sealed record PlayerRuntimeSnapshot(
    Guid PlayerId,
    string AccountId,
    string DisplayName,
    long StateVersion,
    DateTimeOffset LastSimulatedAt,
    PlayerAggregateState State,
    int QueuedActionCount,
    IReadOnlyList<AssetBalanceRecord> Assets,
    IReadOnlySet<PlayerSectionKind>? TouchedSections = null,
    bool? WireAttributesChanged = null);

/// <summary>跨宿主共用的命令回执载荷。</summary>
public sealed record PlayerCommandResult(
    bool Success,
    string Code,
    string Message,
    PlayerRuntimeSnapshot Snapshot,
    SimulationResult? Simulation = null,
    IReadOnlyList<LogEventDto>? LogEvents = null);
