using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class ExpiringRetryCacheTests
{
    [Fact]
    public void CapacityEvictsOnlyEarliestExpiryAndRefreshDoesNotLeaveStaleIndexEntry()
    {
        var at = DateTimeOffset.UnixEpoch;
        var cache = new ExpiringRetryCache<int, string>(2, TimeSpan.FromMinutes(5));
        cache.Set(1, "旧", at);
        cache.Set(2, "其次", at);
        cache.Set(1, "刷新", at.AddMinutes(1));
        cache.Set(3, "新", at.AddMinutes(1));

        Assert.False(cache.TryGet(2, at.AddMinutes(1), out _));
        cache.PruneExpired(at.AddMinutes(5));
        Assert.True(cache.TryGet(1, at.AddMinutes(5), out var refreshed));
        Assert.Equal("刷新", refreshed);
        Assert.True(cache.TryGet(3, at.AddMinutes(5), out _));
        Assert.False(cache.TryGet(1, at.AddMinutes(6), out _));
        cache.Remove(3);
        Assert.False(cache.TryGet(3, at.AddMinutes(5), out _));
    }

    [Fact]
    public void ParallelFailureUpdatesAreAtomicAndExpiryRestartsFailureCount()
    {
        var at = DateTimeOffset.UnixEpoch;
        var cache = new ExpiringRetryCache<int, int>(4, TimeSpan.FromMinutes(1));
        Parallel.For(0, 50, _ => cache.AddOrUpdate(1, 1, static (prior, next) => prior + next, at));

        Assert.True(cache.TryGet(1, at, out var failures));
        Assert.Equal(50, failures);
        cache.AddOrUpdate(1, 1, static (prior, next) => prior + next, at.AddMinutes(1));
        Assert.True(cache.TryGet(1, at.AddMinutes(1), out var restarted));
        Assert.Equal(1, restarted);
    }
}
