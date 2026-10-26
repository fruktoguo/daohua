using System.Reflection;
using Domain.Combat;
using Domain.Common;

namespace DaoHua.Ecs;

/// <summary>
/// 轻量触发器处理器基类。
/// 该类型故意不继承 <c>ISystemTag</c>，避免被 YuoCore 当作实体 System 注册。
/// </summary>
public abstract class CombatTriggerHandler;

/// <summary>无实体命中触发器的固定事件契约。</summary>
public interface IInlineOnHitTrigger
{
    /// <summary>处理一条待派发命中触发器；处理器自行判定宿主是攻击方还是受击方，不匹配时直接返回。</summary>
    void OnHit(PendingCombatTrigger trigger);
}

/// <summary>轻量触发器的稳定执行顺序。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CombatTriggerOrderAttribute : Attribute
{
    /// <summary>声明处理器在同类事件中的执行顺序。</summary>
    public CombatTriggerOrderAttribute(int order) => Order = order;

    /// <summary>升序执行顺序号；相同时按处理器类型全名 Ordinal 二次排序，消除反射扫描顺序差异。</summary>
    public int Order { get; }
}

/// <summary>
/// 无实体战斗的轻量触发器注册表。
/// 反射只在静态初始化阶段执行，派发阶段只遍历已缓存的强类型接口数组。
/// </summary>
internal static class InlineCombatTriggerRegistry
{
    private static readonly IInlineOnHitTrigger[] OnHitHandlers = Build<IInlineOnHitTrigger>();

    internal static int OnHitHandlerCount => OnHitHandlers.Length;

    internal static IReadOnlyList<Type> OnHitHandlerTypes
        => OnHitHandlers.Select(static handler => handler.GetType()).ToArray();

    /// <summary>显式触发类型初始化，使运行时在启动阶段暴露注册错误。</summary>
    internal static void EnsureInitialized()
    {
        _ = OnHitHandlers.Length;
    }

    internal static void Dispatch(PendingCombatTrigger trigger)
    {
        switch (trigger.Kind)
        {
            case CombatTriggerKind.OnHit:
                for (var i = 0; i < OnHitHandlers.Length; i++)
                    OnHitHandlers[i].OnHit(trigger);
                return;
            default:
                throw new DomainException(
                    DomainErrorCode.InvariantViolation,
                    $"未知轻量战斗触发器种类：{trigger.Kind}。");
        }
    }

    private static THandler[] Build<THandler>()
        where THandler : class
    {
        var handlerType = typeof(THandler);
        var assembly = typeof(InlineCombatTriggerRegistry).Assembly;
        var discovered = new List<(int Order, string Name, THandler Handler)>();

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var details = string.Join(
                " | ",
                exception.LoaderExceptions
                    .Where(static error => error is not null)
                    .Select(static error => error!.Message));
            throw new InvalidOperationException(
                $"扫描轻量战斗触发器程序集失败：{details}",
                exception);
        }

        foreach (var type in types)
        {
            if (type.IsAbstract
                || type.IsGenericTypeDefinition
                || !typeof(CombatTriggerHandler).IsAssignableFrom(type)
                || !handlerType.IsAssignableFrom(type))
            {
                continue;
            }

            var order = type.GetCustomAttribute<CombatTriggerOrderAttribute>();
            if (order is null)
                throw new InvalidOperationException(
                    $"轻量战斗触发器 {type.FullName} 缺少 CombatTriggerOrderAttribute。");

            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException(
                    $"轻量战斗触发器 {type.FullName} 必须提供公开无参构造函数。");

            var instance = Activator.CreateInstance(type) as THandler
                ?? throw new InvalidOperationException(
                    $"轻量战斗触发器 {type.FullName} 实例化后无法转换为 {handlerType.FullName}。");
            discovered.Add((order.Order, type.FullName ?? type.Name, instance));
        }

        return discovered
            .OrderBy(static item => item.Order)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Select(static item => item.Handler)
            .ToArray();
    }
}

/// <summary>内联气血汲取：命中后攻击方按实伤比例回血（与实体路径 OnHitLeechSystem 等价）。</summary>
[CombatTriggerOrder(100)]
public sealed class InlineOnHitLeechHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>宿主为攻击方时按 Stats.Effects.HpDrainPoints 结算气血汲取。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        // 一条 OnHit 触发器会广播给全部处理器；本处理器只消费宿主为攻击方的条目。
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) return;

        CombatTriggerMath.HpDrain(
            trigger.Context,
            trigger.Context.Attacker.Stats.Effects.HpDrainPoints);
    }
}

/// <summary>内联灵力汲取：命中后攻击方按实伤从目标灵力池偷取（与实体路径 ManaLeechSystem 等价）。</summary>
[CombatTriggerOrder(110)]
public sealed class InlineManaLeechHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>宿主为攻击方时按 Stats.Effects.MpDrainPoints 结算灵力汲取。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) return;

        CombatTriggerMath.ManaDrain(
            trigger.Context,
            trigger.Context.Attacker.Stats.Effects.MpDrainPoints);
    }
}

/// <summary>内联伤害反弹：受击方把实收伤害按比例弹回攻击方（与实体路径 ThornsSystem 等价）。</summary>
[CombatTriggerOrder(200)]
public sealed class InlineThornsHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>宿主为受击方时按 Stats.Effects.DamageReflectPoints 结算伤害反弹。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Target)) return;

        CombatTriggerMath.DamageReflect(
            trigger.Context,
            trigger.Context.Target.Stats.Effects.DamageReflectPoints);
    }
}

/// <summary>内联受击涨威胁：受击方按攻击方面板原始伤害累积自身威胁（与实体路径 ThreatOnTakenSystem 等价）。</summary>
[CombatTriggerOrder(300)]
public sealed class InlineThreatOnTakenHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>宿主为受击方时按 Stats.Effects.ThreatOnTakenPoints 结算受击涨威胁。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Target)) return;

        CombatTriggerMath.ThreatOnTaken(
            trigger.Context,
            trigger.Context.Target.Stats.Effects.ThreatOnTakenPoints);
    }
}

/// <summary>内联阵盘受击反击：受击方受实伤后由己方存活天雷阵/血煞阵持有者反击。</summary>
[CombatTriggerOrder(400)]
public sealed class InlineFormationReactiveStrikeHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>宿主为受击方时派发己方阵盘反击。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Target)) return;
        CombatTriggerMath.FormationReactiveStrike(trigger.Context);
    }
}
