import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { rm } from 'node:fs/promises'
import { build } from 'esbuild'

const tempOut = path.join(tmpdir(), `daohua-sustained-consumables-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/sustainedConsumables.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
})
const { hasActiveSustainedEffect, resolveSustainedDurationClock } = await import(pathToFileURL(tempOut))

const pill = overrides => ({
  itemId: 'item.test-pill',
  name: '测试丹药',
  description: '',
  kind: 'consumable',
  category: 'consumable',
  tier: 1,
  sellPrice: 0,
  isTradable: false,
  usableBars: ['exploration'],
  cooldownSeconds: 0,
  hitpointRestore: 0,
  manapointRestore: 0,
  isContinuous: true,
  durationSeconds: 300,
  ...overrides,
})

const action = overrides => ({
  id: 'action.test',
  name: '测试行动',
  description: '',
  type: 'cultivation',
  technique: null,
  durationSeconds: 10,
  ...overrides,
})

const entry = overrides => ({
  entryId: 'entry.test',
  actionId: 'action.test',
  actionName: '测试行动',
  type: 'cultivation',
  status: 'running',
  progressSeconds: 4,
  durationSeconds: 10,
  remainingRepeats: -1,
  pvpEnabled: false,
  difficulty: 0,
  ...overrides,
})

test('持续消耗品只对实际提供效果的非战斗行动倒计时', () => {
  const cultivationPill = pill({ cultivationMultiplier: 1.3 })
  assert.equal(hasActiveSustainedEffect(cultivationPill, action(), 'exploration'), true)
  assert.equal(hasActiveSustainedEffect(cultivationPill, action({ type: 'exploration' }), 'exploration'), false)

  const clock = resolveSustainedDurationClock(
    'exploration', cultivationPill, action({ type: 'exploration' }), entry({ type: 'exploration' }), false, 2,
  )
  assert.deepEqual(clock, { mode: 'frozen' })
})

test('暂停队列时不推进持续时间，恢复后按当前行动进度继续', () => {
  const cultivationPill = pill({ cultivationMultiplier: 1.3 })
  assert.deepEqual(resolveSustainedDurationClock(
    'exploration', cultivationPill, action(), entry(), true, 20,
  ), { mode: 'frozen' })

  assert.deepEqual(resolveSustainedDurationClock(
    'exploration', cultivationPill, action(), entry(), false, 1.5,
  ), {
    mode: 'action',
    progressAtSyncSeconds: 4,
    elapsedSinceSyncSeconds: 1.5,
    durationSeconds: 10,
  })
})

test('战斗槽只在 combat 阶段使用同步后的遭遇战时间', () => {
  const battlePill = pill({ usableBars: ['combat'], buffEffects: [{ durationSeconds: 60 }] })
  assert.deepEqual(resolveSustainedDurationClock(
    'combat', battlePill, action({ type: 'combat' }), entry({ type: 'combat', progressSeconds: 25, expedition: { phase: 'search' } }), false, 2,
  ), { mode: 'frozen' })

  assert.deepEqual(resolveSustainedDurationClock(
    'combat', battlePill, action({ type: 'combat' }), entry({
      type: 'combat', progressSeconds: 25, expedition: { phase: 'combat', battleElapsedSeconds: 8 },
    }), false, 0.75,
  ), { mode: 'combat', elapsedSinceSyncSeconds: 0.75 })
})

await rm(tempOut, { force: true })
