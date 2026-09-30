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
/// <summary>玩家复活相位：倒地→复活判定与决策重置。</summary>
public interface ICombatPlayerRespawnPhase : ISystemTag;
/// <summary>玩家出手相位：结算待完成动作并决策下一动作。</summary>
public interface ICombatPlayerActPhase : ISystemTag;
/// <summary>敌方出手相位：与玩家同构，按敌索引逐一结算。</summary>
public interface ICombatEnemyActPhase : ISystemTag;
/// <summary>玩家死亡处理相位：倒地瞬间打断吟唱并记录死亡时刻。</summary>
public interface ICombatPlayerDownedPhase : ISystemTag;
/// <summary>边界收尾相位：序列推进与终局裁决。</summary>
public interface ICombatOutcomePhase : ISystemTag;

/// <summary>
/// 相位工作上下文：AdvanceBoundary 每次边界推进创建一份，
/// 相位 System 经 host 组件读取；事件/死亡/复活列表 lazy 分配保持原 null 语义。
/// </summary>
public sealed class CombatPhaseContext
{
    public CombatEncounterState Encounter = null!;
    public List<CombatEvent>? Events;
    public List<double>? PlayerDeaths;
    public List<double>? PlayerRespawns;
    /// <summary>敌方出手相位的当前敌下标；其它相位保持 -1。</summary>
    public int EnemyIndex = -1;
    /// <summary>Buff tick 相位的结算时刻。</summary>
    public double EffectAt;
    /// <summary>敌方出手相位的当前敌人单位（EnemyIndex 对应引用）。</summary>
    public CombatantState? Enemy;
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
    public static readonly EcsCombatPhaseDispatcher Instance = new();

    public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
    {
        ArgumentNullException.ThrowIfNull(host);
        host.PhaseContext = context;
        YuoWorld.RunSystem<TTag>(host.Entity);
    }
}

/// <summary>无世界路径派发：相位 System 的共享实现直接同步调用，语义与实体派发逐位一致。</summary>
public sealed class InlineCombatPhaseDispatcher : ICombatPhaseDispatcher
{
    public static readonly InlineCombatPhaseDispatcher Instance = new();

    public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
        => Systems.CombatPhaseSystems.DispatchInline<TTag>(context);
}
