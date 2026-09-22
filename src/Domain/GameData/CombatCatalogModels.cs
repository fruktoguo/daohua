using Domain.Combat;

namespace Domain.GameData;

/// <summary>妖兽战斗掉落条目。</summary>
public sealed record CombatDropDefinition(
    ItemId ItemId,
    double DropChance,
    double MinQuantity,
    double MaxQuantity);

/// <summary>怪物品阶：普通妖修 / 精英大妖 / 妖王魔尊。</summary>
public enum CombatMonsterRank : byte
{
    /// <summary>普通妖修：80% 全属性，300% 玩家生命。</summary>
    Minion = 0,
    /// <summary>精英大妖：120% 全属性，500% 玩家生命。</summary>
    Elite = 1,
    /// <summary>妖王魔尊：150% 全属性，2000% 玩家生命。</summary>
    Boss = 2
}

/// <summary>怪物战斗静态属性、品阶、随战技能、掉落配表与击杀奖励（修为/底蕴/六维经验）。</summary>
public sealed record CombatMonsterDefinition(
    string Id,
    string Name,
    double BudgetCost,
    int Level,
    CombatMonsterRank Rank,
    CombatantStats Stats,
    IReadOnlyList<CombatDropDefinition> DropTable,
    IReadOnlyList<SkillSlot> Skills,
    double QiReward = 0,
    double ExperienceReward = 0,
    double RootExpReward = 0);

/// <summary>遭遇中的随机刷怪候选及预算权重。</summary>
public sealed record CombatSpawnDefinition(
    string EnemyId,
    double Weight,
    double Strength);

/// <summary>妖兽试炼遭遇配表，包含搜寻/复生阶段时长。</summary>
public sealed record CombatEncounterDefinition(
    string Id,
    string Name,
    double MinBudget,
    double MaxBudget,
    double MaxTotalStrength,
    double SearchSeconds,
    double RespawnSeconds,
    double PlayerRespawnSeconds,
    IReadOnlyList<CombatSpawnDefinition> RandomSpawns,
    IReadOnlyList<string> EliteEnemyIds,
    IReadOnlyList<string> BossEnemyIds,
    int BattlesPerBoss,
    int BattlesPerElite = 5);
