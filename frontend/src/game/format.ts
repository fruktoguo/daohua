import type { CombatTrigger } from './types'
import { getT, useI18nStore } from '../i18n'

const numberFormatters = new Map<string, Intl.NumberFormat>()

// 数字分组随当前语言切换；Intl.NumberFormat 按 locale 缓存，避免每次渲染重建。
export function formatNumber(value: number): string {
  const locale = useI18nStore.getState().locale
  let formatter = numberFormatters.get(locale)
  if (!formatter) {
    formatter = new Intl.NumberFormat(locale)
    numberFormatters.set(locale, formatter)
  }
  return formatter.format(value)
}

// 触发条件枚举 -> 展示文案统一走 i18n；组合语序由模板键承载（中英语序不同）。
export function describeCombatTrigger(trigger: CombatTrigger): string {
  const t = getT()
  const dependency = t(`game.trigger.dependency.${trigger.dependency}`)
  const condition = t(`game.trigger.condition.${trigger.condition}`)
  const comparator = t(`game.trigger.comparator.${trigger.comparator}`)
  if (trigger.comparator === 'is-active' || trigger.comparator === 'is-inactive') {
    return t('game.trigger.template.status', { dependency, condition, comparator })
  }
  const isPercentage = trigger.condition.includes('percentage')
  return t('game.trigger.template.value', {
    dependency,
    condition,
    comparator,
    value: formatNumber(trigger.value),
    unit: isPercentage ? '%' : '',
  })
}
