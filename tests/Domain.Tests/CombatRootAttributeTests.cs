namespace Domain.Tests;

/// <summary>
/// 经脉灵力回复门禁：每点只提供 +2% 百分比回复，固定值通道已移除
/// （旧口径为每点 +1% 且 +0.5 固定值，总纲 §7.5）。
/// </summary>
public sealed class CombatRootAttributeTests
{
    /// <summary>
    /// 10 点经脉：面板灵力回复 = 基础回复 × (1 + 10 × 2%)，不再叠加 10 × 0.5 的固定值。
    /// </summary>
    [Fact]
    public void MeridianPointsScaleManaRegenByTwoPercentWithoutFlatBonus()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var realmProgression = catalog.RealmProgression
            ?? throw new InvalidOperationException("内嵌配表缺少境界修为表。");
        var baseAttributes = catalog.BaseAttributes
            ?? throw new InvalidOperationException("内嵌配表缺少基础属性锚点。");
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "经脉",
            DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        player.Loadout.CoreAttributes = new PlayerCoreAttributes(
            MeridiansExp: realmProgression.RootCumulativeExpFor(10));
        Assert.Equal(10, player.Loadout.CoreAttributes.Levels(realmProgression).Meridians, 9);

        var resolved = PlayerAttributeResolver.Resolve(player, catalog);
        var baseRegen = baseAttributes.At(player.Progression.RealmLevel).MpRegen;
        Assert.Equal(baseRegen * 1.2, resolved.MpRegen, 9);
        // 旧口径 (R + 5) × 1.1 必须不再成立。
        Assert.NotEqual((baseRegen + 5) * 1.1, resolved.MpRegen, 6);
    }
}
