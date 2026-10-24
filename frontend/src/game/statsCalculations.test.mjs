import test from 'node:test'
import assert from 'node:assert/strict'
import { calculateBriefStats } from './statsCalculations.ts'

function createMockReport(overrides = {}) {
  return {
    reportId: 'test-report-1',
    actionId: 'combat.hunt-qi-beast',
    actionName: '讨伐 青云仙山',
    startedAtUnixSeconds: 1000,
    endedAtUnixSeconds: 0,
    experience: 3600, // 3600 修为
    foundation: 0,
    combatExperience: 1800, // 1800 战斗经验
    rootExp: { stamina: 100, meridians: 200, attack: 300, bone: 0, agility: 0, consciousness: 0 },
    abilityExp: [],
    techniqueExp: [],
    levelUps: [],
    items: [
      { itemId: 'item.ningxue-pill', quantity: 30 },
      { itemId: 'item.beast-fang', quantity: 60 },
    ],
    itemsLost: [],
    cycles: 0,
    encounters: 120, // 120 场
    normalKills: 100,
    eliteKills: 20,
    bossKills: 0,
    deaths: 2,
    duelWins: 0,
    duelLosses: 0,
    ...overrides,
  }
}

test('statsCalculations: 战斗行动1小时流逝速率与日收益估值准确换算', () => {
  const report = createMockReport()
  const nowSeconds = 4600 // 刚好 3600 秒（1 小时）

  const mockCatalogs = {
    items: [
      { itemId: 'item.ningxue-pill', name: '凝血丹', sellPrice: 20 },
      { itemId: 'item.beast-fang', name: '兽牙', sellPrice: 5 },
    ],
    marketStats: {},
    equipments: [],
  }

  const result = calculateBriefStats(report, nowSeconds, mockCatalogs)

  // 1. 时长与模式
  assert.equal(result.durationSeconds, 3600)
  assert.equal(result.hours, 1)
  assert.equal(result.isCombat, true)

  // 2. 战斗与击杀频次
  assert.equal(result.encountersPerHour, 120) // 120 场/小时
  assert.equal(result.killsPerHour, 120) // 100 + 20 = 120 击杀/小时
  assert.equal(result.deathsPerHour, 2) // 2 阵亡/小时

  // 3. 修为与经验
  assert.equal(result.cultivationPerHour, 3600) // 3600 修为/小时
  assert.equal(result.dailyCultivation, 3600 * 24) // 86400 /天
  assert.equal(result.combatExpPerHour, 1800) // 1800 经验/小时
  assert.equal(result.dailyCombatExp, 1800 * 24) // 43200 /天
  assert.equal(result.rootExpPerHour, 600) // 六维总和 600 /小时

  // 4. 掉落物收益折算
  // ningxue-pill: 30 * 20 = 600 灵石
  // beast-fang: 60 * 5 = 300 灵石
  // 总价值 900 灵石/小时 -> 900 * 24 = 21600 灵石/天
  assert.equal(result.revenuePerHour, 900)
  assert.equal(result.dailyRevenue, 21600)

  // 产出明细排序：ningxue-pill 日价值 600*24=14400 居首
  assert.equal(result.topYields.length, 2)
  assert.equal(result.topYields[0].itemId, 'item.ningxue-pill')
  assert.equal(result.topYields[0].dailyQty, 30 * 24)
  assert.equal(result.topYields[0].dailyVal, 14400)
  assert.equal(result.topYields[1].itemId, 'item.beast-fang')
  assert.equal(result.topYields[1].dailyQty, 60 * 24)
  assert.equal(result.topYields[1].dailyVal, 7200)
})

test('statsCalculations: 非战斗生活行动轮次效率换算', () => {
  const report = createMockReport({
    actionId: 'alchemy.refine-pill',
    actionName: '炼制 凝血丹',
    encounters: 0,
    normalKills: 0,
    eliteKills: 0,
    bossKills: 0,
    deaths: 0,
    cycles: 60,
    experience: 7200,
    combatExperience: 0,
    items: [{ itemId: 'item.ningxue-pill', quantity: 60 }],
  })
  const nowSeconds = 2800 // 1800 秒（0.5 小时）

  const mockCatalogs = {
    items: [{ itemId: 'item.ningxue-pill', name: '凝血丹', sellPrice: 20 }],
    marketStats: {},
    equipments: [],
  }

  const result = calculateBriefStats(report, nowSeconds, mockCatalogs)

  assert.equal(result.durationSeconds, 1800)
  assert.equal(result.hours, 0.5)
  assert.equal(result.isCombat, false)
  assert.equal(result.cyclesPerHour, 120) // 60 / 0.5 = 120 轮/小时
  assert.equal(result.cultivationPerHour, 14400) // 7200 / 0.5 = 14400 /小时
  assert.equal(result.combatExpPerHour, 0)
  // 物品 60 * 20 = 1200 灵石在半小时内 -> 2400 /小时 -> 57600 /天
  assert.equal(result.dailyRevenue, 57600)
})

test('statsCalculations: 0秒或极短流逝时长安全保护', () => {
  const report = createMockReport({
    startedAtUnixSeconds: 1000,
    endedAtUnixSeconds: 1000, // 0 秒
    experience: 0,
    combatExperience: 0,
    items: [],
    encounters: 0,
    cycles: 0,
  })

  const result = calculateBriefStats(report, 1000)

  assert.equal(result.durationSeconds, 0)
  assert.equal(result.hours, 0)
  assert.equal(result.dailyRevenue, 0)
  assert.equal(result.encountersPerHour, 0)
  assert.equal(result.cultivationPerHour, 0)
  assert.equal(result.combatExpPerHour, 0)
  assert.equal(result.topYields.length, 0)
})
