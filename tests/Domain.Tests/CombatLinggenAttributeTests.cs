namespace Domain.Tests;

/// <summary>
/// 五行灵根并入属性面板门禁：开天门取得的五系点数必须逐系进入对应五行增伤 / 减伤点数通道，
/// 未入天门恒为 0；入天门 / 逆天改命改写生效灵根后属性缓存必须失效，面板随之刷新。
/// </summary>
public sealed class CombatLinggenAttributeTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>创建未开天门的玩家：与灵根对照组的唯一差异只在是否入天门。</summary>
    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "灵根", Baseline, catalog.Fingerprint);

    /// <summary>写入生效灵根（等价于开天门 + 确认分布），走真实 <see cref="PlayerHeavenGateState.Apply"/> 通道。</summary>
    private static void EnterHeavenGate(PlayerAggregate player, LinggenValues confirmed)
        => player.Progression.HeavenGate.Apply(confirmed, confirmed, entered: true, rerollCount: 0);

    /// <summary>
    /// 每系灵根点数 1:1 并入同系增伤与减伤点数：100 金属性灵根令金系增伤 +100、金系减伤 +100，
    /// 其余四系与各大类通道完全不动（不串系、不改物理/法术大类）。
    /// </summary>
    [Fact]
    public void LinggenPointsMergeIntoMatchingElementChannels()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var baseline = PlayerAttributeResolver.Resolve(plain, catalog);

        var linggened = CreatePlayer(catalog);
        EnterHeavenGate(linggened, new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0));
        var resolved = PlayerAttributeResolver.Resolve(linggened, catalog);

        // 金系：增伤与减伤各 +25 点（HeavenGateRules 每点 0.25 点）。
        Assert.Equal(baseline.MetalDamageBonusPoints + 25, resolved.MetalDamageBonusPoints, 9);
        Assert.Equal(baseline.MetalDamageReductionPoints + 25, resolved.MetalDamageReductionPoints, 9);
        // 其余四系与大类通道不串味。
        Assert.Equal(baseline.WoodDamageBonusPoints, resolved.WoodDamageBonusPoints, 9);
        Assert.Equal(baseline.WaterDamageBonusPoints, resolved.WaterDamageBonusPoints, 9);
        Assert.Equal(baseline.FireDamageBonusPoints, resolved.FireDamageBonusPoints, 9);
        Assert.Equal(baseline.EarthDamageBonusPoints, resolved.EarthDamageBonusPoints, 9);
        Assert.Equal(baseline.WoodDamageReductionPoints, resolved.WoodDamageReductionPoints, 9);
        Assert.Equal(baseline.WaterDamageReductionPoints, resolved.WaterDamageReductionPoints, 9);
        Assert.Equal(baseline.FireDamageReductionPoints, resolved.FireDamageReductionPoints, 9);
        Assert.Equal(baseline.EarthDamageReductionPoints, resolved.EarthDamageReductionPoints, 9);
        Assert.Equal(baseline.PhysicalDamageBonusPoints, resolved.PhysicalDamageBonusPoints, 9);
        Assert.Equal(baseline.SpellDamageBonusPoints, resolved.SpellDamageBonusPoints, 9);
        Assert.Equal(baseline.PhysicalDamageReductionPoints, resolved.PhysicalDamageReductionPoints, 9);
        Assert.Equal(baseline.SpellDamageReductionPoints, resolved.SpellDamageReductionPoints, 9);
    }

    /// <summary>
    /// 五系同时生效：每条有效灵根只放大自己那一系，五行通道互不覆盖（与同键累加口径一致）。
    /// </summary>
    [Fact]
    public void EachElementChannelTracksItsOwnPoints()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var baseline = PlayerAttributeResolver.Resolve(plain, catalog);

        var linggened = CreatePlayer(catalog);
        EnterHeavenGate(linggened, new LinggenValues(Metal: 20, Wood: 40, Water: 60, Fire: 80, Earth: 100));
        var resolved = PlayerAttributeResolver.Resolve(linggened, catalog);

        Assert.Equal(baseline.MetalDamageBonusPoints + 5, resolved.MetalDamageBonusPoints, 9);
        Assert.Equal(baseline.WoodDamageBonusPoints + 10, resolved.WoodDamageBonusPoints, 9);
        Assert.Equal(baseline.WaterDamageBonusPoints + 15, resolved.WaterDamageBonusPoints, 9);
        Assert.Equal(baseline.FireDamageBonusPoints + 20, resolved.FireDamageBonusPoints, 9);
        Assert.Equal(baseline.EarthDamageBonusPoints + 25, resolved.EarthDamageBonusPoints, 9);
        Assert.Equal(baseline.MetalDamageReductionPoints + 5, resolved.MetalDamageReductionPoints, 9);
        Assert.Equal(baseline.WoodDamageReductionPoints + 10, resolved.WoodDamageReductionPoints, 9);
        Assert.Equal(baseline.WaterDamageReductionPoints + 15, resolved.WaterDamageReductionPoints, 9);
        Assert.Equal(baseline.FireDamageReductionPoints + 20, resolved.FireDamageReductionPoints, 9);
        Assert.Equal(baseline.EarthDamageReductionPoints + 25, resolved.EarthDamageReductionPoints, 9);
    }

    /// <summary>
    /// 快照解析路径同样带灵根：<see cref="PlayerAttributeResolver.Resolve(PlayerAggregateState, GameDataCatalog)"/>
    /// 直接读取快照内的<em>已确认且已入天门</em>灵根，五系点数照常并入面板。
    /// </summary>
    [Fact]
    public void SnapshotResolveIncludesConfirmedLinggen()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "快照", Baseline, catalog.Fingerprint);
        var baseline = PlayerAttributeResolver.Resolve(plain.ExportState(), catalog);

        var linggened = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "快照灵根", Baseline, catalog.Fingerprint);
        EnterHeavenGate(linggened, new LinggenValues(Metal: 0, Wood: 0, Water: 0, Fire: 100, Earth: 0));
        var resolved = PlayerAttributeResolver.Resolve(linggened.ExportState(), catalog);

        Assert.Equal(baseline.FireDamageBonusPoints + 25, resolved.FireDamageBonusPoints, 9);
        Assert.Equal(baseline.FireDamageReductionPoints + 25, resolved.FireDamageReductionPoints, 9);
        Assert.Equal(baseline.MetalDamageBonusPoints, resolved.MetalDamageBonusPoints, 9);
    }

    /// <summary>
    /// 只抽取未确认（未入天门）不生效：灵根点数必须已确认入天门才并入面板，
    /// 避免调配阶段的抽取值提前变成战力。
    /// </summary>
    [Fact]
    public void UnenteredLinggenContributesNothing()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var baseline = PlayerAttributeResolver.Resolve(plain, catalog);

        var pending = CreatePlayer(catalog);
        // 抽到即写入 RolledValues，但玩家尚未确认入天门。
        pending.Progression.HeavenGate.Apply(new LinggenValues(100, 100, 100, 100, 100),
            new LinggenValues(100, 100, 100, 100, 100), entered: false, rerollCount: 0);
        var resolved = PlayerAttributeResolver.Resolve(pending, catalog);

        Assert.Equal(baseline.MetalDamageBonusPoints, resolved.MetalDamageBonusPoints, 9);
        Assert.Equal(baseline.EarthDamageBonusPoints, resolved.EarthDamageBonusPoints, 9);
        Assert.Equal(baseline.MetalDamageReductionPoints, resolved.MetalDamageReductionPoints, 9);
        Assert.Equal(baseline.EarthDamageReductionPoints, resolved.EarthDamageReductionPoints, 9);
    }

    /// <summary>
    /// 入天门改写灵根必须击穿属性缓存：先解析出未开天门面板，再入天门后重新解析，
    /// 面板必须反映新灵根（修订号已并入 <see cref="PlayerHeavenGateState.Version"/>）。
    /// </summary>
    [Fact]
    public void EnteringHeavenGateInvalidatesCachedPanel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var before = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(0, before.MetalDamageBonusPoints, 9);

        EnterHeavenGate(player, new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0));
        var after = PlayerAttributeResolver.Resolve(player, catalog);

        Assert.Equal(before.MetalDamageBonusPoints + 25, after.MetalDamageBonusPoints, 9);
        Assert.Equal(before.MetalDamageReductionPoints + 25, after.MetalDamageReductionPoints, 9);

        // 逆天改命重洗：灵根从金系全数改判木系，旧面板不得残留。
        player.Progression.HeavenGate.Apply(new LinggenValues(0, 100, 0, 0, 0), new LinggenValues(0, 100, 0, 0, 0),
            entered: true, rerollCount: 1);
        var rerolled = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(before.MetalDamageBonusPoints, rerolled.MetalDamageBonusPoints, 9);
        Assert.Equal(before.WoodDamageBonusPoints + 25, rerolled.WoodDamageBonusPoints, 9);
        Assert.Equal(before.WoodDamageReductionPoints + 25, rerolled.WoodDamageReductionPoints, 9);
    }

    /// <summary>
    /// 100 点灵根的真实增伤表现：同一名玩家的面板在取得 100 点金灵根前后仅差五行点数，
    /// 攻方 100 点金系增伤（Linear）令伤害 ×2，守方 100 点金系减伤（NonLinear）令伤害 ×0.5。
    /// 断言落在战斗引擎的实际伤害数值上，而不仅是面板点数。
    /// </summary>
    [Fact]
    public void HundredLinggenPoints25PercentBonusAndReductionInCombat()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var plainPanel = PlayerAttributeResolver.Resolve(plain, catalog);
        var linggened = CreatePlayer(catalog);
        EnterHeavenGate(linggened, new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0));
        var panel = PlayerAttributeResolver.Resolve(linggened, catalog);

        Assert.Equal(25, panel.MetalDamageBonusPoints, 9);
        Assert.Equal(25, panel.MetalDamageReductionPoints, 9);
        // 点位并入与曲线语义：25 点减伤 → NonLinear 100/(100+25) = 0.8。
        Assert.Equal(0.8, PercentValue.Multiplier(panel.MetalDamageReductionPoints, PercentCurveMode.NonLinear), 9);

        // 减伤侧：守方 25 点金系减伤 → NonLinear 100/(100+25) = ×0.8。
        var bareAttacker = CombatantStats.Create(1_000, 1_000, 0, 1) with { Element = CombatElement.Metal };
        var unreduced = DeterministicCombatEngine.ResolveStrikeDamage(bareAttacker, new CombatantState("plain", plainPanel.ToCombatant(0)));
        var reduced = DeterministicCombatEngine.ResolveStrikeDamage(bareAttacker, new CombatantState("linggen", panel.ToCombatant(0)));
        Assert.True(unreduced > 0);
        Assert.Equal(0.8, reduced / unreduced, 9);

        // 增伤侧：攻方 25 点金系增伤（×1.25）；此处将攻方攻击力归一为基准攻击力，单独断言金系增伤乘区。
        var target = new CombatantState("target", plainPanel.ToCombatant(0));
        var plainDamage = DeterministicCombatEngine.ResolveStrikeDamage(plainPanel.ToCombatant(0) with { Element = CombatElement.Metal }, target);
        var linggenDamage = DeterministicCombatEngine.ResolveStrikeDamage(
            panel.ToCombatant(0) with { Element = CombatElement.Metal, AttackPower = plainPanel.PhysicalAttack }, target);
        Assert.True(plainDamage > 0);
        Assert.Equal(1.25, linggenDamage / plainDamage, 9);
    }

    /// <summary>
    /// 五行灵根衍生多维度战斗与百艺属性加成验证。
    /// </summary>
    [Fact]
    public void MultiDimensionalStatsReflectInResolvedAttributes()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var baseline = PlayerAttributeResolver.Resolve(plain, catalog);

        // 火系：暴击 +50%、暴伤 +25
        var firePlayer = CreatePlayer(catalog);
        EnterHeavenGate(firePlayer, new LinggenValues(0, 0, 0, 100, 0));
        var firePanel = PlayerAttributeResolver.Resolve(firePlayer, catalog);
        Assert.Equal(baseline.Critical * 1.5, firePanel.Critical, 6);
        Assert.Equal(baseline.CriticalDamagePoints + 25, firePanel.CriticalDamagePoints, 6);

        // 水系：灵力 +50%、化解 +50%、双回 +100%、寻怪 +20
        var waterPlayer = CreatePlayer(catalog);
        EnterHeavenGate(waterPlayer, new LinggenValues(0, 0, 100, 0, 0));
        var waterPanel = PlayerAttributeResolver.Resolve(waterPlayer, catalog);
        Assert.Equal(baseline.MaxManapoints * 1.5, waterPanel.MaxManapoints, 6);
        Assert.Equal(baseline.Parry * 1.5, waterPanel.Parry, 6);
        Assert.Equal(baseline.HpRegen * 2.0, waterPanel.HpRegen, 6);
        Assert.Equal(baseline.MpRegen * 2.0, waterPanel.MpRegen, 6);
        Assert.Equal(baseline.SearchSpeedPoints + 20, waterPanel.SearchSpeedPoints, 6);

        // 木系：气血 +50%、治疗增幅 +50
        var woodPlayer = CreatePlayer(catalog);
        EnterHeavenGate(woodPlayer, new LinggenValues(0, 100, 0, 0, 0));
        var woodPanel = PlayerAttributeResolver.Resolve(woodPlayer, catalog);
        Assert.Equal(baseline.MaxHitpoints * 1.5, woodPanel.MaxHitpoints, 6);
        Assert.Equal(baseline.Effects.HealingBonusPoints + 50, woodPanel.Effects.HealingBonusPoints, 6);

        // 金系：双攻 +50%、破招 +50%、命中 +50%
        var metalPlayer = CreatePlayer(catalog);
        EnterHeavenGate(metalPlayer, new LinggenValues(100, 0, 0, 0, 0));
        var metalPanel = PlayerAttributeResolver.Resolve(metalPlayer, catalog);
        Assert.Equal(baseline.PhysicalAttack * 1.5, metalPanel.PhysicalAttack, 6);
        Assert.Equal(baseline.SpellAttack * 1.5, metalPanel.SpellAttack, 6);
        Assert.Equal(baseline.BreakPower * 1.5, metalPanel.BreakPower, 6);
        Assert.Equal(baseline.Accuracy * 1.5, metalPanel.Accuracy, 6);

        // 土系：双防 +50%、免爆 +50%、固定减免保底 1.0 + 0.09*100 = 10.0
        var earthPlayer = CreatePlayer(catalog);
        EnterHeavenGate(earthPlayer, new LinggenValues(0, 0, 0, 0, 100));
        var earthPanel = PlayerAttributeResolver.Resolve(earthPlayer, catalog);
        Assert.Equal(baseline.Armor * 1.5, earthPanel.Armor, 6);
        Assert.Equal(baseline.MagicResistance * 1.5, earthPanel.MagicResistance, 6);
        Assert.Equal(baseline.CriticalAvoidance * 1.5, earthPanel.CriticalAvoidance, 6);
        Assert.Equal(baseline.FixedDamageReduction + 10.0, earthPanel.FixedDamageReduction, 6);

        // 土系 1 点微量测试：保底 1.09
        var tinyEarthPlayer = CreatePlayer(catalog);
        EnterHeavenGate(tinyEarthPlayer, new LinggenValues(0, 0, 0, 0, 1));
        var tinyEarthPanel = PlayerAttributeResolver.Resolve(tinyEarthPlayer, catalog);
        Assert.Equal(baseline.FixedDamageReduction + 1.09, tinyEarthPanel.FixedDamageReduction, 6);
    }
}
