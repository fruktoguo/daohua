import { formatNumber } from './format'

/**
 * Buff 数值效果通道名（wire `channel` 字符串，与配表键同名；总纲 §7.9.5.3）。
 * 服务端 `BuffEffectRegistry` 的前端镜像；新增通道时两端同步登记。
 */
export type BuffEffectChannel =
  | 'hpRegen'
  | 'mpRegen'
  | 'hpRegenPostRatio'
  | 'mpRegenPostRatio'
  | 'hpRegenPerSecond'
  | 'threatPerSecondMaxHpPoints'
  | 'attackSpeedPoints'
  | 'attackPower'
  | 'accuracy'
  | 'critical'
  | 'damageBonusPoints'
  | 'damageAbsorptionPoints'
  | 'resistancePoints'
  | 'threatPoints'
  | 'evasion'
  | 'evasionPostRatio'

/** 单条效果载荷：通道 + 每层基础值（resistancePoints 实例型通道为总值）。 */
export interface BuffEffectEntry {
  channel: BuffEffectChannel
  value: number
}

/** 数值展示格式（决定 value 的换算与符号呈现）。 */
export type BuffChannelFormat =
  | 'flat' // +N 固定值
  | 'percentPoint' // 点数 → signedPercent(v/100)
  | 'percentRaw' // 比率原值 → signedPercent(v)
  | 'absorption' // NonLinear 减伤 x/(100+|x|)
  | 'resistance' // 正值 1+x/100、负值 -|x|/(100+|x|)
  | 'threatPerSecond' // +x.x%（每秒威胁）

/** 通道展示注册表条目：效果行 pill 的全部呈现元数据。 */
export interface BuffChannelDisplay {
  /** 效果行属性名词条键（market.effect.badge* 命名约定）。 */
  labelKey: string
  /** 属性小图标标识。 */
  glyph: string
  /** 正值 / 负值色调。 */
  positiveTone: string
  negativeTone: string
  /** 数值格式。 */
  format: BuffChannelFormat
  /** 展示是否按层数放大（层数承载数值的通道，与服务端结算同口径）。 */
  perStack: boolean
}

/**
 * Buff 效果通道展示注册表（单一真源）：凡引用 Buff 效果的悬浮行、图鉴描述、
 * 战场方块色调与模拟器统计一律经本表渲染，不在消费点逐字段 if-else。
 */
export const BUFF_CHANNELS: Record<BuffEffectChannel, BuffChannelDisplay> = {
  hpRegen: { labelKey: 'market.effect.badgeHpRegen', glyph: 'hp-regen', positiveTone: 'emerald', negativeTone: 'crimson', format: 'flat', perStack: false },
  mpRegen: { labelKey: 'market.effect.badgeMpRegen', glyph: 'mp-regen', positiveTone: 'cyan', negativeTone: 'crimson', format: 'flat', perStack: false },
  hpRegenPostRatio: { labelKey: 'market.effect.badgeHpPostRatio', glyph: 'hp-regen', positiveTone: 'emerald', negativeTone: 'crimson', format: 'percentRaw', perStack: false },
  mpRegenPostRatio: { labelKey: 'market.effect.badgeMpPostRatio', glyph: 'mp-regen', positiveTone: 'cyan', negativeTone: 'crimson', format: 'percentRaw', perStack: false },
  hpRegenPerSecond: { labelKey: 'market.effect.badgeHpRegenPerSecond', glyph: 'hp-regen', positiveTone: 'emerald', negativeTone: 'crimson', format: 'flat', perStack: true },
  threatPerSecondMaxHpPoints: { labelKey: 'market.effect.badgeThreatSec', glyph: 'threat-bonus', positiveTone: 'amber', negativeTone: 'crimson', format: 'threatPerSecond', perStack: false },
  attackSpeedPoints: { labelKey: 'market.effect.badgeAttackSpeed', glyph: 'attack-speed', positiveTone: 'purple', negativeTone: 'crimson', format: 'percentPoint', perStack: false },
  attackPower: { labelKey: 'market.effect.badgeAttackPower', glyph: 'phys-atk', positiveTone: 'gold', negativeTone: 'crimson', format: 'flat', perStack: true },
  accuracy: { labelKey: 'market.effect.badgeAccuracy', glyph: 'accuracy', positiveTone: 'jade', negativeTone: 'crimson', format: 'flat', perStack: true },
  critical: { labelKey: 'market.effect.badgeCritical', glyph: 'crit', positiveTone: 'gold', negativeTone: 'crimson', format: 'flat', perStack: true },
  damageBonusPoints: { labelKey: 'market.effect.badgeDamageBonus', glyph: 'phys-damage', positiveTone: 'gold', negativeTone: 'crimson', format: 'percentPoint', perStack: false },
  damageAbsorptionPoints: { labelKey: 'market.effect.badgeDamageAbsorption', glyph: 'total-damage', positiveTone: 'jade', negativeTone: 'crimson', format: 'absorption', perStack: false },
  resistancePoints: { labelKey: 'market.effect.badgeResistance', glyph: 'foundation', positiveTone: 'jade', negativeTone: 'crimson', format: 'resistance', perStack: false },
  threatPoints: { labelKey: 'market.effect.badgeThreat', glyph: 'threat-bonus', positiveTone: 'amber', negativeTone: 'crimson', format: 'percentPoint', perStack: false },
  evasion: { labelKey: 'market.effect.badgeEvasion', glyph: 'evasion', positiveTone: 'cyan', negativeTone: 'crimson', format: 'flat', perStack: true },
  evasionPostRatio: { labelKey: 'market.effect.badgeEvasionPostRatio', glyph: 'evasion', positiveTone: 'cyan', negativeTone: 'crimson', format: 'percentRaw', perStack: false },
}

/** wire 通道名 → 是否已登记（未登记通道不参与展示渲染）。 */
export function isBuffEffectChannel(channel: string): channel is BuffEffectChannel {
  return Object.prototype.hasOwnProperty.call(BUFF_CHANNELS, channel)
}

function signedPercent(value: number): string {
  const percent = Math.round(value * 100)
  return `${percent > 0 ? '+' : ''}${percent}%`
}

/**
 * 按通道格式化展示数值（层数承载数值先乘层数；isPerStack 的规格悬浮在单层时附「/层」后缀）。
 */
export function formatBuffChannelValue(
  info: BuffChannelDisplay,
  value: number,
  stacks: number,
  isPerStack = false,
): string {
  const scaled = info.perStack ? value * stacks : value
  const perStackSuffix = isPerStack && stacks <= 1 ? '/层' : ''
  switch (info.format) {
    case 'flat':
      return `${scaled > 0 ? '+' : ''}${formatNumber(scaled)}${perStackSuffix}`
    case 'percentPoint':
      return signedPercent(scaled / 100)
    case 'percentRaw':
      return signedPercent(scaled)
    case 'absorption': {
      const percent = Math.abs(+(value / (100 + Math.abs(value)) * 100).toFixed(1))
      return `${value > 0 ? '+' : '-'}${percent}%`
    }
    case 'resistance': {
      const ratio = value >= 0 ? value / 100 : -Math.abs(value) / (100 + Math.abs(value))
      return signedPercent(ratio)
    }
    case 'threatPerSecond':
      return `+${(value / 100).toFixed(1)}%`
  }
}

/**
 * 配表功法 Buff 规格（平铺字段，catalog 原样透传）→ 效果载荷；按成长规则缩放
 * （GrowsWithLevel 通道乘 growth，双抗破防比率豁免——与服务端 BuffEffectParser 同式）。
 */
export function specToBuffEffects(
  spec: object,
  growth: number,
): BuffEffectEntry[] {
  const effects: BuffEffectEntry[] = []
  for (const [key, raw] of Object.entries(spec)) {
    if (raw === undefined || raw === 0 || !isBuffEffectChannel(key)) continue
    // 双抗破防比率不吃成长（与服务端 GrowsWithLevel 豁免同式）。
    const value = key === 'resistancePoints' ? raw : raw * growth
    effects.push({ channel: key, value })
  }
  return effects
}
