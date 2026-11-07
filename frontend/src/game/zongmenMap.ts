import type { WorldMapDefinition } from './types'

/** 只在个人地图上附加本宗驻地；每次先移除旧投影，覆盖迁宗、离宗和重连。 */
export function withZongmenResidence(maps: readonly WorldMapDefinition[], own: {
  nodeId: string; anchorNodeId: string; name: string
} | null): WorldMapDefinition[] {
  return maps.map(map => {
    const nodes = map.nodes.filter(node => !node.nodeId.startsWith('node.zongmen.'))
    const edges = map.edges.filter(edge => !edge.edgeId.startsWith('edge.zongmen.'))
    const anchor = own ? nodes.find(node => node.nodeId === own.anchorNodeId) : null
    if (own && anchor) {
      nodes.push({ nodeId: own.nodeId, name: own.name, description: '',
        x: Math.min(95, anchor.x + 6), y: Math.min(95, anchor.y + 6), cultivationMultiplier: 1,
        availableActionIds: [], encounterIds: [], npcs: [], buildings: [] })
      edges.push({ edgeId: `edge.zongmen.${own.nodeId}`, fromNodeId: anchor.nodeId, toNodeId: own.nodeId, durationSeconds: 0 })
    }
    return { ...map, nodes, edges }
  })
}
