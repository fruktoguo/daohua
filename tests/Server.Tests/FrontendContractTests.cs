using System.Diagnostics;
using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>包含针对 Frontend Contract Tests 的测试用例与断言集合。</summary>
public sealed class FrontendContractTests
{
    /// <summary>验证 Real Mapper Snapshot Decodes In Frontend And Loadout Restores。</summary>
    /// <returns>表示异步操作的任务。</returns>
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
            player.StateVersion, player.LastSimulatedAt, state, [], []);
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
