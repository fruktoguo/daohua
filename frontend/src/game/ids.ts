import { getT, type TFunction } from '../i18n'
import type { ActionDefinition, ActionType } from './types'

/**
 * 游戏四大核心行动类型数组（修炼、探索、炼丹、战斗）
 */
export const ACTION_TYPES: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy', 'combat']

/**
 * 行动定义纯数据核心（不包含多语言展示文案）
 */
// 行动定义只保留纯数据核心：显示文本统一走 i18n（game.action.<id>.*），
// 由 resolveActionDefinition 在渲染期按当前语言解析，避免数据表内重复存放文案。
export type ActionDefinitionCore = Omit<ActionDefinition, 'name' | 'description'>

/**
 * 静态兜底行动核心定义列表
 */
const ACTION_DEFINITION_CORES: readonly ActionDefinitionCore[] = [
  {
    id: 'cultivation.draw-qi',
    type: 'cultivation',
    durationSeconds: 10,
    outputs: {},
  },
  {
    id: 'explore.gather-ningxue-grass',
    type: 'exploration',
    durationSeconds: 8,
    outputs: { 'item.ningxue-grass': 3 },
  },
  {
    id: 'explore.chop-peach-wood',
    type: 'exploration',
    durationSeconds: 6,
    outputs: { 'item.peach-wood': 1 },
  },
  {
    id: 'explore.mine-dark-iron',
    type: 'exploration',
    durationSeconds: 6,
    outputs: { 'item.dark-iron': 1 },
  },
  {
    id: 'alchemy.refine-qi-pill',
    type: 'alchemy',
    durationSeconds: 12,
    inputs: { 'item.ningxue-grass': 3, 'item.morning-dew': 1 },
    outputs: { 'item.qi-gathering-pill': 1 },
  },
  {
    id: 'alchemy.refine-zhixue-powder',
    type: 'alchemy',
    durationSeconds: 10,
    inputs: { 'item.ningxue-grass': 2 },
    outputs: { 'item.zhixue-powder': 1 },
  },
  {
    id: 'combat.hunt-qi-beast',
    type: 'combat',
    durationSeconds: 0,
    executionKind: 'combat-encounter',
    combatEncounterId: 'encounter.twilight-pass',
  },
  {
    id: 'combat.sweep-twilight-valley',
    type: 'combat',
    durationSeconds: 0,
    executionKind: 'combat-encounter',
    combatEncounterId: 'encounter.twilight-valley',
  },
]

/**
 * 解析单个行动的完整定义（注入当前语言的名称和描述）
 * @param id 行动唯一标识符
 */
export function resolveActionDefinition(id: string): ActionDefinition | undefined {
  const core = ACTION_DEFINITION_CORES.find(action => action.id === id)
  if (!core) return undefined
  const t = getT()
  return {
    ...core,
    name: t(`game.action.${id}.name`),
    description: t(`game.action.${id}.description`),
  }
}

/**
 * 解析全部离线兜底行动定义
 */
// 离线兜底目录：仅在服务端目录尚未到达时展示；语言取模块加载时的当前语言，
// 服务端目录到达后即被整体替换。
export function resolveActionDefinitions(): ActionDefinition[] {
  return ACTION_DEFINITION_CORES.map(core => resolveActionDefinition(core.id)!)
}

/**
 * 将品阶 (Tier) 换算为修士/物品等级 (Level)
 * 1阶 -> 1级, 2阶 -> 18级, 3阶 -> 30级, N阶(>=2) -> 18 + (N - 2) * 12 级
 */
export function convertTierToLevel(tier = 1): number {
  const t = Math.max(1, Math.floor(tier))
  if (t <= 1) return 1
  return 18 + (t - 2) * 12
}

/**
 * 根据行动配置与其关联的产出/消耗/遭遇，解析行动的真实等级 (Level)
 * 优先级：
 * 1. 产出物品/装备的显式等级或通过品阶换算得到的等级
 * 2. 战斗遭遇 (combatEncounterId) 对应的等级
 * 3. 输入消耗材料的最大等级
 * 4. 默认 1 级
 */
export function resolveActionLevel(
  action: ActionDefinition,
  items?: ReadonlyArray<{ itemId: string; tier?: number }>,
  equipment?: ReadonlyArray<{ equipmentId: string; tier?: number }>,
  encounters?: ReadonlyArray<{ encounterId: string; tier?: number }>
): number {
  if (action.outputs) {
    for (const outId of Object.keys(action.outputs)) {
      const eq = equipment?.find(e => e.equipmentId === outId)
      if (eq && typeof eq.tier === 'number' && eq.tier > 0) return convertTierToLevel(eq.tier)
      const it = items?.find(i => i.itemId === outId)
      if (it && typeof it.tier === 'number' && it.tier > 0) return convertTierToLevel(it.tier)
      const meta = ITEM_META_CORES[outId]
      if (meta?.level && meta.level > 0) {
        return meta.level > 2 ? meta.level : convertTierToLevel(meta.level)
      }
    }
  }

  if (action.combatEncounterId) {
    const enc = encounters?.find(e => e.encounterId === action.combatEncounterId)
    if (enc && typeof enc.tier === 'number' && enc.tier > 0) return convertTierToLevel(enc.tier)
    if (action.combatEncounterId.includes('valley') || action.combatEncounterId.includes('tier2')) return convertTierToLevel(2)
    return convertTierToLevel(1)
  }

  if (action.inputs) {
    let maxTier = 1
    for (const inId of Object.keys(action.inputs)) {
      const eq = equipment?.find(e => e.equipmentId === inId)
      if (eq && typeof eq.tier === 'number' && eq.tier > maxTier) maxTier = eq.tier
      const it = items?.find(i => i.itemId === inId)
      if (it && typeof it.tier === 'number' && it.tier > maxTier) maxTier = it.tier
      const meta = ITEM_META_CORES[inId]
      if (meta?.level && meta.level > maxTier) maxTier = meta.level
    }
    if (maxTier > 1) return convertTierToLevel(maxTier)
  }

  return 1
}

/**
 * 根据行动配置与其关联的产出/消耗/遭遇，解析行动的品质 (Rarity)
 */
export function resolveActionRarity(
  action: ActionDefinition,
  items?: ReadonlyArray<{ itemId: string; tier?: number }>,
  equipment?: ReadonlyArray<{ equipmentId: string; tier?: number }>,
  encounters?: ReadonlyArray<{ encounterId: string; tier?: number }>
): ItemRarity {
  if (action.outputs) {
    for (const outId of Object.keys(action.outputs)) {
      const meta = ITEM_META_CORES[outId]
      if (meta?.rarity) return meta.rarity
      const eq = equipment?.find(e => e.equipmentId === outId)
      if (eq && typeof eq.tier === 'number' && eq.tier > 0) return resolveItemGrade(eq.tier).rarity
      const it = items?.find(i => i.itemId === outId)
      if (it && typeof it.tier === 'number' && it.tier > 0) return resolveItemGrade(it.tier).rarity
    }
  }

  if (action.combatEncounterId) {
    const enc = encounters?.find(e => e.encounterId === action.combatEncounterId)
    if (enc && typeof enc.tier === 'number' && enc.tier > 0) return resolveItemGrade(enc.tier).rarity
    if (action.combatEncounterId.includes('valley') || action.combatEncounterId.includes('tier2')) return 'spirit'
    return 'mundane'
  }

  if (action.inputs) {
    let maxTier = 1
    for (const inId of Object.keys(action.inputs)) {
      const meta = ITEM_META_CORES[inId]
      if (meta?.rarity) return meta.rarity
      const eq = equipment?.find(e => e.equipmentId === inId)
      if (eq && typeof eq.tier === 'number' && eq.tier > maxTier) maxTier = eq.tier
      const it = items?.find(i => i.itemId === inId)
      if (it && typeof it.tier === 'number' && it.tier > maxTier) maxTier = it.tier
    }
    if (maxTier > 1) return resolveItemGrade(maxTier).rarity
  }

  return 'mundane'
}

/**
 * 各行动类型对应的 UI 图标符号与主题色
 */
// 页签仅保留非文本元数据；label 统一经 actionTabLabel() 走 i18n。
export const ACTION_TAB_META: Record<ActionType, { glyph: string; accent: string }> = {
  cultivation: { glyph: '☯', accent: 'cyan' },
  exploration: { glyph: '⌕', accent: 'green' },
  alchemy: { glyph: '⚗', accent: 'violet' },
  combat: { glyph: '⚔', accent: 'orange' },
}

/**
 * 获取行动类型的多语言显示标签
 */
export function actionTabLabel(type: ActionType): string {
  return getT()(`game.tab.${type}`)
}

/**
 * 物品品阶品质枚举：凡品、黄阶、玄阶、地阶、天阶、灵品、圣品、帝品
 */
export type ItemRarity = 'mundane' | 'yellow' | 'mystic' | 'earth' | 'heaven' | 'spirit' | 'saint' | 'emperor'

/**
 * 物品品阶信息（包含品质枚举与本地化文案）
 */
export interface ItemGrade {
  rarity: ItemRarity
  label: string
}

const ITEM_GRADE_ORDER: readonly ItemRarity[] = [
  'mundane', 'yellow', 'mystic', 'earth', 'heaven', 'spirit', 'saint', 'emperor',
]

/**
 * 根据阶位数值 (tier, 1~8) 计算对应的物品品质与多语言文本
 * @param tier 阶位数值
 */
export function resolveItemGrade(tier = 1): ItemGrade {
  const index = Math.max(0, Math.min(ITEM_GRADE_ORDER.length - 1, Math.floor(tier) - 1))
  const rarity = ITEM_GRADE_ORDER[index]
  return { rarity, label: getT()(`game.grade.${rarity}`) }
}

/**
 * 将指定品阶/等阶应用到物品元数据上
 */
export function withItemGrade(metadata: ItemMetadata, tier = metadata.level ?? 1): ItemMetadata {
  const grade = resolveItemGrade(tier)
  return { ...metadata, rarity: grade.rarity, rarityLabel: grade.label, level: tier }
}

/**
 * 物品完整元数据结构体
 */
export interface ItemMetadata {
  /** 物品名称 */
  label: string
  /** 物品图标符号 */
  glyph: string
  /** 色调主题 */
  tone: string
  /** 分类名称（如 "材料", "丹药"） */
  category: string
  /** 品质等级 */
  rarity: ItemRarity
  /** 品质中文/多语言标签（如 "黄阶", "通宝"） */
  rarityLabel: string
  /** 物品等阶 */
  level?: number
  /** 详细描述 */
  description: string
  /** 产出来源（可选） */
  source?: string
  /** 用途说明（可选） */
  usage?: string
}

/**
 * 物品分类多语言键枚举
 */
export type ItemCategoryKey =
  | 'general' | 'material' | 'herb' | 'mineral' | 'pill' | 'food' | 'drink'
  | 'talisman' | 'manual' | 'equipment' | 'chest' | 'currency' | 'resource'
  | 'spiritFood' | 'spiritDrink' | 'loot' | 'rareLoot' | 'manualFragment'
  | 'explorationSupply' | 'alchemySupply' | 'weapon' | 'head' | 'armor'
  | 'legs' | 'boots' | 'trinket' | 'mainHandImplement' | 'twoHandImplement'

/**
 * 解析物品分类的多语言显示文本
 * @param category 分类键名或原始文本
 * @param t 可选的本地化函数（默认取全局 getT）
 */
export function resolveItemCategoryLabel(category?: string | null, t?: TFunction): string {
  if (!category) return (t ?? getT())('game.category.general')
  const fn = t ?? getT()
  const key = `game.category.${category}`
  const translated = fn(key)
  if (translated !== key) {
    return translated
  }
  const lower = category.toLowerCase()
  const lowerKey = `game.category.${lower}`
  const lowerTranslated = fn(lowerKey)
  if (lowerTranslated !== lowerKey) {
    return lowerTranslated
  }
  return category
}

/**
 * 物品元数据纯数据核心
 */
interface ItemMetaCore {
  glyph: string
  tone: string
  rarity: ItemRarity
  level?: number
  category: ItemCategoryKey
  /** 特例覆盖：灵石品质标签「通宝」不随 rarity 推导。 */
  rarityLabelOverride?: 'currency'
}

/**
 * 基础物品静态元数据字典
 */
const ITEM_META_CORES: Record<string, ItemMetaCore> = {
  'item.ningxue-grass': { glyph: '🌿', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.peach-wood': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.dark-iron': { glyph: '⛏️', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.beast-hide': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.beast-bone': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.morning-dew': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.broken-spirit-core': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.demon-dan': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.sha-fang': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.banling-flower': { glyph: '🌿', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.yinlingsong-wood': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.crimson-copper': { glyph: '⛏️', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.beast-fang': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.fire-feather': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.soul-talisman': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.broken-blade': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.sha-bone': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.demon-core': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.meteor-fragment': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.refined-iron': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.peach-wood-crystal': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.herb-essence': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.refined-copper': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.spirit-pine-crystal': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.banling-essence': { glyph: '🪵', tone: 'green', rarity: 'yellow', level: 2, category: 'material' },
  'item.qi-gathering-pill': { glyph: '💊', tone: 'gray', rarity: 'mundane', level: 1, category: 'pill' },
  'item.zhixue-powder': { glyph: '💊', tone: 'gray', rarity: 'mundane', level: 1, category: 'pill' },
  'item.shunqi-pill': { glyph: '💊', tone: 'gray', rarity: 'mundane', level: 1, category: 'pill' },
  'item.tongmai-pill': { glyph: '💊', tone: 'green', rarity: 'yellow', level: 2, category: 'pill' },
  'currency.spirit-stone': { glyph: '◈', tone: 'gold', rarity: 'heaven', level: 1, category: 'currency', rarityLabelOverride: 'currency' },
  'item.tome.heavy-slash': { glyph: '📘', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.fragment.heavy-slash': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.chain-strike': { glyph: '📘', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.fragment.chain-strike': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.palm-lightning': { glyph: '📘', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.fragment.palm-lightning': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.blazing-blast': { glyph: '📘', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.fragment.blazing-blast': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.taunt': { glyph: '📘', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.fragment.taunt': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.heavy-clouds': { glyph: '📘', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.fragment.heavy-clouds': { glyph: '📜', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.tome.qingping-melody': { glyph: '📘', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.fragment.qingping-melody': { glyph: '📜', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.tome.lightning-attraction': { glyph: '📘', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.fragment.lightning-attraction': { glyph: '📜', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.tome.pozhen-melody': { glyph: '📘', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.fragment.pozhen-melody': { glyph: '📜', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.tome.star-piercer': { glyph: '📘', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.fragment.star-piercer': { glyph: '📜', tone: 'green', rarity: 'yellow', level: 2, category: 'manualFragment' },
  'item.gathering-incense': { glyph: '🌿', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.alchemy-flame': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'equipment.qingfeng-sword': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'weapon' },
  'equipment.pishan-blade': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'weapon' },
  'equipment.shufa-scarf': { glyph: '👑', tone: 'gray', rarity: 'mundane', level: 1, category: 'head' },
  'equipment.baina-armor': { glyph: '🥋', tone: 'gray', rarity: 'mundane', level: 1, category: 'armor' },
  'equipment.zhakou-pants': { glyph: '👖', tone: 'gray', rarity: 'mundane', level: 1, category: 'legs' },
  'equipment.xingshan-boots': { glyph: '👞', tone: 'gray', rarity: 'mundane', level: 1, category: 'boots' },
  'equipment.shouya-pendant': { glyph: '💍', tone: 'gray', rarity: 'mundane', level: 1, category: 'trinket' },
  'equipment.jiujie-staff': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'weapon' },
  'equipment.suxin-zither': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'weapon' },
  'equipment.hunyuan-scarf': { glyph: '👑', tone: 'gray', rarity: 'mundane', level: 1, category: 'head' },
  'equipment.suma-robe': { glyph: '🥋', tone: 'gray', rarity: 'mundane', level: 1, category: 'armor' },
  'equipment.xingqi-pants': { glyph: '👖', tone: 'gray', rarity: 'mundane', level: 1, category: 'legs' },
  'equipment.shifang-shoes': { glyph: '👞', tone: 'gray', rarity: 'mundane', level: 1, category: 'boots' },
  'equipment.taomu-talisman': { glyph: '💍', tone: 'gray', rarity: 'mundane', level: 1, category: 'trinket' },
  'equipment.pogang-sword': { glyph: '⚔️', tone: 'green', rarity: 'yellow', level: 2, category: 'weapon' },
  'equipment.zhansha-blade': { glyph: '⚔️', tone: 'green', rarity: 'yellow', level: 2, category: 'weapon' },
  'equipment.chijin-helmet': { glyph: '👑', tone: 'green', rarity: 'yellow', level: 2, category: 'head' },
  'equipment.yulin-armor': { glyph: '🥋', tone: 'green', rarity: 'yellow', level: 2, category: 'armor' },
  'equipment.tazhen-greaves': { glyph: '👖', tone: 'green', rarity: 'yellow', level: 2, category: 'legs' },
  'equipment.tashan-boots': { glyph: '👞', tone: 'green', rarity: 'yellow', level: 2, category: 'boots' },
  'equipment.chiwen-ring': { glyph: '💍', tone: 'green', rarity: 'yellow', level: 2, category: 'trinket' },
  'equipment.chaoyuan-staff': { glyph: '⚔️', tone: 'green', rarity: 'yellow', level: 2, category: 'weapon' },
  'equipment.songfeng-zither': { glyph: '⚔️', tone: 'green', rarity: 'yellow', level: 2, category: 'weapon' },
  'equipment.ziwu-crown': { glyph: '👑', tone: 'green', rarity: 'yellow', level: 2, category: 'head' },
  'equipment.pichen-shirt': { glyph: '🥋', tone: 'green', rarity: 'yellow', level: 2, category: 'armor' },
  'equipment.liuyun-skirt': { glyph: '👖', tone: 'green', rarity: 'yellow', level: 2, category: 'legs' },
  'equipment.bichen-shoes': { glyph: '👞', tone: 'green', rarity: 'yellow', level: 2, category: 'boots' },
  'equipment.juling-pendant': { glyph: '💍', tone: 'green', rarity: 'yellow', level: 2, category: 'trinket' },
}

/**
 * 根据物品 ID 解析带多语言文案的完整物品元数据
 * @param itemId 物品唯一标识
 */
export function resolveItemMetadata(itemId: string): ItemMetadata | undefined {
  const core = ITEM_META_CORES[itemId]
  const t = getT()
  if (core) {
    return {
      label: t(`game.item.${itemId}.label`),
      glyph: core.glyph,
      tone: core.tone,
      category: resolveItemCategoryLabel(core.category, t),
      rarity: core.rarity,
      rarityLabel: t(`game.rarityLabel.${core.rarityLabelOverride ?? core.rarity}`),
      level: core.level,
      description: t(`game.item.${itemId}.description`),
    }
  }

  if (itemId.startsWith('equipment.')) {
    return {
      label: itemId,
      glyph: '⚔️',
      tone: 'gray',
      category: resolveItemCategoryLabel('equipment', t),
      rarity: 'mundane',
      rarityLabel: t('game.rarityLabel.mundane'),
      level: 1,
      description: '',
    }
  }

  return undefined
}
