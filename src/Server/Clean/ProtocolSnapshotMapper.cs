using Domain.Player;
using Domain.GameData;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 协议快照映射器：将内部领域运行时状态投影为公开稳定的 Protocol DTO。
/// 严禁把 ECS Entity、Fiber、数据库模型或未授权内部异常直接暴露给网络客户端。
/// </summary>
public static class ProtocolSnapshotMapper
{
    /// <summary>对单个分段载荷执行规范化 JSON 序列化并生成 PlayerSectionDto。</summary>
    public static PlayerSectionDto EncodeSection(Guid playerId, string kind, long stateVersion, object value, DateTimeOffset updatedAt)
    {
        var encoded = SectionCodec.Encode(playerId, kind, Math.Max(1, stateVersion), value, updatedAt, compress: false);
        return new PlayerSectionDto(
            kind,
            encoded.StateVersion,
            encoded.SchemaVersion,
            encoded.Codec,
            Convert.ToBase64String(encoded.Payload.Span),
            encoded.RawBytes,
            encoded.StoredBytes,
            encoded.Checksum,
            encoded.UpdatedAt);
    }

    /// <summary>
    /// 将玩家运行时快照（PlayerRuntimeSnapshot）转换为公开的全量快照 DTO。
    /// 自动结合激活的 GameDataCatalog 透传地图与节点动态生态。
    /// </summary>
    public static PlayerSnapshotDto ToSnapshot(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog? gameData = null)
    {
        var state = runtime.State;
        var sections = new List<PlayerSectionDto>(7);
        foreach (var (kind, value) in EnumerateSections(state, gameData))
        {
            // WebSocket v1 的浏览器载荷保持 JSON；Brotli 只用于 PostgreSQL section，避免
            // 客户端依赖不一致的 Brotli WASM/浏览器实现。
            sections.Add(EncodeSection(runtime.PlayerId, kind, runtime.StateVersion, value, runtime.LastSimulatedAt));
        }

        sections.Sort(static (a, b) => string.CompareOrdinal(a.Section, b.Section));
        var sb = new StringBuilder(sections.Count * 96);
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) sb.Append('|');
            var s = sections[i];
            sb.Append(s.Section).Append(':').Append(s.SectionVersion).Append(':').Append(s.Checksum);
        }
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));

        var head = new PlayerHeadDto(
            runtime.PlayerId.ToString("D"),
            runtime.StateVersion,
            Domain.Player.PlayerStateHead.CurrentSchemaVersion,
            runtime.LastSimulatedAt,
            gameDataFingerprint,
            checksum,
            DateTimeOffset.UtcNow);

        AssetBalanceDto[] assets;
        if (runtime.Assets.Count == 0)
        {
            assets = [];
        }
        else
        {
            assets = new AssetBalanceDto[runtime.Assets.Count];
            for (var i = 0; i < runtime.Assets.Count; i++)
            {
                var asset = runtime.Assets[i];
                assets[i] = new AssetBalanceDto(
                    asset.AssetId,
                    string.IsNullOrEmpty(asset.Variant) ? null : asset.Variant,
                    asset.Quantity,
                    asset.Version);
            }
        }

        return new PlayerSnapshotDto(
            runtime.PlayerId.ToString("D"),
            runtime.DisplayName,
            runtime.LastSimulatedAt,
            runtime.StateVersion,
            head,
            sections,
            assets);
    }

    /// <summary>构造增量快照 DTO（仅携带受影响的变更分段与资产变更）</summary>
    public static PlayerSnapshotDeltaDto ToDelta(
        PlayerRuntimeSnapshot runtime,
        string gameDataFingerprint,
        GameDataCatalog? gameData = null,
        IReadOnlyDictionary<string, string>? previousSectionChecksums = null,
        IReadOnlyList<AssetBalanceDto>? previousAssets = null)
    {
        var snapshot = ToSnapshot(runtime, gameDataFingerprint, gameData);
        var changedSections = DiffSections(previousSectionChecksums, snapshot.Sections);
        var changedAssets = DiffAssets(previousAssets, snapshot.Assets);
        return new PlayerSnapshotDeltaDto(
            snapshot.PlayerId,
            snapshot.StateVersion,
            changedSections,
            changedAssets.Count == 0 ? null : changedAssets);
    }

    /// <summary>构造增量快照 DTO（兼容旧调用签名）</summary>
    public static PlayerSnapshotDeltaDto ToDelta(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog? gameData = null, IReadOnlyList<AssetBalanceDto>? changedAssets = null)
    {
        var snapshot = ToSnapshot(runtime, gameDataFingerprint, gameData);
        return new PlayerSnapshotDeltaDto(snapshot.PlayerId, snapshot.StateVersion, snapshot.Sections, changedAssets);
    }

    /// <summary>对比上次推送基线，返回校验和发生变化的分段；无基线时返回全部当前分段。</summary>
    public static IReadOnlyList<PlayerSectionDto> DiffSections(IReadOnlyDictionary<string, string>? previousChecksums, IReadOnlyList<PlayerSectionDto> current)
    {
        if (previousChecksums is null || previousChecksums.Count == 0) return current;
        List<PlayerSectionDto>? changed = null;
        for (var i = 0; i < current.Count; i++)
        {
            var section = current[i];
            if (previousChecksums.TryGetValue(section.Section, out var prevChecksum) && string.Equals(prevChecksum, section.Checksum, StringComparison.Ordinal))
                continue;
            (changed ??= new List<PlayerSectionDto>(current.Count)).Add(section);
        }
        return changed ?? [];
    }

    /// <summary>对比上次推送基线，返回余额发生变化的品种当前余额；无基线时返回当前全量。</summary>
    public static IReadOnlyList<AssetBalanceDto> DiffAssets(IReadOnlyList<AssetBalanceDto>? previous, IReadOnlyList<AssetBalanceDto> current)
    {
        if (previous is null || previous.Count == 0) return current;
        var baseline = new Dictionary<(string AssetId, string? Variant), AssetBalanceDto>(previous.Count);
        foreach (var asset in previous) baseline[(asset.AssetId, asset.Variant)] = asset;
        var currentKeys = new HashSet<(string AssetId, string? Variant)>();
        List<AssetBalanceDto>? changed = null;
        foreach (var asset in current)
        {
            var key = (asset.AssetId, asset.Variant);
            currentKeys.Add(key);
            if (baseline.TryGetValue(key, out var prior) && prior.Quantity == asset.Quantity) continue;
            (changed ??= []).Add(asset);
        }
        // 余额归零后，领域钱包会移除该品种；显式发送零余额才能让浏览器删除旧库存项。
        foreach (var (key, prior) in baseline)
        {
            if (currentKeys.Contains(key)) continue;
            (changed ??= []).Add(prior with { Quantity = 0 });
        }
        return changed ?? [];
    }

    private static IEnumerable<(string Kind, object Value)> EnumerateSections(PlayerAggregateState state, GameDataCatalog? gameData)
    {
        yield return (PlayerSectionKinds.Progression, state.Progression);
        yield return (PlayerSectionKinds.World, ToWorldSection(state.World, gameData));
        yield return (PlayerSectionKinds.Combat, state.Combat);
        yield return (PlayerSectionKinds.Buffs, state.Buffs);
        yield return (PlayerSectionKinds.Loadout, new { state.EquippedInstances, state.EquippedItemIds, state.LearnedAbilities });
        yield return (PlayerSectionKinds.Settings, new { state.Settings, state.Presence });
        yield return (PlayerSectionKinds.ActionQueue, state.Actions);
    }

    private static object ToWorldSection(PlayerWorldStateDocument world, GameDataCatalog? gameData)
    {
        var hasMap = false;
        var hasNode = false;
        var map = default(JsonElement);
        var node = default(JsonElement);

        if (gameData?.Maps is not null)
        {
            for (var i = 0; i < gameData.Maps.Count; i++)
            {
                var candidate = gameData.Maps[i];
                if (candidate.ValueKind == JsonValueKind.Object
                    && candidate.TryGetProperty("nodes", out var candidateNodes)
                    && candidateNodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in candidateNodes.EnumerateArray())
                    {
                        if (ReadString(n, "id") == world.NodeId)
                        {
                            map = candidate;
                            hasMap = true;
                            node = n;
                            hasNode = true;
                            break;
                        }
                    }
                    if (hasNode) break;
                }
            }
        }

        var x = hasNode ? ReadDouble(node, "x", world.X) : world.X;
        var y = hasNode ? ReadDouble(node, "y", world.Y) : world.Y;
        return new
        {
            mapId = hasMap ? ReadString(map, "id", "map.qingyun-mountains") : "map.qingyun-mountains",
            nodeId = world.NodeId,
            edgeId = (string?)null,
            edgeProgress = 0d,
            x,
            y,
            locationName = hasNode ? ReadString(node, "name", world.NodeId) : world.NodeId,
            cultivationMultiplier = hasNode ? ReadDouble(node, "cultivationMultiplier", 1) : 1d,
            availableActionIds = ReadStringArray(node, "availableActionIds"),
            encounterIds = ReadStringArray(node, "encounterIds"),
            npcs = ReadEntities(node, "npcs"),
            buildings = ReadEntities(node, "buildings"),
            minBudget = ReadDoubleNullable(node, "minBudget"),
            maxBudget = ReadDoubleNullable(node, "maxBudget"),
            eliteInterval = ReadDoubleNullable(node, "eliteInterval"),
            bossInterval = ReadDoubleNullable(node, "bossInterval"),
            monsters = Array.Empty<object>(),
            eliteEnemies = Array.Empty<object>(),
            bossEnemies = Array.Empty<object>()
        };
    }

    private static object[] ReadEntities(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.Array)
            return [];
        var count = child.GetArrayLength();
        if (count == 0) return [];
        var result = new object[count];
        var i = 0;
        foreach (var entity in child.EnumerateArray())
        {
            result[i++] = new
            {
                entityId = ReadString(entity, "id"),
                name = ReadString(entity, "name"),
                kind = ReadString(entity, "kind", "building"),
                description = ReadString(entity, "description"),
                effect = ReadString(entity, "effect")
            };
        }
        return result;
    }

    private static string[] ReadStringArray(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.Array)
            return [];
        var count = child.GetArrayLength();
        if (count == 0) return [];
        var list = new List<string>(count);
        foreach (var item in child.EnumerateArray())
        {
            var str = item.GetString();
            if (!string.IsNullOrEmpty(str)) list.Add(str);
        }
        return list.ToArray();
    }

    private static string ReadString(JsonElement value, string property, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? fallback : fallback;
    private static double ReadDouble(JsonElement value, string property, double fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : fallback;
    private static double? ReadDoubleNullable(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : null;
}
