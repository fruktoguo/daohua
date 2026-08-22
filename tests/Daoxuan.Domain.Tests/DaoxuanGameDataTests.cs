using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

/// <summary>
/// 验证游戏内置静态数据包（DaoxuanGameData）与战斗遭遇战元数据（DaoxuanCombatCatalog）的有效性与规范性。
/// </summary>
public sealed class DaoxuanGameDataTests
{
    /// <summary>
    /// 验证内置嵌入数据的内容完整性、Schema 版本匹配与哈希指纹非空。
    /// 确保行动、物品、技能、怪物、遭遇战及地图配置在加载后均非空且版本一致。
    /// </summary>
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

    /// <summary>
    /// 验证全部战斗遭遇战（Combat Encounters）统一遵循规范的搜寻与重生时间边界：
    /// 初始搜寻 20,000ms、波次间隙 5,000ms、败退后原地重聚 180,000ms。
    /// </summary>
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
