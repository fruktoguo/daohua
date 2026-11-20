import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// 编译 combatConstants.ts 单文件用于 node:test
const tempOut = path.join(tmpdir(), `daohua-trait-desc-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/combatConstants.ts')],
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
          contents: 'export const getT = () => (key) => key;',
          loader: 'js',
        }))
      },
    },
  ],
})

const { formatTraitDescription, TRAIT_DEFINITIONS } = await import(pathToFileURL(tempOut).href)

test.after(() => {
  try {
    fs.unlinkSync(tempOut)
  } catch {}
})

test('特质描述：Lv.1 返回原始出厂描述', () => {
  for (const [id, meta] of Object.entries(TRAIT_DEFINITIONS)) {
    assert.equal(formatTraitDescription(id, 1), meta.description)
  }
})

test('特质描述：纯正向特质升级按 5%/级线性成长', () => {
  // 天生蛮力 (Q1)：物理攻击提升 5% -> Lv.2 5.3% -> Lv.3 5.5%
  assert.equal(
    formatTraitDescription('trait.tianshengmanli', 2),
    '骨壮筋强撼岳峦。物理攻击提升 5.3%'
  )
  assert.equal(
    formatTraitDescription('trait.tianshengmanli', 3),
    '骨壮筋强撼岳峦。物理攻击提升 5.5%'
  )
})

test('特质描述：机制特质升级时长与嘲讽转化提升', () => {
  // 看不到我：持续 10 秒 -> Lv.2 10.5 秒
  assert.equal(
    formatTraitDescription('trait.bukandaowo', 2),
    '身遁虚无匿形迹。开战后获得隐身状态，气机消弭避开敌方视线，持续 10.5 秒'
  )
  // 力微饭否：100% -> Lv.2 105%
  assert.equal(
    formatTraitDescription('trait.liweifanfou', 2),
    '不动如山撼未动。受到伤害时以坚韧气魄招引敌意，将所受之威的 105% 化为敌方瞩目'
  )
})

test('特质描述：双刃剑特质升级时正向增益提升，负面惩罚锁定不变', () => {
  // 1. 万剑归一：物攻/暴击/破招提升，最大生命降低 25% 锁定，受到全伤害增加 15% 锁定
  const wj1 = formatTraitDescription('trait.wanjianguiyi', 1)
  const wj2 = formatTraitDescription('trait.wanjianguiyi', 2)
  const wj10 = formatTraitDescription('trait.wanjianguiyi', 10)
  assert.equal(wj1, '绝意一剑无死生。物理攻击提升 60%、暴击提升 50%、破招提升 45%、最大生命降低 25%、受到全伤害增加 15%')
  assert.equal(wj2, '绝意一剑无死生。物理攻击提升 63%、暴击提升 52.5%、破招提升 47.3%、最大生命降低 25%、受到全伤害增加 15%')
  assert.equal(wj10, '绝意一剑无死生。物理攻击提升 87%、暴击提升 72.5%、破招提升 65.3%、最大生命降低 25%、受到全伤害增加 15%')

  // 2. 不动明王：双抗/生命提升，全伤害降低 30% 锁定
  const bd2 = formatTraitDescription('trait.budongmingwang', 2)
  assert.equal(bd2, '明王镇世不动尊。双抗各提升 115.5%、最大生命提升 89.3%、全伤害降低 30%')

  // 3. 药毒同源：回复/生命提升，全伤害降低 25% 锁定
  const yd2 = formatTraitDescription('trait.yaodutongyuan', 2)
  assert.equal(yd2, '药毒归一逆生死。生命与灵力自动回复各提升 168%、最大生命提升 78.8%、全伤害降低 25%')

  // 4. 九幽噬灵：术攻/灵力/暴击提升，护甲降低 30%、闪避降低 25% 锁定
  const jy2 = formatTraitDescription('trait.jiuyoushiling', 2)
  assert.equal(jy2, '九幽吞噬逆天穹。术法攻击提升 63%、最大灵力提升 78.8%、暴击提升 42%、护甲降低 30%、闪避降低 25%')

  // 5. 天煞狂骨：双攻/暴击提升，回复降低 50%、免爆降低 25% 锁定
  const ts2 = formatTraitDescription('trait.tianshekuanggu', 2)
  assert.equal(ts2, '天煞孤星命入骨。双攻各提升 47.3%、暴击提升 63%、生命自动回复降低 50%、免爆降低 25%')

  // 6. 厚土坤舆身：双抗/生命提升，全伤害降低 15% 锁定
  const ht2 = formatTraitDescription('trait.houtukunyushen', 2)
  assert.equal(ht2, '坤舆厚德载千钧。双抗各提升 73.5%、最大生命提升 57.8%、全伤害降低 15%')

  // 7. 烈阳狂战骨：物攻/暴击破招提升，最大生命降低 15% 锁定
  const ly2 = formatTraitDescription('trait.lieyangkuangzhangu', 2)
  assert.equal(ly2, '烈阳燃尽生与死。物理攻击提升 44.6%、暴击与破招各提升 36.8%、最大生命降低 15%')

  // 8. 冥海噬魂体：术攻/灵力/暴击提升，护甲降低 20% 锁定
  const mh2 = formatTraitDescription('trait.minghaishihunti', 2)
  assert.equal(mh2, '冥海无涯纳万川。术法攻击提升 44.6%、最大灵力提升 52.5%、暴击提升 31.5%、护甲降低 20%')

  // 9. 枯荣长青诀：回复/生命提升，全伤害降低 15% 锁定
  const kr2 = formatTraitDescription('trait.kurongchangqingjue', 2)
  assert.equal(kr2, '一枯一荣皆生机。生命与灵力自动回复各提升 115.5%、最大生命提升 52.5%、全伤害降低 15%')
})


test('机制特质：等级成长只提升收益，固定惩罚与持续时间不变', () => {
  assert.equal(Object.keys(TRAIT_DEFINITIONS).length, 38)
  assert.match(formatTraitDescription('trait.chujian', 3), /330%，持续 2 秒/)
  assert.match(formatTraitDescription('trait.yusuzebuda', 3), /110%，伤害与治疗增幅各降低 100 点/)
  assert.match(formatTraitDescription('trait.woyaodashige', 3), /11 点/)
  assert.match(formatTraitDescription('trait.dantiaowang', 3), /22 点/)
  assert.match(formatTraitDescription('trait.xuyi', 3), /实际伤害的 110%/)
  assert.match(formatTraitDescription('trait.ranhun', 3), /2.2%.*1 点.*5 秒/)
  assert.equal(TRAIT_DEFINITIONS['trait.xuyi'].quality, 6)
  assert.equal(TRAIT_DEFINITIONS['trait.ranhun'].cost, 16)
  assert.equal(TRAIT_DEFINITIONS['trait.jianyuxin'].quality, 5)
  assert.equal(TRAIT_DEFINITIONS['trait.jianyuxin'].cost, 12)
  assert.match(formatTraitDescription('trait.jianyuxin', 1), /物理与术法攻击各提升 5%；拥有「移形」时，伤害型技能的主要目标无视仇恨随机选取/)
  assert.match(formatTraitDescription('trait.jianyuxin', 3), /物理与术法攻击各提升 5.5%；拥有「移形」时/)
  assert.equal(TRAIT_DEFINITIONS['trait.huoshen'].quality, 5)
  assert.equal(TRAIT_DEFINITIONS['trait.huoshen'].cost, 12)
  assert.match(formatTraitDescription('trait.huoshen', 1), /火属性增伤提升 10%/)
  assert.match(formatTraitDescription('trait.huoshen', 2), /火属性增伤提升 10\.5%/)
  assert.match(formatTraitDescription('trait.huoshen', 3), /火属性增伤提升 11%/)
})
