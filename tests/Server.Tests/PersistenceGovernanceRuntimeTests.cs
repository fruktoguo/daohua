using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 两层持久化与 5 类白名单运行时契约回归测试：
/// 1. 在线模拟推进仅在内存缓冲（零每 Tick 写库），满 10 分钟检查点或关键边界一次性原子合批刷盘（PersistLedger=false）；
/// 2. 6 类单人日常命令设为 PersistLedger=false，shop.buy 与 player.unlock_avatar 严格设为 PersistLedger=true；
/// 3. 上线/离线生命周期审计（含新建角色首连、重连防重、卸载离线）；
/// 4. PVP 斗法自然结算与中途弃战均幂等写入 game.duel_logs 并立即强制刷盘。
/// </summary>
public sealed class PersistenceGovernanceRuntimeTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnlineAdvance_BuffersInMemoryWithZeroPerTickWrites_AndFlushesAtomicallyAtTenMinuteCheckpointAndBoundaries()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);
        sections.Seed(CreateOfflineBundle(playerId, "缓冲道友", catalog.Fingerprint, skipTutorial: true));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            simulationTransactionIdentity: new DeterministicSimulationTransactionIdentity(),
            serverOptions: CreateOptions(600));

        await adapter.LoadAsync(playerId);
        await adapter.FlushAsync(playerId, force: true);

        var enqReq = Guid.NewGuid();
        var enqueueResult = await adapter.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                enqReq.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options),
            0, enqReq);
        Assert.True(enqueueResult.Success, enqueueResult.Message);
        await adapter.FlushAsync(playerId, force: true);

        var sectionsAfterEnqueue = sections.EnqueueCount;
        Assert.Empty(economy.Requests);

        // 1. +37s 与 +74s（未满 10 分钟检查点）：零 DB 写入，内存钱包与快照实时可见采集收益
        clock.Advance(TimeSpan.FromSeconds(37));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(37));
        await adapter.AdvanceAllAsync(clock.UtcNow);

        Assert.Empty(economy.Requests);
        Assert.Equal(sectionsAfterEnqueue, sections.EnqueueCount);
        var loadedMid = adapter.TryGetLoaded(playerId)!;
        var grassQtyMid = loadedMid.Assets.Single(a => a.AssetId == "item.ningxue-grass").Quantity;
        Assert.True(grassQtyMid > 0, "在线模拟收益应实时反映在内存钱包/快照中");
        Assert.Empty(await assets.LoadBalancesAsync(playerId));

        // 2. 推至 +600s（满 10 分钟安全检查点）：原子合批提交 CreateCheckpoint（PersistLedger=false）与 Progression 分段
        clock.UtcNow = Epoch.AddMinutes(10);
        await adapter.AdvanceAllAsync(clock.UtcNow);

        var checkpointReq = Assert.Single(economy.Requests);
        Assert.False(checkpointReq.PersistLedger);
        Assert.StartsWith("sim-ckpt:", checkpointReq.IdempotencyKey, StringComparison.Ordinal);
        Assert.Contains(checkpointReq.Sections!, s => s.SectionKind == PlayerSectionKinds.Progression);
        var grassPersistedAtCheckpoint = (await assets.LoadBalancesAsync(playerId))
            .Single(b => b.AssetId == "item.ningxue-grass").Quantity;
        Assert.True(grassPersistedAtCheckpoint > grassQtyMid);

        // 3. 检查点刷盘失败（IOException）：内存钱包、缓冲差量与时钟完整回滚
        clock.UtcNow = Epoch.AddMinutes(20);
        economy.FailNextCommit = true;
        await Assert.ThrowsAsync<IOException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));
        Assert.Equal(Epoch.AddMinutes(10), adapter.TryGetLoaded(playerId)!.LastSimulatedAt);
        Assert.Equal(grassPersistedAtCheckpoint,
            adapter.TryGetLoaded(playerId)!.Assets.Single(a => a.AssetId == "item.ningxue-grass").Quantity);

        // 4. 在 +20m37s（新周期内，尚未到 +30m 检查点）触发新采集后再执行 item.sell：
        // 命令前必须先将未落盘采集增量经 sim-ckpt 刷库，再执行 item.sell，二者均 PersistLedger=false 且余额严格一致。
        clock.UtcNow = Epoch.AddMinutes(10).AddSeconds(37);
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Single(economy.Requests);

        var grassBeforeSell = adapter.TryGetLoaded(playerId)!.Assets.Single(a => a.AssetId == "item.ningxue-grass").Quantity;
        Assert.True(grassBeforeSell > grassPersistedAtCheckpoint);

        var sellReqId = Guid.NewGuid();
        var sellResult = await adapter.ExecuteAsync(playerId, "item.sell",
            JsonSerializer.SerializeToElement(new { itemId = "item.ningxue-grass", variant = "", quantity = 1 }),
            0, sellReqId);
        Assert.True(sellResult.Success, sellResult.Message);

        Assert.Equal(3, economy.Requests.Count);
        Assert.StartsWith("sim-ckpt:", economy.Requests[1].IdempotencyKey, StringComparison.Ordinal);
        Assert.False(economy.Requests[1].PersistLedger);
        Assert.Equal("item.sell", economy.Requests[2].TransactionType);
        Assert.False(economy.Requests[2].PersistLedger);

        var dbBalances = await assets.LoadBalancesAsync(playerId);
        Assert.Equal(grassBeforeSell - 1, dbBalances.Single(b => b.AssetId == "item.ningxue-grass").Quantity);
        Assert.Equal(5, dbBalances.Single(b => b.AssetId == "currency.spirit-stone").Quantity);
    }

    [Fact]
    public async Task SinglePlayerDailyCommands_SetPersistLedgerFalse_WhileShopBuyAndUnlockAvatarPersistLedgerTrue()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        assets.Seed(playerId, ("item.tome.heavy-slash", "", 1));
        assets.Seed(playerId, ("item.ningxue-grass", "", 3));
        assets.Seed(playerId, ("item.gongde-bound", "", 10000));
        var economy = new RecordingEconomy(sections, assets);
        sections.Seed(CreateOfflineBundle(playerId, "白名单命令测试", catalog.Fingerprint, skipTutorial: false));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock);
        await adapter.LoadAsync(playerId);

        // 单人日常 1: tutorial.skip -> PersistLedger=false
        var skipResult = await adapter.ExecuteAsync(playerId, "tutorial.skip",
            JsonSerializer.SerializeToElement(new { }), 0, Guid.NewGuid());
        Assert.True(skipResult.Success, skipResult.Message);
        Assert.Equal("tutorial.skip", economy.Requests[^1].TransactionType);
        Assert.False(economy.Requests[^1].PersistLedger);

        // 单人日常 2: ability.learn -> PersistLedger=false
        var learnResult = await adapter.ExecuteAsync(playerId, "ability.learn",
            JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash" }), 0, Guid.NewGuid());
        Assert.True(learnResult.Success, learnResult.Message);
        Assert.Equal("ability.learn", economy.Requests[^1].TransactionType);
        Assert.False(economy.Requests[^1].PersistLedger);

        // 单人日常 3 & 4: item.sell & item.discard -> PersistLedger=false
        var sellResult = await adapter.ExecuteAsync(playerId, "item.sell",
            JsonSerializer.SerializeToElement(new { itemId = "item.ningxue-grass", variant = "", quantity = 1 }), 0, Guid.NewGuid());
        Assert.True(sellResult.Success, sellResult.Message);
        Assert.Equal("item.sell", economy.Requests[^1].TransactionType);
        Assert.False(economy.Requests[^1].PersistLedger);

        var discardResult = await adapter.ExecuteAsync(playerId, "item.discard",
            JsonSerializer.SerializeToElement(new { itemId = "item.ningxue-grass", variant = "", quantity = 1 }), 0, Guid.NewGuid());
        Assert.True(discardResult.Success, discardResult.Message);
        Assert.Equal("item.discard", economy.Requests[^1].TransactionType);
        Assert.False(economy.Requests[^1].PersistLedger);

        // 白名单第 2 类: shop.buy -> PersistLedger=true
        var buyResult = await adapter.ExecuteAsync(playerId, "shop.buy",
            JsonSerializer.SerializeToElement(new { offerId = "shop.awakening-stone.daily-100", quantity = 1 }), 0, Guid.NewGuid());
        Assert.True(buyResult.Success, buyResult.Message);
        Assert.Equal("shop.buy", economy.Requests[^1].TransactionType);
        Assert.True(economy.Requests[^1].PersistLedger);

        // 白名单第 2 类: player.unlock_avatar -> PersistLedger=true
        var avatarResult = await adapter.ExecuteAsync(playerId, "player.unlock_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "sword-immortal" }), 0, Guid.NewGuid());
        Assert.True(avatarResult.Success, avatarResult.Message);
        Assert.Equal("player.unlock_avatar", economy.Requests[^1].TransactionType);
        Assert.True(economy.Requests[^1].PersistLedger);
    }

    [Fact]
    public async Task PlayerLifecycleAudit_EmitsOnlineForReconnectAndBrandNewPlayer_WithoutDuplicateOnReattach_AndOfflineOnUnload()
    {
        var brandNewPlayerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);
        var lifecycleAudit = new RecordingLifecycleAuditStore();

        // 空分段新建档：PlayerAggregate.Create 默认就是 Presence=Online，首连也必须记录 player.online
        sections.Seed(new PlayerStateBundle(
            new PlayerIdentity(brandNewPlayerId, accountId, "初入仙途", Epoch),
            new StoredHead(brandNewPlayerId, 0, SectionCodec.CurrentSchemaVersion, Epoch, catalog.Fingerprint, "", Epoch),
            new Dictionary<string, StoredSection>(StringComparer.Ordinal),
            []));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            lifecycleAudit: lifecycleAudit);

        // 1. 新建角色首次连接 -> 写入 1 条 player.online
        await adapter.AttachConnectionAsync(brandNewPlayerId);
        Assert.Single(lifecycleAudit.Events, e => e.EventType == "player.online" && e.PlayerId == brandNewPlayerId && e.AccountId == accountId);

        // 2. 同会话重复 AttachConnectionAsync -> 不重复刷审计
        await adapter.AttachConnectionAsync(brandNewPlayerId);
        Assert.Single(lifecycleAudit.Events, e => e.EventType == "player.online");

        // 3. 真实卸载 RemoveAsync(flush: true) -> 写入 1 条 player.offline
        clock.Advance(TimeSpan.FromMinutes(2));
        await adapter.RemoveAsync(brandNewPlayerId, flush: true, offlineSince: clock.UtcNow);
        Assert.Single(lifecycleAudit.Events, e => e.EventType == "player.offline" && e.PlayerId == brandNewPlayerId);

        // 4. 离线重连 AttachConnectionAsync -> Offline 转 Online 再写 1 条 player.online
        clock.Advance(TimeSpan.FromMinutes(5));
        await adapter.AttachConnectionAsync(brandNewPlayerId);
        Assert.Equal(2, lifecycleAudit.Events.Count(e => e.EventType == "player.online"));
        Assert.Single(lifecycleAudit.Events, e => e.EventType == "player.offline");
    }

    [Fact]
    public async Task PvpDuel_NormalCompletionAndForfeit_RecordSingleDuelLogWithGainsLossesAndForceFlush()
    {
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);
        var duelLogs = new RecordingDuelLogRepository();

        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        sections.Seed(CreateDuelistBundle(playerA, "青云剑修", catalog.Fingerprint, "item.ningxue-grass", 3, equipSkill: true));
        sections.Seed(CreateDuelistBundle(playerB, "云溪散修", catalog.Fingerprint, "item.peach-wood", 2, equipSkill: false));

        using var ecs = new EcsGameRuntime(catalog, Epoch, duelAdvance: new DuelAdvanceOptions(MatchingEnabled: true));
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(600),
            duelLogs: duelLogs);

        await adapter.AttachConnectionAsync(playerA);
        await adapter.AttachConnectionAsync(playerB);
        await adapter.FlushAsync(playerA, force: true);
        await adapter.FlushAsync(playerB, force: true);

        // 入队同节点 PvP 讨伐
        await EnqueueCombatAsync(adapter, playerA);
        await EnqueueCombatAsync(adapter, playerB);

        // 场景 A：推进直到撮合斗法，再推进至斗法自然结算
        string? matchedDuelId = null;
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await adapter.AdvanceAllAsync(clock.UtcNow);
            matchedDuelId ??= adapter.TryGetLoaded(playerA)!.State.Duel.ActiveDuelId;
            if (matchedDuelId is not null
                && adapter.TryGetLoaded(playerA)!.State.Duel.ActiveDuelId is null
                && adapter.TryGetLoaded(playerB)!.State.Duel.ActiveDuelId is null)
            {
                break;
            }
        }

        Assert.False(string.IsNullOrEmpty(matchedDuelId));
        var normalEntry = Assert.Single(duelLogs.Entries, e => e.DuelId == matchedDuelId);
        Assert.False(normalEntry.Forfeited);
        Assert.Equal("node.yunxi-village", normalEntry.NodeId);
        Assert.Equal(playerA, normalEntry.WinnerPlayerId);
        if (normalEntry.InitiatorPlayerId == playerA)
        {
            Assert.Equal("initiator_win", normalEntry.Outcome);
            var gain = Assert.Single(normalEntry.InitiatorGains);
            Assert.Equal("item.peach-wood", gain.ItemId);
            Assert.Equal(2d, gain.Quantity);
            var loss = Assert.Single(normalEntry.TargetLosses);
            Assert.Equal("item.peach-wood", loss.ItemId);
            Assert.Equal(2d, loss.Quantity);
        }
        else
        {
            Assert.Equal("target_win", normalEntry.Outcome);
            var gain = Assert.Single(normalEntry.TargetGains);
            Assert.Equal("item.peach-wood", gain.ItemId);
            Assert.Equal(2d, gain.Quantity);
            var loss = Assert.Single(normalEntry.InitiatorLosses);
            Assert.Equal("item.peach-wood", loss.ItemId);
            Assert.Equal(2d, loss.Quantity);
        }
        // 未满 10 分钟检查点，斗法结束时必须已触发持久化刷盘
        Assert.True(adapter.TryGetLoaded(playerA)!.LastSimulatedAt < Epoch.AddMinutes(10));
        Assert.Equal(adapter.TryGetLoaded(playerA)!.LastSimulatedAt,
            SectionCodec.DecodeAggregate((await sections.LoadAsync(playerA))!).LastSimulatedAt);

        // 场景 B：卸载场景 A 玩家避免串场撮合，再起一对玩家，撮合后主动认输弃战（queue.clear）
        await adapter.RemoveAsync(playerA, flush: true, offlineSince: clock.UtcNow);
        await adapter.RemoveAsync(playerB, flush: true, offlineSince: clock.UtcNow);

        var playerC = Guid.NewGuid();
        var playerD = Guid.NewGuid();
        clock.Advance(TimeSpan.FromSeconds(5));
        sections.Seed(CreateDuelistBundle(playerC, "弃战测试甲", catalog.Fingerprint, "item.ningxue-grass", 4, equipSkill: true, at: clock.UtcNow));
        sections.Seed(CreateDuelistBundle(playerD, "受降测试乙", catalog.Fingerprint, "item.peach-wood", 2, equipSkill: true, at: clock.UtcNow));
        await adapter.AttachConnectionAsync(playerC);
        await adapter.AttachConnectionAsync(playerD);
        await adapter.FlushAsync(playerC, force: true);
        await adapter.FlushAsync(playerD, force: true);
        await EnqueueCombatAsync(adapter, playerC);
        await EnqueueCombatAsync(adapter, playerD);

        string? forfeitDuelId = null;
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await adapter.AdvanceAllAsync(clock.UtcNow);
            forfeitDuelId = adapter.TryGetLoaded(playerC)!.State.Duel.ActiveDuelId;
            if (!string.IsNullOrEmpty(forfeitDuelId)) break;
        }
        Assert.False(string.IsNullOrEmpty(forfeitDuelId));

        var writesBeforeForfeit = sections.WriteCountFor(playerC) + sections.WriteCountFor(playerD);
        var clearResult = await adapter.ExecuteAsync(playerC, "queue.clear",
            JsonSerializer.SerializeToElement(new { }), 0, Guid.NewGuid());
        Assert.True(clearResult.Success, clearResult.Message);

        var forfeitEntry = Assert.Single(duelLogs.Entries, e => e.DuelId == forfeitDuelId);
        Assert.True(forfeitEntry.Forfeited);
        Assert.Equal(playerD, forfeitEntry.WinnerPlayerId);
        var forfeiterLosses = forfeitEntry.InitiatorPlayerId == playerC
            ? forfeitEntry.InitiatorLosses
            : forfeitEntry.TargetLosses;
        var lostItem = Assert.Single(forfeiterLosses);
        Assert.Equal("item.ningxue-grass", lostItem.ItemId);
        Assert.Equal(4d, lostItem.Quantity);
        // 弃战方与在线对手双侧都必须立即刷盘落库
        Assert.True(sections.WriteCountFor(playerC) + sections.WriteCountFor(playerD) > writesBeforeForfeit);
        Assert.Null(SectionCodec.DecodeAggregate((await sections.LoadAsync(playerC))!).Duel.ActiveDuelId);
        var dPersistedDuel = SectionCodec.DecodeAggregate((await sections.LoadAsync(playerD))!).Actions.FirstOrDefault()?.Expedition?.Duel;
        Assert.NotNull(dPersistedDuel);
        Assert.True(dPersistedDuel!.Forfeited);
        Assert.True(dPersistedDuel.Resolved);
    }

    /// <summary>
    /// 单玩家模拟或资产损坏抛出异常时，AdvanceAllAsync 必须按玩家隔离并保留异常上报，
    /// 严禁因为一名坏玩家导致全服健康在线玩家停摆回滚。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsync_IsolatesBrokenPlayerAndAllowsHealthyPlayersToContinue()
    {
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);

        var healthyA = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var brokenB = Guid.Parse("00000000-0000-4000-8000-000000000002");
        var healthyC = Guid.Parse("00000000-0000-4000-8000-000000000003");
        foreach (var (id, name) in new[] { (healthyA, "健康甲"), (brokenB, "异常乙"), (healthyC, "健康丙") })
            sections.Seed(CreateOfflineBundle(id, name, catalog.Fingerprint, skipTutorial: true));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(600));

        foreach (var id in new[] { healthyA, brokenB, healthyC })
        {
            await adapter.AttachConnectionAsync(id);
            var req = Guid.NewGuid();
            var enq = await adapter.ExecuteAsync(id, "action.enqueue",
                JsonSerializer.SerializeToElement(new ActionIntentRequest(
                    req.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options),
                0, req);
            Assert.True(enq.Success, enq.Message);
        }

        // 在 ECS 库存中注入负余额以精确复现 AssetWallet.Restore("资产余额无效。") 的单人故障
        ecs.SyncAssetBalances(brokenB,
            [new Domain.Economy.AssetBalance(new Domain.Common.PlayerId(brokenB),
                new Domain.Economy.AssetKey(new Domain.Common.ItemId("item.ningxue-grass")), -1d, 0)]);

        clock.Advance(TimeSpan.FromSeconds(37));
        await Assert.ThrowsAsync<DomainException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));

        // 健康甲与健康丙（排序分别在异常乙前后）都必须正常推进并获得收益
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyA)!.LastSimulatedAt);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyC)!.LastSimulatedAt);
        Assert.True(adapter.TryGetLoaded(healthyA)!.Assets.Single(a => a.AssetId == "item.ningxue-grass").Quantity > 0);
        Assert.True(adapter.TryGetLoaded(healthyC)!.Assets.Single(a => a.AssetId == "item.ningxue-grass").Quantity > 0);
        Assert.Equal(Epoch, adapter.TryGetLoaded(brokenB)!.LastSimulatedAt);
    }

    /// <summary>
    /// 当 ECS 内存库存与宿主钱包发生分叉导致 BufferOnlineSimulationOwned 扣出负余额时，
    /// 必须立即拒绝写入负余额、完整回滚并发起钱包重读自愈，防止负余额写回 ECS 或推给前端。
    /// </summary>
    [Fact]
    public async Task BufferOnlineSimulationOwned_RejectsNegativeWalletDeltaAndSelfHealsOnNextTick()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);

        var playerId = Guid.NewGuid();
        sections.Seed(CreateOfflineBundle(playerId, "防负自愈", catalog.Fingerprint, skipTutorial: true));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(600));

        await adapter.AttachConnectionAsync(playerId);
        // 仅向 ECS 注入 1 份材料而持久层与 _wallets 为 0，模拟库存分叉后触发扣减
        var enqReq = Guid.NewGuid();
        var enq = await adapter.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                enqReq.ToString("D"), "alchemy.refine-zhixue-powder", -1), ProtocolJson.Options),
            0, enqReq);
        Assert.True(enq.Success, enq.Message);

        ecs.SyncAssetBalances(playerId,
            [new Domain.Economy.AssetBalance(new Domain.Common.PlayerId(playerId),
                new Domain.Economy.AssetKey(new Domain.Common.ItemId("item.ningxue-grass")), 1d, 0),
             new Domain.Economy.AssetBalance(new Domain.Common.PlayerId(playerId),
                new Domain.Economy.AssetKey(new Domain.Common.ItemId("item.herb-essence")), 1d, 0)]);

        clock.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<DomainException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));

        // 拒绝负余额后快照与钱包绝对不得出现 Quantity < 0，且时间回到推进前
        var afterReject = adapter.TryGetLoaded(playerId)!;
        Assert.Equal(Epoch, afterReject.LastSimulatedAt);
        Assert.All(afterReject.Assets, asset => Assert.True(asset.Quantity >= 0));

        // 下一轮推进触发 EnsureWalletFreshAsync 自动从存储重读权威余额（0），同步覆盖修复 ECS 库存并恢复阻塞推进
        clock.Advance(TimeSpan.FromSeconds(5));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        var afterHeal = adapter.TryGetLoaded(playerId)!;
        Assert.Equal(clock.UtcNow, afterHeal.LastSimulatedAt);
        Assert.All(afterHeal.Assets, asset => Assert.True(asset.Quantity >= 0));
        Assert.Equal(ActionQueueStatus.Blocked, afterHeal.State.Actions[0].Status);
    }

    /// <summary>
    /// 回归：同节点开启斗法的玩家在原地复活遭遇战倒地（Phase=Combat 且 PlayerRespawnAtSeconds!=null）时，
    /// 即使持有遭遇计数也不得被 MatchDuelsOwnedAsync 选作应战方而拼出「Duel+PlayerRespawnAtSeconds」无效态。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsync_RespawningPvpDefenderIsNotMatchedIntoInvalidDuel()
    {
        var catalog = LoadIsolatedCatalog(playerRespawnSeconds: 60, onPartyWipe: "respawn", searchSeconds: 2);
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);

        var seekerId = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
        var downedDefenderId = Guid.Parse("00000000-0000-4000-8000-0000000000c2");
        sections.Seed(CreateDuelistBundle(seekerId, "寻战甲", catalog.Fingerprint, "item.peach-wood", 1, equipSkill: true));
        sections.Seed(CreateDuelistBundle(downedDefenderId, "倒地乙", catalog.Fingerprint, "item.peach-wood", 1, equipSkill: false));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(600));

        await adapter.AttachConnectionAsync(seekerId);
        await adapter.AttachConnectionAsync(downedDefenderId);
        await EnqueueCombatAsync(adapter, seekerId);
        await EnqueueCombatAsync(adapter, downedDefenderId);

        // 先走完 InitialSearch 入场建档；然后把应战方置入「战斗倒地等待原地复活」合法状态（与线上 66b188b2 事故前完全一致）。
        clock.Advance(TimeSpan.FromSeconds(1));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        ecs.MutatePlayer(downedDefenderId, aggregate =>
        {
            aggregate.Actions[0].Status = ActionQueueStatus.Running;
            aggregate.Actions[0].ProgressSeconds = 0;
            aggregate.Resources.Hitpoints = 0;
            aggregate.Actions[0].Expedition = new CombatExpeditionState(
                "encounter.test-qi-beast",
                CombatExpeditionPhase.Combat,
                0, 0, 123L,
                BattleElapsedSeconds: 10,
                PlayerHitpoints: 0,
                PlayerManapoints: 100,
                PlayerNextAttackAtSeconds: 12,
                PlayerAttackCount: 1,
                PlayerThreat: 1,
                CombatOutcome.Running,
                WaveNumber: 1,
                [new ExpeditionEnemyState("enemy.test-beast", new CombatantSnapshot(100, 10, 2, 0, 0))])
            {
                PlayerState = new CombatantSnapshot(0, 100, 12, 1, 1, RespawnAtSeconds: 120),
                PlayerRespawnAtSeconds = 120
            };
        });

        // 连续推进跨越寻战甲 2s Search 边界进入 DuelPending：绝不能把倒地乙拉入无效斗法状态，全服稳定推进且无任何异常。
        for (var step = 0; step < 6; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await adapter.AdvanceAllAsync(clock.UtcNow);
            var defenderSnapshot = adapter.TryGetLoaded(downedDefenderId)!;
            Assert.NotNull(PlayerAggregate.Restore(defenderSnapshot.State));
            Assert.NotEqual(CombatExpeditionPhase.Duel, defenderSnapshot.State.Actions[0].Expedition?.Phase);
        }

        // 倒地乙此时仍未复活（总计推进 13s < 120s），双方均未报错且均在下次可见边界正常跟进。
        Assert.Null(adapter.TryGetLoaded(seekerId)!.State.Duel.ActiveDuelId);
        Assert.Null(adapter.TryGetLoaded(downedDefenderId)!.State.Duel.ActiveDuelId);
        Assert.NotNull(PlayerAggregate.Restore(adapter.TryGetLoaded(downedDefenderId)!.State));
    }

    /// <summary>
    /// 回归：若某玩家运行时快照在推进前已损坏、导致异常回滚时的 PlayerAggregate.Restore 一并抛出，
    /// AdvanceAllAsync 必须将其强制逐出内存且不得拖累同批健康玩家回滚，也不得把调度边界钉死在过去触发 50ms 空转。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsync_EvictsCorruptedRollbackPlayerAndKeepsBoundaryHealthy()
    {
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var economy = new RecordingEconomy(sections, assets);

        var healthyA = Guid.Parse("00000000-0000-4000-8000-0000000000d1");
        var corruptB = Guid.Parse("00000000-0000-4000-8000-0000000000d2");
        var healthyC = Guid.Parse("00000000-0000-4000-8000-0000000000d3");
        foreach (var (id, name) in new[] { (healthyA, "健康甲"), (corruptB, "坏档乙"), (healthyC, "健康丙") })
            sections.Seed(CreateOfflineBundle(id, name, catalog.Fingerprint, skipTutorial: true));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(600));

        foreach (var id in new[] { healthyA, corruptB, healthyC })
            await adapter.AttachConnectionAsync(id);
        foreach (var id in new[] { healthyA, healthyC })
        {
            var enqReq = Guid.NewGuid();
            var enq = await adapter.ExecuteAsync(id, "action.enqueue",
                JsonSerializer.SerializeToElement(new ActionIntentRequest(
                    enqReq.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options),
                0, enqReq);
            Assert.True(enq.Success, enq.Message);
        }
        await EnqueueCombatAsync(adapter, corruptB);

        // 在内存组件直接制造与生产故障同型的坏态（Phase=Duel 且携带 PlayerRespawnAtSeconds）：
        // RunPlayerOnceAsync 校验失败的同时，RestoreBufferingForRollback 触发 PlayerAggregate.Restore 亦将抛出。
        ecs.MutatePlayer(corruptB, aggregate =>
        {
            aggregate.Actions[0].Status = ActionQueueStatus.Running;
            aggregate.Actions[0].ProgressSeconds = 0;
            aggregate.Actions[0].Expedition = new CombatExpeditionState(
                "encounter.test-qi-beast",
                CombatExpeditionPhase.Duel,
                0, 0, 1L,
                BattleElapsedSeconds: 0,
                PlayerHitpoints: 100,
                PlayerManapoints: 100,
                PlayerNextAttackAtSeconds: 0,
                PlayerAttackCount: 0,
                PlayerThreat: 0,
                CombatOutcome.Running,
                WaveNumber: 1,
                null)
            {
                PlayerRespawnAtSeconds = 204.79
            };
        });

        clock.Advance(TimeSpan.FromSeconds(15));
        var thrown = await Assert.ThrowsAsync<AggregateException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));
        Assert.Contains(thrown.InnerExceptions, ex => ex is DomainException && ex.Message.Contains("战斗远征状态无效"));
        Assert.Contains(thrown.InnerExceptions, ex => ex is DomainException && ex.Message.Contains("行动状态无效"));

        // 坏档乙已被直接逐出内存；健康甲与健康丙本轮正常推进，且下一边界未钉死在过去。
        Assert.Null(adapter.TryGetLoaded(corruptB));
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyA)!.LastSimulatedAt);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyC)!.LastSimulatedAt);
        Assert.True(adapter.NextAdvanceBoundaryUtc is null || adapter.NextAdvanceBoundaryUtc >= clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(25));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyA)!.LastSimulatedAt);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyC)!.LastSimulatedAt);
    }

    /// <summary>
    /// 回归：单玩家状态合法但持续推进/刷盘失败时，第 1 次失败仍允许下拍立即自愈重连，
    /// 连续失败第 2 次起进入退避并从 NextAdvanceBoundaryUtc 剔除滞留边界，待退避到期并故障解除后自动恢复。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsync_BacksOffPersistentlyFailingPlayerAndUnblocksBoundary()
    {
        var catalog = LoadIsolatedCatalog();
        var clock = new MutableClock(Epoch);
        var sections = new MultiPlayerSections();
        var assets = new MultiPlayerAssets();
        var failingPlayerId = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
        var healthyPlayerId = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
        var economy = new RecordingEconomy(sections, assets) { FailCommitsForPlayerId = failingPlayerId };

        sections.Seed(CreateOfflineBundle(healthyPlayerId, "正常甲", catalog.Fingerprint, skipTutorial: true));
        sections.Seed(CreateOfflineBundle(failingPlayerId, "阻滞乙", catalog.Fingerprint, skipTutorial: true));

        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: CreateOptions(1));

        foreach (var id in new[] { healthyPlayerId, failingPlayerId })
        {
            await adapter.AttachConnectionAsync(id);
            var req = Guid.NewGuid();
            var enq = await adapter.ExecuteAsync(id, "action.enqueue",
                JsonSerializer.SerializeToElement(new ActionIntentRequest(
                    req.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options),
                0, req);
            Assert.True(enq.Success, enq.Message);
        }

        // 第 1 次刷盘失败（ConsecutiveFailures=1）：跨越 8s 采集完成边界，正常甲推进并落袋，阻滞乙回滚到 Epoch。
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IOException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyPlayerId)!.LastSimulatedAt);
        Assert.Equal(Epoch, adapter.TryGetLoaded(failingPlayerId)!.LastSimulatedAt);

        // 第 2 次刷盘失败（ConsecutiveFailures=2）：1s 后仍触发即时重试并再次失败，阻滞乙进入 4 秒退避窗口，NextAdvanceBoundaryUtc 推后。
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(async () => await adapter.AdvanceAllAsync(clock.UtcNow));
        Assert.Equal(Epoch, adapter.TryGetLoaded(failingPlayerId)!.LastSimulatedAt);
        Assert.True(adapter.NextAdvanceBoundaryUtc > clock.UtcNow);

        // 退避窗口内（+1s）再推进：阻滞乙被静默跳过，AdvanceAllAsync 无异常通过；+6s 跨越健康甲第二轮采集边界时健康甲正常推进。
        clock.Advance(TimeSpan.FromSeconds(1));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Equal(Epoch, adapter.TryGetLoaded(failingPlayerId)!.LastSimulatedAt);

        // 解除故障并跨越退避截止点（+6s，总计 Epoch+16s，同时覆盖健康甲第二轮 8s 采集与阻滞乙的 4s 退避），全员自动恢复对齐。
        economy.FailCommitsForPlayerId = null;
        clock.Advance(TimeSpan.FromSeconds(6));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(healthyPlayerId)!.LastSimulatedAt);
        Assert.Equal(clock.UtcNow, adapter.TryGetLoaded(failingPlayerId)!.LastSimulatedAt);
    }

    private static async Task EnqueueCombatAsync(EcsPlayerRuntimeAdapter adapter, Guid playerId)
    {
        var reqId = Guid.NewGuid();
        var result = await adapter.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                reqId.ToString("D"), "combat.hunt-qi-beast", -1, PvpEnabled: true), ProtocolJson.Options),
            0, reqId);
        Assert.True(result.Success, result.Message);
    }

    private static PlayerStateBundle CreateOfflineBundle(Guid playerId, string name, string fingerprint, bool skipTutorial)
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId), name, Epoch, fingerprint);
        if (skipTutorial) aggregate.SkipTutorial();
        aggregate.SetPresence(PresenceState.Offline, Epoch);
        return BundleFromState(aggregate.ExportState());
    }

    private static PlayerStateBundle CreateDuelistBundle(
        Guid playerId, string name, string fingerprint, string stagedItemId, double stagedQuantity,
        bool equipSkill, DateTimeOffset? at = null)
    {
        var start = at ?? Epoch;
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId), name, start, fingerprint);
        aggregate.SkipTutorial();
        aggregate.Duel.EncounterCharges = 2;
        aggregate.StageItem(new ItemId(stagedItemId), stagedQuantity, start.ToUnixTimeSeconds());
        if (equipSkill)
        {
            aggregate.LearnAbility(new AbilityId("ability.heavy-slash"));
            if (aggregate.Loadout.AbilitySlots.Count == 0)
                aggregate.Loadout.AbilitySlots.Add(new PlayerAbilitySlot("ability.heavy-slash", 1));
            else
                aggregate.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.heavy-slash", 1);
        }
        aggregate.SetPresence(PresenceState.Offline, start);
        return BundleFromState(aggregate.ExportState());
    }

    private static ServerOptions CreateOptions(int onlineCheckpointSeconds) =>
        ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
            ["Storage:OnlineCheckpointIntervalSeconds"] = onlineCheckpointSeconds.ToString()
        }).Build());

    private static PlayerStateBundle BundleFromState(PlayerAggregateState state)
    {
        var writes = SectionCodec.EncodeAggregateFull(state, state.LastSimulatedAt);
        var map = writes.ToDictionary(
            w => w.SectionKind,
            w => new StoredSection(w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec, w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt),
            StringComparer.Ordinal);
        var head = new StoredHead(state.PlayerId.Value, state.StateVersion, SectionCodec.CurrentSchemaVersion,
            state.LastSimulatedAt, state.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(map.Values), state.LastSimulatedAt);
        return new PlayerStateBundle(new PlayerIdentity(state.PlayerId.Value, state.AccountId.Value, state.DisplayName, state.CreatedAt),
            head, map, []);
    }

    private static GameDataCatalog LoadIsolatedCatalog(double playerRespawnSeconds = 0, string? onPartyWipe = null, double? searchSeconds = null)
    {
        string[] stablePaths =
        [
            "duel.json",
            "expected-attributes.json",
            "actions/gathering.json",
            "actions/world.json",
            "items/materials.json",
            "shop/offers.json"
        ];
        var assembly = typeof(GameDataCatalog).Assembly;
        var docs = assembly.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '/'))
            .Zip(assembly.GetManifestResourceNames())
            .Where(pair => stablePaths.Any(path =>
                pair.First.EndsWith(path, StringComparison.OrdinalIgnoreCase)
                || pair.First.EndsWith(path.Replace('/', '.'), StringComparison.OrdinalIgnoreCase)))
            .Select(pair =>
            {
                using var stream = assembly.GetManifestResourceStream(pair.Second)!;
                var bytes = new byte[stream.Length];
                var read = stream.Read(bytes, 0, bytes.Length);
                return (ReadOnlyMemory<byte>)bytes.AsMemory(0, read);
            })
            .ToList();

        Assert.Equal(stablePaths.Length, docs.Count);

        docs.Add(Encoding.UTF8.GetBytes("""
        [
          {
            "id": "map.qingyun-mountains",
            "name": "青云仙山",
            "description": "测试最小地图",
            "nodes": [
              {
                "id": "node.yunxi-village",
                "name": "云溪村",
                "description": "起点节点",
                "x": 20,
                "y": 65,
                "availableActionIds": ["explore.gather-ningxue-grass", "combat.hunt-qi-beast"]
              }
            ],
            "edges": []
          }
        ]
        """));

        docs.Add(Encoding.UTF8.GetBytes("""
        [
          {
            "id": "ability.heavy-slash",
            "name": "重斩",
            "description": "重剑破空。",
            "style": "sword",
            "tier": 1,
            "category": "physical",
            "element": "metal",
            "cooldownMilliseconds": 3000,
            "castMilliseconds": 500,
            "damageRatio": 1.5,
            "flatDamage": 15,
            "manaCost": 0.0,
            "rootAttribute": "agility",
            "discipline": "spell"
          }
        ]
        """));
        docs.Add(Encoding.UTF8.GetBytes("""
        [
          {
            "id": "item.tome.heavy-slash",
            "name": "《重斩秘籍》",
            "description": "测试秘籍",
            "kind": "consumable",
            "category": "manual",
            "sellPrice": 100,
            "subCategory": "manualTome",
            "level": 1
          }
        ]
        """));
        docs.Add(Encoding.UTF8.GetBytes("""
        [
          {
            "id": "enemy.test-beast",
            "name": "测试灵兽",
            "visual": { "renderer": "text", "source": "兽" },
            "level": 1,
            "budgetCost": 10,
            "weights": { "hp": 0.5, "ad": 0.3, "armor": 0.5, "hit": 1.0, "eva": 1.0 },
            "abilities": []
          }
        ]
        """));
        var respawnJson = onPartyWipe is null
            ? ""
            : $", \"playerRespawnSeconds\": {playerRespawnSeconds}, \"onPartyWipe\": \"{onPartyWipe}\"";
        var searchJson = searchSeconds is { } ss ? $", \"searchSeconds\": {ss}" : "";
        docs.Add(Encoding.UTF8.GetBytes($$"""
        [
          {
            "id": "encounter.test-qi-beast",
            "name": "测试遭遇",
            "minBudget": 10,
            "maxBudget": 15{{respawnJson}}{{searchJson}},
            "randomSpawns": [{ "enemyId": "enemy.test-beast", "weight": 1, "strength": 15 }]
          }
        ]
        """));
        docs.Add(Encoding.UTF8.GetBytes("""
        [
          {
            "id": "combat.hunt-qi-beast",
            "name": "讨伐 青云仙山",
            "description": "测试讨伐行动",
            "type": "combat",
            "level": 1,
            "durationSeconds": 0,
            "executionKind": "combatEncounter",
            "combatEncounterId": "encounter.test-qi-beast",
            "availableNodeIds": ["node.yunxi-village"]
          }
        ]
        """));

        return GameDataCatalog.Parse(docs);
    }

    private sealed class MutableClock(DateTimeOffset initial) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class MultiPlayerSections : IPlayerStateRepository
    {
        private readonly ConcurrentDictionary<Guid, PlayerIdentity> _identities = new();
        private readonly ConcurrentDictionary<Guid, string> _fingerprints = new();
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSimulatedAt = new();
        private readonly ConcurrentDictionary<Guid, Dictionary<string, StoredSection>> _sections = new();
        private readonly ConcurrentDictionary<Guid, int> _writesByPlayer = new();
        public int EnqueueCount { get; private set; }
        public int WriteCountFor(Guid playerId) => _writesByPlayer.GetValueOrDefault(playerId);

        public void Seed(PlayerStateBundle bundle)
        {
            _identities[bundle.Identity.PlayerId] = bundle.Identity;
            _fingerprints[bundle.Identity.PlayerId] = bundle.Head.GameDataFingerprint;
            _lastSimulatedAt[bundle.Identity.PlayerId] = bundle.Head.LastSimulatedAt;
            _sections[bundle.Identity.PlayerId] = new Dictionary<string, StoredSection>(bundle.Sections, StringComparer.Ordinal);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            if (!_identities.TryGetValue(playerId, out var identity) || !_sections.TryGetValue(playerId, out var map))
                return ValueTask.FromResult<PlayerStateBundle?>(null);
            var fingerprint = _fingerprints.GetValueOrDefault(playerId, "test-fp");
            var simAt = _lastSimulatedAt.GetValueOrDefault(playerId, Epoch);
            if (map.Count == 0)
            {
                return ValueTask.FromResult<PlayerStateBundle?>(new PlayerStateBundle(
                    identity,
                    new StoredHead(playerId, 0, SectionCodec.CurrentSchemaVersion, simAt, fingerprint, "", simAt),
                    new Dictionary<string, StoredSection>(StringComparer.Ordinal),
                    []));
            }
            var version = map.Values.Max(s => s.StateVersion);
            var head = new StoredHead(playerId, version, SectionCodec.CurrentSchemaVersion,
                simAt, fingerprint, SectionCodec.ComputeHeadChecksum(map.Values), map.Values.Max(s => s.UpdatedAt));
            return ValueTask.FromResult<PlayerStateBundle?>(
                new PlayerStateBundle(identity, head, new Dictionary<string, StoredSection>(map, StringComparer.Ordinal), []));
        }

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
            => EnqueueSectionsAsync(sections, [], cancellationToken);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts,
            CancellationToken cancellationToken = default)
        {
            EnqueueCount++;
            foreach (var write in sections)
            {
                if (write.LastSimulatedAt is { } simAt) _lastSimulatedAt[write.PlayerId] = simAt;
                var map = _sections.GetOrAdd(write.PlayerId, _ => new Dictionary<string, StoredSection>(StringComparer.Ordinal));
                map[write.SectionKind] = new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                    write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt);
                _writesByPlayer.AddOrUpdate(write.PlayerId, 1, (_, c) => c + 1);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class MultiPlayerAssets : IAssetBalanceRepository
    {
        private readonly ConcurrentDictionary<Guid, Dictionary<(string AssetId, string Variant), (double Quantity, long Version)>> _data = new();

        public void Seed(Guid playerId, params (string AssetId, string Variant, double Quantity)[] rows)
        {
            var map = _data.GetOrAdd(playerId, _ => new());
            foreach (var (assetId, variant, qty) in rows) map[(assetId, variant)] = (qty, 1);
        }

        public IReadOnlyList<AssetBalanceRecord> Apply(IReadOnlyList<AssetDelta> deltas)
        {
            var changed = new List<AssetBalanceRecord>();
            foreach (var d in deltas)
            {
                var map = _data.GetOrAdd(d.PlayerId, _ => new());
                var key = (d.AssetId, d.Variant);
                var current = map.GetValueOrDefault(key);
                var next = (Quantity: current.Quantity + d.Delta, Version: current.Version + 1);
                if (next.Quantity < -1e-9) throw new InvalidOperationException($"余额不足 {d.AssetId}");
                map[key] = next;
                changed.Add(new AssetBalanceRecord(d.PlayerId, d.AssetId, d.Variant, next.Quantity, next.Version));
            }
            return changed;
        }

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            if (!_data.TryGetValue(playerId, out var map))
                return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
            return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                map.Where(p => p.Value.Quantity > 0 || p.Value.Version > 0)
                    .Select(p => new AssetBalanceRecord(playerId, p.Key.AssetId, p.Key.Variant, p.Value.Quantity, p.Value.Version))
                    .ToArray());
        }
    }

    private sealed class RecordingEconomy(MultiPlayerSections sections, MultiPlayerAssets assets) : IEconomyTransactionStore
    {
        public List<EconomyTransactionRequest> Requests { get; } = [];
        public bool FailNextCommit { get; set; }
        public Guid? FailCommitsForPlayerId { get; set; }

        public async ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("模拟检查点刷盘瞬断");
            }
            if (FailCommitsForPlayerId is { } failingId && request.ActorId == failingId)
                throw new IOException("模拟指定玩家持续刷盘失败");
            Requests.Add(request);
            var updated = assets.Apply(request.Deltas);
            if (request.Sections is { Count: > 0 } || request.Receipts is { Count: > 0 })
                await sections.EnqueueSectionsAsync(request.Sections ?? [], request.Receipts ?? [], cancellationToken).ConfigureAwait(false);
            return new EconomyTransactionResult(
                request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload, updated);
        }
    }

    private sealed class RecordingLifecycleAuditStore : IPlayerLifecycleAuditStore
    {
        public sealed record AuditRecord(string EventType, Guid PlayerId, Guid AccountId, string DisplayName, string? NodeId, DateTimeOffset OccurredAt);
        public List<AuditRecord> Events { get; } = [];

        public ValueTask RecordOnlineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
            DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        {
            Events.Add(new AuditRecord("player.online", playerId, accountId, displayName, nodeId, occurredAt));
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordOfflineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
            DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        {
            Events.Add(new AuditRecord("player.offline", playerId, accountId, displayName, nodeId, occurredAt));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDuelLogRepository : IDuelLogRepository
    {
        public List<DuelLogEntry> Entries { get; } = [];

        public ValueTask RecordAsync(DuelLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (!Entries.Any(e => e.DuelId == entry.DuelId))
                Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }
}
