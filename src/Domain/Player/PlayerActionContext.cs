using Domain.Simulation;

namespace Domain.Player;

/// <summary>聚合把既有分段与单写者操作直接借给计时原语，不创建另一份状态。</summary>
public sealed partial class PlayerAggregate : IPlayerResourceContext
{
    DateTimeOffset IPlayerResourceContext.LastSimulatedAt { get => LastSimulatedAt; set => LastSimulatedAt = value; }
    long IPlayerResourceContext.StateVersion { get => StateVersion; set => StateVersion = value; }
    PlayerAttributes IPlayerResourceContext.ResolveAttributes(GameDataCatalog catalog)
        => PlayerAttributeResolver.Resolve(this, catalog);
    IReadOnlyList<IPlayerActionState> IPlayerActionContext.Actions => _actionsView;
    void IPlayerActionContext.EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => EnsureActionReport(instanceId, actionId, actionName, now);
    void IPlayerActionContext.RecordStats(Guid? actionInstanceId, double atUnixSeconds, Action<PlayerBattleReport> apply)
        => RecordStats(actionInstanceId, atUnixSeconds, apply);
}
