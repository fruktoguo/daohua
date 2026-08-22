// ==============================================================================
// 文件名称: DaoHuaIdConstants.cs
// 主要职责: 集中定义修仙世界预置原型、动作、物品、Buff、副本遭遇、地图与节点的静态唯一标识符（ID）常量。
// 核心逻辑 / 定义内容:
//   1. 预置基础行为与动作原型 ID 常量定义（DaoHuaActionIds）。
//   2. 预置基础物品、灵药、法宝装备与货币资源 ID 常量定义（DaoHuaItemIds）。
//   3. 预置状态增益/减益 Buff ID 常量定义（DaoHuaBuffIds）。
//   4. 预置战斗副本遭遇与世界地图/地点节点 ID 常量定义（DaoHuaCombatEncounterIds, DaoHuaWorldMapIds, DaoHuaWorldNodeIds）。
// ==============================================================================

namespace DaoHua.Domain.Constants;

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

