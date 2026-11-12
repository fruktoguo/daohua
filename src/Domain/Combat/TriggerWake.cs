using System.Text.Json;
using Domain.Common;

namespace Domain.Combat;

/// <summary>
/// 单位事实通道：单个战斗单位在一个边界内可能被翻转的事实类别。
/// 触发条件经「对象 × 条件」映射为这些通道的订阅位，边界结算后只唤醒
/// 订阅了已翻转通道的槽位/技能，替代每边界全槽扫描（TASK-20261112-15）。
/// </summary>
[Flags]
public enum UnitChannel : byte
{
    None = 0,
    /// <summary>气血值变化（含受击、治疗、回复）。</summary>
    Hp = 1,
    /// <summary>灵力值变化（含消耗、回复、燃烧反应）。</summary>
    Mana = 2,
    /// <summary>存活状态翻转（阵亡/复活）。</summary>
    Alive = 4,
    /// <summary>Buff 列表/层数/到期签名变化（含外部视图注入）。</summary>
    Buff = 8,
    /// <summary>有效威胁值变化（伤害、嘲讽、威胁 Buff 间接落账）。</summary>
    Threat = 16,
    /// <summary>静态面板引用更替（RefreshStats 等换入新 CombatantStats）。</summary>
    Stats = 32,
}

/// <summary>
/// 战场级事实：不属于单个单位通道的集合/全局事实（按遭遇视角绝对命名——
/// 「友方列表」恒指 PlayerAllies、「敌方列表」恒指 Enemies，与施放方阵营无关；
/// 施放方视角的对象语义在 <see cref="TriggerWakePlanner"/> 编译期折算成绝对侧）。
/// </summary>
[Flags]
public enum BattleFact : byte
{
    None = 0,
    /// <summary>PlayerAllies 列表成员变化（波次注入/名单变更）。</summary>
    AllyList = 1,
    /// <summary>Enemies 列表成员变化（波次注入新敌）。</summary>
    EnemyList = 2,
    /// <summary>外部视图事实：窗口边界面板刷新、共享钱包脏标记等追踪器不可 diff 的输入。</summary>
    External = 4,
    /// <summary>战斗时钟推进：battle/elapsed-seconds 条件的订阅位，每边界恒置位。</summary>
    Elapsed = 8,
}

/// <summary>
/// 槽位唤醒掩码：一个技能槽/消耗品槽全部触发条件折算出的订阅位。
/// 任一条件命中已翻转事实即唤醒重评；掩码语义为「遭遇绝对侧」——
/// AllySide 对应 PlayerAllies 数组、EnemySide 对应 Enemies 数组。
/// </summary>
public readonly struct SlotWakeMask
{
    /// <summary>施放方自身单位通道；按 <see cref="SelfInEnemies"/> 定位到 delta 的数组。</summary>
    public UnitChannel Self { get; init; }
    /// <summary>遭遇友方（PlayerAllies）任一单位的通道。</summary>
    public UnitChannel AllySide { get; init; }
    /// <summary>遭遇敌方（Enemies）任一单位的通道。</summary>
    public UnitChannel EnemySide { get; init; }
    /// <summary>战场级事实位。</summary>
    public BattleFact Facts { get; init; }
    /// <summary>施放方单位位于 Enemies 数组（敌方单位施放技能时为 true）。</summary>
    public bool SelfInEnemies { get; init; }

    /// <summary>全量订阅：永不跳过判定（未知条件/未接入追踪路径的兜底）。</summary>
    public static SlotWakeMask Full { get; } = new()
    {
        Self = (UnitChannel)0xFF, AllySide = (UnitChannel)0xFF, EnemySide = (UnitChannel)0xFF,
        Facts = (BattleFact)0xFF,
    };

    /// <summary>空掩码：任何事实增量都不唤醒（恒真条件/静态条件）。</summary>
    public static SlotWakeMask None => default;

    /// <summary>掩码是否为空（无订阅位，待判定缓存可直接复用）。</summary>
    public bool IsEmpty => Self == 0 && AllySide == 0 && EnemySide == 0 && Facts == 0;

    /// <summary>订阅位交集测试：两掩码按位对齐（同为遭遇绝对侧语义），任一共同位非空即命中。</summary>
    public bool Intersects(SlotWakeMask other)
        => (Self & other.Self) != 0 || (AllySide & other.AllySide) != 0
            || (EnemySide & other.EnemySide) != 0 || (Facts & other.Facts) != 0;

    /// <summary>合并另一掩码的订阅位（OR 折叠；SelfInEnemies 必须一致，由调用方保证）。</summary>
    public SlotWakeMask Union(SlotWakeMask other) => new()
    {
        Self = Self | other.Self,
        AllySide = AllySide | other.AllySide,
        EnemySide = EnemySide | other.EnemySide,
        Facts = Facts | other.Facts,
        SelfInEnemies = SelfInEnemies,
    };

    /// <summary>
    /// 判定本掩码是否被边界事实增量唤醒：自身通道按 selfIndex 精确定位，
    /// 侧级通道按并集粗粒度匹配；delta 无明细数组时视为全量（兜底唤醒）。
    /// </summary>
    public bool WakesOn(in CombatFactDelta delta, int selfIndex)
    {
        if (IsEmpty) return false;
        if (delta.IsFull) return true;
        var selfArr = SelfInEnemies ? delta.Enemies : delta.Allies;
        if (selfArr is not null && selfIndex >= 0 && selfIndex < selfArr.Length
            && (Self & selfArr[selfIndex]) != 0) return true;
        if ((AllySide & delta.AllyUnion) != 0) return true;
        if ((EnemySide & delta.EnemyUnion) != 0) return true;
        return (Facts & delta.Facts) != 0;
    }
}

/// <summary>
/// 边界事实增量：一个自然边界结算完成后翻转的事实集合。
/// Allies/Enemies 为追踪器内部缓冲的只读借用（调用方不得保留或修改），
/// 两侧并集为快捷粗粒度位；<see cref="IsFull"/> 表示「未追踪全量」兜底语义。
/// </summary>
public readonly struct CombatFactDelta
{
    /// <summary>PlayerAllies 各单位翻转通道（下标即成员下标）；null 表示无明细（全量兜底）。</summary>
    public UnitChannel[]? Allies { get; init; }
    /// <summary>Enemies 各单位翻转通道；null 同上。</summary>
    public UnitChannel[]? Enemies { get; init; }
    /// <summary>PlayerAllies 通道并集。</summary>
    public UnitChannel AllyUnion { get; init; }
    /// <summary>Enemies 通道并集。</summary>
    public UnitChannel EnemyUnion { get; init; }
    /// <summary>战场级事实位。</summary>
    public BattleFact Facts { get; init; }
    /// <summary>全量兜底：无追踪明细时所有槽位一律唤醒（等价旧全扫语义）。</summary>
    public bool IsFull { get; init; }

    /// <summary>全量兜底实例：所有订阅位均视为已翻转。</summary>
    public static CombatFactDelta Full { get; } = new()
    {
        AllyUnion = (UnitChannel)0xFF, EnemyUnion = (UnitChannel)0xFF,
        Facts = (BattleFact)0xFF, IsFull = true,
    };
}

/// <summary>
/// 触发条件→依赖事实的编译注册表：词表内每条（对象, 条件）对在 <see cref="ResolveMask"/>
/// 有确定映射；词表外输入一律兜底为全量订阅，杜绝「漏注册即永不唤醒」。
/// 映射权威性与 <see cref="SkillTriggerEvaluator"/> 的求值语义一一对应：
/// 求值读到的每一个可变输入都必须有订阅位覆盖（覆盖由词表全量测试钉住）。
/// </summary>
public static class TriggerWakePlanner
{
    /// <summary>
    /// 把一组触发条件折算为槽位唤醒掩码；列表为空时返回空掩码（无条件槽只靠门槛唤醒）。
    /// </summary>
    /// <param name="triggers">触发条件 JSON 列表（已校验词表）。</param>
    /// <param name="selfInEnemies">施放方单位位于遭遇 Enemies 数组（敌方技能）。</param>
    public static SlotWakeMask ResolveMask(IReadOnlyList<JsonElement> triggers, bool selfInEnemies)
    {
        if (triggers.Count == 0) return SlotWakeMask.None;
        var mask = SlotWakeMask.None with { SelfInEnemies = selfInEnemies };
        for (var i = 0; i < triggers.Count; i++)
            mask = mask.Union(ResolveSingle(triggers[i], selfInEnemies));
        return mask;
    }

    /// <summary>单条条件的订阅位：对象域决定订阅侧，条件类型决定通道，未知输入兜底全量。</summary>
    private static SlotWakeMask ResolveSingle(JsonElement trigger, bool selfInEnemies)
    {
        if (trigger.ValueKind != JsonValueKind.Object) return SlotWakeMask.Full;
        var dependency = JsonRead.Text(trigger, "dependency") ?? "self";
        var condition = JsonRead.Text(trigger, "condition");
        var selfSide = selfInEnemies ? SlotSide.Enemies : SlotSide.Allies;
        var opponentSide = selfInEnemies ? SlotSide.Allies : SlotSide.Enemies;

        // 战场时钟条件：每边界恒置位（等价旧语义，不做时间切片优化）。
        if (dependency == "battle")
            return condition == "elapsed-seconds" ? FactOnly(BattleFact.Elapsed) : SlotWakeMask.Full;

        // 集合对象：集合事实 + 该侧全体单位的条件通道。
        if (dependency is "ally" or "allies" or "enemy" or "enemies")
        {
            var side = dependency is "ally" or "allies" ? selfSide : opponentSide;
            var setFact = side == SlotSide.Allies ? BattleFact.AllyList : BattleFact.EnemyList;
            return condition switch
            {
                "alive-count" or "dead-count" or "total-count"
                    or "has-player" or "has-elite" or "has-boss" or "has-summon"
                    => ForSide(side, UnitChannel.Alive) with { Facts = setFact },
                "total-hp-percentage" or "lowest-hp-percentage" or "highest-hp-percentage"
                    => ForSide(side, UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats) with { Facts = setFact },
                "total-mana-percentage"
                    => ForSide(side, UnitChannel.Alive | UnitChannel.Mana | UnitChannel.Stats) with { Facts = setFact },
                _ => SlotWakeMask.Full,
            };
        }

        // 单体条件通道：数值条件读可变输入；身份/控制类为静态/恒零，无订阅位。
        var condCh = condition switch
        {
            "current-hp" or "missing-hp" => UnitChannel.Hp | UnitChannel.Stats,
            "current-hp-percentage" or "missing-hp-percentage" => UnitChannel.Hp | UnitChannel.Stats,
            "current-mana" or "missing-mana" => UnitChannel.Mana | UnitChannel.Stats,
            "current-mana-percentage" or "missing-mana-percentage" => UnitChannel.Mana | UnitChannel.Stats,
            "buff-stacks" => UnitChannel.Buff,
            "is-player" or "is-elite" or "is-boss" or "is-summon"
                or "stun-status" or "blind-status" or "silence-status" => UnitChannel.None,
            _ => (UnitChannel)0xFF,
        };
        if (condCh == (UnitChannel)0xFF) return SlotWakeMask.Full;

        var mask = dependency switch
        {
            "self" => new SlotWakeMask { Self = condCh, SelfInEnemies = selfInEnemies },
            // 集火目标 = 威胁最高存活敌：身份随敌方存活/威胁/名单变化，条件值读该单位自身通道。
            "target" or "target-enemy" => ForSide(opponentSide,
                    UnitChannel.Alive | UnitChannel.Threat | condCh)
                with { Facts = opponentSide == SlotSide.Allies ? BattleFact.AllyList : BattleFact.EnemyList },
            // 血线极值单位：身份随本侧存活/血量/面板变化，条件值读被解析单位的自身通道。
            "ally-lowest-hp" or "ally-highest-hp" => ForSide(selfSide,
                    UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats | condCh)
                with { Facts = selfSide == SlotSide.Allies ? BattleFact.AllyList : BattleFact.EnemyList },
            "enemy-lowest-hp" or "enemy-highest-hp" => ForSide(opponentSide,
                    UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats | condCh)
                with { Facts = opponentSide == SlotSide.Allies ? BattleFact.AllyList : BattleFact.EnemyList },
            _ => SlotWakeMask.Full,
        };
        // buff-stacks 的外部层数视图（宿主注入委托）不可 diff：以外部事实位兜底唤醒。
        if (condition == "buff-stacks")
            mask = mask.Union(new SlotWakeMask { Facts = BattleFact.External });
        return mask;
    }

    private enum SlotSide : byte { Allies, Enemies }

    private static SlotWakeMask FactOnly(BattleFact fact) => new() { Facts = fact };

    private static SlotWakeMask ForSide(SlotSide side, UnitChannel channels) => side == SlotSide.Allies
        ? new SlotWakeMask { AllySide = channels }
        : new SlotWakeMask { EnemySide = channels };
}

/// <summary>
/// 边界事实追踪器：对遭遇战中全部单位做逐边界快照 diff，输出 <see cref="CombatFactDelta"/>。
/// 由边界钩子按调用持有（一次 AdvanceTo 一份）；首次 Advance 以构造时的快照为基线，
/// 之后逐边界滚动。差量数组为内部缓冲的只读借用，调用方不得保留引用。
/// </summary>
public sealed class CombatFactTracker
{
    private struct UnitSnapshot
    {
        public double Hp;
        public double Mana;
        public bool Alive;
        public int BuffCount;
        public double BuffStackSum;
        public double BuffExpirySum;
        public double Threat;
        public CombatantStats? Stats;
    }

    private UnitSnapshot[] _allies;
    private UnitSnapshot[] _enemies;
    private UnitChannel[] _allyDelta;
    private UnitChannel[] _enemyDelta;
    private BattleFact _pendingExternal;

    /// <summary>以遭遇当前状态为基线创建追踪器；首个边界产出的增量相对此基线。
    /// 追踪启动即作废既有逐槽判定缓存——缓存只覆盖「已折叠增量」区间的有效性，
    /// 追踪前发生的未折叠变化必须强制重评。</summary>
    public CombatFactTracker(CombatEncounterState state)
    {
        state.WakeTracking = true;
        InvalidateVerdicts(state.PlayerAllies);
        InvalidateVerdicts(state.Enemies);
        _allies = CaptureAll(state.PlayerAllies);
        _enemies = CaptureAll(state.Enemies);
        _allyDelta = new UnitChannel[_allies.Length];
        _enemyDelta = new UnitChannel[_enemies.Length];
    }

    private static void InvalidateVerdicts(IReadOnlyList<CombatantState> units)
    {
        for (var i = 0; i < units.Count; i++)
        {
            units[i].SkillWakePending = null;
            units[i].SkillTriggerVerdicts = null;
            units[i].SkillVerdictTargets = null;
        }
    }

    /// <summary>宿主注入外部事实（钱包脏/面板刷新）：并入下一次产出的增量。</summary>
    public void MarkExternal() => _pendingExternal |= BattleFact.External;

    /// <summary>对遭遇当前状态做差分并滚动基线；数组长度变化同时置集合事实位。</summary>
    public CombatFactDelta Advance(CombatEncounterState state)
    {
        var facts = _pendingExternal | BattleFact.Elapsed;
        _pendingExternal = BattleFact.None;
        if (state.PlayerAllies.Count != _allies.Length)
        {
            facts |= BattleFact.AllyList;
            Array.Resize(ref _allies, state.PlayerAllies.Count);
            Array.Resize(ref _allyDelta, state.PlayerAllies.Count);
        }
        if (state.Enemies.Count != _enemies.Length)
        {
            facts |= BattleFact.EnemyList;
            Array.Resize(ref _enemies, state.Enemies.Count);
            Array.Resize(ref _enemyDelta, state.Enemies.Count);
        }
        var allyUnion = DiffList(state.PlayerAllies, _allies, _allyDelta);
        var enemyUnion = DiffList(state.Enemies, _enemies, _enemyDelta);
        return new CombatFactDelta
        {
            Allies = _allyDelta, Enemies = _enemyDelta,
            AllyUnion = allyUnion, EnemyUnion = enemyUnion, Facts = facts,
        };
    }

    private static UnitChannel DiffList(IReadOnlyList<CombatantState> units,
        UnitSnapshot[] snapshots, UnitChannel[] delta)
    {
        var union = UnitChannel.None;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var snap = Capture(unit);
            var ch = UnitChannel.None;
            var prev = snapshots[i];
            // 引用不同（新单位/新实例）视为全通道翻转，杜绝对象复用误判。
            if (!ReferenceEquals(prev.Stats, snap.Stats)) ch |= UnitChannel.Stats | UnitChannel.Hp | UnitChannel.Mana;
            if (prev.Hp != snap.Hp) ch |= UnitChannel.Hp;
            if (prev.Mana != snap.Mana) ch |= UnitChannel.Mana;
            if (prev.Alive != snap.Alive) ch |= UnitChannel.Alive | UnitChannel.Hp;
            if (prev.Threat != snap.Threat) ch |= UnitChannel.Threat;
            if (prev.BuffCount != snap.BuffCount || prev.BuffStackSum != snap.BuffStackSum
                || prev.BuffExpirySum != snap.BuffExpirySum) ch |= UnitChannel.Buff;
            // 新增数组槽位（波次注入）无上一次快照：全通道翻转。
            if (snapshots[i].Stats is null && snap.Stats is not null) ch = (UnitChannel)0xFF;
            snapshots[i] = snap;
            delta[i] = ch;
            union |= ch;
        }
        return union;
    }

    private static UnitSnapshot[] CaptureAll(IReadOnlyList<CombatantState> units)
    {
        var arr = new UnitSnapshot[units.Count];
        for (var i = 0; i < units.Count; i++) arr[i] = Capture(units[i]);
        return arr;
    }

    private static UnitSnapshot Capture(CombatantState unit)
    {
        double stackSum = 0, expirySum = 0;
        var buffs = unit.Buffs;
        for (var i = 0; i < buffs.Count; i++)
        {
            stackSum += buffs[i].Stacks;
            expirySum += buffs[i].ExpiresAtSeconds;
        }
        return new UnitSnapshot
        {
            Hp = unit.Hitpoints,
            Mana = unit.Manapoints,
            Alive = unit.IsAlive,
            BuffCount = buffs.Count,
            BuffStackSum = stackSum,
            BuffExpirySum = expirySum,
            Threat = unit.EffectiveThreat,
            Stats = unit.Stats,
        };
    }
}
