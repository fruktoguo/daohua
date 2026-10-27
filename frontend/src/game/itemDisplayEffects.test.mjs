import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// esbuild 动态 bundle ItemDisplay.tsx 导出纯逻辑解析函数
const tempOut = path.join(tmpdir(), `daohua-item-display-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/components/ItemDisplay.tsx')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  jsx: 'automatic',
  plugins: [
    {
      name: 'mock-i18n-and-stores',
      setup(b) {
        b.onResolve({ filter: /^\.\.\/i18n$/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
        b.onResolve({ filter: /^\.\.\/game\/gameStore$/ }, args => ({ path: args.path, namespace: 'mock-store' }))
        b.onResolve({ filter: /^\.\.\/network\/apiClient$/ }, args => ({ path: args.path, namespace: 'mock-api' }))
        b.onResolve({ filter: /itemIcons/ }, args => ({ path: args.path, namespace: 'mock-icons' }))
        b.onResolve({ filter: /^\.\/FloatingPopover$/ }, args => ({ path: args.path, namespace: 'mock-popover' }))
        b.onResolve({ filter: /^\.\/PillLabel$/ }, args => ({ path: args.path, namespace: 'mock-pill' }))
        b.onResolve({ filter: /^\.\/StatGlyph$/ }, args => ({ path: args.path, namespace: 'mock-glyph' }))

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
              export const useI18nStore = { getState: () => ({ language: 'zh' }) };
              export const hasTranslation = () => true;
            `,
            loader: 'js',
            resolveDir: path.resolve(process.cwd(), 'frontend/src/components'),
          }
        })

        b.onLoad({ filter: /.*/, namespace: 'mock-store' }, () => ({
          contents: `
            export const useGameStore = {
              getState: () => ({
                items: [],
                equipment: [],
                abilities: [],
                marketStats: {},
              }),
            };
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-api' }, () => ({
          contents: `
            export const marketStatsKey = () => '';
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-icons' }, () => ({
          contents: `
            export const resolveItemIconUrl = () => undefined;
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-popover' }, () => ({
          contents: `
            export const FloatingPopover = () => null;
            export const FloatingInfoCard = () => null;
            export const FloatingTooltip = () => null;
            export const createDescriptionAsideCard = () => null;
            export const createProgressAsideCard = () => null;
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-pill' }, () => ({
          contents: `
            export const PillBonusLabel = (props) => ({ type: 'PillBonusLabel', props });
            export const PillStatLabel = () => null;
          `,
          loader: 'js',
        }))

        b.onLoad({ filter: /.*/, namespace: 'mock-glyph' }, () => ({
          contents: `
            export const StatGlyph = (props) => ({ type: 'StatGlyph', props });
            export const resolveStatGlyph = () => undefined;
          `,
          loader: 'js',
        }))
      },
    },
  ],
})

const { describeItemEffects, resolveItemAsideCards } = await import(pathToFileURL(tempOut).href)

test('describeItemEffects: 战斗药品（如止血散）从 buffEffects 提取持续时间与冷却', () => {
  const zhixueItem = {
    itemId: 'item.zhixue-powder',
    name: '止血散',
    category: 'consumable',
    subCategory: 'pill',
    cooldownSeconds: 60,
    durationSeconds: 0,
    buffEffects: [
      {
        buffId: 'buff.zhixue',
        name: '止血散·回春',
        durationSeconds: 30,
        hpRegen: 36,
      },
    ],
  }

  const lines = describeItemEffects(zhixueItem)
  assert.ok(lines.length >= 2, '止血散应生成持续时间与冷却等效果行')

  const durationLine = lines.find(l => l.key === 'item-duration')
  assert.ok(durationLine, '应包含 item-duration 标签')
  assert.equal(durationLine.props.value, '30秒')
  assert.equal(durationLine.props.label, '持续时间')

  const cdLine = lines.find(l => l.key === 'item-cd')
  assert.ok(cdLine, '应包含 item-cd 标签')
  assert.equal(cdLine.props.value, '60s')
})

test('describeItemEffects: 技艺增益物品（定神香、淬灵粉）展示加成与持续时间', () => {
  const dingshen = {
    itemId: 'item.dingshen-incense',
    name: '定神香',
    category: 'consumable',
    durationSeconds: 300,
    techniqueSpeedRatio: 0.3,
  }

  const dingshenLines = describeItemEffects(dingshen)
  const speedLine = dingshenLines.find(l => l.key === 'item-technique-speed')
  assert.ok(speedLine, '定神香主卡片应包含技艺速度行')
  assert.equal(speedLine.props.label, '技艺速度')
  assert.equal(speedLine.props.value, '+30%')

  const durationLine = dingshenLines.find(l => l.key === 'item-duration')
  assert.ok(durationLine, '定神香主卡片应包含持续时间行')
  assert.equal(durationLine.props.value, '300秒')

  const cuiling = {
    itemId: 'item.cuiling-powder',
    name: '淬灵粉',
    category: 'consumable',
    durationSeconds: 300,
    enhancementSuccessBonus: 0.03,
  }

  const cuilingLines = describeItemEffects(cuiling)
  const enhanceLine = cuilingLines.find(l => l.key === 'item-enhancement-success')
  assert.ok(enhanceLine, '淬灵粉主卡片应包含强化成功率行')
  assert.equal(enhanceLine.props.label, '强化成功率')
  assert.equal(enhanceLine.props.value, '+3%')
})

test('resolveItemAsideCards: 技艺增益与强化物品生成规范的副卡片', () => {
  const dingshen = {
    itemId: 'item.dingshen-incense',
    name: '定神香',
    category: 'consumable',
    durationSeconds: 300,
    techniqueSpeedRatio: 0.3,
  }

  const cards = resolveItemAsideCards(dingshen)
  assert.equal(cards.length, 1, '定神香应生成 1 张技艺速度副卡片')
  assert.equal(cards[0].id, 'aside-technique-item.dingshen-incense')
  assert.equal(cards[0].subtitle, '持续 300 秒')
  assert.equal(cards[0].title, '定神香')

  const cuiling = {
    itemId: 'item.cuiling-powder',
    name: '淬灵粉',
    category: 'consumable',
    durationSeconds: 300,
    enhancementSuccessBonus: 0.03,
  }

  const cuilingCards = resolveItemAsideCards(cuiling)
  assert.equal(cuilingCards.length, 1, '淬灵粉应生成 1 张强化成功率副卡片')
  assert.equal(cuilingCards[0].id, 'aside-enhancement-item.cuiling-powder')
  assert.equal(cuilingCards[0].subtitle, '持续 300 秒')
})

test('resolveItemAsideCards: 战斗药品 Buff 副卡片显示持续时间副标题', () => {
  const zhixue = {
    itemId: 'item.zhixue-powder',
    name: '止血散',
    category: 'consumable',
    buffEffects: [
      {
        buffId: 'buff.zhixue',
        name: '止血散·回春',
        durationSeconds: 30,
        hpRegen: 36,
      },
    ],
  }

  const cards = resolveItemAsideCards(zhixue)
  assert.equal(cards.length, 1, '止血散应生成 1 张 Buff 副卡片')
  assert.equal(cards[0].id, 'aside-buff-止血散·回春')
  assert.equal(cards[0].subtitle, '持续 30 秒')
})
