using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("用法: dotnet run --project src/DataTool -- validate <游戏数据目录> [--strict]");
    return 2;
}

var command = args[0].Trim().ToLowerInvariant();
if (command is not "validate" and not "fingerprint")
{
    Console.Error.WriteLine($"未知命令: {args[0]}。新基线只提供 validate/fingerprint，不生成旧默认配置。");
    return 2;
}

var directory = Path.GetFullPath(args[1]);
var strict = args.Length == 3 && string.Equals(args[2], "--strict", StringComparison.Ordinal);
if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"游戏数据目录不存在: {directory}");
    return 1;
}

try
{
    var result = Validate(directory, strict);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}
catch (DataValidationException exception)
{
    Console.Error.WriteLine($"游戏数据校验失败: {exception.Message}");
    return 1;
}

static ValidationResult Validate(string directory, bool strict)
{
    var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
        .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) throw new DataValidationException("目录中没有 JSON 配置文件。");

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var objectCount = 0L;
    var arrayCount = 0L;
    var idCount = 0L;
    using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
        var bytes = File.ReadAllBytes(file);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object) objectCount++;
            if (document.RootElement.ValueKind == JsonValueKind.Array) arrayCount++;
            foreach (var element in EnumerateObjects(document.RootElement))
            {
                if (!element.TryGetProperty("id", out var idElement)) continue;
                if (idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString()))
                    throw new DataValidationException($"{relative} 中 id 必须是非空字符串。");
                var id = idElement.GetString()!;
                if (!ids.Add(id)) throw new DataValidationException($"重复配置 ID: {id}（文件 {relative}）。");
                idCount++;
            }
        }
        catch (JsonException exception)
        {
            throw new DataValidationException($"{relative} JSON 无效: {exception.Message}");
        }

        fingerprint.AppendData(bytes);
    }

    var manifestPath = Path.Combine(directory, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        if (strict) throw new DataValidationException("缺少 manifest.json。");
    }
    else
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (manifest.RootElement.ValueKind != JsonValueKind.Object)
            throw new DataValidationException("manifest.json 根节点必须是对象。");
    }

    return new ValidationResult(
        "gamedata.v1",
        Path.GetFileName(directory),
        files.Length,
        objectCount,
        arrayCount,
        idCount,
        Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant()[..16],
        strict,
        DateTimeOffset.UtcNow);
}

static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        yield return element;
        foreach (var property in element.EnumerateObject())
        {
            foreach (var child in EnumerateObjects(property.Value)) yield return child;
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var child in element.EnumerateArray())
        {
            foreach (var nested in EnumerateObjects(child)) yield return nested;
        }
    }
}

file sealed record ValidationResult(
    string ContractVersion,
    string Directory,
    int Files,
    long ObjectCount,
    long ArrayCount,
    long IdCount,
    string Fingerprint,
    bool Strict,
    DateTimeOffset ValidatedAt);

file sealed class DataValidationException(string message) : Exception(message);
