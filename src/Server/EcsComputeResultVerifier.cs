using DaoHua.Ecs;
using Server.Clean;

namespace Server;

/// <summary>
/// 主服独立重演 ComputeNode 候选结果。
/// 节点输出只作为候选，验证器以任务中冻结的完整源基线重新装载独立 ECS 世界，
/// 逐 section 比较编码字节、原始 JSON 字节、checksum、版本与目标时钟；资产增量必须与独立重演逐项一致。
/// </summary>
public sealed class EcsComputeResultVerifier(GameDataCatalog gameData) : IComputeResultVerifier, IDisposable
{
    private readonly DeterministicCatchUpEngine _engine = new(gameData);

    public async ValueTask<ComputeVerificationResult> VerifyAsync(
        ComputeAdvanceJobRequest request,
        ComputeAdvanceJobResult candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.JobId != request.JobId || candidate.PlayerId != request.PlayerId)
                return ComputeVerificationResult.Rejected("结果身份与冻结任务不一致。");
            if (!candidate.Success)
                return ComputeVerificationResult.Rejected("失败结果不得进入可信补算验证写回。");
            if (candidate.SourceStateVersion != request.SourceStateVersion
                || candidate.TargetStateVersion < candidate.SourceStateVersion)
                return ComputeVerificationResult.Rejected("结果版本与冻结任务不一致。");

            var replay = await ReplayAsync(request, cancellationToken).ConfigureAwait(false);

            if (replay.SourceStateVersion != candidate.SourceStateVersion
                || replay.TargetStateVersion != candidate.TargetStateVersion
                || replay.LastSimulatedAt.ToUnixTimeMilliseconds() != candidate.LastSimulatedAtUnixMilliseconds)
                return ComputeVerificationResult.Rejected("独立重演版本或实际模拟时钟与节点结果不一致。");

            var expectedDeltas = MergeReplayAssetDeltas(request.PlayerId, replay.AssetDeltas);
            var candidateDeltas = ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
                request.PlayerId, request, candidate.AssetDeltas, gameData);
            if (!expectedDeltas.SequenceEqual(candidateDeltas))
                return ComputeVerificationResult.Rejected("独立重演资产增量与节点结果不一致。");

            var writes = CompareSections(request.PlayerId, replay.Sections, candidate);
            if (replay.TargetStateVersion == replay.SourceStateVersion && writes.Count != 0)
                return ComputeVerificationResult.Rejected("版本未前进的结果不得携带 section。");

            return new ComputeVerificationResult(
                true,
                null,
                writes,
                expectedDeltas,
                replay.SourceStateVersion,
                replay.TargetStateVersion,
                replay.LastSimulatedAt);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException
            or ArgumentException or DomainException or NotSupportedException)
        {
            return ComputeVerificationResult.Rejected($"独立重演拒绝：{exception.Message}");
        }
    }

    /// <summary>
    /// 主服本地执行入口（§14.4）：独立重演产物本身即可信结果，作为写回依据直接返回；
    /// 不再携带候选比较。解码/领域异常与 <see cref="VerifyAsync"/> 同口径转 Rejected。
    /// </summary>
    public async ValueTask<ComputeVerificationResult> ExecuteAsync(
        ComputeAdvanceJobRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            var replay = await ReplayAsync(request, cancellationToken).ConfigureAwait(false);
            return new ComputeVerificationResult(
                true,
                null,
                replay.Sections,
                MergeReplayAssetDeltas(request.PlayerId, replay.AssetDeltas),
                replay.SourceStateVersion,
                replay.TargetStateVersion,
                replay.LastSimulatedAt);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException
            or ArgumentException or DomainException or NotSupportedException)
        {
            return ComputeVerificationResult.Rejected($"本地执行重演拒绝：{exception.Message}");
        }
    }

    /// <summary>
    /// 独立重演公共路径：解码冻结 bundle + 还原钱包快照 + 在独立 ECS 世界推进到目标时刻。
    /// 验证与本地执行共用同一 <see cref="DeterministicCatchUpEngine"/>，其内部门串行即可。
    /// </summary>
    private async ValueTask<DeterministicCatchUpResult> ReplayAsync(
        ComputeAdvanceJobRequest request, CancellationToken cancellationToken)
    {
        var bundle = DecodeFrozenBundle(request);
        var balances = request.AssetBalances is { Count: > 0 } rows
            ? rows.Select(row => new AssetBalance(
                new PlayerId(request.PlayerId), new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)).ToArray()
            : null;
        var target = DateTimeOffset.FromUnixTimeMilliseconds(request.TargetUnixMilliseconds);
        return await _engine.AdvanceAsync(bundle, balances, target, target, cancellationToken).ConfigureAwait(false);
    }

    private static Server.Clean.AssetDelta[] MergeReplayAssetDeltas(
        Guid playerId, IReadOnlyList<Domain.Economy.AssetDelta> rows)
    {
        if (rows.Count == 0) return [];
        var merged = new Dictionary<(string AssetId, string Variant), double>();
        foreach (var row in rows)
        {
            var key = (row.Asset.ItemId.Value, row.Asset.Variant);
            merged[key] = merged.TryGetValue(key, out var current)
                ? current + row.QuantityDelta
                : row.QuantityDelta;
            if (!double.IsFinite(merged[key]))
                throw new InvalidDataException("独立重演资产增量不是有限数值。");
        }

        return merged
            .Where(pair => pair.Value != 0)
            .OrderBy(pair => pair.Key.AssetId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Variant, StringComparer.Ordinal)
            .Select(pair => new Server.Clean.AssetDelta(playerId, pair.Key.AssetId, pair.Key.Variant, pair.Value))
            .ToArray();
    }

    private static PlayerStateBundle DecodeFrozenBundle(ComputeAdvanceJobRequest request)
    {
        if (request.Sections is null || request.Sections.Count != PlayerSectionKinds.All.Count)
            throw new InvalidDataException("冻结源基线 section 不完整。");

        var sections = new Dictionary<string, StoredSection>(StringComparer.Ordinal);
        foreach (var payload in request.Sections)
        {
            if (payload is null || !PlayerSectionKinds.IsValid(payload.Kind) || !sections.TryAdd(payload.Kind, null!))
                throw new InvalidDataException("冻结源基线 section 重复或未知。");
            var encoded = Convert.FromBase64String(payload.PayloadBase64);
            if (encoded.Length == 0 || encoded.Length > ComputeDispatchContract.MaxBatchBytes)
                throw new InvalidDataException("冻结源基线 section 大小无效。");
            var codec = payload.Codec switch
            {
                "identity" => SectionCodec.RawJson,
                "brotli" => SectionCodec.BrotliJson,
                _ => throw new NotSupportedException($"不支持 section 编码：{payload.Codec}")
            };
            var raw = SectionCodec.DecodeRaw(codec, encoded);
            var rawChecksum = Convert.ToHexStringLower(SHA256.HashData(raw));
            if (!string.Equals(rawChecksum, payload.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"源 section {payload.Kind} 原始校验和不匹配。");
            sections[payload.Kind] = new StoredSection(
                request.PlayerId,
                payload.Kind,
                payload.Version,
                SectionCodec.CurrentSchemaVersion,
                SectionCodec.RawJson,
                raw,
                raw.Length,
                rawChecksum,
                DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMilliseconds));
        }

        if (sections.Count != PlayerSectionKinds.All.Count)
            throw new InvalidDataException("冻结源基线 section 缺失。");
        var head = new PlayerStateHead(
            request.PlayerId,
            request.SourceStateVersion,
            SectionCodec.CurrentSchemaVersion,
            DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMilliseconds),
            request.HeadGameDataFingerprint,
            request.HeadChecksum,
            DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMilliseconds));
        var identity = new PlayerIdentity(
            request.PlayerId,
            request.AccountId,
            request.DisplayName,
            DateTimeOffset.FromUnixTimeMilliseconds(request.CreatedAtUnixMilliseconds));
        var bundle = new PlayerStateBundle(identity, head, sections, []);
        if (!string.Equals(SectionCodec.ComputeHeadChecksum(sections.Values), request.HeadChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("冻结源基线提交头 checksum 不匹配。");
        return bundle;
    }

    private static IReadOnlyList<SectionWrite> CompareSections(
        Guid playerId,
        IReadOnlyList<SectionWrite> expected,
        ComputeAdvanceJobResult candidate)
    {
        var actualPayloads = candidate.Sections ?? [];
        var actualKinds = new HashSet<string>(StringComparer.Ordinal);
        var actual = new Dictionary<string, SectionWrite>(StringComparer.Ordinal);
        foreach (var payload in actualPayloads)
        {
            if (payload is null || !actualKinds.Add(payload.Kind))
                throw new InvalidDataException("节点结果包含重复 section。");
            if (payload.Version != candidate.TargetStateVersion)
                throw new InvalidDataException("节点结果 section 版本不等于目标版本。");
            var write = ComputeSectionCodec.ToSectionWrite(
                playerId, candidate.TargetStateVersion, payload, DateTimeOffset.UnixEpoch);
            if (!actual.TryAdd(write.SectionKind, write))
                throw new InvalidDataException("节点结果包含重复 section。");
        }

        var expectedByKind = expected.ToDictionary(write => write.SectionKind, StringComparer.Ordinal);
        if (actual.Count != expectedByKind.Count || actual.Keys.Except(expectedByKind.Keys, StringComparer.Ordinal).Any())
            throw new InvalidDataException("节点结果 section 集合与独立重演不一致。");

        foreach (var (kind, expectedWrite) in expectedByKind)
        {
            var actualWrite = actual[kind];
            if (actualWrite.StateVersion != expectedWrite.StateVersion
                || actualWrite.SchemaVersion != expectedWrite.SchemaVersion
                || actualWrite.Codec != expectedWrite.Codec
                || actualWrite.RawBytes != expectedWrite.RawBytes
                || actualWrite.Checksum != expectedWrite.Checksum
                || !actualWrite.Payload.Span.SequenceEqual(expectedWrite.Payload.Span))
                throw new InvalidDataException($"section {kind} 与独立重演的原始 JSON/编码字节不一致。");
        }
        return expected;
    }

    public void Dispose() => _engine.Dispose();
}
