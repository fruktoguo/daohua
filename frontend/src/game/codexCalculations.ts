import type { ItemDefinition, InventoryItem } from './types'
import { resolveCodexMaterialSubTab } from './codexFilters.ts'

/**
 * 解析材料所关联的生活技艺：
 * - 灵材（`spiritMaterial`）-> gathering（采集）
 * - 矿石（`mineral`）-> mining（采矿）
 * - 异材残蜕与精材 -> null（无专属生活技艺，纯由境界裁决）
 */
export function resolveMaterialTechnique(item: ItemDefinition): 'gathering' | 'mining' | null {
  const subTab = resolveCodexMaterialSubTab(item)
  if (subTab === 'spiritMaterial') return 'gathering'
  if (subTab === 'mineral') return 'mining'
  return null
}

/**
 * 格式化角色境界解锁文案：
 * - 1 级及以下：默认解锁
 * - 18 级特例：境界达到 lv18[叩天门] 解锁
 * - 其余等级：境界达到 lv{level}[{realmName}] 解锁
 */
export function formatRealmUnlockCondition(level: number, realmName?: string | null): string {
  if (level <= 1) return '默认解锁'
  const tag = realmName ? `[${realmName}]` : (level === 18 ? '[叩天门]' : '')
  return `境界达到 lv${level}${tag} 解锁`
}

/**
 * 格式化境界或技艺双重解锁文案：
 * - 1 级及以下：默认解锁
 * - 有关联技艺：境界达到 lv{level}[{realmName}] 或 {techniqueName}达到 {level} 级解锁
 * - 无关联技艺：退化为境界解锁文案
 */
export function formatDualUnlockCondition(level: number, techniqueName?: string | null, realmName?: string | null): string {
  if (level <= 1) return '默认解锁'
  if (!techniqueName) return formatRealmUnlockCondition(level, realmName)
  const tag = realmName ? `[${realmName}]` : (level === 18 ? '[叩天门]' : '')
  return `境界达到 lv${level}${tag} 或 ${techniqueName}达到 ${level} 级解锁`
}

/**
 * 判定材料是否已在图鉴中解锁收录：
 * 1. 角色当前境界达到材料等阶所需等级（如 T1 对应 1 级默认解锁，T2 对应 18 级叩天门）
 * 2. 对应生活技艺达到材料等阶所需等级（如采集/采矿达到 18 级）
 * 3. 玩家背包当前或曾持有该物品（数量 > 0）
 */
export function isMaterialUnlocked(
  item: ItemDefinition,
  realmLevel: number,
  inventory: readonly InventoryItem[] | Record<string, number> = [],
  techniqueLevels?: Record<string, number>
): boolean {
  const t = Math.max(1, Math.floor(item.tier ?? 1))
  const requiredLevel = t <= 1 ? 1 : 18 + (t - 2) * 12
  if (realmLevel >= requiredLevel) return true

  const tech = resolveMaterialTechnique(item)
  if (tech && (techniqueLevels?.[tech] ?? 0) >= requiredLevel) return true

  if (Array.isArray(inventory)) {
    const found = inventory.find(i => i.itemId === item.itemId && !i.variant)
    return (found?.amount ?? 0) > 0
  }
  return ((inventory as Record<string, number>)[item.itemId] ?? 0) > 0
}
