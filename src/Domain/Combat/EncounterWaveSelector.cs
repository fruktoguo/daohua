using Domain.GameData;

namespace Domain.Combat;

/// <summary>按遭遇预算生成确定性的单波怪物列表；战斗引擎仍逐只交战。</summary>
public static class EncounterWaveSelector
{
    /// <summary>
    /// 执行 Select 操作。
    /// </summary>
    /// <param name="encounter">encounter 参数。</param>
    /// <param name="monsters">monsters 参数。</param>
    /// <param name="seed">seed 参数。</param>
    /// <param name="waveNumber">waveNumber 参数。</param>
    /// <returns>返回处理结果。</returns>
    public static IReadOnlyList<CombatMonsterDefinition> Select(CombatEncounterDefinition encounter,
        IReadOnlyDictionary<string, CombatMonsterDefinition> monsters, long seed, int waveNumber)
    {
        ArgumentNullException.ThrowIfNull(encounter); ArgumentNullException.ThrowIfNull(monsters);
        if (waveNumber < 1 || !double.IsFinite(encounter.MinBudget) || !double.IsFinite(encounter.MaxBudget)
            || encounter.MinBudget <= 0 || encounter.MaxBudget < encounter.MinBudget)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇波次或预算无效。");
        var bossInterval = encounter.BattlesPerBoss > 0 ? encounter.BattlesPerBoss : 20;
        var special = waveNumber % bossInterval == 0 ? encounter.BossEnemyIds : waveNumber % 5 == 0 ? encounter.EliteEnemyIds : null;
        var result = new List<CombatMonsterDefinition>();
        var remain = encounter.MinBudget + (Next(ref seed) % 1_000_000UL) / 1_000_000d * (encounter.MaxBudget - encounter.MinBudget);
        if (special is not null && special.Count > 0)
        {
            var id = special[(int)(Next(ref seed) % (ulong)special.Count)];
            if (!monsters.TryGetValue(id, out var monster)) throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物未定义。");
            if (!double.IsFinite(monster.BudgetCost) || monster.BudgetCost <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物预算无效。");
            result.Add(monster);
            remain = encounter.MaxBudget - monster.BudgetCost;
        }
        var candidates = encounter.RandomSpawns.ToArray();
        foreach (var candidate in candidates)
            if (!double.IsFinite(candidate.Weight) || candidate.Weight <= 0 || !monsters.TryGetValue(candidate.EnemyId, out var definition)
                || !double.IsFinite(definition.BudgetCost) || definition.BudgetCost <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇怪物引用、权重或预算无效。");
        if (candidates.Length == 0 && result.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇没有有效怪物池。");
        while (result.Count < 5 && remain > 0)
        {
            var affordable = candidates.Where(candidate => monsters[candidate.EnemyId].BudgetCost <= remain).ToArray();
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
        }
        if (result.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇预算不足以生成任何怪物。");
        return result;
    }
    private static ulong Next(ref long seed) { unchecked { var x = (ulong)seed; x ^= x << 13; x ^= x >> 7; x ^= x << 17; seed = (long)x; return x; } }
}
