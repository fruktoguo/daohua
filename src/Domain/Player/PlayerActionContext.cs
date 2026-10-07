using Domain.Simulation;

namespace Domain.Player;

/// <summary>聚合把既有分段与单写者操作直接借给计时原语，不创建另一份状态。</summary>
public sealed partial class PlayerAggregate : IPlayerResourceContext
{
    DateTimeOffset IPlayerResourceContext.LastSimulatedAt { get => LastSimulatedAt; set => LastSimulatedAt = value; }
    long IPlayerResourceContext.StateVersion { get => StateVersion; set => StateVersion = value; }
    PlayerAttributes IPlayerResourceContext.ResolveAttributes(GameDataCatalog catalog)
        => PlayerAttributeResolver.Resolve(this, catalog);
    bool IPlayerResourceContext.ExpireBuffs(double atUnixSeconds)
    {
        var expired = Buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= atUnixSeconds);
        if (expired > 0) MarkDirty(PlayerSectionKind.Buffs);
        return expired > 0;
    }
    IReadOnlyList<IPlayerActionState> IPlayerActionContext.Actions => _actionsView;
    void IPlayerActionContext.EnsureActionReport(Guid instanceId, ActionId actionId, string actionName, DateTimeOffset now)
        => EnsureActionReport(instanceId, actionId, actionName, now);
    void IPlayerActionContext.RecordStats(Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta)
        => RecordStats(actionInstanceId, atUnixSeconds, delta);
    // 公共入口返回具体 QueuedAction 供命令路径直接使用；接口按基类型转发同一方法。
    IPlayerActionState IPlayerActionContext.EnqueuePriority(ActionIntent intent, DateTimeOffset at,
        ActionCatalog catalog, TravelActionState? travel, bool incrementVersion, ActionParticipationBinding? participation)
        => EnqueuePriority(intent, at, catalog, travel, incrementVersion, participation);
}
