namespace Domain.Tests;

/// <summary>
/// 技艺消耗品端到端复现：真实内嵌配表下验证持续消耗品在炼丹行动的激活、加速与扣时，
/// 以及秘法行动（enhancement 大类）下强化成功率消耗品的生效守卫。
/// </summary>
public sealed class ConsumableReproTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (AssetKey Asset, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, balance.Asset, balance.Quantity, 0)));
        return wallet;
    }

    /// <summary>定神香在炼丹行动中应自动充能、扣件并按 30% 加速折减单轮耗时。</summary>
    [Fact]
    public void DingshenIncenseActivatesOnAlchemyAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("丹修");
        var dingshen = new ItemId("item.dingshen-incense");
        var grass = new ItemId("item.ningxue-grass");
        var wallet = CreateWallet(player,
            (new AssetKey(dingshen), 3),
            (new AssetKey(grass), 30));
        player.ConfigureAutoUse("alchemy", 0, dingshen.Value, 1, true, stackableSlotCount: 3);
        player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), -1), Baseline, catalog.Actions);

        // 单轮基准 12s；定神香 +30% 后约 9.23s。推进 10s 应完成至少一轮。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.True(result.CompletedRepeats > 0, $"未完成任何轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.True(wallet.Get(new AssetKey(dingshen)) < 3, "定神香未被消耗");
        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == dingshen.Value);
        Assert.True(slot.RemainingDurationSeconds > 0, $"RemainingDurationSeconds={slot.RemainingDurationSeconds}");
    }

    /// <summary>淬灵粉在装备强化行动上正常充能：消耗 1 件并充入 300 秒持续时长。</summary>
    [Fact]
    public void CuilingPowderActivatesOnEnhancementAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("器修");
        var cuiling = new ItemId("item.cuiling-powder");
        var sword = new ItemId("equipment.qingfeng-sword");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(sword), 1),
            (new AssetKey(stone), 500));
        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId(EnhancementRules.ActionId), -1, TargetItemId: sword.Value),
            Baseline, catalog.Actions);

        // +1 目标基础耗时 6s，推进 7s 应完成至少一轮强化。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(7), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.True(result.CompletedRepeats > 0,
            $"未完成强化轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.True(wallet.Get(new AssetKey(cuiling)) < 3, "淬灵粉未被消耗");
        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value);
        Assert.True(slot.RemainingDurationSeconds > 0,
            $"RemainingDurationSeconds={slot.RemainingDurationSeconds}");
    }

    /// <summary>
    /// 淬灵粉在秘法化灵行动上不得激活：强化成功率加成仅被装备强化结算消费，
    /// 秘法行动同属 enhancement 大类但无成功率判定，对其充能只会白扣物品与持续时长。
    /// </summary>
    [Fact]
    public void CuilingPowderDoesNotActivateOnSecretArtLiquefy()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("秘修");
        var cuiling = new ItemId("item.cuiling-powder");
        var hide = new ItemId("item.beast-hide");
        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(hide), 30));
        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("secret_art.liquefy"), -1, TargetItemId: hide.Value),
            Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 化灵本身正常结算，但淬灵粉不充能不扣件。
        Assert.True(result.CompletedRepeats > 0,
            $"未完成化灵轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.Equal(3, wallet.Get(new AssetKey(cuiling)));
        Assert.DoesNotContain(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value);
        Assert.Equal(0, slot.RemainingDurationSeconds);
    }

    /// <summary>强化加速后淬灵粉按实际耗时扣减持续时长，而非未加速的基础时长（5+t）。</summary>
    [Fact]
    public void CuilingPowderConsumesActualElapsedSecondsOnAcceleratedEnhancement()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("速修");
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement] = 10_000;
        var cuiling = new ItemId("item.cuiling-powder");
        var sword = new ItemId("equipment.qingfeng-sword");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(sword), 1),
            (new AssetKey(stone), 500));

        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        var action = player.Enqueue(
            new ActionIntent(new ActionId(EnhancementRules.ActionId), 1, TargetItemId: sword.Value),
            Baseline, catalog.Actions);

        var baseDuration = EnhancementRules.DurationSeconds(1);
        var actualDuration = TimedActionDurationRules.CalculateCore(
            baseDuration, ActionKind.Enhancement, TechniqueCatalog.Enhancement,
            player.World.EffectiveNodeBuildingEffects,
            player.Loadout.TechniqueExperiences, player.Loadout.Equipment, catalog);
        Assert.True(actualDuration < baseDuration, $"预期技艺加速生效: actual={actualDuration}, base={baseDuration}");

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(actualDuration + 0.05), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(1, result.CompletedRepeats);
        Assert.Equal(2, wallet.Get(new AssetKey(cuiling)));
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value);
        Assert.Equal(300d - actualDuration, slot.RemainingDurationSeconds, 6);

        // 技艺经验按未加速基础耗时（5+t）与全局 2× 倍率结算，同时伴生等额底蕴，避免加速反降经验。
        var itemLevel = CraftingRecipeRules.ResolveItemLevel(sword.Value, catalog);
        var expectedTechExp = TechniqueCatalog.ComputeTechniqueExperience(catalog, itemLevel, baseDuration);
        Assert.Equal(expectedTechExp, player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement] - 10_000, 9);
        Assert.Equal(expectedTechExp, player.Progression.Foundation, 9);
    }

    /// <summary>
    /// 淬灵粉与固灵砂均应可放入秘法（enhancement）槽位：
    /// 装备强化期间正常充能与扣减，切换执行化灵/分解/转化等其他秘法行动时不充能、不扣物品且冻结残余时长。
    /// </summary>
    [Fact]
    public void EnhancementBonusConsumablesCanBeAssignedToEnhancementBarButStayFrozenOnSecretArts()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("双修");
        var cuiling = new ItemId("item.cuiling-powder");
        var guling = new ItemId("item.guling-sand");
        var sword = new ItemId("equipment.qingfeng-sword");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var hide = new ItemId("item.beast-hide");
        var fragment = new ItemId("item.fragment.heavy-slash");

        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(guling), 3),
            (new AssetKey(sword), 2),
            (new AssetKey(stone), 500),
            (new AssetKey(hide), 10),
            (new AssetKey(fragment), 10));

        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        player.ConfigureAutoUse("enhancement", 1, guling.Value, 1, true);
        Assert.Equal(2, player.Settings.AutoUseSlots.Count(s => s.ActionType == AutoUseBar.Enhancement && s.Enabled));

        // 1. 先执行 1 轮装备强化，两个强化消耗品均应充能并按真实耗时扣减。
        var cursor = Baseline;
        player.Enqueue(new ActionIntent(new ActionId(EnhancementRules.ActionId), 1, TargetItemId: sword.Value),
            cursor, catalog.Actions);
        cursor = cursor.AddSeconds(7);
        var enhanceResult = SimulationEngine.AdvanceTo(player, cursor, catalog, wallet);
        enhanceResult.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(1, enhanceResult.CompletedRepeats);
        Assert.Equal(2, wallet.Get(new AssetKey(cuiling)));
        Assert.Equal(2, wallet.Get(new AssetKey(guling)));
        var cuilingRemaining = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value).RemainingDurationSeconds;
        var gulingRemaining = player.Settings.AutoUseSlots.Single(s => s.ItemId == guling.Value).RemainingDurationSeconds;
        Assert.InRange(cuilingRemaining, 290, 299.9);
        Assert.InRange(gulingRemaining, 290, 299.9);

        // 2. 携带残余时长依次执行化灵 / 分解 / 转化：均应正常结算，但槽位时长保持冻结且不扣消耗品。
        var secretArts = new (string ActionId, ActionIntent Intent, double AdvanceSeconds)[]
        {
            ("secret_art.liquefy", new ActionIntent(new ActionId("secret_art.liquefy"), 1, TargetItemId: hide.Value), 7),
            ("secret_art.disassemble", new ActionIntent(
                new ActionId("secret_art.disassemble"),
                1,
                TargetItemId: sword.Value,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 0 }), 8),
            ("secret_art.convert", new ActionIntent(new ActionId("secret_art.convert"), 1, TargetItemId: fragment.Value), 10),
        };

        foreach (var (actionId, intent, advanceSeconds) in secretArts)
        {
            player.Enqueue(intent, cursor, catalog.Actions);
            cursor = cursor.AddSeconds(advanceSeconds);
            var secretResult = SimulationEngine.AdvanceTo(player, cursor, catalog, wallet);
            secretResult.AssetDeltas.ToList().ForEach(wallet.Apply);

            Assert.Equal(1, secretResult.CompletedRepeats);
            Assert.DoesNotContain(secretResult.Events, e => e.Code is "auto-use.activated" or "auto-use.depleted");
            Assert.Equal(2, wallet.Get(new AssetKey(cuiling)));
            Assert.Equal(2, wallet.Get(new AssetKey(guling)));
            Assert.Equal(cuilingRemaining, player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value).RemainingDurationSeconds, 9);
            Assert.Equal(gulingRemaining, player.Settings.AutoUseSlots.Single(s => s.ItemId == guling.Value).RemainingDurationSeconds, 9);
        }
    }

    /// <summary>带速度加成的秘法持续消耗品按加速后的实际耗时扣减剩余时长。</summary>
    [Fact]
    public void SecretArtSpeedConsumableConsumesActualElapsedSeconds()
    {
        var embedded = GameDataCatalog.LoadEmbedded();
        var secretIncense = new ItemDefinition(
            new ItemId("item.test-secret-incense"),
            "测试秘香",
            "",
            true,
            100,
            new ConsumableDefinition(
                new HashSet<string> { AutoUseBar.Enhancement },
                DurationSeconds: 300,
                IsContinuous: true,
                TechniqueSpeedRatio: 0.25));
        var items = new Dictionary<ItemId, ItemDefinition>(embedded.Items) { [secretIncense.Id] = secretIncense };
        var liquefyAction = embedded.Actions.Get(new ActionId("secret_art.liquefy"));
        var catalog = new GameDataCatalog(
            embedded.ContentVersion,
            embedded.Fingerprint,
            new ActionCatalog([liquefyAction]),
            items: items,
            rawItems: embedded.RawItems,
            rawEquipments: embedded.RawEquipments,
            realmProgression: embedded.RealmProgression);

        var player = CreatePlayer("加速秘修");
        var hide = new ItemId("item.beast-hide");
        var wallet = CreateWallet(player,
            (new AssetKey(secretIncense.Id), 2),
            (new AssetKey(hide), 10));
        player.ConfigureAutoUse(AutoUseBar.Enhancement, 0, secretIncense.Id.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("secret_art.liquefy"), 1, TargetItemId: hide.Value),
            Baseline, catalog.Actions);

        // 基础 5s；25% 加速后单轮实际 4.0s。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4.05), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(1, result.CompletedRepeats);
        Assert.Equal(1, wallet.Get(new AssetKey(secretIncense.Id)));
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == secretIncense.Id.Value);
        Assert.Equal(300d - 4.0, slot.RemainingDurationSeconds, 6);
    }
}
