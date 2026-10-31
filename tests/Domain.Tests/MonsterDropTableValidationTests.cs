namespace Domain.Tests;

/// <summary>
/// 怪物掉落配表与解析回归门禁：
/// 1. 所有怪物掉落条目 minQuantity 必须 >= 1，maxQuantity 必须 >= minQuantity，防止出现 0-1 导致结算跳过。
/// 2. 异常掉落配置在解析阶段必须被显式拒绝，不得静默跳过。
/// </summary>
public sealed class MonsterDropTableValidationTests
{
    [Fact]
    public void AllMonstersDropTableHaveValidQuantityRange()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.NotEmpty(catalog.CombatMonsters);

        foreach (var monster in catalog.CombatMonsters.Values)
        {
            foreach (var drop in monster.DropTable)
            {
                Assert.True(drop.ItemId.IsValid, $"怪物 {monster.Id} 掉落 itemId 无效");
                Assert.True(drop.DropChance > 0 && drop.DropChance <= 1, $"怪物 {monster.Id} 掉落 {drop.ItemId} 概率无效: {drop.DropChance}");
                Assert.True(drop.MinQuantity >= 1, $"怪物 {monster.Id} 掉落 {drop.ItemId} 的 minQuantity 必须 >= 1，实际为 {drop.MinQuantity}");
                Assert.True(drop.MaxQuantity >= drop.MinQuantity, $"怪物 {monster.Id} 掉落 {drop.ItemId} 的 maxQuantity ({drop.MaxQuantity}) 必须 >= minQuantity ({drop.MinQuantity})");
            }
        }
    }
}
