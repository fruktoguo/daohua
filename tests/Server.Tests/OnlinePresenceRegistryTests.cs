using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 在线存在感注册表（OnlinePresenceRegistry）的机器人隐身契约：
/// 1. 机器人（isBot = true）不写入节点倒排索引、不出现在 SnapshotAtNode（「附近玩家」）列表中；
/// 2. 机器人上下线、换节点与变更形象不向同节点修士触发 PresenceChanged 广播；
/// 3. 机器人自身的 IsOnline / GetNodeId / SnapshotOnlinePlayerIds / OnlineChanged / NodeChanged 仍完整工作。
/// </summary>
public sealed class OnlinePresenceRegistryTests
{
    [Fact]
    public async Task BotAccount_HiddenFromNearbySnapshotAndSuppressesNeighborPresenceBroadcast()
    {
        var presence = new OnlinePresenceRegistry();
        var presenceBroadcasts = new List<IReadOnlyList<Guid>>();
        var onlineSignals = new List<Guid>();
        var nodeSignals = new List<Guid>();
        presence.PresenceChanged += ids => { presenceBroadcasts.Add(ids); return ValueTask.CompletedTask; };
        presence.OnlineChanged += id => { onlineSignals.Add(id); return ValueTask.CompletedTask; };
        presence.NodeChanged += id => { nodeSignals.Add(id); return ValueTask.CompletedTask; };

        var human = Guid.NewGuid();
        var bot = Guid.NewGuid();
        const string nodeA = "node.yunxi-village";
        const string nodeB = "node.herb-valley";

        await presence.RegisterAsync(human, "青云修士", nodeA, realmLevel: 12);
        presenceBroadcasts.Clear();
        onlineSignals.Clear();
        nodeSignals.Clear();

        // 机器人上线同节点：自身收到上线与节点信号，但不惊扰邻近人类，也不出现在附近列表。
        await presence.RegisterAsync(bot, "采药小童", nodeA, realmLevel: 18, isBot: true);

        Assert.True(presence.IsOnline(bot));
        Assert.Equal(nodeA, presence.GetNodeId(bot));
        Assert.Equal(2, presence.OnlineCount);
        Assert.Contains(bot, presence.SnapshotOnlinePlayerIds());
        Assert.Equal([bot], onlineSignals);
        Assert.Equal([bot], nodeSignals);
        Assert.Empty(presenceBroadcasts);

        var nearbyAtA = presence.SnapshotAtNode(nodeA);
        var single = Assert.Single(nearbyAtA);
        Assert.Equal(human.ToString("D"), single.PlayerId);
        Assert.Empty(presence.SnapshotAtNode(nodeB));

        // 机器人换节点与换形象：自身 NodeChanged 正常触发，人类附近列表与通知完全无感。
        nodeSignals.Clear();
        await presence.UpdateNodeAsync(bot, nodeB);
        await presence.UpdateAvatarAsync(bot, "player-male");
        Assert.Equal(nodeB, presence.GetNodeId(bot));
        Assert.Equal([bot], nodeSignals);
        Assert.Empty(presenceBroadcasts);
        Assert.Empty(presence.SnapshotAtNode(nodeB));

        // 机器人离线：自身触发 OnlineChanged，人类不收到 PresenceChanged。
        onlineSignals.Clear();
        await presence.UnregisterAsync(bot);
        Assert.False(presence.IsOnline(bot));
        Assert.Equal([bot], onlineSignals);
        Assert.Empty(presenceBroadcasts);
    }
}
