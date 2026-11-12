using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Simulator;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Protocol.Simulator;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 战斗预演与实战同源的窄口径一致性回归：同一导入快照分别经模拟器运行器与真实
/// <see cref="EcsGameRuntime"/>/<see cref="ActionParticipationSessionEngine"/> 推进，
/// 逐自然边界比较资源/技能/Buff/波次/资产差量/收益/单位统计，绝不只比胜负。
/// </summary>
public sealed class CombatSimulatorParityTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlayerIdValue = Guid.Parse("30000000-0000-4000-8000-00000000aa01");
    private static readonly Guid AllyIdValue = Guid.Parse("30000000-0000-4000-8000-00000000aa02");
    private const double CapturedAt = 1_790_841_600d; // 2026-10-01T08:00:00Z

    /// <summary>必可触发条件：自身血量百分比 ≥ 0 恒真（验证真实词表 current-hp-percentage）。</summary>
    private static JsonElement AlwaysTrigger => JsonDocument.Parse(
        """{"dependency":"self","condition":"current-hp-percentage","comparator":"greater-than-equal","value":0,"buffId":null}""").RootElement;
    /// <summary>永假条件：自身血量百分比 < 0 恒假。</summary>
    private static JsonElement NeverTrigger => JsonDocument.Parse(
        """{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than","value":0,"buffId":null}""").RootElement;

    private static string PickCombatNode(GameDataCatalog catalog)
    {
        var action = catalog.Actions.All.First(definition => definition.Kind == ActionKind.Combat
            && definition.ExecutionKind == ActionExecutionKind.CombatEncounter
            && definition.AllowedNodeIds is { Count: > 0 });
        return action.AllowedNodeIds!.First(nodeId =>
            catalog.WorldGraph.TryGetNode(nodeId, out var node) && node.EncounterIds.Count > 0);
    }

    private static ActionDefinition CombatAction(GameDataCatalog catalog, string nodeId)
        => catalog.Actions.All
            .Where(definition => definition.Kind == ActionKind.Combat
                && definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (definition.AllowedNodeIds is null || definition.AllowedNodeIds.Contains(nodeId))
                && (definition.ExcludedNodeIds is null || !definition.ExcludedNodeIds.Contains(nodeId)))
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal).First();

    private static CombatEncounterDefinition PickEncounter(GameDataCatalog catalog, string nodeId, long seed)
    {
        var pool = catalog.WorldGraph.GetNode(nodeId).EncounterIds;
        var encounterId = pool[(int)((ulong)seed % (ulong)pool.Count)];
        return catalog.CombatEncounters[encounterId];
    }

    /// <summary>构造覆盖装备强化/内功/机制特质/条件三态/非满资源/冷却/战内外 Buff 的富快照。</summary>
    private static PlayerAggregateState BuildRichState(
        GameDataCatalog catalog, Guid playerId, string displayName, double consumableStock,
        JsonElement[]? consumableTriggerOverride = null, bool withReflectArmor = true)
    {
        var baseState = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId),
            displayName, SeedTime, catalog.Fingerprint).ExportState();
        var customTrigger = JsonDocument.Parse("""
            {"dependency":"self","condition":"current-hp-percentage","comparator":"less-than","value":50,"buffId":null}
            """).RootElement;
        var equipment = new List<PlayerEquipmentSlot>
        {
            new("equipment.qingfeng-sword", 3),
            new("equipment.shufa-scarf", 2)
        };
        if (withReflectArmor) equipment.Add(new PlayerEquipmentSlot("equipment.handi-armor", 2));
        return baseState with
        {
            Progression = baseState.Progression with
            {
                RealmLevel = 20,
                Experience = 12000,
                Foundation = 800,
                CombatExperience = 50000,
                LifetimeExperience = 20000,
                AgeSeconds = 3600,
                TraitIds = ["trait.chujian", "trait.dantiaowang"],
                UnlockedTraits =
                [
                    new TraitProgressState("trait.chujian", 3, 2),
                    new TraitProgressState("trait.dantiaowang", 2, 1)
                ],
                HeavenGateConfirmed = new LinggenValues(10, 20, 30, 5, 15),
                HeavenGateEntered = true
            },
            Combat = baseState.Combat with
            {
                Hitpoints = 220,
                MaxHitpoints = 400,
                Manapoints = 36,
                MaxManapoints = 90,
                RegenCarrySeconds = 1.5,
                Skills = new PlayerCombatSkillState(
                    [new CombatSkillCooldown("ability.heavy-slash", 3.5)], "ability.heavy-slash", 1.2, 4),
                Buffs = new PlayerCombatBuffState(
                    [new CombatBuffCarry("buff.zhixue", 12.5, 2, "止血散",
                        0, [new BuffEffect(BuffEffectChannel.HpRegen, 36)])])
            },
            Buffs =
            [
                new ActiveBuff("buff.zhixue", CapturedAt + 300, 1, "止血散增益", "item.zhixue-powder",
                    [new BuffEffect(BuffEffectChannel.HpRegen, 36)],
                    new Dictionary<string, double> { ["armor"] = 0.05 }),
                // 捕获时刻前已过期的战外增益：准备态必须清除，不能带进推演。
                new ActiveBuff("buff.expired-ward", CapturedAt - 10, 1, "过期护佑", "item.huangya-pill",
                    [new BuffEffect(BuffEffectChannel.HpRegen, 50)])
            ],
            Loadout = new PlayerLoadoutDocument(
                new PlayerCoreAttributes(5000, 3000, 4000, 2000, 1000, 800),
                [
                    new PlayerAbilitySlot("ability.tuna-gong", 8),
                    new PlayerAbilitySlot("ability.heavy-slash", 5),
                    new PlayerAbilitySlot(null, 1),
                    new PlayerAbilitySlot(null, 1)
                ],
                equipment,
                new Dictionary<string, double>
                {
                    ["ability.tuna-gong"] = 50000,
                    ["ability.heavy-slash"] = 12500
                },
                null,
                new Dictionary<string, JsonElement[]> { ["ability.heavy-slash"] = [] },
                new Dictionary<string, JsonElement[]>
                {
                    ["item.zhixue-powder"] = consumableTriggerOverride ?? [customTrigger.Clone()]
                }),
            Settings = baseState.Settings with
            {
                AutoUseSlots =
                [
                    new AutoUseSlotState(AutoUseBar.Combat, 0, "item.zhixue-powder",
                        Math.Max(1, consumableStock), true, 0, 0, AutoUseSlotState.SlotGroupPotion),
                    new AutoUseSlotState(AutoUseBar.Combat, 0, "item.leiji-talisman",
                        Math.Max(1, consumableStock), true, 0, 0, AutoUseSlotState.SlotGroupGeneral)
                ]
            }
        };
    }

    private static SimulatorEnvironmentDto BuildEnvironment(
        GameDataCatalog catalog, string nodeId, SimulatorEnvironmentNodeDto? overrideNode = null)
    {
        var nodes = new List<SimulatorEnvironmentNodeDto>();
        foreach (var node in catalog.WorldGraph.Nodes.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            if (string.Equals(node.Id, nodeId, StringComparison.Ordinal) && overrideNode is not null)
            {
                nodes.Add(overrideNode);
                continue;
            }
            nodes.Add(new SimulatorEnvironmentNodeDto(node.Id, [], new SimulatorBuildingEffectsDto(), []));
        }
        return new SimulatorEnvironmentDto(
            SimulatorContractVersions.Environment, catalog.Fingerprint, CapturedAt, nodes);
    }

    private static SimulatorProfileDto Capture(
        GameDataCatalog catalog, PlayerAggregateState state, IReadOnlyList<SimulatorAssetDto> assets)
        => SimulatorProfileMapper.Capture(state, assets, catalog);

    private static SimulatorRunRequestDto Request(
        string nodeId, SimulatorEnvironmentDto environment,
        long seed, double durationSeconds, int difficulty, params SimulatorProfileDto[] members)
        => new(SimulatorContractVersions.Request, nodeId, difficulty, durationSeconds,
            seed.ToString(System.Globalization.CultureInfo.InvariantCulture), members, environment);

    /// <summary>逐字段比对模拟器成员结果与真实终态；浮点严格相等，绝不放宽容差。</summary>
    private static void AssertMemberMatchesOracle(
        PlayerAggregateState oracle, SimulatorMemberResultDto member,
        ActionDefinition action, IReadOnlyList<SimulatorAssetDto> assets,
        IReadOnlyDictionary<AssetKey, double> oracleAssets)
    {
        Assert.Equal(oracle.Combat.Hitpoints, member.Hitpoints);
        Assert.Equal(oracle.Combat.Manapoints, member.Manapoints);
        Assert.Equal(oracle.Combat.RegenCarrySeconds, member.RegenCarrySeconds);
        Assert.Equal(oracle.Combat.Skills.Cooldowns.Count, member.CombatSkills.Cooldowns.Count);
        foreach (var (expected, actual) in oracle.Combat.Skills.Cooldowns.Zip(member.CombatSkills.Cooldowns))
        {
            Assert.Equal(expected.AbilityId, actual.AbilityId);
            Assert.Equal(expected.RemainingSeconds, actual.RemainingSeconds);
        }
        Assert.Equal(oracle.Combat.Skills.CastingAbilityId, member.CombatSkills.CastingAbilityId);
        Assert.Equal(oracle.Combat.Skills.CastRemainingSeconds, member.CombatSkills.CastRemainingSeconds);
        Assert.Equal(oracle.Combat.Buffs.Buffs.Count, member.CombatBuffCarry.Count);
        foreach (var (expected, actual) in oracle.Combat.Buffs.Buffs.Zip(member.CombatBuffCarry))
        {
            Assert.Equal(expected.BuffId, actual.BuffId);
            Assert.Equal(expected.RemainingSeconds, actual.RemainingSeconds);
            Assert.Equal(expected.Stacks, actual.Stacks);
        }
        var oracleExpedition = oracle.Actions[0].Expedition!;
        Assert.Equal(oracleExpedition.WaveNumber, member.WaveNumber);
        Assert.Equal(oracleExpedition.Phase.ToString(), member.Phase);
        Assert.Equal(oracleExpedition.Enemies?.Count ?? 0, member.Enemies.Count);
        foreach (var (expected, actual) in (oracleExpedition.Enemies ?? []).Zip(member.Enemies))
        {
            Assert.Equal(expected.EnemyId, actual.EnemyId);
            Assert.Equal(expected.State.Hitpoints, actual.Hitpoints);
            Assert.Equal(expected.State.Manapoints, actual.Manapoints);
        }

        // 资产净差量与剩余余额：模拟器逐键与真实钱包一致。
        var net = member.AssetDeltas
            .GroupBy(delta => (delta.ItemId, delta.Variant))
            .ToDictionary(group => group.Key, group => group.Sum(delta => delta.Quantity));
        foreach (var asset in assets)
        {
            var key = new AssetKey(new ItemId(asset.ItemId), asset.Variant);
            Assert.Equal(oracleAssets.GetValueOrDefault(key) - asset.Quantity,
                net.GetValueOrDefault((asset.ItemId, asset.Variant)));
        }
        var remaining = member.RemainingConsumableBalances
            .ToDictionary(balance => (balance.ItemId, balance.Variant), balance => balance.Quantity);
        foreach (var pair in remaining)
        {
            var key = new AssetKey(new ItemId(pair.Key.ItemId), pair.Key.Variant);
            Assert.Equal(oracleAssets.GetValueOrDefault(key), pair.Value);
        }
        foreach (var pair in oracleAssets)
            if (pair.Value > 0)
                Assert.True(remaining.TryGetValue((pair.Key.ItemId.Value, pair.Key.Variant), out var seen)
                    && seen == pair.Value, $"模拟器剩余余额缺少 {pair.Key.ItemId.Value}/{pair.Key.Variant}。");

        // 战报收益：经验/底蕴/战斗经验/波次/遭遇/击杀/死亡与真实账本一致。
        var report = oracle.Duel.ActiveReports?.FirstOrDefault(entry => entry.ActionId == action.Id.Value)
            ?? oracle.Duel.CurrentReport
            ?? oracle.Duel.Reports.FirstOrDefault(entry => entry.ActionId == action.Id.Value);
        if (report is not null)
        {
            Assert.Equal(report.Experience, member.Experience);
            Assert.Equal(report.Foundation, member.Foundation);
            Assert.Equal(report.CombatExperience, member.CombatExperience);
            Assert.Equal(report.Cycles, member.Cycles);
            Assert.Equal(report.Encounters, member.Encounters);
            Assert.Equal(report.NormalKills + report.EliteKills + report.BossKills,
                member.NormalKills + member.EliteKills + member.BossKills);
            Assert.Equal(report.Deaths, member.Deaths);
            Assert.Equal(report.Combatants?.Count ?? 0, member.CombatStats.Count);
        }
    }

    [Fact]
    public async Task SoloRun_MatchesRealEcsRuntimeBoundaryByBoundary()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        const long seed = 424242;
        const double duration = 180;
        var sourceState = BuildRichState(catalog, PlayerIdValue, "预演本尊", 5);
        var assets = new[]
        {
            new SimulatorAssetDto("item.zhixue-powder", "", 3),
            new SimulatorAssetDto("item.leiji-talisman", "", 2),
            new SimulatorAssetDto("item.qi-gathering-pill", "", 9)
        };
        var profile = Capture(catalog, sourceState, assets);
        var environment = BuildEnvironment(catalog, nodeId);
        var envNode = environment.Nodes.First(node => node.NodeId == nodeId);

        // 真实 ECS 运行时单调推进：同一准备态快照、同一钉死遭遇与种子、同一钱包。
        var restored = SimulatorProfileMapper.RestoreToState(profile, 0, nodeId, envNode, catalog, CapturedAt);
        var encounter = PickEncounter(catalog, nodeId, seed);
        var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(CapturedAt * 1000d));
        var target = startAt.AddSeconds(duration);
        using var runtime = new EcsGameRuntime(catalog, startAt);
        var balances = assets.Select(asset => new AssetBalance(restored.PlayerId,
            new AssetKey(new ItemId(asset.ItemId), asset.Variant), asset.Quantity, 0)).ToArray();
        runtime.CreatePlayer(restored, balances);
        var action = CombatAction(catalog, nodeId);
        runtime.EnqueueActionIntent(PlayerIdValue, new ActionIntent(action.Id, -1, Difficulty: 2));
        runtime.MutatePlayer(PlayerIdValue, player =>
        {
            // 队首 Pending→Running 激活是推进管线职责；oracle 手工冻结远征态时一并激活，
            // 否则 0 边界唤醒的零时长推进不会激活行动，边界永远停在起点。
            var head = player.Actions[0];
            head.Status = ActionQueueStatus.Running;
            ((IPlayerActionState)head).Expedition = SimulationEngine.CreateCombatSearchState(
                player, head, encounter, CombatExpeditionPhase.InitialSearch, catalog, seed);
        });

        // 逐自然边界累计推进 oracle、就地导出终态并重放模拟器比对；边界不足时以总时长兜底。
        var boundaries = new List<DateTimeOffset>();
        var rawBoundaries = new List<string>();
        var oracleEvents = new List<SimulationEvent>();
        var compared = 0;
        for (var i = 0; i < 48; i++)
        {
            var next = runtime.NextAdvanceBoundary(PlayerIdValue);
            rawBoundaries.Add(next is { } at ? $"{(at - startAt).TotalSeconds:F3}" : "null");
            if (next is not { } boundary || boundary >= target) break;
            await runtime.RunPlayerOnceAsync(PlayerIdValue, boundary);
            oracleEvents.AddRange(runtime.DrainSimulationEvents(PlayerIdValue));
            if (boundaries.Count > 0 && boundary <= boundaries[^1]) continue;
            boundaries.Add(boundary);

            // 推演时长合法域为 1~86400 秒：不足 1 秒的亚秒边界只推进 oracle，不重放比对。
            var seconds = (boundary - startAt).TotalSeconds;
            if (seconds < 1) continue;
            var result = SimulatorRunner.Run(
                Request(nodeId, environment, seed, seconds, 2, profile), catalog);
            var member = Assert.Single(result.Members);
            var oracle = runtime.ExportSnapshot(PlayerIdValue);
            AssertMemberMatchesOracle(oracle.State, member, action, assets, oracle.Assets);

            // 事件流水：真实引擎尾窗与模拟器尾窗逐条一致（代码/行动/物品明细）。
            var tail = oracleEvents.Skip(Math.Max(0, oracleEvents.Count - member.Events.Count)).ToArray();
            Assert.Equal(tail.Length, member.Events.Count);
            foreach (var (expected, actual) in tail.Zip(member.Events))
            {
                Assert.Equal(expected.Code, actual.Code);
                Assert.Equal(expected.ActionId?.Value, actual.ActionId);
                Assert.Equal(expected.ItemDeltas?.Count ?? 0, actual.ItemDeltas?.Count ?? 0);
            }
            compared++;
        }
        if (boundaries.Count == 0 || boundaries[^1] < target)
        {
            await runtime.RunPlayerOnceAsync(PlayerIdValue, target);
            oracleEvents.AddRange(runtime.DrainSimulationEvents(PlayerIdValue));
            var result = SimulatorRunner.Run(
                Request(nodeId, environment, seed, duration, 2, profile), catalog);
            var member = Assert.Single(result.Members);
            var oracle = runtime.ExportSnapshot(PlayerIdValue);
            AssertMemberMatchesOracle(oracle.State, member, action, assets, oracle.Assets);
            compared++;
        }
        Assert.True(compared >= 2,
            $"自然边界比对少于 2 段（实际 {compared}），分段覆盖不足。原始边界={string.Join(", ", rawBoundaries)}");
        Assert.True(oracleEvents.Count > 0);
        Assert.True(runtime.ExportSnapshot(PlayerIdValue).Assets
            .GetValueOrDefault(new AssetKey(new ItemId("item.zhixue-powder"), "")) >= 0);
    }

    [Fact]
    public async Task PartyRun_MatchesDirectSessionEngineSegmented()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        const long seed = 314159;
        const double duration = 240;
        var leaderState = BuildRichState(catalog, PlayerIdValue, "预演队长", 3);
        var allyBase = BuildRichState(catalog, AllyIdValue, "预演队友", 1);
        var allyState = allyBase with
        {
            Combat = allyBase.Combat with { Hitpoints = 90, Manapoints = 12 }
        };
        var leader = Capture(catalog, leaderState, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var ally = Capture(catalog, allyState, [new SimulatorAssetDto("item.zhixue-powder", "", 1)]);
        var environment = BuildEnvironment(catalog, nodeId);

        // 对照：真实会话引擎单调推进，分段收集结算并逐段比对模拟器重放。
        var envNode = environment.Nodes.First(node => node.NodeId == nodeId);
        var profiles = new[] { leader, ally };
        var states = profiles.Select((profile, index) =>
            SimulatorProfileMapper.RestoreToState(profile, index, nodeId, envNode, catalog, CapturedAt)).ToArray();
        var snapshots = states.Select(state => ActionParticipantSnapshot.FromState(state, catalog)).ToArray();
        var action = CombatAction(catalog, nodeId);
        var spec = new ActionParticipationSpec(
            $"action:{Guid.NewGuid():N}", PickEncounter(catalog, nodeId, seed).Id, nodeId, seed,
            snapshots.Average(member => member.SearchSpeedPoints), CapturedAt, snapshots)
        {
            ActionId = action.Id,
            PvpEnabled = false,
            MonsterWeakenRatio = 0,
            Difficulty = 0
        };
        var wallet = states.Select((state, index) =>
            SimulatorProfileMapper.BuildWorkingWallet(state.PlayerId, profiles[index]))
            .SelectMany(items => items).ToArray();
        var engine = ActionParticipationSessionEngine.Create(spec, catalog, wallet);
        var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(CapturedAt * 1000d));
        var target = startAt.AddSeconds(duration);
        var memberPlayers = states.Select(PlayerAggregate.Restore).ToArray();
        var queued = memberPlayers
            .Select(player => player.StartActionParticipation(spec, startAt, catalog.Actions))
            .ToArray();
        // 逐自然边界累计推进会话引擎；每帧必须就地物化标量快照——ReportFor/ExportState
        // 返回的是可继续累积的活引用，存引用会让早期边界读到终局累计值。
        var oracleFrames = new List<(DateTimeOffset At, double Hp, double Mp, double Carry, double Repeats,
            double Wave, string Phase, double Exp, double Foundation, double CombatExp,
            double NormalKills, double Deaths)[]>();
        void CaptureFrame(DateTimeOffset at)
        {
            oracleFrames.Add(memberPlayers.Select((player, index) =>
            {
                var state = player.ExportState();
                var report = player.Duel.ReportFor(queued[index]!.Id);
                return (At: at, Hp: state.Combat.Hitpoints, Mp: state.Combat.Manapoints,
                    Carry: state.Combat.RegenCarrySeconds, Repeats: queued[index]!.CompletedRepeats,
                    Wave: (double)(state.Actions[0].Expedition?.WaveNumber ?? 1),
                    Phase: state.Actions[0].Expedition?.Phase.ToString()
                        ?? CombatExpeditionPhase.InitialSearch.ToString(),
                    Exp: state.Progression.Experience - profiles[index].Experience,
                    Foundation: state.Progression.Foundation - profiles[index].Foundation,
                    CombatExp: state.Progression.CombatExperience - profiles[index].CombatExperience,
                    NormalKills: report?.NormalKills ?? 0, Deaths: report?.Deaths ?? 0);
            }).ToArray());
        }
        var boundaryCount = 0;
        for (var i = 0; i < 32 && boundaryCount < 8; i++)
        {
            var next = engine.NextVisibleBoundaryUtc;
            if (next is not { } boundary || boundary >= target) break;
            ApplySegment(memberPlayers, engine.AdvanceTo(boundary), catalog);
            CaptureFrame(boundary);
            boundaryCount++;
        }
        ApplySegment(memberPlayers, engine.AdvanceTo(target), catalog);
        CaptureFrame(target);
        Assert.True(oracleFrames.Count >= 2, $"分段边界少于 2 个（实际 {oracleFrames.Count}）。");
        _ = engine.TakeAssetDeltas();

        var compared = 0;
        foreach (var frame in oracleFrames)
        {
            // 推演时长合法域为 1~86400 秒：不足 1 秒的亚秒边界只保留 oracle 帧，不重放比对。
            var seconds = (frame[0].At - startAt).TotalSeconds;
            if (seconds < 1 && frame[0].At != target) continue;
            var result = SimulatorRunner.Run(
                Request(nodeId, environment, seed, seconds, 0, leader, ally), catalog);
            Assert.Equal(2, result.Members.Count);
            for (var i = 0; i < 2; i++)
            {
                var member = result.Members[i];
                var oracle = frame[i];
                Assert.Equal(oracle.Hp, member.Hitpoints);
                Assert.Equal(oracle.Mp, member.Manapoints);
                Assert.Equal(oracle.Carry, member.RegenCarrySeconds);
                Assert.Equal(oracle.Repeats, member.CompletedRepeats);
                Assert.Equal(oracle.Wave, member.WaveNumber);
                Assert.Equal(oracle.Phase, member.Phase);
                Assert.Equal(oracle.Exp, member.Experience);
                Assert.Equal(oracle.Foundation, member.Foundation);
                Assert.Equal(oracle.CombatExp, member.CombatExperience);
                Assert.Equal(oracle.NormalKills, member.NormalKills);
                Assert.Equal(oracle.Deaths, member.Deaths);
            }
            compared++;
        }
        Assert.True(compared >= 2, $"有效分段比对少于 2 段（实际 {compared}）。");
    }

    private static void ApplySegment(
        PlayerAggregate[] memberPlayers,
        IReadOnlyList<ActionParticipationMemberSettlement> segment, GameDataCatalog catalog)
    {
        foreach (var settlement in segment.OrderBy(entry => entry.Sequence))
            for (var i = 0; i < memberPlayers.Length; i++)
                if (settlement.PlayerId == memberPlayers[i].PlayerId)
                    memberPlayers[i].ApplyActionParticipationSettlement(settlement, catalog);
    }

    [Fact]
    public void RestoreToState_PreparesResourcesWithoutConsumingInventory()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var sourceState = BuildRichState(catalog, PlayerIdValue, "准备态", 5);
        var assets = new[] { new SimulatorAssetDto("item.zhixue-powder", "", 3) };
        var profile = Capture(catalog, sourceState, assets);
        var environment = BuildEnvironment(catalog, nodeId);
        var envNode = environment.Nodes.First(node => node.NodeId == nodeId);
        var restored = SimulatorProfileMapper.RestoreToState(profile, 0, nodeId, envNode, catalog, CapturedAt);

        // 过期战外增益被清理，未过期增益保留；资源不强行填满，时钟平移到环境捕获时刻。
        Assert.DoesNotContain(restored.Buffs, buff => buff.BuffId == "buff.expired-ward");
        Assert.Contains(restored.Buffs, buff => buff.BuffId == "buff.zhixue");
        Assert.Equal(220, restored.Combat.Hitpoints);
        Assert.Equal(36, restored.Combat.Manapoints);
        Assert.Equal(1.5, restored.Combat.RegenCarrySeconds);
        Assert.True(restored.Combat.MaxHitpoints >= 220);
    }

    [Fact]
    public void SoloRun_RealMechanicsProduceObservableEffects()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var environment = BuildEnvironment(catalog, nodeId);

        // 机制特质入战：resolved 战斗面板携带真实特质与等级，而非仅存 ID。
        var source = BuildRichState(catalog, PlayerIdValue, "机制生效", 5);
        var profile = Capture(catalog, source, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var resolved = SimulatorRunner.ResolveProfiles(
            new SimulatorResolveRequestDto(SimulatorContractVersions.Request, nodeId, [profile], environment), catalog);
        var combatant = Assert.Single(resolved.Members).CombatantStats;
        Assert.Equal(JsonValueKind.Object, combatant.ValueKind);
        var combatantText = combatant.GetRawText();
        Assert.Contains("trait.chujian", combatantText);
        Assert.Contains("trait.dantiaowang", combatantText);

        // 反伤：难度 5 下 armored 成员承受有效物理伤害且输出 > 无甲对照（反伤是唯一新增伤害源）。
        var armored = Capture(catalog, source, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var unarmoredSource = BuildRichState(catalog, PlayerIdValue, "无甲对照", 5, withReflectArmor: false);
        var unarmored = Capture(catalog, unarmoredSource, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var armoredResult = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5, armored), catalog);
        var controlResult = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5, unarmored), catalog);
        var armoredUnit = armoredResult.Members[0].CombatStats
            .FirstOrDefault(unit => unit.UnitKey.Contains("player") || unit.Kind == 0);
        var controlUnit = controlResult.Members[0].CombatStats
            .FirstOrDefault(unit => unit.UnitKey.Contains("player") || unit.Kind == 0);
        Assert.NotNull(armoredUnit);
        Assert.NotNull(controlUnit);
        Assert.True(armoredUnit.DamageTakenPhysical + armoredUnit.DamageTakenSpell
            + armoredUnit.DamageTakenTrue > 0, "难度 5 下成员未承受有效伤害，反伤前提不成立。");
        Assert.True(armoredUnit.DamageDealtPhysical + armoredUnit.DamageDealtSpell
            + armoredUnit.DamageDealtTrue > controlUnit.DamageDealtPhysical
            + controlUnit.DamageDealtSpell + controlUnit.DamageDealtTrue,
            "装备反伤甲后输出未超过无甲对照，反伤机制未产生可观测效果。");
    }

    [Fact]
    public void SoloRun_ConsumableTriggerGatesRealConsumption()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var environment = BuildEnvironment(catalog, nodeId);

        // 必可触发：库存 1 的止血散最多消耗恰 1 件且末库存归零。
        var always = BuildRichState(catalog, PlayerIdValue, "必发", 1, [AlwaysTrigger.Clone()]);
        var alwaysProfile = Capture(catalog, always, [new SimulatorAssetDto("item.zhixue-powder", "", 1)]);
        var alwaysResult = SimulatorRunner.Run(Request(nodeId, environment, 555, 300, 0, alwaysProfile), catalog);
        var alwaysMember = Assert.Single(alwaysResult.Members);
        var alwaysSpent = alwaysMember.AssetDeltas
            .Where(delta => delta.ItemId == "item.zhixue-powder" && delta.Quantity < 0)
            .Sum(delta => -delta.Quantity);
        Assert.Equal(1, alwaysSpent);
        Assert.DoesNotContain(alwaysMember.RemainingConsumableBalances,
            balance => balance.ItemId == "item.zhixue-powder");

        // 永假条件：同一档案换永假条件后零消耗、末库存不变。
        var never = BuildRichState(catalog, PlayerIdValue, "永假", 1, [NeverTrigger.Clone()]);
        var neverProfile = Capture(catalog, never, [new SimulatorAssetDto("item.zhixue-powder", "", 1)]);
        var neverResult = SimulatorRunner.Run(Request(nodeId, environment, 555, 300, 0, neverProfile), catalog);
        var neverMember = Assert.Single(neverResult.Members);
        Assert.DoesNotContain(neverMember.AssetDeltas,
            delta => delta.ItemId == "item.zhixue-powder" && delta.Quantity < 0);
        Assert.Equal(1, neverMember.RemainingConsumableBalances
            .Single(balance => balance.ItemId == "item.zhixue-powder").Quantity);
    }

    [Fact]
    public async Task SoloRun_TargetBuildingProjectionAppliesOnce()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        const long seed = 9090;
        var weaken = new SimulatorBuildingEffectsDto(MonsterWeakenRatio: 0.4, CombatHitpointRegen: 12);
        var overrideNode = new SimulatorEnvironmentNodeDto(
            nodeId,
            [new SimulatorNodeBuildingDto("building.demon-seal-array", 3, 0, CapturedAt - 3600)],
            weaken,
            [new SimulatorNodeBuildingUsageDto("building.demon-seal-array", 0, 120, 1)]);
        var sourceState = BuildRichState(catalog, PlayerIdValue, "建筑投影", 0);
        var profile = Capture(catalog, sourceState, []);
        var environment = BuildEnvironment(catalog, nodeId, overrideNode);
        var result = SimulatorRunner.Run(Request(nodeId, environment, seed, 120, 0, profile), catalog);
        var member = Assert.Single(result.Members);

        // 目标建筑效果只折入一次：怪物削弱与 native 真入口一致——同态输入经真实 ECS 对照复跑。
        var restored = SimulatorProfileMapper.RestoreToState(profile, 0, nodeId, overrideNode, catalog, CapturedAt);
        var attributes = PlayerAttributeResolver.Resolve(restored, catalog);
        Assert.Equal(12, attributes.HpRegen - PlayerAttributeResolver.Resolve(
            SimulatorProfileMapper.RestoreToState(profile, 0, nodeId,
                new SimulatorEnvironmentNodeDto(nodeId, [], new SimulatorBuildingEffectsDto(), []),
                catalog, CapturedAt), catalog).HpRegen, 9);

        var encounter = PickEncounter(catalog, nodeId, seed);
        var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(CapturedAt * 1000d));
        using var runtime = new EcsGameRuntime(catalog, startAt);
        runtime.CreatePlayer(restored, []);
        var action = CombatAction(catalog, nodeId);
        runtime.EnqueueActionIntent(PlayerIdValue, new ActionIntent(action.Id, -1));
        runtime.MutatePlayer(PlayerIdValue, player =>
            ((IPlayerActionState)player.Actions[0]).Expedition = SimulationEngine.CreateCombatSearchState(
                player, player.Actions[0], encounter, CombatExpeditionPhase.InitialSearch, catalog, seed));
        await runtime.RunPlayerOnceAsync(PlayerIdValue, startAt.AddSeconds(120));
        var oracle = runtime.ExportSnapshot(PlayerIdValue).State;
        Assert.Equal(oracle.Combat.Hitpoints, member.Hitpoints);
        Assert.Equal(oracle.Actions[0].Expedition!.WaveNumber, member.WaveNumber);
    }

    [Fact]
    public void Run_AndResolve_RejectInvalidInputsConsistently()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var sourceState = BuildRichState(catalog, PlayerIdValue, "拒绝校验", 0);
        var profile = Capture(catalog, sourceState, []);
        var environment = BuildEnvironment(catalog, nodeId);

        void Reject(SimulatorProfileDto[] members, SimulatorEnvironmentDto? env = null)
        {
            var targetEnv = env ?? environment;
            Assert.Throws<DomainException>(() => SimulatorRunner.Run(
                Request(nodeId, targetEnv, 1, 60, 0, members), catalog));
            Assert.Throws<DomainException>(() => SimulatorRunner.ResolveProfiles(
                new SimulatorResolveRequestDto(SimulatorContractVersions.Request, nodeId, members, targetEnv), catalog));
        }
        void RejectRun(string? seed = null, double? duration = null, int? difficulty = null)
        {
            Assert.Throws<DomainException>(() => SimulatorRunner.Run(
                Request(nodeId, environment, 1, duration ?? 60, difficulty ?? 0, profile)
                    with { Seed = seed ?? "1" }, catalog));
        }

        // 身份与复活。
        Reject([profile with { SourcePlayerId = "" }]);
        Reject([profile with { SourcePlayerId = Guid.Empty.ToString("D") }]);
        Reject([profile with { SourcePlayerId = "not-a-guid" }]);
        var duplicate = profile with { SourcePlayerId = PlayerIdValue.ToString("N") };
        var duplicateUpper = profile with { SourcePlayerId = PlayerIdValue.ToString("D").ToUpperInvariant() };
        Reject([duplicate, duplicateUpper]);
        Reject([profile with { RespawnPolicy = 256 }]);
        Reject([profile with { RespawnPolicy = 2 }]);
        Reject([profile with { RespawnPolicy = 2, RespawnTargetPlayerId = PlayerIdValue.ToString("D") }]);
        Reject([profile with { RespawnPolicy = 2, RespawnTargetPlayerId = Guid.NewGuid().ToString("D") }]);

        // 环境：重复节点、未知节点、缺字段节点。
        Reject([profile], environment with
        {
            Nodes = [.. environment.Nodes, environment.Nodes[0]]
        });
        Reject([profile], environment with
        {
            Nodes = [.. environment.Nodes,
                new SimulatorEnvironmentNodeDto("node.nonexistent-zone", [], new SimulatorBuildingEffectsDto(), [])]
        });
        Reject([profile], environment with
        {
            Nodes = environment.Nodes.Select(node => node.NodeId == nodeId
                ? node with { Buildings = null! } : node).ToArray()
        });

        // 档案：未知引用与非法数值。
        Reject([profile with { TraitIds = ["trait.nonexistent"] }]);
        Reject([profile with { TraitIds = ["trait.chujian", "trait.chujian"] }]);
        Reject([profile with { RealmLevel = 1 }]); // 特质总耗 24 远超境界 1 容量
        Reject([profile with
        {
            UnlockedTraits = [new SimulatorTraitProgressDto("trait.chujian", 25, 0)]
        }]);
        Reject([profile with
        {
            Equipment = [.. profile.Equipment, new SimulatorEquipmentSlotDto("equipment.qingfeng-sword", 1)]
        }]);
        Reject([profile with
        {
            Equipment = [new SimulatorEquipmentSlotDto("equipment.nonexistent", 0)]
        }]);
        Reject([profile with
        {
            AbilitySlots = [new SimulatorAbilitySlotDto("ability.heavy-slash", 5), .. profile.AbilitySlots.Skip(1)]
        }]);
        Reject([profile with
        {
            Assets = [new SimulatorAssetDto("item.zhixue-powder", "", 1),
                new SimulatorAssetDto("item.zhixue-powder", "", 2)]
        }]);
        Reject([profile with
        {
            Assets = [new SimulatorAssetDto("item.nonexistent", "", 1)]
        }]);
        Reject([profile with
        {
            Assets = [new SimulatorAssetDto("item.zhixue-powder", "", -1)]
        }]);

        // 消耗品槽：同物品占两槽（stackable=0 上限 1）、potion 组放非药品、未知物品、组外 index。
        Reject([profile with
        {
            CombatAutoUseSlots =
            [
                new SimulatorAutoUseSlotDto("combat", 0, "item.zhixue-powder", 1, true,
                    Group: AutoUseSlotState.SlotGroupPotion),
                new SimulatorAutoUseSlotDto("combat", 0, "item.zhixue-powder", 1, true,
                    Group: AutoUseSlotState.SlotGroupGeneral)
            ]
        }]);
        Reject([profile with
        {
            CombatAutoUseSlots =
            [
                new SimulatorAutoUseSlotDto("combat", 0, "item.leiji-talisman", 1, true,
                    Group: AutoUseSlotState.SlotGroupPotion)
            ]
        }]);
        Reject([profile with
        {
            CombatAutoUseSlots =
            [
                new SimulatorAutoUseSlotDto("combat", 0, "item.qi-gathering-pill", 1, true)
            ]
        }]);
        Reject([profile with
        {
            CombatAutoUseSlots =
            [
                new SimulatorAutoUseSlotDto("combat", 9, "item.zhixue-powder", 1, true,
                    Group: AutoUseSlotState.SlotGroupPotion)
            ]
        }]);
        Reject([profile with
        {
            CombatAutoUseSlots =
            [
                new SimulatorAutoUseSlotDto("combat", 0, null, 1, true)
            ]
        }]);

        // 触发条件：旧非法比较符、未知功法键、未知物品键。
        var illegalTrigger = JsonDocument.Parse("""
            {"dependency":"self","condition":"current-hp","comparator":"less-than-ratio","value":0.5,"buffId":null}
            """).RootElement;
        Reject([profile with
        {
            AbilityTriggers = new Dictionary<string, JsonElement[]>
            { ["ability.heavy-slash"] = [illegalTrigger.Clone()] }
        }]);
        Reject([profile with
        {
            AbilityTriggers = new Dictionary<string, JsonElement[]>
            { ["ability.nonexistent"] = [] }
        }]);
        Reject([profile with
        {
            ConsumableTriggers = new Dictionary<string, JsonElement[]>
            { ["item.qingfeng-sword"] = [] }
        }]);

        // 种子与时长、难度、环境缺失。
        RejectRun(seed: "9223372036854775808");
        RejectRun(seed: "not-a-number");
        RejectRun(duration: double.NaN);
        RejectRun(duration: 0.5);
        RejectRun(difficulty: 6);
        var missingNode = environment with
        {
            Nodes = environment.Nodes.Where(node => node.NodeId != nodeId).ToArray()
        };
        Reject([profile], missingNode);
        var badFingerprint = environment with { GameDataFingerprint = "deadbeefdeadbeef" };
        Reject([profile], badFingerprint);
    }

    [Fact]
    public void Run_IsDeterministicAndDoesNotMutateInputs()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var sourceState = BuildRichState(catalog, PlayerIdValue, "确定复现", 4);
        var profile = Capture(catalog, sourceState,
            [new SimulatorAssetDto("item.zhixue-powder", "", 4)]);
        var environment = BuildEnvironment(catalog, nodeId);
        var request = Request(nodeId, environment, 13579, 240, 1, profile);
        var inputJson = JsonSerializer.Serialize(request, SimulatorJson.Options);
        var first = JsonSerializer.Serialize(SimulatorRunner.Run(request, catalog), SimulatorJson.Options);
        var second = JsonSerializer.Serialize(SimulatorRunner.Run(request, catalog), SimulatorJson.Options);
        Assert.Equal(first, second);
        Assert.Equal(inputJson, JsonSerializer.Serialize(request, SimulatorJson.Options));
    }
}
