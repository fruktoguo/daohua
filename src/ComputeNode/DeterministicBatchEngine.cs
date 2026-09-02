namespace DaoHua.ComputeNode;

/// <summary>
/// 确定性离线挂机批量补算引擎。
/// 架构边界：
/// 1. 节点只运行确定性纯计算，无状态、不持有任何数据库读写权限；
/// 2. 接收来自主服务的已编码 Section 快照与离线时间跨度，推导生成推进后的 Section 载荷；
/// 3. 输出的新状态版本号与校验和必须能够被主服务的 CAS 机制无缝校验并落盘。
/// </summary>
public sealed class DeterministicBatchEngine(JsonSerializerOptions jsonOptions)
{
    /// <summary>
    /// 对离线任务批次执行确定性时间快进推演。
    /// </summary>
    public AdvanceJobResult Advance(AdvanceJobRequest job, string gameDataFingerprint)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var elapsed = job.TargetUnixMilliseconds - job.FromUnixMilliseconds;
            var completedActions = CountCompletedActions(job.Actions, elapsed);
            var output = new List<SectionPayload>(job.Sections.Count);
            long inputBytes = 0;
            long outputBytes = 0;

            foreach (var section in job.Sections)
            {
                var input = Decode(section);
                inputBytes += input.Length;
                var advanced = AdvanceJson(section.Kind, input, job, completedActions, gameDataFingerprint);
                var encoded = Encode(section.Codec, advanced);
                outputBytes += encoded.Length;
                output.Add(section with
                {
                    Version = checked(section.Version + 1),
                    PayloadBase64 = Convert.ToBase64String(encoded),
                    Checksum = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant()
                });
            }

            var metrics = new ComputeAdvanceMetrics(
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                completedActions,
                output.Count,
                inputBytes,
                outputBytes);
            return new AdvanceJobResult(
                job.JobId,
                job.PlayerId,
                true,
                job.SourceStateVersion,
                checked(job.SourceStateVersion + 1),
                output,
                metrics,
                null,
                null,
                false);
        }
        catch (FormatException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_PAYLOAD", "section Base64 载荷无效。", false);
        }
        catch (JsonException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_JSON_SECTION", "section JSON 载荷无效。", false);
        }
        catch (InvalidDataException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_CODEC", "section 压缩载荷无效。", false);
        }
        catch (OverflowException)
        {
            return AdvanceJobResult.Failed(job, "VERSION_OVERFLOW", "状态版本超出允许范围。", false);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return AdvanceJobResult.Failed(job, "INVALID_SECTION", exception.Message, false);
        }
    }

    private byte[] Decode(SectionPayload section)
    {
        var encoded = Convert.FromBase64String(section.PayloadBase64);
        if (encoded.Length > ComputeContract.MaxPayloadBytesPerSection)
        {
            throw new InvalidDataException("section 载荷超过大小限制。");
        }

        var decoded = section.Codec switch
        {
            "identity" => encoded,
            "brotli" => DecompressBrotli(encoded),
            _ => throw new NotSupportedException($"不支持 section 编码: {section.Codec}")
        };
        if (decoded.Length > ComputeContract.MaxPayloadBytesPerSection)
        {
            throw new InvalidDataException("解压后的 section 载荷超过大小限制。");
        }

        var checksum = Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(section.Checksum)
            && !string.Equals(checksum, section.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("section 校验和不匹配。");
        }

        return decoded;
    }

    private static byte[] Encode(string codec, byte[] payload) => codec switch
    {
        "identity" => payload,
        "brotli" => CompressBrotli(payload),
        _ => throw new NotSupportedException($"不支持 section 编码: {codec}")
    };

    private byte[] AdvanceJson(
        string kind,
        byte[] input,
        AdvanceJobRequest job,
        long completedActions,
        string gameDataFingerprint)
    {
        // 非 JSON 的领域二进制 section 原样传递；主服务可在提交前使用自己的 Domain 解码。
        if (input.Length == 0 || input[0] is not ((byte)'{' or (byte)'[')) return input;

        var node = JsonNode.Parse(
            input,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { MaxDepth = jsonOptions.MaxDepth });
        if (node is not JsonObject obj) return input;

        obj["lastSimulatedAtUnixMilliseconds"] = job.TargetUnixMilliseconds;
        obj["simulationElapsedMilliseconds"] = Math.Max(
            0,
            job.TargetUnixMilliseconds - job.FromUnixMilliseconds);
        obj["completedActions"] = completedActions;
        obj["gameDataFingerprint"] = gameDataFingerprint;
        obj["computeContractVersion"] = ComputeContract.Version;
        return JsonSerializer.SerializeToUtf8Bytes(node, jsonOptions);
    }

    private static long CountCompletedActions(IReadOnlyList<ActionIntent>? actions, long elapsedMilliseconds)
    {
        if (actions is null || actions.Count == 0 || elapsedMilliseconds <= 0) return 0;
        var elapsedSeconds = elapsedMilliseconds / 1000d;
        long total = 0;
        foreach (var action in actions)
        {
            if (action is null || action.DurationSeconds <= 0 || action.RepeatCount <= 0) continue;
            total = checked(total + Math.Min(
                action.RepeatCount,
                (long)Math.Floor(elapsedSeconds / action.DurationSeconds)));
        }

        return total;
    }

    private static byte[] DecompressBrotli(byte[] input)
    {
        using var source = new MemoryStream(input, writable: false);
        using var stream = new BrotliStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > ComputeContract.MaxPayloadBytesPerSection)
                    throw new InvalidDataException("解压后的 section 载荷超过大小限制。");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return output.ToArray();
    }

    private static byte[] CompressBrotli(byte[] input)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            stream.Write(input);
        }

        return output.ToArray();
    }
}
