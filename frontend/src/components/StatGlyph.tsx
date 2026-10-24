import React, { memo } from 'react'
import type { PillTone } from './PillLabel'

/**
 * 属性小图标静态映射表 (由 Vite import.meta.glob 自动收集)
 */
const GLYPH_MODULES = import.meta.glob('../assets/glyphs/glyph.*.webp', {
  eager: true,
  import: 'default',
}) as Record<string, string>

/**
 * 根据属性标识解析小图标资产 URL
 * @param glyphId 属性名或 glyph 标识（如 'hp', 'mp', 'phys-atk', 'spell-atk'）
 */
export function resolveGlyphUrl(glyphId: string): string | null {
  const normalized = glyphId.replace(/^glyph\./, '')
  const path = `../assets/glyphs/glyph.${normalized}.webp`
  return GLYPH_MODULES[path] ?? null
}
/**
 * 属性标识与中文词条到小图标的智能反查映射表
 */
const STAT_KEY_TO_GLYPH: Record<string, string> = {
  // 基础四维
  hp: 'hp',
  maxHitpoints: 'hp',
  hitpoints: 'hp',
  生命: 'hp',
  气血: 'hp',
  生命上限: 'hp',
  气血上限: 'hp',

  mp: 'mp',
  maxManapoints: 'mp',
  manapoints: 'mp',
  法力: 'mp',
  灵力: 'mp',
  法力上限: 'mp',
  灵力上限: 'mp',

  hpRegen: 'hp-regen',
  hitpointRegen: 'hp-regen',
  生命回复: 'hp-regen',
  气血回复: 'hp-regen',

  mpRegen: 'mp-regen',
  manapointRegen: 'mp-regen',
  法力回复: 'mp-regen',
  灵力回复: 'mp-regen',

  physAtk: 'phys-atk',
  physicalAttack: 'phys-atk',
  物理攻击: 'phys-atk',
  物攻: 'phys-atk',
  攻击力: 'phys-atk',
  damage: 'phys-atk',

  spellAtk: 'spell-atk',
  spellAttack: 'spell-atk',
  术法攻击: 'spell-atk',
  法攻: 'spell-atk',

  armor: 'armor',
  physicalArmor: 'armor',
  物理护甲: 'armor',
  护甲: 'armor',
  物防: 'armor',

  magicRes: 'magic-res',
  spellResistance: 'magic-res',
  mr: 'magic-res',
  术法抗性: 'magic-res',
  抗性: 'magic-res',
  法防: 'magic-res',

  accuracy: 'accuracy',
  hit: 'accuracy',
  命中: 'accuracy',
  命中率: 'accuracy',

  evasion: 'evasion',
  eva: 'evasion',
  闪避: 'evasion',
  闪避率: 'evasion',

  crit: 'crit',
  critical: 'crit',
  criticalChance: 'crit',
  暴击: 'crit',
  暴击率: 'crit',

  critAvoid: 'crit-avoid',
  criticalResistance: 'crit-avoid',
  tenacity: 'crit-avoid',
  免暴: 'crit-avoid',
  韧性: 'crit-avoid',

  parryBreak: 'parry-break',
  breakPower: 'parry-break',
  破招: 'parry-break',
  破防: 'parry-break',

  parryResolve: 'parry-resolve',
  化解: 'parry-resolve',
  格挡: 'parry-resolve',

  // 时序与增幅
  attackSpeed: 'attack-speed',
  attackSpeedPoints: 'attack-speed',
  攻击攻速: 'attack-speed',
  出招速度: 'attack-speed',
  攻速: 'attack-speed',

  actionSpeed: 'action-speed',
  actionSpeedPoints: 'action-speed',
  行动加速: 'action-speed',
  行动速度: 'action-speed',

  cooldownReduction: 'cooldown-reduction',
  cooldownReductionPoints: 'cooldown-reduction',
  冷却缩减: 'cooldown-reduction',
  冷却时间: 'cooldown-reduction',
  冷却: 'cooldown-reduction',

  critDamage: 'crit-damage',
  criticalDamage: 'crit-damage',
  criticalDamageBonusPoints: 'crit-damage',
  暴击伤害: 'crit-damage',

  basicAttackBonus: 'basic-attack-bonus',
  basicAttackBonusPoints: 'basic-attack-bonus',
  普攻增幅: 'basic-attack-bonus',

  skillBonus: 'skill-bonus',
  skillBonusPoints: 'skill-bonus',
  技能增幅: 'skill-bonus',

  threatBonus: 'threat-bonus',
  threatBonusPoints: 'threat-bonus',
  威胁增幅: 'threat-bonus',

  healingBonus: 'healing-bonus',
  healingBonusPoints: 'healing-bonus',
  治疗增幅: 'healing-bonus',
  治疗效果: 'healing-bonus',

  hpDrain: 'hp-drain',
  hpDrainPoints: 'hp-drain',
  气血汲取: 'hp-drain',
  吸血: 'hp-drain',

  mpDrain: 'mp-drain',
  mpDrainPoints: 'mp-drain',
  灵力汲取: 'mp-drain',
  吸蓝: 'mp-drain',

  damageReflect: 'damage-reflect',
  damageReflectPoints: 'damage-reflect',
  伤害反弹: 'damage-reflect',

  // 增减伤
  damageAbsorption: 'total-damage',
  damageAbsorptionPoints: 'total-damage',
  伤害减免: 'total-damage',
  总伤害减免: 'total-damage',
  总增伤: 'total-damage',

  physicalDamageBonusPoints: 'phys-damage',
  fixedPhysicalDamageBonus: 'phys-damage',
  物理增伤: 'phys-damage',
  physicalDamageReductionPoints: 'phys-damage',
  fixedPhysicalDamageReduction: 'phys-damage',
  物理减伤: 'phys-damage',

  spellDamageBonusPoints: 'spell-damage',
  fixedSpellDamageBonus: 'spell-damage',
  术法增伤: 'spell-damage',
  spellDamageReductionPoints: 'spell-damage',
  fixedSpellDamageReduction: 'spell-damage',
  术法减伤: 'spell-damage',

  // 五行
  metalDamageBonusPoints: 'element-metal',
  metalDamageReductionPoints: 'element-metal',
  woodDamageBonusPoints: 'element-wood',
  woodDamageReductionPoints: 'element-wood',
  waterDamageBonusPoints: 'element-water',
  waterDamageReductionPoints: 'element-water',
  fireDamageBonusPoints: 'element-fire',
  fireDamageReductionPoints: 'element-fire',
  earthDamageBonusPoints: 'element-earth',
  earthDamageReductionPoints: 'element-earth',

  // 底蕴、经验与修行
  cultivationExpBonusPoints: 'experience',
  修为获取: 'experience',
  修为: 'experience',
  灵气: 'experience',
  境界: 'realm',
  等级: 'realm',
  底蕴: 'foundation',
  道行底蕴: 'foundation',
  战斗经验: 'combat-exp',
  历练: 'combat-exp',
  寿元: 'bio-lifespan',
  修炼: 'bio-cultivation',
  修炼效率: 'bio-cultivation',
  修炼增效: 'bio-cultivation',
  采集: 'alchemy',
  采集产出: 'alchemy',
  采集增效: 'alchemy',
  炼丹: 'alchemy',
  炼丹产出: 'alchemy',
  炼丹增效: 'alchemy',
  持续时间: 'cooldown-reduction',
  药效时长: 'cooldown-reduction',
  有效时长: 'cooldown-reduction',
}
/**
 * 智能根据属性标识或中文词条标签反查对应的图标 ID
 */
export function resolveStatGlyph(keyOrLabel?: string | null): string | null {
  if (!keyOrLabel) return null
  const cleaned = keyOrLabel.trim()
  if (STAT_KEY_TO_GLYPH[cleaned]) return STAT_KEY_TO_GLYPH[cleaned]

  // 前缀/包含模糊匹配
  for (const [k, glyph] of Object.entries(STAT_KEY_TO_GLYPH)) {
    if (cleaned.includes(k) || k.includes(cleaned)) {
      return glyph
    }
  }
  return null
}

/**
 * 极简二值属性小图标组件 (StatGlyph)
 *
 * 采用 CSS mask 遮罩体系：
 * 资产本身为纯白二值剪影，通过 mask-image 与 currentColor / background-color 动态着色，
 * 天然自适应父级色系（--emerald / --cyan / --crimson / --gold 等），无需额外请求不同颜色切片。
 */
export const StatGlyph = memo(function StatGlyph({
  glyph,
  tone,
  className = '',
  size = 16.5,
}: {
  glyph: string
  tone?: PillTone
  className?: string
  size?: number
}): React.JSX.Element | null {
  const url = resolveGlyphUrl(glyph)
  if (!url) return null

  return (
    <span
      className={`stat-glyph ${tone ? `pill-tone--${tone}` : ''} ${className}`.trim()}
      style={{
        display: 'inline-block',
        width: size,
        height: size,
        maskImage: `url(${url})`,
        WebkitMaskImage: `url(${url})`,
        maskSize: 'contain',
        WebkitMaskSize: 'contain',
        maskRepeat: 'no-repeat',
        WebkitMaskRepeat: 'no-repeat',
        maskPosition: 'center',
        WebkitMaskPosition: 'center',
        backgroundColor: 'currentColor',
        verticalAlign: '-0.15em',
        flexShrink: 0,
      }}
      aria-hidden="true"
    />
  )
})
