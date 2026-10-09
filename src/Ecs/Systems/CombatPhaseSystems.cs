using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using Domain.Combat;
using YuoCore.Ecs;
using static DaoHua.Ecs.DeterministicCombatEngine;

namespace DaoHua.Ecs.Systems;

/// <summary>无世界相位派发只选择对应 System；规则与实体 tag 派发共用唯一实现。</summary>
internal static class CombatPhaseSystems
{
    public static void DispatchInline<TTag>(CombatPhaseContext context) where TTag : ISystemTag
    {
        if (typeof(TTag) == typeof(ICombatBuffTickPhase)) { CombatBuffTickSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatAllyRespawnPhase)) { CombatAllyRespawnSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatAllyActPhase)) { CombatAllyActSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatEnemyActPhase)) { CombatEnemyActSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatAllyDownedPhase)) { CombatAllyDownedSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatOutcomePhase)) { CombatOutcomeSystem.Apply(context); return; }
        throw new InvalidOperationException($"未登记的战斗相位 tag：{typeof(TTag).Name}。");
    }
}

/// <summary>
/// 战内效果相位（order 100）：剔除到期 Buff 并按 5 秒网格对存活单位结算周期回复。
/// 实体路径经 RunSystem 派发、内联路径直接调用，两者共用 Apply 实现。
/// </summary>
[SystemOrder(100)]
public sealed class CombatBuffTickSystem : YuoSystem<CombatEncounterComponent>, ICombatBuffTickPhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>战内回复沿既有 5 秒网格落账。</summary>
    internal const double TickSeconds = 5;

    /// <summary>战内效果相位结算：先剔除到期 Buff，再按 5 秒网格对存活单位结算周期回复。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var at = context.EffectAt;
        for (var i = 0; i < encounter.PlayerAllies.Count; i++)
            encounter.PlayerAllies[i].Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        for (var i = 0; i < encounter.Enemies.Count; i++)
            encounter.Enemies[i].Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        if (Math.Abs(at / TickSeconds - Math.Round(at / TickSeconds)) > 1e-9) return;
        var statsBook = encounter.StatsBook;
        // 结算顺序与原 Prepend(player) 一致：先全体盟友（成员序）后敌人。
        for (var i = 0; i < encounter.PlayerAllies.Count; i++) Tick(encounter.PlayerAllies[i], statsBook);
        for (var i = 0; i < encounter.Enemies.Count; i++) Tick(encounter.Enemies[i], statsBook);

        static void Tick(CombatantState unit, CombatUnitStatsBook? statsBook)
        {
            // 面板回复与战内 Buff 回复同口径：均为每 5 秒结算点一次；
            // 收口公式（总纲 §20.3）：(固定回复合计) × (1 + Σ后段比率)。
            double hp = unit.Stats.HpRegen, mp = unit.Stats.MpRegen;
            double hpPost = 0, mpPost = 0;
            var buffs = unit.Buffs;
            for (var i = 0; i < buffs.Count; i++)
            {
                hp += buffs[i].HpRegen * buffs[i].Stacks;
                mp += buffs[i].MpRegen * buffs[i].Stacks;
                hpPost += buffs[i].HpRegenPostRatio * buffs[i].Stacks;
                mpPost += buffs[i].MpRegenPostRatio * buffs[i].Stacks;
            }
            hp *= 1 + hpPost;
            mp *= 1 + mpPost;
            // 阵亡单位气血锁 0 不结算回血，灵力回复照常入账（死亡灵力运转规约战内同构）。
            if (unit.IsAlive && hp != 0)
            {
                var hitpointsBefore = unit.Hitpoints;
                unit.Hitpoints = Math.Clamp(unit.Hitpoints + hp, 0, unit.Stats.MaxHitpoints);
                // 周期回复计「自动回复」；负向回复（减益跳血）不归属任何单位，不入账。
                if (unit.Hitpoints > hitpointsBefore)
                    statsBook?.RecordHealing(unit, autoRegen: true, unit.Hitpoints - hitpointsBefore);
            }
            if (mp != 0) unit.Manapoints = Math.Clamp(unit.Manapoints + mp, 0, unit.Stats.MaxManapoints);
        }
    }
}

/// <summary>友方复活相位（order 110）：单个盟友倒地倒计时到期时恢复满气血并重新决策动作。</summary>
[SystemOrder(110)]
public sealed class CombatAllyRespawnSystem : YuoSystem<CombatEncounterComponent>, ICombatAllyRespawnPhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>友方复活相位：倒地倒计时到期的盟友恢复满气血、清空复活计时，未定动作的全体单位补决策。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var ally = context.Ally ?? throw new InvalidOperationException("复活相位缺少盟友上下文。");
        ally.Hitpoints = ally.Stats.MaxHitpoints;
        ally.RespawnAtSeconds = double.PositiveInfinity;
        CombatActSystem.DecideInitialActions(encounter);
        // 复活标记分流：本机成员走 PlayerRespawns（沿用单人写回口径），其余成员进盟友标记列表。
        if (context.AllyIndex == encounter.SelfIndex)
            (context.PlayerRespawns ??= []).Add(encounter.ElapsedSeconds);
        else
            (context.AllyRespawns ??= []).Add((context.AllyIndex, encounter.ElapsedSeconds));
    }
}

/// <summary>友方出手相位（order 120）：单个盟友结算待完成动作（普攻/吟唱完成的技能）后决策下一动作。</summary>
[SystemOrder(120)]
public sealed class CombatAllyActSystem : CombatActSystem, ICombatAllyActPhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>
    /// 友方出手相位：先结算当前盟友待完成动作（普攻或吟唱完成的技能），再决策下一动作。
    /// 随机侧位按成员下标固定分配（成员 0 沿用单人位 0，其余成员高位偏移），
    /// 与所在分片/在线状态无关，保证各成员重放同一剧本。
    /// </summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var ally = context.Ally ?? throw new InvalidOperationException("出手相位缺少盟友上下文。");
        var side = AllySide(context.AllyIndex);
        var allies = encounter.PlayerAllies;
        ResolvePending(encounter, ally, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, side, ally.AttackCount, 3), side, ref context.Events);
        DecideNextAction(encounter, ally, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, side, ally.AttackCount, 3));
    }
}

/// <summary>
/// 敌方出手相位（order 130）：与玩家同构，先结算待完成动作再决策下一动作。
/// 目标在玩家方友军上按有效威胁轮盘抽取（通道 18/19），多盟友下威胁权重自动生效。
/// </summary>
[SystemOrder(130)]
public sealed class CombatEnemyActSystem : CombatActSystem, ICombatEnemyActPhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>敌方出手相位：与玩家同构，先结算待完成动作再决策下一动作。
    /// 目标在玩家方友军上按有效威胁轮盘抽取（通道 18/19）；单盟友提前返回不掷点，
    /// 与固定锁定玩家逐位等价，多盟友（召唤物/组队）下威胁权重自动生效。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var enemy = context.Enemy!;
        var side = (ulong)(1 + context.EnemyIndex);
        var target = SelectTarget(encounter.PlayerAllies, encounter.Seed, side, enemy.AttackCount, 18);
        ResolvePending(encounter, enemy, encounter.Enemies, encounter.PlayerAllies, target,
            side, ref context.Events);
        DecideNextAction(encounter, enemy, encounter.Enemies, encounter.PlayerAllies,
            SelectTarget(encounter.PlayerAllies, encounter.Seed, side, enemy.AttackCount, 19));
    }
}

/// <summary>
/// 友方倒地相位（order 140）：单个盟友阵亡瞬间进入复活倒计时并作废其吟唱；
/// 若构成全灭则按单人口径作废全场吟唱。死亡时刻按本机/队友分流记录。
/// </summary>
[SystemOrder(140)]
public sealed class CombatAllyDownedSystem : YuoSystem<CombatEncounterComponent>, ICombatAllyDownedPhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>
    /// 友方倒地相位：倒地盟友进入复活倒计时，自身吟唱作废退还；
    /// 全灭瞬间（单人恒成立）全场吟唱作废，与单人口径逐位一致。
    /// </summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var ally = context.Ally ?? throw new InvalidOperationException("倒地相位缺少盟友上下文。");
        ally.RespawnAtSeconds = encounter.ElapsedSeconds + encounter.PlayerRespawnSeconds;
        if (encounter.IsAllAlliesDowned) InterruptCasts(encounter);
        else InterruptCast(ally);
        if (context.AllyIndex == encounter.SelfIndex)
            (context.PlayerDeaths ??= []).Add(encounter.ElapsedSeconds);
        else
            (context.AllyDeaths ??= []).Add((context.AllyIndex, encounter.ElapsedSeconds));
    }

    /// <summary>全灭瞬间全场吟唱作废：未完成的吟唱视为未释放，退还起手预扣灵力且该槽不进入冷却。</summary>
    private static void InterruptCasts(CombatEncounterState encounter)
    {
        // 处理顺序与原 Prepend(player) 一致：先全体盟友（成员序）后敌人。
        for (var i = 0; i < encounter.PlayerAllies.Count; i++) InterruptCast(encounter.PlayerAllies[i]);
        for (var i = 0; i < encounter.Enemies.Count; i++) InterruptCast(encounter.Enemies[i]);
    }

    /// <summary>作废单个单位未完成的吟唱：视为未释放，退还起手预扣灵力且该槽不进入冷却。</summary>
    private static void InterruptCast(CombatantState unit)
    {
        var pending = unit.PendingSkillIndex;
        unit.PendingSkillIndex = -1;
        if (pending < 0 || pending >= unit.Skills.Count) return;
        unit.Manapoints = Math.Min(unit.Stats.MaxManapoints,
            unit.Manapoints + AbilityNumber(unit.Skills[pending].Ability, "manaCost"));
        if (unit.SkillCooldownUntil is { } cooldowns && pending < cooldowns.Length)
            cooldowns[pending] = 0;
    }
}

/// <summary>终局裁决相位（order 150）：按双方存活状态更新战局结果，是每个自然边界的收尾派发点。</summary>
[SystemOrder(150)]
public sealed class CombatOutcomeSystem : YuoSystem<CombatEncounterComponent>, ICombatOutcomePhase
{
    /// <summary>相位入口：从遭遇组件读取相位上下文并执行本相位结算。</summary>
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>
    /// 按双方存活状态裁决战局；原地复活待触发期间全灭不算终局（保持 Running，任一盟友起身后统一裁定）。
    /// </summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        encounter.Outcome = (AnyAlive(encounter.PlayerAllies), AnyAlive(encounter.Enemies)) switch
        {
            // 原地复活待触发期间全灭不是终局：敌方残存或被持续伤害跳死全灭都保持 Running，起身后统一裁定。
            (false, _) when double.IsFinite(encounter.NextAllyRespawnAtSeconds) => CombatOutcome.Running,
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }
}
