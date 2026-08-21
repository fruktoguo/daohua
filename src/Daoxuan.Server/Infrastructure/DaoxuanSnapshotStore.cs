using System.Text.Json;
using YuoCore.Persistence;

namespace Daoxuan.Server.Infrastructure;

public sealed class DaoxuanSnapshotStore : IYuoSnapshotStore
{
    private readonly string _rootDirectory;

    public DaoxuanSnapshotStore(DaoxuanStoragePaths paths)
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

        // 临时文件写入后必须 fsync 落盘再原子替换，否则主机断电时页缓存丢失会导致最近快照损坏。
        await using (var stream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await stream.WriteAsync(snapshot.Payload.ToArray(), cancellationToken);
            // 先刷新托管与 OS 缓冲，再强制 fsync；文件本体 fsync 失败必须向上抛出，不得吞掉。
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, target, overwrite: true);

        // 目录 fsync 为尽力而为，仅 POSIX 生效；文件本体 fsync 失败必须向上抛出，不得吞掉。
        try
        {
            await using var directory = new FileStream(
                _rootDirectory,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous);
            directory.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows 或权限受限环境无法对目录 fsync，忽略即可，不影响替换结果。
        }
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
