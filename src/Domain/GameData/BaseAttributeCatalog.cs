using System.Text.Json;

namespace Domain.GameData;

/// <summary>单个境界等级的基础属性锚点：15 维面板基准值。</summary>
public sealed record BaseAttributeAnchor(
    double Level, double Hp, double Mp, double Ad, double Ap, double Armor, double Mr,
    double Acc, double Eva, double Crit, double Cavoid, double BreakPt, double Parry,
    double HpRegen, double MpRegen);

/// <summary>境界属性按配表锚点插值；回复是独立列，不从生命或灵力上限反推。</summary>
public sealed class BaseAttributeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    /// <summary>等级严格递增的属性锚点表。</summary>
    public IReadOnlyList<BaseAttributeAnchor> Anchors { get; }

    /// <summary>以锚点表创建目录；锚点必须非空、等级严格递增且全部数值非负有限。</summary>
    /// <param name="anchors">按等级排序的属性锚点表。</param>
    public BaseAttributeCatalog(IReadOnlyList<BaseAttributeAnchor> anchors)
    {
        if (anchors.Count == 0) throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性锚点不能为空。");
        var previous = 0d;
        foreach (var anchor in anchors)
        {
            var values = new[] { anchor.Level, anchor.Hp, anchor.Mp, anchor.Ad, anchor.Ap, anchor.Armor,
                anchor.Mr, anchor.Acc, anchor.Eva, anchor.Crit, anchor.Cavoid, anchor.BreakPt, anchor.Parry,
                anchor.HpRegen, anchor.MpRegen };
            if (values.Any(x => !double.IsFinite(x) || x < 0) || anchor.Level <= previous || anchor.Hp <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性锚点必须递增且数值有效。");
            previous = anchor.Level;
        }
        Anchors = anchors.ToArray();
    }

    /// <summary>从 JSON 数组反序列化锚点目录。</summary>
    /// <param name="value">锚点 JSON 数组。</param>
    /// <returns>解析完成的锚点目录。</returns>
    public static BaseAttributeCatalog Parse(JsonElement value)
    {
        return new BaseAttributeCatalog(value.Deserialize<BaseAttributeAnchor[]>(JsonOptions)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性配置无效。"));
    }

    /// <summary>
    /// 从 expected-attributes.json 的 `levelExpected[].finalExpected` 解析每级期望玩家面板锚点目录（四乘区完全体）。
    /// 怪物同构建模以此面板为底座（总纲 §7.3/§10.1），而非裸 `baseAttributeAnchors`；
    /// 期望面板不建模回复列，HpRegen/MpRegen 置 0（怪物面板不消费回复属性）。
    /// </summary>
    /// <param name="tiers">expected-attributes 中携带 level/finalExpected 的锚点数组 JSON（levelExpected 或 tiers 均可）。</param>
    /// <returns>按等阶锚点等级排列的期望属性目录。</returns>
    public static BaseAttributeCatalog ParseExpected(JsonElement tiers)
    {
        var anchors = new List<BaseAttributeAnchor>();
        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object
                || !tier.TryGetProperty("finalExpected", out var expected) || expected.ValueKind != JsonValueKind.Object)
                continue;
            double Field(string key) => JsonRead.Number(expected, key, 0);
            anchors.Add(new BaseAttributeAnchor(
                JsonRead.Number(tier, "level", 0),
                Field("maxHitpoints"), Field("maxManapoints"),
                Field("physicalAttack"), Field("magicAttack"),
                Field("armor"), Field("magicResistance"),
                Field("accuracyRating"), Field("evasionRating"),
                Field("criticalRating"), Field("criticalAvoidance"),
                Field("breakRating"), Field("parryRating"), 0, 0));
        }
        return new BaseAttributeCatalog(anchors);
    }

    /// <summary>按等级在相邻锚点间线性插值；越界时钳制到端点锚点。</summary>
    /// <param name="level">目标境界等级。</param>
    /// <returns>插值后的属性基准（Level 字段为目标等级）。</returns>
    public BaseAttributeAnchor At(double level)
    {
        var low = Anchors[0];
        var high = low;
        foreach (var anchor in Anchors)
        {
            high = anchor;
            if (anchor.Level >= level) break;
            low = anchor;
        }
        var ratio = high.Level == low.Level ? 0 : Math.Clamp((level - low.Level) / (high.Level - low.Level), 0, 1);
        double Mix(double a, double b) => a + (b - a) * ratio;
        return new(level, Mix(low.Hp, high.Hp), Mix(low.Mp, high.Mp), Mix(low.Ad, high.Ad), Mix(low.Ap, high.Ap),
            Mix(low.Armor, high.Armor), Mix(low.Mr, high.Mr), Mix(low.Acc, high.Acc), Mix(low.Eva, high.Eva),
            Mix(low.Crit, high.Crit), Mix(low.Cavoid, high.Cavoid), Mix(low.BreakPt, high.BreakPt),
            Mix(low.Parry, high.Parry), Mix(low.HpRegen, high.HpRegen), Mix(low.MpRegen, high.MpRegen));
    }

    /// <summary>等阶 = 等级不超过给定等级的锚点个数（锚点即各等阶起始等级），T1 起步。</summary>
    public int TierAt(double level)
    {
        var tier = 0;
        foreach (var anchor in Anchors)
        {
            if (anchor.Level > level) break;
            tier++;
        }
        return Math.Max(1, tier);
    }
}

/// <summary>
/// 境界修为与寿元配置：expToNext 为本层升至下一级所需修为（Lv.127 封顶为 0），
/// lifespanYearsByTier 为 T1~T12 等阶寿元上限（年）。
/// </summary>
public sealed record RealmProgressionCatalog(IReadOnlyList<double> ExpToNext, IReadOnlyList<double> LifespanYearsByTier)
{
    /// <summary>最大境界等级（expToNext 长度）。</summary>
    public int MaxLevel => ExpToNext.Count;

    /// <summary>当前层升至下一级所需修为；已满级返回 0。</summary>
    public double ExpCapFor(int realmLevel)
        => realmLevel >= 1 && realmLevel <= ExpToNext.Count ? ExpToNext[realmLevel - 1] : 0;

    /// <summary>
    /// 到达指定等级所需的累计修为 Σ expToNext[1..level-1]（怪物战斗经验派生源，总纲 §7.7.6）。
    /// level ≤ 1 时为空累计返回 0；超出配表上界的等级按已配层数截断。
    /// </summary>
    public double CumulativeExpFor(int level)
    {
        var count = Math.Clamp(level - 1, 0, ExpToNext.Count);
        var total = 0d;
        for (var i = 0; i < count; i++) total += ExpToNext[i];
        return total;
    }

    /// <summary>六维点数上限：每级境界对应 3 点，共 3×MaxLevel（127 级表为 381 点）。</summary>
    public int RootPointCap => ExpToNext.Count * 3;

    /// <summary>六维升级成本表（懒构建缓存）：PointCost[p] 为升至第 p 点所需经验，Cumulative[p] 为升至 p 点的累计经验。</summary>
    private (double[] PointCost, double[] Cumulative)? _rootPointTable;

    private (double[] PointCost, double[] Cumulative) RootPointTable => _rootPointTable ??= BuildRootPointTable();

    /// <summary>
    /// 由境界修为表展开六维点成本表（总纲 §3.2.2）：记 B[i] = expToNext[max(0, i−1)]
    /// （跨入第 i+1 级消耗的那格修为条，B[0] 取第一条），升至第 p 点成本 =
    /// B 在 t=(p−1)/3 处的线性插值——一级境界摊三点，相邻档间平滑过渡；
    /// 单维累计至 3Lv 点 ≈ 3×ΣexpToNext[1..Lv−1]（约 3.05~3.25 倍境界累计修为）。
    /// </summary>
    private (double[] PointCost, double[] Cumulative) BuildRootPointTable()
    {
        var cap = RootPointCap;
        var cost = new double[cap + 1];
        var cumulative = new double[cap + 1];
        for (var p = 1; p <= cap; p++)
        {
            var t = (p - 1) / 3d;
            var i0 = (int)Math.Floor(t);
            var i1 = Math.Min(i0 + 1, ExpToNext.Count - 1);
            var b0 = ExpToNext[i0 <= 0 ? 0 : i0 - 1];
            var b1 = ExpToNext[i1 - 1];
            cost[p] = b0 + (b1 - b0) * (t - i0);
            cumulative[p] = cumulative[p - 1] + cost[p];
        }
        return (cost, cumulative);
    }

    /// <summary>累计六维经验换算点数（0 点起算）；经验非有限或 ≤0 返回 0。</summary>
    public int RootLevelFor(double exp)
    {
        if (!double.IsFinite(exp) || exp <= 0) return 0;
        var index = Array.BinarySearch(RootPointTable.Cumulative, exp);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, RootPointCap);
    }

    /// <summary>升至指定点数所需的累计六维经验；越界按下限 0/上限截断。</summary>
    public double RootCumulativeExpFor(int points)
        => RootPointTable.Cumulative[Math.Clamp(points, 0, RootPointCap)];

    /// <summary>points → points+1 点所需六维经验；已满点返回 0。</summary>
    public double RootExpToNextFor(int points)
        => points < 0 || points >= RootPointCap ? 0 : RootPointTable.PointCost[points + 1];

    /// <summary>
    /// 境界等级标准基准经验（总纲 §7.10.3 / §20.1.4）：
    /// expToNext[level] / 1000 × 0.9^(level-1)。
    /// 等级 1 时为 10 点，等级 18 时为 133.417 点。
    /// 作为怪物普通击杀经验（1×）及 10 秒行动 100% 经验产出的统一数值基准。
    /// </summary>
    public double BaseExperienceForLevel(int level)
    {
        var capLevel = Math.Clamp(level, 1, Math.Max(1, ExpToNext.Count - 1));
        return ExpCapFor(capLevel) / 1000d * Math.Pow(0.9, Math.Max(1, level) - 1);
    }

    /// <summary>
    /// 怪物击杀修为奖励派生（总纲 §7.10.3）：复用等级基准经验 × 层次倍率
    /// （小怪 1×/精英 3×/妖王 10×）。份额以 Lv1 = 1/1000 为基准每级指数衰减 10% 渐近 0；
    /// 等级超出配表上界时按末档非零修为条派生。配表 experience 显式声明时覆盖本值。
    /// </summary>
    public double MonsterKillExperience(int level, CombatMonsterRank rank)
    {
        var multiplier = rank switch { CombatMonsterRank.Elite => 3d, CombatMonsterRank.Boss => 10d, _ => 1d };
        return BaseExperienceForLevel(level) * multiplier;
    }

    /// <summary>等阶对应寿元上限（年）；越界取末档。</summary>
    public double LifespanYearsFor(int tier)
        => LifespanYearsByTier.Count == 0 ? 0
            : LifespanYearsByTier[Math.Clamp(tier, 1, LifespanYearsByTier.Count) - 1];

    /// <summary>解析 realmProgression 配置（修为上限与寿元表）。</summary>
    /// <param name="value">realmProgression JSON 对象。</param>
    /// <returns>解析完成的境界进度配置。</returns>
    public static RealmProgressionCatalog Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("expToNext", out var exp) || exp.ValueKind != JsonValueKind.Array
            || !value.TryGetProperty("lifespanYearsByTier", out var lifespan) || lifespan.ValueKind != JsonValueKind.Array)
            throw new DomainException(DomainErrorCode.InvalidArgument, "境界进度配置无效。");
        var expToNext = exp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var lifespanYears = lifespan.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        if (expToNext.Length == 0 || lifespanYears.Length == 0
            || expToNext.Any(v => !double.IsFinite(v) || v < 0)
            || expToNext.Take(expToNext.Length - 1).Any(v => v <= 0)
            || lifespanYears.Any(v => !double.IsFinite(v) || v <= 0))
            throw new DomainException(DomainErrorCode.InvalidArgument, "境界进度配置数值无效。");
        return new RealmProgressionCatalog(expToNext, lifespanYears);
    }
}
