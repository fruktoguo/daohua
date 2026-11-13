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
const {
  resolveCatalogItemLevel,
  resolveCatalogItemMetadata,
  resolveItemMetadata,
  resolveActionLevel,
  convertTierToLevel,
} = await import(pathToFileURL(tempOut).href)

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

test('目录元数据解析优先装备与物品真实等级，避免被等阶折算覆盖为 1/18/30', () => {
  const xuantie = resolveCatalogItemMetadata('equipment.xuantie-sword', {
    equipmentDefinition: {
      equipmentId: 'equipment.xuantie-sword',
      name: '玄铁长剑',
      slot: 'weapon',
      tier: 1,
      level: 9,
    },
    tier: 1,
  })
  assert.equal(xuantie.level, 9)
  assert.equal(xuantie.tier, 1)
  assert.equal(xuantie.rarity, 'mundane')

  const duanchen = resolveCatalogItemMetadata('equipment.duanchen-blade', {
    equipmentDefinition: {
      equipmentId: 'equipment.duanchen-blade',
      name: '断尘刀',
      slot: 'weapon',
      tier: 3,
      level: 20,
    },
    tier: 3,
  })
  assert.equal(duanchen.level, 20)
  assert.equal(duanchen.tier, 3)
  assert.equal(duanchen.rarity, 'mystic')

  // 未在静态注册表登记的新装备：仍须从服务端 equipmentDefinition 读取真实 tier 与 level
  const customEq = resolveCatalogItemMetadata('equipment.future-blade', {
    equipmentDefinition: {
      equipmentId: 'equipment.future-blade',
      name: '太虚神锋',
      slot: 'weapon',
      tier: 3,
      level: 24,
    },
    tier: 1,
  })
  assert.equal(customEq.level, 24)
  assert.equal(customEq.tier, 3)
  assert.equal(customEq.rarity, 'mystic')

  // 显式等级的消耗品/材料也保留真实 level
  const mat = resolveCatalogItemMetadata('item.demon-dan', {
    itemDefinition: {
      itemId: 'item.demon-dan',
      name: '妖丹',
      description: '',
      kind: 'material',
      category: 'material',
      tier: 2,
      level: 12,
      sellPrice: 10,
      isTradable: true,
      usableBars: [],
      cooldownSeconds: 0,
      hitpointRestore: 0,
      manapointRestore: 0,
      buffEffects: null,
      durationSeconds: 0,
      isContinuous: false,
      cultivationMultiplier: 1,
      gatheringMultiplier: 1,
      alchemyMultiplier: 1,
    },
  })
  assert.equal(mat.level, 12)
  assert.equal(mat.tier, 2)
  assert.equal(mat.rarity, 'yellow')

  // 离线静态元数据：9 级装备不应回退为 1 级
  assert.equal(resolveItemMetadata('equipment.xuantie-sword')?.level, 9)
  assert.equal(resolveItemMetadata('equipment.bibo-pendant')?.level, 9)

  // 行动等级兜底：缺省 action.level 时优先读取产出装备的真实等级
  const forgeLevel = resolveActionLevel(
    {
      id: 'forge.xuantie-sword',
      name: '锻造玄铁长剑',
      description: '',
      type: 'alchemy',
      durationSeconds: 10,
      yields: [{ kind: 'item', id: 'equipment.xuantie-sword', amount: 1 }],
    },
    [],
    [{ equipmentId: 'equipment.xuantie-sword', tier: 1, level: 9 }],
    [],
  )
  assert.equal(forgeLevel, 9)
})

await rm(tempOut, { force: true })
