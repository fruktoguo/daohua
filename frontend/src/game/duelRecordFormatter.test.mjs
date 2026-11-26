import assert from 'node:assert/strict'
import test from 'node:test'
import { formatDuelBattleRecord } from './duelRecordFormatter.ts'

const t = (key, params = {}) => {
  const dict = {
    'combat.stats.duelRecordBasicAttack': '普通攻击',
    'combat.stats.duelRecordPeriodicAction': '周期结算',
    'combat.stats.duelRecordDamageTypePhysical': '物理伤害',
    'combat.stats.duelRecordDamageTypeSpell': '术法伤害',
    'combat.stats.duelRecordDamageTypeTrue': '真实伤害',
    'combat.stats.duelRecordElementMetal': '金属性',
    'combat.stats.duelRecordElementWood': '木属性',
    'combat.stats.duelRecordElementWater': '水属性',
    'combat.stats.duelRecordElementFire': '火属性',
    'combat.stats.duelRecordElementEarth': '土属性',
    'combat.stats.duelRecordMechanicBreak': '破招',
    'combat.stats.duelRecordMechanicParry': '化解',
    'combat.stats.duelRecordMechanicCrit': '暴击',
    'combat.stats.duelRecordMechanicDodge': '闪避',
    'combat.stats.duelRecordVerbUse': `${params.actor ?? ''} 使用 `,
    'combat.stats.duelRecordVerbBasic': `${params.actor ?? ''} 使用 普通攻击`,
    'combat.stats.duelRecordVerbTrigger': `${params.actor ?? ''} 触发 `,
    'combat.stats.duelRecordVerbActivate': `${params.actor ?? ''} 启用 `,
    'combat.stats.duelRecordDodgedBy': `被 ${params.target ?? ''} `,
    'combat.stats.duelRecordDealtDamage': `对 ${params.target ?? ''} 造成 ${params.value ?? ''} 点${params.damageType ?? ''}`,
    'combat.stats.duelRecordHealSelf': `回复 ${params.value ?? ''} 点气血`,
    'combat.stats.duelRecordHealOther': `为 ${params.target ?? ''} 回复 ${params.value ?? ''} 点气血`,
    'combat.stats.duelRecordManaSelf': `回复 ${params.value ?? ''} 点灵力`,
    'combat.stats.duelRecordManaOther': `为 ${params.target ?? ''} 回复 ${params.value ?? ''} 点灵力`,
    'combat.stats.duelRecordBuffSelf': '获得状态 ',
    'combat.stats.duelRecordBuffOther': `使 ${params.target ?? ''} 获得状态 `,
    'combat.stats.duelRecordHpDrain': `${params.source ?? ''} 气血汲取 ${params.value ?? ''} 点`,
    'combat.stats.duelRecordManaDrain': `从 ${params.target ?? ''} 汲取灵力 ${params.value ?? ''} 点`,
    'combat.stats.duelRecordReflect': `${params.source ?? ''} 反弹 ${params.value ?? ''} 点${params.damageType ?? ''}给 ${params.target ?? ''}`,
    'combat.stats.duelRecordTraitBurstPrefix': ` 爆发对 ${params.target ?? ''} 造成 ${params.value ?? ''} 点${params.damageType ?? ''}`,
    'combat.stats.duelRecordTraitBurstFallback': `特质爆发对 ${params.target ?? ''} 造成 ${params.value ?? ''} 点${params.damageType ?? ''}`,
    'combat.stats.duelRecordFormationStrikeSource': `${params.source ?? ''} 触发 `,
    'combat.stats.duelRecordFormationStrikeTarget': ` 对 ${params.target ?? ''} 造成 ${params.value ?? ''} 点${params.damageType ?? ''}`,
    'combat.stats.duelRecordUpkeepMana': ` 维持消耗 ${params.value ?? ''} 点灵力`,
    'combat.stats.duelRecordUpkeepHp': ` 维持消耗 ${params.value ?? ''} 点气血`,
    'combat.stats.duelRecordUpkeepFallback': `阵盘维持消耗 ${params.value ?? ''}`,
    'combat.stats.duelRecordDowned': `${params.target ?? ''} 力竭倒地`,
    'combat.stats.duelRecordListSep': '、',
    'combat.stats.duelRecordClauseJoin': '；',
    'combat.stats.duelRecordSentenceComma': '，',
    'combat.stats.duelRecordSentenceEnd': '。',
  }
  return dict[key] ?? key
}

const catalog = {
  abilities: [
    { abilityId: 'ability.huoyan-fengbao', name: '火焰风暴' },
    { abilityId: 'ability.zhanyu', name: '斩玉' },
  ],
  items: [
    { itemId: 'item.tianlei-formation-disk', name: '天雷阵盘' },
  ],
  buffs: [
    { buffId: 'formation.tianlei', name: '天雷阵' },
  ],
}

test('formatDuelBattleRecord 合并多目标伤害并保留功法/物品/特质/状态/机制引用标识', () => {
  const record = {
    id: 'duel-1',
    at: 1000,
    dur: 12.5,
    out: 1,
    tr: true,
    u: [
      { n: '玩家1', ally: true },
      { n: '玩家2', ally: false },
      { n: '玩家3', ally: false },
    ],
    defs: [
      'ability.huoyan-fengbao',
      'ability.zhanyu',
      '踏云',
      'item.tianlei-formation-disk',
      'trait.xuyi',
      'buff.ranhun',
    ],
    acts: [
      {
        t: 0.4,
        a: 0,
        k: 1,
        d: 0, // ability.huoyan-fengbao
        fx: [
          { s: 0, tg: 1, ek: 0, dk: 1, el: 4, fl: 5, v: 120.5 }, // 火属性术法 + 暴击
          { s: 0, tg: 2, ek: 0, dk: 1, el: 4, fl: 5, v: 120.5 },
          { s: 0, tg: 0, ek: 4, v: 18.2 }, // 气血汲取
          { s: 1, tg: 0, ek: 8, dk: 1, el: 2, fl: 1, v: 33, df: 3 }, // 天雷阵盘反击
          { s: 0, tg: 1, ek: 7, dk: 2, el: 0, fl: 1, v: 88, df: 4 }, // 蓄意真实伤害爆发
        ],
      },
      {
        t: 1.2,
        a: 0,
        k: 1,
        d: 1, // ability.zhanyu
        o: 2, // 切态踏云
        fx: [
          { s: 0, tg: 1, ek: 0, dk: 0, el: 0, fl: 2, v: 0 }, // 闪避
        ],
      },
    ],
  }

  const formatted = formatDuelBattleRecord(record, catalog, t)
  assert.equal(formatted.length, 2)

  // 第一条：火属性术法伤害多目标合并 + 暴击 + 阵盘 item 引用 + 蓄意 trait 引用
  const firstRefs = formatted[0].segments.filter(s => s.type === 'reference')
  assert.ok(firstRefs.some(r => r.kind === 'ability' && r.id === 'ability.huoyan-fengbao'))
  assert.ok(firstRefs.some(r => r.kind === 'mechanic' && r.id === 'crit'))
  assert.ok(firstRefs.some(r => r.kind === 'item' && r.id === 'item.tianlei-formation-disk'))
  assert.ok(firstRefs.some(r => r.kind === 'trait' && r.id === 'trait.xuyi'))

  const firstText = formatted[0].segments.map(s => (s.type === 'text' ? s.value : s.label)).join('')
  assert.match(firstText, /玩家2、玩家3/)
  assert.match(firstText, /火属性术法伤害/)
  assert.match(firstText, /真实伤害/)

  // 第二条：切态覆写显示踏云，但仍保留原 ability.zhanyu ID
  const secondAbility = formatted[1].segments.find(s => s.type === 'reference' && s.kind === 'ability')
  assert.equal(secondAbility?.id, 'ability.zhanyu')
  assert.equal(secondAbility?.label, '踏云')
})
