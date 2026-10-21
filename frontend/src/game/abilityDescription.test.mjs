import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// 编译 format.ts 获得纯净 ESM 模块（导入真实的 zhCN 词条进行插值解析）
const tempOut = path.join(tmpdir(), `daohua-format-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/format.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  plugins: [
    {
      name: 'mock-i18n',
      setup(b) {
        b.onResolve({ filter: /^\.\.\/i18n$/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
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
            `,
            loader: 'js',
            resolveDir: path.resolve(process.cwd(), 'frontend/src/game'),
          }
        })
      },
    },
  ],
})

const { describeAbility } = await import(pathToFileURL(tempOut).href)

test.after(() => {
  try {
    fs.unlinkSync(tempOut)
  } catch {}
})

test('describeAbility：甘露无论 camelCase 还是 kebab-case 均正确说明对生命值百分比最低的队友施加增益', () => {
  const ganluCamel = {
    abilityId: 'ability.ganlu',
    name: '甘露',
    description: '天降甘露，润物无声。',
    targetType: 'lowestHitpointsAlly',
    category: 'spell',
    element: 'water',
    cooldownMilliseconds: 20000,
    castMilliseconds: 1500,
    manaCost: 6.0,
    buff: {
      buffId: 'buff.ganlu',
      name: '甘露',
      durationSeconds: 10.0,
      hpRegenPerSecond: 1.0,
      stackPolicy: 'maxKeep',
      spellAttackStackPercent: 0.08,
    },
  }

  const camelResult = describeAbility(ganluCamel, 1)
  assert.ok(
    camelResult.description.includes('对生命值百分比最低的队友施加增益'),
    `甘露描述应包含目标说明，实际为: ${camelResult.description}`
  )
  assert.ok(
    camelResult.description.includes('每层每秒回复 1 点气血'),
    `甘露描述应包含每秒回血效果，实际为: ${camelResult.description}`
  )
  assert.ok(
    camelResult.description.includes('层数为 1 + 施法者术法攻击的 8%（向下取整）'),
    `甘露描述应包含法攻百分比层数说明，实际为: ${camelResult.description}`
  )

  const ganluKebab = {
    ...ganluCamel,
    targetType: 'lowest-hp-ally',
  }
  const kebabResult = describeAbility(ganluKebab, 1)
  assert.ok(
    kebabResult.description.includes('对生命值百分比最低的队友施加增益'),
    `kebab 格式甘露描述应包含目标说明，实际为: ${kebabResult.description}`
  )
})

test('describeAbility：清平乐正确说明为生命值百分比最低的队友恢复生命', () => {
  const qingping = {
    abilityId: 'ability.qingping-melody',
    name: '清平乐',
    description: '清平一曲起，枯木又逢春。',
    targetType: 'lowestHitpointsAlly',
    category: 'spell',
    element: 'wood',
    cooldownMilliseconds: 30000,
    castMilliseconds: 500,
    manaCost: 10.0,
    effectType: 'heal',
    healingRatio: 0.6,
    flatHealing: 20,
  }

  const result = describeAbility(qingping, 1)
  assert.ok(
    result.description.includes('为生命值百分比最低的队友恢复'),
    `清平乐描述应包含目标说明，实际为: ${result.description}`
  )
})

test('describeAbility：破阵子与碎罡保持全队/自身目标说明', () => {
  const pozhen = {
    abilityId: 'ability.pozhen-melody',
    name: '破阵子',
    description: '一曲破阵壮军威。',
    targetType: 'allAllies',
    category: 'spell',
    element: 'none',
    buff: {
      name: '破阵子',
      durationSeconds: 10.0,
      damageBonusPoints: 40,
    },
  }
  const pozhenResult = describeAbility(pozhen, 1)
  assert.ok(pozhenResult.description.includes('使己方全体伤害提升'))
  assert.ok(pozhenResult.description.includes('40%'))

  const suiGang = {
    abilityId: 'ability.sui-gang',
    name: '碎罡',
    description: '崩碎自身护体罡气。',
    targetType: 'self',
    category: 'physical',
    buffs: [
      {
        name: '碎罡',
        durationSeconds: 10.0,
        damageAbsorptionPoints: -100,
        attackPower: 50,
      },
    ],
  }
  const suiGangResult = describeAbility(suiGang, 1)
  assert.ok(suiGangResult.description.includes('使自身伤害减免'))
})
