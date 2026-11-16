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

const { describeAbility, describeMorphAbility } = await import(pathToFileURL(tempOut).href)

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
    camelResult.description.includes('对生命值百分比最低的队友施加 [[8%术法攻击]] [[+ 1层]] 的 [[甘露]]，持续 10 秒。'),
    `甘露主描述应只保留施加关系、层数构成与持续时间，实际为: ${camelResult.description}`
  )
  assert.ok(
    !camelResult.description.includes('每层每秒回复') && !camelResult.description.includes('重复施加'),
    `甘露主描述不应堆叠 Buff 效果与控制细节，实际为: ${camelResult.description}`
  )
  assert.equal(
    camelResult.lines.length,
    4,
    `甘露主卡片应仅保留四项基础属性行，实际为: ${camelResult.lines.join(' | ')}`
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
    lv1.description.includes('对己方全体施加 [[破阵子]]，持续 10 秒。'),
    `破阵子主描述应统一由独立 Buff 悬浮窗承接效果明细，实际为: ${lv1.description}`
  )
  assert.equal(
    lv1.lines.length,
    4,
    `破阵子主卡片应仅保留四项基础属性行，实际为: ${lv1.lines.join(' | ')}`
  )
  const lv2 = describeAbility(pozhen, 2)
  assert.ok(
    lv2.description.includes('对己方全体施加 [[破阵子]]，持续 10 秒。'),
    `破阵子 Lv.2 主描述结构应保持一致，实际为: ${lv2.description}`
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
    lv1.description.includes('对自身施加 [[碎罡]]、[[1 + 30%双抗]] 层的 [[煞力]]、每 [[5 点已损气血]] [[1 层]]的 [[罡锋]]，持续 20 秒。'),
    `碎罡主描述应交代各 Buff 层数构成与持续时间，实际为: ${lv1.description}`
  )
  assert.ok(
    !lv1.description.includes('重复施加') && !lv1.description.includes('随功法等级与技能增幅'),
    `碎罡主描述不应出现控制细节长句，实际为: ${lv1.description}`
  )
  assert.equal(
    lv1.lines.length,
    4,
    `碎罡主卡片应仅保留四项基础属性行，实际为: ${lv1.lines.join(' | ')}`
  )

  const lv2 = describeAbility(suiGang, 2)
  assert.ok(
    lv2.description.includes('[[1 + 30.6%双抗]] 层的 [[煞力]]')
      && lv2.description.includes('每 [[4.9 点已损气血]] [[1 层]]的 [[罡锋]]'),
    `碎罡层数构成应随等级成长并体现在主描述中，实际为: ${lv2.description}`
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
    result.description.includes('引动敌意向己，获得 500 点威胁值，对自身施加 [[嘲讽]]，持续 60 秒。'),
    `嘲讽主描述应保留威胁、施加关系与持续时间，实际为: ${result.description}`
  )
  assert.equal(
    result.lines.length,
    4,
    `嘲讽主卡片应仅保留四项基础属性行，实际为: ${result.lines.join(' | ')}`
  )
})

test('describeAbility：跃迁、斩玉(踏云)与火焰风暴按等级与机制准确描述', () => {
  const yueqian = {
    abilityId: 'ability.yueqian',
    name: '跃迁',
    description: '缩地成寸，腾挪移形。引符阵庇佑全体友方周身气机游离，步虚避锋。',
    targetType: 'allAllies',
    category: 'spell',
    element: 'none',
    cooldownMilliseconds: 24000,
    castMilliseconds: 3000,
    manaCost: 50,
    buff: {
      buffId: 'buff.yixing',
      name: '移形',
      durationSeconds: 5.0,
      maxStacks: 99,
      evasionPostRatio: 1.0,
    },
  }
  const yueqianLv1 = describeAbility(yueqian, 1)
  const yueqianLv5 = describeAbility(yueqian, 5)
  assert.ok(
    yueqianLv1.description.includes('对己方全体施加 [[移形]]，持续 5 秒。'),
    `跃迁主描述应统一交代施加移形与持续时间，实际为: ${yueqianLv1.description}`
  )
  assert.equal(
    yueqianLv1.lines.length,
    4,
    `跃迁主卡片应仅保留四项基础属性行，实际为: ${yueqianLv1.lines.join(' | ')}`
  )
  assert.ok(
    yueqianLv5.description.includes('对己方全体施加 [[移形]]，持续 5 秒。'),
    `跃迁 Lv.5 主描述结构应保持一致，实际为: ${yueqianLv5.description}`
  )
  assert.equal(describeMorphAbility(yueqian, 1), null)

  const zhanyu = {
    abilityId: 'ability.zhanyu',
    name: '斩玉',
    description: '剑光凝霜斩碎玉。若身负「移形」，则借势化态为「踏云」，凌空掠影横扫全体敌阵。',
    targetType: 'enemy',
    category: 'physical',
    element: 'metal',
    cooldownMilliseconds: 8000,
    castMilliseconds: 500,
    manaCost: 10,
    damageRatio: 1.0,
    flatDamage: 10,
    yixingMorphName: '踏云',
    yixingMorphTargetType: 'allEnemies',
  }
  const zhanyuLv1 = describeAbility(zhanyu, 1)
  assert.ok(
    zhanyuLv1.description.includes('对单个目标造成[[100%物理攻击]] [[+10]] 金属性物理伤害；拥有[[移形]]时切换为 [[踏云]]。'),
    `斩玉主描述应含切态踏云说明，实际为: ${zhanyuLv1.description}`
  )
  const morphLv1 = describeMorphAbility(zhanyu, 1)
  assert.ok(morphLv1, '斩玉应构造踏云变招独立描述')
  assert.equal(morphLv1.title, '踏云')
  assert.equal(morphLv1.badge, zhanyuLv1.badge)
  assert.equal(morphLv1.badge, '物理 · 金行')
  assert.equal(
    morphLv1.description,
    '对敌方全体造成[[100%物理攻击]] [[+10]] 金属性物理伤害；施放后移除 1 层[[移形]]。'
  )
  assert.equal(morphLv1.lines.length, 4)

  const morphLv2 = describeMorphAbility(zhanyu, 2)
  assert.ok(
    morphLv2 && morphLv2.description.includes('[[102%物理攻击]] [[+10.2]]'),
    `踏云变招伤害应随功法等级缩放，实际为: ${morphLv2?.description}`
  )

  const huoyan = {
    abilityId: 'ability.huoyan-fengbao',
    name: '火焰风暴',
    description: '赤焰狂飙席卷四野，烈火焚身引燃魂魄，余烬久烧不息。',
    targetType: 'allEnemies',
    category: 'spell',
    element: 'fire',
    cooldownMilliseconds: 18000,
    castMilliseconds: 3000,
    manaCost: 30,
    damageRatio: 1.0,
    flatDamage: 50,
    ranhunBaseStacks: 5,
    ranhunSpellAttackRatio: 0.2,
  }
  const huoyanLv1 = describeAbility(huoyan, 1)
  const huoyanLv2 = describeAbility(huoyan, 2)
  assert.ok(
    huoyanLv1.description.includes('对敌方全体造成[[100%术法攻击]] [[+50]] 火属性术法伤害；并对命中目标施加 [[20%术法攻击]] [[+5 层]] 的 [[燃烧]]，持续 5 秒。'),
    `火焰风暴 Lv.1 主描述异常，实际为: ${huoyanLv1.description}`
  )
  assert.equal(
    huoyanLv1.lines.length,
    4,
    `火焰风暴主卡片应仅保留四项基础属性行，实际为: ${huoyanLv1.lines.join(' | ')}`
  )
  assert.ok(
    huoyanLv2.description.includes('对敌方全体造成[[102%术法攻击]] [[+51]] 火属性术法伤害；并对命中目标施加 [[20.4%术法攻击]] [[+5.1 层]] 的 [[燃烧]]，持续 5 秒。'),
    `火焰风暴 Lv.2 主描述应随等级成长，实际为: ${huoyanLv2.description}`
  )
})

test('describeAbility：雾梦泽 20 级五新技能主描述与固定基底层数、不成长豁免精准对齐', () => {
  const tianleiPo = {
    abilityId: 'ability.tianlei-po',
    name: '天雷破',
    targetType: 'allEnemies',
    maxTargets: 2,
    category: 'spell',
    element: 'wood',
    damageRatio: 1.1,
    flatDamage: 50,
    onHitBuff: {
      buffId: 'buff.mabi',
      name: '麻痹',
      durationSeconds: 5,
      noLevelGrowth: true,
      evasionPostRatio: -0.2,
      parryPostRatio: -0.2,
      actionSpeedPoints: -10,
    },
  }
  const tianleiDesc = describeAbility(tianleiPo, 1)
  assert.ok(
    tianleiDesc.description.includes('对至多 2 名敌人造成[[110%术法攻击]] [[+50]] 木属性术法伤害；并对命中目标施加 [[麻痹]]，持续 5 秒。'),
    `天雷破描述异常: ${tianleiDesc.description}`,
  )

  const zhuoxin = {
    abilityId: 'ability.zhuoxin',
    name: '灼心',
    targetType: 'targetEnemy',
    category: 'spell',
    element: 'fire',
    damageRatio: 0,
    flatDamage: 0,
    ranhunBaseStacks: 10,
    ranhunSpellAttackRatio: 0.5,
  }
  const zhuoxinDesc = describeAbility(zhuoxin, 1)
  assert.ok(
    zhuoxinDesc.description.includes('对单个目标施加 [[50%术法攻击]] [[+10 层]] 的 [[燃烧]]，持续 5 秒。'),
    `灼心描述异常: ${zhuoxinDesc.description}`,
  )

  const shunshan = {
    abilityId: 'ability.shunshan',
    name: '瞬闪',
    targetType: 'targetEnemy',
    category: 'physical',
    element: 'metal',
    damageRatio: 2.4,
    flatDamage: 100,
    resetCooldownOnYixingKill: true,
    selfBuff: {
      buffId: 'buff.yixing',
      name: '移形',
      durationSeconds: 5,
      noLevelGrowth: true,
      skipIfHasYixing: true,
      evasionPostRatio: 1,
    },
  }
  const shunshanDesc = describeAbility(shunshan, 1)
  assert.ok(
    shunshanDesc.description.includes('对单个目标造成[[240%物理攻击]] [[+100]] 金属性物理伤害；若起手未持有[[移形]]，则自身获得 [[移形]]，持续 5 秒；若起手已持有[[移形]]且本次击杀目标，则立即重置冷却且不再施加[[移形]]。'),
    `瞬闪描述异常: ${shunshanDesc.description}`,
  )

  const shangui = {
    abilityId: 'ability.shangui-yao',
    name: '山鬼谣',
    targetType: 'allAllies',
    category: 'spell',
    element: 'water',
    damageRatio: 0,
    buff: {
      buffId: 'buff.ganlu',
      name: '甘露',
      durationSeconds: 10,
      hpRegenPerSecond: 1,
      baseStacks: 5,
      spellAttackStackPercent: 0.12,
    },
  }
  const shanguiLv1 = describeAbility(shangui, 1)
  const shanguiLv2 = describeAbility(shangui, 2)
  assert.ok(
    shanguiLv1.description.includes('对己方全体施加 [[12%术法攻击]] [[+5 层]] 的 [[甘露]]，持续 10 秒。'),
    `山鬼谣 Lv.1 描述异常: ${shanguiLv1.description}`,
  )
  assert.ok(
    shanguiLv2.description.includes('对己方全体施加 [[12.2%术法攻击]] [[+5 层]] 的 [[甘露]]，持续 10 秒。'),
    `山鬼谣 Lv.2 百分比应成长且 5 层基底保持固定，实际为: ${shanguiLv2.description}`,
  )

  const pojun = {
    abilityId: 'ability.pojun',
    name: '破军',
    targetType: 'allEnemies',
    category: 'physical',
    element: 'metal',
    damageRatio: 1.2,
    flatDamage: 50,
    selfBuffs: [
      { buffId: 'buff.yixing', name: '移形', durationSeconds: 5, noLevelGrowth: true, evasionPostRatio: 1 },
      { buffId: 'buff.shibukedang', name: '势不可挡', durationSeconds: 5, baseStacks: 5, noLevelGrowth: true, damageAbsorptionPoints: 20, decayStacksPerSecond: 1 },
    ],
  }
  const pojunDesc = describeAbility(pojun, 1)
  assert.ok(
    pojunDesc.description.includes('对敌方全体造成[[120%物理攻击]] [[+50]] 金属性物理伤害；自身获得 [[移形]]、[[5 层]]的 [[势不可挡]]，持续 5 秒。'),
    `破军描述异常: ${pojunDesc.description}`,
  )
})
