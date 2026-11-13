import test from 'node:test'
import assert from 'node:assert/strict'
import { aggregateNodeBuildingEffects, weakenMonsterAttributes, resolveEquipmentAttributeDelta } from './buildingCalculations.ts'

const definitions = [
  {
    buildingId: 'building.seal-array',
    name: '封魔阵',
    group: 'combat',
    baseModifiers: { monsterWeakenRatio: 0.06, combatHitpointRegen: 2.4, combatManapointRegen: 2.4, combatDamageReductionRatio: 0.02 },
    growthModifiers: { monsterWeakenRatio: 0.01, combatHitpointRegen: 0.4, combatManapointRegen: 0.4, combatDamageReductionRatio: 0.005 },
  },
  {
    buildingId: 'building.forge',
    name: '锻造坊',
    group: 'production',
    baseModifiers: { forgingSpeedRatio: 0.1, combatHitpointRegen: 1 },
    growthModifiers: { forgingSpeedRatio: 0.05, combatHitpointRegen: 0.5 },
  },
]

test('aggregateNodeBuildingEffects：按等级线性成长聚合，多建筑求和', () => {
  const effects = aggregateNodeBuildingEffects([
    { buildingId: 'building.seal-array', level: 10, targetLevel: 0 },
    { buildingId: 'building.forge', level: 3, targetLevel: 0 },
  ], definitions)

  // 封魔阵 Lv10：0.06 + 0.01×9 = 0.15；锻造坊 Lv3：1 + 0.5×2 = 2 → 合计 8
  assert.ok(Math.abs((effects.monsterWeakenRatio ?? 0) - 0.15) < 1e-9)
  assert.ok(Math.abs((effects.combatHitpointRegen ?? 0) - 8) < 1e-9)
  assert.ok(Math.abs((effects.combatManapointRegen ?? 0) - 6) < 1e-9)
  // 非战斗类键不纳入战斗效果集（锻造加速不影响战斗推演）
  assert.equal(effects.forgingSpeedRatio, undefined)
})

test('aggregateNodeBuildingEffects：未建成等级与未知建筑一律不计入', () => {
  const effects = aggregateNodeBuildingEffects([
    { buildingId: 'building.seal-array', level: 0, targetLevel: 5 },
    { buildingId: 'building.unknown', level: 20, targetLevel: 0 },
  ], definitions)

  assert.equal(effects.monsterWeakenRatio, 0)
  assert.equal(effects.combatHitpointRegen, 0)
})

test('weakenMonsterAttributes：双曲阻尼折减气血与双攻双防，灵力上限不受影响', () => {
  const monster = { maxHitpoints: 1000, maxManapoints: 500, physicalAttack: 100, spellAttack: 80, armor: 50, magicResistance: 40 }
  const weakened = weakenMonsterAttributes(monster, 0.15)

  assert.equal(weakened.maxHitpoints, Math.ceil(1000 / 1.15))
  assert.equal(weakened.physicalAttack, Math.ceil(100 / 1.15))
  assert.equal(weakened.spellAttack, Math.ceil(80 / 1.15))
  assert.equal(weakened.armor, Math.ceil(50 / 1.15))
  assert.equal(weakened.magicResistance, Math.ceil(40 / 1.15))
  // 灵力上限不受削弱影响
  assert.equal(weakened.maxManapoints ?? 500, 500)
})

test('weakenMonsterAttributes：比率 ≤ 0 时原样透传', () => {
  const monster = { maxHitpoints: 1000, physicalAttack: 100, spellAttack: 80, armor: 50, magicResistance: 40 }
  assert.deepEqual(weakenMonsterAttributes(monster, 0), monster)
  assert.deepEqual(weakenMonsterAttributes(monster, -0.5), monster)
})

test('resolveEquipmentAttributeDelta：强化按 1.05^等级 缩放，比率键映射到面板字段', () => {
  const delta = resolveEquipmentAttributeDelta(
    [{ itemId: 'eq.sword', enhancementLevel: 20 }],
    [{ equipmentId: 'eq.sword', flatStats: { physicalAttack: 100, breakPower: 14, parryResolve: 8, physicalDamageReductionPoints: 10 }, baseStatRatios: { physicalArmor: 0.1 } }]
  )

  // 1.05^20 ≈ 2.6533
  const scale = Math.pow(1.05, 20)
  assert.ok(Math.abs((delta.physicalAttack ?? 0) - 100 * scale) < 1e-6)
  assert.ok(Math.abs((delta.breakPower ?? 0) - 14 * scale) < 1e-6)
  assert.ok(Math.abs((delta.parryResolve ?? 0) - 8 * scale) < 1e-6)
  assert.ok(Math.abs((delta.physicalDamageReductionPoints ?? 0) - 10 * scale) < 1e-6)
  // baseStatRatios 的 physicalArmor 映射到面板 armor 字段（旧实现在此处静默丢弃）
  assert.ok(Math.abs((delta.armor ?? 0) - 0.1 * scale) < 1e-6)
})

test('resolveEquipmentAttributeDelta：未映射键不得写入面板字段', () => {
  const delta = resolveEquipmentAttributeDelta(
    [{ itemId: 'eq.odd', enhancementLevel: 0 }],
    [{ equipmentId: 'eq.odd', flatStats: { unknownStat: 999 }, baseStatRatios: { searchSpeedPoints: 0.5 } }]
  )
  assert.equal(Object.keys(delta).length, 0)
})
