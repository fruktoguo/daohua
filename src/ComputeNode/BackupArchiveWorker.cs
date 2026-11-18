using System.Globalization;
using System.Security.Cryptography;
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
    // 只接受归档生成器的文件名；.partial、下载临时文件等不属于源备份清单。
    private static readonly Regex FilePattern = new(
        @"(?<![a-zA-Z0-9._-])daohua-postgres-(hourly|daily|weekly|base)-([0-9]{8}T[0-9]{6}Z)\.(dump|tar\.gz)(\.sha256)?(?![a-zA-Z0-9._-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
        while (!stoppingToken.IsCancellationRequested)
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
                _logger.LogError(ex, "内置备份归档同步失败，将在下个周期重试。");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _state.LastSyncStartedAt = now;
        var verified = 0;
        try
        {
            foreach (var category in Categories)
            {
                var categoryDir = Path.Combine(_options.Directory, category);
                Directory.CreateDirectory(categoryDir);
                var url = $"{_options.BaseUrl.TrimEnd('/')}/{category}/";
                using var request = BackupRequest(url);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var extension = category == "base" ? "tar.gz" : "dump";
                var files = FilePattern.Matches(html)
                    .Where(m => m.Groups[1].Value == category && m.Groups[3].Value == extension)
                    .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
                var newest = DateTimeOffset.MinValue;
                foreach (var filename in files.Where(name => !name.EndsWith(".sha256", StringComparison.Ordinal)))
                {
                    if (!files.Contains(filename + ".sha256"))
                        throw new InvalidDataException($"源备份缺少校验对: {filename}");
                    var stamp = filename.AsSpan("daohua-postgres-".Length + category.Length + 1, 16);
                    if (!DateTimeOffset.TryParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                        || created > now.AddMinutes(5))
                        throw new InvalidDataException($"源备份时间无效: {filename}");
                    if (created > newest) newest = created;
                    await SyncPairAsync(url, categoryDir, filename, cancellationToken).ConfigureAwait(false);
                    verified++;
                }
                if (files.Any(name => name.EndsWith(".sha256", StringComparison.Ordinal) && !files.Contains(name[..^7])))
                    throw new InvalidDataException($"源备份目录 {category} 存在无数据文件的边车。");

                // daily/weekly 在首次启用对应计划前可为空；小时与物理基线必须来自本轮源目录。
                var maxAge = category switch { "hourly" => TimeSpan.FromHours(3), "base" => TimeSpan.FromHours(36), _ => TimeSpan.Zero };
                if (maxAge > TimeSpan.Zero && now - newest > maxAge)
                    throw new InvalidDataException($"源备份目录 {category} 缺少新鲜校验对。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            PruneOldHourlyArchives();
            _state.VerifiedCount = verified;
            _state.FailedCount = 0;
            _state.LastError = null;
            _state.LastSyncCompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("备份归档同步与对账完成: verified={Verified}", verified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.VerifiedCount = verified;
            _state.FailedCount = 1;
            // /healthz 不返回请求 URI、本地路径或未过滤的内部异常。
            _state.LastError = ex is HttpRequestException { StatusCode: { } status }
                ? $"源备份请求失败: HTTP {(int)status}" : "备份目录、下载、校验或新鲜度检查失败";
            throw;
        }
    }

    private HttpRequestMessage BackupRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Backup-Token", _options.Token);
        return request;
    }

    private async Task SyncPairAsync(string url, string categoryDir, string filename, CancellationToken cancellationToken)
    {
        var target = Path.Combine(categoryDir, filename);
        var sidecar = target + ".sha256";
        var tempSuffix = $".{Guid.NewGuid():N}.downloading";
        var tempData = target + tempSuffix;
        var tempSha = sidecar + tempSuffix;
        try
        {
            using var request = BackupRequest(url + filename + ".sha256");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
            if (text.Length < 66 || text[..64].Any(c => !Uri.IsHexDigit(c))
                || (text[64..] != "  " + filename && text[64..] != " *" + filename))
                throw new InvalidDataException($"备份摘要格式或文件名不符: {filename}");

            var expected = text[..64];
            if (!await CheckHashAsync(target, expected, cancellationToken).ConfigureAwait(false))
            {
                using var dataRequest = BackupRequest(url + filename);
                using var dataResponse = await _httpClient.SendAsync(dataRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                dataResponse.EnsureSuccessStatusCode();
                await using (var remote = await dataResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var local = new FileStream(tempData, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                if (!await CheckHashAsync(tempData, expected, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException($"下载内容与备份摘要不符: {filename}");
            }
            await File.WriteAllTextAsync(tempSha, text + "\n", cancellationToken).ConfigureAwait(false);
            if (File.Exists(tempData)) File.Move(tempData, target, overwrite: true);
            File.Move(tempSha, sidecar, overwrite: true);
        }
        finally
        {
            File.Delete(tempData);
            File.Delete(tempSha);
        }
    }

    private static async Task<bool> CheckHashAsync(string targetFile, string expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(targetFile)) return false;
        await using var stream = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length == 0) return false;
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneOldHourlyArchives()
    {
        if (_options.RetentionDays <= 0) return;
        var hourlyDir = Path.Combine(_options.Directory, "hourly");
        if (!Directory.Exists(hourlyDir)) return;

        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        foreach (var file in Directory.EnumerateFiles(hourlyDir, "daohua-postgres-hourly-*"))
        {
            if (file.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    var shaFile = file + ".sha256";
                    if (File.Exists(shaFile))
                    {
                        File.Delete(shaFile);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "清理过期小时归档失败: {File}", file);
            }
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
