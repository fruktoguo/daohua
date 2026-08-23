// ==============================================================================
// 文件名称: IdConstants.cs
// 主要职责: 集中定义修仙世界预置原型、动作、物品、Buff、副本遭遇、地图与节点的静态唯一标识符（ID）常量。
// 核心逻辑 / 定义内容:
//   1. 预置基础行为与动作原型 ID 常量定义（ActionIds）。
//   2. 预置基础物品、灵药、法宝装备与货币资源 ID 常量定义（ItemIds）。
//   3. 预置状态增益/减益 Buff ID 常量定义（BuffIds）。
//   4. 预置战斗副本遭遇与世界地图/地点节点 ID 常量定义（CombatEncounterIds, WorldMapIds, WorldNodeIds）。
// ==============================================================================

namespace Domain.Constants;

/// <summary>
/// 核心内置动作标识符常量。
/// </summary>
public static class ActionIds
{
    /// <summary>采集凝血草动作。</summary>
    public const string GatherHerbs = "explore.gather-ningxue-grass";
    /// <summary>引气丹动作。</summary>
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
public static class ItemIds
{
    /// <summary>凝血草材料。</summary>
    public const string SpiritHerb = "item.ningxue-grass";
    /// <summary>引气丹消耗品。</summary>
    public const string QiGatheringPill = "item.qi-gathering-pill";
    /// <summary>止血散消耗品。</summary>
    public const string ZhixuePowder = "item.zhixue-powder";
    /// <summary>顺气丸消耗品。</summary>
    public const string ShunqiPill = "item.shunqi-pill";
    /// <summary>通脉丹消耗品。</summary>
    public const string TongmaiPill = "item.tongmai-pill";
    /// <summary>灵石基础流通货币。</summary>
    public const string Spirit = "currency.spirit-stone";
    /// <summary>灵气修为资源。</summary>
    public const string Qi = "resource.qi";
    /// <summary>破碎灵核。</summary>
    public const string QiBeastCore = "item.broken-spirit-core";
    /// <summary>兽皮。</summary>
    public const string BeastHide = "item.beast-hide";
    /// <summary>兽骨。</summary>
    public const string BeastBone = "item.beast-bone";
    /// <summary>百炼铁。</summary>
    public const string RefinedIron = "item.refined-iron";
    /// <summary>青锋剑（装备）。</summary>
    public const string QingfengSword = "equipment.qingfeng-sword";
    /// <summary>劈山刀（装备）。</summary>
    public const string PishanBlade = "equipment.pishan-blade";
    /// <summary>破罡剑（装备）。</summary>
    public const string PogangSword = "equipment.pogang-sword";
    /// <summary>朝元杖（装备）。</summary>
    public const string ChaoyuanStaff = "equipment.chaoyuan-staff";
    /// <summary>羽鳞甲（装备）。</summary>
    public const string YulinArmor = "equipment.yulin-armor";
    /// <summary>烈火符残卷。</summary>
    public const string BlazingBlastFragment = "item.fragment.blazing-blast";
    /// <summary>寻灵香。</summary>
    public const string GatheringIncense = "item.gathering-incense";
    /// <summary>地火引。</summary>
    public const string AlchemyFlame = "item.alchemy-flame";
}

/// <summary>
/// 核心内置 Buff 增益/减益效果标识符常量。
/// </summary>
public static class BuffIds
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
public static class CombatEncounterIds
{
    /// <summary>灵兽巢穴遭遇。</summary>
    public const string QiBeast = "encounter.qi-beast";
    /// <summary>暮色幽谷副本。</summary>
    public const string TwilightValley = "encounter.twilight-valley";
}

/// <summary>
/// 核心内置世界地图标识符常量。
/// </summary>
public static class WorldMapIds
{
    /// <summary>青云山脉大地图。</summary>
    public const string QingyunMountains = "map.qingyun-mountains";
}

/// <summary>
/// 核心内置世界地图节点地点标识符常量。
/// </summary>
public static class WorldNodeIds
{
    /// <summary>云溪村（故事起源）。</summary>
    public const string YunxiVillage = "node.yunxi-village";
    /// <summary>漱玉溪。</summary>
    public const string ShuyuBrook = "node.shuyu-brook";
    /// <summary>百草谷。</summary>
    public const string HerbValley = "node.herb-valley";
    /// <summary>落霞桃林。</summary>
    public const string PeachForest = "node.peach-forest";
    /// <summary>清石水滩。</summary>
    public const string RockBeach = "node.rock-beach";
    /// <summary>望云茶肆。</summary>
    public const string TeaHouse = "node.tea-house";
    /// <summary>青云山门。</summary>
    public const string CloudGate = "node.cloud-gate";
    /// <summary>云巅草庐。</summary>
    public const string CloudPeak = "node.cloud-peak";
}

