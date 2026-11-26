using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 追忆副本 ECS 核心仿真测试：
/// 覆盖单人 10 波逐波结算与通关独占掉落、开局实扣团灭不返体力且停连刷、
/// 现世模式按最大玩家等级动态升阶并放大通用经验、体力耗尽自动停刷，以及组队追忆副本（付费/助战/通用等级差/自然终局）。
/// </summary>
public sealed class DungeonSimulationTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private const string QingyunDungeonId = "dungeon.qingyun-secret-gorge";
    private const string QingyunNodeId = "node.cloud-peak";
    private const string QingyunCrystalId = "item.qingyun-secret-crystal";

    private static PlayerAggregate CreateSoloPlayer(
        GameDataCatalog catalog,
        Guid playerId,
        int realmLevel,
        int initialTili,
        double nowUnix,
        bool makeStrong)
    {
        var player = PlayerAggregate.Create(
            new PlayerId(playerId),
            new AccountId(Guid.NewGuid()),
            "追忆试炼者",
            BaseTime,
            catalog.Fingerprint);
        player.World.NodeId = QingyunNodeId;
        player.Progression.RealmLevel = realmLevel;
        player.Progression.TiliValue = initialTili;
        player.Progression.TiliUpdatedAtUnixSeconds = nowUnix;
        if (makeStrong)
        {
            // 穿戴高阶高强装备并赋予高境界，确保可稳定横扫青云古涧 10 波
            player.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.wuyin-spirit-sword", 20));
            player.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.golden-ape-cloak", 20));
            player.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.starfall-crown", 20));
            player.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.changhu-pendant", 20));
            var attrs = PlayerAttributeResolver.Resolve(player, catalog);
            player.Resources.MaxHitpoints = attrs.MaxHitpoints;
            player.Resources.Hitpoints = attrs.MaxHitpoints;
            player.Resources.MaxManapoints = attrs.MaxManapoints;
            player.Resources.Manapoints = attrs.MaxManapoints;
        }
        else
        {
            player.Resources.Hitpoints = 1;
            player.Resources.MaxHitpoints = 1;
        }
        return player;
    }

    [Fact]
    public void SoloDungeon_ClearsTenWaves_DeductsEnergyUpfront_AndGrantsExclusiveClearMaterial()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var t0 = BaseTime.ToUnixTimeMilliseconds() / 1000.0;
        var player = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d101"), realmLevel: 60, initialTili: 240, nowUnix: t0, makeStrong: true);

        player.Enqueue(new ActionIntent(new ActionId(QingyunDungeonId), RepeatCount: 1, Difficulty: 0, PvpEnabled: false), BaseTime, catalog.Actions);

        var totalCrystal = 0.0;
        var totalSpiritStones = 0.0;
        var now = BaseTime;
        var energyCheckedAfterStart = false;
        for (var step = 0; step < 300; step++)
        {
            now = now.AddSeconds(2);
            var result = SimulationEngine.AdvanceTo(player, now, catalog);
            foreach (var delta in result.AssetDeltas)
            {
                if (delta.Asset.ItemId.Value == QingyunCrystalId) totalCrystal += delta.QuantityDelta;
                if (delta.Asset.ItemId.Value == "currency.spirit-stone") totalSpiritStones += delta.QuantityDelta;
            }
            if (!energyCheckedAfterStart && player.Actions.Count > 0 && player.Actions[0].Expedition?.DungeonEnergyCharged == true)
            {
                energyCheckedAfterStart = true;
                // 开局第 1 波直接实扣 30 体力：240 - 30 = 210
                Assert.Equal(210, player.Progression.TiliValue);
            }
            if (player.Actions.Count == 0) break;
        }

        Assert.True(energyCheckedAfterStart, "应已进入第 1 波并完成开局实扣体力");
        Assert.Empty(player.Actions);
        Assert.True(totalCrystal >= 2, $"第 10 波通关应发放保底青云秘晶，实际获得 {totalCrystal}");
        Assert.True(totalSpiritStones >= 300, $"第 10 波通关应发放基准通关灵石 >= 300，实际获得 {totalSpiritStones}");
        Assert.True(player.Progression.BossKills >= 1, "应至少击杀 1 次关底妖王");
        Assert.True(player.Progression.EliteKills >= 3, "应至少击杀 3 波精英怪");
        // 开局扣过 30 体力后，通关不应重复扣体
        var snap = DungeonEnergyRules.Compute(player.Progression.TiliValue, player.Progression.TiliUpdatedAtUnixSeconds, now.ToUnixTimeMilliseconds() / 1000.0);
        Assert.InRange(snap.Current, 210, 212);
    }

    [Fact]
    public void SoloDungeon_RealityMode_ScalesMonsterHpAndRewardsWithPlayerRealmLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var dungeon = catalog.Dungeons[QingyunDungeonId];
        var t0 = BaseTime.ToUnixTimeMilliseconds() / 1000.0;
        // 30 级修士进入现世·凡阶（d=3，耗 60 体力）：青云古涧第 1 波小怪应升至 30+ 级并叠乘 1.30^3 血量
        var player = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d104"), realmLevel: 30, initialTili: 240, nowUnix: t0, makeStrong: true);
        player.Enqueue(new ActionIntent(new ActionId(QingyunDungeonId), RepeatCount: 1, Difficulty: 3, PvpEnabled: false), BaseTime, catalog.Actions);

        // 推进 6 秒越过 5 秒开局读条进入第 1 波战斗
        SimulationEngine.AdvanceTo(player, BaseTime.AddSeconds(6), catalog);
        var expState = player.Actions[0].Expedition;
        Assert.NotNull(expState);
        Assert.Equal(CombatExpeditionPhase.Combat, expState!.Phase);
        Assert.Equal(180, player.Progression.TiliValue); // 240 - 60 = 180

        var wave1EnemyId = dungeon.Waves[0].EnemyIds[0];
        var virtualScaledHp = DungeonDifficultyRules.ScaleMonsterStats(catalog.CombatMonsters[wave1EnemyId].Stats, 3).MaxHitpoints;
        var realityMonster = catalog.ResolveDungeonMonster(dungeon, wave1EnemyId, difficulty: 3, maxPartyRealmLevel: 30);
        var expectedRealityHp = DungeonDifficultyRules.ScaleMonsterStats(realityMonster.Stats, 3).MaxHitpoints;
        Assert.True(expectedRealityHp > virtualScaledHp * 3, "现世 30 级动态重算后气血应远高于原始低等级缩放值");
    }

    [Fact]
    public void SoloDungeon_WipeDoesNotRefundEnergy_AndStopsRepeatChain()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var t0 = BaseTime.ToUnixTimeMilliseconds() / 1000.0;
        // 1 级脆弱角色挑战 D10 现世·帝阶（耗 240 体力），连续挑战 5 次
        var player = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d102"), realmLevel: 1, initialTili: 240, nowUnix: t0, makeStrong: false);

        player.Enqueue(new ActionIntent(new ActionId(QingyunDungeonId), RepeatCount: 5, Difficulty: 10, PvpEnabled: false), BaseTime, catalog.Actions);

        var totalCrystal = 0.0;
        var now = BaseTime;
        for (var step = 0; step < 120; step++)
        {
            now = now.AddSeconds(2);
            var result = SimulationEngine.AdvanceTo(player, now, catalog);
            foreach (var delta in result.AssetDeltas)
            {
                if (delta.Asset.ItemId.Value == QingyunCrystalId) totalCrystal += delta.QuantityDelta;
            }
            if (player.Actions.Count == 0) break;
        }

        // 团灭立刻移出队列停止连刷，不返还 240 体力，不给通关专属材料
        Assert.Empty(player.Actions);
        Assert.Equal(0, player.Progression.TiliValue);
        Assert.Equal(0.0, totalCrystal);
        Assert.True(player.Progression.TotalDefeats >= 1);
    }

    [Fact]
    public void SoloDungeon_InfiniteRepeats_StopsAutomaticallyWhenTiliExhausted()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var t0 = BaseTime.ToUnixTimeMilliseconds() / 1000.0;
        // 初始 65 体力，D0 每次 30 体力，无限挑战只能完整打完 2 次，剩 5 体力自动停刷
        var player = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d103"), realmLevel: 60, initialTili: 65, nowUnix: t0, makeStrong: true);

        player.Enqueue(new ActionIntent(new ActionId(QingyunDungeonId), RepeatCount: -1, Difficulty: 0, PvpEnabled: false), BaseTime, catalog.Actions);

        var crystalCount = 0.0;
        var now = BaseTime;
        for (var step = 0; step < 500; step++)
        {
            now = now.AddSeconds(2);
            var result = SimulationEngine.AdvanceTo(player, now, catalog);
            foreach (var delta in result.AssetDeltas)
            {
                if (delta.Asset.ItemId.Value == QingyunCrystalId) crystalCount += delta.QuantityDelta;
            }
            if (player.Actions.Count == 0) break;
        }

        Assert.Empty(player.Actions);
        Assert.Equal(2, player.Progression.BossKills);
        Assert.True(crystalCount >= 4, $"连续通关 2 次至少获得 4 枚青云秘晶，实际 {crystalCount}");
        // 两次各扣 30，剩余锚点为 5（期间时间很短不足 360s）
        Assert.Equal(5, player.Progression.TiliValue);
    }

    [Fact]
    public void PartyDungeon_SettlesPerMember_RespectsAssistZeroRewards_GenericLevelMalus_AndNaturalEnd()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var t0 = BaseTime.ToUnixTimeMilliseconds() / 1000.0;

        // 成员 0：60 级大佬，付费（初始 240 体力）
        var carry = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d201"), realmLevel: 60, initialTili: 240, nowUnix: t0, makeStrong: true);
        // 成员 1：10 级低等级队友，付费（初始 240 体力，触发野外组队通用等级差惩罚）
        var rookie = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d202"), realmLevel: 10, initialTili: 240, nowUnix: t0, makeStrong: false);
        rookie.Resources.MaxHitpoints = 50_000;
        rookie.Resources.Hitpoints = 50_000;
        // 成员 2：60 级助战大佬（显式 AssistMode = true，零耗体零收益）
        var helper = CreateSoloPlayer(catalog, Guid.Parse("10000000-0000-4000-8000-00000000d203"), realmLevel: 60, initialTili: 240, nowUnix: t0, makeStrong: true);

        var snapCarry = carry.CaptureActionParticipantSnapshot(catalog) with { AssistMode = false };
        var snapRookie = rookie.CaptureActionParticipantSnapshot(catalog) with { AssistMode = false };
        var snapHelper = helper.CaptureActionParticipantSnapshot(catalog) with { AssistMode = true };

        var spec = new ActionParticipationSpec(
            SessionId: "party:dungeon-test:1",
            EncounterId: QingyunDungeonId,
            NodeId: QingyunNodeId,
            Seed: 20261125L,
            SearchSpeedPoints: 0,
            StartedAtUnixSeconds: t0,
            Members: [snapCarry, snapRookie, snapHelper])
        {
            ActionId = new ActionId(QingyunDungeonId),
            PvpEnabled = false,
            Difficulty = 0,
            DungeonId = QingyunDungeonId,
            DungeonRepeatCount = 1
        };

        var engine = ActionParticipationSessionEngine.Create(spec, catalog);
        var expByMember = new double[3];
        var stoneByMember = new double[3];
        var crystalByMember = new double[3];
        int?[] finalTili = new int?[3];

        var now = BaseTime;
        for (var step = 0; step < 300 && !engine.IsEnded; step++)
        {
            now = now.AddSeconds(2);
            var settlements = engine.AdvanceTo(now);
            for (var i = 0; i < settlements.Count; i++)
            {
                var s = settlements[i];
                if (s.TiliValue is not null) finalTili[i] = s.TiliValue;
                foreach (var entry in s.Stats)
                {
                    expByMember[i] += entry.Delta.CombatExperience + entry.Delta.CultivationExperience + entry.Delta.Experience;
                }
                foreach (var delta in s.AssetDeltas)
                {
                    if (delta.Asset.ItemId.Value == "currency.spirit-stone") stoneByMember[i] += delta.QuantityDelta;
                    if (delta.Asset.ItemId.Value == QingyunCrystalId) crystalByMember[i] += delta.QuantityDelta;
                }
            }
        }

        // 1. 达到目标次数（1 次）后自然结束
        Assert.True(engine.IsEnded, "组队追忆副本打完 1 次后应自动进入自然终局");
        Assert.Equal("dungeon-completed", engine.NaturalEndReason);

        // 2. 体力扣费：付费成员 0/1 各扣 30（240→210），助战成员 2 零耗体（仍 240）
        Assert.Equal(210, finalTili[0]);
        Assert.Equal(210, finalTili[1]);
        Assert.Equal(240, finalTili[2]);

        // 3. 助战成员 2 严格零经验、零灵石、零掉落
        Assert.Equal(0.0, expByMember[2]);
        Assert.Equal(0.0, stoneByMember[2]);
        Assert.Equal(0.0, crystalByMember[2]);

        // 4. 付费成员 0/1 独立获得通关专属材料与通关灵石（且等级差不折减灵石和物品）
        Assert.True(crystalByMember[0] >= 2, $"成员 0 应获通关保底秘晶，实际 {crystalByMember[0]}");
        Assert.True(crystalByMember[1] >= 2, $"成员 1 应获通关保底秘晶（不受等级差折减），实际 {crystalByMember[1]}");
        Assert.True(stoneByMember[0] >= 300);
        Assert.True(stoneByMember[1] >= 300);

        // 5. 通用等级差仅折减低等级成员 1 的经验（CalculateLevelMalus(10, 60) > 0）
        var expectedMalus = ActionParticipationSpec.CalculateLevelMalus(10, 60);
        Assert.True(expectedMalus > 0.5, $"10 级与 60 级应触发显著等级差惩罚，实际 {expectedMalus}");
        Assert.True(expByMember[0] > 0);
        Assert.True(expByMember[1] > 0 && expByMember[1] < expByMember[0] * 0.5,
            $"低等级成员经验应按通用等级差折减：carry={expByMember[0]}, rookie={expByMember[1]}");
    }
}
