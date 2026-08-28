using Domain.Player;
using Domain.GameData;
using Protocol;

namespace Server.Clean;

/// <summary>把领域运行时投影为稳定的 Protocol DTO；不把 PlayerAggregate 或数据库类型暴露给网络。</summary>
public static class ProtocolSnapshotMapper
{
    public static PlayerSnapshotDto ToSnapshot(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog? gameData = null)
    {
        var state = runtime.State;
        var sections = new List<PlayerSectionDto>(7);
        foreach (var (kind, value) in EnumerateSections(state, gameData))
        {
            // WebSocket v1 的浏览器载荷保持 JSON；Brotli 只用于 PostgreSQL section，避免
            // 客户端依赖不一致的 Brotli WASM/浏览器实现。
            var encoded = SectionCodec.Encode(runtime.PlayerId, kind, Math.Max(1, runtime.StateVersion), value, runtime.LastSimulatedAt, compress: false);
            sections.Add(new PlayerSectionDto(
                kind,
                encoded.StateVersion,
                encoded.SchemaVersion,
                encoded.Codec,
                Convert.ToBase64String(encoded.Payload.ToArray()),
                encoded.RawBytes,
                encoded.StoredBytes,
                encoded.Checksum,
                encoded.UpdatedAt));
        }
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', sections
            .OrderBy(section => section.Section, StringComparer.Ordinal)
            .Select(section => $"{section.Section}:{section.SectionVersion}:{section.Checksum}"))))).ToLowerInvariant();
        var head = new PlayerHeadDto(
            runtime.PlayerId.ToString("D"),
            runtime.StateVersion,
            Domain.Player.PlayerStateHead.CurrentSchemaVersion,
            runtime.LastSimulatedAt,
            gameDataFingerprint,
            checksum,
            DateTimeOffset.UtcNow);
        var assets = runtime.Assets
            .Select(asset => new AssetBalanceDto(
                asset.AssetId,
                string.IsNullOrEmpty(asset.Variant) ? null : asset.Variant,
                asset.Quantity,
                asset.Version))
            .ToArray();
        return new PlayerSnapshotDto(
            runtime.PlayerId.ToString("D"),
            runtime.DisplayName,
            runtime.LastSimulatedAt,
            runtime.StateVersion,
            head,
            sections,
            assets);
    }

    public static PlayerSnapshotDeltaDto ToDelta(PlayerRuntimeSnapshot runtime, string gameDataFingerprint, GameDataCatalog? gameData = null, IReadOnlyList<AssetBalanceDto>? changedAssets = null)
    {
        var snapshot = ToSnapshot(runtime, gameDataFingerprint, gameData);
        return new PlayerSnapshotDeltaDto(snapshot.PlayerId, snapshot.StateVersion, snapshot.Sections, changedAssets);
    }

    /// <summary>对比上次推送基线，返回余额发生变化的品种当前余额；无基线时返回当前全量。</summary>
    public static IReadOnlyList<AssetBalanceDto> DiffAssets(IReadOnlyList<AssetBalanceDto>? previous, IReadOnlyList<AssetBalanceDto> current)
    {
        if (previous is null || previous.Count == 0) return current;
        var baseline = new Dictionary<(string AssetId, string? Variant), double>(previous.Count);
        foreach (var asset in previous) baseline[(asset.AssetId, asset.Variant)] = asset.Quantity;
        List<AssetBalanceDto>? changed = null;
        foreach (var asset in current)
        {
            if (baseline.TryGetValue((asset.AssetId, asset.Variant), out var quantity) && quantity == asset.Quantity) continue;
            (changed ??= []).Add(asset);
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
        var map = gameData?.Maps.FirstOrDefault(candidate =>
            candidate.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array
            && nodes.EnumerateArray().Any(node => ReadString(node, "id") == world.NodeId)) ?? default;
        var hasMap = map.ValueKind == JsonValueKind.Object;
        var node = hasMap && map.TryGetProperty("nodes", out var mapNodes)
            ? mapNodes.EnumerateArray().FirstOrDefault(candidate => ReadString(candidate, "id") == world.NodeId)
            : default;
        var hasNode = node.ValueKind == JsonValueKind.Object;
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

    private static object[] ReadEntities(JsonElement value, string property) => ReadArray(value, property).Select(entity => new
    {
        entityId = ReadString(entity, "id"),
        name = ReadString(entity, "name"),
        kind = ReadString(entity, "kind", "building"),
        description = ReadString(entity, "description"),
        effect = ReadString(entity, "effect")
    }).Cast<object>().ToArray();

    private static JsonElement[] ReadArray(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Array
        ? child.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
    private static string[] ReadStringArray(JsonElement value, string property) => ReadArray(value, property).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
    private static string ReadString(JsonElement value, string property, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? fallback : fallback;
    private static double ReadDouble(JsonElement value, string property, double fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : fallback;
    private static double? ReadDoubleNullable(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetDouble(out var parsed) ? parsed : null;
}
