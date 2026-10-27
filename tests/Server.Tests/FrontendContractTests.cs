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
        player.ReplaceCombatSlots([new("ability.heavy-slash", 1)], []);
        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal("ability.heavy-slash", restored.Loadout.AbilitySlots[0].AbilityId);
        Assert.Equal(0, restored.Loadout.CoreAttributes.Levels(catalog.RealmProgression).Stamina);
        // 开天门灵根状态必须随 progression 分段下发：缺这段投影会让客户端永远看不到抽取结果，
        // 表现为「点抽取灵根没反应」（数据已落库但界面收不到）。
        player.Progression.HeavenGate.Apply(
            new LinggenValues(53, 57, 95, 36, 0), new LinggenValues(53, 57, 95, 36, 0),
            entered: false, rerollCount: 2);
        var state = player.ExportState();
        Assert.Equal(new LinggenValues(53, 57, 95, 36, 0), state.Progression.HeavenGateRolled);
        Assert.Equal(2, state.Progression.HeavenGateRerollCount);
        Assert.False(state.Progression.HeavenGateEntered);
        var snapshot = new PlayerRuntimeSnapshot(player.PlayerId.Value, player.AccountId.Value.ToString(), player.DisplayName,
            player.StateVersion, player.LastSimulatedAt, state, 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(snapshot, catalog.Fingerprint, catalog);
        // 协议投影必须显式携带灵根四字段：只落库不下发会让前端永远停在「未抽取」。
        var progressionJson = JsonSerializer.Serialize(
            dto.Sections.Single(section => section.Section == "progression").Data, ProtocolJson.Options);
        using (var projected = JsonDocument.Parse(progressionJson))
        {
            var projectedRoot = projected.RootElement;
            Assert.True(projectedRoot.TryGetProperty("heavenGateRolled", out var rolledJson), "progression 投影缺少 heavenGateRolled。");
            Assert.Equal(53, rolledJson.GetProperty("metal").GetInt32());
            Assert.Equal(95, rolledJson.GetProperty("water").GetInt32());
            Assert.True(projectedRoot.TryGetProperty("heavenGateEntered", out var enteredJson));
            Assert.False(enteredJson.GetBoolean());
            Assert.Equal(2, projectedRoot.GetProperty("heavenGateRerollCount").GetInt32());
        }
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

    /// <summary>遭遇静态目录投影中的 playerRespawnMilliseconds 必须与领域默认值（180000ms）严格一致，防前端复活读条错位。</summary>
    [Fact]
    public void EncountersMapperHasAccuratePlayerRespawnMilliseconds()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var encounters = StaticGameCatalogMapper.Encounters(catalog);
        Assert.NotEmpty(encounters);
        foreach (var encounter in encounters)
        {
            var json = JsonSerializer.Serialize(encounter, ProtocolJson.Options);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("playerRespawnMilliseconds", out var msProp));
            Assert.Equal(180_000, msProp.GetInt32());
        }
    }

    /// <summary>物品静态目录投影中必须包含技艺加速与强化成功率加成，防止前端无法展示增益效果。</summary>
    [Fact]
    public void ItemsMapperIncludesTechniqueSpeedAndEnhancementBonus()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var items = StaticGameCatalogMapper.Items(catalog);
        Assert.NotEmpty(items);

        var json = JsonSerializer.Serialize(items, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement.EnumerateArray().ToArray();

        // 定神香验证：techniqueSpeedRatio 必须为 0.3
        var dingshen = array.Single(e => e.GetProperty("itemId").GetString() == "item.dingshen-incense");
        Assert.True(dingshen.TryGetProperty("techniqueSpeedRatio", out var dingshenSpeed));
        Assert.Equal(0.3, dingshenSpeed.GetDouble(), precision: 2);

        // 淬灵粉验证：enhancementSuccessBonus 必须为 0.03
        var cuiling = array.Single(e => e.GetProperty("itemId").GetString() == "item.cuiling-powder");
        Assert.True(cuiling.TryGetProperty("enhancementSuccessBonus", out var cuilingBonus));
        Assert.Equal(0.03, cuilingBonus.GetDouble(), precision: 3);
    }
}
