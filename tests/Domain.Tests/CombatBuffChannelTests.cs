namespace Domain.Tests;

/// <summary>
/// Buff 效果通道模型门禁（总纲 §7.9.5.3）：聚合缓存与逐条求和基准等价、
/// 受控变更入口的缓存失效完整性，以及 buff-stacks 触发条件的运行时求值口径。
/// </summary>
public sealed class CombatBuffChannelTests
{
    private static CombatantState UnitWithBuffs(params CombatBuffState[] buffs)
    {
        var unit = new CombatantState("player",
            CombatantStats.Create(1000, 50, 10, 2, accuracy: 100, evasion: 5)
                with { MagicResistance = 10 });
        foreach (var buff in buffs) unit.AddBuff(buff);
        return unit;
    }

    /// <summary>
    /// 聚合缓存等价性：PerStack 通道按层数放大、Instance 通道（双抗破防）不放大，
    /// 各通道合计与手算基准逐位一致——防止聚合实现（StackMode/累加序）回归。
    /// </summary>
    [Fact]
    public void BuffTotalsMatchPerChannelExpectations()
    {
        var unit = UnitWithBuffs(
            new CombatBuffState("buff.a", 10, 2,
                Effects: [new BuffEffect(BuffEffectChannel.AttackPower, 3),
                    new BuffEffect(BuffEffectChannel.HpRegen, 4)]),
            new CombatBuffState("buff.b", 10, 5,
                Effects: [new BuffEffect(BuffEffectChannel.ResistancePoints, -400)]));
        Assert.Equal(6, unit.BuffTotal(BuffEffectChannel.AttackPower));
        Assert.Equal(8, unit.BuffTotal(BuffEffectChannel.HpRegen));
        Assert.Equal(-400, unit.BuffTotal(BuffEffectChannel.ResistancePoints));
        // 折叠面板：攻力 +6，双抗 ×0.2（-400 点 Linear 收口）。
        Assert.Equal(56, unit.EffectiveStats.AttackPower);
        Assert.Equal(2, unit.EffectiveStats.Defense);
        Assert.Equal(2, unit.EffectiveStats.MagicResistance);
    }

    /// <summary>
    /// 受控入口失效完整性：SetBuff 覆盖、RemoveBuffsWhere 剔除、ReplaceBuffs 整体替换后，
    /// 聚合值与周期网格标志立即重建——防止 stale 缓存把过期 Buff 的增益残留在结算里。
    /// </summary>
    [Fact]
    public void ControlledMutationsInvalidateAggregatesAndTickFlags()
    {
        var unit = UnitWithBuffs(new CombatBuffState("buff.dot", 10,
            Effects: [new BuffEffect(BuffEffectChannel.HpRegen, -30)]));
        Assert.True(unit.BuffRegenTickPending);

        unit.SetBuff(new CombatBuffState("buff.dot", 10,
            Effects: [new BuffEffect(BuffEffectChannel.HpRegen, 0)]));
        Assert.False(unit.BuffRegenTickPending);

        unit.SetBuff(new CombatBuffState("buff.dot", 10,
            Effects: [new BuffEffect(BuffEffectChannel.AttackPower, 7)]));
        Assert.False(unit.BuffRegenTickPending);
        Assert.Equal(7, unit.BuffTotal(BuffEffectChannel.AttackPower));

        unit.RemoveBuffsWhere(buff => buff.BuffId == "buff.dot");
        Assert.Equal(0, unit.BuffTotal(BuffEffectChannel.AttackPower));

        unit.ReplaceBuffs([new CombatBuffState("buff.second", 10,
            Effects: [new BuffEffect(BuffEffectChannel.HpRegenPerSecond, 1)])]);
        Assert.True(unit.BuffSecondTickPending);
        Assert.False(unit.BuffRegenTickPending);
    }

    /// <summary>
    /// 快照往返保通道载荷：Export→Restore 后 EffectValue 与层数完整保留，
    /// 且恢复单位立即读到正确聚合（防止快照恢复遗漏失效的回归）。
    /// </summary>
    [Fact]
    public void SnapshotRoundTripPreservesEffectPayload()
    {
        var unit = UnitWithBuffs(new CombatBuffState("buff.carry", 30, 3,
            Name: "试炼", Effects: [new BuffEffect(BuffEffectChannel.Accuracy, 2),
                new BuffEffect(BuffEffectChannel.Critical, 1.5)]));
        var snapshot = CombatEncounterState.Snapshot(unit);
        var encounter = CombatEncounterState.Restore("restore.test", 7,
            new CombatantTemplate(unit.Stats), snapshot, [new CombatantTemplate(CombatantStats.Create(500, 10, 0, 2))],
            [CombatEncounterState.Snapshot(new CombatantState("enemy:0", CombatantStats.Create(500, 10, 0, 2)))],
            5, CombatOutcome.Running);
        var player = encounter.Player;
        var carry = Assert.Single(player.Buffs);
        Assert.Equal(2, carry.EffectValue(BuffEffectChannel.Accuracy));
        Assert.Equal(1.5, carry.EffectValue(BuffEffectChannel.Critical));
        Assert.Equal(3, carry.Stacks);
        Assert.Equal(106, player.EffectiveStats.Accuracy);
    }

    /// <summary>
    /// buff-stacks 运行时求值：战内层数（含 perCaster 后缀 buffId:caster 前缀归并）
    /// 与宿主注入的战外视图合并——触发条件「目标身上无该 Buff」的判定口径
    /// （此前无直接单测，防止前缀归并或外部视图合并被误删）。
    /// </summary>
    [Fact]
    public void BuffStacksMergeCarryAndExternalViewsWithPerCasterPrefix()
    {
        var unit = UnitWithBuffs(
            new CombatBuffState("buff.ganlu:enemy:0", 10, 3),
            new CombatBuffState("buff.ganlu", 10, 2));
        unit.ExternalBuffStacks = buffId => buffId == "buff.ganlu" ? 5 : 0;
        // 指定 buffId：战内同 ID + perCaster 前缀条目 + 战外视图合并。
        Assert.Equal(10, unit.BuffStacks("buff.ganlu"));
        // 聚合口径（buffId 为 null）：全部战内层数（外部视图按 null 查询恒 0）。
        Assert.Equal(5, unit.BuffStacks(null));
    }
}
