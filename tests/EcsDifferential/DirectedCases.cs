using YuoCore.Ecs;

namespace DaoHua.EcsDifferential;

/// <summary>
/// 定向边界用例：每个用例独立世界、固定操作序列，两侧逐字节对齐。
/// set_active_subset 是已知的"优化版独占 API"差异区，差异内容本身即证据。
/// </summary>
public static class DirectedCases
{
    public static void RunAll(Ledger ledger)
    {
        var r = new Runner(seed: 999_91, ops: 0, ledger);
        DiffProbe.L = ledger;
        DiffProbe.CallbackCounts.Clear();
        try
        {
            CaseChurnReuse(r);
            CaseDestroyMultiComponent(r);
            CaseBaseDeclared(r);
            CaseReentry(r);
            CaseSetActiveAll(r);
            CaseSetActiveSubset(r);
            CaseQuitThenRemove(r);
            CasePendingStartSkip(r);
            CaseForceInsidePending(r);
            CaseSwitchComponent(r);
            CaseChildrenCascade(r);
            CaseManualDispatch(r);
            CaseEmptyWorld(r);
        }
        finally
        {
            DiffProbe.L = null;
        }
    }

    /// <summary>连续 destroy→create：覆盖延迟销毁缓冲内的 id 复用与 EntityIndex 回收。</summary>
    private static void CaseChurnReuse(Runner r)
    {
        var w = r.BeginCase("churn_reuse");
        var seq = 0;
        // 未冲刷缓冲就复用同一 id：RegisterEntity 应撞车，实体成为孤儿。
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            r.TrackCreated(e.ID);
            e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
        });
        long reuseId = -1;
        r.Do(seq++, "destroy_first", null, () =>
        {
            reuseId = r.PickEntityId();
            w.GetEntity(reuseId)?.Destroy();
            r.TrackDestroyed(reuseId);
        });
        r.Do(seq++, "recreate_same_id_pending", $"\"id\":{reuseId}", () =>
        {
            var e = new YuoEntity(w, reuseId);
            r.TrackCreated(e.ID);
            DiffProbe.L?.Q("registered", ReferenceEquals(w.GetEntity(reuseId), e) ? "1" : "0");
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.Do(seq++, "recreate_same_id_after_flush", $"\"id\":{reuseId}", () =>
        {
            var e = new YuoEntity(w, reuseId);
            r.TrackCreated(e.ID);
            DiffProbe.L?.Q("registered", ReferenceEquals(w.GetEntity(reuseId), e) ? "1" : "0");
        });
        r.Do(seq++, "update", null, () => w.Update());
        // 快速交替销毁创建，逼出索引复用路径。
        for (var i = 0; i < 8; i++)
        {
            var iter = i;
            r.Do(seq++, "churn", $"\"i\":{iter}", () =>
            {
                var e = new YuoEntity(w);
                r.TrackCreated(e.ID);
                e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
                if (iter % 2 == 0)
                {
                    e.Destroy();
                    r.TrackDestroyed(e.ID);
                }

                w.Update();
            });
        }

        r.EndCase(seq);
    }

    /// <summary>携带 6 个组件的实体销毁：IDestroy 逐组件派发顺序。</summary>
    private static void CaseDestroyMultiComponent(Runner r)
    {
        var w = r.BeginCase("destroy_multi_comp");
        var seq = 0;
        r.Do(seq++, "mk_multi", null, () =>
        {
            var e = new YuoEntity(w);
            r.TrackCreated(e.ID);
            e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
            e.AddComponent<VelocityComponent>().Serial = r.AllocSerial();
            e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
            e.AddComponent<ManaComponent>().Serial = r.AllocSerial();
            e.AddComponent<MarkComponent>().Serial = r.AllocSerial();
            e.AddComponent<ShieldComponent>().Serial = r.AllocSerial();
        });
        r.Do(seq++, "destroy_force", null, () =>
        {
            var id = r.PickEntityId();
            var e = w.GetEntity(id);
            if (e != null) YuoWorld.DestroyEntityForce(e);
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>基类声明类型挂多子类实例 + 首元素缓存：增删顺序敏感期。</summary>
    private static void CaseBaseDeclared(Runner r)
    {
        var w = r.BeginCase("base_declared");
        var seq = 0;
        long eid = 0;
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            eid = e.ID;
            r.TrackCreated(e.ID);
        });
        r.Do(seq++, "set_base_poison", null, () =>
        {
            var e = w.GetEntity(eid);
            var p = new PoisonBuffComponent { Stacks = 1, Tick = 7, Serial = r.AllocSerial() };
            e.SetComponent(typeof(BuffBaseComponent), p);
            DiffProbe.L?.Q("decl", $"\"{ComponentPool.Snap(e.GetComponent<BuffBaseComponent>())}\"");
            DiffProbe.L?.Q("base", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
        });
        r.Do(seq++, "set_base_regen_same_key", null, () =>
        {
            var e = w.GetEntity(eid);
            var g = new RegenBuffComponent { Stacks = 2, Rate = 3, Serial = r.AllocSerial() };
            // 声明键重复：Components.TryAdd 失败但仍走 World.AddComponent 注册
            e.SetComponent(typeof(BuffBaseComponent), g);
            DiffProbe.L?.Q("decl", $"\"{ComponentPool.Snap(e.GetComponent<BuffBaseComponent>())}\"");
            DiffProbe.L?.Q("base", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
            DiffProbe.L?.Q("has_base", e.HasBaseComponent<BuffBaseComponent>() ? "1" : "0");
        });
        r.Do(seq++, "remove_base_key", null, () =>
        {
            var e = w.GetEntity(eid);
            e.RemoveComponent(typeof(BuffBaseComponent));
            DiffProbe.L?.Q("base_after_rm", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
            DiffProbe.L?.Q("has_base", e.HasBaseComponent<BuffBaseComponent>() ? "1" : "0");
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.Do(seq++, "remove_first_recheck", null, () =>
        {
            // 移除第二个实例：检验"首元素缓存"是否跟随集合当前首项
            var e = w.GetEntity(eid);
            var remaining = new List<string>();
            foreach (var c in e.GetBaseComponentsEnumerator<BuffBaseComponent>())
                remaining.Add(ComponentPool.Snap(c));
            DiffProbe.L?.Q("base_enum", "[\"" + string.Join("\",\"", remaining) + "\"]");
            var first = e.GetBaseComponent<BuffBaseComponent>();
            if (first != null) e.RemoveComponent(first);
            DiffProbe.L?.Q("base_after_rm2", $"\"{ComponentPool.Snap(e.GetBaseComponent<BuffBaseComponent>())}\"");
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>系统回调重入：Update 迭代内加组件/移除/销毁自身与目标。</summary>
    private static void CaseReentry(Runner r)
    {
        var w = r.BeginCase("reentry");
        var seq = 0;
        var ids = new List<long>();
        r.Do(seq++, "mk3", null, () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var e = new YuoEntity(w);
                ids.Add(e.ID);
                r.TrackCreated(e.ID);
                e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
                e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
            }
        });
        // 回调内：给目标加组件、销毁目标、创建新实体、移除自身组件
        for (var i = 0; i < ids.Count; i++)
        {
            var idx = i;
            r.Do(seq++, "arm_chaos", $"\"e\":{ids[idx]}", () =>
            {
                var c = w.GetEntity(ids[idx]).GetOrAddComponent<ChaosComponent>();
                c.Serial = r.AllocSerial();
                c.Op = idx switch
                {
                    0 => 2,
                    1 => 5,
                    _ => 1
                };
                c.Arg = ids[(idx + 1) % ids.Count];
            });
        }

        r.Do(seq++, "update_reentry", null, () => w.Update());
        r.Do(seq++, "update_after", null, () => w.Update());
        r.Do(seq++, "update_after2", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>SetActiveEntities(全部存活)：优化版应与不设集合的基线行为完全一致。</summary>
    private static void CaseSetActiveAll(Runner r)
    {
        var w = r.BeginCase("set_active_all");
        var seq = 0;
        r.Do(seq++, "mk", null, () =>
        {
            for (var i = 0; i < 5; i++)
            {
                var e = new YuoEntity(w);
                r.TrackCreated(e.ID);
                e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
                e.AddComponent<VelocityComponent>().Serial = r.AllocSerial();
                e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
            }
        });
        r.Do(seq++, "update_set_active_all", null, () =>
        {
#if YUOCORE_OPTIMIZED
            var alive = new List<YuoEntity>();
            foreach (var kv in w.Entities) alive.Add(kv.Value);
            w.SetActiveEntities(alive);
            w.Update();
            w.ClearActiveEntities();
#else
            w.Update();
#endif
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>
    /// SetActiveEntities(子集)：本用例预期产生差异——基线 Update 全量实体，
    /// 优化版只跑子集。差异区块用于核对过滤是否精确落在指定子集上。
    /// </summary>
    private static void CaseSetActiveSubset(Runner r)
    {
        var w = r.BeginCase("set_active_subset");
        var seq = 0;
        var ids = new List<long>();
        r.Do(seq++, "mk", null, () =>
        {
            for (var i = 0; i < 6; i++)
            {
                var e = new YuoEntity(w);
                ids.Add(e.ID);
                r.TrackCreated(e.ID);
                e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
                e.AddComponent<VelocityComponent>().Serial = r.AllocSerial();
            }
        });
        // 取排序后的偶数槽位作为活动子集；同一表达式两侧共享，差异仅由 API 语义产生
        var subset = ids.OrderBy(x => x).Where((_, i) => i % 2 == 0).ToList();
        r.Do(seq++, "update_set_active_subset", $"\"ids\":[{string.Join(',', subset)}]", () =>
        {
#if YUOCORE_OPTIMIZED
            var alive = subset.Select(w.GetEntity).Where(e => e != null).ToList();
            w.SetActiveEntities(alive);
            w.Update();
            w.ClearActiveEntities();
            DiffProbe.L?.Q("active_applied", alive.Count.ToString());
#else
            w.Update();
            DiffProbe.L?.Q("active_applied", "\"unsupported\"");
#endif
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>OnQuit 后移除组件：IsPlaying=false 时 IDestroy 不应派发。</summary>
    private static void CaseQuitThenRemove(Runner r)
    {
        var w = r.BeginCase("quit_then_remove");
        var seq = 0;
        long eid = 0;
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            eid = e.ID;
            r.TrackCreated(e.ID);
            e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
            e.AddComponent<MarkComponent>().Serial = r.AllocSerial();
        });
        r.Do(seq++, "quit", null, () => w.OnQuit());
        r.Do(seq++, "remove_after_quit", null, () =>
        {
            w.GetEntity(eid)?.RemoveComponent<HealthComponent>();
        });
        r.Do(seq++, "update_after_quit", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>add→remove 同 tick：IStart 待决列表对已失效组件的跳过路径。</summary>
    private static void CasePendingStartSkip(Runner r)
    {
        var w = r.BeginCase("pending_start_skip");
        var seq = 0;
        long eid = 0;
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            eid = e.ID;
            r.TrackCreated(e.ID);
        });
        r.Do(seq++, "add_rm_same_tick", null, () =>
        {
            var e = w.GetEntity(eid);
            var h = e.AddComponent<HealthComponent>();
            h.Serial = r.AllocSerial();
            h.Hp = 33;
            e.RemoveComponent<HealthComponent>();
            var m = e.AddComponent<ManaComponent>();
            m.Serial = r.AllocSerial();
            e.AddComponent<AnchorComponent>().Serial = r.AllocSerial(); // 下一帧触发 AutoLinked
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>Destroy() 挂缓冲后再 DestroyEntityForce()：缓冲内已释放实体的二次 Dispose。</summary>
    private static void CaseForceInsidePending(Runner r)
    {
        var w = r.BeginCase("force_inside_pending");
        var seq = 0;
        r.Do(seq++, "mk", null, () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var e = new YuoEntity(w);
                r.TrackCreated(e.ID);
                e.AddComponent<MarkComponent>().Serial = r.AllocSerial();
            }
        });
        r.Do(seq++, "destroy_then_force", null, () =>
        {
            foreach (var e in w.Entities.Values.Where(e => e.ID >= 2_000_000).ToList())
            {
                e.Destroy();
                YuoWorld.DestroyEntityForce(e);
            }
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>ReplaceComponent 触发 ISwitchComponent + 组件替换语义。</summary>
    private static void CaseSwitchComponent(Runner r)
    {
        var w = r.BeginCase("switch_component");
        var seq = 0;
        long eid = 0;
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            eid = e.ID;
            r.TrackCreated(e.ID);
            e.AddComponent<HealthComponent>().Serial = r.AllocSerial();
        });
        r.Do(seq++, "replace", null, () =>
        {
            w.GetEntity(eid)?.ReplaceComponent(new HealthComponent { Hp = 777, Serial = r.AllocSerial() });
        });
        r.Do(seq++, "replace_absent", null, () =>
        {
            // 对不存在的组件执行替换：走 Components.Add 新挂路径
            w.GetEntity(eid)?.ReplaceComponent(new ManaComponent { Mp = 5, Serial = r.AllocSerial() });
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>父子实体：父销毁级联子销毁，Children/ChildComponents 簿记。</summary>
    private static void CaseChildrenCascade(Runner r)
    {
        var w = r.BeginCase("children_cascade");
        var seq = 0;
        long pid = 0;
        r.Do(seq++, "mk_tree", null, () =>
        {
            var parent = new YuoEntity(w);
            pid = parent.ID;
            r.TrackCreated(pid);
            var c1 = parent.AddChild<PositionComponent>();
            c1.Serial = r.AllocSerial();
            r.TrackCreated(c1.Entity.ID);
            var c2 = parent.AddChild<HealthComponent>();
            c2.Serial = r.AllocSerial();
            r.TrackCreated(c2.Entity.ID);
            var grand = c1.Entity.AddChild<ManaComponent>();
            grand.Serial = r.AllocSerial();
            r.TrackCreated(grand.Entity.ID);
            DiffProbe.L?.Q("children", parent.Children.Count.ToString());
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.Do(seq++, "destroy_parent", null, () =>
        {
            var p = w.GetEntity(pid);
            if (p != null) YuoWorld.DestroyEntityForce(p);
        });
        r.Do(seq++, "update", null, () => w.Update());
        r.EndCase(seq);
    }

    /// <summary>手动派发：RunSystemForAll / 单实体 RunSystem / 组过滤。</summary>
    private static void CaseManualDispatch(Runner r)
    {
        var w = r.BeginCase("manual_dispatch");
        var seq = 0;
        long eid = 0;
        r.Do(seq++, "mk", null, () =>
        {
            var e = new YuoEntity(w);
            eid = e.ID;
            r.TrackCreated(e.ID);
            e.AddComponent<PositionComponent>().Serial = r.AllocSerial();
            e.AddComponent<VelocityComponent>().Serial = r.AllocSerial();
            e.AddComponent<MarkComponent>().Serial = r.AllocSerial();
            e.AddComponent<ShieldComponent>().Serial = r.AllocSerial();
        });
        r.Do(seq++, "pulse_all", null, () => YuoWorld.RunSystemForAll<IPulse>());
        r.Do(seq++, "run_update_entity", null, () => YuoWorld.RunSystem<IUpdate>(w.GetEntity(eid)));
        r.Do(seq++, "run_group_only", null, () => w.GetEntity(eid).RunSystemOnlyGroup<IUpdate>(typeof(DiffGroupA)));
        r.Do(seq++, "run_group_exclude", null, () => w.GetEntity(eid).RunSystemExcludeGroup<IUpdate>(typeof(DiffGroupA)));
        r.Do(seq++, "disable_then_update", null, () =>
        {
            YuoWorld.DisableSystem<MoveSystem>();
            w.Update();
            YuoWorld.EnableSystem<MoveSystem>();
            w.Update();
        });
        r.EndCase(seq);
    }

    /// <summary>空世界连刷：只有内建实体时的 Update 语义。</summary>
    private static void CaseEmptyWorld(Runner r)
    {
        var w = r.BeginCase("empty_world");
        var seq = 0;
        for (var i = 0; i < 3; i++) r.Do(seq++, "update", null, () => w.Update());
        r.Do(seq++, "fixed_late", null, () =>
        {
            w.FixedUpdate();
            w.LateUpdate();
        });
        r.EndCase(seq);
    }
}
