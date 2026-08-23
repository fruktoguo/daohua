// ==============================================================================
// 文件名称: Enums.cs
// 主要职责: 定义修仙领域模型中通用的核心业务枚举类型。
// 核心逻辑 / 定义内容:
///   1. 基础行为类型、战斗流派、伤害大类与五行属性枚举（ActionType, CombatStyle, DamageCategory, Element）。
//   2. 玩家在线模式、动作执行状态与推进模式枚举（PresenceMode, ActionStatus, ActionExecutionKind）。
//   3. 状态增量同步脏段掩码枚举（DirtySections）。
// ==============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Models;

/// <summary>
/// 角色行为/动作的四大基本类型枚举。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionType
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
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CombatStyle
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
/// 伤害大类枚举，决定攻击力加成基础池与第一道防御结算（护甲 vs 法抗）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DamageCategory
{
    /// <summary>物理伤害，受物理攻击力加成，吃目标物理护甲（Armor）减免。</summary>
    Physical = 0,
    /// <summary>术法伤害，受术法攻击力加成，吃目标法术抗性（MagicResistance）减免。</summary>
    Spell = 1
}

/// <summary>
/// 五行与元气属性枚举，决定攻方五行增伤与守方五行减免/抗性。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Element
{
    /// <summary>无属性（纯粹内劲/白板术法），不触发五行增减伤。</summary>
    None = 0,
    /// <summary>金系属性，锐利贯穿。</summary>
    Metal = 1,
    /// <summary>木系属性，生生不息、自然毒素。</summary>
    Wood = 2,
    /// <summary>水系属性，冰霜阴柔、冻结缓速。</summary>
    Water = 3,
    /// <summary>火系属性，灼热炎阳、爆发爆裂。</summary>
    Fire = 4,
    /// <summary>土系属性，厚重沉稳、碎石重压。</summary>
    Earth = 5
}

/// <summary>
/// 玩家在线与挂机状态模式枚举。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresenceMode
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
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionStatus
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
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionExecutionKind
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
public enum DirtySections
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
/// 战斗即时行为动作类型枚举。
/// </summary>
[JsonConverter(typeof(CombatActionKindJsonConverter))]
public enum CombatActionKind : byte
{
    /// <summary>基础普通攻击。</summary>
    Attack = 0,
    /// <summary>释放主动技能或法术。</summary>
    Cast = 1,
    /// <summary>处于眩晕、冰冻等受控状态。</summary>
    Control = 2,
    /// <summary>等待冷却或无动作。</summary>
    Wait = 3,
    /// <summary>阵亡等待复活或重构肉身。</summary>
    Respawn = 4
}

/// <summary>
/// 历练与战斗推进阶段枚举。
/// </summary>
[JsonConverter(typeof(ExpeditionPhaseJsonConverter))]
public enum ExpeditionPhase : byte
{
    /// <summary>无阶段 / 空闲。</summary>
    None = 0,
    /// <summary>大世界节点间旅行移动中。</summary>
    Travel = 1,
    /// <summary>首次搜寻敌人中。</summary>
    InitialSearch = 2,
    /// <summary>击败敌人后搜寻下一波敌人。</summary>
    Search = 3,
    /// <summary>正在与敌人进行实时战斗交锋。</summary>
    Combat = 4
}

/// <summary>
/// 装备穿戴槽位枚举。
/// </summary>
[JsonConverter(typeof(EquipmentSlotJsonConverter))]
public enum EquipmentSlot : byte
{
    /// <summary>主手武器。</summary>
    Weapon = 0,
    /// <summary>双手重武器（占用主副手）。</summary>
    TwoHand = 1,
    /// <summary>副手防具/法器。</summary>
    OffHand = 2,
    /// <summary>头部护具/法冠。</summary>
    Head = 3,
    /// <summary>胸部衣服/宝甲/法袍。</summary>
    Armor = 4,
    /// <summary>腿部护甲/下裳。</summary>
    Legs = 5,
    /// <summary>鞋履/步云履。</summary>
    Boots = 6,
    /// <summary>戒指/乾坤戒。</summary>
    Ring = 7,
    /// <summary>项链/灵佩。</summary>
    Neck = 8,
    /// <summary>饰品/护符。</summary>
    Trinket = 9,
    /// <summary>本命法宝/本命符箓。</summary>
    Talisman = 10
}

/// <summary>
/// 装备槽位位掩码，用于实现 0-GC 的穿戴冲突检查。
/// </summary>
[Flags]
public enum EquipmentSlotMask : ushort
{
    None = 0,
    Weapon = 1 << 0,
    TwoHand = 1 << 1,
    OffHand = 1 << 2,
    Head = 1 << 3,
    Armor = 1 << 4,
    Legs = 1 << 5,
    Boots = 1 << 6,
    Ring = 1 << 7,
    Neck = 1 << 8,
    Trinket = 1 << 9,
    Talisman = 1 << 10
}

/// <summary>
/// 怪物与非玩家单位生命层次/阶层枚举。
/// </summary>
[JsonConverter(typeof(UnitRankJsonConverter))]
public enum UnitRank : byte
{
    /// <summary>基础妖兽（普通单位）。</summary>
    Minion = 0,
    /// <summary>精英妖兽（气血与属性倍率提升）。</summary>
    Elite = 1,
    /// <summary>绝世妖王（关底领主/高乘区大怪）。</summary>
    Boss = 2,
    /// <summary>召唤物。</summary>
    Summon = 3,
    /// <summary>灵宠/坐骑。</summary>
    Pet = 4,
    /// <summary>玩家镜像/化身。</summary>
    PlayerMirror = 5,
    /// <summary>城镇 NPC。</summary>
    Npc = 6
}

/// <summary>
/// 物品二级业务分类枚举。
/// </summary>
[JsonConverter(typeof(ItemCategoryJsonConverter))]
public enum ItemCategory : byte
{
    /// <summary>通用杂项。</summary>
    General = 0,
    /// <summary>炼器/炼丹基础材料。</summary>
    Material = 1,
    /// <summary>灵草/灵药。</summary>
    Herb = 2,
    /// <summary>灵矿/奇石。</summary>
    Mineral = 3,
    /// <summary>丹药（服用增益或回血回蓝）。</summary>
    Pill = 4,
    /// <summary>灵食（战斗或探索中食用的灵谷佳肴）。</summary>
    Food = 5,
    /// <summary>灵茶/仙酿饮品。</summary>
    Drink = 6,
    /// <summary>符箓（一次性消耗法符）。</summary>
    Talisman = 7,
    /// <summary>功法/秘籍书页。</summary>
    Manual = 8,
    /// <summary>法宝/装备。</summary>
    Equipment = 9,
    /// <summary>宝箱/福袋容器。</summary>
    Chest = 10,
    /// <summary>货币（灵石、仙玉）。</summary>
    Currency = 11,
    /// <summary>纯净灵力/天地灵气资源。</summary>
    Resource = 12
}

/// <summary>
/// 大地图地点附近实体原型种类枚举。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NearbyEntityKind : byte
{
    /// <summary>非玩家角色 NPC。</summary>
    Npc = 0,
    /// <summary>宗门建筑/坊市/洞府。</summary>
    Building = 1,
    /// <summary>传送门/阵法结界入口。</summary>
    Portal = 2,
    /// <summary>野外灵脉/采集资源点。</summary>
    ResourceNode = 3
}

/// <summary>
/// 角色与怪物视觉形象渲染器类型枚举。
/// </summary>
[JsonConverter(typeof(VisualRendererKindJsonConverter))]
public enum VisualRendererKind : byte
{
    /// <summary>文本/文字图标渲染。</summary>
    Text = 0,
    /// <summary>特殊字符/符印渲染。</summary>
    Glyph = 1,
    /// <summary>静态 2D 贴图/图片。</summary>
    Image = 2,
    /// <summary>Live2D 动态立绘。</summary>
    Live2D = 3,
    /// <summary>3D 角色模型。</summary>
    Model3D = 4
}

/// <summary>
/// 坊市交易与撮合结果状态码枚举。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketResultCode : ushort
{
    /// <summary>操作成功。</summary>
    Ok = 0,
    /// <summary>无效请求参数。</summary>
    InvalidRequest = 1,
    /// <summary>无权限或被禁止下单。</summary>
    OrderForbidden = 2,
    /// <summary>重复的订单 ID。</summary>
    DuplicateOrderId = 3,
    /// <summary>未找到指定订单。</summary>
    OrderNotFound = 4,
    /// <summary>订单非活跃挂单状态（无法撤单）。</summary>
    OrderNotActive = 5,
    /// <summary>物品不足，无法挂单卖出。</summary>
    InsufficientItem = 6,
    /// <summary>灵石不足，无法挂单买入。</summary>
    InsufficientSpirit = 7,
    /// <summary>单笔挂单数量超过上限。</summary>
    OrderTooLarge = 8,
    /// <summary>无效的订单 ID 格式。</summary>
    InvalidOrderId = 9,
    /// <summary>无效或不存在的交易品种标的。</summary>
    InvalidInstrument = 10,
    /// <summary>该物品绑定无法交易。</summary>
    ItemNotTradable = 11,
    /// <summary>税率策略无效。</summary>
    InvalidTaxPolicy = 12,
    /// <summary>商贾回收基准价无效。</summary>
    InvalidVendorPrice = 13,
    /// <summary>挂单售价低于商贾回收价（防止套利）。</summary>
    PriceBelowVendor = 14,
    /// <summary>价格步长不符合规范。</summary>
    InvalidPriceIncrement = 15,
    /// <summary>创建时间戳不合法。</summary>
    InvalidCreatedAt = 16
}

public sealed class ItemCategoryJsonConverter : JsonConverter<ItemCategory>
{
    public override ItemCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (ItemCategory)reader.GetByte();
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str)) return ItemCategory.General;

        var span = str.AsSpan().Trim();
        if (span.Equals("材料", StringComparison.Ordinal) || span.Equals("material", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Material;
        if (span.Equals("药材", StringComparison.Ordinal) || span.Equals("herb", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Herb;
        if (span.Equals("矿石", StringComparison.Ordinal) || span.Equals("mineral", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Mineral;
        if (span.Equals("丹药", StringComparison.Ordinal) || span.Equals("pill", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Pill;
        if (span.Equals("灵食", StringComparison.Ordinal) || span.Equals("food", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Food;
        if (span.Equals("灵饮", StringComparison.Ordinal) || span.Equals("drink", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Drink;
        if (span.Equals("符箓", StringComparison.Ordinal) || span.Equals("talisman", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Talisman;
        if (span.Equals("功法", StringComparison.Ordinal) || span.Equals("功法秘籍", StringComparison.Ordinal) || span.Equals("manual", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Manual;
        if (span.Equals("装备", StringComparison.Ordinal) || span.Equals("equipment", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Equipment;
        if (span.Equals("宝箱", StringComparison.Ordinal) || span.Equals("chest", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Chest;
        if (span.Equals("货币", StringComparison.Ordinal) || span.Equals("currency", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Currency;
        if (span.Equals("资源", StringComparison.Ordinal) || span.Equals("resource", StringComparison.OrdinalIgnoreCase)) return ItemCategory.Resource;

        return ItemCategory.General;
    }

    public override void Write(Utf8JsonWriter writer, ItemCategory value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            ItemCategory.Material => "material",
            ItemCategory.Herb => "herb",
            ItemCategory.Mineral => "mineral",
            ItemCategory.Pill => "pill",
            ItemCategory.Food => "food",
            ItemCategory.Drink => "drink",
            ItemCategory.Talisman => "talisman",
            ItemCategory.Manual => "manual",
            ItemCategory.Equipment => "equipment",
            ItemCategory.Chest => "chest",
            ItemCategory.Currency => "currency",
            ItemCategory.Resource => "resource",
            _ => "general"
        };
        writer.WriteStringValue(text);
    }
}

public sealed class EquipmentSlotJsonConverter : JsonConverter<EquipmentSlot>
{
    public override EquipmentSlot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (EquipmentSlot)reader.GetByte();
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str)) return EquipmentSlot.Weapon;

        var span = str.AsSpan().Trim();
        if (span.Equals("two-hand", StringComparison.OrdinalIgnoreCase) || span.Equals("twohand", StringComparison.OrdinalIgnoreCase) || span.Equals("双手法器", StringComparison.Ordinal)) return EquipmentSlot.TwoHand;
        if (span.Equals("weapon", StringComparison.OrdinalIgnoreCase) || span.Equals("main-hand", StringComparison.OrdinalIgnoreCase) || span.Equals("mainhand", StringComparison.OrdinalIgnoreCase) || span.Equals("武器", StringComparison.Ordinal) || span.Equals("主手法器", StringComparison.Ordinal)) return EquipmentSlot.Weapon;
        if (span.Equals("off-hand", StringComparison.OrdinalIgnoreCase) || span.Equals("offhand", StringComparison.OrdinalIgnoreCase) || span.Equals("shield", StringComparison.OrdinalIgnoreCase) || span.Equals("副手", StringComparison.Ordinal)) return EquipmentSlot.OffHand;
        if (span.Equals("head", StringComparison.OrdinalIgnoreCase) || span.Equals("helm", StringComparison.OrdinalIgnoreCase) || span.Equals("helmet", StringComparison.OrdinalIgnoreCase) || span.Equals("头部", StringComparison.Ordinal)) return EquipmentSlot.Head;
        if (span.Equals("armor", StringComparison.OrdinalIgnoreCase) || span.Equals("body", StringComparison.OrdinalIgnoreCase) || span.Equals("chest", StringComparison.OrdinalIgnoreCase) || span.Equals("衣服", StringComparison.Ordinal) || span.Equals("护甲", StringComparison.Ordinal)) return EquipmentSlot.Armor;
        if (span.Equals("legs", StringComparison.OrdinalIgnoreCase) || span.Equals("pants", StringComparison.OrdinalIgnoreCase) || span.Equals("腿部", StringComparison.Ordinal) || span.Equals("下装", StringComparison.Ordinal)) return EquipmentSlot.Legs;
        if (span.Equals("boots", StringComparison.OrdinalIgnoreCase) || span.Equals("feet", StringComparison.OrdinalIgnoreCase) || span.Equals("鞋靴", StringComparison.Ordinal)) return EquipmentSlot.Boots;
        if (span.Equals("ring", StringComparison.OrdinalIgnoreCase) || span.Equals("finger", StringComparison.OrdinalIgnoreCase) || span.Equals("戒指", StringComparison.Ordinal)) return EquipmentSlot.Ring;
        if (span.Equals("neck", StringComparison.OrdinalIgnoreCase) || span.Equals("necklace", StringComparison.OrdinalIgnoreCase) || span.Equals("amulet", StringComparison.OrdinalIgnoreCase) || span.Equals("项链", StringComparison.Ordinal)) return EquipmentSlot.Neck;
        if (span.Equals("trinket", StringComparison.OrdinalIgnoreCase) || span.Equals("accessory", StringComparison.OrdinalIgnoreCase) || span.Equals("饰品", StringComparison.Ordinal) || span.Equals("法宝", StringComparison.Ordinal)) return EquipmentSlot.Trinket;
        if (span.Equals("talisman", StringComparison.OrdinalIgnoreCase) || span.Equals("符箓", StringComparison.Ordinal) || span.Equals("灵宝", StringComparison.Ordinal)) return EquipmentSlot.Talisman;

        return EquipmentSlot.Weapon;
    }

    public override void Write(Utf8JsonWriter writer, EquipmentSlot value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            EquipmentSlot.TwoHand => "two-hand",
            EquipmentSlot.OffHand => "off-hand",
            EquipmentSlot.Head => "head",
            EquipmentSlot.Armor => "armor",
            EquipmentSlot.Legs => "legs",
            EquipmentSlot.Boots => "boots",
            EquipmentSlot.Ring => "ring",
            EquipmentSlot.Neck => "neck",
            EquipmentSlot.Trinket => "trinket",
            EquipmentSlot.Talisman => "talisman",
            _ => "weapon"
        };
        writer.WriteStringValue(text);
    }
}

public sealed class UnitRankJsonConverter : JsonConverter<UnitRank>
{
    public override UnitRank Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (UnitRank)reader.GetByte();
        var str = reader.GetString();
        return GameData.CharacterStatResolver.TryParseRank(str, out var rank) ? rank : UnitRank.Minion;
    }

    public override void Write(Utf8JsonWriter writer, UnitRank value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            UnitRank.Boss => "boss",
            UnitRank.Elite => "elite",
            UnitRank.Summon => "summon",
            UnitRank.Pet => "pet",
            UnitRank.PlayerMirror => "player-mirror",
            UnitRank.Npc => "npc",
            _ => "minion"
        };
    }
}

public sealed class VisualRendererKindJsonConverter : JsonConverter<VisualRendererKind>
{
    public override VisualRendererKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (VisualRendererKind)reader.GetByte();
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str)) return VisualRendererKind.Text;

        var span = str.AsSpan().Trim();
        if (span.Equals("glyph", StringComparison.OrdinalIgnoreCase)) return VisualRendererKind.Glyph;
        if (span.Equals("image", StringComparison.OrdinalIgnoreCase)) return VisualRendererKind.Image;
        if (span.Equals("live2d", StringComparison.OrdinalIgnoreCase)) return VisualRendererKind.Live2D;
        if (span.Equals("model3d", StringComparison.OrdinalIgnoreCase) || span.Equals("3d", StringComparison.OrdinalIgnoreCase)) return VisualRendererKind.Model3D;

        return VisualRendererKind.Text;
    }

    public override void Write(Utf8JsonWriter writer, VisualRendererKind value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            VisualRendererKind.Glyph => "glyph",
            VisualRendererKind.Image => "image",
            VisualRendererKind.Live2D => "live2d",
            VisualRendererKind.Model3D => "model3d",
            _ => "text"
        };
        writer.WriteStringValue(text);
    }
}

public sealed class CombatActionKindJsonConverter : JsonConverter<CombatActionKind>
{
    public override CombatActionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (CombatActionKind)reader.GetByte();
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str)) return CombatActionKind.Wait;
        var span = str.AsSpan().Trim();
        if (span.Equals("attack", StringComparison.OrdinalIgnoreCase)) return CombatActionKind.Attack;
        if (span.Equals("cast", StringComparison.OrdinalIgnoreCase)) return CombatActionKind.Cast;
        if (span.Equals("control", StringComparison.OrdinalIgnoreCase)) return CombatActionKind.Control;
        if (span.Equals("wait", StringComparison.OrdinalIgnoreCase)) return CombatActionKind.Wait;
        if (span.Equals("respawn", StringComparison.OrdinalIgnoreCase)) return CombatActionKind.Respawn;
        return CombatActionKind.Wait;
    }

    public override void Write(Utf8JsonWriter writer, CombatActionKind value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            CombatActionKind.Attack => "attack",
            CombatActionKind.Cast => "cast",
            CombatActionKind.Control => "control",
            CombatActionKind.Respawn => "respawn",
            _ => "wait"
        };
        writer.WriteStringValue(text);
    }
}

public sealed class ExpeditionPhaseJsonConverter : JsonConverter<ExpeditionPhase>
{
    public override ExpeditionPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (ExpeditionPhase)reader.GetByte();
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str)) return ExpeditionPhase.None;
        var span = str.AsSpan().Trim();
        if (span.Equals("travel", StringComparison.OrdinalIgnoreCase)) return ExpeditionPhase.Travel;
        if (span.Equals("initial-search", StringComparison.OrdinalIgnoreCase) || span.Equals("initialsearch", StringComparison.OrdinalIgnoreCase)) return ExpeditionPhase.InitialSearch;
        if (span.Equals("search", StringComparison.OrdinalIgnoreCase)) return ExpeditionPhase.Search;
        if (span.Equals("combat", StringComparison.OrdinalIgnoreCase)) return ExpeditionPhase.Combat;
        return ExpeditionPhase.None;
    }

    public override void Write(Utf8JsonWriter writer, ExpeditionPhase value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            ExpeditionPhase.Travel => "travel",
            ExpeditionPhase.InitialSearch => "initial-search",
            ExpeditionPhase.Search => "search",
            ExpeditionPhase.Combat => "combat",
            _ => "none"
        };
        writer.WriteStringValue(text);
    }
}

