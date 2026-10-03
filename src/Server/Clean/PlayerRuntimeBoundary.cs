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
    DateTimeOffset UtcNow { get; }
    bool IsDeterministic { get; }
    void AdvanceTo(DateTimeOffset target);
}

public sealed class PlayerSimulationClock : IPlayerSimulationClock
{
    private long _utcTicks;

    private PlayerSimulationClock(DateTimeOffset utcNow, bool isDeterministic)
    {
        _utcTicks = utcNow.UtcTicks;
        IsDeterministic = isDeterministic;
    }

    public DateTimeOffset UtcNow => IsDeterministic
        ? new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero)
        : DateTimeOffset.UtcNow;
    public bool IsDeterministic { get; }

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
/// 玩家运行时宿主边界。HTTP、WebSocket 和运维只依赖这组消息型操作，
/// 具体是 OOP 聚合单写者还是 ECS 世界由服务装配决定。
/// </summary>
public interface IPlayerRuntime : IAsyncDisposable
{
    int LoadedCount { get; }
    event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
    event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
    ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default);
    /// <summary>读取已加载玩家的最新快照；未加载返回 null（不触发装载，供估值等只读旁路）。</summary>
    PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId);
    ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default);
    ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default);
    ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default);
    ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default);
    DateTimeOffset? NextAdvanceBoundaryUtc { get; }
    ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default);
    IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId);
    ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default);
}

/// <summary>跨 OOP/ECS 宿主共用的运行时快照载荷。</summary>
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

/// <summary>跨 OOP/ECS 宿主共用的命令回执载荷。</summary>
public sealed record PlayerCommandResult(
    bool Success,
    string Code,
    string Message,
    PlayerRuntimeSnapshot Snapshot,
    SimulationResult? Simulation = null,
    IReadOnlyList<LogEventDto>? LogEvents = null);
