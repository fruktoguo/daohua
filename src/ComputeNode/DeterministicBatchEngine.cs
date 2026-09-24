namespace DaoHua.ComputeNode;

/// <summary>
/// 确定性离线挂机批量补算引擎（compute.v2 真实模拟路径）。
/// 架构边界：
/// 1. 节点只运行确定性纯计算，无状态、不持有任何数据库读写权限；
/// 2. 与主服重连补算共用同一条领域管线：SectionCodec 解码 8 段载荷 →
///    PlayerAggregate.Restore 还原聚合 → RebindGameDataFingerprint 对齐当前配表 →
///    AssetWallet.Restore 还原钱包快照 → SimulationEngine.AdvanceTo 离线推进 →
///    EncodeAggregate 只编码脏分段。同输入两端逐字节一致由 golden vector 测试守门；
/// 3. 离线补算一律 <see cref="DuelAdvanceOptions"/>(MatchingEnabled: false)：
///    斗法撮合是跨玩家交互，只能由主服在线路径完成；
/// 4. 输出的新版本号与校验和必须能被主服 CAS 与 section checksum 机制校验落盘；
///    资产增量随结果回传，由主服在结果落库事务内经既有经济事务原子入账——
///    节点不创造第二套资产协议，也不直接写余额。
/// </summary>
public sealed class DeterministicBatchEngine(GameDataCatalog gameData)
{
    /// <summary>
    /// 对单个离线任务执行确定性时间快进推演。
    /// </summary>
    /// <param name="job">单个离线任务请求。</param>
    /// <returns>任务推进结果。</returns>
    public AdvanceJobResult Advance(AdvanceJobRequest job)
    {
        try
        {
            return AdvanceCore(job);
        }
        catch (FormatException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_PAYLOAD", "section Base64 载荷无效。", false);
        }
        catch (JsonException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_JSON_SECTION", "section JSON 载荷无效。", false);
        }
        catch (InvalidDataException exception)
        {
            return AdvanceJobResult.Failed(job, "INVALID_CODEC", exception.Message, false);
        }
        catch (DomainException exception)
        {
            // 存档违反领域不变量（浮点非有限、非法队列态等）：重试不会自愈，判死保留证据。
            return AdvanceJobResult.Failed(job, "INVALID_STATE", exception.Message, false);
        }
        catch (OverflowException)
        {
            return AdvanceJobResult.Failed(job, "VERSION_OVERFLOW", "状态版本超出允许范围。", false);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_SECTION", exception.Message, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 引擎内部未预期异常：不回传细节堆栈，错误码保留可追溯证据，重试同一输入必然复发。
            return AdvanceJobResult.Failed(job, "ENGINE_ERROR", $"模拟推进异常：{exception.GetType().Name}", false);
        }
    }

    /// <summary>真实模拟主流程：解码 → 还原 → 推进 → 编码回传。</summary>
    private AdvanceJobResult AdvanceCore(AdvanceJobRequest job)
    {
        var started = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        var target = DateTimeOffset.FromUnixTimeMilliseconds(job.TargetUnixMilliseconds);

        // 线载荷还原为存储形态：codec=json + payload=原始 JSON 字节。
        // DecodeRaw 已按线编码解压，StoredSection.Payload 承载解码后字节、Codec 记为 json，
        // 是合法且自洽的存储形态（checksum/RawBytes 均按原始字节口径）。
        var sections = new Dictionary<string, StoredSection>(job.Sections.Count, StringComparer.Ordinal);
        long inputBytes = 0;
        foreach (var section in job.Sections)
        {
            var stored = DecodeSection(job.PlayerId, section, now);
            sections.Add(stored.SectionKind, stored);
            inputBytes += stored.RawBytes;
        }

        // 重建持久化绑定包：提交头校验和在此完成完整性核验（任一段篡改都会被拒）。
        var identity = new PlayerIdentity(
            job.PlayerId, job.AccountId, job.DisplayName,
            DateTimeOffset.FromUnixTimeMilliseconds(job.CreatedAtUnixMilliseconds));
        var head = new PlayerStateHead(
            job.PlayerId, job.SourceStateVersion, SectionCodec.CurrentSchemaVersion,
            DateTimeOffset.FromUnixTimeMilliseconds(job.FromUnixMilliseconds),
            job.HeadGameDataFingerprint, job.HeadChecksum, now);
        var state = SectionCodec.DecodeAggregate(new PlayerStateBundle(identity, head, sections, []));
        var player = PlayerAggregate.Restore(state);
        // 与主服重连路径一致：存档指纹切换到当前配表，轮换时 Settings 标脏并入版本。
        player.RebindGameDataFingerprint(gameData.Fingerprint);

        // 钱包只用于消耗品/材料可用性判定；扣减结果经 AssetDeltas 回传，由主服事务入账。
        var wallet = new AssetWallet(player.PlayerId);
        if (job.AssetBalances is { Count: > 0 } rows)
            wallet.Restore(rows.Select(row => new AssetBalance(
                player.PlayerId, new AssetKey(new ItemId(row.AssetId), row.Variant ?? ""), row.Quantity, 0)));

        var result = SimulationEngine.AdvanceTo(player, target, gameData, wallet,
            new DuelAdvanceOptions(MatchingEnabled: false));

        // 只回传推进中真正变更的分段；编码与校验和由共享 SectionCodec 单源产出。
        var writes = SectionCodec.EncodeAggregate(player, now);
        var output = new List<SectionPayload>(writes.Count);
        long outputBytes = 0;
        foreach (var write in writes)
        {
            outputBytes += write.Payload.Length;
            // 响应侧 checksum 针对编码后线字节：主服 ToSectionWrite 先验传输完整性再解压重算存储口径。
            output.Add(new SectionPayload(
                write.SectionKind,
                write.StateVersion,
                ToWireCodec(write.Codec),
                Convert.ToBase64String(write.Payload.Span),
                Convert.ToHexStringLower(SHA256.HashData(write.Payload.Span))));
        }

        var metrics = new ComputeAdvanceMetrics(
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (long)result.CompletedActions,
            output.Count,
            inputBytes,
            outputBytes);
        return new AdvanceJobResult(
            job.JobId,
            job.PlayerId,
            true,
            job.SourceStateVersion,
            result.StateVersion,
            output,
            MergeDeltas(result.AssetDeltas),
            metrics,
            null,
            null,
            false);
    }

    /// <summary>
    /// 解码并校验单个线 section 载荷：Base64 → 按线编码解压 → 原始字节校验和。
    /// 产出的 StoredSection 以 codec=json 承载原始字节（见 AdvanceCore 注释）。
    /// </summary>
    private static StoredSection DecodeSection(Guid playerId, SectionPayload section, DateTimeOffset now)
    {
        var encoded = Convert.FromBase64String(section.PayloadBase64);
        if (encoded.Length > ComputeContract.MaxPayloadBytesPerSection)
        {
            throw new InvalidDataException("section 载荷超过大小限制。");
        }

        var raw = SectionCodec.DecodeRaw(section.Codec switch
        {
            "identity" => SectionCodec.RawJson,
            "brotli" => SectionCodec.BrotliJson,
            _ => throw new NotSupportedException($"不支持 section 编码: {section.Codec}")
        }, encoded);
        if (raw.Length > ComputeContract.MaxPayloadBytesPerSection)
        {
            throw new InvalidDataException("解压后的 section 载荷超过大小限制。");
        }

        // 请求侧 checksum 针对解码后原始 JSON 字节（提交头绑定口径），与存储 checksum 语义一致。
        var checksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (!string.Equals(checksum, section.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("section 校验和不匹配。");
        }

        return new StoredSection(
            playerId, section.Kind, section.Version, SectionCodec.CurrentSchemaVersion,
            SectionCodec.RawJson, raw, raw.Length, section.Checksum, now);
    }

    /// <summary>存储编码到线编码的映射（与主服 ComputeSectionCodec.ToWire 镜像）。</summary>
    private static string ToWireCodec(string storageCodec) => storageCodec switch
    {
        SectionCodec.RawJson => "identity",
        SectionCodec.BrotliJson => "brotli",
        _ => throw new NotSupportedException($"存储编码不可外传：{storageCodec}。")
    };

    /// <summary>
    /// 按 (assetId, variant) 合并同键增量并过滤零值，Ordinal 排序保证输出稳定；
    /// 主服经济事务内部同样按键合并入账，此处先行收拢只减少线字节与流水行数。
    /// </summary>
    private static AssetDeltaRow[]? MergeDeltas(IReadOnlyList<Domain.Economy.AssetDelta> deltas)
    {
        if (deltas.Count == 0) return null;
        var merged = new Dictionary<(string AssetId, string Variant), double>();
        foreach (var delta in deltas)
        {
            var key = (delta.Asset.ItemId.Value, delta.Asset.Variant);
            merged[key] = merged.TryGetValue(key, out var sum) ? sum + delta.QuantityDelta : delta.QuantityDelta;
        }
        var rows = merged
            .Where(pair => pair.Value != 0 && double.IsFinite(pair.Value))
            .Select(pair => new AssetDeltaRow(pair.Key.AssetId, pair.Key.Variant, pair.Value))
            .OrderBy(row => row.AssetId, StringComparer.Ordinal)
            .ThenBy(row => row.Variant, StringComparer.Ordinal)
            .ToArray();
        return rows.Length == 0 ? null : rows;
    }
}
