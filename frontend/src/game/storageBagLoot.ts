import type { PillTone } from '../components/PillLabel'

/**
 * 储物袋单项产出定义
 */
export interface StorageBagLootEntry {
  /** 产出条目唯一标识 */
  id: string
  /** 产出物品或资产 ID */
  itemId: string
  /** 多语言名称翻译键 */
  nameKey: string
  /** 最小数量 */
  minQuantity: number
  /** 最大数量 */
  maxQuantity: number
  /** 掉落概率 [0, 1] */
  chance: number
  /** 是否属于概率触发的额外暴击奖励 */
  isExtra?: boolean
  /** 对应的修仙药丸主题色 */
  tone?: PillTone
}

/**
 * 各级修仙储物袋静态开启掉落表（与服务端 StorageBagOpenRules 严格逐项对齐）
 */
export const STORAGE_BAG_LOOT_TABLE: Record<string, readonly StorageBagLootEntry[]> = {
  'item.storage-bag.broken': [
    {
      id: 'spirit-stone-base',
      itemId: 'currency.spirit-stone',
      nameKey: 'game.storageBag.lootSpiritStoneBase',
      minQuantity: 1,
      maxQuantity: 1000,
      chance: 1.0,
      tone: 'gold',
    },
    {
      id: 'spirit-stone-crit',
      itemId: 'currency.spirit-stone',
      nameKey: 'game.storageBag.lootSpiritStoneCrit',
      minQuantity: 1,
      maxQuantity: 10000,
      chance: 0.1,
      isExtra: true,
      tone: 'gold',
    },
    {
      id: 'gongde-base',
      itemId: 'item.gongde',
      nameKey: 'game.storageBag.lootGongdeBase',
      minQuantity: 5,
      maxQuantity: 10,
      chance: 0.1,
      tone: 'emerald',
    },
    {
      id: 'gongde-crit',
      itemId: 'item.gongde',
      nameKey: 'game.storageBag.lootGongdeCrit',
      minQuantity: 50,
      maxQuantity: 100,
      chance: 0.01,
      isExtra: true,
      tone: 'emerald',
    },
    {
      id: 'awakening-stone',
      itemId: 'item.awakening-stone',
      nameKey: 'game.storageBag.lootAwakeningStone',
      minQuantity: 1,
      maxQuantity: 1,
      chance: 0.001,
      tone: 'purple',
    },
  ],
}

/**
 * 判断指定物品 ID 是否为储物袋
 */
export function isStorageBag(itemId: string): boolean {
  return itemId in STORAGE_BAG_LOOT_TABLE || itemId.startsWith('item.storage-bag.')
}

/**
 * 获取指定储物袋的全部开启产出列表
 */
export function getStorageBagLoot(itemId: string): readonly StorageBagLootEntry[] {
  return STORAGE_BAG_LOOT_TABLE[itemId] ?? []
}

/**
 * 格式化储物袋产出数量区间文本
 */
export function formatStorageBagLootQuantity(entry: StorageBagLootEntry): string {
  if (entry.minQuantity === entry.maxQuantity) {
    return `× ${entry.minQuantity.toLocaleString()}`
  }
  return `${entry.minQuantity.toLocaleString()}~${entry.maxQuantity.toLocaleString()}`
}

/**
 * 格式化储物袋开启掉率百分比标签
 */
export function formatStorageBagLootChance(chance: number): string {
  if (chance >= 1.0 - 1e-9) {
    return '100% 必得'
  }
  const pct = chance * 100
  const formattedPct = +(pct >= 0.1 ? pct.toFixed(2) : pct.toFixed(4))
  return `${formattedPct}% 概率`
}
