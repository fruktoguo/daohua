using YuoCore.Ecs;

namespace DaoHua.EcsDifferential;

// 组件类型池：全部为 YuoComponent 直接子类（避免意外进入 baseTypeMap），
// 唯二的继承体系是 BuffBaseComponent 一族，专门覆盖"以基类声明类型添加子类实例"。
// Serial 由执行器在创建时分配，用于台账中跨变体标识同一个逻辑组件实例。

public class PositionComponent : YuoComponent
{
    public int X;
    public int Y;
    public int Serial;
}

public class VelocityComponent : YuoComponent
{
    public int Dx;
    public int Dy;
    public int Serial;
}

public class HealthComponent : YuoComponent, IComponentInit<int>
{
    public int Hp;
    public int MaxHp;
    public int Serial;

    public void ComponentInit(int initData)
    {
        Hp = initData;
        MaxHp = initData * 2 + 1;
    }
}

public class ManaComponent : YuoComponent
{
    public int Mp;
    public int Serial;
}

public class MarkComponent : YuoComponent
{
    public int Serial;
}

public class ShieldComponent : YuoComponent
{
    public int Sp;
    public int Serial;
}

[SingleComponent]
public class CharmComponent : YuoComponent
{
    public int Serial;
}

/// <summary>
/// 重入驱动组件：Op 为指令码，Arg 为目标实体 id；由 ChaosSystem 在回调内执行，
/// 覆盖"在系统回调内再次 add/remove/destroy"。
/// </summary>
public class ChaosComponent : YuoComponent
{
    public int Op;
    public long Arg;
    public int Serial;
}

public class BuffBaseComponent : YuoComponent
{
    public int Stacks;
    public int Serial;
}

public class PoisonBuffComponent : BuffBaseComponent
{
    public int Tick;
}

public class RegenBuffComponent : BuffBaseComponent
{
    public int Rate;
}

public class AnchorComponent : YuoComponent
{
    public int Serial;
}

/// <summary>[LinkComponentOf]：向实体添加 AnchorComponent 后，Update 的 IStart 阶段自动补挂本组件。</summary>
[LinkComponentOf(typeof(AnchorComponent))]
public class AutoLinkedComponent : YuoComponent
{
    public int Serial;
}

/// <summary>把所有用到的组件类型集中注册，供操作生成器按索引选取。</summary>
public static class ComponentPool
{
    public static readonly Type[] Types =
    {
        typeof(PositionComponent),
        typeof(VelocityComponent),
        typeof(HealthComponent),
        typeof(ManaComponent),
        typeof(MarkComponent),
        typeof(ShieldComponent),
        typeof(CharmComponent),
        typeof(AnchorComponent),
        typeof(AutoLinkedComponent),
        typeof(BuffBaseComponent),
        typeof(PoisonBuffComponent),
        typeof(RegenBuffComponent),
    };

    /// <summary>台账中使用的短名，避免 Type.Name 与内部命名空间噪音。</summary>
    public static string Short(Type t)
    {
        if (t == typeof(PositionComponent)) return "pos";
        if (t == typeof(VelocityComponent)) return "vel";
        if (t == typeof(HealthComponent)) return "hp";
        if (t == typeof(ManaComponent)) return "mp";
        if (t == typeof(MarkComponent)) return "mark";
        if (t == typeof(ShieldComponent)) return "sh";
        if (t == typeof(CharmComponent)) return "charm";
        if (t == typeof(AnchorComponent)) return "anchor";
        if (t == typeof(AutoLinkedComponent)) return "alink";
        if (t == typeof(BuffBaseComponent)) return "buff";
        if (t == typeof(PoisonBuffComponent)) return "poison";
        if (t == typeof(RegenBuffComponent)) return "regen";
        if (t == typeof(ChaosComponent)) return "chaos";
        return t.Name;
    }

    /// <summary>组件快照：声明类型 + 可见字段，序列化结果进台账/摘要。</summary>
    public static string Snap(YuoComponent c)
    {
        switch (c)
        {
            case null:
                return "null";
            case PositionComponent p:
                return $"{p.X},{p.Y},s{p.Serial}";
            case VelocityComponent v:
                return $"{v.Dx},{v.Dy},s{v.Serial}";
            case HealthComponent h:
                return $"{h.Hp},{h.MaxHp},s{h.Serial}";
            case ManaComponent m:
                return $"{m.Mp},s{m.Serial}";
            case MarkComponent mk:
                return $"s{mk.Serial}";
            case ShieldComponent s:
                return $"{s.Sp},s{s.Serial}";
            case CharmComponent ch:
                return $"s{ch.Serial}";
            case ChaosComponent c2:
                return $"{c2.Op},{c2.Arg},s{c2.Serial}";
            case PoisonBuffComponent p2:
                return $"{p2.Stacks},{p2.Tick},s{p2.Serial}";
            case RegenBuffComponent r2:
                return $"{r2.Stacks},{r2.Rate},s{r2.Serial}";
            case BuffBaseComponent b:
                return $"{b.Stacks},s{b.Serial}";
            case AnchorComponent a:
                return $"s{a.Serial}";
            case AutoLinkedComponent al:
                return $"s{al.Serial}";
            case EntityComponent:
                return "entitydata";
            case YuoWorldMainComponent:
                return "worldmain";
            case SceneComponent:
                return "scene";
            default:
                return c.Type.Name;
        }
    }
}
