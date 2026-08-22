import type { PlayerSnapshot, QueueState } from './types'

/**
 * 本地预览/初始状态下的演示行动队列定义。
 * 包含修炼、探索、炼丹的初始槽位及预设演示行动。
 */
const demoQueue: QueueState = {
  loopEnabled: true,
  autoUseSlots: {
    cultivation: [
      { itemId: 'item.qi-gathering-pill', quantity: 1, enabled: true },
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
    ],
    exploration: [
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
    ],
    alchemy: [
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
    ],
    combat: [
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
      { itemId: null, quantity: 1, enabled: false },
    ],
  },
  entries: [
    {
      entryId: 'demo-gather',
      actionId: 'explore.gather-herbs',
      actionName: '采集草药',
      type: 'exploration',
      status: 'running',
      progressSeconds: 3,
      durationSeconds: 8,
      remainingRepeats: 1,
    },
    {
      entryId: 'demo-alchemy',
      actionId: 'alchemy.refine-qi-pill',
      actionName: '炼制引气丹',
      type: 'alchemy',
      status: 'queued',
      progressSeconds: 0,
      durationSeconds: 12,
      remainingRepeats: 1,
    },
    {
      entryId: 'demo-cultivation',
      actionId: 'cultivation.draw-qi',
      actionName: '引气入体',
      type: 'cultivation',
      status: 'queued',
      progressSeconds: 0,
      durationSeconds: 10,
      remainingRepeats: 1,
    },
  ],
}

/**
 * 构造用于本地离线预览或初次加载时的玩家快照数据。
 * @param playerId 玩家唯一标识符
 * @returns 完整的玩家快照对象 (PlayerSnapshot)
 */
export function createDemoSnapshot(playerId: string): PlayerSnapshot {
  const now = new Date()
  return {
    playerId,
    name: 'YuoHira',
    visual: { renderer: 'text', source: '修' },
    version: 0,
    serverTimeUnixSeconds: Math.floor(now.getTime() / 1000),
    presenceMode: 'online',
    queue: demoQueue,
    inventory: [
      { itemId: 'item.spirit-herb', name: '灵草', category: '材料', amount: 0, tone: 'green', glyph: '✤' },
      { itemId: 'item.qi-gathering-pill', name: '引气丹', category: '丹药', amount: 0, tone: 'violet', glyph: '✺' },
      { itemId: 'currency.spirit', name: '灵石', category: '货币', amount: 0, tone: 'gold', glyph: '◈' },
      { itemId: 'resource.qi', name: '灵气', category: '资源', amount: 0, tone: 'cyan', glyph: '◉' },
    ],
    buffs: [],
    stats: {
      qi: 0,
      spirit: 0,
      foundationExp: 0,
      herbsCollected: 0,
      pillsCrafted: 0,
      cultivationCycles: 0,
      completedActions: 0,
      blockedActions: 0,
      combatWins: 0,
      combatLosses: 0,
      combatExperience: 0,
      combatDamageDealt: 0,
      combatDamageTaken: 0,
    },
    world: {
      mapId: 'map.qingyun-mountains',
      nodeId: 'node.herb-vale',
      edgeId: null,
      edgeProgress: 0,
      x: 13,
      y: 63,
      locationName: '青岚药谷',
      cultivationMultiplier: 1,
      availableActionIds: ['explore.gather-herbs', 'combat.hunt-qi-beast'],
      encounterIds: ['encounter.qi-beast'],
      npcs: [],
      buildings: [],
    },
    logEvents: [
      {
        sequence: 1,
        timestampUnixMilliseconds: now.getTime(),
        template: '行动队列已建立：采集 → 炼丹 → 修炼',
      },
    ],
  }
}
