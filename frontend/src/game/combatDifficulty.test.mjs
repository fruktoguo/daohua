import test from 'node:test'
import assert from 'node:assert/strict'
import {
  COMBAT_DIFFICULTY_TIERS,
  DIFFICULTY_BRACKET_DEFAULT,
  DIFFICULTY_BRACKET_T1_TO_T3,
  DIFFICULTY_BRACKET_T4_TO_T5,
  clampDifficultyTier,
  getDifficultyBracket,
  getDifficultyAttributeMultiplier,
  getDifficultyExperienceMultiplier,
  getDifficultyDropRateMultiplier,
  getDifficultyDropQuantityMultiplier,
  scaleMonsterAttributes,
  scaleMonsterDrops,
} from './combatDifficulty.ts'

test('combatDifficulty: 难度等级与匹配区间映射自洽', () => {
  assert.equal(COMBAT_DIFFICULTY_TIERS.length, 6)
  assert.deepEqual([...COMBAT_DIFFICULTY_TIERS], [0, 1, 2, 3, 4, 5])

  // 默认档区间
  assert.equal(getDifficultyBracket(0), DIFFICULTY_BRACKET_DEFAULT)
  assert.equal(getDifficultyBracket(-1), DIFFICULTY_BRACKET_DEFAULT)

  // T1~T3 区间
  assert.equal(getDifficultyBracket(1), DIFFICULTY_BRACKET_T1_TO_T3)
  assert.equal(getDifficultyBracket(2), DIFFICULTY_BRACKET_T1_TO_T3)
  assert.equal(getDifficultyBracket(3), DIFFICULTY_BRACKET_T1_TO_T3)

  // T4~T5 区间
  assert.equal(getDifficultyBracket(4), DIFFICULTY_BRACKET_T4_TO_T5)
  assert.equal(getDifficultyBracket(5), DIFFICULTY_BRACKET_T4_TO_T5)
  assert.equal(getDifficultyBracket(6), DIFFICULTY_BRACKET_T4_TO_T5)
})

test('combatDifficulty: 收益线性增长与属性指数增长算法精度', () => {
  // 默认难度倍率恒为 1.0
  assert.equal(getDifficultyAttributeMultiplier(0), 1.0)
  assert.equal(getDifficultyExperienceMultiplier(0), 1.0)
  assert.equal(getDifficultyDropRateMultiplier(0), 1.0)
  assert.equal(getDifficultyDropQuantityMultiplier(0), 1.0)

  // T1 难度
  assert.equal(getDifficultyAttributeMultiplier(1), 1.25)
  assert.equal(getDifficultyExperienceMultiplier(1), 1.6)
  assert.equal(getDifficultyDropRateMultiplier(1), 1.25)
  assert.equal(getDifficultyDropQuantityMultiplier(1), 1.25)

  // T5 难度
  // 属性指数提升：1.25^5 = 3.0517578125
  assert.ok(Math.abs(getDifficultyAttributeMultiplier(5) - Math.pow(1.25, 5)) < 1e-9)
  // 经验线性提升：1 + 0.60 * 5 = 4.0 (提升 300%)
  assert.equal(getDifficultyExperienceMultiplier(5), 4.0)
  // 掉落线性提升：1 + 0.25 * 5 = 2.25 (提升 125%)
  assert.equal(getDifficultyDropRateMultiplier(5), 2.25)
  assert.equal(getDifficultyDropQuantityMultiplier(5), 2.25)
})

test('combatDifficulty: 怪物属性缩放恒向上取整 (Math.ceil)', () => {
  const baseAttrs = {
    maxHitpoints: 100,
    maxManapoints: 50,
    physicalAttack: 15,
    spellAttack: 13,
    armor: 9,
    magicResistance: 7,
    attackIntervalMilliseconds: 1500,
  }

  // T1 缩放 (1.25x)
  // hp: 100 * 1.25 = 125
  // atk: 15 * 1.25 = 18.75 -> ceil 19
  // spell: 13 * 1.25 = 16.25 -> ceil 17
  // armor: 9 * 1.25 = 11.25 -> ceil 12
  // res: 7 * 1.25 = 8.75 -> ceil 9
  const t1Attrs = scaleMonsterAttributes(baseAttrs, 1)
  assert.equal(t1Attrs.maxHitpoints, 125)
  assert.equal(t1Attrs.physicalAttack, 19)
  assert.equal(t1Attrs.spellAttack, 17)
  assert.equal(t1Attrs.armor, 12)
  assert.equal(t1Attrs.magicResistance, 9)
  // 攻速毫秒数保持不变
  assert.equal(t1Attrs.attackIntervalMilliseconds, 1500)
})

test('combatDifficulty: 怪物掉落根据概率档位自适应提升概率或保底数量', () => {
  const baseDrops = [
    { itemId: 'item.skin', dropChance: 0.2, minQuantity: 1, maxQuantity: 1 },
    { itemId: 'item.guaranteed-ore', dropChance: 1.0, minQuantity: 2, maxQuantity: 4 },
  ]

  // T5 缩放 (倍率 2.25x)
  const t5Drops = scaleMonsterDrops(baseDrops, 5)

  // 概率提升：0.2 * 2.25 = 0.45
  assert.equal(t5Drops[0].dropChance, 0.45)
  assert.equal(t5Drops[0].minQuantity, 1)
  assert.equal(t5Drops[0].maxQuantity, 1)

  // 100% 保底条目数量提升：
  // min: ceil(2 * 2.25) = ceil(4.5) = 5
  // max: ceil(4 * 2.25) = ceil(9.0) = 9
  assert.equal(t5Drops[1].dropChance, 1.0)
  assert.equal(t5Drops[1].minQuantity, 5)
  assert.equal(t5Drops[1].maxQuantity, 9)
})
