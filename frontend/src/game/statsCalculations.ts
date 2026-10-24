import type { BattleReport, CombatEquipmentDefinition, ItemDefinition, MarketItemStats } from './types'

export interface BriefItemYield {
  itemId: string
  quantity: number
  unitPrice: number
  dailyQty: number
  dailyVal: number
}

export interface BriefStatsResult {
  durationSeconds: number
  hours: number
  isCombat: boolean
  encountersPerHour: number
  killsPerHour: number
  deathsPerHour: number
  cyclesPerHour: number
  dailyRevenue: number
  revenuePerHour: number
  cultivationPerHour: number
  dailyCultivation: number
  combatExpPerHour: number
  dailyCombatExp: number
  rootExpPerHour: number
  topYields: BriefItemYield[]
}

/**
 * 快速解析物品单价估值（优先坊市成交参考均价，回落系统回收价）
 */
export function resolveBriefItemUnitPrice(
  itemId: string,
  itemsCatalog?: readonly ItemDefinition[] | null,
  marketStats?: Readonly<Record<string, MarketItemStats>> | null,
  equipmentsCatalog?: readonly CombatEquipmentDefinition[] | null,
): number {
  if (marketStats) {
    const stats = marketStats[`${itemId}|0`]
    if (stats?.referencePrice && stats.referencePrice > 0) return stats.referencePrice
  }
  if (Array.isArray(itemsCatalog)) {
    const item = itemsCatalog.find(i => i.itemId === itemId)
    if (item?.sellPrice && item.sellPrice > 0) return item.sellPrice
  }
  if (Array.isArray(equipmentsCatalog)) {
    const eq = equipmentsCatalog.find(e => e.id === itemId)
    if (eq?.sellPrice && eq.sellPrice > 0) return eq.sellPrice
  }
  return 0
}

/**
 * 根据行动收益账本与流逝时间计算简要统计与速率估算指标
 *
 * @param report 目标账本
 * @param nowSeconds 当前估算的服务端秒数
 * @param catalogs 物品配表、坊市行情与装备配表
 */
export function calculateBriefStats(
  report: BattleReport,
  nowSeconds: number,
  catalogs?: {
    items?: readonly ItemDefinition[]
    marketStats?: Readonly<Record<string, MarketItemStats>>
    equipments?: readonly CombatEquipmentDefinition[]
  },
): BriefStatsResult {
  const durationSeconds = Math.max(
    0,
    (report.endedAtUnixSeconds > 0 ? report.endedAtUnixSeconds : nowSeconds) - report.startedAtUnixSeconds,
  )
  const hours = durationSeconds > 0 ? durationSeconds / 3600 : 0

  const isCombat = report.encounters > 0
    || report.normalKills > 0
    || report.eliteKills > 0
    || report.bossKills > 0
    || report.deaths > 0
    || (report.actionId ? report.actionId.startsWith('combat.') : false)

  const totalKills = report.normalKills + report.eliteKills + report.bossKills
  const encountersPerHour = hours > 0 ? report.encounters / hours : 0
  const killsPerHour = hours > 0 ? totalKills / hours : 0
  const deathsPerHour = hours > 0 ? report.deaths / hours : 0
  const cyclesPerHour = hours > 0 ? report.cycles / hours : 0

  // 掉落物品灵石收益估算
  let totalValue = 0
  const yields: BriefItemYield[] = []

  for (const item of report.items) {
    const unitPrice = resolveBriefItemUnitPrice(
      item.itemId,
      catalogs?.items,
      catalogs?.marketStats,
      catalogs?.equipments,
    )
    const val = item.quantity * unitPrice
    totalValue += val
    const dailyQty = hours > 0 ? Math.round((item.quantity / hours) * 24) : 0
    const dailyVal = hours > 0 ? Math.round((val / hours) * 24) : 0
    yields.push({
      itemId: item.itemId,
      quantity: item.quantity,
      unitPrice,
      dailyQty,
      dailyVal,
    })
  }

  yields.sort((a, b) => b.dailyVal - a.dailyVal || b.dailyQty - a.dailyQty)

  const dailyRevenue = hours > 0 ? Math.round((totalValue / hours) * 24) : 0
  const revenuePerHour = hours > 0 ? Math.round(totalValue / hours) : 0

  // 修为与战斗经验速率
  const cultivationPerHour = hours > 0 ? Math.round(report.experience / hours) : 0
  const dailyCultivation = Math.round(cultivationPerHour * 24)

  const combatExpPerHour = hours > 0 ? Math.round(report.combatExperience / hours) : 0
  const dailyCombatExp = Math.round(combatExpPerHour * 24)

  const totalRootExp = Object.values(report.rootExp).reduce((a, b) => a + b, 0)
  const rootExpPerHour = hours > 0 ? Math.round(totalRootExp / hours) : 0

  return {
    durationSeconds,
    hours,
    isCombat,
    encountersPerHour,
    killsPerHour,
    deathsPerHour,
    cyclesPerHour,
    dailyRevenue,
    revenuePerHour,
    cultivationPerHour,
    dailyCultivation,
    combatExpPerHour,
    dailyCombatExp,
    rootExpPerHour,
    topYields: yields.slice(0, 6),
  }
}
