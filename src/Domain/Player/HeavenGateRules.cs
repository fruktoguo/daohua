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

    /// <summary>开天门抽取的总值下限与上限：总值越高，五行分布越充裕。</summary>
    public const int RollTotalMin = 120;

    /// <summary>开天门抽取的总值上限。</summary>
    public const int RollTotalMax = 300;

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
    /// 开天门抽取：在 [<see cref="RollTotalMin"/>, <see cref="RollTotalMax"/>] 内确定总值，
    /// 再按确定性随机把总值切分到五系，单系不超过 <see cref="RollAxisCap"/>。
    /// 总值与切分全部由 seed 决定，保证同种子结果逐位一致。
    /// </summary>
    /// <param name="seed">抽取随机种子。</param>
    /// <param name="averageBonus">逆天改命累计的均值加成（每次改命提升抽取下限）。</param>
    /// <returns>抽取值（未确认生效）。</returns>
    public static LinggenValues Roll(long seed, int averageBonus = 0)
    {
        var random = new DeterministicRandom(seed);
        var bonusSpan = Math.Max(0, averageBonus) * 10;
        var min = Math.Min(RollTotalMax, RollTotalMin + bonusSpan);
        var total = random.NextInt(min, RollTotalMax + 1);
        var axes = new int[5];
        var remaining = total;
        for (var i = 0; i < axes.Length; i++)
        {
            var axesLeft = axes.Length - i;
            if (axesLeft == 1)
            {
                axes[i] = Math.Min(RollAxisCap, remaining);
                break;
            }
            var reserveForRest = (axesLeft - 1) * 0;
            var maxHere = Math.Min(RollAxisCap, Math.Max(0, remaining - reserveForRest));
            var slice = axesLeft <= 2
                ? maxHere
                : random.NextInt(maxHere / 2, maxHere + 1);
            axes[i] = slice;
            remaining -= slice;
        }
        // 总值超出单轴上界总和时，余量按剩余容量回填，保证抽取值不静默丢失。
        var overflow = remaining;
        for (var i = 0; i < axes.Length && overflow > 0; i++)
        {
            var headroom = RollAxisCap - axes[i];
            if (headroom <= 0) continue;
            var add = Math.Min(headroom, overflow);
            axes[i] += add;
            overflow -= add;
        }
        return new LinggenValues(axes[0], axes[1], axes[2], axes[3], axes[4]);
    }

    /// <summary>
    /// 校验重分配是否守恒：五系点数全部合法，且总值与抽取结果一致。
    /// 玩家只能在总值不变的前提下调整五行分布。
    /// </summary>
    /// <param name="rolled">本次抽取的原始结果。</param>
    /// <param name="candidate">玩家提交的分布。</param>
    public static bool IsValidAllocation(LinggenValues rolled, LinggenValues candidate)
        => candidate.IsValid
            && candidate.Total == rolled.Total
            && candidate.EffectiveCount > 0;

    /// <summary>
    /// 灵根提供的五行增伤点数：每点灵根提供 1 点增伤，100 点即 100%（Linear 直加）。
    /// </summary>
    /// <param name="points">该系灵根点数。</param>
    public static double ResolveDamageBonusPoints(int points)
        => Math.Clamp(points, 0, LinggenValues.MaxPointsPerElement);

    /// <summary>
    /// 灵根提供的五行减伤点数：与增伤同为每点 1 点，但减伤走 NonLinear 曲线，
    /// 100 点对应实际减伤 50%（伤害减半），与总纲第七篇 §7.7.5 收敛口径一致。
    /// </summary>
    /// <param name="points">该系灵根点数。</param>
    public static double ResolveDamageReductionPoints(int points)
        => Math.Clamp(points, 0, LinggenValues.MaxPointsPerElement);

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
    /// 确定性伪随机数发生器：xorshift 变体，保证同种子同序列，便于离线补算与在线逐帧结果一致。
    /// </summary>
    private struct DeterministicRandom(long seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : unchecked((ulong)seed);

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
    }
}
