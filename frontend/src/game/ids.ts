import { getT } from '../i18n'
import type { ActionDefinition, ActionType } from './types'

export const ACTION_TYPES: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy', 'combat']

// 行动定义只保留纯数据核心：显示文本统一走 i18n（game.action.<id>.*），
// 由 resolveActionDefinition 在渲染期按当前语言解析，避免数据表内重复存放文案。
export type ActionDefinitionCore = Omit<ActionDefinition, 'name' | 'description'>

const ACTION_DEFINITION_CORES: readonly ActionDefinitionCore[] = [
  {
    id: 'cultivation.draw-qi',
    type: 'cultivation',
    durationSeconds: 10,
    outputs: { 'resource.qi': 10 },
  },
  {
    id: 'explore.gather-herbs',
    type: 'exploration',
    durationSeconds: 8,
    outputs: { 'item.spirit-herb': 3 },
  },
  {
    id: 'alchemy.refine-qi-pill',
    type: 'alchemy',
    durationSeconds: 12,
    inputs: { 'item.spirit-herb': 3 },
    outputs: { 'item.qi-gathering-pill': 1 },
  },
  {
    id: 'combat.hunt-qi-beast',
    type: 'combat',
    durationSeconds: 60,
    executionKind: 'combat-encounter',
    combatEncounterId: 'encounter.qi-beast',
    outputs: { 'item.qi-beast-core': 1 },
  },
  {
    id: 'combat.sweep-twilight-valley',
    type: 'combat',
    durationSeconds: 60,
    executionKind: 'combat-encounter',
    combatEncounterId: 'encounter.twilight-valley',
  },
]

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

// 离线兜底目录：仅在服务端目录尚未到达时展示；语言取模块加载时的当前语言，
// 服务端目录到达后即被整体替换。
export function resolveActionDefinitions(): ActionDefinition[] {
  return ACTION_DEFINITION_CORES.map(core => resolveActionDefinition(core.id)!)
}

// 页签仅保留非文本元数据；label 统一经 actionTabLabel() 走 i18n。
export const ACTION_TAB_META: Record<ActionType, { glyph: string; accent: string }> = {
  cultivation: { glyph: '☯', accent: 'cyan' },
  exploration: { glyph: '⌕', accent: 'green' },
  alchemy: { glyph: '⚗', accent: 'violet' },
  combat: { glyph: '⚔', accent: 'orange' },
}

export function actionTabLabel(type: ActionType): string {
  return getT()(`game.tab.${type}`)
}

export type ItemRarity = 'mundane' | 'yellow' | 'mystic' | 'earth' | 'heaven' | 'spirit' | 'saint' | 'emperor'

export interface ItemGrade {
  rarity: ItemRarity
  label: string
}

const ITEM_GRADE_ORDER: readonly ItemRarity[] = [
  'mundane', 'yellow', 'mystic', 'earth', 'heaven', 'spirit', 'saint', 'emperor',
]

export function resolveItemGrade(tier = 1): ItemGrade {
  const index = Math.max(0, Math.min(ITEM_GRADE_ORDER.length - 1, Math.floor(tier) - 1))
  const rarity = ITEM_GRADE_ORDER[index]
  return { rarity, label: getT()(`game.grade.${rarity}`) }
}

export function withItemGrade(metadata: ItemMetadata, tier = metadata.level ?? 1): ItemMetadata {
  const grade = resolveItemGrade(tier)
  return { ...metadata, rarity: grade.rarity, rarityLabel: grade.label, level: tier }
}

export interface ItemMetadata {
  label: string
  glyph: string
  tone: string
  category: string
  rarity: ItemRarity
  rarityLabel: string
  level?: number
  description: string
  source: string
  usage: string
}

export type ItemCategoryKey =
  | 'material' | 'pill' | 'currency' | 'resource' | 'spiritFood' | 'spiritDrink'
  | 'talisman' | 'loot' | 'rareLoot' | 'manualFragment' | 'explorationSupply'
  | 'alchemySupply' | 'mainHandImplement' | 'twoHandImplement'

// 物品元数据核心：category/rarityLabel 去重为键（game.category.* / game.rarityLabel.*），
// 专属文本（label/description/source/usage）走 game.item.<itemId>.*。
interface ItemMetaCore {
  glyph: string
  tone: string
  rarity: ItemRarity
  level?: number
  category: ItemCategoryKey
  /** 特例覆盖：灵石品质标签「通宝」不随 rarity 推导。 */
  rarityLabelOverride?: 'currency'
}

const ITEM_META_CORES: Record<string, ItemMetaCore> = {
  'item.spirit-herb': { glyph: '✤', tone: 'green', rarity: 'spirit', level: 1, category: 'material' },
  'item.qi-gathering-pill': { glyph: '✺', tone: 'violet', rarity: 'mystic', level: 1, category: 'pill' },
  'currency.spirit': { glyph: '◈', tone: 'gold', rarity: 'heaven', category: 'currency', rarityLabelOverride: 'currency' },
  'resource.qi': { glyph: '◉', tone: 'cyan', rarity: 'mundane', category: 'resource' },
  'item.healing-elixir': { glyph: '◒', tone: 'cyan', rarity: 'spirit', level: 1, category: 'spiritFood' },
  'item.rejuvenating-dew': { glyph: '◒', tone: 'green', rarity: 'mystic', level: 2, category: 'spiritFood' },
  'item.spirit-tea': { glyph: '♨', tone: 'cyan', rarity: 'mystic', level: 1, category: 'spiritDrink' },
  'item.fire-talisman': { glyph: '⌁', tone: 'orange', rarity: 'mystic', level: 2, category: 'talisman' },
  'item.qi-beast-core': { glyph: '◆', tone: 'green', rarity: 'spirit', level: 1, category: 'loot' },
  'item.cloud-sugar': { glyph: '◇', tone: 'green', rarity: 'spirit', level: 1, category: 'material' },
  'item.verdant-spirit-milk': { glyph: '◒', tone: 'green', rarity: 'spirit', level: 1, category: 'spiritFood' },
  'item.azure-spirit-milk': { glyph: '◒', tone: 'cyan', rarity: 'spirit', level: 1, category: 'spiritFood' },
  'item.spirit-pulse-manual': { glyph: '▧', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.small-qi-chest': { glyph: '▣', tone: 'orange', rarity: 'heaven', level: 3, category: 'rareLoot' },
  'item.gathering-incense': { glyph: '⌁', tone: 'green', rarity: 'spirit', level: 1, category: 'explorationSupply' },
  'item.alchemy-flame': { glyph: '♨', tone: 'orange', rarity: 'spirit', level: 1, category: 'alchemySupply' },
  'equipment.cursed-spirit-bow': { glyph: '弓', tone: 'orange', rarity: 'heaven', level: 5, category: 'twoHandImplement' },
  'equipment.furious-spirit-spear': { glyph: '枪', tone: 'orange', rarity: 'heaven', level: 5, category: 'mainHandImplement' },
  'equipment.griffin-spirit-bulwark': { glyph: '盾', tone: 'green', rarity: 'heaven', level: 5, category: 'twoHandImplement' },
  'equipment.sundering-spirit-crossbow': { glyph: '弩', tone: 'violet', rarity: 'heaven', level: 5, category: 'mainHandImplement' },
}

export function resolveItemMetadata(itemId: string): ItemMetadata | undefined {
  const core = ITEM_META_CORES[itemId]
  if (!core) return undefined
  const t = getT()
  return {
    label: t(`game.item.${itemId}.label`),
    glyph: core.glyph,
    tone: core.tone,
    category: t(`game.category.${core.category}`),
    rarity: core.rarity,
    rarityLabel: t(`game.rarityLabel.${core.rarityLabelOverride ?? core.rarity}`),
    level: core.level,
    description: t(`game.item.${itemId}.description`),
    source: t(`game.item.${itemId}.source`),
    usage: t(`game.item.${itemId}.usage`),
  }
}
