using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>在线断线宽限注册表（总纲18 §14.3）的登记/取消/到期/停机收尾行为。</summary>
public sealed class DisconnectGraceRegistryTests
{
    [Fact]
    public async Task ScheduledPlayerStaysOnlineUntilGraceExpiresThenTearsDown()
    {
        var presence = new OnlinePresenceRegistry();
        var runtime = new RecordingPlayerRuntime();
        var registry = CreateRegistry(presence, runtime, graceSeconds: 1);
        var player = Guid.NewGuid();
        await presence.RegisterAsync(player, "测试", "node.yunxi-village");
        var expired = new List<Guid>();
        registry.Expired += playerId => { expired.Add(playerId); return ValueTask.CompletedTask; };

        var disconnectedAt = DateTimeOffset.UtcNow.AddMilliseconds(-200);
        await registry.ScheduleAsync(player, disconnectedAt);
        Assert.True(presence.IsOnline(player));
        Assert.Empty(runtime.Removed);

        await WaitForAsync(() => runtime.Removed.Count == 1);
        Assert.False(presence.IsOnline(player));
        var removal = Assert.Single(runtime.Removed);
        Assert.Equal(player, removal.PlayerId);
        Assert.True(removal.Flush);
        Assert.Equal(disconnectedAt, removal.OfflineSince);
        Assert.Equal([player], expired);
    }

    [Fact]
    public async Task CancelWithinGracePreventsTeardown()
    {
        var presence = new OnlinePresenceRegistry();
        var runtime = new RecordingPlayerRuntime();
        var registry = CreateRegistry(presence, runtime, graceSeconds: 1);
        var player = Guid.NewGuid();
        await presence.RegisterAsync(player, "测试", null);

        await registry.ScheduleAsync(player, DateTimeOffset.UtcNow);
        await registry.CancelAsync(player, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.True(presence.IsOnline(player));
        Assert.Empty(runtime.Removed);
    }

    [Fact]
    public async Task CancelDuringRunningTeardownWaitsForCompletion()
    {
        var presence = new OnlinePresenceRegistry();
        var runtime = new RecordingPlayerRuntime { BlockRemove = true };
        var registry = CreateRegistry(presence, runtime, graceSeconds: 1);
        var player = Guid.NewGuid();
        await presence.RegisterAsync(player, "测试", null);
        var disconnectedAt = DateTimeOffset.UtcNow;

        await registry.ScheduleAsync(player, disconnectedAt);
        // 等收尾真正进入 RemoveAsync：注销已完成、落盘卸载进行中。
        await runtime.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelTask = registry.CancelAsync(player, CancellationToken.None).AsTask();
        Assert.False(cancelTask.IsCompleted);

        runtime.RemoveRelease.SetResult();
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(presence.IsOnline(player));
        Assert.Single(runtime.Removed);
    }

    [Fact]
    public async Task StopAsyncDrainsAllPendingEntriesWithOriginalAnchors()
    {
        var presence = new OnlinePresenceRegistry();
        var runtime = new RecordingPlayerRuntime();
        var registry = CreateRegistry(presence, runtime, graceSeconds: 3600);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await presence.RegisterAsync(first, "甲", null);
        await presence.RegisterAsync(second, "乙", null);
        var firstAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var secondAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await registry.ScheduleAsync(first, firstAt);
        await registry.ScheduleAsync(second, secondAt);
        await registry.StopAsync(CancellationToken.None);

        Assert.False(presence.IsOnline(first));
        Assert.False(presence.IsOnline(second));
        Assert.Equal(2, runtime.Removed.Count);
        var byPlayer = runtime.Removed.ToDictionary(item => item.PlayerId);
        Assert.Equal(firstAt, byPlayer[first].OfflineSince);
        Assert.Equal(secondAt, byPlayer[second].OfflineSince);
    }

    [Fact]
    public async Task ZeroGraceRunsTeardownInline()
    {
        var presence = new OnlinePresenceRegistry();
        var runtime = new RecordingPlayerRuntime();
        var registry = CreateRegistry(presence, runtime, graceSeconds: 0);
        var player = Guid.NewGuid();
        await presence.RegisterAsync(player, "测试", null);
        var disconnectedAt = DateTimeOffset.UtcNow;

        // 宽限为 0：Schedule 返回时收尾已完成（断开即卸载的旧语义）。
        await registry.ScheduleAsync(player, disconnectedAt);
        Assert.False(presence.IsOnline(player));
        var removal = Assert.Single(runtime.Removed);
        Assert.Equal(disconnectedAt, removal.OfflineSince);
    }

    private static DisconnectGraceRegistry CreateRegistry(OnlinePresenceRegistry presence,
        RecordingPlayerRuntime runtime, int graceSeconds)
    {
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = "Host=unused;Database=unused",
                ["OfflineIdle:GraceSeconds"] = graceSeconds.ToString()
            }).Build());
        return new DisconnectGraceRegistry(presence, runtime, new FixedClock(), options,
            NullLogger<DisconnectGraceRegistry>.Instance);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }

    private sealed class FixedClock : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public bool IsDeterministic => false;
        public void AdvanceTo(DateTimeOffset target) => throw new NotSupportedException();
    }

    /// <summary>记录型运行时替身：只响应卸载调用，其余通道本测试不触达。</summary>
    private sealed class RecordingPlayerRuntime : IPlayerRuntime
    {
        public readonly ConcurrentQueue<(Guid PlayerId, bool Flush, DateTimeOffset? OfflineSince)> Removed = new();
        public readonly TaskCompletionSource RemoveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource RemoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockRemove;

        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated { add { } remove { } }
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted { add { } remove { } }
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
            long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince,
            CancellationToken cancellationToken = default)
        {
            Removed.Enqueue((playerId, flush, offlineSince));
            RemoveStarted.TrySetResult();
            if (BlockRemove) await RemoveRelease.Task;
        }
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
