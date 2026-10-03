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
        if (typeof(TTag) == typeof(ICombatPlayerRespawnPhase)) { CombatPlayerRespawnSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatPlayerActPhase)) { CombatPlayerActSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatEnemyActPhase)) { CombatEnemyActSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatPlayerDownedPhase)) { CombatPlayerDownedSystem.Apply(context); return; }
        if (typeof(TTag) == typeof(ICombatOutcomePhase)) { CombatOutcomeSystem.Apply(context); return; }
        throw new InvalidOperationException($"未登记的战斗相位 tag：{typeof(TTag).Name}。");
    }
}

[SystemOrder(100)]
public sealed class CombatBuffTickSystem : YuoSystem<CombatEncounterComponent>, ICombatBuffTickPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>战内回复沿既有 5 秒网格落账。</summary>
    internal const double TickSeconds = 5;

    /// <summary>战内效果相位结算：先剔除到期 Buff，再按 5 秒网格对存活单位结算周期回复。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var at = context.EffectAt;
        encounter.Player.Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        for (var i = 0; i < encounter.Enemies.Count; i++)
            encounter.Enemies[i].Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        if (Math.Abs(at / TickSeconds - Math.Round(at / TickSeconds)) > 1e-9) return;
        // 结算顺序与原 Prepend(player) 一致：先玩家后敌人。
        Tick(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) Tick(encounter.Enemies[i]);

        static void Tick(CombatantState unit)
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
            if (unit.IsAlive && hp != 0) unit.Hitpoints = Math.Clamp(unit.Hitpoints + hp, 0, unit.Stats.MaxHitpoints);
            if (mp != 0) unit.Manapoints = Math.Clamp(unit.Manapoints + mp, 0, unit.Stats.MaxManapoints);
        }
    }
}

[SystemOrder(110)]
public sealed class CombatPlayerRespawnSystem : YuoSystem<CombatEncounterComponent>, ICombatPlayerRespawnPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>玩家复活相位：倒地倒计时到期时恢复满气血、清空复活计时并重新决策初始动作。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        encounter.Player.Hitpoints = encounter.Player.Stats.MaxHitpoints;
        encounter.PlayerRespawnAtSeconds = double.PositiveInfinity;
        CombatActSystem.DecideInitialActions(encounter);
        (context.PlayerRespawns ??= []).Add(encounter.ElapsedSeconds);
    }
}

[SystemOrder(120)]
public sealed class CombatPlayerActSystem : CombatActSystem, ICombatPlayerActPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>玩家方出手相位：先结算待完成动作（普攻或吟唱完成的技能），再决策下一动作。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var player = encounter.Player;
        var allies = encounter.PlayerAllies;
        ResolvePending(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3), 0, ref context.Events);
        DecideNextAction(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3));
    }
}

[SystemOrder(130)]
public sealed class CombatEnemyActSystem : CombatActSystem, ICombatEnemyActPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>敌方出手相位：与玩家同构，先结算待完成动作再决策下一动作。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        var enemy = context.Enemy!;
        ResolvePending(encounter, enemy, encounter.Enemies, encounter.PlayerAllies, encounter.Player,
            (ulong)(1 + context.EnemyIndex), ref context.Events);
        DecideNextAction(encounter, enemy, encounter.Enemies, encounter.PlayerAllies, encounter.Player);
    }
}

[SystemOrder(140)]
public sealed class CombatPlayerDownedSystem : YuoSystem<CombatEncounterComponent>, ICombatPlayerDownedPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>玩家死亡相位：倒地瞬间进入复活倒计时，全场吟唱作废并记录死亡时刻。</summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        encounter.PlayerRespawnAtSeconds = encounter.ElapsedSeconds + encounter.PlayerRespawnSeconds;
        InterruptCasts(encounter);
        (context.PlayerDeaths ??= []).Add(encounter.ElapsedSeconds);
    }

    /// <summary>团灭瞬间全场吟唱作废：未完成的吟唱视为未释放，退还起手预扣灵力且该槽不进入冷却。</summary>
    private static void InterruptCasts(CombatEncounterState encounter)
    {
        // 处理顺序与原 Prepend(player) 一致：先玩家后敌人。
        InterruptCast(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) InterruptCast(encounter.Enemies[i]);
        static void InterruptCast(CombatantState unit)
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
}

[SystemOrder(150)]
public sealed class CombatOutcomeSystem : YuoSystem<CombatEncounterComponent>, ICombatOutcomePhase
{
    public override void Run(CombatEncounterComponent encounter)
        => Apply(encounter.PhaseContext!);

    /// <summary>
    /// 根据交战双方存活状态更新战局结果。
    /// </summary>
    internal static void Apply(CombatPhaseContext context)
    {
        var encounter = context.Encounter;
        encounter.Outcome = (encounter.Player.IsAlive, AnyAlive(encounter.Enemies)) switch
        {
            // 原地复活待触发期间团灭不是终局：敌方残存或被持续伤害跳死全灭都保持 Running，起身后统一裁定。
            (false, _) when double.IsFinite(encounter.PlayerRespawnAtSeconds) => CombatOutcome.Running,
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }
}
