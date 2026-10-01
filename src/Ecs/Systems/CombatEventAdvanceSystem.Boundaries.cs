using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Combat;

namespace DaoHua.Ecs.Systems;

public sealed partial class CombatEventAdvanceSystem
{
    /// <summary>单次推进的最大自然边界数，限制异常配表导致的无限循环。</summary>
    public const int MaxEventsPerAdvance = 100_000;

    /// <summary>开启一场玩家对单敌的遭遇战：纯创建后立即决策各单位首次出手。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy,
        double playerRespawnSeconds = 0)
        => Start(encounterId, seed, new CombatantTemplate(player), [new CombatantTemplate(enemy)], playerRespawnSeconds);

    /// <summary>开启一场多敌方单位的战斗遭遇。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, IReadOnlyList<CombatantTemplate> enemies,
        double playerRespawnSeconds = 0)
        => Start(encounterId, seed, new CombatantTemplate(player), enemies, playerRespawnSeconds);

    /// <summary>开启一场玩家方携带技能模板的遭遇（斗法剧本双方镜像同构）。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantTemplate player, IReadOnlyList<CombatantTemplate> enemies,
        double playerRespawnSeconds = 0)
    {
        var encounter = CombatEncounterState.Create(encounterId, seed, player, enemies, playerRespawnSeconds);
        // 开战即决策各单位首次出手：可放技能直接进入吟唱，否则按普攻周期蓄势。
        CombatActSystem.DecideInitialActions(encounter);
        return encounter;
    }

    /// <summary>
    /// 查询下一确定性战斗边界。返回值不会早于当前战斗时钟，也不会晚于 targetSeconds。
    /// ECS 宿主可用它把一帧拆成离散边界；现行 OOP 调用仍应使用 <see cref="AdvanceTo"/>。
    /// </summary>
    public static double NextBoundarySeconds(CombatEncounterState encounter, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        if (encounter.Outcome != CombatOutcome.Running || encounter.ElapsedSeconds >= targetSeconds)
            return encounter.ElapsedSeconds;

        var next = encounter.IsPlayerDowned ? encounter.PlayerRespawnAtSeconds : encounter.Player.NextAttackAtSeconds;
        if (!encounter.IsPlayerDowned)
        {
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds < next) next = enemy.NextAttackAtSeconds;
            }
        }

        var effectAt = NextBuffBoundary(encounter);
        if (effectAt < next) next = effectAt;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
        return Math.Min(next, targetSeconds);
    }

    /// <summary>
    /// 只推进一个确定性战斗边界：Buff 到期/回复、复活或一个时刻内双方按既有顺序的出手。
    /// 不复制伤害公式，也不改变随机流；返回结果的事件列表只包含本边界产生的事件。
    /// 当目标时间早于下一边界时只推进战斗时钟，不产生事件。
    /// </summary>
    public static CombatAdvanceResult AdvanceOneBoundary(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        if (encounter.Outcome != CombatOutcome.Running || from >= targetSeconds)
            return EmptyResult(encounter, from, targetSeconds);

        var context = new CombatPhaseContext { Encounter = encounter };
        AdvanceBoundary(encounter, targetSeconds, dispatcher ?? InlineCombatPhaseDispatcher.Instance, host, context);
        return new CombatAdvanceResult(encounter.EncounterId, from, encounter.ElapsedSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)context.Events ?? [], encounter.Sequence, context.PlayerDeaths, context.PlayerRespawns);
    }

    /// <summary>
    /// 将战斗状态按事件驱动推进至目标时刻：只跨过双方出手时刻，不产生等间隔时间片。
    /// </summary>
    /// <param name="encounter">遭遇战状态（就地推进）。</param>
    /// <param name="targetSeconds">目标战斗时钟秒数（不得早于当前已推进时间）。</param>
    /// <returns>本段推进的战斗帧：事件明细、终局裁决与序列号。</returns>
    public static CombatAdvanceResult AdvanceTo(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        dispatcher ??= InlineCombatPhaseDispatcher.Instance;
        var context = new CombatPhaseContext { Encounter = encounter };
        var eventCount = 0;
        while (encounter.Outcome == CombatOutcome.Running && encounter.ElapsedSeconds < targetSeconds - 1e-12)
        {
            if (++eventCount > MaxEventsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗推进超过最大事件数。");
            if (!AdvanceBoundary(encounter, targetSeconds, dispatcher, host, context)) break;
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)context.Events ?? [], encounter.Sequence, context.PlayerDeaths, context.PlayerRespawns);
    }

    private static void ValidateTarget(CombatEncounterState encounter, double targetSeconds)
    {
        if (!double.IsFinite(targetSeconds) || targetSeconds < encounter.ElapsedSeconds)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗目标时间无效。");
    }

    private static CombatAdvanceResult EmptyResult(CombatEncounterState encounter, double from, double to)
        => new(encounter.EncounterId, from, to, encounter.Outcome, [], encounter.Sequence);

    private static bool AdvanceBoundary(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher dispatcher, CombatEncounterComponent? host, CombatPhaseContext context)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.event");
        var naturalBoundary = GetNaturalBoundarySeconds(encounter, out var effectAt);
        if (naturalBoundary > targetSeconds)
        {
            encounter.ElapsedSeconds = targetSeconds;
            return false;
        }

        // 与原 AdvanceTo 完全一致：即使浮点差值小于容差，也必须写入边界原值，
        // 否则 5 秒回复网格在 9.999999999999991 等表示下会重复命中同一边界。
        encounter.ElapsedSeconds = Math.Max(encounter.ElapsedSeconds, naturalBoundary);

        if (effectAt <= encounter.ElapsedSeconds + 1e-12)
        {
            context.EffectAt = effectAt;
            dispatcher.Dispatch<ICombatBuffTickPhase>(host, context);
        }

        if (encounter.IsPlayerDowned)
        {
            if (encounter.PlayerRespawnAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                dispatcher.Dispatch<ICombatPlayerRespawnPhase>(host, context);
        }
        else
        {
            if (encounter.Player.IsAlive && encounter.Player.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                dispatcher.Dispatch<ICombatPlayerActPhase>(host, context);
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                {
                    context.EnemyIndex = i;
                    context.Enemy = enemy;
                    dispatcher.Dispatch<ICombatEnemyActPhase>(host, context);
                }
            }

            if (!encounter.Player.IsAlive && encounter.PlayerRespawnSeconds > 0)
                dispatcher.Dispatch<ICombatPlayerDownedPhase>(host, context);
        }

        encounter.Sequence += 1;
        dispatcher.Dispatch<ICombatOutcomePhase>(host, context);
        return true;
    }

    private static double GetNaturalBoundarySeconds(CombatEncounterState encounter, out double effectAt)
    {
        var next = encounter.IsPlayerDowned ? encounter.PlayerRespawnAtSeconds : encounter.Player.NextAttackAtSeconds;
        if (!encounter.IsPlayerDowned)
        {
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds < next) next = enemy.NextAttackAtSeconds;
            }
        }

        effectAt = NextBuffBoundary(encounter);
        if (effectAt < next) next = effectAt;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
        return next;
    }

    /// <summary>最近的战内效果边界：全体单位 Buff 的最早到期时刻；存在回复类 Buff 时叠加 5 秒结算网格。</summary>
    private static double NextBuffBoundary(CombatEncounterState encounter)
    {
        var next = double.PositiveInfinity;
        var regenPending = false;
        // 扫描顺序与原 Prepend(player) 一致：先玩家后敌人；min 求值无平局歧义。
        Scan(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) Scan(encounter.Enemies[i]);
        if (regenPending)
            next = Math.Min(next, (Math.Floor(encounter.ElapsedSeconds / Systems.CombatBuffTickSystem.TickSeconds) + 1) * Systems.CombatBuffTickSystem.TickSeconds);
        return next;

        void Scan(CombatantState unit)
        {
            if (unit.Stats.HpRegen != 0 || unit.Stats.MpRegen != 0) regenPending = true;
            var buffs = unit.Buffs;
            for (var i = 0; i < buffs.Count; i++)
            {
                if (buffs[i].ExpiresAtSeconds < next) next = buffs[i].ExpiresAtSeconds;
                if (buffs[i].HpRegen != 0 || buffs[i].MpRegen != 0) regenPending = true;
            }
        }
    }

}
