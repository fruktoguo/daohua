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
    /// <summary>看不到我：身遁虚无匿形迹。开战后获得隐身状态，气机消弭避开敌方视线，持续 10 秒。</summary>
    public const string UnseenTraitId = "trait.bukandaowo";
    /// <summary>力微饭否：不动如山撼未动。受到伤害时以自身坚韧招引敌意，将所受之威等额化为敌方瞩目。</summary>
    public const string ThreatOnTakenTraitId = "trait.liweifanfou";
    /// <summary>初见特质标识。</summary>
    public const string FirstSightTraitId = "trait.chujian";
    /// <summary>欲速则不达特质标识。</summary>
    public const string HasteTraitId = "trait.yusuzebuda";
    /// <summary>我要打十个特质标识。</summary>
    public const string OutnumberedTraitId = "trait.woyaodashige";
    /// <summary>单挑王特质标识。</summary>
    public const string DuelKingTraitId = "trait.dantiaowang";
    /// <summary>蓄意特质与状态标识。</summary>
    public const string XuyiTraitId = "trait.xuyi", XuyiBuffId = "buff.xuyi";
    /// <summary>燃魂特质与状态标识。</summary>
    public const string RanhunTraitId = "trait.ranhun", RanhunBuffId = "buff.ranhun";
    /// <summary>移形统一位移状态与剑与心特质标识。</summary>
    public const string YixingBuffId = "buff.yixing", JianyuxinTraitId = "trait.jianyuxin";
    /// <summary>火神特质标识。</summary>
    public const string HuoshenTraitId = "trait.huoshen";
    /// <summary>阵法大师特质标识。</summary>
    public const string ZhenfadashiTraitId = "trait.zhenfadashi";
    /// <summary>符法·疾特质标识。</summary>
    public const string FufajiTraitId = "trait.fufaji";
    /// <summary>符法·破阵特质标识。</summary>
    public const string FufapozhenTraitId = "trait.fufapozhen";
    /// <summary>符法·天罡特质标识。</summary>
    public const string FufatiangangTraitId = "trait.fufatiangang";
    /// <summary>符法·天命特质标识。</summary>
    public const string FufatianmingTraitId = "trait.fufatianming";
    /// <summary>养剑术特质与剑意状态标识。</summary>
    public const string YangjianshuTraitId = "trait.yangjianshu", JianyiBuffId = "buff.jianyi";
    /// <summary>藏锋特质标识。</summary>
    public const string CangfengTraitId = "trait.cangfeng";
    /// <summary>血气化盾特质标识。</summary>
    public const string XueqihuadunTraitId = "trait.xueqihuadun";
    /// <summary>闪身步特质标识。</summary>
    public const string ShanshenbuTraitId = "trait.shanshenbu";
    /// <summary>天罡断岳引导受击状态与神行符状态标识。</summary>
    public const string TiangangBuffId = "buff.tiangang", ShenxingBuffId = "buff.shenxing";

    /// <summary>解析携带「火神」特质后的实际伤害五行属性（仅覆写/附加元素，不改变伤害大类）。</summary>
    public static CombatElement ResolveDamageElement(IReadOnlyList<string>? traits, CombatElement baseElement)
        => ContainsTrait(traits, HuoshenTraitId) ? CombatElement.Fire : baseElement;

    /// <summary>解析符箓冷却加速点数（符法·疾：基础 400 点即冷却减少 80%，每级 +5% 线性成长）。</summary>
    public static double TalismanCooldownSpeedPoints(IReadOnlyList<string>? traits, IReadOnlyDictionary<string, int>? traitLevels = null)
    {
        if (!ContainsTrait(traits, FufajiTraitId)) return 0;
        var lv = traitLevels is not null && traitLevels.TryGetValue(FufajiTraitId, out var v) ? Math.Max(1, v) : 1;
        return 400.0 * (1.0 + 0.05 * (lv - 1));
    }

    /// <summary>解析符箓灵力消耗倍率（符法·疾：消耗提升 300% 即变为 400%，负面代价锁定不变）。</summary>
    public static double TalismanManaCostMultiplier(IReadOnlyList<string>? traits)
        => ContainsTrait(traits, FufajiTraitId) ? 4.0 : 1.0;

    /// <summary>判断符箓是否可触发暴击（符法·破阵）。</summary>
    public static bool CanTalismanCrit(IReadOnlyList<string>? traits)
        => ContainsTrait(traits, FufapozhenTraitId);

    /// <summary>判断主动术法槽位是否允许装配符箓（符法·天命）。</summary>
    public static bool CanEquipTalismanInAbilitySlot(IReadOnlyList<string>? traits)
        => ContainsTrait(traits, FufatianmingTraitId);

    /// <summary>解析「闪身步」内置触发冷却（基础 3.0s，随等级按反比例曲线缩短，20 级满级加速 100% 缩至 1.5s）。</summary>
    public static double ShanshenbuCooldownSeconds(IReadOnlyList<string>? traits, IReadOnlyDictionary<string, int>? traitLevels = null)
    {
        if (!ContainsTrait(traits, ShanshenbuTraitId)) return double.PositiveInfinity;
        var lv = traitLevels is not null && traitLevels.TryGetValue(ShanshenbuTraitId, out var v) ? Math.Clamp(v, 1, 20) : 1;
        return 3.0 / (1.0 + (lv - 1) / 19.0);
    }

    /// <summary>解析「血气化盾」超出 20% 最大生命部分转化为护盾的比例（Lv1 为 100%，每级 +5%）。</summary>
    public static double XueqihuadunConversionRatio(IReadOnlyList<string>? traits, IReadOnlyDictionary<string, int>? traitLevels = null)
    {
        if (!ContainsTrait(traits, XueqihuadunTraitId)) return 0;
        var lv = traitLevels is not null && traitLevels.TryGetValue(XueqihuadunTraitId, out var v) ? Math.Max(1, v) : 1;
        return 1.0 + 0.05 * (lv - 1);
    }

    /// <summary>同源不固定时长 Buff 的到期时间「保高不降」合并：保留原剩余时长与新赋予时长中的较大值。</summary>
    public static double MergeBuffExpiry(double? existingExpiresAt, double proposedExpiresAt)
        => existingExpiresAt is { } cur && cur < BattleLifetimeSeconds && proposedExpiresAt < BattleLifetimeSeconds
            ? Math.Max(cur, proposedExpiresAt)
            : proposedExpiresAt;
    /// <summary>常驻战内状态使用有限最大到期值，每波清理。</summary>
    public const double BattleLifetimeSeconds = double.MaxValue;
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
        traitLevels ??= unit.Stats.TraitLevels;
        if (ContainsTrait(unit.Stats.Traits, FirstSightTraitId))
            unit.AddBuff(new CombatBuffState("buff.chujian", 2, Name: "初见", ResetOnWave: true,
                Effects: [new(BuffEffectChannel.EvasionPostRatio, 3 * LevelScale(unit, FirstSightTraitId))]));
        if (ContainsTrait(unit.Stats.Traits, HasteTraitId))
            unit.AddBuff(new CombatBuffState("buff.yusuzebuda", BattleLifetimeSeconds, Name: "欲速则不达", ResetOnWave: true,
                Effects: [new(BuffEffectChannel.ActionSpeedPoints, 100 * LevelScale(unit, HasteTraitId)),
                    new(BuffEffectChannel.DamageBonusPoints, -100), new(BuffEffectChannel.HealingBonusPoints, -100)]));
        if (ContainsTrait(unit.Stats.Traits, UnseenTraitId))
        {
            var level = traitLevels is not null && traitLevels.TryGetValue(UnseenTraitId, out var lv) ? Math.Max(1, lv) : 1;
            var duration = UnseenBuffDurationSeconds * (1.0 + 0.05 * (level - 1));
            var existing = unit.Buffs.FirstOrDefault(b => b.BuffId == UnseenBuffId);
            unit.SetBuff(new CombatBuffState(UnseenBuffId, MergeBuffExpiry(existing?.ExpiresAtSeconds, duration),
                Name: "隐身", ResetOnWave: true,
                Effects: [new BuffEffect(BuffEffectChannel.ThreatPoints, -100)]));
        }
    }


    /// <summary>特质正向数值的统一等级成长。</summary>
    public static double LevelScale(CombatantState unit, string traitId)
        => 1 + 0.05 * (Math.Max(1, unit.Stats.TraitLevels?.GetValueOrDefault(traitId) ?? 1) - 1);

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

        // 表内物攻与术攻为品阶配平后减半的实际值，其余属性保留配平值。
        // ==================== Q1 凡品 (白) · 消耗 2 点 ====================
        Add(new("trait.tianshengmanli", "天生蛮力", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.05 },
            Description: "骨壮筋强撼岳峦。物理攻击提升 5%。"));
        Add(new("trait.qiganchongying", "气感充盈", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.05 },
            Description: "灵机微动感清微。术法攻击提升 5%。"));
        Add(new("trait.jinqiangguijan", "筋强骨健", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.12 },
            Description: "气血充盈根基厚。最大生命提升 12%。"));
        Add(new("trait.jingluoweitong", "经络微通", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxManapoints"] = 0.15 },
            Description: "百脉舒张蕴流泉。最大灵力提升 15%。"));
        Add(new("trait.tiepihoujian", "铁皮厚茧", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 0.12 },
            Description: "肌若坚韧抗锋芒。物理护甲提升 12%。"));
        Add(new("trait.bihankangre", "避寒抗热", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellResistance"] = 0.12 },
            Description: "万毒难侵避水火。法术抗性提升 12%。"));
        Add(new("trait.qinglingxiaobu", "轻灵小步", 1, 2,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.08, ["accuracy"] = 0.08 },
            Description: "足踏轻风移寸影。闪避与命中各提升 8%。"));

        // ==================== Q2 黄品 (绿) · 消耗 5 点 ====================
        Add(new("trait.tianshengjiangu", "天生剑骨", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.125, ["criticalChance"] = 0.15 },
            Description: "骨若青锋藏绝壁。物理攻击提升 12.5%、暴击提升 15%。"));
        Add(new("trait.xuanyinlingqiao", "玄阴灵窍", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.125, ["maxManapoints"] = 0.20 },
            Description: "窍引玄阴涵浩渺。术法攻击提升 12.5%、最大灵力提升 20%。"));
        Add(new("trait.tongjiaotiezhu", "铜浇铁铸", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.22, ["physicalArmor"] = 0.22 },
            Description: "身铸精铜百劫牢。最大生命与物理护甲各提升 22%。"));
        Add(new("trait.daoxinanhe", "道心安和", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellResistance"] = 0.22, ["criticalAvoidance"] = 0.20 },
            Description: "心沉似水不惊澜。法术抗性提升 22%、免爆提升 20%。"));
        Add(new("trait.fengxingbu", "风行步", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.25, ["parry"] = 0.20 },
            Description: "身化游风捕不捉。闪避提升 25%、拆招提升 20%。"));
        Add(new("trait.ningshenfayan", "凝神法眼", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["accuracy"] = 0.25, ["break"] = 0.20 },
            Description: "慧眼如炬破微芒。命中提升 25%、破招提升 20%。"));
        Add(new("trait.tunamianchang", "吐纳绵长", 2, 5,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 0.30, ["manapointRegen"] = 0.30 },
            Description: "呼吸绵绵归造化。生命与灵力自动回复各提升 30%。"));

        // ==================== Q3 玄品 (蓝) · 消耗 10 点 ====================
        Add(new("trait.chunyangzhenmai", "纯阳真脉", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.275, ["break"] = 0.30 },
            Description: "纯阳炽烈焚百邪。物理攻击提升 27.5%、破招提升 30%。"));
        Add(new("trait.taiyinfahun", "太阴法魂", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.275, ["criticalChance"] = 0.30 },
            Description: "寒月凝魂照太虚。术法攻击提升 27.5%、暴击提升 30%。"));
        Add(new("trait.panshijingang", "磐石金刚", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.40, ["physicalArmor"] = 0.40, ["spellResistance"] = 0.35 },
            Description: "擎天巨岳立玄黄。最大生命提升 40%、物理护甲提升 40%、法抗提升 35%。"));
        Add(new("trait.taxuewuying", "踏雪无影", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["evasion"] = 0.45, ["parry"] = 0.35, ["accuracy"] = 0.25 },
            Description: "踏雪无痕踏月行。闪避提升 45%、拆招提升 35%、命中提升 25%。"));
        Add(new("trait.shengjiyongdong", "生机涌动", 3, 10,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["maxHitpoints"] = 0.35, ["hitpointRegen"] = 0.65, ["manapointRegen"] = 0.65 },
            Description: "春风化雨润灵根。最大生命提升 35%、生命与灵力自动回复各提升 65%。"));

        // ==================== Q4 地品 (紫) · 消耗 15 点 ====================
        Add(new("trait.houtukunyushen", "厚土坤舆身", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 0.70, ["spellResistance"] = 0.70, ["maxHitpoints"] = 0.55 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -15 },
            Description: "坤舆厚德载千钧。双抗各提升 70%、最大生命提升 55%、全伤害降低 15%。"));
        Add(new("trait.lieyangkuangzhangu", "烈阳狂战骨", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.425, ["criticalChance"] = 0.35, ["break"] = 0.35, ["maxHitpoints"] = -0.15 },
            Description: "烈阳燃尽生与死。物理攻击提升 42.5%、暴击与破招各提升 35%、最大生命降低 15%。"));
        Add(new("trait.minghaishihunti", "冥海噬魂体", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.425, ["maxManapoints"] = 0.50, ["criticalChance"] = 0.30, ["physicalArmor"] = -0.20 },
            Description: "冥海无涯纳万川。术法攻击提升 42.5%、最大灵力提升 50%、暴击提升 30%、护甲降低 20%。"));
        Add(new("trait.kurongchangqingjue", "枯荣长青诀", 4, 15,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 1.10, ["manapointRegen"] = 1.10, ["maxHitpoints"] = 0.50 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -15 },
            Description: "一枯一荣皆生机。生命与灵力自动回复各提升 110%、最大生命提升 50%、全伤害降低 15%。"));

        // ==================== Q5 天品 (金) · 消耗 20 点 ====================
        Add(new("trait.yaodutongyuan", "药毒同源", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["hitpointRegen"] = 1.60, ["manapointRegen"] = 1.60, ["maxHitpoints"] = 0.75 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -25 },
            Description: "药毒归一逆生死。生命与灵力自动回复各提升 160%、最大生命提升 75%、全伤害降低 25%。"));
        Add(new("trait.wanjianguiyi", "万剑归一", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.6, ["criticalChance"] = 0.50, ["break"] = 0.45, ["maxHitpoints"] = -0.25 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalDamageReductionPoints"] = -15, ["spellDamageReductionPoints"] = -15 },
            Description: "绝意一剑无死生。物理攻击提升 60%、暴击提升 50%、破招提升 45%、最大生命降低 25%、受到全伤害增加 15%。"));
        Add(new("trait.jiuyoushiling", "九幽噬灵", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["spellAttack"] = 0.6, ["maxManapoints"] = 0.75, ["criticalChance"] = 0.40, ["physicalArmor"] = -0.30, ["evasion"] = -0.25 },
            Description: "九幽吞噬逆天穹。术法攻击提升 60%、最大灵力提升 75%、暴击提升 40%、护甲降低 30%、闪避降低 25%。"));
        Add(new("trait.budongmingwang", "不动明王", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalArmor"] = 1.10, ["spellResistance"] = 1.10, ["maxHitpoints"] = 0.85 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["totalDamageBonusPoints"] = -30 },
            Description: "明王镇世不动尊。双抗各提升 110%、最大生命提升 85%、全伤害降低 30%。"));
        Add(new("trait.tianshekuanggu", "天煞狂骨", 5, 20,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.45, ["spellAttack"] = 0.45, ["criticalChance"] = 0.60, ["hitpointRegen"] = -0.50, ["criticalAvoidance"] = -0.25 },
            Description: "天煞孤星命入骨。双攻各提升 45%、暴击提升 60%、生命自动回复降低 50%、免爆降低 25%。"));

        // ==================== 机制特质 · 单项消耗 ====================
        Add(new(UnseenTraitId, "看不到我", 3, 8,
            Description: "身遁虚无匿形迹。开战后获得隐身状态，气机消弭避开敌方视线，持续 10 秒。"));
        Add(new(ThreatOnTakenTraitId, "力微饭否", 3, 8,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["threatOnTakenPoints"] = ThreatOnTakenTraitPoints },
            Description: "不动如山撼未动。受到伤害时以自身坚韧招引敌意，将所受之威等额化为敌方瞩目。"));
        Add(new(FirstSightTraitId, "初见", 3, 8, Description: "开战后闪避提升 300%，持续 2 秒。"));
        Add(new(HasteTraitId, "欲速则不达", 4, 10, Description: "行动速度提升 100%，伤害与治疗增幅各降低 100 点；无法击杀生命值大于 1 的目标，先降至 1，再次攻击可击杀。"));
        Add(new(OutnumberedTraitId, "我要打十个", 4, 12, Description: "敌方每多一名存活单位，增伤与免伤各提升 10 点。"));
        Add(new(DuelKingTraitId, "单挑王", 5, 16, Description: "双方各剩一名存活单位时，增伤、免伤、暴击伤害与行动速度各提升 20 点。"));
        Add(new(XuyiTraitId, "蓄意", 6, 25, Description: "受到的实际伤害积为蓄意层数；命中攻击时，若层数超过最大生命值，消耗全部层数对所有敌人造成等量威力伤害，主目标为真实伤害，其余为物理伤害。"));
        Add(new(RanhunTraitId, "燃魂", 5, 16, Description: "直接火伤按实际伤害的 2% 施加燃烧，向下取整且至少 1 层；每层每秒造成 1 点火属性术法威力伤害，持续 5 秒，叠层并刷新持续时间。"));
        Add(new(JianyuxinTraitId, "剑与心", 5, 12,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["physicalAttack"] = 0.05, ["spellAttack"] = 0.05 },
            Description: "剑随意动，心御其锋。物理与术法攻击各提升 5%；拥有「移形」时，伤害型技能的主要目标无视仇恨随机选取。"));
        Add(new(HuoshenTraitId, "火神", 5, 12,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["fireDamageBonusPoints"] = 10 },
            Description: "火焰尊主，万法归炎。自身所有伤害转为火属性（保留物理、术法与真实伤害类别），火属性增伤提升 10%。"));
        Add(new(ZhenfadashiTraitId, "阵法大师", 5, 16,
            Points: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["formationDamageBonusPoints"] = 200,
                ["skillBonusPoints"] = -500,
                ["basicAttackBonusPoints"] = -500
            },
            Description: "盘坐阵眼执乾坤。阵法造成的伤害提升 200%，技能增幅降低 500%，普通攻击伤害降低 500%。"));
        Add(new(FufajiTraitId, "符法·疾", 5, 16,
            Points: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["skillBonusPoints"] = -500,
                ["basicAttackBonusPoints"] = -500
            },
            Description: "袖藏万符疾如电。符箓冷却减少 80%（400 点加速随等级提升），但符箓消耗灵力提升 300%，技能增幅与普攻伤害各降低 500%。"));
        Add(new(FufapozhenTraitId, "符法·破阵", 4, 12,
            StatRatios: new Dictionary<string, double>(StringComparer.Ordinal) { ["criticalChance"] = 0.10 },
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["criticalDamageBonusPoints"] = 10 },
            Description: "符敕破罡洞玄机。符箓可以触发暴击，暴击提升 10%，暴击伤害提升 10%。"));
        Add(new(FufatiangangTraitId, "符法·天罡", 3, 8,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["talismanDamageBonusPoints"] = 30 },
            Description: "天罡入墨威自生。符箓造成的伤害提升 30%。"));
        Add(new(FufatianmingTraitId, "符法·天命", 5, 16,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["talismanDamageBonusPoints"] = 10 },
            Description: "以符代法顺天命。主动术法槽位可以装配符箓（全栏同名互斥、无吟唱瞬发），符箓造成的伤害提升 10%。"));
        Add(new(YangjianshuTraitId, "养剑术", 5, 16,
            Description: "十年藏锋养剑意。每秒未造成直接伤害获得 1 层剑意（持续最多 10 秒，获层刷新）；每层提升 30% 物理攻击、30% 暴击与 30% 暴击伤害，造成直接伤害后散去。"));
        Add(new(CangfengTraitId, "藏锋", 4, 12,
            Points: new Dictionary<string, double>(StringComparer.Ordinal) { ["skillBonusPoints"] = 20 },
            Description: "收剑入鞘待惊雷。战斗中不再进行普通攻击，技能增幅提升 20%。"));
        Add(new(XueqihuadunTraitId, "血气化盾", 6, 20,
            Description: "燃血凝罡战归源。开战及每 5 秒将超出 20% 最大生命的部分作为受击伤害扣除并 100% 转化为永久护盾；每波战罢剩余护盾转化为治疗（受治疗增幅加成）。"));
        Add(new(ShanshenbuTraitId, "闪身步", 5, 15,
            Description: "擦锋掠影步生莲。闪避攻击时获得 1 层移形，每 3 秒最多触发一次（等级按反比例缩短冷却，20 级缩至 1.5 秒）。"));
        return dict;
    }
}
