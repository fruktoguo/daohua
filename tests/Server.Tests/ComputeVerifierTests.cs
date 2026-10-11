using DaoHua.ComputeNode;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Server;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class ComputeVerifierTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    [Fact]
    public async Task ForgedSectionIsRejectedByIndependentReplay()
    {
        var request = BuildRequest();
        using var engine = new DeterministicBatchEngine(Catalog);
        var candidate = await engine.AdvanceAsync(ToNodeRequest(request));
        Assert.True(candidate.Success);
        using var verifier = new EcsComputeResultVerifier(Catalog);
        Assert.True((await verifier.VerifyAsync(request, ToServerResult(candidate))).Accepted);
        var first = candidate.Sections![0];
        var raw = SectionCodec.DecodeRaw(first.Codec == "identity" ? SectionCodec.RawJson : SectionCodec.BrotliJson,
            Convert.FromBase64String(first.PayloadBase64));
        // 仍是合法 JSON，且重新计算正确 checksum；只有严格重演原始字节比较能拒绝这次自证伪造。
        var forgedRaw = raw.Concat(new byte[] { 32 }).ToArray();
        var forged = candidate with
        {
            Sections = candidate.Sections.Select((section, index) => index == 0
                ? section with
                {
                    Codec = "identity",
                    PayloadBase64 = Convert.ToBase64String(forgedRaw),
                    Checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(forgedRaw))
                }
                : section).ToArray()
        };
        var result = await verifier.VerifyAsync(request, ToServerResult(forged));
        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task DuplicateSectionAndAssetRowsAreRejected()
    {
        var request = BuildRequest();
        using var engine = new DeterministicBatchEngine(Catalog);
        var candidate = await engine.AdvanceAsync(ToNodeRequest(request));
        Assert.True(candidate.Success);
        var duplicate = candidate with
        {
            Sections = candidate.Sections!.Concat([candidate.Sections![0]]).ToArray()
        };
        using var verifier = new EcsComputeResultVerifier(Catalog);
        Assert.False((await verifier.VerifyAsync(request, ToServerResult(duplicate))).Accepted);

        var duplicateAsset = candidate with
        {
            AssetDeltas = [new AssetDeltaRow("item.ningxue-grass", "", 1), new AssetDeltaRow("item.ningxue-grass", "", 1)]
        };
        Assert.False((await verifier.VerifyAsync(request, ToServerResult(duplicateAsset))).Accepted);
    }

    [Fact]
    public async Task ForgedVersionClockAndRewardAreRejected()
    {
        var request = BuildRequest();
        using var engine = new DeterministicBatchEngine(Catalog);
        var candidate = ToServerResult(await engine.AdvanceAsync(ToNodeRequest(request)));
        using var verifier = new EcsComputeResultVerifier(Catalog);
        Assert.True((await verifier.VerifyAsync(request, candidate)).Accepted);
        Assert.False((await verifier.VerifyAsync(request, candidate with
        {
            TargetStateVersion = candidate.TargetStateVersion + 1
        })).Accepted);
        Assert.False((await verifier.VerifyAsync(request, candidate with
        {
            LastSimulatedAtUnixMilliseconds = candidate.LastSimulatedAtUnixMilliseconds + 1
        })).Accepted);
        Assert.False((await verifier.VerifyAsync(request, candidate with
        {
            AssetDeltas = [new ComputeAssetDelta("item.ningxue-grass", "", 999)]
        })).Accepted);
    }

    private static ComputeAdvanceJobRequest BuildRequest()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "验证者", Baseline, Catalog.Fingerprint);
        player.SetPresence(PresenceState.Offline, Baseline);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1), Baseline, Catalog.Actions);
        var writes = SectionCodec.EncodeAggregate(player, Baseline);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt), StringComparer.Ordinal);
        var head = new PlayerStateHead(playerId, player.StateVersion, SectionCodec.CurrentSchemaVersion, Baseline,
            Catalog.Fingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), Baseline);
        return new ComputeAdvanceJobRequest(
            Guid.NewGuid(), playerId, accountId, "验证者", Baseline.ToUnixTimeMilliseconds(), player.StateVersion,
            Baseline.ToUnixTimeMilliseconds(), Baseline.AddSeconds(30).ToUnixTimeMilliseconds(), head.Checksum,
            Catalog.Fingerprint, sections.Values.OrderBy(s => s.SectionKind, StringComparer.Ordinal).Select(ComputeSectionCodec.ToWire).ToArray(), []);
    }

    private static DaoHua.ComputeNode.AdvanceJobRequest ToNodeRequest(ComputeAdvanceJobRequest request) =>
        System.Text.Json.JsonSerializer.Deserialize<DaoHua.ComputeNode.AdvanceJobRequest>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, ServerJson.Options),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    private static ComputeAdvanceJobResult ToServerResult(DaoHua.ComputeNode.AdvanceJobResult result) =>
        System.Text.Json.JsonSerializer.Deserialize<ComputeAdvanceJobResult>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result), ServerJson.Options)!;
}
