namespace DaoHua.LoadTests;

/// <summary>
/// WebSocket 真实连接压测场景。
/// 对 <c>--url</c> 指向的 ws(s) 端点建立 <c>--players</c> 个并发连接，
/// 每个连接完成 hello/helloAck/snapshot 协议握手后持续接收快照增量直到 <c>--duration-seconds</c> 到期。
/// 服务端 /ws 端点要求 Cookie 会话；<c>--require-server</c> 时先通过 HTTP 注册压测账号获取会话，
/// 注册受限流或失败时降级为匿名 Upgrade 探测（会被 401，仅验证服务可达与应答耗时）。
/// </summary>
public static class WebSocketScenario
{
    /// <summary>连接成功率门禁：握手成功数 / 带会话尝试数。</summary>
    public const double ConnectSuccessRateGate = 0.99;
    /// <summary>helloAck 握手延迟 P99 预算（毫秒）。</summary>
    public const double HelloAckP99BudgetMilliseconds = 5_000;
    /// <summary>匿名探测模式下服务应答率门禁（拒绝也证明端点存活）。</summary>
    public const double ServerResponseRateGate = 0.99;
    /// <summary>会话 Cookie 名称，与 Server 侧 SessionAuthenticationDefaults.CookieName 对齐。</summary>
    private const string SessionCookieName = "daohua_session";
    /// <summary>注册并发上限：服务端 auth 限流为每源 IP 12 次/分钟，压测侧主动降速。</summary>
    private const int RegisterConcurrency = 4;
    /// <summary>注册请求之间的最小间隔（毫秒），避免瞬时打满固定窗口。</summary>
    private const int RegisterIntervalMilliseconds = 250;
    /// <summary>单个账号注册遇 429 时的最大重试次数。</summary>
    private const int RegisterMaxRetries = 1;
    /// <summary>429 重试等待上限（秒）；固定窗口为 60 秒，超过即放弃转入匿名。</summary>
    private const int RegisterRetryDelayCapSeconds = 65;
    /// <summary>单连接 Upgrade 超时。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>helloAck + 首个 snapshot 的握手超时。</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    /// <summary>HTTP 注册请求超时。</summary>
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 执行 WebSocket 压测并输出结构化报告。
    /// </summary>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>压测报告。</returns>
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        // 先强制回收再取基线，使内存增量口径只反映本次压测自身分配。
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();

        var webSocketUri = DeriveWebSocketUri(options.ServerUri);
        var httpBaseUri = DeriveHttpBaseUri(options.ServerUri);
        var stats = new WebSocketStats();

        // 鉴权阶段：仅在 --require-server 下注册压测账号；注册不占压测计时窗口。
        string?[] cookies = new string?[options.Players];
        if (options.RequireServer)
        {
            await RegisterAccountsAsync(httpBaseUri, options, cookies, stats, warnings, cancellationToken);
        }
        else
        {
            warnings.Add("未指定 --require-server：跳过账号注册，所有连接按匿名 Upgrade 探测执行。");
        }

        // 连接阶段：semaphore 只限制同时进行的 Upgrade 数，升级完成后连接保持至统一截止时间。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.DurationSeconds);
        var connectGate = new SemaphoreSlim(options.Concurrency);
        var tasks = new Task[options.Players];
        for (var index = 0; index < options.Players; index++)
        {
            var captured = index;
            tasks[index] = Task.Run(() => ConnectAndHoldAsync(webSocketUri, cookies[captured], deadline, connectGate, stats, cancellationToken), cancellationToken);
        }

        await Task.WhenAll(tasks);

        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var attempted = Interlocked.Read(ref stats.Attempted);
        var upgraded = Interlocked.Read(ref stats.Upgraded);
        var rejected = Interlocked.Read(ref stats.Rejected);
        var failed = Interlocked.Read(ref stats.Failed);
        var handshaken = Interlocked.Read(ref stats.HandshakeCompleted);
        var registered = Interlocked.Read(ref stats.Registered);
        var registrationFailed = Interlocked.Read(ref stats.RegistrationFailed);
        var protocolErrors = Interlocked.Read(ref stats.ProtocolErrors);

        var authMode = registered == options.Players
            ? "cookie"
            : registered > 0 ? "mixed" : "anonymous";
        var helloAckP99 = LatencySummary.P99(stats.HelloAckMilliseconds);
        // 成功率分母只含带会话的连接；匿名 401 属预期拒绝，另走 serverResponseRate 可达性口径。
        var upgradeSuccessRate = registered == 0 ? 0 : (double)upgraded / registered;
        var serverResponseRate = attempted == 0 ? 0 : (double)(upgraded + rejected) / attempted;

        var errors = 0;
        if (registered > 0)
        {
            if (upgradeSuccessRate < ConnectSuccessRateGate) errors++;
            if (helloAckP99 > HelloAckP99BudgetMilliseconds) errors++;
        }
        else
        {
            // 匿名降级口径：401 拒绝计入“服务已应答”，只校验端点可达性，不校验协议握手。
            if (serverResponseRate < ServerResponseRateGate) errors++;
            warnings.Add("authMode=anonymous：连接被 401 拒绝属预期，报告仅证明服务可达，未覆盖协议握手门禁。");
        }

        if (protocolErrors > 0)
        {
            errors++;
            warnings.Add($"握手/运行期协议错误 {protocolErrors} 次。");
        }

        if (handshaken < upgraded)
        {
            warnings.Add($"升级成功但未完成 helloAck+snapshot 握手的连接: {upgraded - handshaken}。");
        }

        return new LoadTestReport(
            "loadtest.v1",
            "websocket",
            options.Players,
            options.DurationSeconds,
            elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            Interlocked.Read(ref stats.SentBytes),
            Interlocked.Read(ref stats.ReceivedBytes),
            Interlocked.Read(ref stats.SentFrames),
            Interlocked.Read(ref stats.ReceivedFrames),
            errors,
            errors == 0,
            new Dictionary<string, object?>
            {
                ["target"] = new
                {
                    url = webSocketUri.ToString(),
                    httpBase = httpBaseUri.ToString(),
                    players = options.Players,
                    concurrency = options.Concurrency,
                    durationSeconds = options.DurationSeconds
                },
                ["authMode"] = authMode,
                ["registration"] = new
                {
                    attempted = options.RequireServer ? options.Players : 0,
                    succeeded = registered,
                    failed = registrationFailed,
                    rateLimited = Interlocked.Read(ref stats.RateLimited)
                },
                ["connections"] = new
                {
                    attempted,
                    upgraded,
                    rejected,
                    failed,
                    handshakeCompleted = handshaken,
                    upgradeSuccessRate = Math.Round(upgradeSuccessRate, 4),
                    serverResponseRate = Math.Round(serverResponseRate, 4)
                },
                ["latencyMs"] = new
                {
                    connect = LatencySummary.Describe(stats.ConnectMilliseconds),
                    helloAck = LatencySummary.Describe(stats.HelloAckMilliseconds)
                },
                ["firstSnapshotBytes"] = stats.FirstSnapshotBytes.Count == 0
                    ? null
                    : new
                    {
                        count = stats.FirstSnapshotBytes.Count,
                        min = stats.FirstSnapshotBytes.Min(),
                        max = stats.FirstSnapshotBytes.Max(),
                        average = Math.Round(stats.FirstSnapshotBytes.Average(), 1)
                    },
                ["frames"] = new
                {
                    snapshots = Interlocked.Read(ref stats.SnapshotFrames),
                    deltas = Interlocked.Read(ref stats.DeltaFrames),
                    logBatches = Interlocked.Read(ref stats.LogBatchFrames),
                    errors = Interlocked.Read(ref stats.ErrorFrames)
                },
                ["gate"] = new
                {
                    connectSuccessRate = ConnectSuccessRateGate,
                    helloAckP99Milliseconds = HelloAckP99BudgetMilliseconds,
                    serverResponseRate = ServerResponseRateGate
                }
            },
            warnings);
    }

    /// <summary>
    /// 为每个压测账号注册并提取会话 Cookie；限流耗尽后剩余账号直接降级匿名。
    /// </summary>
    private static async Task RegisterAccountsAsync(
        Uri httpBaseUri,
        LoadTestOptions options,
        string?[] cookies,
        WebSocketStats stats,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = RegisterConcurrency };
        using var client = new HttpClient(handler) { BaseAddress = httpBaseUri, Timeout = RegisterTimeout };
        // runToken 使多次运行的账号名互不冲突（login_normalized 有唯一约束）。
        var runToken = Guid.NewGuid().ToString("N")[..8];
        var password = $"LoadTest#{runToken}";
        var gate = new SemaphoreSlim(RegisterConcurrency);
        var saturated = 0;
        var registrationTasks = new Task[options.Players];
        for (var index = 0; index < options.Players; index++)
        {
            var captured = index;
            registrationTasks[index] = Task.Run(async () =>
            {
                if (Volatile.Read(ref saturated) == 1)
                {
                    Interlocked.Increment(ref stats.RegistrationFailed);
                    return;
                }

                await gate.WaitAsync(cancellationToken);
                try
                {
                    // 均匀错开注册请求，降低触发 12 次/分钟固定窗口的概率。
                    await Task.Delay(RegisterIntervalMilliseconds, cancellationToken);
                    // 服务端账号规则为 [a-zA-Z0-9_]{3,32}，连字符会被拒绝。
                    var account = $"loadtest_ws_{runToken}_{captured}";
                    cookies[captured] = await TryRegisterAsync(client, account, password, stats, cancellationToken);
                    if (cookies[captured] is null)
                    {
                        Interlocked.Increment(ref stats.RegistrationFailed);
                        Volatile.Write(ref saturated, 1);
                    }
                    else
                    {
                        Interlocked.Increment(ref stats.Registered);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(registrationTasks);
        if (Interlocked.Read(ref stats.Registered) < options.Players)
        {
            warnings.Add($"注册限流或失败：{Interlocked.Read(ref stats.Registered)}/{options.Players} 个账号获得会话，其余连接按匿名探测执行。");
        }
    }

    /// <summary>
    /// 调用 POST /api/v1/auth/register 注册单个账号，返回 daohua_session Cookie 值；失败返回 null。
    /// </summary>
    private static async Task<string?> TryRegisterAsync(
        HttpClient client,
        string account,
        string password,
        WebSocketStats stats,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { account, password, displayName = account },
            ProtocolJson.Options);
        for (var attempt = 0; attempt <= RegisterMaxRetries; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.PostAsync("/api/v1/auth/register", content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ExtractSessionCookie(response);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < RegisterMaxRetries)
                {
                    Interlocked.Increment(ref stats.RateLimited);
                    var delay = response.Headers.RetryAfter?.Delta
                                ?? TimeSpan.FromSeconds(RegisterRetryDelayCapSeconds);
                    await Task.Delay(delay > TimeSpan.FromSeconds(RegisterRetryDelayCapSeconds)
                        ? TimeSpan.FromSeconds(RegisterRetryDelayCapSeconds)
                        : delay, cancellationToken);
                    continue;
                }

                return null;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 从注册响应的 Set-Cookie 头中提取会话 Cookie（name=value 片段）。
    /// </summary>
    private static string? ExtractSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var value in values)
        {
            if (value.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
            {
                var end = value.IndexOf(';');
                return end < 0 ? value : value[..end];
            }
        }

        return null;
    }

    /// <summary>
    /// 建立单个 WebSocket 连接：并发位只覆盖 Upgrade 阶段（随后立即释放），
    /// 协议握手与连接存续不占并发位，持续到统一截止时间后正常关闭。
    /// </summary>
    private static async Task ConnectAndHoldAsync(
        Uri webSocketUri,
        string? cookie,
        DateTimeOffset deadline,
        SemaphoreSlim connectGate,
        WebSocketStats stats,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref stats.Attempted);
        using var socket = new ClientWebSocket();
        // 15 秒 keepalive：存续窗口内即使长时间无下行增量，也维持连接不被空闲回收。
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (cookie is not null)
        {
            socket.Options.SetRequestHeader("Cookie", cookie);
        }

        var connectStart = Stopwatch.GetTimestamp();
        await connectGate.WaitAsync(cancellationToken);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(webSocketUri, connectTimeout.Token);
                stats.ConnectMilliseconds.Add(Stopwatch.GetElapsedTime(connectStart).TotalMilliseconds);
                Interlocked.Increment(ref stats.Upgraded);
            }
            catch (Exception exception) when (IsServerRejection(exception))
            {
                // 服务端返回非 101（如 401）：说明端点可达且应答了 Upgrade。
                stats.ConnectMilliseconds.Add(Stopwatch.GetElapsedTime(connectStart).TotalMilliseconds);
                Interlocked.Increment(ref stats.Rejected);
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or OperationCanceledException)
            {
                Interlocked.Increment(ref stats.Failed);
                return;
            }
        }
        finally
        {
            connectGate.Release();
        }

        try
        {
            await HandshakeAndReceiveAsync(socket, deadline, stats, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 全局取消：安静退出。
        }
        catch (WebSocketException)
        {
            Interlocked.Increment(ref stats.Failed);
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "压测结束", closeTimeout.Token);
            }
            catch
            {
                socket.Abort();
            }
        }
    }

    /// <summary>
    /// 发送 hello 握手帧，等待 helloAck 与首个 snapshot，然后持续接收增量直到截止时间。
    /// </summary>
    private static async Task HandshakeAndReceiveAsync(
        ClientWebSocket socket,
        DateTimeOffset deadline,
        WebSocketStats stats,
        CancellationToken cancellationToken)
    {
        var helloFrame = WireEnvelopeCodec.EncodeJson(
            WireMessageType.Hello,
            Guid.NewGuid(),
            1,
            new HelloRequest("loadtest", null));
        var helloStart = Stopwatch.GetTimestamp();
        await socket.SendAsync(helloFrame, WebSocketMessageType.Binary, true, cancellationToken);
        Interlocked.Increment(ref stats.SentFrames);
        Interlocked.Add(ref stats.SentBytes, helloFrame.Length);

        var handshakeDeadline = DateTimeOffset.UtcNow.Add(HandshakeTimeout);
        var handshakeCompleted = false;
        var gotHelloAck = false;
        var gotSnapshot = false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                // 两阶段超时：握手用独立短超时，握手完成后切换到全局统一截止时间。
                var remaining = (handshakeCompleted ? deadline : handshakeDeadline) - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(remaining);

                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, timeout.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 握手或存续窗口到期：静默结束该连接的接收循环。
                    break;
                }

                var frameBytes = (int)stream.Length;
                Interlocked.Increment(ref stats.ReceivedFrames);
                Interlocked.Add(ref stats.ReceivedBytes, frameBytes);
                if (!WireEnvelopeCodec.TryDecode(stream.GetBuffer().AsSpan(0, frameBytes), out var envelope))
                {
                    Interlocked.Increment(ref stats.ProtocolErrors);
                    continue;
                }

                switch (envelope.MessageType)
                {
                    case WireMessageType.HelloAck:
                        if (!handshakeCompleted)
                        {
                            gotHelloAck = true;
                            stats.HelloAckMilliseconds.Add(Stopwatch.GetElapsedTime(helloStart).TotalMilliseconds);
                        }
                        break;
                    case WireMessageType.Snapshot:
                        if (!handshakeCompleted && !gotSnapshot) stats.FirstSnapshotBytes.Add(frameBytes);
                        gotSnapshot = true;
                        Interlocked.Increment(ref stats.SnapshotFrames);
                        break;
                    case WireMessageType.SnapshotDelta:
                        Interlocked.Increment(ref stats.DeltaFrames);
                        break;
                    case WireMessageType.LogBatch:
                        Interlocked.Increment(ref stats.LogBatchFrames);
                        break;
                    case WireMessageType.Error:
                        Interlocked.Increment(ref stats.ErrorFrames);
                        Interlocked.Increment(ref stats.ProtocolErrors);
                        break;
                }

                if (!handshakeCompleted && gotHelloAck && gotSnapshot)
                {
                    handshakeCompleted = true;
                    Interlocked.Increment(ref stats.HandshakeCompleted);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 判断 Upgrade 失败是否由服务端返回了非 101 状态码（端点可达但被拒绝）。
    /// ClientWebSocket 不直接暴露 HTTP 状态码，只能从异常类型与消息文本判定。
    /// </summary>
    private static bool IsServerRejection(Exception exception) =>
        exception switch
        {
            WebSocketException webSocketException => webSocketException.Message.Contains("status code", StringComparison.Ordinal),
            HttpRequestException httpRequestException => httpRequestException.StatusCode is not null,
            _ => false
        };

    /// <summary>
    /// 将 --url 归一化为 ws(s) 地址；http(s) 输入自动转换方案并补默认 /ws 路径。
    /// </summary>
    private static Uri DeriveWebSocketUri(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri)
        {
            Scheme = serverUri.Scheme switch
            {
                "http" => "ws",
                "https" => "wss",
                _ => serverUri.Scheme
            },
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (string.IsNullOrEmpty(serverUri.AbsolutePath.Trim('/')))
        {
            builder.Path = "/ws";
        }

        return builder.Uri;
    }

    /// <summary>
    /// 将 --url 归一化为 http(s) 基地址（仅保留主机与端口），用于注册等 HTTP 接口。
    /// </summary>
    private static Uri DeriveHttpBaseUri(Uri serverUri) => new UriBuilder(serverUri)
    {
        Scheme = serverUri.Scheme is "wss" or "https" ? "https" : "http",
        Path = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty
    }.Uri;

    /// <summary>
    /// WebSocket 场景的线程安全统计容器。
    /// </summary>
    private sealed class WebSocketStats
    {
        public long Attempted;
        public long Upgraded;
        public long Rejected;
        public long Failed;
        public long HandshakeCompleted;
        public long Registered;
        public long RegistrationFailed;
        public long RateLimited;
        public long ProtocolErrors;
        public long SentFrames;
        public long ReceivedFrames;
        public long SentBytes;
        public long ReceivedBytes;
        public long SnapshotFrames;
        public long DeltaFrames;
        public long LogBatchFrames;
        public long ErrorFrames;
        public readonly ConcurrentBag<double> ConnectMilliseconds = new();
        public readonly ConcurrentBag<double> HelloAckMilliseconds = new();
        public readonly ConcurrentBag<long> FirstSnapshotBytes = new();
    }
}
