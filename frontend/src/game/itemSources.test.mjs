import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// esbuild 动态 bundle itemSources.tsx 纯逻辑解析函数
const tempOut = path.join(tmpdir(), `daohua-item-sources-test-${Date.now()}.mjs`)

await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/itemSources.tsx')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  jsx: 'automatic',
  define: {
    'import.meta.env': '{}',
  },
  plugins: [
    {
      name: 'mock-i18n-and-store',
      setup(b) {
        b.onResolve({ filter: /i18n$/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
        b.onResolve({ filter: /gameStore$/ }, args => ({ path: args.path, namespace: 'mock-store' }))
        b.onResolve({ filter: /FloatingPopover$/ }, args => ({ path: args.path, namespace: 'mock-popover' }))

        b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => {
          const zhCNPath = path.resolve(process.cwd(), 'frontend/src/i18n/locales/zh-CN.ts').replace(/\\/g, '/')
          return {
            contents: `
              import { zhCN } from '${zhCNPath}';
              function getByPath(obj, pathParts) {
                let curr = obj;
                for (const part of pathParts) {
                  if (!curr) return undefined;
                  curr = curr[part];
                }
                return curr;
              }
              export const hasTranslation = () => true;
              export const getT = () => (key, params) => {
                const parts = key.split('.');
                const template = getByPath(zhCN, parts);
                if (typeof template !== 'string') return key;
                if (!params) return template;
                return template.replace(/\\{([^}]+)\\}/g, (_, paramKey) => {
                  return params[paramKey] !== undefined ? String(params[paramKey]) : '{' + paramKey + '}';
                });
              };
              export const useT = getT;
            `,
            loader: 'js',
            resolveDir: path.resolve(process.cwd(), 'frontend/src/game'),
          }
        })

        b.onLoad({ filter: /.*/, namespace: 'mock-store' }, () => ({
          contents: `
            globalThis.__mockState = {
              encounters: [],
              actions: [],
              items: [],
            };
            export const useGameStore = {
              getState: () => globalThis.__mockState,
              setState: (next) => { globalThis.__mockState = { ...globalThis.__mockState, ...next }; },
            };
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-popover' }, () => ({
          contents: `
            export const FloatingPopover = () => null;
          `,
          loader: 'js',
        }))
      },
    },
  ],
})

const { resolveItemSources, resolveItemSourcesAsideCard } = await import(pathToFileURL(tempOut).href)
const setMockState = (next) => { globalThis.__mockState = { ...globalThis.__mockState, ...next } }

test('resolveItemSources: 妖兽掉落能正确反查，并按等阶与掉率排序', () => {
  setMockState({
    encounters: [
      {
        encounterId: 'enc.wolf-pack',
        name: '青云古道·狼群',
        enemy: {
          enemyId: 'monster.green-wolf',
          name: '青煞狼',
          tier: 1,
          level: 3,
          rank: 'normal',
          drops: [
            { itemId: 'item.beast-bone', dropChance: 0.5, minQuantity: 1, maxQuantity: 2 },
          ],
        },
        randomSpawns: [],
        eliteEnemies: [
          {
            enemyId: 'monster.wolf-king',
            name: '青煞狼王',
            tier: 2,
            level: 10,
            rank: 'boss',
            drops: [
              { itemId: 'item.beast-bone', dropChance: 1.0, minQuantity: 5, maxQuantity: 5 },
            ],
          },
        ],
        bossEnemies: [],
      },
    ],
    actions: [],
    items: [],
  })

  const result = resolveItemSources('item.beast-bone')
  assert.equal(result.totalMonstersCount, 2, '应包含2只掉落怪物')
  assert.equal(result.monsters.length, 2)
  // T2 狼王应排在 T1 普通狼前面
  assert.equal(result.monsters[0].enemyId, 'monster.wolf-king')
  assert.equal(result.monsters[0].dropChance, 1.0)
  assert.equal(result.monsters[1].enemyId, 'monster.green-wolf')
  assert.equal(result.monsters[1].dropChance, 0.5)
})

test('resolveItemSources: 怪物超过 5 只时自动截断并保留总数统计', () => {
  const spawns = []
  for (let i = 1; i <= 8; i++) {
    spawns.push({
      enemy: {
        enemyId: `monster.beast-${i}`,
        name: `山兽${i}号`,
        tier: 1,
        level: i,
        drops: [{ itemId: 'item.beast-hide', dropChance: 0.2, minQuantity: 1, maxQuantity: 1 }],
      },
      weight: 1,
      strength: 1,
    })
  }

  setMockState({
    encounters: [
      {
        encounterId: 'enc.zoo',
        name: '万兽林',
        enemy: {
          enemyId: 'monster.alpha',
          name: '头兽',
          tier: 1,
          level: 1,
          drops: [],
        },
        randomSpawns: spawns,
        eliteEnemies: [],
        bossEnemies: [],
      },
    ],
    actions: [],
    items: [],
  })

  const result = resolveItemSources('item.beast-hide')
  assert.equal(result.totalMonstersCount, 8, '总数应为8只')
  assert.equal(result.monsters.length, 5, '展示列表应截断为前5只')
})

test('resolveItemSources: 制造配方与生活技艺产出正确归类反查', () => {
  setMockState({
    encounters: [],
    actions: [
      {
        id: 'forge.qingfeng-sword',
        name: '锻造 青锋剑',
        type: 'alchemy',
        technique: 'forging',
        level: 1,
        durationSeconds: 15,
        yields: [
          { kind: 'item', id: 'equipment.qingfeng-sword', amount: 1, per: 'repeat' },
        ],
      },
      {
        id: 'explore.gather-herb',
        name: '采摘 凝气草',
        type: 'exploration',
        technique: 'gathering',
        level: 1,
        durationSeconds: 8,
        yields: [
          { kind: 'item', id: 'item.ningxue-grass', amount: 1, per: 'repeat' },
        ],
      },
    ],
    items: [],
  })

  const swordSources = resolveItemSources('equipment.qingfeng-sword')
  assert.equal(swordSources.crafting.length, 1)
  assert.equal(swordSources.crafting[0].actionId, 'forge.qingfeng-sword')
  assert.equal(swordSources.crafting[0].category, 'crafting')
  assert.equal(swordSources.gatherings.length, 0)

  const herbSources = resolveItemSources('item.ningxue-grass')
  assert.equal(herbSources.gatherings.length, 1)
  assert.equal(herbSources.gatherings[0].actionId, 'explore.gather-herb')
  assert.equal(herbSources.gatherings[0].category, 'gathering')
  assert.equal(herbSources.crafting.length, 0)
})

test('resolveItemSources: 通用货币（灵石、功德）特殊概括处理，不罗列全图怪兽', () => {
  setMockState({
    encounters: [
      {
        encounterId: 'enc.common',
        name: '全图怪',
        enemy: {
          enemyId: 'monster.common-1',
          name: '小怪A',
          drops: [{ itemId: 'currency.spirit-stone', dropChance: 1.0, minQuantity: 1, maxQuantity: 5 }],
        },
        randomSpawns: [],
        eliteEnemies: [],
        bossEnemies: [],
      },
    ],
    actions: [],
    items: [
      { itemId: 'item.storage-bag.broken', name: '破损的储物袋' },
    ],
  })

  // 1. 灵石
  const spiritStoneResult = resolveItemSources('currency.spirit-stone')
  assert.equal(spiritStoneResult.monsters.length, 0, '灵石不应直接平铺怪物列表')
  assert.ok(spiritStoneResult.generals.length >= 4, '灵石应提供四大核心概括途径')
  assert.ok(spiritStoneResult.generals.some(g => g.title.includes('妖兽')), '应包含妖兽讨伐概括')
  assert.ok(spiritStoneResult.generals.some(g => g.title.includes('储物袋')), '应包含储物袋开启概括')

  // 2. 功德
  const gongdeResult = resolveItemSources('item.gongde')
  assert.equal(gongdeResult.monsters.length, 0, '功德不应直接平铺怪物列表')
  assert.ok(gongdeResult.generals.length >= 2, '功德应提供储物袋与天道机缘途径')

  // 3. 觉醒石从储物袋正常反查出 packs
  const awakeningResult = resolveItemSources('item.awakening-stone')
  assert.ok(awakeningResult.packs.length >= 1, '觉醒石应查出破损储物袋开启渠道')
  assert.equal(awakeningResult.packs[0].packItemId, 'item.storage-bag.broken')
  assert.equal(awakeningResult.packs[0].chanceText, '0.1%')
})

test('resolveItemSourcesAsideCard: 无产出物品返回 null，有产出物品生成 jade AsideCard', () => {
  setMockState({
    encounters: [],
    actions: [],
    items: [],
  })

  const nullCard = resolveItemSourcesAsideCard('item.unknown-nonexistent')
  assert.equal(nullCard, null, '无任何来源时不渲染副卡片')

  setMockState({
    encounters: [
      {
        encounterId: 'enc.test',
        name: '测试',
        enemy: {
          enemyId: 'monster.test',
          name: '测试妖兽',
          drops: [{ itemId: 'item.real-material', dropChance: 0.1, minQuantity: 1, maxQuantity: 1 }],
        },
        randomSpawns: [],
        eliteEnemies: [],
        bossEnemies: [],
      },
    ],
    actions: [],
    items: [],
  })

  const card = resolveItemSourcesAsideCard('item.real-material')
  assert.ok(card !== null, '有来源时应生成 AsideCard')
  assert.equal(card.tone, 'jade')
  assert.equal(card.id, 'aside-item-sources-item.real-material')
  assert.ok(card.content !== undefined)
})
