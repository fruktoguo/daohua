using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Simulation;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class SimulationLogMapperTests
{
    [Fact]
    public void ActionCompletedWithOutputs_MapsToTemplateWithItemReferences()
    {
        var gameData = BuildCatalog();
        var buffer = new PlayerLogBuffer(200);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_724_800_000_000);
        var simulationEvent = new SimulationEvent(
            at,
            "action.completed",
            "采集灵草",
            new ActionId("gather.spirit-grass"),
            new Dictionary<ItemId, double> { [new ItemId("item.spirit-grass")] = 2.5 });

        var events = SimulationLogMapper.ToLogEvents([simulationEvent], gameData, buffer);

        Assert.Single(events);
        Assert.Equal(1, events[0].Sequence);
        Assert.Equal(1_724_800_000_000, events[0].TimestampUnixMilliseconds);
        Assert.Equal("完成行动 {{action:gather.spirit-grass|采集灵草}}，获得 {{item:item.spirit-grass|灵草|2.5}}", events[0].Template);
        Assert.Single(buffer.SnapshotRecent());
    }

    [Fact]
    public void ActionCompletedWithoutOutputs_OmitsRewardSectionAndUnknownCodeFallsBackToMessage()
    {
        var gameData = BuildCatalog();
        var buffer = new PlayerLogBuffer(200);
        var at = DateTimeOffset.UtcNow;
        var completed = new SimulationEvent(at, "action.completed", "吐纳炼气", new ActionId("cultivation.draw-qi"));
        var custom = new SimulationEvent(at, "custom.code", "自定义事件文本");

        var events = SimulationLogMapper.ToLogEvents([completed, custom], gameData, buffer);

        Assert.Equal(2, events.Count);
        Assert.Equal("完成行动 {{action:cultivation.draw-qi|吐纳炼气}}", events[0].Template);
        Assert.Equal("自定义事件文本", events[1].Template);
        Assert.Equal(2, events[1].Sequence);
    }

    [Fact]
    public void LogBatchDto_SerializesCamelCaseAndRoundTrips()
    {
        var payload = new LogBatchDto([new LogEventDto(3, 1_724_800_000_000, "完成行动 {{action:a|行动}}")]);

        var json = JsonSerializer.Serialize(payload, ProtocolJson.Options);

        // System.Text.Json 默认转义非 ASCII 字符，中文字面量断言必须基于反序列化回读而非原始文本。
        Assert.Contains("\"sequence\":3", json);
        Assert.Contains("\"timestampUnixMilliseconds\":1724800000000", json);
        var decoded = JsonSerializer.Deserialize<LogBatchDto>(json, ProtocolJson.Options);
        Assert.NotNull(decoded);
        Assert.Single(decoded!.Events);
        Assert.Equal(3, decoded.Events[0].Sequence);
        Assert.Equal("完成行动 {{action:a|行动}}", decoded.Events[0].Template);
    }

    [Fact]
    public void SimulationLogMapper_100kEvents_TruncatesToBufferCapacityAndAvoidsMemoryBlowup()
    {
        var gameData = BuildCatalog();
        var buffer = new PlayerLogBuffer(200);
        var baseTime = DateTimeOffset.UtcNow;

        // 构造 100,000 个仿真事件
        const int eventCount = 100_000;
        var events = new List<SimulationEvent>(eventCount);
        for (var i = 0; i < eventCount; i++)
        {
            events.Add(new SimulationEvent(
                baseTime.AddSeconds(i),
                "action.completed",
                $"吐纳_{i}",
                new ActionId("cultivation.draw-qi")));
        }

        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var logEvents = SimulationLogMapper.ToLogEvents(events, gameData, buffer, bufferCapacity: 200);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

        // 断言：只格式化并返回最新的 200 条日志
        Assert.Equal(200, logEvents.Count);
        Assert.Equal(200, buffer.SnapshotRecent().Count);

        // 验证序号与最新事件对应
        Assert.Equal(1, logEvents[0].Sequence);
        Assert.Equal(200, logEvents[199].Sequence);
        Assert.Equal("完成行动 {{action:cultivation.draw-qi|吐纳_99999}}", logEvents[199].Template);

        // 验证内存分配在极小范围内（仅 200 个 DTO 和字符串，分配小于 200KB）
        Assert.True(allocated < 200 * 1024, $"100k 事件处理分配内存过大: {allocated} bytes");
    }

    private static GameDataCatalog BuildCatalog()
    {
        var rawItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["item.spirit-grass"] = JsonSerializer.Deserialize<JsonElement>("{\"id\":\"item.spirit-grass\",\"name\":\"灵草\"}")
        };
        return new GameDataCatalog("test-1", "test-fingerprint", ActionCatalog.Default, rawItems: rawItems);
    }
}
