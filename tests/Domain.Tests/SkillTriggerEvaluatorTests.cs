using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 统一技能触发器求值器门禁：玩家/怪物/斗法镜像共用同一套 dependency/condition/comparator
/// 词汇与求值语义；非法词表在装载期拒绝，运行期求值失败一律按条件不满足处理（不抛异常）。
/// </summary>
public sealed class SkillTriggerEvaluatorTests
{
    private static JsonElement Trigger(string dependency, string condition, string comparator, double value,
        string? buffId = null)
        => JsonDocument.Parse(
            $"{{\"dependency\":\"{dependency}\",\"condition\":\"{condition}\",\"comparator\":\"{comparator}\",\"value\":{value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)},\"buffId\":{(buffId is null ? "null" : $"\"{buffId}\"")}}}")
            .RootElement.Clone();

    private static JsonElement Ability(string extra)
        => JsonDocument.Parse(
            $"{{\"id\":\"ability.test\",\"category\":\"physical\",\"element\":\"none\",\"castMilliseconds\":1000,\"cooldownMilliseconds\":0,\"damageRatio\":1,\"manaCost\":0{extra}}}")
            .RootElement.Clone();

    /// <summary>
    /// 依赖目标按真实语义解析：self/target-enemy/友军与敌方最低最高生命单位各自独立求值，
    /// 不得把非 self 依赖折叠为集火目标。
    /// </summary>
    [Fact]
    public void DependenciesResolveAgainstRealUnits()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1);
        var enemyLow = CombatantStats.Create(400, 10, 0, 1);
        var enemyHigh = CombatantStats.Create(800, 10, 0, 1);
        var templates = (IReadOnlyList<CombatantTemplate>)[new(enemyLow), new(enemyHigh)];
        // 血量经快照注入：enemies[0] 50%，enemies[1] 绝对值 600（75%）。
        var encounter = CombatEncounterState.Restore("trigger", 3, new CombatantTemplate(player),
            new CombatantSnapshot(1000, 0, 0, 0, 0), templates,
            [new CombatantSnapshot(200, 0, 0, 0, 0), new CombatantSnapshot(600, 0, 0, 0, 0)],
            0, CombatOutcome.Running);
        var allies = (IReadOnlyList<CombatantState>)[encounter.Player];
        var opponents = (IReadOnlyList<CombatantState>)encounter.Enemies;

        // enemy-lowest-hp → enemies[0]（百分比最低 50%）；enemy-highest-hp → enemies[1]（绝对值 600 最高）。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy-lowest-hp", "current-hp-percentage", "less-than-equal", 60)],
            encounter.Player, encounter.Enemies[0], allies, opponents));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy-lowest-hp", "current-hp-percentage", "less-than-equal", 40)],
            encounter.Player, encounter.Enemies[0], allies, opponents));
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy-highest-hp", "current-hp", "greater-than-equal", 600)],
            encounter.Player, encounter.Enemies[0], allies, opponents));
        // ally-lowest-hp 解析施放方友军（此处仅玩家自身，100% 气血）。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("ally-lowest-hp", "current-hp-percentage", "greater-than-equal", 99)],
            encounter.Player, encounter.Enemies[0], allies, opponents));

        // target-enemy 解析集火目标；无存活集火目标时条件直接判定失败。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("target-enemy", "current-hp", "greater-than-equal", 1)],
            encounter.Player, encounter.Enemies[0], allies, opponents));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("target-enemy", "current-hp", "greater-than-equal", 1)],
            encounter.Player, null, allies, opponents));
    }

    /// <summary>
    /// buff-stacks 条件按 buffId 读取战内 Buff 层数（含宿主注入的战外视图）；
    /// is-active/is-inactive 以层数是否为正作活性判定。
    /// </summary>
    [Fact]
    public void BuffStacksReadFromCombatBuffsAndStatusConditions()
    {
        var stats = CombatantStats.Create(1000, 10, 0, 1);
        var unit = new CombatantState("player", stats);
        unit.Buffs.Add(new CombatBuffState("buff.war-song", 30, Stacks: 2));
        var peers = (IReadOnlyList<CombatantState>)[unit];

        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "buff-stacks", "greater-than-equal", 2, "buff.war-song")],
            unit, null, peers, peers));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "buff-stacks", "greater-than-equal", 3, "buff.war-song")],
            unit, null, peers, peers));
        // buff-stacks 为数值类型：层数存在与否按大小等于表达（>=1 / equal 0）。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "buff-stacks", "greater-than-equal", 1, "buff.war-song")], unit, null, peers, peers));
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "buff-stacks", "equal", 0, "buff.other")], unit, null, peers, peers));
        // 控制类预留词表为 Boolean 条件（当前恒为 0 层）：is-inactive 成立，is-active 不成立。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "stun-status", "is-inactive", 0)], unit, null, peers, peers));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "stun-status", "is-active", 0)], unit, null, peers, peers));
    }

    /// <summary>
    /// 集合对象（友方/敌方）群体聚合条件：alive-count/dead-count 对集合求值，
    /// 单体对象不得使用群体类型；集合对象不得使用单体状态条件（装载期矩阵校验）。
    /// </summary>
    [Fact]
    public void SetDependenciesEvaluateGroupAggregatesAndEnforceScopeMatrix()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1);
        var enemy = CombatantStats.Create(400, 10, 0, 1);
        var encounter = CombatEncounterState.Restore("set-dep", 3, new CombatantTemplate(player),
            new CombatantSnapshot(1000, 0, 0, 0, 0), [new CombatantTemplate(enemy), new CombatantTemplate(enemy), new CombatantTemplate(enemy)],
            [new CombatantSnapshot(200, 0, 0, 0, 0), new CombatantSnapshot(0, 0, 0, 0, 0), new CombatantSnapshot(300, 0, 0, 0, 0)],
            0, CombatOutcome.Running);
        var allies = (IReadOnlyList<CombatantState>)[encounter.Player];
        var opponents = (IReadOnlyList<CombatantState>)encounter.Enemies;

        // enemy 集合：存活 2、阵亡 1。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy", "alive-count", "equal", 2)], encounter.Player, null, allies, opponents));
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy", "dead-count", "greater-than-equal", 1)], encounter.Player, null, allies, opponents));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("enemy", "alive-count", "less-than", 2)], encounter.Player, null, allies, opponents));
        // ally 集合：仅玩家自身存活。
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("ally", "alive-count", "equal", 1)], encounter.Player, null, allies, opponents));

        // 作用域矩阵：单体对象不可用群体聚合类型；集合对象不可用单体状态类型。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "alive-count", "equal", 1)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("target-enemy", "dead-count", "equal", 1)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("ally", "current-hp-percentage", "less-than-equal", 50)));
        // Boolean 条件只接受是否比对；数值条件不接受是否比对。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "stun-status", "equal", 1)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "buff-stacks", "is-active", 0, "buff.x")));
        // 合法组合：单体数值 equal / 集合数值比对 / Boolean 是否。
        SkillTriggerEvaluator.ValidateTrigger(Trigger("self", "current-hp", "equal", 100));
        SkillTriggerEvaluator.ValidateTrigger(Trigger("enemy", "alive-count", "less-than-equal", 2));
        SkillTriggerEvaluator.ValidateTrigger(Trigger("target-enemy", "stun-status", "is-inactive", 0));
    }

    /// <summary>
    /// 词表外输入：装载期 ValidateTrigger 拒绝，运行期 MatchesAll 判定失败而不抛异常。
    /// </summary>
    [Fact]
    public void InvalidVocabularyRejectedAtValidationAndFailsClosedAtRuntime()
    {
        var stats = CombatantStats.Create(1000, 10, 0, 1);
        var unit = new CombatantState("player", stats);
        var peers = (IReadOnlyList<CombatantState>)[unit];

        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("party-pet", "current-hp", "less-than-equal", 50)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "current-rage", "less-than-equal", 50)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "current-hp", "approximately", 50)));
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "current-hp", "less-than-equal", -1)));
        // buff-stacks 必须显式声明 buffId。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "buff-stacks", "greater-than-equal", 1)));

        // 运行期兜底：词表外触发器一律判定失败，绝不抛异常打断模拟。
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("party-pet", "current-hp", "less-than-equal", 50)], unit, null, peers, peers));
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "current-hp", "approximately", 50)], unit, null, peers, peers));
    }

    /// <summary>
    /// 统一功法校验：无可执行效果、非法目标类型、非法伤害类别、非法默认触发器与
    /// 无持续时长的 Buff 一律在装载期拒绝。
    /// </summary>
    [Fact]
    public void ValidateAbilityRejectsUnexecutableAbilities()
    {
        // 零效果技能（无伤害/治疗/Buff/威胁修正）。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            Ability(",\"damageRatio\":0")));
        // 非法目标类型。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            Ability(",\"targetType\":\"enemy-party\"")));
        // 非法伤害类别。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            JsonDocument.Parse("{\"id\":\"x\",\"category\":\"divine\",\"castMilliseconds\":1000,\"damageRatio\":1}").RootElement));
        // 缺施放周期（cast+cooldown 全零）。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            JsonDocument.Parse("{\"id\":\"x\",\"category\":\"physical\",\"damageRatio\":1}").RootElement));
        // Buff 缺正持续时长。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            Ability(",\"targetType\":\"allAllies\",\"damageRatio\":0,\"buff\":{\"durationSeconds\":0,\"damageAbsorptionRatio\":0.1}")));
        // 非法默认触发器。
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateAbility(
            Ability(",\"defaultTriggers\":[{\"dependency\":\"ghost\",\"condition\":\"current-hp\",\"comparator\":\"less-than-equal\",\"value\":1}]")));
        // 合法技能正常通过。
        SkillTriggerEvaluator.ValidateAbility(Ability(""));
    }

    /// <summary>
    /// 怪物技能槽解析统一折叠：triggerHpRatio 简写折叠为等价 self 气血百分比触发器，
    /// 槽位 triggers 优先于配表 defaultTriggers；三种来源共用同一词表校验。
    /// </summary>
    [Fact]
    public void MonsterSkillSlotsFoldLegacyRatioAndHonorExplicitTriggers()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        // enemy.blade-corpse：heavy-slash(1.0) / heavy-clouds(0.75) / taunt(0.5) 均为 triggerHpRatio 简写。
        var corpse = catalog.CombatMonsters["enemy.blade-corpse"];
        var taunt = corpse.Skills[2];
        var folded = Assert.Single(taunt.Triggers);
        Assert.Equal("self", JsonReadProbe.Text(folded, "dependency"));
        Assert.Equal("current-hp-percentage", JsonReadProbe.Text(folded, "condition"));
        Assert.Equal("less-than-equal", JsonReadProbe.Text(folded, "comparator"));
        Assert.Equal(50, JsonReadProbe.Number(folded, "value"));

        // 语义验证：残刃尸煞血量压到 40%（低于嘲讽门槛 50%）后，嘲讽按槽位顺序轮到即施放。
        var tank = CombatantStats.Create(10_000_000, 100, 0, 1) with { MaxManapoints = 1000 };
        var state = CombatEncounterState.Restore("fold", 5, new CombatantTemplate(tank),
            new CombatantSnapshot(tank.MaxHitpoints, 1000, 0, 0, 0),
            [new CombatantTemplate(corpse.Stats, corpse.Skills)],
            [new CombatantSnapshot(corpse.Stats.MaxHitpoints * 0.4, corpse.Stats.MaxManapoints, 0, 0, 0)],
            0, CombatOutcome.Running);
        var result = DeterministicCombatEngine.AdvanceTo(state, 60);
        Assert.Contains(result.Events, item => item.AbilityId == "ability.taunt");
    }

    /// <summary>
    /// 斗法镜像与玩家装配共用同一技能槽模型：镜像技能触发条件与装配口径完全一致，
    /// 且推演引擎与远征共用同一施放管线（事件携带功法 ID）。
    /// </summary>
    [Fact]
    public void DuelMirrorSharesTheSameSkillSlotModel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "镜像", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        player.Loadout.AbilitySlots.Add(new("ability.heavy-slash", 1));
        var mirror = player.BuildDuelMirror(catalog);
        var slot = Assert.Single(mirror.Skills);
        // 装配缺省触发条件折叠为配表 defaultTriggers，与怪物槽位同构。
        Assert.Equal("target-enemy", JsonReadProbe.Text(Assert.Single(slot.Triggers), "dependency"));

        var defender = new DuelCombatant(PlayerId.New(), "对手", 0,
            CombatantStats.Create(10_000, 50, 0, 1), [], [], []);
        var initiator = mirror with { Stats = mirror.Stats with { MaxHitpoints = 10_000 } };
        var script = DuelScriptEngine.Resolve(initiator, defender, 42, catalog.Duel);
        Assert.Contains(script.Events, item => item.AbilityId == "ability.heavy-slash");
    }

    /// <summary>
    /// allEnemies AoE：按威胁顺序不可重复抽取至多 maxTargets 个目标，每个目标独立结算一次伤害事件。
    /// </summary>
    [Fact]
    public void AllEnemiesSkillHitsUpToMaxTargetsDistinctEnemies()
    {
        var ability = Ability(",\"targetType\":\"allEnemies\",\"maxTargets\":2,\"cooldownMilliseconds\":10000");
        var caster = CombatantStats.Create(1000, 200, 0, 1) with { MaxManapoints = 100 };
        var enemy = CombatantStats.Create(100_000, 0, 0, 100);
        var encounter = DeterministicCombatEngine.Start("aoe", 9,
            new CombatantTemplate(caster, [new SkillSlot(ability, 1)]),
            [new CombatantTemplate(enemy), new CombatantTemplate(enemy), new CombatantTemplate(enemy)]);
        var result = DeterministicCombatEngine.AdvanceTo(encounter, 2);
        var hits = result.Events.Where(item => item.AbilityId == "ability.test").ToArray();
        Assert.Equal(2, hits.Length);
        Assert.Equal(2, hits.Select(item => item.TargetId).Distinct().Count());
    }

    /// <summary>
    /// 基准点数口径门禁：怪物配表属性解析走 CombatGrowthCurve 基准注入（攻速/行动/冷却/普攻/暴伤/技能增幅），
    /// 漂移即破防；玩家侧特殊属性为获取型——无装备/内功等来源时各通道恒为 0，仅暴伤通道保留基础 50 点。
    /// </summary>
    [Fact]
    public void MonsterBaselineGrowthCurve_PlayerReceivesNoInjection()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var spirit = catalog.CombatMonsters["enemy.wood-spirit"];
        Assert.Equal(CombatGrowthCurve.ActionSpeedPoints(spirit.Level), spirit.Stats.ActionSpeedPoints, 12);
        Assert.Equal(CombatGrowthCurve.AttackSpeedPoints(spirit.Level), spirit.Stats.AttackSpeedPoints, 12);
        Assert.Equal(CombatGrowthCurve.CooldownReductionPoints(spirit.Level), spirit.Stats.CooldownReductionPoints, 12);
        Assert.Equal(CombatGrowthCurve.BasicAttackBonusPoints(spirit.Level), spirit.Stats.BasicAttackBonusPoints, 12);
        Assert.Equal(CombatGrowthCurve.CriticalDamagePoints(spirit.Level), spirit.Stats.CriticalDamagePoints, 12);
        Assert.Equal(CombatGrowthCurve.SkillBonusPoints(catalog.BaseAttributes!.TierAt(spirit.Level)),
            spirit.Stats.SkillBonusPoints, 12);

        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "曲线", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        var stats = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(0);
        Assert.Equal(0, stats.ActionSpeedPoints);
        Assert.Equal(0, stats.AttackSpeedPoints);
        Assert.Equal(0, stats.CooldownReductionPoints);
        Assert.Equal(0, stats.BasicAttackBonusPoints);
        Assert.Equal(CombatGrowthCurve.CriticalDamageBasePoints, stats.CriticalDamagePoints, 12);
        Assert.Equal(0, stats.SkillBonusPoints);
    }

    /// <summary>
    /// 战内 Buff 随快照恢复：恢复后 Buff 列表、到期时刻与层数完全一致，后续事件序列不漂移。
    /// </summary>
    [Fact]
    public void CombatBuffsSurviveSnapshotRestore()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var stats = CombatantStats.Create(10_000_000, 200, 0, 1) with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.pozhen-melody"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(10_000_000, 50, 0, 2);
        var whole = DeterministicCombatEngine.Start("buff-restore", 21, playerTemplate, [new CombatantTemplate(enemy)]);
        var split = DeterministicCombatEngine.Start("buff-restore", 21, playerTemplate, [new CombatantTemplate(enemy)]);
        var expected = DeterministicCombatEngine.AdvanceTo(whole, 20).Events;
        var first = DeterministicCombatEngine.AdvanceTo(split, 7).Events;
        Assert.NotEmpty(split.Player.Buffs);
        var restored = CombatEncounterState.Restore("buff-restore", split.Seed, playerTemplate,
            CombatEncounterState.Snapshot(split.Player),
            [new CombatantTemplate(enemy)],
            [CombatEncounterState.Snapshot(split.Enemies[0])],
            split.ElapsedSeconds, split.Outcome, split.Sequence);
        Assert.Equal(split.Player.Buffs, restored.Player.Buffs);
        var second = DeterministicCombatEngine.AdvanceTo(restored, 20).Events;
        Assert.Equal(expected, first.Concat(second));
    }

    /// <summary>
    /// equal 必须按浮点容差判定：气血百分比由除法得出（如 1/3 → 33.333…），
    /// 精确 == 会让玩家填写的阈值永不成立。容差宽度取自领域层单一常量。
    /// </summary>
    [Fact]
    public void EqualComparatorUsesFloatingTolerance()
    {
        // 1000/3000 → 33.333…%：阈值 33.333333 落在容差内应当成立。
        var stats = CombatantStats.Create(3000, 0, 0, 1) with { MaxManapoints = 0 };
        var unit = new CombatantState("player", stats);
        unit.Hitpoints = 1000;
        var peers = (IReadOnlyList<CombatantState>)[unit];

        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "current-hp-percentage", "equal", 100d / 3d)], unit, null, peers, peers));
        Assert.True(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "current-hp-percentage", "equal", 33.333333)], unit, null, peers, peers));
        // 超出容差的阈值仍判定不成立。
        Assert.False(SkillTriggerEvaluator.MatchesAll(
            [Trigger("self", "current-hp-percentage", "equal", 33.4)], unit, null, peers, peers));
        Assert.Equal(1e-6, SkillTriggerEvaluator.EqualTolerance);
    }

    /// <summary>
    /// 条数上限为单源常量：超过 MaxTriggersPerList 的列表在装载/命令期被拒绝，空表合法。
    /// </summary>
    [Fact]
    public void TriggerListRejectsExceedingCapAndAllowsEmpty()
    {
        var one = Trigger("self", "current-hp-percentage", "less-than-equal", 50);
        var cap = SkillTriggerEvaluator.MaxTriggersPerList;
        var exact = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat(one.GetRawText(), cap)) + "]");
        var over = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat(one.GetRawText(), cap + 1)) + "]");
        // 空数组 = 显式无条件，合法。
        SkillTriggerEvaluator.ValidateTriggerList(JsonDocument.Parse("[]").RootElement);
        SkillTriggerEvaluator.ValidateTriggerList(exact.RootElement);
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTriggerList(over.RootElement));
    }

    /// <summary>
    /// Buff 词表校验：传入词表时引用配表中不存在的 Buff 直接拒绝（此类条件层数恒为 0，
    /// 属静默死配置）；不传词表时保持只校验非空。
    /// </summary>
    [Fact]
    public void BuffStacksValidatesAgainstProvidedVocabulary()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "buff.zhixue" };
        SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "buff-stacks", "greater-than-equal", 1, "buff.zhixue"), known);
        Assert.Throws<DomainException>(() => SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "buff-stacks", "greater-than-equal", 1, "buff.qi-gathering"), known));
        // 未提供词表时不校验命中（保持既有调用点语义）。
        SkillTriggerEvaluator.ValidateTrigger(
            Trigger("self", "buff-stacks", "greater-than-equal", 1, "buff.qi-gathering"));
    }

    /// <summary>
    /// 条件列表三态语义：键存在即覆盖（空数组 = 玩家显式无条件，不回退配表默认），
    /// 键缺失才回退 defaultTriggers。这是「清空全部条件」能被正确表达的前提。
    /// </summary>
    [Fact]
    public void AbilitySlotTriggerOverrideDistinguishesMissingKeyFromEmptyArray()
    {
        var ability = JsonDocument.Parse(
            "{\"id\":\"ability.test\",\"category\":\"physical\",\"element\":\"none\",\"castMilliseconds\":1000,"
            + "\"cooldownMilliseconds\":0,\"damageRatio\":1,\"manaCost\":0,"
            + "\"defaultTriggers\":[{\"dependency\":\"self\",\"condition\":\"current-hp-percentage\","
            + "\"comparator\":\"less-than-equal\",\"value\":50}]}").RootElement.Clone();
        var abilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["ability.test"] = ability };
        var slots = (IReadOnlyList<PlayerAbilitySlot>)[new("ability.test", 1)];

        // 键缺失 → 回退配表出厂默认（1 条）。
        var fallback = SkillSlot.FromAbilitySlots(slots, abilities);
        Assert.Single(fallback);
        Assert.Single(fallback[0].Triggers);

        // 键存在且为空数组 → 显式无条件恒可用，绝不回退默认。
        var explicitNone = SkillSlot.FromAbilitySlots(slots, abilities,
            new Dictionary<string, JsonElement[]>(StringComparer.Ordinal) { ["ability.test"] = [] });
        Assert.Single(explicitNone);
        Assert.Empty(explicitNone[0].Triggers);

        // 键存在且非空 → 玩家配置覆盖默认。
        var custom = (JsonElement[])[Trigger("self", "current-mana-percentage", "less-than-equal", 20)];
        var overridden = SkillSlot.FromAbilitySlots(slots, abilities,
            new Dictionary<string, JsonElement[]>(StringComparer.Ordinal) { ["ability.test"] = custom });
        Assert.Single(overridden[0].Triggers);
        Assert.Equal("current-mana-percentage", JsonReadProbe.Text(overridden[0].Triggers[0], "condition"));
    }

    /// <summary>
    /// 消耗品条件解析同构三态：玩家按物品 ID 的配置覆盖配表默认，空数组表示无条件恒可用。
    /// </summary>
    [Fact]
    public void ConsumableTriggerResolverOverridesDefaultByItemId()
    {
        var factory = (JsonElement[])[Trigger("self", "current-hp-percentage", "less-than-equal", 30)];
        var consumable = new ConsumableDefinition(new HashSet<string>(StringComparer.Ordinal) { "combat" },
            CooldownSeconds: 10, Triggers: factory);

        // 玩家未配置该物品 → 配表出厂默认。
        Assert.Same(factory, ConsumableTriggerResolver.Resolve("item.pill", consumable, null));
        Assert.Same(factory, ConsumableTriggerResolver.Resolve("item.pill", consumable,
            new Dictionary<string, JsonElement[]>(StringComparer.Ordinal)));

        // 键存在且为空数组 → 显式无条件恒可用。
        Assert.Empty(ConsumableTriggerResolver.Resolve("item.pill", consumable,
            new Dictionary<string, JsonElement[]>(StringComparer.Ordinal) { ["item.pill"] = [] }));

        // 键存在且非空 → 玩家配置覆盖配表默认。
        JsonElement[] custom = [Trigger("self", "current-mana-percentage", "greater-than-equal", 70)];
        var resolved = ConsumableTriggerResolver.Resolve("item.pill", consumable,
            new Dictionary<string, JsonElement[]>(StringComparer.Ordinal) { ["item.pill"] = custom });
        Assert.Single(resolved);
        Assert.Equal("current-mana-percentage", JsonReadProbe.Text(resolved[0], "condition"));
    }
}

/// <summary>测试内 JSON 探针：仅用于断言配表折叠结果，与生产读取助手同口径。</summary>
file static class JsonReadProbe
{
    public static string? Text(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    public static double Number(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.TryGetDouble(out var number) ? number : 0;
}
