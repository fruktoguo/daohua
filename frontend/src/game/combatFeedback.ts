import type { CombatRecentEvent } from './types'

/**
 * 战斗事件表现调度（纯函数，供 `CombatArena` 的 `ArenaActor` / `ArenaResource` 共用）。
 *
 * 服务端按资源自然窗口（默认 5 秒）批量结算并下发战斗事件，一次快照可能同时携带
 * 数秒内的多次出手。若全部按「收到即播」处理，整段伤害会在同一帧爆开、与血条同步
 * 跳变；若只播最后一条，又会吞掉前面的伤害数字。这里把同一批**新**事件压缩到
 * `COMBAT_EVENT_REPLAY_BUDGET_MS` 内按出手时序重放，并保留同一时刻多段连击的错峰。
 */

/** 普攻动作前摇/命中时刻（毫秒）：对应 @keyframes arena-attack-lunge 43% 突进峰值 */
export const COMBAT_ATTACK_HIT_DELAY_MS = 200

/** 技能法术蓄力/爆发命中时刻（毫秒）：对应 @keyframes arena-cast-pulse 爆发峰值 */
export const COMBAT_CAST_HIT_DELAY_MS = 280

/** 单个快照批次的表现重放预算（毫秒）：跨度更长的历史事件按比例压缩到该预算内 */
export const COMBAT_EVENT_REPLAY_BUDGET_MS = 520

/** 单个快照批次最多重放的事件条数：离线补算可能一次携带整段战斗事件，避免计时器风暴 */
export const COMBAT_EVENT_REPLAY_LIMIT = 32

/** 单次调度最多扫描的事件条数：超长离线批次只扫描尾部，限定逐卡面的 O(n) 成本 */
export const COMBAT_EVENT_SCAN_LIMIT = 512

/** 同一时刻多段连击的逐段错开（毫秒） */
export const COMBAT_COMBO_STEP_MS = 110

/** 单张卡面同时保留的浮字/切片上限：避免连续多段与多单位集火时数字被挤出画面 */
export const COMBAT_RESOURCE_EFFECT_LIMIT = 24

/** 一条被排期的新战斗事件 */
export interface ScheduledCombatEvent {
  event: CombatRecentEvent
  /** 稳定去重键：同一事件在后续快照中重新下发时保持一致（不随数组下标漂移） */
  key: string
  /** 相对批次起点的表现延迟（毫秒），未含击中帧前摇；已含连击错峰 */
  delay: number
  /** 同一出手时刻、同一攻守双方中的第几段（0 起） */
  comboIndex: number
}

/**
 * 命中帧前摇：技能（有 abilityId）走施法爆发帧，普攻走挥砍命中帧。
 */
export function combatHitDelayMilliseconds(event: Pick<CombatRecentEvent, 'abilityId'>): number {
  return event.abilityId ? COMBAT_CAST_HIT_DELAY_MS : COMBAT_ATTACK_HIT_DELAY_MS
}

/**
 * 事件稳定键：包含全部可见结算字段。`occurrence` 只用于同一快照内完全相同的重复事件，
 * 避免同一时刻多段 0 伤害/闪避帧互相覆盖。
 */
function combatEventBaseKey(event: CombatRecentEvent): string {
  return [
    event.at.toFixed(3),
    event.attackerId,
    event.targetId,
    event.abilityId ?? '',
    event.damage,
    event.targetHp ?? '',
    event.hit ? 1 : 0,
    event.critical ? 1 : 0,
    event.broken ? 1 : 0,
    event.parried ? 1 : 0,
  ].join('|')
}

/**
 * 从一批战斗事件中挑出「匹配当前单位且尚未消费」的新事件，并按批次内时序排期。
 *
 * @param events 快照下发的最近事件（服务端按出手时序排列）
 * @param matches 当前单位关心的事件（受击方 / 出手方或受击方）
 * @param handled 该单位已消费的事件稳定键集合（跨快照复用）
 */
export function collectScheduledCombatEvents(
  events: readonly CombatRecentEvent[],
  matches: (event: CombatRecentEvent) => boolean,
  handled: ReadonlySet<string>,
): ScheduledCombatEvent[] {
  if (events.length === 0) return []
  const source = events.length > COMBAT_EVENT_SCAN_LIMIT
    ? events.slice(-COMBAT_EVENT_SCAN_LIMIT)
    : events

  // 先对扫描范围内的事件编号：即使本次只消费其中一部分，稳定键也不受过滤结果影响。
  const occurrences = new Map<string, number>()
  const identified: Array<{ event: CombatRecentEvent; key: string }> = []
  for (const event of source) {
    const base = combatEventBaseKey(event)
    const occurrence = occurrences.get(base) ?? 0
    occurrences.set(base, occurrence + 1)
    identified.push({ event, key: `${base}#${occurrence}` })
  }

  const fresh: Array<{ event: CombatRecentEvent; key: string }> = []
  for (const entry of identified) {
    if (!matches(entry.event) || handled.has(entry.key)) continue
    fresh.push(entry)
  }
  if (fresh.length === 0) return []

  // 离线补算可能一次携带整段战斗的成千上万条事件：只重放距当前最近的一小段，
  // 其余历史伤害已体现在最终血条里，不必逐条补播。
  const replay = fresh.length > COMBAT_EVENT_REPLAY_LIMIT
    ? fresh.slice(-COMBAT_EVENT_REPLAY_LIMIT)
    : fresh

  let earliest = Infinity
  let latest = -Infinity
  for (const { event } of replay) {
    if (event.at < earliest) earliest = event.at
    if (event.at > latest) latest = event.at
  }
  const spanMilliseconds = Math.max(0, (latest - earliest) * 1000)
  const scale = spanMilliseconds > COMBAT_EVENT_REPLAY_BUDGET_MS
    ? COMBAT_EVENT_REPLAY_BUDGET_MS / spanMilliseconds
    : 1

  const combos = new Map<string, number>()
  const scheduled: ScheduledCombatEvent[] = []
  for (const { event, key } of replay) {
    const comboKey = `${event.at.toFixed(2)}|${event.attackerId}|${event.targetId}`
    const comboIndex = combos.get(comboKey) ?? 0
    combos.set(comboKey, comboIndex + 1)
    const offset = Math.max(0, (event.at - earliest) * 1000 * scale)
    scheduled.push({
      event,
      key,
      delay: Math.round(offset + comboIndex * COMBAT_COMBO_STEP_MS),
      comboIndex,
    })
  }
  return scheduled
}
