// ==============================================================================
// 文件名称: GameDataTests.cs
// 主要职责: 游戏静态内嵌配置数据（GameData）与元数据目录有效性门禁测试。
// 核心逻辑 / 定义内容:
//   1. 验证内嵌游戏静态数据的 Schema 版本兼容性、内容指纹（Fingerprint）与实体非空。
//   2. 验证全部战斗遭遇战（Combat Encounters）搜寻、波次与重生时间边界规范。
//   3. 验证地图节点、行动项与物品配置字典引用的完整性。
// ==============================================================================

namespace Domain.Tests;

/// <summary>
/// 验证游戏内置静态数据包（GameData）与战斗遭遇战元数据（CombatCatalog）的有效性与规范性。
/// </summary>
public sealed class GameDataCatalogTests
{
    /// <summary>
    /// 验证内置嵌入数据的内容完整性、Schema 版本匹配与哈希指纹非空。
    /// 确保行动、物品、技能、怪物、遭遇战及地图配置在加载后均非空且版本一致。
    /// </summary>
    [Fact]
    public void Embedded_game_data_is_valid_and_versioned()
    {
        var snapshot = GameDataCatalog.Current;

        Assert.Equal(1, GameDataCatalog.SupportedSchemaVersion);
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
        Assert.All(CombatCatalog.AllEncounters, encounter =>
        {
            Assert.Equal(20_000, encounter.SearchMilliseconds);
            Assert.Equal(5_000, encounter.RespawnMilliseconds);
            Assert.Equal(180_000, encounter.PlayerRespawnMilliseconds);
        });
    }
}
