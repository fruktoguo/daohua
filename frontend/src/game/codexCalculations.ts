import type { ItemDefinition, InventoryItem } from './types'
import type { CodexMaterialSubTab } from '../uiStore'

/**
 * 判定材料是否属于指定的图鉴子分类：
 * - herb（药材）：自然采集之草木灵材（spiritMaterial/herb）及百草精粹（草木精华、伴灵精华、桃木晶、灵松晶）
 * - mineral（矿石）：矿脉采掘之原矿（mineral）及金铁精材（百炼铁、赤铜精）
 * - exotic（异材）：妖兽讨伐掉落之奇珍残蜕（exoticMaterial）
 */
export function isMaterialSubTabItem(
  item: ItemDefinition,
  subTab: CodexMaterialSubTab
): boolean {
  const sub = item.subCategory
  if (subTab === 'herb') {
    return (
      sub === 'spiritMaterial' ||
      sub === 'herb' ||
      item.itemId === 'item.herb-essence' ||
      item.itemId === 'item.banling-essence' ||
      item.itemId === 'item.peach-wood-crystal' ||
      item.itemId === 'item.spirit-pine-crystal'
    )
  }
  if (subTab === 'mineral') {
    return (
      sub === 'mineral' ||
      item.itemId === 'item.refined-iron' ||
      item.itemId === 'item.refined-copper'
    )
  }
  if (subTab === 'exotic') {
    return sub === 'exoticMaterial'
  }
  return false
}

/**
 * 判定材料是否已在图鉴中解锁收录：
 * 1. 角色当前境界达到材料等阶所需等级（如 T1 对应 1 级默认解锁，T2 对应 18 级叩仙门）
 * 2. 玩家背包当前或曾持有该物品（数量 > 0）
 */
export function isMaterialUnlocked(
  item: ItemDefinition,
  realmLevel: number,
  inventory: readonly InventoryItem[] | Record<string, number> = []
): boolean {
  const t = Math.max(1, Math.floor(item.tier ?? 1))
  const requiredLevel = t <= 1 ? 1 : 18 + (t - 2) * 12
  if (realmLevel >= requiredLevel) return true

  if (Array.isArray(inventory)) {
    const found = inventory.find(i => i.itemId === item.itemId && !i.variant)
    return (found?.amount ?? 0) > 0
  }
  return ((inventory as Record<string, number>)[item.itemId] ?? 0) > 0
}
