namespace DaoHua.ComputeNode;

/// <summary>只读游戏配置的确定性指纹。ComputeNode 不解析领域对象，只验证双方配置是否一致。</summary>
public sealed class GameDataFingerprint
{
    public GameDataFingerprint(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["GameData:Directory"]
                         ?? Environment.GetEnvironmentVariable("DAOHUA_GAME_DATA_DIRECTORY");
        var directory = ResolveDirectory(environment.ContentRootPath, configured);
        Value = Compute(directory);
    }

    public string Value { get; }

    private static string ResolveDirectory(string contentRoot, string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRoot, configured));
        }

        candidates.Add(Path.Combine(contentRoot, "config", "game-data"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "config", "game-data"));
        return candidates.FirstOrDefault(Directory.Exists) ?? contentRoot;
    }

    private static string Compute(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IEnumerable<string> files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        foreach (var file in files) hash.AppendData(File.ReadAllBytes(file));

        // gamedata.v1 约定使用稳定文件顺序和原始字节生成 16 位指纹，避免
        // 主服务与冷挂机节点因资源封装方式不同而误判配置不一致。
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }
}
