using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Player;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Combat;

/// <summary>
/// 战斗边界相位事件 tag：AdvanceBoundary 内部各结算阶段的派发标识。
/// 每个 tag 对应边界管线上的一个固定相位；同 tag 订阅者按 SystemOrder 顺序消费。
/// </summary>
public interface ICombatBuffTickPhase : ISystemTag;
/// <summary>友方复活相位：单个倒地盟友倒计时到点→复活判定与决策重置；多盟友按成员序逐个派发。</summary>
public interface ICombatAllyRespawnPhase : ISystemTag;
/// <summary>友方出手相位：结算待完成动作并决策下一动作；多盟友按成员序逐个派发。</summary>
public interface ICombatAllyActPhase : ISystemTag;
/// <summary>敌方出手相位：与玩家同构，按敌索引逐一结算。</summary>
public interface ICombatEnemyActPhase : ISystemTag;
/// <summary>友方倒地相位：单个盟友阵亡瞬间打断吟唱并记录死亡时刻；全灭时顺带作废全场吟唱。</summary>
public interface ICombatAllyDownedPhase : ISystemTag;
/// <summary>边界收尾相位：序列推进与终局裁决。</summary>
public interface ICombatOutcomePhase : ISystemTag;

/// <summary>
/// 相位工作上下文：每次 AdvanceTo 创建一份并在各自然边界间复用，
/// 相位 System 经 host 组件读取；事件/死亡/复活列表 lazy 分配保持原 null 语义。
/// </summary>
public sealed class CombatPhaseContext
{
    /// <summary>本边界推进的遭遇战状态（就地可变）。</summary>
    public CombatEncounterState Encounter = null!;
    /// <summary>本段产生的领域事件列表；无事件保持 null（与原 OOP 结果语义一致）。</summary>
    public List<CombatEvent>? Events;
    /// <summary>玩家死亡时刻列表（战斗时钟秒）；无死亡保持 null。</summary>
    public List<double>? PlayerDeaths;
    /// <summary>玩家复活时刻列表（战斗时钟秒）；无复活保持 null。</summary>
    public List<double>? PlayerRespawns;
    /// <summary>敌方出手相位的当前敌下标；其它相位保持 -1。</summary>
    public int EnemyIndex = -1;
    /// <summary>Buff tick 相位的结算时刻。</summary>
    public double EffectAt;
    /// <summary>敌方出手相位的当前敌人单位（EnemyIndex 对应引用）。</summary>
    public CombatantState? Enemy;
    /// <summary>盟友出手/倒地/复活相位的当前成员下标（共享剧本成员序）；其它相位保持 -1。</summary>
    public int AllyIndex = -1;
    /// <summary>盟友出手/倒地/复活相位的当前友方单位（AllyIndex 对应引用）。</summary>
    public CombatantState? Ally;
    /// <summary>非本机成员阵亡时刻列表（MemberIndex, 战斗时钟秒）；无阵亡保持 null。</summary>
    public List<(int MemberIndex, double AtSeconds)>? AllyDeaths;
    /// <summary>非本机成员复活时刻列表（MemberIndex, 战斗时钟秒）；无复活保持 null。</summary>
    public List<(int MemberIndex, double AtSeconds)>? AllyRespawns;
}

/// <summary>
/// 战斗相位派发器：决定相位走 RunSystem&lt;tag&gt; 实体派发还是直接调用。
/// 实体宿主路径由 <see cref="EcsCombatPhaseDispatcher"/> 承载；测试/斗法镜像等
/// 无世界上下文的路径由 <see cref="InlineCombatPhaseDispatcher"/> 承载——两条路调用同一实现。
/// </summary>
public interface ICombatPhaseDispatcher
{
    /// <summary>把当前相位派发给订阅的相位 System；host 非空时经实体派发，否则内联。</summary>
    void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag;
}

/// <summary>实体宿主派发：经 RunSystem&lt;tag&gt; 把相位消息发给战斗实体。</summary>
public sealed class EcsCombatPhaseDispatcher : ICombatPhaseDispatcher
{
    /// <summary>进程级共享实例（无状态）。</summary>
    public static readonly EcsCombatPhaseDispatcher Instance = new();

    /// <summary>把相位上下文挂到遭遇组件后经 RunSystem<tag> 派发，派发完摘除上下文。</summary>
    public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
    {
        ArgumentNullException.ThrowIfNull(host);
        host.PhaseContext = context;
        try
        {
            YuoWorld.RunSystem<TTag>(host.Entity);
        }
        finally
        {
            host.PhaseContext = null;
        }
    }
}

/// <summary>无世界路径派发：相位 System 的共享实现直接同步调用，语义与实体派发逐位一致。</summary>
public sealed class InlineCombatPhaseDispatcher : ICombatPhaseDispatcher
{
    /// <summary>进程级共享实例（无状态）。</summary>
    public static readonly InlineCombatPhaseDispatcher Instance = new();

    /// <summary>无实体路径：直接同步调用相位 System 的共享实现。</summary>
    public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
        => Systems.CombatPhaseSystems.DispatchInline<TTag>(context);
}
