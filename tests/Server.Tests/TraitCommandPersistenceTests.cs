using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 特质装配（trait.set）与觉醒石（item.awakening-stone）服务端持久化门禁：
/// 验证两次操作均推进 StateVersion、标脏 Progression 分段并在刷盘/经济提交时持久化，
/// 杜绝切完火神/燃魂后重连或重启回滚丢失。
/// </summary>
public sealed class TraitCommandPersistenceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    [Fact]
    public async Task TraitSet_MarksProgressionDirtyAdvancesVersionAndPersistsOnFlush()
    {
        var playerId = Guid.NewGuid();
        var repo = new RecordingRepository(playerId, aggregate =>
        {
            aggregate.Progression.RealmLevel = 30;
            aggregate.Progression.UnlockedTraits[CombatTraitRules.HuoshenTraitId] = new(CombatTraitRules.HuoshenTraitId, 1, 0);
            aggregate.Progression.UnlockedTraits[CombatTraitRules.RanhunTraitId] = new(CombatTraitRules.RanhunTraitId, 1, 0);
        });

        await using var runtime = EcsRuntimeFactory.Create(
            repo,
            new EmptyAssets(),
            new NoopEconomy(),
            Catalog,
            TestSimulationClock.Fixed(Epoch),
            Epoch);

        var before = await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            traitIds = new[] { CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId }
        });
        var result = await runtime.ExecuteAsync(playerId, "trait.set", payload, 0, requestId);

        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.True(result.Snapshot.StateVersion > before.StateVersion);
        Assert.True(result.Snapshot.WireAttributesChanged);
        Assert.Contains(PlayerSectionKind.Progression, result.Snapshot.TouchedSections!);
        Assert.Equal(
            [CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId],
            result.Snapshot.State.Progression.TraitIds);

        // 刷盘必须把 Progression 分段落库，重放解码可见新装配的火神与燃魂
        await runtime.FlushAllAsync(force: true);
        var progWrite = Assert.Single(repo.WrittenSections, w => w.SectionKind == PlayerSectionKinds.Progression);
        var decoded = JsonSerializer.Deserialize<PlayerProgressionState>(
            SectionCodec.DecodeRaw(progWrite.Codec, progWrite.Payload),
            PersistenceJson.Options)!;
        Assert.Equal([CombatTraitRules.HuoshenTraitId, CombatTraitRules.RanhunTraitId], decoded.TraitIds);
    }

    [Fact]
    public async Task AwakeningStone_MarksProgressionDirtyAndPersistsUnlockedTraitsWithAssetCommit()
    {
        var playerId = Guid.NewGuid();
        var repo = new RecordingRepository(playerId, aggregate =>
        {
            aggregate.Progression.RealmLevel = 30;
        });

        var economy = new RecordingEconomy();
        await using var runtime = EcsRuntimeFactory.Create(
            repo,
            new FixedAssets(playerId, "item.awakening-stone", 2),
            economy,
            Catalog,
            TestSimulationClock.Fixed(Epoch),
            Epoch);

        var before = await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            itemId = "item.awakening-stone",
            quantity = 1
        });
        var result = await runtime.ExecuteAsync(playerId, "item.use", payload, 0, requestId);

        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.True(result.Snapshot.StateVersion > before.StateVersion);
        Assert.Contains(PlayerSectionKind.Progression, result.Snapshot.TouchedSections!);
        Assert.NotEmpty(result.Snapshot.State.Progression.UnlockedTraits!);
        Assert.Contains(economy.CommittedSections, w => w.SectionKind == PlayerSectionKinds.Progression);
    }

    [Fact]
    public async Task Fufatianming_AllowsTalismanInSpellSlotEnforcesMutualExclusionAndClearsOnTraitUnequip()
    {
        var playerId = Guid.NewGuid();
        var repo = new RecordingRepository(playerId, aggregate =>
        {
            aggregate.Progression.RealmLevel = 30;
            aggregate.Progression.UnlockedTraits[CombatTraitRules.FufatianmingTraitId] = new(CombatTraitRules.FufatianmingTraitId, 1, 0);
            aggregate.Settings.AutoUseSlots.Add(new("combat", 0, "item.jian-talisman", 1, true, 0, 0, AutoUseSlotState.SlotGroupGeneral));
        });

        await using var runtime = EcsRuntimeFactory.Create(
            repo,
            new FixedAssets(playerId, "item.jian-talisman", 10),
            new NoopEconomy(),
            Catalog,
            TestSimulationClock.Fixed(Epoch),
            Epoch);

        await runtime.LoadAsync(playerId);

        // 1. 未装配「符法·天命」时，术法槽拒绝装配符箓
        var loadoutPayload = JsonSerializer.SerializeToElement(new
        {
            equipment = Array.Empty<object>(),
            abilitySlots = new object[]
            {
                new { abilityId = (string?)null, level = 1 },
                new { abilityId = "item.jian-talisman", level = 1 }
            }
        });
        var rejected = await runtime.ExecuteAsync(playerId, "loadout.replace", loadoutPayload, 0, Guid.NewGuid());
        Assert.False(rejected.Success);

        // 2. 装配「符法·天命」后，术法槽可装配剑符，并自动将消耗品栏同物品槽清空互斥
        var equipTrait = await runtime.ExecuteAsync(playerId, "trait.set",
            JsonSerializer.SerializeToElement(new { traitIds = new[] { CombatTraitRules.FufatianmingTraitId } }),
            0, Guid.NewGuid());
        Assert.True(equipTrait.Success, $"{equipTrait.Code}: {equipTrait.Message}");

        var equippedLoadout = await runtime.ExecuteAsync(playerId, "loadout.replace", loadoutPayload, 0, Guid.NewGuid());
        Assert.True(equippedLoadout.Success, $"{equippedLoadout.Code}: {equippedLoadout.Message}");
        Assert.Equal("item.jian-talisman", equippedLoadout.Snapshot.State.Loadout.AbilitySlots[1].AbilityId);
        Assert.DoesNotContain(equippedLoadout.Snapshot.State.Settings.AutoUseSlots ?? [], s => s.ItemId == "item.jian-talisman");

        // 3. 卸下「符法·天命」时，主动术法槽内的符箓自动卸除并标脏 Loadout 分段
        var unequipTrait = await runtime.ExecuteAsync(playerId, "trait.set",
            JsonSerializer.SerializeToElement(new { traitIds = Array.Empty<string>() }),
            0, Guid.NewGuid());
        Assert.True(unequipTrait.Success, $"{unequipTrait.Code}: {unequipTrait.Message}");
        Assert.Null(unequipTrait.Snapshot.State.Loadout.AbilitySlots[1].AbilityId);
        Assert.Contains(PlayerSectionKind.Loadout, unequipTrait.Snapshot.TouchedSections!);
    }

    private sealed class RecordingRepository : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle;
        public List<SectionWrite> WrittenSections { get; } = [];

        public RecordingRepository(Guid playerId, Action<PlayerAggregate> configure)
        {
            var aggregate = PlayerAggregate.Create(new PlayerId(playerId), AccountId.New(), "特质测试", Epoch, Catalog.Fingerprint);
            configure(aggregate);
            var state = aggregate.ExportState();
            var writes = SectionCodec.EncodeAggregateFull(state, Epoch);
            var sections = writes.ToDictionary(
                write => write.SectionKind,
                write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                    write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, SectionCodec.CurrentSchemaVersion,
                Epoch, state.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), Epoch);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "特质测试", Epoch), head, sections, []);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            CancellationToken cancellationToken = default)
        {
            WrittenSections.AddRange(sections);
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default)
        {
            WrittenSections.AddRange(sections);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class FixedAssets(Guid playerId, string itemId, double quantity) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                id == playerId ? [new AssetBalanceRecord(playerId, itemId, string.Empty, quantity, 1)] : []);
    }

    private sealed class RecordingEconomy : IEconomyTransactionStore
    {
        public List<SectionWrite> CommittedSections { get; } = [];

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Sections is { Count: > 0 } sections)
                CommittedSections.AddRange(sections);
            return ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, false, Epoch, request.ResultPayload));
        }
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, false, Epoch, request.ResultPayload));
    }
}
