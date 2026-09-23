using System.Diagnostics;
using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 前后端协议契约门禁：服务端真实产出的快照 DTO 落盘后由
/// scripts/check-frontend-contracts.mjs 以 Node 侧逻辑解码验证，防止单边协议漂移导致断连。
/// </summary>
public sealed class FrontendContractTests
{
    /// <summary>真实配表产出的快照 JSON 必须能被前端契约脚本成功解码；习得技能经快照往返不丢失。</summary>
    [Fact]
    public async Task RealMapperSnapshotDecodesInFrontendAndLoadoutRestores()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts/check-frontend-contracts.mjs"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
        var catalog = GameDataCatalog.Parse(Directory.EnumerateFiles(Path.Combine(root, "config/game-data"), "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path)));
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "协议契约", DateTimeOffset.UtcNow, catalog.Fingerprint);
        player.LearnAbility(new AbilityId("ability.heavy-slash"));
        player.ReplaceCombatSlots([new("ability.heavy-slash", 1, [])], []);
        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal("ability.heavy-slash", restored.Loadout.AbilitySlots[0].AbilityId);
        Assert.Equal(10, restored.Loadout.CoreAttributes.StaminaLevel);
        var state = restored.ExportState();
        var snapshot = new PlayerRuntimeSnapshot(player.PlayerId.Value, player.AccountId.Value.ToString(), player.DisplayName,
            player.StateVersion, player.LastSimulatedAt, state, 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(snapshot, catalog.Fingerprint, catalog);
        var fixture = Path.Combine(Path.GetTempPath(), $"daohua-frontend-contract-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(dto, ProtocolJson.Options));
            using var process = new Process { StartInfo = new("node") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true } };
            process.StartInfo.ArgumentList.Add("scripts/check-frontend-contracts.mjs");
            process.StartInfo.ArgumentList.Add(fixture);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally { File.Delete(fixture); }
    }
}
