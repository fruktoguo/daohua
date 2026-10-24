import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

// mobileNav 依赖 uiStore 的类型与 components/Icon 的类型，Node 原生 ESM 无法解析
// TS 目录导入，先用 esbuild 打包成单文件再加载。
const require = createRequire(import.meta.url)
const esbuild = require('esbuild')
const tempOut = path.join(tmpdir(), `daohua-mobile-nav-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/mobileNav.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
})
const {
  MOBILE_NAV_GROUPS,
  MOBILE_NAV_GROUP_ORDER,
  MOBILE_NAV_GROUP_DEFS,
  TAB_ICON,
  mobileNavGroupById,
  mobileNavGroupIdOf,
} = await import(pathToFileURL(tempOut).href)

test('移动端导航：一级分组恰好 7 项且覆盖全部页签，无重复无遗漏', () => {
  assert.equal(MOBILE_NAV_GROUPS.length, 7)
  assert.deepEqual(
    MOBILE_NAV_GROUPS.map(group => group.id),
    [...MOBILE_NAV_GROUP_ORDER],
  )
  const covered = MOBILE_NAV_GROUPS.flatMap(group => [...group.tabs])
  assert.equal(new Set(covered).size, covered.length, '同一页签不得出现在两个分组')
  // 除 `alchemy` 外，每个页签都必须能在某个分组里被点到，否则该功能在竖屏彻底不可达。
  // `alchemy` 只是直达技艺-炼丹的别名，由 setActiveTab 归一为 crafting，不需要独立入口。
  const routable = Object.keys(TAB_ICON).filter(tab => tab !== 'alchemy')
  assert.deepEqual([...covered].sort(), routable.sort())
  assert.equal(mobileNavGroupIdOf('alchemy'), 'craft', '别名页签仍须归入技艺分组，避免记录落点丢失')
})

test('移动端导航：组内首个页签是分组默认落点，单页签分组不渲染子行', () => {
  for (const group of MOBILE_NAV_GROUPS) {
    assert.ok(group.tabs.length >= 1, `${group.id} 必须至少有一个页签`)
    if (group.subNav === 'none') assert.equal(group.tabs.length, 1, `${group.id} 无子行时必须只有一个页签`)
    if (group.subNav === 'tabs') assert.ok(group.tabs.length > 1, `${group.id} 要渲染多页签子行`)
  }
})

test('移动端导航：页签反查分组与分组取项一致', () => {
  for (const group of MOBILE_NAV_GROUPS) {
    for (const tab of group.tabs) {
      assert.equal(mobileNavGroupIdOf(tab), group.id)
    }
    assert.deepEqual(mobileNavGroupById(group.id), group)
  }
})

test('移动端导航：坊市/商店同组、历练/地图同组、日志与同道归入更多组', () => {
  assert.equal(mobileNavGroupIdOf('market'), mobileNavGroupIdOf('shop'))
  assert.equal(mobileNavGroupIdOf('exploration'), mobileNavGroupIdOf('map'))
  assert.equal(mobileNavGroupIdOf('log'), 'more')
  assert.equal(mobileNavGroupIdOf('social'), 'more')
  assert.equal(mobileNavGroupIdOf('records'), 'more')
  // 详情是行动结算后的自动落点，必须保留一级直达
  assert.equal(mobileNavGroupIdOf('combat'), 'detail')
  assert.deepEqual(MOBILE_NAV_GROUP_DEFS.detail.tabs, ['combat'])
})
