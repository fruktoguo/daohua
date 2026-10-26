import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import {
  DEFAULT_CODEX_FILTERS,
  matchesCodexCollect,
  matchesCodexGrade,
  matchesCodexTraitGrade,
  resolveCodexCraftingSubTab,
  resolveCodexMaterialSubTab,
  resolveCodexMonsterRank,
  tallyCodexBuckets,
} from './codexFilters.ts'

// 配表按技艺/阶位拆成多份文件，测试侧整体拼装为一张扁平表，与 GameDataCatalog 装载口径一致。
const readRows = dir => fs.readdirSync(path.resolve(process.cwd(), 'config/game-data', dir))
  .filter(name => name.endsWith('.json'))
  .flatMap(name => JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'config/game-data', dir, name), 'utf8')))
const actions = readRows('actions')
const monsters = readRows('monsters')

test('codexFilters: 技艺归属与配表 technique 一致，且互斥完备', () => {
  const crafting = actions.filter(a => a.type === 'alchemy')
  assert.ok(crafting.length > 0, '配表必须存在制造类行动，否则该断言空跑')
  for (const action of crafting) {
    const tab = resolveCodexCraftingSubTab({ id: action.id, technique: action.technique })
    assert.ok(tab, `${action.id} 必须能归入某个图鉴技艺子分类，否则会从图鉴消失`)
    if (['alchemy', 'forging', 'formation', 'talisman'].includes(action.technique)) {
      assert.equal(tab, action.technique, `${action.id} 的归属必须等于配表 technique`)
    }
  }
})

test('codexFilters: 精炼与萃取类 craft.* 行动按实际技艺归属，不落空', () => {
  // craft.refine-* 属炼器、craft.extract-* 属炼丹，二者 technique 字段与 ID 前缀必须同向。
  const refine = actions.filter(a => a.id.startsWith('craft.refine-'))
  const extract = actions.filter(a => a.id.startsWith('craft.extract-'))
  assert.ok(refine.length > 0 && extract.length > 0, '配表必须仍存在精炼与萃取行动')
  for (const a of refine) assert.equal(resolveCodexCraftingSubTab({ id: a.id, technique: a.technique }), 'forging')
  for (const a of extract) assert.equal(resolveCodexCraftingSubTab({ id: a.id, technique: a.technique }), 'alchemy')
})

test('codexFilters: 妖兽生命层次归一覆盖全部配表条目且三档互斥', () => {
  assert.ok(monsters.length > 0, '配表必须存在妖兽')
  for (const monster of monsters) {
    const rank = resolveCodexMonsterRank(monster.rank)
    assert.ok(['normal', 'elite', 'boss'].includes(rank), `${monster.id} 的生命层次必须归入三档之一`)
    if (monster.rank === 'boss') assert.equal(rank, 'boss')
    else if (monster.rank === 'elite') assert.equal(rank, 'elite')
    else assert.equal(rank, 'normal')
  }
})

test('codexFilters: 材料子分类未声明时返回 null（提示补配表，而非静默归入某类）', () => {
  assert.equal(resolveCodexMaterialSubTab({ subCategory: undefined }), null)
  assert.equal(resolveCodexMaterialSubTab({ subCategory: 'unknownKind' }), null)
  for (const sub of ['spiritMaterial', 'mineral', 'exoticMaterial', 'refinedMaterial']) {
    assert.equal(resolveCodexMaterialSubTab({ subCategory: sub }), sub)
  }
})

test('codexFilters: 等阶过滤把缺省等阶按 1 阶计，0 表示任意', () => {
  assert.equal(matchesCodexGrade(undefined, 0), true)
  assert.equal(matchesCodexGrade(undefined, 1), true)
  assert.equal(matchesCodexGrade(undefined, 2), false)
  assert.equal(matchesCodexGrade(3, 3), true)
  assert.equal(matchesCodexGrade(3, 1), false)
})

test('codexFilters: 特质品阶过滤只放行 Q1~Q5，未知品阶仅在「全部」下可见', () => {
  assert.equal(matchesCodexTraitGrade(0, 'all'), true)
  assert.equal(matchesCodexTraitGrade(0, 'q1'), false)
  assert.equal(matchesCodexTraitGrade(6, 'all'), true)
  assert.equal(matchesCodexTraitGrade(6, 'q5'), false)
  assert.equal(matchesCodexTraitGrade(4, 'q4'), true)
  assert.equal(matchesCodexTraitGrade(4, 'q3'), false)
})

test('codexFilters: 收录状态过滤三态互斥完备', () => {
  assert.equal(matchesCodexCollect('all', true), true)
  assert.equal(matchesCodexCollect('all', false), true)
  assert.equal(matchesCodexCollect('collected', true), true)
  assert.equal(matchesCodexCollect('collected', false), false)
  assert.equal(matchesCodexCollect('missing', false), true)
  assert.equal(matchesCodexCollect('missing', true), false)
})

test('codexFilters: Toggle 角标的「全部」计数等于各子项之和，且已收录不超过总数', () => {
  const rows = [
    { kind: 'a', collected: true },
    { kind: 'b', collected: false },
    { kind: 'a', collected: false },
  ]
  const tally = tallyCodexBuckets(
    rows,
    [
      { value: 'all', match: () => true },
      { value: 'a', match: row => row.kind === 'a' },
      { value: 'b', match: row => row.kind === 'b' },
    ],
    row => row.collected,
  )
  assert.deepEqual(tally.all, { collected: 1, total: 3 })
  assert.deepEqual(tally.a, { collected: 1, total: 2 })
  assert.deepEqual(tally.b, { collected: 0, total: 1 })
  assert.equal(tally.a.total + tally.b.total, tally.all.total)
  assert.equal(tally.a.collected + tally.b.collected, tally.all.collected)
})

test('codexFilters: 默认筛选状态为「全部维度不过滤」', () => {
  for (const value of Object.values(DEFAULT_CODEX_FILTERS)) {
    assert.ok(value === 'all' || value === 0, `默认筛选必须是不做过滤的取值，实际是 ${String(value)}`)
  }
})
test('codexFilters: 逆命丹配方解析为 18 级且底蕴配方必须境界达标才解锁', () => {
  const reversePill = actions.find(a => a.id === 'alchemy.refine-reverse-fate-pill')
  assert.ok(reversePill, '配表必须存在逆命丹炼制行动')
  assert.equal(reversePill.level, 18, '逆命丹行动必须显式配置 level: 18')
  const pills = readRows('items')
  const pillItem = pills.find(it => it.id === 'item.reverse-fate-pill')
  assert.ok(pillItem, '配表必须存在逆命丹道具')
  assert.equal(pillItem.tier, 2, '逆命丹道具必须配置 tier: 2')

  // 模拟 App.tsx / CodexPage.tsx 中的解锁判定：
  const isUnlocked = (action, realmLevel, techLevel) => {
    const reqLevel = action.level ?? (action.tier ? (action.tier <= 1 ? 1 : 18 + (action.tier - 2) * 12) : 1)
    if (reqLevel <= 1) return true
    if (realmLevel >= reqLevel) return true
    const isTechAllowed = !action.foundationCostRatio && Boolean(action.technique && techLevel >= reqLevel)
    return isTechAllowed
  }

  // 1 级境界，即使炼丹技艺 30 级，也绝不能放行逆命丹
  assert.equal(isUnlocked(reversePill, 1, 30), false)
  assert.equal(isUnlocked(reversePill, 17, 30), false)

  // 18 级境界，技艺 0 级也可放行
  assert.equal(isUnlocked(reversePill, 18, 0), true)
})
