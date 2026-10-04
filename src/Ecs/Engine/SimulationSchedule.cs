using System.Collections.ObjectModel;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;

namespace DaoHua.Ecs;

/// <summary>
/// 在线推送调度辅助：从聚合状态推导「下一个用户可见事件」的墙钟时刻。
/// 战斗出手、相位切换、行动完成、Buff 到期等边界都是确定性可知的；
/// 宿主服务在边界时刻醒来推进并推送，事件到达时间≈发生时刻，
/// 避免每秒批量节拍让结算表现晚于客户端动作动画。
/// 只读状态、不产生副作用，可独立单测。
/// </summary>
public static class SimulationSchedule
{
    /// <summary>
    /// 推导下一个可见事件边界的墙钟时刻；无可见边界（空闲/受阻/斗法等待撮合）返回 null。
    /// 返回值为估计下界：推进器到达该时刻后按真实结算重算，误差只影响推送早晚，不影响结果。
    /// </summary>
    public static DateTimeOffset? NextVisibleBoundaryUtc(IPlayerResourceContext player, GameDataCatalog gameData)
    {
        var from = player.LastSimulatedAt;
        var fromUnix = from.ToUnixTimeMilliseconds() / 1000d;
        var best = double.PositiveInfinity;

        void Consider(double secondsFromNow)
        {
            if (secondsFromNow < best) best = secondsFromNow;
        }
        void ConsiderUnix(double unixSeconds)
        {
            if (unixSeconds > fromUnix) Consider(unixSeconds - fromUnix);
        }

        var action = player.Actions.Count > 0 ? player.Actions[0] : null;
        // 队列暂停时行动侧边界全部冻结；Buff/技能/自动使用等墙钟边界照常参与调度。
        if (action is not null && action.Status != ActionQueueStatus.Blocked && !player.Settings.QueuePaused)
        {
            if (action.Expedition is { } expedition)
            {
                switch (expedition.Phase)
                {
                    case CombatExpeditionPhase.InitialSearch
                        or CombatExpeditionPhase.Search
                        or CombatExpeditionPhase.Respawning:
                        Consider(Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds));
                        break;
                    case CombatExpeditionPhase.Duel:
                        if (expedition.Duel is { } playback && !playback.Resolved)
                        {
                            // 回放按剧本时间轴推进：下一事件或终局结算。
                            var cursor = (int)playback.EventCursor;
                            var nextEventAt = cursor < playback.Events.Count
                                ? playback.Events[cursor].AtSeconds
                                : double.PositiveInfinity;
                            var playbackBoundary = Math.Min(nextEventAt, playback.DurationSeconds);
                            if (double.IsFinite(playbackBoundary))
                                Consider(Math.Max(0, playbackBoundary - expedition.BattleElapsedSeconds));
                        }
                        break;
                    case CombatExpeditionPhase.Combat:
                        // 倒地窗口（原地复活）：双方停手，唯一可见边界是复活截止点；回复静默推进不推送。
                        if (expedition.PlayerRespawnAtSeconds is { } respawnAt)
                        {
                            Consider(Math.Max(0, respawnAt - expedition.BattleElapsedSeconds));
                            break;
                        }
                        // 出手边界：存活方最早出手时刻（战斗时钟 → 墙钟换算按相位不变前提）。
                        var nextAttack = expedition.PlayerHitpoints > 0
                            ? expedition.PlayerNextAttackAtSeconds
                            : double.PositiveInfinity;
                        if (expedition.Enemies is not null)
                            foreach (var enemy in expedition.Enemies)
                                if (enemy.State.Hitpoints > 0)
                                    nextAttack = Math.Min(nextAttack, enemy.State.NextAttackAtSeconds);
                        if (double.IsFinite(nextAttack))
                            Consider(Math.Max(0, nextAttack - expedition.BattleElapsedSeconds));
                        // 战斗内技能边界（吟唱/冷却按战斗时钟扣减）。
                        var battleSkillBoundary = player.Combat.Skills.NextBoundarySeconds;
                        if (double.IsFinite(battleSkillBoundary)) Consider(Math.Max(0, battleSkillBoundary));
                        break;
                    // DuelPending 不排边界：撮合由每拍兜底处理。
                }
            }
            else if (action.Travel is { } travel)
            {
                // 路段切换会改变地图展示，先排到当前路段终点，再兜底全程终点。
                if (travel.Segments is { Count: > 0 } segments
                    && travel.CurrentSegmentIndex >= 0 && travel.CurrentSegmentIndex < segments.Count)
                    Consider(Math.Max(0, segments[travel.CurrentSegmentIndex].DurationSeconds - travel.SegmentElapsedSeconds));
                else
                    Consider(Math.Max(0, travel.TotalDurationSeconds - travel.ProgressSeconds));
            }
            else
            {
                var duration = gameData.Actions.Get(action.Intent.ActionId).DurationSeconds;
                if (duration > 0) Consider(Math.Max(0, duration - action.ProgressSeconds));
            }
        }

        // 脱战技能冷却：非交战相位按墙钟扣减（AdvanceIdle 口径）。
        if (action?.Expedition?.Phase != CombatExpeditionPhase.Combat)
        {
            var idleSkillBoundary = player.Combat.Skills.NextBoundarySeconds;
            if (double.IsFinite(idleSkillBoundary)) Consider(Math.Max(0, idleSkillBoundary));
        }

        // Buff 到期与自动使用冷却到点均为墙钟事件且影响界面展示。
        foreach (var buff in player.Buffs.Active)
            ConsiderUnix(buff.ExpiresAtUnixSeconds);
        foreach (var slot in player.Settings.AutoUseSlots)
            if (slot.Enabled)
                ConsiderUnix(slot.CooldownUntilUnixSeconds);

        return double.IsFinite(best) ? from.AddSeconds(Math.Max(0, best)) : null;
    }
}
