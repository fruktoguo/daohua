namespace Server.Clean.AntiMultiOpen;

/// <summary>
/// 设备指纹并发账号配额注册表（纯进程内实现，无外部存储依赖）。
/// 键为客户端上报的设备指纹哈希，值为「玩家 ID → 引用计数」：
/// 同一账号在同一设备上的重连/顶替会先增后减，计数不漂移；
/// 连接断开即引用归零，socket 生命周期即真值，不需要心跳续期。
/// </summary>
public sealed class DeviceQuotaRegistry
{
    private readonly ConcurrentDictionary<string, Dictionary<Guid, int>> _devices = new(StringComparer.Ordinal);

    /// <summary>当前被跟踪的设备指纹数量</summary>
    public int DeviceCount => _devices.Count;

    /// <summary>
    /// 尝试把玩家登记到指定设备。同一玩家重复登记只增加引用计数，不占用新的配额名额；
    /// 设备下不同账号数达到上限时返回 false。
    /// </summary>
    public bool TryEnter(string fingerprint, Guid playerId, int maxAccounts)
    {
        var players = _devices.GetOrAdd(fingerprint, _ => new Dictionary<Guid, int>());
        lock (players)
        {
            if (players.TryGetValue(playerId, out var refs))
            {
                players[playerId] = refs + 1;
                return true;
            }
            if (players.Count >= maxAccounts) return false;
            players[playerId] = 1;
            return true;
        }
    }

    /// <summary>
    /// 释放玩家在指定设备上的一个引用；计数归零时移除该玩家，设备为空时移除整条记录。
    /// </summary>
    public void Leave(string fingerprint, Guid playerId)
    {
        if (!_devices.TryGetValue(fingerprint, out var players)) return;
        lock (players)
        {
            if (players.TryGetValue(playerId, out var refs))
            {
                if (refs <= 1) players.Remove(playerId);
                else players[playerId] = refs - 1;
            }
            if (players.Count == 0) _devices.TryRemove(new KeyValuePair<string, Dictionary<Guid, int>>(fingerprint, players));
        }
    }

    /// <summary>查询指定设备当前在线的不同账号数（诊断用）</summary>
    public int OnlineAccountCount(string fingerprint) =>
        _devices.TryGetValue(fingerprint, out var players) ? players.Count : 0;
}
