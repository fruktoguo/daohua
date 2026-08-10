using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Simulation;
using Xunit;

namespace Xiuxian.Domain.Tests;

public sealed class XiuxianGameDataTests
{
    [Fact]
    public void Embedded_game_data_is_valid_and_versioned()
    {
        var snapshot = XiuxianGameData.Current;

        Assert.Equal(1, XiuxianGameData.SupportedSchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ContentVersion));
        Assert.Equal(16, snapshot.Fingerprint.Length);
        Assert.NotEmpty(snapshot.Actions);
        Assert.NotEmpty(snapshot.Items);
        Assert.NotEmpty(snapshot.Abilities);
        Assert.NotEmpty(snapshot.Monsters);
        Assert.NotEmpty(snapshot.Encounters);
        Assert.NotEmpty(snapshot.Maps);
    }

    [Fact]
    public void All_combat_encounters_use_standard_search_and_respawn_timings()
    {
        Assert.All(XiuxianCombatCatalog.AllEncounters, encounter =>
        {
            Assert.Equal(20_000, encounter.SearchMilliseconds);
            Assert.Equal(5_000, encounter.RespawnMilliseconds);
            Assert.Equal(180_000, encounter.PlayerRespawnMilliseconds);
        });
    }
}
