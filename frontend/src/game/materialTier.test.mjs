import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'

// 材料品阶体系不变量（总纲 §6.7 / §4.6.3）：凡品=采集直出与高爆率战利品，稀有=低概率掉落，
// 稀有度随产出源（怪物等阶）逐阶抬升；常规物品与装备制造只允许消耗凡品材料。
// 这些断言保护的是「配表改一个 tier 就静默破坏制造经济与关卡」这一类回归：
// 单看某一条材料配置无法发现，必须跨 items / monsters / actions 三域联合校验。

const DATA_ROOT = path.resolve(process.cwd(), 'config/game-data')
const readJson = (rel) => JSON.parse(fs.readFileSync(path.join(DATA_ROOT, rel), 'utf8'))
const readDir = (rel) => fs.readdirSync(path.join(DATA_ROOT, rel)).filter(f => f.endsWith('.json'))

const materials = readJson('items/materials.json')
const materialById = new Map(materials.map(it => [it.id, it]))
const monsters = readDir('monsters').flatMap(f => readJson(`monsters/${f}`))
const actions = readDir('actions').flatMap(f => readJson(`actions/${f}`))

/** 与前端 convertLevelToTier 同口径（1 级→T1，18 级→T2，每 12 级一阶） */
const levelToTier = (level) => (level <= 1 ? 1 : Math.min(8, Math.floor((level - 18) / 12) + 2))

/** 非材料的秘籍/残卷按独立爆率体系投放，不参与材料品阶规则 */
const isGradeMaterial = (itemId) =>
  materialById.has(itemId) && materialById.get(itemId).category === 'material'

const gatherYields = new Set()
const manufacturingInputs = new Set()
for (const action of actions) {
  for (const y of action.yields ?? []) {
    if (y.kind === 'item' && y.id && isGradeMaterial(y.id)) gatherYields.add(y.id)
  }
  // 制造 = 生活制造大类的 alchemy/forging/talisman/formation 行动，收集其全部材料消耗
  if (action.type === 'alchemy' || action.type === 'enhancement') {
    for (const itemId of Object.keys(action.inputs ?? {})) manufacturingInputs.add(itemId)
  }
}

/** itemId → 掉落源列表（含怪物等阶） */
const dropSources = new Map()
for (const monster of monsters) {
  for (const drop of monster.dropTable ?? []) {
    if (!isGradeMaterial(drop.itemId)) continue
    const list = dropSources.get(drop.itemId) ?? []
    list.push({ monsterId: monster.id, level: monster.level ?? 1, chance: drop.dropChance ?? 1 })
    dropSources.set(drop.itemId, list)
  }
}

test('材料品阶：所有材料必须显式声明 tier（禁止靠缺省值兜底）', () => {
  const missing = materials.filter(it => it.category === 'material' && !Number.isInteger(it.tier))
  assert.deepEqual(missing.map(it => it.id), [], '每项材料的 tier 必须显式写进配表')
})

test('材料品阶：采集直出与高爆率战利品（≥50%）一律凡品 tier1', () => {
  const violations = []
  for (const itemId of gatherYields) {
    const tier = materialById.get(itemId)?.tier
    if (tier !== 1) violations.push(`${itemId} 采集直出却是 tier${tier}`)
  }
  for (const [itemId, sources] of dropSources) {
    if (!sources.some(s => s.chance >= 0.5)) continue
    const tier = materialById.get(itemId)?.tier
    if (tier !== 1) violations.push(`${itemId} 高爆率掉落却是 tier${tier}`)
  }
  assert.deepEqual(violations, [], '采集直出与高爆率战利品必须全部为凡品')
})

test('材料品阶：低概率掉落（<50%）一律稀有，且稀有度 = 产出源等阶 + 1', () => {
  const violations = []
  for (const [itemId, sources] of dropSources) {
    const rare = sources.filter(s => s.chance < 0.5)
    const tier = materialById.get(itemId)?.tier
    if (rare.length === 0) continue
    if (tier < 2) {
      violations.push(`${itemId} 低概率掉落却是 tier${tier}`)
      continue
    }
    const expected = Math.max(...rare.map(s => levelToTier(s.level) + 1))
    if (tier !== expected) {
      violations.push(`${itemId} 稀有 tier${tier} ≠ 产出源等阶+1（应为 tier${expected}）`)
    }
  }
  assert.deepEqual(violations, [])
})

test('材料品阶：常规物品与装备制造只允许消耗凡品材料', () => {
  const violations = []
  for (const itemId of manufacturingInputs) {
    const tier = materialById.get(itemId)?.tier
    // 非材料条目（装备、残卷等）不在材料品阶规则内
    if (tier === undefined) continue
    if (tier !== 1) violations.push(`${itemId} 为 tier${tier} 稀有材料却被制造配方消耗`)
  }
  assert.deepEqual(violations, [], '稀有材料不得出现在常规制造配方的消耗栏')
})

test('材料品阶：凡品材料必有正产出来源（采集 / 高爆率掉落 / 凡品加工）', () => {
  const refinedByTier1Only = new Set()
  for (const action of actions) {
    if (action.type !== 'alchemy') continue
    const inputs = Object.keys(action.inputs ?? {})
    if (!inputs.every(id => (materialById.get(id)?.tier ?? 1) === 1)) continue
    for (const y of action.yields ?? []) {
      if (y.kind === 'item' && y.id && isGradeMaterial(y.id)) refinedByTier1Only.add(y.id)
    }
  }
  const orphans = materials
    .filter(it => it.category === 'material' && it.tier === 1)
    .map(it => it.id)
    .filter(id =>
      !gatherYields.has(id)
      && !refinedByTier1Only.has(id)
      && !(dropSources.get(id) ?? []).some(s => s.chance >= 0.5))
  assert.deepEqual(orphans, [], '凡品材料必须能从采集、高爆率掉落或凡品加工中获得')
})

test('材料等级：所有材料必须显式声明 level，且等级对齐产出源生态（怪物等级或采集行动等级）', () => {
  const missingLevel = materials.filter(it => it.category === 'material' && (!Number.isInteger(it.level) || it.level < 1))
  assert.deepEqual(missingLevel.map(it => it.id), [], '每项材料的 level 必须显式声明且 >= 1')

  const violations = []
  for (const it of materials) {
    if (it.category !== 'material') continue
    const sources = dropSources.get(it.id)
    if (sources && sources.length > 0) {
      const minMonsterLvl = Math.min(...sources.map(s => s.level))
      const maxMonsterLvl = Math.max(...sources.map(s => s.level))
      // 材料等级应落在产出怪物的等级跨度内（允许上下浮动 2 级以保证阶梯平滑）
      if (it.level < minMonsterLvl - 2 || it.level > maxMonsterLvl + 2) {
        violations.push(`${it.id}(Lv.${it.level}) 与产出怪物等级区间 [${minMonsterLvl}, ${maxMonsterLvl}] 偏离过大`)
      }
    }
  }
  assert.deepEqual(violations, [], '怪物掉落材料等级必须与产出怪物等级区间对齐')
})
