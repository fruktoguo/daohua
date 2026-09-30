namespace Domain.Tests;

/// <summary>
/// 团灭分流测试：原地复活倒地窗口（怪物停手、回复/冷却照走、吟唱打断退还、DoT 跳杀照常判胜）
/// 与团灭即败终局两个分支的引擎级行为门禁。
/// </summary>
public sealed class CombatPartyWipeTests
{
    /// <summary>
    /// 倒地窗口内敌方停手但时钟照走：敌方出手计数冻结、玩家气血锁 0、灵力回复照常入账；
    /// 复活点起身仅回满气血——灵力保留死亡期累计值、已完成技能的冷却不打折。
    /// </summary>
    [Fact]
    public void DownedWindowFreezesEnemyActionsButKeepsRegenAndCooldownsRunning()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var stats = PlayerAttributeResolver.Resolve(CreatePlayer(catalog), catalog).ToCombatant(0)
            with { MaxManapoints = 1000, MpRegen = 2 };
        var ability = catalog.Abilities["ability.heavy-slash"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        var enemy = CombatantStats.Create(100_000, 1_000, 0, 2);
        var encounter = DeterministicCombatEngine.Start("downed", 5, playerTemplate, [new CombatantTemplate(enemy)],
            playerRespawnSeconds: 10);

        // t=1 玩家完成吟唱（预扣 10 灵力、冷却预排至 19）；t=2 敌方出手击杀玩家 → 倒地窗口至 t=12。
        var downed = DeterministicCombatEngine.AdvanceTo(encounter, 3);
        Assert.True(encounter.IsPlayerDowned);
        Assert.Equal(CombatOutcome.Running, encounter.Outcome);
        Assert.Equal(12, encounter.PlayerRespawnAtSeconds, 9);
        Assert.Equal(2, Assert.Single(downed.PlayerDeathTimestamps!), 9);
        Assert.Equal(0, encounter.Player.Hitpoints);
        Assert.Equal(990, encounter.Player.Manapoints);
        Assert.True(encounter.Enemies[0].IsAlive);
        Assert.Equal(1, encounter.Enemies[0].AttackCount);

        // 倒地窗口推进到复活前：敌方出手计数冻结（无存活目标停手），回复网格照常结算。
        DeterministicCombatEngine.AdvanceTo(encounter, 11.5);
        Assert.Equal(1, encounter.Enemies[0].AttackCount);
        Assert.Equal(0, encounter.Player.Hitpoints);
        Assert.Equal(994, encounter.Player.Manapoints, 9);

        // 复活点起身：仅气血回满，灵力不重置、技能冷却保持原到期时刻（19s > 12s → 回落普攻蓄势）。
        var respawned = DeterministicCombatEngine.AdvanceTo(encounter, 12.5);
        Assert.False(encounter.IsPlayerDowned);
        Assert.Equal(stats.MaxHitpoints, encounter.Player.Hitpoints);
        Assert.Equal(12, Assert.Single(respawned.PlayerRespawnTimestamps!), 9);
        Assert.Equal(994, encounter.Player.Manapoints, 9);
        Assert.Equal(19, encounter.Player.SkillCooldownUntil![0], 6);
        Assert.Equal(-1, encounter.Player.PendingSkillIndex);
        Assert.Equal(CombatOutcome.Running, encounter.Outcome);
    }

    /// <summary>
    /// 团灭瞬间全场吟唱作废：未完成的吟唱视为未释放——退还起手预扣灵力、预排冷却清空、
    /// 不产生施放事件；敌方吟唱单位同口径处理。
    /// </summary>
    [Fact]
    public void PartyWipeInterruptsAllPendingCastsAndRefundsMana()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var stats = PlayerAttributeResolver.Resolve(CreatePlayer(catalog), catalog).ToCombatant(0)
            with { MaxManapoints = 1000 };
        var ability = catalog.Abilities["ability.heavy-slash"];
        var playerTemplate = new CombatantTemplate(stats, [new SkillSlot(ability, 1)]);
        // 敌方双单位：A 以 0.4s 普攻周期击杀玩家；B 吟唱长技能（1s × CastTimeFactor 10 = 10s）未及释放。
        var killer = CombatantStats.Create(100_000, 1_000, 0, 0.4);
        var caster = CombatantStats.Create(100_000, 1, 0, 100) with { MaxManapoints = 100, CastTimeFactor = 10 };
        var encounter = DeterministicCombatEngine.Start("interrupt", 7, playerTemplate,
            [new CombatantTemplate(killer), new CombatantTemplate(caster, [new SkillSlot(ability, 1)])],
            playerRespawnSeconds: 30);

        // 开局决策：玩家与 B 均预扣灵力进入吟唱并预排冷却。
        Assert.Equal(0, encounter.Player.PendingSkillIndex);
        Assert.Equal(990, encounter.Player.Manapoints);
        Assert.True(encounter.Player.SkillCooldownUntil![0] > 0);
        Assert.Equal(0, encounter.Enemies[1].PendingSkillIndex);
        Assert.Equal(90, encounter.Enemies[1].Manapoints);

        var result = DeterministicCombatEngine.AdvanceTo(encounter, 0.5);
        // t=0.4 玩家阵亡：双方吟唱作废——退还预扣灵力、预排冷却清空、不产生施放事件。
        Assert.True(encounter.IsPlayerDowned);
        Assert.Equal(30.4, encounter.PlayerRespawnAtSeconds, 9);
        Assert.Equal(-1, encounter.Player.PendingSkillIndex);
        Assert.Equal(1_000, encounter.Player.Manapoints);
        Assert.Equal(0, encounter.Player.SkillCooldownUntil![0]);
        Assert.Equal(-1, encounter.Enemies[1].PendingSkillIndex);
        Assert.Equal(100, encounter.Enemies[1].Manapoints);
        Assert.Equal(0, encounter.Enemies[1].SkillCooldownUntil![0]);
        Assert.DoesNotContain(result.Events, entry => entry.AbilityId == "ability.heavy-slash");
    }

    /// <summary>
    /// 倒地窗口内玩家方持续伤害跳死全部敌人不算败局：复活点起身后统一裁定为 PlayerWon。
    /// </summary>
    [Fact]
    public void DownedWindowDotKillStillAwardsVictoryAfterRevive()
    {
        var playerStats = CombatantStats.Create(100, 10, 0, 2);
        var enemy = CombatantStats.Create(50, 1_000, 0, 2);
        var encounter = DeterministicCombatEngine.Start("dot", 11,
            new CombatantTemplate(playerStats), [new CombatantTemplate(enemy)], playerRespawnSeconds: 10);
        // 玩家阵亡前挂出的持续伤害：战斗时钟每 5s 结算 30 点（负回复即 DoT），无到期时刻。
        encounter.Enemies[0].Buffs.Add(new CombatBuffState("dot.test", double.PositiveInfinity, 1, HpRegen: -30));

        var result = DeterministicCombatEngine.AdvanceTo(encounter, 20);
        // t=2 玩家阵亡 → t=5/10 DoT 两次结算跳死敌方 → t=12 起身后统一裁定。
        Assert.Equal(CombatOutcome.PlayerWon, encounter.Outcome);
        Assert.False(encounter.Enemies[0].IsAlive);
        Assert.Equal(encounter.Player.Stats.MaxHitpoints, encounter.Player.Hitpoints);
        Assert.Equal(12, Assert.Single(result.PlayerRespawnTimestamps!), 9);
    }

    /// <summary>
    /// 团灭即败（playerRespawnSeconds=0，对应配表 onPartyWipe=defeat）：玩家方全灭即 EnemyWon 终局，
    /// 不产生倒地窗口与复活点，善后由模式编排层处理。
    /// </summary>
    [Fact]
    public void DefeatRuleMakesPartyWipeTerminal()
    {
        var player = new CombatantTemplate(CombatantStats.Create(100, 10, 0, 2));
        var enemy = CombatantStats.Create(1_000, 1_000, 0, 2);
        var encounter = DeterministicCombatEngine.Start("defeat", 13, player, [new CombatantTemplate(enemy)],
            playerRespawnSeconds: 0);

        var result = DeterministicCombatEngine.AdvanceTo(encounter, 5);
        Assert.Equal(CombatOutcome.EnemyWon, encounter.Outcome);
        Assert.Equal(CombatOutcome.EnemyWon, result.Outcome);
        Assert.False(encounter.IsPlayerDowned);
        Assert.True(double.IsPositiveInfinity(encounter.PlayerRespawnAtSeconds));
        Assert.True(result.PlayerDeathTimestamps is null || result.PlayerDeathTimestamps.Count == 0);
        Assert.True(result.PlayerRespawnTimestamps is null || result.PlayerRespawnTimestamps.Count == 0);
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "团灭", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        return player;
    }
}
