// ==============================================================================
// 文件名称: PlayerStatsComponent.cs
// 主要职责: 玩家生涯统计、行为计数与灵石资产组件。
// 核心逻辑 / 定义内容:
//   1. 纯数据保存灵石资产与各类生产、修炼、战斗统计数值，数值统一采用 double 精度。
// ==============================================================================

namespace Domain.Components;

using YuoCore.Ecs;

/// <summary>
/// 玩家生涯统计、行为计数与灵石资产组件。
/// </summary>
public sealed class PlayerStatsComponent : YuoComponent
{
    /// <summary>灵石持有数量。</summary>
    public double Spirit { get; set; }

    /// <summary>累计采集灵草总数。</summary>
    public double HerbsCollected { get; set; }

    /// <summary>累计炼制丹药总数。</summary>
    public double PillsCrafted { get; set; }

    /// <summary>累计打坐修炼大周天次数。</summary>
    public double CultivationCycles { get; set; }

    /// <summary>累计完成动作总次数。</summary>
    public double CompletedActions { get; set; }

    /// <summary>累计受阻动作次数。</summary>
    public double BlockedActions { get; set; }

    /// <summary>战斗胜利总场次。</summary>
    public double CombatWins { get; set; }

    /// <summary>战斗失败总场次。</summary>
    public double CombatLosses { get; set; }

    /// <summary>累计战斗经验值。</summary>
    public double CombatExperience { get; set; }

    /// <summary>战斗中累计造成的总伤害。</summary>
    public double CombatDamageDealt { get; set; }

    /// <summary>战斗中累计承受的总伤害。</summary>
    public double CombatDamageTaken { get; set; }

    /// <summary>击败敌怪总数。</summary>
    public double CombatEnemiesDefeated { get; set; }

    /// <summary>切磋击败玩家总数。</summary>
    public double CombatPlayersDefeated { get; set; }
}
