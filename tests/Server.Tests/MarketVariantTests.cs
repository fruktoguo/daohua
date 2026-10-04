using System.Text.Json;
using Domain.GameData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 坊市装备强化变体盘口契约门禁（TASK-20261004-09）：
/// 1. market.place 的 variant 仅接受 "+N"（1 ≤ N ≤ EnhancementRules.MaxLevel）且仅限装备资产；
/// 2. 已装备出售拦截收窄为「同 itemId + 同强化等级」——钱包按变体行冻结，不同级变体互不挤占。
/// 市场运行时不启动（未就绪）：通过命令层校验的请求由 PlaceAsync 的 EnsureReady 抛出
/// InvalidOperationException 透出，用例以此证明校验已放行，无需真实数据库。
/// </summary>
public sealed class MarketVariantTests
{
    /// <summary>非 "+N" 变体、超上限等级与非装备 "+N" 一律 INVALID_VARIANT；装备 "+N" 与 "+0" 归一后放行。</summary>
    [Fact]
    public async Task MarketPlace_RejectsNonCanonicalOrUnsupportedVariant()
    {
        var playerId = Guid.NewGuid();
        var catalog = LoadCatalogFiles("equipment/weapons.json");
        await using var runtime = Create(catalog, playerId);
        await runtime.LoadAsync(playerId);

        foreach (var variant in new[] { "abc", "+21", "+", "-1" })
        {
            var rejected = await runtime.ExecuteAsync(playerId, "market.place",
                PlacePayload("equipment.qingfeng-sword", "buy", variant), 0, Guid.NewGuid());
            Assert.False(rejected.Success);
            Assert.Equal("INVALID_VARIANT", rejected.Code);
        }

        // 非装备资产不允许 "+N" 盘口（强化变体是装备专属）。
        var nonEquipment = await runtime.ExecuteAsync(playerId, "market.place",
            PlacePayload("item.soul-talisman", "buy", "+3"), 0, Guid.NewGuid());
        Assert.False(nonEquipment.Success);
        Assert.Equal("INVALID_VARIANT", nonEquipment.Code);

        // 装备 "+N"（含 "+03" 异形写法归一 "+3"）与 "+0"（归一基础簿）通过命令层校验，
        // 由未就绪市场运行时的 EnsureReady 拒绝——证明已越过 INVALID_VARIANT 防线。
        foreach (var variant in new[] { "+3", "+03", "+0" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runtime.ExecuteAsync(playerId, "market.place",
                    PlacePayload("equipment.qingfeng-sword", "buy", variant), 0, Guid.NewGuid()));
        }
    }

    /// <summary>+2 已装备时同级变体出售被 ITEM_EQUIPPED 拦截；基础簿与相邻等级簿不受挤占。</summary>
    [Fact]
    public async Task MarketPlace_EquippedCheckMatchesSameEnhancementLevel()
    {
        var playerId = Guid.NewGuid();
        var assets = new VariantAssets(playerId, ("equipment.qingfeng-sword", "+2"));
        var catalog = LoadCatalogFiles("equipment/weapons.json");
        await using var runtime = Create(catalog, playerId, assets);
        await runtime.LoadAsync(playerId);

        var equip = await runtime.ExecuteAsync(playerId, "loadout.replace",
            JsonSerializer.SerializeToElement(new
            {
                equipment = new[] { new { itemId = "equipment.qingfeng-sword", enhancementLevel = 2d } },
                abilitySlots = Array.Empty<object>()
            }, ProtocolJson.Options), 0, Guid.NewGuid());
        Assert.True(equip.Success, $"{equip.Code}: {equip.Message}");

        var sameLevel = await runtime.ExecuteAsync(playerId, "market.place",
            PlacePayload("equipment.qingfeng-sword", "sell", "+2"), 0, Guid.NewGuid());
        Assert.False(sameLevel.Success);
        Assert.Equal("ITEM_EQUIPPED", sameLevel.Code);

        // 已装备 +2 不挤占其他变体行：基础簿（""/"+0"）与 +3 均越过装备拦截进入市场层。
        foreach (var variant in new[] { null, "", "+0", "+3" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runtime.ExecuteAsync(playerId, "market.place",
                    PlacePayload("equipment.qingfeng-sword", "sell", variant), 0, Guid.NewGuid()));
        }
    }

    /// <summary>装配带未启动市场运行时的适配器：未就绪状态由 PlaceAsync 的 EnsureReady 暴露，不触库。</summary>
    private static EcsPlayerRuntimeAdapter Create(GameDataCatalog catalog, Guid playerId, IAssetBalanceRepository? assets = null)
    {
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var market = new MarketRuntime(
            new PostgresMarketRepository(NpgsqlDataSource.Create("Host=unused;Database=unused"), options, NullLogger<PostgresMarketRepository>.Instance),
            options, NullLogger<MarketRuntime>.Instance);
        return EcsRuntimeFactory.Create(new FakeRepository(playerId), assets ?? new EmptyAssets(), new NoopEconomy(), catalog,
            marketRuntime: market);
    }

    private static JsonElement PlacePayload(string itemId, string side, string? variant) =>
        JsonSerializer.SerializeToElement(new
        {
            orderId = Guid.NewGuid().ToString("D"),
            itemId,
            side,
            variant,
            price = 10d,
            quantity = 1d
        }, ProtocolJson.Options);

    /// <summary>按配置相对路径过滤 Domain 程序集内嵌的游戏数据并走正式解析链构建目录。</summary>
    private static GameDataCatalog LoadCatalogFiles(params string[] relativePaths)
    {
        var assembly = typeof(GameDataCatalog).Assembly;
        var documents = assembly.GetManifestResourceNames()
            .Where(name => relativePaths.Any(path =>
                name.EndsWith(path, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(path.Replace('/', '.'), StringComparison.OrdinalIgnoreCase)))
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
            new PlayerIdentity(playerId, Guid.NewGuid(), "坊市变体", DateTimeOffset.UtcNow),
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
