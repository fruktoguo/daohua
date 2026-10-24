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
    /// <summary>从 JSON 数组反序列化锚点目录，并程序化应用前三后二缩放规则。</summary>
    /// <param name="value">锚点 JSON 数组。</param>
    /// <returns>解析完成的锚点目录。</returns>
    public static BaseAttributeCatalog Parse(JsonElement value)
    {
        var raw = value.Deserialize<BaseAttributeAnchor[]>(JsonOptions)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性配置无效。");
        return new BaseAttributeCatalog(ScaleAnchors(raw));
    }

    /// <summary>仅反序列化原始配置锚点，不应用缩放（差量对账用）。</summary>
    public static BaseAttributeCatalog ParseRaw(JsonElement value)
    {
        var raw = value.Deserialize<BaseAttributeAnchor[]>(JsonOptions)
            ?? throw new DomainException(DomainErrorCode.InvalidArgument, "基础属性配置无效。");
        return new BaseAttributeCatalog(raw);
    }

    /// <summary>
    /// 程序化缩放锚点（总纲 §3.2.1）：
    /// Lv.18 前增量 3.0 倍，Lv.18 后各等阶增量 2.0 倍累进放大，确保平滑单调且彻底消除倒挂。
    /// 若锚点数不足 2 或不包含 Lv.18 节点（例如单元测试用例假数据），保持原样不变。
    /// </summary>
    internal static BaseAttributeAnchor[] ScaleAnchors(BaseAttributeAnchor[] raw)
    {
        if (raw.Length < 2 || raw[0].Level != 1) return raw;
        var idx18 = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i].Level == 18) { idx18 = i; break; }
        }
        if (idx18 < 0) return raw;

        var a1 = raw[0];
        var a18 = raw[idx18];

        var result = new BaseAttributeAnchor[raw.Length];
        result[0] = a1;

        static double Scale18(double v1, double v18) => v1 + 3.0 * (v18 - v1);
        var scaled18 = new BaseAttributeAnchor(
            18,
            Scale18(a1.Hp, a18.Hp),
            Scale18(a1.Mp, a18.Mp),
            Scale18(a1.Ad, a18.Ad),
            Scale18(a1.Ap, a18.Ap),
            Scale18(a1.Armor, a18.Armor),
            Scale18(a1.Mr, a18.Mr),
            Scale18(a1.Acc, a18.Acc),
            Scale18(a1.Eva, a18.Eva),
            Scale18(a1.Crit, a18.Crit),
            Scale18(a1.Cavoid, a18.Cavoid),
            Scale18(a1.BreakPt, a18.BreakPt),
            Scale18(a1.Parry, a18.Parry),
            Scale18(a1.HpRegen, a18.HpRegen),
            Scale18(a1.MpRegen, a18.MpRegen)
        );

        for (var i = 1; i < raw.Length; i++)
        {
            var cur = raw[i];
            if (cur.Level == 18)
            {
                result[i] = scaled18;
            }
            else if (cur.Level < 18)
            {
                static double ScalePre18(double v1, double vi) => v1 + 3.0 * (vi - v1);
                result[i] = new BaseAttributeAnchor(
                    cur.Level,
                    ScalePre18(a1.Hp, cur.Hp),
                    ScalePre18(a1.Mp, cur.Mp),
                    ScalePre18(a1.Ad, cur.Ad),
                    ScalePre18(a1.Ap, cur.Ap),
                    ScalePre18(a1.Armor, cur.Armor),
                    ScalePre18(a1.Mr, cur.Mr),
                    ScalePre18(a1.Acc, cur.Acc),
                    ScalePre18(a1.Eva, cur.Eva),
                    ScalePre18(a1.Crit, cur.Crit),
                    ScalePre18(a1.Cavoid, cur.Cavoid),
                    ScalePre18(a1.BreakPt, cur.BreakPt),
                    ScalePre18(a1.Parry, cur.Parry),
                    ScalePre18(a1.HpRegen, cur.HpRegen),
                    ScalePre18(a1.MpRegen, cur.MpRegen)
                );
            }
            else
            {
                static double ScalePost18(double vNew18, double vOld18, double vi) => vNew18 + 2.0 * (vi - vOld18);
                result[i] = new BaseAttributeAnchor(
                    cur.Level,
                    ScalePost18(scaled18.Hp, a18.Hp, cur.Hp),
                    ScalePost18(scaled18.Mp, a18.Mp, cur.Mp),
                    ScalePost18(scaled18.Ad, a18.Ad, cur.Ad),
                    ScalePost18(scaled18.Ap, a18.Ap, cur.Ap),
                    ScalePost18(scaled18.Armor, a18.Armor, cur.Armor),
                    ScalePost18(scaled18.Mr, a18.Mr, cur.Mr),
                    ScalePost18(scaled18.Acc, a18.Acc, cur.Acc),
                    ScalePost18(scaled18.Eva, a18.Eva, cur.Eva),
                    ScalePost18(scaled18.Crit, a18.Crit, cur.Crit),
                    ScalePost18(scaled18.Cavoid, a18.Cavoid, cur.Cavoid),
                    ScalePost18(scaled18.BreakPt, a18.BreakPt, cur.BreakPt),
                    ScalePost18(scaled18.Parry, a18.Parry, cur.Parry),
                    ScalePost18(scaled18.HpRegen, a18.HpRegen, cur.HpRegen),
                    ScalePost18(scaled18.MpRegen, a18.MpRegen, cur.MpRegen)
                );
            }
        }
        return result;
    }

    /// <summary>
    /// 从 expected-attributes.json 的 `levelExpected[].finalExpected` 解析每级期望玩家面板锚点目录（四乘区完全体）。
    /// 怪物同构建模以此面板为底座（总纲 §7.3/§10.1），而非裸 `baseAttributeAnchors`；
    /// 期望面板不建模回复列，HpRegen/MpRegen 置 0（怪物面板不消费回复属性）。
    /// </summary>
    /// <param name="tiers">expected-attributes 中携带 level/finalExpected 的锚点数组 JSON（levelExpected 或 tiers 均可）。</param>
    /// <param name="scaledBase">已应用前三后二缩放的境界基础属性目录（若提供，用于同步抬升同构期望底座）。</param>
    /// <param name="rawBase">原始未缩放的境界基础属性目录。</param>
    /// <returns>按等阶锚点等级排列的期望属性目录。</returns>
    public static BaseAttributeCatalog ParseExpected(JsonElement tiers, BaseAttributeCatalog? scaledBase = null, BaseAttributeCatalog? rawBase = null)
    {
        var anchors = new List<BaseAttributeAnchor>();
        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object
                || !tier.TryGetProperty("finalExpected", out var expected) || expected.ValueKind != JsonValueKind.Object)
                continue;
            var level = JsonRead.Number(tier, "level", 0);
            double Field(string key) => JsonRead.Number(expected, key, 0);

            double Diff(Func<BaseAttributeAnchor, double> getter, bool isAp = false)
                => scaledBase is not null && rawBase is not null
                    ? Math.Max(0, getter(scaledBase.At(level)) - getter(rawBase.At(level))) * CompoundMultiplier(level, isAp)
                    : 0;

            anchors.Add(new BaseAttributeAnchor(
                level,
                Field("maxHitpoints") + Diff(a => a.Hp),
                Field("maxManapoints") + Diff(a => a.Mp),
                Field("physicalAttack") + Diff(a => a.Ad),
                Field("magicAttack") + Diff(a => a.Ap, isAp: true),
                Field("armor") + Diff(a => a.Armor),
                Field("magicResistance") + Diff(a => a.Mr),
                Field("accuracyRating") + Diff(a => a.Acc),
                Field("evasionRating") + Diff(a => a.Eva),
                Field("criticalRating") + Diff(a => a.Crit),
                Field("criticalAvoidance") + Diff(a => a.Cavoid),
                Field("breakRating") + Diff(a => a.BreakPt),
                Field("parryRating") + Diff(a => a.Parry), 0, 0));
        }
        return new BaseAttributeCatalog(anchors);
    }

    private static double CompoundMultiplier(double level, bool isAp = false)
    {
        var pts = 3 * level;
        var rootMult = 1.0 + pts * (isAp ? 0.02 : 0.01);
        var equipMult = level < 18 ? 1.1158 : level < 30 ? 1.2042 : level < 42 ? 1.2814 : level < 54 ? 1.4654 : level < 66 ? 1.6516 : 1.8979;
        var buffMult = level < 18 ? 1.0042 : level < 30 ? 1.0133 : level < 42 ? 1.0333 : level < 54 ? 1.075 : level < 66 ? 1.1333 : 1.2292;
        return rootMult * equipMult * buffMult;
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
