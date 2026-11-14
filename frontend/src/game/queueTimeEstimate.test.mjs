import { after, describe, it } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { rm } from 'node:fs/promises'
import { build } from 'esbuild'

const suffix = `${Date.now()}-${process.pid}`
const estimateOut = path.join(tmpdir(), `daohua-queue-estimate-${suffix}.mjs`)
const zhOut = path.join(tmpdir(), `daohua-queue-estimate-zh-${suffix}.mjs`)

await Promise.all([
  build({
    entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/queueTimeEstimate.ts')],
    bundle: true,
    format: 'esm',
    outfile: estimateOut,
    logLevel: 'error',
  }),
  build({
    entryPoints: [path.resolve(process.cwd(), 'frontend/src/i18n/locales/zh-CN.ts')],
    bundle: true,
    format: 'esm',
    outfile: zhOut,
    logLevel: 'error',
  }),
])

const {
  calculateQueueActionEstimate,
  formatCompactDuration,
  formatQueueRepeatsText,
  getQueueEstimateDependencies,
  resolveLiveRemainingSeconds,
} = await import(pathToFileURL(estimateOut))
const { zhCN } = await import(pathToFileURL(zhOut))

after(async () => {
  await Promise.all([
    rm(estimateOut, { force: true }),
    rm(zhOut, { force: true }),
  ])
})

/** 测试用真实中文翻译器，校验词条路径与占位符完整闭合 */
function t(key, params = {}) {
  let node = zhCN
  for (const part of key.split('.')) {
    node = node?.[part]
  }
  assert.equal(typeof node, 'string', `缺失中文词条：${key}`)
  return node.replace(/\{(\w+)\}/g, (_, name) => {
    assert.ok(name in params, `词条 ${key} 缺少参数 {${name}}`)
    return String(params[name])
  })
}

describe('queueTimeEstimate：紧凑时长格式化与行动剩余总时间推算', () => {
  it('formatCompactDuration：覆盖秒/分/分秒/小时/天进位边界与异常值', () => {
    assert.equal(formatCompactDuration(0, t), '0秒')
    assert.equal(formatCompactDuration(-5, t), '0秒')
    assert.equal(formatCompactDuration(Number.NaN, t), '0秒')
    assert.equal(formatCompactDuration(0.3, t), '1秒')
    assert.equal(formatCompactDuration(42, t), '42秒')
    assert.equal(formatCompactDuration(60, t), '1分')
    assert.equal(formatCompactDuration(125, t), '2分5秒')
    assert.equal(formatCompactDuration(3599, t), '59分59秒')
    assert.equal(formatCompactDuration(3599.2, t), '1小时')
    assert.equal(formatCompactDuration(7200, t), '2小时')
    assert.equal(formatCompactDuration(20880, t), '5.8小时')
    assert.equal(formatCompactDuration(86390, t), '1天')
    assert.equal(formatCompactDuration(129600, t), '1.5天')
  })

  it('无限制造行动：按所有消耗材料最小可支持次数与加速单轮耗时计算，并支持实时扣减', () => {
    const entry = {
      entryId: 'q1',
      actionId: 'alchemy.yinqi-dan',
      type: 'alchemy',
      executionKind: 'timed',
      progressSeconds: 0,
      durationSeconds: 52.2,
      remainingRepeats: -1,
      status: 'running',
    }
    const action = {
      id: 'alchemy.yinqi-dan',
      name: '引气丹',
      type: 'alchemy',
      durationSeconds: 60,
      inputs: {
        'item.lingcao': 2,
        'item.qinglinghua': 1,
      },
    }
    const inventory = [
      { itemId: 'item.lingcao', amount: 1000, name: '灵草', subCategory: 'material' },
      { itemId: 'item.qinglinghua', amount: 400, name: '青灵花', subCategory: 'material' },
    ]

    const estimate = calculateQueueActionEstimate({ entry, action, inventory })
    assert.equal(estimate.hasEstimate, true)
    assert.equal(estimate.effectiveCount, 400)
    assert.equal(estimate.showEffectiveCount, true)
    assert.equal(formatQueueRepeatsText(estimate, resolveLiveRemainingSeconds(estimate, 0), t), '∞ (x400 5.8小时)')

    // 实时扣减验证（短耗时精确到秒）
    const shortEstimate = calculateQueueActionEstimate({
      entry: { ...entry, durationSeconds: 20, progressSeconds: 5 },
      action,
      inventory: [
        { itemId: 'item.lingcao', amount: 6, name: '灵草', subCategory: 'material' },
        { itemId: 'item.qinglinghua', amount: 3, name: '青灵花', subCategory: 'material' },
      ],
    })
    assert.equal(shortEstimate.effectiveCount, 3)
    // 快照基准剩余：(20 - 5) + 2 * 20 = 55s；流逝 3s 后剩余 52s
    assert.equal(resolveLiveRemainingSeconds(shortEstimate, 0), 55)
    assert.equal(formatQueueRepeatsText(shortEstimate, resolveLiveRemainingSeconds(shortEstimate, 3), t), '∞ (x3 52秒)')
  })

  it('无限制造材料不足 1 次时显示 x0 且不展示虚假耗时', () => {
    const estimate = calculateQueueActionEstimate({
      entry: {
        entryId: 'q1',
        actionId: 'alchemy.yinqi-dan',
        type: 'alchemy',
        executionKind: 'timed',
        progressSeconds: 0,
        durationSeconds: 30,
        remainingRepeats: -1,
        status: 'blocked',
      },
      action: {
        id: 'alchemy.yinqi-dan',
        name: '引气丹',
        type: 'alchemy',
        durationSeconds: 30,
        inputs: { 'item.lingcao': 2 },
      },
      inventory: [{ itemId: 'item.lingcao', amount: 1, name: '灵草', subCategory: 'material' }],
    })
    assert.equal(estimate.hasEstimate, true)
    assert.equal(estimate.effectiveCount, 0)
    assert.equal(formatQueueRepeatsText(estimate, 0, t), '∞ (x0)')
  })

  it('有限制造行动：材料充足直接显总耗时，材料不足按可用次数截断并显 xN', () => {
    const baseEntry = {
      entryId: 'q1',
      actionId: 'alchemy.yinqi-dan',
      type: 'alchemy',
      executionKind: 'timed',
      progressSeconds: 0,
      durationSeconds: 52.2,
      remainingRepeats: 500,
      status: 'running',
    }
    const action = {
      id: 'alchemy.yinqi-dan',
      name: '引气丹',
      type: 'alchemy',
      durationSeconds: 60,
      inputs: { 'item.lingcao': 2 },
    }

    const capped = calculateQueueActionEstimate({
      entry: baseEntry,
      action,
      inventory: [{ itemId: 'item.lingcao', amount: 800, name: '灵草', subCategory: 'material' }],
    })
    assert.equal(capped.effectiveCount, 400)
    assert.equal(capped.showEffectiveCount, true)
    assert.equal(formatQueueRepeatsText(capped, capped.baseRemainingSeconds, t), '500次 (x400 5.8小时)')

    const zeroCapped = calculateQueueActionEstimate({
      entry: baseEntry,
      action,
      inventory: [{ itemId: 'item.lingcao', amount: 1, name: '灵草', subCategory: 'material' }],
    })
    assert.equal(formatQueueRepeatsText(zeroCapped, 0, t), '500次 (x0)')

    const full = calculateQueueActionEstimate({
      entry: { ...baseEntry, remainingRepeats: 400 },
      action,
      inventory: [{ itemId: 'item.lingcao', amount: 2000, name: '灵草', subCategory: 'material' }],
    })
    assert.equal(full.effectiveCount, 400)
    assert.equal(full.showEffectiveCount, false)
    assert.equal(formatQueueRepeatsText(full, full.baseRemainingSeconds, t), '400次 (5.8小时)')
  })

  it('采集与修炼：有限次显示总耗时，无限次仅显 ∞ 且不依附背包订阅', () => {
    const gatheringAction = { id: 'gather.herb', name: '采药', type: 'exploration', durationSeconds: 15 }
    const gatheringInf = {
      entryId: 'g1',
      actionId: 'gather.herb',
      type: 'exploration',
      executionKind: 'timed',
      progressSeconds: 4,
      durationSeconds: 15,
      remainingRepeats: -1,
      status: 'running',
    }
    assert.deepEqual(getQueueEstimateDependencies(gatheringInf, gatheringAction), {
      needsInventory: false,
      needsFoundation: false,
      isExcluded: false,
    })
    const estGatherInf = calculateQueueActionEstimate({ entry: gatheringInf, action: gatheringAction, inventory: [] })
    assert.equal(estGatherInf.hasEstimate, false)
    assert.equal(formatQueueRepeatsText(estGatherInf, 0, t), '∞')

    const gatheringFin = calculateQueueActionEstimate({
      entry: { ...gatheringInf, remainingRepeats: 10, progressSeconds: 5 },
      action: gatheringAction,
      inventory: [],
    })
    assert.equal(gatheringFin.hasEstimate, true)
    // (15 - 5) + 9 * 15 = 145s = 2分25秒
    assert.equal(formatQueueRepeatsText(gatheringFin, gatheringFin.baseRemainingSeconds, t), '10次 (2分25秒)')

    const cultivationAction = { id: 'cultivate.tuna', name: '吐纳', type: 'cultivation', durationSeconds: 12 }
    const cultivationInf = {
      entryId: 'c1',
      actionId: 'cultivate.tuna',
      type: 'cultivation',
      executionKind: 'timed',
      progressSeconds: 2,
      durationSeconds: 12,
      remainingRepeats: -1,
      parameters: { spiritStone: 1 },
      status: 'running',
    }
    const estCultInf = calculateQueueActionEstimate({ entry: cultivationInf, action: cultivationAction, inventory: [] })
    assert.equal(estCultInf.hasEstimate, false)
    assert.equal(formatQueueRepeatsText(estCultInf, 0, t), '∞')

    const estCultFin = calculateQueueActionEstimate({
      entry: { ...cultivationInf, remainingRepeats: 5 },
      action: cultivationAction,
      inventory: [],
    })
    // (12 - 2) + 4 * 12 = 58s
    assert.equal(formatQueueRepeatsText(estCultFin, estCultFin.baseRemainingSeconds, t), '5次 (58秒)')
  })

  it('秘法与逆命丹底蕴：按目标强化变体数量（扣已穿戴占用）及可用底蕴估算', () => {
    const secretEntry = {
      entryId: 's1',
      actionId: 'secret_art.disassemble',
      type: 'enhancement',
      executionKind: 'timed',
      targetItemId: 'equipment.iron-sword',
      enhancementCurrentLevel: 3,
      progressSeconds: 0,
      durationSeconds: 6,
      remainingRepeats: -1,
      status: 'running',
    }
    const estSecret = calculateQueueActionEstimate({
      entry: secretEntry,
      inventory: [
        { itemId: 'equipment.iron-sword', variant: null, amount: 5, name: '铁剑', subCategory: 'weapon' },
        { itemId: 'equipment.iron-sword', variant: '+3', amount: 4, name: '铁剑', subCategory: 'weapon' },
      ],
      combatEquipment: [{ slot: 'weapon', itemId: 'equipment.iron-sword', enhancementLevel: 3 }],
      queueEntries: [secretEntry],
      equipmentCatalog: [{ equipmentId: 'equipment.iron-sword' }],
    })
    // 4 件 +3 中 1 件已装备 -> 可分解 3 件，共 18 秒
    assert.equal(estSecret.effectiveCount, 3)
    assert.equal(formatQueueRepeatsText(estSecret, estSecret.baseRemainingSeconds, t), '∞ (x3 18秒)')

    const pillAction = {
      id: 'alchemy.refine-reverse-fate-pill',
      name: '逆命丹',
      type: 'alchemy',
      durationSeconds: 30,
      inputs: {},
      foundationCostRatio: 0.25,
    }
    const pillEntry = {
      entryId: 'p1',
      actionId: pillAction.id,
      type: 'alchemy',
      executionKind: 'timed',
      progressSeconds: 0,
      durationSeconds: 30,
      remainingRepeats: -1,
      status: 'running',
    }
    assert.deepEqual(getQueueEstimateDependencies(pillEntry, pillAction), {
      needsInventory: false,
      needsFoundation: true,
      isExcluded: false,
    })
    const estPill = calculateQueueActionEstimate({
      entry: pillEntry,
      action: pillAction,
      inventory: [],
      stats: { realmLevel: 1, experience: 100, expToNextLinggen: 200, foundation: 220 },
    })
    // 200 * 0.25 = 50/次；220 底蕴 -> 4次 * 30s = 120s = 2分
    assert.equal(estPill.effectiveCount, 4)
    assert.equal(formatQueueRepeatsText(estPill, estPill.baseRemainingSeconds, t), '∞ (x4 2分)')
  })

  it('装备强化：无限次仅按当前材料（灵石+触发保护材料）算最大次数，不预测成败变化', () => {
    // 1. 当前 +1 -> +2，单次灵石 20，无保护
    const enhanceNoProtect = {
      entryId: 'e1',
      actionId: 'enhance.equipment',
      type: 'enhancement',
      executionKind: 'timed',
      targetItemId: 'equipment.iron-sword',
      enhancementCurrentLevel: 1,
      progressSeconds: 1,
      durationSeconds: 7,
      remainingRepeats: -1,
      status: 'running',
    }
    const est1 = calculateQueueActionEstimate({
      entry: enhanceNoProtect,
      inventory: [{ itemId: 'currency.spirit-stone', amount: 215, name: '灵石', subCategory: 'currency' }],
      queueEntries: [enhanceNoProtect],
      equipmentCatalog: [{ equipmentId: 'equipment.iron-sword' }],
    })
    assert.equal(est1.effectiveCount, 10)
    // (7 - 1) + 9 * 7 = 69s = 1分9秒
    assert.equal(formatQueueRepeatsText(est1, est1.baseRemainingSeconds, t), '∞ (x10 1分9秒)')

    // 2. 当前 +4 -> +5 且起护 2 级已激活，保护材料成瓶颈
    const enhanceProtected = {
      ...enhanceNoProtect,
      enhancementCurrentLevel: 4,
      auxItemId: 'item.protect-stone',
      parameters: { protectLevel: 2 },
      progressSeconds: 0,
      durationSeconds: 10,
    }
    const est2 = calculateQueueActionEstimate({
      entry: enhanceProtected,
      inventory: [
        { itemId: 'currency.spirit-stone', amount: 5000, name: '灵石', subCategory: 'currency' },
        { itemId: 'item.protect-stone', amount: 6, name: '护灵石', subCategory: 'material' },
      ],
      queueEntries: [enhanceProtected],
    })
    assert.equal(est2.effectiveCount, 6)
    assert.equal(formatQueueRepeatsText(est2, est2.baseRemainingSeconds, t), '∞ (x6 1分)')

    // 3. 当前 +0 未达起护等级 2 时仅受灵石限制
    const belowProtectLevel = {
      ...enhanceNoProtect,
      enhancementCurrentLevel: 0,
      auxItemId: 'equipment.iron-sword',
      parameters: { protectLevel: 2 },
      progressSeconds: 0,
      durationSeconds: 5,
    }
    const estBelowProtect = calculateQueueActionEstimate({
      entry: belowProtectLevel,
      inventory: [
        { itemId: 'currency.spirit-stone', amount: 500, name: '灵石', subCategory: 'currency' },
        { itemId: 'equipment.iron-sword', variant: null, amount: 3, name: '铁剑', subCategory: 'weapon' },
      ],
      queueEntries: [belowProtectLevel],
      equipmentCatalog: [{ equipmentId: 'equipment.iron-sword' }],
    })
    assert.equal(estBelowProtect.effectiveCount, 50)

    // 4. 当前 +0 缺省起护参数（服务端默认为 0 即起护）用同类基础件自保护：锁定 1 件本体，仅余 2 件代护件
    const selfProtect = {
      ...enhanceNoProtect,
      enhancementCurrentLevel: 0,
      auxItemId: 'equipment.iron-sword',
      progressSeconds: 0,
      durationSeconds: 5,
    }
    const estSelf = calculateQueueActionEstimate({
      entry: selfProtect,
      inventory: [
        { itemId: 'currency.spirit-stone', amount: 500, name: '灵石', subCategory: 'currency' },
        { itemId: 'equipment.iron-sword', variant: null, amount: 3, name: '铁剑', subCategory: 'weapon' },
      ],
      queueEntries: [selfProtect],
      equipmentCatalog: [{ equipmentId: 'equipment.iron-sword' }],
    })
    assert.equal(estSelf.effectiveCount, 2)
    assert.equal(formatQueueRepeatsText(estSelf, estSelf.baseRemainingSeconds, t), '∞ (x2 10秒)')
  })

  it('战斗与赶路行动：不附加耗时或材料次数估算', () => {
    const combatEntry = {
      entryId: 'cb1',
      actionId: 'combat.hunt',
      type: 'combat',
      executionKind: 'combat-encounter',
      progressSeconds: 3,
      durationSeconds: 20,
      remainingRepeats: -1,
      status: 'running',
    }
    const estCombatInf = calculateQueueActionEstimate({ entry: combatEntry, inventory: [] })
    assert.equal(estCombatInf.hasEstimate, false)
    assert.equal(formatQueueRepeatsText(estCombatInf, 0, t), '∞')

    const estCombatFin = calculateQueueActionEstimate({ entry: { ...combatEntry, remainingRepeats: 15 }, inventory: [] })
    assert.equal(estCombatFin.hasEstimate, false)
    assert.equal(formatQueueRepeatsText(estCombatFin, 0, t), '15次')

    const travelEntry = {
      entryId: 'tr1',
      actionId: 'gather.herb',
      type: 'exploration',
      executionKind: 'travel',
      progressSeconds: 2,
      durationSeconds: 12,
      remainingRepeats: 1,
      status: 'running',
    }
    const estTravel = calculateQueueActionEstimate({ entry: travelEntry, inventory: [] })
    assert.equal(estTravel.hasEstimate, false)
    assert.equal(formatQueueRepeatsText(estTravel, 0, t), '1次')
  })
})
