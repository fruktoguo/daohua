using Domain.GameData;

namespace Domain.Combat;

/// <summary>
/// 按遭遇预算生成确定性的单波怪物阵容。
/// 波次结构：普通波最多 3 只普通怪；精英波 1 只精英 + 普通怪护从；Boss 波 1 只 Boss + 普通怪护从。
/// 总强度受 maxTotalStrength 约束；普通怪抽取按预算轮盘直至预算耗尽或满编 3 只。
/// </summary>
public static class EncounterWaveSelector
{
    /// <summary>单波满编怪物数量（1 特殊怪 + 2 护从 / 3 普通怪）。</summary>
    public const int MaxWaveSize = 3;

    /// <summary>按确定性种子抽取本波怪物阵容；Boss/精英波按 waveNumber 整除配表间隔触发。</summary>
    /// <param name="encounter">遭遇配表（预算、强度上限与刷怪池）。</param>
    /// <param name="monsters">怪物定义表（预算与强度校验依据）。</param>
    /// <param name="seed">确定性随机种子。</param>
    /// <param name="waveNumber">当前波次（1 起）。</param>
    /// <returns>本波怪物定义列表（1~MaxWaveSize 只）。</returns>
    public static IReadOnlyList<CombatMonsterDefinition> Select(CombatEncounterDefinition encounter,
        IReadOnlyDictionary<string, CombatMonsterDefinition> monsters, long seed, int waveNumber)
    {
        ArgumentNullException.ThrowIfNull(encounter); ArgumentNullException.ThrowIfNull(monsters);
        if (waveNumber < 1 || !double.IsFinite(encounter.MinBudget) || !double.IsFinite(encounter.MaxBudget)
            || encounter.MinBudget <= 0 || encounter.MaxBudget < encounter.MinBudget)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇波次或预算无效。");
        var bossInterval = encounter.BattlesPerBoss > 0 ? encounter.BattlesPerBoss : 20;
        var eliteInterval = encounter.BattlesPerElite > 0 ? encounter.BattlesPerElite : 5;
        var special = waveNumber % bossInterval == 0 ? encounter.BossEnemyIds : waveNumber % eliteInterval == 0 ? encounter.EliteEnemyIds : null;
        var result = new List<CombatMonsterDefinition>();
        var remain = encounter.MinBudget + (Next(ref seed) % 1_000_000UL) / 1_000_000d * (encounter.MaxBudget - encounter.MinBudget);
        var strengthPool = encounter.MaxTotalStrength > 0 ? encounter.MaxTotalStrength : double.PositiveInfinity;
        var strengthOf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var spawn in encounter.RandomSpawns)
            strengthOf[spawn.EnemyId] = spawn.Strength;
        if (special is not null && special.Count > 0)
        {
            var id = special[(int)(Next(ref seed) % (ulong)special.Count)];
            if (!monsters.TryGetValue(id, out var monster)) throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物未定义。");
            if (!double.IsFinite(monster.BudgetCost) || monster.BudgetCost <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物预算无效。");
            result.Add(monster);
            remain = encounter.MaxBudget - monster.BudgetCost;
            strengthPool -= strengthOf.GetValueOrDefault(id);
        }
        var candidates = encounter.RandomSpawns.ToArray();
        foreach (var candidate in candidates)
            if (!double.IsFinite(candidate.Weight) || candidate.Weight <= 0 || !monsters.TryGetValue(candidate.EnemyId, out var definition)
                || !double.IsFinite(definition.BudgetCost) || definition.BudgetCost <= 0
                || !double.IsFinite(candidate.Strength) || candidate.Strength <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇怪物引用、权重、预算或强度无效。");
        if (candidates.Length == 0 && result.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇没有有效怪物池。");
        while (result.Count < MaxWaveSize && remain > 0)
        {
            var affordable = candidates.Where(candidate =>
                monsters[candidate.EnemyId].BudgetCost <= remain && candidate.Strength <= strengthPool).ToArray();
            if (affordable.Length == 0) break;
            var total = affordable.Sum(candidate => candidate.Weight);
            if (!double.IsFinite(total)) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇总权重无效。");
            var ticket = (Next(ref seed) % 1_000_000UL) / 1_000_000d * total;
            var chosen = affordable[^1];
            foreach (var candidate in affordable)
            {
                if (ticket < candidate.Weight) { chosen = candidate; break; }
                ticket -= candidate.Weight;
            }
            var monster = monsters[chosen.EnemyId];
            result.Add(monster);
            remain -= monster.BudgetCost;
            strengthPool -= chosen.Strength;
        }
        if (result.Count == 0)
        {
            // 保底：候选池为空时塞入预算最低的普通小怪，杜绝空波次。
            var fallback = candidates.Length == 0 ? null : candidates.MinBy(candidate => monsters[candidate.EnemyId].BudgetCost);
            if (fallback is null) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇预算不足以生成任何怪物。");
            result.Add(monsters[fallback.EnemyId]);
        }
        return result;
    }
    private static ulong Next(ref long seed) { unchecked { var x = (ulong)seed; x ^= x << 13; x ^= x >> 7; x ^= x << 17; seed = (long)x; return x; } }
}
