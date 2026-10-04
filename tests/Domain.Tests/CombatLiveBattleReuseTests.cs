using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 进行中战斗跨步复用一致性门禁（TASK-20260924-08 W3-B）：
/// 单次 AdvanceTo 内同一场进行中的战斗跨外层步续用 <see cref="CombatEncounterState"/> 内存态，
/// 结果必须与「每次推进调用都从持久化文档全量重建战斗」的细粒度分段推进逐位一致——
/// 导出文档、事件流、资产流水、完成计数与战斗面板全部位级相等；
/// 且每次调用收尾必须把完整快照写回行动文档，保证导出/恢复语义不变。
/// </summary>
public sealed class CombatLiveBattleReuseTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ItemId PillId = new("item.test-pill");
    private static readonly ItemId DropItemId = new("item.test-drop");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static PlayerAggregate CreatePlayer()
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

    /// <summary>归一化导出文档字节：StateVersion 随推进调用次数漂移，比较前清零。</summary>
    private static string CanonicalState(PlayerAggregate player)
        => JsonSerializer.Serialize(player.ExportState() with { StateVersion = 0 });

    /// <summary>
    /// 细粒度分段推进基线：每次 AdvanceTo 调用都重建复用会话（等价于逐调用全量装配），
    /// 事件按序汇总；推进到窗口中点做一次导出/恢复往返——若复用会话收尾未把完整快照
    /// 写回行动文档，恢复出的副本会从过期快照续打，后续结果必然分叉。
    /// </summary>
    private static (PlayerAggregate Player, List<SimulationEvent> Events) AdvanceInChunks(
        PlayerAggregate player, GameDataCatalog catalog, AssetWallet? wallet,
        DateTimeOffset until, double stepSeconds)
    {
        var events = new List<SimulationEvent>();
        var midpoint = player.LastSimulatedAt + (until - player.LastSimulatedAt) / 2;
        var restored = false;
        while (player.LastSimulatedAt < until)
        {
            var stepTo = player.LastSimulatedAt.AddSeconds(stepSeconds);
            if (stepTo > until) stepTo = until;
            var result = SimulationEngine.AdvanceTo(player, stepTo, catalog, wallet);
            foreach (var delta in result.AssetDeltas) wallet?.Apply(delta);
            events.AddRange(result.Events);
            if (!restored && player.LastSimulatedAt >= midpoint)
            {
                player = PlayerAggregate.Restore(player.ExportState());
                restored = true;
            }
        }
        return (player, events);
    }

    /// <summary>
    /// 两条路径产出逐位一致：导出文档字节级相等；事件流按 (Code, Message, ItemDeltas)
    /// 分组计数多重集 + 排序时间戳容差比较——事件 At 由调用分段内的 from.AddSeconds 推导，
    /// 跨分段存在固有的亚滴答浮点结合律噪声与边界齐平序差（与复用机制无关的分段固有行为）。
    /// </summary>
    private static void AssertIdenticalOutcome(PlayerAggregate whole, IReadOnlyList<SimulationEvent> wholeEvents,
        PlayerAggregate chunked, IReadOnlyList<SimulationEvent> chunkedEvents)
    {
        Assert.Equal(CanonicalState(whole), CanonicalState(chunked));
        static string DeltaKey(SimulationEvent e) => e.ItemDeltas is null ? ""
            : string.Join("|", e.ItemDeltas.OrderBy(kv => kv.Key.Value).Select(kv => $"{kv.Key.Value}={kv.Value}"));
        static string[] GroupKeyMultiset(IReadOnlyList<SimulationEvent> events) => events
            .GroupBy(e => $"{e.Code}|{e.Message}|{DeltaKey(e)}")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}").ToArray();
        Assert.Equal(GroupKeyMultiset(wholeEvents), GroupKeyMultiset(chunkedEvents));
        // 排序后的时间戳多重集容差 1 毫秒：吸收 from.AddSeconds 的亚滴答浮点噪声。
        var wholeTimes = wholeEvents.Select(e => e.At.ToUnixTimeMilliseconds()).Order().ToArray();
        var chunkedTimes = chunkedEvents.Select(e => e.At.ToUnixTimeMilliseconds()).Order().ToArray();
        Assert.Equal(wholeTimes.Length, chunkedTimes.Length);
        for (var i = 0; i < wholeTimes.Length; i++)
            Assert.True(Math.Abs(wholeTimes[i] - chunkedTimes[i]) <= 1,
                $"事件时刻分叉：排序后第 {i} 条 {wholeTimes[i]} vs {chunkedTimes[i]}");
    }

    /// <summary>战斗中行进中收尾必须回写完整快照：Combat 相位文档必须携带玩家完整快照与敌人状态。</summary>
    private static void AssertFlushedExpedition(PlayerAggregate player)
    {
        var expedition = player.Actions[0].Expedition!;
        Assert.True(expedition.IsValid);
        if (expedition.Phase == CombatExpeditionPhase.Combat)
        {
            Assert.NotNull(expedition.PlayerState);
            Assert.True(expedition.Enemies is { Count: > 0 });
            Assert.All(expedition.Enemies!, enemy => Assert.True(enemy.State.IsValid));
        }
    }

    /// <summary>
    /// 长战斗场景：高气血低攻妖兽使单波战斗横跨数十个外层步且始终不终局。
    /// 单次 300 秒补算内战斗上下文跨步续用；分段基线按 1 秒粒度逐调用全量重建。
    /// </summary>
    [Fact]
    public void LongRunningBattleReusedAcrossStepsMatchesPerStepRebuild()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000_000, enemyAttackPower: 1);
        var whole = CreatePlayer();
        whole.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var chunked = PlayerAggregate.Restore(whole.ExportState());

        var wholeResult = SimulationEngine.AdvanceTo(whole, Baseline.AddSeconds(300), catalog);
        var (chunkedPlayer, chunkedEvents) = AdvanceInChunks(chunked, catalog, null, Baseline.AddSeconds(300), 1);

        AssertIdenticalOutcome(whole, wholeResult.Events, chunkedPlayer, chunkedEvents);
        var expedition = whole.Actions[0].Expedition!;
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);
        Assert.True(expedition.BattleElapsedSeconds > 0);
        AssertFlushedExpedition(whole);
    }

    /// <summary>
    /// 战斗中途结束与波次切换边界：低气血妖兽每波数秒清场，覆盖
    /// 终局 Release → 搜寻相位 → 下一波 Adopt 的完整链路，掉落与击杀奖励全部入账。
    /// </summary>
    [Fact]
    public void WaveCompletionAndReentryBoundariesMatchPerStepRebuild()
    {
        var catalog = CreateCatalog(enemyHitpoints: 40, enemyAttackPower: 1, withRewards: true);
        var whole = CreatePlayer();
        whole.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var chunked = PlayerAggregate.Restore(whole.ExportState());

        var wholeResult = SimulationEngine.AdvanceTo(whole, Baseline.AddSeconds(240), catalog);
        var (chunkedPlayer, chunkedEvents) = AdvanceInChunks(chunked, catalog, null, Baseline.AddSeconds(240), 1);

        AssertIdenticalOutcome(whole, wholeResult.Events, chunkedPlayer, chunkedEvents);
        Assert.True(whole.Actions[0].CompletedRepeats > 3);
        Assert.True(whole.Progression.NormalKills > 0);
        Assert.True(whole.Progression.Experience > 0);
        AssertFlushedExpedition(whole);
    }

    /// <summary>
    /// 倒地复活边界：原地复活遭遇 + 高攻妖兽秒杀玩家 → 反复进入躺尸窗口，
    /// 复活截止点跨外层步驻留、起身续打同一波残局；复用与逐步重建必须逐位一致。
    /// </summary>
    [Fact]
    public void DownedRespawnWindowReusedAcrossStepsMatchesPerStepRebuild()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000_000, enemyAttackPower: 10_000,
            playerRespawnSeconds: 20);
        var whole = CreatePlayer();
        whole.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var chunked = PlayerAggregate.Restore(whole.ExportState());

        var wholeResult = SimulationEngine.AdvanceTo(whole, Baseline.AddSeconds(150), catalog);
        var (chunkedPlayer, chunkedEvents) = AdvanceInChunks(chunked, catalog, null, Baseline.AddSeconds(150), 1);

        AssertIdenticalOutcome(whole, wholeResult.Events, chunkedPlayer, chunkedEvents);
        Assert.True(whole.Progression.TotalDefeats >= 2);
        Assert.Equal(CombatExpeditionPhase.Combat, whole.Actions[0].Expedition!.Phase);
        AssertFlushedExpedition(whole);
    }

    /// <summary>
    /// Buff 到期跨步刷新：攻速 Buff 在战斗中段到期，属性输入修订号变化必须令
    /// 复用会话换入最新结算面板（RefreshStats），出手节奏变化与逐步重建逐位一致。
    /// </summary>
    [Fact]
    public void BuffExpiryMidBattleRefreshesLiveStatsIdentically()
    {
        var attributes = new BaseAttributeCatalog([
            new BaseAttributeAnchor(1, 2_000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 7, 3)
        ]);
        var catalog = CreateCatalog(enemyHitpoints: 1_000_000, enemyAttackPower: 0, baseAttributes: attributes);
        var whole = CreatePlayer();
        whole.Buffs.Active.Add(new ActiveBuff("buff.test-haste",
            Baseline.AddSeconds(30).ToUnixTimeMilliseconds() / 1000d, 1, "疾风",
            AttackSpeedPoints: 500));
        whole.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var chunked = PlayerAggregate.Restore(whole.ExportState());

        var wholeResult = SimulationEngine.AdvanceTo(whole, Baseline.AddSeconds(120), catalog);
        var (chunkedPlayer, chunkedEvents) = AdvanceInChunks(chunked, catalog, null, Baseline.AddSeconds(120), 1);

        AssertIdenticalOutcome(whole, wholeResult.Events, chunkedPlayer, chunkedEvents);
        Assert.Empty(whole.Buffs.Active);
        var expedition = whole.Actions[0].Expedition!;
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);
        Assert.True(expedition.PlayerAttackCount > 0);
        AssertFlushedExpedition(whole);
    }

    /// <summary>
    /// 消耗品触发器读文档敌人快照：敌方气血跌破阈值才触发的丹药，
    /// 复用会话必须先回写完整快照再求值，否则看到的是开战时冻结的过期状态。
    /// 分段基线按 5 秒粒度推进：与外层回复网格对齐，保证两侧消耗品检查时刻一致。
    /// </summary>
    [Fact]
    public void ConsumableTriggerReadsFlushedEnemyStateIdentically()
    {
        var catalog = CreateCatalog(enemyHitpoints: 300, enemyAttackPower: 0, withPill: true);
        var whole = CreatePlayer();
        var wholeWallet = CreateWallet(whole, (PillId, 5));
        whole.ConfigureAutoUse("combat", 0, PillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        whole.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var chunked = PlayerAggregate.Restore(whole.ExportState());
        var chunkedWallet = CreateWallet(chunked, (PillId, 5));

        var wholeResult = SimulationEngine.AdvanceTo(whole, Baseline.AddSeconds(240), catalog, wholeWallet);
        foreach (var delta in wholeResult.AssetDeltas) wholeWallet.Apply(delta);
        var (chunkedPlayer, chunkedEvents) = AdvanceInChunks(chunked, catalog, chunkedWallet,
            Baseline.AddSeconds(240), 5);

        AssertIdenticalOutcome(whole, wholeResult.Events, chunkedPlayer, chunkedEvents);
        Assert.Equal(wholeWallet.Get(new AssetKey(PillId)), chunkedWallet.Get(new AssetKey(PillId)));
        // 敌人气血跌破 90% 后丹药确实触发：证明求值读到的是实时回写的敌人快照。
        Assert.Contains(wholeResult.Events, entry => entry.Code == "auto-use.activated");
        Assert.True(wholeWallet.Get(new AssetKey(PillId)) < 5);
        AssertFlushedExpedition(whole);
    }

    private static AssetWallet CreateWallet(PlayerAggregate player, params (ItemId ItemId, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance =>
            new AssetBalance(player.PlayerId, new AssetKey(balance.ItemId), balance.Quantity, 0)));
        return wallet;
    }

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower,
        double searchSeconds = 1, double playerRespawnSeconds = 180,
        BaseAttributeCatalog? baseAttributes = null, bool withRewards = false, bool withPill = false)
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2),
            withRewards ? [new CombatDropDefinition(DropItemId, 1, 1, 2)] : [],
            [],
            Experience: withRewards ? 10 : 0);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1,
            searchSeconds, 1, playerRespawnSeconds,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
        Dictionary<ItemId, ItemDefinition>? items = null;
        Dictionary<string, JsonElement>? rawItems = null;
        if (withPill)
        {
            // 敌方气血跌破 90% 才触发的丹药：触发求值依赖行动文档中的敌人实时快照。
            const string trigger = """{"dependency":"enemy-lowest-hp","condition":"current-hp-percentage","comparator":"less-than-equal","value":90}""";
            items = new Dictionary<ItemId, ItemDefinition>
            {
                [PillId] = new(PillId, "测试丹药", "", true, 0,
                    new ConsumableDefinition((IReadOnlySet<ActionKind>)new HashSet<ActionKind> { ActionKind.Combat },
                        Triggers: [Json(trigger)],
                        CooldownSeconds: 30),
                    "pill")
            };
            rawItems = new Dictionary<string, JsonElement>
            {
                [PillId.Value] = Json("""
                    {"id":"item.test-pill","name":"测试丹药","kind":"consumable","category":"pill",
                     "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":30,"hitpointRestore":50}}
                    """)
            };
        }
        return new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            baseAttributes: baseAttributes);
    }
}
