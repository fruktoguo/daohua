using System.Net.Http.Json;
using System.Net.Sockets;

namespace DaoHua.LoadTests;

/// <summary>
/// compute-link 真实链路压测场景（可选场景，不进常规门禁）。
/// 与只测本地位预算的 <see cref="ComputeLoadScenario"/> 不同，本场景拉起真实进程组：
/// 真实 Server（ColdCatchUpWorker 生产 → ComputeDispatcherWorker 派发 → compute-link WebSocket）
/// + 真实 ComputeNode（DeterministicBatchEngine 真模拟）+ 真实 PostgreSQL 结果回写。
///
/// 测量口径：
/// 1. 端到端批次/任务吞吐：种子玩家被冷扫描入队 → 链路派发 → 节点补算 → 事务回写 succeeded 的全流程速率；
/// 2. 节点断线改派时延：中途杀掉一个节点进程，统计其在飞任务从「租约释放回 pending」到「改派后完结」的时延；
/// 3. 结果回写速率：轮询 compute_jobs 状态迁移得到 succeeded 增速曲线；
/// 4. 节点重连恢复：重启被杀节点，经运维面板 /ops/api/compute 观察重新注册耗时。
///
/// 依赖与跳过条件：需要可达的 PostgreSQL 测试库（--database / DAOHUA_COMPUTE_TEST_CONNECTION /
/// DAOHUA_MARKET_TEST_CONNECTION / DAOHUA_DATABASE_URL，优先级从高到低）与可构建的
/// src/Server、src/ComputeNode 源码树（--repo-root 可显式指定）。数据库不可达时输出
/// skipped 警告报告（--require-database 时判失败）；本场景不在 CI 门禁内，供本地/部署前手测。
/// 种子数据按运行标识前缀隔离，结束默认清理（--keep-probe-schema 保留排查）。
/// </summary>
internal static class ComputeLinkLoadScenario
{
    private const int SectionCountExpected = 8;
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NodeRegisterBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RejoinPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>场景入口：组装进程组与种子数据，执行测量并产出结构化报告。</summary>
    public static async Task<LoadTestReport> RunReportAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();
        var errors = 0;

        var connectionString = ResolveConnectionString(options);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await ProbeDatabaseAsync(dataSource, cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            var message = $"PostgreSQL 测试库不可达，compute-link 场景跳过：{exception.Message}";
            if (!options.RequireDatabase)
            {
                return new LoadTestReport(
                    "loadtest.v1", "compute-link", options.Players, options.DurationSeconds,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, managedBefore,
                    GC.GetTotalMemory(false), process.WorkingSet64, 0, 0, 0, 0, 0, true,
                    new Dictionary<string, object?> { ["skipped"] = true, ["reason"] = message },
                    [message, "需要可达测试库：--database / DAOHUA_COMPUTE_TEST_CONNECTION / DAOHUA_MARKET_TEST_CONNECTION / DAOHUA_DATABASE_URL。"]);
            }
            return LoadTestReport.Failure("compute-link", message);
        }

        var repoRoot = options.RepoRoot ?? FindRepoRoot();
        if (repoRoot is null)
        {
            return LoadTestReport.Failure("compute-link",
                "找不到仓库根（Game.slnx）：请从仓库目录运行或用 --repo-root 指定。");
        }

        var runTag = $"lt{DateTimeOffset.UtcNow:MMddHHmmss}{Random.Shared.Next(0, 999):D3}";
        var spawned = new List<SpawnedProcess>();
        var runPlayerIds = Array.Empty<Guid>();
        var templatePlayerId = Guid.Empty;
        var templateCreatedByUs = false;
        try
        {
            // 构建产物：缺失时现场 dotnet build（增量化，热缓存下秒级）。
            var serverDll = await EnsureBuiltAsync(repoRoot, Path.Combine("src", "Server", "Server.csproj"), "Server.dll", cancellationToken);
            var nodeDll = await EnsureBuiltAsync(repoRoot, Path.Combine("src", "ComputeNode", "ComputeNode.csproj"), "DaoHua.ComputeNode.dll", cancellationToken);

            var logDir = Path.Combine(Path.GetTempPath(), $"daohua-linkload-{runTag}");
            Directory.CreateDirectory(logDir);
            warnings.Add($"子进程日志目录：{logDir}（server/节点 stdout+stderr 落盘供排查）。");

            var apiPort = FreePort();
            var opsPort = FreePort();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            var server = SpawnServer(serverDll, apiPort, opsPort, connectionString, token, logDir);
            spawned.Add(server);
            Log($"主服已拉起 api=:{apiPort} ops=:{opsPort}，等待健康就绪…");
            await WaitHttpOkAsync($"http://127.0.0.1:{apiPort}/healthz", StartupBudget, cancellationToken);

            for (var index = 0; index < options.ComputeNodes; index++)
            {
                spawned.Add(SpawnNode(nodeDll, apiPort, token, $"lt-node-{index}", logDir));
            }
            Log($"{options.ComputeNodes} 个计算节点已拉起，等待注册…");
            var opsBase = $"http://127.0.0.1:{opsPort}/ops";
            var registered = await WaitNodeCountAsync(opsBase, options.ComputeNodes, NodeRegisterBudget, cancellationToken);
            if (!registered)
            {
                warnings.Add("运维面板节点数未在预算内达到预期，继续执行（节点注册计数可能滞后可观测）。");
            }

            // 模板玩家：优先复用库中已有全段存档；缺失时经真实注册接口创建（生产链一致）。
            (templatePlayerId, templateCreatedByUs) = await EnsureTemplatePlayerAsync(
                dataSource, apiPort, runTag, cancellationToken);
            runPlayerIds = await ClonePlayersAsync(dataSource, templatePlayerId, options.Players, runTag, cancellationToken);
            Log($"种子完成：模板 {templatePlayerId:D} → 克隆 {runPlayerIds.Length} 个冷挂机玩家。");

            var result = await MeasureAsync(
                dataSource, opsBase, spawned, runPlayerIds, options, cancellationToken);
            result.JobsEnqueued = result.Waves.Values.Sum();
            errors += result.Errors + (result.JobsDead > 0 ? 1 : 0);
            if (result.JobsSucceeded < result.JobsEnqueued)
            {
                errors++;
                warnings.Add($"完结任务 {result.JobsSucceeded}/{result.JobsEnqueued}，存在未完成任务。");
            }
            warnings.AddRange(result.Warnings);

            var elapsed = Stopwatch.GetElapsedTime(started);
            process.Refresh();
            return new LoadTestReport(
                "loadtest.v1",
                "compute-link",
                options.Players,
                options.DurationSeconds,
                elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                managedBefore,
                GC.GetTotalMemory(forceFullCollection: false),
                process.WorkingSet64,
                result.BytesSent,
                0,
                result.JobsEnqueued,
                result.JobsSucceeded,
                errors,
                errors == 0,
                result.ToMetrics(options, connectionString),
                warnings);
        }
        catch (OperationCanceledException)
        {
            return LoadTestReport.Failure("compute-link", "压测被取消或超出整体时间预算。");
        }
        catch (Exception exception)
        {
            return LoadTestReport.Failure("compute-link", $"compute-link 场景执行失败：{exception.Message}");
        }
        finally
        {
            foreach (var child in spawned) child.Dispose();
            if (!options.KeepProbeSchema)
            {
                try { await CleanupAsync(dataSource, runPlayerIds, templateCreatedByUs ? templatePlayerId : null, runTag); }
                catch (Exception exception) { Log($"清理种子数据失败（忽略）：{exception.Message}"); }
            }
        }
    }

    /// <summary>compute-link 专用连接串解析：显式 --database 优先，其后专用环境变量，最后通用链。</summary>
    private static string ResolveConnectionString(LoadTestOptions options)
    {
        if (options.DatabaseExplicit) return options.DatabaseConnectionString;
        return Environment.GetEnvironmentVariable("DAOHUA_COMPUTE_TEST_CONNECTION")
               ?? Environment.GetEnvironmentVariable("DAOHUA_MARKET_TEST_CONNECTION")
               ?? options.DatabaseConnectionString;
    }

    private static async Task ProbeDatabaseAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = await source.OpenConnectionAsync(timeout.Token);
        await using var command = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = 8 };
        await command.ExecuteScalarAsync(timeout.Token);
    }

    /// <summary>从当前目录与进程基目录向上找含 Game.slnx 的目录。</summary>
    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Game.slnx"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>定位项目构建产物；缺失时执行 dotnet build -c Release。</summary>
    private static async Task<string> EnsureBuiltAsync(
        string repoRoot, string projectRelative, string assemblyName, CancellationToken cancellationToken)
    {
        var output = Path.Combine(repoRoot, Path.GetDirectoryName(projectRelative)!,
            "bin", "Release", "net10.0", assemblyName);
        if (File.Exists(output)) return output;
        var project = Path.Combine(repoRoot, projectRelative);
        if (!File.Exists(project))
            throw new InvalidOperationException($"项目文件不存在：{project}");
        Log($"构建产物缺失，执行 dotnet build {projectRelative} -c Release …");
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = DotnetHost(),
            Arguments = $"build \"{project}\" -c Release --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("无法启动 dotnet build。");
        var stdout = await build.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await build.StandardError.ReadToEndAsync(cancellationToken);
        await build.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        if (build.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"dotnet build {projectRelative} 失败：{stderr}{stdout[^Math.Min(800, stdout.Length)..]}");
        return output;
    }

    /// <summary>dotnet 宿主定位：优先当前进程宿主（dotnet run 场景），否则按 PATH 解析。</summary>
    private static string DotnetHost()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return self;
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>拉起真实主服：compute 派发链全配（短轮询/短租约/短扫描间隔），运维面板开在本机随机端口。</summary>
    private static SpawnedProcess SpawnServer(
        string serverDll, int apiPort, int opsPort, string connectionString, string token, string logDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            Arguments = $"\"{serverDll}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__daohua"] = connectionString;
        psi.Environment["Storage__EnableSchemaBootstrap"] = "true";
        psi.Environment["ComputeNode__Enabled"] = "true";
        psi.Environment["ComputeNode__Token"] = token;
        psi.Environment["ComputeNode__PollIntervalMilliseconds"] = "500";
        psi.Environment["ComputeNode__RequestTimeoutSeconds"] = "30";
        psi.Environment["ComputeNode__OutboundWriteTimeoutSeconds"] = "10";
        psi.Environment["ComputeNode__LeaseSeconds"] = "30";
        // 单批 8 任务：多节点均摊在飞批次，断线改派测量才杀得到持有在飞任务的节点。
        psi.Environment["ComputeNode__BatchSize"] = "8";
        psi.Environment["ComputeNode__ColdCatchUpIntervalSeconds"] = "10";
        psi.Environment["ComputeNode__ColdCatchUpMinIdleMinutes"] = "1";
        psi.Environment["ComputeNode__ColdCatchUpBatchSize"] = "2000";
        psi.Environment["Ops__Enabled"] = "true";
        psi.Environment["Ops__Port"] = opsPort.ToString();
        psi.Environment["Ops__ListenAddress"] = "127.0.0.1";
        psi.Environment["Logging__LogLevel__Default"] = "Information";
        psi.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        return SpawnedProcess.Start(psi, "server", logDir);
    }

    /// <summary>拉起真实计算节点进程：反向拨入主服 compute-link 端点，调试口绑随机端口防冲突。</summary>
    private static SpawnedProcess SpawnNode(
        string nodeDll, int apiPort, string token, string nodeName, string logDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            Arguments = $"\"{nodeDll}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(nodeDll)!
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DAOHUA_COMPUTE_TOKEN"] = token;
        psi.Environment["DAOHUA_COMPUTE_UPSTREAM_URL"] = $"http://127.0.0.1:{apiPort}/internal/compute-link";
        psi.Environment["DAOHUA_COMPUTE_NODE_NAME"] = nodeName;
        psi.Environment["DAOHUA_COMPUTE_URLS"] = "http://127.0.0.1:0";
        psi.Environment["ComputeNode__MaxConcurrentBatches"] = "4";
        psi.Environment["Logging__LogLevel__Default"] = "Warning";
        return SpawnedProcess.Start(psi, nodeName, logDir);
    }

    /// <summary>轮询直到 URL 返回 2xx；超时抛 InvalidOperationException。</summary>
    private static async Task WaitHttpOkAsync(string url, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow + budget;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                last = exception;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException($"等待 {url} 就绪超时（{budget}）。{last?.Message}");
    }

    /// <summary>经运维面板 /ops/api/compute 观察在线节点数；面板不可达时按 false 返回不阻塞。</summary>
    private static async Task<bool> WaitNodeCountAsync(
        string opsBase, int expected, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await TryReadNodeCountAsync(opsBase, cancellationToken);
            if (count >= expected) return true;
            await Task.Delay(RejoinPollInterval, cancellationToken);
        }
        return false;
    }

    /// <summary>读取运维面板节点数；不可达或面板未装配返回 -1。</summary>
    private static async Task<int> TryReadNodeCountAsync(string opsBase, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var document = JsonDocument.Parse(
                await http.GetByteArrayAsync($"{opsBase}/api/compute", cancellationToken));
            return document.RootElement.TryGetProperty("totals", out var totals)
                && totals.TryGetProperty("nodeCount", out var count)
                ? count.GetInt32()
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 模板玩家选取：优先复用库中已有「全段（8）存档」玩家；没有时经真实注册接口创建
    /// （注册路径会 Load+Flush 完整初始状态，与生产一致）。
    /// </summary>
    private static async Task<(Guid PlayerId, bool CreatedByUs)> EnsureTemplatePlayerAsync(
        NpgsqlDataSource source, int apiPort, string runTag, CancellationToken cancellationToken)
    {
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT p.player_id
            FROM identity.players p
            JOIN game.player_state_heads h ON h.player_id = p.player_id
            WHERE (SELECT count(*) FROM game.player_state_sections s WHERE s.player_id = p.player_id) = $1
            ORDER BY p.created_at DESC
            LIMIT 1
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = SectionCountExpected });
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid playerId) return (playerId, false);
        }

        // 库中无可复用存档：走真实注册（创建账号+玩家+初始分段落库）。
        var account = $"{runTag}_tpl";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{apiPort}/api/v1/auth/register",
            new { account, password = $"pw-{runTag}", displayName = $"模板{runTag}" },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"模板玩家注册失败 HTTP {(int)response.StatusCode}：{await response.Content.ReadAsStringAsync(cancellationToken)}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var created = document.RootElement.GetProperty("playerId").GetGuid();
        // 注册响应先于脏段刷盘返回（SectionFlushWorker 异步落库）：必须等 8 段与提交头
        // 真实可见再克隆，否则克隆体无提交头会被冷扫描 JOIN 永远排除。
        await WaitTemplateArchiveAsync(source, created, cancellationToken);
        return (created, true);
    }

    /// <summary>轮询等待模板玩家 8 段成段落库（注册路径的异步刷盘收敛点），超时快速失败。</summary>
    private static async Task WaitTemplateArchiveAsync(
        NpgsqlDataSource source, Guid playerId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM game.player_state_sections WHERE player_id = $1
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            var sections = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (sections >= SectionCountExpected) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException($"模板玩家 {playerId} 的 {SectionCountExpected} 段存档未在 60s 内落库。");
    }

    /// <summary>
    /// 以模板玩家为源克隆 count 个冷挂机种子玩家：账号/玩家/提交头/8 段/投影/余额整行复制，
    /// 提交头 last_simulated_at 与投影 last_active_at 回拨 2 小时使其落入冷扫描窗口
    /// （离线 + 过宽限 + 48h 窗口内）。head.checksum 覆盖 section 集与玩家 ID 无关，克隆行自洽。
    /// </summary>
    private static async Task<Guid[]> ClonePlayersAsync(
        NpgsqlDataSource source, Guid templatePlayerId, int count, string runTag, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        Guid templateAccountId;
        await using (var query = new NpgsqlCommand(
            "SELECT account_id FROM identity.players WHERE player_id = $1", connection))
        {
            query.Parameters.Add(new NpgsqlParameter { Value = templatePlayerId });
            templateAccountId = (Guid)(await query.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("模板玩家缺少 identity.players 行。"));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecAsync(connection, transaction, "CREATE TEMP TABLE lt_map (player_id uuid, account_id uuid, idx int)");
        await ExecAsync(connection, transaction,
            "INSERT INTO lt_map SELECT gen_random_uuid(), gen_random_uuid(), i FROM generate_series(1, $1) AS i",
            count);
        await ExecAsync(connection, transaction, """
            INSERT INTO identity.accounts
                (account_id, login_normalized, password_hash, password_salt, password_iterations, status, created_at, updated_at)
            SELECT m.account_id, $1 || '_' || m.idx, a.password_hash, a.password_salt, a.password_iterations, a.status, now(), now()
            FROM lt_map m JOIN identity.accounts a ON a.account_id = $2
            """, runTag, templateAccountId);
        await ExecAsync(connection, transaction, """
            INSERT INTO identity.players
                (player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
            SELECT m.player_id, m.account_id, $1 || m.idx, $1 || m.idx, now(), now() - interval '2 hours', 1
            FROM lt_map m
            """, $"测{runTag}-");
        await ExecAsync(connection, transaction, """
            INSERT INTO game.player_state_heads
                (player_id, state_version, schema_version, last_simulated_at, game_data_fingerprint, checksum, updated_at)
            SELECT m.player_id, h.state_version, h.schema_version, now() - interval '2 hours',
                   h.game_data_fingerprint, h.checksum, now()
            FROM lt_map m JOIN game.player_state_heads h ON h.player_id = $1
            """, templatePlayerId);
        await ExecAsync(connection, transaction, """
            INSERT INTO game.player_state_sections
                (player_id, section_kind, section_version, schema_version, codec, payload, raw_bytes, stored_bytes, checksum, updated_at)
            SELECT m.player_id, s.section_kind, s.section_version, s.schema_version, s.codec,
                   s.payload, s.raw_bytes, s.stored_bytes, s.checksum, now()
            FROM lt_map m JOIN game.player_state_sections s ON s.player_id = $1
            """, templatePlayerId);
        await ExecAsync(connection, transaction, """
            INSERT INTO game.player_projection
                (player_id, realm_rank, current_node_id, presence, last_active_at, projection_version, updated_at)
            SELECT m.player_id, 0, NULL, 'offline', now() - interval '2 hours', 1, now()
            FROM lt_map m
            """);
        await ExecAsync(connection, transaction, """
            INSERT INTO economy.player_asset_balances
                (player_id, asset_id, variant, quantity, version, updated_at)
            SELECT m.player_id, b.asset_id, b.variant, b.quantity, b.version, now()
            FROM lt_map m JOIN economy.player_asset_balances b ON b.player_id = $1
            """, templatePlayerId);

        var ids = new List<Guid>();
        await using (var read = new NpgsqlCommand("SELECT player_id FROM lt_map ORDER BY idx", connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }
        await transaction.CommitAsync(cancellationToken);
        if (ids.Count != count)
            throw new InvalidOperationException($"克隆玩家数不符：期望 {count}，实得 {ids.Count}。");
        return [.. ids];
    }

    /// <summary>
    /// 测量主循环：每波把种子玩家回拨为「离线且模拟欠债 2h」，等冷扫描生产任务并经真实链路完结；
    /// 单波内按 compute_jobs 状态迁移统计吞吐/回写速率/单任务时延；
    /// 开启 node-kill 且节点数 ≥2 时在任务完成过半后杀掉末位节点测断线改派，随后重启测重连。
    /// </summary>
    private static async Task<ScenarioResult> MeasureAsync(
        NpgsqlDataSource source,
        string opsBase,
        List<SpawnedProcess> spawned,
        Guid[] playerIds,
        LoadTestOptions options,
        CancellationToken cancellationToken)
    {
        var result = new ScenarioResult();
        var jobLatencies = new List<double>();
        var writeBackSamples = new List<double>();
        DateTimeOffset? firstJobAt = null;
        DateTimeOffset? lastTerminalAt = null;
        SpawnedProcess? killed = null;
        var killedAt = DateTimeOffset.MinValue;
        var killedLeased = Array.Empty<Guid>();
        double? releaseMs = null;
        double? reassignMs = null;
        double? rejoinMs = null;

        for (var wave = 1; wave <= options.ComputeWaves; wave++)
        {
            var waveStart = DateTimeOffset.UtcNow;
            await RewindPlayersAsync(source, playerIds, cancellationToken);
            Log($"第 {wave}/{options.ComputeWaves} 波：已回拨种子玩家模拟欠债，等待冷扫描生产任务…");

            var waveDeadline = waveStart + TimeSpan.FromSeconds(options.DurationSeconds);
            var previousSucceeded = 0L;
            var previousSampleAt = waveStart;
            var waveEnqueued = 0L;
            var killDone = !options.NodeKill || options.ComputeNodes < 2 || killed is not null;

            while (DateTimeOffset.UtcNow < waveDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var counts = await QueryStatusCountsAsync(source, playerIds, waveStart, cancellationToken);
                var pending = counts.GetValueOrDefault("pending");
                var leased = counts.GetValueOrDefault("leased");
                var succeeded = counts.GetValueOrDefault("succeeded");
                var dead = counts.GetValueOrDefault("dead");
                var total = pending + leased + succeeded + dead;
                if (total > 0 && firstJobAt is null) firstJobAt = DateTimeOffset.UtcNow;
                waveEnqueued = Math.Max(waveEnqueued, total);
                result.JobsSucceeded += Math.Max(0, succeeded - result.WaveSucceededBaseline(wave));
                result.JobsDead += Math.Max(0, dead - result.WaveDeadBaseline(wave));
                result.SetWaveBaseline(wave, succeeded, dead);

                // 回写速率：succeeded 计数差分换算每秒完结数。
                var now = DateTimeOffset.UtcNow;
                var delta = succeeded - previousSucceeded;
                var span = (now - previousSampleAt).TotalSeconds;
                if (delta > 0 && span > 0)
                {
                    writeBackSamples.Add(delta / span);
                    lastTerminalAt = now;
                }
                previousSucceeded = succeeded;
                previousSampleAt = now;

                // 断线改派测量：首次观测到在飞租约即杀持有最多在飞批次的节点；
                // 若轮询错过在飞窗口则退化为任务过半时杀最后拉起的节点（保底有信号）。
                var killDue = leased > 0 || (waveEnqueued > 0 && succeeded + dead >= waveEnqueued / 2.0);
                if (!killDone && killDue)
                {
                    killed = await PickKillTargetAsync(spawned, opsBase, cancellationToken);
                    killedLeased = await QueryJobIdsAsync(source, playerIds, "leased", cancellationToken);
                    killedAt = DateTimeOffset.UtcNow;
                    Log($"观测到在飞/过半，杀掉节点 {killed.Name}（pid={killed.Process.Id}）测断线改派；在飞 {killedLeased.Length} 任务…");
                    killed.KillTree();
                    killDone = true;
                }

                // 被杀节点在飞任务的释放/改派观测。
                if (killed is not null && releaseMs is null && killedLeased.Length > 0)
                {
                    var stillLeased = await CountJobsAsync(source, killedLeased, "leased", cancellationToken);
                    if (stillLeased == 0) releaseMs = (DateTimeOffset.UtcNow - killedAt).TotalMilliseconds;
                }
                if (killed is not null && reassignMs is null && killedLeased.Length > 0)
                {
                    var open = await CountJobsAsync(source, killedLeased, ["pending", "leased"], cancellationToken);
                    if (open == 0) reassignMs = (DateTimeOffset.UtcNow - killedAt).TotalMilliseconds;
                }

                if (pending == 0 && leased == 0 && total > 0)
                {
                    // 本波全部终态：重启被杀节点测重连，然后进入下一波。
                    if (killed is not null && rejoinMs is null)
                    {
                        var restarted = RespawnNode(killed);
                        spawned.Add(restarted);
                        var rejoinStart = DateTimeOffset.UtcNow;
                        var back = await WaitNodeCountAsync(opsBase, options.ComputeNodes, TimeSpan.FromSeconds(30), cancellationToken);
                        rejoinMs = back ? (DateTimeOffset.UtcNow - rejoinStart).TotalMilliseconds : -1;
                        if (!back) result.Warnings.Add("被杀节点重启后未在 30s 内重新注册。");
                        killed = null;
                    }
                    break;
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
            result.WaveEnqueued(wave, waveEnqueued);

            if (DateTimeOffset.UtcNow >= waveDeadline)
            {
                result.Errors++;
                result.Warnings.Add($"第 {wave} 波在 {options.DurationSeconds}s 预算内未全部完结。");
                break;
            }
        }

        if (killed is not null)
        {
            // 波次结束时仍有被杀节点未恢复：补齐重启避免遗留缺口。
            spawned.Add(RespawnNode(killed));
            result.Warnings.Add("测量结束时被杀节点的改派观测未完成。");
        }

        var latencies = await QueryJobLatenciesAsync(source, playerIds, cancellationToken);
        result.JobLatencyMs = latencies;
        result.WriteBackSamples = writeBackSamples;
        result.ReleaseMs = releaseMs;
        result.ReassignMs = reassignMs;
        result.RejoinMs = rejoinMs;
        result.FirstJobAt = firstJobAt;
        result.LastTerminalAt = lastTerminalAt;
        result.BytesSent = await TryReadBytesSentAsync(opsBase, cancellationToken);
        if (result.ReassignMs is null && options.NodeKill && options.ComputeNodes >= 2)
            result.Warnings.Add("断线改派未完成测量（在飞任务未全部终态或无在飞任务）。");
        return result;
    }

    /// <summary>把种子玩家重新拨回「离线 + 模拟欠债」状态，让下一波冷扫描再次生产任务。</summary>
    private static async Task RewindPlayersAsync(NpgsqlDataSource source, Guid[] playerIds, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await ExecAsync(connection, null,
            "UPDATE game.player_state_heads SET last_simulated_at = now() - interval '2 hours' WHERE player_id = ANY($1)",
            playerIds);
        await ExecAsync(connection, null,
            "UPDATE game.player_projection SET presence = 'offline', last_active_at = now() - interval '2 hours' WHERE player_id = ANY($1)",
            playerIds);
    }

    /// <summary>统计本波次（created_at ≥ waveStart）任务按状态计数。</summary>
    private static async Task<Dictionary<string, long>> QueryStatusCountsAsync(
        NpgsqlDataSource source, Guid[] playerIds, DateTimeOffset waveStart, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status, count(*) FROM platform.compute_jobs
            WHERE player_id = ANY($1) AND created_at >= $2
            GROUP BY status
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds });
        command.Parameters.Add(new NpgsqlParameter { Value = waveStart });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }

    /// <summary>取当前指定状态的 job_id 集（断线时点记录在飞任务）。</summary>
    private static async Task<Guid[]> QueryJobIdsAsync(
        NpgsqlDataSource source, Guid[] playerIds, string status, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT job_id FROM platform.compute_jobs WHERE player_id = ANY($1) AND status = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return [.. ids];
    }

    /// <summary>统计给定 job_id 集中处于指定状态的行数。</summary>
    private static async Task<long> CountJobsAsync(
        NpgsqlDataSource source, Guid[] jobIds, string status, CancellationToken cancellationToken) =>
        await CountJobsAsync(source, jobIds, [status], cancellationToken);

    private static async Task<long> CountJobsAsync(
        NpgsqlDataSource source, Guid[] jobIds, string[] statuses, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform.compute_jobs WHERE job_id = ANY($1) AND status = ANY($2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = jobIds });
        command.Parameters.Add(new NpgsqlParameter { Value = statuses });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>单任务端到端时延（入队→完结，毫秒），仅 succeeded 终态。</summary>
    private static async Task<List<double>> QueryJobLatenciesAsync(
        NpgsqlDataSource source, Guid[] playerIds, CancellationToken cancellationToken)
    {
        var latencies = new List<double>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXTRACT(EPOCH FROM (updated_at - created_at)) * 1000.0
            FROM platform.compute_jobs
            WHERE player_id = ANY($1) AND status = 'succeeded'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) latencies.Add(reader.GetDouble(0));
        return latencies;
    }

    /// <summary>从运维面板累计节点发送字节（测量吞吐的链路侧口径）；不可达返回 0。</summary>
    private static async Task<long> TryReadBytesSentAsync(string opsBase, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var document = JsonDocument.Parse(
                await http.GetByteArrayAsync($"{opsBase}/api/compute", cancellationToken));
            long total = 0;
            foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
                if (node.TryGetProperty("bytesSent", out var bytes)) total += bytes.GetInt64();
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>按运行标识清理种子数据；模板为本次创建时一并删除。</summary>
    private static async Task CleanupAsync(
        NpgsqlDataSource source, Guid[] playerIds, Guid? templatePlayerId, string runTag)
    {
        if (playerIds.Length == 0 && templatePlayerId is null) return;
        var allPlayers = templatePlayerId is { } tpl ? playerIds.Append(tpl).ToArray() : playerIds;
        await using var connection = await source.OpenConnectionAsync();
        // 按 FK 依赖序删除：先清引用 players/accounts 的下游行，再清身份行。
        await ExecAsync(connection, null,
            "DELETE FROM platform.compute_jobs WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM game.player_runtime_leases WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM game.player_action_queue WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM economy.player_asset_balances WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM game.player_projection WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM game.player_state_sections WHERE player_id = ANY($1)", allPlayers);
        await ExecAsync(connection, null,
            "DELETE FROM game.player_state_heads WHERE player_id = ANY($1)", allPlayers);
        var accountIds = new List<Guid>();
        await using (var query = new NpgsqlCommand(
            "SELECT account_id FROM identity.players WHERE player_id = ANY($1)", connection))
        {
            query.Parameters.Add(new NpgsqlParameter { Value = allPlayers });
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) accountIds.Add(reader.GetGuid(0));
        }
        await ExecAsync(connection, null,
            "DELETE FROM identity.auth_sessions WHERE account_id = ANY($1)", accountIds.ToArray());
        await ExecAsync(connection, null,
            "DELETE FROM identity.players WHERE player_id = ANY($1)", allPlayers);
        if (accountIds.Count > 0)
            await ExecAsync(connection, null,
                "DELETE FROM identity.accounts WHERE account_id = ANY($1) AND login_normalized LIKE $2",
                accountIds.ToArray(), $"{runTag}%");
    }

    /// <summary>被杀节点进程按原配置重启（同 dll/参数/日志目录），供重连恢复时延测量。</summary>
    private static SpawnedProcess RespawnNode(SpawnedProcess killed) =>
        SpawnedProcess.Start(killed.StartInfo, killed.Name, killed.LogDir);

    /// <summary>
    /// 挑选要杀的节点：优先经运维面板找出在飞批次最多的计算节点；
    /// 面板不可达时退化杀最后拉起的节点。主服进程（spawned[0]）永不入选。
    /// </summary>
    private static async Task<SpawnedProcess> PickKillTargetAsync(
        List<SpawnedProcess> spawned, string opsBase, CancellationToken cancellationToken)
    {
        var nodes = spawned.Skip(1).ToArray();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var document = JsonDocument.Parse(
                await http.GetByteArrayAsync($"{opsBase}/api/compute", cancellationToken));
            var busiest = nodes
                .Select(spawn =>
                {
                    var view = document.RootElement.GetProperty("nodes").EnumerateArray()
                        .FirstOrDefault(node => node.GetProperty("name").GetString() == spawn.Name);
                    return (spawn, inFlight: view.ValueKind == JsonValueKind.Object
                        ? view.GetProperty("inFlight").GetInt32() : 0);
                })
                .OrderByDescending(entry => entry.inFlight)
                .ToArray();
            if (busiest.Length > 0 && busiest[0].inFlight > 0) return busiest[0].spawn;
        }
        catch { /* 面板不可达时退化 */ }
        return nodes[^1];
    }

    private static Task ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command.ExecuteNonQueryAsync();
    }

    private static void Log(string message) => Console.Error.WriteLine($"[compute-link] {message}");

    /// <summary>子进程簿记：尾部日志环形缓冲 + 全量 stdout/stderr 落盘 + 进程树清理。</summary>
    private sealed class SpawnedProcess : IDisposable
    {
        private readonly ConcurrentQueue<string> _logTail = new();
        private readonly StreamWriter _logFile;
        private bool _killed;

        public required Process Process { get; init; }
        public required ProcessStartInfo StartInfo { get; init; }
        public required string Name { get; init; }
        public required string LogDir { get; init; }

        public static SpawnedProcess Start(ProcessStartInfo info, string name, string logDir)
        {
            var spawned = new SpawnedProcess(
                new StreamWriter(Path.Combine(logDir, $"{name}-{Guid.NewGuid():N}.log")) { AutoFlush = true })
            {
                Process = Process.Start(info) ?? throw new InvalidOperationException($"无法拉起进程 {name}。"),
                StartInfo = info,
                Name = name,
                LogDir = logDir
            };
            spawned.Process.OutputDataReceived += (_, args) => spawned.Enqueue(args.Data);
            spawned.Process.ErrorDataReceived += (_, args) => spawned.Enqueue(args.Data);
            spawned.Process.BeginOutputReadLine();
            spawned.Process.BeginErrorReadLine();
            return spawned;
        }

        private SpawnedProcess(StreamWriter logFile) => _logFile = logFile;

        private void Enqueue(string? line)
        {
            if (line is null) return;
            _logTail.Enqueue(line);
            while (_logTail.Count > 200) _logTail.TryDequeue(out _);
            try { lock (_logFile) _logFile.WriteLine(line); } catch { }
        }

        public void KillTree()
        {
            if (_killed) return;
            _killed = true;
            try { Process.Kill(entireProcessTree: true); } catch { }
        }

        public void Dispose()
        {
            KillTree();
            try { Process.WaitForExit(5000); } catch { }
            Process.Dispose();
            _logFile.Dispose();
        }
    }

    /// <summary>场景测量结果集。</summary>
    private sealed class ScenarioResult
    {
        private readonly Dictionary<int, (long Succeeded, long Dead)> _waveBaselines = new();
        public long JobsEnqueued;
        public long JobsSucceeded;
        public long JobsDead;
        public int Errors;
        public long BytesSent;
        public List<double> JobLatencyMs = [];
        public List<double> WriteBackSamples = [];
        public double? ReleaseMs;
        public double? ReassignMs;
        public double? RejoinMs;
        public DateTimeOffset? FirstJobAt;
        public DateTimeOffset? LastTerminalAt;
        public Dictionary<int, long> Waves = new();
        public List<string> Warnings { get; } = [];

        public long WaveSucceededBaseline(int wave) => _waveBaselines.TryGetValue(wave, out var b) ? b.Succeeded : 0;
        public long WaveDeadBaseline(int wave) => _waveBaselines.TryGetValue(wave, out var b) ? b.Dead : 0;
        public void SetWaveBaseline(int wave, long succeeded, long dead) => _waveBaselines[wave] = (succeeded, dead);
        public void WaveEnqueued(int wave, long count) => Waves[wave] = count;

        public Dictionary<string, object?> ToMetrics(LoadTestOptions options, string connectionString)
        {
            var activeSeconds = FirstJobAt is { } first && LastTerminalAt is { } last && last > first
                ? (last - first).TotalSeconds
                : 0;
            var latencies = JobLatencyMs.OrderBy(v => v).ToArray();
            var wb = WriteBackSamples.OrderBy(v => v).ToArray();
            return new Dictionary<string, object?>
            {
                ["topology"] = new
                {
                    nodes = options.ComputeNodes,
                    waves = options.ComputeWaves,
                    playersPerWave = options.Players,
                    nodeKill = options.NodeKill
                },
                ["throughput"] = new
                {
                    jobsEnqueued = JobsEnqueued,
                    jobsSucceeded = JobsSucceeded,
                    jobsDead = JobsDead,
                    activeWindowSeconds = Math.Round(activeSeconds, 2),
                    jobsPerSecond = activeSeconds > 0 ? Math.Round(JobsSucceeded / activeSeconds, 2) : 0,
                    writeBackPerSecondAvg = wb.Length == 0 ? 0 : Math.Round(wb.Average(), 2),
                    writeBackPerSecondPeak = wb.Length == 0 ? 0 : Math.Round(wb[^1], 2)
                },
                ["jobLatencyMs"] = new
                {
                    count = latencies.Length,
                    p50 = Math.Round(Percentile(latencies, 50), 1),
                    p95 = Math.Round(Percentile(latencies, 95), 1),
                    max = latencies.Length == 0 ? 0 : Math.Round(latencies[^1], 1)
                },
                ["nodeKill"] = new
                {
                    attempted = options.NodeKill && options.ComputeNodes >= 2,
                    leaseReleaseMs = ReleaseMs.HasValue ? Math.Round(ReleaseMs.Value, 1) : (double?)null,
                    reassignToTerminalMs = ReassignMs.HasValue ? Math.Round(ReassignMs.Value, 1) : (double?)null,
                    rejoinMs = RejoinMs.HasValue && RejoinMs >= 0 ? Math.Round(RejoinMs.Value, 1) : (double?)null
                },
                ["database"] = MaskConnectionString(connectionString)
            };
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
            sorted.Count == 0 ? 0 : LatencySummary.Percentile(sorted, percentile);

        /// <summary>报告内只允许出现主机/库名，绝不回传口令。</summary>
        private static object MaskConnectionString(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return new { host = builder.Host, port = builder.Port, database = builder.Database };
        }
    }
}
