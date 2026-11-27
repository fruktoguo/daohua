using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Systems;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 新战斗流派（阵法大师、符修四特质与五新符箓、蓄剑藏锋、护盾卖血与迸裂、天罡断岳、闪身步）全链路确定性回归门禁。
/// </summary>
[Collection("ECS telemetry")]
public sealed class NewBuildArchetypeCombatTests
{
    private static CombatantStats Stats(double hp = 1000, double attack = 100, double interval = 100)
        => CombatantStats.Create(hp, attack, 0, interval) with { Accuracy = 1_000_000, Evasion = 0 };

    [Fact]
    public void NineNewTraits_MetadataModifiersAndScalingRulesMatchDesign()
    {
        var expected = new (string Id, int Quality, int Cost)[]
        {
            (CombatTraitRules.ZhenfadashiTraitId, 5, 16),
            (CombatTraitRules.FufajiTraitId, 5, 16),
            (CombatTraitRules.FufapozhenTraitId, 4, 12),
            (CombatTraitRules.FufatiangangTraitId, 3, 8),
            (CombatTraitRules.FufatianmingTraitId, 5, 16),
            (CombatTraitRules.YangjianshuTraitId, 5, 16),
            (CombatTraitRules.CangfengTraitId, 4, 12),
            (CombatTraitRules.XueqihuadunTraitId, 6, 20),
            (CombatTraitRules.ShanshenbuTraitId, 5, 15),
        };
        foreach (var (id, quality, cost) in expected)
        {
            var def = CombatTraitRules.AllTraits[id];
            Assert.Equal(quality, def.Quality);
            Assert.Equal(cost, def.Cost);
        }

        // 阵法大师：Lv.3 正向 +220 点阵法增伤，负面 -500 技能与普攻增幅锁定不放大
        var zfRatios = new Dictionary<string, double>();
        var zfPoints = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(
            [CombatTraitRules.ZhenfadashiTraitId],
            new Dictionary<string, int> { [CombatTraitRules.ZhenfadashiTraitId] = 3 },
            zfRatios,
            zfPoints);
        Assert.Equal(220.0, zfPoints["formationDamageBonusPoints"], 12);
        Assert.Equal(-500.0, zfPoints["skillBonusPoints"], 12);
        Assert.Equal(-500.0, zfPoints["basicAttackBonusPoints"], 12);

        // 符法·疾：Lv.2 冷却加速 420 点，灵力消耗倍率锁定 ×4.0，技能与普攻惩罚锁定 -500
        var ffjRatios = new Dictionary<string, double>();
        var ffjPoints = new Dictionary<string, double>();
        var ffjLevels = new Dictionary<string, int> { [CombatTraitRules.FufajiTraitId] = 2 };
        CombatTraitRules.AccumulateModifiers([CombatTraitRules.FufajiTraitId], ffjLevels, ffjRatios, ffjPoints);
        Assert.Equal(-500.0, ffjPoints["skillBonusPoints"], 12);
        Assert.Equal(-500.0, ffjPoints["basicAttackBonusPoints"], 12);
        Assert.Equal(420.0, CombatTraitRules.TalismanCooldownSpeedPoints([CombatTraitRules.FufajiTraitId], ffjLevels), 12);
        Assert.Equal(4.0, CombatTraitRules.TalismanManaCostMultiplier([CombatTraitRules.FufajiTraitId]), 12);

        // 闪身步：Lv.1 冷却 3.0s，Lv.20 反比例缩至 1.5s
        Assert.Equal(3.0, CombatTraitRules.ShanshenbuCooldownSeconds([CombatTraitRules.ShanshenbuTraitId]), 12);
        Assert.Equal(
            1.5,
            CombatTraitRules.ShanshenbuCooldownSeconds(
                [CombatTraitRules.ShanshenbuTraitId],
                new Dictionary<string, int> { [CombatTraitRules.ShanshenbuTraitId] = 20 }),
            12);

        // 非永久同 ID Buff 持续时间取较长者保留
        Assert.Equal(20.0, CombatTraitRules.MergeBuffExpiry(20.0, 10.0), 12);
        Assert.Equal(15.0, CombatTraitRules.MergeBuffExpiry(8.0, 15.0), 12);
    }

    [Fact]
    public void Xueqihuadun_ConvertsExcessHpOnStartAndEveryFiveSecondsTriggersXuyiAndHealsAtBattleEnd()
    {
        var playerStats = Stats(hp: 1000, attack: 500, interval: 1.0) with
        {
            Traits = [CombatTraitRules.XueqihuadunTraitId, CombatTraitRules.XuyiTraitId],
            Effects = new CombatEffectProfile(HealingBonusPoints: 50)
        };
        var battle = CombatEventAdvanceSystem.Start("xueqi-wave", 101, playerStats, Stats(hp: 300, attack: 0));

        // 开战即刻将超出 20% 生命的部分（800 点）作为受击转化为护盾，并计入蓄意层数
        Assert.Equal(200.0, battle.Player.Hitpoints, 9);
        Assert.Equal(800.0, battle.Player.Shield, 9);
        Assert.Equal(800.0, battle.Player.BuffStacks(CombatTraitRules.XuyiBuffId), 9);

        var endRes = CombatEventAdvanceSystem.AdvanceTo(battle, 5.0);
        Assert.Equal(CombatOutcome.PlayerWon, endRes.Outcome);
        // 战斗结束剩余 800 护盾乘 1.5（+50 治疗增幅）转为治疗，回满至 1000，护盾归零
        Assert.Equal(1000.0, battle.Player.Hitpoints, 9);
        Assert.Equal(0.0, battle.Player.Shield, 9);
        Assert.Contains(endRes.Events, e => e.AbilityId == CombatTraitRules.XueqihuadunTraitId && e.Healing > 0);

        // 验证每 5 秒周期：生命高于 20% 时继续转化为永久护盾
        var tickStats = Stats(hp: 1000, attack: 0, interval: 100) with
        {
            HpRegen = 300,
            Traits = [CombatTraitRules.XueqihuadunTraitId],
            TraitLevels = new Dictionary<string, int> { [CombatTraitRules.XueqihuadunTraitId] = 3 } // 110% 转化率
        };
        var tickBattle = CombatEventAdvanceSystem.Start("xueqi-tick", 102, tickStats, Stats(hp: 10_000, attack: 0));
        Assert.Equal(200.0, tickBattle.Player.Hitpoints, 9);
        Assert.Equal(880.0, tickBattle.Player.Shield, 9); // 800 * 1.1
        CombatEventAdvanceSystem.AdvanceTo(tickBattle, 5.1);
        // 5s 自然回复 +300 至 500 后立即把超出 200 的 300 点按 110% 转为 +330 护盾
        Assert.Equal(200.0, tickBattle.Player.Hitpoints, 9);
        Assert.Equal(1210.0, tickBattle.Player.Shield, 9);
    }

    [Fact]
    public void GangqiHutiAndBenglie_GrantShieldDeductHpCostAndConsumeAllShieldIntoAoEDamage()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var gangqi = catalog.Abilities["ability.gangqi-huti"];
        var benglie = catalog.Abilities["ability.benglie"];

        var caster = Stats(hp: 2000, attack: 100, interval: 100) with { MaxManapoints = 500 };
        var dummy = Stats(hp: 20_000, attack: 0, interval: 100);
        var battle = CombatEventAdvanceSystem.Start(
            "benglie-test",
            103,
            new CombatantTemplate(caster, [new SkillSlot(gangqi, 1, []), new SkillSlot(benglie, 1, [])]),
            [new CombatantTemplate(dummy), new CombatantTemplate(dummy)]);

        CombatEventAdvanceSystem.AdvanceTo(battle, 1.05);
        Assert.Equal(400.0, battle.Player.Shield, 9);
        Assert.Equal(1900.0, battle.Player.Hitpoints, 9); // 迸裂起手扣除 5% MaxHP (100)

        var res = CombatEventAdvanceSystem.AdvanceTo(battle, 2.6);
        Assert.Equal(0.0, battle.Player.Shield, 9);
        var hits = res.Events.Where(e => e.AbilityId == "ability.benglie").ToArray();
        Assert.Equal(2, hits.Length);
        var expectedDmg = (750.0 * 750.0) / 751.0; // 100 AD + 200 (10% MaxHP) + 400 (消耗护盾) + 50
        foreach (var hit in hits)
            Assert.Equal(expectedDmg, hit.Damage, 6);
    }

    [Fact]
    public void TiangangDuanyue_CountsIndependentAttackActionsDuringChannelAndAppliesPostBuffMultiplier()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var tiangang = catalog.Abilities["ability.tiangang-duanyue"];
        using var multiHitDoc = JsonDocument.Parse("""
        {
          "id": "test.multihit",
          "name": "五连斩",
          "category": "physical",
          "element": "none",
          "targetType": "targetEnemy",
          "cooldownMilliseconds": 10000,
          "castMilliseconds": 1000,
          "manaCost": 0,
          "damageRatio": 0.1,
          "flatDamage": 1,
          "hitCount": 5
        }
        """);

        var caster = Stats(hp: 10_000, attack: 100, interval: 100) with { MaxManapoints = 500 };
        var attacker = Stats(hp: 50_000, attack: 10, interval: 2.0);
        var battle = CombatEventAdvanceSystem.Start(
            "tiangang-test",
            104,
            new CombatantTemplate(caster, [new SkillSlot(tiangang, 1, [])]),
            [new CombatantTemplate(attacker, [new SkillSlot(multiHitDoc.RootElement.Clone(), 1, [])])]);

        CombatEventAdvanceSystem.AdvanceTo(battle, 1.1); // 1.0s 敌人 5 连击只记 1 次攻击行为 -> 1 层天罡
        Assert.Equal(1.0, battle.Player.BuffStacks(CombatTraitRules.TiangangBuffId), 9);

        CombatEventAdvanceSystem.AdvanceTo(battle, 3.1); // 3.0s 敌人普攻 1 次 -> 2 层天罡
        Assert.Equal(2.0, battle.Player.BuffStacks(CombatTraitRules.TiangangBuffId), 9);

        var res = CombatEventAdvanceSystem.AdvanceTo(battle, 5.1);
        var hit = Assert.Single(res.Events, e => e.AbilityId == "ability.tiangang-duanyue");
        var expectedDmg = ((400.0 * 400.0) / 401.0) * 1.2 * 1.4; // 先吃 2 层天罡 +20% 增伤，再乘 (1 + 0.2*2)
        Assert.Equal(expectedDmg, hit.Damage, 6);
        Assert.Equal(0.0, battle.Player.BuffStacks(CombatTraitRules.TiangangBuffId), 9);
    }

    [Fact]
    public void YangjianshuAndCangfengAndShanshenbu_BehaveDeterministically()
    {
        using var slashDoc = JsonDocument.Parse("""
        {
          "id": "test.slash",
          "name": "拔剑",
          "category": "physical",
          "element": "metal",
          "targetType": "targetEnemy",
          "cooldownMilliseconds": 10000,
          "castMilliseconds": 500,
          "manaCost": 0,
          "damageRatio": 1.0,
          "flatDamage": 0
        }
        """);
        using var jianyiTriggerDoc = JsonDocument.Parse("""
        [{"dependency":"self","condition":"buff-stacks","buffId":"buff.jianyi","comparator":"greater-than-equal","value":2}]
        """);
        var triggers = jianyiTriggerDoc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();

        var cangfengCaster = Stats(hp: 5000, attack: 100, interval: 0.5) with
        {
            Traits = [CombatTraitRules.YangjianshuTraitId, CombatTraitRules.CangfengTraitId],
            SkillBonusPoints = 20
        };
        var cfBattle = CombatEventAdvanceSystem.Start(
            "yangjian-cangfeng",
            105,
            new CombatantTemplate(cangfengCaster, [new SkillSlot(slashDoc.RootElement.Clone(), 1, triggers)]),
            [new CombatantTemplate(Stats(hp: 20_000, attack: 0))]);

        var waitRes = CombatEventAdvanceSystem.AdvanceTo(cfBattle, 2.05);
        Assert.DoesNotContain(waitRes.Events, e => e.AttackerId == "player");
        Assert.Equal(2.0, cfBattle.Player.BuffStacks(CombatTraitRules.JianyiBuffId), 9);
        Assert.Equal(160.0, cfBattle.Player.EffectiveStats.AttackPower, 9);

        var slashRes = CombatEventAdvanceSystem.AdvanceTo(cfBattle, 2.6);
        Assert.Single(slashRes.Events, e => e.AttackerId == "player" && e.AbilityId == "test.slash");
        Assert.Equal(0.0, cfBattle.Player.BuffStacks(CombatTraitRules.JianyiBuffId), 9);

        // 闪身步 Lv.20（1.5s 冷却）：1.0s 闪避触发 -> 2.0s 冷却内不触发 -> 3.0s 再次触发
        var dodgeCaster = Stats(hp: 5000, attack: 10, interval: 100) with
        {
            Evasion = 1_000_000,
            Traits = [CombatTraitRules.ShanshenbuTraitId],
            TraitLevels = new Dictionary<string, int> { [CombatTraitRules.ShanshenbuTraitId] = 20 }
        };
        var poker = Stats(hp: 5000, attack: 10, interval: 1.0) with { Accuracy = 1 };
        var ssbBattle = CombatEventAdvanceSystem.Start("shanshenbu-test", 106, dodgeCaster, poker);
        CombatEventAdvanceSystem.AdvanceTo(ssbBattle, 1.05);
        Assert.Equal(1.0, ssbBattle.Player.BuffStacks(CombatTraitRules.YixingBuffId), 9);
        CombatEventAdvanceSystem.AdvanceTo(ssbBattle, 2.05);
        Assert.Equal(1.0, ssbBattle.Player.BuffStacks(CombatTraitRules.YixingBuffId), 9);
        CombatEventAdvanceSystem.AdvanceTo(ssbBattle, 3.05);
        Assert.Equal(2.0, ssbBattle.Player.BuffStacks(CombatTraitRules.YixingBuffId), 9);
    }

    [Fact]
    public void FiveTalismansAndFufaTianming_ExecuteFromAbilitySlotsScaleCooldownManaAndPreserveLongerInvisibility()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var start = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        var playerId = PlayerId.New();
        var player = PlayerAggregate.Create(playerId, AccountId.New(), "天命符师", start, catalog.Fingerprint);
        player.Progression.RealmLevel = 50; // 充足道韵点数容纳全部测试特质（共需 64 点）
        foreach (var tid in new[]
        {
            CombatTraitRules.FufatianmingTraitId,
            CombatTraitRules.FufajiTraitId,
            CombatTraitRules.FufapozhenTraitId,
            CombatTraitRules.FufatiangangTraitId,
            "trait.bukandaowo"
        })
        {
            player.Progression.UnlockedTraits[tid] = new(tid, tid == "trait.bukandaowo" ? 5 : 1, 0);
        }
        player.Progression.SetTraits([
            CombatTraitRules.FufatianmingTraitId,
            CombatTraitRules.FufajiTraitId,
            CombatTraitRules.FufapozhenTraitId,
            CombatTraitRules.FufatiangangTraitId,
            "trait.bukandaowo" // Lv.5 开战隐身 12s，验证隐身符 10s 不缩短已有 12s
        ]);
        var resolved = PlayerAttributeResolver.Resolve(player, catalog);
        player.Resources.MaxHitpoints = resolved.MaxHitpoints;
        player.Resources.Hitpoints = resolved.MaxHitpoints;
        player.Resources.MaxManapoints = resolved.MaxManapoints;
        player.Resources.Manapoints = resolved.MaxManapoints;

        // 主动术法槽装配剑符、怒焰符、定神符；普通消耗品栏装配神行符、隐身符
        player.Loadout.AbilitySlots.Clear();
        player.Loadout.AbilitySlots.Add(new(null, 1));
        player.Loadout.AbilitySlots.Add(new("item.jian-talisman", 1));
        player.Loadout.AbilitySlots.Add(new("item.nuyan-talisman", 1));
        player.Loadout.AbilitySlots.Add(new("item.dingshen-talisman", 1));
        player.Settings.AutoUseSlots.Clear();
        player.Settings.AutoUseSlots.Add(new(AutoUseBar.Combat, 0, "item.shenxing-talisman", 10, true, Group: AutoUseSlotState.SlotGroupGeneral));
        player.Settings.AutoUseSlots.Add(new(AutoUseBar.Combat, 1, "item.yinshen-talisman", 10, true, Group: AutoUseSlotState.SlotGroupGeneral));
        // 显式清空隐身符默认「无隐身时施放」条件，强制在开战已有 12s 隐身时重叠施放 10s 隐身符以验证保高不降
        player.Loadout.ConsumableTriggers["item.yinshen-talisman"] = [];

        var monsterId = catalog.CombatMonsters.Keys.First();
        var foeStats = Stats(hp: 50_000, attack: 10, interval: 100) with { Evasion = 50, ParryResolve = 80 };
        var battle = CombatEventAdvanceSystem.Start(
            "five-talismans",
            107,
            new CombatantTemplate(resolved.ToCombatant(player.Progression.CombatExperience)),
            [
                new CombatantTemplate(foeStats, DefinitionId: monsterId),
                new CombatantTemplate(foeStats, DefinitionId: monsterId)
            ]);
        battle.Player.Hitpoints = resolved.MaxHitpoints;
        battle.Player.Manapoints = resolved.MaxManapoints;
        battle.Enemies[0].DefinitionId = monsterId;
        battle.Enemies[1].DefinitionId = monsterId;

        var action = player.EnqueuePriority(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), start, catalog.Actions);
        action.Expedition = new CombatExpeditionState(
            "five-talismans", CombatExpeditionPhase.Combat, 0, 0, 107, 0,
            battle.Player.Hitpoints, battle.Player.Manapoints, battle.Player.NextAttackAtSeconds,
            battle.Player.AttackCount, battle.Player.Threat, CombatOutcome.Running, 1,
            [
                new ExpeditionEnemyState(monsterId, CombatEncounterState.Snapshot(battle.Enemies[0])),
                new ExpeditionEnemyState(monsterId, CombatEncounterState.Snapshot(battle.Enemies[1]))
            ])
        {
            PlayerState = CombatEncounterState.Snapshot(battle.Player)
        };

        var wallet = new AssetWallet(playerId);
        foreach (var itemId in new[]
        {
            "item.jian-talisman",
            "item.nuyan-talisman",
            "item.dingshen-talisman",
            "item.shenxing-talisman",
            "item.yinshen-talisman"
        })
        {
            wallet.Apply(new AssetDelta(playerId, new AssetKey(new ItemId(itemId)), 20, "test"));
        }

        // 开战时由「看不到我」Lv.5 获得 12s 隐身；使用隐身符（10s）后到期时间必须保留 12s 不被缩短
        Assert.Equal(12.0, battle.Player.Buffs.Single(b => b.BuffId == "buff.yinshen").ExpiresAtSeconds, 9);

        var events = new SimulationEngine.SimulationEventTail();
        // 首轮满蓝：符法·疾使符箓耗蓝 ×4，神行符(20%) + 隐身符(40%) + 剑符(40%) 恰好耗尽 100% 灵力（验证 ×4 耗蓝门禁）
        var spentFirst = ActionExecutionRules.ActivateInstantConsumables(player, catalog, wallet, start, events, liveState: battle);
        Assert.Equal(3, spentFirst.Count);
        Assert.Equal(0.0, player.Resources.Manapoints, 6);
        Assert.Equal(12.0, battle.Player.Buffs.Single(b => b.BuffId == "buff.yinshen").ExpiresAtSeconds, 9);
        Assert.Equal(10.0, battle.Player.EffectiveStats.ActionSpeedPoints, 9);
        Assert.Equal(start.ToUnixTimeMilliseconds() / 1000.0 + 12.0, player.Combat.TalismanCooldowns["item.jian-talisman"], 6);

        // 补满灵力后触发剩余两张术法槽符箓：怒焰符(40%) + 定神符(20%) = 消耗 60% 灵力，剩余 40%
        player.Resources.Manapoints = resolved.MaxManapoints;
        battle.Player.Manapoints = resolved.MaxManapoints;
        var spentSecond = ActionExecutionRules.ActivateInstantConsumables(player, catalog, wallet, start, events, liveState: battle);
        Assert.Equal(2, spentSecond.Count);
        Assert.Equal(resolved.MaxManapoints * 0.4, player.Resources.Manapoints, 6);
        Assert.True(battle.Enemies[0].BuffStacks(CombatTraitRules.RanhunBuffId) >= 14);
        Assert.True(battle.Enemies[1].BuffStacks(CombatTraitRules.RanhunBuffId) >= 14);

        // 定神符命中主目标施加 5 层麻痹：闪避与化解按反比例软着陆 ×0.5（50->25, 80->40），绝不归零
        var paralysedTarget = battle.Enemies.Single(e => e.BuffStacks("buff.mabi") > 0);
        Assert.Equal(5.0, paralysedTarget.BuffStacks("buff.mabi"), 9);
        Assert.Equal(25.0, paralysedTarget.EffectiveStats.Evasion, 9);
        Assert.Equal(40.0, paralysedTarget.EffectiveStats.ParryResolve, 9);
        Assert.Equal(-50.0, paralysedTarget.EffectiveStats.ActionSpeedPoints, 9);
    }
}
