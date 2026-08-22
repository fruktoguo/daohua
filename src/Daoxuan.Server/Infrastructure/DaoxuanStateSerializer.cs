using System.Text.Json;

namespace Daoxuan.Server.Infrastructure;

/// <summary>
/// 玩家完整状态数据序列化器 (DaoxuanStateSerializer)。
/// <para>
/// 职责：
/// 1. 提供领域状态模型 <see cref="Daoxuan.Domain.Models.DaoxuanPlayerState"/> 到紧凑 UTF-8 JSON 字节数组的高性能序列化。
/// 2. 负责从快照二进制载荷安全反序列化重建玩家领域状态。
/// </para>
/// </summary>
public static class DaoxuanStateSerializer
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
    public static byte[] Serialize(Daoxuan.Domain.Models.DaoxuanPlayerState state)
    {
        return JsonSerializer.SerializeToUtf8Bytes(state, Options);
    }

    /// <summary>
    /// 将快照 JSON 字节载荷反序列化为玩家领域状态模型。
    /// </summary>
    public static Daoxuan.Domain.Models.DaoxuanPlayerState? Deserialize(ReadOnlyMemory<byte> payload)
    {
        return JsonSerializer.Deserialize<Daoxuan.Domain.Models.DaoxuanPlayerState>(payload.Span, Options);
    }
}
