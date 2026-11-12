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
        IReadOnlyDictionary<AssetKey, double> oracleAssets,
        IReadOnlyList<SimulationEvent> oracleEvents)
    {
        Assert.Equal(oracle.Combat.Hitpoints, member.Hitpoints);
        Assert.Equal(oracle.Combat.MaxHitpoints, member.MaxHitpoints);
        Assert.Equal(oracle.Combat.Manapoints, member.Manapoints);
        Assert.Equal(oracle.Combat.MaxManapoints, member.MaxManapoints);
        // 回复 carry 与冷却/吟唱/Buff 剩余都是亚秒残差累加值：oracle 逐自然边界推进与
        // Runner 单次 AdvanceTo 的内部边界划分不同，加法结合序差产生 ~1e-7 级末位舍入差，
        // 按绝对残差 1e-6 秒对齐（仍远小于任何真实调度步长，不掩盖逻辑分歧）。
        Assert.True(Math.Abs(oracle.Combat.RegenCarrySeconds - member.RegenCarrySeconds) < 1e-6,
            $"回复余数偏差超限：{oracle.Combat.RegenCarrySeconds:R} vs {member.RegenCarrySeconds:R}。");
        Assert.Equal(oracle.Actions[0].CompletedRepeats, member.CompletedRepeats);
        Assert.Equal(oracle.Combat.Skills.Cooldowns.Count, member.CombatSkills.Cooldowns.Count);
        foreach (var (expected, actual) in oracle.Combat.Skills.Cooldowns.Zip(member.CombatSkills.Cooldowns))
        {
            Assert.Equal(expected.AbilityId, actual.AbilityId);
            Assert.True(Math.Abs(expected.RemainingSeconds - actual.RemainingSeconds) < 1e-6,
                $"技能冷却偏差超限：{expected.AbilityId} {expected.RemainingSeconds:R} vs {actual.RemainingSeconds:R}。");
        }
        Assert.Equal(oracle.Combat.Skills.CastingAbilityId, member.CombatSkills.CastingAbilityId);
        Assert.True(Math.Abs(oracle.Combat.Skills.CastRemainingSeconds - member.CombatSkills.CastRemainingSeconds) < 1e-6,
            $"吟唱剩余偏差超限：{oracle.Combat.Skills.CastRemainingSeconds:R} vs {member.CombatSkills.CastRemainingSeconds:R}。");
        Assert.Equal(oracle.Combat.Skills.CastCount, member.CombatSkills.CastCount);
        Assert.Equal(oracle.Combat.Buffs.Buffs.Count, member.CombatBuffCarry.Count);
        foreach (var (expected, actual) in oracle.Combat.Buffs.Buffs.Zip(member.CombatBuffCarry))
        {
            Assert.Equal(expected.BuffId, actual.BuffId);
            Assert.True(Math.Abs(expected.RemainingSeconds - actual.RemainingSeconds) < 1e-6,
                $"Buff 剩余偏差超限：{expected.BuffId} {expected.RemainingSeconds:R} vs {actual.RemainingSeconds:R}。");
            Assert.Equal(expected.Stacks, actual.Stacks);
            Assert.Equal(
                JsonSerializer.Serialize(expected.Effects?.Select(SimulatorProfileMapper.ToDto).ToArray()),
                JsonSerializer.Serialize(actual.Effects));
        }

        // 战斗消耗品槽完整时钟：组/序号/物品/启用/持续/冷却逐项一致。
        var oracleSlots = (oracle.Settings.AutoUseSlots ?? [])
            .Where(slot => slot.ActionType == AutoUseBar.Combat)
            .Select(SimulatorProfileMapper.ToDto).ToArray();
        Assert.Equal(JsonSerializer.Serialize(oracleSlots, SimulatorJson.Options),
            JsonSerializer.Serialize(member.CombatAutoUseSlots, SimulatorJson.Options));

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

        // 战报收益：经验/底蕴/战斗经验/六维/波次/遭遇/击杀/死亡/物品与真实账本一致。
        // 活动账本（ActiveReports）承载逐单位战斗统计；行动归档后文档 Combatants 恒空。
        var report = oracle.Duel.ActiveReports?.FirstOrDefault(entry => entry.ActionId == action.Id.Value)
            ?? oracle.Duel.CurrentReport
            ?? oracle.Duel.Reports.FirstOrDefault(entry => entry.ActionId == action.Id.Value);
        Assert.NotNull(report);
        var gains = new
        {
            report.Experience, report.Foundation, report.CombatExperience,
            RootExp = (report.RootExp.Stamina, report.RootExp.Meridians, report.RootExp.Attack,
                report.RootExp.Bone, report.RootExp.Agility, report.RootExp.Consciousness),
            report.Cycles, report.Encounters,
            report.NormalKills, report.EliteKills, report.BossKills, report.Deaths,
            Items = report.Items.Select(item => (item.ItemId, item.Quantity)).ToArray(),
            ItemsLost = (report.ItemsLost ?? Array.Empty<PlayerReportItem>())
                .Select(item => (item.ItemId, item.Quantity)).ToArray()
        };
        Assert.Equal(gains.Experience, member.Experience);
        Assert.Equal(gains.Foundation, member.Foundation);
        Assert.Equal(gains.CombatExperience, member.CombatExperience);
        Assert.Equal(gains.RootExp.Stamina, member.RootExp.Stamina);
        Assert.Equal(gains.RootExp.Meridians, member.RootExp.Meridians);
        Assert.Equal(gains.RootExp.Attack, member.RootExp.Attack);
        Assert.Equal(gains.RootExp.Bone, member.RootExp.Bone);
        Assert.Equal(gains.RootExp.Agility, member.RootExp.Agility);
        Assert.Equal(gains.RootExp.Consciousness, member.RootExp.Consciousness);
        Assert.Equal(gains.Cycles, member.Cycles);
        Assert.Equal(gains.Encounters, member.Encounters);
        Assert.Equal(gains.NormalKills, member.NormalKills);
        Assert.Equal(gains.EliteKills, member.EliteKills);
        Assert.Equal(gains.BossKills, member.BossKills);
        Assert.Equal(gains.Deaths, member.Deaths);
        Assert.Equal(
            gains.Items.OrderBy(entry => entry.Item1).ToArray(),
            member.Items.Select(item => (item.ItemId, item.Quantity)).OrderBy(entry => entry.Item1).ToArray());
        Assert.Equal(
            gains.ItemsLost.OrderBy(entry => entry.Item1).ToArray(),
            member.ItemsLost.Select(item => (item.ItemId, item.Quantity)).OrderBy(entry => entry.Item1).ToArray());

        // 逐单位战斗统计：键/类别/名与全部输出/承受/减免/治疗/真实通道值一致。
        var oracleUnitEntries = report.Combatants ?? [];
        Assert.Equal(oracleUnitEntries.Count, member.CombatStats.Count);
        var oracleUnits = oracleUnitEntries
            .Select(unit => JsonSerializer.Serialize(new SimulatorCombatUnitStatDto(
                unit.UnitKey, (int)unit.Kind, unit.DisplayName,
                unit.DamageDealtPhysical, unit.DamageDealtSpell,
                unit.DamageTakenPhysical, unit.DamageTakenSpell,
                unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
                unit.HealingRegen, unit.HealingOther, unit.DamageDealtTrue, unit.DamageTakenTrue),
                SimulatorJson.Options)).ToArray();
        var memberUnits = member.CombatStats
            .Select(unit => JsonSerializer.Serialize(unit, SimulatorJson.Options)).ToArray();
        Assert.Equal(oracleUnits.OrderBy(text => text, StringComparer.Ordinal).ToArray(),
            memberUnits.OrderBy(text => text, StringComparer.Ordinal).ToArray());

        // 事件流水：完整有序后缀逐条一致（时刻/代码/文案/行动/物品明细），差量与 dropped 计数守恒。
        Assert.Equal(Math.Max(0, oracleEvents.Count - member.Events.Count), member.DroppedEventCount);
        var eventTail = oracleEvents.Skip(Math.Max(0, oracleEvents.Count - member.Events.Count)).ToArray();
        Assert.Equal(eventTail.Length, member.Events.Count);
        foreach (var (expected, actual) in eventTail.Zip(member.Events))
        {
            Assert.Equal(expected.At.ToUnixTimeMilliseconds() / 1000d, actual.AtUnixSeconds);
            Assert.Equal(expected.Code, actual.Code);
            Assert.Equal(expected.Message, actual.Message);
            Assert.Equal(expected.ActionId?.Value, actual.ActionId);
            Assert.Equal(
                expected.ItemDeltas?.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key.Value}={pair.Value}").ToArray(),
                actual.ItemDeltas?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}").ToArray());
        }
    }

    [Fact]
    public Task SoloRun_MatchesRealEcsRuntimeBoundaryByBoundary()
        => SoloRunBoundaryByBoundaryCore(stripBuffStatRatios: false);

    /// <summary>[诊断对照] 同一份富 fixture 仅剥离全部战外 Buff 的 StatRatios（armor=0.05），
    /// 检验止血散激活把原 Buff 替换为无 StatRatios 新 Buff 后的面板刷新时机是否即承伤分歧源。</summary>
    [Fact]
    public Task SoloRun_MatchesRealEcsRuntimeBoundaryByBoundary_NoBuffStatRatios()
        => SoloRunBoundaryByBoundaryCore(stripBuffStatRatios: true);

    private async Task SoloRunBoundaryByBoundaryCore(bool stripBuffStatRatios)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        const long seed = 424242;
        const double duration = 180;
        var sourceState = BuildRichState(catalog, PlayerIdValue, "预演本尊", 5);
        if (stripBuffStatRatios)
            sourceState = sourceState with
            {
                Buffs = sourceState.Buffs.Select(buff => buff with { StatRatios = null }).ToArray()
            };
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
            ((IPlayerActionState)player.Actions[0]).Expedition = SimulationEngine.CreateCombatSearchState(
                player, player.Actions[0], encounter, CombatExpeditionPhase.InitialSearch, catalog, seed));

        // 队首 Pending 的零时长边界由一次真实 1 秒推进消化（零时长推进不会激活行动），
        // 之后逐自然边界累计推进 oracle、就地导出终态并重放模拟器比对。
        var oracleEvents = new List<SimulationEvent>();
        var compared = 0;
        var lastAdvanced = startAt;
        var pendingDigested = false;
        var stall = 0;
        for (var i = 0; i < 96; i++)
        {
            DateTimeOffset boundary;
            if (!pendingDigested)
            {
                pendingDigested = true;
                boundary = startAt.AddSeconds(1);
            }
            else
            {
                var next = runtime.NextAdvanceBoundary(PlayerIdValue);
                if (next is not { } nb || nb >= target) break;
                // 边界 ≤ 当前时刻 = 调度报告的过期/即时边界（与生产宿主按墙钟推进同口径）：
                // 以 1 tick 微步消化待处理边界——足以触发「advance 越界即消费」语义，
                // 又不至于把后续遭遇事件链推移到可观测宽度（毫秒级微步会造成真实错位）。
                boundary = nb <= lastAdvanced ? lastAdvanced.AddTicks(1) : nb;
                if (nb <= lastAdvanced)
                    Assert.True(++stall <= 8,
                        $"调度边界连续未前进：{(boundary - startAt).TotalSeconds:F3}s 已重复 {stall} 次。");
                else stall = 0;
            }
            var prevBoundary = lastAdvanced;
            var seconds = (boundary - startAt).TotalSeconds;
            await runtime.RunPlayerOnceAsync(PlayerIdValue, boundary);
            lastAdvanced = boundary;
            oracleEvents.AddRange(runtime.DrainSimulationEvents(PlayerIdValue));

            var result = SimulatorRunner.Run(
                Request(nodeId, environment, seed, seconds, 2, profile), catalog);
            var member = Assert.Single(result.Members);
            var oracle = runtime.ExportSnapshot(PlayerIdValue);
            try
            {
                AssertMemberMatchesOracle(oracle.State, member, action, assets, oracle.Assets, oracleEvents);
            }
            catch (Exception ex)
            {
                var oracleTail = string.Join(" | ", oracleEvents.TakeLast(3)
                    .Select(e => $"{e.At.ToUnixTimeMilliseconds() / 1000d:R}:{e.Code}"));
                var memberTail = string.Join(" | ", member.Events.TakeLast(3)
                    .Select(e => $"{e.AtUnixSeconds:R}:{e.Code}"));
                var oracleUnit = oracle.State.Duel.ActiveReports?
                    .FirstOrDefault(r => r.ActionId == action.Id.Value)?.Combatants?
                    .FirstOrDefault(u => u.Kind == CombatStatUnitKind.Self);
                var memberUnit = member.CombatStats.FirstOrDefault(u => u.Kind == 0);
                var oracleExpHp = oracle.State.Actions[0].Expedition?.PlayerHitpoints;
                string Fmt(double? v) => v?.ToString("R") ?? "?";
                static double? Sum(double? a, double? b) => a is null || b is null ? null : a + b;
                var oracleBuffs = string.Join(",", (oracle.State.Combat.Buffs.Buffs ?? [])
                    .Select(b => $"{b.BuffId}:{b.Stacks}:{b.RemainingSeconds:R}"));
                var memberBuffs = string.Join(",", member.CombatBuffCarry
                    .Select(b => $"{b.BuffId}:{b.Stacks}:{b.RemainingSeconds:R}"));
                var evTail = oracleEvents.Skip(Math.Max(0, oracleEvents.Count - member.Events.Count)).ToArray();
                var firstDiff = -1;
                for (var k = 0; k < Math.Min(evTail.Length, member.Events.Count); k++)
                    if (evTail[k].Message != member.Events[k].Message
                        || Math.Abs(evTail[k].At.ToUnixTimeMilliseconds() / 1000d - member.Events[k].AtUnixSeconds) > 1e-6)
                    { firstDiff = k; break; }
                var evDiff = firstDiff < 0 ? "events-identical"
                    : $"firstEventDiff[{firstDiff}] oracle=[{evTail[firstDiff].At.ToUnixTimeMilliseconds() / 1000d:R}|{evTail[firstDiff].Code}|{evTail[firstDiff].Message}] member=[{member.Events[firstDiff].AtUnixSeconds:R}|{member.Events[firstDiff].Code}|{member.Events[firstDiff].Message}]";
                throw new Exception(
                    $"segment seconds={seconds:R} prevBoundary={(prevBoundary - startAt).TotalSeconds:R} oracleHp={oracle.State.Combat.Hitpoints:R}/{oracle.State.Combat.MaxHitpoints:R} memberHp={member.Hitpoints:R}/{member.MaxHitpoints:R} oracleExpHp={Fmt(oracleExpHp)} "
                    + $"oTaken[P/S/T]={Fmt(oracleUnit?.DamageTakenPhysical)}/{Fmt(oracleUnit?.DamageTakenSpell)}/{Fmt(oracleUnit?.DamageTakenTrue)} "
                    + $"mTaken[P/S/T]={Fmt(memberUnit?.DamageTakenPhysical)}/{Fmt(memberUnit?.DamageTakenSpell)}/{Fmt(memberUnit?.DamageTakenTrue)} "
                    + $"oMit[P/S]={Fmt(oracleUnit?.DamageMitigatedPhysical)}/{Fmt(oracleUnit?.DamageMitigatedSpell)} "
                    + $"mMit[P/S]={Fmt(memberUnit?.DamageMitigatedPhysical)}/{Fmt(memberUnit?.DamageMitigatedSpell)} "
                    + $"oTakenMit[P/S]={Fmt(Sum(oracleUnit?.DamageTakenPhysical, oracleUnit?.DamageMitigatedPhysical))}/{Fmt(Sum(oracleUnit?.DamageTakenSpell, oracleUnit?.DamageMitigatedSpell))} "
                    + $"mTakenMit[P/S]={Fmt(Sum(memberUnit?.DamageTakenPhysical, memberUnit?.DamageMitigatedPhysical))}/{Fmt(Sum(memberUnit?.DamageTakenSpell, memberUnit?.DamageMitigatedSpell))} "
                    + $"oDealt[P/S/T]={Fmt(oracleUnit?.DamageDealtPhysical)}/{Fmt(oracleUnit?.DamageDealtSpell)}/{Fmt(oracleUnit?.DamageDealtTrue)} "
                    + $"mDealt[P/S/T]={Fmt(memberUnit?.DamageDealtPhysical)}/{Fmt(memberUnit?.DamageDealtSpell)}/{Fmt(memberUnit?.DamageDealtTrue)} "
                    + $"oHeal[R/O/T]={Fmt(oracleUnit?.HealingRegen)}/{Fmt(oracleUnit?.HealingOther)}/{Fmt(oracleUnit?.DamageTakenTrue)} "
                    + $"mHeal[R/O]={Fmt(memberUnit?.HealingRegen)}/{Fmt(memberUnit?.HealingOther)} "
                    + $"oracleSim={oracle.State.LastSimulatedAt:O} {evDiff} oBuffs=[{oracleBuffs}] mBuffs=[{memberBuffs}] oracleTail=[{oracleTail}] memberTail=[{memberTail}] :: {ex.Message}", ex);
            }
            compared++;
        }
        if (lastAdvanced < target)
        {
            await runtime.RunPlayerOnceAsync(PlayerIdValue, target);
            oracleEvents.AddRange(runtime.DrainSimulationEvents(PlayerIdValue));
            var result = SimulatorRunner.Run(
                Request(nodeId, environment, seed, duration, 2, profile), catalog);
            var member = Assert.Single(result.Members);
            var oracle = runtime.ExportSnapshot(PlayerIdValue);
            AssertMemberMatchesOracle(oracle.State, member, action, assets, oracle.Assets, oracleEvents);
            compared++;
        }
        Assert.True(compared >= 2, $"自然边界比对少于 2 段（实际 {compared}），分段覆盖不足。");
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
        var allyBase = BuildRichState(catalog, AllyIdValue, "预演队友", 1, consumableTriggerOverride: []);
        var allyState = allyBase with
        {
            Combat = allyBase.Combat with { Hitpoints = 90, Manapoints = 12 }
        };
        // 条件三态随成员覆盖：队长缺键用配表默认、队友 [] 显式恒真、自定义在统一底座用例覆盖。
        var leader = Capture(catalog, leaderState, [new SimulatorAssetDto("item.zhixue-powder", "", 3)])
            with { ConsumableTriggers = null };
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

        // 累计 oracle：结算差量 + 会话引擎每段 TakeAssetDeltas 立即入账，事件与 dropped 逐段累计。
        var oracleDeltas = Enumerable.Range(0, profiles.Length).Select(_ => new List<AssetDelta>()).ToArray();
        var oracleEvents = Enumerable.Range(0, profiles.Length).Select(_ => new List<SimulationEvent>()).ToArray();
        var oracleDropped = new long[profiles.Length];
        void Accumulate(IReadOnlyList<ActionParticipationMemberSettlement> segment)
        {
            foreach (var settlement in segment.OrderBy(entry => entry.Sequence))
                for (var i = 0; i < memberPlayers.Length; i++)
                    if (settlement.PlayerId == memberPlayers[i].PlayerId)
                    {
                        var applied = memberPlayers[i].ApplyActionParticipationSettlement(settlement, catalog);
                        oracleDeltas[i].AddRange(applied.AssetDeltas);
                        oracleEvents[i].AddRange(applied.Events);
                        oracleDropped[i] += applied.DroppedEventCount;
                    }
            foreach (var delta in engine.TakeAssetDeltas())
                for (var i = 0; i < memberPlayers.Length; i++)
                    if (delta.PlayerId == memberPlayers[i].PlayerId)
                        oracleDeltas[i].Add(delta);
        }

        // 每帧就地物化全部比对字段（JSON 串固化快照）——ExportState/ReportFor 是活引用，
        // 存对象会让早期边界读到终局累计值。
        var oracleFrames = new List<(DateTimeOffset At, string ExpectedJson)[]>();
        void CaptureFrame(DateTimeOffset at)
        {
            oracleFrames.Add(memberPlayers.Select((player, index) =>
            {
                var state = player.ExportState();
                var report = player.Duel.ReportFor(queued[index]!.Id);
                var core = state.Loadout.CoreAttributes;
                var baseCore = profiles[index].CoreAttributes;
                var expected = new
                {
                    MemberIndex = index,
                    PlayerId = player.PlayerId.Value.ToString("D"),
                    state.DisplayName,
                    state.Combat.Hitpoints,
                    state.Combat.MaxHitpoints,
                    state.Combat.Manapoints,
                    state.Combat.MaxManapoints,
                    state.Combat.RegenCarrySeconds,
                    CompletedRepeats = queued[index]!.CompletedRepeats,
                    WaveNumber = state.Actions[0].Expedition?.WaveNumber ?? 1,
                    Phase = state.Actions[0].Expedition?.Phase.ToString()
                        ?? CombatExpeditionPhase.InitialSearch.ToString(),
                    Experience = state.Progression.Experience - profiles[index].Experience,
                    Foundation = state.Progression.Foundation - profiles[index].Foundation,
                    CombatExperience = state.Progression.CombatExperience - profiles[index].CombatExperience,
                    LifetimeExperience = state.Progression.LifetimeExperience - profiles[index].LifetimeExperience,
                    CombatSkills = SimulatorProfileMapper.ToDto(state.Combat.Skills),
                    CombatBuffCarry = state.Combat.Buffs.Buffs.Select(SimulatorProfileMapper.ToDto).ToArray(),
                    CombatAutoUseSlots = (state.Settings.AutoUseSlots ?? [])
                        .Where(slot => slot.ActionType == AutoUseBar.Combat)
                        .Select(SimulatorProfileMapper.ToDto).ToArray(),
                    Cycles = report?.Cycles ?? 0, Encounters = report?.Encounters ?? 0,
                    NormalKills = report?.NormalKills ?? 0, EliteKills = report?.EliteKills ?? 0,
                    BossKills = report?.BossKills ?? 0, Deaths = report?.Deaths ?? 0,
                    // 组队结算的六维收益是装配快照差值，与 Runner 的 core-baseCore 口径一致。
                    RootExp = new SimulatorRootExpDto(
                        core.StaminaExp - baseCore.StaminaExp, core.MeridiansExp - baseCore.MeridiansExp,
                        core.AttackExp - baseCore.AttackExp, core.BoneExp - baseCore.BoneExp,
                        core.AgilityExp - baseCore.AgilityExp, core.ConsciousnessExp - baseCore.ConsciousnessExp),
                    CombatStats = report?.CombatStats.Entries
                        .Select(unit => new SimulatorCombatUnitStatDto(
                            unit.UnitKey, (int)unit.Kind, unit.DisplayName,
                            unit.DamageDealtPhysical, unit.DamageDealtSpell,
                            unit.DamageTakenPhysical, unit.DamageTakenSpell,
                            unit.DamageMitigatedPhysical, unit.DamageMitigatedSpell,
                            unit.HealingRegen, unit.HealingOther,
                            unit.DamageDealtTrue, unit.DamageTakenTrue)).ToArray() ?? [],
                    // 物品/资产类列表按 (ItemId,Variant) 定序：两侧合法插入序差不作分歧，
                    // 条目集合与净差仍严格一致。
                    Items = (report?.Items ?? [])
                        .Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity))
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal).ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    ItemsLost = (report?.ItemsLost ?? [])
                        .Select(item => new SimulatorReportItemDto(item.ItemId, "", item.Quantity))
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal).ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    Enemies = (state.Actions[0].Expedition?.Enemies ?? [])
                        .Select(enemy => new SimulatorExpeditionEnemyDto(
                            enemy.EnemyId, enemy.State.Hitpoints, enemy.State.Manapoints)).ToArray(),
                    AssetDeltas = oracleDeltas[index].Select(SimulatorProfileMapper.ToDto)
                        .GroupBy(item => (item.ItemId, item.Variant))
                        .Select(group => new SimulatorAssetDto(
                            group.Key.ItemId, group.Key.Variant, group.Sum(item => item.Quantity)))
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal).ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    RemainingConsumableBalances = RebuildRemaining(
                        profiles[index].Assets, oracleDeltas[index])
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal).ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    Events = oracleEvents[index].Select(SimulatorProfileMapper.ToDto).ToArray(),
                    DroppedEventCount = oracleDropped[index]
                };
                return (at, JsonSerializer.Serialize(expected, SimulatorJson.Options));
            }).ToArray());
        }

        for (var i = 0; i < 32; i++)
        {
            var next = engine.NextVisibleBoundaryUtc;
            if (next is not { } boundary || boundary >= target) break;
            Accumulate(engine.AdvanceTo(boundary));
            CaptureFrame(boundary);
        }
        Accumulate(engine.AdvanceTo(target));
        CaptureFrame(target);
        Assert.True(oracleFrames.Count >= 2, $"分段边界少于 2 个（实际 {oracleFrames.Count}）。");

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
                var actual = new
                {
                    member.MemberIndex, member.PlayerId, member.DisplayName,
                    member.Hitpoints, member.MaxHitpoints, member.Manapoints, member.MaxManapoints,
                    member.RegenCarrySeconds, member.CompletedRepeats, member.WaveNumber, member.Phase,
                    member.Experience, member.Foundation, member.CombatExperience, member.LifetimeExperience,
                    member.CombatSkills, member.CombatBuffCarry, member.CombatAutoUseSlots,
                    member.Cycles, member.Encounters,
                    member.NormalKills, member.EliteKills, member.BossKills, member.Deaths,
                    member.RootExp, member.CombatStats,
                    Items = member.Items.OrderBy(item => item.ItemId, StringComparer.Ordinal)
                        .ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    ItemsLost = member.ItemsLost.OrderBy(item => item.ItemId, StringComparer.Ordinal)
                        .ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    member.Enemies,
                    AssetDeltas = member.AssetDeltas
                        .GroupBy(item => (item.ItemId, item.Variant))
                        .Select(group => new SimulatorAssetDto(
                            group.Key.ItemId, group.Key.Variant, group.Sum(item => item.Quantity)))
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal)
                        .ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    RemainingConsumableBalances = member.RemainingConsumableBalances
                        .OrderBy(item => item.ItemId, StringComparer.Ordinal)
                        .ThenBy(item => item.Variant, StringComparer.Ordinal).ToArray(),
                    member.Events, member.DroppedEventCount
                };
                Assert.Equal(frame[i].ExpectedJson,
                    JsonSerializer.Serialize(actual, SimulatorJson.Options));
            }
            compared++;
        }
        Assert.True(compared >= 2, $"有效分段比对少于 2 段（实际 {compared}）。");
    }

    /// <summary>与 Runner 同口径重建剩余余额：初始 + 净差，仅保留正余额（含正小数）。</summary>
    private static SimulatorAssetDto[] RebuildRemaining(
        IReadOnlyList<SimulatorAssetDto> initial, IReadOnlyList<AssetDelta> deltas)
    {
        var remaining = new Dictionary<(string ItemId, string Variant), double>();
        foreach (var asset in initial)
            remaining[(asset.ItemId, asset.Variant ?? string.Empty)] =
                remaining.GetValueOrDefault((asset.ItemId, asset.Variant ?? string.Empty)) + asset.Quantity;
        foreach (var delta in deltas)
        {
            var key = (delta.Asset.ItemId.Value, delta.Asset.Variant);
            remaining[key] = remaining.GetValueOrDefault(key) + delta.QuantityDelta;
        }
        return remaining.Where(pair => pair.Value > 0)
            .Select(pair => new SimulatorAssetDto(pair.Key.ItemId, pair.Key.Variant, pair.Value)).ToArray();
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

        // 准备态不消费输入库存：profile 资产条目原样、由它重建的工作钱包余额不变，
        // 重复恢复得到同一结果（准备态幂等）。
        Assert.Equal(assets, profile.Assets);
        var wallet = SimulatorProfileMapper.BuildWorkingWallet(restored.PlayerId, profile);
        Assert.Equal(3, wallet.Single(balance => balance.Asset.ItemId.Value == "item.zhixue-powder").Quantity);
        var restoredAgain = SimulatorProfileMapper.RestoreToState(profile, 0, nodeId, envNode, catalog, CapturedAt);
        Assert.Equal(restored.Combat.Hitpoints, restoredAgain.Combat.Hitpoints);
        Assert.Equal(restored.Buffs.Count, restoredAgain.Buffs.Count);
    }

    [Fact]
    public void SoloRun_RealMechanicsProduceObservableEffects()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var environment = BuildEnvironment(catalog, nodeId);

        var source = BuildRichState(catalog, PlayerIdValue, "机制生效", 5);
        var profile = Capture(catalog, source, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);

        // 属性特质真实生效：trait.shengjiyongdong（生机涌动，最大生命 +25% 随等级 5%/级成长）
        // 经 AccumulateModifiers → PlayerAttributeResolver 真实面板路径落入 member.MaxHitpoints，
        // 装配 > 未装配、等级 3 > 等级 1，均为确定性派生而非仅保存 ID。
        var statSource = source with
        {
            Progression = source.Progression with
            {
                TraitIds = ["trait.chujian", "trait.shengjiyongdong"],
                UnlockedTraits =
                [
                    new TraitProgressState("trait.chujian", 3, 2),
                    new TraitProgressState("trait.shengjiyongdong", 3, 2)
                ]
            }
        };
        var statProfile = Capture(catalog, statSource, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var noStatTraitProfile = Capture(catalog, statSource with
        {
            Progression = statSource.Progression with { TraitIds = ["trait.chujian"] }
        }, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var statRun = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5, statProfile), catalog);
        var noStatTraitRun = SimulatorRunner.Run(
            Request(nodeId, environment, 777, 120, 5, noStatTraitProfile), catalog);
        Assert.True(statRun.Members[0].MaxHitpoints > noStatTraitRun.Members[0].MaxHitpoints,
            $"生机涌动装配后最大生命未提升：{statRun.Members[0].MaxHitpoints} <= {noStatTraitRun.Members[0].MaxHitpoints}。");

        var lowStatSource = statSource with
        {
            Progression = statSource.Progression with
            {
                UnlockedTraits =
                [
                    new TraitProgressState("trait.chujian", 1, 0),
                    new TraitProgressState("trait.shengjiyongdong", 1, 0)
                ]
            }
        };
        var lowStat = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5,
            Capture(catalog, lowStatSource, [new SimulatorAssetDto("item.zhixue-powder", "", 3)])), catalog);
        Assert.True(statRun.Members[0].MaxHitpoints > lowStat.Members[0].MaxHitpoints,
            $"生机涌动等级载荷未随等级升高：lv3={statRun.Members[0].MaxHitpoints} vs lv1={lowStat.Members[0].MaxHitpoints}。");

        // trait.chujian 入场 2 秒 EvasionPostRatio（闪避 ×(1+3×等级缩放)）真实生效：
        // 与仅装配属性特质的隔离变体同种子对照，受击闪避增多使玩家单位承伤台账严格下降
        // （闪避判定复用同一随机通道，特质只翻转 hit/miss 结果，不消耗额外随机数）。
        var noChujianProfile = Capture(catalog, statSource with
        {
            Progression = statSource.Progression with { TraitIds = ["trait.shengjiyongdong"] }
        }, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var noChujianRun = SimulatorRunner.Run(
            Request(nodeId, environment, 777, 120, 5, noChujianProfile), catalog);
        static double Taken(SimulatorMemberResultDto member) => member.CombatStats
            .Where(unit => unit.Kind == 0)
            .Sum(unit => unit.DamageTakenPhysical + unit.DamageTakenSpell + unit.DamageTakenTrue);
        Assert.True(Taken(statRun.Members[0]) < Taken(noChujianRun.Members[0]),
            $"初见入场闪避未降低真实承伤：装配 {Taken(statRun.Members[0])} vs 未装配 {Taken(noChujianRun.Members[0])}。");

        // 反伤：resolved 面板 effects.damageReflectPoints>0、承受有效物理受击为结算前提；
        // 输出差量由真实 StatsBook 反伤入账给出（CombatTriggerMath.DamageReflect 落账到受击方输出）。
        var resolved = SimulatorRunner.ResolveProfiles(
            new SimulatorResolveRequestDto(SimulatorContractVersions.Request, nodeId, [profile], environment), catalog);
        var combatant = Assert.Single(resolved.Members).CombatantStats;
        var reflectPoints = combatant.GetProperty("effects").GetProperty("damageReflectPoints").GetDouble();
        Assert.True(reflectPoints > 0, "resolved 面板未解析出反伤点数，前提不成立。");
        var unarmoredSource = BuildRichState(catalog, PlayerIdValue, "无甲对照", 5, withReflectArmor: false);
        var unarmored = Capture(catalog, unarmoredSource, [new SimulatorAssetDto("item.zhixue-powder", "", 3)]);
        var armoredResult = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5, profile), catalog);
        var controlResult = SimulatorRunner.Run(Request(nodeId, environment, 777, 120, 5, unarmored), catalog);
        var armoredUnit = armoredResult.Members[0].CombatStats
            .FirstOrDefault(unit => unit.UnitKey.Contains("player") || unit.Kind == 0);
        var controlUnit = controlResult.Members[0].CombatStats
            .FirstOrDefault(unit => unit.UnitKey.Contains("player") || unit.Kind == 0);
        Assert.NotNull(armoredUnit);
        Assert.NotNull(controlUnit);
        Assert.True(armoredUnit.DamageTakenPhysical > 0,
            "难度 5 下成员未承受有效物理伤害，反伤结算前提不成立。");
        Assert.True(armoredUnit.DamageDealtPhysical + armoredUnit.DamageDealtSpell
            + armoredUnit.DamageDealtTrue > controlUnit.DamageDealtPhysical
            + controlUnit.DamageDealtSpell + controlUnit.DamageDealtTrue,
            "反伤结算未在真实输出统计中产生增量。");
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
    public void UnifiedTriggerBase_EventDrivenGatesConsumableAndSkillByBuffWake()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var environment = BuildEnvironment(catalog, nodeId);

        // buff.chujian 由 trait.chujian 在遭遇创建时授予战斗单位（2 秒、EvasionPostRatio 通道）。
        // 消耗品条件 = 自身 buff.chujian 层数 > 0：只允许在入场 Buff 存续窗口触发；
        // 队长技能条件 = 层数 == 0：Buff 存续期一律不可施法，到期唤醒后才出手；
        // 队友技能条件 = 显式 [] 恒真：同一遭遇内尽早施法，作为门控延迟的对照基线。
        var buffPresent = JsonDocument.Parse("""
            {"dependency":"self","condition":"buff-stacks","comparator":"greater-than","value":0,"buffId":"buff.chujian"}
            """).RootElement;
        var buffGone = JsonDocument.Parse("""
            {"dependency":"self","condition":"buff-stacks","comparator":"equal","value":0,"buffId":"buff.chujian"}
            """).RootElement;
        SimulatorProfileDto BuildProfile(Guid playerId, string name, JsonElement[] abilityTriggers)
        {
            var source = BuildRichState(catalog, playerId, name, 1, [buffPresent.Clone()]) with { };
            source = source with
            {
                // 干净的战斗技能态：无残留吟唱、施法计数为 0，施法只能由本次触发产生。
                Combat = source.Combat with { Skills = new PlayerCombatSkillState([], null, 0, 0) },
                Loadout = source.Loadout with
                {
                    AbilityTriggers = new Dictionary<string, JsonElement[]>
                    { ["ability.heavy-slash"] = abilityTriggers }
                }
            };
            return Capture(catalog, source, [new SimulatorAssetDto("item.zhixue-powder", "", 1)]);
        }
        var leader = BuildProfile(PlayerIdValue, "门控队长", [buffGone.Clone()]);
        var ally = BuildProfile(AllyIdValue, "恒真队友", []);

        static double FirstAt(SimulatorMemberResultDto member, string code) => member.Events
            .Where(entry => entry.Code == code)
            .Select(entry => entry.AtUnixSeconds).DefaultIfEmpty(double.PositiveInfinity).Min();

        // 成员事件流含敌方与其他成员施法播报：每个成员自身施放一律渲染为「你施放」。
        static double FirstOwnSkillAt(SimulatorMemberResultDto member) => member.Events
            .Where(entry => entry.Code == "combat.skill"
                && entry.Message.StartsWith("你施放", StringComparison.Ordinal)
                && entry.Message.Contains("ability.heavy-slash"))
            .Select(entry => entry.AtUnixSeconds).DefaultIfEmpty(double.PositiveInfinity).Min();

        static void AssertMember(SimulatorMemberResultDto member, double firstCombatAt)
        {
            // 止血散在入场 buff 存续窗口内恰好触发一次（库存 1 → 恰 1），且绝不早于首场战斗。
            // 激活事件不携带 ItemDeltas（消耗走资产差量记账），按事件码+道具名匹配。
            var powderUse = Assert.Single(member.Events, entry =>
                entry.Code == "auto-use.activated" && entry.Message.Contains("止血散"));
            Assert.True(powderUse.AtUnixSeconds >= firstCombatAt,
                "搜寻阶段提前触发自动使用。");
            Assert.True(powderUse.AtUnixSeconds < firstCombatAt + 2,
                "入场 buff 存续窗口内未触发消耗品。");
            Assert.Equal(1, member.AssetDeltas
                .Where(delta => delta.ItemId == "item.zhixue-powder").Sum(delta => -delta.Quantity));
            Assert.True(member.CombatSkills.CastCount > 0, "技能未实际施放（CastCount 仍为 0）。");
        }

        var solo = SimulatorRunner.Run(Request(nodeId, environment, 31415, 120, 0, leader), catalog);
        var soloMember = Assert.Single(solo.Members);
        var soloCombatAt = FirstAt(soloMember, "combat.started");
        Assert.False(double.IsInfinity(soloCombatAt), "solo 推演未发生任何战斗。");
        AssertMember(soloMember, soloCombatAt);
        // 门控成员自身：首个技能事件严格晚于消耗品激活（后者在 buff 存续期触发，
        // 技能只能在 buff 到期唤醒后出手——入场 buff 由遭遇创建授予，到期即唤醒）。
        var soloPowderAt = soloMember.Events
            .Where(entry => entry.Code == "auto-use.activated" && entry.Message.Contains("止血散"))
            .Select(entry => entry.AtUnixSeconds).Min();
        Assert.True(FirstOwnSkillAt(soloMember) > soloPowderAt,
            "门控技能未延迟到入场 buff 到期之后。");

        var party = SimulatorRunner.Run(
            Request(nodeId, environment, 31415, 120, 0, leader, ally), catalog);
        Assert.Equal(2, party.Members.Count);
        var partyCombatAt = party.Members
            .Select(member => FirstAt(member, "combat.started"))
            .Where(at => !double.IsInfinity(at)).DefaultIfEmpty(double.PositiveInfinity).Min();
        Assert.False(double.IsInfinity(partyCombatAt), "party 推演未发生任何战斗。");
        foreach (var member in party.Members) AssertMember(member, partyCombatAt);
        // 门控对照：同一遭遇内恒真队友尽早施法，门控队长须真实延迟约一个 buff 窗口。
        var allySkillAt = FirstOwnSkillAt(party.Members[1]);
        var leaderSkillAt = FirstOwnSkillAt(party.Members[0]);
        Assert.False(double.IsInfinity(allySkillAt),
            $"恒真队友未施放技能。DisplayName={party.Members[1].DisplayName}; CastCount={party.Members[1].CombatSkills.CastCount}; skills=[{string.Join(" | ", party.Members[1].Events.Where(e => e.Code == "combat.skill").Select(e => $"{e.AtUnixSeconds}:{e.Message}"))}]");
        Assert.True(leaderSkillAt > allySkillAt + 1,
            $"门控未产生真实延迟：队长 {leaderSkillAt} vs 队友 {allySkillAt}。");
    }

    [Fact]
    public void PartyRun_SustainedFormationDisk_ConsumesOnceAndBuffsTeam()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var environment = BuildEnvironment(catalog, nodeId);

        // 聚灵阵盘是真实配表的持续型战斗消耗品（isContinuous 300 秒、general 组、formation 子类）：
        // 会话启动时恰扣 1 件并按剩余重注入全队 buff；槽位关闭或钱包为零时完全不消费。
        SimulatorProfileDto BuildMember(Guid playerId, string name, bool slotEnabled, double stock)
        {
            var source = BuildRichState(catalog, playerId, name, 1) with { };
            source = source with
            {
                Settings = source.Settings with
                {
                    AutoUseSlots =
                    [
                        new AutoUseSlotState(AutoUseBar.Combat, 0, "item.juling-formation-disk",
                            1, slotEnabled, 0, 0, AutoUseSlotState.SlotGroupGeneral)
                    ]
                }
            };
            return Capture(catalog, source,
                stock > 0 ? [new SimulatorAssetDto("item.juling-formation-disk", "", stock)] : []);
        }

        var enabled = SimulatorRunner.Run(
            Request(nodeId, environment, 2718, 120, 0,
                BuildMember(PlayerIdValue, "阵盘队长", true, 2),
                BuildMember(AllyIdValue, "阵盘队友", true, 2)), catalog);
        foreach (var member in enabled.Members)
        {
            Assert.Equal(1, member.AssetDeltas
                .Where(delta => delta.ItemId == "item.juling-formation-disk")
                .Sum(delta => -delta.Quantity));
            Assert.Single(member.RemainingConsumableBalances,
                balance => balance.ItemId == "item.juling-formation-disk" && balance.Quantity == 1);
            // 持续型消耗品的激活事件不带 ItemDeltas（消耗在会话启动记账），按事件码+道具名匹配。
            Assert.Contains(member.Events, entry => entry.Code == "auto-use.activated"
                && entry.Message.Contains("聚灵阵盘"));
            // 持续型 buff 注入战斗单位（非跨战 carry）；槽位持续剩余时长是真实激活态的可观测投影。
            Assert.Contains(member.CombatAutoUseSlots, slot =>
                slot.ItemId == "item.juling-formation-disk" && slot.RemainingDurationSeconds > 0);
        }

        // 槽位停用/钱包为零：不产生任何差量（槽配置仍在档案中，只是未启用）。
        var gated = SimulatorRunner.Run(
            Request(nodeId, environment, 2718, 120, 0,
                BuildMember(PlayerIdValue, "停用队长", false, 2),
                BuildMember(AllyIdValue, "空库队友", true, 0)), catalog);
        foreach (var member in gated.Members)
        {
            Assert.DoesNotContain(member.AssetDeltas,
                delta => delta.ItemId == "item.juling-formation-disk");
            Assert.DoesNotContain(member.CombatAutoUseSlots, slot =>
                slot.ItemId == "item.juling-formation-disk" && slot.RemainingDurationSeconds > 0);
        }
    }

    [Fact]
    public void RunJson_StrictDeserialization_RejectsLooseInputs()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var nodeId = PickCombatNode(catalog);
        var profile = Capture(catalog, BuildRichState(catalog, PlayerIdValue, "严格输入", 0),
            [new SimulatorAssetDto("item.zhixue-powder", "", 7)]);
        var environment = BuildEnvironment(catalog, nodeId);
        var valid = JsonSerializer.Serialize(Request(nodeId, environment, 42, 60, 0, profile), SimulatorJson.Options);

        // 正常完整 Capture 序列化输出可原样通过严格反序列化。
        var accepted = SimulatorRunner.RunJson(valid);
        Assert.Contains("\"members\"", accepted);

        // 字符串数字：quantity 必须是 JSON 数字字面量，"7" 一律拒绝。
        var stringQuantity = valid.Replace("\"quantity\":7", "\"quantity\":\"7\"");
        Assert.NotEqual(valid, stringQuantity);
        Assert.Throws<JsonException>(() => SimulatorRunner.RunJson(stringQuantity));

        // 大小写错误键：nodeId → NodeId 视为缺键拒绝，不做大小写回退。
        var wrongCase = valid.Replace("\"nodeId\"", "\"NodeId\"");
        Assert.Throws<JsonException>(() => SimulatorRunner.RunJson(wrongCase));

        // 缺字段：槽位时钟 remainingDurationSeconds 必须出现，缺键拒绝而非补默认。
        var missingClock = valid.Replace("\"remainingDurationSeconds\":0,", "");
        Assert.NotEqual(valid, missingClock);
        Assert.Throws<JsonException>(() => SimulatorRunner.RunJson(missingClock));

        // ResolveProfilesJson 同口径：非法环境 JSON 同样拒绝。
        var resolveValid = JsonSerializer.Serialize(
            new SimulatorResolveRequestDto(SimulatorContractVersions.Request, nodeId, [profile], environment),
            SimulatorJson.Options);
        Assert.Contains("\"members\"", SimulatorRunner.ResolveProfilesJson(resolveValid));
        var resolveWrongCase = resolveValid.Replace("\"nodeId\"", "\"NodeId\"");
        Assert.Throws<JsonException>(() => SimulatorRunner.ResolveProfilesJson(resolveWrongCase));
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
