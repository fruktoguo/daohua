using Domain.Common;
using Domain.Player;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class ProtocolSnapshotMapperTests
{
    [Fact]
    public void SnapshotMapper_EmitsProtocolDtoWithoutDomainRuntimeObjects()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "映射测试", DateTimeOffset.UtcNow, "fp");
        var state = aggregate.ExportState();
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, [], []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp");
        Assert.Equal(playerId.ToString("D"), dto.PlayerId);
        Assert.Equal("映射测试", dto.DisplayName);
        Assert.Equal(7, dto.Sections.Count);
        Assert.All(dto.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.PayloadBase64)));
    }
}
