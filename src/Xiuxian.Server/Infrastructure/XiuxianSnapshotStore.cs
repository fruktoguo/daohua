using System.Text.Json;
using YuoCore.Persistence;

namespace Xiuxian.Server.Infrastructure;

public sealed class XiuxianSnapshotStore : IYuoSnapshotStore
{
    private readonly string _rootDirectory;

    public XiuxianSnapshotStore(XiuxianStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _rootDirectory = paths.SnapshotDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public async ValueTask SaveAsync(YuoSnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        var safeKind = Sanitize(snapshot.Kind);
        var safeId = Sanitize(snapshot.Id);
        var target = Path.Combine(_rootDirectory, $"{safeKind}-{safeId}.json");
        var temporary = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        await File.WriteAllBytesAsync(temporary, snapshot.Payload.ToArray(), cancellationToken);
        File.Move(temporary, target, overwrite: true);
    }

    public async ValueTask<YuoSnapshotRecord?> LoadAsync(
        string kind,
        string id,
        CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(_rootDirectory, $"{Sanitize(kind)}-{Sanitize(id)}.json");
        if (!File.Exists(target)) return null;

        var payload = await File.ReadAllBytesAsync(target, cancellationToken);
        return new YuoSnapshotRecord(kind, id, payload, File.GetLastWriteTimeUtc(target));
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("快照标识不能为空。", nameof(value));
        return string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
    }
}

public static class XiuxianStateSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static byte[] Serialize(Xiuxian.Domain.Models.XiuxianPlayerState state)
    {
        return JsonSerializer.SerializeToUtf8Bytes(state, Options);
    }

    public static Xiuxian.Domain.Models.XiuxianPlayerState? Deserialize(ReadOnlyMemory<byte> payload)
    {
        return JsonSerializer.Deserialize<Xiuxian.Domain.Models.XiuxianPlayerState>(payload.Span, Options);
    }
}
