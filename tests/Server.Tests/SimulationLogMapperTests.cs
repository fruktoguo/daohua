using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Server.Clean;
using Protocol;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 战报日志引用标记门禁：{{item:id|名称|数量}} 中的名称必须来自配表真实名称。
/// 锻造/强化链把装备以物品引用语法记入日志，名称解析漏掉装备表时界面会裸显
/// `equipment.*` 标识——本测试锁定该回退分支不再退化。
/// </summary>
public sealed class SimulationLogMapperTests
{
    [Fact]
    public void ActionCompleted_EquipmentDelta_UsesCatalogNameInsteadOfRawId()
    {
        var catalog = LoadCatalogFiles("equipment/weapons.json");
        var at = DateTimeOffset.UnixEpoch;
        var simulationEvent = new SimulationEvent(
            at,
            "action.completed",
            "打造 枯藤法杖",
            new ActionId("forge.kuteng-staff"),
            new Dictionary<ItemId, double> { [new ItemId("equipment.kuteng-staff")] = 1 });

        var buffer = new PlayerLogBuffer(16);
        var logs = SimulationLogMapper.ToLogEvents([simulationEvent], catalog, buffer);

        var template = Assert.Single(logs).Template;
        Assert.Contains("{{item:equipment.kuteng-staff|枯藤法杖|1}}", template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "强化成功：{{item:equipment.juling-pendant|聚灵佩}} 提升至 +1。")]
    [InlineData(false, "强化失败：{{item:equipment.juling-pendant|聚灵佩}} 回落至 +0。")]
    public void EnhancementRound_LogsNamedEquipmentReference(bool success, string expectedTemplate)
    {
        var catalog = LoadCatalogFiles("equipment/accessories.json", "actions/enhancement.json");
        var at = DateTimeOffset.UnixEpoch;
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "强化修士", at, catalog.Fingerprint);
        var equipmentId = new ItemId("equipment.juling-pendant");
        var equipmentKey = new AssetKey(equipmentId);
        var stoneKey = new AssetKey(new ItemId(EnhancementRules.SpiritStoneItemId));
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Credit(equipmentKey, 1, "test");
        wallet.Credit(stoneKey, 500, "test");
        var intent = new ActionIntent(new ActionId("enhance.equipment"), 1, TargetItemId: equipmentId.Value);
        var prep = EnhancementRules.Resolve(player.Loadout, intent,
            new Dictionary<AssetKey, double> { [equipmentKey] = 1, [stoneKey] = 500 },
            catalog, 0, out _, out _);
        Assert.NotNull(prep);
        var instanceId = Enumerable.Range(1, 100).Select(n => new Guid(n, 0, 0, new byte[8]))
            .First(id => (EnhancementRules.Roll(id, 0, prep) == EnhancementRules.Outcome.Success) == success);
        player.EnqueuePriority(intent, at, catalog.Actions, actionInstanceId: instanceId);

        var events = SimulationEngine.AdvanceTo(player, at.AddSeconds(7), catalog, wallet).Events;
        var logs = SimulationLogMapper.ToLogEvents(events, catalog, new PlayerLogBuffer(16));
        var log = Assert.Single(logs, log => log.Template.StartsWith("强化", StringComparison.Ordinal));
        Assert.Equal(LogChannels.System, log.Channel);
        Assert.Equal(expectedTemplate, log.Template);
    }

    [Fact]
    public void DuelStagedSettled_DoesNotEmitLogEvent()
    {
        Assert.Null(LogChannels.FromEventCode("duel.staged.settled"));

        var catalog = LoadCatalogFiles("equipment/weapons.json");
        var at = DateTimeOffset.UnixEpoch;
        var simulationEvent = new SimulationEvent(
            at,
            "duel.staged.settled",
            "暂存物品入账：枯藤法杖 ×1。",
            null,
            new Dictionary<ItemId, double> { [new ItemId("equipment.kuteng-staff")] = 1 });

        var buffer = new PlayerLogBuffer(16);
        var logs = SimulationLogMapper.ToLogEvents([simulationEvent], catalog, buffer);

        Assert.Empty(logs);
        Assert.Empty(buffer.SnapshotRecent());
    }

    /// <summary>
    /// 按配置相对路径过滤 Domain 程序集内嵌的游戏数据并走正式解析链构建目录
    /// （与 EnhancementContractTests 同一装载口径，保证读取的就是随镜像发布的配表）。
    /// </summary>
    private static GameDataCatalog LoadCatalogFiles(params string[] relativePaths)
    {
        var assembly = typeof(GameDataCatalog).Assembly;
        var documents = assembly.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '/'))
            .Zip(assembly.GetManifestResourceNames())
            .Where(pair => relativePaths.Any(path =>
                pair.First.EndsWith(path, StringComparison.OrdinalIgnoreCase)
                || pair.First.EndsWith(path.Replace('/', '.'), StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Second)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                var bytes = new byte[stream.Length];
                var read = stream.Read(bytes, 0, bytes.Length);
                return (ReadOnlyMemory<byte>)bytes.AsMemory(0, read);
            }).ToArray();
        Assert.True(documents.Length == relativePaths.Length,
            $"未找到全部内嵌配置：{string.Join(", ", relativePaths)}");
        return GameDataCatalog.Parse(documents);
    }
}
