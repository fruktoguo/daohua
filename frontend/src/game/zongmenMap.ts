import type { WorldMapDefinition } from './types'
import { WORLD_UNIT_X, WORLD_UNIT_Y } from '../components/worldMapVisibility.ts'

/** 宗门投影相对于挂靠节点的候选偏移（世界单位），按默认优先序排列 */
const ZONGMEN_CANDIDATE_OFFSETS: ReadonlyArray<readonly [number, number]> = [
  [9, 9],
  [9, -9],
  [-9, 9],
  [-9, -9],
  [12, 0],
  [-12, 0],
  [0, 12],
  [0, -12],
]

const MIN_CLEARANCE_PX = 96

function resolveSafeZongmenPosition(
  anchor: { x: number; y: number },
  existingNodes: ReadonlyArray<{ x: number; y: number }>,
): { x: number; y: number } {
  let fallback = { x: anchor.x + ZONGMEN_CANDIDATE_OFFSETS[0][0], y: anchor.y + ZONGMEN_CANDIDATE_OFFSETS[0][1] }
  let bestMinDist = -1

  for (const [dx, dy] of ZONGMEN_CANDIDATE_OFFSETS) {
    const cx = anchor.x + dx
    const cy = anchor.y + dy
    if (cx < 0 || cy < 0) continue

    let minDist = Infinity
    for (const node of existingNodes) {
      const dist = Math.hypot((cx - node.x) * WORLD_UNIT_X, (cy - node.y) * WORLD_UNIT_Y)
      if (dist < minDist) minDist = dist
    }

    if (minDist >= MIN_CLEARANCE_PX) {
      return { x: cx, y: cy }
    }
    if (minDist > bestMinDist) {
      bestMinDist = minDist
      fallback = { x: cx, y: cy }
    }
  }

  return fallback
}

/** 只在个人地图上附加本宗驻地；每次先移除旧投影，覆盖迁宗、离宗和重连。 */
export function withZongmenResidence(maps: readonly WorldMapDefinition[], own: {
  nodeId: string; anchorNodeId: string; name: string
} | null): WorldMapDefinition[] {
  const occupiedNodes = maps.flatMap(map =>
    map.nodes.filter(node => !node.nodeId.startsWith('node.zongmen.')),
  )

  return maps.map(map => {
    const nodes = map.nodes.filter(node => !node.nodeId.startsWith('node.zongmen.'))
    const edges = map.edges.filter(edge => !edge.edgeId.startsWith('edge.zongmen.'))
    const anchor = own ? nodes.find(node => node.nodeId === own.anchorNodeId) : null
    if (own && anchor) {
      const pos = resolveSafeZongmenPosition(anchor, occupiedNodes)
      nodes.push({
        nodeId: own.nodeId,
        name: own.name,
        description: '',
        x: pos.x,
        y: pos.y,
        cultivationMultiplier: 1,
        availableActionIds: [],
        encounterIds: [],
        npcs: [],
        buildings: [],
      })
      edges.push({ edgeId: `edge.zongmen.${own.nodeId}`, fromNodeId: anchor.nodeId, toNodeId: own.nodeId, durationSeconds: 0 })
    }
    return { ...map, nodes, edges }
  })
}
