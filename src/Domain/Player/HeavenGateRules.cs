namespace Domain.Player;

/// <summary>
/// 开天门与逆天改命的纯领域规则：抽取分布、重分配守恒、底蕴成本与乘区系数。
/// 全部为确定性纯函数，便于领域测试与离线补算共用同一代码路径。
/// </summary>
public static class HeavenGateRules
{
    /// <summary>叩天门境界等级：该级满修为后必须先开天门才能继续突破。</summary>
    public const int HeavenGateRealmLevel = 18;

    /// <summary>逆天改命默认底蕴成本比例：按当前升级所需修为折算（25%）。</summary>
    public const double RerollCostRatio = 0.25;

    /// <summary>使用逆命丹后的逆天改命底蕴成本比例（5%）。</summary>
    public const double RerollCostRatioWithPill = 0.05;

    /// <summary>逆命丹道具 ID：炼丹特殊配方产出，逆天改命时可选用。</summary>
    public const string ReverseFatePillItemId = "item.reverse-fate-pill";

    /// <summary>炼制逆命丹消耗的底蕴比例：按当前升级所需修为折算（25%）。</summary>
    public const double ReverseFatePillFoundationRatio = 0.25;

    /// <summary>开天门抽取的单系点数基准上限（总值分配前的单轴刻度）。</summary>
    public const int RollAxisCap = LinggenValues.MaxPointsPerElement;

    /// <summary>开天门抽取的总值下限：全域支持，理论最低为 1。</summary>
    public const int RollTotalMin = 1;

    /// <summary>开天门抽取的总值上限：五系全部 100 满值时为 500。</summary>
    public const int RollTotalMax = 500;

    /// <summary>是否已到达可开天门的境界。</summary>
    /// <param name="realmLevel">当前境界等级。</param>
    public static bool IsHeavenGateRealm(int realmLevel) => realmLevel >= HeavenGateRealmLevel;

    /// <summary>逆天改命的底蕴成本：按当前境界升级所需修为的比例折算，至少 1 点。</summary>
    /// <param name="expToNext">当前境界升级所需修为（已含灵根乘区）。</param>
    /// <param name="useReverseFatePill">是否消耗逆命丹把成本降到 5%。</param>
    public static double ResolveRerollFoundationCost(double expToNext, bool useReverseFatePill)
    {
        if (!double.IsFinite(expToNext) || expToNext <= 0) return 0;
        var ratio = useReverseFatePill ? RerollCostRatioWithPill : RerollCostRatio;
        return Math.Max(1, Math.Round(expToNext * ratio, MidpointRounding.AwayFromZero));
    }

    /// <summary>炼制逆命丹需要消耗的底蕴：按当前升级所需修为的 25% 折算，至少 1 点。</summary>
    /// <param name="expToNext">当前境界升级所需修为。</param>
    public static double ResolveReverseFatePillFoundationCost(double expToNext)
        => ResolveFoundationCost(expToNext, ReverseFatePillFoundationRatio);

    /// <summary>
    /// 通用的行动底蕴成本折算：按当前境界升级所需修为的比例取整，至少 1 点。
    /// 比例 ≤ 0 或修为上限非有限/非正时返回 0，表示该行动不产生底蕴成本。
    /// </summary>
    /// <param name="expToNext">当前境界升级所需修为（本层 expToNext）。</param>
    /// <param name="ratio">底蕴占升级所需修为的比例。</param>
    public static double ResolveFoundationCost(double expToNext, double ratio)
    {
        if (!double.IsFinite(expToNext) || expToNext <= 0 || !double.IsFinite(ratio) || ratio <= 0) return 0;
        return Math.Max(1, Math.Round(expToNext * ratio, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// 截断指数连续温度分布参数表（对应改命次数 0 ~ 40 次）。
    /// 基准期望约 180，每次改命期望 +8，40 次期望达到 500（单次出 500 概率约 1/6）。
    /// 全域支持 [1, 500]，首掷万分之 4.4 奇迹爆头，百次仍有极小概率抽 1，平均改命约 40.5 次毕业。
    /// </summary>
    private static readonly double[] LambdaTable =
    [
        -1.78328, -1.56318, -1.34992, -1.14233, -0.93938,
        -0.74014, -0.54374, -0.34941, -0.15638,  0.03607,
         0.22866,  0.42209,  0.61711,  0.81448,  1.01501,
         1.21958,  1.42917,  1.64484,  1.86785,  2.09960,
         2.34175,  2.59630,  2.86563,  3.15268,  3.46109,
         3.79551,  4.16194,  4.56826,  5.02516,  5.54737,
         6.15589,  6.88155,  7.77132,  8.89990, 10.39252,
        12.47441, 15.59371, 20.79167, 31.18750, 50.00000,
       300.00000
    ];

    /// <summary>
    /// 根据改命次数按 Softmax 温度模型从 [<see cref="RollTotalMin"/>, <see cref="RollTotalMax"/>] 采样总点数。
    /// </summary>
    public static int SampleTotalPoints(long seed, int averageBonus)
    {
        var random = new DeterministicRandom(seed);
        return SampleTotalPoints(ref random, averageBonus);
    }

    private static int SampleTotalPoints(ref DeterministicRandom random, int averageBonus)
    {
        var bonus = Math.Max(0, averageBonus);
        var idx = Math.Min(bonus, LambdaTable.Length - 1);
        var lam = LambdaTable[idx];
        var u = Math.Clamp(random.NextDouble(), 1e-12, 1.0 - 1e-12);
        double y;
        if (Math.Abs(lam) < 1e-6)
        {
            y = u;
        }
        else if (lam > 20.0)
        {
            y = 1.0 + Math.Log(Math.Max(1e-15, u + (1.0 - u) * Math.Exp(-lam))) / lam;
        }
        else if (lam < -20.0)
        {
            y = Math.Log(Math.Max(1e-15, 1.0 - u + u * Math.Exp(lam))) / lam;
        }
        else
        {
            y = Math.Log(Math.Max(1e-15, 1.0 + u * (Math.Exp(lam) - 1.0))) / lam;
        }
        y = Math.Clamp(y, 0.0, 1.0);
        return Math.Clamp((int)Math.Round(1.0 + 499.0 * y), RollTotalMin, RollTotalMax);
    }

    /// <summary>
    /// 开天门抽取：
    /// 1. 按 Softmax 温度分布采样总点数 [RollTotalMin, RollTotalMax]（期望 100~500，全域支持）；
    /// 2. 按容量下限 [ceil(total/100), 5] 随机抽取本局有效灵根条数 N；
    /// 3. Fisher-Yates 随机选出 N 个五行属性，其余 5 - N 个属性归 0；
    /// 4. 将总点数切分到这 N 个属性上，每系保底 1 点、上限 100 点，总和严格等于 total。
    /// </summary>
    /// <param name="seed">抽取随机种子。</param>
    /// <param name="averageBonus">逆天改命累计次数（每次提升 10 点总值期望，上限 500）。</param>
    /// <returns>抽取值（未确认生效）。</returns>
    public static LinggenValues Roll(long seed, int averageBonus = 0)
    {
        var random = new DeterministicRandom(seed);
        var total = SampleTotalPoints(ref random, averageBonus);

        // 容量感知条数范围：每系上限 100，装下 total 至少需要 ceil(total / 100) 条
        var minCount = Math.Max(1, (total + RollAxisCap - 1) / RollAxisCap);
        var maxCount = Math.Min(5, total);
        if (minCount > maxCount) minCount = maxCount;
        var count = random.NextInt(minCount, maxCount + 1);

        // 从五轴中随机挑选 count 个轴（Fisher-Yates 随机前 count 项）
        var elements = new[] { 0, 1, 2, 3, 4 };
        for (var i = 0; i < count; i++)
        {
            var swapIdx = random.NextInt(i, 5);
            (elements[i], elements[swapIdx]) = (elements[swapIdx], elements[i]);
        }

        // 分配点数给被选中的 count 个轴：每轴保底 1 点
        var axes = new int[5];
        for (var i = 0; i < count; i++)
        {
            axes[elements[i]] = 1;
        }

        var remainder = total - count;
        if (remainder > 0)
        {
            var weights = new double[count];
            var weightSum = 0.0;
            for (var i = 0; i < count; i++)
            {
                var w = 0.1 + 0.9 * random.NextDouble();
                weights[i] = w;
                weightSum += w;
            }

            var allocated = 0;
            for (var i = 0; i < count; i++)
            {
                var share = (int)Math.Floor(remainder * (weights[i] / weightSum));
                var extra = Math.Min(RollAxisCap - 1, share);
                axes[elements[i]] += extra;
                allocated += extra;
            }

            var overflow = remainder - allocated;
            for (var i = 0; i < count && overflow > 0; i++)
            {
                var headroom = RollAxisCap - axes[elements[i]];
                if (headroom <= 0) continue;
                var add = Math.Min(headroom, overflow);
                axes[elements[i]] += add;
                overflow -= add;
            }
        }

        return new LinggenValues(axes[0], axes[1], axes[2], axes[3], axes[4]);
    }

    /// <summary>
    /// 单轴点数池净贡献：低于先天值按「散功 2 返 1」折半**返还**（正贡献），
    /// 高于先天值按 1:1 **消耗**（负贡献）。
    /// </summary>
    /// <param name="innateValue">该轴抽取时的先天值。</param>
    /// <param name="currentValue">该轴当前值。</param>
    public static double ResolveAxisPointContribution(int innateValue, int currentValue)
        => currentValue <= innateValue
            ? (innateValue - currentValue) * 0.5
            : -(currentValue - innateValue);

    /// <summary>
    /// 剩余可分配点数：五轴净贡献之和（散功折半返还记正、灌注 1:1 消耗记负）。
    /// 为负表示当前分布已透支可分配额度；为 0 表示额度刚好用尽。
    /// </summary>
    /// <param name="innate">抽取时的先天分布。</param>
    /// <param name="current">当前分布。</param>
    public static double ResolveRemainingPoints(LinggenValues innate, LinggenValues current)
    {
        var total = 0d;
        foreach (var element in Enum.GetValues<LinggenElement>())
            total += ResolveAxisPointContribution(innate.Of(element), current.Of(element));
        return Math.Round(total * 2, MidpointRounding.AwayFromZero) / 2;
    }

    /// <summary>
    /// 单轴在其余四轴让点支持下可达的最高值：让点额度按 1:1 转化为该轴点数。
    /// </summary>
    /// <param name="innate">抽取时的先天分布。</param>
    /// <param name="current">当前分布。</param>
    /// <param name="axis">目标轴。</param>
    public static int ResolveMaxReachableValue(LinggenValues innate, LinggenValues current, LinggenElement axis)
    {
        var pool = 0d;
        foreach (var element in Enum.GetValues<LinggenElement>())
        {
            if (element == axis) continue;
            pool += ResolveAxisPointContribution(innate.Of(element), current.Of(element));
        }
        // 其余轴让出的点数按 1:1 追加到本轴；让点额度可能为负（其余轴被灌注过），此时上限低于当前值。
        var raw = current.Of(axis) + pool;
        return Math.Clamp((int)Math.Floor(raw), 0, LinggenValues.MaxPointsPerElement);
    }

    /// <summary>
    /// 校验重分配是否成立：五系点数合法、剩余点数不得为负、且至少保留一条有效灵根。
    /// </summary>
    /// <param name="innate">抽取时的先天分布（原生点数基准）。</param>
    /// <param name="candidate">玩家提交的分布。</param>
    public static bool IsValidAllocation(LinggenValues innate, LinggenValues candidate)
        => candidate.IsValid
            && ResolveRemainingPoints(innate, candidate) >= 0
            && candidate.EffectiveCount > 0;

    /// <summary>
    /// 灵根提供的五行增伤点数：每点灵根提供 0.25 点增伤，100 点即 25%（Linear 直加，总纲 §7.7.5.1）。
    /// </summary>
    /// <param name="points">该系灵根点数。</param>
    public static double ResolveDamageBonusPoints(int points)
        => Math.Clamp(points, 0, LinggenValues.MaxPointsPerElement) * 0.25;

    /// <summary>
    /// 灵根提供的五行减伤点数：每点灵根提供 0.25 点减伤点数，100 点对应 25 点（NonLinear 收敛，总纲 §7.7.5.1）。
    /// </summary>
    /// <param name="points">该系灵根点数。</param>
    public static double ResolveDamageReductionPoints(int points)
        => Math.Clamp(points, 0, LinggenValues.MaxPointsPerElement) * 0.25;

    // ── 火系衍生 ──
    /// <summary>火灵根暴击率加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveFireCriticalChanceRatio(int firePoints)
        => 0.50 * (Math.Clamp(firePoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>火灵根暴击伤害加成（100 点 = +25 点暴伤点数）。</summary>
    public static double ResolveFireCriticalDamageBonusPoints(int firePoints)
        => 25.0 * (Math.Clamp(firePoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    // ── 水系衍生 ──
    /// <summary>水灵根最大灵力上限加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveWaterMaxManapointsRatio(int waterPoints)
        => 0.50 * (Math.Clamp(waterPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>水灵根化解/拆招加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveWaterParryRatio(int waterPoints)
        => 0.50 * (Math.Clamp(waterPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>水灵根生命与灵力自动回复加成（100 点 = +100% 基础属性比例）。</summary>
    public static double ResolveWaterRegenRatio(int waterPoints)
        => 1.00 * (Math.Clamp(waterPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>水灵根波间寻怪速度点数（100 点 = +20 点寻怪速度点数）。</summary>
    public static double ResolveWaterSearchSpeedPoints(int waterPoints)
        => 20.0 * (Math.Clamp(waterPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    // ── 木系衍生 ──
    /// <summary>木灵根最大生命上限加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveWoodMaxHitpointsRatio(int woodPoints)
        => 0.50 * (Math.Clamp(woodPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>木灵根治疗与被治疗增幅加成（100 点 = +50 点治疗点数）。</summary>
    public static double ResolveWoodHealingBonusPoints(int woodPoints)
        => 50.0 * (Math.Clamp(woodPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    // ── 金系衍生 ──
    /// <summary>金灵根双攻加成（物理攻击与术法攻击各 +50% 基础属性比例）。</summary>
    public static double ResolveMetalAttackRatio(int metalPoints)
        => 0.50 * (Math.Clamp(metalPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>金灵根破招加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveMetalBreakRatio(int metalPoints)
        => 0.50 * (Math.Clamp(metalPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>金灵根命中加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveMetalAccuracyRatio(int metalPoints)
        => 0.50 * (Math.Clamp(metalPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    // ── 土系衍生 ──
    /// <summary>土灵根双防加成（物理护甲与法术抗性各 +50% 基础属性比例）。</summary>
    public static double ResolveEarthDefenseRatio(int earthPoints)
        => 0.50 * (Math.Clamp(earthPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>土灵根免爆抗暴加成（100 点 = +50% 基础属性比例）。</summary>
    public static double ResolveEarthCriticalAvoidanceRatio(int earthPoints)
        => 0.50 * (Math.Clamp(earthPoints, 0, LinggenValues.MaxPointsPerElement) / 100.0);

    /// <summary>
    /// 土灵根固定减伤（保底机制：P=0 为 0；P>=1 时基础保底 1.0 点，每点灵根额外 +0.09 点，满级 100 点整 10.0 点）。
    /// </summary>
    public static double ResolveEarthFixedDamageReduction(int earthPoints)
    {
        var safe = Math.Clamp(earthPoints, 0, LinggenValues.MaxPointsPerElement);
        return safe <= 0 ? 0 : 1.0 + safe * 0.09;
    }

    // ── 百艺机缘速度 ──
    /// <summary>
    /// 灵根对七大生活技艺行动的耗时折减加成（duration /= 1 + 技艺等级加成 + 工具加成 + 灵根百艺加成）：
    /// 炼丹/炼器(火)、符箓(水)、采集(木)、强化(金)、阵法(土)，100 点各加速 25%（总纲 §7.7.5.1）。
    /// </summary>
    public static double ResolveTechniqueSpeedRatio(LinggenValues linggen, string? technique) => technique switch
    {
        GameData.TechniqueCatalog.Alchemy => 0.25 * (Math.Clamp(linggen.Fire, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        GameData.TechniqueCatalog.Forging => 0.25 * (Math.Clamp(linggen.Fire, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        GameData.TechniqueCatalog.Talisman => 0.25 * (Math.Clamp(linggen.Water, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        GameData.TechniqueCatalog.Gathering => 0.25 * (Math.Clamp(linggen.Wood, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        GameData.TechniqueCatalog.Enhancement => 0.25 * (Math.Clamp(linggen.Metal, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        GameData.TechniqueCatalog.Formation => 0.25 * (Math.Clamp(linggen.Earth, 0, LinggenValues.MaxPointsPerElement) / 100.0),
        _ => 0
    };

    /// <summary>
    /// 灵根带来的升级所需修为乘区：每多一条有效灵根使上限 +50%，五系齐全为 3.0 倍。
    /// 未入天门时恒为 1.0（调配阶段不放大）。
    /// </summary>
    /// <param name="values">生效灵根。</param>
    /// <param name="entered">是否已入天门。</param>
    public static double ResolveCultivationMultiplier(LinggenValues values, bool entered)
    {
        if (!entered) return 1;
        // 五系齐全即 3.0 倍封顶：基础 1.0 已含首条灵根，此后每多一条再 +50%。
        var effective = values.EffectiveCount;
        return effective <= 0 ? 1 : 1 + 0.5 * (Math.Min(effective, 5) - 1);
    }

    /// <summary>
    /// 本层修为瓶颈（含灵根乘区，总纲 §7.7.5.1）：配表 ExpCapFor × <see cref="ResolveCultivationMultiplier"/>。
    /// 突破校验、行动修为封顶与底蕴成本折算基数统一走本口径，保证全链路一致；瓶颈非正时原样返回。
    /// </summary>
    /// <param name="realmLevel">当前境界等级。</param>
    /// <param name="confirmedValues">已确认灵根（未开天门传 Empty）。</param>
    /// <param name="entered">是否已入天门。</param>
    /// <param name="realm">境界进度配表。</param>
    public static double ResolveExpCapWithLinggen(int realmLevel, LinggenValues confirmedValues, bool entered,
        GameData.RealmProgressionCatalog? realm)
    {
        var rawCap = realm?.ExpCapFor(realmLevel) ?? 0;
        if (rawCap <= 0) return rawCap;
        return rawCap * ResolveCultivationMultiplier(confirmedValues, entered);
    }

    /// <summary>
    /// 确定性伪随机数发生器：xorshift 变体，保证同种子同序列，便于离线补算与在线逐帧结果一致。
    /// </summary>
    private struct DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(long seed)
        {
            var raw = seed == 0 ? 0x9E3779B97F4A7C15UL : unchecked((ulong)seed);
            _state = SplitMix64(raw);
        }

        private static ulong SplitMix64(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
        /// <summary>取 [minInclusive, maxExclusive) 内的整数；区间非法时返回 minInclusive。</summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            var span = (ulong)(maxExclusive - minInclusive);
            return minInclusive + (int)(_state % span);
        }

        /// <summary>取 [0.0, 1.0) 内的双精度浮点数。</summary>
        public double NextDouble()
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return (_state >> 11) * (1.0 / (1UL << 53));
        }
    }
}
