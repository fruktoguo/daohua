using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// compute-link.v1.2 派发边界单测：链路帧编解码、动态节点注册表、批次预算与 section 线编码校验。
/// 不依赖真实 ComputeNode 进程或数据库。
/// </summary>
public sealed class ComputeDispatchTests
{
    /// <summary>全部链路消息类型必须经 type 判别字段无损往返。</summary>
    [Fact]
    public void CodecRoundTripsAllMessageTypes()
    {
        var batchId = Guid.NewGuid();
        var job = NewJobRequest();
        object[] messages =
        [
            new ComputeLinkHello(ComputeLinkContract.Version, "odin", 4, "fp123", 96, 4L * 1024 * 1024,
                "Arm64/.NET 10.0.1"),
            new ComputeLinkWelcome(ComputeLinkContract.Version, Guid.NewGuid(), 1_700_000_000_000),
            new ComputeLinkBatch(batchId, new ComputeAdvanceBatchRequest(
                ComputeDispatchContract.Version, "fp123", [job])),
            new ComputeLinkResult(batchId, new ComputeAdvanceBatchResponse(
                ComputeDispatchContract.Version, "fp123", 1_700_000_000_000,
                [new ComputeAdvanceJobResult(job.JobId, job.PlayerId, true, 7, 8, [], null, null, null, null, false)])),
            new ComputeLinkError("NODE_LIMIT", "上限")
        ];

        foreach (var message in messages)
        {
            var decoded = ComputeLinkCodec.Decode(ComputeLinkCodec.Encode(message));
            Assert.IsType(message.GetType(), decoded);
        }

        var decodedHello = (ComputeLinkHello)ComputeLinkCodec.Decode(
            ComputeLinkCodec.Encode(messages[0]));
        Assert.Equal(96, decodedHello.MaxJobsPerBatch);
        Assert.Equal(4L * 1024 * 1024, decodedHello.MaxBatchBytes);
        Assert.Equal("Arm64/.NET 10.0.1", decodedHello.RuntimeFingerprint);

        var decodedBatch = (ComputeLinkBatch)ComputeLinkCodec.Decode(
            ComputeLinkCodec.Encode(messages[2]));
        Assert.Equal(batchId, decodedBatch.BatchId);
        Assert.Equal(ComputeDispatchContract.Version, decodedBatch.Request.ContractVersion);
        Assert.Single(decodedBatch.Request.Jobs);
        Assert.Equal(job.JobId, decodedBatch.Request.Jobs[0].JobId);
    }

    /// <summary>缺 type 或未知 type 的帧一律判非法，不得静默吞掉。</summary>
    [Fact]
    public void CodecRejectsMalformedAndUnknownFrames()
    {
        Assert.Throws<InvalidDataException>(() =>
            ComputeLinkCodec.Decode(Encoding.UTF8.GetBytes("""{"foo":1}""")));
        Assert.Throws<InvalidDataException>(() =>
            ComputeLinkCodec.Decode(Encoding.UTF8.GetBytes("""{"type":"bogus"}""")));
    }

    /// <summary>派发器总是拿到剩余容量最大的在线节点并原子占槽；节点组为空或全忙时返回 null。</summary>
    [Fact]
    public void RegistryAcquiresLeastBusyNode()
    {
        var registry = new ComputeLinkRegistry();
        Assert.Null(registry.Acquire());

        var idle = new ComputeLinkNode("idle", 4);
        var busy = new ComputeLinkNode("busy", 4);
        Assert.True(busy.TryReserveSlot());
        Assert.True(busy.TryReserveSlot()); // busy 已占 2/4，idle 全空 → idle 最闲。
        Assert.True(registry.TryRegister(idle));
        Assert.True(registry.TryRegister(busy));

        Assert.Same(idle, registry.Acquire()); // Acquire 即占槽：idle 在飞 1/4。
        Assert.Equal(1, idle.InFlight);
        Assert.Equal(3, idle.Available);

        // idle 塞满后轮到 busy（仍剩 2 格容量）。
        Assert.True(idle.TryReserveSlot());
        Assert.True(idle.TryReserveSlot());
        Assert.True(idle.TryReserveSlot());
        Assert.Equal(0, idle.Available);
        Assert.False(idle.TryReserveSlot());
        Assert.Same(busy, registry.Acquire()); // busy 在飞 3/4。

        // 全满 → null，任务留 pending 不消耗 attempts。
        Assert.True(busy.TryReserveSlot());
        Assert.Null(registry.Acquire());

        // 槽位归还后立即可再取。
        idle.ReleaseSlot();
        Assert.Equal(1, idle.Available);
        Assert.Same(idle, registry.Acquire());
    }

    /// <summary>容量信号：槽位归还与新节点注册都要唤醒等待中的派发循环，且信号为一次性。</summary>
    [Fact]
    public async Task RegistryCapacitySignalWakesOnReleaseAndRegister()
    {
        var registry = new ComputeLinkRegistry();
        var node = new ComputeLinkNode("n1", 1);
        Assert.True(registry.TryRegister(node));
        Assert.True(node.TryReserveSlot());

        var first = registry.CapacitySignal;
        Assert.False(first.IsCompleted);
        node.ReleaseSlot();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        // 已完成的信号被消费后必须换新：下一次等待不被旧信号立刻满足。
        var second = registry.CapacitySignal;
        Assert.False(second.IsCompleted);
        Assert.True(registry.TryRegister(new ComputeLinkNode("n2", 1)));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>装批预算：按解码后字节贪心截断，装不下的进 Overflow；单元素超预算不阻塞后续小任务。</summary>
    [Fact]
    public void BatchBudgetPacksByDecodedBytes()
    {
        // 200 base64 字符 ≈ 150 解码字节。
        var payload = new string('a', 200);
        var job = new ComputeAdvanceJobRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "name",
            0, 1, 0, 1, new string('a', 64), "fp123",
            [new ComputeSectionPayload(PlayerSectionKinds.Settings, 1, "identity", payload, new string('a', 64))]);
        Assert.Equal(150, ComputeBatchBudget.EstimateDecodedBytes(job));

        var (fits, overflow) = ComputeBatchBudget.Pack([300L, 100L, 50L, 80L], size => size, budget: 200);
        Assert.Equal([100L, 50L], fits);
        Assert.Equal([300L, 80L], overflow);

        // 恰好装满与空批次边界。
        var exact = ComputeBatchBudget.Pack([100L, 100L], size => size, budget: 200);
        Assert.Equal(2, exact.Fits.Count);
        Assert.Empty(exact.Overflow);
    }

    /// <summary>结果帧必须了结对应批次等待方；未知 batchId 拒绝。</summary>
    [Fact]
    public async Task RegistryCompletesPendingBatchResult()
    {
        var registry = new ComputeLinkRegistry();
        var node = new ComputeLinkNode("n1", 2);
        Assert.True(registry.TryRegister(node));
        var batchId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<ComputeAdvanceBatchResponse>();
        node.Pending[batchId] = waiter;

        var response = new ComputeAdvanceBatchResponse(
            ComputeDispatchContract.Version, "fp", 1, []);
        Assert.True(node.CompleteResult(batchId, response));
        Assert.Same(response, await waiter.Task);
        Assert.False(node.CompleteResult(batchId, response));
    }

    /// <summary>节点注销时在飞批次必须立即收到断线失败，派发器才能退回租约。</summary>
    [Fact]
    public async Task RegistryUnregisterFailsInFlightBatches()
    {
        var registry = new ComputeLinkRegistry();
        var node = new ComputeLinkNode("odin", 2);
        Assert.True(registry.TryRegister(node));
        var waiter = new TaskCompletionSource<ComputeAdvanceBatchResponse>();
        node.Pending[Guid.NewGuid()] = waiter;

        registry.Unregister(node);
        Assert.Equal(0, registry.NodeCount);
        await Assert.ThrowsAsync<ComputeNodeDisconnectedException>(() => waiter.Task);
        Assert.Null(registry.Acquire());
    }

    /// <summary>存储 codec 到线 codec 的映射必须一致，未知存储编码拒绝外传。</summary>
    [Fact]
    public void ToWireMapsStorageCodecs()
    {
        var raw = Encoding.UTF8.GetBytes("{}");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        var stored = new StoredSection(Guid.NewGuid(), PlayerSectionKinds.Settings, 3,
            SectionCodec.CurrentSchemaVersion, SectionCodec.RawJson, raw, raw.Length, checksum, DateTimeOffset.UtcNow);
        var wire = ComputeSectionCodec.ToWire(stored);
        Assert.Equal("identity", wire.Codec);
        Assert.Equal(checksum, wire.Checksum);

        var unknown = stored with { Codec = "lz4" };
        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToWire(unknown));
    }

    /// <summary>
    /// output_payload 摘要口径：完结任务只存 jobId/玩家/版本对/结局/指标/错误，
    /// 不再整存含全量 base64 section 的 result JSON（存储放大 ~2.3 倍）。
    /// </summary>
    [Fact]
    public void OutputSummaryRoundTripsWithoutSectionPayloads()
    {
        var summary = new ComputeJobOutputSummary(
            Guid.NewGuid(), Guid.NewGuid(), 7, 8, ComputeApplyOutcome.Applied.ToString(), 3,
            new ComputeAdvanceMetrics(42, 3, 8, 1024, 512), null, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, ServerJson.Options);
        var decoded = JsonSerializer.Deserialize<ComputeJobOutputSummary>(bytes, ServerJson.Options);
        Assert.Equal(summary, decoded);
        // 摘要体量应远小于整份 result JSON，且不得携带 section 载荷字段。
        Assert.True(bytes.Length < 1024);
        Assert.DoesNotContain("\"sections\"", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>节点回传 section 必须通过编码字节校验和才能还原为 SectionWrite。</summary>
    [Fact]
    public void ToSectionWriteValidatesEncodedChecksum()
    {
        var player = Guid.NewGuid();
        var raw = Encoding.UTF8.GetBytes("{\"presence\":0}");
        var encoded = Convert.ToBase64String(raw);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));

        var write = ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload(PlayerSectionKinds.Settings, 8, "identity", encoded, checksum),
            DateTimeOffset.UtcNow);
        Assert.Equal(SectionCodec.RawJson, write.Codec);
        Assert.Equal(raw.Length, write.RawBytes);
        Assert.Equal(9, write.StateVersion);

        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload(PlayerSectionKinds.Settings, 8, "identity", encoded,
                new string('0', 64)), DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => ComputeSectionCodec.ToSectionWrite(player, 9,
            new ComputeSectionPayload("unknown.kind", 8, "identity", encoded, checksum),
            DateTimeOffset.UtcNow));
    }

    private static ComputeAdvanceJobRequest NewJobRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "name",
            0, 7, 1_000, 2_000, new string('a', 64), "fp123",
            [new ComputeSectionPayload(PlayerSectionKinds.Settings, 1, "identity",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")), new string('a', 64))]);
}
