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
            var writes = Domain.Persistence.SectionCodec.EncodeAggregateFull(state, DateTimeOffset.UtcNow);
            var sections = writes.ToDictionary(
                w => w.SectionKind,
                w => new StoredSection(w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec, w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion, DateTimeOffset.UtcNow, state.GameDataFingerprint, Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), DateTimeOffset.UtcNow);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "强化契约", DateTimeOffset.UtcNow), head, sections, []);
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

    private sealed class RecordingEconomy : IEconomyTransactionStore
    {
        public EconomyTransactionRequest? LastRequest { get; private set; }

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
        }
    }

    private sealed class CustomQuantityAssets(Guid playerId, params (string AssetId, string Variant, double Quantity)[] rows) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                rows.Select(row => new AssetBalanceRecord(playerId, row.AssetId, row.Variant, row.Quantity, 0)).ToArray());
    }
}
