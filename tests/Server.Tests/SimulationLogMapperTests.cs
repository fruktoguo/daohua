using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Simulation;
using Server.Clean;
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
