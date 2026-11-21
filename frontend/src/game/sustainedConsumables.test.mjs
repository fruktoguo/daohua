import '../../test-loader.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'

const {
  hasActiveSustainedEffect,
  resolveEffectiveEnhancementSuccessBonuses,
  resolveSustainedDurationClock,
} = await import('./sustainedConsumables.ts')
const { enhanceSuccessRate } = await import('./combatConstants.ts')

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

test('强化成功率消耗品仅对装备强化行动生效，秘法行动不充能', () => {
  const powder = pill({ usableBars: ['enhancement'], enhancementSuccessBonus: 0.03 })
  const enhanceAction = action({ id: 'enhance.equipment', type: 'enhancement', technique: 'enhancement' })
  const liquefyAction = action({ id: 'secret_art.liquefy', type: 'enhancement', technique: 'enhancement' })
  const disassembleAction = action({ id: 'secret_art.disassemble', type: 'enhancement', technique: 'enhancement' })

  assert.equal(hasActiveSustainedEffect(powder, enhanceAction, 'enhancement'), true)
  assert.equal(hasActiveSustainedEffect(powder, liquefyAction, 'enhancement'), false)
  assert.equal(hasActiveSustainedEffect(powder, disassembleAction, 'enhancement'), false)
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

test('秘法化灵、分解、转化对强化成功率消耗品均返回冻结时钟', () => {
  const sand = pill({ itemId: 'item.guling-sand', usableBars: ['enhancement'], enhancementSuccessBonus: 0.05 })
  for (const actionId of ['secret_art.liquefy', 'secret_art.disassemble', 'secret_art.convert']) {
    const secretArtAction = action({ id: actionId, type: 'enhancement', technique: 'enhancement', durationSeconds: 6 })
    const secretArtEntry = entry({ actionId, type: 'enhancement', progressSeconds: 3, durationSeconds: 6 })
    assert.equal(hasActiveSustainedEffect(sand, secretArtAction, 'enhancement'), false, `${actionId} 不应激活强化成功率消耗品`)
    assert.deepEqual(
      resolveSustainedDurationClock('enhancement', sand, secretArtAction, secretArtEntry, false, 1.5),
      { mode: 'frozen' },
      `${actionId} 期间应冻结强化消耗品倒计时`,
    )
  }
})

test('enhanceSuccessRate 正确累加消耗品成功率并钳位到 99%', () => {
  const base = enhanceSuccessRate(1, 0)
  assert.ok(Math.abs(enhanceSuccessRate(1, 0, 0.03) - (base + 0.03)) < 1e-12)
  assert.ok(Math.abs(enhanceSuccessRate(1, 0, 0.08) - (base + 0.08)) < 1e-12)
  assert.equal(enhanceSuccessRate(1, 0, 0.8), 0.99)
  assert.equal(enhanceSuccessRate(1, 0, -0.2), base)
})

test('resolveEffectiveEnhancementSuccessBonuses 按库存与激活残余即时汇总并防重复计数', () => {
  const powder = pill({
    itemId: 'item.cuiling-powder',
    name: '淬灵粉',
    usableBars: ['enhancement'],
    enhancementSuccessBonus: 0.03,
  })
  const sand = pill({
    itemId: 'item.guling-sand',
    name: '固灵砂',
    usableBars: ['enhancement'],
    enhancementSuccessBonus: 0.05,
  })
  const catalog = [powder, sand]

  // 1. 未激活但背包库存充足：即时计入预期加成
  const ready = resolveEffectiveEnhancementSuccessBonuses(
    [
      { itemId: 'item.cuiling-powder', quantity: 1, enabled: true, remainingDurationSeconds: 0 },
      { itemId: 'item.guling-sand', quantity: 1, enabled: true, remainingDurationSeconds: 0 },
    ],
    catalog,
    [
      { itemId: 'item.cuiling-powder', amount: 1 },
      { itemId: 'item.guling-sand', amount: 2 },
    ],
  )
  assert.ok(Math.abs(ready.totalBonus - 0.08) < 1e-12)
  assert.deepEqual(ready.items.map(i => i.itemId), ['item.cuiling-powder', 'item.guling-sand'])

  // 2. 已充能且仍有剩余时长：即便背包已扣空（0 件），本轮依然有效
  const activeNoInventory = resolveEffectiveEnhancementSuccessBonuses(
    [{ itemId: 'item.cuiling-powder', quantity: 1, enabled: true, remainingDurationSeconds: 120 }],
    catalog,
    [],
  )
  assert.ok(Math.abs(activeNoInventory.totalBonus - 0.03) < 1e-12)

  // 3. 禁用或无库存且无残余时长：不计入
  const disabledOrEmpty = resolveEffectiveEnhancementSuccessBonuses(
    [
      { itemId: 'item.cuiling-powder', quantity: 1, enabled: false, remainingDurationSeconds: 120 },
      { itemId: 'item.guling-sand', quantity: 1, enabled: true, remainingDurationSeconds: 0 },
    ],
    catalog,
    [{ itemId: 'item.cuiling-powder', amount: 5 }],
  )
  assert.equal(disabledOrEmpty.totalBonus, 0)
  assert.equal(disabledOrEmpty.items.length, 0)

  // 4. 两个未充能槽同挂一种物品但库存只有 1 件：只预约 1 件不重复翻倍
  const singleStockTwoSlots = resolveEffectiveEnhancementSuccessBonuses(
    [
      { itemId: 'item.guling-sand', quantity: 1, enabled: true, remainingDurationSeconds: 0 },
      { itemId: 'item.guling-sand', quantity: 1, enabled: true, remainingDurationSeconds: 0 },
    ],
    catalog,
    [{ itemId: 'item.guling-sand', amount: 1 }],
  )
  assert.ok(Math.abs(singleStockTwoSlots.totalBonus - 0.05) < 1e-12)
  assert.equal(singleStockTwoSlots.items.length, 1)
})
