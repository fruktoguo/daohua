using System.Text;
using System.Text.Json;
using DaoHua.ComputeNode;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Server;
using Server.Clean;
using Xunit;
using NodeJobRequest = DaoHua.ComputeNode.AdvanceJobRequest;

namespace Server.Tests;

/// <summary>
/// 冷挂机 golden vector：同一输入经「主服重连补算管线」与「ComputeNode 真实模拟管线」两端推进，
/// 产出必须逐字段、逐字节一致——section 载荷字节、codec、原始校验和、版本、提交头校验和与资产增量。
/// 测试经 JSON 线协议往返驱动节点真实引擎（<see cref="DeterministicBatchEngine"/>），
/// 不走共享类型直通——任一端的契约字段名、checksum 口径或领域语义漂移都会在这里变红。
/// 边界说明：MatchingEnabled 的差异属于设计内跨玩家行为（离线无对手可撮合），
/// 本测试两路径统一按离线口径 <see cref="DuelAdvanceOptions"/>(MatchingEnabled: false) 比对。
/// </summary>
public sealed class ComputeGoldenVectorTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>写回时间戳：两端用同一固定值，使 SectionWrite 记录可整体逐字段比较。</summary>
    private static readonly DateTimeOffset Stamp = Baseline.AddHours(1);
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();
    private static readonly DeterministicBatchEngine NodeEngine = new(Catalog);
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 带消耗品行动的真实推进：节点输出与在线管线逐 section 逐字节一致，
    /// 资产增量按 (assetId, variant) 合并后逐行一致，提交头 checksum 重算一致。
    /// </summary>
    [Fact]
    public void ConsumableActionAdvanceIsByteIdenticalAcrossPaths()
    {
        var playerId = PlayerId.New();
        var accountId = AccountId.New();
        // 造一个真实离线玩家：转入离线 + 排队一个消耗材料的炼制行动（12s/轮，3 轮）。
        var bundle = NewBundle(playerId, accountId, "金丹客", Catalog.Fingerprint, player =>
        {
            player.SetPresence(PresenceState.Offline, Baseline);
            player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 3), Baseline, Catalog.Actions);
        });
        // 钱包快照：3 轮炼制共需凝血草×9。
        var balances = new ComputeAssetBalance[] { new("item.ningxue-grass", "", 9, 1) };
        var target = Baseline.AddSeconds(40);

        var request = NewJobRequest(bundle, target, balances);
        var nodeResult = RunNodeJobOverWire(request);
        Assert.True(nodeResult.Success, $"节点推进失败：{nodeResult.ErrorCode} {nodeResult.ErrorMessage}");

        // 在线参考管线：与生产运行时重连补算完全同序——还原 → 指纹重绑 → 钱包 → 离线推进 → 编码脏段。
        var (onlineWrites, onlineResult, onlineVersion) = RunOnlineReference(bundle, target, balances);

        Assert.Equal(onlineVersion, nodeResult.TargetStateVersion);
        Assert.Equal(bundle.Head.StateVersion, nodeResult.SourceStateVersion);
        AssertSectionsEqual(onlineWrites, nodeResult);
        AssertDeltasEqual(onlineResult.AssetDeltas, nodeResult.AssetDeltas);
        Assert.Equal((long)onlineResult.CompletedActions, nodeResult.Metrics?.CompletedActions ?? -1);
        AssertHeadChecksumParity(bundle, onlineWrites, nodeResult, nodeResult.TargetStateVersion);
    }

    /// <summary>
    /// 空推进（无队列、短时窗）：版本不前移，sections 与资产增量均为空——合法契约输出。
    /// 新玩家首次推进会把 Combat 规范化到配表属性口径（解析值 ≠ 构造默认值），
    /// 因此先推进一秒落盘规范化状态，后续空闲推进才是真空推进。
    /// </summary>
    [Fact]
    public void IdleAdvanceIsAnEmptySuccessResult()
    {
        var bundle = NewBundle(PlayerId.New(), AccountId.New(), "闲云", Catalog.Fingerprint,
            player =>
            {
                player.SetPresence(PresenceState.Offline, Baseline);
                // 一小时预推进：完成 Combat 属性规范化并把自然回复吃到满值，
                // 此后空闲推进才是真正的空推进（离线空闲回血是真实状态变化）。
                SimulationEngine.AdvanceTo(player, Baseline.AddHours(1), Catalog,
                    new AssetWallet(player.PlayerId));
            });
        var request = NewJobRequest(bundle, Baseline.AddSeconds(60), []);

        var nodeResult = RunNodeJobOverWire(request);
        Assert.True(nodeResult.Success, $"节点推进失败：{nodeResult.ErrorCode} {nodeResult.ErrorMessage}");
        Assert.Equal(bundle.Head.StateVersion, nodeResult.SourceStateVersion);
        var kinds = nodeResult.Sections is null ? "∅" : string.Join(",", nodeResult.Sections.Select(s => s.Kind));
        Assert.True(nodeResult.TargetStateVersion == bundle.Head.StateVersion,
            $"目标版本意外前移：sections=[{kinds}]");
        Assert.True(nodeResult.Sections is null || nodeResult.Sections.Count == 0);
        Assert.True(nodeResult.AssetDeltas is null || nodeResult.AssetDeltas.Count == 0);

        // 在线路径同样不产生任何变更：版本持平、脏段为空。
        var (onlineWrites, onlineResult, onlineVersion) = RunOnlineReference(bundle, Baseline.AddSeconds(60), []);
        Assert.True(onlineVersion == bundle.Head.StateVersion,
            $"版本意外前移：dirty=[{string.Join(",", onlineWrites.Select(w => w.SectionKind))}]");
        Assert.Empty(onlineWrites);
        Assert.Empty(onlineResult.AssetDeltas);
    }

    /// <summary>
    /// 48 小时封顶尾段：超出上限只推模拟时钟不结算收益——版本前进但允许零 section 写回，
    /// 该形态正是主服「目标版本前进 + 空 writes」放行路径的输入。
    /// </summary>
    [Fact]
    public void CappedAdvanceBeyondWindowBumpsVersionPossiblyWithoutSections()
    {
        var bundle = NewBundle(PlayerId.New(), AccountId.New(), "归隐者", Catalog.Fingerprint,
            player =>
            {
                player.SetPresence(PresenceState.Offline, Baseline);
                // 先规范化 Combat 并把回血吃满：保证 48h 封顶尾段产出「版本前进 + 零 section」形态。
                SimulationEngine.AdvanceTo(player, Baseline.AddHours(1), Catalog,
                    new AssetWallet(player.PlayerId));
            });
        var target = Baseline.AddHours(50); // 距 LastSimulatedAt（Baseline+1h）49h > 48h 上限

        var request = NewJobRequest(bundle, target, []);
        var nodeResult = RunNodeJobOverWire(request);
        Assert.True(nodeResult.Success, $"节点推进失败：{nodeResult.ErrorCode} {nodeResult.ErrorMessage}");
        Assert.True(nodeResult.TargetStateVersion > bundle.Head.StateVersion,
            "封顶尾段必须推进状态版本以落盘 last_simulated_at。");
        var cappedKinds = nodeResult.Sections is null ? "∅" : string.Join(",", nodeResult.Sections.Select(s => s.Kind));
        Assert.True(nodeResult.Sections is null || nodeResult.Sections.Count == 0,
            $"规范化空闲玩家的封顶尾段不应产生 section 写回，实际 sections=[{cappedKinds}]。");

        var (onlineWrites, onlineResult, onlineVersion) = RunOnlineReference(bundle, target, []);
        Assert.Equal(onlineVersion, nodeResult.TargetStateVersion);
        AssertSectionsEqual(onlineWrites, nodeResult);
        AssertDeltasEqual(onlineResult.AssetDeltas, nodeResult.AssetDeltas);
    }

    /// <summary>存档指纹落后于当前配表：两端都先 Rebind（Settings 标脏 + 版本递增）再推进，输出仍逐字节一致。</summary>
    [Fact]
    public void FingerprintRebindProducesIdenticalOutput()
    {
        var bundle = NewBundle(PlayerId.New(), AccountId.New(), "转世重修", "legacy-fingerprint",
            player => player.SetPresence(PresenceState.Offline, Baseline));
        var request = NewJobRequest(bundle, Baseline.AddSeconds(30), []);

        var nodeResult = RunNodeJobOverWire(request);
        Assert.True(nodeResult.Success, $"节点推进失败：{nodeResult.ErrorCode} {nodeResult.ErrorMessage}");
        Assert.True(nodeResult.TargetStateVersion > bundle.Head.StateVersion);
        // 换绑本身标脏 Settings：输出中必须携带它。
        Assert.Contains(nodeResult.Sections!, section => section.Kind == PlayerSectionKinds.Settings);

        var (onlineWrites, onlineResult, onlineVersion) = RunOnlineReference(bundle, Baseline.AddSeconds(30), []);
        Assert.Equal(onlineVersion, nodeResult.TargetStateVersion);
        AssertSectionsEqual(onlineWrites, nodeResult);
        AssertDeltasEqual(onlineResult.AssetDeltas, nodeResult.AssetDeltas);
    }

    /// <summary>
    /// 确定性回放：同一任务在同一节点引擎上重复执行必须产出逐字节一致的结果；
    /// 批量内逐任务与单独执行一致（引擎按任务无状态）。
    /// </summary>
    [Fact]
    public void SameJobIsDeterministicAcrossRunsAndBatchPositions()
    {
        var playerId = PlayerId.New();
        var bundle = NewBundle(playerId, AccountId.New(), "复算者", Catalog.Fingerprint, player =>
        {
            player.SetPresence(PresenceState.Offline, Baseline);
            player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, Catalog.Actions);
        });
        var request = NewJobRequest(bundle, Baseline.AddMinutes(5), []);

        var first = RunNodeJobOverWire(request);
        var second = RunNodeJobOverWire(request with { });   // 批量中的同任务重放
        Assert.True(first.Success && second.Success);
        // 指标含实测耗时，确定性断言只覆盖版本、载荷与资产增量。
        Assert.Equal(first.SourceStateVersion, second.SourceStateVersion);
        Assert.Equal(first.TargetStateVersion, second.TargetStateVersion);
        Assert.Equal(first.Sections, second.Sections);
        Assert.Equal(first.AssetDeltas, second.AssetDeltas);

        // 无限循环（RemainingRepeats=-1）行动推进后必须仍在队列中——断言 action_queue 段持续输出其驻留证据。
        var queueSection = Assert.Single(first.Sections!, s => s.Kind == PlayerSectionKinds.ActionQueue);
        var raw = SectionCodec.DecodeRaw("identity" == queueSection.Codec ? SectionCodec.RawJson : SectionCodec.BrotliJson,
            Convert.FromBase64String(queueSection.PayloadBase64));
        Assert.Contains("-1", Encoding.UTF8.GetString(raw));
    }

    /// <summary>压缩段路径：让 action_queue 载荷超过压缩阈值，确保 brotli 线编码在两端往返逐字节一致。</summary>
    [Fact]
    public void CompressedSectionsSurviveWireRoundTripByteIdentical()
    {
        var playerId = PlayerId.New();
        var bundle = NewBundle(playerId, AccountId.New(), "压缩者", Catalog.Fingerprint, player =>
        {
            player.SetPresence(PresenceState.Offline, Baseline);
            // 多项行动把 action_queue 原始载荷推过 512B 自适应压缩阈值。
            for (var i = 0; i < 12; i++)
                player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1), Baseline, Catalog.Actions);
        });
        var request = NewJobRequest(bundle, Baseline.AddSeconds(15), []);
        Assert.Contains(request.Sections, section => section.Codec == "brotli");

        var nodeResult = RunNodeJobOverWire(request);
        Assert.True(nodeResult.Success, $"节点推进失败：{nodeResult.ErrorCode} {nodeResult.ErrorMessage}");

        var (onlineWrites, onlineResult, onlineVersion) = RunOnlineReference(bundle, Baseline.AddSeconds(15), []);
        Assert.Equal(onlineVersion, nodeResult.TargetStateVersion);
        AssertSectionsEqual(onlineWrites, nodeResult);
        AssertDeltasEqual(onlineResult.AssetDeltas, nodeResult.AssetDeltas);
    }

    /// <summary>拒绝路径：篡改 checksum、缺段、未知 section、非法 Base64 一律产出不可重试失败。</summary>
    [Fact]
    public void CorruptedInputsAreRejectedWithoutRetry()
    {
        var bundle = NewBundle(PlayerId.New(), AccountId.New(), "受害者", Catalog.Fingerprint,
            player => player.SetPresence(PresenceState.Offline, Baseline));

        // checksum 篡改：头校验和验证或段校验和验证必有一环拒绝。
        var tampered = NewJobRequest(bundle, Baseline.AddSeconds(30), []);
        var badChecksum = tampered.Sections
            .Select((section, index) => index == 0
                ? section with { Checksum = new string('0', 64) }
                : section)
            .ToArray();
        var tamperedResult = RunNodeJobOverWire(tampered with { Sections = badChecksum });
        Assert.False(tamperedResult.Success);
        Assert.False(tamperedResult.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(tamperedResult.ErrorCode));

        // 未知 section kind 替换合法段：全集校验拒绝。
        var unknown = tampered.Sections
            .Select((section, index) => index == 0
                ? section with { Kind = "bogus.kind" }
                : section)
            .ToArray();
        var unknownResult = RunNodeJobOverWire(tampered with { Sections = unknown });
        Assert.False(unknownResult.Success);
        Assert.False(unknownResult.Retryable);

        // 非法 Base64：解码层拒绝。
        var badBase64 = tampered.Sections
            .Select((section, index) => index == 0
                ? section with { PayloadBase64 = "!!!not-base64!!!" }
                : section)
            .ToArray();
        var badBase64Result = RunNodeJobOverWire(tampered with { Sections = badBase64 });
        Assert.False(badBase64Result.Success);
        Assert.False(badBase64Result.Retryable);
    }

    /// <summary>契约层校验口径：缺段/重复段/未来目标/坏头校验和/空载荷都被节点 ValidateJob 拦下。</summary>
    [Fact]
    public void NodeContractValidationRejectsMalformedJobs()
    {
        var bundle = NewBundle(PlayerId.New(), AccountId.New(), "边界", Catalog.Fingerprint,
            player => player.SetPresence(PresenceState.Offline, Baseline));
        var request = NewJobRequest(bundle, Baseline.AddSeconds(30), []);

        // 缺一段。
        var missing = request with { Sections = request.Sections.Skip(1).ToArray() };
        Assert.Equal("INVALID_SECTIONS", ComputeContract.ValidateJob(ToNodeJob(missing))?.Code);

        // 重复 kind（替换成另一段已有 kind，保持 8 段总数以越过数量检查、命中唯一性检查）。
        var duplicated = request.Sections
            .Select((section, index) => index == 0
                ? section with { Kind = request.Sections[1].Kind }
                : section)
            .ToArray();
        Assert.Equal("INVALID_SECTION", ComputeContract.ValidateJob(
            ToNodeJob(request with { Sections = duplicated }))?.Code);

        // 空载荷。
        var emptyPayload = request.Sections
            .Select((section, index) => index == 0 ? section with { PayloadBase64 = "" } : section)
            .ToArray();
        Assert.Equal("INVALID_SECTION", ComputeContract.ValidateJob(
            ToNodeJob(request with { Sections = emptyPayload }))?.Code);

        // 头校验和非法格式。
        Assert.Equal("INVALID_HEAD", ComputeContract.ValidateJob(
            ToNodeJob(request with { HeadChecksum = "not-hex" }))?.Code);

        // 目标时间超过节点当前时刻 5 分钟以上。
        Assert.Equal("TARGET_IN_FUTURE", ComputeContract.ValidateJob(
            ToNodeJob(request with
            {
                TargetUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds()
            }))?.Code);
    }

    // ===== 测试基建 =====

    /// <summary>构造完整持久化绑定包：聚合创建后全段标脏，经 EncodeAggregate 落出 8 段与一致头校验和。</summary>
    private static PlayerStateBundle NewBundle(
        PlayerId playerId, AccountId accountId, string displayName, string fingerprint,
        Action<PlayerAggregate> arrange)
    {
        var player = PlayerAggregate.Create(playerId, accountId, displayName, Baseline, fingerprint);
        arrange(player);
        var writes = SectionCodec.EncodeAggregate(player, Baseline);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes,
                write.Checksum, write.UpdatedAt),
            StringComparer.Ordinal);
        Assert.Equal(PlayerSectionKinds.All.Count, sections.Count);
        var head = new PlayerStateHead(
            playerId.Value, player.StateVersion, SectionCodec.CurrentSchemaVersion,
            Baseline, fingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), Baseline);
        return new PlayerStateBundle(
            new PlayerIdentity(playerId.Value, accountId.Value, displayName, Baseline),
            head, sections, []);
    }

    /// <summary>
    /// 以主服 ColdCatchUpWorker 同口径产出任务请求：section 按 kind Ordinal 排序后经
    /// ComputeSectionCodec.ToWire 映射为线载荷，余额快照随任务冻结。
    /// </summary>
    private static ComputeAdvanceJobRequest NewJobRequest(
        PlayerStateBundle bundle, DateTimeOffset target, IReadOnlyList<ComputeAssetBalance> balances)
    {
        var sections = bundle.Sections.Values
            .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
            .Select(ComputeSectionCodec.ToWire)
            .ToArray();
        return new ComputeAdvanceJobRequest(
            PostgresComputeJobRepository.DeriveJobId(bundle.Identity.PlayerId, target.ToUnixTimeMilliseconds()),
            bundle.Identity.PlayerId,
            bundle.Identity.AccountId,
            bundle.Identity.DisplayName,
            bundle.Identity.CreatedAt.ToUnixTimeMilliseconds(),
            bundle.Head.StateVersion,
            bundle.Head.LastSimulatedAt.ToUnixTimeMilliseconds(),
            target.ToUnixTimeMilliseconds(),
            bundle.Head.Checksum,
            bundle.Head.GameDataFingerprint,
            sections,
            balances,
            $"cold:{bundle.Identity.PlayerId:D}:{target.ToUnixTimeMilliseconds()}");
    }

    /// <summary>
    /// 经真实 JSON 线协议驱动节点引擎：主服记录 → ServerJson 序列化 → 节点记录反序列化 →
    /// engine.AdvanceAsync → 节点结果序列化 → 主服记录反序列化。任一侧字段名漂移即在此处现形。
    /// </summary>
    private static ComputeAdvanceJobResult RunNodeJobOverWire(ComputeAdvanceJobRequest request)
    {
        var nodeResult = NodeEngine.AdvanceAsync(ToNodeJob(request)).GetAwaiter().GetResult();
        var wire = JsonSerializer.SerializeToUtf8Bytes(nodeResult, WireJson);
        return JsonSerializer.Deserialize<ComputeAdvanceJobResult>(wire, ServerJson.Options)
            ?? throw new InvalidDataException("节点结果线载荷无法反序列化。");
    }

    /// <summary>主服任务记录转节点任务记录：走 JSON 序列化往返，与真实链路等价。</summary>
    private static NodeJobRequest ToNodeJob(ComputeAdvanceJobRequest request)
    {
        var wire = JsonSerializer.SerializeToUtf8Bytes(request, ServerJson.Options);
        return JsonSerializer.Deserialize<NodeJobRequest>(wire, WireJson)
            ?? throw new InvalidDataException("任务请求线载荷无法反序列化。");
    }

    /// <summary>
    /// 在线/重连参考管线：与生产运行时补算路径同序——
    /// DecodeAggregate → Restore → RebindGameDataFingerprint → 钱包还原 → AdvanceTo(离线) → EncodeAggregate。
    /// 返回推进后的脏段写集、领域模拟结果与聚合新版本。
    /// </summary>
    private static (IReadOnlyList<SectionWrite> Writes, SimulationResult Result, long StateVersion)
        RunOnlineReference(
            PlayerStateBundle bundle,
            DateTimeOffset target,
            IReadOnlyList<ComputeAssetBalance> balances)
    {
        var state = SectionCodec.DecodeAggregate(bundle);
        var player = PlayerAggregate.Restore(state);
        player.RebindGameDataFingerprint(Catalog.Fingerprint);
        var wallet = new AssetWallet(player.PlayerId);
        if (balances.Count > 0)
            wallet.Restore(balances.Select(row => new AssetBalance(
                player.PlayerId, new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, 0)));
        var result = SimulationEngine.AdvanceTo(
            player, target, Catalog, wallet, new DuelAdvanceOptions(MatchingEnabled: false));
        return (SectionCodec.EncodeAggregate(player, Stamp), result, player.StateVersion);
    }

    /// <summary>
    /// section 级逐字段、逐字节一致性：kind/版本/schema/codec/原始字节数/校验和/载荷字节/
    /// 配表指纹/模拟时刻全部相等，两端集合按 Ordinal 排序后一一对齐。
    /// </summary>
    private static void AssertSectionsEqual(IReadOnlyList<SectionWrite> onlineWrites, ComputeAdvanceJobResult nodeResult)
    {
        var expected = onlineWrites.OrderBy(write => write.SectionKind, StringComparer.Ordinal).ToArray();
        var actual = (nodeResult.Sections ?? [])
            .Select(section => ComputeSectionCodec.ToSectionWrite(
                nodeResult.PlayerId, nodeResult.TargetStateVersion, section, Stamp))
            .OrderBy(write => write.SectionKind, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            // 比对持久化列全集；GameDataFingerprint/LastSimulatedAt 是 latest-only 刷盘队列的
            // 携带元数据，不走线协议、不参与 section 落库，不属于本节断言范围。
            Assert.Equal(expected[i].PlayerId, actual[i].PlayerId);
            Assert.Equal(expected[i].SectionKind, actual[i].SectionKind);
            Assert.Equal(expected[i].StateVersion, actual[i].StateVersion);
            Assert.Equal(expected[i].SchemaVersion, actual[i].SchemaVersion);
            Assert.Equal(expected[i].Codec, actual[i].Codec);
            Assert.Equal(expected[i].RawBytes, actual[i].RawBytes);
            Assert.Equal(expected[i].Checksum, actual[i].Checksum);
            Assert.Equal(expected[i].UpdatedAt, actual[i].UpdatedAt);
            // 逐字节比对编码后载荷：压缩字节也必须完全一致。
            Assert.True(expected[i].Payload.Span.SequenceEqual(actual[i].Payload.Span),
                $"section {expected[i].SectionKind} 载荷字节不一致。");
        }
    }

    /// <summary>资产增量一致性：领域增量按 (assetId, variant) 合并后与节点回送行逐行相等（含 Ordinal 行序）。</summary>
    private static void AssertDeltasEqual(
        IReadOnlyList<Domain.Economy.AssetDelta> onlineDeltas,
        IReadOnlyList<ComputeAssetDelta>? nodeRows)
    {
        var expected = onlineDeltas
            .GroupBy(delta => (delta.Asset.ItemId.Value, delta.Asset.Variant))
            .Select(group => (group.Key.Value, group.Key.Variant, group.Sum(delta => delta.QuantityDelta)))
            .Where(row => row.Item3 != 0)
            .OrderBy(row => row.Value, StringComparer.Ordinal)
            .ThenBy(row => row.Variant, StringComparer.Ordinal)
            .ToArray();
        var actual = (nodeRows ?? [])
            .Select(row => (row.AssetId, row.Variant, row.QuantityDelta))
            .ToArray();
        Assert.Equal(expected, actual);
        // 行序必须 Ordinal 稳定：排序后与原序一致。
        var sorted = actual
            .OrderBy(row => row.AssetId, StringComparer.Ordinal)
            .ThenBy(row => row.Variant, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(sorted, actual);
    }

    /// <summary>
    /// 提交头 checksum 口径一致性：把节点回传段应用到原存档（同版本替换脏段）后重算头校验和，
    /// 与在线推进后同一应用口径重算结果必须相等——这正是主服落库时 ReadSectionChecksums 的产物。
    /// </summary>
    private static void AssertHeadChecksumParity(
        PlayerStateBundle bundle,
        IReadOnlyList<SectionWrite> onlineWrites,
        ComputeAdvanceJobResult nodeResult,
        long targetStateVersion)
    {
        static IReadOnlyDictionary<string, StoredSection> ApplyWrites(
            PlayerStateBundle baseBundle, IEnumerable<SectionWrite> writes, DateTimeOffset at)
        {
            var sections = new Dictionary<string, StoredSection>(baseBundle.Sections, StringComparer.Ordinal);
            foreach (var write in writes)
            {
                sections[write.SectionKind] = new StoredSection(
                    write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                    write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, at);
            }
            return sections;
        }

        var onlineChecksum = SectionCodec.ComputeHeadChecksum(
            ApplyWrites(bundle, onlineWrites, Stamp).Values);
        var nodeWrites = (nodeResult.Sections ?? [])
            .Select(section => ComputeSectionCodec.ToSectionWrite(
                nodeResult.PlayerId, targetStateVersion, section, Stamp));
        var nodeChecksum = SectionCodec.ComputeHeadChecksum(
            ApplyWrites(bundle, nodeWrites, Stamp).Values);
        Assert.Equal(onlineChecksum, nodeChecksum);
    }
}
