using Domain.Common;
using Domain.Player;
using Protocol;
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

    [Fact]
    public void ToDelta_CarriesOnlyChangedAssetsAgainstBaseline()
    {
        var baseline = new List<AssetBalanceDto> { new("spirit", null, 100, 1) };
        var current = new List<AssetBalanceDto> { new("spirit", null, 80, 2), new("item.herb", null, 5, 1) };

        var changed = ProtocolSnapshotMapper.DiffAssets(baseline, current);
        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, asset => asset.AssetId == "spirit" && asset.Quantity == 80);
        Assert.Contains(changed, asset => asset.AssetId == "item.herb" && asset.Quantity == 5);

        // 余额与基线一致时不产生变更；无基线时回退为全量。
        Assert.Empty(ProtocolSnapshotMapper.DiffAssets(current, current));
        Assert.Equal(current.Count, ProtocolSnapshotMapper.DiffAssets(null, current).Count);
    }
}
