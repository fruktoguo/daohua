using System.Text.Json;
using Domain.Combat;
using Domain.GameData;
using Domain.Player;
using Domain.World;

namespace Domain.Tests;

/// <summary>
/// 「属性输入修订号」缓存与 Duel/Loadout 导出缓存门禁：
/// ①缓存命中必须与全量重算逐字段、逐字节一致；
/// ②Resolve 全部输入源（境界/装备/六维/Buff/节点建筑效果/配表实例）的任一写入路径必须令缓存失效；
/// ③非属性输入的写入不得误伤缓存（面板兜底字段、功法槽、账本等）。
/// </summary>
public sealed class AttributeRevisionCacheTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ItemId TestEquipment = new("equipment.test");
    private static readonly ItemId TestItem = new("item.test");
    private static readonly AbilityId TestAbility = new("ability.test");

    /// <summary>最小可用配表：两档境界锚点 + 一件带机制点数的装备 + 一个采集行动。</summary>
    private static GameDataCatalog CreateCatalog()
    {
        using var equipment = JsonDocument.Parse("""
            {"flatStats":{"physicalAttack":50,"physicalArmor":20},
             "specialMechanics":{"attackSpeedPoints":10,"physicalDamageBonusPoints":5}}
            """);
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.gather"), "采集", ActionKind.Exploration, 2,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, TestItem)])
        ]);
        return new GameDataCatalog("test", "test", actions,
            baseAttributes: new BaseAttributeCatalog([
                new(1, 1000, 100, 10, 5, 10, 8, 100, 5, 0.1, 0.1, 0.2, 0.15, 7, 3),
                new(2, 2000, 200, 20, 10, 20, 16, 200, 10, 0.2, 0.2, 0.4, 0.3, 14, 6)]),
            rawEquipments: new Dictionary<string, JsonElement> { [TestEquipment.Value] = equipment.RootElement });
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "测试", Baseline, catalog.Fingerprint);

    /// <summary>两路径位级一致性：record 全字段相等（含 init-only 点数与嵌套 Effects）+ 序列化字节逐位相等。</summary>
    private static void AssertAttributesEquivalent(PlayerAttributes expected, PlayerAttributes actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    /// <summary>断言：聚合缓存路径结果与快照全量重算路径逐位一致，且二次调用命中同实例缓存。</summary>
    private static PlayerAttributes AssertConsistentResolve(PlayerAggregate player, GameDataCatalog catalog)
    {
        var viaAggregate = PlayerAttributeResolver.Resolve(player, catalog);
        AssertAttributesEquivalent(PlayerAttributeResolver.Resolve(player.ExportState(), catalog), viaAggregate);
        Assert.Same(viaAggregate, PlayerAttributeResolver.Resolve(player, catalog));
        return viaAggregate;
    }

    /// <summary>无输入变更时：Resolve 与 Apply 均命中缓存实例，且与快照重算路径逐位一致。</summary>
    [Fact]
    public void UnchangedInputsHitCacheAndMatchSnapshotResolve()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var first = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Same(first, PlayerAttributeResolver.Resolve(player, catalog));
        Assert.Same(first, PlayerAttributeResolver.Apply(player, catalog));
        AssertAttributesEquivalent(PlayerAttributeResolver.Resolve(player.ExportState(), catalog), first);
    }

    /// <summary>Buff 增/删/清空/比率通道全部令缓存失效，命中路径仍与重算逐位一致。</summary>
    [Fact]
    public void BuffMutationsInvalidateCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var baseline = AssertConsistentResolve(player, catalog);
        var expiry = Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d;

        player.Buffs.Active.Add(new ActiveBuff("buff.speed", expiry, 2, "疾风", null, 0, 0, 25, 15));
        var afterAdd = AssertConsistentResolve(player, catalog);
        Assert.NotSame(baseline, afterAdd);
        Assert.Equal(50, afterAdd.AttackSpeedPoints);
        Assert.Equal(30, afterAdd.DamageAbsorptionPoints);

        player.Buffs.Active.Add(new ActiveBuff("buff.power", expiry, 1, "蛮力", null, 0, 0, 0, 0,
            new Dictionary<string, double> { ["physicalAttack"] = 0.5 }));
        var afterRatio = AssertConsistentResolve(player, catalog);
        Assert.Equal(baseline.PhysicalAttack * 1.5, afterRatio.PhysicalAttack);

        player.Buffs.Active.RemoveAll(buff => buff.BuffId == "buff.speed");
        var afterRemove = AssertConsistentResolve(player, catalog);
        Assert.Equal(0, afterRemove.AttackSpeedPoints);
        Assert.Equal(baseline.PhysicalAttack * 1.5, afterRemove.PhysicalAttack);

        player.Buffs.Active.Clear();
        var cleared = AssertConsistentResolve(player, catalog);
        Assert.Equal(baseline.PhysicalAttack, cleared.PhysicalAttack);
    }

    /// <summary>Buff 过期移除（引擎推进路径）同样令缓存失效。</summary>
    [Fact]
    public void BuffExpiryThroughEngineInvalidatesCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var expiry = Baseline.AddSeconds(7).ToUnixTimeMilliseconds() / 1000d;
        player.Buffs.Active.Add(new ActiveBuff("buff.fading", expiry, 1, "易逝", null, 0, 0, 25, 0));
        var withBuff = AssertConsistentResolve(player, catalog);
        Assert.Equal(25, withBuff.AttackSpeedPoints);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog);
        var expired = AssertConsistentResolve(player, catalog);
        Assert.NotSame(withBuff, expired);
        Assert.Equal(0, expired.AttackSpeedPoints);
    }

    /// <summary>穿戴、强化与卸下装备均令缓存失效；flatStats 按强化缩放、specialMechanics 不缩放。</summary>
    [Fact]
    public void EquipmentMutationsInvalidateCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var baseline = AssertConsistentResolve(player, catalog);

        player.ReplaceLoadout([], [], [TestEquipment]);
        var equipped = AssertConsistentResolve(player, catalog);
        Assert.NotSame(baseline, equipped);
        Assert.Equal(baseline.PhysicalAttack + 50, equipped.PhysicalAttack);
        Assert.Equal(baseline.Armor + 20, equipped.Armor);
        Assert.Equal(10, equipped.AttackSpeedPoints);
        Assert.Equal(5, equipped.PhysicalDamageBonusPoints);

        // 强化 +5：flatStats ×1.1，specialMechanics 点数不经强化缩放。
        player.ReplaceCombatSlots([new PlayerAbilitySlot(TestAbility.Value, 1, [])],
            [new PlayerEquipmentSlot(TestEquipment.Value, 5)]);
        var enhanced = AssertConsistentResolve(player, catalog);
        Assert.NotSame(equipped, enhanced);
        Assert.Equal(baseline.PhysicalAttack + 55, enhanced.PhysicalAttack);
        Assert.Equal(10, enhanced.AttackSpeedPoints);

        player.ReplaceLoadout([], [], []);
        var stripped = AssertConsistentResolve(player, catalog);
        Assert.NotSame(enhanced, stripped);
        Assert.Equal(baseline.PhysicalAttack, stripped.PhysicalAttack);
    }

    /// <summary>六维经验（RestoreFrom 路径）、境界突破、节点建筑效果与跨节点失效全部触发重算。</summary>
    [Fact]
    public void CoreAttributesRealmAndNodeEffectsInvalidateCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var baseline = AssertConsistentResolve(player, catalog);

        // 六维：体力/筋骨经验经同实例恢复路径写入（根骨等级 → 生命/护甲加成）。
        var doc = player.ExportState();
        player.RestoreFrom(doc with
        {
            Loadout = doc.Loadout with
            {
                CoreAttributes = new PlayerCoreAttributes(StaminaExp: 1_000_000, BoneExp: 1_000_000)
            }
        });
        var core = AssertConsistentResolve(player, catalog);
        Assert.NotSame(baseline, core);
        Assert.True(core.MaxHitpoints > baseline.MaxHitpoints);
        Assert.True(core.Armor > baseline.Armor);

        // 突破：RealmLevel → 锚点档位切换。
        player.Breakthrough(null);
        var breakthrough = AssertConsistentResolve(player, catalog);
        Assert.NotSame(core, breakthrough);
        Assert.Equal(2, player.Progression.RealmLevel);

        // 节点建筑投影归属当前节点：回复固定值与减伤比率并入属性。
        player.World.NodeBuildingNodeId = player.World.NodeId;
        player.World.NodeBuildingEffects = new BuildingEffectSet(CombatHitpointRegen: 50, CombatDamageReductionRatio: 0.1);
        var buffed = AssertConsistentResolve(player, catalog);
        Assert.NotSame(breakthrough, buffed);
        Assert.Equal(breakthrough.HpRegen + 50, buffed.HpRegen);
        Assert.Equal(10, buffed.DamageAbsorptionPoints);

        // 换节点：投影归属不一致 → 效果集回空，属性回落。
        player.World.NodeId = "node.other";
        var moved = AssertConsistentResolve(player, catalog);
        Assert.NotSame(buffed, moved);
        Assert.Equal(breakthrough.HpRegen, moved.HpRegen);
        Assert.Equal(0, moved.DamageAbsorptionPoints);
    }

    /// <summary>非属性输入的写入不得误伤缓存：面板当前值、修为/经验、行动队列、功法习得与参悟、暂存区、设置。</summary>
    [Fact]
    public void NonInputMutationsKeepCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var cached = AssertConsistentResolve(player, catalog);

        player.Resources.Hitpoints = 42;              // 面板当前值是 Apply 写回目标，不是解析输入
        player.Resources.Manapoints = 7;
        player.Progression.Qi = 123;               // 修为/经验不直接参与属性解析（RealmLevel 才是）
        player.Progression.Experience = 456;
        player.World.X = 5;                        // 坐标不参与解析
        player.Duel.EncounterCharges = 3;          // 斗法标量不参与解析
        player.LearnAbility(TestAbility);          // 功法槽与已习得列表不参与解析
        player.GrantAbilityExperience(TestAbility, 10); // 技能经验不参与解析
        player.StageItem(TestItem, 2, Baseline.ToUnixTimeMilliseconds() / 1000d); // 暂存区+账本记账
        player.ConfigureAutoUse("combat", 0, TestItem.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), -1), Baseline, catalog.Actions);

        Assert.Same(cached, PlayerAttributeResolver.Resolve(player, catalog));
        Assert.Same(cached, PlayerAttributeResolver.Apply(player, catalog));
    }

    /// <summary>配表实例切换（同内容不同对象）必须重算：引用相等守卫防止跨配表脏命中。</summary>
    [Fact]
    public void CatalogInstanceChangeRecomputes()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var first = PlayerAttributeResolver.Resolve(player, catalog);

        var reloaded = CreateCatalog();
        var resolved = PlayerAttributeResolver.Resolve(player, reloaded);
        Assert.NotSame(first, resolved);
        AssertAttributesEquivalent(first, resolved); // 同配表内容 → 结果逐位一致但为新实例
        Assert.Same(resolved, PlayerAttributeResolver.Resolve(player, reloaded));
    }

    /// <summary>catalog.BaseAttributes 缺失时面板由 Combat 兜底：该路径不经修订号覆盖，旁路缓存逐次重算。</summary>
    [Fact]
    public void MissingBaseAttributesBypassesCache()
    {
        var catalog = new GameDataCatalog("test", "test", new ActionCatalog([]));
        var player = CreatePlayer(catalog);
        var first = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(player.Resources.MaxHitpoints, first.MaxHitpoints);
        player.Resources.MaxHitpoints = 777; // 兜底输入变化必须立刻反映（不走修订号）
        var second = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(777, second.MaxHitpoints);
        Assert.NotSame(first, second);
    }

    /// <summary>Loadout 导出缓存：未变更复用同文档实例，任一装配写入触发重建，字节与恢复聚合逐位一致。</summary>
    [Fact]
    public void LoadoutExportCachesDocumentUntilMutation()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var d0 = player.ExportState().Loadout;
        Assert.Same(d0, player.ExportState().Loadout);

        // 功法槽变化（习得初始化 4 空槽）→ 重建
        player.LearnAbility(TestAbility);
        var d1 = player.ExportState().Loadout;
        Assert.NotSame(d0, d1);
        Assert.Same(d1, player.ExportState().Loadout);
        Assert.Equal(4, d1.AbilitySlots.Count);

        // 技能经验 → 重建；能力槽位触发器仅在装配变更时克隆，未变槽位语义保持
        player.GrantAbilityExperience(TestAbility, 10);
        var d2 = player.ExportState().Loadout;
        Assert.NotSame(d1, d2);
        Assert.Equal(10, d2.AbilityExperiences![TestAbility.Value]);
        Assert.Same(d2, player.ExportState().Loadout);

        // 六维经验（RestoreFrom）→ 重建
        var doc = player.ExportState();
        player.RestoreFrom(doc with { Loadout = doc.Loadout with { CoreAttributes = new(123, 0, 0, 0, 0, 0) } });
        var d3 = player.ExportState().Loadout;
        Assert.NotSame(d2, d3);

        // 触发器克隆仅在装配变更时发生：连续两次导出复用同文档（同 JsonElement 实例）。
        var t1 = player.ExportState().Loadout.AbilitySlots[0].Triggers;
        var t2 = player.ExportState().Loadout.AbilitySlots[0].Triggers;
        Assert.Same(t1, t2);

        // 字节逐位一致：独立恢复的聚合首轮导出（无缓存命中）与缓存路径序列化字节相同。
        var cachedBytes = JsonSerializer.SerializeToUtf8Bytes(player.ExportState().Loadout);
        var restored = PlayerAggregate.Restore(player.ExportState());
        var freshBytes = JsonSerializer.SerializeToUtf8Bytes(restored.ExportState().Loadout);
        Assert.Equal(cachedBytes, freshBytes);
    }

    /// <summary>斗法导出缓存：结构与队首账本未变时整文档复用；仅被写账本重建，干净账本逐位复用。</summary>
    [Fact]
    public void DuelExportCachesDocumentAndReusesCleanReports()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var d0 = player.ExportState().Duel;
        Assert.Same(d0, player.ExportState().Duel);

        // 结构变化（暂存区入物）→ 整文档重建
        player.StageItem(TestItem, 2, Baseline.ToUnixTimeMilliseconds() / 1000d);
        var d1 = player.ExportState().Duel;
        Assert.NotSame(d0, d1);
        Assert.Single(d1.StagedItems);
        Assert.Same(d1, player.ExportState().Duel);

        // 纯账本写入（参悟记账）：结构未变但 DailyReport 脏 → 重建；
        // 未脏账本（OfflineReport 对象/文档实例）逐位复用，DailyReport 文档换新。
        player.LearnAbility(TestAbility);
        player.GrantAbilityExperience(TestAbility, 10);
        var d2 = player.ExportState().Duel;
        Assert.NotSame(d1, d2);
        Assert.Same(d1.OfflineReport, d2.OfflineReport);   // 未记账账本复用同文档实例
        Assert.NotSame(d1.DailyReport, d2.DailyReport);    // 被写账本重建
        Assert.Equal(10, d2.DailyReport!.AbilityExp!.Sum(p => p.Experience));

        // 归档账本复用：一次性行动完成归档 → 再次记账只重建被写账本，归档文档实例不变。
        player.SetLoopEnabled(false);
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), catalog);
        var d3 = player.ExportState().Duel;
        Assert.Single(d3.Reports);
        player.GrantAbilityExperience(TestAbility, 5);
        var d4 = player.ExportState().Duel;
        Assert.NotSame(d3, d4);
        Assert.Same(d3.Reports[0], d4.Reports[0]);         // 归档账本未脏 → 文档逐位复用

        // 字节逐位一致：恢复聚合首轮导出与缓存路径序列化字节相同，且字典序/列表序保持。
        var cachedBytes = JsonSerializer.SerializeToUtf8Bytes(player.ExportState().Duel);
        var restored = PlayerAggregate.Restore(player.ExportState());
        var freshBytes = JsonSerializer.SerializeToUtf8Bytes(restored.ExportState().Duel);
        Assert.Equal(cachedBytes, freshBytes);
    }

    /// <summary>斗法标量写入（遭遇计数/冷却/进行中斗法）均令导出缓存失效。</summary>
    [Fact]
    public void DuelScalarWritesInvalidateExportCache()
    {
        var catalog = CreateCatalog();
        var player = CreatePlayer(catalog);
        var d0 = player.ExportState().Duel;

        player.Duel.EncounterCharges = 2;
        var d1 = player.ExportState().Duel;
        Assert.NotSame(d0, d1);
        Assert.Equal(2, d1.EncounterCharges);

        player.Duel.CooldownUntilUnixSeconds = 123.5;
        Assert.NotSame(d1, player.ExportState().Duel);

        player.Duel.ActiveDuelId = Guid.NewGuid().ToString("N");
        Assert.NotSame(player.ExportState().Duel, d1);
        Assert.Same(player.ExportState().Duel, player.ExportState().Duel);
    }
}
