// ==============================================================================
// 文件名称: SystemConstants.cs
// 主要职责: 集中定义修仙领域系统中战斗容量限制、模拟引擎运行上限、数据版本号与市场硬限制常量。
// 核心逻辑 / 定义内容:
//   1. 战斗技能槽、消耗品槽与装备强化上限常量（CombatLimits）。
//   2. 确定性模拟时间推进步数、日志条目与寻敌延迟范围常量（SimulationConstants）。
//   3. 游戏数据配置架构版本号（GameDataCatalogConstants）。
//   4. 市场挂牌生命周期、价格下限与分片哈希算法常量（MarketConstants）。
// ==============================================================================

namespace Domain.Constants;

/// <summary>
/// 战斗与装备系统容量硬限制与槽位常量。
/// </summary>
public static class CombatLimits
{
    /// <summary>主动与心法技能装备槽位上限（5格：首位内功+后续4格术法，支持动态扩展）。</summary>
    public const int AbilitySlotCount = 5;
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
/// 确定性世界模拟与行动队列推进系统运行常量。
/// </summary>
public static class SimulationConstants
{
    /// <summary>玩家默认最大离线挂机补偿结算时长（48 小时，可通过配置/属性扩展）。</summary>
    public static readonly TimeSpan DefaultMaxOfflineDuration = TimeSpan.FromHours(48);
    /// <summary>单次快进推进状态机最大跳转次数上限（防止死循环与单 Fiber 阻塞）。</summary>
    public const int MaxTransitionsPerAdvance = 50_000;
    /// <summary>玩家内存中保留的最近事件日志最大条数。</summary>
    public const int EventLogLimit = 40;
    /// <summary>离线结算时单次批量聚合待处理事件日志上限。</summary>
    public const int PendingEventLimit = 4_096;
    /// <summary>战斗遭遇最小搜敌延迟毫秒数（1.0秒）。</summary>
    public const int MinimumCombatSearchMilliseconds = 1_000;
    /// <summary>战斗遭遇最大搜敌延迟毫秒数（60.0秒）。</summary>
    public const int MaximumCombatSearchMilliseconds = 60_000;
}

/// <summary>
/// 游戏数据配置体系静态常量。
/// </summary>
public static class GameDataCatalogConstants
{
    /// <summary>当前领域模型支持的游戏数据架构版本号。</summary>
    public const int SupportedSchemaVersion = 1;
}

/// <summary>
/// 灵石坊市与分布式账本核心规则与算法常量。
/// </summary>
public static class MarketConstants
{
    /// <summary>挂牌单价绝对最低限制（2 灵石）。</summary>
    public const long AbsoluteMinimumPrice = 2;
    /// <summary>市场挂牌默认生命周期时长（30 天）。</summary>
    public static readonly TimeSpan ListingLifetime = TimeSpan.FromDays(30);
    /// <summary>市场分片哈希算法标识。</summary>
    public const string PartitionAlgorithm = "fnv1a64-utf16-v1";
    /// <summary>FNV-1a 64位哈希初始偏移量。</summary>
    public const ulong FnvOffsetBasis = 14695981039346656037UL;
    /// <summary>FNV-1a 64位哈希质数倍率。</summary>
    public const ulong FnvPrime = 1099511628211UL;
}

