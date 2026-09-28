using System.Text;
using YuoCore.Ecs;

namespace DaoHua.EcsDifferential;

/// <summary>
/// 随机操作执行器。实体选择只依赖执行器自己的 id 台账（创建/销毁序列由
/// 伪随机流唯一决定），不读世界状态回推，保证两侧在分叉后操作序列仍逐一对齐。
/// </summary>
public sealed class Runner
{
    private readonly int _ops;
    private readonly Ledger _ledger;
    private XorShift64 _rng;
    private YuoWorld _world;

    // 全部由操作序列决定、跨变体对齐的簿记
    private readonly List<long> _created = new();     // 曾创建过的实体 id（含已销毁）
    private readonly List<long> _destroyedPool = new(); // 我主动销毁过的 id，供显式复用
    private int _serial;
    private int _namedCount;
    private int _namedReuseCursor;

    private static readonly Type[] BuffTypes =
        { typeof(BuffBaseComponent), typeof(PoisonBuffComponent), typeof(RegenBuffComponent) };

    public Runner(int seed, int ops, Ledger ledger)
    {
        _ops = ops;
        _ledger = ledger;
        _rng = new XorShift64((ulong)seed * 0x9E3779B97F4A7C15UL + 0x12345UL);
    }

    private int NextSerial() => ++_serial;

    /// <summary>分配组件实例流水号（定向用例也用）。</summary>
    public int AllocSerial() => ++_serial;

    public YuoWorld World => _world;

    /// <summary>从台账中选一个实体 id；不做活性过滤——选中死实体也是合法边界输入。</summary>
    public long PickEntityId()
    {
        if (_created.Count == 0) return -1;
        return _created[_rng.Range(_created.Count)];
    }

    private YuoEntity PickEntity() => _world.GetEntity(PickEntityId());

    public void Run()
    {
        DiffProbe.L = _ledger;
        DiffProbe.CallbackCounts.Clear();
        try
        {
            _world = WorldFactory.Create();
            var names = _world.GetAllSystem.Select(s => s.SystemName).ToList();
            _ledger.SysList("[\"" + string.Join("\",\"", names) + "\"]");
            for (var i = 0; i < _ops; i++)
            {
                Step(i);
            }

            Finish("shutdown");
        }
        finally
        {
            DiffProbe.L = null;
        }
    }

    /// <summary>收尾：OnQuit + OnDestroy 也是可观察派发面（IExitGame/组件销毁）。</summary>
    public void Finish(string reason)
    {
        _ledger.OpSeq = _ops; // 收尾行挂在 ops 序号后
        try
        {
            _world.OnQuit();
            _world.OnDestroy();
        }
        catch (Exception e)
        {
            _ledger.Ex(reason, e);
        }

        _ledger.Final(CountsPayload() + ",\"entities\":" + _world.Entities.Count);
    }

    /// <summary>"counts":{...} 片段：回调按 sys|tag 键汇总，排序输出保证跨变体对齐。</summary>
    public static string CountsPayload()
    {
        var counts = new StringBuilder(256);
        counts.Append("\"counts\":{");
        var first = true;
        foreach (var kv in DiffProbe.CallbackCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) counts.Append(',');
            first = false;
            counts.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
        }

        counts.Append('}');
        return counts.ToString();
    }

    /// <summary>执行单个随机操作并写台账。</summary>
    public void Step(int seq)
    {
        _ledger.OpSeq = seq;
        _ledger.CbIndex = 0;
        var op = PickOp();
        DiffProbe.Batch = new Dictionary<string, List<long>>();
        try
        {
            op();
        }
        catch (Exception e)
        {
            _ledger.Ex("op", e);
        }

        EmitBatch();
        _ledger.St(Fnv64.Hash(WorldDump.Build(_world)), _world.Entities.Count);
        if (seq % 250 == 0 || seq == _ops - 1)
        {
            _ledger.Dump(WorldDump.Build(_world));
        }
    }

    /// <summary>定向用例的单步：显式操作名 + 动作体，台账骨架与随机路径一致。</summary>
    public void Do(int seq, string kind, string args, Action body)
    {
        _ledger.OpSeq = seq;
        _ledger.CbIndex = 0;
        _ledger.Op(kind, args);
        DiffProbe.Batch = new Dictionary<string, List<long>>();
        try
        {
            body();
        }
        catch (Exception e)
        {
            _ledger.Ex("op", e);
        }

        EmitBatch();
        _ledger.St(Fnv64.Hash(WorldDump.Build(_world)), _world.Entities.Count);
    }

    private void EmitBatch()
    {
        var batch = DiffProbe.Batch;
        DiffProbe.Batch = null;
        if (batch is { Count: > 0 }) _ledger.Bs(batch);
    }

    /// <summary>开新世界并登记定向用例名；返回世界实例。</summary>
    public YuoWorld BeginCase(string name)
    {
        _world = WorldFactory.Create();
        _ledger.CaseName = name;
        _ledger.Case(name);
        _created.Clear();
        _destroyedPool.Clear();
        _namedCount = 0;
        _namedReuseCursor = 0;
        var names = _world.GetAllSystem.Select(s => s.SystemName).ToList();
        _ledger.SysList("[\"" + string.Join("\",\"", names) + "\"]");
        return _world;
    }

    /// <summary>用例收尾：退出+销毁世界（过程也进台账），随后清掉用例上下文。</summary>
    public void EndCase(int seqBase)
    {
        _ledger.OpSeq = seqBase;
        try
        {
            _world.OnQuit();
            _world.OnDestroy();
        }
        catch (Exception e)
        {
            _ledger.Ex("teardown", e);
        }

        _ledger.St(Fnv64.Hash(WorldDump.Build(_world)), _world.Entities.Count);
        _ledger.CaseName = null;
    }

    private Action PickOp()
    {
        // 权重总和 122
        const int total = 122;
        var roll = _rng.Range(total);
        // 权重表
        if ((roll -= 10) < 0) return OpCreate;
        if ((roll -= 3) < 0) return OpCreateNamed;
        if ((roll -= 4) < 0) return OpCreateWithId;
        if ((roll -= 9) < 0) return OpDestroy;
        if ((roll -= 3) < 0) return OpDestroyForce;
        if ((roll -= 14) < 0) return OpAdd;
        if ((roll -= 4) < 0) return OpAddInit;
        if ((roll -= 5) < 0) return OpAddBaseTyped;
        if ((roll -= 10) < 0) return OpRemove;
        if ((roll -= 4) < 0) return OpDestroyComponent;
        if ((roll -= 4) < 0) return OpReplace;
        if ((roll -= 4) < 0) return OpReAdd;
        if ((roll -= 6) < 0) return OpUpdate;
        if ((roll -= 2) < 0) return OpFixedUpdate;
        if ((roll -= 2) < 0) return OpLateUpdate;
        if ((roll -= 5) < 0) return OpQueryGet;
        if ((roll -= 3) < 0) return OpQueryBase;
        if ((roll -= 3) < 0) return OpQueryAnyType;
        if ((roll -= 3) < 0) return OpForEach;
        if ((roll -= 2) < 0) return OpGetAll;
        if ((roll -= 3) < 0) return OpSysMembers;
        if ((roll -= 2) < 0) return OpBuildSystem;
        if ((roll -= 2) < 0) return OpToggleSystem;
        if ((roll -= 2) < 0) return OpRunGroup;
        if ((roll -= 2) < 0) return OpRunPulseAll;
        if ((roll -= 2) < 0) return OpRunTagOnEntity;
        if ((roll -= 3) < 0) return OpAddChild;
        if ((roll -= 1) < 0) return OpRemoveChild;
        if ((roll -= 5) < 0) return OpChaos;
        return OpWorldEntities;
    }

    private void OpCreate()
    {
        _ledger.Op("create", null);
        var e = new YuoEntity(_world);
        _created.Add(e.ID);
        _ledger.Q("id", e.ID.ToString());
    }

    private void OpCreateNamed()
    {
        var reuse = _namedCount > 4 && _rng.Chance(25);
        var name = reuse ? $"re{_namedReuseCursor++ % _namedCount}" : $"n{_namedCount++}";
        _ledger.Op("create_named", $"\"name\":\"{name}\"");
        var e = new YuoEntity(_world, name);
        _created.Add(e.ID);
        _ledger.Q("id", e.ID.ToString());
        _ledger.Q("lookup", (_world.GetEntity(name)?.ID ?? -1).ToString());
    }

    /// <summary>显式 id 创建：优先复用已销毁 id（含仍滞留在销毁缓冲里的）。</summary>
    private void OpCreateWithId()
    {
        long id;
        var reused = false;
        if (_destroyedPool.Count > 0 && _rng.Chance(60))
        {
            id = _destroyedPool[_rng.Range(_destroyedPool.Count)];
            reused = true;
        }
        else
        {
            id = 9_000_000 + _rng.Range(100_000);
        }

        _ledger.Op("create_with_id", $"\"id\":{id},\"reused\":{(reused ? 1 : 0)}");
        var e = new YuoEntity(_world, id);
        _created.Add(id);
        _ledger.Q("registered", ReferenceEquals(_world.GetEntity(id), e) ? "1" : "0");
    }

    private void OpDestroy()
    {
        var id = PickEntityId();
        _ledger.Op("destroy", $"\"e\":{id}");
        _world.GetEntity(id)?.Destroy();
        _destroyedPool.Add(id);
    }

    private void OpDestroyForce()
    {
        var id = PickEntityId();
        _ledger.Op("destroy_force", $"\"e\":{id}");
        var e = _world.GetEntity(id);
        if (e != null) YuoWorld.DestroyEntityForce(e);
        _destroyedPool.Add(id);
    }

    private void OpAdd()
    {
        var id = PickEntityId();
        var type = ComponentPool.Types[_rng.Range(ComponentPool.Types.Length)];
        _ledger.Op("add", $"\"e\":{id},\"c\":\"{ComponentPool.Short(type)}\"");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("add", "\"null_entity\"");
            return;
        }

        var c = e.AddComponent(type);
        StampSerial(c);
        _ledger.Q("add", $"\"{ComponentPool.Snap(c)}\"");
    }

    private void OpAddInit()
    {
        var id = PickEntityId();
        var hp = _rng.Range(500);
        _ledger.Op("add_init", $"\"e\":{id},\"hp\":{hp}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("add_init", "\"null_entity\"");
            return;
        }

        var c = e.AddComponent<HealthComponent, int>(hp);
        StampSerial(c);
        _ledger.Q("add_init", $"\"{ComponentPool.Snap(c)}\"");
    }

    /// <summary>以基类声明类型挂载子类实例；也覆盖直接以子类类型挂载的对照路径。</summary>
    private void OpAddBaseTyped()
    {
        var id = PickEntityId();
        var buff = BuffTypes[_rng.Range(BuffTypes.Length)];
        var useBaseKey = _rng.Chance(70);
        _ledger.Op("add_typed", $"\"e\":{id},\"impl\":\"{ComponentPool.Short(buff)}\",\"key\":\"{(useBaseKey ? "buff" : ComponentPool.Short(buff))}\"");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("add_typed", "\"null_entity\"");
            return;
        }

        var instance = (BuffBaseComponent)Activator.CreateInstance(buff)!;
        instance.Stacks = _rng.Range(5);
        StampSerial(instance);
        var key = useBaseKey ? typeof(BuffBaseComponent) : buff;
        e.SetComponent(key, instance);
        _ledger.Q("get_decl", $"\"{ComponentPool.Snap(e.GetComponent(key))}\"");
        _ledger.Q("get_base", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
    }

    private void OpRemove()
    {
        var id = PickEntityId();
        var type = ComponentPool.Types[_rng.Range(ComponentPool.Types.Length)];
        var byInstance = _rng.Chance(30);
        _ledger.Op("remove", $"\"e\":{id},\"c\":\"{ComponentPool.Short(type)}\",\"inst\":{(byInstance ? 1 : 0)}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("remove", "\"null_entity\"");
            return;
        }

        if (byInstance)
        {
            var c = e.GetComponent(type);
            if (c != null) e.RemoveComponent(c);
            _ledger.Q("remove", c == null ? "\"absent\"" : "\"removed\"");
        }
        else
        {
            var had = e.HasComponent(type);
            e.RemoveComponent(type);
            _ledger.Q("remove", had ? "\"removed\"" : "\"absent\"");
        }
    }

    private void OpDestroyComponent()
    {
        var id = PickEntityId();
        var type = ComponentPool.Types[_rng.Range(ComponentPool.Types.Length)];
        _ledger.Op("dcomp", $"\"e\":{id},\"c\":\"{ComponentPool.Short(type)}\"");
        var e = _world.GetEntity(id);
        var c = e?.GetComponent(type);
        c?.DestroyComponent();
        _ledger.Q("dcomp", c == null ? "\"absent\"" : "\"queued\"");
    }

    private void OpReplace()
    {
        var id = PickEntityId();
        var health = _rng.Chance(50);
        var typed = !health && _rng.Chance(50);
        _ledger.Op("replace", $"\"e\":{id},\"c\":\"{(health ? "hp" : "poison")}\",\"typed\":{(typed ? 1 : 0)}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("replace", "\"null_entity\"");
            return;
        }

        if (health)
        {
            e.ReplaceComponent(new HealthComponent { Hp = _rng.Range(900), Serial = NextSerial() });
        }
        else
        {
            var p = new PoisonBuffComponent { Stacks = _rng.Range(4), Tick = _rng.Range(9), Serial = NextSerial() };
            if (typed) e.ReplaceComponent(p, typeof(BuffBaseComponent));
            else e.ReplaceComponent(p);
        }

        _ledger.Q("get_hp", $"\"{ComponentPool.Snap(e.GetComponent<HealthComponent>())}\"");
        _ledger.Q("get_buff", $"\"{ComponentPool.Snap(e.GetComponent<BuffBaseComponent>())}\"");
        _ledger.Q("base_buff", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
    }

    /// <summary>同操作内 remove→add：覆盖 IStart 待决列表与重复添加路径。</summary>
    private void OpReAdd()
    {
        var id = PickEntityId();
        var type = ComponentPool.Types[_rng.Range(ComponentPool.Types.Length)];
        _ledger.Op("readd", $"\"e\":{id},\"c\":\"{ComponentPool.Short(type)}\"");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("readd", "\"null_entity\"");
            return;
        }

        e.RemoveComponent(type);
        var c = e.AddComponent(type);
        StampSerial(c);
        _ledger.Q("readd", $"\"{ComponentPool.Snap(c)}\"");
    }

    private void OpUpdate()
    {
        _ledger.Op("update", null);
        _world.Update();
    }

    private void OpFixedUpdate()
    {
        _ledger.Op("fixed_update", null);
        _world.FixedUpdate();
    }

    private void OpLateUpdate()
    {
        _ledger.Op("late_update", null);
        _world.LateUpdate();
    }

    private void OpQueryGet()
    {
        var id = PickEntityId();
        var type = ComponentPool.Types[_rng.Range(ComponentPool.Types.Length)];
        _ledger.Op("q_get", $"\"e\":{id},\"c\":\"{ComponentPool.Short(type)}\"");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("get", "\"null_entity\"");
            return;
        }

        _ledger.Q("get", $"\"{ComponentPool.Snap(e.GetComponent(type))}\"");
        _ledger.Q("has", e.HasComponent(type) ? "1" : "0");
        _ledger.Q("try", e.TryGetComponent(type, out var c2) ? $"\"{ComponentPool.Snap(c2)}\"" : "\"no\"");
        _ledger.Q("worldget", $"\"{ComponentPool.Snap(_world.GetComponent<HealthComponent>(id))}\"");
    }

    private void OpQueryBase()
    {
        var id = PickEntityId();
        _ledger.Op("q_base", $"\"e\":{id}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("base", "\"null_entity\"");
            return;
        }

        _ledger.Q("has_base", e.HasBaseComponent<BuffBaseComponent>() ? "1" : "0");
        _ledger.Q("base_first", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
        var serials = new List<string>();
        foreach (var c in e.GetBaseComponentsEnumerator<BuffBaseComponent>())
            serials.Add(ComponentPool.Snap(c));
        // 同一容器实现在两侧一致，保留原始枚举顺序 + 排序结果两份信号
        _ledger.Q("base_enum_raw", "[\"" + string.Join("\",\"", serials) + "\"]");
        serials.Sort(StringComparer.Ordinal);
        _ledger.Q("base_enum_sorted", "[\"" + string.Join("\",\"", serials) + "\"]");
    }

    private void OpQueryAnyType()
    {
        var id = PickEntityId();
        _ledger.Op("q_any", $"\"e\":{id}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("any", "\"null_entity\"");
            return;
        }

        var serials = e.GetComponentsAnyType<BuffBaseComponent>().Select(ComponentPool.Snap).ToList();
        _ledger.Q("any_raw", "[\"" + string.Join("\",\"", serials) + "\"]");
        serials.Sort(StringComparer.Ordinal);
        _ledger.Q("any_sorted", "[\"" + string.Join("\",\"", serials) + "\"]");
    }

    private void OpForEach()
    {
        var pair = _rng.Chance(40);
        _ledger.Op("foreach", $"\"pair\":{(pair ? 1 : 0)}");
        if (pair)
        {
            var set = YuoWorld.ForEach<PositionComponent, VelocityComponent>();
            var rows = set.Select(t => $"{t.Item1.Entity?.ID ?? -1}:{ComponentPool.Snap(t.Item1)}").ToList();
            rows.Sort(StringComparer.Ordinal);
            _ledger.Q("foreach2", "[\"" + string.Join("\",\"", rows) + "\"]");
            YuoWorld.ReturnHashSet(set);
        }
        else
        {
            var set = YuoWorld.ForEach<HealthComponent>();
            var rows = set.Select(c => $"{c.Entity?.ID ?? -1}:{ComponentPool.Snap(c)}").ToList();
            rows.Sort(StringComparer.Ordinal);
            _ledger.Q("foreach1", "[\"" + string.Join("\",\"", rows) + "\"]");
            YuoWorld.ReturnHashSet(set);
        }
    }

    private void OpGetAll()
    {
        // GetAllComponents<T> 以 typeof(T) 为键查 MultiHashSetMap；
        // 轮换 4 个代表性键，覆盖"声明键/运行时键错配"的落点差异。
        var pick = _rng.Range(4);
        _ledger.Op("getall", $"\"c\":{pick}");
        IReadOnlySet<YuoComponent> set = pick switch
        {
            0 => _world.GetAllComponents<HealthComponent>(),
            1 => _world.GetAllComponents<BuffBaseComponent>(),
            2 => _world.GetAllComponents<PoisonBuffComponent>(),
            _ => _world.GetAllComponents<PositionComponent>(),
        };
        var rows = set.Select(c => $"{c.Entity?.ID ?? -1}:{c.Type.Name}:{ComponentPool.Snap(c)}").ToList();
        rows.Sort(StringComparer.Ordinal);
        _ledger.Q("getall", "[\"" + string.Join("\",\"", rows) + "\"]");
    }

    private void OpSysMembers()
    {
        _ledger.Op("sys_members", null);
        var sb = new StringBuilder(512);
        foreach (var s in _world.GetAllSystem)
        {
            var ids = s.Entities.Select(e => e.ID).ToList();
            var sorted = ids.ToList();
            sorted.Sort();
            sb.Append(s.SystemName).Append(s.Enabled ? '+' : '-')
                .Append('[').Append(string.Join(',', sorted)).Append(']')
                .Append('{').Append(Fnv64.Hash(string.Join(',', ids)).ToString("x8")).Append('}')
                .Append('|');
        }

        _ledger.Q("members", $"\"{sb}\"");
    }

    private void OpBuildSystem()
    {
        var id = PickEntityId();
        _ledger.Op("build_sys", $"\"e\":{id}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("build", "\"null_entity\"");
            return;
        }

        var a = YuoWorld.BuildSystem<IUpdate>(e).Select(s => s.SystemName).ToList();
        a.Sort(StringComparer.Ordinal);
        var b = YuoWorld.BuildSystem<IAwake>(e).Select(s => s.SystemName).ToList();
        b.Sort(StringComparer.Ordinal);
        _ledger.Q("build_update", "[\"" + string.Join("\",\"", a) + "\"]");
        _ledger.Q("build_awake", "[\"" + string.Join("\",\"", b) + "\"]");
    }

    private void OpToggleSystem()
    {
        var disable = _rng.Chance(50);
        var target = _rng.Chance(50) ? "MoveSystem" : "PairSystem";
        _ledger.Op("toggle", $"\"sys\":\"{target}\",\"op\":\"{(disable ? "off" : "on")}\"");
        if (target == "MoveSystem")
        {
            if (disable) YuoWorld.DisableSystem<MoveSystem>();
            else YuoWorld.EnableSystem<MoveSystem>();
        }
        else
        {
            if (disable) YuoWorld.DisableSystem<PairSystem>();
            else YuoWorld.EnableSystem<PairSystem>();
        }
    }

    private void OpRunGroup()
    {
        var id = PickEntityId();
        var only = _rng.Chance(50);
        _ledger.Op("run_group", $"\"e\":{id},\"mode\":\"{(only ? "only" : "exclude")}\"");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("run_group", "\"null_entity\"");
            return;
        }

        if (only) e.RunSystemOnlyGroup<IUpdate>(typeof(DiffGroupA));
        else e.RunSystemExcludeGroup<IUpdate>(typeof(DiffGroupA));
    }

    private void OpRunPulseAll()
    {
        _ledger.Op("pulse_all", null);
        YuoWorld.RunSystemForAll<IPulse>();
    }

    private void OpRunTagOnEntity()
    {
        var id = PickEntityId();
        _ledger.Op("run_tag", $"\"e\":{id}");
        var e = _world.GetEntity(id);
        if (e == null)
        {
            _ledger.Q("run_tag", "\"null_entity\"");
            return;
        }

        YuoWorld.RunSystem<IUpdate>(e);
    }

    private void OpAddChild()
    {
        var id = PickEntityId();
        var withComp = _rng.Chance(60);
        _ledger.Op("add_child", $"\"p\":{id},\"comp\":{(withComp ? 1 : 0)}");
        var e = _world.GetEntity(id);
        if (e == null || e.IsUnusable())
        {
            _ledger.Q("child", "\"null_or_dead_parent\"");
            return;
        }

        if (withComp)
        {
            var c = e.AddChild<PositionComponent>();
            c.Serial = NextSerial();
            c.X = _rng.Range(10);
            _ledger.Q("child", c.Entity.ID.ToString());
            _created.Add(c.Entity.ID);
        }
        else
        {
            var child = e.AddChild();
            _created.Add(child.ID);
            _ledger.Q("child", child.ID.ToString());
        }
    }

    private void OpRemoveChild()
    {
        var id = PickEntityId();
        _ledger.Op("rm_child", $"\"p\":{id}");
        var e = _world.GetEntity(id);
        if (e == null || e.IsUnusable())
        {
            _ledger.Q("rm_child", "\"null_or_dead_parent\"");
            return;
        }

        var child = e.Children.FirstOrDefault();
        if (child == null)
        {
            _ledger.Q("rm_child", "\"none\"");
            return;
        }

        e.RemoveChild(child);
        _ledger.Q("rm_child", child.ID.ToString());
        _ledger.Q("children_left", e.Children.Count.ToString());
    }

    private void OpChaos()
    {
        var id = PickEntityId();
        var target = PickEntityId();
        var instr = 1 + _rng.Range(10);
        _ledger.Op("chaos", $"\"e\":{id},\"i\":{instr},\"t\":{target}");
        var e = _world.GetEntity(id);
        if (e == null || e.IsUnusable())
        {
            _ledger.Q("chaos", "\"null_or_dead\"");
            return;
        }

        var c = e.GetOrAddComponent<ChaosComponent>();
        if (c.Serial == 0) c.Serial = NextSerial();
        c.Op = instr;
        c.Arg = target;
    }

    private void OpWorldEntities()
    {
        _ledger.Op("entities", null);
        var ids = _world.Entities.Keys.ToList();
        ids.Sort();
        _ledger.Q("entities", "[\"" + string.Join(",", ids) + "\"]");
    }

    /// <summary>定向用例入口：登记/复用实体 id 台账，保持与随机路径同一簿记。</summary>
    public void TrackCreated(long id) => _created.Add(id);

    public void TrackDestroyed(long id) => _destroyedPool.Add(id);

    public int NextInt(int maxExclusive) => _rng.Range(maxExclusive);

    public bool Chance(int percent) => _rng.Chance(percent);

    public void StampSerial(YuoComponent c)
    {
        switch (c)
        {
            case null: return;
            case PositionComponent p: if (p.Serial == 0) p.Serial = NextSerial(); break;
            case VelocityComponent v: if (v.Serial == 0) v.Serial = NextSerial(); break;
            case HealthComponent h: if (h.Serial == 0) h.Serial = NextSerial(); break;
            case ManaComponent m: if (m.Serial == 0) m.Serial = NextSerial(); break;
            case MarkComponent m: if (m.Serial == 0) m.Serial = NextSerial(); break;
            case ShieldComponent s: if (s.Serial == 0) s.Serial = NextSerial(); break;
            case CharmComponent ch: if (ch.Serial == 0) ch.Serial = NextSerial(); break;
            case ChaosComponent c2: if (c2.Serial == 0) c2.Serial = NextSerial(); break;
            case BuffBaseComponent b: if (b.Serial == 0) b.Serial = NextSerial(); break;
            case AnchorComponent a: if (a.Serial == 0) a.Serial = NextSerial(); break;
            case AutoLinkedComponent al: if (al.Serial == 0) al.Serial = NextSerial(); break;
        }
    }
}
