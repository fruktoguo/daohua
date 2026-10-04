namespace Server.Clean.AntiMultiOpen;

/// <summary>单实例设备在线额度；字典与桶的生命周期在同一锁内，避免空桶移除时遗失并发准入。</summary>
public sealed class DeviceQuotaRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<Guid, int>> _devices = new(StringComparer.Ordinal);

    /// <summary>当前设备数量。</summary>
    public int DeviceCount { get { lock (_gate) return _devices.Count; } }

    /// <summary>同账号重连增加引用；不同账号按配置额度登记。</summary>
    public bool TryEnter(string fingerprint, Guid playerId, int maxAccounts)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(fingerprint, out var players)) _devices.Add(fingerprint, players = []);
            if (players.TryGetValue(playerId, out var refs)) { players[playerId] = refs + 1; return true; }
            if (players.Count >= maxAccounts) return false;
            players.Add(playerId, 1);
            return true;
        }
    }

    /// <summary>减少引用，最后一个账号离开时原子摘除设备桶。</summary>
    public void Leave(string fingerprint, Guid playerId)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(fingerprint, out var players) || !players.TryGetValue(playerId, out var refs)) return;
            if (refs == 1) players.Remove(playerId); else players[playerId] = refs - 1;
            if (players.Count == 0) _devices.Remove(fingerprint);
        }
    }

    /// <summary>读取一个设备的不同在线账号数。</summary>
    public int OnlineAccountCount(string fingerprint)
    {
        lock (_gate) return _devices.TryGetValue(fingerprint, out var players) ? players.Count : 0;
    }
}
