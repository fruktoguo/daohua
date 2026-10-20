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
    /// 嘲讽回归（总纲 §5.1/§7.8.1）：归类为术法可装配施放；开战全体基础威胁为 100；
    /// 0.2s 施放完成即时 +500 基础威胁并授予 60s「嘲讽」增益（威胁增幅 +400 点）——
    /// Buff 覆盖期长于 55s 冷却、有效威胁按基础值×(1+增幅点数/100) 放大。
    /// </summary>
    [Fact]
    public void TauntIsSpellAndGrantsSixtySecondThreatBuff()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0) with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.taunt"];
        Assert.Equal("spell", ability.GetProperty("discipline").GetString());
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100_000_000, 0, 0, 100);
        var encounter = DeterministicCombatEngine.Start("taunt", 29, playerTemplate, [new CombatantTemplate(enemy)]);
        // 开战全体基础威胁重置为 100；嘲讽起手即决策吟唱。
        Assert.Equal(CombatantState.BaseThreat, encounter.Player.Threat);
        Assert.Equal(CombatantState.BaseThreat, encounter.Enemies[0].Threat);
        Assert.Equal(0, encounter.Player.PendingSkillIndex);

        var castDoneAt = 0.2 * PercentValue.Multiplier(stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 1e-9);
        // 施放完成：+500 基础威胁直接改写存储值（不预乘增幅），并授予 60s 嘲讽增益。
        Assert.Equal(CombatantState.BaseThreat + 500, encounter.Player.Threat, 6);
        var buff = Assert.Single(encounter.Player.Buffs);
        Assert.Equal("ability-buff:ability.taunt", buff.BuffId);
        Assert.Equal(400, buff.ThreatPoints, 9);
        Assert.Equal(castDoneAt + 60, buff.ExpiresAtSeconds, 6);
        // 技能授予 Buff 默认跨波存续（resetOnWave 未配置即 false）。
        Assert.False(buff.ResetOnWave);
        // Buff 覆盖 60s > 冷却 55s：冷却窗口内增幅恒生效，有效威胁 600×5=3000。
        var cooldownEnd = castDoneAt + 55 * PercentValue.Multiplier(stats.CooldownReductionPoints, PercentCurveMode.NonLinear);
        Assert.Equal(cooldownEnd, encounter.Player.SkillCooldownUntil![0], 6);
        Assert.True(buff.ExpiresAtSeconds > cooldownEnd);
        Assert.Equal(600 * 5, encounter.Player.EffectiveThreat, 6);
    }

    /// <summary>
    /// 嘲讽等级成长回归（总纲 §5.2）：即时威胁与 Buff 威胁增幅点数同吃每级 +2% 线性成长——
    /// Lv3 下 500×1.04=520、400×1.04=416。
    /// </summary>
    [Fact]
    public void TauntThreatValuesScaleWithAbilityLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0) with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.taunt"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 3)]);
        var enemy = CombatantStats.Create(100_000_000, 0, 0, 100);
        var encounter = DeterministicCombatEngine.Start("taunt.level", 31, playerTemplate, [new CombatantTemplate(enemy)]);

        var castDoneAt = 0.2 * PercentValue.Multiplier(stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 1e-9);
        // Lv3：统一成长 ×1.04 → threatGain 520、Buff 威胁增幅 416 点。
        // 成长率字面断言锁死「每级 +2% 线性」口径：Lv1=1.00、Lv2=1.02、Lv3=1.04，防平方化回归。
        Assert.Equal(1.0, CombatGrowthCurve.AbilityLevelGrowth(1), 9);
        Assert.Equal(1.02, CombatGrowthCurve.AbilityLevelGrowth(2), 9);
        Assert.Equal(1.04, CombatGrowthCurve.AbilityLevelGrowth(3), 9);
        Assert.Equal(CombatantState.BaseThreat + 520, encounter.Player.Threat, 6);
        var buff = Assert.Single(encounter.Player.Buffs);
        Assert.Equal(416, buff.ThreatPoints, 6);
        Assert.Equal(10 * 1.04, buff.ThreatPerSecondMaxHpPoints, 6);
    }

    /// <summary>
    /// 嘲讽每秒威胁累积回归（总纲 §7.8.1）：Buff 携带 threatPerSecondMaxHpPoints=10 时，
    /// 沿全局 1 秒结算网格每整秒 +最大气血×10% 基础威胁——与伤害涨威胁通道隔离观测。
    /// </summary>
    [Fact]
    public void TauntBuffAccruesThreatEverySecond()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var ability = catalog.Abilities["ability.taunt"];
        // 超长普攻间隔：嘲讽吟唱完成后双方不再出手，隔离「伤害累积威胁」通道，只观测周期累积。
        var stats = CombatantStats.Create(2000, 0, 0, 1e9) with { MaxManapoints = 1000 };
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100_000_000, 0, 0, 1e9);
        var encounter = DeterministicCombatEngine.Start("taunt.tick", 37, playerTemplate, [new CombatantTemplate(enemy)]);

        var castDoneAt = 0.2 * PercentValue.Multiplier(stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        DeterministicCombatEngine.AdvanceTo(encounter, castDoneAt + 1e-9);
        Assert.Equal(CombatantState.BaseThreat + 500, encounter.Player.Threat, 6);
        var buff = Assert.Single(encounter.Player.Buffs);
        Assert.Equal(10, buff.ThreatPerSecondMaxHpPoints, 9);

        // 整秒网格结算：0.2s 授予后 t=1/2/3 各 +2000×10%=200，三跳后基础威胁 600+600=1200。
        DeterministicCombatEngine.AdvanceTo(encounter, 3.5);
        Assert.Equal(CombatantState.BaseThreat + 500 + 3 * 200, encounter.Player.Threat, 6);
        // 周期累积写入基础威胁存储值，仍被 Buff 增幅放大：1200×(1+400/100)=6000。
        Assert.Equal(1200 * 5, encounter.Player.EffectiveThreat, 6);
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
    /// 跨波携带折算回归：resetOnWave=false 的 Buff 按战斗时钟剩余秒数定格携带，
    /// resetOnWave=true 与已到期的条目不携带；重注入按新时钟折算到期时刻、同 ID 覆盖。
    /// </summary>
    [Fact]
    public void CombatBuffCarryFiltersResetOnWaveAndExpiredEntries()
    {
        var stats = CombatantStats.Create(1000, 0, 0, 100);
        var unit = new CombatantState("player", stats);
        unit.Buffs.Add(new CombatBuffState("buff.keep", 60, 2, ThreatPoints: 400, Name: "存续"));
        unit.Buffs.Add(new CombatBuffState("buff.reset", 60, 1, ThreatPoints: 50, Name: "波清", ResetOnWave: true));
        unit.Buffs.Add(new CombatBuffState("buff.expired", 4.9, 1, Name: "到期"));

        var carried = PlayerCombatBuffState.FromCombatant(unit, elapsedSeconds: 10);
        var entry = Assert.Single(carried.Buffs);
        Assert.Equal("buff.keep", entry.BuffId);
        Assert.Equal(50, entry.RemainingSeconds, 9); // 60 - 10
        Assert.Equal(2, entry.Stacks);
        Assert.Equal(400, entry.ThreatPoints, 9);
        Assert.Equal(400 * 2, carried.SumThreatPoints(), 9);
        Assert.True(carried.IsValid);

        // JSON 往返：列表为引用比较语义（同技能态口径），逐条断言载荷一致。
        var roundtripped = JsonSerializer.Deserialize<PlayerCombatBuffState>(
            JsonSerializer.Serialize(carried))!;
        Assert.Equal(carried.Buffs, roundtripped.Buffs);
        Assert.True(roundtripped.IsValid);

        // 重注入：到期时刻 = 新时钟 + 定格剩余；同 ID 旧条目先移除（防叠影）。
        var next = new CombatantState("player", stats);
        next.Buffs.Add(new CombatBuffState("buff.keep", 999, 9, ThreatPoints: 1, Name: "残留"));
        next.Buffs.Add(new CombatBuffState("buff.other", 20, 1, Name: "旁证"));
        carried.ApplyTo(next, battleElapsedSeconds: 0);
        var injected = Assert.Single(next.Buffs, buff => buff.BuffId == "buff.keep");
        Assert.Equal(50, injected.ExpiresAtSeconds, 9);
        Assert.Equal(2, injected.Stacks);
        Assert.Equal(400, injected.ThreatPoints, 9);
        Assert.False(injected.ResetOnWave); // 重注入条目按默认口径继续跨波
        Assert.Single(next.Buffs, buff => buff.BuffId == "buff.other");
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

    /// <summary>
    /// 雷光引：单体伤害、0.5s 吟唱与 150% 伤害倍率确定性回归。
    /// </summary>
    [Fact]
    public void LightningAttraction_DealsSingleTargetDamageWithHalfSecondCast()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var ability = catalog.Abilities["ability.lightning-attraction"];
        Assert.Equal(500, ability.GetProperty("castMilliseconds").GetInt32());
        Assert.Equal(1.5, ability.GetProperty("damageRatio").GetDouble());
        Assert.Equal(30, ability.GetProperty("flatDamage").GetInt32());
        Assert.False(ability.TryGetProperty("targetType", out var targetType) && targetType.GetString() == "allEnemies");

        var stats = CombatantStats.Create(1000, 100, 0, 100) with
        {
            MaxManapoints = 1000,
            SpellAttack = 200,
            AttackIntervalSeconds = 100
        };
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        // 敌方两名目标，验证雷光引为单体攻击，只命中一个目标
        var enemy1 = CombatantStats.Create(100_000, 0, 0, 100);
        var enemy2 = CombatantStats.Create(100_000, 0, 0, 100);
        var encounter = DeterministicCombatEngine.Start("lightning", 42, playerTemplate, [new CombatantTemplate(enemy1), new CombatantTemplate(enemy2)]);

        // 推进到 0.6s（0.5s 吟唱完成时命中）
        var result = DeterministicCombatEngine.AdvanceTo(encounter, 0.6);
        var hits = result.Events.Where(e => e.AbilityId == "ability.lightning-attraction" && e.Damage > 0).ToList();
        Assert.Single(hits);
        Assert.Equal(0.5, hits[0].AtSeconds, 6);
        Assert.Equal(CombatDamageKind.Spell, hits[0].DamageKind);
    }

    [Fact]
    public void InternalAbilitiesAreLoadedAndBypassedInCombatSkills()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        string[] internalIds =
        [
            "ability.tuna-gong",
            "ability.manniu-jin",
            "ability.tiebu-shan",
            "ability.feiyan-gong",
            "ability.qingxin-jue",
            "ability.fenxue-gong",
            "ability.xiaozhoutian-gong",
            "ability.cunshen-jue",
            "ability.lianjing-huaqi-jue",
            "ability.xuanyuan-yiqi-gong"
        ];
        foreach (var id in internalIds)
        {
            Assert.True(catalog.Abilities.TryGetValue(id, out var ability), $"缺少内功心法: {id}");
            Assert.Equal("internal", ability.GetProperty("discipline").GetString());
            var slots = SkillSlot.FromAbilitySlots([new PlayerAbilitySlot(id, 1)], catalog.Abilities);
            Assert.Empty(slots);
        }
    }

    [Fact]
    public void InternalAbilityPassivesApplyToPlayerAttributes()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        player.LearnAbility(new AbilityId("ability.tiebu-shan"));
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.tiebu-shan", 1);
        player.Touch(PlayerSectionKind.Loadout);

        // 1 级基础效果提升 5 倍：铁布衫由 2% 提升至 10%
        var attrs = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(10, attrs.PhysicalDamageReductionPoints);

        // 切换为蛮牛劲 1 级：蛮牛劲由 2% 提升至 10%
        player.LearnAbility(new AbilityId("ability.manniu-jin"));
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.manniu-jin", 1);
        player.Touch(PlayerSectionKind.Loadout);

        var attrs2 = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(0, attrs2.PhysicalDamageReductionPoints);
        Assert.Equal(10, attrs2.PhysicalDamageBonusPoints);

        // 参悟升级至 3 级（每级 +5%）：倍率 1 + 0.05 * 2 = 1.10，10 * 1.10 = 11 点
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.manniu-jin", 3);
        player.Touch(PlayerSectionKind.Loadout);
        var attrsLevel3 = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(11, attrsLevel3.PhysicalDamageBonusPoints);

        // 验证研读经验升级联动：由 1 级注入经验升至 2 级（AbilityExpCurve 2级需 80 经验）
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.manniu-jin", 1);
        player.Touch(PlayerSectionKind.Loadout);
        Assert.Equal(10, PlayerAttributeResolver.Resolve(player, catalog).PhysicalDamageBonusPoints);
        player.GrantAbilityExperience(new AbilityId("ability.manniu-jin"), 100);
        Assert.Equal(2, player.Loadout.AbilitySlots[0].Level);
        // 2 级：10 * (1 + 0.05 * 1) = 10.5 点
        var attrsExpLevel2 = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(10.5, attrsExpLevel2.PhysicalDamageBonusPoints);

        // 切换为飞燕功 1 级：行动速度数值减半后为 5%
        player.LearnAbility(new AbilityId("ability.feiyan-gong"));
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.feiyan-gong", 1);
        player.Touch(PlayerSectionKind.Loadout);
        var feiyanAttrs = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(5, feiyanAttrs.ActionSpeedPoints);
    }

    [Fact]
    public void BulkAbilityExperience_MatchesPerBookLoop_AndIgnoresQuantityMagnitude()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var abilityId = new AbilityId("ability.blazing-blast");
        const double perBook = 500d;

        // 逐本基线：200 次单本注入。
        var perBookPlayer = CreatePlayer(catalog);
        perBookPlayer.LearnAbility(abilityId);
        for (var i = 0; i < 200; i++) perBookPlayer.GrantAbilityExperience(abilityId, perBook);

        // 批量总量：一次注入 200 本合计，经验与等级必须与逐本一致。
        var bulkPlayer = CreatePlayer(catalog);
        bulkPlayer.LearnAbility(abilityId);
        bulkPlayer.GrantAbilityExperienceTotal(abilityId, 200 * perBook);

        Assert.Equal(perBookPlayer.Loadout.AbilityExperiences[abilityId],
            bulkPlayer.Loadout.AbilityExperiences[abilityId], 6);
        Assert.Equal(perBookPlayer.AbilityLevelFor(abilityId), bulkPlayer.AbilityLevelFor(abilityId));

        // 十亿本（客户端可控数量）：一次结算即封顶，执行路径与数量无关。
        var hugePlayer = CreatePlayer(catalog);
        hugePlayer.LearnAbility(abilityId);
        hugePlayer.GrantAbilityExperienceTotal(abilityId, 1_000_000_000d * 10_000d);
        Assert.Equal(AbilityExpCurve.MaxLevel, hugePlayer.AbilityLevelFor(abilityId));
    }

    [Fact]
    public void AbilitySlotLevel_RestoresAndSyncsWithExperience()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var abilityId = new AbilityId("ability.blazing-blast");
        player.LearnAbility(abilityId);

        // 59018 经验经立方曲线换算应为 9 级（51200 <= exp < 72900）
        Assert.Equal(9, AbilityExpCurve.LevelFor(59018));
        Assert.Equal(9, AbilityExpCurve.LevelFor(55500));
        Assert.Equal(8, AbilityExpCurve.LevelFor(39168));

        // 模拟快照中 slot.Level 滞后记录为 8 级，Restore 后应自动对齐为已掌握的 9 级
        var doc = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(),
            [new PlayerAbilitySlot("ability.blazing-blast", 8)],
            [],
            new Dictionary<string, double> { ["ability.blazing-blast"] = 59018 });
        player.Loadout.Restore(doc);

        Assert.Equal(9, player.Loadout.AbilitySlots[0].Level);
    }

    /// <summary>
    /// 甘露技能实装与maxKeep叠层验证：
    /// 1. 释放者法攻动态折算层数：每 10 点法攻额外 +1 层（向下取整）；
    /// 2. 目标为最低血量存活友方；
    /// 3. maxKeep 策略验证：先施加高层后再施加低层保持高层并刷新到期时间；低层被高层覆盖并刷新时间；
    /// 4. 周期回复沿 5 秒网格按每层 5 点（等效 1 点/秒）准确落账；水灵怪物配置完备。
    /// </summary>
    [Fact]
    public void GanluAbilityScalesWithSpellAttackAndFollowsMaxKeepStackingPolicy()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.water-spirit", out var waterSpirit));
        Assert.Equal(4, waterSpirit.Level);
        Assert.Equal(CombatElement.Water, waterSpirit.Stats.Element);
        var skill = Assert.Single(waterSpirit.Skills);
        Assert.Equal("ability.ganlu", skill.Ability.GetProperty("id").GetString());
        // 掉落投放与技能槽解耦：水灵只投放《掌心雷》秘籍/残卷（对齐普通小怪 0.1%/2.0% 爆率），
        // 但技能槽恒为甘露——投放绝不允许反向装配到怪物身上。
        Assert.Contains(waterSpirit.DropTable, d => d.ItemId.Value == "item.tome.palm-lightning" && d.DropChance == 0.001);
        Assert.Contains(waterSpirit.DropTable, d => d.ItemId.Value == "item.fragment.palm-lightning" && d.DropChance == 0.02);
        Assert.DoesNotContain(waterSpirit.Skills, s => s.Ability.GetProperty("id").GetString() == "ability.palm-lightning");
        var ability = catalog.Abilities["ability.ganlu"];
        SkillTriggerEvaluator.ValidateAbility(ability);

        var stats1 = CombatantStats.Create(1000, 0, 0, 100) with { SpellAttack = 25, MaxManapoints = 1000 };
        var stats2 = CombatantStats.Create(1000, 0, 0, 100) with { SpellAttack = 52, MaxManapoints = 1000 };
        var targetStats = CombatantStats.Create(1000, 0, 0, 100) with { MaxManapoints = 1000 };
        var enemy = new CombatantTemplate(CombatantStats.Create(100000, 0, 0, 100));

        var t1 = new CombatantTemplate(stats1, [new SkillSlot(ability, 1)]);
        var t2 = new CombatantTemplate(stats2, [new SkillSlot(ability, 1)]);
        var targetTemplate = new CombatantTemplate(targetStats);

        var encounter = CombatEncounterState.CreateAllied("ganlu-test", 42, [t1, t2, targetTemplate], 0, [enemy]);
        encounter.PlayerAllies[1].Manapoints = 0; // t2 初始无蓝，不参与起手施法
        encounter.PlayerAllies[2].Hitpoints = 200;
        DaoHua.Ecs.Systems.CombatActSystem.DecideInitialActions(encounter);

        // t1 吟唱 1.5s 完成，推进至 1.6s
        DeterministicCombatEngine.AdvanceTo(encounter, 1.6);
        var buff = Assert.Single(encounter.PlayerAllies[2].Buffs, b => b.BuffId == "buff.ganlu");
        Assert.Equal(3, buff.Stacks);
        Assert.Equal(11.5, buff.ExpiresAtSeconds, 3);
        Assert.Equal(5.0, buff.HpRegen);

        // 推进到 5.0s 触发第 1 次周期回复：3 层 * 5 点 = 15 HP，200 -> 215
        DeterministicCombatEngine.AdvanceTo(encounter, 5.0 + 1e-9);
        Assert.Equal(215, encounter.PlayerAllies[2].Hitpoints, 3);

        // 验证 maxKeep 规则：
        // 1. 低层(3)遇到高层(6)：给 t2 补蓝并触发出手，吟唱 1.5s 完成后升级为 6 层，持续时间刷新
        var t2Caster = encounter.PlayerAllies[1];
        t2Caster.Manapoints = 1000;
        t2Caster.NextAttackAtSeconds = 6.0;
        DeterministicCombatEngine.AdvanceTo(encounter, 7.6);
        buff = Assert.Single(encounter.PlayerAllies[2].Buffs, b => b.BuffId == "buff.ganlu");
        Assert.Equal(6, buff.Stacks);
        var expiresAfterT2 = buff.ExpiresAtSeconds;
        Assert.True(expiresAfterT2 > 11.5);

        // 2. 高层(6)遇到低层(3)：让 t1（法攻25，只能施加3层）冷却完毕再次施法，层数保持 6 不变，持续时间继续刷新
        var t1Caster = encounter.PlayerAllies[0];
        t1Caster.Manapoints = 1000;
        if (t1Caster.SkillCooldownUntil is { Length: > 0 } cds)
            cds[0] = 0;
        t1Caster.NextAttackAtSeconds = 14.0;
        DeterministicCombatEngine.AdvanceTo(encounter, 15.6);
        buff = Assert.Single(encounter.PlayerAllies[2].Buffs, b => b.BuffId == "buff.ganlu");
        Assert.Equal(6, buff.Stacks);
        Assert.True(buff.ExpiresAtSeconds > expiresAfterT2);
    }

    /// <summary>
    /// 到期时刻与 5 秒回复网格重合时，最后一跳必须照常入账：
    /// 到期时刻本身属于 Buff 生效区间（授予写入 ElapsedSeconds + duration），
    /// 若先剔除到期 Buff 再结算网格，10 秒「每秒每层 1 血」会退化成只跳 1 次。
    /// </summary>
    [Fact]
    public void BuffRegenTickOnExpiryBoundaryStillLands()
    {
        var enemy = new CombatantTemplate(CombatantStats.Create(100_000, 0, 0, 100));
        var ally = new CombatantTemplate(CombatantStats.Create(1000, 0, 0, 100) with { MaxManapoints = 1000 });
        var encounter = CombatEncounterState.CreateAllied("expiry-tick", 7, [ally], 0, [enemy]);
        var unit = encounter.PlayerAllies[0];
        unit.Hitpoints = 200;
        // 单层甘露：每 5 秒跳 5 点；到期恰好压在 10.0s 网格边界上。
        unit.Buffs.Add(new CombatBuffState("buff.ganlu", 10.0, 1, HpRegen: 5.0, Name: "甘露"));

        DeterministicCombatEngine.AdvanceTo(encounter, 10.0);

        Assert.Equal(210, unit.Hitpoints, 6);
        Assert.DoesNotContain(unit.Buffs, b => b.BuffId == "buff.ganlu");
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "技能", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        return player;
    }
}
