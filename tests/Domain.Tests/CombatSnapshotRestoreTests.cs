using System.Text.Json;
using Domain.Combat;
using Domain.Common;

namespace Domain.Tests;

/// <summary>
/// 战斗快照反序列化与状态恢复门禁：
/// 覆盖技能槽位扩充、缩减、吟唱越界回退与非法冷却数值拦截。
/// </summary>
public sealed class CombatSnapshotRestoreTests
{
    private static SkillSlot CreateMockSkill(string id)
    {
        var json = JsonDocument.Parse($$"""{"id":"{{id}}","name":"{{id}}"}""").RootElement;
        return new SkillSlot(json, 1);
    }

    [Fact]
    public void RestoreSnapshot_WhenSkillsExpanded_PadsRemainingCooldownsWithZero()
    {
        // 场景：历史快照中仅有 1 个技能冷却记录（40s），当前玩家装配了 2 个技能
        var stats = CombatantStats.Create(1000, 100, 10, 1.0);
        var skills = new[] { CreateMockSkill("ability.one"), CreateMockSkill("ability.two") };
        var playerTemplate = new CombatantTemplate(stats, skills);
        var enemyTemplate = new CombatantTemplate(stats);

        var snapshot = new CombatantSnapshot(
            Hitpoints: 800,
            Manapoints: 50,
            NextAttackAtSeconds: 10,
            AttackCount: 5,
            Threat: 100,
            PendingSkillIndex: 0,
            SkillCooldownUntil: [40]);

        var restored = CombatEncounterState.Restore(
            encounterId: "encounter.test",
            seed: 12345,
            player: playerTemplate,
            playerSnapshot: snapshot,
            enemies: [enemyTemplate],
            enemySnapshots: [CombatEncounterState.Snapshot(new CombatantState("enemy:0", stats))],
            elapsedSeconds: 5,
            outcome: CombatOutcome.Running);

        Assert.Equal(2, restored.Player.Skills.Count);
        Assert.NotNull(restored.Player.SkillCooldownUntil);
        Assert.Equal(2, restored.Player.SkillCooldownUntil.Length);
        Assert.Equal(40, restored.Player.SkillCooldownUntil[0]);
        Assert.Equal(0, restored.Player.SkillCooldownUntil[1]);
        Assert.Equal(0, restored.Player.PendingSkillIndex);
    }

    [Fact]
    public void RestoreSnapshot_WhenSkillsShrunk_TruncatesCooldownsAndResetsOutOfRangePendingSkill()
    {
        // 场景：历史快照中有 2 个技能冷却记录（[40, 60]），且正在吟唱技能下标 1；当前玩家仅装配 1 个技能
        var stats = CombatantStats.Create(1000, 100, 10, 1.0);
        var skills = new[] { CreateMockSkill("ability.one") };
        var playerTemplate = new CombatantTemplate(stats, skills);
        var enemyTemplate = new CombatantTemplate(stats);

        var snapshot = new CombatantSnapshot(
            Hitpoints: 800,
            Manapoints: 50,
            NextAttackAtSeconds: 10,
            AttackCount: 5,
            Threat: 100,
            PendingSkillIndex: 1, // 越界下标
            SkillCooldownUntil: [40, 60]);

        var restored = CombatEncounterState.Restore(
            encounterId: "encounter.test",
            seed: 12345,
            player: playerTemplate,
            playerSnapshot: snapshot,
            enemies: [enemyTemplate],
            enemySnapshots: [CombatEncounterState.Snapshot(new CombatantState("enemy:0", stats))],
            elapsedSeconds: 5,
            outcome: CombatOutcome.Running);

        Assert.Single(restored.Player.Skills);
        Assert.NotNull(restored.Player.SkillCooldownUntil);
        Assert.Single(restored.Player.SkillCooldownUntil);
        Assert.Equal(40, restored.Player.SkillCooldownUntil[0]);
        // 越界吟唱技能回退为普攻 (-1)
        Assert.Equal(-1, restored.Player.PendingSkillIndex);
    }

    [Fact]
    public void RefreshStats_WhenResourceCapsShrink_MatchesRestoreWithoutRefilling()
    {
        var original = CombatantStats.Create(1000, 100, 10, 1.0) with { MaxManapoints = 100 };
        var player = new CombatantState("player", original) { Hitpoints = 800, Manapoints = 75 };
        var reduced = original with { MaxHitpoints = 700, MaxManapoints = 50 };
        var enemy = new CombatantState("enemy:0", original);
        var restored = CombatEncounterState.Restore("encounter.test", 42,
            new CombatantTemplate(reduced), CombatEncounterState.Snapshot(player),
            [new CombatantTemplate(original)], [CombatEncounterState.Snapshot(enemy)], 0, CombatOutcome.Running);

        player.RefreshStats(reduced);

        Assert.Equal(700, player.Hitpoints);
        Assert.Equal(50, player.Manapoints);
        Assert.Equal(restored.Player.Hitpoints, player.Hitpoints);
        Assert.Equal(restored.Player.Manapoints, player.Manapoints);

        player.RefreshStats(original);

        Assert.Equal(700, player.Hitpoints);
        Assert.Equal(50, player.Manapoints);
    }

    [Fact]
    public void RetaggedSnapshotWithoutFormationCooldowns_RestoresAsUntriggered()
    {
        // 迁移只改标签，v43 战斗快照没有这两个 JSON 属性。
        const string legacyJson = """{"hitpoints":100,"manapoints":50,"nextAttackAtSeconds":1,"attackCount":0,"threat":100}""";
        var snapshot = JsonSerializer.Deserialize<CombatantSnapshot>(legacyJson, Domain.Persistence.PersistenceJson.Options)!;
        var stats = CombatantStats.Create(100, 10, 0, 2) with { MaxManapoints = 50 };
        var template = new CombatantTemplate(stats);
        var restored = CombatEncounterState.Restore("encounter.test", 42, template, snapshot,
            [template], [snapshot], 0, CombatOutcome.Running);

        Assert.Equal(-1, restored.Player.LastTianleiTriggerAtSeconds);
        Assert.Equal(-1, restored.Player.LastXueshaTriggerAtSeconds);
    }

    [Fact]
    public void RestoreSnapshot_WithNegativeOrInvalidCooldown_ThrowsDomainException()
    {
        var stats = CombatantStats.Create(1000, 100, 10, 1.0);
        var skills = new[] { CreateMockSkill("ability.one") };
        var playerTemplate = new CombatantTemplate(stats, skills);
        var enemyTemplate = new CombatantTemplate(stats);

        var snapshot = new CombatantSnapshot(
            Hitpoints: 800,
            Manapoints: 50,
            NextAttackAtSeconds: 10,
            AttackCount: 5,
            Threat: 100,
            SkillCooldownUntil: [-5]);

        Assert.Throws<DomainException>(() => CombatEncounterState.Restore(
            encounterId: "encounter.test",
            seed: 12345,
            player: playerTemplate,
            playerSnapshot: snapshot,
            enemies: [enemyTemplate],
            enemySnapshots: [CombatEncounterState.Snapshot(new CombatantState("enemy:0", stats))],
            elapsedSeconds: 5,
            outcome: CombatOutcome.Running));
    }

    private static PlayerAggregate BuildStuckCombatPlayer(GameDataCatalog catalog, DateTimeOffset baseline)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", baseline, catalog.Fingerprint);
        var exported = player.ExportState();
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true),
            baseline,
            ActionQueueStatus.Running,
            0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, unchecked((long)4122577177101012044UL),
                15.472613, 88.20322454180142, 45, 16, 15, 88.20322454180142, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit",
                    new CombatantSnapshot(150, 4.752941176470588, 15.997984378936762, 7, 13.111788034361007, 1))])
            {
                PlayerState = new CombatantSnapshot(88.20322454180142, 45, 16, 15, 88.20322454180142)
            });
        return PlayerAggregate.Restore(exported with
        {
            Actions = [action],
            Combat = new PlayerCombatStateDocument(88.20322454180142, 100, 45, 50, 11, 11, 1.5453399),
        });
    }

    /// <summary>一次性大步长补算路径：模拟时钟必须推进到目标时刻且至少完成一轮讨伐。</summary>
    [Fact]
    public void PersistedCombatExpeditionAdvancesAcrossLongCatchUp()
    {
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = BuildStuckCombatPlayer(catalog, baseline);

        var result = SimulationEngine.AdvanceTo(player, baseline.AddSeconds(32_000), catalog,
            duel: new DuelAdvanceOptions(MatchingEnabled: true));

        Assert.Equal(baseline.AddSeconds(32_000), player.LastSimulatedAt);
        Assert.True(result.CompletedRepeats > 0 || player.Progression.TotalDefeats > 0,
            "长时间补算应至少完成一轮讨伐或产生败走结论。");
        Assert.True(player.Progression.NormalKills + player.Progression.EliteKills
            + player.Progression.BossKills + player.Progression.TotalDefeats > 0);
    }

    /// <summary>在线小步推进路径：远征战斗计时必须持续增长，不得原地踏步。</summary>
    [Fact]
    public void PersistedCombatExpeditionAdvancesInSmallSteps()
    {
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = BuildStuckCombatPlayer(catalog, baseline);

        var cursor = baseline;
        for (var i = 0; i < 200; i++)
        {
            cursor = cursor.AddSeconds(0.25);
            SimulationEngine.AdvanceTo(player, cursor, catalog, duel: new DuelAdvanceOptions(MatchingEnabled: true));
        }
        Assert.Equal(cursor, player.LastSimulatedAt);
        var expedition = player.Actions[0].Expedition!;
        Assert.True(expedition.Phase != CombatExpeditionPhase.Combat
            || expedition.BattleElapsedSeconds > 15.472613
            || expedition.WaveNumber > 1,
            $"逐拍推进下战斗时钟与相位均不得原地踏步（phase={expedition.Phase} " +
            $"elapsed={expedition.BattleElapsedSeconds} wave={expedition.WaveNumber} " +
            $"kills={player.Progression.NormalKills} defeats={player.Progression.TotalDefeats}）。");
    }
}
