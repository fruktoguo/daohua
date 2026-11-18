using Domain.GameData;

namespace Domain.Player;

/// <summary>
/// 筑基期专属养成「根基」状态：Lv.30 半步筑基满修为后解锁，提供全六维百分比增幅。
/// 开启后作为常驻养成保留，消耗当前层修为逐点凝练。
/// </summary>
public sealed class PlayerGenjiState
{
    /// <summary>已凝练的根基点数（每点提升 +1% 全六维，向上取整）。</summary>
    public int Points { get; private set; }

    /// <summary>是否已解锁根基常驻养成（Lv.30 首次满修为或突破至 Lv.31+ 后置 true）。</summary>
    public bool Unlocked { get; private set; }

    /// <summary>本地状态版本号：任何写入自增，供属性解析缓存失效检测。</summary>
    public long Version { get; private set; }

    /// <summary>写入根基点数与解锁标记，并推进版本号。</summary>
    /// <param name="points">当前根基点数（非负）。</param>
    /// <param name="unlocked">是否已解锁根基。</param>
    public void Apply(int points, bool unlocked)
    {
        Points = Math.Max(0, points);
        Unlocked = unlocked || Points > 0;
        Version = checked(Version + 1);
    }

    /// <summary>恢复快照检查点时同步版本号（仅持久化恢复边界使用）。</summary>
    internal void RestoreVersion(long version) => Version = version;
}

/// <summary>
/// 筑基期「根基」纯领域规则：上限曲线、对称守恒修为消耗、六维向上取整加成与筑基破境门槛。
/// 全部为确定性纯函数，供领域引擎、服务端命令与前端展示同源对齐（总纲 §3.2.4）。
/// </summary>
public static class GenjiRules
{
    /// <summary>开启根基的境界等级：Lv.30 半步筑基（练气第 12 层，T3 起点）。</summary>
    public const int GenjiRealmLevel = 30;

    /// <summary>半步筑基（Lv.30）初始可达根基上限（30 点）。</summary>
    public const int InitialCapAtHalfStepFoundation = 30;

    /// <summary>普通小境界突破追加的根基上限（+4 点）。</summary>
    public const int MinorBreakCapBonus = 4;

    /// <summary>中境界阶段突破（跨入第 4/7/10 层）追加的根基上限（+8 点，覆盖普通小境界档）。</summary>
    public const int PhaseBreakCapBonus = 8;

    /// <summary>大境界突破（跨入筑基一层 Lv.31、金丹一层 Lv.43……飞升 Lv.127）追加的根基上限（+20 点，覆盖小/中境界档）。</summary>
    public const int MajorBreakCapBonus = 20;

    /// <summary>从半步筑基突破至筑基一层（Lv.30 → Lv.31）要求任意一项有效六维（含根基加成）达到的点数。</summary>
    public const int FoundationRealmRequiredAttributeLevel = 99;

    /// <summary>标准单点根基消耗占当前境界升级所需修为（含灵根乘区）的基准比例（50%）。</summary>
    public const double BaseExpCostRatio = 0.50;

    /// <summary>0 点起步时相对于标准基准成本的最低折扣倍率（20%，即当前境界升级修为的 10%）。</summary>
    public const double MinRelativeCostMultiplier = 0.20;

    /// <summary>是否已到达可接触根基系统的境界（Lv.30 及以上）。</summary>
    public static bool IsGenjiRealm(int realmLevel) => realmLevel >= GenjiRealmLevel;

    /// <summary>
    /// 判定角色当前是否已解锁根基常驻凝练：
    /// 1. 已处于 Lv.31（筑基一层）及以上天然解锁；
    /// 2. 处于 Lv.30（半步筑基）时，已落盘解锁标记、已有根基点数或当前修为已触及本层瓶颈即解锁。
    /// </summary>
    public static bool IsUnlocked(int realmLevel, bool unlockedFlag, int currentPoints, double currentExp, double expCapWithLinggen)
    {
        if (realmLevel < GenjiRealmLevel) return false;
        if (realmLevel > GenjiRealmLevel || unlockedFlag || currentPoints > 0) return true;
        return expCapWithLinggen > 0 && currentExp >= expCapWithLinggen - 1e-9;
    }

    /// <summary>
    /// 计算指定境界等级累计可达的根基点数总上限（保留在上限池，档位覆盖不叠加，总纲 §3.2.4）：
    /// Lv.30 起步 30 点；Lv.31 起大境界 +20、中境界阶段（4/7/10 层）+8、其余小境界 +4。
    /// </summary>
    public static int CalculateCap(int realmLevel)
    {
        if (realmLevel < GenjiRealmLevel) return 0;
        var cap = InitialCapAtHalfStepFoundation;
        for (var lv = GenjiRealmLevel + 1; lv <= realmLevel; lv++)
        {
            if (lv <= 127)
            {
                var cultLevel = lv - 18;
                var layerInMajor = (cultLevel - 1) % 12 + 1;
                if (lv == 127 || layerInMajor == 1)
                    cap += MajorBreakCapBonus;
                else if (layerInMajor is 4 or 7 or 10)
                    cap += PhaseBreakCapBonus;
                else
                    cap += MinorBreakCapBonus;
            }
            else
            {
                cap += MinorBreakCapBonus;
            }
        }
        return cap;
    }

    /// <summary>
    /// 计算单项六维在根基百分比增幅下的额外加成点数（每点根基 +1%，向上取整，纯整数无浮点尾差）：
    /// 例如原始 90 点 + 10 点根基 → +9（合计 99）；原始 96 点 + 33 点根基 → +32（合计 128）。
    /// </summary>
    /// <param name="basePoints">六维经验导出的原始整数点数。</param>
    /// <param name="genjiPoints">当前已凝练根基点数。</param>
    public static int ResolveBonusPoints(int basePoints, int genjiPoints)
    {
        if (basePoints <= 0 || genjiPoints <= 0) return 0;
        return (int)(((long)basePoints * genjiPoints + 99L) / 100L);
    }

    /// <summary>浮点六维等级重载：先向下取整为有效基础点数再计算向上取整的根基加成。</summary>
    public static int ResolveBonusPoints(double baseLevel, int genjiPoints)
        => ResolveBonusPoints((int)Math.Floor(Math.Max(0, baseLevel)), genjiPoints);

    /// <summary>
    /// 计算根基凝练使用的有效修为上限基数（含灵根破镜乘区）：
    /// 常规境界直接复用 <see cref="HeavenGateRules.ResolveExpCapWithLinggen"/>；
    /// 若处于配表满级封顶层（如 Lv.127 对应 expToNext = 0），回退采用末档非零境界修为基数。
    /// </summary>
    public static double ResolveEffectiveExpCap(int realmLevel, LinggenValues confirmedValues, bool entered,
        RealmProgressionCatalog? realm)
    {
        if (realm is null || realmLevel < 1) return 0;
        var cap = HeavenGateRules.ResolveExpCapWithLinggen(realmLevel, confirmedValues, entered, realm);
        if (cap > 0) return cap;
        if (realm.MaxLevel > 1 && realmLevel >= realm.MaxLevel)
            return HeavenGateRules.ResolveExpCapWithLinggen(realm.MaxLevel - 1, confirmedValues, entered, realm);
        return 0;
    }

    /// <summary>
    /// 计算在当前上限 <paramref name="maxCap"/> 下，从 <paramref name="currentPoints"/> 凝练至下一点时
    /// 相对于标准基准成本（50% 本层升级修为）的倍率（总纲 §3.2.4）：
    /// - 达到上限 50% 前线性递增：0 点起步为 0.20（即 10% 升级修为）；
    /// - 恰达上限 50%（currentPoints == maxCap / 2）时严格等于 1.00（即 50% 升级修为）；
    /// - 50% 之后线性递增，将前 50% 节省的修为精确守恒转移至后 50% 区间（总倍率之和严格等于 maxCap）。
    /// </summary>
    public static double ResolveRelativeCostMultiplier(int currentPoints, int maxCap)
    {
        if (maxCap <= 0 || currentPoints < 0 || currentPoints >= maxCap) return 0;
        var half = maxCap / 2;
        if (half <= 0) return 1.0;
        if (currentPoints < half)
        {
            return MinRelativeCostMultiplier + (1.0 - MinRelativeCostMultiplier) * currentPoints / half;
        }

        var offset = currentPoints - half;
        if (offset == 0) return 1.0;
        if (half == 1) return 2.0 - MinRelativeCostMultiplier;
        // 前半段节省总额 S = (1 - Min)/2 * (half + 1)，分摊至后半段 j = 0..half-1 满足 ∑ slope * j = S
        var slope = (1.0 - MinRelativeCostMultiplier) * (half + 1) / ((double)half * (half - 1));
        return 1.0 + slope * offset;
    }

    /// <summary>
    /// 计算凝练下一级根基所需消耗的当前层修为（四舍五入取整，至少 1 点；已达上限或基数非正返回 0）。
    /// </summary>
    /// <param name="expCapWithLinggen">含灵根乘区的当前境界有效升级修为上限。</param>
    /// <param name="currentPoints">当前已凝练根基点数。</param>
    /// <param name="maxCap">当前境界可达根基上限。</param>
    public static double ResolveCondenseExpCost(double expCapWithLinggen, int currentPoints, int maxCap)
    {
        if (!double.IsFinite(expCapWithLinggen) || expCapWithLinggen <= 0
            || maxCap <= 0 || currentPoints < 0 || currentPoints >= maxCap)
            return 0;
        var baseCost = expCapWithLinggen * BaseExpCostRatio;
        var multiplier = ResolveRelativeCostMultiplier(currentPoints, maxCap);
        if (multiplier <= 0) return 0;
        return Math.Max(1d, Math.Round(baseCost * multiplier, MidpointRounding.AwayFromZero));
    }
}
