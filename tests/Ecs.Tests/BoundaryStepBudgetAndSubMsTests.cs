using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 离线推进边界预算与亚毫秒墙钟对齐回归：
/// 1. 亚毫秒起点 / double ULP 误差的 Buff、自动消耗品冷却边界不发生 0-tick 死循环或微步空转；
/// 2. 48 小时高频行动超过单窗口 10 万阈值但在请求总步数上限内可正常完成；
/// 3. 超过请求总步数上限时即使处于未准备态也必须抛出带诊断信息的 DomainException，不得被 NextBoundaryAt 的 InvalidOperationException 掩盖。
/// </summary>
public sealed class BoundaryStepBudgetAndSubMsTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog, DateTimeOffset createdAt)
        => PlayerAggregate.Create(
            new PlayerId(Guid.Parse("50000000-0000-4000-8000-000000000001")),
            new AccountId(Guid.Parse("60000000-0000-4000-8000-000000000001")),
            "边界测试",
            createdAt,
            catalog.Fingerprint);

    [Fact]
    public async Task SubMillisecondBuffAndCooldownBoundaries_DoNotSpinOrDeadlock()
    {
        var actions = new ActionCatalog([
            new(new("test.cultivate"), "打坐", ActionKind.Cultivation, 4, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);

        // 构造携带 0.5ms 亚毫秒 tick 的起点，以及 0-tick 量级（1e-8s）与 1-ULP 量级（2.38e-7s）边界。
        var subMsStart = SeedTime.AddTicks(5_000);
        var nowUnix = subMsStart.ToUnixTimeMilliseconds() / 1000d;
        var player = CreatePlayer(catalog, subMsStart);
        player.Buffs.Active.Add(new ActiveBuff("buff.zero-tick", nowUnix + 1e-8, 1, "微差增益", "item.test"));
        player.Buffs.Active.Add(new ActiveBuff("buff.ulp-offset", nowUnix + 0.5 + 2.38e-7, 1, "浮点余差增益", "item.test"));
        player.Settings.AutoUseSlots.Add(new AutoUseSlotState(AutoUseBar.Exploration, 0, "item.missing", 1, true,
            CooldownUntilUnixSeconds: nowUnix + 2.38e-7));
        player.Enqueue(new(new("test.cultivate"), -1), subMsStart, actions);

        using var runtime = new EcsGameRuntime(catalog, subMsStart);
        var id = runtime.CreatePlayer(player.ExportState());
        var target = subMsStart.AddSeconds(10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runtime.RunPlayerOnceAsync(id, target, cts.Token);

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Equal(target, snapshot.State.LastSimulatedAt);
        Assert.Empty(snapshot.State.Buffs);
        Assert.Equal(2, snapshot.State.Actions[0].CompletedRepeats);
    }

    [Fact]
    public async Task LongOffline48HoursExceeding100kOuterSteps_CompletesWithoutError()
    {
        // Cultivation 不走 CanBulkAdvance；2 秒一轮在 48h 内共 86,400 轮 + 34,560 个 5s 窗口 ≈ 120,960 外层步（> 100,000）。
        var actions = new ActionCatalog([
            new(new("test.fast-cultivate"), "极速吐纳", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 1)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);
        var player = CreatePlayer(catalog, SeedTime);
        player.Enqueue(new(new("test.fast-cultivate"), -1), SeedTime, actions);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var id = runtime.CreatePlayer(player.ExportState());
        var target = SeedTime.AddHours(48);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.RunPlayerOnceAsync(id, target, cts.Token);

        var snapshot = runtime.ExportSnapshot(id);
        Assert.Equal(target, snapshot.State.LastSimulatedAt);
        Assert.Equal(86_400, snapshot.State.Progression.CompletedActions);
    }

    [Fact]
    public async Task ExceedingMaxRequestStepsPerAdvance_ThrowsDomainExceptionInsteadOfNextBoundaryInvalidOperation()
    {
        var actions = new ActionCatalog([
            new(new("test.bulk-step"), "批量采集", ActionKind.Exploration, 0.1, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 1)]),
            new(new("test.tail-cultivate"), "尾段打坐", ActionKind.Cultivation, 0.5, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 1)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);
        var player = CreatePlayer(catalog, SeedTime);
        // 先经批量路径累积约 99.95 万步，再切入完整 ECS 循环触发请求级步数熔断（此时队首处于未准备态）。
        player.Enqueue(new(new("test.bulk-step"), 979_900), SeedTime, actions);
        player.Enqueue(new(new("test.tail-cultivate"), -1), SeedTime, actions);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var id = runtime.CreatePlayer(player.ExportState());
        var target = SeedTime.AddHours(48);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => runtime.RunPlayerOnceAsync(id, target, cts.Token).AsTask());

        Assert.Equal(DomainErrorCode.InvariantViolation, ex.Code);
        Assert.Contains("ECS 自然边界推进超过最大转换数", ex.Message);
        Assert.Contains("next=needs-preparation", ex.Message);
    }

    [Fact]
    public void DurableMaxValueBuff_InNextVisibleBoundaryUtc_DoesNotOverflow()
    {
        var actions = new ActionCatalog([
            new(new("test.cultivate"), "打坐", ActionKind.Cultivation, 4, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);

        // 场景 A：空队列且仅有 double.MaxValue 的永驻 Buff，不应在 AddSecondsExact 溢出，应返回 null。
        var idlePlayer = CreatePlayer(catalog, SeedTime);
        idlePlayer.Buffs.Active.Add(new ActiveBuff("buff.durable-array", double.MaxValue, 1, "常驻阵法", "item.array"));
        using (var runtimeIdle = new EcsGameRuntime(catalog, SeedTime))
        {
            var idIdle = runtimeIdle.CreatePlayer(idlePlayer.ExportState());
            Assert.Null(runtimeIdle.NextAdvanceBoundary(idIdle));
            Assert.Null(runtimeIdle.NextAdvanceBoundaryUtc);
        }

        // 场景 B：Running 队首行动下暂停队列且仅携带 double.MaxValue Buff，同样应返回 null。
        var pausedPlayer = CreatePlayer(catalog, SeedTime);
        var queued = pausedPlayer.Enqueue(new(new("test.cultivate"), -1), SeedTime, actions);
        queued.Status = ActionQueueStatus.Running;
        pausedPlayer.Buffs.Active.Add(new ActiveBuff("buff.durable-array", double.MaxValue, 1, "常驻阵法", "item.array"));
        pausedPlayer.SetQueuePaused(true);
        using (var runtimePaused = new EcsGameRuntime(catalog, SeedTime))
        {
            var idPaused = runtimePaused.CreatePlayer(pausedPlayer.ExportState());
            Assert.Null(runtimePaused.NextAdvanceBoundary(idPaused));
            Assert.Null(runtimePaused.NextAdvanceBoundaryUtc);
        }
    }
}
