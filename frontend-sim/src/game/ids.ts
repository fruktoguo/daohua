// 与 src/Domain/Models/Ids.cs 一一对应。
// 接真实后端时，这些 id 就是协议里的字符串。

export type ActionType = 'cultivation' | 'exploration' | 'alchemy'
export type PresenceMode = 'online' | 'idleOffline' | 'fullyOffline'
export type ActionStatus = 'queued' | 'running' | 'blocked'

export const ACTION_TYPES: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy']

export const ACTION_TYPE_NAMES: Record<ActionType, string> = {
  cultivation: '修炼',
  exploration: '探索',
  alchemy: '炼丹'
}

export const ACTION_IDS = {
  gatherHerbs: 'explore.gather-herbs',
  refineQiPill: 'alchemy.refine-qi-pill',
  drawQi: 'cultivation.draw-qi'
} as const

export const ITEM_IDS = {
  spiritHerb: 'item.spirit-herb',
  qiGatheringPill: 'item.qi-gathering-pill',
  spirit: 'currency.spirit',
  qi: 'resource.qi'
} as const

export const BUFF_IDS = {
  qiGathering: 'buff.qi-gathering'
} as const
