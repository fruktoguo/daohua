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
/// 战斗与装备系统容量硬限制与槽位常量。
/// </summary>
public static class DaoHuaCombatLimits
{
    /// <summary>主动技能装备槽位上限（4格）。</summary>
    public const int AbilitySlotCount = 4;
    /// <summary>战斗消耗品快捷槽位上限（3格）。</summary>
    public const int ConsumableSlotCount = 3;
    /// <summary>自动使用物品槽位上限（3格）。</summary>
    public const int AutoUseSlotCount = 3;
    /// <summary>每个槽位最多支持的触发条件规则数（4条）。</summary>
    public const int TriggerCountPerSlot = 4;
    /// <summary>装备穿戴部位槽位总数（14格）。</summary>
    public const int EquipmentSlotCount = 14;
    /// <summary>装备最高允许强化等级（+20）。</summary>
    public const int MaxEnhancementLevel = 20;
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

/// <summary>
/// 核心内置动作标识符常量。
/// </summary>
public static class DaoHuaActionIds
{
    /// <summary>采集灵草动作。</summary>
    public const string GatherHerbs = "explore.gather-herbs";
    /// <summary>炼制聚气丹动作。</summary>
    public const string RefineQiPill = "alchemy.refine-qi-pill";
    /// <summary>吐纳运功吸纳灵气动作。</summary>
    public const string DrawQi = "cultivation.draw-qi";
    /// <summary>猎杀灵兽动作。</summary>
    public const string HuntQiBeast = "combat.hunt-qi-beast";
    /// <summary>扫荡暮色谷副本动作。</summary>
    public const string SweepTwilightValley = "combat.sweep-twilight-valley";
    /// <summary>大地图旅行移动动作。</summary>
    public const string WorldTravel = "world.travel";
}

/// <summary>
/// 核心内置物品/货币/资源标识符常量。
/// </summary>
public static class DaoHuaItemIds
{
    /// <summary>灵草材料。</summary>
    public const string SpiritHerb = "item.spirit-herb";
    /// <summary>聚气丹消耗品。</summary>
    public const string QiGatheringPill = "item.qi-gathering-pill";
    /// <summary>灵石基础流通货币。</summary>
    public const string Spirit = "currency.spirit";
    /// <summary>灵气修为资源。</summary>
    public const string Qi = "resource.qi";
    /// <summary>疗伤灵药。</summary>
    public const string HealingElixir = "item.healing-elixir";
    /// <summary>回生甘露。</summary>
    public const string RejuvenatingDew = "item.rejuvenating-dew";
    /// <summary>清心灵茶。</summary>
    public const string SpiritTea = "item.spirit-tea";
    /// <summary>赤炎灵符。</summary>
    public const string FireTalisman = "item.fire-talisman";
    /// <summary>灵兽内丹。</summary>
    public const string QiBeastCore = "item.qi-beast-core";
    /// <summary>云锦糖。</summary>
    public const string CloudSugar = "item.cloud-sugar";
    /// <summary>青灵玉乳。</summary>
    public const string VerdantSpiritMilk = "item.verdant-spirit-milk";
    /// <summary>碧落天乳。</summary>
    public const string AzureSpiritMilk = "item.azure-spirit-milk";
    /// <summary>灵脉真解功法书。</summary>
    public const string SpiritPulseManual = "item.spirit-pulse-manual";
    /// <summary>小型灵气锦囊。</summary>
    public const string SmallQiChest = "item.small-qi-chest";
    /// <summary>采药定神香。</summary>
    public const string GatheringIncense = "item.gathering-incense";
    /// <summary>地心丹火。</summary>
    public const string AlchemyFlame = "item.alchemy-flame";
    /// <summary>怨灵长弓（装备）。</summary>
    public const string CursedSpiritBow = "equipment.cursed-spirit-bow";
    /// <summary>狂烈灵枪（装备）。</summary>
    public const string FuriousSpiritSpear = "equipment.furious-spirit-spear";
    /// <summary>狮鹫重盾（装备）。</summary>
    public const string GriffinSpiritBulwark = "equipment.griffin-spirit-bulwark";
    /// <summary>破甲灵弩（装备）。</summary>
    public const string SunderingSpiritCrossbow = "equipment.sundering-spirit-crossbow";
}

/// <summary>
/// 核心内置 Buff 增益/减益效果标识符常量。
/// </summary>
public static class DaoHuaBuffIds
{
    /// <summary>聚气增益状态。</summary>
    public const string QiGathering = "buff.qi-gathering";
    /// <summary>灵茶清心状态。</summary>
    public const string SpiritTea = "buff.spirit-tea";
    /// <summary>赤炎符威能状态。</summary>
    public const string FireTalisman = "buff.fire-talisman";
    /// <summary>采药福泽增益。</summary>
    public const string GatheringBlessing = "buff.gathering-blessing";
    /// <summary>炼丹专注目力增益。</summary>
    public const string AlchemyFocus = "buff.alchemy-focus";
}

/// <summary>
/// 核心内置战斗遭遇副本标识符常量。
/// </summary>
public static class DaoHuaCombatEncounterIds
{
    /// <summary>灵兽巢穴遭遇。</summary>
    public const string QiBeast = "encounter.qi-beast";
    /// <summary>暮色幽谷副本。</summary>
    public const string TwilightValley = "encounter.twilight-valley";
}

/// <summary>
/// 核心内置世界地图标识符常量。
/// </summary>
public static class DaoHuaWorldMapIds
{
    /// <summary>青云山脉大地图。</summary>
    public const string QingyunMountains = "map.qingyun-mountains";
}

/// <summary>
/// 核心内置世界地图节点地点标识符常量。
/// </summary>
public static class DaoHuaWorldNodeIds
{
    /// <summary>百草幽谷。</summary>
    public const string HerbVale = "node.herb-vale";
    /// <summary>云海天门。</summary>
    public const string CloudGate = "node.cloud-gate";
    /// <summary>灵泉灵脉。</summary>
    public const string SpiritSpring = "node.spirit-spring";
    /// <summary>落霞危崖。</summary>
    public const string SunsetRidge = "node.sunset-ridge";
    /// <summary>暮光古隘。</summary>
    public const string TwilightPass = "node.twilight-pass";
    /// <summary>玄石灵矿。</summary>
    public const string StoneVein = "node.stone-vein";
    /// <summary>云端坊市。</summary>
    public const string CloudMarket = "node.cloud-market";
    /// <summary>上古祭台。</summary>
    public const string AncientTerrace = "node.ancient-terrace";
}

