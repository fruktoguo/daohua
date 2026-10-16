import { create } from 'zustand'
import '../network/gameClient'
import { AUTO_USE_BARS, AUTO_USE_BAR_SLOT_COUNT, findActionInCatalog, registerCatalogActions, resolveActionDefinition, resolveActionDefinitions } from './ids'
import { mergeBattleReportHistory } from './battleReportStore'
import { useUiStore } from '../uiStore'
import type { ActionDefinition, AutoUseBarType, AutoUseSlot, BuildingDefinition, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, ItemDefinition, MarketItemStats, PlayerPatch, PlayerSnapshot, QueueState, WorldMapDefinition } from './types'

/**
 * 前端与服务端的网络连接生命周期状态：
 * - 'connecting': 正在建立初始连接
 * - 'online': 已连接且正常同步
 * - 'reconnecting': 连接意外断开，正在指数退避重连
 * - 'offline': 已离线（登出或因鉴权被拒停止自动重连）
 */
export type ConnectionState = 'connecting' | 'online' | 'reconnecting' | 'offline'

/**
 * 游戏全局状态接口（Zustand Store）
 * 集中管理玩家快照、世界状态、行动队列、战斗装配、静态目录与网络同步状态
 */
interface GameStore {
  /** 玩家唯一标识符 (UUID) */
  playerId: string
  /** 角色名 (展示用) */
  name: string
  /** 账号标识 */
  account: string
  /** 角色自增序列编号 */
  sequenceId: number
  /** 角色创建时间戳（Unix 秒） */
  createdAtUnixSeconds: number
  /** 改名冷却倒计时（秒） */
  renameCooldownSeconds: number
  /** 角色视觉形象配置 */
  visual: PlayerSnapshot['visual']
  /** 权威快照/增量版本号（单调递增，用于丢弃乱序旧包） */
  version: number
  /** 服务端权威时间（Unix 秒） */
  serverTimeUnixSeconds: number
  /** 服务端权威时间（Unix 毫秒） */
  serverTimeUnixMilliseconds: number
  /** 上次收到服务端同步包时的客户端 performance.now() 时间戳（用于平滑进度插值） */
  lastSyncPerformanceTime: number
  /** 上次收到行动队列专属同步包时的客户端 performance.now() 时间戳（避免非队列 patch 干扰进度） */
  lastQueueSyncPerformanceTime: number
  /** 玩家在线/离线补算模式 */
  presenceMode: PlayerSnapshot['presenceMode']
  /** 是否处于突破待确认状态 */
  isPendingBreakthrough: boolean
  /** 是否已成功接收到服务端全量快照 */
  hasReceivedSnapshot: boolean
  /** 新手引导里程碑下标（settings 分段权威；0~6 进行中，7 完成/跳过终态） */
  tutorialStep: number
  /** 行动队列状态（当前执行、等待列表、自动消耗槽位） */
  queue: PlayerSnapshot['queue']
  /** 背包物品列表 */
  inventory: PlayerSnapshot['inventory']
  /** 角色当前生效的 Buff 列表 */
  buffs: PlayerSnapshot['buffs']
  /** 角色统计数据（修为、击杀、战斗胜负等） */
  stats: PlayerSnapshot['stats']
  /** 战斗装配（流派、已学技能、技能槽配置、战斗消耗品槽） */
  combat: PlayerSnapshot['combat']
  /** 实时战斗资源（生命值、灵力值、冷却时间） */
  combatResources: PlayerSnapshot['combatResources']
  /** 战斗属性面板（攻击力、护甲、抗性、命中/闪避等） */
  combatAttributes: PlayerSnapshot['combatAttributes']
  /** 当前所在大地图节点与环境信息 */
  world: NonNullable<PlayerSnapshot['world']>
  /** 斗法（PVP）与战斗收益战报状态 */
  duel: PlayerSnapshot['duel']
  /** 全量行动目录（服务端下发或离线兜底） */
  actions: readonly ActionDefinition[]
  /** 全量物品目录 */
  items: readonly ItemDefinition[]
  /** 全量功法技能目录 */
  abilities: readonly CombatAbilityDefinition[]
  /** 全量遭遇战/野怪目录 */
  encounters: readonly CombatEncounterDefinition[]
  /** 全量装备配置目录 */
  equipment: readonly CombatEquipmentDefinition[]
  /** 全量大地图世界配置目录 */
  maps: readonly WorldMapDefinition[]
  /** 全量玩家可建造建筑目录 */
  buildings: readonly BuildingDefinition[]
  /** 坊市全品种行情统计缓存（键 `itemId|强化等级`，由 gameClient 周期轮询维护；悬浮行情卡数据源） */
  marketStats: Readonly<Record<string, MarketItemStats>>
  /** 当前网络连接状态 */
  connection: ConnectionState
  /** 最近一次 Ping/Pong 测得的往返延迟（毫秒）；null 表示尚无测量结果 */
  latencyMs: number | null
  /** 终态断连原因的 i18n 词条 key（顶号/会话失效等；null 表示无特定原因，恢复在线时自动清除） */
  disconnectReason: string | null
  /** 顶部全局提示消息（为 null 时隐藏） */
  notice: string | null
  /** 提示序号：每次下发非空提示递增，供气泡在同文案重复时重置计时并重播动画 */
  noticeSeq: number
  /** 设置网络连接状态 */
  setConnection: (connection: ConnectionState) => void
  /** 写回往返延迟测量值 */
  setLatencyMs: (latencyMs: number | null) => void
  /** 设置终态断连原因词条 key */
  setDisconnectReason: (key: string | null) => void
  /** 设置顶部全局提示消息 */
  setNotice: (notice: string | null) => void
  /** 更新行动目录 */
  setActions: (actions: readonly ActionDefinition[]) => void
  /** 更新物品目录 */
  setItems: (items: readonly ItemDefinition[]) => void
  /** 更新功法技能目录 */
  setAbilities: (abilities: readonly CombatAbilityDefinition[]) => void
  /** 更新战斗遭遇目录 */
  setEncounters: (encounters: readonly CombatEncounterDefinition[]) => void
  /** 更新装备目录 */
  setEquipment: (equipment: readonly CombatEquipmentDefinition[]) => void
  /** 更新世界地图目录 */
  setMaps: (maps: readonly WorldMapDefinition[]) => void
  /** 更新可建造建筑目录 */
  setBuildings: (buildings: readonly BuildingDefinition[]) => void
  /** 更新坊市行情统计缓存（整表替换，键 `itemId|强化等级`） */
  setMarketStats: (stats: Readonly<Record<string, MarketItemStats>>) => void
  /** 应用服务端全量快照 (Snapshot) */
  applySnapshot: (snapshot: PlayerSnapshot) => void
  /** 应用服务端增量补丁 (Delta Patch)；allowSameVersion 仅供命令回执：钱包类命令不推版本但携带资产差额 */
  applyPatch: (patch: PlayerPatch, options?: { allowSameVersion?: boolean }) => void
  /** 导出当前 UI 投影，供 wire.v1 增量解码器合并变更 section。 */
  toSnapshotView: () => PlayerSnapshot
  /** 重置游戏状态至离线初始值（登出时调用） */
  resetGameStore: () => void
}

/** 单组自动消耗槽位数（技艺各栏与战斗栏的单组均为 3 槽）。 */
const AUTO_USE_SLOT_COUNT = 3
/** 战斗装配的功法槽位下限（1 内功心法位 + 4 术法位；服务端扩容时取 max 保留）。 */
const COMBAT_ABILITY_SLOT_COUNT = 5

/** 槽位分组按下标位置判定：战斗栏域前 3 槽为药品栏，其余槽位（含非战斗栏域全部）为通用栏。 */
function autoUseSlotGroup(bar: AutoUseBarType, index: number): AutoUseSlot['group'] {
  return bar === 'combat' && index < AUTO_USE_SLOT_COUNT ? 'potion' : 'general'
}

/** 登录前/登出后的离线占位投影：为渲染层提供全部必需字段的安全默认值，首个权威快照到达后被整体覆盖。 */
function createInitialSnapshot(playerId = 'local-player'): PlayerSnapshot {
  const nowUnixSeconds = Math.floor(Date.now() / 1000)
  return {
    playerId,
    name: '修士',
    visual: { renderer: 'text', source: '修' },
    version: 0,
    serverTimeUnixSeconds: nowUnixSeconds,
    serverTimeUnixMilliseconds: nowUnixSeconds * 1000,
    presenceMode: 'online',
    queue: {
      loopEnabled: false,
      autoTravelEnabled: false,
      paused: false,
      autoUseSlots: {
        exploration: emptyAutoUseSlots('exploration'),
        alchemy: emptyAutoUseSlots('alchemy'),
        forging: emptyAutoUseSlots('forging'),
        talisman: emptyAutoUseSlots('talisman'),
        formation: emptyAutoUseSlots('formation'),
        enhancement: emptyAutoUseSlots('enhancement'),
        combat: emptyAutoUseSlots('combat'),
      },
      entries: [],
    },
    inventory: [],
    buffs: [],
    stats: {
      experience: 0,
      lifetimeExperience: 0,
      foundation: 0,
      combatExperience: 0,
      completedActions: 0,
    },
    duel: {
      encounterCharges: 0,
      cooldownUntilUnixSeconds: 0,
      activeDuelId: null,
      stagedItems: [],
      currentReport: null,
      offlineReport: null,
      dailyReport: null,
      reports: [],
    },
    world: {
      mapId: 'map.qingyun-mountains',
      nodeId: 'node.yunxi-village',
      edgeId: null,
      edgeProgress: 0,
      x: 20,
      y: 65,
      locationName: '云溪村',
      cultivationMultiplier: 1,
      availableActionIds: ['cultivation.draw-qi'],
      encounterIds: [],
      npcs: [],
      buildings: [],
      nodeBuildings: [],
      buildableBuildingIds: null,
    },
    tutorialStep: 0,
  }
}

const initial = createInitialSnapshot()

/** 生成指定物品栏域的空自动消耗槽位集（含分组标记）。 */
function emptyAutoUseSlots(bar: AutoUseBarType): AutoUseSlot[] {
  return Array.from({ length: AUTO_USE_BAR_SLOT_COUNT[bar] }, (_, index) => ({
    itemId: null,
    quantity: 1,
    enabled: false,
    remainingDurationSeconds: 0,
    group: autoUseSlotGroup(bar, index),
  }))
}

/**
 * 归一化单个自动消耗槽位数据，确保默认值完整
 */
function normalizeAutoUseSlot(slot: AutoUseSlot | null | undefined, group: AutoUseSlot['group']): AutoUseSlot {
  return {
    itemId: slot?.itemId ?? null,
    quantity: slot?.quantity ?? 1,
    enabled: slot?.enabled ?? false,
    remainingDurationSeconds: slot?.remainingDurationSeconds ?? 0,
    group,
  }
}

/**
 * 归一化各物品栏域的自动消耗槽位集合：历练/战斗 6 槽（战斗前 3 药品栏、后 3 通用栏），技艺各栏 3 槽
 */
function normalizeAutoUseSlots(
  slots: Partial<Record<AutoUseBarType, AutoUseSlot[]>> | null | undefined,
): QueueState['autoUseSlots'] {
  const normalized = {} as QueueState['autoUseSlots']
  for (const bar of AUTO_USE_BARS) {
    const list = Array.isArray(slots?.[bar]) ? slots[bar] : []
    normalized[bar] = Array.from({ length: AUTO_USE_BAR_SLOT_COUNT[bar] }, (_, index) => normalizeAutoUseSlot(list[index], autoUseSlotGroup(bar, index)))
  }
  return normalized
}

/**
 * 归一化行动队列对象
 */
function normalizeQueue(queue: PlayerSnapshot['queue']): PlayerSnapshot['queue'] {
  return { ...queue, autoUseSlots: normalizeAutoUseSlots(queue.autoUseSlots) }
}

/**
 * 归一化战斗装配对象（补齐至 5 个技能槽，并保证已学技能列表完整；
 * learnedAbilityIds 缺失时从已装配技能槽反推。消耗品槽位收敛于 queue.autoUseSlots，不在此归一化。）
 */
function normalizeCombat(combat: PlayerSnapshot['combat']): PlayerSnapshot['combat'] {
  if (!combat) return combat
  const abilitySlots = Array.isArray(combat.abilitySlots) ? combat.abilitySlots : []
  const targetSlotCount = Math.max(COMBAT_ABILITY_SLOT_COUNT, abilitySlots.length)
  const normalizedAbilitySlots = Array.from({ length: targetSlotCount }, (_, index) => ({
    abilityId: abilitySlots[index]?.abilityId ?? null,
    level: Math.max(1, abilitySlots[index]?.level ?? 1),
  }))
  const learnedAbilityIds = Array.isArray(combat.learnedAbilityIds)
    ? combat.learnedAbilityIds
    : normalizedAbilitySlots.map(slot => slot.abilityId).filter((abilityId): abilityId is string => Boolean(abilityId))
  return {
    ...combat,
    abilitySlots: normalizedAbilitySlots,
    learnedAbilityIds,
    // 功法触发条件按功法 ID 挂接；快照缺省时兜底为空表，读取方无需再判空。
    abilityTriggers: combat.abilityTriggers ?? {},
  }
}

/**
 * 游戏核心全局 Zustand 状态机
 */
export const useGameStore = create<GameStore>((set, get) => ({
  playerId: initial.playerId,
  name: initial.name,
  account: initial.name,
  sequenceId: 0,
  createdAtUnixSeconds: initial.serverTimeUnixSeconds,
  renameCooldownSeconds: 0,
  visual: initial.visual,
  version: initial.version,
  serverTimeUnixSeconds: initial.serverTimeUnixSeconds,
  serverTimeUnixMilliseconds: initial.serverTimeUnixMilliseconds ?? initial.serverTimeUnixSeconds * 1000,
  lastSyncPerformanceTime: performance.now(),
  lastQueueSyncPerformanceTime: performance.now(),
  presenceMode: initial.presenceMode,
  isPendingBreakthrough: false,
  hasReceivedSnapshot: false,
  tutorialStep: 0,
  queue: initial.queue,
  inventory: initial.inventory,
  buffs: initial.buffs,
  stats: initial.stats,
  combat: initial.combat,
  combatResources: initial.combatResources,
  combatAttributes: initial.combatAttributes,
  world: initial.world!,
  duel: initial.duel,
  actions: resolveActionDefinitions(),
  items: [],
  abilities: [],
  encounters: [],
  equipment: [],
  maps: [],
  buildings: [],
  marketStats: {},
  connection: 'connecting',
  latencyMs: null,
  disconnectReason: null,
  notice: null,
  noticeSeq: 0,
  setConnection: (connection) => set(state => ({ connection, disconnectReason: connection === 'online' ? null : state.disconnectReason })),
  setLatencyMs: (latencyMs) => set({ latencyMs }),
  setDisconnectReason: (key) => set({ disconnectReason: key }),
  setNotice: (notice) => set(state => ({ notice, noticeSeq: notice === null ? state.noticeSeq : state.noticeSeq + 1 })),
  setActions: (actions) => {
    registerCatalogActions(actions)
    set((state) => {
      // 快照可能先于目录到达：此时队列条目的 actionName 只回填了 actionId。
      // 目录就绪后按 actionId 反查定义，补齐显示名与类型；未命中条目原样保留。
      let entriesChanged = false
      const updatedEntries = state.queue.entries.map(entry => {
        if (!entry.actionName || entry.actionName === entry.actionId) {
          const def = findActionInCatalog(actions, entry.actionId) ?? resolveActionDefinition(entry.actionId)
          if (def && (def.name !== entry.actionName || def.type !== entry.type)) {
            entriesChanged = true
            return {
              ...entry,
              actionName: def.name || entry.actionName,
              type: def.type || entry.type,
            }
          }
        }
        return entry
      })

      return {
        actions,
        queue: entriesChanged ? { ...state.queue, entries: updatedEntries } : state.queue,
      }
    })
  },
  setItems: (items) => set({ items }),
  setAbilities: (abilities) => set({ abilities }),
  setEncounters: (encounters) => set({ encounters }),
  setEquipment: (equipment) => set({ equipment }),
  setMaps: (maps) => set({ maps }),
  setBuildings: (buildings) => set({ buildings }),
  setMarketStats: (marketStats) => set({ marketStats }),
  applySnapshot: (snapshot) => {
    const state = get()
    // 全量快照也可能因命令回执/重连竞态晚到；禁止旧版本回写覆盖当前权威状态。
    if (state.hasReceivedSnapshot && snapshot.version < state.version) return
    // 记录「页面加载后首个权威快照」标记：仅本次用于刷新自动进详情，重连与后续 patch 不触发。
    const isFirstSnapshot = !state.hasReceivedSnapshot
    // 每次应用含 duel 分段的快照后，将战报合并写入本地历史缓存（reportId 去重、倒序截断 50 条）。
    mergeBattleReportHistory(snapshot.playerId, snapshot.duel)
    const now = performance.now()
    set({
      playerId: snapshot.playerId,
      name: snapshot.name,
      account: snapshot.account ?? snapshot.name,
      sequenceId: snapshot.sequenceId ?? 0,
      createdAtUnixSeconds: snapshot.createdAtUnixSeconds ?? snapshot.serverTimeUnixSeconds,
      renameCooldownSeconds: snapshot.renameCooldownSeconds ?? 0,
      hasReceivedSnapshot: true,
      visual: snapshot.visual ?? stateVisualFallback(snapshot.name),
      version: snapshot.version,
      serverTimeUnixSeconds: snapshot.serverTimeUnixSeconds,
      serverTimeUnixMilliseconds: snapshot.serverTimeUnixMilliseconds ?? snapshot.serverTimeUnixSeconds * 1000,
      lastSyncPerformanceTime: now,
      lastQueueSyncPerformanceTime: now,
      presenceMode: snapshot.presenceMode,
      isPendingBreakthrough: snapshot.isPendingBreakthrough ?? false,
      queue: normalizeQueue(snapshot.queue),
      inventory: snapshot.inventory,
      buffs: snapshot.buffs,
      stats: snapshot.stats,
      combat: normalizeCombat(snapshot.combat),
      combatResources: snapshot.combatResources,
      combatAttributes: snapshot.combatAttributes,
      world: snapshot.world ?? initial.world!,
      duel: snapshot.duel,
      tutorialStep: snapshot.tutorialStep,
    })
    // 刷新自动进详情：首个权威快照且行动队列非空时，直接切到「详情」页查看当前行动统计与战况。
    if (isFirstSnapshot && snapshot.queue.entries.length > 0) {
      useUiStore.getState().setActiveTab('combat')
    }
  },
  applyPatch: (patch, options) => {
    const state = get()
    // 1. 若尚未接收到服务端全量快照，则拒绝应用增量包
    if (!state.hasReceivedSnapshot) {
      return
    }
    // 2. 丢弃版本过旧或重复的乱序补丁。
    // 注意：stateVersion 在一次推进内可能多次 +1（transitions/changed/Touch 各路径），
    // 相邻推送间正常跳 +N；增量 section 携带的是全量当前值而非差分，跳过中间版本无损。
    // 真实的帧级连续性由 WS 通道与信封 sequence 保证，不得对领域版本做 +1 连续校验——
    // 否则每个 delta 都会误判为断层，陷入「断开重连 → 全量快照 → 再断层」的死循环。
    // 例外：命令回执可能与推送错序到达且钱包类命令不推状态版本——同版本回执仍要合入
    // 资产差额（同版本分段内容按服务端不变量逐位相同，重写无害），仅严格更旧的回执才丢弃。
    if (patch.version < state.version || (patch.version === state.version && options?.allowSameVersion !== true)) return
    // 每次应用含 duel 分段的增量后，将战报合并写入本地历史缓存（reportId 去重、倒序截断 50 条）。
    if (patch.duel) mergeBattleReportHistory(patch.playerId, patch.duel)
    // action_queue 分段为唯一行动队列通道（全量分段替换），仅当队列分段或暂停状态实际变更时刷新队列同步时间锚点。
    const hasQueueUpdate = patch.queueEntriesUpdated ?? Boolean(patch.queue)
    const now = performance.now()
    const queue = patch.queue ? normalizeQueue(patch.queue) : state.queue
    set({
      playerId: patch.playerId || state.playerId,
      name: patch.name ?? state.name,
      account: patch.account ?? state.account,
      sequenceId: patch.sequenceId ?? state.sequenceId,
      createdAtUnixSeconds: patch.createdAtUnixSeconds ?? state.createdAtUnixSeconds,
      renameCooldownSeconds: patch.renameCooldownSeconds ?? state.renameCooldownSeconds,
      visual: patch.visual ?? state.visual,
      version: patch.version,
      serverTimeUnixSeconds: patch.serverTimeUnixSeconds,
      serverTimeUnixMilliseconds: patch.serverTimeUnixMilliseconds ?? patch.serverTimeUnixSeconds * 1000,
      lastSyncPerformanceTime: now,
      lastQueueSyncPerformanceTime: hasQueueUpdate ? now : state.lastQueueSyncPerformanceTime,
      presenceMode: patch.presenceMode ?? state.presenceMode,
      isPendingBreakthrough: patch.isPendingBreakthrough ?? state.isPendingBreakthrough,
      queue,
      inventory: patch.inventory ?? state.inventory,
      buffs: patch.buffs ?? state.buffs,
      stats: patch.stats ?? state.stats,
      combat: normalizeCombat(patch.combat) ?? state.combat,
      combatResources: patch.combatResources ?? state.combatResources,
      combatAttributes: patch.combatAttributes ?? state.combatAttributes,
      world: patch.world ?? state.world,
      duel: patch.duel ?? state.duel,
      tutorialStep: patch.tutorialStep ?? state.tutorialStep,
    })
  },
  toSnapshotView: () => {
    const state = get()
    return {
      playerId: state.playerId,
      name: state.name,
      account: state.account,
      sequenceId: state.sequenceId,
      createdAtUnixSeconds: state.createdAtUnixSeconds,
      renameCooldownSeconds: state.renameCooldownSeconds,
      visual: state.visual,
      version: state.version,
      serverTimeUnixSeconds: state.serverTimeUnixSeconds,
      serverTimeUnixMilliseconds: state.serverTimeUnixMilliseconds,
      presenceMode: state.presenceMode,
      isPendingBreakthrough: state.isPendingBreakthrough,
      queue: state.queue,
      inventory: state.inventory,
      buffs: state.buffs,
      stats: state.stats,
      combat: state.combat,
      combatResources: state.combatResources,
      combatAttributes: state.combatAttributes,
      world: state.world,
      duel: state.duel,
      tutorialStep: state.tutorialStep,
    }
  },
  resetGameStore: () => set({
    playerId: initial.playerId,
    name: initial.name,
    account: initial.name,
    sequenceId: 0,
    createdAtUnixSeconds: initial.serverTimeUnixSeconds,
    renameCooldownSeconds: 0,
    visual: initial.visual,
    version: 0,
    hasReceivedSnapshot: false,
    connection: 'offline',
    latencyMs: null,
    disconnectReason: null,
    notice: null,
    queue: initial.queue,
    inventory: initial.inventory,
    buffs: initial.buffs,
    stats: initial.stats,
    combat: initial.combat,
    combatResources: initial.combatResources,
    combatAttributes: initial.combatAttributes,
    world: initial.world!,
    duel: initial.duel,
    marketStats: {},
  }),
}))

/**
 * 角色形象缺省回退（使用角色首字作为文本头像）
 */
function stateVisualFallback(name: string): NonNullable<PlayerSnapshot['visual']> {
  return { renderer: 'text', source: name.slice(0, 1) || '修' }
}
