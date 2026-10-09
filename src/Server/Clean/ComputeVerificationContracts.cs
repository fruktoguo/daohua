namespace Server.Clean;

/// <summary>主服对 ComputeNode 候选结果执行独立 ECS 重演的验证边界。</summary>
public interface IComputeResultVerifier
{
    ValueTask<ComputeVerificationResult> VerifyAsync(
        ComputeAdvanceJobRequest request,
        ComputeAdvanceJobResult candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>同一 RepeatableRead 快照内冻结的玩家存档与钱包基线。</summary>
public sealed record ComputeFrozenInput(
    PlayerStateBundle Bundle,
    IReadOnlyList<AssetBalanceRecord> Balances);

/// <summary>可信补算验证结果；只有 Accepted 才允许进入同一数据库事务写回。</summary>
public sealed record ComputeVerificationResult(
    bool Accepted,
    string? Error,
    IReadOnlyList<SectionWrite> Writes,
    IReadOnlyList<AssetDelta> AssetDeltas,
    long SourceStateVersion,
    long TargetStateVersion,
    DateTimeOffset LastSimulatedAt)
{
    public static ComputeVerificationResult Rejected(string error) =>
        new(false, error, [], [], 0, 0, default);
}
