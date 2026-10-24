using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 计算任务标识派生：纯哈希函数，不触数据库。
/// 归此而不进 PostgresIntegration 集合，避免该用例在 --fast 下被整类排除。
/// </summary>
public sealed class ComputeJobIdentityTests
{
    /// <summary>派生 job_id 必须对相同输入确定、对不同输入区分。</summary>
    [Fact]
    public void DeriveJobIdIsDeterministicAndDistinct()
    {
        var player = Guid.NewGuid();
        var first = PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000);
        Assert.Equal(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_001));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(Guid.NewGuid(), 1_700_000_000_000));
    }
}
