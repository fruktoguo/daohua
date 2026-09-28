using YuoCore.Ecs;

namespace DaoHua.EcsDifferential;

/// <summary>自定义系统组：验证 RunSystemOnlyGroup / ExcludeGroup 的组过滤派发。</summary>
public class DiffGroupA : SystemGroupBase;

public class DiffGroupB : SystemGroupBase;

/// <summary>自定义标签接口：验证 ISystemTag 的自定义标签派发路径。</summary>
public interface IPulse : ISystemTag;

/// <summary>
/// 探针出口：系统回调统一从这里进台账。静态持有当前 Ledger，
/// 因为系统实例由 YuoWorld 反射创建、无法注入依赖。
/// </summary>
public static class DiffProbe
{
    public static Ledger L;
    public static int ChaosDepth;
    public static readonly Dictionary<string, long> CallbackCounts = new();

    /// <summary>
    /// 当前操作内的批量回调收集器：sys|tag -> 按执行顺序的实体 id 列表。
    /// 用于在不打乱 cb 原始顺序信号的前提下，另发一条归一化批次摘要（bs 行），
    /// 让"同一集合不同迭代顺序"与"集合本身不同"在 diff 中可区分。
    /// </summary>
    public static Dictionary<string, List<long>> Batch;

    public static void Cb<T>(SystemBase sys, string comp, T component) where T : YuoComponent
    {
        var tag = sys.RunType?.Name ?? "?";
        var name = sys.SystemName;
        var key = name + "|" + tag;
        CallbackCounts.TryGetValue(key, out var n);
        CallbackCounts[key] = n + 1;
        var eid = component?.Entity?.ID ?? -1;
        if (Batch != null)
        {
            if (!Batch.TryGetValue(key, out var list)) Batch[key] = list = new List<long>();
            list.Add(eid);
        }

        L?.Cb(name, tag, eid, comp, ComponentPool.Snap(component));
    }
}

// 同一组件挂多个系统：覆盖"多系统监听同组件"的派发顺序。
public class HealthAwakeSystem : YuoSystem<HealthComponent>, IAwake, IDestroy
{
    public override void Run(HealthComponent component) => DiffProbe.Cb(this, "hp", component);
}

public class HealthAuditSystem : YuoSystem<HealthComponent>, IAwake, IDestroy
{
    public override void Run(HealthComponent component) => DiffProbe.Cb(this, "hp", component);
}

// ReplaceComponent 触发的 ISwitchComponent 派发。
public class HealthSwitchSystem : YuoSystem<HealthComponent>, ISwitchComponent
{
    public override void Run(HealthComponent component) => DiffProbe.Cb(this, "hp", component);
}

public class HealthStartSystem : YuoSystem<HealthComponent>, IStart
{
    public override void Run(HealthComponent component) => DiffProbe.Cb(this, "hp", component);
}

public class PositionAwakeSystem : YuoSystem<PositionComponent>, IAwake, IDestroy
{
    public override void Run(PositionComponent component) => DiffProbe.Cb(this, "pos", component);
}

// 双组件系统：influenceTypes 集合语义（两个组件齐全才进入成员集）。
[SystemOrder(5)]
public class PairSystem : YuoSystem<PositionComponent, HealthComponent>, IAwake, IDestroy, IUpdate
{
    public override void Run(PositionComponent c1, HealthComponent c2) =>
        DiffProbe.Cb(this, "pos+hp", c1);
}

public class MoveSystem : YuoSystem<PositionComponent, VelocityComponent>, IUpdate, IFixedUpdate
{
    public override void Run(PositionComponent c1, VelocityComponent c2) =>
        DiffProbe.Cb(this, "pos+vel", c1);
}

[SystemOrder(-3)]
public class ManaLateSystem : YuoSystem<ManaComponent>, ILateUpdate
{
    public override void Run(ManaComponent component) => DiffProbe.Cb(this, "mp", component);
}

// 基类组件系统：YuoSystemOfBase 走 systemsOfBaseComponent / HasBaseComponent 路径。
public class BuffWatchSystem : YuoSystemOfBase<BuffBaseComponent>, IAwake, IDestroy, IUpdate
{
    public override void Run(BuffBaseComponent baseComponent) =>
        DiffProbe.Cb(this, "buff", baseComponent);
}

// 组系统：只属于 DiffGroupA。
[SystemGroup(typeof(DiffGroupA))]
public class ShieldGroupSystem : YuoSystem<ShieldComponent>, IUpdate, IAwake, IDestroy
{
    public override void Run(ShieldComponent component) => DiffProbe.Cb(this, "sh", component);
}

// 被覆盖系统：实体持有 ShieldComponent 时被 CoveringSystem 声明覆盖、跳过执行。
public class CoveredMarkSystem : YuoSystem<MarkComponent>, IUpdate
{
    public override void Run(MarkComponent component) => DiffProbe.Cb(this, "mark", component);
}

[CoverSystem(typeof(CoveredMarkSystem), typeof(ShieldComponent))]
public class CoveringMarkSystem : YuoSystem<MarkComponent>, IUpdate
{
    public override void Run(MarkComponent component) => DiffProbe.Cb(this, "mark", component);
}

// 自定义标签：RunSystemForAll<IPulse> 派发。
public class MarkPulseSystem : YuoSystem<MarkComponent>, IPulse
{
    public override void Run(MarkComponent component) => DiffProbe.Cb(this, "mark", component);
}

public class AnchorWatchSystem : YuoSystem<AnchorComponent>, IAwake, IDestroy, IStart
{
    public override void Run(AnchorComponent component) => DiffProbe.Cb(this, "anchor", component);
}

public class AutoLinkedWatchSystem : YuoSystem<AutoLinkedComponent>, IAwake, IDestroy, IStart
{
    public override void Run(AutoLinkedComponent component) => DiffProbe.Cb(this, "alink", component);
}

public class CharmWatchSystem : YuoSystem<CharmComponent>, IAwake, IDestroy
{
    public override void Run(CharmComponent component) => DiffProbe.Cb(this, "charm", component);
}

// IExitGame 在世界销毁时对所有成员实体派发。
public class MarkExitSystem : YuoSystem<MarkComponent>, IExitGame
{
    public override void Run(MarkComponent component) => DiffProbe.Cb(this, "mark", component);
}

/// <summary>
/// 重入系统：Run 内根据 ChaosComponent.Op 继续对其它实体执行结构变更，
/// 覆盖"系统回调内再次 add/remove/destroy/create"的所有形态。
/// </summary>
public class ChaosSystem : YuoSystem<ChaosComponent>, IAwake, IDestroy, IUpdate
{
    public override void Run(ChaosComponent component)
    {
        DiffProbe.Cb(this, "chaos", component);
        var op = component.Op;
        if (op == 0) return;
        component.Op = 0; // 一次性指令，避免二次回调反复触发
        if (DiffProbe.ChaosDepth >= 4)
        {
            DiffProbe.L?.Q("chaos_cap", "4");
            return;
        }

        var entity = component.Entity;
        var world = entity.World;
        var target = world.GetEntity(component.Arg);
        DiffProbe.ChaosDepth++;
        try
        {
            switch (op)
            {
                case 1: // 回调内移除自身组件
                    entity.RemoveComponent<ChaosComponent>();
                    break;
                case 2: // 回调内给目标实体加组件
                    if (target != null) DiffProbe.L?.Q("chaos_add_hp", ComponentPool.Snap(target.AddComponent<HealthComponent>()));
                    else DiffProbe.L?.Q("chaos_add_hp", "\"null_target\"");
                    break;
                case 3: // 回调内延迟销毁目标
                    target?.Destroy();
                    DiffProbe.L?.Q("chaos_destroy", target == null ? "\"null_target\"" : "1");
                    break;
                case 4: // 回调内创建新实体并挂组件（派发嵌套）
                {
                    var child = new YuoEntity(world);
                    var mark = child.AddComponent<MarkComponent>();
                    mark.Serial = -7;
                    DiffProbe.L?.Q("chaos_create", child.ID.ToString());
                    break;
                }
                case 5: // 回调内强制销毁目标
                    if (target != null)
                    {
                        YuoWorld.DestroyEntityForce(target);
                        DiffProbe.L?.Q("chaos_fdestroy", "1");
                    }
                    else DiffProbe.L?.Q("chaos_fdestroy", "\"null_target\"");
                    break;
                case 6: // 回调内移除目标的 HealthComponent
                    if (target != null) target.RemoveComponent<HealthComponent>();
                    DiffProbe.L?.Q("chaos_rm_hp", target == null ? "\"null_target\"" : "1");
                    break;
                case 7: // 回调内延迟销毁自身
                    entity.Destroy();
                    DiffProbe.L?.Q("chaos_selfdestroy", "1");
                    break;
                case 8: // 回调内替换目标组件
                    if (target != null) target.ReplaceComponent(new HealthComponent { Hp = -1, Serial = -8 });
                    DiffProbe.L?.Q("chaos_replace", target == null ? "\"null_target\"" : "1");
                    break;
                case 9: // 回调内销毁目标的 PositionComponent（走组件缓冲）
                    target?.GetComponent<PositionComponent>()?.DestroyComponent();
                    DiffProbe.L?.Q("chaos_dcomp", target == null ? "\"null_target\"" : "1");
                    break;
                case 10: // 回调内把目标实体挂为子实体
                    if (target != null) entity.AddChild<VelocityComponent>().Serial = -10;
                    DiffProbe.L?.Q("chaos_child", target == null ? "\"null_target\"" : "1");
                    break;
            }
        }
        finally
        {
            DiffProbe.ChaosDepth--;
        }
    }
}
