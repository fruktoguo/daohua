using System.Text.Json;

namespace Daoxuan.Server.Infrastructure;

public static class DaoxuanStateSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static byte[] Serialize(Daoxuan.Domain.Models.DaoxuanPlayerState state)
    {
        return JsonSerializer.SerializeToUtf8Bytes(state, Options);
    }

    public static Daoxuan.Domain.Models.DaoxuanPlayerState? Deserialize(ReadOnlyMemory<byte> payload)
    {
        return JsonSerializer.Deserialize<Daoxuan.Domain.Models.DaoxuanPlayerState>(payload.Span, Options);
    }
}
