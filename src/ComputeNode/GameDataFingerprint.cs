namespace DaoHua.ComputeNode;

/// <summary>只读游戏配置的确定性指纹。ComputeNode 不解析领域对象，只验证双方配置是否一致。</summary>
public sealed class GameDataFingerprint
{
    /// <summary>
    /// 根据配置目录或环境变量解析游戏数据路径，并计算配置指纹。
    /// </summary>
    /// <param name="environment">宿主环境。</param>
    /// <param name="configuration">配置对象。</param>
    public GameDataFingerprint(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["GameData:Directory"]
                         ?? Environment.GetEnvironmentVariable("DAOHUA_GAME_DATA_DIRECTORY");
        var directory = ResolveDirectory(environment.ContentRootPath, configured);
        Value = Compute(directory);
    }

    /// <summary>获取计算得到的 16 位游戏配置指纹字符串。</summary>
    public string Value { get; }

    /// <summary>
    /// 解析有效的游戏数据目录候选路径。
    /// </summary>
    /// <param name="contentRoot">应用程序内容根目录。</param>
    /// <param name="configured">显式配置的目录路径，可为 <see langword="null"/>。</param>
    /// <returns>存在的第一个候选目录，或回退到 <paramref name="contentRoot"/>。</returns>
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

    /// <summary>
    /// 对目标目录下所有 JSON 文件按稳定顺序求 SHA256 摘要，并截取前 16 位十六进制字符。
    /// </summary>
    /// <param name="directory">游戏数据目录。</param>
    /// <returns>16 位指纹字符串。</returns>
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
