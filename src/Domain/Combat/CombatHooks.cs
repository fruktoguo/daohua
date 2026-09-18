using System.Reflection;

namespace Domain.Combat;

/// <summary>
/// 战斗反应效果参数包：单位随战携带的「攻击后/受击后」衍生效果强度集合。
/// 打包为单一记录挂载在 <see cref="CombatantStats.Effects"/> 上，后续新增效果
/// 只需增加字段，无需改动结算签名、快照结构与钩子接口。
/// 强度来源：装备 specialMechanics 与怪物配表，经属性解析折算写入。
/// </summary>
/// <param name="HpDrainRatio">气血汲取比例：命中后按实际伤害回血（普攻与技能同构）。</param>
/// <param name="MpDrainRatio">灵力汲取比例：命中后按实际伤害从目标灵力池偷取灵力。</param>
/// <param name="DamageReflectRatio">伤害反弹比例：受击落账后把实收伤害按比例弹回攻击方。</param>
public sealed record CombatEffectProfile(
    double HpDrainRatio = 0,
    double MpDrainRatio = 0,
    double DamageReflectRatio = 0)
{
    /// <summary>全部效果为零的空参数包。</summary>
    public static CombatEffectProfile Empty { get; } = new();

    /// <summary>校验全部效果强度为有限非负数；非法时抛出 InvalidArgument。</summary>
    internal void Validate()
    {
        if (!double.IsFinite(HpDrainRatio) || HpDrainRatio < 0
            || !double.IsFinite(MpDrainRatio) || MpDrainRatio < 0
            || !double.IsFinite(DamageReflectRatio) || DamageReflectRatio < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗反应效果参数无效。");
    }
}

/// <summary>
/// 单次攻击结算上下文：行动标识、攻击方、受击方、技能信息与结算中间量打包传递。
/// 管线各阶段就地读写本对象；钩子禁止另起随机源或读取墙钟，
/// 随机判定一律走 <see cref="NextRoll"/> 的确定性通道。
/// </summary>
public sealed class CombatHitContext
{
    /// <summary>当前遭遇战（含种子、敌方阵容与终局裁决）。</summary>
    public required CombatEncounterState Encounter { get; init; }
    /// <summary>攻击方运行时状态（气血/灵力/威胁就地可变）。</summary>
    public required CombatantState Attacker { get; init; }
    /// <summary>受击方运行时状态。</summary>
    public required CombatantState Target { get; init; }
    /// <summary>本次攻击生效的攻击方面板（技能为折算面板）；判定前钩子可整体替换。</summary>
    public required CombatantStats AttackStats { get; set; }
    /// <summary>技能来源标识；null 表示普通攻击。</summary>
    public string? AbilityId { get; init; }
    /// <summary>是否技能结算（AbilityId 非空）。</summary>
    public bool IsSkill => AbilityId is not null;
    /// <summary>攻击方阵营侧位（确定性抽样维度）。</summary>
    public required ulong Side { get; init; }
    /// <summary>出手计数（确定性抽样维度）。</summary>
    public required double Count { get; init; }
    /// <summary>结算发生的战斗时钟秒数。</summary>
    public required double AtSeconds { get; init; }

    /// <summary>命中判定结果（由核心结算填充）。</summary>
    public bool Hit { get; set; }
    /// <summary>暴击判定结果。</summary>
    public bool Critical { get; set; }
    /// <summary>破防判定结果（攻方破拆胜出）。</summary>
    public bool Broken { get; set; }
    /// <summary>招架判定结果（守方招架胜出）。</summary>
    public bool Parried { get; set; }
    /// <summary>最终结算伤害；落账前钩子可改写（护盾/上限/免死），引擎按非负夹取落账。</summary>
    public double Damage { get; set; }

    /// <summary>钩子产出的衍生事件（汲取回血、反伤跳字等）；在主事件之后按序并入。</summary>
    public List<CombatEvent>? DerivedEvents { get; set; }

    // 0~15 号通道为核心结算保留（命中/暴击/破防招架/目标抽取），钩子通道自 64 起递增。
    private ulong _rollChannel = 64;

    /// <summary>取下一个钩子专属确定性随机数；通道按序递增，结果与快照恢复无关。</summary>
    public double NextRoll() => DeterministicCombatEngine.Roll(Encounter.Seed, Side, Count, _rollChannel++);

    /// <summary>追加一条衍生战斗事件。</summary>
    public void Emit(CombatEvent combatEvent) => (DerivedEvents ??= []).Add(combatEvent);
}

/// <summary>
/// 钩子执行顺序特性：值小者先执行；同值按实现类型全名字典序兜底，
/// 保证任何进程与反射顺序下的执行序列完全一致。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CombatHookOrderAttribute(int order) : Attribute
{
    /// <summary>排序值（越小越先执行）。</summary>
    public int Order { get; } = order;
}

/// <summary>判定前钩子：命中/暴击掷骰之前执行，可整体替换 <see cref="CombatHitContext.AttackStats"/>（必中、元素转换、面板增益）。</summary>
public interface ICombatResolveMutator
{
    /// <summary>就地修改结算上下文。</summary>
    void Mutate(CombatHitContext context);
}

/// <summary>伤害已算出、未落账钩子：护盾吸收、伤害上限、免死等最终伤害修正。</summary>
public interface ICombatDamageMutator
{
    /// <summary>就地修改结算上下文（主要读写 Damage 与判定结果）。</summary>
    void Mutate(CombatHitContext context);
}

/// <summary>伤害已落账钩子：汲取、反伤、击杀特效等衍生结算；衍生伤害直接落账，不再回流管线。</summary>
public interface ICombatAftermathHook
{
    /// <summary>执行衍生结算（自行检查 Hit/Damage/IsAlive 等前置条件）。</summary>
    void Apply(CombatHitContext context);
}

/// <summary>
/// 战斗钩子管线：静态构造时反射扫描领域程序集，把全部钩子实现实例化为无状态单例，
/// 按 <see cref="CombatHookOrderAttribute"/> 排序（同值类型名字典序兜底）冻结数组，运行期零反射。
/// </summary>
public static class CombatHookPipeline
{
    private static readonly ICombatResolveMutator[] Resolvers = Scan<ICombatResolveMutator>();
    private static readonly ICombatDamageMutator[] DamageMutators = Scan<ICombatDamageMutator>();
    private static readonly ICombatAftermathHook[] Aftermaths = Scan<ICombatAftermathHook>();

    /// <summary>按序执行判定前钩子。</summary>
    internal static void MutateResolve(CombatHitContext context)
    {
        foreach (var hook in Resolvers) hook.Mutate(context);
    }

    /// <summary>按序执行伤害落账前钩子。</summary>
    internal static void MutateDamage(CombatHitContext context)
    {
        foreach (var hook in DamageMutators) hook.Mutate(context);
    }

    /// <summary>按序执行伤害落账后钩子。</summary>
    internal static void ApplyAftermath(CombatHitContext context)
    {
        foreach (var hook in Aftermaths) hook.Apply(context);
    }

    private static T[] Scan<T>()
        => typeof(CombatHookPipeline).Assembly.GetTypes()
            .Where(type => type is { IsInterface: false, IsAbstract: false } && typeof(T).IsAssignableFrom(type))
            .Select(type => (Hook: (T)Activator.CreateInstance(type)!,
                Order: type.GetCustomAttribute<CombatHookOrderAttribute>()?.Order ?? 0))
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Hook!.GetType().FullName, StringComparer.Ordinal)
            .Select(entry => entry.Hook)
            .ToArray();
}

/// <summary>气血汲取：命中后攻击方按实际伤害比例回复气血，产出治疗衍生事件。</summary>
[CombatHookOrder(100)]
public sealed class HpDrainHook : ICombatAftermathHook
{
    /// <inheritdoc />
    public void Apply(CombatHitContext context)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        var ratio = context.Attacker.Stats.Effects.HpDrainRatio;
        if (ratio <= 0) return;
        var healing = Math.Min(context.Attacker.Stats.MaxHitpoints - context.Attacker.Hitpoints,
            context.Damage * ratio);
        if (healing <= 0) return;
        context.Attacker.Hitpoints += healing;
        context.Emit(new CombatEvent(context.AtSeconds, context.Attacker.Id, context.Attacker.Id,
            0, true, context.AttackStats.DamageKind, context.Attacker.Hitpoints)
        { AbilityId = context.AbilityId, Healing = healing });
    }
}

/// <summary>灵力汲取：命中后按实际伤害从目标灵力池偷取；目标减多少算多少，攻击方只装得下的部分。</summary>
[CombatHookOrder(110)]
public sealed class ManaDrainHook : ICombatAftermathHook
{
    /// <inheritdoc />
    public void Apply(CombatHitContext context)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        var ratio = context.Attacker.Stats.Effects.MpDrainRatio;
        if (ratio <= 0) return;
        var drained = Math.Min(context.Target.Manapoints, context.Damage * ratio);
        if (drained <= 0) return;
        context.Target.Manapoints -= drained;
        context.Attacker.Manapoints = Math.Min(context.Attacker.Stats.MaxManapoints,
            context.Attacker.Manapoints + drained);
    }
}

/// <summary>伤害反弹：受击方把实收伤害按比例弹回攻击方；衍生伤害直接落账并计入受击方威胁值。</summary>
[CombatHookOrder(200)]
public sealed class DamageReflectHook : ICombatAftermathHook
{
    /// <inheritdoc />
    public void Apply(CombatHitContext context)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        var ratio = context.Target.Stats.Effects.DamageReflectRatio;
        if (ratio <= 0) return;
        var reflected = context.Damage * ratio;
        if (reflected <= 0) return;
        context.Attacker.Hitpoints = Math.Max(0, context.Attacker.Hitpoints - reflected);
        context.Target.Threat += reflected;
        context.Emit(new CombatEvent(context.AtSeconds, context.Target.Id, context.Attacker.Id,
            reflected, true, context.AttackStats.DamageKind, context.Attacker.Hitpoints));
    }
}
