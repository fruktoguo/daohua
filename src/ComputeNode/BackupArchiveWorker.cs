using System.Text.RegularExpressions;

namespace DaoHua.ComputeNode;

/// <summary>
/// 计算节点内置备份归档状态单例（供 /healthz 与监控观测）。
/// </summary>
public sealed class BackupArchiveState
{
    public bool Enabled { get; init; }
    public string? LocalDirectory { get; init; }
    public DateTimeOffset? LastSyncStartedAt { get; internal set; }
    public DateTimeOffset? LastSyncCompletedAt { get; internal set; }
    public int VerifiedCount { get; internal set; }
    public int FailedCount { get; internal set; }
    public string? LastError { get; internal set; }
}

/// <summary>
/// 计算节点内置备份归档配置参数。
/// </summary>
public sealed record BackupArchiveOptions(
    string Directory,
    string BaseUrl,
    string Token,
    int IntervalMinutes = 60,
    int RetentionDays = 90);

/// <summary>
/// 计算节点内置备份归档后台托管服务：
/// 当节点配置了本地存储目录时自适应激活，在后台定时通过安全凭据通道拉取主服数据库备份并对账落盘，
/// 彻底免除在新节点上手动配置 Shell 脚本、cron 或 systemd timer 的运维负担。
/// </summary>
public sealed class BackupArchiveWorker : BackgroundService
{
    private static readonly Regex FilePattern = new(@"daohua-postgres-[a-zA-Z0-9._-]+", RegexOptions.Compiled);
    private static readonly string[] Categories = ["hourly", "daily", "weekly", "base"];

    private readonly BackupArchiveOptions _options;
    private readonly BackupArchiveState _state;
    private readonly ILogger<BackupArchiveWorker> _logger;
    private readonly HttpClient _httpClient;

    public BackupArchiveWorker(
        BackupArchiveOptions options,
        BackupArchiveState state,
        ILogger<BackupArchiveWorker> logger,
        HttpClient? httpClient = null)
    {
        _options = options;
        _state = state;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("计算节点内置备份归档服务已就绪 localDir={Dir} baseUrl={Url} interval={Interval}m",
            _options.Directory, _options.BaseUrl, _options.IntervalMinutes);

        // 启动延迟 15 秒，避开计算节点上游握手热路径
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes)));
        do
        {
            try
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.LastError = ex.Message;
                _logger.LogError(ex, "内置备份归档同步失败，将在下个周期重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        _state.LastSyncStartedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("开始执行备份归档增量拉取与对账...");

        Directory.CreateDirectory(_options.Directory);
        foreach (var cat in Categories)
        {
            Directory.CreateDirectory(Path.Combine(_options.Directory, cat));
        }

        foreach (var category in Categories)
        {
            await SyncCategoryAsync(category, cancellationToken).ConfigureAwait(false);
        }

        // 全量哈希比对
        var (verified, failed) = await VerifyAllHashesAsync(cancellationToken).ConfigureAwait(false);
        _state.VerifiedCount = verified;
        _state.FailedCount = failed;

        // 老化清理本地旧 hourly 备份（默认 90 天）
        PruneOldHourlyArchives();

        _state.LastSyncCompletedAt = DateTimeOffset.UtcNow;
        _state.LastError = failed > 0 ? $"存在 {failed} 个文件哈希校验失败" : null;

        _logger.LogInformation("备份归档同步与对账完成: verified={Verified} failed={Failed}", verified, failed);
    }

    private async Task SyncCategoryAsync(string category, CancellationToken cancellationToken)
    {
        var categoryDir = Path.Combine(_options.Directory, category);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/{category}/";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Backup-Token", _options.Token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("无法连接主服备份目录 url={Url} error={Error}", url, ex.Message);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("获取主服备份目录失败 status={Status} url={Url}", response.StatusCode, url);
            return;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var matches = FilePattern.Matches(html);
        var filenames = matches.Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var filename in filenames)
        {
            var localPath = Path.Combine(categoryDir, filename);
            var fileUrl = $"{_options.BaseUrl.TrimEnd('/')}/{category}/{filename}";

            // 若已有本地文件和 sha256 校验文件且哈希比对一致，跳过下载
            if (!filename.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                && File.Exists(localPath)
                && File.Exists(localPath + ".sha256"))
            {
                if (CheckHash(localPath, localPath + ".sha256"))
                {
                    continue;
                }
            }

            try
            {
                await DownloadFileAsync(fileUrl, localPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "下载备份文件失败: {File}", filename);
            }
        }
    }

    private async Task DownloadFileAsync(string fileUrl, string localPath, CancellationToken cancellationToken)
    {
        var tempPath = localPath + ".downloading";
        using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        request.Headers.Add("X-Backup-Token", _options.Token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var localStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, localPath, overwrite: true);
    }

    private async Task<(int Verified, int Failed)> VerifyAllHashesAsync(CancellationToken cancellationToken)
    {
        var verified = 0;
        var failed = 0;

        var shaFiles = Directory.EnumerateFiles(_options.Directory, "*.sha256", SearchOption.AllDirectories).ToArray();
        foreach (var shaFile in shaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = shaFile[..^7]; // 截掉 ".sha256"
            if (!File.Exists(targetFile)) continue;

            if (CheckHash(targetFile, shaFile))
            {
                verified++;
            }
            else
            {
                failed++;
                _logger.LogError("文件完整性校验失败: {File}", targetFile);
            }
        }

        return (verified, failed);
    }

    private static bool CheckHash(string targetFile, string shaFile)
    {
        try
        {
            var content = File.ReadAllText(shaFile).Trim();
            var expected = content.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

            using var stream = File.OpenRead(targetFile);
            var actualBytes = SHA256.HashData(stream);
            var actual = Convert.ToHexString(actualBytes).ToLowerInvariant();

            return string.Equals(expected, actual, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void PruneOldHourlyArchives()
    {
        if (_options.RetentionDays <= 0) return;
        var hourlyDir = Path.Combine(_options.Directory, "hourly");
        if (!Directory.Exists(hourlyDir)) return;

        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        foreach (var file in Directory.EnumerateFiles(hourlyDir, "daohua-postgres-hourly-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
