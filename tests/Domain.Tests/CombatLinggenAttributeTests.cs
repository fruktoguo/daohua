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

        // 金系：增伤与减伤各 +100 点（HeavenGateRules 每点 1 点）。
        Assert.Equal(baseline.MetalDamageBonusPoints + 100, resolved.MetalDamageBonusPoints, 9);
        Assert.Equal(baseline.MetalDamageReductionPoints + 100, resolved.MetalDamageReductionPoints, 9);
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

        Assert.Equal(baseline.MetalDamageBonusPoints + 20, resolved.MetalDamageBonusPoints, 9);
        Assert.Equal(baseline.WoodDamageBonusPoints + 40, resolved.WoodDamageBonusPoints, 9);
        Assert.Equal(baseline.WaterDamageBonusPoints + 60, resolved.WaterDamageBonusPoints, 9);
        Assert.Equal(baseline.FireDamageBonusPoints + 80, resolved.FireDamageBonusPoints, 9);
        Assert.Equal(baseline.EarthDamageBonusPoints + 100, resolved.EarthDamageBonusPoints, 9);
        Assert.Equal(baseline.MetalDamageReductionPoints + 20, resolved.MetalDamageReductionPoints, 9);
        Assert.Equal(baseline.WoodDamageReductionPoints + 40, resolved.WoodDamageReductionPoints, 9);
        Assert.Equal(baseline.WaterDamageReductionPoints + 60, resolved.WaterDamageReductionPoints, 9);
        Assert.Equal(baseline.FireDamageReductionPoints + 80, resolved.FireDamageReductionPoints, 9);
        Assert.Equal(baseline.EarthDamageReductionPoints + 100, resolved.EarthDamageReductionPoints, 9);
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

        Assert.Equal(baseline.FireDamageBonusPoints + 100, resolved.FireDamageBonusPoints, 9);
        Assert.Equal(baseline.FireDamageReductionPoints + 100, resolved.FireDamageReductionPoints, 9);
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

        Assert.Equal(before.MetalDamageBonusPoints + 100, after.MetalDamageBonusPoints, 9);
        Assert.Equal(before.MetalDamageReductionPoints + 100, after.MetalDamageReductionPoints, 9);

        // 逆天改命重洗：灵根从金系全数改判木系，旧面板不得残留。
        player.Progression.HeavenGate.Apply(new LinggenValues(0, 100, 0, 0, 0), new LinggenValues(0, 100, 0, 0, 0),
            entered: true, rerollCount: 1);
        var rerolled = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(before.MetalDamageBonusPoints, rerolled.MetalDamageBonusPoints, 9);
        Assert.Equal(before.WoodDamageBonusPoints + 100, rerolled.WoodDamageBonusPoints, 9);
        Assert.Equal(before.WoodDamageReductionPoints + 100, rerolled.WoodDamageReductionPoints, 9);
    }

    /// <summary>
    /// 100 点灵根的真实增伤表现：同一名玩家的面板在取得 100 点金灵根前后仅差五行点数，
    /// 攻方 100 点金系增伤（Linear）令伤害 ×2，守方 100 点金系减伤（NonLinear）令伤害 ×0.5。
    /// 断言落在战斗引擎的实际伤害数值上，而不仅是面板点数。
    /// </summary>
    [Fact]
    public void HundredLinggenPointsDoubleBonusAndHalveReductionInCombat()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var plain = CreatePlayer(catalog);
        var plainPanel = PlayerAttributeResolver.Resolve(plain, catalog);
        var linggened = CreatePlayer(catalog);
        EnterHeavenGate(linggened, new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0));
        var panel = PlayerAttributeResolver.Resolve(linggened, catalog);
        // 两份面板仅五行点数不同：同境界、同装备（无装备）、同六维，比值因此只反映灵根贡献。
        Assert.Equal(100, panel.MetalDamageBonusPoints, 9);
        Assert.Equal(100, panel.MetalDamageReductionPoints, 9);
        // 点位并入与曲线语义在同一条链路上一并锁定：100 点减伤 → NonLinear 100/(100+100) = 0.5。
        Assert.Equal(0.5, PercentValue.Multiplier(panel.MetalDamageReductionPoints, PercentCurveMode.NonLinear), 9);

        // 减伤侧：守方 100 点金系减伤 → NonLinear 100/(100+100) = ×0.5。
        var bareAttacker = CombatantStats.Create(1_000, 1_000, 0, 1) with { Element = CombatElement.Metal };
        var unreduced = DeterministicCombatEngine.ResolveStrikeDamage(bareAttacker, new CombatantState("plain", plainPanel.ToCombatant(0)));
        var reduced = DeterministicCombatEngine.ResolveStrikeDamage(bareAttacker, new CombatantState("linggen", panel.ToCombatant(0)));
        Assert.True(unreduced > 0);
        Assert.Equal(0.5, reduced / unreduced, 9);

        // 增伤侧：攻方 100 点金系增伤 → Linear 1+100/100 = ×2（守方为同一具未开天门面板）。
        var target = new CombatantState("target", plainPanel.ToCombatant(0));
        var plainDamage = DeterministicCombatEngine.ResolveStrikeDamage(plainPanel.ToCombatant(0) with { Element = CombatElement.Metal }, target);
        var linggenDamage = DeterministicCombatEngine.ResolveStrikeDamage(panel.ToCombatant(0) with { Element = CombatElement.Metal }, target);
        Assert.True(plainDamage > 0);
        Assert.Equal(2.0, linggenDamage / plainDamage, 9);
    }
}
