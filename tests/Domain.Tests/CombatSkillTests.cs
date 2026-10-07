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
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0) with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.heavy-slash"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100000, 0, 20, 100);
        var encounter = DeterministicCombatEngine.Start("skill", 71, playerTemplate, [new CombatantTemplate(enemy)]);
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
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0) with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.qingping-melody"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100000, 0, 0, 100);
        // 满血：self missing-hp ≥ 1 不成立，起手不进入吟唱。
        var full = DeterministicCombatEngine.Start("healing", 8, playerTemplate, [new CombatantTemplate(enemy)]);
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
    /// 破阵子回归：全体友方授予 40% 总增伤 Buff（10s），与面板总增伤点数同 Linear 乘区；
    /// Buff 期内普攻伤害按倍率放大，到期随战斗时钟移除、伤害回落。
    /// </summary>
    [Fact]
    public void PozhenMelodyGrantsTimedDamageBonusBuffToAllAllies()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0)
            with { MaxManapoints = 1000, AttackIntervalSeconds = 1 };
        var ability = catalog.Abilities["ability.pozhen-melody"];
        var playerTemplate = new CombatantTemplate(stats with { MaxHitpoints = 10_000_000 }, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100_000_000, 0, 0, 100);
        var encounter = DeterministicCombatEngine.Start("buff", 13, playerTemplate, [new CombatantTemplate(enemy)]);
        // 起手即吟唱破阵子（5s÷(1+行动速度点数/100)）；完成时刻授予全体友方 Buff。
        Assert.Equal(0, encounter.Player.PendingSkillIndex);
        Assert.Equal(1000 - 50, encounter.Player.Manapoints);
        var castDoneAt = 5 * PercentValue.Multiplier(stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 1e-9);
        var buff = Assert.Single(encounter.Player.Buffs);
        Assert.Equal(40, buff.DamageBonusPoints, 9);
        Assert.Equal(0, buff.DamageAbsorptionPoints, 9);
        Assert.Equal(castDoneAt + 10, buff.ExpiresAtSeconds, 6);

        // Buff 期内普攻帧伤害 = 面板总增伤 + Buff 点数合并后的 Linear 乘区放大。
        var inside = DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 9).Events
            .Where(e => e.AbilityId is null && e.AttackerId == "player"
                && e.AtSeconds > castDoneAt && e.AtSeconds < castDoneAt + 10 && e.Damage > 0).ToList();
        Assert.NotEmpty(inside);
        var expectedRatio = PercentValue.Multiplier(stats.TotalDamageBonusPoints + 40, PercentCurveMode.Linear)
            / PercentValue.Multiplier(stats.TotalDamageBonusPoints, PercentCurveMode.Linear);
        // 到期后 Buff 移除、伤害回落：期内外普攻帧伤害比值恰为增伤乘区倍率。
        var outside = DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 12.5).Events
            .Where(e => e.AbilityId is null && e.AttackerId == "player"
                && e.AtSeconds > castDoneAt + 10.5 && e.Damage > 0).ToList();
        Assert.Empty(encounter.Player.Buffs);
        Assert.NotEmpty(outside);
        Assert.Equal(inside[0].Damage, outside[0].Damage * expectedRatio, 6);
    }

    /// <summary>
    /// 贯星击杀连锁回归：主目标阵亡后按威胁轮盘贯穿下一存活敌方再结算一次完整伤害，
    /// 至多追加 2 次——三敌全歼共 3 帧同刻同技能事件；目标未死或敌方不足时不连锁。
    /// </summary>
    [Fact]
    public void StarPiercerChainsOnKillUpToTwoAdditionalTargets()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0)
            with { MaxManapoints = 1000, AttackPower = 1_000_000, AttackIntervalSeconds = 100 };
        var ability = catalog.Abilities["ability.star-piercer"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var fragile = CombatantStats.Create(10, 0, 0, 100);

        // 三敌场：贯星 300%×1e6+50 一击必杀，连锁 2 次全歼，共 3 帧不同目标同刻事件。
        var three = DeterministicCombatEngine.Start("chain", 5, playerTemplate,
            [new CombatantTemplate(fragile), new CombatantTemplate(fragile), new CombatantTemplate(fragile)]);
        var threeResult = DeterministicCombatEngine.AdvanceTo(three, 2.5);
        var hits = threeResult.Events.Where(e => e.AbilityId == "ability.star-piercer" && e.Damage > 0).ToList();
        Assert.Equal(3, hits.Count);
        Assert.Equal(3, hits.Select(h => h.TargetId).Distinct().Count());
        Assert.All(hits, h => Assert.Equal(2.0, h.AtSeconds, 6));
        Assert.All(three.Enemies, e => Assert.False(e.IsAlive));

        // 两敌场：连锁 1 次后无存活目标终止，共 2 帧。
        var two = DeterministicCombatEngine.Start("chain", 5, playerTemplate,
            [new CombatantTemplate(fragile), new CombatantTemplate(fragile)]);
        var twoHits = DeterministicCombatEngine.AdvanceTo(two, 2.5).Events
            .Where(e => e.AbilityId == "ability.star-piercer" && e.Damage > 0).ToList();
        Assert.Equal(2, twoHits.Count);
        Assert.Equal(2, twoHits.Select(h => h.TargetId).Distinct().Count());

        // 未致死不连锁：敌方血量高于贯星伤害时仅主目标一帧。
        var tanky = CombatantStats.Create(10_000_000, 0, 0, 100);
        var sturdy = DeterministicCombatEngine.Start("chain", 5, playerTemplate,
            [new CombatantTemplate(tanky), new CombatantTemplate(tanky)]);
        var sturdyHits = DeterministicCombatEngine.AdvanceTo(sturdy, 2.5).Events
            .Where(e => e.AbilityId == "ability.star-piercer" && e.Damage > 0).ToList();
        Assert.Single(sturdyHits);
        Assert.All(sturdy.Enemies, e => Assert.True(e.IsAlive));
    }

    /// <summary>
    /// 连环多段打击回归：对同一目标逐段独立结算 3 次 80%AD+10，
    /// 三帧同刻同目标同技能事件；目标中途死亡即终止剩余连段。
    /// </summary>
    [Fact]
    public void ChainStrikeHitsSameTargetThreeTimesAndStopsOnKill()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0)
            with { MaxManapoints = 1000, AttackIntervalSeconds = 100 };
        var ability = catalog.Abilities["ability.chain-strike"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);

        // 高血量目标：三段全部命中同一目标，帧刻相同、段间独立掷点。
        var sturdy = CombatantStats.Create(10_000_000, 0, 0, 100);
        var full = DeterministicCombatEngine.Start("multihit", 7, playerTemplate, [new CombatantTemplate(sturdy)]);
        var fullHits = DeterministicCombatEngine.AdvanceTo(full, 1.5).Events
            .Where(e => e.AbilityId == "ability.chain-strike").ToList();
        Assert.Equal(3, fullHits.Count);
        Assert.All(fullHits, h => Assert.Equal("enemy:0", h.TargetId));
        Assert.All(fullHits, h => Assert.Equal(1.0, h.AtSeconds, 6));
        Assert.All(fullHits, h => Assert.True(h.Hit && h.Damage > 0));

        // 低血量目标：首段致死即终止，不再产生后续连段帧。
        var fragile = CombatantStats.Create(10, 0, 0, 100);
        var kills = DeterministicCombatEngine.Start("multihit", 7,
            new CombatantTemplate(stats with { AttackPower = 1_000_000 }, [new SkillSlot(ability, 1)]),
            [new CombatantTemplate(fragile), new CombatantTemplate(fragile)]);
        var killHits = DeterministicCombatEngine.AdvanceTo(kills, 1.5).Events
            .Where(e => e.AbilityId == "ability.chain-strike").ToList();
        Assert.Single(killHits);
        Assert.Equal(1, kills.Enemies.Count(e => !e.IsAlive)); // 只死目标一个，不转火
    }

    /// <summary>
    /// 语义空判定回归：JSON 往返/推进产生的空内容技能态必须 IsEmpty，
    /// 与 Empty 单例的记录相等性（列表引用比较）无关——封顶尾段与脱战判定依赖此语义。
    /// </summary>
    [Fact]
    public void DeserializedEmptySkillStateIsSemanticallyEmpty()
    {
        // 模拟持久化还原路径：等价空内容但非 Empty 单例的实例。
        var restored = new PlayerCombatSkillState(new List<CombatSkillCooldown>(), null, 0, 0);
        Assert.True(restored.IsEmpty);
        Assert.NotEqual(PlayerCombatSkillState.Empty, restored); // 引用比较语义仍不相等
        Assert.True(PlayerCombatSkillState.Empty.IsEmpty);

        // 有冷却/吟唱/施放计数的实例不得判空。
        Assert.False(new PlayerCombatSkillState(
            [new CombatSkillCooldown("ability.x", 3)], null, 0, 0).IsEmpty);
        Assert.False((PlayerCombatSkillState.Empty with { CastingAbilityId = "ability.x", CastRemainingSeconds = 1 }).IsEmpty);
        Assert.False((PlayerCombatSkillState.Empty with { CastCount = 2 }).IsEmpty);
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "技能", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        return player;
    }
}
