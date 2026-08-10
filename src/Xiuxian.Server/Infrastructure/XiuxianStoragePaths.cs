namespace Xiuxian.Server.Infrastructure;

public sealed class XiuxianStoragePaths
{
    public XiuxianStoragePaths(IConfiguration configuration, IHostEnvironment environment)
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
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRoot, configuredPath));
    }
}
