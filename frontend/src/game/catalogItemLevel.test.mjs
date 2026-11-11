import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { rm } from 'node:fs/promises'
import { build } from 'esbuild'

// 编译 ids.ts 获得纯净 ESM 模块（mock 掉对 React/Zustand 的 i18n 目录依赖）
const tempOut = path.join(tmpdir(), `daohua-catalog-item-level-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/ids.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  plugins: [
    {
      name: 'mock-i18n',
      setup(b) {
        b.onResolve({ filter: /i18n/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
        b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => ({
          contents: 'export const getT = () => (key) => key; export const useT = () => (key) => key; export const hasTranslation = () => false;',
          loader: 'js',
        }))
      },
    },
  ],
})
const { resolveCatalogItemLevel, convertTierToLevel } = await import(pathToFileURL(tempOut).href)

test('代表等级解析与服务端 ResolveItemLevel 同口径：显式 level 优先于等阶换算', () => {
  // 装备目录 level 已由服务端 ResolveEquipmentRequiredLevel 预解析（含等阶回退）
  assert.equal(resolveCatalogItemLevel({ level: 9 }, undefined, 1), 9)
  // 物品仅在配表显式声明 level 时下发；tier=1 的 level=3 物品不得按 1 级结算
  assert.equal(resolveCatalogItemLevel(undefined, { level: 3 }, 1), 3)
  assert.equal(resolveCatalogItemLevel(undefined, { level: 1 }, 1), 1)
  // 未声明显式等级时回退等阶换算
  assert.equal(resolveCatalogItemLevel(undefined, { level: null }, 1), convertTierToLevel(1))
  assert.equal(resolveCatalogItemLevel(undefined, undefined, 2), convertTierToLevel(2))
  assert.equal(resolveCatalogItemLevel(null, null), 1)
})

await rm(tempOut, { force: true })
