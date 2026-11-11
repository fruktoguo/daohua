import type { CombatEquipmentDefinition } from './types'

/**
 * 装备槽位展示序（与装备管理面板 EquipmentPanel 的布局口径一致）：
 * 武器（含双手）→ 副手 → 头 → 衣 → 裤 → 饰品（trinket 归并 ring/neck/talisman）
 * → 技艺工具 tool-*（采集/挖掘/炼丹/炼器/符箓/阵法/强化）。
 * 配表未登记的槽位与缺配表条目统一垫底，保持穿戴先后相对顺序。
 */
const EQUIPMENT_SLOT_RANK: Readonly<Record<string, number>> = {
  weapon: 0,
  'two-hand': 0,
  'off-hand': 1,
  head: 2,
  armor: 3,
  legs: 4,
  trinket: 5,
  ring: 5,
  neck: 5,
  talisman: 5,
  'tool-gathering': 6,
  'tool-mining': 7,
  'tool-alchemy': 8,
  'tool-forging': 9,
  'tool-talisman': 10,
  'tool-formation': 11,
  'tool-enhancement': 12,
}

const UNKNOWN_SLOT_RANK = 99

/** 槽位 → 展示序权重；未知槽位返回垫底权重。 */
export function equipmentSlotRank(slot: string | null | undefined): number {
  return slot !== null && slot !== undefined ? EQUIPMENT_SLOT_RANK[slot] ?? UNKNOWN_SLOT_RANK : UNKNOWN_SLOT_RANK
}

/**
 * 已穿戴装备按槽位展示序排序（返回新数组，不改动入参数组；同权重保持原相对顺序）。
 * 装备配表缺失或条目无 slot 时按未知槽位垫底。
 */
export function sortEquippedItemsBySlot<T extends { itemId: string }>(
  items: readonly T[],
  equipmentCatalog: readonly Pick<CombatEquipmentDefinition, 'equipmentId' | 'slot'>[],
): T[] {
  if (items.length < 2) return [...items]
  const rankById = new Map<string, number>()
  for (const def of equipmentCatalog) rankById.set(def.equipmentId, equipmentSlotRank(def.slot))
  return [...items].sort(
    (a, b) => (rankById.get(a.itemId) ?? UNKNOWN_SLOT_RANK) - (rankById.get(b.itemId) ?? UNKNOWN_SLOT_RANK),
  )
}
