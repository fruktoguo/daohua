using System.Linq;
using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 天门阵盘命令门禁（TASK-20261022-02）：
/// 1. progression.heaven_gate 四步（roll/allocate/enter/reroll）的真实不变量：
///    境界与满修前置、分配总值守恒、入天门才生效、逆天改命按 25%（逆命丹 5%）扣底蕴且次数永久递增；
/// 2. 逆命丹消耗走经济事务扣物品，缺丹药必须先拒绝而不是先扣底蕴；
/// 3. 入天门前后突破的放行差异，以及同 requestId 重放走幂等回执不重复结算。
/// </summary>
public sealed class HeavenGateCommandTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>测试用境界曲线配表原值：Lv18 的 expToNext 原值 1000；经前 30 级线性削减后实际突破门槛为 EffectiveGateExpCap (607)。</summary>
    private const double GateExpCap = 1000d;
    private static double EffectiveGateExpCap =>
        TestCatalog.RealmProgression!.UpgradeExpCapFor(HeavenGateRules.HeavenGateRealmLevel);

    /// <summary>叩天门满修开局：开天门抽取成功后抽取值落在配表区间且尚未生效。</summary>
    [Fact]
    public async Task RollRequiresRealmAndFullExperience_ThenStoresUnconfirmedValues()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, GateAtFullExperience);
        await runtime.LoadAsync(playerId);

        var rolled = await Execute(runtime, playerId, new { action = "roll" });

        Assert.True(rolled.Success, $"{rolled.Code}: {rolled.Message}");
        var gate = rolled.Snapshot.State.Progression;
        Assert.NotNull(gate.HeavenGateRolled);
        Assert.Null(gate.HeavenGateConfirmed);
        Assert.False(gate.HeavenGateEntered);
        Assert.Equal(0, gate.HeavenGateRerollCount);
        Assert.InRange(gate.HeavenGateRolled!.Value.Total, HeavenGateRules.RollTotalMin, HeavenGateRules.RollTotalMax);
        Assert.Contains(PlayerSectionKind.Progression,
            Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(rolled.Snapshot.TouchedSections));

        // 已有抽取值后再开天门一律拒绝：抽取只发生一次，改命才是重洗入口。
        var again = await Execute(runtime, playerId, new { action = "roll" });
        Assert.False(again.Success);
        Assert.Equal("HEAVEN_GATE_ALREADY_ROLLED", again.Code);
    }

    /// <summary>境界未到叩天门、或本级修为未满时不得开天门。</summary>
    [Fact]
    public async Task RollRejectedBelowGateRealmOrBeforeExperienceIsFull()
    {
        var lowPlayer = Guid.NewGuid();
        await using (var lowRuntime = CreateRuntime(lowPlayer, aggregate =>
        {
            aggregate.Progression.RealmLevel = HeavenGateRules.HeavenGateRealmLevel - 1;
            aggregate.Progression.Experience = GateExpCap;
        }))
        {
            await lowRuntime.LoadAsync(lowPlayer);
            var rejected = await Execute(lowRuntime, lowPlayer, new { action = "roll" });
            Assert.False(rejected.Success);
            Assert.Equal("REALM_TOO_LOW", rejected.Code);
        }

        var partialPlayer = Guid.NewGuid();
        await using var partialRuntime = CreateRuntime(partialPlayer, aggregate =>
        {
            aggregate.Progression.RealmLevel = HeavenGateRules.HeavenGateRealmLevel;
            aggregate.Progression.Experience = EffectiveGateExpCap - 1;
        });
        await partialRuntime.LoadAsync(partialPlayer);
        var notFull = await Execute(partialRuntime, partialPlayer, new { action = "roll" });
        Assert.False(notFull.Success);
        Assert.Equal("EXPERIENCE_NOT_FULL", notFull.Code);
        Assert.Null(notFull.Snapshot.State.Progression.HeavenGateRolled);
    }

    /// <summary>
    /// 分配走点数池：透支可分配额度的提交被拒；合法提交写入待生效灵根而不立刻生效。
    /// </summary>
    [Fact]
    public async Task AllocateMustStayWithinPointPoolAndDoesNotTakeEffectBeforeEntering()
    {
        // 透支候选五系全满 100：仅当抽取恰好五系全 100（总值 500 极端 roll）时该候选反而合法，
        // 辅助方法循环换随机种子直至抽取分布保证透支必拒（边界概率极低，通常首次即命中）。
        var overdrawn = new LinggenValues(
            LinggenValues.MaxPointsPerElement, LinggenValues.MaxPointsPerElement,
            LinggenValues.MaxPointsPerElement, LinggenValues.MaxPointsPerElement,
            LinggenValues.MaxPointsPerElement);
        var (playerId, runtime, rolledValues) = await RollUntilAsync(GateAtFullExperience, new { action = "roll" },
            values => !HeavenGateRules.IsValidAllocation(values, overdrawn));
        await using (runtime)
        {
            // 合法分配：散功轴选点数最大的系，散功后保留 1 点——roll 点数全部集中在单一系时
            // 把该系散到 0 会触发「未保留有效灵根」拒绝，那是分配额度的正确行为而非本测试场景。
            var drainedAxis = new[] { LinggenElement.Metal, LinggenElement.Wood, LinggenElement.Water, LinggenElement.Fire, LinggenElement.Earth }
                .OrderByDescending(rolledValues.Of)
                .First();
            var redistribution = rolledValues.With(drainedAxis, Math.Min(1, rolledValues.Of(drainedAxis)));

            var illegal = await Execute(runtime, playerId, new
            {
                action = "allocate",
                metal = overdrawn.Metal, wood = overdrawn.Wood, water = overdrawn.Water,
                fire = overdrawn.Fire, earth = overdrawn.Earth
            });
            Assert.False(illegal.Success);
            Assert.Equal("INVALID_ALLOCATION", illegal.Code);

            var legal = await Execute(runtime, playerId, new
            {
                action = "allocate",
                metal = redistribution.Metal, wood = redistribution.Wood, water = redistribution.Water,
                fire = redistribution.Fire, earth = redistribution.Earth
            });
            Assert.True(legal.Success, $"{legal.Code}: {legal.Message}");
            var gate = legal.Snapshot.State.Progression;
            Assert.Equal(redistribution, gate.HeavenGateConfirmed);
            Assert.Equal(rolledValues, gate.HeavenGateRolled);
            Assert.False(gate.HeavenGateEntered);
            // 未入天门时生效灵根为空：分配只是待生效值，属性乘区不得提前读到它。
            Assert.Equal(1d, HeavenGateRules.ResolveCultivationMultiplier(gate.HeavenGateConfirmed ?? LinggenValues.Empty, gate.HeavenGateEntered));
        }
    }

    /// <summary>
    /// 建运行时并执行一次抽取类命令（roll/reroll），直到抽取分布满足 accept 谓词：
    /// 抽取种子取自随机 playerId，命中「总值 &gt; 300 须用尽点数」等边界分布时换种子重试
    /// （概率极低，通常首次即返回）。
    /// </summary>
    private static async Task<(Guid PlayerId, EcsPlayerRuntimeAdapter Runtime, LinggenValues RolledValues)> RollUntilAsync(
        Action<PlayerAggregate> configure, object rollPayload, Func<LinggenValues, bool> accept)
    {
        for (var attempt = 0; ; attempt++)
        {
            var playerId = Guid.NewGuid();
            var runtime = CreateRuntime(playerId, configure);
            await runtime.LoadAsync(playerId);
            var rolled = await Execute(runtime, playerId, rollPayload);
            var rolledValues = rolled.Snapshot.State.Progression.HeavenGateRolled!.Value;
            if (accept(rolledValues) || attempt >= 15)
                return (playerId, runtime, rolledValues);
            await runtime.DisposeAsync();
        }
    }

    /// <summary>
    /// 「散功 2 点换灌注 1 点」的确定性微调候选：源选点数最大的系；
    /// 全系点数不足 2 时不存在合法的散功换灌注分配，返回原生值（走直接候选值路径）。
    /// </summary>
    private static LinggenValues BuildModifiedCandidate(LinggenValues innate)
    {
        var (source, target) = new[] { (LinggenElement.Metal, LinggenElement.Wood), (LinggenElement.Wood, LinggenElement.Metal),
                (LinggenElement.Water, LinggenElement.Fire), (LinggenElement.Fire, LinggenElement.Water), (LinggenElement.Earth, LinggenElement.Metal) }
            .OrderByDescending(pair => innate.Of(pair.Item1))
            .First();
        return innate.Of(source) >= 2
            ? innate
                .With(source, innate.Of(source) - 2)
                .With(target, Math.Min(LinggenValues.MaxPointsPerElement, innate.Of(target) + 1))
            : innate;
    }

    /// <summary>「全灌注金系」单轴候选：其余四轴散功返还的点数尽数灌入金系，单轴上限 100。</summary>
    private static LinggenValues BuildSingleAxisMetalCandidate(LinggenValues innate)
    {
        var othersZero = innate with { Wood = 0, Water = 0, Fire = 0, Earth = 0 };
        var maxMetal = HeavenGateRules.ResolveMaxReachableValue(innate, othersZero, LinggenElement.Metal);
        return new LinggenValues(maxMetal, 0, 0, 0, 0);
    }

    /// <summary>
    /// 入天门单次发包：enter 载荷中直接携带五行分配点数，服务端一次性完成校验、分配与入天门生效。
    /// </summary>
    [Fact]
    public async Task EnterWithDirectCandidateValuesSucceedsInSingleCommand()
    {
        // 微调候选仅在「散功 2 点换灌注 1 点」不越界时才与抽取值满足最终分配约束；
        // 抽取恰好使目标轴顶满 100 且总值 &gt; 300 时微调会残留 1 点未分配，换种子重试。
        var (playerId, runtime, rolledValues) = await RollUntilAsync(GateAtFullExperience, new { action = "roll" },
            values => HeavenGateRules.IsValidFinalAllocation(values, BuildModifiedCandidate(values)));
        await using (runtime)
        {
            var modified = BuildModifiedCandidate(rolledValues);
            var entered = await Execute(runtime, playerId, new
            {
                action = "enter",
                metal = modified.Metal, wood = modified.Wood, water = modified.Water,
                fire = modified.Fire, earth = modified.Earth
            });
            Assert.True(entered.Success, $"{entered.Code}: {entered.Message}");
            var gate = entered.Snapshot.State.Progression;
            Assert.True(gate.HeavenGateEntered);
            Assert.Equal(modified, gate.HeavenGateConfirmed);
        }
    }

    /// <summary>
    /// 未入天门时（allocating 阶段）亦允许逆天改命：扣除底蕴后抽取原生值刷新，保持未入天门状态。
    /// </summary>
    [Fact]
    public async Task RerollBeforeEnteringIsAllowedAndKeepsNotEntered()
    {
        var playerId = Guid.NewGuid();
        var cost = HeavenGateRules.ResolveRerollFoundationCost(EffectiveGateExpCap, useReverseFatePill: false);
        await using var runtime = CreateRuntime(playerId, aggregate =>
        {
            GateAtFullExperience(aggregate);
            aggregate.Progression.Foundation = cost + 1000;
        });
        await runtime.LoadAsync(playerId);
        var firstRoll = await Execute(runtime, playerId, new { action = "roll" });
        var firstRolledValues = firstRoll.Snapshot.State.Progression.HeavenGateRolled!.Value;

        var rerolled = await Execute(runtime, playerId, new { action = "reroll", usePill = false });
        Assert.True(rerolled.Success, $"{rerolled.Code}: {rerolled.Message}");
        var gate = rerolled.Snapshot.State.Progression;
        Assert.False(gate.HeavenGateEntered);
        Assert.Equal(1, gate.HeavenGateRerollCount);
        Assert.NotNull(gate.HeavenGateRolled);
        Assert.NotEqual(firstRolledValues, gate.HeavenGateRolled.Value);
    }

    /// <summary>
    /// 入天门令灵根生效：入天门状态与确认灵根同时成立，随后突破前置解除；重复入天门被拒。
    /// </summary>
    [Fact]
    public async Task EnterActivatesLinggenAndUnblocksBreakthrough()
    {
        // 单轴候选在「总值 > 300 须用尽点数」规则下不可能合法（单轴上限 100 装不下全部点数），
        // 换种子重试直至抽取分布使单轴入天门满足最终分配约束。
        var (playerId, runtime, rolledValues) = await RollUntilAsync(GateAtFullExperience, new { action = "roll" },
            values => HeavenGateRules.IsValidFinalAllocation(values, BuildSingleAxisMetalCandidate(values)));
        await using (runtime)
        {
            var beforeEnter = await Execute(runtime, playerId, "progression.breakthrough", new { });
            Assert.False(beforeEnter.Success);
            Assert.Contains("叩开天门", beforeEnter.Message, StringComparison.Ordinal);

            var candidate = BuildSingleAxisMetalCandidate(rolledValues);
            // enter 载荷直接携带单系分配：随机抽取可能落在多系，显式分配把乘区钉在 1.0，
            // 让本用例专注于「入天门解除突破封锁」；乘区抬升语义由 Domain.Tests 权威覆盖。
            var entered = await Execute(runtime, playerId, new { action = "enter", metal = candidate.Metal, wood = 0, water = 0, fire = 0, earth = 0 });
            Assert.True(entered.Success, $"{entered.Code}: {entered.Message}");
            var gate = entered.Snapshot.State.Progression;
            Assert.True(gate.HeavenGateEntered);
            Assert.Equal(candidate, gate.HeavenGateConfirmed);
            // 入天门即生效：乘区按条数计算（单系为 1.0；五系齐全为 3.0 倍封顶）。
            Assert.Equal(1d, HeavenGateRules.ResolveCultivationMultiplier(gate.HeavenGateConfirmed ?? LinggenValues.Empty, gate.HeavenGateEntered));

            var again = await Execute(runtime, playerId, new { action = "enter" });
            Assert.False(again.Success);
            Assert.Equal("HEAVEN_GATE_ALREADY_ENTERED", again.Code);

            // 灵根乘区已生效（总纲 §7.7.5.1）：入天门后突破瓶颈 = 配表值 × 条数乘区。
            // 本用例开局只确认单系灵根，乘区恒 1.0，原修为仍可直接突破——
            // 乘区抬升与阻断语义由 Domain.Tests.BreakthroughCapScalesWithLinggenMultiplierAfterEnteringGate 权威覆盖。
            var breakthrough = await Execute(runtime, playerId, "progression.breakthrough", new { });
            Assert.True(breakthrough.Success, $"{breakthrough.Code}: {breakthrough.Message}");
            Assert.Equal(HeavenGateRules.HeavenGateRealmLevel + 1, breakthrough.Snapshot.State.Progression.RealmLevel);
        }
    }

    /// <summary>
    /// 逆天改命按当前升级所需修为的 25% 扣底蕴：底蕴不足先拒绝且不改状态，足够时扣 250、次数永久 +1。
    /// </summary>
    [Fact]
    public async Task RerollDeductsQuarterOfExperienceCapAndAccumulatesCount()
    {
        var playerId = Guid.NewGuid();
        var expectedCost = HeavenGateRules.ResolveRerollFoundationCost(EffectiveGateExpCap, useReverseFatePill: false);
        await using var runtime = CreateRuntime(playerId, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = expectedCost + 100;
        });
        await runtime.LoadAsync(playerId);
        var foundationBefore = (await runtime.LoadAsync(playerId)).State.Progression.Foundation;

        var rerolled = await Execute(runtime, playerId, new { action = "reroll", usePill = false });
        Assert.True(rerolled.Success, $"{rerolled.Code}: {rerolled.Message}");
        var gate = rerolled.Snapshot.State.Progression;
        Assert.Equal(Math.Round(EffectiveGateExpCap * 0.25d, MidpointRounding.AwayFromZero), expectedCost);
        Assert.Equal(foundationBefore - expectedCost, gate.Foundation, 6);
        Assert.Equal(1, gate.HeavenGateRerollCount);
        Assert.True(gate.HeavenGateEntered);
        // 改命生成候选灵根：Rolled 换新，但已生效的 Confirmed 保持原值不变。
        Assert.NotEqual(gate.HeavenGateRolled, gate.HeavenGateConfirmed);
        Assert.Equal(new LinggenValues(60, 0, 0, 0, 0), gate.HeavenGateConfirmed);
        Assert.InRange(gate.HeavenGateRolled!.Value.Total, HeavenGateRules.RollTotalMin, HeavenGateRules.RollTotalMax);
        // 底蕴不足：同一命令整体拒绝，不改底蕴也不加改命次数。
        var poorPlayer = Guid.NewGuid();
        await using var poorRuntime = CreateRuntime(poorPlayer, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = expectedCost - 1;
        });
        await poorRuntime.LoadAsync(poorPlayer);
        var rejected = await Execute(poorRuntime, poorPlayer, new { action = "reroll", usePill = false });
        Assert.False(rejected.Success);
        Assert.Equal("INSUFFICIENT_FOUNDATION", rejected.Code);
        Assert.Equal(expectedCost - 1, rejected.Snapshot.State.Progression.Foundation);
        Assert.Equal(0, rejected.Snapshot.State.Progression.HeavenGateRerollCount);
    }

    /// <summary>
    /// 使用逆命丹：底蕴成本降为 5%（50 点），逆命丹经经济事务在同一提交内扣 1；缺丹先拒绝，不扣底蕴。
    /// </summary>
    [Fact]
    public async Task RerollWithPillDiscountsCostAndDebitsOnePillAtomically()
    {
        var playerId = Guid.NewGuid();
        var economy = new RecordingEconomy();
        await using var runtime = CreateRuntime(playerId, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = 1000;
        }, new PillAssets(playerId, 1), economy);
        await runtime.LoadAsync(playerId);

        var rerolled = await Execute(runtime, playerId, new { action = "reroll", usePill = true });
        Assert.True(rerolled.Success, $"{rerolled.Code}: {rerolled.Message}");
        var gate = rerolled.Snapshot.State.Progression;
        // 5% 档：削减后上限（607）的 5% 折算即 30 点，与改命默认 25% 档形成 5 倍差。
        var pillCost = HeavenGateRules.ResolveRerollFoundationCost(EffectiveGateExpCap, useReverseFatePill: true);
        Assert.Equal(Math.Round(EffectiveGateExpCap * 0.05d, MidpointRounding.AwayFromZero), pillCost);
        Assert.Equal(1000d - pillCost, gate.Foundation, 6);
        Assert.Equal(1, gate.HeavenGateRerollCount);
        var delta = Assert.Single(economy.LastRequest!.Deltas);
        Assert.Equal(HeavenGateRules.ReverseFatePillItemId, delta.AssetId);
        Assert.Equal(-1d, delta.Delta);

        // 背包无逆命丹：勾选 usePill 必须整体拒绝，底蕴与改命次数都不动。
        var noPillPlayer = Guid.NewGuid();
        await using var noPillRuntime = CreateRuntime(noPillPlayer, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = 1000;
            aggregate.Progression.HeavenGate.Apply(
                new LinggenValues(60, 0, 0, 0, 0), new LinggenValues(60, 0, 0, 0, 0), entered: true, rerollCount: 2);
        }, new PillAssets(noPillPlayer, 0), new RecordingEconomy());
        await noPillRuntime.LoadAsync(noPillPlayer);
        var rejected = await Execute(noPillRuntime, noPillPlayer, new { action = "reroll", usePill = true });
        Assert.False(rejected.Success);
        Assert.Equal("INSUFFICIENT_ITEM", rejected.Code);
        Assert.Equal(1000d, rejected.Snapshot.State.Progression.Foundation);
        Assert.Equal(2, rejected.Snapshot.State.Progression.HeavenGateRerollCount);
    }

    /// <summary>未开天门不得逆天改命；载荷缺 action 或 action 未知一律 INVALID_PAYLOAD。</summary>
    [Fact]
    public async Task RerollBeforeRollingAndUnknownActionsAreRejected()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, GateAtFullExperience);
        await runtime.LoadAsync(playerId);

        var reroll = await Execute(runtime, playerId, new { action = "reroll", usePill = false });
        Assert.False(reroll.Success);
        Assert.Equal("HEAVEN_GATE_NOT_ROLLED", reroll.Code);

        var noAction = await Execute(runtime, playerId, new { });
        Assert.False(noAction.Success);
        Assert.Equal("INVALID_PAYLOAD", noAction.Code);

        var unknown = await Execute(runtime, playerId, new { action = "ascend" });
        Assert.False(unknown.Success);
        Assert.Equal("INVALID_PAYLOAD", unknown.Code);
    }

    /// <summary>
    /// 逆天改命后选择替换（accept）或保留（discard）的完整业务闭环：
    /// 1. 改命后生效灵根保持原状，新灵根存入 Rolled 作为待决议候选；
    /// 2. 执行 accept（可带微调五行）后，新灵根覆盖写入 Confirmed 生效，候选清除；
    /// 3. 再次改命后执行 discard，候选新灵根废弃，原 Confirmed 保持不变。
    /// </summary>
    [Fact]
    public async Task RerollAcceptAndDiscardFlowBehavesCorrectly()
    {
        // 微调候选与 EnterWithDirectCandidate 同源：抽取恰好使目标轴顶满 100 且总值 > 300 时
        // accept 会残留 1 点未分配被拒，换种子重试直至改命候选满足最终分配约束。
        var (playerId, runtime, candidateRoll) = await RollUntilAsync(
            aggregate =>
            {
                GateEnteredAtFullExperience(aggregate);
                aggregate.Progression.Foundation = 5000;
            },
            new { action = "reroll", usePill = false },
            values => HeavenGateRules.IsValidFinalAllocation(values, BuildModifiedCandidate(values)));
        await using (runtime)
        {
            // 改命已扣底蕴并刷新 Rolled，但 Confirmed 原样保留为入天门时的种子灵根。
            var initialConfirmed = (await runtime.LoadAsync(playerId)).State.Progression.HeavenGateConfirmed;
            Assert.Equal(new LinggenValues(60, 0, 0, 0, 0), initialConfirmed);
            Assert.NotEqual(initialConfirmed, candidateRoll);

            // 2. 采纳替换（带微调）：散功 2 点换灌注 1 点，严格在额度内。
            var modified = BuildModifiedCandidate(candidateRoll);
            var accepted = await Execute(runtime, playerId, new
            {
                action = "accept",
                metal = modified.Metal,
                wood = modified.Wood,
                water = modified.Water,
                fire = modified.Fire,
                earth = modified.Earth,
            });
            Assert.True(accepted.Success, $"{accepted.Code}: {accepted.Message}");
            var gateAccepted = accepted.Snapshot.State.Progression;
            Assert.Equal(modified, gateAccepted.HeavenGateConfirmed);
            Assert.Equal(modified, gateAccepted.HeavenGateRolled);

            // 3. 再次改命，随后放弃（discard）
            var rerolledAgain = await Execute(runtime, playerId, new { action = "reroll", usePill = false });
            Assert.True(rerolledAgain.Success);
            Assert.Equal(modified, rerolledAgain.Snapshot.State.Progression.HeavenGateConfirmed);
            Assert.NotEqual(modified, rerolledAgain.Snapshot.State.Progression.HeavenGateRolled);

            var discarded = await Execute(runtime, playerId, new { action = "discard" });
            Assert.True(discarded.Success, $"{discarded.Code}: {discarded.Message}");
            var gateDiscarded = discarded.Snapshot.State.Progression;
            Assert.Equal(modified, gateDiscarded.HeavenGateConfirmed);
            Assert.Equal(modified, gateDiscarded.HeavenGateRolled);
        }
    }

    /// <summary>同一 requestId 重放走幂等回执：状态不再变更，也不重复扣底蕴。</summary>
    [Fact]
    public async Task HeavenGateCommandReplayIsIdempotentByRequestId()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = 1000;
        });
        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            requestId = requestId.ToString("D"), action = "reroll", usePill = false
        });
        var first = await runtime.ExecuteAsync(playerId, "progression.heaven_gate", payload, 0, requestId);
        Assert.True(first.Success, $"{first.Code}: {first.Message}");
        var afterFirst = first.Snapshot.State.Progression;

        var replay = await runtime.ExecuteAsync(playerId, "progression.heaven_gate", payload, 0, requestId);
        Assert.True(replay.Success);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Code);
        Assert.Equal(afterFirst.Foundation, replay.Snapshot.State.Progression.Foundation);
        Assert.Equal(afterFirst.HeavenGateRerollCount, replay.Snapshot.State.Progression.HeavenGateRerollCount);
    }

    /// <summary>
    /// 灵根总值超过 300 后：allocate 允许暂存，但 enter 与 accept 必须将可分配点数用尽；
    /// 总值 <= 300 时仍允许保留未分配点数直接入天门。
    /// </summary>
    [Fact]
    public async Task EnterAndAcceptRequireFullPointAllocationWhenInnateExceeds300()
    {
        // 1. 总值 = 300：散功留有未分配点数仍可直接入天门
        var normalPlayer = Guid.NewGuid();
        var normalInnate = new LinggenValues(60, 60, 60, 60, 60);
        await using (var normalRuntime = CreateRuntime(normalPlayer, aggregate =>
        {
            GateAtFullExperience(aggregate);
            aggregate.Progression.HeavenGate.Apply(normalInnate, null, entered: false, rerollCount: 0);
        }))
        {
            await normalRuntime.LoadAsync(normalPlayer);
            var entered = await Execute(normalRuntime, normalPlayer, new
            {
                action = "enter",
                metal = 60, wood = 0, water = 0, fire = 0, earth = 0
            });
            Assert.True(entered.Success, $"{entered.Code}: {entered.Message}");
        }

        // 2. 总值 = 500 (>300)：allocate 可暂存单灵根，但 enter 拒绝并提示须用尽点数；用尽后成功
        var highPlayer = Guid.NewGuid();
        var highInnate = new LinggenValues(100, 100, 100, 100, 100);
        await using (var highRuntime = CreateRuntime(highPlayer, aggregate =>
        {
            GateAtFullExperience(aggregate);
            aggregate.Progression.HeavenGate.Apply(highInnate, null, entered: false, rerollCount: 0);
        }))
        {
            await highRuntime.LoadAsync(highPlayer);

            var allocated = await Execute(highRuntime, highPlayer, new
            {
                action = "allocate",
                metal = 100, wood = 0, water = 0, fire = 0, earth = 0
            });
            Assert.True(allocated.Success, $"{allocated.Code}: {allocated.Message}");

            var blockedEnter = await Execute(highRuntime, highPlayer, new
            {
                action = "enter",
                metal = 100, wood = 0, water = 0, fire = 0, earth = 0
            });
            Assert.False(blockedEnter.Success);
            Assert.Equal("INVALID_ALLOCATION", blockedEnter.Code);
            Assert.Contains("300", blockedEnter.Message);

            var fullEnter = await Execute(highRuntime, highPlayer, new
            {
                action = "enter",
                metal = 100, wood = 100, water = 100, fire = 100, earth = 100
            });
            Assert.True(fullEnter.Success, $"{fullEnter.Code}: {fullEnter.Message}");
        }

        // 3. 改命采纳（accept）当候选总值 > 300 时同样拒绝未用尽点数的单灵根
        var rerollPlayer = Guid.NewGuid();
        var existing = new LinggenValues(60, 0, 0, 0, 0);
        var pendingHigh = new LinggenValues(80, 80, 80, 61, 0);
        await using var rerollRuntime = CreateRuntime(rerollPlayer, aggregate =>
        {
            GateAtFullExperience(aggregate);
            aggregate.Progression.HeavenGate.Apply(pendingHigh, existing, entered: true, rerollCount: 1);
        });
        await rerollRuntime.LoadAsync(rerollPlayer);

        var blockedAccept = await Execute(rerollRuntime, rerollPlayer, new
        {
            action = "accept",
            metal = 80, wood = 0, water = 0, fire = 0, earth = 0
        });
        Assert.False(blockedAccept.Success);
        Assert.Equal("INVALID_ALLOCATION", blockedAccept.Code);
        Assert.Contains("300", blockedAccept.Message);

        var fullAccept = await Execute(rerollRuntime, rerollPlayer, new
        {
            action = "accept",
            metal = 100, wood = 90, water = 80, fire = 1, earth = 0
        });
        Assert.True(fullAccept.Success, $"{fullAccept.Code}: {fullAccept.Message}");
        Assert.Equal(new LinggenValues(100, 90, 80, 1, 0), fullAccept.Snapshot.State.Progression.HeavenGateConfirmed);
    }

    /// <summary>叩天门境界、本级修为圆满的开局配置。</summary>
    private static void GateAtFullExperience(PlayerAggregate aggregate)
    {
        aggregate.Progression.RealmLevel = HeavenGateRules.HeavenGateRealmLevel;
        aggregate.Progression.Experience = EffectiveGateExpCap;
    }

    /// <summary>已入天门并持有等量抽取/确认灵根的开局配置（为改命用例准备生效状态）。
    /// 单系灵根使破镜乘区恒 1.0，改命成本折算基数保持配表原值 1000。</summary>
    private static void GateEnteredAtFullExperience(PlayerAggregate aggregate)
    {
        GateAtFullExperience(aggregate);
        var seed = new LinggenValues(60, 0, 0, 0, 0);
        aggregate.Progression.HeavenGate.Apply(seed, seed, entered: true, rerollCount: 0);
    }

    /// <summary>发送一条命令：字段与 requestId 平铺进同一载荷（与前端 gameClient 的拼装口径一致）。</summary>
    private static ValueTask<PlayerCommandResult> Execute(EcsPlayerRuntimeAdapter runtime, Guid playerId,
        object fields) => Execute(runtime, playerId, "progression.heaven_gate", fields);

    private static ValueTask<PlayerCommandResult> Execute(EcsPlayerRuntimeAdapter runtime, Guid playerId,
        string command, object fields)
    {
        var requestId = Guid.NewGuid();
        var values = new Dictionary<string, JsonElement> { ["requestId"] = JsonSerializer.SerializeToElement(requestId.ToString("D")) };
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(fields)))
            foreach (var property in document.RootElement.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        return runtime.ExecuteAsync(playerId, command, JsonSerializer.SerializeToElement(values), 0, requestId);
    }

    private static EcsPlayerRuntimeAdapter CreateRuntime(Guid playerId, Action<PlayerAggregate> configure,
        IAssetBalanceRepository? assets = null, IEconomyTransactionStore? economy = null)
        => EcsRuntimeFactory.Create(
            new FakeRepository(playerId, configure),
            assets ?? new EmptyAssets(),
            economy ?? new NoopEconomy(),
            TestCatalog,
            TestSimulationClock.Fixed(Epoch),
            Epoch);

    /// <summary>测试配表：境界曲线 Lv18 本层上限 1000，其余各层 100，供成本折算断言。</summary>
    private static readonly GameDataCatalog TestCatalog = CreateCatalog();

    private static GameDataCatalog CreateCatalog()
    {
        var expToNext = Enumerable.Repeat(100d, 20).ToArray();
        expToNext[HeavenGateRules.HeavenGateRealmLevel - 1] = GateExpCap;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            expToNext, lifespanYearsByTier = new[] { 100d, 120d }
        }));
        return new GameDataCatalog("heaven-gate-test", "heaven-gate-test", ActionCatalog.Default,
            realmProgression: RealmProgressionCatalog.Parse(document.RootElement));
    }

    /// <summary>全套 8 段存档的只读假仓库：按配置把初始状态编码成 durable bundle。</summary>
    private sealed class FakeRepository : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle;

        public FakeRepository(Guid playerId, Action<PlayerAggregate> configure)
        {
            var aggregate = PlayerAggregate.Create(new PlayerId(playerId), AccountId.New(), "叩天门", Epoch, "heaven-gate-test");
            configure(aggregate);
            var state = aggregate.ExportState();
            var writes = Domain.Persistence.SectionCodec.EncodeAggregateFull(state, Epoch);
            var sections = writes.ToDictionary(
                write => write.SectionKind,
                write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                    write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion,
                Epoch, state.GameDataFingerprint, Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), Epoch);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "叩天门", Epoch), head, sections, []);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    /// <summary>只持有一行逆命丹余额的钱包假实现；数量为 0 时等价空钱包。</summary>
    private sealed class PillAssets(Guid playerId, double quantity) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(quantity <= 0
                ? []
                : [new AssetBalanceRecord(playerId, HeavenGateRules.ReverseFatePillItemId, string.Empty, quantity, 1)]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, false, Epoch, request.ResultPayload));
    }

    private sealed class RecordingEconomy : IEconomyTransactionStore
    {
        public EconomyTransactionRequest? LastRequest { get; private set; }

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, false, Epoch, request.ResultPayload));
        }
    }
}
