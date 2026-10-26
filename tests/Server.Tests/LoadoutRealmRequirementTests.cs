using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 装备境界等级要求与穿戴限制回归门禁：
/// 1. 境界不足的新装备穿戴被严格拦截；
/// 2. 境界达标的新装备允许穿戴；
/// 3. 已穿戴的装备不受境界门槛影响（不强制脱下，豁免保留）。
/// </summary>
public sealed class LoadoutRealmRequirementTests
{
    [Fact]
    public async Task LoadoutReplace_UnderleveledNewEquipment_Rejected()
    {
        var playerId = Guid.NewGuid();
        // 玩家初始为 1 级
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId, p => p.Progression.RealmLevel = 1),
            new EquipmentAssets(playerId),
            new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());

        var requestId = Guid.NewGuid();
        // 尝试新穿戴 9 级玄铁长剑
        var payload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.xuantie-sword", enhancementLevel = 0d } },
            abilitySlots = Enumerable.Range(0, 4)
                .Select(_ => new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<JsonElement>() })
                .ToArray(),
            requestId = requestId.ToString("D"),
        }, ProtocolJson.Options);

        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", payload, 0, requestId);
        Assert.False(result.Success, "1级玩家穿戴9级装备应当被拒绝");
        Assert.Contains("境界不足", result.Message);
    }

    [Fact]
    public async Task LoadoutReplace_LevelMetNewEquipment_Accepted()
    {
        var playerId = Guid.NewGuid();
        // 玩家升至 9 级
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId, p => p.Progression.RealmLevel = 9),
            new EquipmentAssets(playerId),
            new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.xuantie-sword", enhancementLevel = 0d } },
            abilitySlots = Enumerable.Range(0, 4)
                .Select(_ => new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<JsonElement>() })
                .ToArray(),
            requestId = requestId.ToString("D"),
        }, ProtocolJson.Options);

        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", payload, 0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Contains(result.Snapshot.State.Loadout.Equipment, slot => slot.ItemId == "equipment.xuantie-sword");
    }

    [Fact]
    public async Task LoadoutReplace_AlreadyEquippedUnderleveled_NotForcefullyUnequipped()
    {
        var playerId = Guid.NewGuid();
        // 玩家仅 1 级，但历史已穿戴 9 级玄铁长剑（旧装豁免）
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId, p =>
            {
                p.Progression.RealmLevel = 1;
                p.Loadout.Equipment.Add(new PlayerEquipmentSlot("equipment.xuantie-sword", 0));
            }),
            new EquipmentAssets(playerId),
            new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());

        var requestId = Guid.NewGuid();
        // 发起 loadout.replace，继续保留已穿戴的玄铁长剑
        var payload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.xuantie-sword", enhancementLevel = 0d } },
            abilitySlots = Enumerable.Range(0, 4)
                .Select(_ => new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<JsonElement>() })
                .ToArray(),
            requestId = requestId.ToString("D"),
        }, ProtocolJson.Options);

        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", payload, 0, requestId);
        Assert.True(result.Success, $"已穿戴旧装备即使境界不足也应当允许保留，实际被拒: {result.Code} {result.Message}");
        Assert.Contains(result.Snapshot.State.Loadout.Equipment, slot => slot.ItemId == "equipment.xuantie-sword");
    }

    private sealed class FakeRepository : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle;

        public FakeRepository(Guid playerId, Action<PlayerAggregate>? configure = null)
        {
            var aggregate = PlayerAggregate.Create(new PlayerId(playerId), AccountId.New(), "境界测试", DateTimeOffset.UnixEpoch, "catalog-a");
            configure?.Invoke(aggregate);
            var state = aggregate.ExportState();
            var writes = Domain.Persistence.SectionCodec.EncodeAggregateFull(state, DateTimeOffset.UtcNow);
            var sections = writes.ToDictionary(
                w => w.SectionKind,
                w => new StoredSection(w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec, w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion, DateTimeOffset.UtcNow, state.GameDataFingerprint, Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), DateTimeOffset.UtcNow);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "境界测试", DateTimeOffset.UtcNow), head, sections, []);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EquipmentAssets(Guid playerId) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
            [
                new AssetBalanceRecord(playerId, "equipment.qingfeng-sword", string.Empty, 1, 0),
                new AssetBalanceRecord(playerId, "equipment.xuantie-sword", string.Empty, 1, 0),
                new AssetBalanceRecord(playerId, "equipment.pogang-sword", string.Empty, 1, 0),
            ]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
