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

test('describeAbility：甘露主描述只保留施加关系与层数构成，百分比随等级缩放', () => {
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
    camelResult.description.includes('对生命值百分比最低的队友施加 [[8%术法攻击]] [[+ 1层]] 的 [[甘露]]。'),
    `甘露主描述应只保留施加关系与层数构成，实际为: ${camelResult.description}`
  )
  assert.ok(
    !camelResult.description.includes('每层每秒回复') && !camelResult.description.includes('重复施加'),
    `甘露主描述不应堆叠 Buff 效果与控制细节，实际为: ${camelResult.description}`
  )
  assert.ok(
    camelResult.lines.includes('[[甘露]]：每层每秒回复 1 点气血，持续 10 秒'),
    `甘露 Buff 效果应进入词条行，实际为: ${camelResult.lines.join(' | ')}`
  )

  const lv2Result = describeAbility(ganluCamel, 2)
  assert.ok(
    lv2Result.description.includes('[[8.2%术法攻击]] [[+ 1层]]'),
    `甘露层数百分比应随等级成长，实际为: ${lv2Result.description}`
  )

  const ganluKebab = {
    ...ganluCamel,
    targetType: 'lowest-hp-ally',
  }
  const kebabResult = describeAbility(ganluKebab, 1)
  assert.ok(
    kebabResult.description.includes('对生命值百分比最低的队友施加'),
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

test('describeAbility：破阵子保持直接效果短句，增伤随等级成长', () => {
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
  const lv1 = describeAbility(pozhen, 1)
  assert.ok(
    lv1.description.includes('使己方全体伤害提升 40%，持续 10 秒。'),
    `破阵子应保持直接效果短句，实际为: ${lv1.description}`
  )
  const lv2 = describeAbility(pozhen, 2)
  assert.ok(
    lv2.description.includes('使己方全体伤害提升 40.8%'),
    `破阵子增伤应随等级成长，实际为: ${lv2.description}`
  )
})

test('describeAbility：碎罡三 Buff 胶囊化，层数构成随等级成长且每层效果固定', () => {
  const suiGang = {
    abilityId: 'ability.sui-gang',
    name: '碎罡',
    description: '崩碎自身护体罡气。',
    targetType: 'self',
    category: 'physical',
    element: 'none',
    buffs: [
      {
        name: '碎罡',
        durationSeconds: 20.0,
        resistancePoints: -400,
        stackPolicy: 'maxKeep',
      },
      {
        name: '煞力',
        durationSeconds: 20.0,
        attackPower: 1.0,
        resistancePerStack: 1.0,
        resistanceStackRatio: 0.3,
        stackPolicy: 'maxKeep',
      },
      {
        name: '罡锋',
        durationSeconds: 20.0,
        accuracy: 1.0,
        critical: 1.0,
        missingHitpointsPerStack: 5.0,
        stackPolicy: 'maxKeep',
      },
    ],
  }
  const lv1 = describeAbility(suiGang, 1)
  assert.ok(
    lv1.description.includes('对自身施加 [[碎罡]]、[[煞力]]、[[罡锋]]。'),
    `碎罡主描述应只列高亮 Buff 名，实际为: ${lv1.description}`
  )
  assert.ok(
    !lv1.description.includes('重复施加') && !lv1.description.includes('随功法等级与技能增幅'),
    `碎罡主描述不应出现控制细节长句，实际为: ${lv1.description}`
  )

  const lv2 = describeAbility(suiGang, 2)
  assert.ok(
    lv2.lines.includes('[[煞力]]：[[1 + 30.6%双抗]] 层，每层物攻 +1，持续 20 秒')
      && lv2.lines.includes('[[罡锋]]：每 [[4.9 点已损气血]] [[1 层]]，每层命中/暴击 +1，持续 20 秒'),
    `碎罡层数构成应随等级成长并进入词条行，实际为: ${lv2.lines.join(' | ')}`
  )
})

test('describeAbility：嘲讽主描述保留威胁与施加关系，Buff 效果进入词条行', () => {
  const taunt = {
    abilityId: 'ability.taunt',
    name: '嘲讽',
    description: '怒喝惊四野，引敌向吾身。',
    targetType: 'self',
    category: 'physical',
    element: 'none',
    threatGain: 500,
    buff: {
      name: '嘲讽',
      durationSeconds: 60.0,
      threatPoints: 400,
      threatPerSecondMaxHpPoints: 10,
    },
  }
  const result = describeAbility(taunt, 1)
  assert.ok(
    result.description.includes('引动敌意向己，获得 500 点威胁值，对自身施加 [[嘲讽]]。'),
    `嘲讽主描述应保留威胁与施加关系，实际为: ${result.description}`
  )
  assert.ok(
    result.lines.includes('[[嘲讽]]：威胁获取提升 400%，每秒获得 10% 最大生命的威胁，持续 60 秒'),
    `嘲讽 Buff 效果应进入词条行，实际为: ${result.lines.join(' | ')}`
  )
})
