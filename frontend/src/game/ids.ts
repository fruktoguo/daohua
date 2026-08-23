import { getT } from '../i18n'
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
  /** 产出来源 */
  source: string
  /** 用途说明 */
  usage: string
}

/**
 * 物品分类多语言键枚举
 */
export type ItemCategoryKey =
  | 'material' | 'pill' | 'currency' | 'resource' | 'spiritFood' | 'spiritDrink'
  | 'talisman' | 'loot' | 'rareLoot' | 'manualFragment' | 'explorationSupply'
  | 'alchemySupply' | 'mainHandImplement' | 'twoHandImplement'

/**
 * 物品元数据纯数据核心
 */
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
  'item.banling-flower': { glyph: '🌿', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.yinlingsong-wood': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.crimson-copper': { glyph: '⛏️', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.beast-fang': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.fire-feather': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.soul-talisman': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.broken-blade': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.sha-bone': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.demon-core': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.meteor-fragment': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.refined-iron': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.peach-wood-crystal': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.herb-essence': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.refined-copper': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.spirit-pine-crystal': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.banling-essence': { glyph: '🪵', tone: 'green', rarity: 'spirit', level: 2, category: 'material' },
  'item.qi-gathering-pill': { glyph: '💊', tone: 'green', rarity: 'spirit', level: 1, category: 'pill' },
  'item.zhixue-powder': { glyph: '💊', tone: 'green', rarity: 'spirit', level: 1, category: 'pill' },
  'item.shunqi-pill': { glyph: '💊', tone: 'green', rarity: 'spirit', level: 1, category: 'pill' },
  'item.tongmai-pill': { glyph: '💊', tone: 'violet', rarity: 'mystic', level: 2, category: 'pill' },
  'currency.spirit-stone': { glyph: '◈', tone: 'gold', rarity: 'heaven', level: 1, category: 'currency', rarityLabelOverride: 'currency' },
  'item.tome.heavy-slash': { glyph: '📘', tone: 'cyan', rarity: 'spirit', level: 1, category: 'manualFragment' },
  'item.fragment.heavy-slash': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.chain-strike': { glyph: '📘', tone: 'cyan', rarity: 'spirit', level: 1, category: 'manualFragment' },
  'item.fragment.chain-strike': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.palm-lightning': { glyph: '📘', tone: 'cyan', rarity: 'spirit', level: 1, category: 'manualFragment' },
  'item.fragment.palm-lightning': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.blazing-blast': { glyph: '📘', tone: 'cyan', rarity: 'spirit', level: 1, category: 'manualFragment' },
  'item.fragment.blazing-blast': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.taunt': { glyph: '📘', tone: 'cyan', rarity: 'spirit', level: 1, category: 'manualFragment' },
  'item.fragment.taunt': { glyph: '📜', tone: 'gray', rarity: 'mundane', level: 1, category: 'manualFragment' },
  'item.tome.heavy-clouds': { glyph: '📘', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.fragment.heavy-clouds': { glyph: '📜', tone: 'green', rarity: 'spirit', level: 2, category: 'manualFragment' },
  'item.tome.qingping-melody': { glyph: '📘', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.fragment.qingping-melody': { glyph: '📜', tone: 'green', rarity: 'spirit', level: 2, category: 'manualFragment' },
  'item.tome.lightning-attraction': { glyph: '📘', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.fragment.lightning-attraction': { glyph: '📜', tone: 'green', rarity: 'spirit', level: 2, category: 'manualFragment' },
  'item.tome.pozhen-melody': { glyph: '📘', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.fragment.pozhen-melody': { glyph: '📜', tone: 'green', rarity: 'spirit', level: 2, category: 'manualFragment' },
  'item.tome.star-piercer': { glyph: '📘', tone: 'violet', rarity: 'mystic', level: 2, category: 'manualFragment' },
  'item.fragment.star-piercer': { glyph: '📜', tone: 'green', rarity: 'spirit', level: 2, category: 'manualFragment' },
  'item.gathering-incense': { glyph: '🌿', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'item.alchemy-flame': { glyph: '🪵', tone: 'gray', rarity: 'mundane', level: 1, category: 'material' },
  'equipment.qingfeng-sword': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'mainHandImplement' },
  'equipment.pishan-blade': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'mainHandImplement' },
  'equipment.shufa-scarf': { glyph: '👑', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.baina-armor': { glyph: '🥋', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.zhakou-pants': { glyph: '👖', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.xingshan-boots': { glyph: '👞', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.shouya-pendant': { glyph: '💍', tone: 'gray', rarity: 'mundane', level: 1, category: 'rareLoot' },
  'equipment.jiujie-staff': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'mainHandImplement' },
  'equipment.suxin-zither': { glyph: '⚔️', tone: 'gray', rarity: 'mundane', level: 1, category: 'mainHandImplement' },
  'equipment.hunyuan-scarf': { glyph: '👑', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.suma-robe': { glyph: '🥋', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.xingqi-pants': { glyph: '👖', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.shifang-shoes': { glyph: '👞', tone: 'gray', rarity: 'mundane', level: 1, category: 'twoHandImplement' },
  'equipment.taomu-talisman': { glyph: '💍', tone: 'gray', rarity: 'mundane', level: 1, category: 'rareLoot' },
  'equipment.pogang-sword': { glyph: '⚔️', tone: 'green', rarity: 'spirit', level: 2, category: 'mainHandImplement' },
  'equipment.zhansha-blade': { glyph: '⚔️', tone: 'green', rarity: 'spirit', level: 2, category: 'mainHandImplement' },
  'equipment.chijin-helmet': { glyph: '👑', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.yulin-armor': { glyph: '🥋', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.tazhen-greaves': { glyph: '👖', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.tashan-boots': { glyph: '👞', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.chiwen-ring': { glyph: '💍', tone: 'green', rarity: 'spirit', level: 2, category: 'rareLoot' },
  'equipment.chaoyuan-staff': { glyph: '⚔️', tone: 'green', rarity: 'spirit', level: 2, category: 'mainHandImplement' },
  'equipment.songfeng-zither': { glyph: '⚔️', tone: 'green', rarity: 'spirit', level: 2, category: 'mainHandImplement' },
  'equipment.ziwu-crown': { glyph: '👑', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.pichen-shirt': { glyph: '🥋', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.liuyun-skirt': { glyph: '👖', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.bichen-shoes': { glyph: '👞', tone: 'green', rarity: 'spirit', level: 2, category: 'twoHandImplement' },
  'equipment.juling-pendant': { glyph: '💍', tone: 'green', rarity: 'spirit', level: 2, category: 'rareLoot' },
}

/**
 * 根据物品 ID 解析带多语言文案的完整物品元数据
 * @param itemId 物品唯一标识
 */
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
