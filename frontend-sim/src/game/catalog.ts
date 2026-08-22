import { ACTION_IDS, ITEM_IDS } from './ids'
import type { ActionDefinition } from './types'

// 与 DaoHuaActionCatalog.cs 一致。

const definitions = new Map<string, ActionDefinition>([
  [
    ACTION_IDS.gatherHerbs,
    {
      id: ACTION_IDS.gatherHerbs,
      name: '采集草药',
      description: '在野外寻找灵草，收集炼丹材料。',
      type: 'exploration',
      durationSeconds: 8,
      inputs: {},
      outputs: { [ITEM_IDS.spiritHerb]: 3 }
    }
  ],
  [
    ACTION_IDS.refineQiPill,
    {
      id: ACTION_IDS.refineQiPill,
      name: '炼制引气丹',
      description: '消耗三株灵草，炼制一枚引气丹。',
      type: 'alchemy',
      durationSeconds: 12,
      inputs: { [ITEM_IDS.spiritHerb]: 3 },
      outputs: { [ITEM_IDS.qiGatheringPill]: 1 }
    }
  ],
  [
    ACTION_IDS.drawQi,
    {
      id: ACTION_IDS.drawQi,
      name: '引气入体',
      description: '吐纳灵气，积累灵力与修为。',
      type: 'cultivation',
      durationSeconds: 10,
      inputs: {},
      outputs: { [ITEM_IDS.qi]: 10 }
    }
  ]
])

export function getAction(actionId: string): ActionDefinition {
  const definition = definitions.get(actionId)
  if (!definition) {
    throw new Error(`未知行动: ${actionId}`)
  }
  return definition
}

export function tryGetAction(actionId: string): ActionDefinition | undefined {
  return definitions.get(actionId)
}

export function actionsOfType(type: ActionDefinition['type']): ActionDefinition[] {
  return [...definitions.values()].filter(definition => definition.type === type)
}

export interface ItemMeta {
  id: string
  name: string
  category: 'material' | 'consumable' | 'currency' | 'resource'
  tone: 'green' | 'amber' | 'violet' | 'cyan'
  glyph: string
  description: string
}

// 对应 DaoHuaInventoryItemDto 的展示元数据。
export const ITEM_METAS: Record<string, ItemMeta> = {
  [ITEM_IDS.spiritHerb]: {
    id: ITEM_IDS.spiritHerb,
    name: '灵草',
    category: 'material',
    tone: 'green',
    glyph: '草',
    description: '山野间采集的灵草，是炼丹的基础材料。'
  },
  [ITEM_IDS.qiGatheringPill]: {
    id: ITEM_IDS.qiGatheringPill,
    name: '引气丹',
    category: 'consumable',
    tone: 'amber',
    glyph: '丹',
    description: '服下后短时间内提升引气入体的收益，可放入自动使用槽。'
  },
  [ITEM_IDS.spirit]: {
    id: ITEM_IDS.spirit,
    name: '灵石',
    category: 'currency',
    tone: 'violet',
    glyph: '灵',
    description: '修仙界通行的货币。'
  },
  [ITEM_IDS.qi]: {
    id: ITEM_IDS.qi,
    name: '灵气',
    category: 'resource',
    tone: 'cyan',
    glyph: '气',
    description: '吐纳积累的灵气，是修为的度量。'
  }
}

export function getItemMeta(itemId: string): ItemMeta {
  return (
    ITEM_METAS[itemId] ?? {
      id: itemId,
      name: itemId,
      category: 'material',
      tone: 'green',
      glyph: '？',
      description: ''
    }
  )
}
