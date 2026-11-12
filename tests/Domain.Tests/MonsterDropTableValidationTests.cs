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

    [Fact]
    public void DemonizedRabbitAndArmoredBear_HaveHeavySlashSkillsAndDrops()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        // 1. 魔化野兔
        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.demonized-rabbit", out var rabbit));
        Assert.Contains(rabbit.Skills, s => JsonRead.Text(s.Ability, "id", "") == "ability.heavy-slash");
        Assert.DoesNotContain(rabbit.Skills, s => JsonRead.Text(s.Ability, "id", "") == "ability.chain-strike");
        Assert.Contains(rabbit.DropTable, d => d.ItemId.Value == "item.tome.heavy-slash" && Math.Abs(d.DropChance - 0.001) < 1e-9);
        Assert.Contains(rabbit.DropTable, d => d.ItemId.Value == "item.fragment.heavy-slash" && Math.Abs(d.DropChance - 0.02) < 1e-9);
        Assert.DoesNotContain(rabbit.DropTable, d => d.ItemId.Value == "item.tome.chain-strike");
        Assert.DoesNotContain(rabbit.DropTable, d => d.ItemId.Value == "item.fragment.chain-strike");

        // 2. 铁甲熊
        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.armored-bear", out var bear));
        Assert.Contains(bear.Skills, s => JsonRead.Text(s.Ability, "id", "") == "ability.heavy-slash");
        Assert.DoesNotContain(bear.Skills, s => JsonRead.Text(s.Ability, "id", "") == "ability.chain-strike");
        Assert.Contains(bear.Skills, s => JsonRead.Text(s.Ability, "id", "") == "ability.taunt");
        Assert.Contains(bear.DropTable, d => d.ItemId.Value == "item.tome.heavy-slash" && Math.Abs(d.DropChance - 0.005) < 1e-9);
        Assert.Contains(bear.DropTable, d => d.ItemId.Value == "item.fragment.heavy-slash" && Math.Abs(d.DropChance - 0.02) < 1e-9);
        Assert.DoesNotContain(bear.DropTable, d => d.ItemId.Value == "item.tome.chain-strike");
        Assert.DoesNotContain(bear.DropTable, d => d.ItemId.Value == "item.fragment.chain-strike");
    }

    /// <summary>
    /// TASK-20261112-08：木灵/火灵在默认难度（难度倍率 1.0）满蓝仅可施放一次技能。
    /// 怪物无灵力回复且开战满蓝，manaCost ≤ MaxManapoints &lt; 2×manaCost 即恰好一次；
    /// MaxManapoints 断言锁定怪我同构派生底座，防止期望面板或权重漂移改变施法次数。
    /// </summary>
    [Fact]
    public void WoodAndFireSpirits_CastSkillOnlyOnceAtFullMana()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.wood-spirit", out var wood));
        var palmCost = JsonRead.Number(wood.Skills[0].Ability, "manaCost", 0);
        Assert.True(wood.Stats.MaxManapoints >= palmCost && wood.Stats.MaxManapoints < palmCost * 2,
            $"木灵 MP={wood.Stats.MaxManapoints} 应落在 [{palmCost}, {palmCost * 2}) 以恰好施放一次掌心雷。");
        Assert.Equal(7, wood.Stats.MaxManapoints, 0);

        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.fire-spirit", out var fire));
        var blastCost = JsonRead.Number(fire.Skills[0].Ability, "manaCost", 0);
        Assert.True(fire.Stats.MaxManapoints >= blastCost && fire.Stats.MaxManapoints < blastCost * 2,
            $"火灵 MP={fire.Stats.MaxManapoints} 应落在 [{blastCost}, {blastCost * 2}) 以恰好施放一次轰炎。");
        Assert.Equal(17, fire.Stats.MaxManapoints, 0);
    }
}
