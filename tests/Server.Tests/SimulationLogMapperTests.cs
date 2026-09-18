using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Simulation;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 模拟日志映射门禁：事件→带实体引用占位符的模板消息、未知事件码回退原文、
/// 日志缓冲区容量截断、相同事件合并计数，以及大批量事件下的内存分配上限。
/// </summary>
public sealed class SimulationLogMapperTests
{
    /// <summary>带产出的完成事件映射为含 {{action}}/{{item}} 引用占位符的模板，并写入日志缓冲区。</summary>
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
    /// <summary>无产出的完成事件不拼奖励段；未注册的事件码回退为原始消息文本。</summary>

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
    /// <summary>日志批次 DTO 以 camelCase 序列化且往返一致（协议字段命名门禁）。</summary>

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
    /// <summary>10 万事件只物化最新 200 条，分配低于 200KB（容量水位门禁，防止大批量补算打爆内存）。</summary>

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
    /// <summary>相同事件合并为单条且 Count 累加，只占一个缓冲槽位。</summary>

    [Fact]
    public void ToLogEvents_200IdenticalEvents_CollapsesToSingleEntryWithCountAndOccupiesSingleBufferSlot()
    {
        var gameData = BuildCatalog();
        var buffer = new PlayerLogBuffer(200);
        var baseTime = DateTimeOffset.UtcNow;

        // 构造 200 个完全相同的采集事件
        var events = new List<SimulationEvent>(200);
        for (var i = 0; i < 200; i++)
        {
            events.Add(new SimulationEvent(
                baseTime.AddSeconds(i),
                "action.completed",
                "采集灵草",
                new ActionId("gather.spirit-grass"),
                new Dictionary<ItemId, double> { [new ItemId("item.spirit-grass")] = 1 }));
        }

        var logEvents = SimulationLogMapper.ToLogEvents(events, gameData, buffer, bufferCapacity: 200);

        // 断言：200 条相同事件合并为 1 条，Count 为 200
        Assert.Single(logEvents);
        Assert.Equal(200, logEvents[0].Count);

        // 关键断言：在最大历史记录为 200 的缓冲区中，200 条合并后依然只占 1 条！
        var snapshot = buffer.SnapshotRecent();
        Assert.Single(snapshot);
        Assert.Equal(200, snapshot[0].Count);
    }
    /// <summary>跨批次追加的相同日志同样合并占单槽位，此前历史条目不受影响。</summary>

    [Fact]
    public void PlayerLogBuffer_IdenticalEventsAcrossBatches_OccupiesSingleSlotAndPreservesPreviousHistory()
    {
        var buffer = new PlayerLogBuffer(200);

        // 1. 先写入 10 条不同的历史事件
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(new LogEventDto(buffer.NextSequence(), 1000 + i, $"历史日志_{i}"));
        }
        Assert.Equal(10, buffer.SnapshotRecent().Count);

        // 2. 连续追加 200 条相同的采集日志
        for (var i = 0; i < 200; i++)
        {
            buffer.Append(new LogEventDto(buffer.NextSequence(), 2000 + i, "获得伴灵草", 1));
        }

        // 3. 断言：这 200 条相同日志合并后只占 1 条，之前的 10 条历史日志完好保留，总计 11 条！
        var snapshot = buffer.SnapshotRecent();
        Assert.Equal(11, snapshot.Count);
        Assert.Equal("历史日志_0", snapshot[0].Template);
        Assert.Equal("获得伴灵草", snapshot[10].Template);
        Assert.Equal(200, snapshot[10].Count);
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
