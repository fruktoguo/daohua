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
    void OnHit(PendingCombatTrigger trigger);
}

/// <summary>轻量触发器的稳定执行顺序。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CombatTriggerOrderAttribute : Attribute
{
    public CombatTriggerOrderAttribute(int order) => Order = order;

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

[CombatTriggerOrder(100)]
public sealed class InlineOnHitLeechHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) return;

        CombatTriggerMath.HpDrain(
            trigger.Context,
            trigger.Context.Attacker.Stats.Effects.HpDrainPoints);
    }
}

[CombatTriggerOrder(110)]
public sealed class InlineManaLeechHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) return;

        CombatTriggerMath.ManaDrain(
            trigger.Context,
            trigger.Context.Attacker.Stats.Effects.MpDrainPoints);
    }
}

[CombatTriggerOrder(200)]
public sealed class InlineThornsHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (!ReferenceEquals(trigger.Unit, trigger.Context.Target)) return;

        CombatTriggerMath.DamageReflect(
            trigger.Context,
            trigger.Context.Target.Stats.Effects.DamageReflectPoints);
    }
}
