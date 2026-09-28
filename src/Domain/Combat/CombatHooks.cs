namespace Domain.Combat;

/// <summary>
/// 战斗反应效果参数包：单位随战携带的「攻击后/受击后」衍生效果强度集合。
/// 打包为单一记录挂载在 <see cref="CombatantStats.Effects"/> 上，后续新增效果
/// 只需增加字段，无需改动结算签名、快照结构与钩子接口。
/// 强度来源：装备 specialMechanics 与怪物配表，经属性解析折算写入。
/// </summary>
/// <param name="HpDrainPoints">气血汲取点数：命中后按「实伤 × 点数/100」回血（Linear 非负钳制，普攻与技能同构）。</param>
/// <param name="MpDrainPoints">灵力汲取点数：命中后按「实伤 × 点数/100」从目标灵力池偷取灵力。</param>
/// <param name="DamageReflectPoints">伤害反弹点数：受击落账后把实收伤害按「点数/100」弹回攻击方。</param>
/// <param name="HealingBonusPoints">治疗增幅点数（Linear）：施放治疗技能时按 1+点数/100 放大治疗量。</param>
public sealed record CombatEffectProfile(
    double HpDrainPoints = 0,
    double MpDrainPoints = 0,
    double DamageReflectPoints = 0,
    double HealingBonusPoints = 0)
{
    /// <summary>全部效果为零的空参数包。</summary>
    public static CombatEffectProfile Empty { get; } = new();

    /// <summary>校验全部效果强度为有限非负数；非法时抛出 InvalidArgument。</summary>
    internal void Validate()
    {
        if (!double.IsFinite(HpDrainPoints) || HpDrainPoints < 0
            || !double.IsFinite(MpDrainPoints) || MpDrainPoints < 0
            || !double.IsFinite(DamageReflectPoints) || DamageReflectPoints < 0
            || !double.IsFinite(HealingBonusPoints) || HealingBonusPoints < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗反应效果参数无效。");
    }
}

/// <summary>
/// 单次攻击结算上下文：行动标识、攻击方、受击方、技能信息与结算中间量打包传递。
/// 触发器派发期间就地读写本对象；触发器禁止另起随机源或读取墙钟，
/// 随机判定一律走 <see cref="NextRoll"/> 的确定性通道。
/// </summary>
public sealed class CombatHitContext
{
    /// <summary>当前遭遇战（含种子、敌方阵容与终局裁决）。</summary>
    public required CombatEncounterState Encounter { get; init; }
    /// <summary>攻击方运行时状态（气血/灵力/威胁就地可变）。</summary>
    public required CombatantState Attacker { get; init; }
    /// <summary>受击方运行时状态。</summary>
    public required CombatantState Target { get; init; }
    /// <summary>本次攻击生效的攻击方面板（技能为折算面板）；判定前可整体替换。</summary>
    public required CombatantStats AttackStats { get; set; }
    /// <summary>技能来源标识；null 表示普通攻击。</summary>
    public string? AbilityId { get; init; }
    /// <summary>是否技能结算（AbilityId 非空）。</summary>
    public bool IsSkill => AbilityId is not null;
    /// <summary>攻击方阵营侧位（确定性抽样维度）。</summary>
    public required ulong Side { get; init; }
    /// <summary>出手计数（确定性抽样维度）。</summary>
    public required double Count { get; init; }
    /// <summary>结算发生的战斗时钟秒数。</summary>
    public required double AtSeconds { get; init; }

    /// <summary>命中判定结果（由核心结算填充）。</summary>
    public bool Hit { get; set; }
    /// <summary>暴击判定结果。</summary>
    public bool Critical { get; set; }
    /// <summary>破防判定结果（攻方破拆胜出）。</summary>
    public bool Broken { get; set; }
    /// <summary>招架判定结果（守方招架胜出）。</summary>
    public bool Parried { get; set; }
    /// <summary>最终结算伤害；落账前触发器可改写（护盾/上限/免死），引擎按非负夹取落账。</summary>
    public double Damage { get; set; }

    /// <summary>触发器产出的衍生事件（汲取回血、反伤跳字等）；在主事件之后按序并入。</summary>
    public List<CombatEvent>? DerivedEvents { get; set; }

    // 0~15 号通道为核心结算保留（命中/暴击/破防招架/目标抽取），触发器通道自 64 起递增。
    private ulong _rollChannel = 64;

    /// <summary>取下一个触发器专属确定性随机数；通道按序递增，结果与快照恢复无关。</summary>
    public double NextRoll() => DeterministicRoll.Next(Encounter.Seed, Side, Count, _rollChannel++);

    /// <summary>追加一条衍生战斗事件。</summary>
    public void Emit(CombatEvent combatEvent) => (DerivedEvents ??= []).Add(combatEvent);
}

/// <summary>
/// 战斗触发器种类：每种对应一组消费系统/内联数学实现，由战斗边界在固定点派发。
/// 新增种类必须同时落：PendingCombatTrigger 生产点、派发映射与等价内联数学路径。
/// </summary>
public enum CombatTriggerKind
{
    /// <summary>伤害落账后：吸血/吸蓝（攻击方组件）与反伤（受击方组件）。</summary>
    OnHit = 0,
}

/// <summary>
/// 一条待派发触发器：宿主单位 + 命中上下文 + 主事件落点。
/// Unit 为宿主的纯领域状态引用；UnitEntityId 为其 ECS 实体标识（未挂实体时为 -1）。
/// Events/MainEventIndex 指向本条命中在结果事件流中的主事件槽位，派发完毕后由派发方
/// 回填双方灵力真值并按序并入 Context.DerivedEvents。
/// 该类型是纯数据记录，不参与序列化。
/// </summary>
public sealed record PendingCombatTrigger(
    CombatTriggerKind Kind,
    long UnitEntityId,
    CombatantState Unit,
    CombatHitContext Context,
    List<CombatEvent> Events,
    int MainEventIndex);

/// <summary>
/// 战斗触发器派发口：命中结算等固定点之后由引擎调用，按入队顺序消费 PendingTriggers。
/// ECS 宿主实现为 RunSystem&lt;tag&gt;(unitEntity) 实体派发；无实体路径实现为内联数学，
/// 两条路径共用同一 CombatTriggerMath，结算结果逐位一致。
/// </summary>
public interface ICombatTriggerSink
{
    /// <summary>按入队顺序清空并结算 <see cref="CombatEncounterState.PendingTriggers"/>。</summary>
    void Drain(CombatEncounterState encounter);
}
