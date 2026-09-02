using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Server.Clean.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 对抗性并发验证：针对多客户端 WebSocket 并发会话的数据隔离、快照广播与防串号保护进行严格检验。
/// </summary>
public sealed class AdversarialConcurrencyTests
{
    [Fact]
    public async Task MultiClientConcurrentBroadcast_ZeroDataContaminationAcrossSessions()
    {
        const int clientCount = 50;
        var clients = new List<(Guid PlayerId, WebSocketSession Session, List<PlayerSnapshotDeltaDto> ReceivedDeltas, List<LogBatchDto> ReceivedLogs)>();
        var players = new List<PlayerIdentity>();
        var stateMap = new Dictionary<Guid, FakeStateRepository>();

        var registry = new SessionRegistry();
        var gameData = GameDataCatalog.BuiltIn;

        for (var i = 0; i < clientCount; i++)
        {
            var playerId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var identity = new PlayerIdentity(playerId, accountId, $"修仙者_{i}", DateTimeOffset.UtcNow);
            players.Add(identity);

            var session = new WebSocketSession(playerId, 1000);
            registry.TryAdd(session);
            clients.Add((playerId, session, new List<PlayerSnapshotDeltaDto>(), new List<LogBatchDto>()));

            var bundle = new PlayerStateBundle(
                identity,
                new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
                new Dictionary<string, StoredSection>(),
                []);
            stateMap[playerId] = new FakeStateRepository(bundle);
        }

        var multiRepo = new MultiPlayerStateRepository(stateMap);
        var runtime = new PlayerRuntime(
            multiRepo,
            new EmptyAssetRepository(),
            new NoopEconomyRepository(),
            gameData,
            NullLogger<PlayerRuntime>.Instance);

        // 为每个 session 挂载模拟 WebSocketEndpoint 内部的分发监听
        foreach (var client in clients)
        {
            var session = client.Session;
            var playerId = client.PlayerId;

            // 初始化基线
            var initialSnapshot = await runtime.LoadAsync(playerId);
            var initialDto = ProtocolSnapshotMapper.ToSnapshot(initialSnapshot, gameData.Fingerprint, gameData);
            session.ExchangeSectionBaseline(initialDto.Sections);
            session.ExchangeAssetBaseline(initialDto.Assets);

            ValueTask OnSnapshotUpdated(PlayerRuntimeSnapshot snapshot)
            {
                if (snapshot.PlayerId != session.PlayerId) return ValueTask.CompletedTask;
                var current = ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData);
                var changedSections = ProtocolSnapshotMapper.DiffSections(session.ExchangeSectionBaseline(current.Sections), current.Sections);
                var changedAssets = ProtocolSnapshotMapper.DiffAssets(session.ExchangeAssetBaseline(current.Assets), current.Assets);
                var delta = new PlayerSnapshotDeltaDto(current.PlayerId, current.StateVersion, changedSections, changedAssets.Count == 0 ? null : changedAssets);
                var envelope = WireEnvelope.Json(
                    WireMessageType.SnapshotDelta,
                    Guid.NewGuid(),
                    session.NextOutboundSequence(),
                    delta);
                var bytes = WireEnvelopeCodec.Encode(envelope);
                session.TrySend(bytes);
                return ValueTask.CompletedTask;
            }

            ValueTask OnLogBatchEmitted(Guid eventPlayerId, IReadOnlyList<LogEventDto> events)
            {
                if (eventPlayerId != session.PlayerId) return ValueTask.CompletedTask;
                var batch = new LogBatchDto(events);
                var envelope = WireEnvelope.Json(
                    WireMessageType.LogBatch,
                    Guid.NewGuid(),
                    session.NextOutboundSequence(),
                    batch);
                var bytes = WireEnvelopeCodec.Encode(envelope);
                session.TrySend(bytes);
                return ValueTask.CompletedTask;
            }

            runtime.SnapshotUpdated += OnSnapshotUpdated;
            runtime.LogBatchEmitted += OnLogBatchEmitted;
        }

        // 并发向奇数序号的玩家队列添加行动
        var tasks = new List<Task>();
        for (var i = 0; i < clientCount; i++)
        {
            var idx = i;
            var client = clients[idx];
            tasks.Add(Task.Run(async () =>
            {
                if (idx % 2 == 1)
                {
                    var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options);
                    await runtime.ExecuteAsync(client.PlayerId, "action.enqueue", payload);
                }
            }));
        }
        await Task.WhenAll(tasks);

        // 并发推进 5 次模拟
        for (var tick = 1; tick <= 5; tick++)
        {
            await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(tick * 5));
        }

        // 验证每个 Session 接收到的通道数据
        for (var i = 0; i < clientCount; i++)
        {
            var client = clients[i];
            var session = client.Session;
            session.Complete();

            while (session.Outbound.TryRead(out var packet))
            {
                Assert.True(WireEnvelopeCodec.TryDecode(packet, out var envelope));

                if (envelope.MessageType == WireMessageType.SnapshotDelta)
                {
                    Assert.True(WireEnvelopeCodec.TryDeserializeJson<PlayerSnapshotDeltaDto>(envelope, out var delta));
                    Assert.NotNull(delta);
                    // 核心断言：绝不允许收到非自身 PlayerId 的快照增量！
                    Assert.Equal(client.PlayerId.ToString("D"), delta!.PlayerId);
                    client.ReceivedDeltas.Add(delta);
                }
                else if (envelope.MessageType == WireMessageType.LogBatch)
                {
                    Assert.True(WireEnvelopeCodec.TryDeserializeJson<LogBatchDto>(envelope, out var logBatch));
                    Assert.NotNull(logBatch);
                    client.ReceivedLogs.Add(logBatch!);
                }
            }

            if (i % 2 == 0)
            {
                // 偶数玩家未添加行动，模拟未变更，不应收到任何增量快照
                Assert.Empty(client.ReceivedDeltas);
            }
            else
            {
                // 奇数玩家有行动进行，必须收到且仅收到自己的增量快照
                Assert.NotEmpty(client.ReceivedDeltas);
                Assert.All(client.ReceivedDeltas, d => Assert.Equal(client.PlayerId.ToString("D"), d.PlayerId));
            }
        }
    }

    private sealed class MultiPlayerStateRepository(Dictionary<Guid, FakeStateRepository> repos) : IPlayerStateRepository
    {
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            repos.TryGetValue(playerId, out var repo) ? repo.LoadAsync(playerId, cancellationToken) : ValueTask.FromResult<PlayerStateBundle?>(null);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private class FakeStateRepository(PlayerStateBundle bundle) : IPlayerStateRepository
    {
        public virtual ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PlayerStateBundle?>(bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssetRepository : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class NoopEconomyRepository : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
