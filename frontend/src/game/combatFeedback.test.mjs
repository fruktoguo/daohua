import test from 'node:test'
import assert from 'node:assert/strict'
import {
  COMBAT_ATTACK_HIT_DELAY_MS,
  COMBAT_CAST_HIT_DELAY_MS,
  COMBAT_COMBO_STEP_MS,
  COMBAT_EVENT_REPLAY_BUDGET_MS,
  COMBAT_EVENT_REPLAY_LIMIT,
  collectScheduledCombatEvents,
  combatHitDelayMilliseconds,
} from './combatFeedback.ts'

function event(overrides = {}) {
  return {
    at: 10,
    attackerId: 'enemy:0',
    targetId: 'player',
    damage: 12,
    hit: true,
    abilityId: null,
    targetHp: 88,
    ...overrides,
  }
}

test('combatFeedback：稳定键不随数组窗口平移变化', () => {
  const first = event({ at: 10, damage: 12, targetHp: 88 })
  const second = event({ at: 10.2, damage: 15, targetHp: 73 })
  const third = event({ at: 10.4, damage: 9, targetHp: 64 })

  const handled = new Set()
  const initial = collectScheduledCombatEvents([first, second], () => true, handled)
  assert.equal(initial.length, 2)
  for (const entry of initial) handled.add(entry.key)

  // 斗法回放窗口滑动：同一事件的下标从 1 变成 0，仍必须按已消费跳过。
  const shifted = collectScheduledCombatEvents([second, third], () => true, handled)
  assert.equal(shifted.length, 1)
  assert.equal(shifted[0].event, third)
})

test('combatFeedback：批次跨度压缩到重放预算内且保持出手先后', () => {
  const events = [
    event({ at: 100, damage: 5, targetHp: 95 }),
    event({ at: 102, damage: 6, targetHp: 89 }),
    event({ at: 104, damage: 7, targetHp: 82 }),
  ]
  const scheduled = collectScheduledCombatEvents(events, () => true, new Set())
  assert.deepEqual(scheduled.map(entry => entry.delay), [
    0,
    Math.round(COMBAT_EVENT_REPLAY_BUDGET_MS / 2),
    COMBAT_EVENT_REPLAY_BUDGET_MS,
  ])
})

test('combatFeedback：同一时刻多段连击逐段错开', () => {
  const events = [
    event({ at: 20, damage: 3, targetHp: 97 }),
    event({ at: 20, damage: 4, targetHp: 93 }),
    event({ at: 20, damage: 5, targetHp: 88 }),
  ]
  const scheduled = collectScheduledCombatEvents(events, () => true, new Set())
  assert.deepEqual(scheduled.map(entry => entry.delay), [
    0,
    COMBAT_COMBO_STEP_MS,
    COMBAT_COMBO_STEP_MS * 2,
  ])
  assert.deepEqual(scheduled.map(entry => entry.comboIndex), [0, 1, 2])
})

test('combatFeedback：只返回匹配且未消费的事件', () => {
  const playerHit = event({ targetId: 'player', at: 30 })
  const enemyHit = event({ targetId: 'enemy:1', at: 31, attackerId: 'player' })
  const scheduled = collectScheduledCombatEvents(
    [playerHit, enemyHit],
    candidate => candidate.targetId === 'enemy:1',
    new Set(),
  )
  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].event, enemyHit)
})

test('combatFeedback：空批次与全部已消费返回空数组', () => {
  assert.deepEqual(collectScheduledCombatEvents([], () => true, new Set()), [])
  const handled = new Set()
  const first = collectScheduledCombatEvents([event()], () => true, handled)
  for (const entry of first) handled.add(entry.key)
  assert.deepEqual(collectScheduledCombatEvents([event()], () => true, handled), [])
})

test('combatFeedback：离线补算的超长批次只保留最近一段', () => {
  const events = Array.from({ length: COMBAT_EVENT_REPLAY_LIMIT + 12 }, (_, index) => event({
    at: index,
    damage: 1 + index,
    targetHp: 10_000 - index * 10,
  }))
  const scheduled = collectScheduledCombatEvents(events, () => true, new Set())
  assert.equal(scheduled.length, COMBAT_EVENT_REPLAY_LIMIT)
  assert.equal(scheduled[0].event.at, 12)
  assert.equal(scheduled.at(-1).event.at, events.at(-1).at)
  assert.equal(scheduled[0].delay, 0)
  assert.equal(scheduled.at(-1).delay, COMBAT_EVENT_REPLAY_BUDGET_MS)
})

test('combatFeedback：命中帧按普攻/技能区分', () => {
  assert.equal(combatHitDelayMilliseconds({ abilityId: null }), COMBAT_ATTACK_HIT_DELAY_MS)
  assert.equal(combatHitDelayMilliseconds({ abilityId: 'ability.thunder' }), COMBAT_CAST_HIT_DELAY_MS)
})
