using Domain.Player;
using Domain.Simulation;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>奖励结算后的同步统计反应；行动、离线、每日账本按 SystemOrder 顺序消费。</summary>
public interface IRecordPlayerStats : ISystemTag;

/// <summary>常驻统计信箱，只在同步派发期间持有上下文与增量，不进入存档。</summary>
public sealed class PlayerStatsInboxComponent : YuoComponent
{
    public IPlayerResourceContext? Player { get; internal set; }
    public Guid? ActionInstanceId { get; internal set; }
    public double AtUnixSeconds { get; internal set; }
    public PlayerStatsDelta Delta { get; internal set; }
}

/// <summary>请求内的统计派发器；业务完成入账后立即运行 tag，返回前清除借用的状态与集合引用。</summary>
public sealed class EcsPlayerStatsSink(YuoEntity entity) : IPlayerStatsSink
{
    private readonly PlayerStatsInboxComponent _inbox = entity.GetComponent<PlayerStatsInboxComponent>()!;

    public void Record(IPlayerResourceContext player, Guid? actionInstanceId, double atUnixSeconds, in PlayerStatsDelta delta)
    {
        _inbox.Player = player;
        _inbox.ActionInstanceId = actionInstanceId;
        _inbox.AtUnixSeconds = atUnixSeconds;
        _inbox.Delta = delta;
        try
        {
            YuoWorld.RunSystem<IRecordPlayerStats>(_inbox.Entity);
        }
        finally
        {
            _inbox.Player = null;
            _inbox.ActionInstanceId = null;
            _inbox.AtUnixSeconds = 0;
            _inbox.Delta = default;
        }
    }
}
