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
}
