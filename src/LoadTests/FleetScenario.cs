using Npgsql;
using Protocol;

namespace DaoHua.LoadTests;

/// <summary>
/// 持续仿真压测场景（fleet）：批量真实注册账号，逐角色建立 WebSocket 长连接，
/// 驱动「跳引导 → 采集/狩猎备料 → 锻造一阶铁甲套 → 装配 → 角色分工常驻行为」的完整业务闭环。
/// 分工：采集 / 制造 / 单人战斗（半数开启斗法）/ 组队讨伐（5 人满槽就绪自动开战循环）/ 巡游。
/// 运行期间后台监控协程周期采集 ops 健康端点与数据库计数，异常打印 [anomaly] 行供外层处置。
/// --duration-seconds 传 0 表示常驻运行直到进程被取消。
/// </summary>
public static class FleetScenario
{
    private const string SessionCookieName = "daohua_session";

    // 青云仙山节点（配表真源：config/game-data/maps + actions 的 availableNodeIds 白名单）
    private const string Village = "node.yunxi-village";       // 凝气草 + 木灵遭遇池
    private const string Brook = "node.shuyu-brook";           // 晨露 + 木灵池
    private const string HerbValley = "node.herb-valley";      // 凝气草 + 木灵池
    private const string PeachForest = "node.peach-forest";    // 桃木 + 落霞岭遭遇池
    private const string RockBeach = "node.rock-beach";        // 玄铁 + 落霞岭遭遇池

    // 一阶真实套装六件（GearSet[0]=武器、[1..4]=铁甲四件、[5]=桃木符）：
    // 普攻全流派统一走物攻、压测角色无技能槽，只取物理件；兽骨仅精英 50%/Boss 掉落，
    // 全链避开兽骨件（副手选精铁盾、饰品选桃木符——碎灵核为木灵 100% 掉落、兽皮为野兔 100% 掉落）。
    private static readonly (string Equipment, string Forge)[] GearSet =
    [
        ("equipment.qingfeng-sword", "forge.qingfeng-sword"),   // 武器：跳引导补发百炼铁×7+兽骨×1 恰一轮
        ("equipment.shufa-scarf", "forge.shufa-scarf"),         // 头：百炼铁×5+兽皮×1
        ("equipment.baina-armor", "forge.baina-armor"),         // 衣：百炼铁×7+兽皮×1
        ("equipment.zhakou-pants", "forge.zhakou-pants"),       // 裤：百炼铁×5+兽皮×1
        ("equipment.tiepi-guard", "forge.tiepi-guard"),         // 副手：百炼铁×5+兽皮×1
        ("equipment.taomu-talisman", "forge.taomu-talisman"),   // 饰：桃木晶×5+碎灵核×1
    ];

    // 材料账：玄铁×66→百炼铁×22、桃木×15→桃木晶×5；兽皮×4、碎灵核×1 走真实狩猎掉落。
    private static readonly (string Node, string Action, int Repeats, string Material, double Needed)[] BootstrapGather =
    [
        (PeachForest, "explore.chop-peach-wood", 15, "item.peach-wood", 15),
        (RockBeach, "explore.mine-dark-iron", 66, "item.dark-iron", 66),
    ];

    private static readonly (string Action, string Output, double Needed)[] BootstrapRefine =
    [
        ("craft.refine-peach-wood-crystal", "item.peach-wood-crystal", 5),
        ("craft.refine-iron", "item.refined-iron", 22),
    ];

    private enum FleetRole { Gatherer, Crafter, SoloCombat, PartyMember, Roamer }

    /// <summary>共享统计与异常收集。</summary>
    private sealed class FleetStats
    {
        public long Registered, RegisterFailed, Connected, Handshaken, Bootstrapped, Equipped;
        public long CommandsSent, CommandsOk, CommandsFailed, SocialSent, SocialFailed;
        public long ErrorFrames, Reconnects, SoloPvpEnabled;
        public long MarketPlaced, MarketCancelled, MarketClaimed, MarketFailed, ItemOps;
        public readonly ConcurrentQueue<string> Anomalies = new();
        public void Anomaly(string message)
        {
            var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
            Anomalies.Enqueue(line);
            while (Anomalies.Count > 500) Anomalies.TryDequeue(out _);
            Console.WriteLine($"[fleet][anomaly] {line}");
        }
    }

    /// <summary>跨角色编排上下文：组队编制为静态角色引用（PlayerId 运行时解析，允许迟到补登归队）。</summary>
    private sealed class FleetContext
    {
        public readonly FleetStats Stats = new();
        public FleetCharacter[][] PartyMembers = [];
        public HttpClient? RegisterClient;   // 角色协程内补注册复用阶段一共享连接池
    }

    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var context = new FleetContext();
        var httpBase = DeriveHttpBase(options.ServerUri);
        var wsUri = DeriveWebSocketUri(options.ServerUri);
        var runToken = Guid.NewGuid().ToString("N")[..8];
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();

        using var handler = new SocketsHttpHandler { UseCookies = false, MaxConnectionsPerServer = 32 };
        using var registerClient = new HttpClient(handler) { BaseAddress = httpBase, Timeout = TimeSpan.FromSeconds(20) };

        var roster = BuildRoster(options.Players);
        var characters = new FleetCharacter[roster.Count];
        for (var i = 0; i < roster.Count; i++)
            characters[i] = new FleetCharacter(i, runToken, roster[i], httpBase, wsUri, context);

        // 阶段一：注册 + WebSocket 握手（并发收敛；XFF 逐账号分散使 auth 限流按不同来源计数）。
        var gate = new SemaphoreSlim(Math.Max(4, options.Concurrency));
        await Task.WhenAll(characters.Select(character => Task.Run(async () =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await character.RegisterAndConnectAsync(registerClient, cancellationToken); }
            finally { gate.Release(); }
        }, cancellationToken)));

        var online = characters.Where(character => character.IsOnline).ToArray();
        if (online.Length == 0)
            return Report(options, started, context, warnings, "没有角色完成握手，仿真未启动。");
        if (online.Length < characters.Length)
            warnings.Add($"握手闭环 {online.Length}/{characters.Length}；缺口角色不参与仿真。");

        // 组队编制是静态设计：前 N 个 PartyMember 按 Index 每 5 人一队，队长取队首。
        // 不依赖握手瞬间的在线快照——注册风暴中断线的角色由协程补登后归原队，
        // 此前用在线快照编队会使掉线队员降级 SoloCombat（v4 实测 150 名队员全灭、0 队成型）。
        var partyMembers = characters.Where(c => c.RoleOf == FleetRole.PartyMember).OrderBy(c => c.Index).ToArray();
        var partySlots = new List<FleetCharacter[]>();
        for (var i = 0; i + 5 <= partyMembers.Length; i += 5)
            partySlots.Add(partyMembers.Skip(i).Take(5).ToArray());
        context.PartyMembers = partySlots.ToArray();
        foreach (var character in partyMembers)
        {
            var seq = Array.IndexOf(partyMembers, character);
            var slot = seq / 5;
            if (slot >= context.PartyMembers.Length) character.DemoteTo(FleetRole.SoloCombat);
            else character.AssignParty(slot, seq % 5 == 0);
        }

        var monitorTask = MonitorLoopAsync(options, characters, context, cancellationToken);
        context.RegisterClient = registerClient;
        // actor 协程覆盖全部角色（不止当前在线）：阶段一注册失败的角色在协程内持续补注册，
        // 服务端重启窗口导致的批量拒绝不再造成角色永久缺席。
        var actorTasks = characters.Select(character => character.RunForeverAsync(cancellationToken)).ToArray();

        if (options.DurationSeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), cancellationToken); }
            catch (OperationCanceledException) { }
            foreach (var character in characters) character.RequestStop();
            await Task.WhenAll(actorTasks);
        }
        else
        {
            await Task.WhenAll(actorTasks); // 常驻：直到取消令牌触发。
        }
        try { await monitorTask.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); } catch { }

        return Report(options, started, context, warnings, null);
    }

    /// <summary>
    /// 分工矩阵：组队 30%（5 倍数）/ 其余 7战:2采:1造游交替。
    /// 斗法仅对战斗远征生效，故单人战斗员占满 7/10（全员开启斗法），使斗法开启者≈半数全员。
    /// </summary>
    private static List<FleetRole> BuildRoster(int players)
    {
        var roster = new List<FleetRole>(players);
        var partyCount = Math.Max(0, (int)Math.Floor(players * 0.30 / 5.0) * 5);
        for (var i = 0; i < players; i++)
        {
            if (i < partyCount) roster.Add(FleetRole.PartyMember);
            else
            {
                var mod = (i - partyCount) % 10;
                roster.Add(mod switch
                {
                    < 7 => FleetRole.SoloCombat,
                    < 9 => FleetRole.Gatherer,
                    _ => (i % 2) == 0 ? FleetRole.Crafter : FleetRole.Roamer,
                });
            }
        }
        return roster;
    }

    /// <summary>周期采集 ops 端点与数据库计数；任何异常输出 anomaly 行并落盘 fleet-status.log。</summary>
    private static async Task MonitorLoopAsync(LoadTestOptions options, FleetCharacter[] characters, FleetContext context, CancellationToken cancellationToken)
    {
        var stats = context.Stats;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("X-Ops-Token", "daohua-local-ops");
        var baseUri = DeriveHttpBase(options.ServerUri).ToString().TrimEnd('/');
        NpgsqlDataSource? db = null;
        try { db = NpgsqlDataSource.Create(options.DatabaseConnectionString); }
        catch (Exception exception) { stats.Anomaly($"数据库连接串无效：{exception.Message}"); }
        var statusPath = Path.Combine(Directory.GetCurrentDirectory(), "fleet-status.log");
        long rewardBaseline = -1, sessionBaseline = -1, killBaseline = -1, tradeBaseline = -1, settleBaseline = -1;
        DateTimeOffset baselineTime = default;

        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (OperationCanceledException) { break; }
            try
            {
                var healthResponse = await client.GetAsync($"{baseUri}/healthz", cancellationToken);
                if (!healthResponse.IsSuccessStatusCode) stats.Anomaly($"healthz 非 200：{(int)healthResponse.StatusCode}");
                var readyResponse = await client.GetAsync($"{baseUri}/readyz", cancellationToken);
                if (!readyResponse.IsSuccessStatusCode) stats.Anomaly($"readyz 非 200：{(int)readyResponse.StatusCode}");

                var overview = await client.GetStringAsync($"{baseUri}/ops/api/overview", cancellationToken);
                using var overviewDoc = JsonDocument.Parse(overview);
                long sessions = 0, loadedPlayers = 0;
                foreach (var property in overviewDoc.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("sessions")) sessions = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt64() : 0;
                    if (property.NameEquals("loadedPlayers")) loadedPlayers = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt64() : 0;
                }

                long partySessions = -1, simRewards = -1, participationSettlements = -1, pendingFlush = -1, players = -1, combatKills = -1, outbox = -1,
                     marketTrades = -1, openOrders = -1;
                if (db is not null)
                {
                    try
                    {
                        await using var connection = await db.OpenConnectionAsync(cancellationToken);
                        if (baselineTime == default)
                        {
                            // 首次全量采样：transactions 已千万级，一次性重扫建基线（放宽单语句超时），
                            // 与 now() 同语句保证快照一致，此后全部转为时间窗增量计数。
                            await using var command = new NpgsqlCommand("""
                                SELECT now(),
                                  (SELECT count(*) FROM social.party_sessions),
                                  (SELECT count(*) FROM economy.transactions WHERE transaction_type='simulation.reward'),
                                  (SELECT count(*) FROM economy.transactions WHERE transaction_type='participation.settlement'),
                                  -- 战斗掉落资产（怪物的 dropTable 独有产物，采集/制造不产出）：一条行≈一场击杀结算
                                  (SELECT count(*) FROM economy.transaction_lines WHERE asset_id LIKE 'item.fragment.%' OR asset_id LIKE 'item.tome.%'
                                     OR asset_id IN ('item.beast-bone','item.beast-fang','item.beast-hide','item.broken-blade','item.broken-spirit-core',
                                                     'item.demon-core','item.demon-dan','item.fire-feather','item.meteor-fragment','item.sha-bone',
                                                     'item.sha-fang','item.soul-talisman')),
                                  (SELECT count(*) FROM market.trades)
                                """, connection) { CommandTimeout = 180 };
                            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                            if (await reader.ReadAsync(cancellationToken))
                            {
                                baselineTime = reader.GetFieldValue<DateTimeOffset>(0);
                                sessionBaseline = reader.GetInt64(1); rewardBaseline = reader.GetInt64(2);
                                settleBaseline = reader.GetInt64(3); killBaseline = reader.GetInt64(4);
                                tradeBaseline = reader.GetInt64(5);
                            }
                        }
                        else
                        {
                            // 增量采样：大表全部走 created_at 时间窗，成本与近期交易量成正比而非全表；
                            // 击杀行经事务时间窗关联（掉落行可来自 simulation.reward 或组队结算两种事务，不按类型过滤）。
                            await using var command = new NpgsqlCommand("""
                                SELECT
                                  (SELECT count(*) FROM social.party_sessions),
                                  (SELECT count(*) FROM economy.transactions WHERE created_at > $1 AND transaction_type='simulation.reward'),
                                  (SELECT count(*) FROM economy.transactions WHERE created_at > $1 AND transaction_type='participation.settlement'),
                                  (SELECT count(*) FROM platform.section_flush_queue),
                                  (SELECT count(*) FROM identity.players),
                                  (SELECT count(*) FROM economy.transaction_lines tl
                                     WHERE (tl.asset_id LIKE 'item.fragment.%' OR tl.asset_id LIKE 'item.tome.%'
                                        OR tl.asset_id IN ('item.beast-bone','item.beast-fang','item.beast-hide','item.broken-blade','item.broken-spirit-core',
                                                           'item.demon-core','item.demon-dan','item.fire-feather','item.meteor-fragment','item.sha-bone',
                                                           'item.sha-fang','item.soul-talisman'))
                                       AND tl.transaction_id IN (SELECT transaction_id FROM economy.transactions WHERE created_at > $1)),
                                  (SELECT count(*) FROM platform.integration_outbox WHERE delivered_at IS NULL),
                                  (SELECT count(*) FROM market.trades),
                                  (SELECT count(*) FROM market.orders WHERE status IN ('active','partially_filled'))
                                """, connection);
                            command.Parameters.Add(new NpgsqlParameter { Value = baselineTime });
                            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                            if (await reader.ReadAsync(cancellationToken))
                            {
                                partySessions = reader.GetInt64(0);
                                simRewards = rewardBaseline + reader.GetInt64(1);
                                participationSettlements = settleBaseline + reader.GetInt64(2);
                                pendingFlush = reader.GetInt64(3); players = reader.GetInt64(4);
                                combatKills = killBaseline + reader.GetInt64(5);
                                outbox = reader.GetInt64(6); marketTrades = reader.GetInt64(7); openOrders = reader.GetInt64(8);
                            }
                        }
                    }
                    catch (Exception exception) { stats.Anomaly($"监控数据库采样失败：{exception.Message}"); }
                }

                var onlineCount = characters.Count(character => character.IsOnline);
                var loopCount = characters.Count(character => character.IsInLoopPhase);
                var line = $"[{DateTimeOffset.Now:HH:mm:ss}] online={onlineCount} loop={loopCount} players={players} loaded={loadedPlayers} sessions={sessions} " +
                           $"partySessions={partySessions}(+{(sessionBaseline < 0 ? 0 : partySessions - sessionBaseline)}) " +
                           $"simRewards={simRewards}(+{(rewardBaseline < 0 ? 0 : simRewards - rewardBaseline)}) " +
                           $"soloKills={combatKills}(+{(killBaseline < 0 ? 0 : combatKills - killBaseline)}) " +
                           $"partSettle={participationSettlements} flushQ={pendingFlush} outbox={outbox} " +
                           $"cmdOk={Interlocked.Read(ref stats.CommandsOk)} cmdFail={Interlocked.Read(ref stats.CommandsFailed)} " +
                           $"errFrames={Interlocked.Read(ref stats.ErrorFrames)} equipped={Interlocked.Read(ref stats.Equipped)} " +
                           $"pvp={Interlocked.Read(ref stats.SoloPvpEnabled)} " +
                           $"trades={marketTrades}(+{(tradeBaseline < 0 ? 0 : marketTrades - tradeBaseline)}) orders={openOrders} " +
                           $"placed={Interlocked.Read(ref stats.MarketPlaced)} claimed={Interlocked.Read(ref stats.MarketClaimed)} " +
                           $"mktFail={Interlocked.Read(ref stats.MarketFailed)} itemOps={Interlocked.Read(ref stats.ItemOps)}";
                Console.WriteLine($"[fleet][monitor] {line}");
                await File.AppendAllTextAsync(statusPath, line + Environment.NewLine, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            // HttpClient 超时抛的是 TaskCanceledException（OCE 子类）：那是一次采样失败而非退出信号，
            // 若按 is not OperationCanceledException 过滤，监控协程在首个超时后即静默死亡。
            catch (Exception exception)
            {
                stats.Anomaly($"监控协程异常：{exception.Message}");
            }
        }
    }

    private static LoadTestReport Report(LoadTestOptions options, long started, FleetContext context, List<string> warnings, string? fatal)
    {
        var stats = context.Stats;
        if (fatal is not null) warnings.Add(fatal);
        var errors = (fatal is null ? 0 : 1)
            + (int)Math.Min(100000, Interlocked.Read(ref stats.CommandsFailed))
            + (int)Math.Min(100000, Interlocked.Read(ref stats.RegisterFailed));
        return new LoadTestReport("loadtest.v1", "fleet", options.Players, options.DurationSeconds,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, 0, 0, 0, 0, 0,
            Interlocked.Read(ref stats.CommandsSent) + Interlocked.Read(ref stats.SocialSent),
            Interlocked.Read(ref stats.CommandsOk) + Interlocked.Read(ref stats.SocialSent),
            errors, errors == 0 && fatal is null,
            new Dictionary<string, object?>
            {
                ["registered"] = Interlocked.Read(ref stats.Registered),
                ["connected"] = Interlocked.Read(ref stats.Connected),
                ["handshaken"] = Interlocked.Read(ref stats.Handshaken),
                ["bootstrapped"] = Interlocked.Read(ref stats.Bootstrapped),
                ["equipped"] = Interlocked.Read(ref stats.Equipped),
                ["soloPvpEnabled"] = Interlocked.Read(ref stats.SoloPvpEnabled),
                ["commandsOk"] = Interlocked.Read(ref stats.CommandsOk),
                ["commandsFailed"] = Interlocked.Read(ref stats.CommandsFailed),
                ["socialFailed"] = Interlocked.Read(ref stats.SocialFailed),
                ["errorFrames"] = Interlocked.Read(ref stats.ErrorFrames),
                ["reconnects"] = Interlocked.Read(ref stats.Reconnects),
                ["parties"] = context.PartyMembers.Length,
                ["anomalies"] = stats.Anomalies.Take(200).ToArray(),
            }, warnings);
    }

    private static Uri DeriveHttpBase(Uri serverUri) => serverUri.Scheme switch
    {
        "ws" => new UriBuilder(serverUri) { Scheme = "http", Path = "/", Query = "", Fragment = "" }.Uri,
        "wss" => new UriBuilder(serverUri) { Scheme = "https", Path = "/", Query = "", Fragment = "" }.Uri,
        _ => new UriBuilder(serverUri) { Path = "/", Query = "", Fragment = "" }.Uri,
    };

    private static Uri DeriveWebSocketUri(Uri serverUri) => new UriBuilder(serverUri)
    {
        Scheme = serverUri.Scheme switch { "http" => "ws", "https" => "wss", _ => serverUri.Scheme },
        Path = "/ws", Query = string.Empty, Fragment = string.Empty,
    }.Uri;

    /// <summary>
    /// 单个仿真角色：独立账号/Cookie/WebSocket/设备指纹/来源 IP。
    /// 主循环为幂等 tick：每次迭代检查在线态与行动队列，缺什么补什么，
    /// 断线自动重连后由队列检查自然恢复分工，不依赖一次性脚本顺序。
    /// </summary>
    private sealed class FleetCharacter
    {
        public readonly int Index;
        private readonly string _account;
        private readonly string _password;
        public FleetRole RoleOf { get; private set; }
        private readonly Uri _httpBase;
        private readonly Uri _wsUri;
        private readonly FleetContext _context;
        private FleetStats Stats => _context.Stats;
        private readonly string _clientIp;
        private readonly string _deviceId = Guid.NewGuid().ToString("D");
        private readonly string _deviceFingerprint = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        private readonly string _deviceConfigHash;
        private string? _cookie;
        public Guid PlayerId { get; private set; }
        private volatile ClientWebSocket? _ws;
        private long _sequence;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCommands = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingSocial = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, JsonElement> _sections = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, double> _assets = new(StringComparer.Ordinal);
        private volatile bool _stop;
        private volatile bool _inLoop;
        private volatile bool _bootstrapped;
        private int _partySlot = -1;
        private bool _partyLeader;
        private bool _partyCreated;
        private string? _partyNode;
        // 以下字段被接收线程（OnSocialState）与角色 tick 线程并发读写：bool 用 volatile，
        // 64 位时间戳与成员名册走 Interlocked/并发集合，普通 HashSet 在长跑中已被实锤会写坏。
        private volatile bool _partySessionActive;
        private volatile bool _inExpectedParty;
        private long _sessionStartedAtUnixMs;
        private readonly ConcurrentDictionary<Guid, byte> _partyMemberIds = new();
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _inviteSentAt = new(); // 队长补邀请节流：目标成员 → 上次发送时间
        private readonly ConcurrentDictionary<string, Guid> _pendingInviteTargets = new(StringComparer.Ordinal); // 在途 party.invite：requestId → 目标成员
        private int _roamCursor;
        // 市场/物品使用 ambient tick 状态：全部仅被角色 tick 线程访问，无需并发容器。
        private DateTimeOffset _nextMarketAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastAmbientErrorAt = DateTimeOffset.MinValue;
        private int _marketCycle;
        private readonly Queue<(string ItemId, Guid OrderId)> _myOrders = new();   // 自有挂单登记（容量封顶滚动撤销）
        // 每标的至多一笔活动挂单（服务端 OwnerOrderExists 约束）：itemId → 当前活动单，重挂前必须先撤旧单。
        private readonly Dictionary<string, Guid> _openOrders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unlearnableItems = new(StringComparer.Ordinal); // 研读被拒的物品（如未习得功法的残卷）
        private bool _potionConfigured;
        private bool _explorePillConfigured;

        public bool IsOnline => _ws?.State == WebSocketState.Open && PlayerId != Guid.Empty;
        public bool IsInLoopPhase => _inLoop;
        public void DemoteTo(FleetRole role) => RoleOf = role;
        public void AssignParty(int slot, bool leader)
        {
            _partySlot = slot;
            _partyLeader = leader;
            _partyNode = slot % 2 == 0 ? PeachForest : RockBeach;
        }

        public FleetCharacter(int index, string runToken, FleetRole role, Uri httpBase, Uri wsUri, FleetContext context)
        {
            Index = index; RoleOf = role; _httpBase = httpBase; _wsUri = wsUri; _context = context;
            _account = $"fleet_{runToken}_{index:D4}";
            _password = $"Fleet#{runToken}";
            _clientIp = $"10.{16 + index / 256}.{index % 256}.{1 + index % 200}";
            _deviceConfigHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_account)));
        }

        public void RequestStop() => _stop = true;

        /// <summary>
        /// 阶段一注册驱动：HTTP 建号 + WS 握手各为独立步进，外层做 12 次带抖动的有界重试。
        /// 服务端重启窗口内 Connection refused 会毫秒级快速失败，单次尝试能把整批 500 任务
        /// 在几十秒内全部烧光（v3 实测 456/500 永久出局）——重试是唯一止血点。
        /// </summary>
        public async Task RegisterAndConnectAsync(HttpClient client, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 12 && !IsOnline && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(4 + Random.Shared.Next(0, 9)), cancellationToken);
                await EnsureConnectedAsync(client, cancellationToken);
            }
        }

        /// <summary>补全连接的单次步进：无账号先 HTTP 注册，有会话票据再建 WS 握手；角色协程亦复用。</summary>
        public async Task EnsureConnectedAsync(HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                if (PlayerId == Guid.Empty || _cookie is null)
                    await TryRegisterAccountAsync(client, cancellationToken);
                if (PlayerId != Guid.Empty && _cookie is not null && !IsOnline)
                    await ConnectSocketAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            // 注册 HTTP 20s / WS 连接 15s 的内部超时抛 TaskCanceledException：按单角色失败记账，
            // 不能让一次慢握手穿透出去经 Task.WhenAll 掀翻整场仿真。
            catch (Exception exception)
            {
                Interlocked.Increment(ref Stats.RegisterFailed);
                if (Interlocked.Read(ref Stats.RegisterFailed) <= 10) Stats.Anomaly($"注册异常 {_account}: {exception.Message}");
            }
        }

        /// <summary>注册账号换取会话 Cookie；失败计入异常不阻塞其他角色。成功后重入本方法不再重复注册。</summary>
        private async Task TryRegisterAccountAsync(HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    account = _account,
                    password = _password,
                    displayName = _account,
                    riskSignals = new
                    {
                        deviceId = _deviceId,
                        deviceConfigHash = _deviceConfigHash,
                        networkHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("net:" + _account))),
                        browserFingerprint = _deviceFingerprint,
                    }
                }, ProtocolJson.Options);
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register") { Content = content };
                if (_httpBase.IsLoopback) request.Headers.Add("X-Forwarded-For", _clientIp);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    Interlocked.Increment(ref Stats.RegisterFailed);
                    if (Interlocked.Read(ref Stats.RegisterFailed) <= 10)
                        Stats.Anomaly($"注册失败 {_account}: HTTP {(int)response.StatusCode} {body[..Math.Min(200, body.Length)]}");
                    return;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                PlayerId = doc.RootElement.GetProperty("playerId").GetGuid();
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                    _cookie = cookies.Select(value => value.Split(';')[0])
                        .FirstOrDefault(value => value.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));
                if (_cookie is null) { Interlocked.Increment(ref Stats.RegisterFailed); Stats.Anomaly($"注册无会话 Cookie：{_account}"); return; }
                Interlocked.Increment(ref Stats.Registered);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Interlocked.Increment(ref Stats.RegisterFailed);
                if (Interlocked.Read(ref Stats.RegisterFailed) <= 10) Stats.Anomaly($"注册异常 {_account}: {exception.Message}");
            }
        }

        /// <summary>建立 WebSocket：发送 Hello，等待首个 Snapshot 落入分段视图视为握手完成。</summary>
        private async Task ConnectSocketAsync(CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.SetRequestHeader("Cookie", _cookie!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(_wsUri, timeout.Token);
            _ws = socket;
            _ = Task.Run(() => ReceiveLoopAsync(socket), CancellationToken.None);
            await SendFrameAsync(socket, WireMessageType.Hello, Guid.NewGuid(),
                new HelloRequest("fleet-loadtest", null, _deviceFingerprint, null, _deviceId), cancellationToken);
            var handshakeDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < handshakeDeadline && !cancellationToken.IsCancellationRequested)
            {
                if (_sections.ContainsKey("world")) { Interlocked.Increment(ref Stats.Handshaken); Interlocked.Increment(ref Stats.Connected); return; }
                await Task.Delay(150, cancellationToken);
            }
            Stats.Anomaly($"握手超时：{_account}");
        }

        /// <summary>常驻主循环：未 bootstrap 先跑启动序列；之后按分工 tick 直到停止。</summary>
        public async Task RunForeverAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !_stop)
                {
                    // 整轮包进 try：重连/bootstrap/tick 任一失败只记 anomaly 并退避重试，
                    // 角色常驻是本场景语义，单次异常不得杀死协程。
                    try
                    {
                        if (!IsOnline)
                        {
                            // 从未注册成功（无票据）的角色走补注册路径，已注册断线的走重连。
                            if (PlayerId == Guid.Empty || _cookie is null)
                                await EnsureConnectedAsync(_context.RegisterClient!, cancellationToken);
                            else
                                await ReconnectAsync(cancellationToken);
                            continue;
                        }
                        if (!_bootstrapped)
                        {
                            _bootstrapped = await BootstrapAsync(cancellationToken);
                            if (!_bootstrapped) { await Task.Delay(3000, cancellationToken); continue; }
                            Interlocked.Increment(ref Stats.Bootstrapped);
                            _inLoop = true;
                        }
                        await RoleTickAsync(cancellationToken);
                        await AmbientTickAsync(cancellationToken);
                        await Task.Delay(2500, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        Stats.Anomaly($"角色 tick 异常 {_account}: {exception.Message}");
                        await Task.Delay(5000, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Stats.Anomaly($"角色协程异常 {_account}: {exception.Message}"); }
            finally { _inLoop = false; }
        }

        /// <summary>断线恢复：重连 WS 并重新握手；服务端行动队列持久，恢复后队列检查驱动幂等补齐。</summary>
        private async Task ReconnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                _ws?.Abort();
                await Task.Delay(2000, cancellationToken);
                await ConnectSocketAsync(cancellationToken);
                Interlocked.Increment(ref Stats.Reconnects);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            // 连接超时以 TaskCanceledException 上抛：记 anomaly 后继续重试，不外溢杀死角色协程。
            catch (Exception exception)
            {
                Stats.Anomaly($"重连失败 {_account}: {exception.Message}");
                await Task.Delay(5000, cancellationToken);
            }
        }

        // ---------- 推送状态视图 ----------

        private string? NodeId()
        {
            if (_sections.TryGetValue("world", out var world)
                && world.TryGetProperty("nodeId", out var node) && node.ValueKind == JsonValueKind.String)
                return node.GetString();
            return null;
        }

        private bool OnEdge()
            => _sections.TryGetValue("world", out var world)
                && world.TryGetProperty("edgeId", out var edge)
                && edge.ValueKind == JsonValueKind.String;

        private int ActionQueueCount()
        {
            if (_sections.TryGetValue("action_queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
                return queue.GetArrayLength();
            return -1;
        }

        private double Asset(string assetId) => _assets.TryGetValue(assetId, out var quantity) ? quantity : 0;

        private async Task SendFrameAsync<T>(ClientWebSocket socket, WireMessageType type, Guid requestId, T payload, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) return;
            var frame = WireEnvelopeCodec.EncodeJson(type, requestId, Interlocked.Increment(ref _sequence), payload);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
        }

        /// <summary>发送通用命令并等待回执；action.enqueue 类载荷内 requestId 与信封一致由工厂函数保证。</summary>
        private async Task<(bool Ok, string Code, string Message)> SendCommandAsync(
            string command, Func<string, object> payloadFactory, CancellationToken cancellationToken, int timeoutSeconds = 20)
        {
            var requestId = Guid.NewGuid();
            var key = requestId.ToString("D");
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommands[key] = tcs;
            var envelope = new CommandEnvelopeDto(key, command,
                JsonSerializer.SerializeToElement(payloadFactory(key), ProtocolJson.Options), 0);
            Interlocked.Increment(ref Stats.CommandsSent);
            var socket = _ws;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                _pendingCommands.TryRemove(key, out _);
                return (false, "OFFLINE", "连接不可用");
            }
            try { await SendFrameAsync(socket, WireMessageType.Command, requestId, envelope, cancellationToken); }
            catch (Exception exception) { Interlocked.Increment(ref Stats.CommandsFailed); _pendingCommands.TryRemove(key, out _); return (false, "SEND_FAILED", exception.Message); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var result = await tcs.Task.WaitAsync(timeout.Token);
                var succeeded = result.TryGetProperty("succeeded", out var s) && s.GetBoolean();
                var code = result.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var message = result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                if (succeeded) Interlocked.Increment(ref Stats.CommandsOk); else Interlocked.Increment(ref Stats.CommandsFailed);
                return (succeeded, code, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref Stats.CommandsFailed);
                return (false, "TIMEOUT", "命令回执超时");
            }
            finally { _pendingCommands.TryRemove(key, out _); }
        }

        /// <summary>发送社交指令并等待 RequestId 配对的 SocialState 回执。</summary>
        private async Task<JsonElement?> SendSocialAsync(string action, string? targetPlayerId = null, PartyProfileDto? party = null)
        {
            var requestId = Guid.NewGuid();
            var key = requestId.ToString("D");
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSocial[key] = tcs;
            // 记录邀请目标供失败回执（Error 帧）按 requestId 反查成员。
            if (action == SocialActions.PartyInvite && Guid.TryParse(targetPlayerId, out var inviteTarget))
                _pendingInviteTargets[key] = inviteTarget;
            var dto = new SocialActionDto(key, action, targetPlayerId, null, null, null, party);
            Interlocked.Increment(ref Stats.SocialSent);
            var socket = _ws;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                _pendingSocial.TryRemove(key, out _);
                _pendingInviteTargets.TryRemove(key, out _);
                return null;
            }
            try { await SendFrameAsync(socket, WireMessageType.SocialAction, requestId, dto, CancellationToken.None); }
            catch { Interlocked.Increment(ref Stats.SocialFailed); _pendingSocial.TryRemove(key, out _); _pendingInviteTargets.TryRemove(key, out _); return null; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { return await tcs.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Stats.SocialFailed); return null; }
            finally { _pendingSocial.TryRemove(key, out _); _pendingInviteTargets.TryRemove(key, out _); }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (socket.State == WebSocketState.Open && !_stop)
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    var bytes = (int)stream.Length;
                    if (!WireEnvelopeCodec.TryDecode(stream.GetBuffer().AsSpan(0, bytes), out var envelope))
                    {
                        Interlocked.Increment(ref Stats.ErrorFrames);
                        continue;
                    }
                    switch (envelope.MessageType)
                    {
                        case WireMessageType.Snapshot:
                            if (WireEnvelopeCodec.TryDeserializeJson<JsonElement>(envelope, out var snapshot))
                                ApplySnapshot(snapshot);
                            break;
                        case WireMessageType.SnapshotDelta:
                            if (WireEnvelopeCodec.TryDeserializeJson<JsonElement>(envelope, out var delta))
                                ApplyDelta(delta);
                            break;
                        case WireMessageType.CommandResult:
                            if (WireEnvelopeCodec.TryDeserializeJson<JsonElement>(envelope, out var commandResult)
                                && commandResult.TryGetProperty("requestId", out var rid)
                                && _pendingCommands.TryGetValue(rid.GetString() ?? "", out var pending))
                            {
                                if (commandResult.TryGetProperty("snapshot", out var snap) && snap.ValueKind == JsonValueKind.Object)
                                {
                                    if (snap.TryGetProperty("sections", out _)) ApplySnapshot(snap);
                                    else ApplyDelta(snap);
                                }
                                pending.TrySetResult(commandResult);
                            }
                            break;
                        case WireMessageType.SocialState:
                            if (WireEnvelopeCodec.TryDeserializeJson<JsonElement>(envelope, out var social))
                            {
                                if (social.TryGetProperty("requestId", out var srid) && srid.ValueKind == JsonValueKind.String
                                    && _pendingSocial.TryGetValue(srid.GetString()!, out var pendingSocial))
                                    pendingSocial.TrySetResult(social);
                                OnSocialState(social);
                            }
                            break;
                        case WireMessageType.Error:
                            Interlocked.Increment(ref Stats.ErrorFrames);
                            if (WireEnvelopeCodec.TryDeserializeJson<JsonElement>(envelope, out var error))
                                OnErrorFrame(error);
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_stop) Stats.Anomaly($"接收循环终止 {_account}: {exception.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                // 断线即失败所有挂起回执：防止发送方空等超时，主循环经 IsOnline 走重连路径。
                foreach (var pending in _pendingCommands.Values) pending.TrySetCanceled();
                foreach (var pending in _pendingSocial.Values) pending.TrySetCanceled();
            }
        }

        private void ApplySnapshot(JsonElement snapshot)
        {
            if (snapshot.TryGetProperty("playerId", out var pid) && pid.ValueKind == JsonValueKind.String
                && Guid.TryParse(pid.GetString(), out var parsed) && PlayerId == Guid.Empty)
                PlayerId = parsed;
            if (snapshot.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                foreach (var section in sections.EnumerateArray())
                    if (section.TryGetProperty("section", out var name) && section.TryGetProperty("data", out var data))
                        _sections[name.GetString() ?? ""] = data.Clone();
            if (snapshot.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                    if (asset.TryGetProperty("assetId", out var id) && asset.TryGetProperty("quantity", out var quantity))
                        _assets[id.GetString() ?? ""] = quantity.GetDouble();
        }

        private void ApplyDelta(JsonElement delta)
        {
            if (delta.TryGetProperty("changedSections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                foreach (var section in sections.EnumerateArray())
                    if (section.TryGetProperty("section", out var name) && section.TryGetProperty("data", out var data))
                        _sections[name.GetString() ?? ""] = data.Clone();
            if (delta.TryGetProperty("changedAssets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                    if (asset.TryGetProperty("assetId", out var id))
                    {
                        var key = id.GetString() ?? "";
                        if (asset.TryGetProperty("quantity", out var quantity)) _assets[key] = quantity.GetDouble();
                        else if (asset.TryGetProperty("quantityDelta", out var quantityDelta)) _assets[key] = Asset(key) + quantityDelta.GetDouble();
                    }
        }

        /// <summary>社交推送：组队成员收到邀请自动接受；维护会话活跃标记与队伍名册视图。</summary>
        private void OnSocialState(JsonElement social)
        {
            if (RoleOf == FleetRole.PartyMember
                && social.TryGetProperty("partyInvitation", out var invitation)
                && invitation.ValueKind == JsonValueKind.Object)
            {
                _ = Task.Run(() => SendSocialAsync(SocialActions.PartyAccept));
            }
            if (!social.TryGetProperty("party", out var party)) return;
            if (party.ValueKind == JsonValueKind.Object)
            {
                var sessionActive = party.TryGetProperty("activeSession", out var session)
                    && session.ValueKind == JsonValueKind.Object;
                if (sessionActive && !_partySessionActive)
                    Interlocked.Exchange(ref _sessionStartedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _partySessionActive = sessionActive;
                if (party.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                {
                    _partyMemberIds.Clear();
                    foreach (var member in members.EnumerateArray())
                        if (member.TryGetProperty("playerId", out var memberId) && Guid.TryParse(memberId.GetString(), out var parsed))
                            _partyMemberIds.TryAdd(parsed, 0);
                }
                var leaderId = PartyLeaderId(party);
                _inExpectedParty = RoleOf == FleetRole.PartyMember && _partySlot >= 0
                    && _partySlot < _context.PartyMembers.Length
                    && leaderId == _context.PartyMembers[_partySlot][0].PlayerId;
                // 自愈：确认身处他队（leaderId 已解析且与本队编制队长不符）才离队——
                // 否则队长的补邀请永远吃 SOCIAL_TARGET_IN_PARTY，本队凑不满槽位不再开战。
                if (RoleOf == FleetRole.PartyMember && _partySlot >= 0 && leaderId is not null && !_inExpectedParty)
                    _ = Task.Run(() => SendSocialAsync(SocialActions.PartyLeave));
            }
            else
            {
                _partySessionActive = false;
                _inExpectedParty = false;
                _partyMemberIds.Clear();
            }
        }

        /// <summary>解析 party 推送中的 leaderId；缺失或非字符串返回 null。</summary>
        private static Guid? PartyLeaderId(JsonElement party)
            => party.TryGetProperty("leaderId", out var element) && element.ValueKind == JsonValueKind.String
                && Guid.TryParse(element.GetString(), out var id) ? id : null;

        /// <summary>
        /// 错误帧：社交指令失败时服务端以 Error 帧回包（无 SocialState 回执），按 requestId 解除挂起等待。
        /// SOCIAL_TARGET_IN_PARTY 只记节流不补名册——目标多半卡在别队（成员侧按 leaderId 自愈离队），
        /// 误标已入队会让它永远等不到补邀请；本队推送滞后的同队情形会由 members 视图自然覆盖。
        /// </summary>
        private void OnErrorFrame(JsonElement error)
        {
            var requestId = error.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;
            if (requestId is not null && _pendingSocial.TryGetValue(requestId, out var pendingSocial))
                pendingSocial.TrySetCanceled();
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (code == "SOCIAL_TARGET_IN_PARTY" && requestId is not null
                && _pendingInviteTargets.TryGetValue(requestId, out var memberId))
            {
                _inviteSentAt[memberId] = DateTimeOffset.UtcNow;
                return; // 预期内噪音，不刷 anomaly。
            }
            var text = error.GetRawText();
            Stats.Anomaly($"错误帧 {_account}: {text[..Math.Min(240, text.Length)]}");
        }

        // ---------- Bootstrap：跳引导 → 狩猎/采集备料 → 精炼 → 锻造 → 装配 ----------

        private async Task<bool> BootstrapAsync(CancellationToken cancellationToken)
        {
            if (Asset("item.ningxue-grass") < 3)
            {
                var skip = await SendCommandAsync("tutorial.skip", _ => new { }, cancellationToken);
                if (!skip.Ok) Stats.Anomaly($"tutorial.skip 失败 {_account}: {skip.Code} {skip.Message}");
            }

            // 行动队列默认关闭 loopEnabled；采集/精炼/狩猎按资产余额等待，达到目标后 queue.clear 清掉残留行动。

            // ① 引导剑：跳引导补发百炼铁×7+兽骨×1 恰为青锋剑一轮材料，先持剑再谈狩猎。
            if (!await EnsureForgedAsync("forge.qingfeng-sword", "equipment.qingfeng-sword", cancellationToken)) return false;
            if (!await EquipOwnedAsync(cancellationToken)) return false;

            // 缺口判定：终件已齐视作上游完成，重入直接跳过对应段（GearSet[0]=剑、[1..4]=铁甲四件、[5]=桃木符）。
            var talismanMissing = Asset("equipment.taomu-talisman") < 1;
            var ironGearMissing = GearSet.Skip(1).Take(4).Any(item => Asset(item.Equipment) < 1);
            var needCore = talismanMissing && Asset("item.broken-spirit-core") < 1;
            var needCrystal = talismanMissing && Asset("item.peach-wood-crystal") < 5;
            var needIron = ironGearMissing && Asset("item.refined-iron") < 22;
            var needHide = ironGearMissing && Asset("item.beast-hide") < 4;

            // ② 碎灵核：木灵 100% 掉落（溪谷池，Lv1+剑即引导设计的对战基线）。
            if (needCore && !await HuntUntilAsync(Village, "item.broken-spirit-core", 1, TimeSpan.FromMinutes(8), cancellationToken)) return false;

            // ③ 原材采集：桃木→桃木晶、玄铁→百炼铁。
            foreach (var (node, action, repeats, material, needed) in BootstrapGather)
            {
                var downstreamMet = material == "item.peach-wood" ? !needCrystal : !needIron;
                if (downstreamMet || Asset(material) >= needed) continue;
                if (!await GatherUntilAsync(node, action, repeats, material, needed, cancellationToken)) return false;
            }

            // ④ 精炼就地执行（无节点限制）。
            foreach (var (action, output, needed) in BootstrapRefine)
            {
                var downstreamMet = output == "item.peach-wood-crystal" ? !needCrystal : !needIron;
                if (downstreamMet || Asset(output) >= needed) continue;
                if (!await RefineUntilAsync(action, output, needed, cancellationToken)) return false;
            }

            // ⑤ 桃木符并补装：刷兽皮前尽量满配。
            if (talismanMissing && !await EnsureForgedAsync("forge.taomu-talisman", "equipment.taomu-talisman", cancellationToken)) return false;
            if (!await EquipOwnedAsync(cancellationToken)) return false;

            // ⑥ 兽皮×4：魔化野兔 100% 掉落（日落岭池）；精英熊波致阵亡由复活相位续跑，
            // 等待超时返回 false 后整体幂等重入续刷，不视作硬失败。
            if (needHide && !await HuntUntilAsync(RockBeach, "item.beast-hide", 4, TimeSpan.FromMinutes(20), cancellationToken)) return false;

            // ⑦ 锻齐剩余件并全装校验。
            foreach (var (equipment, forge) in GearSet)
                if (Asset(equipment) < 1 && !await EnsureForgedAsync(forge, equipment, cancellationToken)) return false;
            foreach (var (equipment, _) in GearSet)
                if (Asset(equipment) < 1) { Stats.Anomaly($"锻造产出缺失 {_account}: {equipment}"); return false; }
            if (!await EquipOwnedAsync(cancellationToken)) return false;
            Interlocked.Increment(ref Stats.Equipped);
            return true;
        }

        /// <summary>单轮锻造并等待产出到手；入队被拒或产出超时返回 false（上游备料不足的征兆）。</summary>
        private async Task<bool> EnsureForgedAsync(string forgeAction, string output, CancellationToken cancellationToken)
        {
            if (Asset(output) >= 1) return true;
            await ClearQueueAsync(cancellationToken);
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = forgeAction, repeatCount = 1,
            }, cancellationToken);
            if (!enqueue.Ok) { Stats.Anomaly($"锻造入队失败 {_account} {forgeAction}: {enqueue.Code} {enqueue.Message}"); return false; }
            if (!await WaitAssetAsync(output, 1, TimeSpan.FromSeconds(90), cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            return true;
        }

        /// <summary>定点采集至目标量：赶路落地后按轮数入队，按资产余额等待，达标清队。</summary>
        private async Task<bool> GatherUntilAsync(string node, string action, int repeats, string material, double needed, CancellationToken cancellationToken)
        {
            if (!await TravelAndSettleAsync(node, cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = action, repeatCount = repeats,
            }, cancellationToken);
            if (!enqueue.Ok) { Stats.Anomaly($"采集入队失败 {_account} {action}: {enqueue.Code} {enqueue.Message}"); return false; }
            if (!await WaitAssetAsync(material, needed, TimeSpan.FromSeconds(repeats * 12 + 60), cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            return true;
        }

        /// <summary>就地精炼至目标量：按缺口轮数入队，按制品余额等待，达标清队。</summary>
        private async Task<bool> RefineUntilAsync(string action, string output, double needed, CancellationToken cancellationToken)
        {
            await ClearQueueAsync(cancellationToken);
            var repeats = (int)Math.Ceiling(needed - Asset(output));
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = action, repeatCount = repeats,
            }, cancellationToken);
            if (!enqueue.Ok) { Stats.Anomaly($"精炼入队失败 {_account} {action}: {enqueue.Code} {enqueue.Message}"); return false; }
            if (!await WaitAssetAsync(output, needed, TimeSpan.FromSeconds(repeats * 15 + 60), cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            return true;
        }

        /// <summary>驻点狩猎至掉落达标：∞ 讨伐入队按资产等待；阵亡由远征复活相位自动续跑，不额外处置。</summary>
        private async Task<bool> HuntUntilAsync(string node, string drop, double needed, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Asset(drop) >= needed) return true;
            if (!await TravelAndSettleAsync(node, cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = "combat.hunt-qi-beast", repeatCount = -1,
            }, cancellationToken);
            if (!enqueue.Ok) { Stats.Anomaly($"狩猎入队失败 {_account}: {enqueue.Code} {enqueue.Message}"); return false; }
            if (!await WaitAssetAsync(drop, needed, timeout, cancellationToken)) return false;
            await ClearQueueAsync(cancellationToken);
            return true;
        }

        /// <summary>把当前持有的套装件整套装上（loadout.replace 为全量替换语义，部分件即部分装）。</summary>
        private async Task<bool> EquipOwnedAsync(CancellationToken cancellationToken)
        {
            var owned = GearSet.Where(item => Asset(item.Equipment) >= 1).ToArray();
            if (owned.Length == 0) return true;
            var loadout = await SendCommandAsync("loadout.replace", _ => new
            {
                equipment = owned.Select(item => new { itemId = item.Equipment, enhancementLevel = 0 }).ToArray(),
                abilitySlots = Array.Empty<object>(),
            }, cancellationToken);
            if (!loadout.Ok) { Stats.Anomaly($"装配失败 {_account}: {loadout.Code} {loadout.Message}"); return false; }
            return true;
        }

        /// <summary>赶路到目标节点并等待落地（world.travel 入队优先行动，等待 nodeId 落定）。</summary>
        private async Task<bool> TravelAndSettleAsync(string node, CancellationToken cancellationToken, int timeoutSeconds = 240)
        {
            if (NodeId() == node && !OnEdge()) return true;
            var travel = await SendCommandAsync("world.travel", _ => new { targetNodeId = node }, cancellationToken);
            if (!travel.Ok) { Stats.Anomaly($"赶路失败 {_account} → {node}: {travel.Code} {travel.Message}"); return false; }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested && !_stop && IsOnline)
            {
                if (NodeId() == node && !OnEdge()) return true;
                await Task.Delay(1000, cancellationToken);
            }
            if (IsOnline) Stats.Anomaly($"赶路超时 {_account} → {node}");
            return false;
        }

        /// <summary>清空行动队列（QUEUE_EMPTY 视为正常）。</summary>
        private async Task ClearQueueAsync(CancellationToken cancellationToken)
        {
            if (ActionQueueCount() <= 0) return;
            await SendCommandAsync("queue.clear", _ => new { }, cancellationToken);
        }

        /// <summary>等待单个资产达到目标数量。</summary>
        private async Task<bool> WaitAssetAsync(string assetId, double needed, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested && !_stop && IsOnline)
            {
                if (Asset(assetId) >= needed) return true;
                await Task.Delay(1500, cancellationToken);
            }
            if (IsOnline) Stats.Anomaly($"资产等待超时 {_account}: {assetId}={Asset(assetId)} 需 {needed}");
            return false;
        }

        // ---------- 市场与物品使用 ambient tick（真实资产驱动，随机买卖） ----------

        /// <summary>
        /// 可挂单盈余目录：Keep 为角色自用保留量（采集/炼制自有消耗不被卖光），超出部分才允许进市场。
        /// 所有物品均来自真实采集、战斗掉落或炼制产出——压测不凭空生成资产。
        /// </summary>
        private static readonly (string ItemId, int Keep, double BasePrice)[] SellableItems =
        [
            ("item.ningxue-grass", 40, 5), ("item.morning-dew", 40, 8), ("item.peach-wood", 30, 5),
            ("item.dark-iron", 30, 5), ("item.beast-hide", 12, 6), ("item.beast-bone", 12, 6),
            ("item.beast-fang", 12, 18), ("item.broken-blade", 5, 60), ("item.broken-spirit-core", 8, 12),
            ("item.herb-essence", 6, 20), ("item.qi-gathering-pill", 3, 35), ("item.zhixue-powder", 3, 25),
            ("item.shunqi-pill", 3, 25), ("item.tongmai-pill", 2, 200),
        ];

        /// <summary>买单需求的随机候选——全部是目录内物品，价格基准复用同一表。</summary>
        private static readonly string[] BuyWanted =
        [
            "item.ningxue-grass", "item.morning-dew", "item.peach-wood", "item.dark-iron",
            "item.beast-hide", "item.beast-bone", "item.herb-essence",
            "item.qi-gathering-pill", "item.zhixue-powder",
        ];

        /// <summary>
        /// 低频环境 tick：每角色 ~190-330s 抖动一次 → 500 角色全局 ≈100-150 笔/min 订单流，
        /// 撮合成交目标 ≥60 笔/min；同一节拍顺带做一次真实物品使用。
        /// </summary>
        private async Task AmbientTickAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextMarketAt) return;
            _nextMarketAt = now + TimeSpan.FromSeconds(190 + Random.Shared.Next(0, 140));
            await ItemUsageCycleAsync(cancellationToken);
            await MarketCycleAsync(cancellationToken);
        }

        /// <summary>
        /// 物品使用周期：每条路径都在消耗真实持有物，不做任何凭空生成。
        /// ① 战斗角色配 potion 组战斗药槽（zhixue/shunqi，被真实战斗消耗）
        /// ② 采集角色配 general 组探索消耗槽（聚气丹被真实采集行动消耗）
        /// ③ 秘籍/残卷研读（ability.learn 真实扣物品，残卷未习得时记入黑名单不再重试）
        /// ④ 灵草盈余炼丹（真实炼制行动消耗灵草、产出丹药回流市场）
        /// </summary>
        private async Task ItemUsageCycleAsync(CancellationToken cancellationToken)
        {
            if (RoleOf is FleetRole.SoloCombat or FleetRole.PartyMember && !_potionConfigured)
            {
                var pill = Asset("item.zhixue-powder") >= 1 ? "item.zhixue-powder"
                    : Asset("item.shunqi-pill") >= 1 ? "item.shunqi-pill" : null;
                if (pill is not null)
                {
                    var configure = await SendCommandAsync("queue.auto_use.configure", requestId => new
                    {
                        requestId, actionType = "combat", slotIndex = 0, itemId = pill,
                        quantity = 1, enabled = true, group = "potion",
                    }, cancellationToken);
                    if (configure.Ok) { _potionConfigured = true; Interlocked.Increment(ref Stats.ItemOps); }
                    else AmbientNote($"战斗药槽配置失败 {_account} {pill}: {configure.Code}");
                }
            }

            if (RoleOf is FleetRole.Gatherer && !_explorePillConfigured && Asset("item.qi-gathering-pill") >= 1)
            {
                var configure = await SendCommandAsync("queue.auto_use.configure", requestId => new
                {
                    requestId, actionType = "exploration", slotIndex = 0, itemId = "item.qi-gathering-pill",
                    quantity = 1, enabled = true, group = "general",
                }, cancellationToken);
                if (configure.Ok) { _explorePillConfigured = true; Interlocked.Increment(ref Stats.ItemOps); }
                else AmbientNote($"探索药槽配置失败 {_account}: {configure.Code}");
            }

            var manual = FirstHeldItem(id => id.StartsWith("item.tome.", StringComparison.Ordinal)
                || id.StartsWith("item.fragment.", StringComparison.Ordinal));
            if (manual is not null && !_unlearnableItems.Contains(manual))
            {
                var learn = await SendCommandAsync("ability.learn", requestId => new { requestId, itemId = manual }, cancellationToken);
                if (learn.Ok) Interlocked.Increment(ref Stats.ItemOps);
                else if (learn.Code is "ABILITY_NOT_LEARNED" or "INVALID_ABILITY" or "INVALID_MANUAL") _unlearnableItems.Add(manual);
                else AmbientNote($"研读失败 {_account} {manual}: {learn.Code} {learn.Message}");
            }

            if (Asset("item.ningxue-grass") >= 6 && Asset("item.qi-gathering-pill") < 3 && ActionQueueCount() < 3)
            {
                var refine = await SendCommandAsync("action.enqueue", requestId => new
                {
                    requestId, actionId = "alchemy.refine-qi-pill", repeatCount = 1,
                }, cancellationToken);
                if (refine.Ok) Interlocked.Increment(ref Stats.ItemOps);
                else AmbientNote($"炼丹入队失败 {_account}: {refine.Code}");
            }
        }

        /// <summary>
        /// 市场周期：随机卖家（扫真实盈余挂单）与随机买家（灵石余额内出价）自然涌现；
        /// 周期性领账回收成交灵石/物品，挂单簿超容滚动撤销回收押物，覆盖 place/cancel/claim 全生命周期。
        /// </summary>
        private async Task MarketCycleAsync(CancellationToken cancellationToken)
        {
            var cycle = ++_marketCycle;

            if (cycle % 5 == 4)
            {
                var claim = await SendCommandAsync("market.claim", requestId => new { requestId }, cancellationToken);
                if (claim.Ok) Interlocked.Increment(ref Stats.MarketClaimed);
                else AmbientNote($"市场领账失败 {_account}: {claim.Code}");
            }

            // 滚动撤单：服务端限制每标的至多一笔活动挂单，挂单前对本标的先撤旧单（见 PlaceAsync 前的 CancelOpenAsync）。
            if (_myOrders.Count > 0 && (_myOrders.Count >= 10 || cycle % 7 == 6)
                && _myOrders.TryDequeue(out var stale))
            {
                await CancelOpenAsync(stale.ItemId, stale.OrderId, cancellationToken);
            }

            var sellPick = PickSellable(out var sellMaxQty);
            var stones = Asset("currency.spirit-stone");
            var roll = Random.Shared.Next(100);

            if (sellPick is not null && roll < 65)
            {
                var itemId = sellPick.Value.ItemId;
                var quantity = Random.Shared.Next(1, sellMaxQty + 1); // 交易量不固定：1..盈余上限
                var price = Math.Max(1, Math.Round(sellPick.Value.BasePrice * (0.9 + Random.Shared.NextDouble() * 0.6)));
                await CancelOpenAsync(itemId, null, cancellationToken);
                var orderId = Guid.NewGuid();
                var place = await SendCommandAsync("market.place", requestId => new
                {
                    requestId, orderId, itemId, variant = "",
                    side = "Sell", price, quantity,
                }, cancellationToken);
                if (place.Ok) { TrackOrder(itemId, orderId); Interlocked.Increment(ref Stats.MarketPlaced); }
                else { Interlocked.Increment(ref Stats.MarketFailed); AmbientNote($"卖单失败 {_account} {itemId}: {place.Code} {place.Message}"); }
            }
            else if (stones > 400 && roll >= 35)
            {
                var wanted = BuyWanted[Random.Shared.Next(BuyWanted.Length)];
                var quantity = Random.Shared.Next(1, 7);
                var price = Math.Max(1, Math.Round(PriceOf(wanted) * (1.1 + Random.Shared.NextDouble() * 0.9)));
                // 买单冻结 price×quantity 灵石：客户端按真实余额预估，避免无效挂单噪音。
                if (price * quantity >= stones - 100) return;
                await CancelOpenAsync(wanted, null, cancellationToken);
                var orderId = Guid.NewGuid();
                var place = await SendCommandAsync("market.place", requestId => new
                {
                    requestId, orderId, itemId = wanted, variant = "",
                    side = "Buy", price, quantity,
                }, cancellationToken);
                if (place.Ok) { TrackOrder(wanted, orderId); Interlocked.Increment(ref Stats.MarketPlaced); }
                else { Interlocked.Increment(ref Stats.MarketFailed); AmbientNote($"买单失败 {_account} {wanted}: {place.Code} {place.Message}"); }
            }
        }

        /// <summary>登记自有活动挂单（每标的唯一，滚动撤销队列按标的去重登记）。</summary>
        private void TrackOrder(string itemId, Guid orderId)
        {
            if (_openOrders.TryAdd(itemId, orderId)) _myOrders.Enqueue((itemId, orderId));
        }

        /// <summary>
        /// 撤销本角色在该标的的活动挂单：服务端要求「同一玩家同一标的至多一笔活动挂单」，
        /// 换向或换价重挂前必须先撤旧单，否则 PlaceAsync 会以 OWNERORDEREXISTS 拒绝。
        /// orderId 非空时按登记的期望值直接撤；空则按本角色在该标的的记忆单撤。
        /// 撤单失败（如已成交/已到期）只从本地登记清除，不视作环境异常。
        /// </summary>
        private async ValueTask CancelOpenAsync(string itemId, Guid? orderId, CancellationToken cancellationToken)
        {
            var target = orderId ?? (_openOrders.TryGetValue(itemId, out var tracked) ? tracked : (Guid?)null);
            if (target is not { } orderIdValue) return;
            if (_openOrders.TryGetValue(itemId, out var known) && known == orderIdValue) _openOrders.Remove(itemId);
            var cancel = await SendCommandAsync("market.cancel", requestId => new { requestId, orderId = orderIdValue }, cancellationToken);
            if (cancel.Ok) Interlocked.Increment(ref Stats.MarketCancelled);
        }

        /// <summary>随机起点遍历目录挑出第一种有盈余的可交易品；残卷作为兜底（保留 10 张自用）。</summary>
        private (string ItemId, int Keep, double BasePrice)? PickSellable(out int maxQuantity)
        {
            var start = Random.Shared.Next(SellableItems.Length);
            for (var i = 0; i < SellableItems.Length; i++)
            {
                var candidate = SellableItems[(start + i) % SellableItems.Length];
                var surplus = (int)Math.Floor(Asset(candidate.ItemId)) - candidate.Keep;
                if (surplus >= 1) { maxQuantity = Math.Min(surplus, 8); return candidate; }
            }
            foreach (var key in _assets.Keys)
            {
                if (!key.StartsWith("item.fragment.", StringComparison.Ordinal)) continue;
                var surplus = (int)Math.Floor(Asset(key)) - 10;
                if (surplus >= 1) { maxQuantity = Math.Min(surplus, 5); return (key, 10, 12); }
            }
            maxQuantity = 0;
            return null;
        }

        private static double PriceOf(string itemId)
        {
            foreach (var (id, _, basePrice) in SellableItems)
                if (id == itemId) return basePrice;
            return 10;
        }

        /// <summary>第一种持有量 ≥1 且满足谓词的物品；找不到返回 null。</summary>
        private string? FirstHeldItem(Func<string, bool> predicate)
        {
            foreach (var key in _assets.Keys)
                if (predicate(key) && Asset(key) >= 1) return key;
            return null;
        }

        /// <summary>环境 tick 失败节流：每角色 5 分钟内最多一条 anomaly，全量计数走 Stats.MarketFailed。</summary>
        private void AmbientNote(string message)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAmbientErrorAt < TimeSpan.FromMinutes(5)) return;
            _lastAmbientErrorAt = now;
            Stats.Anomaly(message);
        }

        // ---------- 分工 tick（幂等，可安全重复进入） ----------

        private async Task RoleTickAsync(CancellationToken cancellationToken)
        {
            switch (RoleOf)
            {
                case FleetRole.Gatherer: await GathererTickAsync(cancellationToken); break;
                case FleetRole.Crafter: await CrafterTickAsync(cancellationToken); break;
                case FleetRole.SoloCombat: await SoloCombatTickAsync(cancellationToken); break;
                case FleetRole.PartyMember: await PartyTickAsync(cancellationToken); break;
                case FleetRole.Roamer: await RoamerTickAsync(cancellationToken); break;
            }
        }

        /// <summary>采集者：驻留指定节点，队列空时补一个无限循环采集。</summary>
        private async Task GathererTickAsync(CancellationToken cancellationToken)
        {
            var (node, action) = (Index % 5) switch
            {
                0 => (Village, "explore.gather-ningxue-grass"),
                1 => (HerbValley, "explore.gather-ningxue-grass"),
                2 => (Brook, "explore.collect-morning-dew"),
                3 => (PeachForest, "explore.chop-peach-wood"),
                _ => (RockBeach, "explore.mine-dark-iron"),
            };
            if (ActionQueueCount() > 0) return;
            if (!await TravelAndSettleAsync(node, cancellationToken)) return;
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = action, repeatCount = -1,
            }, cancellationToken);
            if (!enqueue.Ok) Stats.Anomaly($"采集常驻入队失败 {_account}: {enqueue.Code} {enqueue.Message}");
        }

        /// <summary>制造者：按槽位驻留四种节点之一，队列空时补「采集×N + 加工×M」轮转队列。
        /// 每种配方只用本地采集材料，材料闭环自洽（loopEnabled 整队轮转持续生产）。</summary>
        private async Task CrafterTickAsync(CancellationToken cancellationToken)
        {
            if (ActionQueueCount() > 0) return;
            var (node, loop) = (Index % 4) switch
            {
                0 => (HerbValley, new[] { ("explore.gather-ningxue-grass", 6), ("alchemy.refine-qi-pill", 2) }),
                1 => (RockBeach, new[] { ("explore.mine-dark-iron", 9), ("craft.refine-iron", 3) }),
                2 => (PeachForest, new[] { ("explore.chop-peach-wood", 9), ("craft.refine-peach-wood-crystal", 3) }),
                _ => (Village, new[] { ("explore.gather-ningxue-grass", 6), ("alchemy.refine-qi-pill", 2) }),
            };
            if (!await TravelAndSettleAsync(node, cancellationToken)) return;
            foreach (var (action, repeats) in loop)
            {
                var enqueue = await SendCommandAsync("action.enqueue", requestId => new
                {
                    requestId, actionId = action, repeatCount = repeats,
                }, cancellationToken);
                if (!enqueue.Ok) Stats.Anomaly($"制造入队失败 {_account} {action}: {enqueue.Code} {enqueue.Message}");
            }
        }

        /// <summary>单人战斗：驻留溪谷池节点无限讨伐；单人战斗员全员开启斗法（斗法仅对战斗远征生效）。</summary>
        private async Task SoloCombatTickAsync(CancellationToken cancellationToken)
        {
            var node = (Index % 2) == 0 ? Village : HerbValley;
            const bool pvp = true;
            if (ActionQueueCount() > 0) return;
            if (!await TravelAndSettleAsync(node, cancellationToken)) return;
            var enqueue = await SendCommandAsync("action.enqueue", requestId => new
            {
                requestId, actionId = "combat.hunt-qi-beast", repeatCount = -1, pvpEnabled = pvp,
            }, cancellationToken);
            if (!enqueue.Ok) { Stats.Anomaly($"战斗入队失败 {_account}: {enqueue.Code} {enqueue.Message}"); return; }
            if (pvp) Interlocked.Increment(ref Stats.SoloPvpEnabled);
        }

        /// <summary>
        /// 组队讨伐 tick：队长建队并补齐邀请；全员驻留目标节点维持就绪。
        /// 会话开打后按战斗窗口计时，窗口到期解除就绪触发散场；散场确认后下轮再就绪，
        /// 形成「就绪→开打→散场→再就绪」的持续讨伐节拍（每个 social.party_sessions 行=一场）。
        /// </summary>
        private async Task PartyTickAsync(CancellationToken cancellationToken)
        {
            if (_partySlot < 0 || _partyNode is null || _partySlot >= _context.PartyMembers.Length) return;
            if (!await TravelAndSettleAsync(_partyNode, cancellationToken)) return;

            var members = _context.PartyMembers[_partySlot];
            if (_partyLeader)
            {
                if (!_partyCreated)
                {
                    var created = await SendSocialAsync(SocialActions.PartyCreate, party: new PartyProfileDto(
                        $"讨伐队{_partySlot}", PartyActivityKinds.Combat, _partyNode, 0, false, null));
                    if (created is not null) _partyCreated = true;
                }
                else
                {
                    // 邀请未入队的名册成员（对方 SocialState 推送触发自动接受；同一成员 60 秒内不重复补发；
                    // 迟到注册的成员 PlayerId 尚未就绪则跳过，本轮不阻塞）。
                    var now = DateTimeOffset.UtcNow;
                    foreach (var member in members.Skip(1))
                    {
                        if (member.PlayerId == Guid.Empty) continue;
                        if (_partyMemberIds.ContainsKey(member.PlayerId)) continue;
                        if (_inviteSentAt.TryGetValue(member.PlayerId, out var sentAt) && now - sentAt < TimeSpan.FromSeconds(60)) continue;
                        _inviteSentAt[member.PlayerId] = now;
                        await SendSocialAsync(SocialActions.PartyInvite, targetPlayerId: member.PlayerId.ToString("D"));
                    }
                }
            }

            // 会话窗口计时：开打超窗即解除就绪散场；无会话则保持就绪等待仲裁自动开打。
            // ready/unready 置位服务端幂等，重复发送安全。
            // 未确认身处本队前不发 ready（误在他队时 ready 既不触发本队仲裁也污染他队），
            // 避免 SOCIAL_NOT_IN_PARTY 噪音。
            if (!_inExpectedParty || !_partyMemberIds.ContainsKey(PlayerId)) return;
            if (_partySessionActive)
            {
                var sessionStartedAtMs = Interlocked.Read(ref _sessionStartedAtUnixMs);
                if (sessionStartedAtMs > 0
                    && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sessionStartedAtMs > (70 + (Index % 40)) * 1000)
                    await SendSocialAsync(SocialActions.PartyUnready);
            }
            else
            {
                await SendSocialAsync(SocialActions.PartyReady);
            }
        }

        /// <summary>巡游者：青云各节点间循环赶路，覆盖移动与节点投影。</summary>
        private async Task RoamerTickAsync(CancellationToken cancellationToken)
        {
            var circuit = new[] { Village, Brook, HerbValley, PeachForest, RockBeach, "node.cloud-gate", "node.cloud-peak", "node.tea-house" };
            if (OnEdge()) return; // 旅途中等落地。
            var target = circuit[_roamCursor++ % circuit.Length];
            if (NodeId() == target) target = circuit[_roamCursor++ % circuit.Length];
            await TravelAndSettleAsync(target, cancellationToken, 120);
        }
    }
}
