import { getT, type TFunction } from '../i18n'
import { calculateRootLevelAndProgress } from './combatConstants'
import type { ActionDefinition, ActionType, CombatAbilitySlot, CombatEquipmentDefinition, ItemDefinition, WorldMapDefinition, WorldState } from './types'

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
  // 修炼
  {
    id: 'cultivation.draw-qi', type: 'cultivation', durationSeconds: 10, yields: [
      { kind: 'qi', per: 'second', amount: 1 },
      { kind: 'mana', per: 'repeat', amount: 1 },
      { kind: 'experience', per: 'repeat', amount: 10 },
    ],
  },

  // 采集
  { id: 'explore.gather-ningxue-grass', type: 'exploration', durationSeconds: 8, yields: [{ kind: 'item', per: 'repeat', id: 'item.ningxue-grass', amount: 3 }] },
  { id: 'explore.chop-peach-wood', type: 'exploration', durationSeconds: 6, yields: [{ kind: 'item', per: 'repeat', id: 'item.peach-wood', amount: 1 }] },
  { id: 'explore.mine-dark-iron', type: 'exploration', durationSeconds: 6, yields: [{ kind: 'item', per: 'repeat', id: 'item.dark-iron', amount: 1 }] },
  { id: 'explore.collect-morning-dew', type: 'exploration', durationSeconds: 6, yields: [{ kind: 'item', per: 'repeat', id: 'item.morning-dew', amount: 1 }] },
  { id: 'explore.gather-banling-flower', type: 'exploration', durationSeconds: 8, yields: [{ kind: 'item', per: 'repeat', id: 'item.banling-flower', amount: 1 }] },
  { id: 'explore.chop-yinlingsong', type: 'exploration', durationSeconds: 8, yields: [{ kind: 'item', per: 'repeat', id: 'item.yinlingsong-wood', amount: 1 }] },
  { id: 'explore.mine-crimson-copper', type: 'exploration', durationSeconds: 8, yields: [{ kind: 'item', per: 'repeat', id: 'item.crimson-copper', amount: 1 }] },

  // 赶路
  { id: 'world.travel', type: 'exploration', durationSeconds: 1, executionKind: 'travel', hiddenFromCatalog: true, yields: [] },

  // 炼丹
  { id: 'alchemy.refine-qi-pill', type: 'alchemy', durationSeconds: 12, inputs: { 'item.ningxue-grass': 3, 'item.morning-dew': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.qi-gathering-pill', amount: 1 }] },
  { id: 'alchemy.refine-zhixue-powder', type: 'alchemy', durationSeconds: 10, inputs: { 'item.ningxue-grass': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.zhixue-powder', amount: 1 }] },
  { id: 'alchemy.refine-shunqi-pill', type: 'alchemy', durationSeconds: 10, inputs: { 'item.herb-essence': 1, 'item.banling-flower': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.shunqi-pill', amount: 1 }] },
  { id: 'alchemy.refine-tongmai-pill', type: 'alchemy', durationSeconds: 20, inputs: { 'item.banling-essence': 2, 'item.demon-core': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.tongmai-pill', amount: 1 }] },

  // 精炼加工
  { id: 'craft.refine-iron', type: 'alchemy', durationSeconds: 10, inputs: { 'item.dark-iron': 3 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.refined-iron', amount: 1 }] },
  { id: 'craft.refine-peach-wood-crystal', type: 'alchemy', durationSeconds: 10, inputs: { 'item.peach-wood': 3 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.peach-wood-crystal', amount: 1 }] },
  { id: 'craft.extract-herb-essence', type: 'alchemy', durationSeconds: 10, inputs: { 'item.ningxue-grass': 2, 'item.morning-dew': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.herb-essence', amount: 1 }] },
  { id: 'craft.refine-copper', type: 'alchemy', durationSeconds: 15, inputs: { 'item.crimson-copper': 3 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.refined-copper', amount: 1 }] },
  { id: 'craft.refine-spirit-pine-crystal', type: 'alchemy', durationSeconds: 15, inputs: { 'item.yinlingsong-wood': 3 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.spirit-pine-crystal', amount: 1 }] },
  { id: 'craft.extract-banling-essence', type: 'alchemy', durationSeconds: 15, inputs: { 'item.banling-flower': 2, 'item.morning-dew': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'item.banling-essence', amount: 1 }] },

  // 讨伐战斗
  { id: 'combat.hunt-qi-beast', type: 'combat', durationSeconds: 0, executionKind: 'combat-encounter', combatEncounterId: 'encounter.twilight-pass' },
  { id: 'combat.sweep-twilight-valley', type: 'combat', durationSeconds: 0, executionKind: 'combat-encounter', combatEncounterId: 'encounter.twilight-valley' },

  // 锻造装备 (一阶)
  { id: 'forge.qingfeng-sword', type: 'alchemy', durationSeconds: 15, inputs: { 'item.refined-iron': 7, 'item.beast-bone': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.qingfeng-sword', amount: 1 }] },
  { id: 'forge.pishan-blade', type: 'alchemy', durationSeconds: 15, inputs: { 'item.refined-iron': 7, 'item.beast-bone': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.pishan-blade', amount: 1 }] },
  { id: 'forge.shufa-scarf', type: 'alchemy', durationSeconds: 10, inputs: { 'item.refined-iron': 5, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.shufa-scarf', amount: 1 }] },
  { id: 'forge.baina-armor', type: 'alchemy', durationSeconds: 15, inputs: { 'item.refined-iron': 7, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.baina-armor', amount: 1 }] },
  { id: 'forge.zhakou-pants', type: 'alchemy', durationSeconds: 10, inputs: { 'item.refined-iron': 5, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.zhakou-pants', amount: 1 }] },
  { id: 'forge.xingshan-boots', type: 'alchemy', durationSeconds: 10, inputs: { 'item.refined-iron': 5, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.xingshan-boots', amount: 1 }] },
  { id: 'forge.shouya-pendant', type: 'alchemy', durationSeconds: 10, inputs: { 'item.refined-iron': 5, 'item.beast-bone': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.shouya-pendant', amount: 1 }] },
  { id: 'forge.jiujie-staff', type: 'alchemy', durationSeconds: 15, inputs: { 'item.peach-wood-crystal': 7, 'item.broken-spirit-core': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.jiujie-staff', amount: 1 }] },
  { id: 'forge.suxin-zither', type: 'alchemy', durationSeconds: 15, inputs: { 'item.peach-wood-crystal': 7, 'item.broken-spirit-core': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.suxin-zither', amount: 1 }] },
  { id: 'forge.hunyuan-scarf', type: 'alchemy', durationSeconds: 10, inputs: { 'item.peach-wood-crystal': 5, 'item.broken-spirit-core': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.hunyuan-scarf', amount: 1 }] },
  { id: 'forge.suma-robe', type: 'alchemy', durationSeconds: 15, inputs: { 'item.peach-wood-crystal': 7, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.suma-robe', amount: 1 }] },
  { id: 'forge.xingqi-pants', type: 'alchemy', durationSeconds: 10, inputs: { 'item.peach-wood-crystal': 5, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.xingqi-pants', amount: 1 }] },
  { id: 'forge.shifang-shoes', type: 'alchemy', durationSeconds: 10, inputs: { 'item.peach-wood-crystal': 5, 'item.beast-hide': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.shifang-shoes', amount: 1 }] },
  { id: 'forge.taomu-talisman', type: 'alchemy', durationSeconds: 10, inputs: { 'item.peach-wood-crystal': 5, 'item.beast-bone': 1 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.taomu-talisman', amount: 1 }] },

  // 锻造装备 (二阶)
  { id: 'forge.pogang-sword', type: 'alchemy', durationSeconds: 25, inputs: { 'item.refined-copper': 7, 'item.beast-fang': 1, 'equipment.qingfeng-sword': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.pogang-sword', amount: 1 }] },
  { id: 'forge.zhansha-blade', type: 'alchemy', durationSeconds: 25, inputs: { 'item.refined-copper': 7, 'item.beast-fang': 1, 'equipment.pishan-blade': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.zhansha-blade', amount: 1 }] },
  { id: 'forge.chijin-helmet', type: 'alchemy', durationSeconds: 20, inputs: { 'item.refined-copper': 5, 'item.beast-fang': 1, 'equipment.shufa-scarf': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.chijin-helmet', amount: 1 }] },
  { id: 'forge.yulin-armor', type: 'alchemy', durationSeconds: 25, inputs: { 'item.refined-copper': 7, 'item.beast-fang': 1, 'equipment.baina-armor': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.yulin-armor', amount: 1 }] },
  { id: 'forge.tazhen-greaves', type: 'alchemy', durationSeconds: 20, inputs: { 'item.refined-copper': 5, 'item.beast-fang': 1, 'equipment.zhakou-pants': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.tazhen-greaves', amount: 1 }] },
  { id: 'forge.tashan-boots', type: 'alchemy', durationSeconds: 20, inputs: { 'item.refined-copper': 5, 'item.beast-fang': 1, 'equipment.xingshan-boots': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.tashan-boots', amount: 1 }] },
  { id: 'forge.chiwen-ring', type: 'alchemy', durationSeconds: 20, inputs: { 'item.refined-copper': 5, 'item.beast-fang': 1, 'equipment.shouya-pendant': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.chiwen-ring', amount: 1 }] },
  { id: 'forge.chaoyuan-staff', type: 'alchemy', durationSeconds: 25, inputs: { 'item.spirit-pine-crystal': 7, 'item.soul-talisman': 1, 'equipment.jiujie-staff': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.chaoyuan-staff', amount: 1 }] },
  { id: 'forge.songfeng-zither', type: 'alchemy', durationSeconds: 25, inputs: { 'item.spirit-pine-crystal': 7, 'item.soul-talisman': 1, 'equipment.suxin-zither': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.songfeng-zither', amount: 1 }] },
  { id: 'forge.ziwu-crown', type: 'alchemy', durationSeconds: 20, inputs: { 'item.spirit-pine-crystal': 5, 'item.soul-talisman': 1, 'equipment.hunyuan-scarf': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.ziwu-crown', amount: 1 }] },
  { id: 'forge.pichen-shirt', type: 'alchemy', durationSeconds: 25, inputs: { 'item.spirit-pine-crystal': 7, 'item.fire-feather': 1, 'equipment.suma-robe': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.pichen-shirt', amount: 1 }] },
  { id: 'forge.liuyun-skirt', type: 'alchemy', durationSeconds: 20, inputs: { 'item.spirit-pine-crystal': 5, 'item.fire-feather': 1, 'equipment.xingqi-pants': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.liuyun-skirt', amount: 1 }] },
  { id: 'forge.bichen-shoes', type: 'alchemy', durationSeconds: 20, inputs: { 'item.spirit-pine-crystal': 5, 'item.fire-feather': 1, 'equipment.shifang-shoes': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.bichen-shoes', amount: 1 }] },
  { id: 'forge.juling-pendant', type: 'alchemy', durationSeconds: 20, inputs: { 'item.spirit-pine-crystal': 5, 'item.soul-talisman': 1, 'equipment.taomu-talisman': 2 }, yields: [{ kind: 'item', per: 'repeat', id: 'equipment.juling-pendant', amount: 1 }] },
]

/** 静态兜底行动的 ID 索引（服务端目录未到达前供 resolveActionDefinition 查询） */
const ACTION_CORE_MAP = new Map<string, ActionDefinitionCore>(ACTION_DEFINITION_CORES.map(c => [c.id, c]))

/** 动态配表行动字典缓存（在服务端目录拉取后注册） */
const dynamicActionsMap = new Map<string, ActionDefinition>()

/**
 * 注册从服务端 Catalog 拉取的动态行动定义
 */
export function registerCatalogActions(actions: readonly ActionDefinition[]): void {
  for (const action of actions) {
    dynamicActionsMap.set(action.id, action)
  }
}

let cachedActionsRef: ReadonlyArray<ActionDefinition> | null = null
let cachedActionMap: Map<string, ActionDefinition> = new Map()

/**
 * 获取行动目录的 O(1) 字典映射（基于数组引用的快照级缓存）
 */
export function getActionMap<T extends ActionDefinition>(actions: readonly T[]): Map<string, T> {
  if (actions === cachedActionsRef) return cachedActionMap as Map<string, T>
  cachedActionsRef = actions
  const map = new Map<string, T>()
  for (const action of actions) {
    map.set(action.id, action)
  }
  cachedActionMap = map as Map<string, ActionDefinition>
  return map
}

/**
 * 在行动目录中 O(1) 高效查找行动定义
 */
export function findActionInCatalog(actions: readonly ActionDefinition[], actionId: string): ActionDefinition | undefined {
  return getActionMap(actions).get(actionId)
}

/**
 * 解析单个行动的完整定义（注入当前语言的名称和描述）
 * 优先级：
 * 1. 动态 catalog actions (结合当前 i18n 覆盖或配表名)
 * 2. 静态 ACTION_CORE_MAP 兜底 (结合当前 i18n)
 * 3. 仅通过 i18n 键兜底构建
 * @param id 行动唯一标识符
 */
export function resolveActionDefinition(id: string): ActionDefinition | undefined {
  const t = getT()
  const nameKey = `game.action.${id}.name`
  const descKey = `game.action.${id}.description`
  const translatedName = t(nameKey)
  const translatedDesc = t(descKey)
  const hasI18nName = translatedName !== nameKey
  const hasI18nDesc = translatedDesc !== descKey

  const dynamic = dynamicActionsMap.get(id)
  if (dynamic) {
    return {
      ...dynamic,
      name: hasI18nName ? translatedName : (dynamic.name || id),
      description: hasI18nDesc ? translatedDesc : (dynamic.description || ''),
    }
  }

  const core = ACTION_CORE_MAP.get(id)
  if (core) {
    return {
      ...core,
      name: hasI18nName ? translatedName : core.id,
      description: hasI18nDesc ? translatedDesc : '',
    }
  }

  if (hasI18nName) {
    return {
      id,
      name: translatedName,
      description: hasI18nDesc ? translatedDesc : '',
      type: 'cultivation',
      durationSeconds: 0,
    }
  }

  return undefined
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

let cachedItemsRef: ReadonlyArray<{ itemId: string }> | null = null
let cachedItemMap: Map<string, { itemId: string }> = new Map()

/**
 * 获取物品目录的 O(1) 字典映射（基于数组引用的快照级缓存）
 */
export function getItemMap<T extends { itemId: string }>(items: readonly T[]): Map<string, T> {
  if (items === cachedItemsRef) return cachedItemMap as Map<string, T>
  cachedItemsRef = items
  const map = new Map<string, T>()
  for (const item of items) {
    map.set(item.itemId, item)
  }
  cachedItemMap = map as Map<string, { itemId: string }>
  return map
}

let cachedEquipmentRef: ReadonlyArray<{ equipmentId: string }> | null = null
let cachedEquipmentMap: Map<string, { equipmentId: string }> = new Map()

/**
 * 获取装备目录的 O(1) 字典映射（基于数组引用的快照级缓存）
 */
export function getEquipmentMap<T extends { equipmentId: string }>(equipment: readonly T[]): Map<string, T> {
  if (equipment === cachedEquipmentRef) return cachedEquipmentMap as Map<string, T>
  cachedEquipmentRef = equipment
  const map = new Map<string, T>()
  for (const eq of equipment) {
    map.set(eq.equipmentId, eq)
  }
  cachedEquipmentMap = map as Map<string, { equipmentId: string }>
  return map
}

let cachedAbilitiesRef: ReadonlyArray<{ abilityId: string }> | null = null
let cachedAbilityMap: Map<string, { abilityId: string }> = new Map()

/**
 * 获取功法技能目录的 O(1) 字典映射（基于数组引用的快照级缓存）
 */
export function getAbilityMap<T extends { abilityId: string }>(abilities: readonly T[]): Map<string, T> {
  if (abilities === cachedAbilitiesRef) return cachedAbilityMap as Map<string, T>
  cachedAbilitiesRef = abilities
  const map = new Map<string, T>()
  for (const ab of abilities) {
    map.set(ab.abilityId, ab)
  }
  cachedAbilityMap = map as Map<string, { abilityId: string }>
  return map
}

let cachedEncountersRef: ReadonlyArray<{ encounterId: string; enemy?: { enemyId: string } }> | null = null
let cachedEncounterMap: Map<string, { encounterId: string; enemy?: { enemyId: string } }> = new Map()

/**
 * 获取战斗遭遇目录的 O(1) 字典映射（支持 encounterId 与 enemy.enemyId 双向索引）
 */
export function getEncounterMap<T extends { encounterId: string; enemy?: { enemyId: string } }>(encounters: readonly T[]): Map<string, T> {
  if (encounters === cachedEncountersRef) return cachedEncounterMap as Map<string, T>
  cachedEncountersRef = encounters
  const map = new Map<string, T>()
  for (const enc of encounters) {
    map.set(enc.encounterId, enc)
    if (enc.enemy?.enemyId) {
      map.set(enc.enemy.enemyId, enc)
    }
  }
  cachedEncounterMap = map as Map<string, { encounterId: string; enemy?: { enemyId: string } }>
  return map
}

/**
 * 在物品目录中 O(1) 高效查找物品定义
 */
export function findItemInCatalog<T extends { itemId: string }>(items: readonly T[], itemId: string): T | undefined {
  return getItemMap(items).get(itemId)
}

/**
 * 在装备目录中 O(1) 高效查找装备定义
 */
export function findEquipmentInCatalog<T extends { equipmentId: string }>(equipment: readonly T[], equipmentId: string): T | undefined {
  return getEquipmentMap(equipment).get(equipmentId)
}

/**
 * 在功法目录中 O(1) 高效查找技能定义
 */
export function findAbilityInCatalog<T extends { abilityId: string }>(abilities: readonly T[], abilityId: string): T | undefined {
  return getAbilityMap(abilities).get(abilityId)
}

/**
 * 储物袋展示分组键：重要（货币与特殊物品）、消耗品、装备法宝、功法、材料
 */
export type InventoryGroupKey = 'important' | 'consumable' | 'equipment' | 'manual' | 'material'

/** 储物袋分组固定展示顺序（空组不渲染） */
export const INVENTORY_GROUP_ORDER: readonly InventoryGroupKey[] = ['important', 'consumable', 'equipment', 'manual', 'material']

/**
 * 判定背包物品所属的展示分组。
 * 依据物品目录 kind/category 键与装备目录命中（InventoryItem.category 是本地化展示文案，不可作判定依据）；
 * 目录未覆盖时按 itemId 前缀兜底，仍无法识别归入材料组。
 */
export function resolveInventoryGroup(
  itemId: string,
  items: readonly { itemId: string; kind?: string; category?: string }[],
  equipment: readonly { equipmentId: string }[],
): InventoryGroupKey {
  if (itemId.startsWith('equipment.') || findEquipmentInCatalog(equipment, itemId) !== undefined) return 'equipment'
  if (itemId.startsWith('item.tome.') || itemId.startsWith('item.fragment.')) return 'manual'
  if (itemId.startsWith('currency.')) return 'important'
  const definition = findItemInCatalog(items, itemId)
  if (!definition) return 'material'
  if (definition.category === 'manual') return 'manual'
  if (definition.kind === 'equipment' || definition.category === 'equipment') return 'equipment'
  if (definition.kind === 'currency' || definition.kind === 'resource'
    || definition.category === 'currency' || definition.category === 'chest') return 'important'
  if (definition.kind === 'consumable') return 'consumable'
  return 'material'
}

/**
 * 功法已参悟等级：由功法经验表经立方曲线导出，与服务端 AbilityExpCurve 同式；
 * 未习得或无经验记录按 1 级计。
 */
export function learnedAbilityLevel(
  abilityExperiences: Readonly<Record<string, number>> | undefined,
  abilityId: string,
): number {
  return calculateRootLevelAndProgress(abilityExperiences?.[abilityId] ?? 0).level
}

/**
 * 将功法装配到指定槽位：同一功法若已装配在其它槽位，视为「移动」——
 * 从原槽位移除并把已配置的触发条件一并随迁，而非让服务端以重复装配拒绝；
 * 换装（abilityId 不同）时清空新槽位的旧触发条件，防止残留条件被沿用。
 */
export function assignAbilityToSlot(
  slots: readonly CombatAbilitySlot[],
  index: number,
  abilityId: string | null,
): CombatAbilitySlot[] {
  const movedFrom = abilityId === null
    ? undefined
    : slots.find((slot, slotIndex) => slotIndex !== index && slot.abilityId === abilityId)
  return slots.map((slot, slotIndex) => {
    if (slotIndex === index) {
      const triggers = abilityId === slot.abilityId ? slot.triggers : (movedFrom?.triggers ?? [])
      return { ...slot, abilityId, triggers }
    }
    return slot.abilityId === abilityId ? { ...slot, abilityId: null, triggers: [] } : slot
  })
}

/**
 * 功法槽位发送前归一化：
 * 1. 类别互斥——内功位（index 0）只允许 discipline=internal 的心法内功，其余术法位
 *    只允许神通术法；类别不匹配的槽位 abilityId 置空，清理历史遗留的跨类装配；
 * 2. 装配等级对齐已参悟等级——技能等级只放大输出数值（灵力消耗不随等级变化），
 *    调低无收益，统一回填为参悟等级，避免遗留 1 级槽位以低等级结算。
 * 与服务端 loadout.replace 的槽位校验一致，避免整条命令因旧数据被拒。
 */
export function normalizeAbilitySlots(
  slots: readonly CombatAbilitySlot[],
  abilities: readonly { abilityId: string; discipline?: 'internal' | 'spell' }[],
  abilityExperiences: Readonly<Record<string, number>> | undefined,
): CombatAbilitySlot[] {
  return slots.map((slot, index) => {
    if (slot.abilityId === null || slot.abilityId === undefined) return slot
    const definition = findAbilityInCatalog(abilities, slot.abilityId)
    const isInternal = (definition?.discipline ?? 'spell') === 'internal'
    if ((index === 0) !== isInternal) return { ...slot, abilityId: null }
    const learnedLevel = learnedAbilityLevel(abilityExperiences, slot.abilityId)
    return slot.level === learnedLevel ? slot : { ...slot, level: learnedLevel }
  })
}

/**
 * 在遭遇目录中 O(1) 高效查找遭遇/敌怪定义
 */
export function findEncounterInCatalog<T extends { encounterId: string; enemy?: { enemyId: string } }>(encounters: readonly T[], encounterOrEnemyId: string): T | undefined {
  return getEncounterMap(encounters).get(encounterOrEnemyId)
}

/**
 * 行动显示文本按当前驻留地点本地化：
 * 行动名/描述中内嵌的当前地图名替换为当前节点名
 * （「讨伐 青云仙山」在漱玉溪显示为「讨伐 漱玉溪」），与「讨伐行动在当前节点
 * 生态展开」的实战语义一致——服务端 ResolveCombatEncounter 按玩家当前节点
 * encounterIds 抽取遭遇。行进在道路上（nodeId=null）或文本不含地图名时原样返回。
 */
export function localizeActionDisplayText(
  text: string,
  world: Pick<WorldState, 'mapId' | 'nodeId' | 'locationName'>,
  maps: readonly Pick<WorldMapDefinition, 'mapId' | 'name'>[],
): string {
  if (world.nodeId === null) return text
  const mapName = maps.find(map => map.mapId === world.mapId)?.name
  const { locationName } = world
  if (!mapName || !locationName || mapName === locationName || !text.includes(mapName)) return text
  return text.split(mapName).join(locationName)
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
  const eqMap = equipment ? getEquipmentMap(equipment) : null
  const itMap = items ? getItemMap(items) : null
  const encMap = encounters ? getEncounterMap(encounters) : null

  if (action.yields) {
    for (const yieldEntry of action.yields) {
      if (yieldEntry.kind !== 'item' || !yieldEntry.id) continue
      const outId = yieldEntry.id
      const eq = eqMap?.get(outId)
      if (eq && typeof eq.tier === 'number' && eq.tier > 0) return convertTierToLevel(eq.tier)
      const it = itMap?.get(outId)
      if (it && typeof it.tier === 'number' && it.tier > 0) return convertTierToLevel(it.tier)
      const meta = ITEM_META_CORES[outId]
      if (meta?.level && meta.level > 0) {
        return meta.level > 2 ? meta.level : convertTierToLevel(meta.level)
      }
    }
  }

  if (action.combatEncounterId) {
    const enc = encMap?.get(action.combatEncounterId)
    if (enc && typeof enc.tier === 'number' && enc.tier > 0) return convertTierToLevel(enc.tier)
    if (action.combatEncounterId.includes('valley') || action.combatEncounterId.includes('tier2')) return convertTierToLevel(2)
    return convertTierToLevel(1)
  }

  if (action.inputs) {
    let maxTier = 1
    for (const inId of Object.keys(action.inputs)) {
      const eq = eqMap?.get(inId)
      if (eq && typeof eq.tier === 'number' && eq.tier > maxTier) maxTier = eq.tier
      const it = itMap?.get(inId)
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
  const eqMap = equipment ? getEquipmentMap(equipment) : null
  const itMap = items ? getItemMap(items) : null
  const encMap = encounters ? getEncounterMap(encounters) : null

  if (action.yields) {
    for (const yieldEntry of action.yields) {
      if (yieldEntry.kind !== 'item' || !yieldEntry.id) continue
      const outId = yieldEntry.id
      const meta = ITEM_META_CORES[outId]
      if (meta?.rarity) return meta.rarity
      const eq = eqMap?.get(outId)
      if (eq && typeof eq.tier === 'number' && eq.tier > 0) return resolveItemGrade(eq.tier).rarity
      const it = itMap?.get(outId)
      if (it && typeof it.tier === 'number' && it.tier > 0) return resolveItemGrade(it.tier).rarity
    }
  }

  if (action.combatEncounterId) {
    const enc = encMap?.get(action.combatEncounterId)
    if (enc && typeof enc.tier === 'number' && enc.tier > 0) return resolveItemGrade(enc.tier).rarity
    if (action.combatEncounterId.includes('valley') || action.combatEncounterId.includes('tier2')) return 'spirit'
    return 'mundane'
  }

  if (action.inputs) {
    let maxTier = 1
    for (const inId of Object.keys(action.inputs)) {
      const meta = ITEM_META_CORES[inId]
      if (meta?.rarity) return meta.rarity
      const eq = eqMap?.get(inId)
      if (eq && typeof eq.tier === 'number' && eq.tier > maxTier) maxTier = eq.tier
      const it = itMap?.get(inId)
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

/** 品阶 1~8 与品质枚举的升序对照表（下标 = tier - 1） */
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

/** 静态兜底物品定义：服务端 catalog 未下发对应条目时，保证常用消耗品仍可展示与解算 */
const DEFAULT_ITEM_DEFINITIONS: Record<string, Partial<ItemDefinition>> = {
  'item.qi-gathering-pill': {
    itemId: 'item.qi-gathering-pill',
    name: '引气丹',
    description: '通体泛着淡青色光泽的初阶灵丹，散发着淡淡的草木清香。',
    kind: 'consumable',
    category: 'pill',
    tier: 1,
    sellPrice: 35,
    isTradable: true,
    usableActionTypes: ['cultivation'],
    durationSeconds: 300,
    isContinuous: true,
    cultivationMultiplier: 1.3,
  },
  'item.zhixue-powder': {
    itemId: 'item.zhixue-powder',
    name: '止血散',
    description: '由草木研磨而成的暗红药粉，药性辛温，微带草木清气。',
    kind: 'consumable',
    category: 'pill',
    tier: 1,
    sellPrice: 25,
    isTradable: true,
    usableActionTypes: ['combat'],
    cooldownSeconds: 60,
    hitpointRestore: 0,
    buffEffects: [
      {
        buffId: 'buff.zhixue',
        name: '止血散·回春',
        durationSeconds: 30,
        hpRegen: 12,
        damageAbsorptionPoints: 5,
      },
    ],
  },
  'item.shunqi-pill': {
    itemId: 'item.shunqi-pill',
    name: '顺气丸',
    description: '圆润微润的暗黄药丸，气味清凉醒脑。',
    kind: 'consumable',
    category: 'pill',
    tier: 1,
    sellPrice: 25,
    isTradable: true,
    usableActionTypes: ['combat'],
    cooldownSeconds: 60,
    buffEffects: [
      {
        buffId: 'buff.shunqi',
        name: '顺气丸·凝神',
        durationSeconds: 30,
        mpRegen: 8,
        attackSpeedPoints: 5,
      },
    ],
  },
  'item.tongmai-pill': {
    itemId: 'item.tongmai-pill',
    name: '通脉丹',
    description: '丹面铭刻着细密紫纹的凝气灵丹，丹晕如雾缭绕。',
    kind: 'consumable',
    category: 'pill',
    tier: 2,
    sellPrice: 200,
    isTradable: true,
    usableActionTypes: ['cultivation'],
    durationSeconds: 300,
    isContinuous: true,
    cultivationMultiplier: 1.5,
  },
  'item.gathering-incense': {
    itemId: 'item.gathering-incense',
    name: '寻灵香',
    description: '以幽谷异草凝制的线香，点燃后青烟袅袅，香气沉静。',
    kind: 'consumable',
    category: 'herb',
    tier: 1,
    sellPrice: 20,
    isTradable: true,
    usableActionTypes: ['exploration'],
    durationSeconds: 300,
    isContinuous: true,
    gatheringMultiplier: 1.5,
  },
  'item.alchemy-flame': {
    itemId: 'item.alchemy-flame',
    name: '地火引',
    description: '封存在温玉小管中的一缕地脉火苗，静默跳动，热力含而不发。',
    kind: 'consumable',
    category: 'material',
    tier: 1,
    sellPrice: 22,
    isTradable: true,
    usableActionTypes: ['alchemy'],
    durationSeconds: 300,
    isContinuous: true,
    alchemyMultiplier: 1.25,
  },
}

/** 静态兜底装备定义：服务端 catalog 未下发对应条目时，保证一/二阶装备仍可展示属性 */
const DEFAULT_EQUIPMENT_DEFINITIONS: Record<string, Partial<CombatEquipmentDefinition>> = {
  'equipment.qingfeng-sword': {
    equipmentId: 'equipment.qingfeng-sword',
    name: '青锋剑',
    slot: 'weapon',
    tier: 1,
    baseStatRatios: { physicalAttack: 0.2 },
    flatStats: { physicalAttack: 10, accuracy: 10 },
  },
  'equipment.pishan-blade': {
    equipmentId: 'equipment.pishan-blade',
    name: '劈山刀',
    slot: 'weapon',
    tier: 1,
    baseStatRatios: { physicalAttack: 0.1, physicalArmor: 0.14 },
    flatStats: { physicalAttack: 8, physicalArmor: 8 },
    specialMechanics: { threatBonusPoints: 200 },
  },
  'equipment.jiujie-staff': {
    equipmentId: 'equipment.jiujie-staff',
    name: '九节杖',
    slot: 'weapon',
    tier: 1,
    baseStatRatios: { spellAttack: 0.2 },
    flatStats: { spellAttack: 10, accuracy: 10 },
  },
  'equipment.suxin-zither': {
    equipmentId: 'equipment.suxin-zither',
    name: '素心琴',
    slot: 'weapon',
    tier: 1,
    baseStatRatios: { spellAttack: 0.14 },
    flatStats: { spellAttack: 8, manapointRegen: 2 },
    specialMechanics: { healingBonusPoints: 50 },
  },
  'equipment.shufa-scarf': {
    equipmentId: 'equipment.shufa-scarf',
    name: '束发巾',
    slot: 'head',
    tier: 1,
    baseStatRatios: { physicalArmor: 0.1 },
    flatStats: { maxHitpoints: 6, accuracy: 6 },
  },
  'equipment.baina-armor': {
    equipmentId: 'equipment.baina-armor',
    name: '百衲甲',
    slot: 'armor',
    tier: 1,
    baseStatRatios: { physicalArmor: 0.2 },
    flatStats: { maxHitpoints: 10, physicalArmor: 10 },
  },
  'equipment.zhakou-pants': {
    equipmentId: 'equipment.zhakou-pants',
    name: '扎口裤',
    slot: 'legs',
    tier: 1,
    baseStatRatios: { physicalArmor: 0.1 },
    flatStats: { physicalArmor: 6, maxHitpoints: 6 },
  },
  'equipment.xingshan-boots': {
    equipmentId: 'equipment.xingshan-boots',
    name: '行山靴',
    slot: 'boots',
    tier: 1,
    baseStatRatios: { evasion: 0.1 },
    flatStats: { evasion: 6, maxHitpoints: 6 },
  },
  'equipment.hunyuan-scarf': {
    equipmentId: 'equipment.hunyuan-scarf',
    name: '混元巾',
    slot: 'head',
    tier: 1,
    baseStatRatios: { spellResistance: 0.1 },
    flatStats: { maxManapoints: 6, accuracy: 6 },
  },
  'equipment.suma-robe': {
    equipmentId: 'equipment.suma-robe',
    name: '素麻袍',
    slot: 'armor',
    tier: 1,
    baseStatRatios: { spellResistance: 0.2 },
    flatStats: { maxManapoints: 10, spellResistance: 10 },
  },
  'equipment.xingqi-pants': {
    equipmentId: 'equipment.xingqi-pants',
    name: '行气裤',
    slot: 'legs',
    tier: 1,
    baseStatRatios: { spellResistance: 0.1 },
    flatStats: { spellResistance: 6, maxManapoints: 6 },
  },
  'equipment.shifang-shoes': {
    equipmentId: 'equipment.shifang-shoes',
    name: '十方鞋',
    slot: 'boots',
    tier: 1,
    baseStatRatios: { evasion: 0.1 },
    flatStats: { evasion: 6, maxManapoints: 6 },
  },
  'equipment.pogang-sword': {
    equipmentId: 'equipment.pogang-sword',
    name: '破罡剑',
    slot: 'weapon',
    tier: 2,
    baseStatRatios: { physicalAttack: 0.28 },
    flatStats: { physicalAttack: 14, accuracy: 14 },
  },
  'equipment.zhansha-blade': {
    equipmentId: 'equipment.zhansha-blade',
    name: '斩煞刀',
    slot: 'weapon',
    tier: 2,
    baseStatRatios: { physicalAttack: 0.14, physicalArmor: 0.2 },
    flatStats: { physicalAttack: 11, physicalArmor: 11 },
    specialMechanics: { threatBonusPoints: 300 },
  },
  'equipment.chaoyuan-staff': {
    equipmentId: 'equipment.chaoyuan-staff',
    name: '朝元杖',
    slot: 'weapon',
    tier: 2,
    baseStatRatios: { spellAttack: 0.28 },
    flatStats: { spellAttack: 14, accuracy: 14 },
  },
  'equipment.songfeng-zither': {
    equipmentId: 'equipment.songfeng-zither',
    name: '松风琴',
    slot: 'weapon',
    tier: 2,
    baseStatRatios: { spellAttack: 0.18 },
    flatStats: { spellAttack: 12, manapointRegen: 3 },
    specialMechanics: { healingBonusPoints: 50 },
  },
}

/**
 * 按物品 ID 查询静态兜底物品定义，缺省字段补齐为安全默认值；
 * 未登记的 ID 返回 undefined（交由服务端目录或元数据表兜底）。
 */
export function resolveDefaultItemDefinition(itemId: string): ItemDefinition | undefined {
  const def = DEFAULT_ITEM_DEFINITIONS[itemId]
  if (!def) return undefined
  return {
    itemId,
    name: def.name ?? itemId,
    description: def.description ?? '',
    kind: def.kind ?? 'consumable',
    category: def.category ?? 'pill',
    tier: def.tier ?? 1,
    sellPrice: def.sellPrice ?? 0,
    isTradable: def.isTradable ?? true,
    usableActionTypes: def.usableActionTypes ?? [],
    cooldownSeconds: def.cooldownSeconds ?? 0,
    hitpointRestore: def.hitpointRestore ?? 0,
    manapointRestore: def.manapointRestore ?? 0,
    buffEffects: def.buffEffects ?? null,
    durationSeconds: def.durationSeconds ?? 0,
    isContinuous: def.isContinuous ?? false,
    cultivationMultiplier: def.cultivationMultiplier ?? 1,
    gatheringMultiplier: def.gatheringMultiplier ?? 1,
    alchemyMultiplier: def.alchemyMultiplier ?? 1,
  }
}

/**
 * 按装备 ID 查询静态兜底装备定义，缺省字段补齐为安全默认值；
 * 未登记的 ID 返回 undefined（交由服务端目录兜底）。
 */
export function resolveDefaultEquipmentDefinition(equipmentId: string): CombatEquipmentDefinition | undefined {
  const def = DEFAULT_EQUIPMENT_DEFINITIONS[equipmentId]
  if (!def) return undefined
  return {
    equipmentId,
    name: def.name ?? equipmentId,
    slot: def.slot ?? 'weapon',
    tier: def.tier ?? 1,
    enhancementBonus: def.enhancementBonus,
    baseStatRatios: def.baseStatRatios,
    flatStats: def.flatStats,
    specialMechanics: def.specialMechanics,
  }
}

/** 静态基础物品定义字典 */
export const ITEM_MAP: Readonly<Record<string, Partial<ItemDefinition>>> = DEFAULT_ITEM_DEFINITIONS

/** 静态基础装备定义字典 */
export const EQUIPMENT_MAP: Readonly<Record<string, Partial<CombatEquipmentDefinition>>> = DEFAULT_EQUIPMENT_DEFINITIONS

/** 静态基础物品元数据字典 */
export const ITEM_META_MAP: Readonly<Record<string, ItemMetaCore>> = ITEM_META_CORES

