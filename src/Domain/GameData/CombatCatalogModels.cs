namespace Domain.GameData;

/// <summary>妖兽战斗掉落条目。</summary>
public sealed record CombatDropDefinition(
    ItemId ItemId,
    double DropChance,
    double MinQuantity,
    double MaxQuantity);

/// <summary>怪物战斗静态属性与掉落配表。</summary>
public sealed record CombatMonsterDefinition(
    string Id,
    string Name,
    double BudgetCost,
    CombatantStats Stats,
    IReadOnlyList<CombatDropDefinition> DropTable);

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
    int BattlesPerBoss);
