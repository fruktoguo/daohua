namespace Daoxuan.Domain.Models;

public enum DaoxuanActionType
{
    Cultivation = 0,
    Exploration = 1,
    Alchemy = 2,
    Combat = 3
}

public enum DaoxuanCombatStyle
{
    Sword = 0,
    Blade = 1,
    Talisman = 2,
    FiveElements = 3,
    Body = 4
}

public enum DaoxuanDamageType
{
    Physical = 0,
    Water = 1,
    Nature = 2,
    Fire = 3
}

public enum DaoxuanPresenceMode
{
    Online = 0,
    IdleOffline = 1,
    FullyOffline = 2
}

public enum DaoxuanActionStatus
{
    Queued = 0,
    Running = 1,
    Blocked = 2
}

public enum DaoxuanActionExecutionKind
{
    Timed = 0,
    CombatEncounter = 1,
    Travel = 2
}

public static class DaoxuanCombatLimits
{
    public const int AbilitySlotCount = 4;
    public const int ConsumableSlotCount = 3;
    public const int AutoUseSlotCount = 3;
    public const int TriggerCountPerSlot = 4;
    public const int EquipmentSlotCount = 14;
    public const int MaxEnhancementLevel = 20;
}

[Flags]
public enum DaoxuanDirtySections
{
    None = 0,
    Queue = 1 << 0,
    Inventory = 1 << 1,
    Buffs = 1 << 2,
    Stats = 1 << 3,
    Events = 1 << 4,
    Presence = 1 << 5,
    QueueProgress = 1 << 6,
    Combat = 1 << 7,
    World = 1 << 8,
    All = Queue | Inventory | Buffs | Stats | Events | Presence | QueueProgress | Combat | World
}

public static class DaoxuanActionIds
{
    public const string GatherHerbs = "explore.gather-herbs";
    public const string RefineQiPill = "alchemy.refine-qi-pill";
    public const string DrawQi = "cultivation.draw-qi";
    public const string HuntQiBeast = "combat.hunt-qi-beast";
    public const string SweepTwilightValley = "combat.sweep-twilight-valley";
    public const string WorldTravel = "world.travel";
}

public static class DaoxuanItemIds
{
    public const string SpiritHerb = "item.spirit-herb";
    public const string QiGatheringPill = "item.qi-gathering-pill";
    public const string Spirit = "currency.spirit";
    public const string Qi = "resource.qi";
    public const string HealingElixir = "item.healing-elixir";
    public const string RejuvenatingDew = "item.rejuvenating-dew";
    public const string SpiritTea = "item.spirit-tea";
    public const string FireTalisman = "item.fire-talisman";
    public const string QiBeastCore = "item.qi-beast-core";
    public const string CloudSugar = "item.cloud-sugar";
    public const string VerdantSpiritMilk = "item.verdant-spirit-milk";
    public const string AzureSpiritMilk = "item.azure-spirit-milk";
    public const string SpiritPulseManual = "item.spirit-pulse-manual";
    public const string SmallQiChest = "item.small-qi-chest";
    public const string GatheringIncense = "item.gathering-incense";
    public const string AlchemyFlame = "item.alchemy-flame";
    public const string CursedSpiritBow = "equipment.cursed-spirit-bow";
    public const string FuriousSpiritSpear = "equipment.furious-spirit-spear";
    public const string GriffinSpiritBulwark = "equipment.griffin-spirit-bulwark";
    public const string SunderingSpiritCrossbow = "equipment.sundering-spirit-crossbow";
}

public static class DaoxuanBuffIds
{
    public const string QiGathering = "buff.qi-gathering";
    public const string SpiritTea = "buff.spirit-tea";
    public const string FireTalisman = "buff.fire-talisman";
    public const string GatheringBlessing = "buff.gathering-blessing";
    public const string AlchemyFocus = "buff.alchemy-focus";
}

public static class DaoxuanCombatEncounterIds
{
    public const string QiBeast = "encounter.qi-beast";
    public const string TwilightValley = "encounter.twilight-valley";
}

public static class DaoxuanWorldMapIds
{
    public const string QingyunMountains = "map.qingyun-mountains";
}

public static class DaoxuanWorldNodeIds
{
    public const string HerbVale = "node.herb-vale";
    public const string CloudGate = "node.cloud-gate";
    public const string SpiritSpring = "node.spirit-spring";
    public const string SunsetRidge = "node.sunset-ridge";
    public const string TwilightPass = "node.twilight-pass";
    public const string StoneVein = "node.stone-vein";
    public const string CloudMarket = "node.cloud-market";
    public const string AncientTerrace = "node.ancient-terrace";
}
