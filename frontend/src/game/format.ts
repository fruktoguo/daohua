import type { CombatTrigger } from './types'

const numberFormatter = new Intl.NumberFormat('zh-CN')

export function formatNumber(value: number): string {
  return numberFormatter.format(value)
}

export function describeCombatTrigger(trigger: CombatTrigger): string {
  const dependency = trigger.dependency === 'target-enemy' ? '目标' : '自身'
  const condition = {
    'current-hp': '当前生命',
    'current-mana': '当前灵力',
    'missing-hp': '生命缺口',
    'missing-mana': '灵力缺口',
    'current-hp-percentage': '生命百分比',
    'missing-hp-percentage': '生命缺口百分比',
    'current-mana-percentage': '灵力百分比',
    'missing-mana-percentage': '灵力缺口百分比',
    'buff-stacks': '效果层数',
    'stun-status': '定身',
    'blind-status': '致盲',
    'silence-status': '沉默',
  }[trigger.condition] ?? trigger.condition
  const comparator = {
    'less-than-equal': '不高于',
    'greater-than-equal': '不低于',
    'is-active': '已生效',
    'is-inactive': '未生效',
  }[trigger.comparator] ?? trigger.comparator
  if (trigger.comparator === 'is-active' || trigger.comparator === 'is-inactive') {
    return `${dependency}${condition}${comparator}`
  }
  const isPercentage = trigger.condition.includes('percentage')
  return `${dependency}${condition}${comparator} ${formatNumber(trigger.value)}${isPercentage ? '%' : ''}`
}
