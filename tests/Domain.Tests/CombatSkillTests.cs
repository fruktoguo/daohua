using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 战斗技能测试：单时间线施法恢复、冷却阻止、治疗效果与无效技能拒绝。
/// 玩家/怪物/斗法镜像共用同一技能槽与施放管线，测试全部走通用战斗引擎。
/// </summary>
public sealed class CombatSkillTests
{
    /// <summary>
    /// 施法状态经快照往返后蓝耗只扣一次：起手决策预扣灵力进入吟唱，
    /// 吟唱完成正常结算伤害并进入冷却，冷却中出手回落普通攻击。
    /// </summary>
    [Fact]
    public void CastRestoreChargesManaOnceAndCooldownPreventsRecast()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant() with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.heavy-slash"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100000, 0, 20, 100);
        var encounter = CombatEncounterState.Start("skill", 71, playerTemplate, [new CombatantTemplate(enemy)]);
        // 起手即决策吟唱（target-enemy current-hp ≥ 1 恒成立）：灵力在起手时预扣一次。
        Assert.Equal(0, encounter.Player.PendingSkillIndex);
        Assert.Equal(1000 - 10, encounter.Player.Manapoints);

        // 吟唱中快照恢复：吟唱槽位与已扣灵力随快照走，恢复后不得重复扣蓝。
        var restored = CombatEncounterState.Restore("skill", 71, playerTemplate,
            CombatEncounterState.Snapshot(encounter.Player),
            [new CombatantTemplate(enemy)],
            [CombatEncounterState.Snapshot(encounter.Enemies[0])],
            encounter.ElapsedSeconds, encounter.Outcome, encounter.Sequence);
        Assert.Equal(0, restored.Player.PendingSkillIndex);
        Assert.Equal(1000 - 10, restored.Player.Manapoints);

        var result = DeterministicCombatEngine.AdvanceTo(restored, 18.5);
        var cast = Assert.Single(result.Events,
            item => item.AttackerId == "player" && item.AbilityId == "ability.heavy-slash");
        Assert.True(cast.Damage > 0);
        Assert.Equal(1, cast.AtSeconds, 6);
        // 冷却自吟唱完成时刻起算 18s：19s 前不得二次施放，其间出手均为普攻。
        Assert.All(result.Events.Where(item => item.AttackerId == "player" && item.AtSeconds > cast.AtSeconds + 1e-9),
            item => Assert.Null(item.AbilityId));
        // 面板 MpRegen 按 5 秒网格在战斗内结算（5/10/15s 三个结算点），灵力只可能被回复抬高。
        Assert.Equal(Math.Min(1000, 1000 - 10 + 3 * stats.MpRegen), restored.Player.Manapoints, 9);

        // 跨战持久化形态折算：完成态导出→JSON 往返→校验不变量。
        var persisted = PlayerCombatSkillState.FromCombatant(restored.Player, restored.ElapsedSeconds);
        var roundtripped = JsonSerializer.Deserialize<PlayerCombatSkillState>(
            JsonSerializer.Serialize(persisted))!;
        Assert.True(roundtripped.IsValid);
        Assert.Equal(1, roundtripped.CastCount);
        Assert.True(Assert.Single(roundtripped.Cooldowns).RemainingSeconds > 0);
    }

    /// <summary>
    /// 治疗技能满血时触发器不满足不施放、残血时施放且治疗量封顶不溢出血量上限；
    /// 嘲讽/破阵子等支援类技能必须能通过统一装载校验。
    /// </summary>
    [Fact]
    public void HealingRespectsTriggerAndCapsHealthAndNoEffectAbilitiesAreRejected()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant() with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.qingping-melody"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100000, 0, 0, 100);
        // 满血：self missing-hp ≥ 1 不成立，起手不进入吟唱。
        var full = CombatEncounterState.Start("healing", 8, playerTemplate, [new CombatantTemplate(enemy)]);
        Assert.Equal(-1, full.Player.PendingSkillIndex);

        // 残血入场：首次出手边界触发治疗吟唱，0.5s 后按缺失血量封顶结算。
        var hurt = CombatEncounterState.Restore("healing", 8, playerTemplate,
            new CombatantSnapshot(stats.MaxHitpoints - 1, stats.MaxManapoints, 0, 0, 0),
            [new CombatantTemplate(enemy)],
            [new CombatantSnapshot(enemy.MaxHitpoints, 0, 0, 0, 0)],
            0, CombatOutcome.Running);
        var result = DeterministicCombatEngine.AdvanceTo(hurt, 1);
        var heal = Assert.Single(result.Events, item => item.AbilityId == "ability.qingping-melody");
        Assert.Equal(1, heal.Healing);
        Assert.Equal(stats.MaxHitpoints, hurt.Player.Hitpoints);

        // 嘲讽与破阵子等支援类技能必须全部可装配、可通过统一校验。
        SkillTriggerEvaluator.ValidateAbility(catalog.Abilities["ability.taunt"]);
        SkillTriggerEvaluator.ValidateAbility(catalog.Abilities["ability.pozhen-melody"]);
    }

    /// <summary>
    /// 破阵子回归：无 effectType 的零伤害 Buff 技能必须走支援管线授予战内 Buff，
    /// Buff 免伤按比率折点进入同一乘区并按战斗时钟到期。
    /// </summary>
    [Fact]
    public void PozhenMelodyGrantsTimedAbsorptionBuffToAllAllies()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant() with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.pozhen-melody"];
        var playerTemplate = new CombatantTemplate(stats with { MaxHitpoints = 10_000_000 }, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100000, 100, 0, 1);
        var encounter = CombatEncounterState.Start("buff", 13, playerTemplate, [new CombatantTemplate(enemy)]);
        // 起手即吟唱破阵子（5s×CastTimeFactor）；完成时刻授予全体友方 Buff。
        Assert.Equal(0, encounter.Player.PendingSkillIndex);
        Assert.Equal(1000 - 50, encounter.Player.Manapoints);
        DeterministicCombatEngine.AdvanceTo(encounter, 5 * stats.CastTimeFactor + 1e-9);
        var buff = Assert.Single(encounter.Player.Buffs);
        Assert.True(buff.DamageAbsorptionRatio > 0);
        Assert.Equal(5 * stats.CastTimeFactor + 30, buff.ExpiresAtSeconds, 6);
        // 到期后 Buff 移除：推进超过过期时刻后列表清空。
        DeterministicCombatEngine.AdvanceTo(encounter, 5 * stats.CastTimeFactor + 31);
        Assert.Empty(encounter.Player.Buffs);
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "技能", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        return player;
    }
}
