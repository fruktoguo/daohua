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

    /// <summary>存档 schema 与库内容版本按契约同步推进，单边漂移即失败。</summary>
    [Fact]
    public void PostgresSchemaVersion_AlignedWithSectionCodec()
        => Assert.Equal(SectionCodec.CurrentSchemaVersion, PostgresSchema.Version);

    /// <summary>空库权威 DDL 的版本种子必须包含当前版本，防止升版本时漏改 schema_version 种子导致新库 bootstrap 校验失败。</summary>
    [Fact]
    public void PostgresSchemaSql_SeedsCurrentVersion()
        => Assert.Contains($"({PostgresSchema.Version}, now())", PostgresSchema.Sql);

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

    private sealed class FakeRepository(Guid playerId) : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle = new(
            new PlayerIdentity(playerId, Guid.NewGuid(), "强化契约", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []);

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
}
