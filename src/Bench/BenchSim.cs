using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;

namespace DaoHua.Bench;

/// <summary>
/// 纯内存同源战斗对照压测：同一批确定性玩家、同一真实配表、同一战斗链路
/// （SimulationEngine / ActionParticipationSessionEngine / 真实消耗品与触发器），
/// 仅切换消耗品判定调度（PeriodicTick 现状 5 秒 / EventDriven 逐边界即时）。
/// 不连接任何数据库或网络；输出墙钟、CPU、分配、GC、边界数、判定与激活计数。
/// </summary>
public static class BenchSim
{
    private const string ItemZhixue = "item.zhixue-powder";
    private const string ItemShunqi = "item.shunqi-pill";
    private const string ItemLeiji = "item.leiji-talisman";
    private const string ItemJuling = "item.juling-formation-disk";
    private const string ActionHunt = "combat.hunt-qi-beast";
    // 云巅草庐遭遇含精英怪（enemy.demon-ape，每 5 战一只）——has-elite 条件真实翻转。
    private const string NodeHome = "node.cloud-peak";
    private const double ItemBudget = 1_000_000d;

    public static int Run(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"不支持的位置参数：{args[i]}");
            values[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : "true";
        }
        var options = new SimOptions(
            Solo: ReadInt(values, "solo", 500, 0, 3000),
            Teams: ReadInt(values, "teams", 100, 0, 1000),
            Minutes: ReadDouble(values, "minutes", 60, 0.01, 2880),
            Seed: ReadLong(values, "seed", 20261112L),
            Modes: values.GetValueOrDefault("modes", "tick,event").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => text.ToLowerInvariant() switch
                {
                    "tick" or "periodictick" => ConsumableScheduleMode.PeriodicTick,
                    "event" or "eventdriven" => ConsumableScheduleMode.EventDriven,
                    var other => throw new ArgumentException($"未知模式：{other}")
                }).ToArray(),
            Report: values.GetValueOrDefault("report", "artifacts/bench/consumable-sim.json"),
            Warmup: ReadBool(values, "warmup", true));

        var catalog = GameDataCatalog.LoadEmbedded();
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var target = epoch.AddMinutes(options.Minutes);

        // 预热：两种模式各跑一小批，消除 JIT 首次编译对正式测量的污染。
        if (options.Warmup)
        {
            RunCohort(catalog, options with { Solo = Math.Min(options.Solo, 8), Teams = Math.Min(options.Teams, 2), Minutes = 0.5 },
                ConsumableScheduleMode.PeriodicTick, epoch, target: epoch.AddMinutes(0.5), warmup: true);
            RunCohort(catalog, options with { Solo = Math.Min(options.Solo, 8), Teams = Math.Min(options.Teams, 2), Minutes = 0.5 },
                ConsumableScheduleMode.EventDriven, epoch, target: epoch.AddMinutes(0.5), warmup: true);
        }

        var results = new List<CohortResult>();
        foreach (var mode in options.Modes)
            results.Add(RunCohort(catalog, options, mode, epoch, target, warmup: false));

        var report = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            fingerprint = catalog.Fingerprint,
            scale = new { solo = options.Solo, teams = options.Teams, membersPerTeam = 5, players = options.Solo + options.Teams * 5 },
            minutes = options.Minutes,
            seed = options.Seed,
            machine = new { processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() },
            cohorts = results
        };
        var reportPath = Path.GetFullPath(options.Report);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, BenchJson.Indented));

        // 人读摘要：每组一行，末尾附 event/tick 倍率。
        Console.WriteLine();
        Console.WriteLine($"{"mode",-12} {"wall ms",12} {"cpu ms",12} {"alloc MB",10} {"GC0/1/2",12} {"boundaries",12} {"eval calls",12} {"trig evals",12} {"skill evals",12} {"activations",12}");
        foreach (var r in results)
            Console.WriteLine($"{r.Mode,-12} {r.WallMs,12:0} {r.CpuMs,12:0} {r.AllocatedBytes / 1048576d,10:0.##} {$"{r.Gen0}/{r.Gen1}/{r.Gen2}",12} {r.BoundaryCount,12} {r.EvalCalls,12} {r.TriggerEvals,12} {r.SkillTriggerEvals,12} {r.Activations,12}");
        if (results.FirstOrDefault(r => r.Mode == "tick") is { } tick
            && results.FirstOrDefault(r => r.Mode == "event") is { } evt)
            Console.WriteLine($"event/tick 倍率：wall {evt.WallMs / Math.Max(1, tick.WallMs):0.###}× cpu {evt.CpuMs / Math.Max(1, tick.CpuMs):0.###}× alloc {evt.AllocatedBytes / Math.Max(1.0, (double)tick.AllocatedBytes):0.###}× eval {evt.EvalCalls / Math.Max(1.0, (double)tick.EvalCalls):0.###}× trig {evt.TriggerEvals / Math.Max(1.0, (double)tick.TriggerEvals):0.###}×");
        Console.WriteLine($"报告：{reportPath}");
        return 0;
    }

    /// <summary>跑一组完整对照：500 单人离线补算式推进 + 100 个五人共享会话推进。</summary>
    private static CohortResult RunCohort(GameDataCatalog catalog, SimOptions options,
        ConsumableScheduleMode mode, DateTimeOffset epoch, DateTimeOffset target, bool warmup)
    {
        using var metrics = new MetricsCollector();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var pauseBefore = GC.GetTotalPauseDuration();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var watch = Stopwatch.StartNew();

        var solo = RunSolo(catalog, options, mode, epoch, target);
        var teams = RunTeams(catalog, options, mode, epoch, target);

        watch.Stop();
        metrics.Flush();
        var result = new CohortResult(
            Mode: mode == ConsumableScheduleMode.PeriodicTick ? "tick" : "event",
            WallMs: watch.Elapsed.TotalMilliseconds,
            CpuMs: (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds,
            AllocatedBytes: GC.GetAllocatedBytesForCurrentThread() - allocBefore,
            Gen0: GC.CollectionCount(0) - gen0Before,
            Gen1: GC.CollectionCount(1) - gen1Before,
            Gen2: GC.CollectionCount(2) - gen2Before,
            PauseMs: (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
            BoundaryCount: metrics.Stage("domain.combat.event").Count,
            BoundaryMs: metrics.Stage("domain.combat.event").SumMs,
            EvalCalls: metrics.Counter("consumable.eval_calls"),
            Activations: metrics.Counter("consumable.activations"),
            TriggerEvals: metrics.Counter("consumable.trigger_evals"),
            SkillTriggerEvals: metrics.Counter("skill.trigger_evals"),
            SoloRepeats: solo.Repeats,
            TeamRepeats: teams.Repeats,
            SoloAutoUseEvents: solo.AutoUseEvents,
            TeamAutoUseEvents: teams.AutoUseEvents,
            TeamCombatAdvances: teams.CombatAdvances,
            Digest: new { solo = solo.Digest, teams = teams.Digest },
            StageMs: metrics.TopStages(12));
        if (!warmup)
            Console.WriteLine($"[{result.Mode}] solo {result.SoloRepeats:0} repeats / teams {result.TeamRepeats:0} repeats，激活 {result.Activations} 次 / 判定 {result.EvalCalls} 次 / 边界 {result.BoundaryCount} 个");
        return result;
    }

    /// <summary>单人组：全真 SimulationEngine 一次性离线补算（内部逐资源/战斗边界推进）。</summary>
    private static SoloTotals RunSolo(GameDataCatalog catalog, SimOptions options,
        ConsumableScheduleMode mode, DateTimeOffset epoch, DateTimeOffset target)
    {
        var totals = new SoloTotals();
        for (var i = 0; i < options.Solo; i++)
        {
            var player = BuildPlayer(catalog, $"solo:{i}", epoch);
            var wallet = new AssetWallet(player.PlayerId);
            foreach (var itemId in ConsumableItems)
                wallet.Apply(new AssetDelta(player.PlayerId, new AssetKey(new ItemId(itemId)), ItemBudget, "seed"));
            var result = SimulationEngine.AdvanceTo(player, target, catalog, wallet, null, consumableSchedule: mode);
            totals.Repeats += result.CompletedRepeats;
            totals.AutoUseEvents += result.Events.Count(e => e.Code == "auto-use.activated");
            totals.EndHitpoints += player.Resources.Hitpoints;
            totals.EndManapoints += player.Resources.Manapoints;
            // 引擎推进在内部钱包副本上记账：实际消耗以 result.AssetDeltas（负量为扣件）为准。
            foreach (var delta in result.AssetDeltas)
                if (ConsumableItemSet.Contains(delta.Asset.ItemId.Value))
                    totals.Spent[delta.Asset.ItemId.Value] = totals.Spent.GetValueOrDefault(delta.Asset.ItemId.Value) - delta.QuantityDelta;
        }
        // 单人判定/激活走 RuntimeInstrumentation 计数（由 MetricsCollector 汇总），此处只记摘要。
        totals.Digest = $"hp={totals.EndHitpoints:0.##} mp={totals.EndManapoints:0.##} spent={string.Join(',', ConsumableItems.Select(id => $"{id.Split('.')[^1]}:{totals.Spent.GetValueOrDefault(id):0}"))}";
        return totals;
    }

    /// <summary>组队组：全真 ActionParticipationSessionEngine（开打定格→共享战斗→散场结算）。</summary>
    private static TeamTotals RunTeams(GameDataCatalog catalog, SimOptions options,
        ConsumableScheduleMode mode, DateTimeOffset epoch, DateTimeOffset target)
    {
        var totals = new TeamTotals();
        var definition = catalog.Actions.Get(new ActionId(ActionHunt));
        for (var t = 0; t < options.Teams; t++)
        {
            var members = Enumerable.Range(0, 5)
                .Select(m => BuildPlayer(catalog, $"team:{t}:{m}", epoch)).ToArray();
            var snapshots = members.Select(m => m.CaptureActionParticipantSnapshot(catalog)).ToArray();
            var encounter = SimulationEngine.ResolveCombatEncounterForAction(members[0], definition, 0, catalog);
            var spec = new ActionParticipationSpec(
                TestCharacterSeeder.StableGuid($"sim-team:{options.Seed}:{t}").ToString("D"),
                encounter.Id, NodeHome, options.Seed + t,
                snapshots.Max(m => m.SearchSpeedPoints), epoch.ToUnixTimeSeconds(), snapshots)
            { ActionId = definition.Id };
            var balances = members.SelectMany(m => ConsumableItems.Select(itemId =>
                new AssetBalance(m.PlayerId, new AssetKey(new ItemId(itemId)), ItemBudget, 0))).ToArray();
            var session = ActionParticipationSessionEngine.Create(spec, catalog, balances, mode);
            var settlements = session.AdvanceTo(target);
            totals.EvalCalls += session.ConsumableEvaluationCount;
            totals.Activations += session.ConsumableActivationCount;
            totals.CombatAdvances += session.CombatAdvanceCount;
            foreach (var settlement in settlements)
            {
                totals.Repeats += settlement.CompletedRepeats;
                totals.AutoUseEvents += settlement.Events.Count(e => e.Code == "auto-use.activated");
                totals.EndHitpoints += settlement.Hitpoints;
            }
        }
        totals.Digest = $"hp={totals.EndHitpoints:0.##} repeats={totals.Repeats:0.##} autoUse={totals.AutoUseEvents}";
        return totals;
    }

    /// <summary>确定性玩家画像：真实装备/功法/属性解析 + 三战斗消耗品槽 + 玩家级触发条件。</summary>
    private static PlayerAggregate BuildPlayer(GameDataCatalog catalog, string tag, DateTimeOffset epoch)
    {
        var player = PlayerAggregate.Create(new PlayerId(TestCharacterSeeder.StableGuid($"sim-player:{tag}")),
            new AccountId(TestCharacterSeeder.StableGuid($"sim-account:{tag}")), $"sim-{tag}", epoch, catalog.Fingerprint);
        // 境界 20：对 T1 讨伐能持续作战但会真实失血/耗蓝，触发条件既有命中也有不满足期，
        // 保证消耗品判定路径在整段模拟里持续承压（1 级会在首波团灭，画像退化）。
        player.Progression.RealmLevel = 20;
        player.World.ExplorationSeed = (int)(StableHash(tag) & 0x7fffffff);
        player.World.NodeId = NodeHome;
        player.LearnAbility(new AbilityId("ability.heavy-slash"));
        player.LearnAbility(new AbilityId("ability.palm-lightning"));
        player.ReplaceCombatSlots(
            [new PlayerAbilitySlot("ability.heavy-slash", 1), new PlayerAbilitySlot("ability.palm-lightning", 1)],
            [new PlayerEquipmentSlot("equipment.qingfeng-sword", 0)]);
        // 战斗栏：药品槽止血散 + 通用槽顺气丸/雷击符/聚灵阵盘（持续型覆盖 sustained 路径）。
        player.Settings.AutoUseSlots.Add(new("combat", 0, ItemZhixue, 1, true, Group: AutoUseSlotState.SlotGroupPotion));
        player.Settings.AutoUseSlots.Add(new("combat", 0, ItemShunqi, 1, true, Group: AutoUseSlotState.SlotGroupGeneral));
        player.Settings.AutoUseSlots.Add(new("combat", 1, ItemLeiji, 1, true, Group: AutoUseSlotState.SlotGroupGeneral));
        player.Settings.AutoUseSlots.Add(new("combat", 2, ItemJuling, 1, true, Group: AutoUseSlotState.SlotGroupGeneral));
        // 玩家自定义触发条件（压测画像核心输入）：单槽满配 4 条 AND，覆盖全部依赖类型——
        // 自身/集合数值/布尔集合（有精英、有Boss）/目标单位/战场时钟，是求值最贵的形态。
        player.Loadout.ConsumableTriggers[ItemZhixue] = Triggers(
            """[{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than","value":60},{"dependency":"enemies","condition":"has-boss","comparator":"is-inactive"},{"dependency":"allies","condition":"lowest-hp-percentage","comparator":"less-than","value":90},{"dependency":"battle","condition":"elapsed-seconds","comparator":"greater-than","value":3}]""");
        player.Loadout.ConsumableTriggers[ItemShunqi] = Triggers(
            """[{"dependency":"self","condition":"current-mana-percentage","comparator":"less-than","value":50},{"dependency":"self","condition":"current-hp","comparator":"greater-than","value":0},{"dependency":"enemies","condition":"alive-count","comparator":"greater-than-equal","value":1},{"dependency":"battle","condition":"elapsed-seconds","comparator":"greater-than-equal","value":1}]""");
        player.Loadout.ConsumableTriggers[ItemLeiji] = Triggers(
            """[{"dependency":"enemies","condition":"has-elite","comparator":"is-active"},{"dependency":"enemies","condition":"alive-count","comparator":"greater-than-equal","value":1},{"dependency":"self","condition":"current-mana-percentage","comparator":"greater-than-equal","value":20},{"dependency":"target-enemy","condition":"current-hp-percentage","comparator":"greater-than-equal","value":15}]""");
        var resolved = PlayerAttributeResolver.Resolve(player, catalog).ToCombatant(player.Progression.CombatExperience);
        player.Resources.Hitpoints = player.Resources.MaxHitpoints = resolved.MaxHitpoints;
        player.Resources.Manapoints = player.Resources.MaxManapoints = resolved.MaxManapoints;
        player.Enqueue(new ActionIntent(new ActionId(ActionHunt), -1), epoch, catalog.Actions);
        return player;
    }

    private static readonly string[] ConsumableItems = [ItemZhixue, ItemShunqi, ItemLeiji, ItemJuling];
    private static readonly HashSet<string> ConsumableItemSet = new(ConsumableItems, StringComparer.Ordinal);

    private static JsonElement[] Triggers(string json)
        => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();

    /// <summary>确定性字符串散列（与 StableGuid 同一思路：内容定序种子）。</summary>
    private static long StableHash(string text)
    {
        var hash = 1469598103934665603L;
        foreach (var c in text) { hash ^= c; hash *= 1099511628211L; }
        return hash & long.MaxValue;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"--{key} 必须在 {min} 到 {max} 之间。");
        return parsed;
    }

    private static long ReadLong(IReadOnlyDictionary<string, string> values, string key, long fallback)
        => values.TryGetValue(key, out var value) && long.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback, double min, double max)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        if (!double.TryParse(value, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"--{key} 必须在 {min} 到 {max} 之间。");
        return parsed;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => !values.TryGetValue(key, out var value) ? fallback
            : bool.TryParse(value, out var parsed) ? parsed
            : throw new ArgumentException($"--{key} 必须是 true 或 false。");

    private sealed record SimOptions(int Solo, int Teams, double Minutes, long Seed,
        IReadOnlyList<ConsumableScheduleMode> Modes, string Report, bool Warmup);

    private sealed class SoloTotals
    {
        public double Repeats; public long AutoUseEvents; public double EndHitpoints; public double EndManapoints;
        public readonly Dictionary<string, double> Spent = new(StringComparer.Ordinal);
        public string Digest = "";
    }

    private sealed class TeamTotals
    {
        public double Repeats; public long AutoUseEvents; public double EndHitpoints;
        public long EvalCalls; public long Activations; public long CombatAdvances;
        public string Digest = "";
    }

    public sealed record CohortResult(string Mode, double WallMs, double CpuMs, long AllocatedBytes,
        int Gen0, int Gen1, int Gen2, double PauseMs, long BoundaryCount, double BoundaryMs,
        long EvalCalls, long Activations, double SoloRepeats, double TeamRepeats,
        long SoloAutoUseEvents, long TeamAutoUseEvents, long TeamCombatAdvances,
        long TriggerEvals, long SkillTriggerEvals,
        object Digest, IReadOnlyDictionary<string, object> StageMs);

    /// <summary>RuntimeInstrumentation 计量汇聚：直方图按 stage 计次数与总毫秒，计数器按 name 求和。</summary>
    private sealed class MetricsCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, (long Count, double SumMs)> _stages = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

        public MetricsCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeInstrumentation.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                var stage = Tag(tags, "stage");
                _stages.AddOrUpdate(stage, (1, value), (_, a) => (a.Count + 1, a.SumMs + value));
            });
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var name = Tag(tags, "name");
                _counters.AddOrUpdate(name, value, (_, a) => a + value);
            });
            _listener.Start();
        }

        private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (var tag in tags)
                if (tag.Key == key) return tag.Value as string ?? "?";
            return "?";
        }

        public (long Count, double SumMs) Stage(string stage) => _stages.GetValueOrDefault(stage);
        public long Counter(string name) => _counters.GetValueOrDefault(name);
        public IReadOnlyDictionary<string, object> TopStages(int take)
            => _stages.OrderByDescending(pair => pair.Value.SumMs).Take(take)
                .ToDictionary(pair => pair.Key,
                    pair => (object)new { count = pair.Value.Count, sumMs = pair.Value.SumMs },
                    StringComparer.Ordinal);
        public void Flush() { }
        public void Dispose() => _listener.Dispose();
    }
}
