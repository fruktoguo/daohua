// ==============================================================================
// 文件名称: DaoHuaStateSerializer.cs
// 主要职责: 提供玩家完整领域状态模型与紧凑 UTF-8 JSON 字节数组之间的序列化与反序列化。
// 核心逻辑 / 定义内容:
//   1. 全局统一 Web 规范 JSON 序列化选项配置 (JsonSerializerOptions)。
//   2. 玩家领域状态 (DaoHuaPlayerState) 到 UTF-8 字节数组的高性能序列化。
//   3. 从快照二进制字节载荷安全反序列化重建玩家领域状态对象。
// ==============================================================================

using System.Text.Json;

namespace DaoHua.Server.Infrastructure;

/// <summary>
/// 玩家完整状态数据序列化器 (DaoHuaStateSerializer)。
/// <para>
/// 职责：
/// 1. 提供领域状态模型 <see cref="DaoHua.Domain.Models.DaoHuaPlayerState"/> 到紧凑 UTF-8 JSON 字节数组的高性能序列化。
/// 2. 负责从快照二进制载荷安全反序列化重建玩家领域状态。
/// </para>
/// </summary>
public static class DaoHuaStateSerializer
{
    /// <summary>
    /// 全局统一 Web 风格 JSON 序列化选项（非缩进、紧凑排版）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// 将玩家领域状态序列化为 UTF-8 编码的 JSON 字节数组。
    /// </summary>
    public static byte[] Serialize(DaoHua.Domain.Models.DaoHuaPlayerState state)
    {
        return JsonSerializer.SerializeToUtf8Bytes(state, Options);
    }

    /// <summary>
    /// 将快照 JSON 字节载荷反序列化为玩家领域状态模型。
    /// </summary>
    public static DaoHua.Domain.Models.DaoHuaPlayerState? Deserialize(ReadOnlyMemory<byte> payload)
    {
        return JsonSerializer.Deserialize<DaoHua.Domain.Models.DaoHuaPlayerState>(payload.Span, Options);
    }
}
