namespace Daoxuan.Server.Infrastructure;

public sealed class DaoxuanStoragePaths
{
    public DaoxuanStoragePaths(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var configuredDataDirectory = configuration["Storage:DataDirectory"] ?? "data";
        DataDirectory = Resolve(environment.ContentRootPath, configuredDataDirectory);
        SnapshotDirectory = Path.Combine(DataDirectory, "snapshots");

        var configuredOutboxDirectory = configuration["Storage:OutboxDirectory"];
        OutboxDirectory = string.IsNullOrWhiteSpace(configuredOutboxDirectory)
            ? Path.Combine(DataDirectory, "outbox")
            : Resolve(environment.ContentRootPath, configuredOutboxDirectory);
    }

    public string DataDirectory { get; }
    public string SnapshotDirectory { get; }
    public string OutboxDirectory { get; }

    private static string Resolve(string contentRoot, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);
        var candidates = new[]
        {
            Path.Combine(contentRoot, configuredPath),
            Path.Combine(contentRoot, "../../", configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), configuredPath)
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
    }
}
