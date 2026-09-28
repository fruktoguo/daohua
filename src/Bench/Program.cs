using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Protocol;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;

namespace DaoHua.Bench;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
            var options = BenchOptions.Parse(args.Skip(1));
            return command switch
            {
                "seed" => await RunSeedAsync(options),
                "probe" => await RunProbeAsync(options),
                "run" => await RunWorkloadAsync(options),
                "report" => await RunReportAsync(options),
                "compare" => await RunPersistenceCompareAsync(options),
                "provenance" => RunProvenance(options),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => throw new ArgumentException($"未知命令：{command}")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NpgsqlException or HttpRequestException)
        {
            Console.Error.WriteLine($"bench 失败：{exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunSeedAsync(BenchOptions options)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var fingerprint = options.Fingerprint ?? catalog.Fingerprint;
        if (fingerprint != catalog.Fingerprint) throw new ArgumentException("种子指纹必须来自本次实际加载的配表。");
        var characters = options.Manifest is { } manifestPath
            ? JsonSerializer.Deserialize<List<TestCharacter>>(await File.ReadAllTextAsync(manifestPath), BenchJson.Options)
                ?? throw new InvalidOperationException("manifest 为空。")
            : TestCharacterSeeder.Create(options.Players, fingerprint, options.Epoch, catalog, options.ResourceExhaustion);
        TestCharacterSeeder.Validate(characters, catalog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
        await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(characters, BenchJson.Options));
        if (!string.IsNullOrWhiteSpace(options.Database))
            await BenchDatabase.SeedAsync(options.Database, characters, catalog);
        var database = options.Database is null ? null : new NpgsqlConnectionStringBuilder(options.Database);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            count = characters.Count,
            fingerprint,
            output = Path.GetFullPath(options.Output),
            database = database is null ? null : new { host = database.Host, port = database.Port, name = database.Database }
        }, BenchJson.Indented));
        return 0;
    }

    private static async Task<int> RunProbeAsync(BenchOptions options)
    {
        var variant = BenchVariant.Parse(options.Variant);
        var result = await BenchProbe.RunAsync(variant, options, options.Players, options.RequireWebSocket);
        await BenchReportWriter.WriteAsync(options.Report, result);
        Console.WriteLine(JsonSerializer.Serialize(result, BenchJson.Indented));
        return result.Metric.Errors == 0 ? 0 : 1;
    }

    private static async Task<int> RunReportAsync(BenchOptions options)
    {
        var reports = await BenchReportWriter.ReadManyAsync(options.Report);
        await BenchReportWriter.WriteMissingStageCsvAsync(options.Report, reports);
        Console.WriteLine(JsonSerializer.Serialize(new { reports = reports.Count }, BenchJson.Indented));
        return 0;
    }

    private static async Task<int> RunWorkloadAsync(BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Manifest)) throw new ArgumentException("run 必须提供 --manifest seed 输出文件。");
        var manifest = JsonSerializer.Deserialize<List<TestCharacter>>(await File.ReadAllTextAsync(options.Manifest), BenchJson.Options)
            ?? throw new InvalidOperationException("manifest 为空。");
        TestCharacterSeeder.Validate(manifest, GameDataCatalog.LoadEmbedded());
        var selected = manifest.Where(character => options.Scenario is null || character.Scenario.ToString().Equals(options.Scenario, StringComparison.OrdinalIgnoreCase)).Take(options.Players).ToArray();
        if (selected.Length != options.Players) throw new InvalidOperationException($"manifest 只有 {selected.Length} 个匹配角色，无法执行要求的 {options.Players} 人。");
        var report = await BenchWorkload.RunAsync(BenchVariant.Parse(options.Variant), options, selected);
        await BenchReportWriter.WriteAsync(options.Report, report);
        Console.WriteLine(JsonSerializer.Serialize(report, BenchJson.Indented));
        return report.Metric.Errors == 0 ? 0 : 1;
    }

    private static int RunProvenance(BenchOptions options)
    {
        var variant = BenchVariant.Parse(options.Variant);
        var artifact = Path.GetFullPath(options.Output);
        if (!Directory.Exists(artifact)) throw new InvalidOperationException($"找不到发布目录：{artifact}");
        var result = AssemblyProvenance.Read(variant, artifact, options.RepoRoot);
        Console.WriteLine(JsonSerializer.Serialize(result, BenchJson.Indented));
        return result.Valid ? 0 : 1;
    }

    private static async Task<int> RunPersistenceCompareAsync(BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatabaseB)
            || string.IsNullOrWhiteSpace(options.DatabaseC))
            throw new ArgumentException("compare 必须提供 --database-b 和 --database-c（--database-a 为可选第三库参照）。");

        PersistenceCompareResult? result = null;
        PersistenceCompareBatchResult? batch = null;
        if (!string.IsNullOrWhiteSpace(options.Manifest))
        {
            var manifest = JsonSerializer.Deserialize<List<TestCharacter>>(await File.ReadAllTextAsync(options.Manifest), BenchJson.Options)
                ?? throw new InvalidOperationException("compare manifest 为空。");
            batch = await BenchPersistenceComparer.CompareManyAsync(options.DatabaseA, options.DatabaseB, options.DatabaseC, manifest.Select(character => character.PlayerId));
        }
        else if (!string.IsNullOrWhiteSpace(options.PlayerId) && Guid.TryParse(options.PlayerId, out var playerId))
        {
            result = await BenchPersistenceComparer.CompareAsync(options.DatabaseA, options.DatabaseB, options.DatabaseC, playerId);
        }
        else
        {
            throw new ArgumentException("compare 必须提供 --manifest，或提供有效的 --player-id。");
        }
        object compareOutput = result is not null ? result : batch!;
        await File.WriteAllTextAsync(options.Report, JsonSerializer.Serialize(compareOutput, BenchJson.Indented));
        var csv = new StringBuilder("variant,found,business_fingerprint,canonical_bytes,exact_match\n");
        if (result is not null)
        {
            foreach (var snapshot in result.Variants)
                csv.Append(snapshot.Variant).Append(',')
                    .Append(snapshot.Found ? "true" : "false").Append(',')
                    .Append(snapshot.BusinessFingerprint).Append(',')
                    .Append(snapshot.CanonicalBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(result.ExactMatch ? "true" : "false").Append('\n');
        }
        else if (batch is not null)
        {
            foreach (var item in batch.Players)
                foreach (var snapshot in item.Variants)
                    csv.Append(item.PlayerId).Append(':').Append(snapshot.Variant).Append(',')
                        .Append(snapshot.Found ? "true" : "false").Append(',')
                        .Append(snapshot.BusinessFingerprint).Append(',')
                        .Append(snapshot.CanonicalBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.ExactMatch ? "true" : "false").Append('\n');
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(options.Report, ".csv"), csv.ToString());
        Console.WriteLine(JsonSerializer.Serialize(compareOutput, BenchJson.Indented));
        return (result?.ExactMatch ?? batch?.ExactMatch) == true ? 0 : 1;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("用法：dotnet run --project src/Bench -- seed|probe|run|report [选项]");
        Console.WriteLine("  seed  --output artifacts/bench/characters.json [--players 3000] [--database 连接串]");
        Console.WriteLine("  probe --variant ecs --url http://127.0.0.1:5128 [--account 账号 --password 密码] [--report 文件.json]");
        Console.WriteLine("  run --variant ecs --url http://127.0.0.1:5128 --manifest 文件.json [--scenario Gathering|Crafting|Combat] --players 1 --mode replay|offline-login|online --concurrency 32 [--target-hours 1] [--duration-seconds 900 --interval-ms 1000] [--report 文件.json]");
        Console.WriteLine("  online 墙钟短测：--duration-seconds 10；15/60 分钟：900/3600。replay 的 --target-hours 仅指业务补算时长。");
        Console.WriteLine("  offline-login 先推进业务时钟，再以真实 WS 上线触发离线补算；不发送 simulation.advance。可用 scripts/bench-offline-login.sh 执行 12 小时、100/500/1000 人矩阵。");
        Console.WriteLine("  seed --resource-exhaustion true 单独生成仅够 2 轮制造的耗尽样本；普通种子按实际配表准备 48 小时材料。");
        Console.WriteLine("  report --report 'artifacts/bench/*-gathering.json'（重新生成单报告 stage/counter CSV）");
        Console.WriteLine("  compare --database-b 连接串 --database-c 连接串 [--database-a 连接串] --player-id UUID [--report 文件.json]");
        Console.WriteLine("  provenance --variant ecs --output artifacts/runtime/<variant>");
        return 0;
    }
}

public sealed record BenchOptions(
    string Variant,
    Uri Url,
    int Players,
    string Output,
    string Report,
    string? Database,
    string? Fingerprint,
    DateTimeOffset Epoch,
    string RepoRoot,
    bool RequireWebSocket,
    string? Account,
    string? Password,
    string? Manifest,
    string? Scenario,
    double TargetHours,
    string? DatabaseA = null,
    string? DatabaseB = null,
    string? DatabaseC = null,
    string? PlayerId = null,
    string Mode = "replay",
    double DurationSeconds = 10,
    int Concurrency = 32,
    double IntervalMs = 1000,
    double RequestTimeoutSeconds = 120,
    double MetricsIntervalSeconds = 5,
    bool ResourceExhaustion = false)
{
    public static BenchOptions Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var args = arguments.ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"不支持的位置参数：{args[i]}");
            var key = args[i][2..];
            values[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        var root = values.GetValueOrDefault("repo-root") ?? FindRepoRoot();
        var url = values.GetValueOrDefault("url", "http://127.0.0.1:5128");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("--url 必须是绝对 HTTP/HTTPS URL。");
        var epoch = values.GetValueOrDefault("epoch", "2026-01-01T00:00:00Z");
        if (!DateTimeOffset.TryParse(epoch, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEpoch))
            throw new ArgumentException("--epoch 必须是 ISO-8601 时间。");
        var mode = values.GetValueOrDefault("mode", "replay").ToLowerInvariant();
        if (mode is not ("online" or "replay" or "offline-login")) throw new ArgumentException("--mode 必须是 online、replay 或 offline-login。");
        return new(
            values.GetValueOrDefault("variant", "ecs"), parsed,
            ReadInt(values, "players", 1, 1, 3000),
            values.GetValueOrDefault("output", Path.Combine(root, "artifacts/bench/characters.json")),
            values.GetValueOrDefault("report", Path.Combine(root, "artifacts/bench/report.json")),
            values.GetValueOrDefault("database"), values.GetValueOrDefault("fingerprint"), parsedEpoch.ToUniversalTime(), root,
            ReadBool(values, "require-websocket", true), values.GetValueOrDefault("account"), values.GetValueOrDefault("password"),
            values.GetValueOrDefault("manifest"), values.GetValueOrDefault("scenario"), ReadDouble(values, "target-hours", 1, 0, 48),
            values.GetValueOrDefault("database-a"), values.GetValueOrDefault("database-b"), values.GetValueOrDefault("database-c"), values.GetValueOrDefault("player-id"),
            mode, ReadDouble(values, "duration-seconds", 10, 0.1, 86400), ReadInt(values, "concurrency", 32, 1, 3000),
            ReadDouble(values, "interval-ms", 1000, 1, 60000), ReadDouble(values, "request-timeout-seconds", 120, 1, 3600),
            ReadDouble(values, "metrics-interval-seconds", 5, 0.1, 60), ReadBool(values, "resource-exhaustion", false));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Game.slnx"))) current = current.Parent;
        return current?.FullName ?? Environment.CurrentDirectory;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"--{key} 必须在 {min} 到 {max} 之间。");
        return parsed;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!bool.TryParse(value, out var parsed)) throw new ArgumentException($"--{key} 必须是 true 或 false。");
        return parsed;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback, double min, double max)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"--{key} 必须在 {min} 到 {max} 之间。");
        return parsed;
    }
}

public enum BenchScenario { Gathering, Crafting, Combat }

public sealed record TestCharacter(
    Guid PlayerId,
    Guid AccountId,
    string DisplayName,
    BenchScenario Scenario,
    int Ordinal,
    int RandomSeed,
    DateTimeOffset StartAt,
    string GameDataFingerprint,
    JsonElement InitialState,
    string ActionId,
    Guid ActionInstanceId,
    IReadOnlyDictionary<string, double> StarterAssets)
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

public static class TestCharacterSeeder
{
    private static readonly Guid Namespace = new("a7f7194b-9d8a-4fbf-a2f7-e0fead0a8d4a");

    public static IReadOnlyList<TestCharacter> Create(int players, string fingerprint, DateTimeOffset epoch, GameDataCatalog catalog, bool resourceExhaustion = false)
    {
        if (players is < 1 or > 3000) throw new ArgumentOutOfRangeException(nameof(players));
        var result = new List<TestCharacter>(players);
        var perScenario = players / 3;
        for (var index = 0; index < players; index++)
        {
            var scenario = index < perScenario ? BenchScenario.Gathering : index < perScenario * 2 ? BenchScenario.Crafting : BenchScenario.Combat;
            var local = scenario switch { BenchScenario.Gathering => index + 1, BenchScenario.Crafting => index - perScenario + 1, _ => index - perScenario * 2 + 1 };
            var prefix = scenario switch { BenchScenario.Gathering => "gather", BenchScenario.Crafting => "craft", _ => "combat" };
            var actionId = scenario switch
            {
                BenchScenario.Gathering => "explore.gather-ningxue-grass",
                BenchScenario.Crafting => "alchemy.refine-qi-pill",
                _ => "combat.training-ground"
            };
            var action = scenario == BenchScenario.Combat
                ? catalog.Actions.All.First(candidate => candidate.ExecutionKind == ActionExecutionKind.CombatEncounter)
                : catalog.Actions.All.Single(candidate => candidate.Id.Value == actionId);
            // 以配表单轮时长和消耗推导 48 小时材料；额外 2 轮覆盖起止边界，不混入耗尽样本。
            var repeatBudget = resourceExhaustion ? 2 : Math.Ceiling(TimeSpan.FromHours(48).TotalSeconds / action.DurationSeconds) + 2;
            var starterAssets = action.Inputs.ToDictionary(pair => pair.Key.Value, pair => pair.Value * repeatBudget, StringComparer.Ordinal);
            starterAssets["equipment.qingfeng-sword"] = 1;
            starterAssets["item.shunqi-pill"] = Math.Ceiling(TimeSpan.FromHours(48).TotalSeconds / 60) + 2;
            var playerId = StableGuid($"player:{scenario}:{local:D4}");
            var accountId = StableGuid($"account:{scenario}:{local:D4}");
            var actionInstanceId = StableGuid($"action:{scenario}:{local:D4}");
            var randomSeed = 0x5EED_0000 + index;
            var player = PlayerAggregate.Create(new(playerId), new(accountId), $"{prefix}-{local:D4}", epoch, fingerprint);
            player.World.ExplorationSeed = randomSeed;
            if (action.AllowedNodeIds is { Count: > 0 } nodes && !nodes.Contains(player.World.NodeId))
                player.World.NodeId = nodes.Order(StringComparer.Ordinal).First();
            player.LearnAbility(new("ability.heavy-slash"));
            player.ReplaceCombatSlots([new("ability.heavy-slash", 1, [])], [new("equipment.qingfeng-sword", 0)]);
            // 初始 Buff 完整取自当前顺气丸配表；自动药品槽让真实长战斗继续覆盖 Buff/冷却路径。
            var buff = catalog.RawItems["item.shunqi-pill"].GetProperty("consumable").GetProperty("buffs")[0];
            player.Buffs.Active.Add(new ActiveBuff(buff.GetProperty("buffId").GetString()!,
                epoch.ToUnixTimeSeconds() + buff.GetProperty("durationSeconds").GetDouble(),
                Name: buff.GetProperty("name").GetString()!, SourceItemId: "item.shunqi-pill",
                MpRegen: buff.GetProperty("mpRegen").GetDouble(), AttackSpeedPoints: buff.GetProperty("attackSpeedPoints").GetDouble()));
            player.Settings.AutoUseSlots.Add(new("combat", 0, "item.shunqi-pill", 1, true,
                Group: AutoUseSlotState.SlotGroupPotion));
            var resolved = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant();
            player.Resources.Hitpoints = player.Resources.MaxHitpoints = resolved.MaxHitpoints;
            player.Resources.Manapoints = player.Resources.MaxManapoints = resolved.MaxManapoints;
            player.Enqueue(new ActionIntent(action.Id, -1), epoch, catalog.Actions);
            player.SetPresence(PresenceState.Offline);
            var generated = player.ExportState();
            var state = generated with { Actions = generated.Actions.Select(action => action with { Id = actionInstanceId }).ToArray() };
            result.Add(new(playerId, accountId, player.DisplayName, scenario, local, randomSeed, epoch, fingerprint,
                JsonSerializer.SerializeToElement(state, PersistenceJson.Options), action.Id.Value, actionInstanceId, starterAssets));
        }
        return result;
    }

    public static PlayerAggregateState ReadState(TestCharacter character)
        => character.InitialState.Deserialize<PlayerAggregateState>(PersistenceJson.Options)
            ?? throw new InvalidOperationException($"{character.DisplayName} 缺少真实玩家初态。");

    public static void Validate(IReadOnlyList<TestCharacter> characters, GameDataCatalog catalog)
    {
        if (characters.Count == 0 || characters.Select(item => item.PlayerId).Distinct().Count() != characters.Count)
            throw new InvalidOperationException("manifest 为空或有重复玩家。");
        foreach (var character in characters)
        {
            var state = ReadState(character);
            if (state.PlayerId.Value != character.PlayerId || state.AccountId.Value != character.AccountId
                || state.DisplayName != character.DisplayName || state.LastSimulatedAt != character.StartAt
                || state.GameDataFingerprint != catalog.Fingerprint || character.GameDataFingerprint != catalog.Fingerprint
                || state.World.ExplorationSeed != character.RandomSeed || state.Actions.Count != 1
                || state.Presence != PresenceState.Offline
                || state.Actions[0].Id != character.ActionInstanceId || state.Actions[0].Intent.ActionId.Value != character.ActionId
                || state.Actions[0].RemainingRepeats != -1 || state.Actions[0].Intent.RepeatCount != -1)
                throw new InvalidOperationException($"{character.DisplayName} manifest 元数据与实际落库初态不一致。");
            if (character.StarterAssets.Any(pair => !double.IsFinite(pair.Value) || pair.Value <= 0))
                throw new InvalidOperationException($"{character.DisplayName} 初始资产数量无效。");
        }
    }

    public static Guid StableGuid(string text)
    {
        var namespaceBytes = Namespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(text);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);
        var hash = SHA256.HashData(input);
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public sealed record BenchMetricRecord(
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("total_wall_ms")] double? TotalWallMs,
    [property: JsonPropertyName("cpu_ms")] double? CpuMs,
    [property: JsonPropertyName("simulation_ms")] double? SimulationMs,
    [property: JsonPropertyName("entity_create_ms")] double? EntityCreateMs,
    [property: JsonPropertyName("system_tick_ms")] double? SystemTickMs,
    [property: JsonPropertyName("snapshot_ms")] double? SnapshotMs,
    [property: JsonPropertyName("database_ms")] double? DatabaseMs,
    [property: JsonPropertyName("network_ms")] double? NetworkMs,
    [property: JsonPropertyName("allocated_bytes")] long? AllocatedBytes,
    [property: JsonPropertyName("gc0")] long? Gc0,
    [property: JsonPropertyName("gc1")] long? Gc1,
    [property: JsonPropertyName("gc2")] long? Gc2,
    [property: JsonPropertyName("rss_mb")] double? RssMb,
    [property: JsonPropertyName("p50_ms")] double? P50Ms,
    [property: JsonPropertyName("p95_ms")] double? P95Ms,
    [property: JsonPropertyName("p99_ms")] double? P99Ms,
    [property: JsonPropertyName("max_ms")] double? MaxMs,
    [property: JsonPropertyName("measurement_status")] string MeasurementStatus,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("errors")] int Errors)
{
    [JsonPropertyName("sample_count")]
    public int SampleCount { get; init; }
    [JsonPropertyName("gc_pause_ms")]
    public double? GcPauseMs { get; init; }
    [JsonPropertyName("instrumentation_listener_ms")]
    public double? InstrumentationListenerMs { get; init; }
    [JsonPropertyName("database_requests")]
    public long? DatabaseRequests { get; init; }
    [JsonPropertyName("database_bytes_sent")]
    public double? DatabaseBytesSent { get; init; }
    [JsonPropertyName("database_bytes_received")]
    public double? DatabaseBytesReceived { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("duration_unit")] public string DurationUnit { get; init; } = "seconds";
    [JsonPropertyName("simulated_hours")] public double? SimulatedHours { get; init; }
    [JsonPropertyName("offline_login_wall_ms")] public double? OfflineLoginWallMs { get; init; }
    [JsonPropertyName("login_latency_ms")] public BenchLatencySummary? LoginLatency { get; init; }
    [JsonPropertyName("offline_login_cpu_ms")] public double? OfflineLoginCpuMs { get; init; }
    [JsonPropertyName("offline_login_allocated_bytes")] public long? OfflineLoginAllocatedBytes { get; init; }
    [JsonPropertyName("offline_login_simulation_ms")] public double? OfflineLoginSimulationMs { get; init; }
    [JsonPropertyName("offline_login_system_tick_ms")] public double? OfflineLoginSystemTickMs { get; init; }
    [JsonPropertyName("offline_login_database_ms")] public double? OfflineLoginDatabaseMs { get; init; }
    [JsonPropertyName("offline_login_server_measurements")] public IReadOnlyDictionary<string, BenchLatencySummary>? OfflineLoginServerMeasurements { get; init; }
    [JsonPropertyName("requested_duration_seconds")] public double? RequestedDurationSeconds { get; init; }
    [JsonPropertyName("measured_wall_ms")] public double? MeasuredWallMs { get; init; }
    [JsonPropertyName("setup_wall_ms")] public double? SetupWallMs { get; init; }
    [JsonPropertyName("flush_wall_ms")] public double? FlushWallMs { get; init; }
    [JsonPropertyName("concurrency_limit")] public int? ConcurrencyLimit { get; init; }
    [JsonPropertyName("peak_inflight_requests")] public int? PeakInflightRequests { get; init; }
    [JsonPropertyName("connected_players")] public int? ConnectedPlayers { get; init; }
    [JsonPropertyName("successful_players")] public int? SuccessfulPlayers { get; init; }
    [JsonPropertyName("command_interval_ms")] public double? CommandIntervalMs { get; init; }
    [JsonPropertyName("attempted_commands")] public long? AttemptedCommands { get; init; }
    [JsonPropertyName("successful_commands")] public long? SuccessfulCommands { get; init; }
    [JsonPropertyName("commands_per_second")] public double? CommandsPerSecond { get; init; }
    [JsonPropertyName("request_rtt_ms")] public BenchLatencySummary? RequestRtt { get; init; }
    [JsonPropertyName("client_queue_ms")] public BenchLatencySummary? ClientQueue { get; init; }
    [JsonPropertyName("server_measurements")] public IReadOnlyDictionary<string, BenchLatencySummary>? ServerMeasurements { get; init; }
    [JsonPropertyName("server_counters")] public IReadOnlyDictionary<string, double?>? ServerCounters { get; init; }
    [JsonPropertyName("metric_scope")] public string? MetricScope { get; init; }
    [JsonPropertyName("player_denominator")] public int? PlayerDenominator { get; init; }
    [JsonPropertyName("cpu_ms_per_player")] public double? CpuMsPerPlayer { get; init; }
    [JsonPropertyName("allocated_bytes_per_player")] public double? AllocatedBytesPerPlayer { get; init; }
    [JsonPropertyName("rss_mb_per_player")] public double? RssMbPerPlayer { get; init; }
    [JsonPropertyName("peak_rss_mb")] public double? PeakRssMb { get; init; }
    [JsonPropertyName("received_frames")] public long? ReceivedFrames { get; init; }
    [JsonPropertyName("received_bytes")] public long? ReceivedBytes { get; init; }
    [JsonPropertyName("sent_bytes")] public long? SentBytes { get; init; }

    public static BenchMetricRecord Missing(string variant, string scenario, int players, double duration, string warning) =>
        new(variant, scenario, players, duration, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, "partial", [warning], 1);
}

public sealed record BenchReport(BenchMetricRecord Metric, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string? GameDataFingerprint = null, string? ProcessId = null, string? RuntimeArtifact = null, BenchParityStatus? Parity = null)
{
    public string? ManifestSha256 { get; init; }
    public IReadOnlyList<BenchProcessSample>? ProcessSamples { get; init; }
    public JsonElement? ServerMetricsBefore { get; init; }
    public JsonElement? ServerMetricsAfterLogin { get; init; }
    public JsonElement? ServerMetricsAfter { get; init; }
}
public sealed record BenchParityStatus(string Status, string Scope, IReadOnlyList<string> ComparedFields, IReadOnlyList<string> NotMeasuredFields);

public sealed record BenchStateFingerprint(string Variant, string Scenario, int Players, string? ManifestFingerprint, string? PersistenceFingerprint, bool ExactBusinessStateCompared, IReadOnlyList<string> ExcludedOperationalFields, IReadOnlyList<string> Warnings);

public sealed record AssemblyProvenance(
    string Variant,
    string SourceYuoCoreSha,
    IReadOnlyDictionary<string, string?> Assemblies,
    bool Valid,
    IReadOnlyList<string> Errors)
{
    public static AssemblyProvenance Read(BenchVariant variant, string artifact, string repoRoot)
    {
        var errors = new List<string>();
        var coreDirectory = Path.Combine(repoRoot, variant.YuoCoreDirectory, "src");
        var sourceSha = Directory.Exists(coreDirectory) ? GitSha(Path.GetDirectoryName(coreDirectory)!) : "missing";
        if (!string.Equals(sourceSha, variant.ExpectedYuoCoreSha, StringComparison.Ordinal)) errors.Add($"YuoCore 源码 SHA 不符：{sourceSha}");
        var assemblies = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[] { "YuoCore.Abstractions", "YuoCore.Ecs", "YuoCore.Runtime", "Domain", "Server", "Protocol" })
        {
            var path = Path.Combine(artifact, name + ".dll");
            if (!File.Exists(path)) continue;
            var assembly = System.Reflection.Assembly.LoadFile(path);
            assemblies[name] = $"mvid={assembly.ManifestModule.ModuleVersionId:D};sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}";
        }
        foreach (var required in new[] { "Server", "Domain", "YuoCore.Ecs" })
            if (!assemblies.ContainsKey(required)) errors.Add($"发布目录缺少 {required}.dll");
        return new(variant.Name, sourceSha, assemblies, errors.Count == 0, errors);
    }

    private static string GitSha(string directory)
    {
        var info = new ProcessStartInfo("git", $"-C \"{directory}\" rev-parse HEAD") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 git。");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}

public static class BenchJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}

public sealed record BenchVariant(string Name, int Port, string Database, string ArtifactDirectory, string YuoCoreDirectory, string ExpectedYuoCoreSha)
{
    public static BenchVariant Parse(string name) => name.ToLowerInvariant() switch
    {
        "ecs" => new("ecs", 5130, "daohua_bench_ecs", "artifacts/runtime/ecs", "vendor/YuoCore", "7b36f8002850dbf06e482682ba27d32cf4a0abe6"),
        _ => throw new ArgumentException($"未知 variant：{name}（仅支持 ecs）")
    };
}

public static class BenchDatabase
{
    public static async Task SeedAsync(string connectionString, IReadOnlyList<TestCharacter> characters, GameDataCatalog catalog, CancellationToken cancellationToken = default)
    {
        EnsureBenchConnection(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        TestCharacterSeeder.Validate(characters, catalog);
        // benchmark 数据库只承载一次实验的完整 fixture。清理必须在同一事务内完成，
        // 否则旧玩家、认证会话、流水和 outbox 会污染下一次三版本对比。
        await using (var reset = new NpgsqlCommand("""
            TRUNCATE TABLE
                identity.auth_sessions,
                identity.audit_events,
                identity.players,
                identity.accounts,
                game.player_state_heads,
                game.player_state_sections,
                game.player_action_queue,
                game.player_runtime_leases,
                game.player_projection,
                economy.player_asset_balances,
                economy.player_asset_instances,
                economy.transaction_lines,
                economy.transactions,
                economy.reservations,
                market.orders,
                market.trades,
                market.settlement_claims,
                platform.section_flush_queue,
                platform.integration_outbox,
                platform.command_receipts,
                platform.compute_jobs,
                social.friend_requests,
                social.friendships,
                social.parties,
                social.party_members,
                social.party_invitations,
                social.chat_messages
            RESTART IDENTITY CASCADE;
            """, connection, transaction))
        {
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        var passwordSalt = SHA256.HashData(Encoding.UTF8.GetBytes("daohua-bench-fixture-password"))[..16];
        var passwordHash = Rfc2898DeriveBytes.Pbkdf2("Bench-Deterministic-Password-2026!", passwordSalt, 600_000, HashAlgorithmName.SHA512, 64);
        foreach (var character in characters)
        {
            // 每个 manifest 是一次独立实验的完整初态。先清理同一 PlayerId 的所有权威
            // 分段、队列、资产和基准平台记录，避免上一次运行的余额/receipt 污染本次结果。
            foreach (var sql in new[]
            {
                "DELETE FROM platform.command_receipts WHERE player_id=$1",
                "DELETE FROM platform.section_flush_queue WHERE player_id=$1",
                "DELETE FROM platform.integration_outbox WHERE aggregate_id=$1",
                "DELETE FROM economy.transaction_lines WHERE player_id=$1",
                "DELETE FROM economy.transactions WHERE actor_id=$1 AND NOT EXISTS (SELECT 1 FROM economy.transaction_lines l WHERE l.transaction_id=economy.transactions.transaction_id)",
                "DELETE FROM market.settlement_claims WHERE player_id=$1",
                "DELETE FROM economy.reservations WHERE player_id=$1",
                "DELETE FROM economy.player_asset_balances WHERE player_id=$1",
                "DELETE FROM economy.player_asset_instances WHERE owner_player_id=$1",
                "DELETE FROM game.player_runtime_leases WHERE player_id=$1",
                "DELETE FROM game.player_projection WHERE player_id=$1",
                "DELETE FROM game.player_action_queue WHERE player_id=$1",
                "DELETE FROM game.player_state_sections WHERE player_id=$1",
                "DELETE FROM game.player_state_heads WHERE player_id=$1"
            })
            {
                await using var clear = new NpgsqlCommand(sql, connection, transaction);
                clear.Parameters.AddWithValue(character.PlayerId);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }
            var account = $"bench_{character.Scenario.ToString().ToLowerInvariant()}_{character.Ordinal:D4}";
            var state = TestCharacterSeeder.ReadState(character);
            var writes = SectionCodec.EncodeAggregateFull(state, character.StartAt);
            var stored = writes.Select(write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt)).ToArray();
            var headChecksum = SectionCodec.ComputeHeadChecksum(stored);
            await using var command = new NpgsqlCommand("""
                INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, status, created_at, updated_at)
                VALUES ($1,$2,$3,$4,$5,1,$6,$6)
                ON CONFLICT (account_id) DO UPDATE SET login_normalized=EXCLUDED.login_normalized, password_hash=EXCLUDED.password_hash, password_salt=EXCLUDED.password_salt, password_iterations=EXCLUDED.password_iterations, status=1, updated_at=EXCLUDED.updated_at
                """, connection, transaction);
            command.Parameters.AddWithValue(character.AccountId);
            command.Parameters.AddWithValue(account);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = passwordHash });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = passwordSalt });
            command.Parameters.AddWithValue(600_000);
            command.Parameters.AddWithValue(character.StartAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using (var playerCommand = new NpgsqlCommand("""
                INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
                VALUES ($1,$2,$3,$4,$5,$5,1)
                ON CONFLICT (player_id) DO UPDATE SET account_id=EXCLUDED.account_id, display_name=EXCLUDED.display_name, display_name_normalized=EXCLUDED.display_name_normalized, last_seen_at=EXCLUDED.last_seen_at, status=1
                """, connection, transaction))
            {
                playerCommand.Parameters.AddWithValue(character.PlayerId);
                playerCommand.Parameters.AddWithValue(character.AccountId);
                playerCommand.Parameters.AddWithValue(character.DisplayName);
                playerCommand.Parameters.AddWithValue(character.DisplayName.Trim().ToLowerInvariant());
                playerCommand.Parameters.AddWithValue(character.StartAt);
                await playerCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var sectionBatch = connection.CreateBatch();
            sectionBatch.Transaction = transaction;
            foreach (var write in writes)
            {
                var section = new NpgsqlBatchCommand("""
                    INSERT INTO game.player_state_sections(player_id, section_kind, section_version, schema_version, codec, payload, raw_bytes, stored_bytes, checksum, updated_at)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    ON CONFLICT(player_id, section_kind) DO UPDATE SET section_version=EXCLUDED.section_version, schema_version=EXCLUDED.schema_version, codec=EXCLUDED.codec, payload=EXCLUDED.payload, raw_bytes=EXCLUDED.raw_bytes, stored_bytes=EXCLUDED.stored_bytes, checksum=EXCLUDED.checksum, updated_at=EXCLUDED.updated_at;
                    """);
                section.Parameters.AddWithValue(write.PlayerId); section.Parameters.AddWithValue(write.SectionKind); section.Parameters.AddWithValue(write.StateVersion); section.Parameters.AddWithValue(write.SchemaVersion); section.Parameters.AddWithValue(write.Codec); section.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = write.Payload.ToArray() }); section.Parameters.AddWithValue(write.RawBytes); section.Parameters.AddWithValue(write.Payload.Length); section.Parameters.AddWithValue(write.Checksum); section.Parameters.AddWithValue(write.UpdatedAt); sectionBatch.BatchCommands.Add(section);
            }
            await sectionBatch.ExecuteNonQueryAsync(cancellationToken);
            await using (var head = new NpgsqlCommand("INSERT INTO game.player_state_heads(player_id,state_version,schema_version,last_simulated_at,game_data_fingerprint,checksum,updated_at) VALUES($1,$2,$3,$4,$5,$6,$4) ON CONFLICT(player_id) DO UPDATE SET state_version=EXCLUDED.state_version,schema_version=EXCLUDED.schema_version,last_simulated_at=EXCLUDED.last_simulated_at,game_data_fingerprint=EXCLUDED.game_data_fingerprint,checksum=EXCLUDED.checksum,updated_at=EXCLUDED.updated_at", connection, transaction))
            {
                head.Parameters.AddWithValue(character.PlayerId); head.Parameters.AddWithValue(state.StateVersion); head.Parameters.AddWithValue(SectionCodec.CurrentSchemaVersion); head.Parameters.AddWithValue(character.StartAt); head.Parameters.AddWithValue(character.GameDataFingerprint); head.Parameters.AddWithValue(headChecksum); await head.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var queue = new NpgsqlCommand("INSERT INTO game.player_action_queue(player_id,slot,action_id,action_payload,action_version,status,next_due_at,idempotency_key,updated_at) VALUES($1,0,$2,$3,$4,$5,NULL,$6,$7)", connection, transaction))
            {
                var queued = state.Actions[0]; queue.Parameters.AddWithValue(character.PlayerId); queue.Parameters.AddWithValue(queued.Intent.ActionId.Value); queue.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = JsonSerializer.SerializeToUtf8Bytes(queued.Intent, PersistenceJson.Options) }); queue.Parameters.AddWithValue(state.StateVersion); queue.Parameters.AddWithValue(queued.Status.ToString().ToLowerInvariant()); queue.Parameters.AddWithValue($"action:{queued.Id:D}"); queue.Parameters.AddWithValue(character.StartAt); await queue.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var starter in character.StarterAssets)
            {
                await using var asset = new NpgsqlCommand("INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES($1,$2,'',$3,1,$4) ON CONFLICT(player_id,asset_id,variant) DO UPDATE SET quantity=EXCLUDED.quantity,version=EXCLUDED.version,updated_at=EXCLUDED.updated_at", connection, transaction);
                asset.Parameters.AddWithValue(character.PlayerId); asset.Parameters.AddWithValue(starter.Key); asset.Parameters.AddWithValue(starter.Value); asset.Parameters.AddWithValue(character.StartAt); await asset.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var projection = new NpgsqlCommand("INSERT INTO game.player_projection(player_id,realm_rank,current_node_id,presence,last_active_at,projection_version,updated_at) VALUES($1,$2,$3,'offline',$4,1,$4) ON CONFLICT(player_id) DO UPDATE SET realm_rank=EXCLUDED.realm_rank,current_node_id=EXCLUDED.current_node_id,presence=EXCLUDED.presence,last_active_at=EXCLUDED.last_active_at,projection_version=EXCLUDED.projection_version,updated_at=EXCLUDED.updated_at", connection, transaction))
            {
                projection.Parameters.AddWithValue(character.PlayerId); projection.Parameters.AddWithValue((double)state.Progression.RealmLevel); projection.Parameters.AddWithValue(state.World.NodeId); projection.Parameters.AddWithValue(character.StartAt); await projection.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await ValidateManifestPlayersAsync(connection, transaction, characters, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ValidateManifestPlayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<TestCharacter> characters,
        CancellationToken cancellationToken)
    {
        var expected = characters.Select(character => character.PlayerId).ToHashSet();
        var actual = new HashSet<Guid>();
        await using var command = new NpgsqlCommand("SELECT player_id FROM identity.players", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actual.Add(reader.GetGuid(0));

        if (actual.SetEquals(expected)) return;

        var extra = actual.Except(expected).Take(5).ToArray();
        var missing = expected.Except(actual).Take(5).ToArray();
        throw new InvalidOperationException(
            $"基准 seed 玩家集合不一致：期望 {expected.Count}，实际 {actual.Count}，多余 [{string.Join(',', extra)}]，缺失 [{string.Join(',', missing)}]。");
    }

    public static void EnsureBenchConnection(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(builder.Host, "::1", StringComparison.Ordinal))
            throw new InvalidOperationException("基准 seed 只允许连接本机 PostgreSQL。");
        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.StartsWith("daohua_bench_", StringComparison.Ordinal)
            || !BenchVariantNames.Contains(builder.Database, StringComparer.Ordinal))
            throw new InvalidOperationException("基准 seed 只允许 daohua_bench_ecs。");
    }

    private static readonly string[] BenchVariantNames = ["daohua_bench_ecs"];
}

public static class BenchProbe
{
    public static async Task<BenchReport> RunAsync(BenchVariant variant, BenchOptions options, int players, bool requireWebSocket, CancellationToken cancellationToken = default)
    {
        var baseUri = options.Url;
        var started = DateTimeOffset.UtcNow;
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUri, "/"), Timeout = TimeSpan.FromSeconds(10) };
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = 0;
        var status = "client-protocol-smoke";
        var networkMs = 0d;
        var fingerprint = (string?)null;
        ServerProcessMetrics? before = null;
        ServerProcessMetrics? after = null;
        try
        {
            foreach (var path in new[] { "/healthz", "/readyz", "/api/v1/meta" })
            {
                var requestStarted = Stopwatch.GetTimestamp();
                using var response = await client.GetAsync(path, cancellationToken);
                networkMs += Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                if (!response.IsSuccessStatusCode) { errors++; warnings.Add($"{path} 返回 {(int)response.StatusCode}"); continue; }
                if (path.EndsWith("meta", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                    fingerprint = document.RootElement.TryGetProperty("gameDataFingerprint", out var element) ? element.GetString() : null;
                }
            }
            var auth = await AuthenticateAndSnapshotAsync(client, variant, options, cancellationToken);
            networkMs += auth.NetworkMs;
            if (!auth.Success) { errors++; warnings.Add(auth.Warning!); }
            else if (requireWebSocket)
            {
                before = await ReadServerMetricsAsync(client, cancellationToken);
                var wsStarted = Stopwatch.GetTimestamp();
                var wsFailure = await HelloAndCommandAsync(baseUri, auth.CookieHeader!, fingerprint, cancellationToken);
                networkMs += Stopwatch.GetElapsedTime(wsStarted).TotalMilliseconds;
                if (wsFailure is not null) { errors++; warnings.Add($"WebSocket Hello/首帧/命令回执失败：{wsFailure}"); }
                after = await ReadServerMetricsAsync(client, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or WebSocketException or JsonException)
        {
            errors++; warnings.Add(exception.Message);
        }
        stopwatch.Stop();
        warnings.Add($"客户端请求往返合计 {networkMs:F3} ms；cpu_ms/allocated_bytes/gc0-2/rss_mb 为服务进程指标端点前后差分，其余模拟、数据库、网络字节和百分位字段未采集并保持 null。");
        var metric = new BenchMetricRecord(variant.Name, "protocol-smoke", players, 0, stopwatch.Elapsed.TotalMilliseconds,
            Delta(before?.CpuMs, after?.CpuMs), null, null, null, null, null, null,
            Delta(before?.AllocatedBytes, after?.AllocatedBytes), Delta(before?.Gc0, after?.Gc0), Delta(before?.Gc1, after?.Gc1), Delta(before?.Gc2, after?.Gc2),
            after?.RssBytes / (1024d * 1024d), null, null, null, null, status, warnings, errors);
        return new BenchReport(metric, started, DateTimeOffset.UtcNow, fingerprint, after?.ProcessId.ToString(CultureInfo.InvariantCulture), variant.ArtifactDirectory,
            new BenchParityStatus("not_measured", "protocol-smoke", ["gameDataFingerprint", "HTTP/WS success"], ["PlayerAggregateState", "assets", "action queue", "combat logs/drops", "PostgreSQL section payloads"]));
    }

    private static async Task<ServerProcessMetrics?> ReadServerMetricsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/debug/bench/metrics", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        return new(
            root.GetProperty("processId").GetInt32(),
            root.GetProperty("cpuMs").GetDouble(),
            root.GetProperty("rssBytes").GetInt64(),
            root.GetProperty("allocatedBytes").GetInt64(),
            root.GetProperty("gc0").GetInt64(),
            root.GetProperty("gc1").GetInt64(),
            root.GetProperty("gc2").GetInt64());
    }

    private static long? Delta(long? before, long? after) => before is { } a && after is { } b && b >= a ? b - a : null;
    private static double? Delta(double? before, double? after) => before is { } a && after is { } b && b >= a ? b - a : null;

    private sealed record ServerProcessMetrics(int ProcessId, double CpuMs, long RssBytes, long AllocatedBytes, long Gc0, long Gc1, long Gc2);

    internal static async Task<(bool Success, string? Warning, string? CookieHeader, string? PlayerId, double NetworkMs)> AuthenticateAndSnapshotAsync(HttpClient client, BenchVariant variant, BenchOptions options, CancellationToken cancellationToken)
    {
        var account = options.Account ?? $"bench_{variant.Name.Replace('-', '_')}_{Guid.NewGuid():N}"[..20];
        var password = options.Password ?? "Bench-Deterministic-Password-2026!";
        var path = options.Account is null ? "api/v1/auth/register" : "api/v1/auth/login";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = options.Account is null
                ? JsonContent.Create(new { account, password, displayName = $"bench-{variant.Name}" })
                : JsonContent.Create(new { account, password })
        };
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!response.IsSuccessStatusCode) return (false, $"{path} 失败 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, null, elapsed);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("playerId", out var playerIdElement)) return (false, "认证响应缺少 playerId。", null, null, elapsed);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join("; ", values.Select(value => value.Split(';', 2)[0])) : string.Empty;
        var playerId = playerIdElement.GetString();
        var snapshotStarted = Stopwatch.GetTimestamp();
        using var snapshot = await client.GetAsync($"api/v1/players/{playerId}", cancellationToken);
        elapsed += Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds;
        return snapshot.IsSuccessStatusCode ? (true, null, cookies, playerId, elapsed) : (false, $"快照返回 {(int)snapshot.StatusCode}", cookies, playerId, elapsed);
    }

    internal static async Task<string?> HelloAndCommandAsync(Uri baseUri, string cookieHeader, string? fingerprint, CancellationToken cancellationToken, DateTimeOffset? simulationTarget = null, Guid? stablePlayerId = null)
    {
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/ws" };
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(cookieHeader)) socket.Options.SetRequestHeader("Cookie", cookieHeader);
        await socket.ConnectAsync(builder.Uri, cancellationToken);
        var requestId = stablePlayerId is { } stable
            ? TestCharacterSeeder.StableGuid($"ws:hello:{stable:D}:{simulationTarget:O}")
            : Guid.NewGuid();
        var packet = WireEnvelopeCodec.EncodeJson(WireMessageType.Hello, requestId, 1, new HelloRequest("bench", fingerprint));
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
        var result = await ReceiveFrameAsync(socket, cancellationToken);
        if (result.Type != WebSocketMessageType.Binary) return "握手首帧不是二进制帧";
        if (!WireEnvelopeCodec.TryDecode(result.Payload, out var envelope)) return "握手首帧不可解码";
        if (envelope.MessageType != WireMessageType.HelloAck) return $"握手首帧类型 {envelope.MessageType}，载荷 {Encoding.UTF8.GetString(envelope.Payload.Span)}";
        var commandId = stablePlayerId is { } player
            ? TestCharacterSeeder.StableGuid($"ws:presence:{player:D}:{simulationTarget:O}")
            : Guid.NewGuid();
        var command = new CommandEnvelopeDto(commandId.ToString("D"), "player.presence", JsonSerializer.SerializeToElement(new { requestId = commandId.ToString("D"), presence = "online" }, BenchJson.Options));
        await socket.SendAsync(WireEnvelopeCodec.EncodeJson(WireMessageType.Command, commandId, 2, command), WebSocketMessageType.Binary, true, cancellationToken);
        for (var i = 0; i < 12; i++)
        {
            result = await ReceiveFrameAsync(socket, cancellationToken);
            if (result.Type != WebSocketMessageType.Binary) return "命令回执前收到非二进制帧";
            if (!WireEnvelopeCodec.TryDecode(result.Payload, out envelope)) return "命令回执前帧不可解码";
            if (envelope.MessageType == WireMessageType.Error) return $"服务端错误 {Encoding.UTF8.GetString(envelope.Payload.Span)}";
            if (envelope.MessageType != WireMessageType.CommandResult) continue;
            if (envelope.RequestId != commandId) return "命令回执 RequestId 不一致";
            if (!WireEnvelopeCodec.TryDeserializeJson<CommandResultDto>(envelope, out var receipt) || receipt is null) return "命令回执不可解码";
            if (!receipt.Succeeded) return $"命令失败 {receipt.Code}: {receipt.Message}";
            if (simulationTarget is null) return null;
            var simulationId = stablePlayerId is { } playerForSimulation
                ? TestCharacterSeeder.StableGuid($"ws:simulation:{playerForSimulation:D}:{simulationTarget:O}")
                : Guid.NewGuid();
            var simulation = new CommandEnvelopeDto(simulationId.ToString("D"), "simulation.advance",
                JsonSerializer.SerializeToElement(new { requestId = simulationId.ToString("D"), targetTime = simulationTarget }, BenchJson.Options));
            await socket.SendAsync(WireEnvelopeCodec.EncodeJson(WireMessageType.Command, simulationId, 3, simulation), WebSocketMessageType.Binary, true, cancellationToken);
            for (var simulationFrame = 0; simulationFrame < 24; simulationFrame++)
            {
                result = await ReceiveFrameAsync(socket, cancellationToken);
                if (result.Type != WebSocketMessageType.Binary) return "模拟命令回执前收到非二进制帧";
                if (!WireEnvelopeCodec.TryDecode(result.Payload, out envelope)) return "模拟命令回执前帧不可解码";
                if (envelope.MessageType == WireMessageType.Error) return $"模拟命令服务端错误 {Encoding.UTF8.GetString(envelope.Payload.Span)}";
                if (envelope.MessageType != WireMessageType.CommandResult) continue;
                if (envelope.RequestId != simulationId) return "模拟命令回执 RequestId 不一致";
                if (!WireEnvelopeCodec.TryDeserializeJson<CommandResultDto>(envelope, out var simulationReceipt) || simulationReceipt is null) return "模拟命令回执不可解码";
                return simulationReceipt.Succeeded ? null : $"模拟命令失败 {simulationReceipt.Code}: {simulationReceipt.Message}";
            }
            return "未在 24 帧内收到模拟命令回执";
        }
        return "未在 12 帧内收到命令回执";
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.Count > 0) await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (stream.Length > ProtocolConstants.MaxFrameBytes + 64) throw new InvalidOperationException("WebSocket 帧超出协议上限。");
        } while (!result.EndOfMessage);
        return (result.MessageType, stream.ToArray());
    }
}

/// <summary>
/// 读取三个隔离基准库中同一玩家的业务持久化载荷并做字节级比较。
/// updated_at、lease、attempt 等运维字段不进入指纹，排除清单随结果输出。
/// </summary>
public static class BenchPersistenceComparer
{
    private static readonly string[] IncludedBusinessFields =
    ["identity.players(account_id/display_name/status)", "player_state_heads", "player_state_sections(payload/checksum/version)", "player_action_queue(payload/idempotency/status/timing)", "player_asset_balances", "player_asset_instances", "platform.command_receipts", "platform.section_flush_queue(payload/checksum/version)", "platform.integration_outbox(event/payload)", "economy.transactions/transaction_lines", "market.settlement_claims"];
    private static readonly string[] ExcludedOperationalFields =
    ["updated_at", "created_at", "last_seen_at", "last_active_at", "auth_sessions", "runtime_leases", "section_flush_queue attempts/leases/timestamps", "integration_outbox visible/lease/attempt fields"];

    public static async Task<PersistenceCompareResult> CompareAsync(
        string? databaseA, string databaseB, string databaseC, Guid playerId, CancellationToken cancellationToken = default)
    {
        var rows = new List<PersistenceVariantSnapshot>();
        if (!string.IsNullOrWhiteSpace(databaseA))
            rows.Add(await ReadAsync("a", databaseA, playerId, cancellationToken));
        rows.Add(await ReadAsync("b", databaseB, playerId, cancellationToken));
        rows.Add(await ReadAsync("c", databaseC, playerId, cancellationToken));
        var exact = rows.Select(row => row.BusinessFingerprint).Distinct(StringComparer.Ordinal).Count() == 1
                    && rows.All(row => row.Found);
        var warnings = rows.Where(row => !row.Found).Select(row => $"{row.Variant} 未找到玩家 {playerId}。").ToList();
        return new PersistenceCompareResult(
            playerId,
            exact,
            rows,
            "terminal-business-persistence",
            IncludedBusinessFields,
            ExcludedOperationalFields,
            exact ? Array.Empty<string>() : warnings.Count == 0 ? ["业务持久化指纹不同。"] : warnings);
    }

    public static async Task<PersistenceCompareBatchResult> CompareManyAsync(
        string? databaseA, string databaseB, string databaseC, IEnumerable<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        var results = new List<PersistenceCompareResult>();
        foreach (var playerId in playerIds.Distinct())
            results.Add(await CompareAsync(databaseA, databaseB, databaseC, playerId, cancellationToken));
        return new PersistenceCompareBatchResult(results.Count > 0 && results.All(item => item.ExactMatch), results);
    }

    private static async Task<PersistenceVariantSnapshot> ReadAsync(string variant, string connectionString, Guid playerId, CancellationToken cancellationToken)
    {
        BenchDatabase.EnsureBenchConnection(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT
                p.account_id, p.display_name, p.status,
                h.state_version, h.schema_version, h.last_simulated_at, h.game_data_fingerprint, h.checksum,
                s.section_kind, s.section_version, s.schema_version, s.codec, s.payload, s.raw_bytes, s.stored_bytes, s.checksum,
                q.slot, q.action_id, q.action_payload, q.action_version, q.status, q.next_due_at, q.idempotency_key,
                a.asset_id, a.variant, a.quantity, a.version,
                i.instance_id, i.definition_id, i.state::text, i.version
            FROM identity.players p
            LEFT JOIN game.player_state_heads h ON h.player_id = p.player_id
            LEFT JOIN game.player_state_sections s ON s.player_id = p.player_id
            LEFT JOIN game.player_action_queue q ON q.player_id = p.player_id
            LEFT JOIN economy.player_asset_balances a ON a.player_id = p.player_id
            LEFT JOIN economy.player_asset_instances i ON i.owner_player_id = p.player_id
            WHERE p.player_id = $1
            ORDER BY s.section_kind NULLS FIRST, q.slot NULLS FIRST, a.asset_id NULLS FIRST, a.variant NULLS FIRST, i.instance_id NULLS FIRST
            """, connection);
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        using var canonical = new MemoryStream();
        using var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            found = true;
            WriteNullableString(writer, reader, 0); // account_id
            WriteNullableString(writer, reader, 1); // display_name
            WriteNullableInt(writer, reader, 2); // status
            WriteNullableLong(writer, reader, 3); // head state version
            WriteNullableInt(writer, reader, 4); // head schema
            WriteNullableDate(writer, reader, 5); // business simulation clock
            WriteNullableString(writer, reader, 6); // game data fingerprint
            WriteNullableString(writer, reader, 7); // head checksum
            WriteNullableString(writer, reader, 8); // section kind
            WriteNullableLong(writer, reader, 9);
            WriteNullableInt(writer, reader, 10);
            WriteNullableString(writer, reader, 11);
            WriteNullableBytes(writer, reader, 12);
            WriteNullableInt(writer, reader, 13);
            WriteNullableInt(writer, reader, 14);
            WriteNullableString(writer, reader, 15);
            WriteNullableInt(writer, reader, 16); // action slot
            WriteNullableString(writer, reader, 17);
            WriteNullableBytes(writer, reader, 18);
            WriteNullableLong(writer, reader, 19);
            WriteNullableString(writer, reader, 20);
            WriteNullableDate(writer, reader, 21); // next_due_at is business queue timing
            WriteNullableString(writer, reader, 22);
            WriteNullableString(writer, reader, 23);
            WriteNullableString(writer, reader, 24);
            WriteNullableDouble(writer, reader, 25);
            WriteNullableLong(writer, reader, 26);
            WriteNullableString(writer, reader, 27);
            WriteNullableString(writer, reader, 28);
            WriteNullableString(writer, reader, 29);
            WriteNullableLong(writer, reader, 30);
        }
        await reader.DisposeAsync();
        await AppendRowsAsync(connection, writer, "command_receipts", """
            SELECT request_id, command, payload_hash, result_payload, state_version
            FROM platform.command_receipts WHERE player_id=$1 ORDER BY request_id
            """, playerId, cancellationToken);
        await AppendRowsAsync(connection, writer, "section_flush_queue", """
            SELECT section_kind, state_version, schema_version, codec, payload, raw_bytes, checksum,
                   game_data_fingerprint, last_simulated_at, last_error
            FROM platform.section_flush_queue WHERE player_id=$1 ORDER BY section_kind
            """, playerId, cancellationToken);
        await AppendRowsAsync(connection, writer, "integration_outbox", """
            SELECT outbox_id, aggregate_type, aggregate_id, event_type, payload, occurred_at, delivered_at
            FROM platform.integration_outbox WHERE aggregate_id=$1 ORDER BY outbox_id
            """, playerId, cancellationToken);
        await AppendRowsAsync(connection, writer, "economy_transactions", """
            SELECT t.transaction_id, t.idempotency_key, t.request_hash, t.transaction_type, t.actor_id,
                   t.result_payload, l.line_no, l.asset_id, l.variant, l.instance_id, l.delta
            FROM economy.transactions t
            JOIN economy.transaction_lines l ON l.transaction_id=t.transaction_id
            WHERE l.player_id=$1 ORDER BY t.transaction_id, l.line_no
            """, playerId, cancellationToken);
        await AppendRowsAsync(connection, writer, "market_settlement_claims", """
            SELECT request_id, settled_trade_count
            FROM market.settlement_claims WHERE player_id=$1 ORDER BY request_id
            """, playerId, cancellationToken);
        writer.Flush();
        var hash = Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant();
        return new PersistenceVariantSnapshot(variant, found, hash, canonical.Length);
    }

    private static async Task AppendRowsAsync(NpgsqlConnection connection, BinaryWriter writer, string label, string sql, Guid playerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            writer.Write(label);
            writer.Write(reader.FieldCount);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++) WriteValue(writer, reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));
        }
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)0);
                break;
            case byte[] bytes:
                writer.Write((byte)1); writer.Write(bytes.Length); writer.Write(bytes);
                break;
            case DateTimeOffset timestamp:
                writer.Write((byte)2); writer.Write(timestamp.UtcTicks);
                break;
            case DateTime timestamp:
                writer.Write((byte)2); writer.Write(timestamp.ToUniversalTime().Ticks);
                break;
            case Guid guid:
                writer.Write((byte)3); writer.Write(guid.ToByteArray());
                break;
            case double number:
                writer.Write((byte)4); writer.Write(number);
                break;
            case float number:
                writer.Write((byte)4); writer.Write((double)number);
                break;
            case long number:
                writer.Write((byte)5); writer.Write(number);
                break;
            case int number:
                writer.Write((byte)5); writer.Write((long)number);
                break;
            case short number:
                writer.Write((byte)5); writer.Write((long)number);
                break;
            case bool boolean:
                writer.Write((byte)6); writer.Write(boolean);
                break;
            default:
                writer.Write((byte)7); writer.Write(value.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteNullableString(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(-1); return; }
        writer.Write(reader.GetValue(ordinal)?.ToString() ?? string.Empty);
    }

    private static void WriteNullableBytes(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(-1); return; }
        var value = (byte[])reader.GetValue(ordinal);
        writer.Write(value.Length); writer.Write(value);
    }

    private static void WriteNullableInt(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(int.MinValue); return; }
        writer.Write(Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
    }

    private static void WriteNullableLong(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(long.MinValue); return; }
        writer.Write(Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
    }

    private static void WriteNullableDouble(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(double.NaN); return; }
        writer.Write(Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
    }

    private static void WriteNullableDate(BinaryWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.Write(long.MinValue); return; }
        writer.Write(reader.GetFieldValue<DateTimeOffset>(ordinal).UtcTicks);
    }
}

public sealed record PersistenceVariantSnapshot(string Variant, bool Found, string BusinessFingerprint, long CanonicalBytes);
public sealed record PersistenceCompareResult(Guid PlayerId, bool ExactMatch, IReadOnlyList<PersistenceVariantSnapshot> Variants, string Scope, IReadOnlyList<string> IncludedBusinessFields, IReadOnlyList<string> ExcludedOperationalFields, IReadOnlyList<string> Warnings);
public sealed record PersistenceCompareBatchResult(bool ExactMatch, IReadOnlyList<PersistenceCompareResult> Players);

public static class BenchReportWriter
{
    public static async Task WriteAsync(string path, BenchReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, BenchJson.Indented));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".csv"), ToCsv([report.Metric]));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".stages.csv"), BenchStageReports.ToStageCsv(report));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".counters.csv"), BenchStageReports.ToCounterCsv(report));
    }

    public static async Task<IReadOnlyList<BenchReport>> ReadManyAsync(string path)
    {
        var files = ReportFiles(path);
        var result = new List<BenchReport>();
        foreach (var file in files)
            result.Add(JsonSerializer.Deserialize<BenchReport>(await File.ReadAllTextAsync(file), BenchJson.Options) ?? throw new InvalidOperationException($"报告为空：{file}"));
        return result;
    }

    public static async Task WriteMissingStageCsvAsync(string path, IReadOnlyList<BenchReport> reports)
    {
        var files = ReportFiles(path);
        if (files.Length != reports.Count) throw new InvalidOperationException("报告文件集合在读取期间发生变化。");
        for (var i = 0; i < files.Length; i++)
        {
            var output = Path.ChangeExtension(files[i], ".stages.csv");
            if (!File.Exists(output)) await File.WriteAllTextAsync(output, BenchStageReports.ToStageCsv(reports[i]));
            var counters = Path.ChangeExtension(files[i], ".counters.csv");
            if (!File.Exists(counters)) await File.WriteAllTextAsync(counters, BenchStageReports.ToCounterCsv(reports[i]));
        }
    }

    private static string[] ReportFiles(string path)
        => path.Contains('*')
            ? Directory.EnumerateFiles(Path.GetDirectoryName(path) ?? ".", Path.GetFileName(path))
                .Where(file => !file.EndsWith(".comparison.json", StringComparison.OrdinalIgnoreCase))
                .Where(file => !Path.GetFileName(file).StartsWith("compare-", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray()
            : [path];

    private static string ToCsv(IReadOnlyList<BenchMetricRecord> reports)
    {
        var header = "variant,scenario,players,duration,total_wall_ms,cpu_ms,simulation_ms,entity_create_ms,system_tick_ms,snapshot_ms,database_ms,network_ms,allocated_bytes,gc0,gc1,gc2,rss_mb,p50_ms,p95_ms,p99_ms,max_ms,instrumentation_listener_ms,sample_count,measurement_status,errors,mode,simulated_hours,offline_login_wall_ms,offline_login_cpu_ms,offline_login_allocated_bytes,offline_login_simulation_ms,offline_login_system_tick_ms,offline_login_database_ms";
        var rows = reports.Select(metric => string.Join(',', metric.Variant, metric.Scenario, metric.Players, metric.Duration.ToString(CultureInfo.InvariantCulture), Nullable(metric.TotalWallMs), Nullable(metric.CpuMs), Nullable(metric.SimulationMs), Nullable(metric.EntityCreateMs), Nullable(metric.SystemTickMs), Nullable(metric.SnapshotMs), Nullable(metric.DatabaseMs), Nullable(metric.NetworkMs), Nullable(metric.AllocatedBytes), Nullable(metric.Gc0), Nullable(metric.Gc1), Nullable(metric.Gc2), Nullable(metric.RssMb), Nullable(metric.P50Ms), Nullable(metric.P95Ms), Nullable(metric.P99Ms), Nullable(metric.MaxMs), Nullable(metric.InstrumentationListenerMs), metric.SampleCount, metric.MeasurementStatus, metric.Errors, metric.Mode, Nullable(metric.SimulatedHours), Nullable(metric.OfflineLoginWallMs), Nullable(metric.OfflineLoginCpuMs), Nullable(metric.OfflineLoginAllocatedBytes), Nullable(metric.OfflineLoginSimulationMs), Nullable(metric.OfflineLoginSystemTickMs), Nullable(metric.OfflineLoginDatabaseMs)));
        return header + Environment.NewLine + string.Join(Environment.NewLine, rows) + Environment.NewLine;
    }

    private static string Nullable<T>(T? value) where T : struct => value?.ToString() ?? string.Empty;
}


