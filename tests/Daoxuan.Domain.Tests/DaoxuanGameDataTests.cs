using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

public sealed class DaoxuanGameDataTests
{
    [Fact]
    public void Embedded_game_data_is_valid_and_versioned()
    {
        var snapshot = DaoxuanGameData.Current;

        Assert.Equal(1, DaoxuanGameData.SupportedSchemaVersion);
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
        Assert.All(DaoxuanCombatCatalog.AllEncounters, encounter =>
        {
            Assert.Equal(20_000, encounter.SearchMilliseconds);
            Assert.Equal(5_000, encounter.RespawnMilliseconds);
            Assert.Equal(180_000, encounter.PlayerRespawnMilliseconds);
        });
    }
}
