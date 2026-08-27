using Domain.Player;
using Protocol;

namespace Server.Clean;

/// <summary>把领域运行时投影为稳定的 Protocol DTO；不把 PlayerAggregate 或数据库类型暴露给网络。</summary>
public static class ProtocolSnapshotMapper
{
    public static PlayerSnapshotDto ToSnapshot(PlayerRuntimeSnapshot runtime, string gameDataFingerprint)
    {
        var state = runtime.State;
        var sections = new List<PlayerSectionDto>(7);
        foreach (var (kind, value) in EnumerateSections(state))
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

    public static PlayerSnapshotDeltaDto ToDelta(PlayerRuntimeSnapshot runtime, string gameDataFingerprint)
    {
        var snapshot = ToSnapshot(runtime, gameDataFingerprint);
        return new PlayerSnapshotDeltaDto(snapshot.PlayerId, snapshot.StateVersion, snapshot.Sections);
    }

    private static IEnumerable<(string Kind, object Value)> EnumerateSections(PlayerAggregateState state)
    {
        yield return (PlayerSectionKinds.Progression, state.Progression);
        yield return (PlayerSectionKinds.World, state.World);
        yield return (PlayerSectionKinds.Combat, state.Combat);
        yield return (PlayerSectionKinds.Buffs, state.Buffs);
        yield return (PlayerSectionKinds.Loadout, new { state.EquippedInstances, state.EquippedItemIds, state.LearnedAbilities });
        yield return (PlayerSectionKinds.Settings, new { state.Settings, state.Presence });
        yield return (PlayerSectionKinds.ActionQueue, state.Actions);
    }
}
