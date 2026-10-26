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
    /// <summary>测试用境界曲线：Lv18 的本层修为上限 1000，逆天改命默认成本即 250、逆命丹档 50。</summary>
    private const double GateExpCap = 1000d;

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
            aggregate.Progression.Experience = GateExpCap - 1;
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
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, GateAtFullExperience);
        await runtime.LoadAsync(playerId);
        var rolled = await Execute(runtime, playerId, new { action = "roll" });
        var rolledValues = rolled.Snapshot.State.Progression.HeavenGateRolled!.Value;

        // 合法分配：把一系散到 0（返还半数点数），其余四系各加少许，仍然在额度内。
        var drainedAxis = LinggenElement.Metal;
        var redistribution = rolledValues.With(drainedAxis, 0);

        // 透支：把全部五系都拉到 100，远超可分配点数，必须在写入前整体拒绝。
        var overdrawn = new LinggenValues(
            LinggenValues.MaxPointsPerElement, LinggenValues.MaxPointsPerElement,
            LinggenValues.MaxPointsPerElement, LinggenValues.MaxPointsPerElement,
            LinggenValues.MaxPointsPerElement);
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

    /// <summary>
    /// 入天门令灵根生效：入天门状态与确认灵根同时成立，随后突破前置解除；重复入天门被拒。
    /// </summary>
    /// <summary>
    /// 入天门单次发包：enter 载荷中直接携带五行分配点数，服务端一次性完成校验、分配与入天门生效。
    /// </summary>
    [Fact]
    public async Task EnterWithDirectCandidateValuesSucceedsInSingleCommand()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, GateAtFullExperience);
        await runtime.LoadAsync(playerId);
        var rolled = await Execute(runtime, playerId, new { action = "roll" });
        var rolledValues = rolled.Snapshot.State.Progression.HeavenGateRolled!.Value;
        // 微调源选点数最大的系，保证「散功 2 点」不越界；roll 出全系点数不足 2 时
        // 不存在合法的散功换灌注分配，改为携带原生值走直接候选值路径。
        var (source, target) = new[] { (LinggenElement.Metal, LinggenElement.Wood), (LinggenElement.Wood, LinggenElement.Metal),
                (LinggenElement.Water, LinggenElement.Fire), (LinggenElement.Fire, LinggenElement.Water), (LinggenElement.Earth, LinggenElement.Metal) }
            .OrderByDescending(pair => rolledValues.Of(pair.Item1))
            .First();
        var modified = rolledValues.Of(source) >= 2
            ? rolledValues
                .With(source, rolledValues.Of(source) - 2)
                .With(target, Math.Min(100, rolledValues.Of(target) + 1))
            : rolledValues;

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

    /// <summary>
    /// 未入天门时（allocating 阶段）亦允许逆天改命：扣除底蕴后抽取原生值刷新，保持未入天门状态。
    /// </summary>
    [Fact]
    public async Task RerollBeforeEnteringIsAllowedAndKeepsNotEntered()
    {
        var playerId = Guid.NewGuid();
        var cost = HeavenGateRules.ResolveRerollFoundationCost(GateExpCap, useReverseFatePill: false);
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

    [Fact]
    public async Task EnterActivatesLinggenAndUnblocksBreakthrough()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, GateAtFullExperience);
        await runtime.LoadAsync(playerId);

        var beforeEnter = await Execute(runtime, playerId, "progression.breakthrough", new { });
        Assert.False(beforeEnter.Success);
        Assert.Contains("叩开天门", beforeEnter.Message, StringComparison.Ordinal);

        var rolled = await Execute(runtime, playerId, new { action = "roll" });
        var rolledValues = rolled.Snapshot.State.Progression.HeavenGateRolled!.Value;
        var maxMetal = HeavenGateRules.ResolveMaxReachableValue(rolledValues, LinggenValues.Empty, LinggenElement.Metal);
        // enter 载荷直接携带单系分配：随机抽取可能落在多系，显式分配把乘区钉在 1.0，
        // 让本用例专注于「入天门解除突破封锁」；乘区抬升语义由 Domain.Tests 权威覆盖。
        var entered = await Execute(runtime, playerId, new { action = "enter", metal = maxMetal, wood = 0, water = 0, fire = 0, earth = 0 });
        Assert.True(entered.Success, $"{entered.Code}: {entered.Message}");
        var gate = entered.Snapshot.State.Progression;
        Assert.True(gate.HeavenGateEntered);
        Assert.Equal(new LinggenValues(maxMetal, 0, 0, 0, 0), gate.HeavenGateConfirmed);
        // 入天门即生效：乘区按条数计算（单系为 1.0；五系齐全为 3.0 倍封顶）。
        Assert.Equal(1d, HeavenGateRules.ResolveCultivationMultiplier(gate.HeavenGateConfirmed ?? LinggenValues.Empty, gate.HeavenGateEntered));

        var again = await Execute(runtime, playerId, new { action = "enter" });
        Assert.False(again.Success);
        Assert.Equal("HEAVEN_GATE_ALREADY_ENTERED", again.Code);

        // 灵根乘区已生效（总纲 §7.7.5.1）：入天门后突破瓶颈 = 配表值 × 条数乘区。
        // 本用例开局只确认单系灵根（60,0,0,0,0），乘区恒 1.0，原 1000 修为仍可直接突破——
        // 乘区抬升与阻断语义由 Domain.Tests.BreakthroughCapScalesWithLinggenMultiplierAfterEnteringGate 权威覆盖。
        var breakthrough = await Execute(runtime, playerId, "progression.breakthrough", new { });
        Assert.True(breakthrough.Success, $"{breakthrough.Code}: {breakthrough.Message}");
        Assert.Equal(HeavenGateRules.HeavenGateRealmLevel + 1, breakthrough.Snapshot.State.Progression.RealmLevel);
    }

    /// <summary>
    /// 逆天改命按当前升级所需修为的 25% 扣底蕴：底蕴不足先拒绝且不改状态，足够时扣 250、次数永久 +1。
    /// </summary>
    [Fact]
    public async Task RerollDeductsQuarterOfExperienceCapAndAccumulatesCount()
    {
        var playerId = Guid.NewGuid();
        var expectedCost = HeavenGateRules.ResolveRerollFoundationCost(GateExpCap, useReverseFatePill: false);
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
        Assert.Equal(250d, expectedCost);
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
        // 5% 档：1000 的上限折算即 50 点，与改命默认 25% 档形成 5 倍差。
        Assert.Equal(1000d - 50d, gate.Foundation, 6);
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
        var playerId = Guid.NewGuid();
        await using var runtime = CreateRuntime(playerId, aggregate =>
        {
            GateEnteredAtFullExperience(aggregate);
            aggregate.Progression.Foundation = 5000;
        });
        await runtime.LoadAsync(playerId);
        var initialConfirmed = (await runtime.LoadAsync(playerId)).State.Progression.HeavenGateConfirmed;
        Assert.Equal(new LinggenValues(60, 0, 0, 0, 0), initialConfirmed);

        // 1. 改命：扣底蕴，次数+1，Rolled 刷新，Confirmed 原样保留
        var rerolled = await Execute(runtime, playerId, new { action = "reroll", usePill = false });
        Assert.True(rerolled.Success);
        var candidateRoll = rerolled.Snapshot.State.Progression.HeavenGateRolled!.Value;
        Assert.NotEqual(initialConfirmed, candidateRoll);
        Assert.Equal(initialConfirmed, rerolled.Snapshot.State.Progression.HeavenGateConfirmed);

        // 2. 采纳替换（带微调）：散功 2 点换灌注 1 点，严格在额度内；微调源选点数最大的系，
        //    全系点数不足 2 时改为采纳候选原生值（不存在合法的散功换灌注分配）。
        var (source, target) = new[] { (LinggenElement.Metal, LinggenElement.Wood), (LinggenElement.Wood, LinggenElement.Metal),
                (LinggenElement.Water, LinggenElement.Fire), (LinggenElement.Fire, LinggenElement.Earth), (LinggenElement.Earth, LinggenElement.Metal) }
            .OrderByDescending(pair => candidateRoll.Of(pair.Item1))
            .First();
        var modified = candidateRoll.Of(source) >= 2
            ? candidateRoll
                .With(source, candidateRoll.Of(source) - 2)
                .With(target, Math.Min(LinggenValues.MaxPointsPerElement, candidateRoll.Of(target) + 1))
            : candidateRoll;
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

    /// <summary>叩天门境界、本级修为圆满的开局配置。</summary>
    private static void GateAtFullExperience(PlayerAggregate aggregate)
    {
        aggregate.Progression.RealmLevel = HeavenGateRules.HeavenGateRealmLevel;
        aggregate.Progression.Experience = GateExpCap;
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
            new FixedClock(Epoch),
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

    /// <summary>确定性固定时钟：避免真实墙钟把测试推进引入跨秒抖动。</summary>
    private sealed class FixedClock(DateTimeOffset value) : IPlayerSimulationClock
    {
        /// <inheritdoc />
        public DateTimeOffset UtcNow { get; } = value;
        /// <inheritdoc />
        public bool IsDeterministic => true;
        /// <inheritdoc />
        public void AdvanceTo(DateTimeOffset target) { }
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
