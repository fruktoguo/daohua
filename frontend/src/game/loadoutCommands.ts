import { gameClient } from '../network/gameClient'
import { useGameStore } from './gameStore'
import { normalizeAbilitySlots } from './ids'
import type { CombatLoadout } from './types'

/**
 * loadout.replace 唯一发送入口。
 *
 * 合并补丁后统一执行功法槽位归一化（内功位/术法位类别互斥 + 装配等级对齐
 * 已参悟等级），清理历史遗留的跨类装配与陈旧等级，避免整条命令因旧数据被
 * 服务端拒绝。所有装配相关入口（功法面板、战斗底栏、装备面板、背包使用）
 * 必须经由此函数发送，不得自行拼装 loadout.replace 载荷。
 */
export function sendLoadoutReplace(patch: Partial<CombatLoadout>): void {
  const { combat, abilities } = useGameStore.getState()
  if (!combat) return
  const merged = { ...combat, ...patch }
  merged.abilitySlots = normalizeAbilitySlots(merged.abilitySlots, abilities, merged.abilityExperiences)
  gameClient.sendCommand('loadout.replace', merged)
}
