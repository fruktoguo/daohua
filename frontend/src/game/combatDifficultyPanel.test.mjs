import test from 'node:test'
import assert from 'node:assert/strict'
import {
  scaleMonsterAttributes,
  scaleMonsterDrops,
  getDifficultyExperienceMultiplier,
  getDifficultyBracket,
  DIFFICULTY_BRACKET_DEFAULT,
  DIFFICULTY_BRACKET_T1_TO_T3,
  DIFFICULTY_BRACKET_T4_TO_T5,
} from './combatDifficulty.ts'

test('生态面板难度联动：从默认到 T5 怪物属性、经验与掉落完全按规则计算', () => {
  const baseMonster = {
    name: '青臂猿',
    level: 8,
    experience: 25,
    attributes: {
      maxHitpoints: 320,
      maxManapoints: 0,
      physicalAttack: 36,
      spellAttack: 0,
      armor: 18,
      magicResistance: 12,
      attackIntervalMilliseconds: 1400,
    },
    drops: [
      { itemId: 'item.ape-bone', dropChance: 0.15, minQuantity: 1, maxQuantity: 1 },
      { itemId: 'item.beast-meat', dropChance: 1.0, minQuantity: 1, maxQuantity: 2 },
    ],
  }

  // 1. 默认档 (T0)
  const t0Attrs = scaleMonsterAttributes(baseMonster.attributes, 0)
  const t0Exp = Math.round(baseMonster.experience * getDifficultyExperienceMultiplier(0))
  const t0Drops = scaleMonsterDrops(baseMonster.drops, 0)
  assert.equal(t0Attrs.maxHitpoints, 320)
  assert.equal(t0Attrs.physicalAttack, 36)
  assert.equal(t0Exp, 25)
  assert.equal(t0Drops[0].dropChance, 0.15)
  assert.equal(t0Drops[1].minQuantity, 1)
  assert.equal(t0Drops[1].maxQuantity, 2)

  // 2. T1 档 (1.25^1 属性, 1.6x 经验, 1.25x 掉落)
  const t1Attrs = scaleMonsterAttributes(baseMonster.attributes, 1)
  const t1Exp = Math.round(baseMonster.experience * getDifficultyExperienceMultiplier(1))
  const t1Drops = scaleMonsterDrops(baseMonster.drops, 1)
  assert.equal(t1Attrs.maxHitpoints, 400) // 320 * 1.25 = 400
  assert.equal(t1Attrs.physicalAttack, 45) // 36 * 1.25 = 45
  assert.equal(t1Attrs.armor, 23) // 18 * 1.25 = 22.5 -> ceil 23
  assert.equal(t1Attrs.magicResistance, 15) // 12 * 1.25 = 15
  assert.equal(t1Exp, 40) // 25 * 1.6 = 40
  assert.equal(t1Drops[0].dropChance, 0.15 * 1.25) // 0.1875
  assert.equal(t1Drops[1].minQuantity, Math.ceil(1 * 1.25)) // 2
  assert.equal(t1Drops[1].maxQuantity, Math.ceil(2 * 1.25)) // 3

  // 3. T5 档 (1.25^5 属性 ≈ 3.0518, 4.0x 经验, 2.25x 掉落)
  const t5Attrs = scaleMonsterAttributes(baseMonster.attributes, 5)
  const t5Exp = Math.round(baseMonster.experience * getDifficultyExperienceMultiplier(5))
  const t5Drops = scaleMonsterDrops(baseMonster.drops, 5)
  assert.equal(t5Attrs.maxHitpoints, Math.ceil(320 * Math.pow(1.25, 5))) // 977
  assert.equal(t5Attrs.physicalAttack, Math.ceil(36 * Math.pow(1.25, 5))) // 110
  assert.equal(t5Attrs.armor, Math.ceil(18 * Math.pow(1.25, 5))) // 55
  assert.equal(t5Attrs.magicResistance, Math.ceil(12 * Math.pow(1.25, 5))) // 37
  assert.equal(t5Exp, 100) // 25 * 4.0 = 100
  assert.equal(t5Drops[0].dropChance, 0.15 * 2.25) // 0.3375
  assert.equal(t5Drops[1].minQuantity, Math.ceil(1 * 2.25)) // 3
  assert.equal(t5Drops[1].maxQuantity, Math.ceil(2 * 2.25)) // 5
})

test('斗法三区间队伍过滤联动：各难度正确定位对应区间的队伍数', () => {
  const nodeDuelCombatTeamsByBracket = [12, 35, 7]

  // 默认 (0) -> bracket 0 -> 12
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(0)], 12)
  // T1 ~ T3 -> bracket 1 -> 35
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(1)], 35)
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(2)], 35)
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(3)], 35)
  // T4 ~ T5 -> bracket 2 -> 7
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(4)], 7)
  assert.equal(nodeDuelCombatTeamsByBracket[getDifficultyBracket(5)], 7)
})

test('怪物掉落条目：通用稀有储物袋微小掉率自适应格式化与难度缩放', () => {
  function formatDropChance(chance) {
    const pct = chance * 100
    return `${+(pct >= 0.1 ? pct.toFixed(2) : pct.toFixed(4))}%`
  }

  // 1. 基线微小掉率格式化精度验证
  assert.equal(formatDropChance(0.0003), '0.03%') // 1 级普通怪基线 0.03%
  assert.equal(formatDropChance(0.000315), '0.0315%') // 2 级普通怪 0.03% * 1.05
  assert.equal(formatDropChance(0.0015), '0.15%') // 1 级精英怪 0.03% * 5
  assert.equal(formatDropChance(0.006), '0.6%') // 1 级首领怪 0.03% * 20
  assert.equal(formatDropChance(1.0), '100%') // 100% 保底

  // 2. 难度缩放联动
  const drops = [{ itemId: 'item.storage-bag.broken', dropChance: 0.0003, minQuantity: 1, maxQuantity: 1 }]
  const t1Drops = scaleMonsterDrops(drops, 1)
  // T1 难度 dropRateMultiplier = 1.25 -> 0.0003 * 1.25 = 0.000375 -> 0.0375%
  assert.ok(Math.abs(t1Drops[0].dropChance - 0.000375) < 1e-9)
  assert.equal(formatDropChance(t1Drops[0].dropChance), '0.0375%')
})
