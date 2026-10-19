namespace Domain.Combat;

/// <summary>
/// 特质定义（总纲 §7.12）：表示特质的品阶、点数消耗、属性百分比加成与点数通道加成。
/// </summary>
public sealed record TraitDefinition(
    string Id,
    string Name,
    int Quality,
    int Cost,
    IReadOnlyDictionary<string, double>? StatRatios = null,
    IReadOnlyDictionary<string, double>? Points = null,
    string Description = "");

/// <summary>
/// 玩家特质养成状态记录（总纲 §7.12.4）：记录特质当前等级与已累积经验。
/// </summary>
public sealed record TraitProgressState(string Id, int Level, int Exp);

/// <summary>
/// 特质规则单点表（总纲 §7.12）：特质 ID → 面板点数折算与开战 Buff 授予的集中映射。
/// 配表化（traits 词表）属远期扩展；本期映射硬编码于此，玩家与怪物面板同构消费。
/// </summary>
public static class CombatTraitRules
{
    /// <summary>看不到我：开战获得隐身 buff，威胁权重归零持续 10 秒。</summary>
    public const string UnseenTraitId = "trait.bukandaowo";
    /// <summary>力微饭否：受到伤害时按攻击方面板原始伤害累积自身威胁。</summary>
    public const string ThreatOnTakenTraitId = "trait.liweifanfou";
    /// <summary>隐身 buff：看不到我特质的开战授予效果（威胁增幅点数 -100）。</summary>
    public const string UnseenBuffId = "buff.yinshen";
    /// <summary>隐身 buff 持续时长（战斗时钟秒）。</summary>
    public const double UnseenBuffDurationSeconds = 10;
    /// <summary>力微饭否折算点数：原始伤害 1:1 转为自身威胁。</summary>
    public const double ThreatOnTakenTraitPoints = 100;

    /// <summary>
    /// 全特质静态注册表（Q1 凡品、Q2 黄品、Q3 玄品、Q4 地品、Q5 天品及机制特质）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TraitDefinition> AllTraits = RegisterAllTraits();
    /// <summary>全特质 ID 列表，供觉醒石抽取等概率选定。</summary>
    public static readonly IReadOnlyList<string> AllTraitIds = AllTraits.Keys.ToArray();

    /// <summary>特质升至下一级所需经验点数：Lv N -> N+1 需 N 点经验。</summary>
    public static int ExpForNextLevel(int level) => Math.Max(1, level);

    /// <summary>
    /// 为特质增加经验并自动判定升级（等差经验阶梯，默认上限 20 级）。
    /// </summary>
    public static (int Level, int Exp, bool LeveledUp) AddExp(int currentLevel, int currentExp, int addExp = 1, int maxLevel = 20)
    {
        var level = Math.Max(1, currentLevel);
        if (level >= maxLevel) return (maxLevel, 0, false);
        var exp = Math.Max(0, currentExp) + addExp;
        var leveledUp = false;
        while (level < maxLevel && exp >= ExpForNextLevel(level))
        {
            exp -= ExpForNextLevel(level);
            level++;
            leveledUp = true;
        }
        if (level >= maxLevel) exp = 0;
        return (level, exp, leveledUp);
    }

    /// <summary>从全量特质池中等概率随机抽取一项特质 ID。</summary>
    public static string PickRandomTrait(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return AllTraitIds[random.Next(AllTraitIds.Count)];
    }

    /// <summary>
    /// 计算指定境界等级的角色特质点数容量上限（总纲 §7.12.1）：
    /// 初始 5 点 + 每小境界 1 点 + 每境界阶段额外 1 点 + 每大境界额外 1 点。
    /// </summary>
    public static int CalculateCapacity(int realmLevel)
    {
        if (realmLevel < 1) return 0;
        var pts = 5; // 初始底座 5 点
        pts += realmLevel - 1; // 每升一个小境界 +1 点

        // 凡俗期阶段晋级：武(1~6), 玄(7~12), 天(13~18)
        if (realmLevel >= 7) pts++;
        if (realmLevel >= 13) pts++;

        // 修真期阶段晋级：Lv 19 起，每 12 级一个大境界（初/中/后/极）
        if (realmLevel >= 19)
        {
            var cultLevel = realmLevel - 18;
            var majorIndex = (cultLevel - 1) / 12;
            var layerInMajor = (cultLevel - 1) % 12 + 1;

            // 过去已完整渡过的大境界阶段（每个大境界 4 阶段）
            pts += majorIndex * 4;

            // 当前大境界阶段
            if (layerInMajor >= 1) pts++;
            if (layerInMajor >= 4) pts++;
            if (layerInMajor >= 7) pts++;
            if (layerInMajor >= 10) pts++;

            // 飞升（Lv 127）额外阶段
            if (realmLevel >= 127) pts++;
        }

        // 大境界突破奖励（凡俗→练气、练气→筑基……）
        if (realmLevel >= 19)
        {
            var majorBreaks = (realmLevel - 19) / 12 + 1;
            pts += majorBreaks;
            if (realmLevel >= 127) pts++;
        }

        return pts;
    }

    /// <summary>
    /// 计算指定特质 ID 集合的总特质点数消耗。
    /// </summary>
    public static int TotalCost(IEnumerable<string>? traitIds)
    {
        if (traitIds is null) return 0;
        var total = 0;
        foreach (var id in traitIds)
        {
            if (AllTraits.TryGetValue(id, out var def))
                total += def.Cost;
        }
        return total;
    }

    /// <summary>
    /// 汇总结算特质属性百分比加成与点数通道加成（基础无等级口径，等效全特质 1 级）。
    /// </summary>
    public static void AccumulateModifiers(IReadOnlyList<string>? traitIds, Dictionary<string, double> statRatios, Dictionary<string, double> points)
        => AccumulateModifiers(traitIds, null, statRatios, points);

    /// <summary>
    /// 汇总结算特质属性百分比加成与点数通道加成（支持特质等级每级 5% 线性成长，双刃剑惩罚锁定不变）。
    /// </summary>
    public static void AccumulateModifiers(IReadOnlyList<string>? traitIds, IReadOnlyDictionary<string, int>? traitLevels, Dictionary<string, double> statRatios, Dictionary<string, double> points)
    {
        if (traitIds is null || traitIds.Count == 0) return;
        for (var i = 0; i < traitIds.Count; i++)
        {
            var id = traitIds[i];
            if (!AllTraits.TryGetValue(id, out var def)) continue;

            var level = traitLevels is not null && traitLevels.TryGetValue(id, out var lv) ? Math.Max(1, lv) : 1;
            var scale = 1.0 + 0.05 * (level - 1);

            if (def.StatRatios is not null)
            {
                foreach (var (k, v) in def.StatRatios)
                {
                    // 正向属性每级成长 5%，负向副作用惩罚锁定不变
                    var finalV = v > 0 ? v * scale : v;
                    statRatios[k] = statRatios.GetValueOrDefault(k) + finalV;
                }
            }

            if (def.Points is not null)
            {
                foreach (var (k, v) in def.Points)
                {
                    // 力微饭否受击仇恨或正向点数提升 5%/级，负面惩罚锁定不变
                    var finalV = string.Equals(k, "threatOnTakenPoints", StringComparison.Ordinal) || v > 0
                        ? v * scale
                        : v;
                    points[k] = points.GetValueOrDefault(k) + finalV;
                }
            }
        }
    }

    /// <summary>特质 → 受击涨威胁点数折算：由属性解析汇入 <see cref="CombatEffectProfile"/> 通道。</summary>
    public static double ThreatOnTakenPoints(IReadOnlyList<string>? traits, IReadOnlyDictionary<string, int>? traitLevels = null)
    {
        if (!ContainsTrait(traits, ThreatOnTakenTraitId)) return 0;
        var level = traitLevels is not null && traitLevels.TryGetValue(ThreatOnTakenTraitId, out var lv) ? Math.Max(1, lv) : 1;
        return ThreatOnTakenTraitPoints * (1.0 + 0.05 * (level - 1));
    }

    /// <summary>特质 → 开战 Buff 授予：遭遇创建时对每个单位按其面板 Traits 生效（隐身时长支持等级成长）。</summary>
    public static void ApplyBattleStartTraits(CombatantState unit, IReadOnlyDictionary<string, int>? traitLevels = null)
    {
        if (ContainsTrait(unit.Stats.Traits, UnseenTraitId))
        {
            var level = traitLevels is not null && traitLevels.TryGetValue(UnseenTraitId, out var lv) ? Math.Max(1, lv) : 1;
            var duration = UnseenBuffDurationSeconds * (1.0 + 0.05 * (level - 1));
            unit.Buffs.Add(new CombatBuffState(UnseenBuffId, duration, ThreatPoints: -100, Name: "隐身",
                ResetOnWave: true));
        }
    }


    public static bool ContainsTrait(IReadOnlyList<string>? traits, string traitId)
    {
        if (traits is null) return false;
        for (var i = 0; i < traits.Count; i++)
            if (string.Equals(traits[i], traitId, StringComparison.Ordinal)) return true;
        return false;
    }

    private static Dictionary<string, TraitDefinition> RegisterAllTraits()
    {
        var dict = new Dictionary<string, TraitDefinition>(StringComparer.Ordinal);
        void Add(TraitDefinition trait) => dict[trait.Id] = trait;

        // ==================== Q1 凡品 (白) · 消耗 2 点 ====================
        Add(new("trait.tianshengmanli", "天生蛮力", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.10 },
            Description: "天生骨骼强壮，物理攻击提升 10%。"));
        Add(new("trait.qiganchongying", "气感充盈", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.10 },
            Description: "对天地灵机感应敏锐，术法攻击提升 10%。"));
        Add(new("trait.jinqiangguijan", "筋强骨健", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.12 },
            Description: "气血底子扎实，最大生命提升 12%。"));
        Add(new("trait.jingluoweitong", "经络微通", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxManapoints"] = 0.15 },
            Description: "体内部分经络先天微张，最大灵力提升 15%。"));
        Add(new("trait.tiepihoujian", "铁皮厚茧", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 0.12 },
            Description: "皮糙肉厚耐击打，物理护甲提升 12%。"));
        Add(new("trait.bihankangre", "避寒抗热", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellResistance"] = 0.12 },
            Description: "耐受外邪侵蚀，法术抗性提升 12%。"));
        Add(new("trait.qinglingxiaobu", "轻灵小步", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.08, ["accuracy"] = 0.08 },
            Description: "步法轻巧灵动，闪避与命中各提升 8%。"));

        // ==================== Q2 黄品 (绿) · 消耗 5 点 ====================
        Add(new("trait.tianshengjiangu", "天生剑骨", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.20, ["criticalChance"] = 0.10 },
            Description: "骨若青锋，物理攻击提升 20%、暴击提升 10%。"));
        Add(new("trait.xuanyinlingqiao", "玄阴灵窍", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.20, ["maxManapoints"] = 0.15 },
            Description: "灵窍内蕴玄阴之气，术法攻击提升 20%、最大灵力提升 15%。"));
        Add(new("trait.tongjiaotiezhu", "铜浇铁铸", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.20, ["physicalArmor"] = 0.20 },
            Description: "身躯如铜铸铁打，最大生命与物理护甲各提升 20%。"));
        Add(new("trait.daoxinanhe", "道心安和", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellResistance"] = 0.20, ["criticalAvoidance"] = 0.15 },
            Description: "心静神凝不为外物所动，法术抗性提升 20%、免爆提升 15%。"));
        Add(new("trait.fengxingbu", "风行步", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.18, ["parry"] = 0.15 },
            Description: "身如游风捉摸不定，闪避提升 18%、化解提升 15%。"));
        Add(new("trait.ningshenfayan", "凝神法眼", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["accuracy"] = 0.18, ["break"] = 0.15 },
            Description: "明察秋毫洞悉敌破绽，命中提升 18%、破招提升 15%。"));
        Add(new("trait.tunamianchang", "吐纳绵长", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 0.30, ["manapointRegen"] = 0.30 },
            Description: "呼吸吐纳绵绵不绝，生命与灵力自动回复各提升 30%。"));

        // ==================== Q3 玄品 (蓝) · 消耗 10 点 ====================
        Add(new("trait.chunyangzhenmai", "纯阳真脉", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.35, ["break"] = 0.20 },
            Description: "体内纯阳之气奔涌，物理攻击提升 35%、破招提升 20%。"));
        Add(new("trait.taiyinfahun", "太阴法魂", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.35, ["criticalChance"] = 0.15 },
            Description: "神魂附着太阴灵辉，术法攻击提升 35%、暴击提升 15%。"));
        Add(new("trait.panshijingang", "磐石金刚", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.30, ["physicalArmor"] = 0.30, ["spellResistance"] = 0.25 },
            Description: "如金刚巨岩矗立，最大生命提升 30%、物理护甲提升 30%、法术抗性提升 25%。"));
        Add(new("trait.taxuewuying", "踏雪无影", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.30, ["parry"] = 0.20, ["accuracy"] = 0.15 },
            Description: "踏雪无痕来去无影，闪避提升 30%、化解提升 20%、命中提升 15%。"));
        Add(new("trait.shengjiyongdong", "生机涌动", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.25, ["hitpointRegen"] = 0.50, ["manapointRegen"] = 0.50 },
            Description: "体内生机旺盛如泉涌，最大生命提升 25%、生命与灵力自动回复各提升 50%。"));

        // ==================== Q4 地品 (紫) · 消耗 15 点 ====================
        Add(new("trait.houtukunyushen", "厚土坤舆身", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 0.45, ["spellResistance"] = 0.45, ["maxHitpoints"] = 0.35 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -15 },
            Description: "身托厚土坤舆万钧，物理护甲与法术抗性各提升 45%、最大生命提升 35%，造成的全伤害降低 15%。"));
        Add(new("trait.lieyangkuangzhangu", "烈阳狂战骨", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.45, ["criticalChance"] = 0.20, ["break"] = 0.20, ["maxHitpoints"] = -0.15 },
            Description: "烈火焚骨战意狂飙，物理攻击提升 45%、暴击提升 20%、破招提升 20%，最大生命降低 15%。"));
        Add(new("trait.minghaishihunti", "冥海噬魂体", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.45, ["maxManapoints"] = 0.35, ["criticalChance"] = 0.15, ["physicalArmor"] = -0.20 },
            Description: "如冥海纳百川，术法攻击提升 45%、最大灵力提升 35%、暴击提升 15%，物理护甲降低 20%。"));
        Add(new("trait.kurongchangqingjue", "枯荣长青诀", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 0.60, ["manapointRegen"] = 0.60, ["maxHitpoints"] = 0.25 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -20 },
            Description: "枯荣流转生生不息，生命与灵力自动回复各提升 60%、最大生命提升 25%，造成的全伤害降低 20%。"));

        // ==================== Q5 天品 (金) · 消耗 20 点 ====================
        Add(new("trait.yaodutongyuan", "药毒同源", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 1.00, ["manapointRegen"] = 1.00, ["maxHitpoints"] = 0.40 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -35 },
            Description: "通晓药毒同流自愈圣道，生命与灵力自动回复各提升 100%、最大生命提升 40%，造成的全伤害降低 35%。"));
        Add(new("trait.wanjianguiyi", "万剑归一", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.65, ["criticalChance"] = 0.30, ["break"] = 0.25, ["maxHitpoints"] = -0.30 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalDamageReductionPoints"] = -25, ["spellDamageReductionPoints"] = -25 },
            Description: "身化万剑只攻不守，物理攻击提升 65%、暴击提升 30%、破招提升 25%，最大生命降低 30%，受到物理与术法伤害增加 25%。"));
        Add(new("trait.jiuyoushiling", "九幽噬灵", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.65, ["maxManapoints"] = 0.50, ["criticalChance"] = 0.20, ["physicalArmor"] = -0.35, ["evasion"] = -0.30 },
            Description: "吞噬九幽灵气引动天怒，术法攻击提升 65%、最大灵力提升 50%、暴击提升 20%，物理护甲降低 35%、闪避降低 30%。"));
        Add(new("trait.budongmingwang", "不动明王", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 0.80, ["spellResistance"] = 0.80, ["maxHitpoints"] = 0.50 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -45 },
            Description: "化身不动明王镇压八方，物理护甲与法术抗性各提升 80%、最大生命提升 50%，造成的全伤害降低 45%。"));
        Add(new("trait.tianshekuanggu", "天煞狂骨", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.50, ["spellAttack"] = 0.50, ["criticalChance"] = 0.40, ["hitpointRegen"] = -0.60, ["criticalAvoidance"] = -0.35 },
            Description: "天煞孤星命格入骨，物理与术法攻击各提升 50%、暴击提升 40%，生命自动回复降低 60%、免爆降低 35%。"));

        // ==================== 机制特质 · 消耗 5 点 ====================
        Add(new(UnseenTraitId, "看不到我", 2, 5,
            Description: "开战获得隐身 buff，威胁权重归零持续 10 秒。"));
        Add(new(ThreatOnTakenTraitId, "力微饭否", 2, 5,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["threatOnTakenPoints"] = ThreatOnTakenTraitPoints },
            Description: "受到伤害时按攻击方面板原始伤害累积自身威胁。"));

        return dict;
    }
}
