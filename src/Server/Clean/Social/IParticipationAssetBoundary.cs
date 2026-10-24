namespace Server.Clean.Social;

/// <summary>资产事务的外层会话门：先结清在线实耗，提交结束后才释放；内部会话事务不得反调。</summary>
public interface IParticipationAssetBoundary
{
    /// <summary>按会话标识升序锁定相关战斗消耗品，离线会话只串行资产变更、不触发批算。</summary>
    ValueTask<IDisposable> AcquireAsync(IReadOnlyList<ParticipationAssetKey> keys,
        CancellationToken cancellationToken = default);
}
