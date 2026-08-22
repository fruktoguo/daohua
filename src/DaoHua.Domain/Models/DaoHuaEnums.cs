// ==============================================================================
// 文件名称: DaoHuaEnums.cs
// 主要职责: 定义修仙领域模型中通用的核心业务枚举类型。
// 核心逻辑 / 定义内容:
//   1. 基础行为类型、战斗流派与伤害属性枚举（DaoHuaActionType, DaoHuaCombatStyle, DaoHuaDamageType）。
//   2. 玩家在线模式、动作执行状态与推进模式枚举（DaoHuaPresenceMode, DaoHuaActionStatus, DaoHuaActionExecutionKind）。
//   3. 状态增量同步脏段掩码枚举（DaoHuaDirtySections）。
// ==============================================================================

namespace DaoHua.Domain.Models;

/// <summary>
/// 角色行为/动作的四大基本类型枚举。
/// </summary>
public enum DaoHuaActionType
{
    /// <summary>打坐修炼，吸纳灵气。</summary>
    Cultivation = 0,
    /// <summary>探索采集，获取天材地宝。</summary>
    Exploration = 1,
    /// <summary>炼丹制药，调和灵力。</summary>
    Alchemy = 2,
    /// <summary>降妖除魔/战斗历练。</summary>
    Combat = 3
}

/// <summary>
/// 战斗流派/武道门类枚举。
/// </summary>
public enum DaoHuaCombatStyle
{
    /// <summary>御剑流派，偏敏捷与暴击。</summary>
    Sword = 0,
    /// <summary>重刃流派，偏力量与破防。</summary>
    Blade = 1,
    /// <summary>符箓流派，偏法系控制与附伤。</summary>
    Talisman = 2,
    /// <summary>五行奇术，属性克制与均衡发展。</summary>
    FiveElements = 3,
    /// <summary>横练肉身/体修，高气血防御与反击。</summary>
    Body = 4
}

/// <summary>
/// 伤害属性类型枚举，参与五行克制与抗性减免计算。
/// </summary>
public enum DaoHuaDamageType
{
    /// <summary>物理/纯内劲伤害。</summary>
    Physical = 0,
    /// <summary>水系/冰系灵力伤害。</summary>
    Water = 1,
    /// <summary>木系/自然灵力伤害。</summary>
    Nature = 2,
    /// <summary>火系/炎阳灵力伤害。</summary>
    Fire = 3
}

/// <summary>
/// 玩家在线与挂机状态模式枚举。
/// </summary>
public enum DaoHuaPresenceMode
{
    /// <summary>实时在线，高频接收网络推送。</summary>
    Online = 0,
    /// <summary>离线挂机（轻度计算），由服务端定期或按需补算。</summary>
    IdleOffline = 1,
    /// <summary>完全离线（冷数据），上线时一次性快进追帧补算。</summary>
    FullyOffline = 2
}

/// <summary>
/// 动作队列中单个条目的执行状态枚举。
/// </summary>
public enum DaoHuaActionStatus
{
    /// <summary>排队等待中。</summary>
    Queued = 0,
    /// <summary>当前正在执行中。</summary>
    Running = 1,
    /// <summary>因材料不足或前置条件不满足而受阻。</summary>
    Blocked = 2
}

/// <summary>
/// 动作执行推进模式分类。
/// </summary>
public enum DaoHuaActionExecutionKind
{
    /// <summary>标准计时推进模式。</summary>
    Timed = 0,
    /// <summary>战斗副本遭遇推进模式。</summary>
    CombatEncounter = 1,
    /// <summary>世界地图节点间移动旅行模式。</summary>
    Travel = 2
}

/// <summary>
/// 玩家状态脏标记位掩码，用于增量同步与局部持久化优化。
/// </summary>
[Flags]
public enum DaoHuaDirtySections
{
    /// <summary>无变更。</summary>
    None = 0,
    /// <summary>队列条目或循环配置发生变动。</summary>
    Queue = 1 << 0,
    /// <summary>背包物品数量或类型发生变动。</summary>
    Inventory = 1 << 1,
    /// <summary>Buff 状态或层数发生变动。</summary>
    Buffs = 1 << 2,
    /// <summary>角色累计统计/修为数据发生变动。</summary>
    Stats = 1 << 3,
    /// <summary>产生了新的事件日志。</summary>
    Events = 1 << 4,
    /// <summary>在线状态模式发生变动。</summary>
    Presence = 1 << 5,
    /// <summary>队列执行进度（秒数/历练阶段）发生变动。</summary>
    QueueProgress = 1 << 6,
    /// <summary>战斗配置、属性或资源发生变动。</summary>
    Combat = 1 << 7,
    /// <summary>世界地图坐标或所处节点发生变动。</summary>
    World = 1 << 8,
    /// <summary>全部状态均发生变动（全量同步）。</summary>
    All = Queue | Inventory | Buffs | Stats | Events | Presence | QueueProgress | Combat | World
}

