using Domain.GameData;
using System.Runtime.CompilerServices;

namespace Domain.Combat;

/// <summary>
/// 按遭遇预算生成确定性的单波怪物阵容。
/// 波次结构：普通波最多 3 只普通怪（组队最多 9 只）；精英波 1 只精英 + 普通怪护从；Boss 波 1 只 Boss + 普通怪护从。
/// 总强度受 maxTotalStrength 约束；组队讨伐时随队伍人数增加动态提高威胁度（每多1人+15%，最多160%）。
/// </summary>
public static class EncounterWaveSelector
{
    /// <summary>单人单波满编怪物数量（1 特殊怪 + 2 护从 / 3 普通怪）。</summary>
    public const int MaxWaveSize = 3;

    /// <summary>组队讨伐单波满编怪物数量（九宫格 3x3 敌方阵型上限 9 只）。</summary>
    public const int MaxAlliedWaveSize = 9;

    /// <summary>组队讨伐每多一名玩家增加的威胁度比率（15%）。</summary>
    public const double ThreatRatioPerExtraPlayer = 0.15;

    /// <summary>组队最终威胁度加成倍率上限（160%）。</summary>
    public const double MaxThreatMultiplier = 1.60;

    /// <summary>
    /// 计算队伍人数对应的最终威胁度加成倍率：
    /// 以单人（1 人）为基准 1.0 (100%)，每多一个玩家增加 15%，最高封顶 160% (1.60)。
    /// </summary>
    public static double GetPartyThreatMultiplier(int partySize)
    {
        var extra = Math.Max(0, partySize - 1);
        return Math.Min(MaxThreatMultiplier, 1.0 + extra * ThreatRatioPerExtraPlayer);
    }

    /// <summary>
    /// 遭遇刷怪池的物化缓存：RandomSpawns 拆为并行数组，免每波 ToArray/字段反射。
    /// 只缓存 encounter 自有字段；monsters 字典解析与全部校验仍逐次执行，异常时机与顺序不变。
    /// 弱表以配表实例为键，兼容测试直接 new CombatEncounterDefinition 的手工构造。
    /// </summary>
    private static readonly ConditionalWeakTable<CombatEncounterDefinition, SpawnArrays> s_spawnCache = new();

    private sealed class SpawnArrays
    {
        public required string[] EnemyIds { get; init; }
        public required double[] Weights { get; init; }
        public required double[] Strengths { get; init; }
    }

    /// <summary>按确定性种子抽取本波怪物阵容；Boss/精英波按 waveNumber 整除配表间隔触发。</summary>
    /// <param name="encounter">遭遇配表（预算、强度上限与刷怪池）。</param>
    /// <param name="monsters">怪物定义表（预算与强度校验依据）。</param>
    /// <param name="seed">确定性随机种子。</param>
    /// <param name="waveNumber">当前波次（1 起）。</param>
    /// <param name="partySize">队伍参战人数（默认 1；每多 1 人增加 15% 威胁度，最多 160%）。</param>
    /// <returns>本波怪物定义列表（单人 1~MaxWaveSize 只，组队 1~MaxAlliedWaveSize 只）。</returns>
    public static IReadOnlyList<CombatMonsterDefinition> Select(CombatEncounterDefinition encounter,
        IReadOnlyDictionary<string, CombatMonsterDefinition> monsters, long seed, int waveNumber, int partySize = 1)
    {
        ArgumentNullException.ThrowIfNull(encounter); ArgumentNullException.ThrowIfNull(monsters);
        if (waveNumber < 1 || !double.IsFinite(encounter.MinBudget) || !double.IsFinite(encounter.MaxBudget)
            || encounter.MinBudget <= 0 || encounter.MaxBudget < encounter.MinBudget)
            throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇波次或预算无效。");
        var bossInterval = encounter.BattlesPerBoss > 0 ? encounter.BattlesPerBoss : 20;
        var eliteInterval = encounter.BattlesPerElite > 0 ? encounter.BattlesPerElite : 5;
        var special = waveNumber % bossInterval == 0 ? encounter.BossEnemyIds : waveNumber % eliteInterval == 0 ? encounter.EliteEnemyIds : null;
        var threatMultiplier = GetPartyThreatMultiplier(partySize);
        var minBudget = encounter.MinBudget * threatMultiplier;
        var maxBudget = encounter.MaxBudget * threatMultiplier;
        var strengthPool = encounter.MaxTotalStrength > 0 ? encounter.MaxTotalStrength * threatMultiplier : double.PositiveInfinity;
        var maxWaveSize = partySize > 1 ? MaxAlliedWaveSize : MaxWaveSize;
        var result = new List<CombatMonsterDefinition>();
        var remain = minBudget + (Next(ref seed) % 1_000_000UL) / 1_000_000d * (maxBudget - minBudget);
        var spawns = s_spawnCache.GetValue(encounter, static e =>
        {
            var entries = e.RandomSpawns;
            var ids = new string[entries.Count];
            var weights = new double[entries.Count];
            var strengths = new double[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                ids[i] = entries[i].EnemyId;
                weights[i] = entries[i].Weight;
                strengths[i] = entries[i].Strength;
            }
            return new SpawnArrays { EnemyIds = ids, Weights = weights, Strengths = strengths };
        });
        var enemyIds = spawns.EnemyIds;
        var weights = spawns.Weights;
        var strengths = spawns.Strengths;
        var spawnCount = enemyIds.Length;
        if (special is not null && special.Count > 0)
        {
            var id = special[(int)(Next(ref seed) % (ulong)special.Count)];
            if (!monsters.TryGetValue(id, out var monster)) throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物未定义。");
            if (!double.IsFinite(monster.BudgetCost) || monster.BudgetCost <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "特殊怪物预算无效。");
            result.Add(monster);
            remain = maxBudget - monster.BudgetCost;
            // 特殊怪强度取自刷怪池条目：原 strengthOf 字典按 RandomSpawns 序覆盖填充，
            // 等价于取最后一个匹配条目——反向扫描复刻该语义，缺省为 0。
            var specialStrength = 0d;
            for (var i = spawnCount - 1; i >= 0; i--)
                if (enemyIds[i] == id) { specialStrength = strengths[i]; break; }
            strengthPool -= specialStrength;
        }
        // 校验每次调用必跑（保留异常时机与顺序）；顺手物化怪物定义与预算数组，
        // 后续轮盘循环直接读数组，不再做字典查找或 Where 物化。
        var resolved = new CombatMonsterDefinition[spawnCount];
        var budgetCosts = new double[spawnCount];
        for (var i = 0; i < spawnCount; i++)
        {
            if (!double.IsFinite(weights[i]) || weights[i] <= 0 || !monsters.TryGetValue(enemyIds[i], out var definition)
                || !double.IsFinite(definition.BudgetCost) || definition.BudgetCost <= 0
                || !double.IsFinite(strengths[i]) || strengths[i] <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇怪物引用、权重、预算或强度无效。");
            resolved[i] = definition;
            budgetCosts[i] = definition.BudgetCost;
        }
        if (spawnCount == 0 && result.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇没有有效怪物池。");
        while (result.Count < maxWaveSize && remain > 0)
        {
            // affordable 子集不物化：单趟按原顺序累加满足预算/强度门槛的候选权重，与原 Where+Sum 同序。
            var total = 0d;
            var affordableCount = 0;
            for (var i = 0; i < spawnCount; i++)
                if (budgetCosts[i] <= remain && strengths[i] <= strengthPool)
                {
                    affordableCount++;
                    total += weights[i];
                }
            if (affordableCount == 0) break;
            if (!double.IsFinite(total)) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇总权重无效。");
            var ticket = (Next(ref seed) % 1_000_000UL) / 1_000_000d * total;
            // 轮盘按候选原序推进；chosen 兜底为最后一个可负担候选（原 affordable[^1]）。
            var chosen = -1;
            for (var i = 0; i < spawnCount; i++)
            {
                if (!(budgetCosts[i] <= remain && strengths[i] <= strengthPool)) continue;
                chosen = i;
                if (ticket < weights[i]) break;
                ticket -= weights[i];
            }
            var monster = resolved[chosen];
            result.Add(monster);
            remain -= monster.BudgetCost;
            strengthPool -= strengths[chosen];
        }
        if (result.Count == 0)
        {
            // 保底：候选池为空时塞入预算最低的普通小怪，杜绝空波次；平局取首个（MinBy 语义）。
            var fallback = -1;
            var minCost = double.PositiveInfinity;
            for (var i = 0; i < spawnCount; i++)
                if (budgetCosts[i] < minCost) { minCost = budgetCosts[i]; fallback = i; }
            if (fallback < 0) throw new DomainException(DomainErrorCode.InvalidArgument, "遭遇预算不足以生成任何怪物。");
            result.Add(resolved[fallback]);
        }
        return result;
    }

    private static ulong Next(ref long seed) { unchecked { var x = (ulong)seed; x ^= x << 13; x ^= x >> 7; x ^= x << 17; seed = (long)x; return x; } }
}
