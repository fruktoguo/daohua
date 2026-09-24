namespace Domain.Player;

/// <summary>
/// 玩家聚合状态的持久化拆分边界（Section）。
/// 各 Section 在 PostgreSQL 存储中按行隔离，各自具备单调版本号与变更脏标记，
/// 允许高频变更（如 ActionQueue、Progression）与低频变更（如 Settings、World）差量刷盘，
/// 避免整个聚合大快照的高频写放大与网络锁竞争。
/// 落盘文档形状、存储键与编解码统一收拢在 <see cref="Domain.Persistence.SectionCodec"/>。
/// </summary>
public enum PlayerSectionKind : byte
{
    /// <summary>境界等级、灵气修为、历史总修为与连续模拟进位</summary>
    Progression = 1,
    /// <summary>世界坐标、所在地图节点与探索随机数种子</summary>
    World = 2,
    /// <summary>战斗属性快照（生命、灵力、攻防）</summary>
    Combat = 3,
    /// <summary>活跃 Buff 增益/减益状态及其到期时间</summary>
    Buffs = 4,
    /// <summary>已装备物品实例与已领悟功法神通列表</summary>
    Loadout = 5,
    /// <summary>玩家个人偏好设置（语言、通知、循环模式、快捷自动使用槽位）</summary>
    Settings = 6,
    /// <summary>正在排队与执行中的行动队列（支持进度与重复次数）</summary>
    ActionQueue = 7,
    /// <summary>斗法玩法状态：遭遇计数、失败冷却、暂存区与战斗收益记录</summary>
    Duel = 8
}
