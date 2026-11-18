using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Xunit;
using YuoCore.Ecs;

namespace Ecs.Tests;

/// <summary>
/// 领域、ECS 与计算节点核心修复回归测试：
/// 覆盖 CloseSystemTry 异常直抛、DirtyMarks 异常回滚保留、入战战外 Buff 先手时序、
/// 战内攻速 Buff 展示时长、秘法变体/穿戴等级匹配/富文本日志/跨区域确定性种子、
/// 以及组队阵盘 teamWide 与空槽隔离及 SourceUnitId 单人离线重映射。
/// </summary>
[Collection("ECS telemetry")]
public sealed class CoreFixesRegressionTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static PlayerStateBundle EncodeBundle(PlayerAggregate player, DateTimeOffset stamp)
    {
        var state = player.ExportState();
        var writes = SectionCodec.EncodeAggregateFull(state, stamp);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes,
                write.Checksum, write.UpdatedAt),
            StringComparer.Ordinal);
        return new PlayerStateBundle(
            new PlayerIdentity(state.PlayerId.Value, state.AccountId.Value, state.DisplayName, state.CreatedAt),
            new PlayerStateHead(state.PlayerId.Value, state.StateVersion, SectionCodec.CurrentSchemaVersion,
                state.LastSimulatedAt, state.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), stamp),
            sections,
            []);
    }

    private static PlayerAggregate CreateBrokenRanhunCombatPlayer(GameDataCatalog catalog, DateTimeOffset at)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "异常回滚试炼者", at, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        player.World.NodeId = "node.shuyu-brook";
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1), at, catalog.Actions);
        var monster = catalog.CombatMonsters["enemy.wood-spirit"];
        var brokenBuff = new CombatBuffState(
            CombatTraitRules.RanhunBuffId,
            ExpiresAtSeconds: 30,
            Stacks: 1,
            Name: "燃魂",
            ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.FireSpellDamagePerSecond, 5)],
            SourceUnitId: "ally:99");
        player.Actions[0].Expedition = new CombatExpeditionState(
            "encounter.qi-beast",
            CombatExpeditionPhase.Combat,
            PhaseElapsedSeconds: 0,
            PhaseDurationSeconds: 0,
            Seed: 17,
            BattleElapsedSeconds: 0,
            PlayerHitpoints: player.Resources.MaxHitpoints,
            PlayerManapoints: player.Resources.MaxManapoints,
            PlayerNextAttackAtSeconds: 5,
            PlayerAttackCount: 0,
            PlayerThreat: 0,
            Outcome: CombatOutcome.Running,
            WaveNumber: 1,
            Enemies:
            [
                new ExpeditionEnemyState(
                    monster.Id,
                    new CombatantSnapshot(
                        monster.Stats.MaxHitpoints,
                        monster.Stats.MaxManapoints,
                        NextAttackAtSeconds: 5,
                        AttackCount: 0,
                        Threat: 0,
                        Buffs: [brokenBuff]))
            ])
        {
            PlayerState = new CombatantSnapshot(player.Resources.MaxHitpoints, player.Resources.MaxManapoints, 5, 0, 0),
            IsPinned = true
        };
        return player;
    }

    /// <summary>枚举化灵确定性判定的动作实例 Id：completedRepeats 游标为结算时已完成轮次（首轮为 1）。</summary>
    private static Guid FindLiquefyActionId(double completedRepeats, double chance, bool wantSuccess)
    {
        for (var i = 1; i <= 100_000; i++)
        {
            var id = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x42);
            if (SecretArtRules.LiquefySucceeds(id, completedRepeats, chance) == wantSuccess) return id;
        }
        throw new InvalidOperationException("未在枚举窗口内命中预期化灵判定分支。");
    }

    private static Guid FindConvertActionId(long targetMs, bool wantTome)
    {
        for (var i = 1; i <= 10_000; i++)
        {
            var id = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x42);
            var seedText = FormattableString.Invariant($"{id:D}:1:{targetMs}");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
            var roll = (BitConverter.ToUInt32(hash, 0) % 10000) / 10000.0;
            if (SecretArtRules.FragmentProducesTome(roll) == wantTome) return id;
        }
        throw new InvalidOperationException("未在枚举窗口内命中预期残卷转化分支。");
    }

    /// <summary>冷补算引擎必须启用 CloseSystemTry，防止 ECS System 异常被内核吞并后回传伪造成功结果。</summary>
    [Fact]
    public async Task DeterministicCatchUpEngine_EnablesCloseSystemTryAndPropagatesSystemExceptions()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var engine = new DeterministicCatchUpEngine(catalog);
        Assert.True(YuoWorld.CloseSystemTry);

        var player = CreateBrokenRanhunCombatPlayer(catalog, SeedTime);
        var bundle = EncodeBundle(player, SeedTime);

        var ex = await Assert.ThrowsAsync<DomainException>(async () =>
            await engine.AdvanceAsync(bundle, [], SeedTime.AddSeconds(1), SeedTime.AddSeconds(1)));

        Assert.Equal(DomainErrorCode.InvariantViolation, ex.Code);
        Assert.Contains("持续伤害缺少战斗来源", ex.Message);
        Assert.True(YuoWorld.CloseSystemTry);
    }

    /// <summary>分片推进发生 System 异常回滚时，不仅还原 DirtySections，也必须完整保留既有 DirtyMarks 流水号。</summary>
    [Fact]
    public async Task EcsWorldShard_SystemExceptionRollsBackDirtyMarksWithoutDroppingHistory()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var player = CreateBrokenRanhunCombatPlayer(catalog, SeedTime);
        var id = runtime.CreatePlayer(player);

        runtime.MutatePlayer(id, aggregate => aggregate.SetLoopEnabled(true));
        var marksBefore = new Dictionary<PlayerSectionKind, long>(runtime.GetDirtyMarks(id));
        Assert.True(marksBefore.TryGetValue(PlayerSectionKind.Settings, out var settingsMark) && settingsMark > 0);
        var dirtyBefore = runtime.GetDirtySections(id).ToHashSet();
        Assert.Contains(PlayerSectionKind.Settings, dirtyBefore);

        var ex = await Assert.ThrowsAsync<DomainException>(async () =>
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(1)));

        Assert.Equal(DomainErrorCode.InvariantViolation, ex.Code);
        Assert.Equal(marksBefore, runtime.GetDirtyMarks(id));
        Assert.Equal(dirtyBefore, runtime.GetDirtySections(id).ToHashSet());
    }

    /// <summary>CreateEncounter 必须在 DecideInitialActions 之前注入战外 ActiveBuff，保证首拍攻速与条件求值生效。</summary>
    [Fact]
    public void CreateEncounter_InjectsOutOfBattleBuffsBeforeFirstActDecision()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var hasteBuff = new ActiveBuff(
            "buff.precombat.haste",
            SeedTime.AddSeconds(60).ToUnixTimeSeconds(),
            Stacks: 1,
            Name: "战前疾风",
            Effects: [new BuffEffect(BuffEffectChannel.AttackSpeedPoints, 100)]);

        var attackSpeedPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "攻速先手", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(attackSpeedPlayer, catalog);
        attackSpeedPlayer.Buffs.Active.Add(hasteBuff);

        var triggerPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "触发先手", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(triggerPlayer, catalog);
        triggerPlayer.Buffs.Active.Add(hasteBuff);
        triggerPlayer.LearnAbility(new AbilityId("ability.heavy-slash"));
        var customTrigger = JsonDocument.Parse(
            """{"dependency":"self","condition":"buff-stacks","comparator":"greater-than-equal","value":1,"buffId":"buff.precombat.haste"}""")
            .RootElement.Clone();
        triggerPlayer.ReplaceCombatSlots(
            [new PlayerAbilitySlot("ability.heavy-slash", 1)],
            [],
            new Dictionary<string, JsonElement[]> { ["ability.heavy-slash"] = [customTrigger] });

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var speedId = runtime.CreatePlayer(attackSpeedPlayer);
        runtime.CreateEncounter(speedId, "encounter.qi-beast", seed: 11);
        var speedEntity = runtime.World.GetEntity(runtime.ExportSnapshot(speedId).EntityId)!;
        var speedEncounter = speedEntity.GetComponent<PlayerCombatComponent>()!.Encounter!;

        var unbuffedStats = PlayerAttributeResolver.Resolve(attackSpeedPlayer.ExportState() with { Buffs = [] }, catalog)
            .ToCombatant(attackSpeedPlayer.Progression.CombatExperience);
        var expectedBuffedInterval = unbuffedStats.AttackIntervalSeconds
            * PercentValue.Multiplier(unbuffedStats.AttackSpeedPoints + 100, PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(unbuffedStats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        var unbuffedInterval = unbuffedStats.AttackIntervalSeconds
            * PercentValue.Multiplier(unbuffedStats.AttackSpeedPoints, PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(unbuffedStats.ActionSpeedPoints, PercentCurveMode.NonLinear);

        Assert.Equal(-1, speedEncounter.Player.PendingSkillIndex);
        Assert.Equal(expectedBuffedInterval, speedEncounter.Player.NextAttackAtSeconds, 9);
        Assert.True(speedEncounter.Player.NextAttackAtSeconds < unbuffedInterval);

        var triggerId = runtime.CreatePlayer(triggerPlayer);
        runtime.CreateEncounter(triggerId, "encounter.qi-beast", seed: 11);
        var triggerEntity = runtime.World.GetEntity(runtime.ExportSnapshot(triggerId).EntityId)!;
        var triggerEncounter = triggerEntity.GetComponent<PlayerCombatComponent>()!.Encounter!;

        Assert.Equal(0, triggerEncounter.Player.PendingSkillIndex);
    }

    /// <summary>CaptureCombatState 导出敌人与非本机盟友的 PendingActionDurationSeconds 时，必须计入战内攻速 Buff。</summary>
    [Fact]
    public void PendingActionDurationSeconds_IncludesInCombatAttackSpeedBuffs()
    {
        var baseStats = CombatantStats.Create(1000, 10, 0, attackIntervalSeconds: 4) with
        {
            AttackSpeedPoints = 0,
            ActionSpeedPoints = 0
        };
        var battle = CombatEncounterState.CreateAllied(
            "encounter.qi-beast",
            seed: 5,
            [new CombatantTemplate(baseStats, []), new CombatantTemplate(baseStats, [])],
            selfIndex: 0,
            [new CombatantTemplate(baseStats, [], DefinitionId: "enemy.wood-spirit")]);
        var inCombatHaste = new CombatBuffState(
            "buff.combat.haste",
            ExpiresAtSeconds: 60,
            Stacks: 1,
            Name: "战内疾风",
            Effects: [new BuffEffect(BuffEffectChannel.AttackSpeedPoints, 100)]);
        battle.PlayerAllies[1].AddBuff(inCombatHaste);
        battle.Enemies[0].AddBuff(inCombatHaste);

        var template = new CombatExpeditionState(
            "encounter.qi-beast",
            CombatExpeditionPhase.Combat,
            0, 0, 5, 0,
            battle.Player.Hitpoints, battle.Player.Manapoints,
            battle.Player.NextAttackAtSeconds, 0, 0, CombatOutcome.Running);

        var captured = SimulationEngine.CaptureCombatState(battle, template);
        Assert.Equal(2.0, captured.Enemies![0].PendingActionDurationSeconds, 9);
        Assert.Equal(2.0, captured.Allies![0].PendingActionDurationSeconds, 9);
    }

    /// <summary>排队版秘法行动回归：装备 +N 变体计数与计价、精确穿戴强化等级占用、禁止非装备带等级、富文本日志及 InvariantCulture 种子。</summary>
    [Fact]
    public async Task SecretArtQueue_SupportsVariantsExactEquippedOccupationAndRichLogMarkup()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var swordId = new ItemId("equipment.qingfeng-sword");
        var fragmentId = new ItemId("item.fragment.heavy-slash");

        // 1. +0 穿戴在身时，化灵 +3 剑必须放行并按 +3 计价且日志含 {{item:...}} +3
        var liquefyPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "化灵变体者", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(liquefyPlayer, catalog);
        // 秘法技艺等级高于物品等级（青锋剑配表 level=1）：化灵成功率恒 100%，保证本用例走成功分支。
        liquefyPlayer.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement] = 1_000_000;
        liquefyPlayer.ReplaceCombatSlots([], [new PlayerEquipmentSlot(swordId.Value, 0)]);
        var liquefyBalances = new[]
        {
            new AssetBalance(liquefyPlayer.PlayerId, new AssetKey(swordId), 1, 0),
            new AssetBalance(liquefyPlayer.PlayerId, new AssetKey(swordId, "+3"), 1, 0),
            new AssetBalance(liquefyPlayer.PlayerId, new AssetKey(swordId, "+2"), 1, 0)
        };
        using (var runtime = new EcsGameRuntime(catalog, SeedTime))
        {
            var id = runtime.CreatePlayer(liquefyPlayer.ExportState(), liquefyBalances);
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.LiquefyActionId),
                RepeatCount: 1,
                TargetItemId: swordId.Value,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 3 }));
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(5));

            var result = runtime.DrainSimulationResult(id)!;
            var expectedSpirit = SecretArtRules.ResolveRecycleUnitPrice(catalog, swordId.Value, 3) * SecretArtRules.LiquefactionMultiplier;
            Assert.Contains(result.AssetDeltas, d => d.Asset == new AssetKey(swordId, "+3") && d.QuantityDelta == -1);
            Assert.Contains(result.AssetDeltas, d => d.Asset.ItemId.Value == EnhancementRules.SpiritStoneItemId && d.QuantityDelta == expectedSpirit);

            var completed = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "secret_art.completed");
            Assert.Contains("{{item:equipment.qingfeng-sword|青锋剑}} +3", completed.Message);
            Assert.Contains("{{item:currency.spirit-stone|灵石|", completed.Message);
            Assert.DoesNotContain("返还原生材料", completed.Message);

            // 分解 +2 剑：扣除 +2 变体，返还材料并使用富文本物品标记
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.DisassembleActionId),
                RepeatCount: 1,
                TargetItemId: swordId.Value,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 2 }));
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(11));

            var disResult = runtime.DrainSimulationResult(id)!;
            var expectedReturns = SecretArtRules.ComputeDisassemblyReturns(catalog, swordId.Value)!;
            Assert.Contains(disResult.AssetDeltas, d => d.Asset == new AssetKey(swordId, "+2") && d.QuantityDelta == -1);
            foreach (var (matId, matCount) in expectedReturns)
                Assert.Contains(disResult.AssetDeltas, d => d.Asset == new AssetKey(matId) && d.QuantityDelta == matCount);

            var disEvent = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "secret_art.completed");
            Assert.Contains("{{item:equipment.qingfeng-sword|青锋剑}} +2", disEvent.Message);
            foreach (var matId in expectedReturns.Keys)
                Assert.Contains($"{{{{item:{matId.Value}|{catalog.Items[matId].Name}|", disEvent.Message);
        }

        // 2. 穿戴了 +3 剑且仅有 1 件 +3（另有大量 +0 剑）时，化灵 +3 必须被占用保护阻断
        var occupiedPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "穿戴保护者", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(occupiedPlayer, catalog);
        occupiedPlayer.ReplaceCombatSlots([], [new PlayerEquipmentSlot(swordId.Value, 3)]);
        using (var runtime = new EcsGameRuntime(catalog, SeedTime))
        {
            var id = runtime.CreatePlayer(occupiedPlayer.ExportState(),
            [
                new AssetBalance(occupiedPlayer.PlayerId, new AssetKey(swordId), 5, 0),
                new AssetBalance(occupiedPlayer.PlayerId, new AssetKey(swordId, "+3"), 1, 0)
            ]);
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.LiquefyActionId),
                RepeatCount: 1,
                TargetItemId: swordId.Value,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 3 }));
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(5));

            Assert.Equal(ActionQueueStatus.Blocked, runtime.ExportSnapshot(id).State.Actions[0].Status);
            var blocked = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "action.blocked");
            Assert.Contains("目标装备已穿戴或正处于强化锁定中", blocked.Message);
        }

        // 3. 普通材料或残卷转化携带 initialLevel > 0 一律拒绝
        var invalidLevelPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "非法等级校验", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(invalidLevelPlayer, catalog);
        using (var runtime = new EcsGameRuntime(catalog, SeedTime))
        {
            var id = runtime.CreatePlayer(invalidLevelPlayer.ExportState(),
            [
                new AssetBalance(invalidLevelPlayer.PlayerId, new AssetKey(new ItemId("item.beast-hide")), 2, 0),
                new AssetBalance(invalidLevelPlayer.PlayerId, new AssetKey(fragmentId), 2, 0)
            ]);
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.LiquefyActionId),
                RepeatCount: 1,
                TargetItemId: "item.beast-hide",
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 2 }));
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(5));

            Assert.Equal(ActionQueueStatus.Blocked, runtime.ExportSnapshot(id).State.Actions[0].Status);
            Assert.Contains(runtime.DrainSimulationEvents(id), e => e.Code == "action.blocked" && e.Message == "只有装备可指定强化等级。");

            runtime.MutatePlayer(id, p => p.RemoveAction(p.Actions[0].Id));
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.ConvertActionId),
                RepeatCount: 1,
                TargetItemId: fragmentId.Value,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 1 }));
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(10));

            Assert.Equal(ActionQueueStatus.Blocked, runtime.ExportSnapshot(id).State.Actions[0].Status);
            Assert.Contains(runtime.DrainSimulationEvents(id), e => e.Code == "action.blocked" && e.Message == "只有装备可指定强化等级。");
        }

        // 4. 残卷转化：失败分支动态 2x 残卷 sellPrice、成功分支产出秘籍、富文本日志与跨区域逐位一致
        var convertTarget = SeedTime.AddSeconds(8);
        var targetMs = convertTarget.ToUnixTimeMilliseconds();
        var failActionId = FindConvertActionId(targetMs, wantTome: false);
        var tomeActionId = FindConvertActionId(targetMs, wantTome: true);
        var expectedFallback = SecretArtRules.ResolveFragmentFailureReward(catalog, fragmentId.Value);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var failPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "推演保底", SeedTime, catalog.Fingerprint);
            PlayerAttributeResolver.Apply(failPlayer, catalog);
            using var runtimeA = new EcsGameRuntime(catalog, SeedTime);
            var idA = runtimeA.CreatePlayer(failPlayer.ExportState(), [new AssetBalance(failPlayer.PlayerId, new AssetKey(fragmentId), 2, 0)]);
            runtimeA.EnqueueActionIntent(idA, new ActionIntent(new ActionId(SecretArtRules.ConvertActionId), 1, TargetItemId: fragmentId.Value), failActionId);
            await runtimeA.RunPlayerOnceAsync(idA, convertTarget);
            var resA = runtimeA.DrainSimulationResult(idA)!;
            var evA = Assert.Single(runtimeA.DrainSimulationEvents(idA), e => e.Code == "secret_art.completed");

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var failPlayerB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "推演保底B", SeedTime, catalog.Fingerprint);
            PlayerAttributeResolver.Apply(failPlayerB, catalog);
            using var runtimeB = new EcsGameRuntime(catalog, SeedTime);
            var idB = runtimeB.CreatePlayer(failPlayerB.ExportState(), [new AssetBalance(failPlayerB.PlayerId, new AssetKey(fragmentId), 2, 0)]);
            runtimeB.EnqueueActionIntent(idB, new ActionIntent(new ActionId(SecretArtRules.ConvertActionId), 1, TargetItemId: fragmentId.Value), failActionId);
            await runtimeB.RunPlayerOnceAsync(idB, convertTarget);
            var resB = runtimeB.DrainSimulationResult(idB)!;
            var evB = Assert.Single(runtimeB.DrainSimulationEvents(idB), e => e.Code == "secret_art.completed");

            Assert.Equal(expectedFallback, resA.AssetDeltas.Single(d => d.Asset.ItemId.Value == EnhancementRules.SpiritStoneItemId).QuantityDelta);
            Assert.Equal(resA.AssetDeltas.Select(d => (d.Asset, d.QuantityDelta)), resB.AssetDeltas.Select(d => (d.Asset, d.QuantityDelta)));
            Assert.Equal(evA.Message, evB.Message);
            Assert.Contains("{{item:item.fragment.heavy-slash|《重斩·残卷》}}", evA.Message);
            Assert.Contains("{{item:currency.spirit-stone|灵石|20}}", evA.Message);

            var tomePlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "推演顿悟", SeedTime, catalog.Fingerprint);
            PlayerAttributeResolver.Apply(tomePlayer, catalog);
            using var runtimeC = new EcsGameRuntime(catalog, SeedTime);
            var idC = runtimeC.CreatePlayer(tomePlayer.ExportState(), [new AssetBalance(tomePlayer.PlayerId, new AssetKey(fragmentId), 1, 0)]);
            runtimeC.EnqueueActionIntent(idC, new ActionIntent(new ActionId(SecretArtRules.ConvertActionId), 1, TargetItemId: fragmentId.Value), tomeActionId);
            await runtimeC.RunPlayerOnceAsync(idC, convertTarget);
            var resC = runtimeC.DrainSimulationResult(idC)!;
            var evC = Assert.Single(runtimeC.DrainSimulationEvents(idC), e => e.Code == "secret_art.completed");

            Assert.Contains(resC.AssetDeltas, d => d.Asset.ItemId.Value == "item.tome.heavy-slash" && d.QuantityDelta == 1);
            Assert.Contains("{{item:item.tome.heavy-slash|《重斩秘籍》|1}}", evC.Message);
            Assert.Contains("{{item:item.fragment.heavy-slash|《重斩·残卷》}}", evC.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("item.ningxue-grass", false)]
    [InlineData("equipment.qingfeng-sword", true)]
    public async Task SecretArtConvert_BlocksNonFragmentsWithoutConsumingAssetsOrGrantingExperience(string targetId, bool equipped)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "残卷准入", SeedTime, catalog.Fingerprint);
        if (equipped)
            player.ReplaceCombatSlots([], [new PlayerEquipmentSlot(targetId, 0)]);
        var asset = new AssetKey(new ItemId(targetId));
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var id = runtime.CreatePlayer(player.ExportState(), [new AssetBalance(player.PlayerId, asset, 1, 0)]);
        runtime.EnqueueActionIntent(id, new ActionIntent(new ActionId(SecretArtRules.ConvertActionId), 1, TargetItemId: targetId));
        var before = runtime.ExportSnapshot(id).State;

        await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(60));

        var blocked = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "action.blocked");
        Assert.Contains("功法残卷", blocked.Message);
        var after = runtime.ExportSnapshot(id).State;
        Assert.Equal(ActionQueueStatus.Blocked, Assert.Single(after.Actions).Status);
        Assert.Equal(0, after.Actions[0].CompletedRepeats);
        Assert.Equal(1, runtime.GetAssetBalances(id)[asset]);
        Assert.Empty(runtime.DrainSimulationResult(id)!.AssetDeltas);
        Assert.Equal(before.Progression.Foundation, after.Progression.Foundation);
        Assert.Equal(before.Loadout.TechniqueExperiences, after.Loadout.TechniqueExperiences);
    }

    /// <summary>化灵成功率判定：秘法等级低于物品等级按差值 −5%/级衰减（下限 20%）；失败物品焚毁无产出，耗时与感悟经验照常结算。</summary>
    [Fact]
    public async Task SecretArtLiquefy_LevelDeficitRollsDeterministicallyAndBurnsOnFailure()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var danId = new ItemId("item.demon-dan");
        var itemLevel = CraftingRecipeRules.ResolveItemLevel(danId.Value, catalog);
        Assert.Equal(12, itemLevel);
        // 技艺 0 级 vs 12 级妖丹：成功率 = max(0.2, 1 − 0.05×12) = 40%。
        var chance = SecretArtRules.LiquefySuccessChance(0, itemLevel);
        Assert.Equal(0.4, chance, 9);

        var successActionId = FindLiquefyActionId(1, chance, wantSuccess: true);
        var failActionId = FindLiquefyActionId(1, chance, wantSuccess: false);
        var expectedSpirit = SecretArtRules.ResolveRecycleUnitPrice(catalog, danId.Value, 0) * SecretArtRules.LiquefactionMultiplier;

        // 成功分支：扣 1 妖丹并按三倍回收价发放灵石。
        var successPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "化灵有成", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(successPlayer, catalog);
        using (var runtime = new EcsGameRuntime(catalog, SeedTime))
        {
            var id = runtime.CreatePlayer(successPlayer.ExportState(),
                [new AssetBalance(successPlayer.PlayerId, new AssetKey(danId), 2, 0)]);
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.LiquefyActionId), RepeatCount: 1, TargetItemId: danId.Value), successActionId);

            // 12 级物品基础耗时 5 + 0.3×11 = 8.3 秒；推进 9 秒完成一轮。
            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(9));
            var result = runtime.DrainSimulationResult(id)!;
            Assert.Contains(result.AssetDeltas, d => d.Asset == new AssetKey(danId) && d.QuantityDelta == -1);
            Assert.Contains(result.AssetDeltas, d => d.Asset.ItemId.Value == EnhancementRules.SpiritStoneItemId && d.QuantityDelta == expectedSpirit);
            var ev = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "secret_art.completed");
            Assert.Contains("化灵成功", ev.Message);
        }

        // 失败分支：扣 1 妖丹焚毁、无灵石增量，感悟经验仍按动态耗时入账。
        var failPlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "化灵焚毁", SeedTime, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(failPlayer, catalog);
        using (var runtime = new EcsGameRuntime(catalog, SeedTime))
        {
            var id = runtime.CreatePlayer(failPlayer.ExportState(),
                [new AssetBalance(failPlayer.PlayerId, new AssetKey(danId), 2, 0)]);
            runtime.EnqueueActionIntent(id, new ActionIntent(
                new ActionId(SecretArtRules.LiquefyActionId), RepeatCount: 1, TargetItemId: danId.Value), failActionId);

            await runtime.RunPlayerOnceAsync(id, SeedTime.AddSeconds(9));
            var result = runtime.DrainSimulationResult(id)!;
            Assert.Contains(result.AssetDeltas, d => d.Asset == new AssetKey(danId) && d.QuantityDelta == -1);
            Assert.DoesNotContain(result.AssetDeltas, d => d.Asset.ItemId.Value == EnhancementRules.SpiritStoneItemId);
            var ev = Assert.Single(runtime.DrainSimulationEvents(id), e => e.Code == "secret_art.completed");
            Assert.Contains("化灵失败", ev.Message);
            Assert.Contains("焚毁", ev.Message);

            var (expectedTechExp, _) = SecretArtRules.ComputeYieldExperience(catalog, danId.Value,
                SecretArtRules.LiquefyDurationSeconds(itemLevel));
            var techniqueExps = runtime.ExportSnapshot(id).State.Loadout?.TechniqueExperiences;
            Assert.NotNull(techniqueExps);
            Assert.Equal(expectedTechExp, techniqueExps![TechniqueCatalog.Enhancement], 9);
        }
    }

    /// <summary>组队阵盘：teamWide == false 仅注入施放者自身，后序队友同阵盘空槽不得抹除前序队友已激活的全队阵盘。</summary>
    [Fact]
    public void ActionParticipationSession_FormationTeamWideAndEmptySlotIsolation()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var members = Enumerable.Range(0, 3)
            .Select(i => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), $"阵盘成员{i}", SeedTime, catalog.Fingerprint))
            .ToArray();
        var stats = CombatantStats.Create(50_000, 10, 500, 10) with { MaxManapoints = 1_000 };
        var snapshots = members.Select((member, i) =>
        {
            var slots = i switch
            {
                0 => new[] { new ActionConsumableSlot("item.tianlei-formation-disk", "combat", AutoUseSlotState.SlotGroupGeneral, 0) },
                _ => new[] { new ActionConsumableSlot("item.mizong-formation-disk", "combat", AutoUseSlotState.SlotGroupGeneral, 0) }
            };
            return new ActionParticipantSnapshot(member.PlayerId.Value.ToString("D"), member.DisplayName,
                1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [], ConsumableSlots: slots);
        }).ToArray();

        var spec = new ActionParticipationSpec("session.formation-isolation", "encounter.qi-beast", "node.shuyu-brook",
            31, 0, SeedTime.ToUnixTimeMilliseconds() / 1000d, snapshots) { ActionId = new ActionId("combat.hunt-qi-beast") };
        var wallet = new List<AssetBalance>
        {
            new(members[0].PlayerId, new AssetKey(new ItemId("item.tianlei-formation-disk")), 1, 0),
            new(members[1].PlayerId, new AssetKey(new ItemId("item.mizong-formation-disk")), 1, 0)
            // 故意不为成员 2 提供 item.mizong-formation-disk 余额（未充能空槽）
        };

        var session = ActionParticipationSessionEngine.Create(spec, catalog, wallet);
        var searchSeconds = session.Export().Expedition!.PhaseDurationSeconds;
        session.AdvanceTo(SeedTime.AddSeconds(searchSeconds + 0.5));
        var expedition = session.Export().Expedition!;
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);

        // 天雷阵（teamWide: false）：仅成员 0（队长）持有，成员 1/2 不得被误注入
        Assert.Contains(expedition.PlayerState!.Buffs ?? [], b => b.BuffId == "formation.tianlei");
        Assert.DoesNotContain(expedition.Allies![0].State.Buffs ?? [], b => b.BuffId == "formation.tianlei");
        Assert.DoesNotContain(expedition.Allies![1].State.Buffs ?? [], b => b.BuffId == "formation.tianlei");

        // 迷踪阵（teamWide: true）：成员 1 激活后覆盖全队 3 人，成员 2 的空槽绝对不能清掉它
        Assert.Contains(expedition.PlayerState!.Buffs ?? [], b => b.BuffId == "formation.mizong");
        Assert.Contains(expedition.Allies![0].State.Buffs ?? [], b => b.BuffId == "formation.mizong");
        Assert.Contains(expedition.Allies![1].State.Buffs ?? [], b => b.BuffId == "formation.mizong");
    }

    /// <summary>组队视角投影及战斗回放必须把目标成员的单位 ID 重映射为 player，防止成员离线单推燃魂崩溃。</summary>
    [Fact]
    public void ActionParticipationSession_ProjectForRemapsSourceUnitIdForOfflineSoloResume()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var members = Enumerable.Range(0, 2)
            .Select(i => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), $"投影队员{i}", SeedTime, catalog.Fingerprint))
            .ToArray();
        var stats = CombatantStats.Create(50_000, 10, 500, 10) with { MaxManapoints = 1_000 };
        var snapshots = members.Select(member =>
            new ActionParticipantSnapshot(member.PlayerId.Value.ToString("D"), member.DisplayName,
                1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [])).ToArray();
        var spec = new ActionParticipationSpec("session.source-remap", "encounter.qi-beast", "node.shuyu-brook",
            43, 0, SeedTime.ToUnixTimeMilliseconds() / 1000d, snapshots)
        {
            ActionId = new ActionId("combat.hunt-qi-beast"),
            Difficulty = 5
        };

        var session = ActionParticipationSessionEngine.Create(spec, catalog);
        var searchSeconds = session.Export().Expedition!.PhaseDurationSeconds;
        var enterAt = SeedTime.AddSeconds(searchSeconds + 0.2);
        session.AdvanceTo(enterAt);
        var canonicalDoc = session.Export();
        Assert.Equal(CombatExpeditionPhase.Combat, canonicalDoc.Expedition!.Phase);

        var allyRanhun = new CombatBuffState(
            CombatTraitRules.RanhunBuffId,
            canonicalDoc.Expedition.BattleElapsedSeconds + 20,
            Stacks: 1,
            Name: "燃魂",
            ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.FireSpellDamagePerSecond, 1)],
            SourceUnitId: "ally:1");
        var captainMark = new CombatBuffState(
            CombatTraitRules.RanhunBuffId,
            canonicalDoc.Expedition.BattleElapsedSeconds + 20,
            Stacks: 1,
            Name: "燃魂",
            ResetOnWave: true,
            Effects: [new BuffEffect(BuffEffectChannel.FireSpellDamagePerSecond, 1)],
            SourceUnitId: "player");

        var patchedDoc = canonicalDoc with
        {
            Expedition = canonicalDoc.Expedition with
            {
                Enemies = canonicalDoc.Expedition.Enemies!
                    .Select((enemy, index) => index == 0
                        ? enemy with { State = enemy.State with { Buffs = [allyRanhun] } }
                        : enemy)
                    .ToArray(),
                Allies = canonicalDoc.Expedition.Allies!
                    .Select(ally => ally.MemberIndex == 1
                        ? ally with { State = ally.State with { Buffs = [captainMark] } }
                        : ally)
                    .ToArray()
            }
        };

        // 1. 静态检查点视角投影（ProjectExpedition + ProjectSnapshot）
        var projected = ActionParticipationSessionEngine.ProjectFor(patchedDoc, members[1].PlayerId);
        Assert.Equal("player", Assert.Single(projected.Enemies![0].State.Buffs!).SourceUnitId);
        Assert.Equal("ally:0", Assert.Single(projected.PlayerState!.Buffs!).SourceUnitId);

        // 2. Restore + AdvanceTo（覆盖 ProjectBattle + SynchronizeViews 成员结算视角）
        var restored = ActionParticipationSessionEngine.Restore(patchedDoc, catalog);
        var settlements = restored.AdvanceTo(enterAt.AddSeconds(1.2));
        Assert.Equal("player", Assert.Single(settlements[1].Expedition!.Enemies![0].State.Buffs!,
            b => b.BuffId == CombatTraitRules.RanhunBuffId).SourceUnitId);
        Assert.Equal("ally:0", Assert.Single(settlements[1].Expedition!.PlayerState!.Buffs!,
            b => b.BuffId == CombatTraitRules.RanhunBuffId).SourceUnitId);

        // 3. 队员离队后携带投影单独离线恢复并跨整秒边界推进，不能因找不到 ally:1 崩溃
        var soloMember = PlayerAggregate.Create(members[1].PlayerId, AccountId.New(), "离队单推", enterAt, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(soloMember, catalog);
        soloMember.World.NodeId = "node.shuyu-brook";
        soloMember.Resources.MaxHitpoints = stats.MaxHitpoints;
        soloMember.Resources.Hitpoints = stats.MaxHitpoints;
        soloMember.Resources.MaxManapoints = stats.MaxManapoints;
        soloMember.Resources.Manapoints = stats.MaxManapoints;
        soloMember.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1, Difficulty: 5), enterAt, catalog.Actions);
        soloMember.Actions[0].Expedition = projected with
        {
            SelfIndex = 0,
            Allies = null,
            PlayerState = projected.PlayerState! with { Buffs = null },
            IsPinned = true
        };

        var target = enterAt.AddSeconds(2.5);
        SimulationEngine.AdvanceTo(soloMember, target, catalog);
        Assert.Equal(target, soloMember.LastSimulatedAt);
    }
}
