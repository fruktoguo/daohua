// ==============================================================================
// 文件名称: ProtocolTests.cs
// 主要职责: 纯数据协议层（Protocol）编解码与状态差分同步契约单元测试。
// 核心逻辑 / 定义内容:
//   1. 验证 PacketCodec 对命令包、事件包与快照包的序列化与反序列化往返一致性。
//   2. 验证状态差分增量（Delta）只传输脏分区字段，保持网络网络传输体积极简。
//   3. 验证市场盘口 DTO、排行榜 DTO 及 WebSocket 通信数据结构的字段映射正确性。
// ==============================================================================

using Protocol;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 验证 Protocol（纯数据协议层）的编解码（PacketCodec）、增量差分补丁（Delta/Patch）格式及前后端通信契约。
/// </summary>
public sealed class ProtocolTests
{
    /// <summary>
    /// 验证命令数据包编解码的往返一致性（Round-Trip），确保压测与生产客户端命令在序列化后能无损反序列化。
    /// </summary>
    [Fact]
    public void Production_packet_codec_round_trips_load_test_command()
    {
        var bytes = PacketCodec.EncodeCommand(
            "request-7",
            "enqueue_action",
            new EnqueueActionRequest("explore.gather-herbs", 3));

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        Assert.Equal(PacketKinds.Command, packet.Kind);
        Assert.Equal("request-7", packet.RequestId);
        Assert.Equal("enqueue_action", packet.Payload.GetProperty("command").GetString());
        Assert.Equal(3, packet.Payload.GetProperty("payload").GetProperty("repeats").GetInt32());
    }

    /// <summary>
    /// 验证状态差分增量（Delta）只序列化已标记脏的字段，未变更的分区（如 inventory, buffs）在 JSON 输出中被完全剔除。
    /// </summary>
    [Fact]
    public void Delta_packet_omits_unchanged_sections()
    {
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            12,
            1_786_000_000,
            Queue: new QueueDto([], true, new Dictionary<string, IReadOnlyList<AutoUseSlotDto>>()));

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 9, patch);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"queue\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inventory\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"buffs\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证高频进度差分只下发当前执行项的 progressSeconds，避免每次跳字均全量下发整条队列结构。
    /// </summary>
    [Fact]
    public void Progress_delta_contains_only_current_queue_entry()
    {
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            13,
            1_786_000_001,
            QueueProgress: new QueueProgressDto(
                Guid.NewGuid().ToString("D"),
                "running",
                4,
                8));

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 10, patch);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"queueProgress\"", json, StringComparison.Ordinal);
        Assert.Contains("\"progressSeconds\":4", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"queue\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inventory\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证战斗资源增量独立下发当前生命值与法力值，避免附带冗余的静态战斗属性负载。
    /// </summary>
    [Fact]
    public void Combat_resource_delta_serializes_persistent_current_values()
    {
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            14,
            1_786_000_002,
            CombatResources: new CombatResourceDto(42, 110, 31, 110));

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 11, patch);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"combatResources\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hitpoints\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"manapoints\":31", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"combat\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证结构化日志批量增量（eventBatch）正确携带序号、时间戳与模板，保持单调递增并支持模板字符串解析。
    /// </summary>
    [Fact]
    public void Event_delta_serializes_incremental_structured_batch()
    {
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            14,
            1_786_000_002,
            EventBatch:
            [
                new LogEventDto(
                    41,
                    1_786_000_002_125,
                    "获得 {{item:item.spirit-herb|灵草}} × 3")
            ]);

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 11, patch);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"eventBatch\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sequence\":41", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"events\"", json, StringComparison.Ordinal);
        Assert.True(PacketCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(
            "获得 {{item:item.spirit-herb|灵草}} × 3",
            decoded.Payload.GetProperty("eventBatch")[0].GetProperty("template").GetString());
    }

    /// <summary>
    /// 验证行动与战斗配置 DTO 结构符合传输契约，可在快照与增量中透明互通。
    /// </summary>
    [Fact]
    public void Combat_action_and_loadout_fields_are_wire_compatible()
    {
        var action = new ActionDto(
            "combat.hunt-qi-beast",
            "历练聚气兽",
            "在灵脉外围迎战聚气兽。",
            "combat",
            60,
            "combat-encounter",
            "encounter.qi-beast");
        var bytes = PacketCodec.Encode(PacketKinds.Snapshot, string.Empty, 1, new
        {
            action,
            combat = new CombatLoadoutDto(
                "sword",
                "physical",
                1,
                1,
                1,
                1,
                1,
                1,
                [new CombatAbilitySlotDto("ability.five-elements-flare", 1)],
                [])
        });

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        Assert.Equal("combat", packet.Payload.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal("combat-encounter", packet.Payload.GetProperty("action").GetProperty("executionKind").GetString());
    }

    /// <summary>
    /// 验证战斗动作增量包含服务端时间与动作绝对截止毫秒时间戳，保障前端施法进度条插值平滑且与服务端同步。
    /// </summary>
    [Fact]
    public void Combat_action_packet_contains_absolute_millisecond_deadline()
    {
        const long serverTime = 1_786_000_000_125;
        const long completesAt = 1_786_000_001_875;
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            15,
            serverTime / 1_000,
            QueueProgress: new QueueProgressDto(
                Guid.NewGuid().ToString("D"),
                "running",
                0,
                0,
                new ExpeditionProgressDto(
                    "combat",
                    0,
                    0,
                    0,
                    PlayerAction: new CombatActionStateDto(
                        "五行炽焰",
                        "cast",
                        250,
                        2_000,
                        completesAt),
                    ConsumableCooldownMilliseconds: [59_000, 0, 0])),
            ServerTimeUnixMilliseconds: serverTime);

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 11, patch);

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        Assert.Equal(serverTime, packet.Payload.GetProperty("serverTimeUnixMilliseconds").GetInt64());
        Assert.Equal(
            completesAt,
            packet.Payload
                .GetProperty("queueProgress")
                .GetProperty("expedition")
                .GetProperty("playerAction")
                .GetProperty("completesAtUnixMilliseconds")
                .GetInt64());
        Assert.Equal(
            59_000,
            packet.Payload
                .GetProperty("queueProgress")
                .GetProperty("expedition")
                .GetProperty("consumableCooldownMilliseconds")[0]
                .GetInt64());
    }

    /// <summary>
    /// 验证静态数据目录包（Catalog Snapshot）包含权威的行动输入输出消耗及物品可交易/可用性元数据。
    /// </summary>
    [Fact]
    public void Catalog_packets_include_authoritative_action_rules_and_item_metadata()
    {
        var action = new ActionDto(
            "alchemy.refine-qi-pill",
            "炼制引气丹",
            "以灵草温养一枚引气丹。",
            "alchemy",
            12,
            Inputs: new Dictionary<string, long> { ["item.spirit-herb"] = 3 },
            Outputs: new Dictionary<string, long> { ["item.qi-gathering-pill"] = 1 });
        var item = new ItemDefinitionDto(
            "item.qi-gathering-pill",
            "引气丹",
            "修炼时自动服用。",
            "consumable",
            "丹药",
            1,
            18,
            true,
            ["cultivation"]);

        var bytes = PacketCodec.Encode(PacketKinds.Snapshot, string.Empty, 2, new { action, item });

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        Assert.Equal(3, packet.Payload.GetProperty("action").GetProperty("inputs").GetProperty("item.spirit-herb").GetInt64());
        Assert.Equal(1, packet.Payload.GetProperty("action").GetProperty("outputs").GetProperty("item.qi-gathering-pill").GetInt64());
        Assert.True(packet.Payload.GetProperty("item").GetProperty("isTradable").GetBoolean());
        Assert.Equal("cultivation", packet.Payload.GetProperty("item").GetProperty("usableActionTypes")[0].GetString());
    }

    /// <summary>
    /// 验证大地图移动指令（travel_to）的目的地节点 ID 编解码正确。
    /// </summary>
    [Fact]
    public void Travel_command_round_trips_destination_node()
    {
        var bytes = PacketCodec.EncodeCommand(
            "travel-1",
            "travel_to",
            new TravelToRequest("node.twilight-pass"));

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        Assert.Equal("travel_to", packet.Payload.GetProperty("command").GetString());
        Assert.Equal(
            "node.twilight-pass",
            packet.Payload.GetProperty("payload").GetProperty("destinationNodeId").GetString());
    }

    /// <summary>
    /// 验证大地图路段中途移动状态的权威下发，包含当前路段 ID、行驶进度（0.0 ~ 1.0）且不附带离散节点标识。
    /// </summary>
    [Fact]
    public void World_delta_contains_authoritative_mid_edge_position()
    {
        var world = new WorldStateDto(
            "map.qingyun-mountains",
            null,
            "edge.herb-spring",
            0.5,
            31,
            60,
            "青岚药谷 → 澄心灵泉",
            1,
            [],
            [],
            [],
            []);
        var patch = new PlayerPatchDto(
            Guid.NewGuid().ToString("D"),
            17,
            1_786_000_003,
            World: world);

        var bytes = PacketCodec.Encode(PacketKinds.Delta, string.Empty, 12, patch);

        Assert.True(PacketCodec.TryDecode(bytes, out var packet));
        var serializedWorld = packet.Payload.GetProperty("world");
        Assert.Equal("edge.herb-spring", serializedWorld.GetProperty("edgeId").GetString());
        Assert.Equal(0.5, serializedWorld.GetProperty("edgeProgress").GetDouble());
        Assert.False(serializedWorld.TryGetProperty("nodeId", out _));
        Assert.False(packet.Payload.TryGetProperty("queue", out _));
    }
}
