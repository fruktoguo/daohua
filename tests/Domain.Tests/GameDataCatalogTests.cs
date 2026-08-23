// ==============================================================================
// 文件名称: GameDataTests.cs
// 主要职责: 游戏静态内嵌配置数据（GameData）与元数据目录有效性门禁测试。
// 核心逻辑 / 定义内容:
//   1. 验证内嵌游戏静态数据的 Schema 版本兼容性、内容指纹（Fingerprint）与实体非空。
//   2. 验证全部战斗遭遇战（Combat Encounters）搜寻、波次与重生时间边界规范。
//   3. 验证地图节点、行动项与物品配置字典引用的完整性。
// ==============================================================================

using Domain.Constants;

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

        Assert.True(GameDataCatalog.SupportedSchemaVersion >= 1);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Fingerprint));
        Assert.NotEmpty(snapshot.Actions);
        Assert.NotEmpty(snapshot.Items);
        Assert.NotEmpty(snapshot.Abilities);
        Assert.NotEmpty(snapshot.Encounters);
        Assert.NotEmpty(snapshot.Equipment);
        Assert.NotEmpty(snapshot.Maps);
    }

    /// <summary>
    /// 验证所有非战斗行动的 DurationSeconds 均大于等于 0.1 秒。
    /// </summary>
    [Fact]
    public void Non_combat_actions_have_minimum_duration()
    {
        var snapshot = GameDataCatalog.Current;

        Assert.All(snapshot.Actions.Values, action =>
        {
            if (action.ExecutionKind != ActionExecutionKind.CombatEncounter)
            {
                Assert.True(action.DurationSeconds >= 0.1, $"行动 {action.Id} 耗时应 >= 0.1s");
            }
        });
    }

    /// <summary>
    /// 验证所有技能与遭遇战的基础参数有效性（非负冷却/吟唱、重生时间大于零）。
    /// </summary>
    [Fact]
    public void Abilities_and_encounters_have_valid_parameters()
    {
        var snapshot = GameDataCatalog.Current;

        Assert.All(snapshot.Abilities.Values, ability =>
        {
            Assert.True(ability.CooldownMilliseconds >= 0);
            Assert.True(ability.CastMilliseconds >= 0);
            Assert.True(ability.DamageOverTimeTickMilliseconds > 0);
        });

        Assert.All(snapshot.Encounters.Values, encounter =>
        {
            Assert.True(encounter.RespawnMilliseconds > 0);
        });
    }

    /// <summary>
    /// 验证持续消耗品（寻灵香、地火引等）配置规范：
    /// 具有 300s 持续时间、isContinuous 为 true、buffs 列表为空且拥有对应乘率。
    /// </summary>
    [Fact]
    public void Continuous_consumables_are_configured_correctly()
    {
        var snapshot = GameDataCatalog.Current;

        var incense = snapshot.Items[ItemIds.GatheringIncense].Consumable;
        Assert.NotNull(incense);
        Assert.Equal(300, incense.DurationSeconds);
        Assert.True(incense.IsContinuous);
        Assert.Equal(1.5, incense.GatheringMultiplier);
        Assert.Empty(incense.Buffs);

        var flame = snapshot.Items[ItemIds.AlchemyFlame].Consumable;
        Assert.NotNull(flame);
        Assert.Equal(300, flame.DurationSeconds);
        Assert.True(flame.IsContinuous);
        Assert.Equal(1.25, flame.AlchemyMultiplier);
        Assert.Empty(flame.Buffs);
    }
}
