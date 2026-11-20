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
/// 技艺/强化链路协议门禁（TASK-20261003-04）：
/// 1. action.enqueue 的 targetItemId/auxItemId 经 wire→intent→队列文档与快照行全链路透出；
/// 2. 强化行动缺目标装备时入队被拒绝（INVALID_PAYLOAD）；
/// 3. loadout.replace 的装备持有校验按资产变体键结算（+N 必须持有对应变体余额）；
/// 4. loadout 分段下发 techniqueExperiences；PostgresSchema 与 SectionCodec 版本联动。
/// </summary>
public sealed class EnhancementContractTests
{
    /// <summary>enqueue 载荷新字段写入意图并入队；快照队列行顶层字段与 intent 内嵌字段双通道透出。</summary>
    [Fact]
    public async Task ActionEnqueue_TargetAndAuxItemIds_FlowIntoIntentAndQueueRow()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/cultivation.json");
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), catalog);
        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(
            requestId.ToString("D"), "cultivation.draw-qi", 5,
            TargetItemId: "equipment.qingfeng-sword", AuxItemId: "item.soul-talisman"), ProtocolJson.Options);
        var result = await runtime.ExecuteAsync(playerId, "action.enqueue", payload, 0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");

        var queued = Assert.Single(result.Snapshot.State.Actions);
        Assert.Equal("equipment.qingfeng-sword", queued.Intent.TargetItemId);
        Assert.Equal("item.soul-talisman", queued.Intent.AuxItemId);

        var dto = ProtocolSnapshotMapper.ToSnapshot(result.Snapshot, "fp", catalog);
        var queue = Assert.Single(dto.Sections, section => section.Section == PlayerSectionKinds.ActionQueue);
        var row = Assert.Single(queue.Data.EnumerateArray());
        Assert.Equal("equipment.qingfeng-sword", row.GetProperty("targetItemId").GetString());
        Assert.Equal("item.soul-talisman", row.GetProperty("auxItemId").GetString());
        Assert.Equal("equipment.qingfeng-sword", row.GetProperty("intent").GetProperty("targetItemId").GetString());
    }

    /// <summary>
    /// 强化行动缺 targetItemId 载荷无效；携带目标装备与 targetLevel 参数可正常入队。
    /// 使用真实配表：顺带覆盖 enhance.equipment（type=enhancement）→ ActionKind.Enhancement 解析链。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_EnhancementKind_RequiresTargetItemId()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("actions/enhancement.json"));
        await runtime.LoadAsync(playerId);

        var rejectedRequestId = Guid.NewGuid();
        var rejected = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                rejectedRequestId.ToString("D"), "enhance.equipment", 1), ProtocolJson.Options),
            0, rejectedRequestId);
        Assert.False(rejected.Success);
        Assert.Equal("INVALID_PAYLOAD", rejected.Code);

        var acceptedRequestId = Guid.NewGuid();
        var accepted = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                acceptedRequestId.ToString("D"), "enhance.equipment", 1,
                Parameters: new Dictionary<string, double> { ["targetLevel"] = 3 },
                TargetItemId: "equipment.qingfeng-sword"), ProtocolJson.Options),
            0, acceptedRequestId);
        Assert.True(accepted.Success, $"{accepted.Code}: {accepted.Message}");
        var queued = Assert.Single(accepted.Snapshot.State.Actions);
        Assert.Equal("equipment.qingfeng-sword", queued.Intent.TargetItemId);
        Assert.Equal(3d, queued.Intent.Parameters?["targetLevel"]);
    }

    /// <summary>
    /// 秘法全功能行动化：化灵、分解、转化属于 enhancement 体系，缺 targetItemId 拒绝入队，携带后正常入队并保留重复次数与无限循环。
    /// </summary>
    [Theory]
    [InlineData("secret_art.liquefy", "item.morning-dew", 10)]
    [InlineData("secret_art.disassemble", "equipment.qingfeng-sword", -1)]
    [InlineData("secret_art.convert", "item.fragment.taunt", 5)]
    public async Task SecretArtActions_EnqueueWithTargetItemId_Succeeds(string actionId, string targetItemId, int repeatCount)
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("actions/enhancement.json", "items/manuals.json"));
        await runtime.LoadAsync(playerId);

        var rejectedRequestId = Guid.NewGuid();
        var rejected = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                rejectedRequestId.ToString("D"), actionId, repeatCount), ProtocolJson.Options),
            0, rejectedRequestId);
        Assert.False(rejected.Success);
        Assert.Equal("INVALID_PAYLOAD", rejected.Code);

        var acceptedRequestId = Guid.NewGuid();
        var accepted = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                acceptedRequestId.ToString("D"), actionId, repeatCount,
                TargetItemId: targetItemId), ProtocolJson.Options),
            0, acceptedRequestId);
        Assert.True(accepted.Success, $"{accepted.Code}: {accepted.Message}");
        var queued = Assert.Single(accepted.Snapshot.State.Actions);
        Assert.Equal(targetItemId, queued.Intent.TargetItemId);
        Assert.Equal(repeatCount, queued.RemainingRepeats);
    }

    /// <summary>
    /// 验证立即开始（Priority = true）：
    /// 直接将行动插入队首（Index 0），原队首行动挂起为 Pending，原有队列与指定轮次完整保留，绝不清空队列。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_Priority_InsertsAtHeadAndSuspendsPriorRunningAction()
    {
        var playerId = Guid.NewGuid();
        var assets = new CustomQuantityAssets(playerId, ("item.dark-iron", string.Empty, 100), ("item.morning-dew", string.Empty, 100));
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, new NoopEconomy(),
            LoadCatalogFiles("actions/enhancement.json", "actions/crafting.json", "items/materials.json"));
        await runtime.LoadAsync(playerId);

        // 先入队普通制造行动，3 轮
        var req1 = Guid.NewGuid();
        var res1 = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                req1.ToString("D"), "craft.refine-iron", 3), ProtocolJson.Options),
            0, req1);
        Assert.True(res1.Success, $"{res1.Code}: {res1.Message}");
        Assert.Single(res1.Snapshot.State.Actions);
        Assert.Equal("craft.refine-iron", res1.Snapshot.State.Actions[0].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Pending, res1.Snapshot.State.Actions[0].Status);

        // 再以 Priority = true 优先插队秘法化灵，5 轮
        var req2 = Guid.NewGuid();
        var res2 = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                req2.ToString("D"), "secret_art.liquefy", 5,
                TargetItemId: "item.morning-dew", Priority: true), ProtocolJson.Options),
            0, req2);
        Assert.True(res2.Success, $"{res2.Code}: {res2.Message}");

        // 验证队列不被清空，新行动在 Index 0 且为 Running，原有行动顺延至 Index 1 且挂起为 Pending
        var actions = res2.Snapshot.State.Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal("secret_art.liquefy", actions[0].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, actions[0].Status);
        Assert.Equal(5, actions[0].RemainingRepeats);

        Assert.Equal("craft.refine-iron", actions[1].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Pending, actions[1].Status);
        Assert.Equal(3, actions[1].RemainingRepeats);
    }

    [Fact]
    public async Task ActionEnqueue_RequiredRealmLevel_RejectsUnderleveledActions()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("items/materials.json", "actions/gathering.json", "actions/crafting.json"));
        await runtime.LoadAsync(playerId);

        // 1 级玩家入队 18 级挖掘行动（开采赤铜矿，产物赤铜 T2 -> 18 级）应被拒绝（ACTION_LOCKED）
        var rejectedRequestId = Guid.NewGuid();
        var rejected = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                rejectedRequestId.ToString("D"), "explore.mine-crimson-copper", 1), ProtocolJson.Options),
            0, rejectedRequestId);
        Assert.False(rejected.Success);
        Assert.Equal("ACTION_LOCKED", rejected.Code);

        // 1 级玩家入队 18 级制造行动（赤铜精，产物赤铜精 T2 -> 18 级）应被拒绝（RECIPE_LOCKED）
        var rejectedCraftRequestId = Guid.NewGuid();
        var rejectedCraft = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                rejectedCraftRequestId.ToString("D"), "craft.refine-copper", 1), ProtocolJson.Options),
            0, rejectedCraftRequestId);
        Assert.False(rejectedCraft.Success);
        Assert.Equal("RECIPE_LOCKED", rejectedCraft.Code);

        // 1 级玩家入队 1 级制造行动（百炼铁，产物百炼铁 T1 -> 1 级）应允许入队
        var acceptedRequestId = Guid.NewGuid();
        var accepted = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                acceptedRequestId.ToString("D"), "craft.refine-iron", 1), ProtocolJson.Options),
            0, acceptedRequestId);
        Assert.True(accepted.Success, $"{accepted.Code}: {accepted.Message}");
    }

    /// <summary>
    /// 讨伐行动的 level 仅作推荐强度参考，不设境界解锁门禁：
    /// 1 级玩家身处高阶地图节点时，可以直接入队 18/24 级讨伐行动，不被 ACTION_LOCKED 拒绝。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_CombatCrusade_AllowsUnderleveledPlayer()
    {
        var playerId = Guid.NewGuid();
        var repo = new FakeRepository(playerId, p => p.TravelTo("node.astral-rift"));
        await using var runtime = EcsRuntimeFactory.Create(
            repo, new EmptyAssets(), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());
        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                requestId.ToString("D"), "combat.sweep-twilight-valley", -1), ProtocolJson.Options),
            0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        var queued = Assert.Single(result.Snapshot.State.Actions);
        Assert.Equal("combat.sweep-twilight-valley", queued.Intent.ActionId.Value);
    }

    /// <summary>
    /// 验证境界或技艺双重「或」解锁：
    /// 当玩家角色境界为 1 级，但对应技艺（如采矿 mining、锻造 forging）等级达到 18 级时，
    /// 对应 T2 行动入队直接放行。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_DualUnlock_PermitsWhenTechniqueLevelSatisfied()
    {
        var playerId = Guid.NewGuid();
        var repo = new FakeRepository(playerId, p =>
        {
            p.World.NodeId = "node.fluorite-abyss";
            p.Loadout.TechniqueExperiences["forging"] = 5_000_000;
            p.Loadout.TechniqueExperiences["mining"] = 5_000_000;
        });

        await using var runtime = EcsRuntimeFactory.Create(
            repo, new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("expected-attributes.json", "items/materials.json", "actions/gathering.json", "actions/crafting.json"));
        await runtime.LoadAsync(playerId);

        // 境界仅 1 级，但采矿技艺达标，开采赤铜矿（explore.mine-crimson-copper，18 级）允许入队
        var mineRequestId = Guid.NewGuid();
        var mineResult = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                mineRequestId.ToString("D"), "explore.mine-crimson-copper", 1), ProtocolJson.Options),
            0, mineRequestId);
        Assert.True(mineResult.Success, $"{mineResult.Code}: {mineResult.Message}");

        // 境界仅 1 级，但锻造技艺达标，赤铜精制造（craft.refine-copper，18 级）允许入队
        var craftRequestId = Guid.NewGuid();
        var craftResult = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                craftRequestId.ToString("D"), "craft.refine-copper", 1), ProtocolJson.Options),
            0, craftRequestId);
        Assert.True(craftResult.Success, $"{craftResult.Code}: {craftResult.Message}");
    }
    /// <summary>
    /// 验证逆命丹（特殊底蕴配方）的硬性境界门槛：
    /// 玩家角色境界未达 18 级时，即使炼丹技艺达到 18 级以上，入队依然被拒绝（RECIPE_LOCKED）且提示不含技艺放行；
    /// 只有角色境界达到 18 级时才允许入队。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_ReverseFatePill_RequiresRealmLevel18AndRefusesTechniqueBypass()
    {
        var playerId = Guid.NewGuid();
        var repo = new FakeRepository(playerId, p =>
        {
            p.Progression.RealmLevel = 1;
            p.Loadout.TechniqueExperiences["alchemy"] = 5_000_000;
        });

        await using var runtime = EcsRuntimeFactory.Create(
            repo, new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("expected-attributes.json", "items/pills.json", "actions/alchemy.json"));
        await runtime.LoadAsync(playerId);

        // 境界 1 级但炼丹技艺达标：因含有 FoundationCostRatio，严禁技艺跨境界放行，必须拒绝
        var rejectedRequestId = Guid.NewGuid();
        var rejectedResult = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                rejectedRequestId.ToString("D"), "alchemy.refine-reverse-fate-pill", 1), ProtocolJson.Options),
            0, rejectedRequestId);
        Assert.False(rejectedResult.Success);
        Assert.Equal("RECIPE_LOCKED", rejectedResult.Code);
        Assert.Contains("需境界达到 18 级", rejectedResult.Message);
        Assert.DoesNotContain("或炼丹达到", rejectedResult.Message);

        // 角色境界达到 18 级：允许入队
        var player18Id = Guid.NewGuid();
        var repo18 = new FakeRepository(player18Id, p =>
        {
            p.Progression.RealmLevel = 18;
        });
        await using var runtime18 = EcsRuntimeFactory.Create(
            repo18, new EmptyAssets(), new NoopEconomy(),
            LoadCatalogFiles("expected-attributes.json", "items/pills.json", "actions/alchemy.json"));
        await runtime18.LoadAsync(player18Id);

        var acceptedRequestId = Guid.NewGuid();
        var acceptedResult = await runtime18.ExecuteAsync(player18Id, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                acceptedRequestId.ToString("D"), "alchemy.refine-reverse-fate-pill", 1), ProtocolJson.Options),
            0, acceptedRequestId);
        Assert.True(acceptedResult.Success, $"{acceptedResult.Code}: {acceptedResult.Message}");
    }

    /// <summary>
    /// 7 件稀有材料特殊装备制造门槛回归：
    /// 配方解锁、目录下发等级与装备穿戴等级完全对齐（12/12/16/20/22/24/24），
    /// 未达对应等级拒绝入队（RECIPE_LOCKED），达到对应等级无需等阶锚点（18/30）即可入队。
    /// </summary>
    [Fact]
    public async Task ActionEnqueue_SpecialEquipment_UnlocksAtEquipmentRealmLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        async Task<PlayerCommandResult> TryEnqueueAtLevelAsync(int realmLevel, string actionId)
        {
            var playerId = Guid.NewGuid();
            var repo = new FakeRepository(playerId, p =>
            {
                p.Progression.RealmLevel = realmLevel;
            });
            await using var runtime = EcsRuntimeFactory.Create(
                repo, new EmptyAssets(), new NoopEconomy(), catalog);
            await runtime.LoadAsync(playerId);

            var reqId = Guid.NewGuid();
            return await runtime.ExecuteAsync(playerId, "action.enqueue",
                JsonSerializer.SerializeToElement(new ActionIntentRequest(
                    reqId.ToString("D"), actionId, 1), ProtocolJson.Options),
                0, reqId);
        }

        var locked11 = await TryEnqueueAtLevelAsync(11, "forge.baihu-dagger");
        Assert.False(locked11.Success);
        Assert.Equal("RECIPE_LOCKED", locked11.Code);

        var baihu12 = await TryEnqueueAtLevelAsync(12, "forge.baihu-dagger");
        Assert.True(baihu12.Success, $"{baihu12.Code}: {baihu12.Message}");

        var rongling12 = await TryEnqueueAtLevelAsync(12, "forge.rongling-pendant");
        Assert.True(rongling12.Success, $"{rongling12.Code}: {rongling12.Message}");

        var lockedHandi12 = await TryEnqueueAtLevelAsync(12, "forge.handi-armor");
        Assert.False(lockedHandi12.Success);
        Assert.Equal("RECIPE_LOCKED", lockedHandi12.Code);

        var handi16 = await TryEnqueueAtLevelAsync(16, "forge.handi-armor");
        Assert.True(handi16.Success, $"{handi16.Code}: {handi16.Message}");

        var duanchen20 = await TryEnqueueAtLevelAsync(20, "forge.duanchen-blade");
        Assert.True(duanchen20.Success, $"{duanchen20.Code}: {duanchen20.Message}");

        var lockedCrown20 = await TryEnqueueAtLevelAsync(20, "forge.mingsha-crown");
        Assert.False(lockedCrown20.Success);
        Assert.Equal("RECIPE_LOCKED", lockedCrown20.Code);

        var crown22 = await TryEnqueueAtLevelAsync(22, "forge.mingsha-crown");
        Assert.True(crown22.Success, $"{crown22.Code}: {crown22.Message}");

        var greaves24 = await TryEnqueueAtLevelAsync(24, "forge.fentian-greaves");
        Assert.True(greaves24.Success, $"{greaves24.Code}: {greaves24.Message}");

        var staff24 = await TryEnqueueAtLevelAsync(24, "forge.demon-core-staff");
        Assert.True(staff24.Success, $"{staff24.Code}: {staff24.Message}");
    }
    /// <summary>
    /// 装备持有校验按资产变体键结算：钱包只持 +2 变体时，装配基础形态与 +3 均拒绝，
    /// 装配 +2 成功并写入槽位等级；小数等级同样拒绝。
    /// </summary>
    [Fact]
    public async Task LoadoutReplace_EnhancedEquipment_RequiresMatchingVariantBalance()
    {
        var playerId = Guid.NewGuid();
        var assets = new VariantAssets(playerId, ("equipment.qingfeng-sword", "+2"));
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, new NoopEconomy(),
            LoadCatalogFiles("equipment/weapons.json"));
        await runtime.LoadAsync(playerId);

        static JsonElement EquipPayload(double level) => JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.qingfeng-sword", enhancementLevel = level } },
            abilitySlots = Array.Empty<object>()
        }, ProtocolJson.Options);

        var baseForm = await runtime.ExecuteAsync(playerId, "loadout.replace", EquipPayload(0), 0, Guid.NewGuid());
        Assert.False(baseForm.Success);
        Assert.Equal("INVALIDARGUMENT", baseForm.Code);

        var mismatched = await runtime.ExecuteAsync(playerId, "loadout.replace", EquipPayload(3), 0, Guid.NewGuid());
        Assert.False(mismatched.Success);
        Assert.Equal("INVALIDARGUMENT", mismatched.Code);

        var fractional = await runtime.ExecuteAsync(playerId, "loadout.replace", EquipPayload(1.5), 0, Guid.NewGuid());
        Assert.False(fractional.Success);

        var enhanced = await runtime.ExecuteAsync(playerId, "loadout.replace", EquipPayload(2), 0, Guid.NewGuid());
        Assert.True(enhanced.Success, $"{enhanced.Code}: {enhanced.Message}");
        Assert.Contains(enhanced.Snapshot.State.Loadout.Equipment,
            slot => slot.ItemId == "equipment.qingfeng-sword" && slot.EnhancementLevel == 2);
    }

    /// <summary>装配分段快照必须携带 techniqueExperiences，供内视技艺 Tab 读取累计经验。</summary>
    [Fact]
    public void Snapshot_LoadoutSection_CarriesTechniqueExperiences()
    {
        var aggregate = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "技艺", DateTimeOffset.UnixEpoch, "fp");
        aggregate.Loadout.TechniqueExperiences["alchemy"] = 120;
        aggregate.Loadout.TechniqueExperiences["enhancement"] = 30;
        var runtime = new PlayerRuntimeSnapshot(aggregate.PlayerId.Value, aggregate.AccountId.Value.ToString("D"),
            aggregate.DisplayName, 1, aggregate.LastSimulatedAt, aggregate.ExportState(), 0, []);

        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", GameDataCatalog.BuiltIn);
        var loadout = Assert.Single(dto.Sections, section => section.Section == PlayerSectionKinds.Loadout);
        var experiences = loadout.Data.GetProperty("techniqueExperiences");
        Assert.Equal(120, experiences.GetProperty("alchemy").GetDouble());
        Assert.Equal(30, experiences.GetProperty("enhancement").GetDouble());
    }

    /// <summary>
    /// enhancement.ack_records 命令契约与 progression 分段快照透出（TASK-20261030-01）：
    /// 1. 快照 progression 分段包含 stagedEnhancements 数组；
    /// 2. enhancement.ack_records 命令仅清除标记为 isCompleted 的记录，未完成记录继续保留。
    /// </summary>
    [Fact]
    public async Task EnhancementAckCommand_RemovesOnlyCompletedRecordsAndProjectsToDto()
    {
        var playerId = Guid.NewGuid();
        var catalog = GameDataCatalog.BuiltIn;
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), catalog);
        await runtime.LoadAsync(playerId);

        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();

        // 人工塞入两条暂存记录：A 已完成，B 进行中
        await runtime.MutateOwnedAsync(playerId, p =>
        {
            p.Progression.RecordEnhancementAttempt(actionA, "equipment.test-sword", 1, true, 1000);
            p.Progression.MarkEnhancementCompleted(actionA, 1005);
            p.Progression.RecordEnhancementAttempt(actionB, "equipment.test-robe", 1, false, 1002);
            p.MarkDirty(PlayerSectionKind.Progression);
        });

        // 1. 验证快照投出 stagedEnhancements
        var snapshot = await runtime.LoadAsync(playerId);
        var dto = ProtocolSnapshotMapper.ToSnapshot(snapshot, "fp", catalog);
        var progressionSection = Assert.Single(dto.Sections, s => s.Section == PlayerSectionKinds.Progression);
        var stagedProp = progressionSection.Data.GetProperty("stagedEnhancements");
        Assert.Equal(JsonValueKind.Array, stagedProp.ValueKind);
        Assert.Equal(2, stagedProp.GetArrayLength());

        // 2. 发送 enhancement.ack_records 确认 [actionA, actionB]
        var ackRequestId = Guid.NewGuid();
        var ackPayload = JsonSerializer.SerializeToElement(new
        {
            actionIds = new[] { actionA.ToString("D"), actionB.ToString("D") }
        });
        var ackResult = await runtime.ExecuteAsync(playerId, "enhancement.ack_records", ackPayload, 0, ackRequestId);
        Assert.True(ackResult.Success, $"{ackResult.Code}: {ackResult.Message}");

        // 3. 验证只有 actionA 被清除，actionB（进行中）严格保留
        var afterDto = ProtocolSnapshotMapper.ToSnapshot(ackResult.Snapshot, "fp", catalog);
        var afterProgression = Assert.Single(afterDto.Sections, s => s.Section == PlayerSectionKinds.Progression);
        var afterStaged = afterProgression.Data.GetProperty("stagedEnhancements");
        Assert.Equal(1, afterStaged.GetArrayLength());
        var remainingRecord = afterStaged[0];
        Assert.Equal(actionB.ToString("D"), remainingRecord.GetProperty("actionId").GetString());
        Assert.False(remainingRecord.GetProperty("isCompleted").GetBoolean());
    }

    /// <summary>强化装备 item.sell 正确按 +N 受保折旧单价结算，精确扣除变体行并贷记对应灵石。</summary>
    [Fact]
    public async Task ItemSell_EnhancedEquipment_CalculatesDepreciatedPriceAndDeductsVariantRow()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("items/materials.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var economy = new RecordingEconomy();
        // 玩家持有 1 件 +5 青锋剑
        var assets = new CustomQuantityAssets(playerId, ("equipment.qingfeng-sword", "+5", 1));
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.qingfeng-sword",
            variant = "+5",
            quantity = 1
        });

        var result = await runtime.ExecuteAsync(playerId, "item.sell", payload, 0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");

        Assert.NotNull(economy.LastRequest);
        // 扣减 +5 变体行
        var itemDelta = Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "equipment.qingfeng-sword");
        Assert.Equal("+5", itemDelta.Variant);
        Assert.Equal(-1.0, itemDelta.Delta);

        // 贷记灵石：青锋剑 V0 = 432 (成本 7*30 + 1*6 = 216)，+5 单价 = round(432 * 4.222 + 178) = 2002
        var spiritDelta = Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "currency.spirit-stone");
        Assert.Equal(string.Empty, spiritDelta.Variant);
        Assert.Equal(2002.0, spiritDelta.Delta);
    }

    /// <summary>旧 0 秒批量命令 item.liquefy / item.disassemble / item.convert 已下线，执行一律返回 UNKNOWN_COMMAND 且不触发经济扣减。</summary>
    [Theory]
    [InlineData("item.liquefy")]
    [InlineData("item.disassemble")]
    [InlineData("item.convert")]
    public async Task LegacySecretArtCommands_AreRemoved_AndReturnUnknownCommand(string legacyCommand)
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/enhancement.json", "items/materials.json", "items/manuals.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var economy = new RecordingEconomy();
        var assets = new CustomQuantityAssets(playerId,
            ("item.beast-hide", string.Empty, 2),
            ("equipment.qingfeng-sword", string.Empty, 1),
            ("item.fragment.heavy-slash", string.Empty, 1));
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        var result = await runtime.ExecuteAsync(playerId, legacyCommand, JsonSerializer.SerializeToElement(new
        {
            itemId = "item.beast-hide", quantity = 1
        }), 0, Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal("UNKNOWN_COMMAND", result.Code);
        Assert.Empty(economy.Requests);
    }

    /// <summary>秘法化灵通过 action.enqueue(secret_art.liquefy) 按系统回收价三倍发放灵石，且支持 initialLevel 指定 +N 装备。</summary>
    [Fact]
    public async Task SecretArtLiquefy_ViaActionEnqueue_UsesTripleSystemRecyclePrice()
    {
        var playerId = Guid.NewGuid();
        // 必须带上 expected-attributes.json：realmProgression 缺失时技艺等级恒为 0，
        // 化灵成功率退化为 95% 而非本用例假设的 100%，判定种子又取随机 requestId，会造成 ~5%/轮 flaky。
        var catalog = LoadCatalogFiles("actions/enhancement.json", "items/materials.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json", "expected-attributes.json");
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var assets = new MutableAssets(playerId,
            ("item.beast-hide", string.Empty, 2),
            ("equipment.qingfeng-sword", "+5", 1));
        var economy = new RecordingEconomy(assets);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, economy, catalog,
            simulationClock: clock, startAt: DateTimeOffset.UnixEpoch);
        await runtime.LoadAsync(playerId);

        // 秘法技艺等级高于物品等级（兽皮/青锋剑配表 level=1）：化灵成功率恒 100%，保证本用例走成功分支。
        await runtime.MutateOwnedAsync(playerId, aggregate =>
            aggregate.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement] = 1_000_000);

        var req1 = Guid.NewGuid();
        var enq1 = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                req1.ToString("D"), "secret_art.liquefy", 2, TargetItemId: "item.beast-hide"), ProtocolJson.Options),
            0, req1);
        Assert.True(enq1.Success, $"{enq1.Code}: {enq1.Message}");

        clock.Advance(TimeSpan.FromSeconds(10));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Empty(economy.Requests);
        await runtime.FlushAsync(playerId, true);
        Assert.Equal(-2, Assert.Single(economy.LastRequest!.Deltas, d => d.AssetId == "item.beast-hide").Delta);
        Assert.Equal(36, Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "currency.spirit-stone").Delta);

        var req2 = Guid.NewGuid();
        var enq2 = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                req2.ToString("D"), "secret_art.liquefy", 1,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 5 },
                TargetItemId: "equipment.qingfeng-sword"), ProtocolJson.Options),
            0, req2);
        Assert.True(enq2.Success, $"{enq2.Code}: {enq2.Message}");

        clock.Advance(TimeSpan.FromSeconds(5));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Single(economy.Requests);
        await runtime.FlushAsync(playerId, true);
        Assert.Equal("+5", Assert.Single(economy.LastRequest!.Deltas, d => d.AssetId == "equipment.qingfeng-sword").Variant);
        Assert.Equal(-1, Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "equipment.qingfeng-sword").Delta);
        Assert.Equal(6006, Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "currency.spirit-stone").Delta);
    }

    /// <summary>秘法分解通过 action.enqueue(secret_art.disassemble) 只返还向下取整的锻造基础材料，并受同级穿戴与强化锁定保护。</summary>
    [Fact]
    public async Task SecretArtDisassemble_ViaActionEnqueue_ReturnsFlooredMaterials_AndRespectsLocks()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/enhancement.json", "items/materials.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var assets = new MutableAssets(playerId, ("equipment.qingfeng-sword", string.Empty, 3));
        var economy = new RecordingEconomy(assets);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, economy, catalog,
            simulationClock: clock, startAt: DateTimeOffset.UnixEpoch);
        await runtime.LoadAsync(playerId);

        // 穿戴 1 件，排队锁定 1 件强化（同为白板 0 级），剩余 1 件可被分解。
        // 开启循环队列（LoopEnabled）：第二轮分解受阻后轮转保留在队列中（Blocked 状态）而非被直接出队。
        await runtime.MutateOwnedAsync(playerId, aggregate =>
        {
            aggregate.SetLoopEnabled(true);
            aggregate.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.qingfeng-sword", 0));
        });

        var enhanceReq = Guid.NewGuid();
        var enhanceEnq = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                enhanceReq.ToString("D"), "enhance.equipment", 1,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 0 },
                TargetItemId: "equipment.qingfeng-sword"), ProtocolJson.Options),
            0, enhanceReq);
        Assert.True(enhanceEnq.Success, $"{enhanceEnq.Code}: {enhanceEnq.Message}");

        // 尝试插队分解 2 件：第一轮成功消耗剩余 1 件，第二轮因穿戴 + 强化锁定占用而阻塞。
        var disReq = Guid.NewGuid();
        var disEnq = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                disReq.ToString("D"), "secret_art.disassemble", 2,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 0 },
                TargetItemId: "equipment.qingfeng-sword", Priority: true), ProtocolJson.Options),
            0, disReq);
        Assert.True(disEnq.Success, $"{disEnq.Code}: {disEnq.Message}");

        clock.Advance(TimeSpan.FromSeconds(12));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Empty(economy.Requests);
        await runtime.FlushAsync(playerId, true);

        Assert.Single(economy.Requests);
        Assert.Equal(-1, Assert.Single(economy.LastRequest!.Deltas, d => d.AssetId == "equipment.qingfeng-sword").Delta);
        Assert.Equal(4, Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "item.refined-iron").Delta);
        Assert.DoesNotContain(economy.LastRequest.Deltas, d => d.AssetId == "item.beast-bone");
        var snapshot = runtime.TryGetLoaded(playerId)!;
        var blockedDisassemble = Assert.Single(snapshot.State.Actions, a => a.Intent.ActionId.Value == "secret_art.disassemble");
        Assert.Equal(ActionQueueStatus.Blocked, blockedDisassemble.Status);
        Assert.Equal(1, blockedDisassemble.RemainingRepeats);
    }

    /// <summary>残卷转化通过 action.enqueue(secret_art.convert) 执行多次独立判定，失败兜底取残卷 sellPrice*2=20 灵石。</summary>
    [Fact]
    public async Task SecretArtConvert_ViaActionEnqueue_MultipleRepeats_UsesDynamicFailureReward()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/enhancement.json", "items/materials.json", "items/manuals.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var assets = new MutableAssets(playerId, ("item.fragment.heavy-slash", string.Empty, 10));
        var economy = new RecordingEconomy(assets);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, economy, catalog,
            simulationClock: clock, startAt: DateTimeOffset.UnixEpoch);
        await runtime.LoadAsync(playerId);

        var reqId = Guid.NewGuid();
        var enq = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                reqId.ToString("D"), "secret_art.convert", 10, TargetItemId: "item.fragment.heavy-slash"), ProtocolJson.Options),
            0, reqId);
        Assert.True(enq.Success, $"{enq.Code}: {enq.Message}");

        clock.Advance(TimeSpan.FromSeconds(80));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Empty(economy.Requests);
        await runtime.FlushAsync(playerId, true);

        Assert.Equal(-10, Assert.Single(economy.LastRequest!.Deltas, d => d.AssetId == "item.fragment.heavy-slash").Delta);
        var tomeReward = economy.LastRequest.Deltas.SingleOrDefault(d => d.AssetId == "item.tome.heavy-slash");
        var stoneReward = economy.LastRequest.Deltas.SingleOrDefault(d => d.AssetId == "currency.spirit-stone");
        var tomes = (int)(tomeReward?.Delta ?? 0);
        var stones = (int)(stoneReward?.Delta ?? 0);
        Assert.Equal(0, stones % 20);
        Assert.Equal(10, tomes + (stones / 20));
    }

    /// <summary>单次残卷转化只消耗一张残卷，发放 1 本整本秘籍或 sellPrice*2=20 灵石。</summary>
    [Fact]
    public async Task SecretArtConvert_ViaActionEnqueue_SingleRepeat_ProducesOneConfiguredOutcome()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/enhancement.json", "items/materials.json", "items/manuals.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var assets = new MutableAssets(playerId, ("item.fragment.heavy-slash", string.Empty, 1));
        var economy = new RecordingEconomy(assets);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, economy, catalog,
            simulationClock: clock, startAt: DateTimeOffset.UnixEpoch);
        await runtime.LoadAsync(playerId);

        var reqId = Guid.NewGuid();
        var enq = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                reqId.ToString("D"), "secret_art.convert", 1, TargetItemId: "item.fragment.heavy-slash"), ProtocolJson.Options),
            0, reqId);
        Assert.True(enq.Success, $"{enq.Code}: {enq.Message}");

        clock.Advance(TimeSpan.FromSeconds(8));
        await runtime.AdvanceAllAsync(clock.UtcNow);
        Assert.Empty(economy.Requests);
        await runtime.FlushAsync(playerId, true);

        Assert.Equal(-1, Assert.Single(economy.LastRequest!.Deltas, d => d.AssetId == "item.fragment.heavy-slash").Delta);
        var tomeReward = economy.LastRequest.Deltas.SingleOrDefault(d => d.AssetId == "item.tome.heavy-slash");
        var stoneReward = economy.LastRequest.Deltas.SingleOrDefault(d => d.AssetId == "currency.spirit-stone");
        Assert.True(tomeReward is not null ^ stoneReward is not null);
        Assert.Equal(tomeReward is null ? 20 : 1, tomeReward?.Delta ?? stoneReward!.Delta);
    }

    /// <summary>item.sell 与 shop.buy 遭遇经济提交结局未知时必须立起写入围栏，核实幂等事实前阻断后续命令，核实后丢弃旧状态恢复。</summary>
    [Theory]
    [InlineData("item.sell")]
    [InlineData("shop.buy")]
    public async Task DisposalAndShopBuy_WhenCommitOutcomeUnknown_FenceSubsequentWritesUntilResolved(string command)
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("items/materials.json", "shop/offers.json");
        var assets = new MutableAssets(playerId,
            ("item.beast-hide", string.Empty, 3),
            ("item.gongde", string.Empty, 2000));
        var economy = new RecordingEconomy(assets);
        economy.AfterCommitFault = req => new EconomyCommitOutcomeUnknownException(req.TransactionId, new IOException("injected-commit-drop"));
        economy.HideCommittedResults = true;
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        var payload = command == "item.sell"
            ? JsonSerializer.SerializeToElement(new { itemId = "item.beast-hide", quantity = 1 })
            : JsonSerializer.SerializeToElement(new { offerId = "shop.awakening-stone.open", quantity = 1 });

        await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(() =>
            runtime.ExecuteAsync(playerId, command, payload, 0, Guid.NewGuid()).AsTask());

        // 故障尚未恢复时，后续命令尝试核实围栏仍未通过，必须继续 fail-closed 拦截写入。
        await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(() =>
            runtime.ExecuteAsync(playerId, "player.rename", JsonSerializer.SerializeToElement(new { displayName = "Blocked" }), 0, Guid.NewGuid()).AsTask());

        // 故障恢复后，下一次命令核实原事务已提交并解除围栏、回附单据提交、恢复后正常执行。
        economy.HideCommittedResults = false;
        var recovered = await runtime.ExecuteAsync(playerId, "player.rename",
            JsonSerializer.SerializeToElement(new { displayName = "Recovered" }), 0, Guid.NewGuid());
        Assert.True(recovered.Success, $"{recovered.Code}: {recovered.Message}");
        var originalCommit = Assert.Single(economy.Requests, req => req.TransactionType == command);
        Assert.Single(originalCommit.Receipts!);
    }

    /// <summary>装备 item.sell 穿戴拦截收窄至同级占用：无多余备件拒绝，有多余备件放行。</summary>
    [Fact]
    public async Task ItemSell_EquippedEquipment_BlocksWhenNoSpare()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("items/materials.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var economy = new RecordingEconomy();
        // 玩家持有 1 件白板青锋剑
        var assets = new CustomQuantityAssets(playerId, ("equipment.qingfeng-sword", string.Empty, 1));
        var repo = new FakeRepository(playerId);
        await using var runtime = EcsRuntimeFactory.Create(repo, assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        // 玩家穿戴了这件白板青锋剑
        await runtime.MutateOwnedAsync(playerId, aggregate =>
        {
            aggregate.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.qingfeng-sword", 0));
        });

        // 尝试卖出：仅持有 1 件且已穿戴 → 拦截
        var payload = JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.qingfeng-sword",
            variant = string.Empty,
            quantity = 1
        });
        var result = await runtime.ExecuteAsync(playerId, "item.sell", payload, 0, Guid.NewGuid());
        Assert.False(result.Success);
        Assert.Equal("ITEM_EQUIPPED", result.Code);
    }

    /// <summary>强化装备 item.discard 正确扣减对应变体行且不入账灵石。</summary>
    [Fact]
    public async Task ItemDiscard_EnhancedEquipment_DeductsVariantRow()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("items/materials.json", "actions/crafting.json", "actions/forging.json", "equipment/weapons.json");
        var economy = new RecordingEconomy();
        var assets = new CustomQuantityAssets(playerId, ("equipment.qingfeng-sword", "+3", 2));
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        var payload = JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.qingfeng-sword",
            variant = "+3",
            quantity = 1
        });

        var result = await runtime.ExecuteAsync(playerId, "item.discard", payload, 0, Guid.NewGuid());
        Assert.True(result.Success, $"{result.Code}: {result.Message}");

        Assert.NotNull(economy.LastRequest);
        var itemDelta = Assert.Single(economy.LastRequest.Deltas, d => d.AssetId == "equipment.qingfeng-sword");
        Assert.Equal("+3", itemDelta.Variant);
        Assert.Equal(-1.0, itemDelta.Delta);
        Assert.DoesNotContain(economy.LastRequest.Deltas, d => d.AssetId == "currency.spirit-stone");
    }

    /// <summary>已穿戴采集工具与强化锁定中的装备，在 item.sell 与 item.discard 均被准确拦截且不可越过。</summary>
    [Fact]
    public async Task ItemSellAndDiscard_RejectsEquippedToolAndEnhanceLockedEquipment()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles(
            "items/materials.json", "actions/crafting.json", "actions/forging.json",
            "actions/enhancement.json", "equipment/weapons.json", "equipment/tools.json");
        var economy = new RecordingEconomy();
        var assets = new CustomQuantityAssets(playerId,
            ("equipment.tool-herb-hoe", string.Empty, 1),
            ("equipment.qingfeng-sword", "+2", 1));
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, economy, catalog);
        await runtime.LoadAsync(playerId);

        // 1. 穿戴采集工具（tool-gathering）后卖出与丢弃均返回 ITEM_EQUIPPED
        await runtime.MutateOwnedAsync(playerId, aggregate =>
        {
            aggregate.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.tool-herb-hoe", 0));
        });
        var toolPayload = JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.tool-herb-hoe",
            variant = string.Empty,
            quantity = 1
        });
        var sellTool = await runtime.ExecuteAsync(playerId, "item.sell", toolPayload, 0, Guid.NewGuid());
        Assert.False(sellTool.Success);
        Assert.Equal("ITEM_EQUIPPED", sellTool.Code);

        var discardTool = await runtime.ExecuteAsync(playerId, "item.discard", toolPayload, 0, Guid.NewGuid());
        Assert.False(discardTool.Success);
        Assert.Equal("ITEM_EQUIPPED", discardTool.Code);

        // 2. 入队 enhance.equipment 锁定 +2 青锋剑后，卖出与丢弃均返回 ITEM_ENHANCE_LOCKED
        var enqueueId = Guid.NewGuid();
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                enqueueId.ToString("D"), "enhance.equipment", 1,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 2 },
                TargetItemId: "equipment.qingfeng-sword"), ProtocolJson.Options),
            0, enqueueId);
        Assert.True(enqueued.Success, $"{enqueued.Code}: {enqueued.Message}");

        var swordPayload = JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.qingfeng-sword",
            variant = "+2",
            quantity = 1
        });
        var sellLocked = await runtime.ExecuteAsync(playerId, "item.sell", swordPayload, 0, Guid.NewGuid());
        Assert.False(sellLocked.Success);
        Assert.Equal("ITEM_ENHANCE_LOCKED", sellLocked.Code);

        var discardLocked = await runtime.ExecuteAsync(playerId, "item.discard", swordPayload, 0, Guid.NewGuid());
        Assert.False(discardLocked.Success);
        Assert.Equal("ITEM_ENHANCE_LOCKED", discardLocked.Code);
        Assert.Null(economy.LastRequest);
    }

    /// <summary>
    /// loadout.replace 明确区分「未持有该装备」与「正处于强化锁定中，无法穿戴」，
    /// 不再将两类失败混为同一误导提示。
    /// </summary>
    [Fact]
    public async Task LoadoutReplace_DistinguishesUnheldFromEnhanceLocked()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("actions/enhancement.json", "equipment/weapons.json");
        var assets = new VariantAssets(playerId, ("equipment.qingfeng-sword", "+2"));
        await using var runtime = EcsRuntimeFactory.Create(new FakeRepository(playerId), assets, new NoopEconomy(), catalog);
        await runtime.LoadAsync(playerId);

        // 1. 穿戴未持有的装备 -> 未持有该装备。
        var unheldPayload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.qingfeng-sword", enhancementLevel = 3d } },
            abilitySlots = Array.Empty<object>()
        }, ProtocolJson.Options);
        var unheld = await runtime.ExecuteAsync(playerId, "loadout.replace", unheldPayload, 0, Guid.NewGuid());
        Assert.False(unheld.Success);
        Assert.Contains("未持有", unheld.Message);
        Assert.DoesNotContain("强化锁定", unheld.Message);

        // 2. 持有的 +2 装备已入队强化锁定 -> 该装备正处于强化锁定中，无法穿戴。
        var enqueueId = Guid.NewGuid();
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                enqueueId.ToString("D"), "enhance.equipment", 1,
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = 2 },
                TargetItemId: "equipment.qingfeng-sword"), ProtocolJson.Options),
            0, enqueueId);
        Assert.True(enqueued.Success, $"{enqueued.Code}: {enqueued.Message}");

        var lockedPayload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.qingfeng-sword", enhancementLevel = 2d } },
            abilitySlots = Array.Empty<object>()
        }, ProtocolJson.Options);
        var locked = await runtime.ExecuteAsync(playerId, "loadout.replace", lockedPayload, 0, Guid.NewGuid());
        Assert.False(locked.Success);
        Assert.Contains("强化锁定", locked.Message);
    }

    /// <summary>
    /// 按配置相对路径过滤 Domain 程序集内嵌的游戏数据并走正式解析链构建目录：
    /// 既使用真实配置覆盖 config→Parse 链路，又与同批在途的其他配置文件解耦。
    /// </summary>
    private static GameDataCatalog LoadCatalogFiles(params string[] relativePaths)
    {
        var assembly = typeof(GameDataCatalog).Assembly;
        var documents = assembly.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '/'))
            .Zip(assembly.GetManifestResourceNames())
            .Where(pair => relativePaths.Any(path =>
                pair.First.EndsWith(path, StringComparison.OrdinalIgnoreCase)
                || pair.First.EndsWith(path.Replace('/', '.'), StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Second)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                var bytes = new byte[stream.Length];
                var read = stream.Read(bytes, 0, bytes.Length);
                return (ReadOnlyMemory<byte>)bytes.AsMemory(0, read);
            }).ToArray();
        Assert.True(documents.Length == relativePaths.Length,
            $"未找到全部内嵌配置：{string.Join(", ", relativePaths)}");
        return GameDataCatalog.Parse(documents);
    }

    private sealed class FakeRepository : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle;

        public FakeRepository(Guid playerId, Action<PlayerAggregate>? configure = null)
        {
            var aggregate = PlayerAggregate.Create(new PlayerId(playerId), AccountId.New(), "强化契约", DateTimeOffset.UnixEpoch, "catalog-a");
            configure?.Invoke(aggregate);
            var state = aggregate.ExportState();
            var writes = Domain.Persistence.SectionCodec.EncodeAggregateFull(state, state.LastSimulatedAt);
            var sections = writes.ToDictionary(
                w => w.SectionKind,
                w => new StoredSection(w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec, w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion, state.LastSimulatedAt, state.GameDataFingerprint, Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), state.LastSimulatedAt);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "强化契约", state.CreatedAt), head, sections, []);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    /// <summary>持有指定资产变体行的钱包假实现；每条 (assetId, variant) 一行余额 1。</summary>
    private sealed class VariantAssets(Guid playerId, params (string AssetId, string Variant)[] rows) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                rows.Select(row => new AssetBalanceRecord(playerId, row.AssetId, row.Variant, 1, 0)).ToArray());
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }

    private sealed class RecordingEconomy(MutableAssets? backingAssets = null) : IEconomyTransactionStore
    {
        public EconomyTransactionRequest? LastRequest { get; private set; }
        public List<EconomyTransactionRequest> Requests { get; } = [];
        public Func<EconomyTransactionRequest, Exception?>? FaultInjector { get; set; }
        public Func<EconomyTransactionRequest, Exception>? AfterCommitFault { get; set; }
        public bool HideCommittedResults { get; set; }

        public ValueTask<EconomyTransactionResult?> FindCommittedAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<EconomyTransactionResult?>(!HideCommittedResults && Requests.Contains(request)
                ? new(request.TransactionId, request.IdempotencyKey, true, true, DateTimeOffset.UtcNow, request.ResultPayload)
                : null);

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            if (FaultInjector?.Invoke(request) is { } fault)
                throw fault;
            LastRequest = request;
            Requests.Add(request);
            var balances = backingAssets?.Apply(request.Deltas);
            if (AfterCommitFault?.Invoke(request) is { } faultAfterCommit) throw faultAfterCommit;
            return ValueTask.FromResult(new EconomyTransactionResult(
                request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload, balances));
        }
    }

    private sealed class CustomQuantityAssets(Guid playerId, params (string AssetId, string Variant, double Quantity)[] rows) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                rows.Select(row => new AssetBalanceRecord(playerId, row.AssetId, row.Variant, row.Quantity, 0)).ToArray());
    }

    private sealed class MutableAssets : IAssetBalanceRepository
    {
        private readonly Guid _playerId;
        private readonly Dictionary<(string AssetId, string Variant), (double Quantity, long Version)> _balances = new();

        public MutableAssets(Guid playerId, params (string AssetId, string Variant, double Quantity)[] rows)
        {
            _playerId = playerId;
            foreach (var (assetId, variant, quantity) in rows)
                _balances[(assetId, variant)] = (quantity, 0);
        }

        public IReadOnlyList<AssetBalanceRecord> Apply(IReadOnlyList<AssetDelta> deltas)
        {
            var updated = new List<AssetBalanceRecord>();
            foreach (var delta in deltas)
            {
                if (delta.PlayerId != _playerId) continue;
                var key = (delta.AssetId, delta.Variant);
                var current = _balances.GetValueOrDefault(key);
                var next = (Quantity: current.Quantity + delta.Delta, Version: current.Version + 1);
                _balances[key] = next;
                updated.Add(new AssetBalanceRecord(_playerId, delta.AssetId, delta.Variant, next.Quantity, next.Version));
            }
            return updated;
        }

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                _balances.Where(pair => pair.Value.Quantity > 0 || pair.Value.Version > 0)
                    .Select(pair => new AssetBalanceRecord(_playerId, pair.Key.AssetId, pair.Key.Variant, pair.Value.Quantity, pair.Value.Version))
                    .ToArray());
    }

    private sealed class ManualClock(DateTimeOffset start) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
        public bool IsDeterministic => true;
        public void Advance(TimeSpan delta) => UtcNow += delta;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
    }
}
